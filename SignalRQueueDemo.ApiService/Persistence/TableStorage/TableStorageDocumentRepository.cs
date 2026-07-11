using System.Net;
using Azure;
using Azure.Data.Tables;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Persistence.TableStorage;

/// <summary>
/// Azure Table Storage implementation of <see cref="IDocumentRepository"/> — the document-metadata counterpart
/// of <see cref="TableStorageQueueRepository"/>. Reuses that type's entries table (read-only, via
/// <see cref="TableStorageQueueRepository.EntriesTableName"/>) to answer <see cref="EntryExistsAsync"/> rather
/// than duplicating an entries table of its own, and owns a separate <see cref="DocumentsTableName"/> table
/// (partitioned by entry id — see <see cref="DocumentTableEntity"/>) for the metadata rows themselves. Both
/// tables are created at startup by <c>Program.cs</c>, the same place <see cref="TableStorageQueueSeedData"/>
/// creates its three; this repository assumes they already exist rather than lazily creating them per call.
/// </summary>
public sealed class TableStorageDocumentRepository : IDocumentRepository
{
  /// <summary>Table name for uploaded-document metadata rows — see <see cref="DocumentTableEntity"/>.</summary>
  public const string DocumentsTableName = "QueueDocuments";

  private readonly TableClient entriesTable;
  private readonly TableClient documentsTable;

  public TableStorageDocumentRepository(TableServiceClient tableServiceClient)
  {
    this.entriesTable = tableServiceClient.GetTableClient(TableStorageQueueRepository.EntriesTableName);
    this.documentsTable = tableServiceClient.GetTableClient(DocumentsTableName);
  }

  public async Task<bool> EntryExistsAsync(string entryId, CancellationToken ct = default)
  {
    try
    {
      await this.entriesTable.GetEntityAsync<QueueEntryTableEntity>(
        QueueEntryTableEntity.PartitionKeyValue, entryId, cancellationToken: ct);
      return true;
    }
    catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
    {
      return false;
    }
  }

  public async Task<DocumentMetadata> AddDocumentAsync(
    string entryId,
    string fileName,
    string contentType,
    long sizeBytes,
    string blobName,
    CancellationToken ct = default)
  {
    DocumentTableEntity entity = DocumentTableEntity.Create(
      entryId, fileName, contentType, sizeBytes, blobName, DateTimeOffset.UtcNow);
    await this.documentsTable.AddEntityAsync(entity, ct);
    return entity.ToContract();
  }

  public async Task<IReadOnlyList<DocumentMetadata>> GetDocumentsAsync(string entryId, CancellationToken ct = default)
  {
    // Single-partition query (see DocumentTableEntity remarks) — no PartitionKey/RowKey filter needed beyond
    // the partition scope itself, since every row under this partition belongs to this entry.
    List<DocumentTableEntity> entities = [];
    await foreach (DocumentTableEntity entity in this.documentsTable.QueryAsync<DocumentTableEntity>(
      e => e.PartitionKey == entryId, cancellationToken: ct))
    {
      entities.Add(entity);
    }

    return entities.OrderByDescending(e => e.UploadedAt).Select(e => e.ToContract()).ToList();
  }

  public async Task<DocumentRecord?> GetDocumentAsync(string entryId, string documentId, CancellationToken ct = default)
  {
    try
    {
      Response<DocumentTableEntity> response = await this.documentsTable.GetEntityAsync<DocumentTableEntity>(
        entryId, documentId, cancellationToken: ct);
      return new DocumentRecord { Metadata = response.Value.ToContract(), BlobName = response.Value.BlobName };
    }
    catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
    {
      return null;
    }
  }
}
