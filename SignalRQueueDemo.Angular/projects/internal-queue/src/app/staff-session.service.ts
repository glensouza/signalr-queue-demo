import { Injectable, signal } from '@angular/core';

/**
 * App-local session service for the internal-queue staff console. Holds the staff authentication key in memory
 * for the duration of the session. This service encodes internal-queue's chosen policy: **collect the staff key
 * once at "sign-in" and keep it in memory, never persist to localStorage/sessionStorage**.
 *
 * <p>Why in-memory only: a refresh returns to the sign-in screen, enforcing that the entering staff member has
 * a reason to be there (a shared terminal doesn't stay logged in if stepped away from). A real deployment
 * replaces this with Entra ID and a standard auth flow; this mock `X-Staff-Key` header models the internal-vs-public
 * trust boundary the API enforces — see {@link StaffAuthFilter} on the backend.</p>
 *
 * <p>The shared {@link QueueApiService} takes the staff key as a per-call parameter, not as service state,
 * exactly because token-hold policy is a UI-flow concern it deliberately stays out of. This service is what
 * holds it here and hands it to those calls.</p>
 */
@Injectable({ providedIn: 'root' })
export class StaffSessionService {
  /** The current staff member's authentication key, or null if not signed in. */
  readonly staffKey = signal<string | null>(null);

  /**
   * Signs in the staff member with the provided key. This stores the key in memory only — the signal is the
   * only reference, and it's cleared on every page refresh.
   */
  signIn(key: string): void {
    this.staffKey.set(key);
  }

  /**
   * Signs out the staff member, clearing the in-memory key and returning to the sign-in screen.
   */
  signOut(): void {
    this.staffKey.set(null);
  }
}
