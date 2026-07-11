import { HttpClient } from '@angular/common/http';
import { EnvironmentProviders, Injectable, inject, provideAppInitializer } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { RuntimeConfig } from './runtime-config';

/**
 * Loads {@link RuntimeConfig} from `/config.json`, fetched at app startup rather than baked into the JS bundle
 * by an Angular `environment.ts`. This matters specifically because of how issue #12 (Dockerfiles + AppHost
 * wiring) containerizes these apps: each app's API address is only known once Aspire assigns container ports at
 * `aspire run` time, so an nginx entrypoint script overwrites `config.json` from an environment variable before
 * nginx starts serving. A build-time `environment.ts` value would be frozen into the compiled bundle at `docker
 * build` time — before that address exists — so the container image would be environment-specific and unusable
 * across dev/CI/whatever else runs it. Fetching a small JSON file at runtime keeps the image identical across
 * environments; only this one file changes per deployment.
 *
 * `provideRuntimeConfig()` wires this to run via Angular's app-initializer hook, so `RuntimeConfigService.get()`
 * is guaranteed populated before any component or other service that depends on it runs.
 */
@Injectable({ providedIn: 'root' })
export class RuntimeConfigService {
  private readonly httpClient = inject(HttpClient);
  private config: RuntimeConfig | null = null;

  /**
   * Fetches and caches `config.json`. Called once by the app-initializer provider below; safe to call again
   * (e.g. from a test) since it just re-fetches and replaces the cached value.
   */
  async load(): Promise<void> {
    this.config = await firstValueFrom(this.httpClient.get<RuntimeConfig>('/config.json'));
  }

  /**
   * Returns the loaded config. Throws if called before {@link load} resolves — that would mean a consumer read
   * config outside the app-initializer ordering `provideRuntimeConfig()` establishes, which is a wiring bug in
   * that consumer, not a recoverable runtime state worth a fallback value.
   */
  get(): RuntimeConfig {
    if (this.config === null) {
      throw new Error(
        'RuntimeConfigService.get() called before config.json finished loading. ' +
          'Make sure provideRuntimeConfig() is registered in this app\'s providers.',
      );
    }

    return this.config;
  }
}

/**
 * Registers the app-initializer that loads `config.json` before Angular finishes bootstrapping — every app
 * shell in this workspace must include this in its `ApplicationConfig.providers` alongside `provideHttpClient()`.
 */
export function provideRuntimeConfig(): EnvironmentProviders {
  return provideAppInitializer(() => inject(RuntimeConfigService).load());
}
