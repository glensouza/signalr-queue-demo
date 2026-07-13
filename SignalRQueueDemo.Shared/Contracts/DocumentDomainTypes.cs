namespace SignalRQueueDemo.Contracts;

/// <summary>
/// Metadata for a document uploaded against a queue entry. Tracked alongside the entry itself (see
/// <c>SignalRQueueDemo.ApiService.Persistence.IDocumentRepository</c>) rather than derived from a live Blob
/// Storage container listing, so the staff console can render a document list with one metadata read instead
/// of a per-page-view enumeration of blob contents. Deliberately never carries the randomized blob name that
/// content is actually stored under — that's an internal storage-layer detail the API resolves when streaming
/// a document back, not something a wire client needs.
/// </summary>
public sealed record DocumentMetadata
{
  /// <summary>Server-assigned unique identifier for the document, independent of its storage blob name.</summary>
  public required string Id { get; init; }

  /// <summary>The queue entry this document was uploaded against.</summary>
  public required string EntryId { get; init; }

  /// <summary>
  /// The filename as uploaded, shown to staff for their own reference. Never used to derive the blob's storage
  /// name or path — see the upload endpoint for why a client-supplied filename is never trusted for that.
  /// </summary>
  public required string FileName { get; init; }

  /// <summary>The MIME type reported by the uploader, checked against an allowlist at upload time.</summary>
  public required string ContentType { get; init; }

  /// <summary>Size in bytes, checked against a cap at upload time.</summary>
  public required long SizeBytes { get; init; }

  /// <summary>When the document was uploaded.</summary>
  public required DateTimeOffset UploadedAt { get; init; }
}

/// <summary>The response to a successful <c>POST /checkin/{id}/documents</c>: the metadata row just created.</summary>
public sealed record DocumentUploadResponse
{
  /// <summary>The newly recorded document.</summary>
  public required DocumentMetadata Document { get; init; }
}

/// <summary>The response to <c>GET /queue/{id}/documents</c>: every document uploaded against that entry.</summary>
public sealed record DocumentListResponse
{
  /// <summary>Documents for the requested entry, most-recently-uploaded first.</summary>
  public required IReadOnlyList<DocumentMetadata> Documents { get; init; }
}
