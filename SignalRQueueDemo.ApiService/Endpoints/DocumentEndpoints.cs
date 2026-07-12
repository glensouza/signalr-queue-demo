using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using SignalRQueueDemo.ApiService.Hubs;
using SignalRQueueDemo.ApiService.Persistence;
using SignalRQueueDemo.ApiService.Persistence.Blob;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Endpoints;

/// <summary>
/// Maps the document upload/viewing endpoints: public upload against a queue entry, staff listing and
/// single-document streaming. All three carry the <see cref="CorsPolicies.KnownFrontends"/> CORS policy; upload
/// additionally validates the check-in token (inline — see <see cref="HandleUploadDocumentAsync"/>), and the two
/// staff endpoints are gated by <see cref="StaffAuthFilter"/>, alongside call-next/complete.
/// </summary>
public static class DocumentEndpoints
{
  /// <summary>
  /// Content types accepted on upload. Deliberately small and court-relevant: scanned or photographed
  /// supporting paperwork, nothing executable or script-bearing.
  /// </summary>
  private static readonly string[] AllowedContentTypes = ["application/pdf", "image/jpeg", "image/png"];

  /// <summary>
  /// Upload size cap. 10 MB comfortably covers a multi-page scanned PDF or a phone photo while bounding how
  /// much a public, unauthenticated endpoint can make the API write to Blob Storage per request.
  /// </summary>
  private const long MaxDocumentSizeBytes = 10 * 1024 * 1024;

  /// <summary>
  /// Ceiling the form reader buffers a multipart upload body to. <see cref="MaxDocumentSizeBytes"/> alone only
  /// rejects an oversized file *after* the whole body has been buffered (the handler can't see
  /// <see cref="IFormFile.Length"/> until then), so on a public endpoint it bounds the blob write but not the
  /// buffering. This outer limit closes that gap: a grossly oversized body is cut off by the form reader instead
  /// of spooled in full. Set to the file cap plus 64 KB of headroom for the multipart envelope (boundary + part
  /// headers) so a legitimately ≤10 MB file always fits. Applied via <see cref="UploadFormOptions"/>.
  /// </summary>
  private const long MaxUploadBodyBytes = MaxDocumentSizeBytes + (64 * 1024);

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

  /// <summary>
  /// Cap on documents per queue entry. The upload endpoint is public and unauthenticated, so without a bound an
  /// actor who learns one entry id (returned in plaintext by GET /queue) could push unlimited 10 MB blobs against
  /// it — a storage-exhaustion vector the per-file size cap alone doesn't cover. A soft cap (checked, not
  /// transactionally enforced — two truly simultaneous uploads could both pass it) is enough to bound that at the
  /// POC's scale, where a visitor legitimately attaches at most a handful of supporting documents.
  /// </summary>
  private const int MaxDocumentsPerEntry = 10;

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
    IDocumentRepository documentRepository,
    DocumentBlobStore blobStore,
    IQueueRepository queueRepository,
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

    // Entry existence next, still before touching the body: an upload against an unknown entry id is turned away
    // without buffering anything either.
    if (!await documentRepository.EntryExistsAsync(id, ct))
    {
      return Results.NotFound(new ProblemDetails
      {
        Title = "Entry not found",
        Detail = $"No queue entry with id '{id}' exists."
      });
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

    // Empty-file check is its own branch (not folded into the content-type test below) so a 0-byte upload gets a
    // message about the actual problem — an empty file — rather than a misleading "content type" error when the
    // content type was in fact valid.
    if (file.Length == 0)
    {
      return Results.ValidationProblem(new Dictionary<string, string[]>
      {
        ["file"] = ["File is empty."]
      });
    }

    // Content-type allowlist: a public, no-auth endpoint that accepts arbitrary uploads is exactly where an
    // executable or script masquerading as a "document" would be aimed. Checked against the client-reported
    // Content-Type, not the file's magic bytes — a deliberate, honestly-documented POC-level limit (a
    // malicious client can lie about its own Content-Type header); see docs/decisions.md for why byte-sniffing
    // was judged not worth the added complexity here.
    if (!AllowedContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
    {
      return Results.ValidationProblem(new Dictionary<string, string[]>
      {
        ["file"] = [$"Content type must be one of: {string.Join(", ", AllowedContentTypes)}."]
      });
    }

    // Enforced against IFormFile.Length — what Kestrel actually buffered — not a client-declared header, so it
    // can't be bypassed by lying about Content-Length. (MaxUploadBodyBytes on the endpoint is the outer guard
    // that stops a far-larger body from being buffered at all before this precise check runs.)
    if (file.Length > MaxDocumentSizeBytes)
    {
      return Results.ValidationProblem(new Dictionary<string, string[]>
      {
        ["file"] = [$"File exceeds the {MaxDocumentSizeBytes / (1024 * 1024)} MB upload limit."]
      });
    }

    // Per-entry document cap — see MaxDocumentsPerEntry for why a public upload endpoint needs a volume bound,
    // not just a per-file size bound. 409 Conflict (not 400) because the request itself is well-formed; it's the
    // entry's current state — already at capacity — that rejects it.
    IReadOnlyList<DocumentMetadata> existing = await documentRepository.GetDocumentsAsync(id, ct);
    if (existing.Count >= MaxDocumentsPerEntry)
    {
      return Results.Conflict(new ProblemDetails
      {
        Title = "Document limit reached",
        Detail = $"Queue entry '{id}' already has the maximum of {MaxDocumentsPerEntry} documents."
      });
    }

    // Randomized blob name — never file.FileName. See DocumentBlobStore's type remarks for why a
    // client-controlled storage name is never trusted on a public endpoint.
    string blobName = DocumentBlobStore.NewBlobName();

    // Blob write before metadata write, not the other way round: if the metadata write below fails after this
    // succeeds, the result is an orphaned blob (harmless, cleanable later) rather than a metadata row that
    // promises content that was never actually stored. That ordering is also why DocumentBlobStore.OpenReadAsync
    // can return null for a metadata row that does exist — see its remarks.
    await using (Stream stream = file.OpenReadStream())
    {
      await blobStore.UploadAsync(id, blobName, file.ContentType, stream, ct);
    }

    DocumentMetadata metadata = await documentRepository.AddDocumentAsync(
      id, file.FileName, file.ContentType, file.Length, blobName, ct);

    // Push a fresh snapshot so every connected client's per-entry DocumentCount updates live — this is what lets
    // the staff console reveal its "view documents" control the moment a waiting visitor attaches a file, instead
    // of only after the next call-next/complete. Best-effort, mirroring how the queue mutations broadcast: the
    // document is already durably stored, so a failed or entry-less (null) push must never turn a successful
    // upload into an error. RecordDocumentChangeAsync returns null only if the entry vanished between the
    // existence check above and here — nothing to broadcast in that case.
    QueueUpdated? update = await queueRepository.RecordDocumentChangeAsync(id, ct);
    if (update is not null)
    {
      await broadcaster.BroadcastAsync(update);
    }

    return Results.Created(
      $"/queue/{id}/documents/{metadata.Id}", new DocumentUploadResponse { Document = metadata });
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
