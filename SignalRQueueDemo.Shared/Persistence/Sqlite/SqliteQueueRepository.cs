using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.Shared.Persistence.Sqlite;

/// <summary>
/// SQLite/EF Core implementation of <see cref="IQueueRepository"/> — the default persistence backend for
/// this POC. Every mutation writes both the entry row and a <see cref="QueueChangeEventEntity"/> in a single
/// <c>SaveChangesAsync</c> call, so the entry update and its event-log row commit atomically in one SQLite
/// transaction and the sequence number they share can never be observed half-written.
///
/// <para>
/// Each mutation additionally wraps its <c>SaveChangesAsync</c> and the follow-up snapshot/position reads in
/// one explicit transaction. SQLite serializes writers, so once <c>SaveChangesAsync</c> has taken the write
/// lock no other writer can commit until this transaction ends — which means the snapshot and the position
/// this method reads back reflect exactly this change and nothing newer. Without the transaction those reads
/// run in their own autocommit statements, and a concurrent commit landing in between made the broadcast
/// <see cref="QueueUpdated.Summary"/> newer than its own <see cref="QueueUpdated.SequenceNumber"/> and let two
/// simultaneous check-ins read the same pre-insert position count.
/// </para>
///
/// <para>
/// <b>Why a <see cref="IDbContextFactory{TContext}"/>, not an injected <see cref="QueueDbContext"/>:</b> a
/// directly-injected DbContext is scoped to whatever DI scope constructed this repository. In ApiService that's
/// one HTTP request — fine. In Blazor Server, this repository is resolved once per <em>circuit</em> (one per
/// browser tab, potentially open for a long session) and called concurrently from multiple sources: component
/// event handlers on the circuit's renderer thread, <c>QueueRealtimeService</c>'s polling timer, and its
/// reconnect-triggered catch-up — all on different threads. A single long-lived DbContext is neither thread-safe
/// (concurrent use throws "A second operation was started on this context instance") nor fresh (its identity map
/// would keep returning the first-loaded, increasingly stale copy of each entity instead of reflecting writes
/// ApiService's own separate process makes to the same database). Creating a short-lived context per method call
/// via the factory fixes both: safe to call from any thread, and always reads current state.
/// </para>
/// </summary>
public sealed class SqliteQueueRepository(IDbContextFactory<QueueDbContext> dbContextFactory) : IQueueRepository
{
  // Explicit field (not a bare captured primary-constructor parameter) so call sites can use the
  // this.dbContextFactory prefix required by CLAUDE.md's C# style for instance members.
  private readonly IDbContextFactory<QueueDbContext> dbContextFactory = dbContextFactory;

  /// <summary>
  /// Placeholder for "who called this entry". Real staff identity arrives with mock auth (a static X-Staff-Key
  /// header); until then every call-next is attributed to this fixed value so the field isn't left silently null.
  /// </summary>
  private const string MockStaffIdentity = "front-desk-mock";

  /// <summary>
  /// Above this many missed events, GET /queue/since/{seq} returns a full snapshot instead of the raw diff.
  /// A client that's been offline long enough to miss this many changes gains nothing from replaying them one
  /// at a time — a snapshot is a single bounded payload instead of one that grows without limit the longer a
  /// client stays disconnected, and it's the same end state the client would compute from the diff anyway.
  /// </summary>
  private const int MaxCatchUpEvents = 200;

  public async Task<CheckInResult> CheckInAsync(CheckInRequest request, CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);
    await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(ct);

    QueueEntryEntity entity = new()
    {
      Id = Guid.NewGuid().ToString(),
      DisplayName = request.DisplayName,
      TicketNumber = request.TicketNumber,
      CheckedInAt = DateTimeOffset.UtcNow,
      Status = QueueStatus.Waiting
    };

    dbContext.Entries.Add(entity);
    QueueChangeEventEntity changeEvent = QueueChangeEventEntity.FromEntry(entity);
    dbContext.ChangeEvents.Add(changeEvent);

    await dbContext.SaveChangesAsync(ct);

