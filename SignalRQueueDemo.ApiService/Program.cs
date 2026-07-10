using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalRQueueDemo.ApiService.Endpoints;
using SignalRQueueDemo.ApiService.Persistence;
using SignalRQueueDemo.ApiService.Persistence.Sqlite;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations (OpenTelemetry, health checks, service discovery).
builder.AddServiceDefaults();

builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// SQLite is the default (and only, until issue #4) IQueueRepository backend. The connection string lives in
// config (appsettings.json), not because it's a secret, but so switching the .db path/name never needs a
// code change. "Data Source=App_Data/..." is relative to the process working directory, which `aspire run`
// and `dotnet run` both set to the project directory — see .gitignore for why the App_Data folder itself
// isn't committed.
string connectionString = builder.Configuration.GetConnectionString("QueueDb")
    ?? throw new InvalidOperationException("Missing required connection string 'QueueDb'.");

// SQLite won't create a missing subdirectory for its own file, so App_Data needs to exist before the first
// connection opens. Parsed via SqliteConnectionStringBuilder rather than string-splitting "Data Source="
// so this keeps working if the connection string ever grows extra options (e.g. Cache=Shared).
string? dataDirectory = Path.GetDirectoryName(new SqliteConnectionStringBuilder(connectionString).DataSource);
if (!string.IsNullOrEmpty(dataDirectory))
{
    Directory.CreateDirectory(dataDirectory);
}

// The interceptor sets busy_timeout + WAL on every connection so concurrent writes (a kiosk checking in while
// staff calls the next entry) wait for each other instead of failing with "database is locked". It's stateless,
// so a single shared instance is safe across all scoped contexts.
builder.Services.AddDbContext<QueueDbContext>(options =>
    options.UseSqlite(connectionString).AddInterceptors(new QueueConnectionInterceptor()));
builder.Services.AddScoped<IQueueRepository, SqliteQueueRepository>();

WebApplication app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// EnsureCreated, not migrations: this is a POC scaffold with no schema history to preserve, so a single
// call that stamps the current model is simpler than a migrations project + design-time factory. Tradeoff
// documented on QueueDbContext: it won't pick up later model changes against an already-created .db file.
using (IServiceScope scope = app.Services.CreateScope())
{
    QueueDbContext dbContext = scope.ServiceProvider.GetRequiredService<QueueDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
    await QueueSeedData.SeedIfEmptyAsync(dbContext);
}

app.MapQueueEndpoints();

app.MapDefaultEndpoints();

app.Run();
