using Microsoft.AspNetCore.SignalR;
using SignalRQueueDemo.Contracts;
using SignalRQueueDemo.Shared.Persistence;

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
/// <b>Why the hub never decides what changed:</b> <see cref="QueueUpdated"/> broadcasts always describe already-
/// committed repository state, never a client's own claim about what changed. Most originate at the REST
/// endpoints (see <c>QueueEndpoints</c>), which already build the exact payload and simply hand it to
/// <see cref="Hub{T}.Clients"/>. The one exception, <see cref="NotifyMutation"/>, still holds the line: it takes
/// only a sequence number from the caller and re-reads the actual entry/summary from
/// <see cref="IQueueRepository"/> before broadcasting — see its remarks for why.
/// </para>
///
/// <para>
/// <b>Why catch-up isn't a hub method:</b> "what did I miss" is answered over REST
/// (<c>GET /queue/since/{sequenceNumber}</c>), never by asking the hub. A dropped SignalR connection is
/// exactly the scenario where the hub can't be trusted to answer — REST-over-HTTP has its own independent
/// retry/timeout semantics and doesn't depend on the socket that just failed. (Blazor, which has no REST client
/// at all, answers the same question by calling <see cref="IQueueRepository.GetChangesSinceAsync"/> directly —
/// still not through the hub.)
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
public sealed class QueueHub(IQueueRepository repository, QueueBroadcaster broadcaster) : Hub<IQueueHubClient>
{
  // Explicit fields (not bare captured primary-constructor parameters) so call sites can use the this.
  // prefix required by CLAUDE.md's C# style for instance members.
  private readonly IQueueRepository repository = repository;
  private readonly QueueBroadcaster broadcaster = broadcaster;

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

  /// <summary>
  /// The one client-callable method on this hub, and the reason it's no longer un-gated by design (see type
  /// remarks): <c>SignalRQueueDemo.Web</c> writes to <see cref="IQueueRepository"/> directly (no REST call to
  /// this API — see docs/decisions.md's "Blazor is self-encapsulated"), so unlike a REST-triggered mutation, a
  /// Blazor-originated write never reaches <see cref="QueueBroadcaster"/> on its own. Blazor's own
  /// <c>HubConnection</c> — already open to receive pushes — calls this right after such a write so every other
  /// connected client (Angular, another Blazor tab) still finds out.
  ///
  /// <para>
  /// <b>Deliberately takes only a sequence number, never a client-supplied <see cref="QueueUpdated"/>.</b> This
  /// hub is reachable by any SignalR client that can open a connection to it — CORS (see <c>Program.cs</c>)
  /// stops a browser from doing so cross-origin, but it does nothing to stop a non-browser caller (curl, a
  /// hand-rolled client), since CORS is a browser-enforced concept. Accepting and re-broadcasting a caller-built
  /// payload would let anyone spoof "Now Serving" on every connected display or staff console. Instead this
  /// re-reads the actual change from the repository — the same trusted source every REST-triggered broadcast
  /// already uses — so the worst a hostile caller can do is trigger a redundant broadcast of data that's already
  /// public via the unauthenticated <c>GET /queue/since/{seq}</c> endpoint. No new exposure, and the "hub never
  /// decides what changed" invariant holds: it still only ever repeats what the repository says happened.
  /// </para>
  /// </summary>
  public async Task NotifyMutation(long sequenceNumber)
  {
    QueueChangesSinceResponse changes = await this.repository.GetChangesSinceAsync(sequenceNumber - 1);

    // Only trust an exact, single-event match for the requested sequence number. IsSnapshot means the requested
    // number was too far behind (or unrecognized) to diff — nothing here to safely attribute to this specific
    // mutation, so there's nothing to do: the caller already has its own correct state from the write it just
    // made, and every other client will pick this up on its own next mutation-triggered broadcast or reconnect
    // catch-up. Silently returning (not throwing) matches QueueBroadcaster's "never fail the caller" philosophy.
    QueueChangeEvent? change = !changes.IsSnapshot
      ? changes.Changes?.FirstOrDefault(c => c.SequenceNumber == sequenceNumber)
      : null;
    if (change is null)
    {
      return;
    }

    // The summary must be re-read too, not reused from `changes` (which carries none) — a snapshot as of right
    // now, not as of `sequenceNumber`, matching how every REST-triggered QueueUpdated already pairs a specific
    // change with the current whole-queue state at broadcast time.
    QueueStateResponse state = await this.repository.GetStateAsync();

    await this.broadcaster.BroadcastAsync(new QueueUpdated
    {
      SequenceNumber = sequenceNumber,
      ChangedEntry = change.Entry,
      Summary = state.Snapshot
    });
  }
}
