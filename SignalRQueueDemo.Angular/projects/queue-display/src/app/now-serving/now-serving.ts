import { Component, computed, inject } from '@angular/core';
import { QueueHubService, QueueStatus } from 'shared';

/**
 * Displays the entries currently being served — those with `status === QueueStatus.Serving` from the hub's live
 * snapshot. Derived via a simple filter, never mutable fields (this workspace is zoneless — no zone.js — so
 * {@link computed} drives the view instead of imperative property updates).
 *
 * <p><strong>Court privacy: ticket number only.</strong> This board displays only the {@link
 * ticketNumber} field, never {@link displayName} — court constraint mandates that no names appear on a public
 * waiting-room display. Any attempt to render {@link displayName} would be an oversight, not a feature.</p>
 */
@Component({
  selector: 'app-now-serving',
  imports: [],
  templateUrl: './now-serving.html',
  styleUrl: './now-serving.css',
})
export class NowServing {
  private readonly hub = inject(QueueHubService);

  /** Re-exported for template comparisons. */
  protected readonly QueueStatus = QueueStatus;

  /**
   * All entries with status `Serving` from the live queue snapshot. Recomputes on every live push, catch-up
   * replay, or polling tick — the snapshot signal that drives it is updated from all three transports.
   */
  protected readonly servingEntries = computed(() => {
    const snapshot = this.hub.snapshot();
    if (!snapshot) {
      return [];
    }

    return snapshot.queue.filter((entry) => entry.status === QueueStatus.Serving);
  });
}
