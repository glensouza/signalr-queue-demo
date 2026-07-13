using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using SignalRQueueDemo.ApiService.Hubs;
using SignalRQueueDemo.Contracts;
using SignalRQueueDemo.Shared.Documents;
using SignalRQueueDemo.Shared.Persistence;
using SignalRQueueDemo.Shared.Persistence.Blob;

namespace SignalRQueueDemo.ApiService.Endpoints;

/// <summary>
/// Maps the document upload/viewing endpoints: public upload against a queue entry, staff listing and
/// single-document streaming. All three carry the <see cref="CorsPolicies.KnownFrontends"/> CORS policy; upload
/// additionally validates the check-in token (inline — see <see cref="HandleUploadDocumentAsync"/>), and the two
/// staff endpoints are gated by <see cref="StaffAuthFilter"/>, alongside call-next/complete. Validation (content
/// type, size, per-entry cap) and the store writes themselves live in <see cref="DocumentUploadService"/> — see
/// its remarks for why that moved out of this class — so this file keeps only what's genuinely HTTP-specific:
/// reading the multipart body by hand so the check-in token is validated before any of it is buffered.
/// </summary>
public static class DocumentEndpoints
{
  /// <summary>
  /// Ceiling the form reader buffers a multipart upload body to. <see cref="DocumentUploadService.MaxDocumentSizeBytes"/>
  /// alone only rejects an oversized file *after* the whole body has been buffered (the handler can't see
  /// <see cref="IFormFile.Length"/> until then), so on a public endpoint it bounds the blob write but not the
  /// buffering. This outer limit closes that gap: a grossly oversized body is cut off by the form reader instead
  /// of spooled in full. Set to the file cap plus 64 KB of headroom for the multipart envelope (boundary + part
  /// headers) so a legitimately ≤10 MB file always fits. Applied via <see cref="UploadFormOptions"/>.
  /// </summary>
  private const long MaxUploadBodyBytes = DocumentUploadService.MaxDocumentSizeBytes + (64 * 1024);

  /// <summary>
  /// Form-reader limits for the manual <c>ReadFormAsync</c> in <see cref="HandleUploadDocumentAsync"/>. The
  /// upload reads its multipart body by hand — rather than binding an <c>IFormFile</c> parameter — so the
  /// check-in token is validated *before* any body is buffered (an endpoint filter runs only after parameter
  /// binding has already read the form). A manual read doesn't inherit the endpoint's <c>WithFormOptions</c>
  /// metadata, so the <see cref="MaxUploadBodyBytes"/> bound is set here on an explicit <see cref="FormFeature"/>
  /// instead. <see cref="FormOptions.MultipartBodyLengthLimit"/> is what actually caps the buffered body;
  /// <see cref="FormOptions.BufferBodyLengthLimit"/> is set to match as a belt-and-suspenders outer bound.
  /// </summary>
  private static readonly FormOptions UploadFormOptions = new()
  {
    BufferBodyLengthLimit = MaxUploadBodyBytes,
    MultipartBodyLengthLimit = MaxUploadBodyBytes
  };

  /// <summary>Registers POST /checkin/{id}/documents, GET /queue/{id}/documents, and GET /queue/{id}/documents/{docId}.</summary>
  public static void MapDocumentEndpoints(this WebApplication app)
  {
    // This endpoint reads its multipart body by hand inside the handler (HttpRequest, not an IFormFile
    // parameter) so it can validate the check-in token BEFORE buffering any of the body — an endpoint filter
    // (what POST /checkin uses) runs only after parameter binding has already read the form. Two consequences of
    // not binding IFormFile: no antiforgery metadata is auto-attached, so no .DisableAntiforgery() is needed (the
    // app registers no antiforgery middleware); and the buffering bound moves from .WithFormOptions() onto an
    // explicit FormFeature in the handler (see UploadFormOptions). See docs/decisions.md for the full rationale.
    app.MapPost("/checkin/{id}/documents", HandleUploadDocumentAsync)
      .RequireCors(CorsPolicies.KnownFrontends);

    app.MapGet("/queue/{id}/documents", HandleListDocumentsAsync)
      .RequireCors(CorsPolicies.KnownFrontends)
      .AddEndpointFilter<StaffAuthFilter>();
    app.MapGet("/queue/{id}/documents/{docId}", HandleGetDocumentAsync)
      .RequireCors(CorsPolicies.KnownFrontends)
      .AddEndpointFilter<StaffAuthFilter>();
  }

