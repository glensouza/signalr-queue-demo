import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { firstValueFrom } from 'rxjs';
import { RuntimeConfigService } from '../config/runtime-config.service';
import { QueueEntry, QueueSnapshot, QueueStatus, QueueUpdated } from '../models/queue.models';
import { QueueApiService } from './queue-api.service';

/** How `connectionState` communicates which of the two transports (live push vs. polling) is currently the source of truth, for a smoke page or status indicator to render honestly instead of pretending everything is always "live". */
export type QueueConnectionState = 'connecting' | 'live' | 'reconnecting' | 'polling';

/** GET /queue polling interval used only once the SignalR connection is given up on — see startPolling. Five seconds trades displayed staleness against request volume; short enough that a waiting-room board still feels responsive, long enough not to hammer the API from every client that fell back at once. */
const POLL_INTERVAL_MS = 5_000;

/** How long the initial HubConnection.start() is given to succeed before this service gives up on the socket for this session and switches to polling. Generous enough to cover a slow container cold-start (#12), short enough that a kiosk visitor isn't staring at "connecting..." for a long time first. */
const INITIAL_CONNECT_TIMEOUT_MS = 5_000;

/**
 * The reference client for the reconnect/catch-up protocol documented on `QueueHub.cs`,
 * `QueueEndpoints.HandleGetSinceAsync`, and `docs/architecture.md`'s "Reconnect / catch-up protocol" sequence
 * diagram. Every app in this workspace (#9-#11) is expected to depend on this service rather than opening its
 * own `HubConnection` — the whole point of building this once in the shared lib is that the tricky part (never
 * silently missing a state change across a disconnect) is solved exactly once, reviewed once, and then simply
 * consumed three times.
 *
 * <h3>The three states a consumer needs to know about</h3>
 * Exposed as Angular signals so a component can bind straight to them without manual subscription management:
 * - {@link snapshot} — the current best-known {@link QueueSnapshot}. Always what a list/board should render.
 * - {@link lastUpdate} — the most recent *live* {@link QueueUpdated} push (null until one arrives; not set by
 *   catch-up replay or polling, which don't carry a single "this one entry changed" event) — useful for a
 *   "just changed" highlight animation, which is why it's kept separate from {@link snapshot}.
 * - {@link connectionState} — `'connecting'` until the first attempt resolves one way or the other, `'live'`
 *   while a SignalR connection is actually up, `'reconnecting'` during an automatic-reconnect attempt (state is
 *   still whatever was last known — SignalR does not drop messages mid-reconnect, it queues the gap for catch-up
 *   once reconnected), `'polling'` once the socket has been given up on for this session.
 *
 * <h3>Why sequence numbers, not "did a message arrive"</h3>
 * QueueHub documents that a client must track the *highest* sequence number seen, never just "the most recent
 * arrival" — concurrent server-side commits can have their broadcasts scheduled out of strict sequence order.
 * This service does exactly that: {@link highestSequenceSeen} only ever moves forward (`Math.max`), fed by
 * every source that carries a sequence number (`CurrentSequence` on connect, each live `QueueUpdated`, and every
 * `GET /queue`/`GET /queue/since` response). That number is what {@link catchUp} sends back to the server after
 * a reconnect, so replay is always relative to the highest-confidence baseline this client has ever observed —
 * never an arbitrary "last message I happened to process".
 *
 * <h3>Why catch-up needs its own small local index</h3>
 * A live `QueueUpdated` push carries a full, authoritative {@link QueueSnapshot} in its `summary` field — trivial
 * to apply, just replace {@link snapshot} wholesale. But `GET /queue/since/{seq}`'s non-snapshot response only
 * returns the individual `QueueChangeEvent`s that were missed, each with just the one entry that changed, no
 * snapshot. To turn "here are the three entries that changed while you were gone" back into a full snapshot the
 * rest of this service can treat uniformly, {@link entriesById} keeps a running index of every known entry,
 * updated by every snapshot this service ever applies; `catchUp` patches that index with the missed entries and
 * re-derives counts from it. Entry *order* in the derived snapshot is therefore last-known-order-plus-newly-seen
 * -appended, not guaranteed to exactly match the server's true ordering — acceptable for this reference client
 * (the next live push or full snapshot self-heals it), but worth knowing if a consumer renders order-sensitively.
 */
