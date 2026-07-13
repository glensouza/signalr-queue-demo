using System.Net;
using Azure;
using Azure.Data.Tables;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.Shared.Persistence.TableStorage;

/// <summary>
/// Azure Table Storage implementation of <see cref="IQueueRepository"/> (against the Azurite emulator locally,
/// per <c>Persistence:Provider = TableStorage</c> in Program.cs) — the cheaper-but-fewer-guarantees alternative
/// to <see cref="Sqlite.SqliteQueueRepository"/> that ADR-0001 flagged as worth demonstrating for future
/// low-complexity projects, even though this POC's SQL tables/APIs made it not worth adopting here.
///
/// <para>
/// <b>What you give up vs. SQL:</b> Table Storage has no multi-row transactions and no server-side autoincrement,
/// so this type can't reuse SQLite's "one write-locked transaction covers the mutation, its sequence number, and
/// the consistent read-back" trick. Instead:
/// </para>
/// <list type="bullet">
/// <item><b>Sequence numbers</b> are allocated by <see cref="AppendChangeEventAsync"/> — the change-event row's own
/// insertion IS the allocation (its zero-padded RowKey is the number), so a number can't exist until its row does.
/// That makes the change-event log inherently <b>gap-free and strictly in-order</b> (event N can't be written until
/// N-1 exists to 409-conflict against), which is what lets <see cref="GetChangesSinceAsync"/> return a contiguous
/// diff plus a SequenceNumber a reconnecting client can safely advance to. The
/// <see cref="QueueCounterTableEntity.SequenceRowKey"/> counter survives only as an O(1) start hint so allocation
/// needn't scan the log; it may lag the true max but never leads it.</item>
/// <item><b>The check-in position</b> is the running <see cref="QueueCounterTableEntity.WaitingCountRowKey"/> count,
/// adjusted through <see cref="AdjustCounterAsync"/> — an optimistic-concurrency (ETag If-Match) increment loop over
/// that singleton row — so two truly simultaneous check-ins are serialized into distinct positions instead of racing
/// to count the same rows. It's reconciled to the real Waiting-row count at startup (see
/// <see cref="TableStorageQueueSeedData"/>), which heals any crash-window drift.</item>
/// <item><b>Call-next/complete</b> use the same ETag If-Match pattern directly on the entry row, retrying when a
/// concurrent request wins the race for the same row — see <see cref="CallNextAsync"/>.</item>
/// </list>
/// <para>
/// <b>Why Table Storage is cheaper:</b> no server compute to provision or patch, storage billed per GB/transaction
/// instead of per-instance-hour, and it scales to per-partition throughput limits far past this POC's expected
/// volume (dozens–hundreds of entries/day) without any capacity planning.
/// </para>
/// </summary>
public sealed class TableStorageQueueRepository : IQueueRepository
{
  /// <summary>Table name for current-state entry rows — see <see cref="QueueEntryTableEntity"/>.</summary>
  public const string EntriesTableName = "QueueEntries";

  /// <summary>Table name for the append-only change-event log — see <see cref="QueueChangeEventTableEntity"/>.</summary>
  public const string ChangeEventsTableName = "QueueChangeEvents";

  /// <summary>Table name for the singleton counter rows — see <see cref="QueueCounterTableEntity"/>.</summary>
  public const string CountersTableName = "QueueCounters";

  /// <summary>See <see cref="Sqlite.SqliteQueueRepository.MockStaffIdentity"/> — same placeholder, same reason.</summary>
  private const string MockStaffIdentity = "front-desk-mock";

  /// <summary>Same cutoff and same reasoning as <see cref="Sqlite.SqliteQueueRepository.MaxCatchUpEvents"/>.</summary>
  private const int MaxCatchUpEvents = 200;

