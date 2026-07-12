import { Component, OnInit, inject, signal } from '@angular/core';
import { QueueHubService } from 'shared';
import { StaffSessionService } from './staff-session.service';
import { StaffSignIn } from './staff-sign-in/staff-sign-in';
import { QueueConsole } from './queue-console/queue-console';

/**
 * The staff console shell and its state machine. A staff member's session is exactly two phases — sign in with
 * their key, then manage the queue. The shell holds a single signal tracking whether they're signed in; null means
 * "show the sign-in form", non-null (the staff key) means "show the console". This keeps the phase logic in one
 * obvious place and lets the two child components stay focused (the form only knows how to collect input; the
 * console only knows how to display and action the queue).
 *
 * <p>{@link QueueHubService} is started once here, at the shell level, not per phase: it's a root singleton
 * whose whole value is surviving across reconnects, so tearing it down and restarting on every phase change would
 * defeat the catch-up protocol it exists for. Started eagerly so live updates are flowing by the time a staff
 * member signs in and switches to the console.</p>
 */
@Component({
  selector: 'app-root',
  imports: [StaffSignIn, QueueConsole],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly hub = inject(QueueHubService);
  protected readonly session = inject(StaffSessionService);

  /** The signed-in staff member's key, or null before sign-in / after sign-out. Drives which phase renders. */
  protected readonly staffKey = this.session.staffKey;

  ngOnInit(): void {
    void this.hub.start();
  }

  protected onSignedIn(): void {
    // Staff key is already in the session service, set by the sign-in form.
  }
}
