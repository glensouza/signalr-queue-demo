using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.Shared.Persistence;

/// <summary>
/// Distinguishes why a call-next/complete operation did or didn't happen. Kept as an internal repository-to-
/// endpoint result type (not in Contracts) rather than a wire type, because it never crosses the network as-is —
/// the endpoint translates each outcome into an HTTP status (200/404/409) and, on success, the <see cref="QueueUpdated"/>
/// payload that clients actually see.
/// </summary>
public enum QueueOperationOutcome
{
  /// <summary>The state transition happened; <see cref="QueueOperationResult.Update"/> is populated.</summary>
  Success,

  /// <summary>Call-next found no entry in <see cref="QueueStatus.Waiting"/> — an empty queue, not an error.</summary>
  NoWaitingEntries,

  /// <summary>Complete was called with an id that doesn't match any entry.</summary>
  EntryNotFound,

  /// <summary>Complete was called on an entry that isn't currently <see cref="QueueStatus.Serving"/>.</summary>
  InvalidState
}

/// <summary>Result of a call-next or complete operation against <see cref="IQueueRepository"/>.</summary>
public sealed record QueueOperationResult
{
  /// <summary>Which of the above happened.</summary>
  public required QueueOperationOutcome Outcome { get; init; }

  /// <summary>The broadcast-ready update, populated only when <see cref="Outcome"/> is <see cref="QueueOperationOutcome.Success"/>.</summary>
  public QueueUpdated? Update { get; init; }

  /// <summary>Shorthand for a successful result carrying its update.</summary>
  public static QueueOperationResult Success(QueueUpdated update) =>
    new() { Outcome = QueueOperationOutcome.Success, Update = update };

  /// <summary>Shorthand for a failure result identified by its outcome (no update payload).</summary>
  public static QueueOperationResult Failure(QueueOperationOutcome outcome) =>
    new() { Outcome = outcome };
}
