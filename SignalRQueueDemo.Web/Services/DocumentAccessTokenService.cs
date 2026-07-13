using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SignalRQueueDemo.Web.Services;

/// <summary>
/// Issues and validates the short-lived token that gates the local <c>GET /staff/documents/{entryId}/{docId}</c>
/// streaming endpoint (see <c>Endpoints/DocumentStreamEndpoints.cs</c>). Exists because an <c>&lt;iframe src&gt;</c>,
/// <c>&lt;img src&gt;</c>, or plain download link can't carry the <c>X-Staff-Key</c> header the REST document
/// endpoints use — the browser only ever sends a bare GET to whatever URL server-rendered Razor markup put in the
/// <c>src</c>/<c>href</c> attribute. So instead of a header, the staff page mints a fresh, resource-scoped token
/// server-side (only reachable from already-<see cref="StaffSessionService.IsSignedIn"/> markup) each time it
/// renders a document link, and the streaming endpoint checks that token instead of a header.
///
/// <para>
/// Same signed-expiry-timestamp shape as <c>SignalRQueueDemo.ApiService.Endpoints.CheckInTokenService</c>, but
/// resource-scoped: the signature covers <c>entryId</c> and <c>documentId</c> too, so a token minted for one
/// document can't be replayed against another. Reuses <c>StaffAuth:Key</c> as the signing key rather than
/// introducing a second secret to manage — this token never leaves the staff trust boundary that key already
/// gates (only staff-signed-in markup ever calls <see cref="Issue"/>), so it doesn't need independent rotation.
/// </para>
/// </summary>
public sealed class DocumentAccessTokenService(IConfiguration configuration)
{
  /// <summary>
  /// Kept as bytes from construction, not re-read per call — see <c>CheckInTokenService.signingKey</c>'s remarks
  /// for the same reasoning (rotating this key invalidates every outstanding token anyway, so there's no value
  /// in picking up a live config change mid-process).
  /// </summary>
  private readonly byte[] signingKey = Encoding.UTF8.GetBytes(
    configuration["StaffAuth:Key"] ?? throw new InvalidOperationException("Missing required configuration 'StaffAuth:Key'."));

  /// <summary>
  /// Short enough that a token embedded in server-rendered markup is stale well before the page itself would be
  /// (a live circuit re-renders the document list on every queue update anyway), long enough that a slow browser
  /// fetching an <c>&lt;iframe&gt;</c>'s content never legitimately races past it.
  /// </summary>
  private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(2);

  /// <summary>Issues a token scoped to exactly this <paramref name="entryId"/>/<paramref name="documentId"/> pair.</summary>
  public string Issue(string entryId, string documentId)
  {
    long expiresAtUnixSeconds = DateTimeOffset.UtcNow.Add(TokenLifetime).ToUnixTimeSeconds();
    return $"{expiresAtUnixSeconds}.{this.ComputeSignatureHex(entryId, documentId, expiresAtUnixSeconds)}";
  }

  /// <summary>
  /// True if <paramref name="token"/> was issued by <see cref="Issue"/> for this exact
  /// <paramref name="entryId"/>/<paramref name="documentId"/> pair, hasn't been tampered with, and hasn't expired.
  /// </summary>
  public bool Validate(string entryId, string documentId, string? token)
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

    // Recomputing against the entryId/documentId from the URL (not anything the token itself claims) is what
    // makes a token scoped: a signature computed for a different document simply won't match here.
    string expectedSignatureHex = this.ComputeSignatureHex(entryId, documentId, expiresAtUnixSeconds);
    bool signatureMatches = CryptographicOperations.FixedTimeEquals(
      Encoding.UTF8.GetBytes(parts[1]), Encoding.UTF8.GetBytes(expectedSignatureHex));

    return signatureMatches && DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds) > DateTimeOffset.UtcNow;
  }

  private string ComputeSignatureHex(string entryId, string documentId, long expiresAtUnixSeconds)
  {
    string payload = $"{entryId}.{documentId}.{expiresAtUnixSeconds.ToString(CultureInfo.InvariantCulture)}";
    byte[] signatureBytes = HMACSHA256.HashData(this.signingKey, Encoding.UTF8.GetBytes(payload));
    return Convert.ToHexString(signatureBytes);
  }
}
