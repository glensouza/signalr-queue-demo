using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Persistence;

/// <summary>
/// Storage abstraction for the walk-in queue. Deliberately speaks only in <c>SignalRQueueDemo.Contracts</c>
/// types — no EF Core or Azure SDK types appear on this interface — so <see cref="Sqlite.SqliteQueueRepository"/>
/// (this issue) and the Azure Table Storage implementation (issue #4) can be swapped via config without either
/// one leaking its persistence technology into the API layer or into the other implementation.
/// </summary>
public interface IQueueRepository
{
  /// <summary>
  /// Creates a new <see cref="QueueStatus.Waiting"/> entry and appends a change event for it. Returns both the
  /// kiosk's <see cref="CheckInResponse"/> and the <see cref="QueueUpdated"/> to broadcast — see
  /// <see cref="CheckInResult"/> for why the two are paired rather than the endpoint rebuilding the broadcast.
  /// </summary>
  Task<CheckInResult> CheckInAsync(CheckInRequest request, CancellationToken ct = default);

  /// <summary>
  /// Moves the oldest <see cref="QueueStatus.Waiting"/> entry to <see cref="QueueStatus.Serving"/>.
  /// Returns <see cref="QueueOperationOutcome.NoWaitingEntries"/> rather than throwing when the queue is
  /// empty — an empty queue is an expected, routine state for this endpoint, not an error condition.
  /// </summary>
  Task<QueueOperationResult> CallNextAsync(CancellationToken ct = default);

  /// <summary>
  /// Moves the given <see cref="QueueStatus.Serving"/> entry to <see cref="QueueStatus.Completed"/>.
  /// Returns <see cref="QueueOperationOutcome.EntryNotFound"/> or <see cref="QueueOperationOutcome.InvalidState"/>
  /// instead of throwing so the endpoint can map each case to the correct HTTP status (404 vs 409).
  /// </summary>
  Task<QueueOperationResult> CompleteAsync(string entryId, CancellationToken ct = default);

  /// <summary>
  /// Returns just the latest sequence number, without building a queue snapshot. Exists so the SignalR hub can
  /// hand a newly-connected client its baseline on connect (see <c>QueueHub.OnConnectedAsync</c>) with a single
  /// <c>MAX(SequenceNumber)</c> read instead of materializing and discarding the whole queue via
  /// <see cref="GetStateAsync"/>.
  /// </summary>
  Task<long> GetLatestSequenceAsync(CancellationToken ct = default);

  /// <summary>Returns the current queue snapshot plus the latest sequence number, for GET /queue.</summary>
  Task<QueueStateResponse> GetStateAsync(CancellationToken ct = default);

  /// <summary>
  /// Returns everything that changed after <paramref name="sequenceNumber"/>, for GET /queue/since/{seq} — the
  /// REST half of the reconnect/catch-up protocol (see <c>QueueHub</c>). Implementations fall back to a full
  /// snapshot (<see cref="QueueChangesSinceResponse.IsSnapshot"/> = true) instead of the raw diff when the
  /// requested sequence number can't be trusted (negative, or ahead of everything ever issued) or the diff
  /// would be unreasonably large — see the implementation for its exact cutoff.
  /// </summary>
  Task<QueueChangesSinceResponse> GetChangesSinceAsync(long sequenceNumber, CancellationToken ct = default);
}
