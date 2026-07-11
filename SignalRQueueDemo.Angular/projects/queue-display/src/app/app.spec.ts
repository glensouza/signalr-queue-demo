import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRuntimeConfig } from 'shared';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRuntimeConfig()],
    }).compileComponents();
  });

  // Deliberately never calls fixture.detectChanges(): that would run ngOnInit, which calls
  // QueueHubService.start() and fires real HTTP/APP_INITIALIZER machinery this shell-level test isn't set up to
  // flush. Creating the component alone is still a meaningful smoke test — it exercises the full
  // App -> QueueHubService -> QueueApiService -> RuntimeConfigService -> HttpClient provider chain, which is
  // exactly the wiring issue #8 needs proven correct.
  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    expect(fixture.componentInstance).toBeTruthy();
  });
});
