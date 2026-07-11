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
/// <remarks>
/// Members are <c>required</c> init-only rather than positional so the compiler forces every caller to name
/// the non-nullable fields (per CLAUDE.md) — a partial <c>new QueueEntry { }</c> won't compile, and
/// System.Text.Json rejects a payload missing a required field on deserialization.
/// </remarks>
public sealed record QueueEntry
{
  /// <summary>Server-assigned unique identifier for the entry (the client never supplies it).</summary>
  public required string Id { get; init; }

  /// <summary>The visitor's display name — synthetic test data only in this POC (e.g. "Jane Test").</summary>
  public required string DisplayName { get; init; }

  /// <summary>The paper/kiosk ticket number the visitor holds (e.g. "A-042").</summary>
  public required string TicketNumber { get; init; }

  /// <summary>When the visitor checked in. DateTimeOffset (not DateTime) so the instant is unambiguous across time zones.</summary>
  public required DateTimeOffset CheckedInAt { get; init; }

  /// <summary>Current lifecycle state; drives which frontend surfaces the entry appears on.</summary>
  public required QueueStatus Status { get; init; }

  /// <summary>Which staff member called this entry, or null while still Waiting. Nullable because it only exists once served.</summary>
  public string? ServedBy { get; init; }

  /// <summary>When the entry moved to Serving, or null while still Waiting.</summary>
  public DateTimeOffset? ServedAt { get; init; }
}

/// <summary>
/// The response to GET /checkin/token: a short-lived, server-signed token a kiosk client must echo back on the
/// X-CheckIn-Token header of its next POST /checkin (and POST /checkin/{id}/documents). See
/// SignalRQueueDemo.ApiService.Endpoints.CheckInTokenService for what this token does and doesn't protect
/// against, and why it exists instead of ASP.NET Core's cookie-based antiforgery system.
/// </summary>
public sealed record CheckInTokenResponse
{
  /// <summary>The opaque token value — treat it as an opaque string, not something to parse client-side.</summary>
  public required string Token { get; init; }
}

/// <summary>
/// The payload submitted when a visitor checks in at the kiosk. Minimal: name and ticket number only.
/// The server assigns the ID and position.
/// </summary>
public sealed record CheckInRequest
{
  /// <summary>The visitor's display name — synthetic test data only in this POC.</summary>
  public required string DisplayName { get; init; }

  /// <summary>The ticket number the visitor holds.</summary>
  public required string TicketNumber { get; init; }
}

/// <summary>
/// The response to a successful check-in. Tells the kiosk app "you are entry ID X, position Y in line,
/// and here's the current monotonic sequence number so you can catch up on future changes without relying
/// on push-only SignalR delivery." The entry object is redundant with position (you can compute it), but
/// included for convenience on mobile kiosks that may want to render the full entry data immediately.
/// </summary>
public sealed record CheckInResponse
{
  /// <summary>The server-assigned id of the newly created entry, so the kiosk can track its own row.</summary>
  public required string EntryId { get; init; }

  /// <summary>1-based position in line at the moment of check-in ("you're #N").</summary>
  public required int Position { get; init; }

  /// <summary>The current monotonic sequence number, stored client-side to drive reconnect catch-up.</summary>
  public required long SequenceNumber { get; init; }

  /// <summary>The full created entry, so the kiosk can render immediately without a follow-up GET.</summary>
  public required QueueEntry Entry { get; init; }
}

/// <summary>
/// Broadcast from the server every time the queue state changes. The sequence number is the backbone of
/// the reconnect/catch-up protocol: a client that disconnects stores this number locally, and on reconnect
/// calls GET /queue/since/{sequenceNumber} to replay all changes it missed. This is why the sequence must
/// be **monotonically increasing** — each publish strictly increments it, and no two state changes produce
/// the same sequence number. Relying on push-only SignalR delivery is never safe; a client that misses a
/// message between connection loss and reconnect must be able to catch up deterministically.
/// </summary>
public sealed record QueueUpdated
{
  /// <summary>The monotonic sequence number for this change — strictly greater than the previous broadcast.</summary>
  public required long SequenceNumber { get; init; }

  /// <summary>The single entry whose state changed, so clients can animate/highlight just that row.</summary>
  public required QueueEntry ChangedEntry { get; init; }

  /// <summary>Full queue snapshot alongside the delta, so clients never have to infer total state from one change.</summary>
  public required QueueSnapshot Summary { get; init; }
}

