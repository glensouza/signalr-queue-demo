/** Single-token names longer than this are truncated to this many characters before the ellipsis (see {@link toPublicName}). */
const SINGLE_TOKEN_VISIBLE_CHARS = 2;

/**
 * Formats a visitor's full name for the PUBLIC waiting-room board as first name + last initial (e.g. "Jane Test"
 * → "Jane T."). This board is visible to everyone in the room, so it deliberately does not show full surnames —
 * enough for a visitor to recognise their own entry, without publishing who else is at the court to the whole
 * room. Blank input yields an empty string.
 *
 * <p>A name with no whitespace (a hyphenated surname entered alone, or a mononym) has no separate surname to
 * abbreviate, but showing it verbatim would defeat the masking this function exists to provide — so it's
 * truncated to {@link SINGLE_TOKEN_VISIBLE_CHARS} characters plus an ellipsis instead (e.g. "Prince" → "Pr…").
 * Short single-token names (at or under the visible-char count) are shown as-is since there's nothing left to
 * hide.</p>
 *
 * <p>Shared by {@link NowServing} and {@link WaitingList} so the masking rule lives in exactly one place — if it
 * ever needs to change (full names, initials only, ticket-only), it changes here, not in two templates.</p>
 */
export function toPublicName(displayName: string): string {
  const parts: string[] = displayName.trim().split(/\s+/).filter((part) => part.length > 0);
  if (parts.length === 0) {
    return '';
  }

  if (parts.length === 1) {
    const [name] = parts;
    return name.length > SINGLE_TOKEN_VISIBLE_CHARS ? `${name.slice(0, SINGLE_TOKEN_VISIBLE_CHARS)}…` : name;
  }

  const lastInitial: string = parts[parts.length - 1][0].toUpperCase();
  return `${parts[0]} ${lastInitial}.`;
}
