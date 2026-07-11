namespace SignalRQueueDemo.ApiService.Endpoints;

/// <summary>
/// A shared constant for the one named CORS policy this API defines, so the policy's definition (Program.cs,
/// where <c>AddCors</c> registers it) and its application (<c>.RequireCors(...)</c> on endpoints in this folder
/// and on the SignalR hub in Program.cs) can't drift apart via a typo'd string literal on either side.
/// </summary>
public static class CorsPolicies
{
  /// <summary>
  /// The allowlist of known browser frontends (public <b>and</b> staff Angular apps, plus the Blazor app) that
  /// may call this API cross-origin — origins come from <c>Cors:AllowedOrigins</c> config. Applied to every
  /// browser-reachable surface: the public and staff REST endpoints and the SignalR hub. CORS is not the auth
  /// boundary — <see cref="StaffAuthFilter"/> still gates the staff endpoints and <see cref="CheckInTokenFilter"/>
  /// the check-in POSTs — it just ensures the legitimate cross-origin frontends aren't refused by the browser
  /// before those checks can run, while an unlisted origin still is.
  /// </summary>
  public const string KnownFrontends = "KnownFrontends";
}
