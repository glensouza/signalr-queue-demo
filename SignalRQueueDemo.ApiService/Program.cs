WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations (OpenTelemetry, health checks, service discovery).
builder.AddServiceDefaults();

builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Queue endpoints and the SignalR QueueHub land here — see GitHub issues #2 and #3.
app.MapGet("/", () => "SignalR Queue Demo API is running.");

app.MapDefaultEndpoints();

app.Run();
