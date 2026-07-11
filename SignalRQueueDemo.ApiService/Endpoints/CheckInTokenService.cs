using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SignalRQueueDemo.ApiService.Endpoints;

/// <summary>
/// Issues and validates the short-lived, stateless token that gates the public check-in path (<c>POST
/// /checkin</c> and <c>POST /checkin/{id}/documents</c>) — see docs/decisions.md, "POST
/// /checkin/{id}/documents calls .DisableAntiforgery()". ASP.NET Core's built-in antiforgery system is
/// cookie-based and assumes a same-site browser session; a public kiosk SPA served from a different origin
/// (per the CORS hardening this same work adds) can't be assumed to carry that cookie. This is a from-scratch,
/// stateless alternative instead: an HMAC-signed expiry timestamp the server never has to remember issuing, so
/// there's no server-side token store to clean up or leak.
///
/// <para>
/// <b>What this does and doesn't protect against, stated honestly:</b> it raises the bar against drive-by
/// scripted abuse (a caller has to fetch a token first, and it expires quickly) — it does not prove the request
/// came from the real kiosk app, since the signing key lives in this POC's own config, readable by anything that
/// can read the deployed app's configuration. A public SPA fundamentally cannot hold a secret a determined
/// attacker can't also read; see <c>docs/decisions.md</c> for the full tradeoff.
/// </para>
/// </summary>
public sealed class CheckInTokenService(IConfiguration configuration)
{
  /// <summary>
  /// The HMAC signing key, read from config once at construction. Cached as bytes (rather than re-read and
  /// re-encoded from <c>IConfiguration</c> on every Issue/Validate, both of which are on the check-in hot path)
  /// because — unlike the staff key, which is re-read per request so a rotated value takes effect without a
  /// restart — rotating this key would invalidate every outstanding token anyway, so there's no value in
  /// picking up a live change mid-process. This service is a singleton, so this runs exactly once.
  /// </summary>
  private readonly byte[] signingKey = Encoding.UTF8.GetBytes(
    configuration["CheckInToken:SigningKey"]
      ?? throw new InvalidOperationException("Missing required configuration 'CheckInToken:SigningKey'."));

  /// <summary>
  /// How long an issued token stays valid. Short enough that a leaked or logged token is useless quickly, long
  /// enough that a slow kiosk flow (fetch token, fill the form, submit) never legitimately expires mid-flow.
  /// </summary>
  private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

  /// <summary>Issues a new token, an opaque <c>expiry.signature</c> string, good until <see cref="TokenLifetime"/> from now.</summary>
  public string Issue()
  {
    long expiresAtUnixSeconds = DateTimeOffset.UtcNow.Add(TokenLifetime).ToUnixTimeSeconds();
    return $"{expiresAtUnixSeconds}.{this.ComputeSignatureHex(expiresAtUnixSeconds)}";
  }

  /// <summary>True if <paramref name="token"/> was issued by <see cref="Issue"/>, hasn't been tampered with, and hasn't expired.</summary>
  public bool Validate(string? token)
  {
    if (string.IsNullOrEmpty(token))
    {
      return false;
    }

    string[] parts = token.Split('.');
    if (parts.Length != 2
      || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long expiresAtUnixSeconds))
    {
      return false;
    }

    // Recompute the expected signature for the claimed expiry rather than trusting the caller's — this is what
    // makes the expiry tamper-evident (bump it forward, the signature no longer matches).
    string expectedSignatureHex = this.ComputeSignatureHex(expiresAtUnixSeconds);
    bool signatureMatches = CryptographicOperations.FixedTimeEquals(
      Encoding.UTF8.GetBytes(parts[1]), Encoding.UTF8.GetBytes(expectedSignatureHex));
    if (!signatureMatches)
    {
      return false;
    }

    return DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds) > DateTimeOffset.UtcNow;
  }

  private string ComputeSignatureHex(long expiresAtUnixSeconds)
  {
    // Static HashData rather than newing an HMACSHA256 per call: it's allocation-light and thread-safe (a shared
    // HMACSHA256 instance is not — ComputeHash mutates internal state — so it couldn't be cached across the
    // concurrent check-in requests this runs on anyway).
    byte[] signatureBytes = HMACSHA256.HashData(
      this.signingKey, Encoding.UTF8.GetBytes(expiresAtUnixSeconds.ToString(CultureInfo.InvariantCulture)));
    return Convert.ToHexString(signatureBytes);
  }
}
