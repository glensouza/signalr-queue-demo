using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Persistence.Sqlite;

/// <summary>
/// EF Core-mapped row for one uploaded document's metadata — the SQLite counterpart of
/// <see cref="TableStorage.DocumentTableEntity"/>. A separate table from <see cref="QueueEntryEntity"/> (not
/// extra columns on it) because an entry can have zero-to-many documents; <see cref="EntryId"/> is a plain
/// value copy, mirroring how <see cref="QueueChangeEventEntity"/> already copies entry fields rather than using
/// an EF navigation/foreign key, so this table never needs to join back to <c>QueueEntries</c> just to list.
/// </summary>
public sealed class DocumentEntity
{
  /// <summary>Primary key; mirrors <see cref="DocumentMetadata.Id"/>.</summary>
  public required string Id { get; set; }

  /// <summary>The queue entry this document was uploaded against — indexed (see <see cref="QueueDbContext"/>) since every read filters by it.</summary>
  public required string EntryId { get; set; }

  /// <summary>Mirrors <see cref="DocumentMetadata.FileName"/> — display-only, never used to derive <see cref="BlobName"/>.</summary>
  public required string FileName { get; set; }

  /// <summary>Mirrors <see cref="DocumentMetadata.ContentType"/>.</summary>
  public required string ContentType { get; set; }

  /// <summary>Mirrors <see cref="DocumentMetadata.SizeBytes"/>.</summary>
  public required long SizeBytes { get; set; }

  /// <summary>Mirrors <see cref="DocumentMetadata.UploadedAt"/>.</summary>
  public required DateTimeOffset UploadedAt { get; set; }

  /// <summary>The randomized name this document's content is stored under in Blob Storage — never the client-supplied filename (see the upload endpoint).</summary>
  public required string BlobName { get; set; }

  /// <summary>Maps this row to the wire-facing <see cref="DocumentMetadata"/> record.</summary>
  public DocumentMetadata ToContract() => new()
  {
    Id = this.Id,
    EntryId = this.EntryId,
    FileName = this.FileName,
    ContentType = this.ContentType,
    SizeBytes = this.SizeBytes,
    UploadedAt = this.UploadedAt
  };
}
