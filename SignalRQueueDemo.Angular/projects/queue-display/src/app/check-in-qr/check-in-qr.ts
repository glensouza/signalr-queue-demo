import { Component, inject } from '@angular/core';
import qrcode from 'qrcode-generator';
import { RuntimeConfigService } from 'shared';

/**
 * The "check in here" call-to-action on the waiting-room board: a QR code plus the plain URL of the public-checkin
 * kiosk, so a visitor can start a check-in from their own phone instead of queuing for a physical kiosk.
 *
 * <p>The QR is generated <strong>locally</strong> with {@link qrcode} (a dependency-free encoder) into an inline
 * image data URL — deliberately not fetched from any QR web service, which the court's no-external-calls constraint
 * forbids and which would leak the URL off-machine besides.</p>
 *
 * <p>The URL itself comes from {@link RuntimeConfig.publicCheckinUrl}, injected into only this app's container at
 * `aspire run` time (see the AppHost + docker/write-runtime-config.sh). When it isn't configured this component
 * renders nothing, so the board simply omits the call-to-action rather than showing a broken link. Note for the
 * POC: that URL is a localhost address (all apps run on the one isolated machine), so the QR is illustrative of the
 * pattern — a phone on a separate network can't reach localhost; a real deployment would inject a routable URL.</p>
 *
 * <p>{@link checkInUrl} and {@link qrDataUrl} are lazy getters rather than field initializers: `RuntimeConfigService.get()`
 * throws until the app-initializer in `provideRuntimeConfig()` has loaded config.json, and a field initializer would run
 * at construction — before Angular guarantees that has happened (e.g. under SSG/prerender, or if a future refactor drops
 * `provideRuntimeConfig` from this app). A lazy read defers the throw risk to render time, matching the pattern used by
 * `QueueApiService.baseUrl`.</p>
 */
@Component({
  selector: 'app-check-in-qr',
  imports: [],
  templateUrl: './check-in-qr.html',
  styleUrl: './check-in-qr.css',
})
export class CheckInQr {
  private readonly config = inject(RuntimeConfigService);

  /** The public-checkin URL to advertise, or null when this deployment didn't configure one (then the CTA is hidden). */
  protected get checkInUrl(): string | null {
    return this.config.get().publicCheckinUrl ?? null;
  }

  /**
   * A QR-code image of {@link checkInUrl} as a GIF data URL. Null when there's no URL. Bound to an `<img src>`,
   * which Angular treats an image data URL as safe for, so no sanitizer bypass is needed.
   */
  protected get qrDataUrl(): string | null {
    const url: string | null = this.checkInUrl;
    return url ? CheckInQr.buildQrDataUrl(url) : null;
  }

  private static buildQrDataUrl(url: string): string {
    // typeNumber 0 = auto-size to the data; 'M' error correction tolerates a scuffed/curled printout while keeping
    // the code compact. createDataURL(cellSize, margin) renders each module as a 6px block with an 8-module quiet
    // zone — big enough to scan across a waiting room, small enough to sit in the board's corner.
    const qr = qrcode(0, 'M');
    qr.addData(url);
    qr.make();
    return qr.createDataURL(6, 8);
  }
}
