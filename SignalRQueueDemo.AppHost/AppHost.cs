using Aspire.Hosting.Azure;
using Microsoft.Extensions.Configuration;

// Explicit types (not var) per the repo's C# style standard — the Aspire resource-builder generics are verbose
// but the style rule applies here the same as anywhere else.
IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Azurite-backed Table Storage resource for the second IQueueRepository implementation, plus Blob Storage for
// uploaded documents — one "storage" emulator resource, two sub-resources hung off it. Both are modeled
// unconditionally (not behind a flag) so `aspire run` always starts them; Table Storage is only *used* when
// Persistence:Provider=TableStorage, and Blob Storage only when a document is actually uploaded (see
// ApiService/Program.cs) — same "always available, config/activity picks what's used" shape as the two
// IQueueRepository implementations themselves.
// WithDataVolume persists Azurite's blob/table data in a named Docker volume across `aspire run` restarts, so
// blob *content* has the same lifetime as the SQLite metadata that describes it. Without it the emulator started
// empty every run while the metadata (SQLite file on disk) survived — leaving orphaned document rows that pointed
// at blobs no longer there, so viewing a document uploaded in an earlier run failed with "content missing". Note
// this only aligns the two going forward; documents are also deleted outright when their entry is completed (see
// the complete endpoint), so content lives exactly as long as the visitor is in the queue.
IResourceBuilder<AzureStorageResource> storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithDataVolume());
IResourceBuilder<AzureTableStorageResource> tables = storage.AddTables("tables");
IResourceBuilder<AzureBlobStorageResource> blobs = storage.AddBlobs("blobs");

// Both resources below now write to the queue store directly — ApiService via its REST endpoints, webfrontend
// (Blazor) via SignalRQueueDemo.Shared's repositories called in-process (see docs/decisions.md's "Blazor is
// self-encapsulated"). So both need identical storage wiring: the same tables/blobs references below, and (a few
// lines down) the same absolute QueueDb connection string, so a Blazor-originated check-in and an
// ApiService-originated one land in the exact same store instead of two silently divergent ones.
IResourceBuilder<ProjectResource> apiService = builder.AddProject<Projects.SignalRQueueDemo_ApiService>("apiservice")
    .WithReference(tables)
    .WaitFor(tables)
    .WithReference(blobs)
    .WaitFor(blobs)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// The SQLite provider's connection string ("Data Source=App_Data/queue.db" in each project's own
// appsettings.json) is process-relative — `aspire run` sets each project's working directory to its own project
// folder, so ApiService and Web would otherwise each open a DIFFERENT physical .db file and silently diverge
// (a Blazor check-in would simply never appear on the API/Angular side). Computed once, here, as an absolute
// path anchored to ApiService's own App_Data folder (same "relative to AppHostDirectory" pattern already used
// for the Angular Dockerfile context below) and injected into BOTH resources so they always agree on the file,
// regardless of Persistence:Provider — TableStorage doesn't need this (the "tables"/"blobs" references above
// already point both resources at the same Azurite emulator, no path ambiguity there).
string queueDbConnectionString =
    $"Data Source={Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "SignalRQueueDemo.ApiService", "App_Data", "queue.db"))}";
apiService.WithEnvironment("ConnectionStrings__QueueDb", queueDbConnectionString);

// DECISION: UseAzureSignalR (default false) is the ADR-0001 Option C escape hatch — read here
// independently from ApiService's own copy of the same-named flag (they're separate processes/config; there's
// no shared config source to read once). This copy decides whether the Azure SignalR Emulator resource exists
// at all. Unlike the storage emulator above (always started), this one is conditional: starting a container
// that only demonstrates a POC-only side-channel (see AzureSignalRServerlessDemoService) isn't worth the cost
// when nobody has opted in. Keep both copies of the default in sync when changing it.
bool useAzureSignalR = builder.Configuration.GetValue<bool>("UseAzureSignalR");
if (useAzureSignalR)
{
    // Serverless mode explicitly, not Default: the emulator can't exercise Default (server) mode at all — see
    // AzureSignalRDefaultModeStub in ApiService for the production path this can't stand in for. The emulator
    // exposes HTTP only, matching every other emulator resource in this AppHost (no HTTPS to fight here).
    IResourceBuilder<AzureSignalRResource> signalR = builder
        .AddAzureSignalR("signalr", AzureSignalRServiceMode.Serverless)
        .RunAsEmulator();

    apiService.WithReference(signalR).WaitFor(signalR);
}

