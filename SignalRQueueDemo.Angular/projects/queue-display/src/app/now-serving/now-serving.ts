import { Component, computed, inject } from '@angular/core';
import { QueueHubService, QueueStatus } from 'shared';
import { toPublicName } from '../public-name';

/**
 * Displays the entries currently being served — those with `status === QueueStatus.Serving` from the hub's live
 * snapshot. Derived via a simple filter, never mutable fields (this workspace is zoneless — no zone.js — so
 * {@link computed} drives the view instead of imperative property updates).
 *
 * <p><strong>Court privacy: ticket number + partial name.</strong> This board shows the ticket number and the
 * visitor's name masked to first-name-plus-last-initial via {@link toPublicName} (e.g. "Jane T.") — enough for the
 * called person to recognise themselves, without publishing full surnames to everyone in the room. Rendering the
 * raw {@link displayName} here would defeat that masking; go through {@link publicName}.</p>
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

  /** The public-board name masker (first name + last initial) — bound in the template instead of the raw displayName. */
  protected readonly publicName = toPublicName;

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
