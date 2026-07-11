import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { RuntimeConfigService } from './runtime-config.service';

describe('RuntimeConfigService', () => {
  let service: RuntimeConfigService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(RuntimeConfigService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('throws when read before load() resolves — a consumer reading config outside provideRuntimeConfig()\'s ordering is a wiring bug, not a recoverable state', () => {
    expect(() => service.get()).toThrowError(/before config\.json finished loading/);
  });

  it('caches the fetched config so get() returns it after load() resolves', async () => {
    const loadPromise = service.load();
    httpMock.expectOne('/config.json').flush({ apiBaseUrl: 'http://localhost:5410' });
    await loadPromise;

    expect(service.get()).toEqual({ apiBaseUrl: 'http://localhost:5410' });
  });
});