@Injectable({ providedIn: 'root' })
export class QueueHubService implements OnDestroy {
  private readonly runtimeConfig = inject(RuntimeConfigService);
  private readonly api = inject(QueueApiService);

  private readonly snapshotSignal = signal<QueueSnapshot | null>(null);
  private readonly lastUpdateSignal = signal<QueueUpdated | null>(null);
  private readonly connectionStateSignal = signal<QueueConnectionState>('connecting');

  readonly snapshot = this.snapshotSignal.asReadonly();
  readonly lastUpdate = this.lastUpdateSignal.asReadonly();
  readonly connectionState = this.connectionStateSignal.asReadonly();
  /** Convenience derived signal — true whenever `connectionState` is `'polling'`, for a "reconnecting..." banner. */
  readonly isPolling = computed(() => this.connectionStateSignal() === 'polling');

  /** Local index used only to reconstruct a full snapshot from catch-up's per-entry change log — see the class remarks. */
  private readonly entriesById = new Map<string, QueueEntry>();
  /** Monotonically non-decreasing — see the class remarks on why this is `Math.max`'d, never overwritten. */
  private highestSequenceSeen = 0;

  private connection: HubConnection | null = null;
  private pollHandle: ReturnType<typeof setInterval> | null = null;
  private started = false;
  /** Set once {@link stop} runs. Guards every async continuation that could otherwise start work (a polling interval, a state change) after teardown — see {@link stop} and {@link startPolling}. */
  private disposed = false;

  /**
   * Begins the connect-or-fall-back-to-polling sequence. Idempotent — a second call while already started is a
   * no-op, so a component's constructor can call this unconditionally without a "have I already started?" guard.
   * Callers should await this if they need `snapshot` populated before their first render (e.g. server-rendered
   * paths); a live UI can also just bind to the signals and let them populate asynchronously.
   */
  async start(): Promise<void> {
    if (this.started) {
      return;
    }

    this.started = true;

    // Seed initial state over plain REST before ever touching the socket. GET /queue is also what a client
    // loading for the first time (not reconnecting) is documented to use for exactly this reason — it returns
    // a sequence number in the same response, so there's no separate round-trip needed to discover a baseline.
    //
    // Wrapped in try/catch on purpose: if the API isn't reachable yet — the exact container cold-start window #12
    // introduces — a rejected seed must NOT abort start(), or the hub-connect/polling fallback below would never
    // run and the service would be permanently wedged (started is already true, so nothing retries). A failed
    // seed just leaves `snapshot` null until the hub connects or the polling fallback produces one.
    try {
      await this.fetchAndApplyState();
    } catch {
      // Intentionally swallowed — connectHubOrFallBackToPolling() is the recovery path.
    }

    await this.connectHubOrFallBackToPolling();
  }

  /** Tears down the socket (if any) and stops polling (if running). Called automatically on Angular's destroy hook; exposed publicly too since this is a root-provided singleton that outlives any one component. */
  stop(): void {
    // Set before stopping the connection: connection.stop() fires onclose *asynchronously*, and without this flag
    // that onclose would call startPolling() and spin up an orphaned interval that polls forever after teardown.
    this.disposed = true;
    this.stopPolling();
    void this.connection?.stop();
    this.connection = null;
  }

  ngOnDestroy(): void {
    this.stop();
  }

