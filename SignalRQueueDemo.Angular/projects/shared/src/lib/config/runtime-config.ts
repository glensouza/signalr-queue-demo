/**
 * The one piece of environment-specific configuration every app in this workspace needs: where the API lives.
 * Deliberately NOT an Angular `environment.ts` file (the CLI's usual build-time config pattern) — see
 * RuntimeConfigService for why this has to be resolved after the JS bundle loads, not compiled into it.
 */
export interface RuntimeConfig {
  /**
   * Origin (scheme + host + port, no trailing slash) of SignalRQueueDemo.ApiService — used as the base for both
   * QueueApiService's REST calls and QueueHubService's `/hubs/queue` connection. e.g. "http://localhost:5410".
   */
  readonly apiBaseUrl: string;
}
