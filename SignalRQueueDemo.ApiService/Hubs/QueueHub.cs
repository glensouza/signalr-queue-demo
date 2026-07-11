using Microsoft.AspNetCore.SignalR;
using SignalRQueueDemo.ApiService.Persistence;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Hubs;

/// <summary>
/// Strongly-typed client contract for <see cref="QueueHub"/>. Using a typed <c>Hub&lt;T&gt;</c> instead of the
/// untyped base <c>Hub</c> (which sends by string method name via <c>SendAsync("MethodName", ...)</c>) turns a
/// typo in a broadcast method name from a silent runtime no-op into a compile error — worth the small extra
/// ceremony on the one hub in this reference implementation the vendor team will copy for every future hub.
/// </summary>
public interface IQueueHubClient
{
  /// <summary>
  /// Pushed to every connected client after each successful check-in/call-next/complete. Carries the exact
  /// same <see cref="Contracts.QueueUpdated"/> payload the triggering REST call already returned to its own
  /// caller (see <c>QueueEndpoints</c>), so the client that made the change and every client that merely
  /// observes it see byte-identical data — nothing is recomputed for the broadcast.
  /// </summary>
  Task QueueUpdated(QueueUpdated update);

  /// <summary>
  /// Sent once, right after a connection is accepted (see <see cref="QueueHub.OnConnectedAsync"/>). Tells a
  /// freshly connecting client "the latest sequence number is at least this" so it always has a baseline to
  /// track from. Without it, a client that connects during a quiet period — nothing changes, so no
  /// <see cref="QueueUpdated"/> ever arrives — would have no sequence number recorded at all, and couldn't call
  /// GET /queue/since/{seq} correctly the next time it disconnects.
  ///
  /// <para>
  /// Treat this as a lower-bound floor, not an authoritative reset: fold it in with
  /// <c>lastSeq = max(lastSeq, value)</c>, exactly as you do for each <see cref="QueueUpdated.SequenceNumber"/>.
  /// A connection is admitted to the broadcast set before <see cref="QueueHub.OnConnectedAsync"/> runs, so a
  /// live <see cref="QueueUpdated"/> can legitimately arrive before this baseline — taking the max makes that
  /// arrival order harmless and never rewinds a client that already saw a higher number.
  /// </para>
  /// </summary>
  Task CurrentSequence(long sequenceNumber);
}

/// <summary>
/// Self-hosted SignalR hub — the default topology per ADR-0001 Option C, not the only one: a
/// <c>UseAzureSignalR</c> feature-flag escape hatch to the Azure SignalR emulator is documented in
/// <c>docs/architecture.md</c>. This hub is the reference pattern behind the reconnect/catch-up protocol either
/// way — copy its shape, not just its behavior, for any future hub.
///
/// <para>
/// <b>Why the hub never decides what changed:</b> <see cref="QueueUpdated"/> broadcasts originate at the REST
/// endpoints (see <c>QueueEndpoints</c>), not here. The hub exposes no method a client calls to mutate the
/// queue — every write goes through the REST API, which already builds the exact <see cref="QueueUpdated"/>
/// payload and simply hands it to <see cref="Hub{T}.Clients"/>. Duplicating that payload-building logic in
/// the hub would risk it drifting from what the REST caller received.
/// </para>
///
/// <para>
/// <b>Why catch-up isn't a hub method:</b> "what did I miss" is answered over REST
/// (<c>GET /queue/since/{sequenceNumber}</c>), never by asking the hub. A dropped SignalR connection is
/// exactly the scenario where the hub can't be trusted to answer — REST-over-HTTP has its own independent
/// retry/timeout semantics and doesn't depend on the socket that just failed.
/// </para>
///
/// <para>
/// <b>Ordering guarantee (read before wiring a client):</b> a broadcast only fires after its triggering write
/// has committed to the repository (see <c>QueueEndpoints</c>), so a client is always guaranteed to see
/// committed state if it calls <c>GET /queue/since/{seq}</c> immediately after receiving a push — there is no
/// window where the broadcast arrives ahead of the data it describes. What is <i>not</i> guaranteed is that
/// broadcasts from two concurrent requests arrive in strict sequence-number order — e.g. a check-in and a
/// call-next landing at nearly the same instant can commit in one order but have their <c>SendAsync</c>
/// continuations scheduled by the runtime in the other. Clients must track the <b>highest</b>
/// <see cref="QueueUpdated.SequenceNumber"/> seen so far, never just "the most recently arrived message", and
/// must treat the sequence number — not arrival order — as authoritative.
/// </para>
/// </summary>
public sealed class QueueHub(IQueueRepository repository) : Hub<IQueueHubClient>
{
  // Explicit field (not a bare captured primary-constructor parameter) so call sites can use the
  // this.repository prefix required by CLAUDE.md's C# style for instance members.
  private readonly IQueueRepository repository = repository;

  /// <summary>
  /// SignalR calls this for every new connection — first-time and reconnect look identical at this layer, so
  /// both get the same treatment: read the latest sequence number (a single MAX query via
  /// <see cref="IQueueRepository.GetLatestSequenceAsync"/>, not a whole-queue snapshot) and send it to the
  /// connection that just joined as its baseline floor. The connection is already in the broadcast set by the
  /// time this runs, so a live <see cref="QueueUpdated"/> may reach it before this baseline — that's why
  /// <see cref="IQueueHubClient.CurrentSequence"/> is documented as a floor the client folds in with max(),
  /// not an authoritative reset.
  /// </summary>
  public override async Task OnConnectedAsync()
  {
    // Deliberately NOT passing Context.ConnectionAborted: if the client drops mid-read that token cancels and
    // throws OperationCanceledException out of OnConnectedAsync as a noisy unhandled hub error. The read is a
    // sub-millisecond single-row query, so letting it finish on an already-dead connection is cheaper than the
    // exception; the CurrentSequence send that follows simply no-ops for a gone connection.
    long latestSequence = await this.repository.GetLatestSequenceAsync();
    await this.Clients.Caller.CurrentSequence(latestSequence);
    await base.OnConnectedAsync();
  }
}
