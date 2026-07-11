using Azure.Data.Tables;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Persistence.TableStorage;

/// <summary>
/// Startup-time table creation, seeding, and counter reconciliation for the <see cref="TableStorageQueueRepository"/>
/// backend — the Table Storage counterpart of <see cref="Sqlite.QueueSeedData"/> (and, unlike SQLite's single
/// <c>.db</c> file, also responsible for creating the three named tables themselves, since Azurite starts with none).
/// Same synthetic-data-only rule as the SQLite seed (CLAUDE.md: no real names, "Jane Test"/"Sam Sample" only), kept
/// as identical entries so the manual `.http` test script in README.md reads the same regardless of which provider
/// is configured.
/// </summary>
public static class TableStorageQueueSeedData
{
  /// <summary>
  /// Creates the three tables if they don't exist yet (fresh Azurite volume), seeds the same two fake entries as
  /// the SQLite backend when the entries table is empty, and then — <b>always</b>, seeded or not — reconciles the
  /// two counter rows to the real persisted state. Run once at startup before any request-scoped repository call,
  /// so it is single-threaded and race-free; that is exactly what makes the reconciliation a safe place to heal any
  /// drift a prior crash left (see remarks). Writes tables/entities directly rather than through
  /// <see cref="TableStorageQueueRepository"/>.
  /// </summary>
  /// <remarks>
  /// The reconciliation is the self-healing counterpart to the counters' accepted crash-window drift (see
  /// <see cref="QueueCounterTableEntity"/>): whatever state a prior run left — a <c>WaitingCount</c> that never got
  /// its matching decrement, a <c>Sequence</c> hint that lags the real max event, or counters that a mid-seed crash
  /// left missing entirely — this recomputes both from the actual entry/change-event rows and upserts them. On a
  /// normal restart it is a harmless idempotent recompute. The counts are a full partition scan, which is trivial at
  /// this POC's scale (dozens–hundreds of rows) and startup-only.
  /// </remarks>
  public static async Task EnsureTablesAndSeedAsync(TableServiceClient tableServiceClient, CancellationToken ct = default)
  {
    TableClient entriesTable = tableServiceClient.GetTableClient(TableStorageQueueRepository.EntriesTableName);
    TableClient changeEventsTable = tableServiceClient.GetTableClient(TableStorageQueueRepository.ChangeEventsTableName);
    TableClient countersTable = tableServiceClient.GetTableClient(TableStorageQueueRepository.CountersTableName);

    await entriesTable.CreateIfNotExistsAsync(ct);
    await changeEventsTable.CreateIfNotExistsAsync(ct);
    await countersTable.CreateIfNotExistsAsync(ct);

    if (!await AnyEntryExistsAsync(entriesTable, ct))
    {
      DateTimeOffset now = DateTimeOffset.UtcNow;
      (string DisplayName, string TicketNumber, DateTimeOffset CheckedInAt)[] seedRequests =
      [
        ("Jane Test", "A-042", now.AddMinutes(-10)),
        ("Sam Sample", "A-043", now.AddMinutes(-5))
      ];

      long sequenceNumber = 0;
      foreach ((string displayName, string ticketNumber, DateTimeOffset checkedInAt) in seedRequests)
      {
        QueueEntryTableEntity entity = QueueEntryTableEntity.FromCheckIn(
          Guid.NewGuid().ToString(),
          new CheckInRequest { DisplayName = displayName, TicketNumber = ticketNumber },
          checkedInAt);
        await entriesTable.AddEntityAsync(entity, ct);

        // Seed events are written with explicit contiguous numbers 1..N (safe: single-threaded, no concurrent
        // allocator to conflict with). The counters are set from actual state just below, not here.
        sequenceNumber++;
        await changeEventsTable.AddEntityAsync(QueueChangeEventTableEntity.FromEntry(entity, sequenceNumber), ct);
      }
    }

    // Always reconcile both counters to the real persisted state — the self-healing step (see remarks).
    long maxEventSequence = await MaxEventSequenceAsync(changeEventsTable, ct);
    int waitingCount = await CountWaitingAsync(entriesTable, ct);

    // Upsert (not Add): idempotent whether the row is fresh, stale, or drifted from a prior crash.
    await countersTable.UpsertEntityAsync(
      new QueueCounterTableEntity { RowKey = QueueCounterTableEntity.SequenceRowKey, Value = maxEventSequence },
      TableUpdateMode.Replace, ct);
    await countersTable.UpsertEntityAsync(
      new QueueCounterTableEntity { RowKey = QueueCounterTableEntity.WaitingCountRowKey, Value = waitingCount },
      TableUpdateMode.Replace, ct);
  }

  private static async Task<bool> AnyEntryExistsAsync(TableClient entriesTable, CancellationToken ct)
  {
    await foreach (QueueEntryTableEntity _ in entriesTable.QueryAsync<QueueEntryTableEntity>(maxPerPage: 1, cancellationToken: ct))
    {
      return true;
    }

    return false;
  }

  private static async Task<long> MaxEventSequenceAsync(TableClient changeEventsTable, CancellationToken ct)
  {
    long max = 0;
    await foreach (QueueChangeEventTableEntity changeEvent in changeEventsTable.QueryAsync<QueueChangeEventTableEntity>(cancellationToken: ct))
    {
      if (changeEvent.SequenceNumber > max)
      {
        max = changeEvent.SequenceNumber;
      }
    }

    return max;
  }

  private static async Task<int> CountWaitingAsync(TableClient entriesTable, CancellationToken ct)
  {
    string filter = $"PartitionKey eq '{QueueEntryTableEntity.PartitionKeyValue}' and Status eq '{nameof(QueueStatus.Waiting)}'";

    int count = 0;
    await foreach (QueueEntryTableEntity _ in entriesTable.QueryAsync<QueueEntryTableEntity>(filter, cancellationToken: ct))
    {
      count++;
    }

    return count;
  }
}
