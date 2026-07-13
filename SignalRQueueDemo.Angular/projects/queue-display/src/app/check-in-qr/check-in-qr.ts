import { Component, inject, signal } from '@angular/core';
import { QueueApiService, RuntimeConfigService } from 'shared';

/**
 * The "check in here" call-to-action on the waiting-room board: a QR code plus the plain URL of the public-checkin
 * kiosk, so a visitor can start a check-in from their own phone instead of queuing for a physical kiosk.
 *
 * <p>Both the URL text and the QR image come from the backend API, which is the single source of the public-checkin
 * address (it's the only place `PublicCheckinUrl` is configured): {@link checkInUrl} is fetched from
 * `GET /checkin/url` and {@link qrImageUrl} points an `<img>` at `GET /checkin/qr`. Nothing about the check-in
 * app's address is injected into this board's own config.json anymore — it only needs `apiBaseUrl`.</p>
 *
 * <p>When the API reports no check-in URL configured (404) the fetch errors and {@link checkInUrl} stays null, so
 * the board simply omits the call-to-action rather than showing a broken link. Note for the POC: that URL is a
 * localhost address (all apps run on the one isolated machine), so the QR is illustrative of the pattern — a phone
 * on a separate network can't reach localhost; a real deployment would inject a routable URL.</p>
 */
@Component({
  selector: 'app-check-in-qr',
  imports: [],
  templateUrl: './check-in-qr.html',
  styleUrl: './check-in-qr.css',
})
export class CheckInQr {
  private readonly config = inject(RuntimeConfigService);
  private readonly api = inject(QueueApiService);

  /**
   * The public-checkin URL, fetched once from the API. Null until it loads, or if the API reports none configured
   * (a 404 errors the request) — the call-to-action stays hidden in both cases.
   */
  protected readonly checkInUrl = signal<string | null>(null);

  constructor() {
    this.api.getCheckInUrl().subscribe({
      next: (url) => this.checkInUrl.set(url),
      error: () => this.checkInUrl.set(null),
    });
  }

  /** The API endpoint that renders the QR image, paired with {@link checkInUrl}. */
  protected get qrImageUrl(): string {
    return `${this.config.get().apiBaseUrl}/checkin/qr`;
  }
}
