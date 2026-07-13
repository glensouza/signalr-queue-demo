using Azure;
using Azure.Data.Tables;

namespace SignalRQueueDemo.Shared.Persistence.TableStorage;

/// <summary>
/// A singleton counter row, one per <see cref="SequenceRowKey"/> / <see cref="WaitingCountRowKey"/>, both in the
/// same table. The two rows play deliberately different roles:
/// <list type="bullet">
/// <item><see cref="WaitingCountRowKey"/> is the <b>authoritative</b> running count of
/// <see cref="Contracts.QueueStatus.Waiting"/> entries, adjusted via optimistic concurrency in
/// <see cref="TableStorageQueueRepository.AdjustCounterAsync"/> — read the value and its <see cref="ETag"/>, then
/// <c>UpdateEntity</c> with an If-Match precondition; a racing writer fails the precondition (412) and retries
/// rather than silently overwriting. This is what gives concurrent check-ins distinct positions.</item>
/// <item><see cref="SequenceRowKey"/> is only a <b>start hint</b> for
/// <see cref="TableStorageQueueRepository.AppendChangeEventAsync"/>, which allocates sequence numbers by inserting
/// the change-event row itself (the row's existence IS the number). The authoritative sequence state is therefore
/// the change-event log, not this row — this row just saves a scan.</item>
/// </list>
/// </summary>
/// <remarks>
/// <see cref="WaitingCountRowKey"/> is collision-free between concurrent callers (the ETag precondition admits one
/// writer per read) but — unlike SQLite's single-transaction insert — not immune to drift if a caller commits the
/// counter update and then crashes before writing the row it was counting for (a single-`await`-wide window). That
/// drift, and any staleness of the <see cref="SequenceRowKey"/> hint, is healed on startup:
/// <see cref="TableStorageQueueSeedData"/> reconciles both rows to the real Waiting-count and the real max event
/// number before the app serves traffic. Because the sequence-number authority is the (gap-free, in-order)
/// change-event log rather than this hint, a lagging hint is self-correcting even at runtime and can never skip or
/// duplicate a number (see <see cref="TableStorageQueueRepository.AppendChangeEventAsync"/>).
/// </remarks>
public sealed class QueueCounterTableEntity : ITableEntity
{
  /// <summary>Constant partition for every counter row.</summary>
  public const string PartitionKeyValue = "Counter";

  /// <summary>Row key for the sequence-number start hint (authority is the change-event log — see type remarks).</summary>
  public const string SequenceRowKey = "Sequence";

  /// <summary>Row key for the running count of currently Waiting entries.</summary>
  public const string WaitingCountRowKey = "WaitingCount";

  /// <summary>The per-app partition — defaults to <see cref="PartitionKeyValue"/> but is overwritten with the configured store partition by the repository/seed (so each app has its own Sequence/WaitingCount counter rows).</summary>
  public string PartitionKey { get; set; } = PartitionKeyValue;

  /// <summary><see cref="SequenceRowKey"/> or <see cref="WaitingCountRowKey"/> — selects which counter this row is.</summary>
  public string RowKey { get; set; } = string.Empty;

  /// <summary>Set by the Table service on write; unused by application code.</summary>
  public DateTimeOffset? Timestamp { get; set; }

  /// <summary>The optimistic-concurrency token this counter's adjust loop round-trips on every update — see type remarks.</summary>
  public ETag ETag { get; set; }

  /// <summary>The counter's current value.</summary>
  public long Value { get; set; }
}