    // Position is counted AFTER the insert, inside the write lock SaveChangesAsync just took: the new entry is
    // itself Waiting now, so the count of all Waiting entries IS this visitor's 1-based position. Because
    // SQLite serializes writers, a concurrent check-in has either already committed (and is counted) or is
    // still blocked on the write lock (not yet inserted) — so two simultaneous check-ins get distinct, correct
    // positions instead of both reading the same pre-insert count and both being told "you're #N".
    int position = await dbContext.Entries.CountAsync(e => e.Status == QueueStatus.Waiting, ct);
    QueueSnapshot snapshot = await BuildSnapshotAsync(dbContext, ct);

    await transaction.CommitAsync(ct);

    QueueEntry contract = entity.ToContract();
    return new CheckInResult
    {
      Response = new CheckInResponse
      {
        EntryId = entity.Id,
        Position = position,
        SequenceNumber = changeEvent.SequenceNumber,
        Entry = contract
      },
      Update = new QueueUpdated
      {
        SequenceNumber = changeEvent.SequenceNumber,
        ChangedEntry = contract,
        Summary = snapshot
      }
    };
  }

  public async Task<QueueOperationResult> CallNextAsync(CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);

    QueueEntryEntity? entity = await dbContext.Entries
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

    return QueueOperationResult.Success(await RecordChangeAndBuildUpdateAsync(dbContext, entity, ct));
  }

  public async Task<QueueOperationResult> CompleteAsync(string entryId, CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);

    QueueEntryEntity? entity = await dbContext.Entries.FirstOrDefaultAsync(e => e.Id == entryId, ct);

    if (entity is null)
    {
      return QueueOperationResult.Failure(QueueOperationOutcome.EntryNotFound);
    }

    if (entity.Status != QueueStatus.Serving)
    {
      return QueueOperationResult.Failure(QueueOperationOutcome.InvalidState);
    }

    entity.Status = QueueStatus.Completed;

    return QueueOperationResult.Success(await RecordChangeAndBuildUpdateAsync(dbContext, entity, ct));
  }

  public async Task<long> GetLatestSequenceAsync(CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);
    return await GetLatestSequenceAsync(dbContext, ct);
  }

  private static async Task<long> GetLatestSequenceAsync(QueueDbContext dbContext, CancellationToken ct) =>
    await dbContext.ChangeEvents.MaxAsync(e => (long?)e.SequenceNumber, ct) ?? 0;

  public async Task<QueueStateResponse> GetStateAsync(CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);
    return new QueueStateResponse
    {
      SequenceNumber = await GetLatestSequenceAsync(dbContext, ct),
      Snapshot = await BuildSnapshotAsync(dbContext, ct)
    };
  }

  public async Task<QueueChangesSinceResponse> GetChangesSinceAsync(long sequenceNumber, CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);

    // One read transaction around all three queries so they see a single consistent SQLite snapshot (WAL gives
    // a read transaction a stable view for its whole duration). Without it, a concurrent commit between the
    // MaxAsync and the events query made the response's SequenceNumber lag the highest sequence number the
    // Changes list actually carried — an internally inconsistent payload. Read-only, so it never blocks writers.
    await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(ct);

    long latestSequenceNumber = await GetLatestSequenceAsync(dbContext, ct);

    // Sequence numbers only ever start at 1 and increase, so a negative or future value can't have come from
    // a client that legitimately talked to this server before — most likely the dev .db file was reset since
    // it last connected. Treat that exactly like "too far behind": a snapshot is always safe to trust, an
    // attempted diff against an unrecognized starting point is not.
    bool sequenceUnrecognized = sequenceNumber < 0 || sequenceNumber > latestSequenceNumber;

    // Count first (rather than fetching then measuring) so a far-behind client never materializes an unbounded
    // event list before the cutoff check below decides to send a snapshot instead.
    int missedCount = sequenceUnrecognized
      ? 0
      : await dbContext.ChangeEvents.CountAsync(e => e.SequenceNumber > sequenceNumber, ct);

    if (sequenceUnrecognized || missedCount > MaxCatchUpEvents)
    {
      return new QueueChangesSinceResponse
      {
        SequenceNumber = latestSequenceNumber,
        IsSnapshot = true,
        Snapshot = await BuildSnapshotAsync(dbContext, ct)
      };
    }

    List<QueueChangeEventEntity> events = await dbContext.ChangeEvents
      .Where(e => e.SequenceNumber > sequenceNumber)
      .OrderBy(e => e.SequenceNumber)
      .ToListAsync(ct);

    return new QueueChangesSinceResponse
    {
      SequenceNumber = latestSequenceNumber,
      IsSnapshot = false,
      Changes = events.Select(e => e.ToContract()).ToList()
    };
  }

  /// <summary>
  /// Shared tail for call-next/complete: appends the change event for the now-mutated <paramref name="entity"/>,
  /// saves both rows plus the snapshot read in one transaction (see the type remarks on why the transaction
  /// matters), and builds the <see cref="QueueUpdated"/> broadcast payload. Takes the caller's own
  /// <paramref name="dbContext"/> (rather than creating one itself) so the entity it was handed — already
  /// tracked and mutated by the caller — commits in the same context/transaction, not a second one.
  /// </summary>
  private static async Task<QueueUpdated> RecordChangeAndBuildUpdateAsync(
    QueueDbContext dbContext, QueueEntryEntity entity, CancellationToken ct)
  {
    await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(ct);

    QueueChangeEventEntity changeEvent = QueueChangeEventEntity.FromEntry(entity);
    dbContext.ChangeEvents.Add(changeEvent);

    await dbContext.SaveChangesAsync(ct);

    // Snapshot read while still holding the write lock, so it can't include a change committed after this one.
    QueueSnapshot snapshot = await BuildSnapshotAsync(dbContext, ct);

    await transaction.CommitAsync(ct);

    return new QueueUpdated
    {
      SequenceNumber = changeEvent.SequenceNumber,
      ChangedEntry = entity.ToContract(),
      Summary = snapshot
    };
  }

  public async Task<QueueUpdated?> RecordDocumentChangeAsync(string entryId, CancellationToken ct = default)
  {
    await using QueueDbContext dbContext = await this.dbContextFactory.CreateDbContextAsync(ct);

    QueueEntryEntity? entity = await dbContext.Entries.FirstOrDefaultAsync(e => e.Id == entryId, ct);
    if (entity is null)
    {
      return null;
    }

    // Same "append a change event + rebuild the snapshot in one transaction" tail as call-next/complete — the
    // entry's status is deliberately not touched here; the only thing that changed is its document set, which the
    // rebuilt snapshot's DocumentCount reflects.
    return await RecordChangeAndBuildUpdateAsync(dbContext, entity, ct);
  }

  private static async Task<QueueSnapshot> BuildSnapshotAsync(QueueDbContext dbContext, CancellationToken ct)
  {
    List<QueueEntryEntity> entities = await dbContext.Entries
      .OrderBy(e => e.CheckedInAt)
      .ToListAsync(ct);

    // One grouped count for the whole queue rather than a per-row subquery: cheaper, and it keeps the document
    // count off the entry row (no denormalized counter to keep in sync on every upload/delete). Entries with no
    // documents simply won't appear in the dictionary — GetValueOrDefault yields 0 for them.
    Dictionary<string, int> documentCounts = await dbContext.Documents
      .GroupBy(d => d.EntryId)
      .Select(g => new { EntryId = g.Key, Count = g.Count() })
      .ToDictionaryAsync(g => g.EntryId, g => g.Count, ct);

    return new QueueSnapshot
    {
      TotalWaiting = entities.Count(e => e.Status == QueueStatus.Waiting),
      TotalServing = entities.Count(e => e.Status == QueueStatus.Serving),
      TotalCompleted = entities.Count(e => e.Status == QueueStatus.Completed),
      Queue = entities.Select(e => e.ToContract(documentCounts.GetValueOrDefault(e.Id))).ToList()
    };
  }
}