  private static async Task<IResult> HandleUploadDocumentAsync(
    string id,
    HttpRequest request,
    CheckInTokenService tokenService,
    DocumentUploadService uploadService,
    QueueBroadcaster broadcaster,
    CancellationToken ct)
  {
    // Token first — before a single byte of the (potentially large) multipart body is read. This is why the
    // upload gates the token inline rather than via CheckInTokenFilter; see that filter's Reject remarks.
    IResult? tokenRejection = CheckInTokenFilter.Reject(request, tokenService);
    if (tokenRejection is not null)
    {
      return tokenRejection;
    }

    if (!request.HasFormContentType)
    {
      return Results.ValidationProblem(new Dictionary<string, string[]>
      {
        ["file"] = ["Expected a multipart/form-data upload with a 'file' field."]
      });
    }

    // Read the body by hand under an explicit FormFeature so the buffering bound (UploadFormOptions) still
    // applies — a manual ReadFormAsync ignores the endpoint's form-options metadata. A body over the limit
    // throws InvalidDataException here, which UploadLimitExceptionHandler maps to a clean 413.
    request.HttpContext.Features.Set<IFormFeature>(new FormFeature(request, UploadFormOptions));
    IFormCollection form = await request.ReadFormAsync(ct);
    IFormFile? file = form.Files.GetFile("file");
    if (file is null)
    {
      return Results.ValidationProblem(new Dictionary<string, string[]>
      {
        ["file"] = ["A 'file' form field is required."]
      });
    }

    // Everything past "here are the file's bytes, name, content type, and length" — entry existence, content-type
    // allowlist, size cap, per-entry document cap, the blob-then-metadata write, and the RecordDocumentChangeAsync
    // call — lives in DocumentUploadService, shared with Blazor's own upload page. See that type's remarks.
    await using Stream stream = file.OpenReadStream();
    DocumentUploadResult result = await uploadService.UploadAsync(id, file.FileName, file.ContentType, file.Length, stream, ct);

    // Best-effort, mirroring how every other queue mutation broadcasts: the document is already durably stored
    // by the time Success carries an Update, so a failed or absent (null) push must never turn a successful
    // upload into an error for the caller.
    if (result.Update is not null)
    {
      await broadcaster.BroadcastAsync(result.Update);
    }

    return result.Outcome switch
    {
      DocumentUploadOutcome.Success => Results.Created(
        $"/queue/{id}/documents/{result.Metadata!.Id}", new DocumentUploadResponse { Document = result.Metadata }),
      DocumentUploadOutcome.EntryNotFound => Results.NotFound(new ProblemDetails
      {
        Title = "Entry not found",
        Detail = $"No queue entry with id '{id}' exists."
      }),
      // 409 Conflict (not 400) because the request itself is well-formed; it's the entry's current state —
      // already at capacity — that rejects it.
      DocumentUploadOutcome.LimitReached => Results.Conflict(new ProblemDetails
      {
        Title = "Document limit reached",
        Detail = $"Queue entry '{id}' already has the maximum of {DocumentUploadService.MaxDocumentsPerEntry} documents."
      }),
      _ => Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [result.FailureDetail!] })
    };
  }

  private static async Task<IResult> HandleListDocumentsAsync(
    string id,
    IDocumentRepository documentRepository,
    CancellationToken ct)
  {
    if (!await documentRepository.EntryExistsAsync(id, ct))
    {
      return Results.NotFound(new ProblemDetails
      {
        Title = "Entry not found",
        Detail = $"No queue entry with id '{id}' exists."
      });
    }

    IReadOnlyList<DocumentMetadata> documents = await documentRepository.GetDocumentsAsync(id, ct);
    return Results.Ok(new DocumentListResponse { Documents = documents });
  }

  private static async Task<IResult> HandleGetDocumentAsync(
    string id,
    string docId,
    IDocumentRepository documentRepository,
    DocumentBlobStore blobStore,
    CancellationToken ct)
  {
    DocumentRecord? record = await documentRepository.GetDocumentAsync(id, docId, ct);
    if (record is null)
    {
      return Results.NotFound(new ProblemDetails
      {
        Title = "Document not found",
        Detail = $"No document with id '{docId}' exists for queue entry '{id}'."
      });
    }

    Stream? blobStream = await blobStore.OpenReadAsync(id, record.BlobName, ct);
    if (blobStream is null)
    {
      // See DocumentBlobStore.OpenReadAsync remarks: metadata exists but the blob doesn't. Reported distinctly
      // from "no such document at all" (404 above) — this should only happen after manual interference with
      // the emulator's data, so a 500 correctly signals "something's wrong with the store", not "bad request".
      return Results.Problem(
        title: "Document content missing",
        detail: $"Document '{docId}' has metadata but its content could not be found in Blob Storage.",
        statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.File(blobStream, record.Metadata.ContentType, record.Metadata.FileName);
  }
}
