import {
  Component,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { CheckInResponse, QueueHubService, QueueStatus } from 'shared';
import { DocumentUpload } from '../document-upload/document-upload';

/**
 * How long the "you're all done" screen stays up before the kiosk auto-resets to a blank form. Long enough for
 * the visitor to read the confirmation, short enough that a shared lobby device is ready for the next person
 * quickly. A visible countdown (and a manual button) means the reset is never a surprise.
 */
const AUTO_RESET_SECONDS = 10;

/**
 * The live "you are #N in line" card for one checked-in person. The device shows one of these per person being
 * tracked (see the app shell) — checking someone else in adds another card rather than replacing this one, so a
 * visitor never loses sight of their own place to check in a family member. This is the payoff for the whole
 * reconnect/catch-up machinery in the shared {@link QueueHubService}: it binds straight to that service's signals,
 * so each card's position re-computes on every live push, on catch-up after a dropped connection, and on each
 * polling tick — the component contains no socket logic of its own, which is exactly the reuse the shared
 * QueueHubService was built to enable, and that the sibling staff/display apps lean on the same way.
 *
 * <p>The position is derived from the authoritative queue snapshot, not from the one-shot {@link CheckInResponse}
 * position, so it stays correct as people ahead are served. That initial response position is used only as a
 * seed for the brief window before the snapshot first reflects this visitor's own entry.</p>
 */
@Component({
  selector: 'app-position-view',
  imports: [DocumentUpload],
  templateUrl: './position-view.html',
  styleUrl: './position-view.css',
})
export class PositionView implements OnDestroy {
  private readonly hub = inject(QueueHubService);

  /** The result of this visitor's check-in — supplies the entry id to track and the seed position. */
  readonly checkIn = input.required<CheckInResponse>();

  /** Emitted when this person should stop being tracked on the device — either they reached Completed and the auto-reset elapsed, or the visitor tapped "Stop tracking". The shell removes just this entry from its tracked list; other people being tracked on the same device are unaffected. */
  readonly finished = output<void>();

  /** Surfaced so the template can show an honest "reconnecting…" note when the socket has dropped to polling. */
  protected readonly isPolling = this.hub.isPolling;

  /** Seconds remaining on the post-completion auto-reset countdown; only meaningful once {@link status} is Completed. */
  protected readonly secondsUntilReset = signal(AUTO_RESET_SECONDS);

  /** Re-exported for template comparisons against {@link status}. */
  protected readonly QueueStatus = QueueStatus;

  /** This visitor's entry as it currently appears in the live snapshot, or null in the brief pre-snapshot window. */
  protected readonly myEntry = computed(() => {
    const id: string = this.checkIn().entryId;
    return this.hub.snapshot()?.queue.find((entry) => entry.id === id) ?? null;
  });

  /** The visitor's current queue status, or null until their entry shows up in a snapshot. */
  protected readonly status = computed<QueueStatus | null>(() => this.myEntry()?.status ?? null);

  /**
   * The visitor's 1-based place among everyone still Waiting, recomputed live. Falls back to the check-in
   * response's seed position only while the snapshot doesn't yet contain this entry, so the very first render
   * after check-in shows a number rather than a flash of "finding your place".
   */
  protected readonly position = computed<number | null>(() => {
    const snapshot = this.hub.snapshot();
    const me = this.myEntry();
    if (snapshot === null || me === null) {
      return this.checkIn().position;
    }

    if (me.status !== QueueStatus.Waiting) {
      return null;
    }

    // Order by check-in time (parsed, not lexical string compare — see QueueEntry.checkedInAt: it's a wire
    // string, and a catch-up-derived snapshot doesn't guarantee server order, so this component sorts for
    // itself). This visitor's index among the still-Waiting entries, 1-based, is their position in line.
    const waiting = snapshot.queue
      .filter((entry) => entry.status === QueueStatus.Waiting)
      .sort((a, b) => new Date(a.checkedInAt).getTime() - new Date(b.checkedInAt).getTime());
    return waiting.findIndex((entry) => entry.id === me.id) + 1;
  });

  /**
   * Guards the auto-reset from starting twice. A plain field, not a signal: the effect below must not re-run when
   * this flips (that would re-read nothing new), and nothing renders from it.
   */
  private completionHandled = false;
  private resetHandle: ReturnType<typeof setInterval> | null = null;

  /**
   * Watches for this visitor's entry reaching Completed and, once, kicks off the visible auto-reset countdown.
   * Kept as an effect (not, say, logic inside the position computed) because it has a side effect — a timer — and
   * only the status transition, nothing rendered, should drive it.
   */
  private readonly autoResetOnCompletion = effect(() => {
    if (this.status() === QueueStatus.Completed && !this.completionHandled) {
      this.completionHandled = true;
      this.startAutoReset();
    }
  });

  ngOnDestroy(): void {
    this.clearAutoReset();
  }

  /** Manual "stop tracking this person" — clears any running auto-reset and asks the shell to drop just this card. */
  protected startOver(): void {
    this.clearAutoReset();
    this.finished.emit();
  }

  private startAutoReset(): void {
    this.secondsUntilReset.set(AUTO_RESET_SECONDS);
    this.resetHandle = setInterval(() => {
      const remaining: number = this.secondsUntilReset() - 1;
      this.secondsUntilReset.set(remaining);
      if (remaining <= 0) {
        this.clearAutoReset();
        this.finished.emit();
      }
    }, 1_000);
  }

  private clearAutoReset(): void {
    if (this.resetHandle !== null) {
      clearInterval(this.resetHandle);
      this.resetHandle = null;
    }
  }
}
