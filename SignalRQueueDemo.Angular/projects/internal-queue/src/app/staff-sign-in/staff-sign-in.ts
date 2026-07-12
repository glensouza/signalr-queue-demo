import { Component, inject, output, signal } from '@angular/core';
import { StaffSessionService } from '../staff-session.service';

/**
 * Staff authentication screen: a simple form where the staff member enters their authentication key. Once valid,
 * it's stored in memory by {@link StaffSessionService} and the shell switches to the live queue console.
 *
 * <p>Validation is minimal (non-empty key) — the real validation happens on the first API call (callNext,
 * complete, etc.). A wrong key causes that call to return `401`, surfaced to the user as a "check your key /
 * sign in again" message.</p>
 *
 * <p>This establishes the same form + output pattern as the kiosk check-in component: the form is focused on
 * its own concern (collecting input), and the shell state machine owns the transition to the next phase. The
 * component is standalone and signal-driven, not importing anything that couples it to the feature — same as
 * public-checkin's {@link CheckInForm}.</p>
 */
@Component({
  selector: 'app-staff-sign-in',
  imports: [],
  templateUrl: './staff-sign-in.html',
  styleUrl: './staff-sign-in.css',
})
export class StaffSignIn {
  private readonly session = inject(StaffSessionService);

  /** Emitted once a valid sign-in is submitted. */
  readonly signedIn = output<void>();

  protected readonly staffKey = signal('');
  /** Non-null while the last submit attempt is showing an error. */
  protected readonly error = signal<string | null>(null);

  protected onStaffKeyInput(event: Event): void {
    this.staffKey.set((event.target as HTMLInputElement).value);
    this.error.set(null);
  }

  /**
   * Stashes the entered key and hands off to the console. Deliberately does no validation round-trip: there is no
   * dedicated "verify key" endpoint, and gating the form on one would be more surface than this mock-auth model
   * warrants. The key is proven — or rejected with a 401 — by the first real staff action in the console, which
   * signs the user straight back out to this screen on failure (see {@link QueueConsole}'s `authError`). So the
   * only thing worth checking here is that the field isn't empty.
   */
  protected submit(): void {
    const key: string = this.staffKey().trim();
    if (key.length === 0) {
      this.error.set('Please enter your staff key.');
      return;
    }

    this.session.signIn(key);
    this.signedIn.emit();
  }
}
