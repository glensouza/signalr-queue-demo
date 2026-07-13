using Microsoft.AspNetCore.Mvc;
using SignalRQueueDemo.Shared.Auth;

namespace SignalRQueueDemo.ApiService.Endpoints;

/// <summary>
/// Models the internal-vs-public trust boundary from ADR-0001 with a single static shared-secret header — not
/// real authentication. Gates <c>POST /queue/call-next</c>, <c>POST /queue/{id}/complete</c>, and the two
/// document-viewing endpoints (<c>GET /queue/{id}/documents</c>, <c>GET /queue/{id}/documents/{docId}</c>).
/// Production replaces this filter with Entra ID (see ADR-0001's "schema-scoped SQL logins" requirement and
/// <c>docs/architecture.md</c>'s trust-boundary table); this POC only needs to demonstrate that staff endpoints
/// are gated at all, not stand up a real identity provider.
/// </summary>
public sealed class StaffAuthFilter(IConfiguration configuration) : IEndpointFilter
{
  /// <summary>Header a staff client must send, compared against <c>StaffAuth:Key</c> in config.</summary>
  public const string HeaderName = "X-Staff-Key";

  private readonly IConfiguration configuration = configuration;

  public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
  {
    // appsettings.json ships an obvious placeholder value with a comment pointing at user-secrets/environment
    // for a real deployment — never a real secret in source control (CLAUDE.md's hard constraint). Read fresh
    // on every call (not cached) so a config reload picks up a rotated key without a restart.
    string expectedKey = this.configuration["StaffAuth:Key"]
      ?? throw new InvalidOperationException("Missing required configuration 'StaffAuth:Key'.");

    string? providedKey = context.HttpContext.Request.Headers[HeaderName];

    // The comparison itself — fixed-time, so a naive == can't leak how many leading bytes matched via
    // response-time differences — lives in SignalRQueueDemo.Shared.Auth.StaffKeyVerifier so Blazor's staff
    // sign-in page (which checks the key in-process, not via this filter) uses the identical check.
    if (!StaffKeyVerifier.IsValid(providedKey, expectedKey))
    {
      return Results.Problem(
        title: "Missing or invalid staff key",
        detail: $"This endpoint requires a valid '{HeaderName}' header.",
        statusCode: StatusCodes.Status401Unauthorized);
    }

    return await next(context);
  }
}
