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

builder.Build().Run();
