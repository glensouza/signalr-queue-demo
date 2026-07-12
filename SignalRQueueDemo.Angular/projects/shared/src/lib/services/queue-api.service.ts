import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { RuntimeConfigService } from '../config/runtime-config.service';
import { DocumentListResponse, DocumentUploadResponse } from '../models/document.models';
import {
  CheckInRequest,
  CheckInResponse,
  CheckInTokenResponse,
  QueueChangesSinceResponse,
  QueueStateResponse,
  QueueUpdated,
} from '../models/queue.models';

/** Header names the API's endpoint filters check — see StaffAuthFilter.cs and CheckInTokenFilter.cs. Kept as constants, not inline string literals, so a typo on either side of a call site fails at compile time via the imported symbol, not silently at runtime. */
const STAFF_KEY_HEADER = 'X-Staff-Key';
const CHECK_IN_TOKEN_HEADER = 'X-CheckIn-Token';

/**
 * Typed REST client for every SignalRQueueDemo.ApiService endpoint — the reference implementation the three
 * Angular apps (#9-#11) share instead of each hand-rolling its own HttpClient calls. Mirrors the endpoint list
 * documented on QueueEndpoints.cs and DocumentEndpoints.cs exactly; if a route changes there, it changes here.
 *
 * <p>Auth is passed in per-call, not held as service state: the staff key (StaffAuthFilter) and check-in token
 * (CheckInTokenFilter) are UI-flow concerns — <c>internal-queue</c> collects the staff key once at "sign-in" and
 * keeps it in memory (#10), <c>public-checkin</c> fetches a fresh check-in token right before each gated call
 * (#9) — and this service has no business deciding how long either is cached or where it lives.</p>
 */
@Injectable({ providedIn: 'root' })
export class QueueApiService {
  private readonly httpClient = inject(HttpClient);
  private readonly runtimeConfig = inject(RuntimeConfigService);

  private get baseUrl(): string {
    return this.runtimeConfig.get().apiBaseUrl;
  }

  /** GET /checkin/token — issues a short-lived token the kiosk must echo back via {@link checkIn} / {@link uploadDocument}. */
  getCheckInToken(): Observable<CheckInTokenResponse> {
    return this.httpClient.get<CheckInTokenResponse>(`${this.baseUrl}/checkin/token`);
  }

  /** POST /checkin — creates a queue entry. `checkInToken` must be a value freshly issued by {@link getCheckInToken}. */
  checkIn(request: CheckInRequest, checkInToken: string): Observable<CheckInResponse> {
    return this.httpClient.post<CheckInResponse>(`${this.baseUrl}/checkin`, request, {
      headers: { [CHECK_IN_TOKEN_HEADER]: checkInToken },
    });
  }

  /**
   * GET /staff/verify — succeeds (204) if `staffKey` matches `StaffAuth:Key`, errors with 401 otherwise. Lets the
   * internal-queue sign-in screen reject a wrong key immediately rather than storing it and failing on the first
   * real staff action. Read-only and side-effect-free — it only exercises the same {@link StaffAuthFilter} the
   * mutating staff endpoints do. Emits once (void) on success; the caller distinguishes a 401 (wrong key) from
   * other failures (e.g. API unreachable) via the {@link HttpErrorResponse} status.
   */
  verifyStaffKey(staffKey: string): Observable<void> {
    return this.httpClient.get<void>(`${this.baseUrl}/staff/verify`, {
      headers: { [STAFF_KEY_HEADER]: staffKey },
    });
  }

  /** POST /queue/call-next — moves the oldest Waiting entry to Serving. `staffKey` must match `StaffAuth:Key`. */
  callNext(staffKey: string): Observable<QueueUpdated> {
    return this.httpClient.post<QueueUpdated>(
      `${this.baseUrl}/queue/call-next`,
      {},
      { headers: { [STAFF_KEY_HEADER]: staffKey } },
    );
  }

  /** POST /queue/{id}/complete — moves a Serving entry to Completed. */
  complete(id: string, staffKey: string): Observable<QueueUpdated> {
    return this.httpClient.post<QueueUpdated>(
      `${this.baseUrl}/queue/${encodeURIComponent(id)}/complete`,
      {},
      { headers: { [STAFF_KEY_HEADER]: staffKey } },
    );
  }

  /** POST /queue/{id}/cancel — cancels an entry in the queue. */
  cancel(id: string): Observable<QueueUpdated> {
    return this.httpClient.post<QueueUpdated>(
      `${this.baseUrl}/queue/${encodeURIComponent(id)}/cancel`,
      {}
    );
  }

  /** GET /queue — current queue snapshot + latest sequence number. Used both for a first-load view and as the polling fallback's request when QueueHubService can't hold a SignalR connection open. */
  getQueue(): Observable<QueueStateResponse> {
    return this.httpClient.get<QueueStateResponse>(`${this.baseUrl}/queue`);
  }

  /** GET /queue/since/{sequenceNumber} — the REST half of the reconnect/catch-up protocol; see QueueHubService for the client-side half. */
  getChangesSince(sequenceNumber: number): Observable<QueueChangesSinceResponse> {
    return this.httpClient.get<QueueChangesSinceResponse>(`${this.baseUrl}/queue/since/${sequenceNumber}`);
  }

  /**
   * POST /checkin/{id}/documents — uploads a supporting document against a queue entry. `file` is sent as the
   * `file` multipart field the server's manual `ReadFormAsync` expects (see DocumentEndpoints.cs); client-side
   * type/size validation mirroring the server's allowlist/cap is the calling app's job (#9), not this service's —
   * the server is the source of truth either way, so duplicating its exact limits here would just be one more
   * place for them to drift.
   */
  uploadDocument(id: string, file: File, checkInToken: string): Observable<DocumentUploadResponse> {
    const formData = new FormData();
    formData.append('file', file);
    return this.httpClient.post<DocumentUploadResponse>(
      `${this.baseUrl}/checkin/${encodeURIComponent(id)}/documents`,
      formData,
      { headers: { [CHECK_IN_TOKEN_HEADER]: checkInToken } },
    );
  }

  /** GET /queue/{id}/documents — lists documents uploaded against an entry (metadata only). Staff-gated. */
  listDocuments(id: string, staffKey: string): Observable<DocumentListResponse> {
    return this.httpClient.get<DocumentListResponse>(`${this.baseUrl}/queue/${encodeURIComponent(id)}/documents`, {
      headers: { [STAFF_KEY_HEADER]: staffKey },
    });
  }

  /**
   * GET /queue/{id}/documents/{docId} — fetches a single document's content as a Blob. Returns the Blob (not a
   * bare URL) because the endpoint requires the `X-Staff-Key` header, which a plain `<a href>`/`<img src>` can't
   * attach; the caller is expected to build an object URL from the Blob (`URL.createObjectURL`) and revoke it
   * when the viewer closes.
   */
  getDocument(id: string, docId: string, staffKey: string): Observable<Blob> {
    return this.httpClient.get(`${this.baseUrl}/queue/${encodeURIComponent(id)}/documents/${encodeURIComponent(docId)}`, {
      headers: { [STAFF_KEY_HEADER]: staffKey },
      responseType: 'blob',
    });
  }
}
