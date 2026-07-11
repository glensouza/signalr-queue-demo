using Microsoft.AspNetCore.Mvc;
using SignalRQueueDemo.ApiService.Persistence;
using SignalRQueueDemo.ApiService.Persistence.Blob;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Endpoints;

/// <summary>
/// Maps the document upload/viewing endpoints: public upload against a queue entry, staff listing and
/// single-document streaming. The two staff endpoints are unauthenticated for now — a mock staff-auth filter
/// (a static <c>X-Staff-Key</c> header modeling the internal-vs-public trust boundary) is added separately,
/// across these endpoints plus call-next/complete; see the <c>TODO</c> comment at each mapping below.
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
  /// Ceiling the framework buffers a multipart upload body to, applied via <c>WithFormOptions</c> *before* form
  /// binding. <see cref="MaxDocumentSizeBytes"/> alone only rejects an oversized file *after* the whole body has
  /// been buffered (the handler can't see <see cref="IFormFile.Length"/> until then), so on a public endpoint it
  /// bounds the blob write but not the buffering. This outer limit closes that gap: a grossly oversized body is
  /// cut off by the form reader instead of spooled in full. Set to the file cap plus 64 KB of headroom for the
  /// multipart envelope (boundary + part headers) so a legitimately ≤10 MB file always fits.
  /// </summary>
  private const long MaxUploadBodyBytes = MaxDocumentSizeBytes + (64 * 1024);

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
    // .DisableAntiforgery(): ASP.NET Core's minimal APIs auto-attach antiforgery metadata to any endpoint that
    // binds IFormFile, requiring app.UseAntiforgery() (cookie-based) to be wired up or every request 500s. This
    // POC never adds that middleware — the public-endpoint hardening it uses is a different, stateless mechanism
    // (a rotating HMAC token a kiosk fetches and echoes back), chosen specifically because a public kiosk SPA
    // can't rely on a same-site cookie the way ASP.NET Core's built-in antiforgery system assumes. This call
    // opts out of the built-in system so upload works; that token check still applies to this route as it does
    // to every other public endpoint.
    //
    // .WithFormOptions(...): bounds how much the framework will buffer for this endpoint *before* the handler
    // runs — see MaxUploadBodyBytes for why the in-handler size check isn't enough on its own.
    app.MapPost("/checkin/{id}/documents", HandleUploadDocumentAsync)
      .DisableAntiforgery()
      .WithFormOptions(bufferBodyLengthLimit: MaxUploadBodyBytes, multipartBodyLengthLimit: MaxUploadBodyBytes);

    // TODO: both of these sit behind mock staff auth (a static X-Staff-Key header) once that endpoint filter
    // lands — it gates document viewing alongside call-next/complete. Left unauthenticated here so the document
    // feature can ship and be exercised end-to-end on its own.
    app.MapGet("/queue/{id}/documents", HandleListDocumentsAsync);
    app.MapGet("/queue/{id}/documents/{docId}", HandleGetDocumentAsync);
  }

  private static async Task<IResult> HandleUploadDocumentAsync(
    string id,
    IFormFile file,
    IDocumentRepository documentRepository,
    DocumentBlobStore blobStore,
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
