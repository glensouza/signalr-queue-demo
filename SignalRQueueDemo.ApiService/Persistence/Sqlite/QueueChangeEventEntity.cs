using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Persistence.Sqlite;

/// <summary>
/// A denormalized point-in-time copy of a <see cref="QueueEntry"/>, written alongside every state change.
/// This is the event log the reconnect/catch-up protocol reads from via GET /queue/since/{seq}.
/// It's a full copy rather than a foreign key to <see cref="QueueEntryEntity"/> on purpose: the entry it
/// describes keeps changing (Waiting -> Serving -> Completed), but a historical event must keep showing the
/// entry exactly as it was at that sequence number, not whatever it has since become.
/// </summary>
public sealed class QueueChangeEventEntity
{
  /// <summary>
  /// The monotonic sequence number. Left for SQLite to assign as an autoincrement rowid rather than
  /// generating it in application code: SQLite serializes writers, so an identity column generated in the
  /// same <c>SaveChangesAsync</c> transaction as the entry mutation is guaranteed to be gap-free and
  /// collision-free without any extra locking on our part.
  /// </summary>
  public long SequenceNumber { get; set; }

  /// <summary>Id of the entry this event describes; a plain value copy, not a foreign key (see type remarks).</summary>
  public required string EntryId { get; set; }

  /// <summary>The entry's display name as it was at this change.</summary>
  public required string DisplayName { get; set; }

  /// <summary>The entry's ticket number as it was at this change.</summary>
  public required string TicketNumber { get; set; }

  /// <summary>The entry's check-in timestamp as it was at this change.</summary>
  public required DateTimeOffset CheckedInAt { get; set; }

  /// <summary>The entry's lifecycle state at this change — the whole point of the log row.</summary>
  public required QueueStatus Status { get; set; }

  /// <summary>Who had called the entry as of this change, or null if it was still Waiting.</summary>
  public string? ServedBy { get; set; }

  /// <summary>When the entry was called as of this change, or null if it was still Waiting.</summary>
  public DateTimeOffset? ServedAt { get; set; }

  /// <summary>Builds the event row snapshot from an entry's current state.</summary>
  public static QueueChangeEventEntity FromEntry(QueueEntryEntity entry) => new()
  {
    EntryId = entry.Id,
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
      Status = this.Status,
      ServedBy = this.ServedBy,
      ServedAt = this.ServedAt
    }
  };
}
