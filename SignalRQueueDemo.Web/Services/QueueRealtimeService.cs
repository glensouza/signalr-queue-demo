using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using SignalRQueueDemo.Contracts;
using SignalRQueueDemo.Shared.Persistence;

namespace SignalRQueueDemo.Web.Services;

/// <summary>
/// How <see cref="QueueRealtimeService.ConnectionState"/> communicates which of the two transports (live push vs.
/// polling) is currently the source of truth — so a page can render honestly ("Reconnecting…") instead of
/// pretending everything is always live. Mirrors the Angular shared library's <c>QueueConnectionState</c>.
/// </summary>
public enum QueueConnectionState
{
  Connecting,
  Live,
  Reconnecting,
  Polling
}

/// <summary>
/// The .NET twin of the Angular shared library's <c>QueueHubService</c>
/// (<c>SignalRQueueDemo.Angular/projects/shared/src/lib/services/queue-hub.service.ts</c>) — same state shape
/// (<see cref="Snapshot"/>/<see cref="LastUpdate"/>/<see cref="ConnectionState"/>, monotonic sequence-number
/// tracking, an <c>entriesById</c> index used to reconstruct a snapshot from catch-up's per-entry diff, a
/// polling fallback), reimplemented for Blazor Server. Registered <b>Scoped</b> — one instance per Blazor
/// circuit, i.e. one per browser tab — the direct analogue of the Angular version's tab-scoped
/// <c>providedIn: 'root'</c> singleton (a fresh Angular app load per tab gets its own service instance; a fresh
/// Blazor circuit per tab gets its own DI scope).
///
/// <para>
/// Two real differences from the Angular version, both because Blazor has no REST client at all (see
/// docs/decisions.md's "Blazor is self-encapsulated") and so answers the same questions a different way:
/// </para>
/// <list type="bullet">
/// <item>Catch-up and the polling fallback call <see cref="IQueueRepository"/> directly — the exact in-process
/// call ApiService's own REST handlers make — instead of an HTTP GET.</item>
/// <item>After a local write, <see cref="PublishAsync"/> tells this app's own <c>QueueHub</c> about it over this
/// same <see cref="HubConnection"/> (<c>NotifyMutation</c>) so every other connected Blazor circuit finds out too
/// — see <c>QueueHub.NotifyMutation</c>'s remarks for the trust design behind that method. This circuit's own UI
/// updates from the resulting broadcast arriving back over the normal <c>QueueUpdated</c> handler below, exactly
/// like every other circuit; there's no local-apply special case. (The hub is Blazor's own — the Angular stack
/// has its own separate hub in ApiService, so a Blazor write never reaches Angular clients live, and vice
/// versa; the two stacks are fully independent. See docs/decisions.md.)</item>
/// </list>
/// </summary>
public sealed class QueueRealtimeService(
  IQueueRepository repository,
  NavigationManager navigationManager,
  ILogger<QueueRealtimeService> logger) : IAsyncDisposable
{
  // Same backoff schedule and cutoffs as the Angular reference client, so the two stacks behave identically for
  // the manual reconnect-catch-up test script in README.md.
  private static readonly TimeSpan[] ReconnectDelays =
  [
    TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)
  ];

  private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan InitialConnectTimeout = TimeSpan.FromSeconds(5);

  private readonly IQueueRepository repository = repository;
  private readonly NavigationManager navigationManager = navigationManager;
  private readonly ILogger<QueueRealtimeService> logger = logger;

  /// <summary>
  /// Guards <see cref="entriesById"/> and <see cref="highestSequenceSeen"/> — both are read and mutated from
  /// several different threads (a <see cref="HubConnection"/> callback thread for live pushes/reconnect
  /// handlers, the poll <see cref="Timer"/>'s threadpool thread, and the Blazor circuit's renderer thread via
  /// <see cref="PublishAsync"/>), and neither a plain <see cref="Dictionary{TKey,TValue}"/> nor a bare
  /// read-modify-write on a <c>long</c> is safe under concurrent access. <c>lock</c> is reentrant per-thread, so
  /// helpers below that take the lock can freely call each other without deadlocking.
  /// </summary>
  private readonly Lock stateLock = new();

  /// <summary>Local index used only to reconstruct a full snapshot from catch-up's per-entry change log — see the class remarks. Access only under <see cref="stateLock"/>.</summary>
  private readonly Dictionary<string, QueueEntry> entriesById = [];

  private readonly SemaphoreSlim startGate = new(1, 1);
  private HubConnection? connection;
  private Timer? pollTimer;

  /// <summary>Monotonically non-decreasing — see the class remarks on why this is Math.Max'd, never overwritten. Access only under <see cref="stateLock"/>.</summary>
  private long highestSequenceSeen;

  private bool started;
  private bool disposed;

  /// <summary>The current best-known snapshot. Always what a list/board should render.</summary>
  public QueueSnapshot? Snapshot { get; private set; }

  /// <summary>
  /// The most recent *live* <see cref="QueueUpdated"/> push — null until one arrives; not set by catch-up replay
  /// or polling, which don't carry a single "this one entry changed" event.
  /// </summary>
  public QueueUpdated? LastUpdate { get; private set; }

  public QueueConnectionState ConnectionState { get; private set; } = QueueConnectionState.Connecting;

  /// <summary>
  /// Raised whenever <see cref="Snapshot"/>/<see cref="LastUpdate"/>/<see cref="ConnectionState"/> changes. Fires
  /// on whatever thread the update arrived on (a <see cref="HubConnection"/> callback, a poll tick, or the
  /// calling thread for <see cref="StartAsync"/>'s own initial seed) — a subscribing component must marshal back
  /// with its own <c>InvokeAsync(StateHasChanged)</c>, the same as any Blazor component reacting to a
  /// background event.
  /// </summary>
  public event Action? Changed;

  /// <summary>
  /// Begins the connect-or-fall-back-to-polling sequence. Idempotent — a second call while already started is a
  /// no-op, so a page's <c>OnInitializedAsync</c> can call this unconditionally without a guard of its own.
  /// </summary>
  public async Task StartAsync(CancellationToken ct = default)
  {
    await this.startGate.WaitAsync(ct);
    try
    {
      if (this.started)
      {
        return;
      }

      this.started = true;
    }
    finally
    {
      this.startGate.Release();
    }

    // Seed initial state directly from the repository before ever touching the socket — same reasoning as the
    // Angular version's GET /queue seed, just an in-process call instead of an HTTP one. Swallowed on purpose:
    // a failed seed must not abort startup, or the hub-connect/polling fallback below would never run.
    try
    {
      await this.FetchAndApplyStateAsync(ct);
    }
    catch (Exception ex)
    {
      this.logger.LogWarning(ex, "Initial queue state seed failed; will retry via the hub connection or polling.");
    }

    await this.ConnectHubOrFallBackToPollingAsync(ct);
  }

  /// <summary>
  /// Tells <c>QueueHub</c> about a mutation this circuit just made directly against the repository, so every
  /// other connected client finds out — see <c>QueueHub.NotifyMutation</c>'s remarks. Never throws: the write
  /// behind <paramref name="update"/> is already committed, so a failed notify can't be allowed to fail the
  /// caller (the same "already committed, can't fail the caller" rule <c>QueueBroadcaster</c> follows).
  /// </summary>
  public async Task PublishAsync(QueueUpdated update, CancellationToken ct = default)
  {
    this.UpdateHighestSequenceSeen(update.SequenceNumber);

    if (this.connection is not { State: HubConnectionState.Connected })
    {
      // No live socket to notify over right now — the polling fallback (or the next successful reconnect's
      // catch-up) will pick this change up for other clients soon; this circuit's own view is already correct
      // from the direct repository call the caller just made.
      return;
    }

    try
    {
      await this.connection.SendAsync("NotifyMutation", update.SequenceNumber, ct);
    }
    catch (Exception ex)
    {
      this.logger.LogWarning(
        ex,
        "NotifyMutation failed for sequence {SequenceNumber}; other clients will catch up on their own next reconnect.",
        update.SequenceNumber);
    }
  }

  private async Task ConnectHubOrFallBackToPollingAsync(CancellationToken ct)
  {
    // The hub is THIS app's own (mapped at /hubs/queue in Program.cs), not ApiService's — Blazor is fully
    // self-encapsulated and has no dependency on the API process. So the connection target is simply this app's
    // own base address, read from NavigationManager (BaseUri already ends with '/'). This is a same-process
    // loopback connection: the Blazor Server host connects to its own Kestrel to receive the pushes its own hub
    // fans out. BaseUri is always populated by the time a component's OnInitializedAsync triggers StartAsync, so
    // no "not configured" fallback is needed the way the old cross-process service-discovery lookup required.
    string hubUrl = $"{this.navigationManager.BaseUri}hubs/queue";

    this.connection = new HubConnectionBuilder()
      .WithUrl(hubUrl, options =>
      {
        // This is a loopback connection to our OWN in-process hub. Under `aspire run` the app is served over HTTPS,
        // so the server-to-self connection has to validate the ASP.NET Core dev certificate — which fails in some
        // setups and silently drops the client to polling forever (the persistent "Reconnecting…" banner on every
        // page). Trusting our own machine's certificate for a same-process loopback is safe — the target is always
        // NavigationManager.BaseUri, i.e. this very app — and without it the self-hosted-hub design only works over
        // plain HTTP. Both the negotiate (HTTP) request and the WebSocket transport need the override.
        options.HttpMessageHandlerFactory = handler =>
        {
          if (handler is HttpClientHandler httpClientHandler)
          {
            httpClientHandler.ServerCertificateCustomValidationCallback =
              HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
          }

          return handler;
        };
        options.WebSocketConfiguration = socket =>
          socket.RemoteCertificateValidationCallback = (_, _, _, _) => true;
      })
      .WithAutomaticReconnect(ReconnectDelays)
      .Build();

    this.connection.On<QueueUpdated>("QueueUpdated", this.HandleLiveUpdate);
    // Sent once right after every connect (first-time and reconnect look identical to the hub — see
    // QueueHub.OnConnectedAsync). Treated strictly as a floor, per the hub's documented contract.
    this.connection.On<long>("CurrentSequence", this.UpdateHighestSequenceSeen);

    this.connection.Reconnecting += _ =>
    {
      this.SetConnectionState(QueueConnectionState.Reconnecting);
      return Task.CompletedTask;
    };

    this.connection.Reconnected += async _ =>
    {
      // The one call this whole service exists to make trivial: replay whatever was missed, then resume
      // trusting live pushes again. Falls back to a full resync, then to polling, mirroring the Angular
      // version's onreconnected chain so both stacks behave identically under the same test.
      try
      {
        await this.CatchUpAsync(CancellationToken.None);
        this.SetConnectionState(QueueConnectionState.Live);
      }
      catch (Exception ex)
      {
        this.logger.LogWarning(ex, "Catch-up after reconnect failed; attempting a full resync instead.");
        try
        {
          await this.FetchAndApplyStateAsync(CancellationToken.None);
          this.SetConnectionState(QueueConnectionState.Live);
        }
        catch (Exception resyncEx)
        {
          this.logger.LogWarning(resyncEx, "Full resync after reconnect also failed; falling back to polling.");
          this.StartPolling();
        }
      }
    };

    // Fires once automatic reconnect has exhausted the backoff array above. Guarded against disposed the same
    // way the Angular version guards its onclose: DisposeAsync() below also triggers Closed, and without the
    // guard a normal teardown would spin up an orphaned polling timer.
    this.connection.Closed += _ =>
    {
      if (!this.disposed)
      {
        this.StartPolling();
      }

      return Task.CompletedTask;
    };

    try
    {
      // A hung TCP handshake (e.g. ApiService still starting under Aspire) would otherwise leave this circuit
      // waiting indefinitely with no fallback — HubConnection.StartAsync has no built-in timeout of its own.
      using CancellationTokenSource timeoutCts = new(InitialConnectTimeout);
      using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
      await this.connection.StartAsync(linkedCts.Token);
      this.SetConnectionState(QueueConnectionState.Live);
    }
    catch (Exception ex)
    {
      this.logger.LogWarning(
        ex, "Initial hub connection did not complete within {Timeout}; falling back to polling.", InitialConnectTimeout);
      this.StartPolling();

      // withAutomaticReconnect only takes over AFTER a connection has been established at least once — it does
      // not retry a StartAsync that never succeeded in the first place. This background retry is what covers a
      // slow cold start; it adopts the now-live socket and stops the redundant poller if it comes up late.
      _ = this.RetryInitialConnectInBackgroundAsync();
    }
  }

  private async Task RetryInitialConnectInBackgroundAsync()
  {
    if (this.connection is null)
    {
      return;
    }

    try
    {
      await this.connection.StartAsync();
      if (!this.disposed)
      {
        this.StopPolling();
        this.SetConnectionState(QueueConnectionState.Live);
      }
    }
    catch (Exception ex)
    {
      this.logger.LogWarning(ex, "Background hub connect retry failed; this circuit stays on polling.");
    }
  }

  private void HandleLiveUpdate(QueueUpdated update)
  {
    this.UpdateHighestSequenceSeen(update.SequenceNumber);
    // Set before ApplySnapshot, not after: ApplySnapshot already raises Changed once, so subscribers that read
    // LastUpdate from their Changed handler see it populated. A second Changed?.Invoke() here (as this used to
    // do) would fire every subscriber's re-render twice for one push, for no additional information.
    this.LastUpdate = update;
    this.ApplySnapshot(update.Summary);
  }

  /// <summary>
  /// The catch-up half of the reconnect protocol — called once per successful reconnect. Picks between the two
  /// <see cref="IQueueRepository.GetChangesSinceAsync"/> response shapes exactly as
  /// <see cref="QueueChangesSinceResponse"/>'s doc comment describes: a snapshot fallback replaces state
  /// outright, a change-event list gets patched into <see cref="entriesById"/> and re-derived.
  /// </summary>
  private async Task CatchUpAsync(CancellationToken ct)
  {
    QueueChangesSinceResponse response = await this.repository.GetChangesSinceAsync(this.GetHighestSequenceSeen(), ct);

    // Guard against a live QueueUpdated that arrived while this call was in flight: if a push already advanced
    // us past this catch-up window, its snapshot is newer and authoritative, so applying this replay would
    // clobber it with staler data. (Reading highestSequenceSeen for the check and applying the replay are still
    // two separate locked operations — a push landing in the gap between them is the same accepted race the
    // Angular reference client has in its single-threaded equivalent, not something the lock is meant to close.)
    if (response.SequenceNumber >= this.GetHighestSequenceSeen())
    {
      if (response.IsSnapshot)
      {
        this.ApplySnapshot(response.Snapshot!);
      }
      else
      {
        lock (this.stateLock)
        {
          foreach (QueueChangeEvent change in response.Changes ?? [])
          {
            this.entriesById[change.Entry.Id] = change.Entry;
          }
        }

        this.ApplySnapshot(this.DeriveSnapshotFromIndex());
      }
    }

    this.UpdateHighestSequenceSeen(response.SequenceNumber);
  }

  /// <summary>
  /// Reads the current snapshot + latest sequence directly from the repository and applies both. The polling
  /// fallback's tick and the initial seed both call this — unlike the Angular version's GET /queue, a failed
  /// call here means the repository itself is unreachable, not just the API process, but the error-handling
  /// contract is identical: throw, and let each caller decide how to react.
  /// </summary>
  private async Task FetchAndApplyStateAsync(CancellationToken ct)
  {
    QueueStateResponse state = await this.repository.GetStateAsync(ct);
    this.ApplySnapshot(state.Snapshot);
    this.UpdateHighestSequenceSeen(state.SequenceNumber);
  }

  /// <summary>Replaces <see cref="Snapshot"/> and rebuilds <see cref="entriesById"/> to match — the one place both stay in sync.</summary>
  private void ApplySnapshot(QueueSnapshot snapshot)
  {
    lock (this.stateLock)
    {
      this.entriesById.Clear();
      foreach (QueueEntry entry in snapshot.Queue)
      {
        this.entriesById[entry.Id] = entry;
      }
    }

    this.Snapshot = snapshot;

    // Raised outside the lock: subscribers may synchronously call back into this service (e.g. a component
    // re-rendering could read Snapshot/ConnectionState), and holding stateLock while running arbitrary
    // subscriber code risks a deadlock or an unnecessarily long lock hold.
    this.Changed?.Invoke();
  }

  private QueueSnapshot DeriveSnapshotFromIndex()
  {
    lock (this.stateLock)
    {
      List<QueueEntry> queue = [.. this.entriesById.Values];
      return new QueueSnapshot
      {
        TotalWaiting = queue.Count(e => e.Status == QueueStatus.Waiting),
        TotalServing = queue.Count(e => e.Status == QueueStatus.Serving),
        TotalCompleted = queue.Count(e => e.Status == QueueStatus.Completed),
        Queue = queue
      };
    }
  }

  private long GetHighestSequenceSeen()
  {
    lock (this.stateLock)
    {
      return this.highestSequenceSeen;
    }
  }

  private void UpdateHighestSequenceSeen(long candidate)
  {
    lock (this.stateLock)
    {
      this.highestSequenceSeen = Math.Max(this.highestSequenceSeen, candidate);
    }
  }

  private void StartPolling()
  {
    if (this.disposed || this.pollTimer is not null)
    {
      return;
    }

    this.SetConnectionState(QueueConnectionState.Polling);
    this.pollTimer = new Timer(_ => _ = this.PollOnceAsync(), null, PollInterval, PollInterval);
  }

  private void StopPolling()
  {
    this.pollTimer?.Dispose();
    this.pollTimer = null;
  }

  private async Task PollOnceAsync()
  {
    try
    {
      await this.FetchAndApplyStateAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
      this.logger.LogDebug(ex, "Poll tick failed; will retry on the next interval.");
    }
  }

  private void SetConnectionState(QueueConnectionState state)
  {
    if (this.ConnectionState == state)
    {
      return;
    }

    this.ConnectionState = state;
    this.Changed?.Invoke();
  }

  public async ValueTask DisposeAsync()
  {
    this.disposed = true;
    this.StopPolling();
    this.startGate.Dispose();

    if (this.connection is not null)
    {
      await this.connection.DisposeAsync();
    }
  }
}
