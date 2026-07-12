using Aspire.Hosting.ApplicationModel;
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
IResourceBuilder<AzureStorageResource> storage = builder.AddAzureStorage("storage")
    .RunAsEmulator();
IResourceBuilder<AzureTableStorageResource> tables = storage.AddTables("tables");
IResourceBuilder<AzureBlobStorageResource> blobs = storage.AddBlobs("blobs");

IResourceBuilder<ProjectResource> apiService = builder.AddProject<Projects.SignalRQueueDemo_ApiService>("apiservice")
    .WithReference(tables)
    .WaitFor(tables)
    .WithReference(blobs)
    .WaitFor(blobs)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

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

builder.AddProject<Projects.SignalRQueueDemo_Web>("webfrontend")
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
    .WaitFor(apiService);

// CORS coordination: the browser loads each Angular app from ITS OWN Aspire-assigned external origin (a
// different host:port per container, decided at `aspire run` time), so the API's allowed-origins list
// (appsettings.json's Cors:AllowedOrigins placeholders — see Program.cs) must be overridden with those exact
// origins, not left pointing at the fixed local dev-server ports. Aspire's double-underscore config-binding
// convention (Cors__AllowedOrigins__0 -> configuration key Cors:AllowedOrigins[0]) lets each container's
// endpoint reference flow straight into the array index its dev-server placeholder occupies today.
//
// DECISION: this is a reference only — no WithReference/WaitFor here. apiService already has everything it
// needs to resolve these values once the containers' ports are assigned; it does not need to wait for the
// Angular containers to be *running* first, which is fortunate, because they WaitFor(apiService) above — a
// mutual WaitFor here would deadlock every startup. Env vars built from endpoint references are resolved
// lazily by Aspire regardless of the order resources start in, so declaring this after WaitFor(apiService)
// above (rather than before the Angular resources exist) is just about readability, not correctness.
//
// Same KnownNetworkIdentifiers.LocalhostNetwork reasoning as apiHttpEndpoint above, applied in the opposite
// direction: the browser's CORS preflight sends the Origin it loaded the page from -- the host-mapped
// "http://localhost:{port}" -- so apiService's allowlist must contain that exact value, not a container-network
// address the browser never sees. Confirmed with a live CORS preflight (`curl -X OPTIONS .../queue -H
// "Origin: http://localhost:{publicCheckin's mapped port}"`) returning Access-Control-Allow-Origin only after
// this fix; without it, every containerized Angular app's cross-origin calls silently failed CORS.
apiService
    .WithEnvironment("Cors__AllowedOrigins__0", publicCheckin.GetEndpoint("http", KnownNetworkIdentifiers.LocalhostNetwork))
    .WithEnvironment("Cors__AllowedOrigins__1", internalQueue.GetEndpoint("http", KnownNetworkIdentifiers.LocalhostNetwork))
    .WithEnvironment("Cors__AllowedOrigins__2", queueDisplay.GetEndpoint("http", KnownNetworkIdentifiers.LocalhostNetwork));

builder.Build().Run();
