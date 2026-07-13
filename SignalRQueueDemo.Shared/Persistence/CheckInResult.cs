using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.Shared.Persistence;

/// <summary>
/// The two things a successful check-in produces: the <see cref="CheckInResponse"/> the kiosk gets back, and
/// the <see cref="QueueUpdated"/> that gets broadcast to every other client. Pairing them here keeps check-in
/// symmetric with call-next/complete — those return a <see cref="QueueOperationResult"/> already carrying a
/// ready-built <see cref="QueueUpdated"/>, so the endpoint layer broadcasts all three the same way instead of
/// hand-reassembling a payload for check-in. It also keeps the broadcast snapshot off <see cref="CheckInResponse"/>
/// itself, which is the kiosk's own wire shape and has no reason to carry the whole-queue summary.
/// </summary>
public sealed record CheckInResult
{
  /// <summary>What the checking-in kiosk receives: its entry id, position, sequence number, and entry.</summary>
  public required CheckInResponse Response { get; init; }

  /// <summary>The broadcast payload for every other connected client, built from the same committed state.</summary>
  public required QueueUpdated Update { get; init; }
}
