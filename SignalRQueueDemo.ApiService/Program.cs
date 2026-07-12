using Azure.Data.Tables;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalRQueueDemo.ApiService.Endpoints;
using SignalRQueueDemo.ApiService.Hubs;
using SignalRQueueDemo.ApiService.Persistence;
using SignalRQueueDemo.ApiService.Persistence.Blob;
using SignalRQueueDemo.ApiService.Persistence.Sqlite;
using SignalRQueueDemo.ApiService.Persistence.TableStorage;

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

// DECISION: the IQueueRepository backend is chosen by config, not compiled in. "Sqlite" (default) needs no
// extra infrastructure; "TableStorage" talks to the Azurite emulator that SignalRQueueDemo.AppHost always
// starts (see AppHost.cs), so flipping this one setting is the entire migration with zero other code changes,
// demonstrating the storage swap ADR-0001 flagged as worth evaluating for future low-complexity projects.
string persistenceProvider = builder.Configuration["Persistence:Provider"] ?? "Sqlite";

switch (persistenceProvider)
{
    case "Sqlite":
        // The connection string lives in config (appsettings.json), not because it's a secret, but so
        // switching the .db path/name never needs a code change. "Data Source=App_Data/..." is relative to
        // the process working directory, which `aspire run` and `dotnet run` both set to the project
        // directory — see .gitignore for why the App_Data folder itself isn't committed.
        string connectionString = builder.Configuration.GetConnectionString("QueueDb")
            ?? throw new InvalidOperationException("Missing required connection string 'QueueDb'.");

        // SQLite won't create a missing subdirectory for its own file, so App_Data needs to exist before the
        // first connection opens. Parsed via SqliteConnectionStringBuilder rather than string-splitting
        // "Data Source=" so this keeps working if the connection string ever grows extra options (e.g.
        // Cache=Shared).
        string? dataDirectory = Path.GetDirectoryName(new SqliteConnectionStringBuilder(connectionString).DataSource);
        if (!string.IsNullOrEmpty(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
        }

        // The interceptor sets busy_timeout + WAL on every connection so concurrent writes (a kiosk checking
        // in while staff calls the next entry) wait for each other instead of failing with "database is
        // locked". It's stateless, so a single shared instance is safe across all scoped contexts.
        builder.Services.AddDbContext<QueueDbContext>(options =>
            options.UseSqlite(connectionString).AddInterceptors(new QueueConnectionInterceptor()));
        builder.Services.AddScoped<IQueueRepository, SqliteQueueRepository>();

        // Document metadata rides the same DbContext/connection as the queue entries it describes — "alongside
        // the queue entry" in the literal single-database sense, not just conceptually. See IDocumentRepository's
        // remarks for why this is a separate interface from IQueueRepository despite sharing a backend per provider.
        builder.Services.AddScoped<IDocumentRepository, SqliteDocumentRepository>();
        break;

    case "TableStorage":
        // "tables" matches the resource name AddTables("tables") is given in AppHost.cs — Aspire service
        // discovery injects the emulator's connection string under that name, so no connection string ever
        // appears in source or config here (court constraint: no secrets in source control).
        builder.AddAzureTableServiceClient(connectionName: "tables");

        // Singleton (unlike SqliteQueueRepository's Scoped registration): TableServiceClient is a thread-safe,
        // connection-pooling SDK client meant to be shared, and this repository holds no other per-request
        // state, so there's no reason to pay DI's per-scope allocation cost SqliteQueueRepository pays for its
        // scoped DbContext.
        builder.Services.AddSingleton<IQueueRepository, TableStorageQueueRepository>();
        builder.Services.AddSingleton<IDocumentRepository, TableStorageDocumentRepository>();
        break;

    default:
        throw new InvalidOperationException(
            $"Unknown Persistence:Provider '{persistenceProvider}'. Expected 'Sqlite' or 'TableStorage'.");
}

// Blob Storage (the document-content backend) is NOT part of the Persistence:Provider switch above — unlike
// IQueueRepository/IDocumentRepository, it isn't swappable by config; Azurite's Blob emulator is the only
// backend this POC targets, matching its scope as a stand-in for the court's Document Management System API.
// "blobs" matches AddBlobs("blobs") in AppHost.cs; Aspire service discovery injects the emulator's connection
// string under that name, so (same court constraint as "tables" above) no connection string ever appears in
// source or config here.
builder.AddAzureBlobServiceClient(connectionName: "blobs");
builder.Services.AddSingleton<DocumentBlobStore>();

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

// Schema/table creation + seeding, branched the same way DI registration was above. Both branches leave the
// store ready for the same manual `.http` test script in README.md regardless of which provider is active.
using (IServiceScope scope = app.Services.CreateScope())
{
    if (persistenceProvider == "TableStorage")
    {
        // Azurite starts with zero tables — unlike SQLite's single .db file, there's no schema to stamp, but
        // the three named tables (entries, change events, sequence counter) still need to exist before the
        // first request. See TableStorageQueueSeedData for why seeding also primes the sequence counter.
        TableServiceClient tableServiceClient = scope.ServiceProvider.GetRequiredService<TableServiceClient>();
        await TableStorageQueueSeedData.EnsureTablesAndSeedAsync(tableServiceClient);

        // Documents table has no seed data (no synthetic pre-uploaded documents) — it just needs to exist
        // before the first upload, same "Azurite starts with zero tables" reasoning as the three tables
        // TableStorageQueueSeedData creates above.
        TableClient documentsTable = tableServiceClient.GetTableClient(TableStorageDocumentRepository.DocumentsTableName);
        await documentsTable.CreateIfNotExistsAsync();
    }
    else
    {
        // EnsureCreated, not migrations: this is a POC scaffold with no schema history to preserve, so a
        // single call that stamps the current model is simpler than a migrations project + design-time
        // factory. Tradeoff documented on QueueDbContext: it won't pick up later model changes against an
        // already-created .db file.
        QueueDbContext dbContext = scope.ServiceProvider.GetRequiredService<QueueDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
        await QueueSeedData.SeedIfEmptyAsync(dbContext);
    }
}

app.MapQueueEndpoints();
app.MapDocumentEndpoints();

// The hub gets the same KnownFrontends CORS policy as the REST endpoints: every frontend (all three Angular apps
// and the Blazor app) depends on it for live updates, and a cross-origin SignalR client's negotiate request is a
// CORS request — without this the browser would block the connection before it starts. No auth policy on the hub
// itself: it exposes no client-callable mutation method (all writes go through the REST endpoints, which are
// gated), and it only ever pushes already-public queue state, so there's nothing here that the staff/token gates
// on the REST side would be protecting.
app.MapHub<QueueHub>("/hubs/queue")
  .RequireCors(CorsPolicies.KnownFrontends);

app.MapDefaultEndpoints();

app.Run();
