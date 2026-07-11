using Aspire.Hosting.Azure;

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

builder.AddProject<Projects.SignalRQueueDemo_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
