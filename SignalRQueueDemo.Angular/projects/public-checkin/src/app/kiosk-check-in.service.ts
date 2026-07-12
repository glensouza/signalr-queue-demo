import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CheckInRequest, CheckInResponse, DocumentUploadResponse, QueueApiService, QueueUpdated } from 'shared';

/**
 * The public-checkin app's own thin orchestration over {@link QueueApiService} for the two token-gated calls a
 * kiosk visitor makes: submitting a check-in and uploading a supporting document.
 *
 * <p>Why this exists as an app-local service rather than living in the shared library: fetching the check-in
 * token is a <em>UI-flow</em> concern, and how long a token is held is app-specific. The shared
 * {@link QueueApiService} deliberately takes the token per call and stays out of that decision (see its class
 * doc). This service encodes public-checkin's chosen policy in one place — <b>fetch a fresh token immediately
 * before each gated call, never cache one</b> — so the form and upload components don't each re-implement (and
 * risk diverging on) that flow.</p>
 *
 * <p>Why fetch-fresh-every-time and not cache: the server issues short-lived tokens (a few minutes — see
 * <c>CheckInTokenService.TokenLifetime</c>). A kiosk legitimately sits idle between visitors, and a visitor can
 * dwell on the form or pick a file slowly, so any cached token risks being expired at the moment it's finally
 * used. Re-fetching right before the POST costs one cheap round-trip and sidesteps a whole class of
 * "token expired mid-flow" failure. The token is single-use in spirit (issued, echoed once, discarded), so
 * there's nothing to reuse anyway.</p>
 */
@Injectable({ providedIn: 'root' })
export class KioskCheckInService {
  private readonly api = inject(QueueApiService);

  /**
   * Fetches a fresh check-in token, then submits the check-in with it. Returns the created entry (id + initial
   * position) so the caller can switch to the live position view. Rejects if either the token issue or the
   * check-in POST fails; the caller surfaces that to the visitor.
   */
  async checkIn(request: CheckInRequest): Promise<CheckInResponse> {
    const token: string = await this.issueToken();
    return firstValueFrom(this.api.checkIn(request, token));
  }

  /**
   * Fetches a fresh check-in token, then uploads one document against an existing entry. The same token type
   * gates both check-in and upload on the server (see <c>CheckInTokenFilter</c>), so the flow is identical.
   */
  async uploadDocument(entryId: string, file: File): Promise<DocumentUploadResponse> {
    const token: string = await this.issueToken();
    return firstValueFrom(this.api.uploadDocument(entryId, file, token));
  }

  /**
   * Fetches a fresh check-in token, then cancels an existing entry (the kiosk "stop tracking" action). The same
   * token type gates check-in, upload, and cancel on the server (see <c>CheckInTokenFilter</c>), so the flow is
   * identical — a visitor abandoning the line is on the same public, no-staff-auth path as checking in.
   */
  async cancel(entryId: string): Promise<QueueUpdated> {
    const token: string = await this.issueToken();
    return firstValueFrom(this.api.cancel(entryId, token));
  }

  private async issueToken(): Promise<string> {
    const response = await firstValueFrom(this.api.getCheckInToken());
    return response.token;
  }
}
