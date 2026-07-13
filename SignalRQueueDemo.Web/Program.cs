using SignalRQueueDemo.Shared;
using SignalRQueueDemo.Shared.Realtime;
using SignalRQueueDemo.Web.Components;
using SignalRQueueDemo.Web.Endpoints;
using SignalRQueueDemo.Web.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations (OpenTelemetry, health checks, service discovery).
builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOutputCache();

// Fail fast at startup if StaffAuth:Key is missing — same reasoning as ApiService's own copy of this check.
// CheckInToken:SigningKey is deliberately NOT required here: Blazor's check-in page skips that scheme entirely
// (see docs/decisions.md) because a Blazor Server interactive circuit is itself a stronger anti-abuse boundary
// than the stateless-SPA problem that token exists to mitigate.
if (string.IsNullOrEmpty(builder.Configuration["StaffAuth:Key"]))
{
    throw new InvalidOperationException("Missing required configuration 'StaffAuth:Key'.");
}

// Registers IQueueRepository/IDocumentRepository/DocumentBlobStore/DocumentUploadService — the exact same
// registration ApiService's own Program.cs calls, so both processes talk to the exact same storage (see
// AppHost.cs for how the two are pointed at the same physical SQLite file / Azurite emulator).
builder.AddQueueService();

// Self-hosted SignalR hub — Blazor's OWN, not ApiService's. This is what makes the Blazor stack fully
// self-encapsulated: it no longer connects to ApiService's hub, so it has zero runtime dependency on the API
// process. The hub class + broadcaster live in SignalRQueueDemo.Shared.Realtime and are hosted identically here
// and in ApiService; each host's hub only ever reaches its own clients. The broadcaster is what QueueHub uses to
// fan a NotifyMutation out to every connected Blazor circuit.
builder.Services.AddSignalR();
builder.Services.AddSingleton<QueueBroadcaster>();

// Blazor's own SignalR client to the hub above — both to receive QueueUpdated pushes and to call NotifyMutation
// after a direct repository write. Scoped: one HubConnection per Blazor circuit (one per browser tab), the direct
// analogue of Angular's tab-scoped QueueHubService — see QueueRealtimeService's remarks.
builder.Services.AddScoped<QueueRealtimeService>();

// In-memory-only staff sign-in state, scoped to the circuit — see StaffSessionService's remarks.
builder.Services.AddScoped<StaffSessionService>();

// Stateless HMAC token issuer/validator for the local document-streaming endpoint below — see its remarks.
builder.Services.AddSingleton<DocumentAccessTokenService>();

WebApplication app = builder.Build();

// Schema/table creation + seeding — Blazor now does this itself, because it no longer waits for (or depends on)
// ApiService to have done it first. For the SQLite provider this stamps Blazor's OWN .db file (a separate file
// from ApiService's — see AppHost.cs), so there's no cross-process seeding race. For the Table Storage provider
// the store is deliberately shared with ApiService, and the seed is idempotent/concurrency-safe (CreateIfNotExists
// tables, optimistic-insert sequence, SeedIfEmpty entries — see QueueServiceCollectionExtensions), so both hosts
// running it at startup converges rather than double-seeds.
using (IServiceScope scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.InitializeQueueStorageAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.UseOutputCache();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Blazor's own QueueHub. No RequireCors: the only client is this app's own server-side HubConnection (see
// QueueRealtimeService), a same-process loopback connection — never a cross-origin browser request — so there's
// no CORS negotiate to allow. This is the endpoint QueueRealtimeService points at via NavigationManager.BaseUri.
app.MapHub<QueueHub>("/hubs/queue");

app.MapDocumentStreamEndpoints();

app.MapDefaultEndpoints();

app.Run();
