using Azure;
using Azure.Data.Tables;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.Shared.Persistence.TableStorage;

/// <summary>
/// Table Storage-mapped mirror of <see cref="QueueChangeEvent"/> — the Azure Table Storage counterpart of
/// <see cref="Sqlite.QueueChangeEventEntity"/>. A full point-in-time copy of the entry, not a foreign key to
/// <see cref="QueueEntryTableEntity"/>, for the same reason as the SQLite version: the entry keeps changing
/// (Waiting -> Serving -> Completed), but a historical event must keep showing what it looked like at that
/// sequence number.
/// </summary>
/// <remarks>
/// <see cref="RowKey"/> is the sequence number zero-padded to 19 digits (<c>long.MaxValue</c>'s digit count),
/// not the bare number — Table Storage compares RowKey lexicographically, and an unpadded numeric string sorts
/// "10" before "9". Zero-padding makes lexicographic order equal numeric order, which is what lets
/// <see cref="TableStorageQueueRepository.GetChangesSinceAsync"/> use a single <c>RowKey gt</c> range filter
/// (server-side, index-backed) instead of fetching every row and sorting in memory.
/// </remarks>
public sealed class QueueChangeEventTableEntity : ITableEntity
{
  /// <summary>Constant partition for every change-event row — see <see cref="QueueEntryTableEntity"/> remarks on partition scale.</summary>
  public const string PartitionKeyValue = "Event";

  /// <summary>Digit width for <see cref="RowKey"/>'s zero-padding — <c>long.MaxValue</c> is 19 digits.</summary>
  private const string RowKeyFormat = "D19";

  /// <summary>The per-app partition — defaults to <see cref="PartitionKeyValue"/> but is overwritten with the configured store partition by the repository/seed before every write (so each app's change-event log is independent, with its own sequence numbering).</summary>
  public string PartitionKey { get; set; } = PartitionKeyValue;

  /// <summary>Zero-padded <see cref="SequenceNumber"/> — see type remarks for why padding (not the bare number) is required.</summary>
  public string RowKey { get; set; } = string.Empty;

  /// <summary>Set by the Table service on write; unused by application code.</summary>
  public DateTimeOffset? Timestamp { get; set; }

  /// <summary>Change-event rows are never updated after being written, so this is unused beyond satisfying <see cref="ITableEntity"/>.</summary>
  public ETag ETag { get; set; }

  /// <summary>
  /// The monotonic sequence number, kept as its own typed column (not just parsed back out of <see cref="RowKey"/>)
  /// so callers never need to know the zero-padding format to read it.
  /// </summary>
  public required long SequenceNumber { get; set; }

  /// <summary>Id of the entry this event describes; a plain value copy, not a foreign key (see type remarks).</summary>
  public required string EntryId { get; set; }

  /// <summary>The entry's display name as it was at this change.</summary>
  public required string DisplayName { get; set; }

  /// <summary>The entry's ticket number as it was at this change.</summary>
  public required string TicketNumber { get; set; }

  /// <summary>The entry's check-in timestamp as it was at this change.</summary>
  public required DateTimeOffset CheckedInAt { get; set; }

  /// <summary>The entry's lifecycle state at this change, stored as its enum name (see <see cref="QueueEntryTableEntity.Status"/>).</summary>
  public required string Status { get; set; }

  /// <summary>Who had called the entry as of this change, or null if it was still Waiting.</summary>
  public string? ServedBy { get; set; }

  /// <summary>When the entry was called as of this change, or null if it was still Waiting.</summary>
  public DateTimeOffset? ServedAt { get; set; }

  /// <summary>Formats a sequence number into the zero-padded string used as a <see cref="RowKey"/> and in range filters.</summary>
  public static string FormatRowKey(long sequenceNumber) => sequenceNumber.ToString(RowKeyFormat);

  /// <summary>Builds the event row snapshot from an entry's current state and its newly assigned sequence number.</summary>
  public static QueueChangeEventTableEntity FromEntry(QueueEntryTableEntity entry, long sequenceNumber) => new()
  {
    RowKey = FormatRowKey(sequenceNumber),
    SequenceNumber = sequenceNumber,
    EntryId = entry.RowKey,
    DisplayName = entry.DisplayName,
    TicketNumber = entry.TicketNumber,
    CheckedInAt = entry.CheckedInAt,
    Status = entry.Status,
    ServedBy = entry.ServedBy,
    ServedAt = entry.ServedAt
  };

  /// <summary>Maps this row to the wire-facing <see cref="QueueChangeEvent"/> record.</summary>
  public QueueChangeEvent ToContract() => new()
  {
    SequenceNumber = this.SequenceNumber,
    Entry = new QueueEntry
    {
      Id = this.EntryId,
      DisplayName = this.DisplayName,
      TicketNumber = this.TicketNumber,
      CheckedInAt = this.CheckedInAt,
      Status = Enum.Parse<QueueStatus>(this.Status),
      ServedBy = this.ServedBy,
      ServedAt = this.ServedAt
    }
  };
}
