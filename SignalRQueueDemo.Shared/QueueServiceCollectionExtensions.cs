using Azure.Data.Tables;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SignalRQueueDemo.Shared.Documents;
using SignalRQueueDemo.Shared.Persistence;
using SignalRQueueDemo.Shared.Persistence.Blob;
using SignalRQueueDemo.Shared.Persistence.Sqlite;
using SignalRQueueDemo.Shared.Persistence.TableStorage;

namespace SignalRQueueDemo.Shared;

/// <summary>
/// Registers every queue/document persistence dependency — <see cref="IQueueRepository"/>,
/// <see cref="IDocumentRepository"/>, <see cref="DocumentBlobStore"/>, <see cref="DocumentUploadService"/> — in
/// one place both <c>SignalRQueueDemo.ApiService</c> and <c>SignalRQueueDemo.Web</c> call. Before this shared
/// project existed the <c>Persistence:Provider</c> switch lived inline in ApiService's own <c>Program.cs</c>;
/// pulling it out here means Blazor's <c>Program.cs</c> gets the identical registration — same config keys, same
/// defaults, same comments — by calling one method instead of a second hand-copied switch that could drift.
/// </summary>
public static class QueueServiceCollectionExtensions
{
  /// <summary>
  /// Wires up storage per <c>Persistence:Provider</c> config (<c>Sqlite</c> default, or <c>TableStorage</c>
  /// against the Azurite emulator), plus Blob Storage (always Azurite — not part of the provider switch, see
  /// below) and <see cref="DocumentUploadService"/> on top of whichever repositories were just registered.
  /// </summary>
  public static IHostApplicationBuilder AddQueueService(this IHostApplicationBuilder builder)
  {
    // DECISION: the IQueueRepository backend is chosen by config, not compiled in. "Sqlite" (default) needs no
    // extra infrastructure; "TableStorage" talks to the Azurite emulator that SignalRQueueDemo.AppHost always
    // starts, so flipping this one setting is the entire migration with zero other code changes, demonstrating
    // the storage swap ADR-0001 flagged as worth evaluating for future low-complexity projects.
    string persistenceProvider = builder.Configuration["Persistence:Provider"] ?? "Sqlite";

    switch (persistenceProvider)
    {
      case "Sqlite":
        // The connection string lives in config, not because it's a secret, but so switching the .db path/name
        // never needs a code change. Under `aspire run`, SignalRQueueDemo.AppHost injects an ABSOLUTE path here
        // (see AppHost.cs) so ApiService and Web — two separate processes with two separate working directories
        // — open the exact same physical file; the plain "Data Source=App_Data/queue.db" in each project's own
        // appsettings.json is only a same-process fallback for running one of them outside Aspire.
        string connectionString = builder.Configuration.GetConnectionString("QueueDb")
            ?? throw new InvalidOperationException("Missing required connection string 'QueueDb'.");

        // SQLite won't create a missing subdirectory for its own file, so the target directory needs to exist
        // before the first connection opens. Parsed via SqliteConnectionStringBuilder rather than
        // string-splitting "Data Source=" so this keeps working if the connection string ever grows extra
        // options (e.g. Cache=Shared).
        string? dataDirectory = Path.GetDirectoryName(new SqliteConnectionStringBuilder(connectionString).DataSource);
        if (!string.IsNullOrEmpty(dataDirectory))
        {
          Directory.CreateDirectory(dataDirectory);
        }

        // The interceptor sets busy_timeout + WAL on every connection so concurrent writes (a kiosk checking in
        // while staff calls the next entry — or, now, a Blazor page writing directly — wait for each other
        // instead of failing with "database is locked"). It's stateless, so a single shared instance is safe
        // across all contexts the factory below creates.
        //
        // AddDbContextFactory, not AddDbContext: a directly-injected DbContext is scoped to whatever DI scope
        // resolved this repository — one HTTP request in ApiService (fine), but one Blazor *circuit* (a whole
        // browser-tab session) in Web, called concurrently from multiple threads (component handlers,
        // QueueRealtimeService's poll timer, its reconnect catch-up). A single long-lived context there is
        // neither thread-safe nor fresh — see SqliteQueueRepository's type remarks. The factory lets each
        // repository method create its own short-lived context instead.
        builder.Services.AddDbContextFactory<QueueDbContext>(options =>
            options.UseSqlite(connectionString).AddInterceptors(new QueueConnectionInterceptor()));
        builder.Services.AddScoped<IQueueRepository, SqliteQueueRepository>();

        // Document metadata rides the same DbContext/connection as the queue entries it describes — "alongside
        // the queue entry" in the literal single-database sense, not just conceptually. See IDocumentRepository's
        // remarks for why this is a separate interface from IQueueRepository despite sharing a backend per
        // provider.
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

    // Sit on top of whichever repositories were just registered above — see DocumentUploadService's,
    // QueueCompletionService's, and QueueCancellationService's remarks for why upload validation and
    // complete-/cancel-time document cleanup live here instead of duplicated between ApiService's endpoints and
    // Blazor's pages.
    builder.Services.AddScoped<DocumentUploadService>();
    builder.Services.AddScoped<QueueCompletionService>();
    builder.Services.AddScoped<QueueCancellationService>();

    return builder;
  }

  /// <summary>
  /// Schema/table creation + seeding for whichever provider is active — the startup-time counterpart of
  /// <see cref="AddQueueService"/>. Called at startup by <b>both</b> hosts now that <c>SignalRQueueDemo.Web</c> is
  /// fully self-encapsulated (its own hub, no <c>WaitFor(apiService)</c> to rely on for ordering). That's safe:
  /// for SQLite the two hosts point at separate <c>.db</c> files (see AppHost.cs), so there's no shared state to
  /// race; for Table Storage they share the Azurite store, but every step here is idempotent and
  /// concurrency-safe (CreateIfNotExists tables, optimistic-insert sequence, SeedIfEmpty entries — see
  /// <see cref="QueueSeedData"/> / <see cref="TableStorageQueueSeedData"/>), so two hosts initializing at once
  /// converge rather than double-seed.
  /// </summary>
  public static async Task InitializeQueueStorageAsync(this IServiceProvider services, CancellationToken ct = default)
  {
    string? persistenceProvider = services.GetRequiredService<IConfiguration>()["Persistence:Provider"] ?? "Sqlite";

    if (persistenceProvider == "TableStorage")
    {
      // Azurite starts with zero tables — unlike SQLite's single .db file, there's no schema to stamp, but the
      // three named tables (entries, change events, sequence counter) still need to exist before the first
      // request. See TableStorageQueueSeedData for why seeding also primes the sequence counter.
      TableServiceClient tableServiceClient = services.GetRequiredService<TableServiceClient>();
      await TableStorageQueueSeedData.EnsureTablesAndSeedAsync(tableServiceClient, ct);

      // Documents table has no seed data (no synthetic pre-uploaded documents) — it just needs to exist before
      // the first upload, same "Azurite starts with zero tables" reasoning as the three tables
      // TableStorageQueueSeedData creates above.
      TableClient documentsTable = tableServiceClient.GetTableClient(TableStorageDocumentRepository.DocumentsTableName);
      await documentsTable.CreateIfNotExistsAsync(cancellationToken: ct);
    }
    else
    {
      // EnsureCreated, not migrations: this is a POC scaffold with no schema history to preserve, so a single
      // call that stamps the current model is simpler than a migrations project + design-time factory. Tradeoff
      // documented on QueueDbContext: it won't pick up later model changes against an already-created .db file.
      IDbContextFactory<QueueDbContext> dbContextFactory = services.GetRequiredService<IDbContextFactory<QueueDbContext>>();
      await using QueueDbContext dbContext = await dbContextFactory.CreateDbContextAsync(ct);
      await dbContext.Database.EnsureCreatedAsync(ct);
      await QueueSeedData.SeedIfEmptyAsync(dbContext, ct);
    }
  }
}
