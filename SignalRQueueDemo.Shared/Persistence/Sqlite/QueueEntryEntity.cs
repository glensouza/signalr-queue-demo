using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.Shared.Persistence.Sqlite;

/// <summary>
/// EF Core-mapped mirror of <see cref="QueueEntry"/>. Kept as a separate type rather than mapping
/// <see cref="QueueEntry"/> directly so <c>SignalRQueueDemo.Contracts</c> never needs an EF Core package
/// reference, and so the "current row" shape here can diverge from the "point-in-time snapshot" shape in
/// <see cref="QueueChangeEventEntity"/> without EF trying to treat one as a foreign key to the other.
/// </summary>
public sealed class QueueEntryEntity
{
  /// <summary>Primary key; mirrors <see cref="QueueEntry.Id"/>.</summary>
  public required string Id { get; set; }

  /// <summary>Mirrors <see cref="QueueEntry.DisplayName"/> — synthetic test data only in this POC.</summary>
  public required string DisplayName { get; set; }

  /// <summary>Mirrors <see cref="QueueEntry.TicketNumber"/>.</summary>
  public required string TicketNumber { get; set; }

  /// <summary>Mirrors <see cref="QueueEntry.CheckedInAt"/>; the column call-next and the snapshot order by.</summary>
  public required DateTimeOffset CheckedInAt { get; set; }

  /// <summary>Current lifecycle state; mirrors <see cref="QueueEntry.Status"/> and is mutated in place on each transition.</summary>
  public required QueueStatus Status { get; set; }

  /// <summary>Mirrors <see cref="QueueEntry.ServedBy"/>; null until the entry is called.</summary>
  public string? ServedBy { get; set; }

  /// <summary>Mirrors <see cref="QueueEntry.ServedAt"/>; null until the entry is called.</summary>
  public DateTimeOffset? ServedAt { get; set; }

  /// <summary>
  /// Maps this row to the wire-facing <see cref="QueueEntry"/> record. <paramref name="documentCount"/> comes
  /// from the caller (the snapshot builder counts documents once for the whole queue, rather than this row
  /// carrying a denormalized counter) and defaults to 0 for the change-event/replay call sites that don't have —
  /// and don't need — an accurate count. See <see cref="QueueEntry.DocumentCount"/>.
  /// </summary>
  public QueueEntry ToContract(int documentCount = 0) => new()
  {
    Id = this.Id,
    DisplayName = this.DisplayName,
    TicketNumber = this.TicketNumber,
    CheckedInAt = this.CheckedInAt,
    Status = this.Status,
    ServedBy = this.ServedBy,
    ServedAt = this.ServedAt,
    DocumentCount = documentCount
  };
}
