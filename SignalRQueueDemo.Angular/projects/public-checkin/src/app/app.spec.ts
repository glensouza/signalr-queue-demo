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

  // Deliberately does not call fixture.detectChanges(): that would run ngOnInit, which starts QueueHubService
  // (real HTTP seed + a SignalR connection attempt) and the child form's provider chain — machinery this
  // shell-level test isn't set up to flush. Creating the component and asserting its initial phase still
  // exercises the App -> shared-service provider wiring and confirms the kiosk starts on the check-in form.
  it('starts on the check-in form (no check-in result yet)', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance as unknown as { checkInResult: () => unknown };
    expect(fixture.componentInstance).toBeTruthy();
    expect(app.checkInResult()).toBeNull();
  });
});
