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

// DECISION: allowed origins are config-driven (Cors:AllowedOrigins), not hardcoded or read from Aspire service
// discovery. The Angular apps (public-checkin, queue-display, internal-queue) don't exist as AppHost project
// resources yet — they're later work items — so there's no service-discovery-injected origin to read today.
// appsettings.json seeds this with the Angular CLI dev-server's default port as a placeholder; the Angular work
// items update it to the real container origins once those resources exist. The one policy covers every known
// browser frontend (public and staff) and is applied to every browser-reachable surface — the REST endpoints
// and the SignalR hub below — because CORS isn't the trust boundary (StaffAuthFilter/CheckInTokenFilter are);
// it only keeps legitimate frontends from being refused by the browser before those checks run. See docs/decisions.md.
string[] knownFrontendOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy(
  CorsPolicies.KnownFrontends,
  policy => policy.WithOrigins(knownFrontendOrigins).AllowAnyHeader().AllowAnyMethod()));

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
// This is the default topology, not the only one: ADR-0001 documents a UseAzureSignalR feature-flag escape
// hatch to the Azure SignalR emulator for higher concurrency, added separately.
builder.Services.AddSignalR();

// The one place queue mutations broadcast QueueUpdated. Singleton because it holds only the singleton
// IHubContext + a logger; see QueueBroadcaster for why broadcasting goes through it (single choke point +
// a failed push can't fail an already-committed write).
builder.Services.AddSingleton<QueueBroadcaster>();

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
