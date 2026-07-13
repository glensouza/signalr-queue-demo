using SignalRQueueDemo.Contracts;
using SignalRQueueDemo.Shared.Persistence;
using SignalRQueueDemo.Shared.Persistence.Blob;

namespace SignalRQueueDemo.Shared.Documents;

/// <summary>Which of the possible results a call to <see cref="DocumentUploadService.UploadAsync"/> produced.</summary>
public enum DocumentUploadOutcome
{
  /// <summary>The document was validated, stored, and recorded; <see cref="DocumentUploadResult.Metadata"/> is populated.</summary>
  Success,

  /// <summary><paramref name="entryId"/> in <see cref="DocumentUploadService.UploadAsync"/> doesn't match any queue entry.</summary>
  EntryNotFound,

  /// <summary>
  /// The file itself failed validation (empty, disallowed content type, or over the size cap) — see
  /// <see cref="DocumentUploadResult.FailureDetail"/> for which. Grouped under one outcome because every caller
  /// maps all three to the same "bad request" response shape, just with different wording.
  /// </summary>
  Rejected,

  /// <summary>The entry already has <see cref="DocumentUploadService.MaxDocumentsPerEntry"/> documents.</summary>
  LimitReached
}

/// <summary>
/// Result of a <see cref="DocumentUploadService.UploadAsync"/> call. Carries a human-readable
/// <see cref="FailureDetail"/> (not just the outcome enum) so every caller — the REST endpoint's
/// <c>ValidationProblem</c>/<c>ProblemDetails</c> responses and Blazor's inline form validation alike — shows the
/// identical wording instead of each inventing its own message for the same failure.
/// </summary>
public sealed record DocumentUploadResult
{
  public required DocumentUploadOutcome Outcome { get; init; }
  public string? FailureDetail { get; init; }
  public DocumentMetadata? Metadata { get; init; }

  /// <summary>
  /// The broadcast-ready update for connected clients, populated on <see cref="DocumentUploadOutcome.Success"/>
  /// (mirrors <see cref="QueueOperationResult.Update"/>'s "the repository already built it, don't reassemble it"
  /// shape). Deliberately not published by this service itself — see type remarks — so the caller decides how:
  /// <c>QueueBroadcaster.BroadcastAsync</c> directly in ApiService's own process, or Blazor's
  /// <c>QueueRealtimeService.PublishAsync</c> (a hub round-trip) from Web's.
  /// </summary>
  public QueueUpdated? Update { get; init; }

  public static DocumentUploadResult Success(DocumentMetadata metadata, QueueUpdated? update) =>
    new() { Outcome = DocumentUploadOutcome.Success, Metadata = metadata, Update = update };

  public static DocumentUploadResult EntryNotFound() => new() { Outcome = DocumentUploadOutcome.EntryNotFound };

  public static DocumentUploadResult Rejected(string detail) =>
    new() { Outcome = DocumentUploadOutcome.Rejected, FailureDetail = detail };

  public static DocumentUploadResult LimitReached() => new() { Outcome = DocumentUploadOutcome.LimitReached };
}

