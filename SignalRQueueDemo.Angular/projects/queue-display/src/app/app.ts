import { Component, inject, OnInit } from '@angular/core';
import { QueueHubService } from 'shared';
import { NowServing } from './now-serving/now-serving';
import { WaitingList } from './waiting-list/waiting-list';

/**
 * The public waiting-room board shell — a full-screen read-only display of who is being served and who is still
 * waiting. Starts {@link QueueHubService} at the shell level so the reconnect/catch-up machinery persists across
 * the component's entire lifetime, and passes the live queue snapshot to focused child components (
 * {@link NowServing}, {@link WaitingList}) that derive just the entries they need to render.
 *
 * <p>This shell deliberately holds no state beyond starting the hub — state management is delegated to {@link
 * QueueHubService}'s signals and the child components' computed derivations, which is the zoneless (no zone.js)
 * signal-driven pattern all three apps in this workspace follow. Court privacy constraint: this board displays
 * only ticket numbers, never names or any identifying information.</p>
 */
@Component({
  selector: 'app-root',
  imports: [NowServing, WaitingList],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly hub = inject(QueueHubService);

  protected readonly snapshot = this.hub.snapshot;
  protected readonly isPolling = this.hub.isPolling;

  ngOnInit(): void {
    void this.hub.start();
  }
}
