using Microsoft.Azure.SignalR;

namespace SignalRQueueDemo.ApiService.Hubs;

/// <summary>
/// PATH 1 of the <c>UseAzureSignalR</c> feature flag: the real ADR-0001 production scale-up
/// path — <c>AddAzureSignalR(connectionString)</c> — as a stub only. Its single public member is deliberately
/// never called from <c>Program.cs</c>.
///
/// <para>
/// <b>Why this never runs, even with the flag on.</b> ADR-0001's default (server) mode requires an app server
/// to hold a live connection to a real Azure SignalR resource; there is no local target for that connection.
/// The <a href="https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-howto-emulator">Azure SignalR
/// Local Emulator</a> supports serverless mode only — Microsoft's own guidance is that for default mode,
/// self-hosted SignalR (exactly what this POC runs whenever the flag is off, or on, since this method is never
/// invoked) *is* the correct local stand-in. See <see cref="AzureSignalRServerlessDemoService"/> for the path
/// the emulator can actually demonstrate.
/// </para>
///
/// <para>
/// <b>Why it exists at all.</b> ADR-0001 promises the switch to Azure SignalR is "a single line of code" —
/// this method is that line, kept compiling and reviewable so the vendor team can see the exact production
/// change without it ever touching a real Azure resource from this reference implementation.
/// </para>
/// </summary>
public static class AzureSignalRDefaultModeStub
{
  /// <summary>
  /// The one-line production change ADR-0001 describes. Never called: see type remarks. A real deployment
  /// would call this instead of (not in addition to) <c>builder.Services.AddSignalR()</c> in Program.cs, with
  /// the connection string sourced from user-secrets or environment — never appsettings.json, per CLAUDE.md's
  /// "no secrets in source control" constraint.
  /// </summary>
  public static void Apply(IServiceCollection services, string connectionString) =>
    services.AddSignalR().AddAzureSignalR(connectionString);
}