/// <summary>
/// The validation + storage logic behind "upload a document against a queue entry" — moved out of
/// <c>SignalRQueueDemo.ApiService.Endpoints.DocumentEndpoints.HandleUploadDocumentAsync</c> so Blazor's own
/// upload page (which calls this directly, bypassing the REST endpoint entirely per the "Blazor is
/// self-encapsulated" decision) enforces the exact same content-type allowlist, size cap, and per-entry
/// document cap as the API — one set of rules, not two hand-kept-in-sync copies. What stays endpoint-specific
/// in <c>DocumentEndpoints</c> is only the HTTP-transport concern of reading the multipart body by hand so the
/// check-in token can be validated before any of it is buffered; everything after "here are the file's bytes,
/// name, content type, and length" lives here.
/// </summary>
public sealed class DocumentUploadService(
  IDocumentRepository documentRepository,
  DocumentBlobStore blobStore,
  IQueueRepository queueRepository)
{
  private readonly IDocumentRepository documentRepository = documentRepository;
  private readonly DocumentBlobStore blobStore = blobStore;
  private readonly IQueueRepository queueRepository = queueRepository;

  /// <summary>
  /// Content types accepted on upload. Deliberately small and court-relevant: scanned or photographed
  /// supporting paperwork, nothing executable or script-bearing.
  /// </summary>
  public static readonly string[] AllowedContentTypes = ["application/pdf", "image/jpeg", "image/png"];

  /// <summary>
  /// Upload size cap. 10 MB comfortably covers a multi-page scanned PDF or a phone photo while bounding how
  /// much a public, unauthenticated upload path can make the store write per request.
  /// </summary>
  public const long MaxDocumentSizeBytes = 10 * 1024 * 1024;

  /// <summary>
  /// Cap on documents per queue entry. Upload is reachable without staff auth (the kiosk path) and, from the
  /// Blazor side, without even the REST check-in-token gate, so without a bound an actor who learns one entry id
  /// could push unlimited 10 MB blobs against it — a storage-exhaustion vector the per-file size cap alone
  /// doesn't cover. A soft cap (checked, not transactionally enforced — two truly simultaneous uploads could
  /// both pass it) is enough to bound that at the POC's scale, where a visitor legitimately attaches at most a
  /// handful of supporting documents.
  /// </summary>
  public const int MaxDocumentsPerEntry = 10;

  /// <summary>
  /// Validates and stores one document against <paramref name="entryId"/>. Callers must already know
  /// <paramref name="content"/>'s length up front (transport-specific, so callers compute it their own way —
  /// <c>IFormFile.Length</c> for ApiService, <c>IBrowserFile.Size</c> for Blazor) rather than this method
  /// buffering the stream to measure it itself.
  /// </summary>
  public async Task<DocumentUploadResult> UploadAsync(
    string entryId, string fileName, string contentType, long sizeBytes, Stream content, CancellationToken ct = default)
  {
    if (!await this.documentRepository.EntryExistsAsync(entryId, ct))
    {
      return DocumentUploadResult.EntryNotFound();
    }

    // Empty-file check is its own branch (not folded into the content-type test below) so a 0-byte upload gets a
    // message about the actual problem — an empty file — rather than a misleading "content type" error when the
    // content type was in fact valid.
    if (sizeBytes == 0)
    {
      return DocumentUploadResult.Rejected("File is empty.");
    }

    // Content-type allowlist: an upload path that ultimately accepts arbitrary bytes from the public is exactly
    // where an executable or script masquerading as a "document" would be aimed. Checked against the caller-
    // reported content type, not the file's magic bytes — a deliberate, honestly-documented POC-level limit (a
    // malicious client can lie about its own content type); see docs/decisions.md for why byte-sniffing was
    // judged not worth the added complexity here.
    if (!AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
    {
      return DocumentUploadResult.Rejected($"Content type must be one of: {string.Join(", ", AllowedContentTypes)}.");
    }

    if (sizeBytes > MaxDocumentSizeBytes)
    {
      return DocumentUploadResult.Rejected($"File exceeds the {MaxDocumentSizeBytes / (1024 * 1024)} MB upload limit.");
    }

    // Per-entry document cap — see MaxDocumentsPerEntry for why upload needs a volume bound, not just a
    // per-file size bound.
    IReadOnlyList<DocumentMetadata> existing = await this.documentRepository.GetDocumentsAsync(entryId, ct);
    if (existing.Count >= MaxDocumentsPerEntry)
    {
      return DocumentUploadResult.LimitReached();
    }

    // Randomized blob name — never the caller's filename. See DocumentBlobStore's type remarks for why a
    // client-controlled storage name is never trusted here.
    string blobName = DocumentBlobStore.NewBlobName();

    // Blob write before metadata write, not the other way round: if the metadata write below fails after this
    // succeeds, the result is an orphaned blob (harmless, cleanable later) rather than a metadata row that
    // promises content that was never actually stored.
    await this.blobStore.UploadAsync(entryId, blobName, contentType, content, ct);

    DocumentMetadata metadata = await this.documentRepository.AddDocumentAsync(
      entryId, fileName, contentType, sizeBytes, blobName, ct);

    // Push a fresh snapshot so every connected client's per-entry DocumentCount updates live — this is what lets
    // the staff console reveal its "view documents" control the moment a waiting visitor attaches a file. Never
    // published by this method itself (see DocumentUploadResult.Update remarks) — the document is already
    // durably stored, so a failed or entry-less (null) update must never turn a successful upload into an error
    // for the caller to handle.
    QueueUpdated? update = await this.queueRepository.RecordDocumentChangeAsync(entryId, ct);

    return DocumentUploadResult.Success(metadata, update);
  }
}
