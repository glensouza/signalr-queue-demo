using Microsoft.EntityFrameworkCore;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Persistence.Sqlite;

/// <summary>
/// SQLite/EF Core implementation of <see cref="IQueueRepository"/> — the default persistence backend for
/// this POC. Every mutation writes both the entry row and a <see cref="QueueChangeEventEntity"/> in a single
/// <c>SaveChangesAsync</c> call, so the entry update and its event-log row commit atomically in one SQLite
/// transaction and the sequence number they share can never be observed half-written.
/// </summary>
public sealed class SqliteQueueRepository(QueueDbContext dbContext) : IQueueRepository
{
  // Explicit field (not a bare captured primary-constructor parameter) so call sites can use the
  // this.dbContext prefix required by CLAUDE.md's C# style for instance members.
  private readonly QueueDbContext dbContext = dbContext;

  /// <summary>
  /// Placeholder for "who called this entry". Real staff identity arrives with mock auth in issue #6;
  /// until then every call-next is attributed to this fixed value so the field isn't left silently null.
  /// </summary>
  private const string MockStaffIdentity = "front-desk-mock";

  public async Task<CheckInResponse> CheckInAsync(CheckInRequest request, CancellationToken ct = default)
  {
    int position = await this.dbContext.Entries.CountAsync(e => e.Status == QueueStatus.Waiting, ct) + 1;

    QueueEntryEntity entity = new()
    {
      Id = Guid.NewGuid().ToString(),
      DisplayName = request.DisplayName,
      TicketNumber = request.TicketNumber,
      CheckedInAt = DateTimeOffset.UtcNow,
      Status = QueueStatus.Waiting
    };

    this.dbContext.Entries.Add(entity);
    QueueChangeEventEntity changeEvent = QueueChangeEventEntity.FromEntry(entity);
    this.dbContext.ChangeEvents.Add(changeEvent);

    await this.dbContext.SaveChangesAsync(ct);

    return new CheckInResponse
    {
      EntryId = entity.Id,
      Position = position,
      SequenceNumber = changeEvent.SequenceNumber,
      Entry = entity.ToContract()
    };
  }

  public async Task<QueueOperationResult> CallNextAsync(CancellationToken ct = default)
  {
    QueueEntryEntity? entity = await this.dbContext.Entries
      .Where(e => e.Status == QueueStatus.Waiting)
      .OrderBy(e => e.CheckedInAt)
      .FirstOrDefaultAsync(ct);

    if (entity is null)
    {
      return QueueOperationResult.Failure(QueueOperationOutcome.NoWaitingEntries);
    }

    entity.Status = QueueStatus.Serving;
    entity.ServedBy = MockStaffIdentity;
    entity.ServedAt = DateTimeOffset.UtcNow;

    return QueueOperationResult.Success(await this.RecordChangeAndBuildUpdateAsync(entity, ct));
  }

  public async Task<QueueOperationResult> CompleteAsync(string entryId, CancellationToken ct = default)
  {
    QueueEntryEntity? entity = await this.dbContext.Entries.FirstOrDefaultAsync(e => e.Id == entryId, ct);

    if (entity is null)
    {
      return QueueOperationResult.Failure(QueueOperationOutcome.EntryNotFound);
    }

    if (entity.Status != QueueStatus.Serving)
    {
      return QueueOperationResult.Failure(QueueOperationOutcome.InvalidState);
    }

    entity.Status = QueueStatus.Completed;

    return QueueOperationResult.Success(await this.RecordChangeAndBuildUpdateAsync(entity, ct));
  }

  public async Task<QueueStateResponse> GetStateAsync(CancellationToken ct = default)
  {
    long latestSequenceNumber = await this.dbContext.ChangeEvents.MaxAsync(e => (long?)e.SequenceNumber, ct) ?? 0;

    return new QueueStateResponse
    {
      SequenceNumber = latestSequenceNumber,
      Snapshot = await this.BuildSnapshotAsync(ct)
    };
  }

  /// <summary>
  /// Shared tail for call-next/complete: appends the change event for the now-mutated <paramref name="entity"/>,
  /// saves both rows in one transaction, and builds the <see cref="QueueUpdated"/> broadcast payload.
  /// </summary>
  private async Task<QueueUpdated> RecordChangeAndBuildUpdateAsync(QueueEntryEntity entity, CancellationToken ct)
  {
    QueueChangeEventEntity changeEvent = QueueChangeEventEntity.FromEntry(entity);
    this.dbContext.ChangeEvents.Add(changeEvent);

    await this.dbContext.SaveChangesAsync(ct);

    return new QueueUpdated
    {
      SequenceNumber = changeEvent.SequenceNumber,
      ChangedEntry = entity.ToContract(),
      Summary = await this.BuildSnapshotAsync(ct)
    };
  }

  private async Task<QueueSnapshot> BuildSnapshotAsync(CancellationToken ct)
  {
    List<QueueEntryEntity> entities = await this.dbContext.Entries
      .OrderBy(e => e.CheckedInAt)
      .ToListAsync(ct);

    return new QueueSnapshot
    {
      TotalWaiting = entities.Count(e => e.Status == QueueStatus.Waiting),
      TotalServing = entities.Count(e => e.Status == QueueStatus.Serving),
      TotalCompleted = entities.Count(e => e.Status == QueueStatus.Completed),
      Queue = entities.Select(e => e.ToContract()).ToList()
    };
  }
}
