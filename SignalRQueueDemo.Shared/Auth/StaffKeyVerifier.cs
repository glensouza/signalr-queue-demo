using System.Security.Cryptography;
using System.Text;

namespace SignalRQueueDemo.Shared.Auth;

/// <summary>
/// The one comparison behind the "staff key" mock-auth boundary (ADR-0001) — lifted out of
/// <c>SignalRQueueDemo.ApiService.Endpoints.StaffAuthFilter</c> so a second caller (Blazor's staff sign-in page,
/// which checks the key in-process against its own <c>StaffAuth:Key</c> config rather than calling the REST
/// <c>StaffAuthFilter</c>-gated endpoints) uses the identical check instead of a hand-copied one that could drift.
/// </summary>
public static class StaffKeyVerifier
{
  /// <summary>
  /// True when <paramref name="provided"/> matches <paramref name="expected"/>. Fixed-time comparison: a naive
  /// <c>==</c> leaks how many leading bytes matched via response-time differences. Overkill for a POC demo key,
  /// but it's the correct habit for anything modeling a real auth boundary, and costs nothing here.
  /// <see cref="CryptographicOperations.FixedTimeEquals"/> returns false outright for mismatched lengths without
  /// comparing bytes, so there's no need to length-check first.
  /// </summary>
  public static bool IsValid(string? provided, string expected) =>
    provided is not null
    && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
