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
  /// <summary>Creates a new <see cref="QueueStatus.Waiting"/> entry and appends a change event for it.</summary>
  Task<CheckInResponse> CheckInAsync(CheckInRequest request, CancellationToken ct = default);

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

  /// <summary>Returns the current queue snapshot plus the latest sequence number, for GET /queue.</summary>
  Task<QueueStateResponse> GetStateAsync(CancellationToken ct = default);
}