  /// <summary>
  /// Bound on optimistic-concurrency retries (counter adjustment, event append, call-next, complete) before giving
  /// up and throwing. The binding case is the single <see cref="QueueCounterTableEntity.WaitingCountRowKey"/> row:
  /// N truly-simultaneous check-ins all contend on it, and the unluckiest needs up to N ETag retries (each round
  /// exactly one writer wins), so this bound is the largest instantaneous check-in burst the position counter
  /// tolerates. 32 comfortably covers this POC's handful of kiosks/staff with headroom; a real failure here means
  /// contention far past the court scenario. Retries also back off (see <see cref="BackoffAsync"/>) so the
  /// contenders de-synchronize instead of thundering-herd re-reading in lockstep.
  /// </summary>
  private const int MaxOptimisticConcurrencyAttempts = 32;

  private readonly TableClient entriesTable;
  private readonly TableClient changeEventsTable;
  private readonly TableClient countersTable;
  private readonly TableClient documentsTable;

  public TableStorageQueueRepository(TableServiceClient tableServiceClient)
  {
    this.entriesTable = tableServiceClient.GetTableClient(EntriesTableName);
    this.changeEventsTable = tableServiceClient.GetTableClient(ChangeEventsTableName);
    this.countersTable = tableServiceClient.GetTableClient(CountersTableName);
    // Read-only handle to the document-metadata table (owned by TableStorageDocumentRepository) so the snapshot
    // can report each entry's document count without a cross-repository dependency — the two share the same
    // physical table, addressed here by that repository's public table-name constant, not by taking a reference
    // to the repository itself.
    this.documentsTable = tableServiceClient.GetTableClient(TableStorageDocumentRepository.DocumentsTableName);
  }

  public async Task<CheckInResult> CheckInAsync(CheckInRequest request, CancellationToken ct = default)
  {
    QueueEntryTableEntity entity = QueueEntryTableEntity.FromCheckIn(Guid.NewGuid().ToString(), request, DateTimeOffset.UtcNow);
    await this.entriesTable.AddEntityAsync(entity, ct);

    // Position comes from the same ETag-optimistic-concurrency counter as the sequence number, not a query-time
    // COUNT — two truly simultaneous check-ins are serialized against each other by the counter's If-Match
    // precondition (one's UpdateEntity wins, the other retries and adjusts the now-newer value), so they always
    // come out as distinct, correctly-ordered positions instead of racing to count the same set of rows.
    long position = await this.AdjustCounterAsync(QueueCounterTableEntity.WaitingCountRowKey, delta: 1, ct);
    QueueChangeEventTableEntity changeEvent = await this.AppendChangeEventAsync(entity, ct);
    long sequenceNumber = changeEvent.SequenceNumber;

    QueueSnapshot snapshot = await this.BuildSnapshotAsync(ct);

    QueueEntry contract = entity.ToContract();
    return new CheckInResult
    {
      Response = new CheckInResponse
      {
        EntryId = entity.RowKey,
        Position = (int)position,
        SequenceNumber = sequenceNumber,
        Entry = contract
      },
      Update = new QueueUpdated
      {
        SequenceNumber = sequenceNumber,
        ChangedEntry = contract,
        Summary = snapshot
      }
    };
  }

  public async Task<QueueOperationResult> CallNextAsync(CancellationToken ct = default)
  {
    for (int attempt = 0; attempt < MaxOptimisticConcurrencyAttempts; attempt++)
    {
      QueueEntryTableEntity? entity = await this.FindOldestWaitingAsync(ct);
      if (entity is null)
      {
        return QueueOperationResult.Failure(QueueOperationOutcome.NoWaitingEntries);
      }

      entity.Status = nameof(QueueStatus.Serving);
      entity.ServedBy = MockStaffIdentity;
      entity.ServedAt = DateTimeOffset.UtcNow;

      try
      {
        await this.entriesTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
      }
      catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.PreconditionFailed)
      {
        // Another call-next won the race for this exact row between our read and our write. Retrying re-runs
        // FindOldestWaitingAsync, which now sees this row as no longer Waiting and picks the next-oldest one —
        // this is the optimistic-concurrency stand-in for SQLite's serializing writer lock (see type remarks).
        continue;
      }

      // This entry just left Waiting, so the running WaitingCount counter (see CheckInAsync) needs the matching
      // decrement — exactly once per successful transition, not once per retry attempt above.
      await this.AdjustCounterAsync(QueueCounterTableEntity.WaitingCountRowKey, delta: -1, ct);

      return QueueOperationResult.Success(await this.RecordChangeAndBuildUpdateAsync(entity, ct));
    }