/// <summary>
/// Summary snapshot of the queue included in QueueUpdated. Tells connected clients (kiosk display, staff
/// console, waiting-room board) the current state of all entries without forcing them to infer it from
/// the single changed entry. Includes a count of waiting and serving entries.
/// </summary>
/// <remarks>
/// Equality is overridden to compare <see cref="Queue"/> element-by-element. The compiler-generated record
/// equality would use reference equality on the <see cref="IReadOnlyList{T}"/>, so two snapshots with
/// identical contents would compare unequal — breaking any "did the state actually change?" check.
/// </remarks>
public sealed record QueueSnapshot
{
  /// <summary>Count of entries currently Waiting — cheap for boards to render without filtering the list.</summary>
  public required int TotalWaiting { get; init; }

  /// <summary>Count of entries currently Serving.</summary>
  public required int TotalServing { get; init; }

  /// <summary>Count of entries Completed (typically hidden by displays, kept for staff totals).</summary>
  public required int TotalCompleted { get; init; }

  /// <summary>The full ordered list of entries in the snapshot.</summary>
  public required IReadOnlyList<QueueEntry> Queue { get; init; }

  /// <summary>Structural equality: two snapshots are equal when their counts and every queue element match in order.</summary>
  public bool Equals(QueueSnapshot? other) =>
    other is not null
    && this.TotalWaiting == other.TotalWaiting
    && this.TotalServing == other.TotalServing
    && this.TotalCompleted == other.TotalCompleted
    && this.Queue.SequenceEqual(other.Queue);

  /// <summary>Hash code consistent with <see cref="Equals(QueueSnapshot?)"/>, folding in each queue element.</summary>
  public override int GetHashCode()
  {
    HashCode hash = new();
    hash.Add(this.TotalWaiting);
    hash.Add(this.TotalServing);
    hash.Add(this.TotalCompleted);
    foreach (QueueEntry entry in this.Queue)
    {
      hash.Add(entry);
    }

    return hash.ToHashCode();
  }
}

/// <summary>
/// The response to GET /queue: the full current snapshot plus the latest sequence number, so a client
/// loading for the first time (not reconnecting) can start tracking sequence numbers from a known point
/// without a separate round-trip to discover it.
/// </summary>
public sealed record QueueStateResponse
{
  /// <summary>The most recent sequence number of any change applied so far (0 if the queue has never changed).</summary>
  public required long SequenceNumber { get; init; }

  /// <summary>The current snapshot of all queue entries and their status counts.</summary>
  public required QueueSnapshot Snapshot { get; init; }
}

/// <summary>
/// A single row in the change-event log returned by GET /queue/since/{sequenceNumber}. The API returns
/// all QueueChangeEvent records with sequence numbers greater than the requested number, so a reconnecting
/// client can replay the full queue history since its last known state and catch up deterministically.
/// </summary>
public sealed record QueueChangeEvent
{
  /// <summary>The monotonic sequence number this change was assigned when it happened.</summary>
  public required long SequenceNumber { get; init; }

  /// <summary>The entry as it existed at this change, so the client can replay history in order.</summary>
  public required QueueEntry Entry { get; init; }
}

/// <summary>
/// The response to GET /queue/since/{sequenceNumber}. Replaying the missed <see cref="QueueChangeEvent"/>s is
/// the common case, but a requested sequence number can't always be trusted or replayed cheaply — e.g. it's
/// negative, ahead of anything the server ever issued (most likely a dev database was reset since the client
/// last connected), or so far behind that the diff would be unreasonably large. Implementations fall back to
/// a full <see cref="QueueSnapshot"/> in those cases rather than erroring, so a client never has to special-case
/// "my sequence number wasn't accepted" — it always gets *something* to resync from. See the implementing
/// repository (e.g. <c>SqliteQueueRepository</c>) for the exact cutoff it applies.
/// </summary>
public sealed record QueueChangesSinceResponse
{
  /// <summary>The latest sequence number as of this response — the client's new "last known" baseline either way.</summary>
  public required long SequenceNumber { get; init; }

  /// <summary>True when <see cref="Snapshot"/> is populated instead of <see cref="Changes"/> (see type remarks).</summary>
  public required bool IsSnapshot { get; init; }

  /// <summary>The ordered (ascending sequence number) list of missed changes. Populated only when <see cref="IsSnapshot"/> is false.</summary>
  public IReadOnlyList<QueueChangeEvent>? Changes { get; init; }

  /// <summary>The full current queue state to resync from. Populated only when <see cref="IsSnapshot"/> is true.</summary>
  public QueueSnapshot? Snapshot { get; init; }
}
