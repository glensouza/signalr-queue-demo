using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.Shared.Persistence;

/// <summary>
/// Storage abstraction for uploaded-document metadata. Kept as its own interface rather than folded into
/// <see cref="IQueueRepository"/>, even though a given <c>Persistence:Provider</c> backs both with the same
/// technology — "track document metadata alongside the queue entry" means "in the same store as the entry it
/// belongs to", not "on the same interface as queue mutations/broadcasts". Splitting them means a
/// future document-metadata-only change (e.g. moving it into Blob index tags) never touches queue mutation
/// code, and <see cref="IQueueRepository"/> stays exactly what its own doc comment says it is. Like
/// <see cref="IQueueRepository"/>, this speaks only <c>SignalRQueueDemo.Contracts</c> types (plus
/// <see cref="DocumentRecord"/>, which never crosses the wire) — no EF Core or Azure SDK types leak through.
/// </summary>
public interface IDocumentRepository
{
  /// <summary>
  /// True when a queue entry with this id exists. Checked before accepting an upload or serving a list/read
  /// against an entry id, so a typo'd or stale id 404s instead of silently creating an orphaned document under
  /// an id nothing will ever look up again.
  /// </summary>
  Task<bool> EntryExistsAsync(string entryId, CancellationToken ct = default);

  /// <summary>
  /// Records metadata for a document already uploaded to Blob Storage under <paramref name="blobName"/> — the
  /// blob write happens first (see the upload endpoint for why), so by the time this is called the content this
  /// metadata describes is already durable.
  /// </summary>
  Task<DocumentMetadata> AddDocumentAsync(
    string entryId,
    string fileName,
    string contentType,
    long sizeBytes,
    string blobName,
    CancellationToken ct = default);

  /// <summary>All documents recorded against an entry, most-recently-uploaded first.</summary>
  Task<IReadOnlyList<DocumentMetadata>> GetDocumentsAsync(string entryId, CancellationToken ct = default);

  /// <summary>A single document's metadata plus the blob name it's stored under, or null if no such document exists for that entry.</summary>
  Task<DocumentRecord?> GetDocumentAsync(string entryId, string documentId, CancellationToken ct = default);

  /// <summary>
  /// Removes all document-metadata rows for an entry — the metadata half of the "delete an entry's documents once
  /// it's completed" cleanup (the blob half is <c>DocumentBlobStore.DeleteAllForEntryAsync</c>). Idempotent: an
  /// entry with no documents is a no-op, so completing an entry that never had one is harmless. Metadata is
  /// deleted before the blobs (see the complete endpoint) so a partial failure can only ever leave an orphaned
  /// blob — harmless — never a metadata row pointing at content that's already gone.
  /// </summary>
  Task DeleteDocumentsAsync(string entryId, CancellationToken ct = default);
}

/// <summary>
/// Pairs a <see cref="DocumentMetadata"/> with the randomized blob name it's stored under. This is the one
/// place a blob name crosses out of the repository layer, so the document-streaming endpoint can open the
/// right blob without needing to know the storage naming scheme itself. Not a Contracts type: unlike
/// <see cref="DocumentMetadata"/>, it never crosses the wire as-is.
/// </summary>
public sealed record DocumentRecord
{
  /// <summary>The document's wire-facing metadata.</summary>
  public required DocumentMetadata Metadata { get; init; }

  /// <summary>The randomized name the content is stored under in Blob Storage (see <c>DocumentBlobStore.NewBlobName</c>).</summary>
  public required string BlobName { get; init; }
}
