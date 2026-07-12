import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { RuntimeConfigService } from 'shared';
import { KioskCheckInService } from './kiosk-check-in.service';

const API_BASE = 'http://localhost:5410';

/** Lets the pending task queue drain one hop so an awaited promise chain (token → gated call) can advance. */
const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

/**
 * Waits for the single request to `url` to be dispatched. The gated call is issued only after the awaited token
 * promise resolves — several async hops later — so a plain `expectOne` right after flushing the token races the
 * request. This polls across task boundaries until it appears (or gives up).
 */
async function waitForRequest(httpMock: HttpTestingController, url: string): Promise<TestRequest> {
  for (let attempt = 0; attempt < 20; attempt++) {
    const matches: TestRequest[] = httpMock.match(url);
    if (matches.length === 1) {
      return matches[0];
    }

    await tick();
  }

  throw new Error(`No request to ${url} was dispatched.`);
}

describe('KioskCheckInService', () => {
  let service: KioskCheckInService;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);

    // The shared services read the API base URL from RuntimeConfigService, so it must be loaded before any call.
    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loadPromise = runtimeConfig.load();
    httpMock.expectOne('/config.json').flush({ apiBaseUrl: API_BASE });
    await loadPromise;

    service = TestBed.inject(KioskCheckInService);
  });

  afterEach(() => httpMock.verify());

  it('fetches a fresh token, then submits the check-in echoing that token', async () => {
    const checkInPromise = service.checkIn({ displayName: 'Jane Test', ticketNumber: 'A-042' });

    // Token first — the whole point of the flow: nothing gated is called before a token is in hand.
    httpMock.expectOne(`${API_BASE}/checkin/token`).flush({ token: 'tok-123' });

    const checkInRequest = await waitForRequest(httpMock, `${API_BASE}/checkin`);
    expect(checkInRequest.request.method).toBe('POST');
    expect(checkInRequest.request.headers.get('X-CheckIn-Token')).toBe('tok-123');
    expect(checkInRequest.request.body).toEqual({
      displayName: 'Jane Test',
      ticketNumber: 'A-042',
    });
    checkInRequest.flush({ entryId: 'e1', position: 3, sequenceNumber: 7, entry: {} });

    await expect(checkInPromise).resolves.toMatchObject({ entryId: 'e1', position: 3 });
  });

  it('fetches a fresh token, then uploads the document echoing that token as multipart form data', async () => {
    const file = new File(['pretend-pdf-bytes'], 'evidence.pdf', { type: 'application/pdf' });
    const uploadPromise = service.uploadDocument('e1', file);

    httpMock.expectOne(`${API_BASE}/checkin/token`).flush({ token: 'tok-456' });

    const uploadRequest = await waitForRequest(httpMock, `${API_BASE}/checkin/e1/documents`);
    expect(uploadRequest.request.method).toBe('POST');
    expect(uploadRequest.request.headers.get('X-CheckIn-Token')).toBe('tok-456');
    expect(uploadRequest.request.body).toBeInstanceOf(FormData);
    uploadRequest.flush({ document: { fileName: 'evidence.pdf' } });

    await expect(uploadPromise).resolves.toMatchObject({ document: { fileName: 'evidence.pdf' } });
  });
});
