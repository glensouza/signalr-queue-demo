using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

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

    // Fixed-time comparison: a naive == leaks how many leading bytes matched via response-time differences.
    // Overkill for a POC demo key, but it's the correct habit for anything modeling a real auth boundary, and
    // costs nothing here. FixedTimeEquals returns false outright for mismatched lengths without comparing bytes,
    // so there's no need to length-check first.
    bool isAuthorized = providedKey is not null
      && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(providedKey), Encoding.UTF8.GetBytes(expectedKey));

    if (!isAuthorized)
    {
      return Results.Problem(
        title: "Missing or invalid staff key",
        detail: $"This endpoint requires a valid '{HeaderName}' header.",
        statusCode: StatusCodes.Status401Unauthorized);
    }

    return await next(context);
  }
}