// tables/blobs + the QueueDb connection string: webfrontend needs the exact same storage wiring as apiService —
// see the comment above apiService's own registration for why.
//
// WithReference(apiService) + WaitFor(apiService) are NOT leftovers, despite Blazor being "self-encapsulated":
// that term only means Blazor does its data reads/writes directly through SignalRQueueDemo.Shared's repositories
// instead of REST calls — it does NOT mean Blazor is isolated from the API process. Blazor still connects to the
// SignalR hub, which is hosted in ApiService:
//   - WithReference(apiService) injects the service-discovery key `services__apiservice__http__0` that
//     QueueRealtimeService reads to build the hub URL (`{apiBaseUrl}/hubs/queue`). Without it, the service can't
//     find the hub and permanently falls back to polling — no live pushes from other clients, no NotifyMutation.
//   - WaitFor(apiService) guarantees the API has run its startup schema-creation/seeding (see Program.cs) before
//     Blazor's first repository call, so Web's own Program.cs doesn't repeat that step; it also means the hub is
//     up before Blazor tries to connect.
// (What Blazor genuinely needs nothing for is the check-in QR — CheckInQr.razor points at Blazor's own /checkin
// page via NavigationManager.BaseUri, so no publicCheckin/API URL is injected for that. Hence no variable capture.)
builder.AddProject<Projects.SignalRQueueDemo_Web>("webfrontend")
    .WithReference(tables)
    .WaitFor(tables)
    .WithReference(blobs)
    .WaitFor(blobs)
    .WithEnvironment("ConnectionStrings__QueueDb", queueDbConnectionString)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

// The Angular containers need the API's browser-facing origin -- the endpoint the visitor's/staff's browser
// will actually call -- not the internal service-discovery name a plain WithReference(apiService) would inject
// (e.g. "http://apiservice"). config.json is fetched and read by browser JS (RuntimeConfigService in the
// Angular shared library), which has no way to resolve Aspire's internal service-discovery hostnames; only the
// externally-reachable host:port Aspire assigns to this endpoint works there. "http" matches the endpoint name
// Aspire derives from the "http" launch profile in ApiService/Properties/launchSettings.json.
//
// DECISION, found by testing against a live `aspire run`, not assumed: GetEndpoint("http") (no network context)
// resolves an endpoint's value *relative to whichever resource ends up consuming it* -- when that consumer is a
// container (as all three Angular resources are), Aspire substitutes its own container-network tunnel hostname
// (observed: "http://aspire.dev.internal:{port}"), which only resolves from *inside* Aspire's Docker network,
// never from the host. That value is useless here: config.json isn't read by anything inside that container
// network, it's read by browser JS running on the visitor's/staff's own machine, on the "localhost" network
// (the same network a `docker run -p` port mapping exposes things on). Passing
// KnownNetworkIdentifiers.LocalhostNetwork explicitly forces that resolution regardless of which resource asks
// for it, so this reference always yields "http://localhost:{the mapped port}" -- confirmed by re-running with
// this change and checking each container's actual config.json content and a live CORS preflight against the API.
EndpointReference apiHttpEndpoint = apiService.GetEndpoint("http", KnownNetworkIdentifiers.LocalhostNetwork);

