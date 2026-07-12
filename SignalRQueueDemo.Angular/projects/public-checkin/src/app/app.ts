import { Component, OnInit, inject, signal } from '@angular/core';
import { CheckInResponse, QueueHubService } from 'shared';
import { CheckInForm } from './check-in-form/check-in-form';
import { PositionView } from './position-view/position-view';

/**
 * The public-checkin kiosk shell and its state machine. A visitor's session is exactly two phases — fill the
 * form, then watch their live position — so the shell holds a single {@link checkInResult} signal: null means
 * "show the form", non-null means "show the position view for this entry". This keeps the phase logic in one
 * obvious place and lets the two child components stay focused (the form only knows how to check in; the position
 * view only knows how to display and reset), which is the component split the sibling staff and display apps are
 * meant to mirror.
 *
 * <p>{@link QueueHubService} is started once here, at the shell level, not per phase: it's a root singleton whose
 * whole value is surviving across reconnects, so tearing it down and restarting it on every phase change would
 * defeat the catch-up protocol it exists for. Started eagerly so the first live snapshot is already arriving by
 * the time a visitor finishes the form.</p>
 */
@Component({
  selector: 'app-root',
  imports: [CheckInForm, PositionView],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly hub = inject(QueueHubService);

  /** The current visitor's check-in result, or null before check-in / after an auto-reset. Drives which phase renders. */
  protected readonly checkInResult = signal<CheckInResponse | null>(null);

  ngOnInit(): void {
    void this.hub.start();
  }

  protected onCheckedIn(result: CheckInResponse): void {
    this.checkInResult.set(result);
  }

  /** Back to a blank form — either the auto-reset elapsed or the visitor tapped the button in the position view. */
  protected onFinished(): void {
    this.checkInResult.set(null);
  }
}
