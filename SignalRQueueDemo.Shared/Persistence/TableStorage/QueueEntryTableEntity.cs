using Azure;
using Azure.Data.Tables;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.Shared.Persistence.TableStorage;

/// <summary>
/// Table Storage-mapped mirror of <see cref="QueueEntry"/> — the Azure Table Storage counterpart of
/// <see cref="Sqlite.QueueEntryEntity"/>. All of one app's rows live under a single partition — its per-app
/// PartitionKey (see <see cref="TableStorageQueueRepository"/>, which sets it from <c>Persistence:StorePartition</c>
/// so two hosts can share these tables yet keep separate stores). This table holds at most dozens of entries per
/// app for the life of the demo (per the court's expected walk-in volume), so a single partition per app never
/// approaches Table Storage's per-partition throughput limits, and keeping one app's entries in one partition is
/// what makes <see cref="TableStorageQueueRepository.BuildSnapshotAsync"/> a single partition-scoped query.
/// </summary>
public sealed class QueueEntryTableEntity : ITableEntity
{
  /// <summary>Default/fallback partition, used only when no <c>Persistence:StorePartition</c> is configured; the repository and seed set <see cref="PartitionKey"/> to the per-app value instead.</summary>
  public const string PartitionKeyValue = "Entry";

  /// <summary>The per-app partition — defaults to <see cref="PartitionKeyValue"/> but is overwritten with the configured store partition by the repository/seed before every write.</summary>
  public string PartitionKey { get; set; } = PartitionKeyValue;

  /// <summary>The entry's id (mirrors <see cref="QueueEntry.Id"/>) — RowKey doubles as the natural lookup key for GetEntityAsync.</summary>
  public string RowKey { get; set; } = string.Empty;

  /// <summary>Set by the Table service on write; unused by application code.</summary>
  public DateTimeOffset? Timestamp { get; set; }

  /// <summary>
  /// The optimistic-concurrency token Table Storage assigns on every write. <see cref="TableStorageQueueRepository"/>
  /// round-trips this on every <c>UpdateEntity</c> call (If-Match) instead of blind-overwriting, since Table
  /// Storage has no equivalent of SQLite's single-writer file lock to serialize concurrent call-next/complete
  /// requests for us — see the repository's remarks for the retry loop this enables.
  /// </summary>
  public ETag ETag { get; set; }

  /// <summary>Mirrors <see cref="QueueEntry.DisplayName"/> — synthetic test data only in this POC.</summary>
  public required string DisplayName { get; set; }

  /// <summary>Mirrors <see cref="QueueEntry.TicketNumber"/>.</summary>
  public required string TicketNumber { get; set; }

  /// <summary>Mirrors <see cref="QueueEntry.CheckedInAt"/>. Table Storage's native DateTimeOffset column needs no conversion.</summary>
  public required DateTimeOffset CheckedInAt { get; set; }

  /// <summary>
  /// Mirrors <see cref="QueueEntry.Status"/>, stored as its enum name (not the numeric value) so the row is
  /// readable in Azure Storage Explorer during development/demo — same rationale as <see cref="Sqlite.QueueDbContext"/>'s
  /// string conversion for the SQLite column. Table Storage has no native enum-conversion hook, so this is
  /// mapped by hand at the edges (<see cref="ToContract"/> / <see cref="FromEntry"/>) instead of a convention.
  /// </summary>
  public required string Status { get; set; }

  /// <summary>Mirrors <see cref="QueueEntry.ServedBy"/>; null until the entry is called.</summary>
  public string? ServedBy { get; set; }

  /// <summary>Mirrors <see cref="QueueEntry.ServedAt"/>; null until the entry is called.</summary>
  public DateTimeOffset? ServedAt { get; set; }

  /// <summary>Builds a new row from a check-in request, with a fresh id as its RowKey.</summary>
  public static QueueEntryTableEntity FromCheckIn(string id, CheckInRequest request, DateTimeOffset checkedInAt) =>
    new()
    {
      RowKey = id,
      DisplayName = request.DisplayName,
      TicketNumber = request.TicketNumber,
      CheckedInAt = checkedInAt,
      Status = nameof(QueueStatus.Waiting)
    };

  /// <summary>
  /// Maps this row to the wire-facing <see cref="QueueEntry"/> record. <paramref name="documentCount"/> is supplied
  /// by the snapshot builder (which counts the documents table once for the whole queue) and defaults to 0 for the
  /// change-event/replay call sites that neither have nor need it — see <see cref="QueueEntry.DocumentCount"/>.
  /// </summary>
  public QueueEntry ToContract(int documentCount = 0) => new()
  {
    Id = this.RowKey,
    DisplayName = this.DisplayName,
    TicketNumber = this.TicketNumber,
    CheckedInAt = this.CheckedInAt,
    Status = Enum.Parse<QueueStatus>(this.Status),
    ServedBy = this.ServedBy,
    ServedAt = this.ServedAt,
    DocumentCount = documentCount
  };
}
