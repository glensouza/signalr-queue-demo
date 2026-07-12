/**
 * Formats a visitor's full name for the PUBLIC waiting-room board as first name + last initial (e.g. "Jane Test"
 * → "Jane T."). This board is visible to everyone in the room, so it deliberately does not show full surnames —
 * enough for a visitor to recognise their own entry, without publishing who else is at the court to the whole
 * room. A single-word name is shown as-is (there's no surname to abbreviate); blank input yields an empty string.
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
    return parts[0];
  }

  const lastInitial: string = parts[parts.length - 1][0].toUpperCase();
  return `${parts[0]} ${lastInitial}.`;
}
