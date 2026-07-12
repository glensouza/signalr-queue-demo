import { Component, computed, inject } from '@angular/core';
import { QueueHubService, QueueStatus } from 'shared';
import { toPublicName } from '../public-name';

/**
 * Displays the entries still waiting in line — those with `status === QueueStatus.Waiting` from the hub's live
 * snapshot, sorted by {@link checkedInAt} in ascending order (earliest check-in first). Derived via filter and sort,
 * never mutable fields (this workspace is zoneless — no zone.js — so {@link computed} drives the view instead of
 * imperative property updates).
 *
 * <p><strong>Court privacy: ticket number + partial name.</strong> This board shows the ticket number and the
 * visitor's name masked to first-name-plus-last-initial via {@link toPublicName} (e.g. "Jane T.") — enough for a
 * waiting visitor to spot their own place, without publishing full surnames to the whole room. Render {@link
 * publicName}, not the raw {@link displayName}, on this public surface.</p>
 *
 * <p><strong>Why sort by parsed timestamp, not lexical string order:</strong> {@link checkedInAt} is an ISO 8601
 * string from the wire (a JSON DateTimeOffset); a catch-up-derived snapshot doesn't guarantee server order, so the
 * component sorts for itself using `new Date(...).getTime()`. See {@link QueueHubService} remarks for why the
 * re-derived snapshot needs this local re-sort.</p>
 */
@Component({
  selector: 'app-waiting-list',
  imports: [],
  templateUrl: './waiting-list.html',
  styleUrl: './waiting-list.css',
})
export class WaitingList {
  private readonly hub = inject(QueueHubService);

  /** Re-exported for template comparisons. */
  protected readonly QueueStatus = QueueStatus;

  /** The public-board name masker (first name + last initial) — bound in the template instead of the raw displayName. */
  protected readonly publicName = toPublicName;

  /**
   * All entries with status `Waiting` from the live queue snapshot, sorted by check-in time ascending (oldest
   * first). Recomputes on every live push, catch-up replay, or polling tick — the snapshot signal that drives it
   * is updated from all three transports.
   */
  protected readonly waitingEntries = computed(() => {
    const snapshot = this.hub.snapshot();
    if (!snapshot) {
      return [];
    }

    const waiting = snapshot.queue.filter((entry) => entry.status === QueueStatus.Waiting);
    // Sort by parsed check-in time, not lexical string order — see class remarks for why.
    waiting.sort((a, b) => new Date(a.checkedInAt).getTime() - new Date(b.checkedInAt).getTime());
    return waiting;
  });
}
