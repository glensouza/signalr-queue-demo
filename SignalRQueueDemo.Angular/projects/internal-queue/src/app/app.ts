import { Component, inject, OnInit } from '@angular/core';
import { QueueHubService } from 'shared';

/**
 * Shell + smoke page for the internal-queue app (#10 builds the real staff console here). Exists in this issue
 * (#8) only to prove the shared QueueHubService actually delivers live `QueueUpdated` events end-to-end against
 * a running `aspire run` stack — see the acceptance criteria on issue #8.
 */
@Component({
  selector: 'app-root',
  imports: [],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  protected readonly hub = inject(QueueHubService);

  ngOnInit(): void {
    void this.hub.start();
  }
}
