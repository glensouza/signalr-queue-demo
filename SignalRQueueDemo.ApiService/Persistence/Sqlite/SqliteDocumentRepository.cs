using Microsoft.EntityFrameworkCore;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Persistence.Sqlite;

/// <summary>
/// SQLite/EF Core implementation of <see cref="IDocumentRepository"/> — the document-metadata counterpart of
/// <see cref="SqliteQueueRepository"/>, sharing the same <see cref="QueueDbContext"/>/connection so metadata
/// really does live "alongside" the entry it describes, in the literal single-database sense.
/// No transaction wrapping is needed here the way <see cref="SqliteQueueRepository"/> needs one for its
/// mutations: each method here is a single independent read or insert with no multi-step consistency to protect
/// (unlike, say, "insert + reread the same committed state" in check-in).
/// </summary>
public sealed class SqliteDocumentRepository(QueueDbContext dbContext) : IDocumentRepository
{
  // Explicit field (not a bare captured primary-constructor parameter) so call sites can use the
  // this.dbContext prefix required by CLAUDE.md's C# style for instance members.
  private readonly QueueDbContext dbContext = dbContext;

  public Task<bool> EntryExistsAsync(string entryId, CancellationToken ct = default) =>
    this.dbContext.Entries.AnyAsync(e => e.Id == entryId, ct);

  public async Task<DocumentMetadata> AddDocumentAsync(
    string entryId,
    string fileName,
    string contentType,
    long sizeBytes,
    string blobName,
    CancellationToken ct = default)
  {
    DocumentEntity entity = new()
    {
      Id = Guid.NewGuid().ToString(),
      EntryId = entryId,
      FileName = fileName,
      ContentType = contentType,
      SizeBytes = sizeBytes,
      UploadedAt = DateTimeOffset.UtcNow,
      BlobName = blobName
    };

    this.dbContext.Documents.Add(entity);
    await this.dbContext.SaveChangesAsync(ct);

    return entity.ToContract();
  }

  public async Task<IReadOnlyList<DocumentMetadata>> GetDocumentsAsync(string entryId, CancellationToken ct = default)
  {
    List<DocumentEntity> entities = await this.dbContext.Documents
      .Where(d => d.EntryId == entryId)
      .OrderByDescending(d => d.UploadedAt)
      .ToListAsync(ct);

    return entities.Select(e => e.ToContract()).ToList();
  }

  public async Task<DocumentRecord?> GetDocumentAsync(string entryId, string documentId, CancellationToken ct = default)
  {
    DocumentEntity? entity = await this.dbContext.Documents
      .FirstOrDefaultAsync(d => d.EntryId == entryId && d.Id == documentId, ct);

    return entity is null ? null : new DocumentRecord { Metadata = entity.ToContract(), BlobName = entity.BlobName };
  }
}
