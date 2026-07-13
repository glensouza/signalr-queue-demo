namespace SignalRQueueDemo.Web.Components.Queue;

/// <summary>
/// C# port of the Angular <c>queue-display</c> app's <c>toPublicName</c> (<c>public-name.ts</c>) — kept app-local
/// there and here (not in the shared library) because it's a display-page-only formatting rule, not a domain
/// concern the other experiences need.
///
/// <para>
/// Formats a visitor's full name for the PUBLIC waiting-room board as first name + last initial (e.g. "Jane
/// Test" → "Jane T."). This board is visible to everyone in the room, so it deliberately does not show full
/// surnames — enough for a visitor to recognise their own entry, without publishing who else is at the court to
/// the whole room (2026-07-12 decision superseding an earlier ticket-only stance — see docs/decisions.md).
/// </para>
/// </summary>
public static class PublicName
{
  /// <summary>Single-token names longer than this are truncated to this many characters before the ellipsis.</summary>
  private const int SingleTokenVisibleChars = 2;

  /// <summary>
  /// A name with no whitespace (a hyphenated surname entered alone, or a mononym) has no separate surname to
  /// abbreviate, but showing it verbatim would defeat the masking this method exists to provide — so it's
  /// truncated to <see cref="SingleTokenVisibleChars"/> characters plus an ellipsis instead (e.g. "Prince" →
  /// "Pr…"). Short single-token names (at or under the visible-char count) are shown as-is since there's nothing
  /// left to hide. Blank input yields an empty string.
  /// </summary>
  public static string ToPublicName(string displayName)
  {
    string[] parts = displayName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
    {
      return string.Empty;
    }

    if (parts.Length == 1)
    {
      string name = parts[0];
      return name.Length > SingleTokenVisibleChars ? $"{name[..SingleTokenVisibleChars]}…" : name;
    }

    char lastInitial = char.ToUpperInvariant(parts[^1][0]);
    return $"{parts[0]} {lastInitial}.";
  }
}
