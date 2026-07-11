using Azure.Data.Tables;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalRQueueDemo.ApiService.Endpoints;
using SignalRQueueDemo.ApiService.Hubs;
using SignalRQueueDemo.ApiService.Persistence;
using SignalRQueueDemo.ApiService.Persistence.Sqlite;
using SignalRQueueDemo.ApiService.Persistence.TableStorage;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations (OpenTelemetry, health checks, service discovery).
builder.AddServiceDefaults();

builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// DECISION: the IQueueRepository backend is chosen by config, not compiled in — this is the config switch
// issue #4 asks for. "Sqlite" (default) needs no extra infrastructure; "TableStorage" talks to the Azurite
// emulator that SignalRQueueDemo.AppHost always starts (see AppHost.cs), so flipping this one setting is the
// entire migration with zero other code changes, demonstrating the storage swap ADR-0001 flagged as worth
// evaluating for future low-complexity projects.
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
        break;

    default:
        throw new InvalidOperationException(
            $"Unknown Persistence:Provider '{persistenceProvider}'. Expected 'Sqlite' or 'TableStorage'.");
}

// Self-hosted SignalR — the default topology per ADR-0001 Option C. No extra PackageReference needed:
// Microsoft.NET.Sdk.Web already references the ASP.NET Core shared framework, which includes the SignalR
// server. See SignalRQueueDemo.ApiService/Hubs/QueueHub.cs for the broadcast + reconnect catch-up protocol.
// This is the default topology, not the only one: ADR-0001 documents a UseAzureSignalR feature-flag escape
// hatch to the Azure SignalR emulator for higher concurrency; that flag and its Aspire wiring land in issue #7.
builder.Services.AddSignalR();

// The one place queue mutations broadcast QueueUpdated. Singleton because it holds only the singleton
// IHubContext + a logger; see QueueBroadcaster for why broadcasting goes through it (single choke point +
// a failed push can't fail an already-committed write).
builder.Services.AddSingleton<QueueBroadcaster>();

WebApplication app = builder.Build();

app.UseExceptionHandler();

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

// No CORS or auth policy applied to this hub yet — every client today is same-project/localhost. The
// lightweight public-endpoint hardening (restricted CORS + API-key pattern) is issue #6's job; a browser
// client on a different origin (the future Angular containers) won't be able to connect until that lands.
app.MapHub<QueueHub>("/hubs/queue");

app.MapDefaultEndpoints();

app.Run();
