import { Component, inject, output, signal } from '@angular/core';
import { CheckInResponse } from 'shared';
import { KioskCheckInService } from '../kiosk-check-in.service';

/**
 * Kiosk check-in form: the visitor's name plus an auto-suggested (but editable) ticket number, submitted with no
 * user auth via the anti-forgery-style token flow ({@link KioskCheckInService}).
 *
 * <p>This is the first of the three concrete components that establish the pattern the sibling staff and display
 * apps copy: template-driven with plain {@link signal}s for form state (this workspace is zoneless — no zone.js — so
 * signals, not mutable fields, are what drive change detection), an app-local service for the token-gated HTTP,
 * and a single {@link output} that hands the successful result up to the shell state machine rather than
 * navigating itself.</p>
 */
@Component({
  selector: 'app-check-in-form',
  imports: [],
  templateUrl: './check-in-form.html',
  styleUrl: './check-in-form.css',
})
export class CheckInForm {
  private readonly kiosk = inject(KioskCheckInService);

  /** Emitted once, on a successful check-in, carrying the created entry so the shell can switch to the live position view. */
  readonly checkedIn = output<CheckInResponse>();

  protected readonly displayName = signal('');
  // Prefilled with a suggestion so the common case is one field + tap; deliberately editable, since a real kiosk
  // would print/scan a ticket and the vendor team may wire that here. Fake by construction per the court
  // no-real-data constraint — this never derives from anything real.
  protected readonly ticketNumber = signal(suggestTicketNumber());
  protected readonly submitting = signal(false);
  /** Non-null while the last submit attempt is showing an error; cleared as soon as the visitor edits a field. */
  protected readonly error = signal<string | null>(null);

  protected onDisplayNameInput(event: Event): void {
    this.displayName.set((event.target as HTMLInputElement).value);
    this.error.set(null);
  }

  protected onTicketNumberInput(event: Event): void {
    this.ticketNumber.set((event.target as HTMLInputElement).value);
    this.error.set(null);
  }

  /**
   * Validates locally (mirroring the server's "both fields required" rule so a visitor gets an instant, in-place
   * message instead of a round-trip 400), then submits. The server re-validates regardless — this check is for
   * responsiveness, not trust.
   */
  protected async submit(): Promise<void> {
    if (this.submitting()) {
      return;
    }

    const displayName: string = this.displayName().trim();
    const ticketNumber: string = this.ticketNumber().trim();
    if (displayName.length === 0 || ticketNumber.length === 0) {
      this.error.set('Please enter your name and ticket number.');
      return;
    }

    this.submitting.set(true);
    this.error.set(null);
    try {
      const response: CheckInResponse = await this.kiosk.checkIn({ displayName, ticketNumber });
      this.checkedIn.emit(response);
    } catch {
      // Deliberately generic: a kiosk visitor can't act on an HTTP status, and the specific failure (network,
      // expired token, server validation) is already logged by the browser/console. "Try again" is the only
      // useful instruction, and re-submitting fetches a fresh token, which fixes the most likely transient cause.
      this.error.set('Something went wrong checking you in. Please try again.');
    } finally {
      this.submitting.set(false);
    }
  }
}

/**
 * Builds an obviously-fake ticket number like <c>A-042</c>. Intentionally random and local — no court data, no
 * server call — matching the seed style ("Ticket A-042") the rest of the POC uses.
 */
function suggestTicketNumber(): string {
  const letter: string = String.fromCharCode(65 + Math.floor(Math.random() * 26)); // A–Z
  const number: string = String(Math.floor(Math.random() * 1000)).padStart(3, '0'); // 000–999
  return `${letter}-${number}`;
}
