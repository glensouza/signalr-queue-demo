using Azure;
using Azure.Data.Tables;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.Shared.Persistence.TableStorage;

/// <summary>
/// Table Storage-mapped row for one uploaded document's metadata — the Table Storage counterpart of
/// <see cref="Sqlite.DocumentEntity"/>. Unlike <see cref="QueueEntryTableEntity"/>'s single constant partition,
/// this table partitions by <see cref="PartitionKey"/> = the owning entry's id: every read
/// (<see cref="TableStorageDocumentRepository.GetDocumentsAsync"/>) only ever needs one entry's documents at a
/// time, so per-entry partitioning turns that into a single-partition point query instead of a filtered scan
/// across every document ever uploaded by every visitor.
/// </summary>
public sealed class DocumentTableEntity : ITableEntity
{
  /// <summary>The owning queue entry's id (mirrors <see cref="DocumentMetadata.EntryId"/>) — see type remarks for why this, not a constant, is the partition key.</summary>
  public string PartitionKey { get; set; } = string.Empty;

  /// <summary>The document's id (mirrors <see cref="DocumentMetadata.Id"/>).</summary>
  public string RowKey { get; set; } = string.Empty;

  /// <summary>Set by the Table service on write; unused by application code.</summary>
  public DateTimeOffset? Timestamp { get; set; }

  /// <summary>Document rows are never updated after being written, so this is unused beyond satisfying <see cref="ITableEntity"/>.</summary>
  public ETag ETag { get; set; }

  /// <summary>Mirrors <see cref="DocumentMetadata.FileName"/> — display-only, never used to derive <see cref="BlobName"/>.</summary>
  public required string FileName { get; set; }

  /// <summary>Mirrors <see cref="DocumentMetadata.ContentType"/>.</summary>
  public required string ContentType { get; set; }

  /// <summary>Mirrors <see cref="DocumentMetadata.SizeBytes"/>.</summary>
  public required long SizeBytes { get; set; }

  /// <summary>Mirrors <see cref="DocumentMetadata.UploadedAt"/>. Table Storage's native DateTimeOffset column needs no conversion.</summary>
  public required DateTimeOffset UploadedAt { get; set; }

  /// <summary>The randomized name this document's content is stored under in Blob Storage.</summary>
  public required string BlobName { get; set; }

  /// <summary>Builds a new row for a just-uploaded document, with a fresh id as its RowKey.</summary>
  public static DocumentTableEntity Create(
    string entryId, string fileName, string contentType, long sizeBytes, string blobName, DateTimeOffset uploadedAt) =>
    new()
    {
      PartitionKey = entryId,
      RowKey = Guid.NewGuid().ToString(),
      FileName = fileName,
      ContentType = contentType,
      SizeBytes = sizeBytes,
      UploadedAt = uploadedAt,
      BlobName = blobName
    };

  /// <summary>Maps this row to the wire-facing <see cref="DocumentMetadata"/> record.</summary>
  public DocumentMetadata ToContract() => new()
  {
    Id = this.RowKey,
    EntryId = this.PartitionKey,
    FileName = this.FileName,
    ContentType = this.ContentType,
    SizeBytes = this.SizeBytes,
    UploadedAt = this.UploadedAt
  };
}
