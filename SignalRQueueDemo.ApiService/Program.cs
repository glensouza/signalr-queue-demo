using SignalRQueueDemo.ApiService.Endpoints;
using SignalRQueueDemo.ApiService.Hubs;
using SignalRQueueDemo.Shared;
using SignalRQueueDemo.Shared.Realtime;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations (OpenTelemetry, health checks, service discovery).
builder.AddServiceDefaults();

builder.Services.AddProblemDetails();

// Fail fast at startup if the auth secrets are missing, rather than letting the omission surface as an opaque
// 500 on the first staff request (StaffAuthFilter) or first token issuance (CheckInTokenService). appsettings.json
// ships obvious placeholders for both; a real deployment overrides them via user-secrets/environment.
string[] requiredSecretKeys = ["StaffAuth:Key", "CheckInToken:SigningKey"];
foreach (string requiredKey in requiredSecretKeys)
{
    if (string.IsNullOrEmpty(builder.Configuration[requiredKey]))
    {
        throw new InvalidOperationException($"Missing required configuration '{requiredKey}'.");
    }
}

// DECISION: allowed origins are config-driven (Cors:AllowedOrigins) for any non-loopback deployment, but a
// POC-only escape hatch (Cors:AllowLoopbackOrigins, default true) additionally accepts ANY loopback-family
// origin. This exists because of how Aspire actually serves the three containerized Angular apps: it assigns each
// one a host and port at `aspire run` time, and serves them under its own `*.dev.localhost` hostname scheme
// (e.g. http://public-checkin-signalrqueuedemo.dev.localhost:53592) — NOT plain http://localhost:{port}. A fixed
// allowlist can't name those origins ahead of time, and an earlier attempt to inject them from the AppHost pinned
// the host to "localhost", so it matched the port but not the *.dev.localhost host the browser really loads from —
// CORS then rejected every real-browser request even though same-origin curl checks (which sent an Origin of
// http://localhost:{port}) passed. Every host in play here — localhost, *.localhost, 127.0.0.1, ::1 — resolves to
// loopback on the single isolated machine this court POC runs on, so trusting the loopback family is both correct
// for this environment and immune to Aspire's per-run port/host churn.
//
// This is NOT the trust boundary (StaffAuthFilter/CheckInTokenFilter are) and it is applied to every
// browser-reachable surface — the REST endpoints and the SignalR hub below. It MUST be tightened to an explicit
// allowlist (set Cors:AllowLoopbackOrigins=false and populate Cors:AllowedOrigins) for any deployment reachable
// beyond localhost. See docs/decisions.md.
string[] knownFrontendOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
bool allowLoopbackOrigins = builder.Configuration.GetValue("Cors:AllowLoopbackOrigins", true);
builder.Services.AddCors(options => options.AddPolicy(
  CorsPolicies.KnownFrontends,
  policy => policy
    .SetIsOriginAllowed(origin => IsAllowedFrontendOrigin(origin, knownFrontendOrigins, allowLoopbackOrigins))
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Origin predicate for the KnownFrontends policy. Named (not an inline lambda) so the two-part rule — explicit
// config allowlist OR loopback-family host — reads clearly at the call site. A configured origin matches exactly
// (case-insensitively); the loopback branch accepts localhost, the raw loopback IPs, and any `*.localhost`
// subdomain, which is what covers Aspire's `*.dev.localhost` container hostnames.
static bool IsAllowedFrontendOrigin(string origin, string[] configuredOrigins, bool allowLoopback)
{
    if (Array.Exists(configuredOrigins, o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase)))
    {
        return true;
    }

    if (!allowLoopback || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
    {
        return false;
    }

    // Uri.IsLoopback already covers localhost / 127.0.0.1 / ::1; the suffix check adds Aspire's *.dev.localhost
    // (and any other *.localhost), which Uri.IsLoopback does not treat as loopback on its own.
    return uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}

// Stateless HMAC token issuer/validator for the public check-in path's anti-forgery-style hardening — see
// CheckInTokenService's remarks for why this exists instead of ASP.NET Core's cookie-based antiforgery system.
builder.Services.AddSingleton<CheckInTokenService>();

// Maps the framework's "request body too large" failures (multipart body-length limit on the document-upload
// endpoint, or a server body-size limit) to a clean 413 ProblemDetails instead of an opaque 500 — see
// UploadLimitExceptionHandler. Runs ahead of the default handler registered by UseExceptionHandler below.
builder.Services.AddExceptionHandler<SignalRQueueDemo.ApiService.Endpoints.UploadLimitExceptionHandler>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Registers IQueueRepository/IDocumentRepository/DocumentBlobStore/DocumentUploadService per Persistence:Provider
// config — see SignalRQueueDemo.Shared.QueueServiceCollectionExtensions.AddQueueService for the full switch and
// its comments. Pulled into SignalRQueueDemo.Shared so SignalRQueueDemo.Web (which writes to the queue directly,
// not via this API — see docs/decisions.md's "Blazor is self-encapsulated") registers the identical storage stack
// from one shared call instead of a second hand-copied switch.
builder.AddQueueService();

// Self-hosted SignalR — the default topology per ADR-0001 Option C. No extra PackageReference needed:
// Microsoft.NET.Sdk.Web already references the ASP.NET Core shared framework, which includes the SignalR
// server. See SignalRQueueDemo.ApiService/Hubs/QueueHub.cs for the broadcast + reconnect catch-up protocol.
// This runs unconditionally — regardless of UseAzureSignalR below — because it's the only topology this POC
// actually wires the kiosk/staff/display frontends to; see AzureSignalRDefaultModeStub for why the ADR-0001
// production scale-up path can't run against the local emulator either.
builder.Services.AddSignalR();

// The one place queue mutations broadcast QueueUpdated. Singleton because it holds only the singleton
// IHubContext + a logger; see QueueBroadcaster for why broadcasting goes through it (single choke point +
// a failed push can't fail an already-committed write).
builder.Services.AddSingleton<QueueBroadcaster>();

// DECISION: UseAzureSignalR (default false) is the ADR-0001 Option C escape hatch. It does NOT swap
// out the self-hosted SignalR wired above — see AzureSignalRDefaultModeStub for why the real production path
// (AddAzureSignalR(connectionString), default/server mode) can't run against the local emulator, which only
// supports serverless mode. Instead, turning the flag on registers AzureSignalRServerlessDemoService, an
// illustrative side-channel (a BackgroundService, so it runs off the startup path and never delays readiness)
// that proves the emulator's serverless round-trip on its own — see that class's remarks. AppHost.cs reads the
// same-named flag independently to decide whether the emulator resource (and the "signalr" connection string
// this service depends on) exists at all; keep both defaults in sync.
bool useAzureSignalR = builder.Configuration.GetValue<bool>("UseAzureSignalR");
if (useAzureSignalR)
{
    builder.Services.AddHostedService<AzureSignalRServerlessDemoService>();
}

WebApplication app = builder.Build();

app.UseExceptionHandler();

// Enables the per-endpoint/-hub .RequireCors(...) policies applied in QueueEndpoints, DocumentEndpoints, and on
// the hub below. UseCors() itself defines no default policy — only the surfaces that opt in via RequireCors are
// reachable cross-origin, and only from the configured origins.
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Schema/table creation + seeding — see QueueServiceCollectionExtensions.InitializeQueueStorageAsync for the
// provider-branched detail. Runs only here, in ApiService: SignalRQueueDemo.Web's AppHost-enforced
// WaitFor(apiService) + health-check ordering guarantees the schema/tables already exist by the time Blazor's
// first repository call can happen, so it doesn't repeat this at its own startup.
using (IServiceScope scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.InitializeQueueStorageAsync();
}

app.MapQueueEndpoints();
app.MapDocumentEndpoints();

// The hub gets the same KnownFrontends CORS policy as the REST endpoints: every frontend (all three Angular apps
// and the Blazor app) depends on it for live updates, and a cross-origin SignalR client's negotiate request is a
// CORS request — without this the browser would block the connection before it starts. No auth policy on the hub
// itself: its one client-callable method (QueueHub.NotifyMutation, used by Blazor after a direct repository
// write) never trusts caller-supplied entry/summary data, only re-reads it from the repository — see that
// method's remarks — so it only ever pushes already-public queue state, same as before it existed. There's
// nothing here that the staff/token gates on the REST side would be protecting.
app.MapHub<QueueHub>("/hubs/queue")
  .RequireCors(CorsPolicies.KnownFrontends);

app.MapDefaultEndpoints();

app.Run();
