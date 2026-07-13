# Angular workspace

Three standalone Angular apps sharing one library, all talking to `SignalRQueueDemo.ApiService`. Built for issue #8 of the DASH 2.0 walk-in queue POC — see the root [`README.md`](../README.md) and [`CLAUDE.md`](../CLAUDE.md) for the project's overall goals and standards. This workspace is an Angular CLI ("ng new --no-create-application") multi-project workspace, not a single app — every command below takes a project name.

## Layout

| Project | Path | Type | Purpose |
|---|---|---|---|
| `shared` | `projects/shared` | library | TypeScript mirrors of `SignalRQueueDemo.Contracts`, the runtime-config loader, `QueueApiService` (REST client), and `QueueHubService` (SignalR + reconnect catch-up + polling fallback). All three apps below depend on it. |
| `public-checkin` | `projects/public-checkin` | application | Kiosk check-in + document upload + live position (#9). **Built** — see [The `public-checkin` app](#the-public-checkin-app) below. |
| `internal-queue` | `projects/internal-queue` | application | Staff call-next console + live queue + document viewing (#10). **Built** — see [The `internal-queue` app](#the-internal-queue-app) below. |
| `queue-display` | `projects/queue-display` | application | Public waiting-room board (#11). **Built** — see [The `queue-display` app](#the-queue-display-app) below. |

All three apps are real now (see below).

## The `public-checkin` app

The visitor-facing kiosk: check in, watch your live place in line, optionally attach a document. Designed for a visitor's own phone (opened from a QR on the lobby board) as much as a shared lobby terminal — large touch targets, a single centered column, no browser-chrome assumptions, and an auto-reset back to a blank form once a visit completes.

**Component shape (the pattern #10/#11 copy).** The app is a small state machine plus focused child components, all standalone and signal-driven (this workspace is zoneless — no `zone.js` — so signals, not mutable fields, drive change detection):

- `App` — the shell/state machine. Holds one `checkInResult` signal: null → show the form, non-null → show the position view. Starts `QueueHubService` once, at the shell level, so the reconnect/catch-up machinery survives across the phase change.
- `CheckInForm` — name + auto-suggested (editable, obviously-fake) ticket number. Emits the created entry on success.
- `PositionView` — binds straight to `QueueHubService`'s signals to render the live position; auto-resets on completion. "Stop tracking" drops the card locally *and* cancels the entry on the backend (best-effort, fire-and-forget) so the visitor also leaves the staff queue rather than lingering in it.
- `DocumentUpload` — validates type/size client-side (mirroring the server) then uploads.
- `KioskCheckInService` — app-local orchestration of the three token-gated calls (check-in, upload, cancel): fetch a fresh check-in token immediately before each, never cache one. Lives here, not in `shared`, because token-hold policy is a UI-flow concern the shared `QueueApiService` deliberately stays out of.

**Why position comes from the snapshot, not the check-in response.** `POST /checkin` returns a one-shot position, but people ahead get served while a visitor waits, so `PositionView` recomputes place from the authoritative `QueueHubService` snapshot on every live push / catch-up / poll. The check-in response's position is used only as a seed for the brief window before the snapshot first reflects the new entry.

**Client-side upload validation mirrors the server on purpose.** The allowed content types (`application/pdf`, `image/jpeg`, `image/png`) and 10 MB cap in `DocumentUpload` duplicate `DocumentEndpoints` on the API. The server stays the source of truth (it re-validates every upload); the client copy exists only so a visitor gets an instant, in-place rejection instead of a round-trip 400. Each constant names the server symbol it shadows so the two are easy to keep in step.

See the root [`README.md`](../README.md#running-the-public-checkin-kiosk-angular) for the end-to-end manual test script (including the offline reconnect/catch-up check).

## The `internal-queue` app

The staff queue management console: internally-facing, mock-authenticated with a static key (modeling Entra ID in production), lets staff call the next person in line, mark entries complete, and view supporting documents uploaded during check-in. Staff see names and `servedBy` display (internal context); the public board deliberately hides both (privacy boundary).

**Component shape (pattern same as #9).** The app is a state machine plus focused child components, all standalone and signal-driven:

- `App` — the shell/state machine. Holds one `staffKey` signal: null → show the sign-in form, non-null → show the console. Starts `QueueHubService` once, at the shell level, so the reconnect/catch-up machinery survives across the phase change.
- `StaffSignIn` — simple form where staff enters their authentication key once. Stores it **in memory only** (not localStorage/sessionStorage) — a refresh returns to sign-in. This models the production Entra ID boundary and the mock-auth `X-Staff-Key` header; the server's `StaffAuthFilter` validates the key on every staff call.
- `QueueConsole` — the live console. Two sections: Serving (current entries being served, with Complete button and staff member name) and Waiting (all waiting entries sorted by check-in time, position number shown). A "Call Next" button moves the oldest Waiting entry to Serving; all connected frontends see the change live via hub broadcast. Staff can select any entry (Waiting or Serving) to view its uploaded documents inline.
- `DocumentViewer` — list of documents for a selected entry, with inline display (images in `<img>` tags, PDFs in an `<iframe>` with the browser's native viewer). Fetches documents as Blobs (because the `X-Staff-Key` header is required and a plain `<img src>`/`<a href>` can't carry it), builds object URLs via `URL.createObjectURL`, and **revokes them in three places to avoid memory leaks**: when selecting a different document, when the viewed entry changes, and in `ngOnDestroy`. This lifecycle discipline matters on a long-running staff console left open all day.
- `StaffSessionService` — app-local service holding the staff key in memory for the session. Why here, not in `shared`: the in-memory-only policy is a UI-flow concern the shared `QueueApiService` stays out of. See the service's doc comment for the trust-boundary rationale.

**Auth model:** the staff key is passed per-call to `QueueApiService` methods (`callNext`, `complete`, `listDocuments`, `getDocument`), not held as service state — the shared service deliberately takes it as a parameter so each app can decide its own policy (this app: in-memory-only; a real deployment: Entra ID token). On a staff action failing with `401` (wrong/expired key), the console emits an `authError` that the shell listens for and signs the user straight back out to the sign-in screen — the only recovery, since nothing works without a valid key. Any other (transient) failure surfaces as an inline retry message instead.

**Queue state and live updates:** entirely derived from `QueueHubService`'s signals via `computed`, so the console stays in sync with live `QueueUpdated` pushes, catch-up after reconnect, and polling fallback. Staff see one additional data point (`servedBy`) compared to the public board, modeling the internal-vs-public trust boundary.

See the root [`README.md`](../README.md#running-the-internal-queue-staff-console-angular) for the end-to-end manual test script.

## The `queue-display` app

The public waiting-room board: a full-screen TV display showing who is being served and who is still waiting, updated live from the hub. Displays only ticket numbers (never names) — a court privacy constraint. Designed for a public-facing monitor in a courthouse waiting room.

**Component shape.** The app is minimal by design — the whole point is that `QueueHubService` handles reconnect/catch-up once, so this app just reads its signals and renders:

- `App` — the shell. Starts `QueueHubService` once, passes the live snapshot and connection state to child components.
- `NowServing` — filtered computed of entries with `status === QueueStatus.Serving`. Displays only the ticket number from each.
- `WaitingList` — filtered-and-sorted computed of entries with `status === QueueStatus.Waiting`, sorted by check-in time ascending (oldest first). Displays position number and ticket number only — no names.

**Why Completed entries don't appear.** They're naturally excluded by the Waiting + Serving filter; the board only ever shows entries currently in the queue. As soon as an entry moves to Completed, it vanishes.

**No interaction.** This is a pure read-only board, no forms or user input. The only state changes come from the hub and are reflected automatically.

See the root [`README.md`](../README.md#running-the-queue-display-board-angular) for the end-to-end manual test script.

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

Each `start:*` script builds `shared` in development mode first (apps import it via the `shared` TypeScript path mapping to `dist/shared` — see `tsconfig.json` — so `shared` must be built, not just source-present, before an app can compile against it), then runs `ng serve` for that one app. Each app has its own fixed dev-server port (set per app under `serve.options.port` in `angular.json`), so **all three can run at once** in separate terminals — a realistic setup since the kiosk, the staff console, and the public board are meant to be seen side by side:

| App | Command | URL |
|---|---|---|
| `public-checkin` | `npm run start:public-checkin` | `http://localhost:4200` |
| `internal-queue` | `npm run start:internal-queue` | `http://localhost:4201` |
| `queue-display` | `npm run start:queue-display` | `http://localhost:4202` |

The API's `Cors:AllowedOrigins` (`SignalRQueueDemo.ApiService/appsettings.json`) lists all three (`http://localhost:4200`–`4202`) for exactly this reason — see the root README's Security model section for why CORS is applied to every browser-reachable surface, including the SignalR hub.

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
