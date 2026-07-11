var builder = DistributedApplication.CreateBuilder(args);

// Azurite-backed Table Storage resource for issue #4's second IQueueRepository implementation. Modeled
// unconditionally (not behind a flag) so `aspire run` always starts the emulator; the API only talks to it
// when Persistence:Provider=TableStorage (see ApiService/Program.cs) — same "always available, config picks
// which one's used" shape as the two IQueueRepository implementations themselves.
var tables = builder.AddAzureStorage("storage")
    .RunAsEmulator()
    .AddTables("tables");

var apiService = builder.AddProject<Projects.SignalRQueueDemo_ApiService>("apiservice")
    .WithReference(tables)
    .WaitFor(tables)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.SignalRQueueDemo_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
