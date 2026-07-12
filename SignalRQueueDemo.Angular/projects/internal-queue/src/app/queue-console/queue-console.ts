import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { QueueApiService, QueueHubService, QueueStatus } from 'shared';
import { DocumentViewer } from '../document-viewer/document-viewer';

/**
 * The live staff queue management console: staff see the current queue divided into Waiting and Serving
 * sections, can call the next person in line, and mark entries as complete. Documents uploaded during check-in
 * can be viewed inline.
 *
 * <p>All queue state is derived from {@link QueueHubService}'s signals via {@link computed}, so the view stays
 * in sync with live `QueueUpdated` pushes, catch-up after reconnect, and polling-fallback updates — exactly
 * matching how the public-checkin kiosk and queue-display board work. Staff see one additional piece of
 * information (the `servedBy` display name) that the public board deliberately hides, modeling the
 * internal-vs-public trust boundary.</p>
 *
 * <p><b>Error handling on API calls:</b> A `callNext` or `complete` call failing with `401` means the staff
 * key has become invalid (in the mock-auth model; in production, the Entra ID token expired). The component
 * emits {@link authError} so the shell signs the user out to the sign-in screen — the only recovery, since
 * nothing here works without a valid key. Any other failure is transient and surfaces as an inline retry
 * message instead.</p>
 */
@Component({
  selector: 'app-queue-console',
  imports: [DocumentViewer],
  templateUrl: './queue-console.html',
  styleUrl: './queue-console.css',
})
export class QueueConsole {
  private readonly hub = inject(QueueHubService);
  private readonly api = inject(QueueApiService);

  /** The staff key for authenticated API calls. */
  readonly staffKey = input.required<string>();

  /**
   * Emitted when a staff action is rejected as unauthorized (401) — the key is wrong or, in a real Entra ID
   * deployment, the token expired. The shell listens for this and signs the user out back to the sign-in screen,
   * since nothing in the console works without a valid key and re-entering it is the only recovery.
   */
  readonly authError = output<void>();

  /** Surfaced so the template can show an honest "Reconnecting…" indicator. */
  protected readonly isPolling = this.hub.isPolling;

  /** Re-exported for template status comparisons. */
  protected readonly QueueStatus = QueueStatus;

  /** Waiting entries, sorted by check-in time ascending (oldest first). */
  protected readonly waitingEntries = computed(() => {
    const snapshot = this.hub.snapshot();
    if (!snapshot) return [];

    return snapshot.queue
      .filter((entry) => entry.status === QueueStatus.Waiting)
      .sort((a, b) => new Date(a.checkedInAt).getTime() - new Date(b.checkedInAt).getTime());
  });

  /** Serving entries — should normally be just one, but the code handles multiple. */
  protected readonly servingEntries = computed(() => {
    const snapshot = this.hub.snapshot();
    if (!snapshot) return [];

    return snapshot.queue.filter((entry) => entry.status === QueueStatus.Serving);
  });

  /** Total count of waiting entries, from the current snapshot. */
  protected readonly totalWaiting = computed(() => this.hub.snapshot()?.totalWaiting ?? 0);

  /** The entry currently selected for document viewing, or null if none selected. */
  protected readonly selectedEntryId = signal<string | null>(null);

  /** True while a callNext or complete request is in flight; disables buttons. */
  protected readonly actioning = signal(false);

  /** Error message from the last failed action (callNext/complete), or null. Cleared when a new action starts. */
  protected readonly actionError = signal<string | null>(null);

  /**
   * Calls the next person in line. Moves the oldest Waiting entry to Serving; all connected frontends see
   * this change live via the hub's broadcast. Disables while a call is in flight. On 401, surfaces a
   * "check your key" error and the shell signs the user out.
   */
  protected async callNext(): Promise<void> {
    if (this.actioning() || this.totalWaiting() === 0) {
      return;
    }

    this.actioning.set(true);
    this.actionError.set(null);

    try {
      await new Promise<void>((resolve, reject) => {
        this.api.callNext(this.staffKey()).subscribe({
          next: () => resolve(),
          error: reject,
        });
      });
    } catch (err) {
      this.handleActionError(err, 'Failed to call next. Please try again.');
    } finally {
      this.actioning.set(false);
    }
  }

  /**
   * Marks an entry as complete. Moves it from Serving to Completed; it disappears from the waiting and serving
   * lists immediately (the board only shows Waiting + Serving). Disables while a call is in flight. On 401,
   * surfaces a "check your key" error.
   */
  protected async complete(entryId: string): Promise<void> {
    if (this.actioning()) {
      return;
    }

    this.actioning.set(true);
    this.actionError.set(null);

    try {
      await new Promise<void>((resolve, reject) => {
        this.api.complete(entryId, this.staffKey()).subscribe({
          next: () => resolve(),
          error: reject,
        });
      });

      // Deselect the entry if it was selected for document viewing.
      if (this.selectedEntryId() === entryId) {
        this.selectedEntryId.set(null);
      }
    } catch (err) {
      this.handleActionError(err, 'Failed to complete entry. Please try again.');
    } finally {
      this.actioning.set(false);
    }
  }

  protected selectEntry(entryId: string): void {
    this.selectedEntryId.set(entryId);
  }

  /**
   * Routes a failed action: a 401 means the key is no longer valid, so bubble {@link authError} up to the shell
   * to sign out (no point showing an inline message the user can't act on from here). Anything else is a
   * transient failure the staff member can retry, so it surfaces as an inline message instead.
   */
  private handleActionError(err: unknown, fallbackMessage: string): void {
    if (isUnauthorized(err)) {
      this.authError.emit();
      return;
    }

    this.actionError.set(fallbackMessage);
  }
}

/**
 * True when an HttpClient failure is a 401. Checks {@link HttpErrorResponse.status}, not the message string:
 * Angular's `HttpErrorResponse` is not a subclass of `Error`, so an `instanceof Error` / message-substring test
 * silently never matches — the status code is the only reliable signal.
 */
function isUnauthorized(err: unknown): boolean {
  return err instanceof HttpErrorResponse && err.status === 401;
}
