using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Azure.SignalR.Management;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Hubs;

/// <summary>
/// PATH 2 of the <c>UseAzureSignalR</c> feature flag: the round-trip the local Azure SignalR
/// Emulator can actually demonstrate — serverless mode via the
/// <see href="https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-quickstart-azure-functions-csharp">
/// Microsoft.Azure.SignalR.Management</see> SDK. Only registered when <c>UseAzureSignalR=true</c> (see
/// Program.cs); does nothing otherwise.
///
/// <para>
/// <b>Why a separate client connection, not the app's real traffic.</b> Serverless-mode clients connect
/// directly to the Azure SignalR service (here, the emulator) via a negotiated URL/token — they never touch an
/// ASP.NET Core hub on this server. That is a fundamentally different topology from <see cref="QueueHub"/>'s
/// self-hosted default mode, so this demo proves the emulator round-trip in isolation rather than rerouting
/// the app's actual <see cref="QueueBroadcaster"/> traffic through it. The kiosk/staff/display frontends keep
/// using the self-hosted hub regardless of this flag — see <see cref="AzureSignalRDefaultModeStub"/> for why
/// the real ADR-0001 production path can't run against the emulator either.
/// </para>
///
/// <para>
/// <b>Why a <see cref="BackgroundService"/>, not an inline <c>IHostedService.StartAsync</c>.</b> The round-trip
/// is a network operation that can take up to <see cref="ReceiveTimeout"/> to time out; doing it in
/// <c>StartAsync</c> would block the host's "started" signal (and therefore anything downstream that
/// <c>WaitFor</c>s this API in the AppHost) for that whole window. <see cref="BackgroundService"/> runs
/// <see cref="ExecuteAsync"/> off the startup path — the API is ready immediately and the demo reports its
/// result whenever it finishes. It also needs nothing from Kestrel or <see cref="QueueHub"/> (it talks only to
/// the emulator), so there's no reason for it to gate startup.
/// </para>
///
/// <para>
/// <b>What "success" means here.</b> <see cref="ExecuteAsync"/> connects one <see cref="HubConnection"/>
/// straight to the emulator's negotiated endpoint, pushes a single synthetic <see cref="QueueUpdated"/> through
/// <see cref="ServiceHubContext"/>, and logs whether that client received it — proof the emulator's serverless
/// message path actually works, not a simulation. The broadcast uses obviously-fake data (CLAUDE.md: no real
/// court data) and sequence number 0, which no real <see cref="QueueUpdated"/> ever uses, so it can never be
/// mistaken for a real queue change.
/// </para>
/// </summary>
public sealed class AzureSignalRServerlessDemoService(
  IConfiguration configuration,
  ILoggerFactory loggerFactory,
  ILogger<AzureSignalRServerlessDemoService> logger) : BackgroundService
{
  /// <summary>
  /// Arbitrary hub name for this demo's own <see cref="ServiceHubContext"/> — deliberately distinct from
  /// <c>/hubs/queue</c> (the self-hosted hub's route) so this illustrative channel can never be confused with,
  /// or collide with, the app's real SignalR traffic. No hyphens: Azure SignalR's hub-name validation rejects
  /// them (letters/digits/<c>_`,.[]</c> only) — verified against the emulator, which returns an opaque 400 from
  /// the REST send API (not from negotiate/connect, which accept a hyphenated name — the failure only surfaces
  /// once a broadcast is actually sent).
  /// </summary>
  private const string DemoHubName = "queueServerlessDemo";

  private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

  private readonly IConfiguration configuration = configuration;
  private readonly ILoggerFactory loggerFactory = loggerFactory;
  private readonly ILogger<AzureSignalRServerlessDemoService> logger = logger;

  /// <summary>
  /// Runs the round-trip once, shortly after startup, and logs the result. Never throws past this method: a
  /// failed demo (the emulator container isn't ready, a transient hiccup) is a diagnostic, not a reason to stop
  /// the host — the app's real functionality (self-hosted SignalR, REST endpoints) doesn't depend on this flag
  /// at all. Returning early is fine for a <see cref="BackgroundService"/>: this is a one-shot demonstration,
  /// not a long-running loop.
  /// </summary>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    try
    {
      // "signalr" matches the resource name AddAzureSignalR("signalr", ...) is given in AppHost.cs — Aspire
      // service discovery injects the emulator's connection string under that name only when the AppHost's own
      // UseAzureSignalR copy of the flag is also on (see AppHost.cs remarks on why the flag is read twice).
      string connectionString = this.configuration.GetConnectionString("signalr")
        ?? throw new InvalidOperationException(
          "Missing connection string 'signalr'. UseAzureSignalR is true in ApiService but AppHost didn't " +
          "start the emulator resource — check UseAzureSignalR is also true in AppHost/appsettings.json.");

      // ServiceManager is IDisposable (not async) and ServiceHubContext is IAsyncDisposable — both are disposed
      // here in the same scope that creates them, so the demo leaves no connection to the emulator open after it
      // finishes (it runs once and returns).
      using ServiceManager serviceManager = new ServiceManagerBuilder()
        .WithOptions(options => options.ConnectionString = connectionString)
        .WithLoggerFactory(this.loggerFactory)
        .BuildServiceManager();

      // The concrete ServiceHubContext (not just the IServiceHubContext interface CreateHubContextAsync is
      // declared to return) is what exposes NegotiateAsync — this is the documented pattern from Microsoft's
      // own serverless quickstart, not a reflection workaround.
      await using ServiceHubContext hubContext = (ServiceHubContext)await serviceManager.CreateHubContextAsync(
        DemoHubName, cancellationToken: stoppingToken);

      NegotiationResponse negotiation = await hubContext.NegotiateAsync(cancellationToken: stoppingToken);

      await using HubConnection clientConnection = new HubConnectionBuilder()
        .WithUrl(negotiation.Url!, options => options.AccessTokenProvider = () => Task.FromResult(negotiation.AccessToken))
        .Build();

      TaskCompletionSource<QueueUpdated> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
      clientConnection.On<QueueUpdated>("QueueUpdated", update => received.TrySetResult(update));

      await clientConnection.StartAsync(stoppingToken);

      // Synthetic demo payload only — SequenceNumber 0 and the "DEMO-" ticket prefix mark it as never a real
      // queue change (see type remarks); CLAUDE.md forbids real court data even in throwaway demo messages.
      QueueUpdated demoUpdate = new()
      {
        SequenceNumber = 0,
        ChangedEntry = new QueueEntry
        {
          Id = "demo",
          DisplayName = "Azure SignalR Emulator Demo",
          TicketNumber = "DEMO-000",
          CheckedInAt = DateTimeOffset.UtcNow,
          Status = QueueStatus.Waiting
        },
        Summary = new QueueSnapshot
        {
          TotalWaiting = 0,
          TotalServing = 0,
          TotalCompleted = 0,
          Queue = []
        }
      };

      await hubContext.Clients.All.SendAsync("QueueUpdated", demoUpdate, stoppingToken);

      // Linked CTS so the losing branch of the WhenAny is cancelled promptly: without it, the Task.Delay timer
      // (and its registration on stoppingToken) would linger for the full ReceiveTimeout after the message
      // arrives on the success path.
      using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
      Task completed = await Task.WhenAny(received.Task, Task.Delay(ReceiveTimeout, timeoutCts.Token));
      timeoutCts.Cancel();

      if (completed == received.Task)
      {
        this.logger.LogInformation(
          "UseAzureSignalR serverless demo succeeded: a client connected directly to the Azure SignalR " +
          "Emulator received a QueueUpdated broadcast pushed through ServiceHubContext. The emulator round-trip works.");
      }
      else
      {
        this.logger.LogWarning(
          "UseAzureSignalR serverless demo: the broadcast was sent but no client received it within {Timeout}. " +
          "Check the signalr-emulator container is healthy (aspire dashboard) before assuming the round-trip is broken.",
          ReceiveTimeout);
      }

      // No explicit StopAsync: the `await using` on clientConnection disposes (and thereby stops) it here.
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      // The host is shutting down while the demo was still mid-round-trip — normal cancellation, not a failure,
      // so it's deliberately not logged as an error.
    }
    catch (Exception ex)
    {
      // Diagnostic only — see type remarks. A broken emulator connection must never take down the API that the
      // kiosk/staff/display frontends depend on for their self-hosted SignalR traffic. (A BackgroundService that
      // let this escape would, by default, stop the host — hence the catch.)
      this.logger.LogError(ex, "UseAzureSignalR serverless demo failed to complete.");
    }
  }
}