  private async connectHubOrFallBackToPolling(): Promise<void> {
    const hubUrl = `${this.runtimeConfig.get().apiBaseUrl}/hubs/queue`;

    // withAutomaticReconnect only takes over *after* a connection has been established at least once — it does
    // not retry a HubConnection.start() that never succeeded in the first place. That asymmetry is exactly why
    // this method has two separate failure paths below: the initial-connect timeout race, and the onclose
    // handler for a connection that did come up but then exhausted every automatic-reconnect attempt.
    this.connection = new HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 10_000])
      .configureLogging(LogLevel.Warning)
      .build();

    this.connection.on('QueueUpdated', (update: QueueUpdated) => this.handleLiveUpdate(update));
    // Sent once right after every connect (first-time and reconnect look identical to the hub — see
    // QueueHub.OnConnectedAsync). Treated strictly as a floor, per the hub's documented contract: a live
    // QueueUpdated can legitimately arrive before this, so folding it in with Math.max is what makes that
    // arrival order harmless instead of a rare "sequence number went backwards" bug.
    this.connection.on('CurrentSequence', (sequenceNumber: number) => {
      this.highestSequenceSeen = Math.max(this.highestSequenceSeen, sequenceNumber);
    });

    this.connection.onreconnecting(() => this.connectionStateSignal.set('reconnecting'));
    this.connection.onreconnected(() => {
      // The one line this whole service exists to make trivial for #9-#11: replay whatever was missed, then
      // resume trusting live pushes again. The .catch chain matters: if the catch-up REST call fails transiently
      // right after reconnect, leaving it unhandled would strand connectionState on 'reconnecting' forever AND
      // silently drop the very gap the reconnect exists to recover. So on catch-up failure, try a one-shot
      // full-snapshot resync (GET /queue) — the socket is genuinely live again, so recovering current state that
      // way is enough — and only drop to the polling fallback if that resync also fails.
      void this.catchUp()
        .then(() => this.connectionStateSignal.set('live'))
        .catch(() =>
          this.fetchAndApplyState()
            .then(() => this.connectionStateSignal.set('live'))
            .catch(() => this.startPolling()),
        );
    });
    // Fires once automatic reconnect has exhausted the backoff array above — i.e. the server/network has been
    // unreachable long enough that SignalR itself gave up. Guarded against disposed because connection.stop()
    // *also* fires onclose: without the guard, a normal stop()/ngOnDestroy would start an orphaned polling
    // interval. Only a genuine unexpected close should fall back to polling.
    this.connection.onclose(() => {
      if (!this.disposed) {
        this.startPolling();
      }
    });

    // Hold a single reference to the initial-connect promise so both the timeout race and the late-outcome
    // handlers below observe the *same* promise — that's what keeps a late rejection (start() losing the race,
    // then failing for good a moment later) from surfacing as an unhandled promise rejection.
    const connectPromise = this.connection.start();
    connectPromise
      .then(() => {
        // Slow cold-start: start() finally succeeded *after* the timeout below already fell back to polling.
        // Adopt the now-live socket and stop the redundant poller instead of running both.
        if (!this.disposed && this.connectionStateSignal() === 'polling') {
          this.stopPolling();
          this.connectionStateSignal.set('live');
        }
      })
      .catch(() => {
        // Initial connect failed for good — the timeout catch below has already started polling; this catch
        // exists purely so the rejection is observed rather than left unhandled.
      });

    try {
      // A hung TCP handshake (e.g. a container that's still starting under #12) would otherwise leave this
      // service waiting indefinitely with no fallback — HubConnection.start() has no built-in timeout of its
      // own, so this race is what actually bounds "how long do we wait before assuming the socket won't connect".
      await this.raceWithTimeout(connectPromise, INITIAL_CONNECT_TIMEOUT_MS);
      if (!this.disposed) {
        this.connectionStateSignal.set('live');
      }
    } catch {
      this.startPolling();
    }
  }

  private async raceWithTimeout(promise: Promise<void>, timeoutMs: number): Promise<void> {
    let timeoutHandle: ReturnType<typeof setTimeout>;
    const timeout = new Promise<never>((_resolve, reject) => {
      timeoutHandle = setTimeout(() => reject(new Error(`Timed out after ${timeoutMs}ms`)), timeoutMs);
    });

    try {
      await Promise.race([promise, timeout]);
    } finally {
      clearTimeout(timeoutHandle!);
    }
  }

  private handleLiveUpdate(update: QueueUpdated): void {
    this.highestSequenceSeen = Math.max(this.highestSequenceSeen, update.sequenceNumber);
    this.applySnapshot(update.summary);
    this.lastUpdateSignal.set(update);
  }

  /**
   * The REST half of the reconnect/catch-up protocol — called once per successful reconnect (see
   * `onreconnected` above). Picks between the two GET /queue/since/{seq} response shapes exactly as
   * `QueueChangesSinceResponse`'s doc comment describes: a snapshot fallback replaces state outright, a
   * change-event list gets patched into {@link entriesById} and re-derived (see the class remarks for why).
   */
  private async catchUp(): Promise<void> {
    const response = await firstValueFrom(this.api.getChangesSince(this.highestSequenceSeen));

    // Guard against a live QueueUpdated that arrived while this request was in flight: if a push has already
    // advanced us past this catch-up window, its snapshot is newer and authoritative, so applying the replayed
    // (and, in the change-log branch, locally re-derived and possibly re-ordered) state here would clobber it
    // with staler data. JS is single-threaded, so nothing can interleave between this check and the apply below.
    if (response.sequenceNumber >= this.highestSequenceSeen) {
      if (response.isSnapshot) {
        this.applySnapshot(response.snapshot!);
      } else {
        for (const change of response.changes ?? []) {
          this.entriesById.set(change.entry.id, change.entry);
        }

        this.applySnapshot(this.deriveSnapshotFromIndex());
      }
    }

    this.highestSequenceSeen = Math.max(this.highestSequenceSeen, response.sequenceNumber);
  }

  /** Replaces {@link snapshot} and rebuilds {@link entriesById} to match — the one place both stay in sync, so every other method just calls this instead of touching the signal and the map separately. */
  private applySnapshot(snapshot: QueueSnapshot): void {
    this.entriesById.clear();
    for (const entry of snapshot.queue) {
      this.entriesById.set(entry.id, entry);
    }

    this.snapshotSignal.set(snapshot);
  }

  private deriveSnapshotFromIndex(): QueueSnapshot {
    const queue = Array.from(this.entriesById.values());
    return {
      totalWaiting: queue.filter((entry) => entry.status === QueueStatus.Waiting).length,
      totalServing: queue.filter((entry) => entry.status === QueueStatus.Serving).length,
      totalCompleted: queue.filter((entry) => entry.status === QueueStatus.Completed).length,
      queue,
    };
  }

  private startPolling(): void {
    // Bail if disposed (a post-teardown onclose must not resurrect a timer) or already polling.
    if (this.disposed || this.pollHandle !== null) {
      return;
    }

    this.connectionStateSignal.set('polling');
    // Once we're polling because a *previously working* socket died and its automatic reconnect gave up, we don't
    // periodically retry the hub in the background — polling is the terminal fallback for the session. (The one
    // exception is a slow *initial* connect that lands after this fallback started: see the adopt logic in
    // connectHubOrFallBackToPolling, which upgrades polling→live in that specific case.)
    this.pollHandle = setInterval(() => void this.pollOnce(), POLL_INTERVAL_MS);
  }

  private stopPolling(): void {
    if (this.pollHandle !== null) {
      clearInterval(this.pollHandle);
      this.pollHandle = null;
    }
  }

  private async pollOnce(): Promise<void> {
    // Errors are swallowed on purpose: polling is the fallback for an unreachable API, so each failed tick is
    // expected, must not surface as an unhandled rejection, and simply retries on the next interval — the fixed
    // POLL_INTERVAL_MS cadence is the (deliberate) backoff.
    try {
      await this.fetchAndApplyState();
    } catch {
      // Stay in polling mode; the next tick retries.
    }
  }

  /**
   * Fetches the current snapshot + latest sequence over REST (GET /queue) and applies both. Throws on failure so
   * each caller reacts appropriately: the initial seed swallows it (fallback recovers), a poll tick swallows and
   * retries, and the post-reconnect resync treats it as "try polling instead". Applies the server's authoritative
   * full snapshot, so — unlike catch-up's re-derived snapshot — it's always safe to apply without a race guard.
   */
  private async fetchAndApplyState(): Promise<void> {
    const state = await firstValueFrom(this.api.getQueue());
    this.applySnapshot(state.snapshot);
    this.highestSequenceSeen = Math.max(this.highestSequenceSeen, state.sequenceNumber);
  }

  /** True while a live SignalR connection is up — exposed for consumers that want the raw SignalR state rather than the higher-level {@link connectionState}. */
  get isSocketConnected(): boolean {
    return this.connection?.state === HubConnectionState.Connected;
  }
}
