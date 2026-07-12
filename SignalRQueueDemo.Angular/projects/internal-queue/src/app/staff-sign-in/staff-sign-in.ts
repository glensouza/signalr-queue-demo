import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, output, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { QueueApiService } from 'shared';
import { StaffSessionService } from '../staff-session.service';

/**
 * Staff authentication screen: a form where the staff member enters their authentication key. The key is
 * validated against the API up front (GET /staff/verify) and only stored — handing off to the console — once the
 * server confirms it. A wrong key is rejected right here with an inline message; it never reaches the console.
 *
 * <p>Validating on submit (rather than storing any non-empty string and letting the first real staff action fail
 * with a 401) is what makes "the sign-in only accepts the right key" literally true, instead of "the sign-in
 * accepts anything and the console falls over later". The trade-off is one extra round-trip per sign-in attempt,
 * which for a once-per-session screen is invisible. The console still handles a 401 on its own actions (see
 * {@link QueueConsole}'s `authError`) as defence in depth — a key can be rotated server-side mid-session — but
 * that path is no longer the primary way a wrong key surfaces.</p>
 *
 * <p>This keeps the same form + output pattern as the kiosk check-in component: the form owns collecting and now
 * verifying input, and the shell state machine owns the transition to the next phase once {@link signedIn} fires.</p>
 */
@Component({
  selector: 'app-staff-sign-in',
  imports: [],
  templateUrl: './staff-sign-in.html',
  styleUrl: './staff-sign-in.css',
})
export class StaffSignIn {
  private readonly session = inject(StaffSessionService);
  private readonly api = inject(QueueApiService);

  /** Emitted once a valid sign-in is submitted. */
  readonly signedIn = output<void>();

  protected readonly staffKey = signal('');
  /** Non-null while the last submit attempt is showing an error. */
  protected readonly error = signal<string | null>(null);
  /** True while a verify round-trip is in flight — disables the form so a key can't be double-submitted. */
  protected readonly verifying = signal(false);

  protected onStaffKeyInput(event: Event): void {
    this.staffKey.set((event.target as HTMLInputElement).value);
    this.error.set(null);
  }

  /**
   * Verifies the entered key against the API and, only on success, stashes it and hands off to the console. A 401
   * means the key is wrong (the common case, surfaced with a specific message); any other failure means the verify
   * call itself couldn't complete (API not up yet, network) and gets a distinct "try again" message so the two
   * aren't conflated. Guards against a concurrent second submit while one is already in flight.
   */
  protected async submit(): Promise<void> {
    if (this.verifying()) {
      return;
    }

    const key: string = this.staffKey().trim();
    if (key.length === 0) {
      this.error.set('Please enter your staff key.');
      return;
    }

    this.verifying.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(this.api.verifyStaffKey(key));
      this.session.signIn(key);
      this.signedIn.emit();
    } catch (err) {
      if (err instanceof HttpErrorResponse && err.status === 401) {
        this.error.set('That staff key is not valid. Check it and try again.');
      } else {
        this.error.set('Could not reach the server to verify your key. Please try again.');
      }
    } finally {
      this.verifying.set(false);
    }
  }
}
