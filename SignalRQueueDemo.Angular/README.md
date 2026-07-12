# Angular workspace

Three standalone Angular apps sharing one library, all talking to `SignalRQueueDemo.ApiService`. Built for issue #8 of the DASH 2.0 walk-in queue POC — see the root [`README.md`](../README.md) and [`CLAUDE.md`](../CLAUDE.md) for the project's overall goals and standards. This workspace is an Angular CLI ("ng new --no-create-application") multi-project workspace, not a single app — every command below takes a project name.

## Layout

| Project | Path | Type | Purpose |
|---|---|---|---|
| `shared` | `projects/shared` | library | TypeScript mirrors of `SignalRQueueDemo.Contracts`, the runtime-config loader, `QueueApiService` (REST client), and `QueueHubService` (SignalR + reconnect catch-up + polling fallback). All three apps below depend on it. |
| `public-checkin` | `projects/public-checkin` | application | Kiosk check-in + document upload + live position (#9). **Built** — see [The `public-checkin` app](#the-public-checkin-app) below. |
| `internal-queue` | `projects/internal-queue` | application | Staff call-next console + document viewing (#10). Shell only today. |
| `queue-display` | `projects/queue-display` | application | Public waiting-room board (#11). Shell only today. |

`public-checkin` is a real app now (see below); `internal-queue` and `queue-display` still ship only a "shell" page (see `src/app/app.ts`/`app.html` in each) that starts `QueueHubService` and shows live connection state + queue counts — enough to prove the shared plumbing works end-to-end against a running API. Their real UIs land in #10/#11.

## The `public-checkin` app

The visitor-facing kiosk: check in, watch your live place in line, optionally attach a document. Designed for a visitor's own phone (opened from a QR on the lobby board) as much as a shared lobby terminal — large touch targets, a single centered column, no browser-chrome assumptions, and an auto-reset back to a blank form once a visit completes.

**Component shape (the pattern #10/#11 copy).** The app is a small state machine plus focused child components, all standalone and signal-driven (this workspace is zoneless — no `zone.js` — so signals, not mutable fields, drive change detection):

- `App` — the shell/state machine. Holds one `checkInResult` signal: null → show the form, non-null → show the position view. Starts `QueueHubService` once, at the shell level, so the reconnect/catch-up machinery survives across the phase change.
- `CheckInForm` — name + auto-suggested (editable, obviously-fake) ticket number. Emits the created entry on success.
- `PositionView` — binds straight to `QueueHubService`'s signals to render the live position; auto-resets on completion.
- `DocumentUpload` — validates type/size client-side (mirroring the server) then uploads.
- `KioskCheckInService` — app-local orchestration of the two token-gated calls (check-in, upload): fetch a fresh check-in token immediately before each, never cache one. Lives here, not in `shared`, because token-hold policy is a UI-flow concern the shared `QueueApiService` deliberately stays out of.

**Why position comes from the snapshot, not the check-in response.** `POST /checkin` returns a one-shot position, but people ahead get served while a visitor waits, so `PositionView` recomputes place from the authoritative `QueueHubService` snapshot on every live push / catch-up / poll. The check-in response's position is used only as a seed for the brief window before the snapshot first reflects the new entry.

**Client-side upload validation mirrors the server on purpose.** The allowed content types (`application/pdf`, `image/jpeg`, `image/png`) and 10 MB cap in `DocumentUpload` duplicate `DocumentEndpoints` on the API. The server stays the source of truth (it re-validates every upload); the client copy exists only so a visitor gets an instant, in-place rejection instead of a round-trip 400. Each constant names the server symbol it shadows so the two are easy to keep in step.

See the root [`README.md`](../README.md#running-the-public-checkin-kiosk-angular) for the end-to-end manual test script (including the offline reconnect/catch-up check).

## Why a shared library, not copy-paste

The reconnect/catch-up protocol (sequence-number tracking, `GET /queue/since/{seq}` replay, polling fallback) is the single trickiest piece of client logic in this POC — see `QueueHub.cs`'s remarks and `docs/architecture.md`'s sequence diagram for the protocol it implements. Building it once in `shared` and reviewing it once means `public-checkin`, `internal-queue`, and `queue-display` each just call `QueueHubService.start()` and read its signals; none of them re-derive the tricky ordering/catch-up reasoning themselves.

## Type mirroring

`projects/shared/src/lib/models/*.models.ts` hand-mirrors every record in `SignalRQueueDemo.Contracts` (`QueueDomainTypes.cs`, `DocumentDomainTypes.cs`). Each interface's doc comment names the C# type it mirrors — **update both sides together** when a Contracts record changes shape. Hand-written mirroring (not OpenAPI/NSwag generation) was chosen for this POC's size; see `docs/decisions.md` if that ever needs revisiting at a larger scale.

Two wire-format details that aren't obvious from the C# source alone, both driven by ASP.NET Core minimal APIs' default (`JsonSerializerDefaults.Web`) `System.Text.Json` options:
- **Property names are camelCase** on the wire (`displayName`, not `DisplayName`) — no naming policy override exists in `Program.cs`, so the Web defaults apply.
- **Enums serialize as numbers**, not strings (`QueueStatus.Waiting` is `0` over JSON) — no `JsonStringEnumConverter` is registered. `QueueStatus` in `queue.models.ts` is declared with explicit numeric values for exactly this reason; keep it in the same order as the C# enum if that ever changes.

## Runtime configuration (`config.json`), not build-time `environment.ts`

Every app reads its API base URL from `/config.json`, fetched at startup by `RuntimeConfigService` (see its doc comment in `projects/shared/src/lib/config/runtime-config.service.ts`) — **not** from an Angular `environment.ts` baked into the JS bundle at build time. This is deliberate: issue #12 containerizes these apps and has an nginx entrypoint script overwrite `config.json` from an environment variable at container *start*, once Aspire has actually assigned the API's address. A build-time value would freeze in an address that doesn't exist yet at `docker build` time, making the image environment-specific. Each app ships a placeholder `public/config.json` (copied into `dist/<app>/browser/config.json` by the Angular build) pointing at the API's local dev port:

```json
{ "apiBaseUrl": "http://localhost:5410" }
```

Change that file locally if you run the API on a different port; #12 replaces this mechanism for the containerized path, it doesn't remove it.

## Running locally against `aspire run`

The API needs to be reachable first — either `aspire run` from the repo root, or standalone: `dotnet run --project ../SignalRQueueDemo.ApiService` (listens on `http://localhost:5410` by the `http` launch profile). Then, from this `SignalRQueueDemo.Angular/` directory:

```bash
npm ci
npm run start:public-checkin    # or start:internal-queue / start:queue-display
```

Each `start:*` script builds `shared` in development mode first (apps import it via the `shared` TypeScript path mapping to `dist/shared` — see `tsconfig.json` — so `shared` must be built, not just source-present, before an app can compile against it), then runs `ng serve` for that one app on `http://localhost:4200`. Only run one app at a time locally — they'd otherwise collide on the same dev-server port.

The API's `Cors:AllowedOrigins` (`SignalRQueueDemo.ApiService/appsettings.json`) is seeded with `http://localhost:4200` for exactly this reason — see the root README's Security model section for why CORS is applied to every browser-reachable surface, including the SignalR hub.

## Building the whole workspace

```bash
npm ci
npm run build
```

Builds `shared` first, then all three apps in sequence (same dependency reason as `start:*` above) — output lands in `dist/shared`, `dist/public-checkin`, `dist/internal-queue`, `dist/queue-display`. This is what issue #8's acceptance criteria (`npm ci && npm run build` succeeds) verifies.

## Testing

```bash
ng test shared            # or public-checkin / internal-queue / queue-display
```

Bare `ng test` (no project name) is ambiguous across four projects in this workspace, same reasoning as `ng build`/`ng serve` above — always pass a project name.