    throw new InvalidOperationException(
      $"Could not call the next queue entry after {MaxOptimisticConcurrencyAttempts} optimistic-concurrency attempts.");
  }

  public async Task<QueueOperationResult> CompleteAsync(string entryId, CancellationToken ct = default)
  {
    for (int attempt = 0; attempt < MaxOptimisticConcurrencyAttempts; attempt++)
    {
      QueueEntryTableEntity? entity;
      try
      {
        Response<QueueEntryTableEntity> response = await this.entriesTable.GetEntityAsync<QueueEntryTableEntity>(
          QueueEntryTableEntity.PartitionKeyValue, entryId, cancellationToken: ct);
        entity = response.Value;
      }
      catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
      {
        return QueueOperationResult.Failure(QueueOperationOutcome.EntryNotFound);
      }

      if (entity.Status != nameof(QueueStatus.Serving))
      {
        return QueueOperationResult.Failure(QueueOperationOutcome.InvalidState);
      }

      entity.Status = nameof(QueueStatus.Completed);

      try
      {
        await this.entriesTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
      }
      catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.PreconditionFailed)
      {
        // A concurrent complete (or, impossibly here, a call-next) changed this row since our read. Retrying
        // re-fetches it fresh, so a concurrent completer racing us to the same entry correctly yields
        // InvalidState on the loser's retry instead of a stale double-complete.
        continue;
      }

      // Serving -> Completed never touches the Waiting count, unlike CallNextAsync's Waiting -> Serving.
      return QueueOperationResult.Success(await this.RecordChangeAndBuildUpdateAsync(entity, ct));
    }

    throw new InvalidOperationException(
      $"Could not complete queue entry '{entryId}' after {MaxOptimisticConcurrencyAttempts} optimistic-concurrency attempts.");
  }

  public async Task<long> GetLatestSequenceAsync(CancellationToken ct = default) =>
    await this.ReadCounterAsync(QueueCounterTableEntity.SequenceRowKey, ct);

  public async Task<QueueStateResponse> GetStateAsync(CancellationToken ct = default) =>
    new()
    {
      SequenceNumber = await this.GetLatestSequenceAsync(ct),
      Snapshot = await this.BuildSnapshotAsync(ct)
    };

  public async Task<QueueUpdated?> RecordDocumentChangeAsync(string entryId, CancellationToken ct = default)
  {
    QueueEntryTableEntity entity;
    try
    {
      Response<QueueEntryTableEntity> response = await this.entriesTable.GetEntityAsync<QueueEntryTableEntity>(
        QueueEntryTableEntity.PartitionKeyValue, entryId, cancellationToken: ct);
      entity = response.Value;
    }
    catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
    {
      return null;
    }

    // The entry row itself isn't modified (its document set lives in another table); this only appends a change
    // event so the rebuilt snapshot's DocumentCount reaches connected clients through the normal broadcast path.
    return await this.RecordChangeAndBuildUpdateAsync(entity, ct);
  }

  public async Task<QueueChangesSinceResponse> GetChangesSinceAsync(long sequenceNumber, CancellationToken ct = default)
  {
    // The start-hint counter, used ONLY to gate the "unrecognized sequence number" fallback below. The baseline
    // handed back on the diff path is derived from the events actually returned, never from this hint (see the
    // final return) — the hint can momentarily lag the true max, and reporting it could hand back a number ahead
    // of the returned changes, letting a reconnecting client advance past and permanently miss an event.
    long latestSequenceNumber = await this.GetLatestSequenceAsync(ct);

    // Same "can't have come from a server this one recognizes" reasoning as SqliteQueueRepository — see its
    // GetChangesSinceAsync remarks. Table Storage adds no new failure mode here beyond what SQLite already has.
    // (If the hint is momentarily behind the true max a valid client can be misjudged "future" and get a snapshot
    // instead of a diff — always a safe answer, just occasionally a heavier one, and only in that sub-millisecond
    // window.)
    bool sequenceUnrecognized = sequenceNumber < 0 || sequenceNumber > latestSequenceNumber;
    if (sequenceUnrecognized)
    {
      return new QueueChangesSinceResponse
      {
        SequenceNumber = latestSequenceNumber,
        IsSnapshot = true,
        Snapshot = await this.BuildSnapshotAsync(ct)
      };
    }

    // Table Storage has no server-side COUNT, so unlike SQLite's separate "count, then decide, then fetch"
    // steps, the cutoff is enforced by breaking out of enumeration itself: a far-behind client never causes
    // more than MaxCatchUpEvents + 1 rows to be materialized, regardless of how many actually matched.
    string filter =
      $"PartitionKey eq '{QueueChangeEventTableEntity.PartitionKeyValue}' and RowKey gt '{QueueChangeEventTableEntity.FormatRowKey(sequenceNumber)}'";

    List<QueueChangeEventTableEntity> events = [];
    await foreach (QueueChangeEventTableEntity changeEvent in this.changeEventsTable.QueryAsync<QueueChangeEventTableEntity>(filter, cancellationToken: ct))
    {
      events.Add(changeEvent);
      if (events.Count > MaxCatchUpEvents)
      {
        return new QueueChangesSinceResponse
        {
          SequenceNumber = latestSequenceNumber,
          IsSnapshot = true,
          Snapshot = await this.BuildSnapshotAsync(ct)
        };
      }
    }

    return new QueueChangesSinceResponse
    {
      // Baseline = the last event actually returned, NOT the hint counter. The log is gap-free and in-order (see
      // AppendChangeEventAsync), so the last row is the true max and every number up to it is included in Changes;
      // a client that advances to it has therefore received everything ≤ it. (Empty Changes means the caller was
      // already current, so its own sequence number stands.) This is the guard against handing back a baseline
      // ahead of the diff.
      SequenceNumber = events.Count > 0 ? events[^1].SequenceNumber : sequenceNumber,
      IsSnapshot = false,
      // Table Storage always orders query results by PartitionKey then RowKey ascending, and RowKey's zero-
      // padding (QueueChangeEventTableEntity remarks) makes that lexicographic order equal ascending sequence
      // order — no client-side sort needed, unlike BuildSnapshotAsync's CheckedInAt ordering below.
      Changes = events.Select(e => e.ToContract()).ToList()
    };
  }

  /// <summary>Reads a <see cref="QueueCounterTableEntity"/> row's current value without adjusting it (0 if the row doesn't exist yet).</summary>
  private async Task<long> ReadCounterAsync(string rowKey, CancellationToken ct)
  {
    try
    {
      Response<QueueCounterTableEntity> response = await this.countersTable.GetEntityAsync<QueueCounterTableEntity>(
        QueueCounterTableEntity.PartitionKeyValue, rowKey, cancellationToken: ct);
      return response.Value.Value;
    }
    catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
    {
      return 0;
    }
  }

  /// <summary>
  /// Adjusts the running Waiting-entry count (<see cref="QueueCounterTableEntity.WaitingCountRowKey"/>) by
  /// <paramref name="delta"/> — <c>+1</c> on check-in, <c>-1</c> on call-next — via optimistic concurrency, and
  /// returns its new value (which check-in reports as the visitor's position). See
  /// <see cref="QueueCounterTableEntity"/> remarks for why this is race-free between concurrent callers but not
  /// immune to drift if a caller crashes between this call committing and the row it was counting for being
  /// written; that drift is healed by the startup reconciliation in <see cref="TableStorageQueueSeedData"/>.
  /// </summary>
  private async Task<long> AdjustCounterAsync(string rowKey, long delta, CancellationToken ct)
  {
    for (int attempt = 0; attempt < MaxOptimisticConcurrencyAttempts; attempt++)
    {
      Response<QueueCounterTableEntity>? existing;
      try
      {
        existing = await this.countersTable.GetEntityAsync<QueueCounterTableEntity>(
          QueueCounterTableEntity.PartitionKeyValue, rowKey, cancellationToken: ct);
      }
      catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
      {
        // First-ever adjustment to this counter: AddEntityAsync is itself the concurrency guard here. If two
        // callers race to create the row, only one Add succeeds (409 Conflict); the loser falls through to
        // the retry below, which now finds the row the winner created and takes the read-then-update path.
        try
        {
          await this.countersTable.AddEntityAsync(new QueueCounterTableEntity { RowKey = rowKey, Value = delta }, ct);
          return delta;
        }
        catch (RequestFailedException addEx) when (addEx.Status == (int)HttpStatusCode.Conflict)
        {
          await BackoffAsync(attempt, ct);
          continue;
        }
      }

      QueueCounterTableEntity counter = existing.Value;
      long next = counter.Value + delta;
      counter.Value = next;

      try
      {
        await this.countersTable.UpdateEntityAsync(counter, counter.ETag, TableUpdateMode.Replace, ct);
        return next;
      }
      catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.PreconditionFailed)
      {
        // Another writer adjusted the counter between our read and our update; retry against the new value.
        await BackoffAsync(attempt, ct);
      }
    }

    throw new InvalidOperationException(
      $"Could not adjust counter '{rowKey}' by {delta} after {MaxOptimisticConcurrencyAttempts} optimistic-concurrency attempts.");
  }

  /// <summary>
  /// Jittered backoff between optimistic-concurrency retries so N contenders on the same row de-synchronize instead
  /// of re-reading and re-colliding in lockstep (a thundering herd that would waste the retry budget). Bounded and
  /// short — this is contention smoothing at the POC's handful-of-writers scale, not a general backpressure system.
  /// The randomized component matters more than the exact durations, so a plain <see cref="Random.Shared"/> is fine.
  /// </summary>
  private static Task BackoffAsync(int attempt, CancellationToken ct)
  {
    int milliseconds = Math.Min(2 + attempt, 15) + Random.Shared.Next(0, 8);
    return Task.Delay(milliseconds, ct);
  }

  /// <summary>Appends the change event for the now-mutated <paramref name="entity"/> and builds the broadcast payload — the Table Storage counterpart of <see cref="Sqlite.SqliteQueueRepository.RecordChangeAndBuildUpdateAsync"/>.</summary>
  private async Task<QueueUpdated> RecordChangeAndBuildUpdateAsync(QueueEntryTableEntity entity, CancellationToken ct)
  {
    QueueChangeEventTableEntity changeEvent = await this.AppendChangeEventAsync(entity, ct);

    QueueSnapshot snapshot = await this.BuildSnapshotAsync(ct);

    return new QueueUpdated
    {
      SequenceNumber = changeEvent.SequenceNumber,
      ChangedEntry = entity.ToContract(),
      Summary = snapshot
    };
  }

  /// <summary>
  /// Appends a change-event row for <paramref name="entity"/> and returns it, allocating the row's sequence number
  /// by the row's own insertion rather than a separate counter: take the next unused number, try to Add the row,
  /// and on a 409 Conflict (a concurrent writer already claimed that number) advance and retry. Because a number
  /// only ever exists once its row does, the change-event log is inherently gap-free and strictly in-order — event
  /// N cannot be written until N-1 exists to conflict against — which is exactly what lets
  /// <see cref="GetChangesSinceAsync"/> hand a reconnecting client a contiguous diff and a SequenceNumber it can
  /// safely advance to. The <see cref="QueueCounterTableEntity.SequenceRowKey"/> counter is only an O(1) start hint
  /// (so allocation needn't scan the log each time); it may briefly lag the true max but never leads it, so a stale
  /// hint costs at most a couple of extra Conflict retries, never a wrong or skipped number.
  /// </summary>
  private async Task<QueueChangeEventTableEntity> AppendChangeEventAsync(QueueEntryTableEntity entity, CancellationToken ct)
  {
    long next = await this.ReadCounterAsync(QueueCounterTableEntity.SequenceRowKey, ct) + 1;

    for (int attempt = 0; attempt < MaxOptimisticConcurrencyAttempts; attempt++)
    {
      QueueChangeEventTableEntity changeEvent = QueueChangeEventTableEntity.FromEntry(entity, next);
      try
      {
        await this.changeEventsTable.AddEntityAsync(changeEvent, ct);
      }
      catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Conflict)
      {
        // A concurrent writer already claimed this number. Advance past it (guaranteeing forward progress and a
        // gap-free log); if the winner has since bumped the hint, jump straight to it so a burst of N simultaneous
        // writers converges in a couple of retries instead of N.
        await BackoffAsync(attempt, ct);
        next = Math.Max(next + 1, await this.ReadCounterAsync(QueueCounterTableEntity.SequenceRowKey, ct) + 1);
        continue;
      }

      // Best-effort: raise the start hint so the next allocation doesn't re-walk the numbers we just consumed. The
      // event rows are the source of truth, so a lost update here is harmless — the hint self-corrects on next use.
      await this.TryAdvanceSequenceHintAsync(next, ct);
      return changeEvent;
    }

    throw new InvalidOperationException(
      $"Could not append a change event after {MaxOptimisticConcurrencyAttempts} attempts.");
  }

  /// <summary>
  /// Best-effort raise of the <see cref="QueueCounterTableEntity.SequenceRowKey"/> start hint to at least
  /// <paramref name="value"/>, swallowing the optimistic-concurrency conflicts that just mean another writer already
  /// advanced it. The hint only ever needs to be "roughly the max" (see <see cref="AppendChangeEventAsync"/>), so
  /// none of these races is worth retrying.
  /// </summary>
  private async Task TryAdvanceSequenceHintAsync(long value, CancellationToken ct)
  {
    try
    {
      Response<QueueCounterTableEntity> existing = await this.countersTable.GetEntityAsync<QueueCounterTableEntity>(
        QueueCounterTableEntity.PartitionKeyValue, QueueCounterTableEntity.SequenceRowKey, cancellationToken: ct);
      if (existing.Value.Value >= value)
      {
        return;
      }

      QueueCounterTableEntity counter = existing.Value;
      counter.Value = value;
      await this.countersTable.UpdateEntityAsync(counter, counter.ETag, TableUpdateMode.Replace, ct);
    }
    catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
    {
      try
      {
        await this.countersTable.AddEntityAsync(
          new QueueCounterTableEntity { RowKey = QueueCounterTableEntity.SequenceRowKey, Value = value }, ct);
      }
      catch (RequestFailedException addEx) when (addEx.Status == (int)HttpStatusCode.Conflict)
      {
        // Another writer created the hint row first; theirs is at least as new. Fine.
      }
    }
    catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.PreconditionFailed)
    {
      // Another writer advanced the hint between our read and our write; theirs is at least as new. Fine.
    }
  }

  /// <summary>Scans the Waiting rows client-side for the oldest one — Table Storage has no server-side ORDER BY (same constraint noted on <see cref="Sqlite.QueueDbContext"/>'s DateTimeOffset conversion, but here it applies to every read, not just this one).</summary>
  private async Task<QueueEntryTableEntity?> FindOldestWaitingAsync(CancellationToken ct)
  {
    string filter = $"PartitionKey eq '{QueueEntryTableEntity.PartitionKeyValue}' and Status eq '{nameof(QueueStatus.Waiting)}'";

    QueueEntryTableEntity? oldest = null;
    await foreach (QueueEntryTableEntity entity in this.entriesTable.QueryAsync<QueueEntryTableEntity>(filter, cancellationToken: ct))
    {
      if (oldest is null || entity.CheckedInAt < oldest.CheckedInAt)
      {
        oldest = entity;
      }
    }

    return oldest;
  }

  private async Task<QueueSnapshot> BuildSnapshotAsync(CancellationToken ct)
  {
    string filter = $"PartitionKey eq '{QueueEntryTableEntity.PartitionKeyValue}'";

    List<QueueEntryTableEntity> entities = [];
    await foreach (QueueEntryTableEntity entity in this.entriesTable.QueryAsync<QueueEntryTableEntity>(filter, cancellationToken: ct))
    {
      entities.Add(entity);
    }

    List<QueueEntryTableEntity> ordered = entities.OrderBy(e => e.CheckedInAt).ToList();
    Dictionary<string, int> documentCounts = await this.CountDocumentsByEntryAsync(
      ordered.Select(e => e.RowKey).ToList(), ct);

    return new QueueSnapshot
    {
      TotalWaiting = ordered.Count(e => e.Status == nameof(QueueStatus.Waiting)),
      TotalServing = ordered.Count(e => e.Status == nameof(QueueStatus.Serving)),
      TotalCompleted = ordered.Count(e => e.Status == nameof(QueueStatus.Completed)),
      Queue = ordered.Select(e => e.ToContract(documentCounts.GetValueOrDefault(e.RowKey))).ToList()
    };
  }

  /// <summary>
  /// Counts document-metadata rows per entry, scoped to <paramref name="entryIds"/> (the entries in the snapshot
  /// being built) rather than the whole <c>QueueDocuments</c> table. <see cref="DocumentTableEntity"/>'s
  /// PartitionKey is the owning entry's id, so each entry's documents live in their own partition — this issues one
  /// partition-scoped query per entry (in parallel) instead of a single scan of every document row Table Storage
  /// has ever stored.
  ///
  /// <para>
  /// This replaced an earlier version that queried the whole table and tallied by PartitionKey client-side. That
  /// approach's cost was <c>O(all documents ever stored)</c>, on <em>every</em> snapshot build (every mutation,
  /// every GET /queue, every polling tick per client) — unbounded relative to the queue size it was decorating,
  /// and made worse by the fact that document deletion on complete (<c>QueueEndpoints.HandleCompleteAsync</c>) is
  /// best-effort: a failed cleanup left orphaned rows that were rescanned forever after. Scoping to the current
  /// snapshot's entry ids bounds the cost to <c>O(entries currently in the queue)</c> instead — this POC's expected
  /// scale (dozens, not thousands) — trading a full scan for N small round-trips, which is the right trade at that
  /// volume. It does NOT reclaim orphaned rows (still-dead storage after a failed delete), it just stops paying to
  /// scan them.
  /// </para>
  /// </summary>
  private async Task<Dictionary<string, int>> CountDocumentsByEntryAsync(IReadOnlyCollection<string> entryIds, CancellationToken ct)
  {
    if (entryIds.Count == 0)
    {
      return [];
    }

    async Task<(string EntryId, int Count)> CountForEntryAsync(string entryId)
    {
      int count = 0;
      await foreach (DocumentTableEntity _ in this.documentsTable.QueryAsync<DocumentTableEntity>(
        filter: $"PartitionKey eq '{entryId}'", select: ["PartitionKey"], cancellationToken: ct))
      {
        count++;
      }

      return (entryId, count);
    }

    (string EntryId, int Count)[] results = await Task.WhenAll(entryIds.Select(CountForEntryAsync));

    Dictionary<string, int> counts = [];
    foreach ((string entryId, int count) in results)
    {
      if (count > 0)
      {
        counts[entryId] = count;
      }
    }

    return counts;
  }
}
