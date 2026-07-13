using SignalRQueueDemo.Shared;
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

// Blazor's own SignalR client — both to receive QueueUpdated pushes and to call NotifyMutation after a direct
// repository write. Scoped: one HubConnection per Blazor circuit (one per browser tab), the direct analogue of
// Angular's tab-scoped QueueHubService — see QueueRealtimeService's remarks.
builder.Services.AddScoped<QueueRealtimeService>();

// In-memory-only staff sign-in state, scoped to the circuit — see StaffSessionService's remarks.
builder.Services.AddScoped<StaffSessionService>();

// Stateless HMAC token issuer/validator for the local document-streaming endpoint below — see its remarks.
builder.Services.AddSingleton<DocumentAccessTokenService>();

WebApplication app = builder.Build();

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

app.MapDocumentStreamEndpoints();

app.MapDefaultEndpoints();

app.Run();
