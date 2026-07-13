using Microsoft.EntityFrameworkCore;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.Shared.Persistence.Sqlite;

/// <summary>
/// SQLite/EF Core implementation of <see cref="IDocumentRepository"/> — the document-metadata counterpart of
/// <see cref="SqliteQueueRepository"/>, sharing the same <see cref="QueueDbContext"/>/connection so metadata
/// really does live "alongside" the entry it describes, in the literal single-database sense.
/// No transaction wrapping is needed here the way <see cref="SqliteQueueRepository"/> needs one for its
/// mutations: each method here is a single independent read or insert with no multi-step consistency to protect
/// (unlike, say, "insert + reread the same committed state" in check-in).
///
/// <para>
/// Uses <see cref="IDbContextFactory{TContext}"/>, not an injected <see cref="QueueDbContext"/> — a fresh,
/// short-lived context per call, safe to call concurrently from multiple threads. See
/// <see cref="SqliteQueueRepository"/>'s type remarks for why (this repository is resolved once per Blazor
/// circuit, same as that one, and called from the same mix of threads).
/// </para>
/// </summary>
public sealed class SqliteDocumentRepository(IDbContextFactory<QueueDbContext> dbContextFactory) : IDocumentRepository
{
  // Explicit field (not a bare captured primary-constructor parameter) so call sites can use the
  // this.dbContextFactory prefix required by CLAUDE.md's C# style for instance members.
  private readonly IDbContextFactory<QueueDbContext> dbContextFactory = dbContextFactory;

  public async Task<bool> EntryExistsAsync(string entryId, CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);
    return await dbContext.Entries.AnyAsync(e => e.Id == entryId, ct);
  }

  public async Task<DocumentMetadata> AddDocumentAsync(
    string entryId,
    string fileName,
    string contentType,
    long sizeBytes,
    string blobName,
    CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);

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

    dbContext.Documents.Add(entity);
    await dbContext.SaveChangesAsync(ct);

    return entity.ToContract();
  }

  public async Task<IReadOnlyList<DocumentMetadata>> GetDocumentsAsync(string entryId, CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);

    List<DocumentEntity> entities = await dbContext.Documents
      .Where(d => d.EntryId == entryId)
      .OrderByDescending(d => d.UploadedAt)
      .ToListAsync(ct);

    return entities.Select(e => e.ToContract()).ToList();
  }

  public async Task<DocumentRecord?> GetDocumentAsync(string entryId, string documentId, CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);

    DocumentEntity? entity = await dbContext.Documents
      .FirstOrDefaultAsync(d => d.EntryId == entryId && d.Id == documentId, ct);

    return entity is null ? null : new DocumentRecord { Metadata = entity.ToContract(), BlobName = entity.BlobName };
  }

  public async Task DeleteDocumentsAsync(string entryId, CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);

    // ExecuteDeleteAsync issues a single DELETE ... WHERE rather than loading each row to delete it — the rows
    // aren't needed here (the blobs are cleaned up separately by their container), and it's a no-op when the
    // entry has none.
    await dbContext.Documents.Where(d => d.EntryId == entryId).ExecuteDeleteAsync(ct);
  }
}