// One build recipe (SignalRQueueDemo.Angular/Dockerfile), three container resources differing only in which
// app the APP_NAME build arg selects. See that Dockerfile's header comment for why a single parameterized file
// replaces three near-identical ones. WithBuildArg passes APP_NAME through to `docker build --build-arg`; the
// build context is the whole Angular workspace folder (not one app's subfolder) because every app depends on
// the "shared" library and the workspace-root package.json/angular.json — see the Dockerfile for why.
// targetPort: 80 matches docker/nginx.conf's `listen 80`; WithExternalHttpEndpoints exposes each container's
// Aspire-assigned host port to the browser, the same way webfrontend above does for Blazor.
IResourceBuilder<ContainerResource> publicCheckin = builder
    .AddDockerfile("public-checkin", "../SignalRQueueDemo.Angular", "Dockerfile")
    .WithBuildArg("APP_NAME", "public-checkin")
    .WithHttpEndpoint(targetPort: 80, name: "http")
    .WithExternalHttpEndpoints()
    .WithEnvironment("API_BASE_URL", apiHttpEndpoint)
    .WaitFor(apiService);

IResourceBuilder<ContainerResource> internalQueue = builder
    .AddDockerfile("internal-queue", "../SignalRQueueDemo.Angular", "Dockerfile")
    .WithBuildArg("APP_NAME", "internal-queue")
    .WithHttpEndpoint(targetPort: 80, name: "http")
    .WithExternalHttpEndpoints()
    .WithEnvironment("API_BASE_URL", apiHttpEndpoint)
    .WaitFor(apiService);

IResourceBuilder<ContainerResource> queueDisplay = builder
    .AddDockerfile("queue-display", "../SignalRQueueDemo.Angular", "Dockerfile")
    .WithBuildArg("APP_NAME", "queue-display")
    .WithHttpEndpoint(targetPort: 80, name: "http")
    .WithExternalHttpEndpoints()
    .WithEnvironment("API_BASE_URL", apiHttpEndpoint)
    // queue-display no longer gets the public-checkin URL injected: its "check in from your phone" QR + link now
    // come from the API (GET /checkin/url + GET /checkin/qr, see CheckInQr), so all three containers get the
    // identical API_BASE_URL-only config. The API is the single source of that address (injected just below).
    .WaitFor(apiService);

// publicCheckin is declared above, so its endpoint is now referenceable. Inject its host-reachable URL
// (LocalhostNetwork-pinned, same reasoning as apiHttpEndpoint) into the API only: the API's /checkin/qr endpoint
// encodes THIS URL — the public-checkin Angular app — into an SVG the Angular queue-display board fetches. The
// Blazor webfrontend deliberately gets nothing here: it advertises its own /checkin page, not this Angular one.
apiService.WithEnvironment("PublicCheckinUrl", publicCheckin.GetEndpoint("http", KnownNetworkIdentifiers.LocalhostNetwork));

// CORS coordination is deliberately NOT done here by injecting the Angular containers' origins into apiService.
// That was tried and it silently failed in a real browser: Aspire serves each container under its own
// `*.dev.localhost` hostname (observed: http://public-checkin-signalrqueuedemo.dev.localhost:{port}), but an
// endpoint reference resolved with KnownNetworkIdentifiers.LocalhostNetwork yields the `localhost` host instead —
// matching the port but not the host the browser actually loads the page from, so the API's exact-origin CORS
// check rejected every request. (Same-origin curl checks sending Origin: http://localhost:{port} passed, which is
// exactly why the mismatch wasn't caught at the HTTP level.) Rather than chase Aspire's per-run, per-resource host
// naming from here, the API accepts any loopback-family origin when Cors:AllowLoopbackOrigins is true — correct for
// this localhost-only court POC and immune to the port/host churn. See ApiService/Program.cs's CORS decision
// comment and docs/decisions.md. apiService keeps WithExternalHttpEndpoints() (above) so the browser can still
// reach the API cross-origin at the address baked into each container's config.json.

builder.Build().Run();
