namespace SignalRQueueDemo.Contracts;

/// <summary>
/// Enumerates the lifecycle states of a queue entry. Kept simple to model the walk-in queue at its core:
/// people line up (Waiting), get served one at a time (Serving), and leave (Completed).
/// </summary>
public enum QueueStatus
{
  Waiting,
  Serving,
  Completed
}

/// <summary>
/// A single entry in the walk-in queue. Records the person's name and ticket number, when they checked in,
/// their current status, and (if served) who called them and when. All fields on this record persist to the
/// repository backend — both SQLite and Azure Table Storage implementations return the same shape, so
/// API clients never know which persistence layer is in use.
/// </summary>
public sealed record QueueEntry(
  string Id,
  string DisplayName,
  string TicketNumber,
  DateTimeOffset CheckedInAt,
  QueueStatus Status,
  string? ServedBy = null,
  DateTimeOffset? ServedAt = null
);

/// <summary>
/// The payload submitted when a visitor checks in at the kiosk. Minimal: name and ticket number only.
/// The server assigns the ID and position.
/// </summary>
public sealed record CheckInRequest(
  string DisplayName,
  string TicketNumber
);

/// <summary>
/// The response to a successful check-in. Tells the kiosk app "you are entry ID X, position Y in line,
/// and here's the current monotonic sequence number so you can catch up on future changes without relying
/// on push-only SignalR delivery." The entry object is redundant with position (you can compute it), but
/// included for convenience on mobile kiosks that may want to render the full entry data immediately.
/// </summary>
public sealed record CheckInResponse(
  string EntryId,
  int Position,
  long SequenceNumber,
  QueueEntry Entry
);

/// <summary>
/// Broadcast from the server every time the queue state changes. The sequence number is the backbone of
/// the reconnect/catch-up protocol: a client that disconnects stores this number locally, and on reconnect
/// calls GET /queue/since/{sequenceNumber} to replay all changes it missed. This is why the sequence must
/// be **monotonically increasing** — each publish strictly increments it, and no two state changes produce
/// the same sequence number. Relying on push-only SignalR delivery is never safe; a client that misses a
/// message between connection loss and reconnect must be able to catch up deterministically.
/// </summary>
public sealed record QueueUpdated(
  long SequenceNumber,
  QueueEntry ChangedEntry,
  QueueSnapshot Summary
);

/// <summary>
/// Summary snapshot of the queue included in QueueUpdated. Tells connected clients (kiosk display, staff
/// console, waiting-room board) the current state of all entries without forcing them to infer it from
/// the single changed entry. Includes a count of waiting and serving entries.
/// </summary>
public sealed record QueueSnapshot(
  int TotalWaiting,
  int TotalServing,
  int TotalCompleted,
  IReadOnlyList<QueueEntry> Queue
);

/// <summary>
/// A single row in the change-event log returned by GET /queue/since/{sequenceNumber}. The API returns
/// all QueueChangeEvent records with sequence numbers greater than the requested number, so a reconnecting
/// client can replay the full queue history since its last known state and catch up deterministically.
/// </summary>
public sealed record QueueChangeEvent(
  long SequenceNumber,
  QueueEntry Entry
);
