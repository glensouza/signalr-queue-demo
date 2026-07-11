using Microsoft.AspNetCore.Mvc;

namespace SignalRQueueDemo.ApiService.Endpoints;

/// <summary>
/// Validates the short-lived token from <see cref="CheckInTokenService"/> on the public check-in path. Applied
/// as a filter to <c>POST /checkin</c>; the document-upload route (<c>POST /checkin/{id}/documents</c>) instead
/// calls <see cref="Reject"/> inline (see that method's remarks for why a filter is the wrong tool there). The
/// public GET endpoints (<c>/queue</c>, <c>/queue/since/*</c>) are read-only and stay ungated, matching the
/// usual scope of an anti-forgery-style check.
/// </summary>
public sealed class CheckInTokenFilter(CheckInTokenService tokenService) : IEndpointFilter
{
  /// <summary>Header a kiosk client must echo back the value <c>GET /checkin/token</c> issued it.</summary>
  public const string HeaderName = "X-CheckIn-Token";

  private readonly CheckInTokenService tokenService = tokenService;

  public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
  {
    IResult? rejection = Reject(context.HttpContext.Request, this.tokenService);
    return rejection is not null ? rejection : await next(context);
  }

  /// <summary>
  /// The shared token gate: returns a 401 <see cref="Microsoft.AspNetCore.Mvc.ProblemDetails"/> result when the
  /// request carries no valid token, or null when it does. Exposed as a static because the document-upload
  /// handler must run this check <b>before</b> it reads the multipart body — an <see cref="IEndpointFilter"/>
  /// runs only after minimal-API parameter binding has already buffered that body, so gating the upload via the
  /// filter would let an unauthenticated caller still make the server spool a (bounded) upload. Calling this
  /// first, inline, means an upload with no valid token is rejected before a single form byte is read.
  /// </summary>
  public static IResult? Reject(HttpRequest request, CheckInTokenService tokenService)
  {
    string? token = request.Headers[HeaderName];
    if (tokenService.Validate(token))
    {
      return null;
    }

    return Results.Problem(
      title: "Missing or expired check-in token",
      detail: $"Fetch a fresh token from GET /checkin/token and echo it back on the '{HeaderName}' header.",
      statusCode: StatusCodes.Status401Unauthorized);
  }
}
