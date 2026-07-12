import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CheckInResponse, QueueHubService } from 'shared';
import { CheckInForm } from './check-in-form/check-in-form';
import { PositionView } from './position-view/position-view';

/**
 * The public-checkin kiosk shell and its state machine. Unlike a one-person-at-a-time kiosk, this device can track
 * several people at once: {@link checkIns} holds every check-in made on it, and the tracker renders one live
 * {@link PositionView} per person. Checking someone else in <em>appends</em> to that list — a visitor watching
 * their own place can check in a family member and keep both positions on screen, instead of the second check-in
 * replacing the first (which is the "I lost my spot" trap a single-slot shell would have).
 *
 * <p>Two phases, driven by {@link showForm}: show the check-in form when nobody is tracked yet <em>or</em> when the
 * visitor tapped "check someone else in" ({@link addingAnother}); otherwise show the tracker. A person leaves the
 * tracker either by tapping "stop tracking" or by completing (their card's auto-reset fires {@link PositionView.finished}).</p>
 *
 * <p>{@link QueueHubService} is started once here, at the shell level, not per phase or per card: it's a root
 * singleton whose whole value is surviving across reconnects, so every PositionView reads the same live snapshot
 * from it, and tearing it down on a phase change would defeat the catch-up protocol it exists for.</p>
 */
@Component({
  selector: 'app-root',
  imports: [CheckInForm, PositionView],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly hub = inject(QueueHubService);

  /** Every check-in made on this device, each rendered as its own live-tracked card. Empty before the first check-in. */
  protected readonly checkIns = signal<CheckInResponse[]>([]);

  /** True while the visitor is adding another person on top of an existing tracked list — keeps the list alive behind the form. */
  protected readonly addingAnother = signal(false);

  /** Show the form when there's no one to track yet, or when explicitly adding another; otherwise show the tracker. */
  protected readonly showForm = computed(() => this.checkIns().length === 0 || this.addingAnother());

  ngOnInit(): void {
    void this.hub.start();
  }

  /** Append (never replace) so checking in a second person keeps the first person's live position on screen. */
  protected onCheckedIn(result: CheckInResponse): void {
    this.checkIns.update((list) => [...list, result]);
    this.addingAnother.set(false);
  }

  /** Reveal the form to add another person without discarding anyone already tracked. */
  protected checkAnother(): void {
    this.addingAnother.set(true);
  }

  /** Abandon "add another" and return to the tracker — only offered when at least one person is already tracked. */
  protected cancelAdding(): void {
    this.addingAnother.set(false);
  }

  /** Stop tracking one person (manual "stop tracking", or their post-completion auto-reset) without touching the rest. */
  protected onRemove(entryId: string): void {
    this.checkIns.update((list) => list.filter((result) => result.entryId !== entryId));
  }
}
