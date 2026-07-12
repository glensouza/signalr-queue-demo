# SignalR Queue Demo

Proof-of-concept for the DASH 2.0 walk-in check-in / queue system at San Bernardino Superior Court, backing **ADR-0001: Real-Time Messaging Topology for DASH 2.0 and Walk-In Queue Management** (Status: **Accepted**, 2026-07-09). The ADR lives in the court OneDrive, not this repo — see [Reference](#reference).

This POC will be handed to the vendor dev team as a **reference implementation** — code clarity and comments matter more than cleverness. Targets **.NET 10 LTS** with modern C# (primary constructors, collection expressions, required members). See [CLAUDE.md](CLAUDE.md) for the coding and documentation standards every contributor (human or AI) follows.

## System context

DASH 2.0, built by the Apex Systems dev team (Vikrant Salunkhe, Timothy Meza, Moueen Shaik, Apex Shah, Ravi Ganji) for the court's self-help team, has three parts:

- **Public portal** — public users submit requests and check in (kiosk/tablet), no auth.
- **Internal/support portal** — self-help staff answer questions, manage data, call the next person in line. Entra ID-authenticated.
- **Walk-in queue management** — people line up each morning (~7am), check in, and are served in turn. Built as an independent, reusable component (own database, separate from the DASH internal-portal database, both on one SQL Server instance) so other court departments can plug into it later via connection string.

> **Production vs. POC note:** production confirmed kiosk (check-in) and the public queue display as two pages within one web application. This POC deliberately builds them as **three separate Angular apps** (plus Blazor equivalents) — the goal here is comparing frontend stacks and container orchestration, not mirroring the production deployment shape.

## Real-time architecture decision (ADR-0001)

Three SignalR topologies were evaluated:

| | Option A — two Azure SignalR instances | Option B — one Azure SignalR instance | **Option C — self-hosted (chosen)** |
|---|---|---|---|
| Cost | ~$100/mo, overprovisioned | ~$49/mo | $0 |
| Security | Best — private endpoint on internal | Weakest — shared public exposure | Good — inherits App Service network posture |
| Reliability | Blast-radius isolation | Shared quota/failure domain | Tied to App Service instance health |
| Operational excellence | Two of everything to run | One resource | Nothing extra to run |
| Performance | Far exceeds need | Exceeds need | Sufficient at expected scale (dozens–hundreds concurrent, not thousands) |

**Decision, confirmed 2026-07-09:** start with Option C — SignalR self-hosted inside the App Service, no Azure SignalR Service. The switch to Azure SignalR, if the population grows past a few hundred concurrent connections, is a single line of code (`AddAzureSignalR(connectionString)`) or an app-settings feature flag (`UseAzureSignalR: true/false`) — no hub/client redesign either way.

**Requirements independent of topology:**
- Target **.NET 10 LTS**, not .NET 9 (out of support since May 2026).
- Managed identity, not connection-string secrets, if/when Azure SignalR is adopted — not needed for Option C.
- Decouple the public check-in path from internal-API availability: retry until success — a missed notification means a court visitor never learns it was their turn, so it must never silently drop.
- **Reconnect resiliency:** a client that disconnects and reconnects (internal staff stepping away, kiosk losing wifi) must catch up on what it missed, not just resume live push from that point forward. This is designed into the hub protocol from the start (see below).
- Schema-scoped SQL logins so the public-facing queue API can't read DASH court data (production concern; modeled here by the mock-auth trust boundary).
- Per-environment Azure resource naming convention (in progress — see [Open items](#open-items)).
- Existing notification platform (SMS/email) is available as a fallback when a client can't be reached over SignalR.

**Considered, not actioned for production:** Azure Storage (Table Storage instead of SQL Server, Blob Storage, Storage Queues) would be materially cheaper, but wasn't adopted since the SQL tables/APIs are already built. **This POC demonstrates that path anyway** — Table Storage as a swappable persistence provider and Blob Storage for document upload, both against local emulators — so the team can evaluate it for future low-complexity projects.

## What this POC builds

One shared backend, two frontend implementations (so the team can compare **Angular vs. Blazor**), all orchestrated by a single **.NET Aspire** AppHost.

### 1. `SignalRQueueDemo.ApiService` — shared minimal API + self-hosted SignalR hub

Endpoints (all shapes come from `SignalRQueueDemo.Contracts`):

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /checkin/token` | public (hardened) | Issues a short-lived check-in token a kiosk echoes back on the two POSTs below. |
| `POST /checkin` | public (hardened) | Creates a queue entry — name/ticket number, timestamp, status=`Waiting`. |
| `POST /checkin/{id}/documents` | public (hardened) | Uploads a supporting document to Blob Storage (Azurite locally). |
| `POST /queue/call-next` | mock staff auth | Moves the oldest `Waiting` entry to `Serving`. |
| `POST /queue/{id}/complete` | mock staff auth | Moves a `Serving` entry to `Completed`. |
| `GET /queue` | public (hardened) | Current queue state + latest sequence number. |
| `GET /queue/since/{sequenceNumber}` | public (hardened) | Reconnect catch-up: every change after that sequence number. |
| `GET /queue/{id}/documents` | mock staff auth | Lists documents uploaded against an entry (metadata only). |
| `GET /queue/{id}/documents/{docId}` | mock staff auth | Streams a single uploaded document's content back. |

> **Route correction from the original brief:** the brief specified `POST /queue/{id}/call-next`, but call-next selects the next entry itself — no id belongs in that route. Corrected to `POST /queue/call-next`; `{id}` remains on `complete`, which does act on a specific entry. See [`docs/decisions.md`](docs/decisions.md) for the full rationale, plus why the SQLite repository uses `EnsureCreated` instead of migrations.
>
> **Status:** every endpoint in the table above is implemented, including the auth column — `StaffAuthFilter` gates the mock-staff-auth rows, and a short-lived check-in token gates the two check-in POSTs (`CheckInTokenFilter` on `POST /checkin`, an inline check on the upload), all behind restricted CORS. See [Security model](#security-model) below for what these do and don't protect against. `/checkin`, `/queue/call-next`, `/queue/{id}/complete`, `GET /queue`, and `GET /queue/since/{sequenceNumber}` are implemented against `IQueueRepository`, plus the self-hosted `QueueHub` (mapped at `/hubs/queue`) broadcasting `QueueUpdated`. Two `IQueueRepository` backends are live: `SqliteQueueRepository` (EF Core, `App_Data/queue.db`, git-ignored, default) and `TableStorageQueueRepository` (Azure Table Storage against the Azurite emulator) — see [Flipping the persistence provider](#flipping-the-persistence-provider) below. Document upload/viewing is implemented against a new `IDocumentRepository` (metadata, backed the same way as `IQueueRepository`) plus `DocumentBlobStore` (content, always Azurite Blob Storage) — see [Uploading and viewing documents](#uploading-and-viewing-documents) below.

- **`QueueHub`** (`SignalRQueueDemo.ApiService/Hubs/QueueHub.cs`) broadcasts `QueueUpdated` on every state change and sends `CurrentSequence` on connect so a client always has a baseline. Self-hosted in-process (ADR-0001 Option C), with a feature-flag path to Azure SignalR (below).
- **Reconnect resiliency:** every state change increments a **monotonic sequence number** persisted in a change-event log. Reconnecting clients call `GET /queue/since/{seq}` to replay what they missed — push-only delivery is never relied on. See [`docs/architecture.md`](docs/architecture.md#reconnect--catch-up-protocol) for the full sequence diagram and the push-ordering caveat.
- **Persistence:** behind an `IQueueRepository` interface with two signature-compatible implementations — **SQLite via EF Core** (default) and **Azure Table Storage** (against the Azurite emulator) — selected by config. `SignalRQueueDemo.AppHost` always starts the Azurite Table resource, so flipping the config value at [Flipping the persistence provider](#flipping-the-persistence-provider) is the entire migration, no other code or infrastructure change.
- **Auth model:** restricted CORS + a short-lived HMAC check-in token harden the public check-in path; a static `X-Staff-Key` header models the internal-vs-public trust boundary on staff endpoints — no real Entra ID in the POC. See [Security model](#security-model) below for exactly what's mocked, what's real, and what production must replace.

### 2. `SignalRQueueDemo.Contracts` — shared DTOs/records

Referenced by `ApiService` and `Web` (Blazor). The Angular workspace mirrors the same shapes as TypeScript types.

### 3. Angular workspace (`SignalRQueueDemo.Angular/`) — three apps, one shared library

| App | Purpose |
|---|---|
| `public-checkin` | Kiosk-style check-in form + document upload; shows "you're #N in line" live (SignalR + polling fallback). |
| `internal-queue` | Staff call-next console; live queue; views uploaded documents. |
| `queue-display` | Public waiting-room board (tickets, never names); serving/waiting status; completed entries disappear. |

A shared library holds the API client, the TypeScript contract mirrors, and the SignalR connection service with sequence-number tracking, reconnect catch-up, and polling fallback. Each app gets its own multi-stage `Dockerfile` (Node build stage → nginx static serving) with the API address injected at **runtime** from Aspire service discovery — never baked in at build time.

> **Status:** the workspace, `shared` library, and all three app shells are built (#8) — `npm ci && npm run build` succeeds, and each shell shows live `QueueUpdated` events via `QueueHubService` against a running API (verified against a standalone `dotnet run` of `ApiService`, including a live SignalR client receiving a push after `POST /checkin`). The shared library covers every endpoint on `QueueEndpoints`/`DocumentEndpoints`, mirrors every `SignalRQueueDemo.Contracts` record, and resolves its API base URL from a runtime-fetched `SignalRQueueDemo.Angular/projects/*/public/config.json` rather than a build-time `environment.ts` — see [`SignalRQueueDemo.Angular/README.md`](SignalRQueueDemo.Angular/README.md) for why. The real check-in/staff/display UIs (#9-#11) and the containerized `aspire run` wiring (#12) are still ahead.

### 4. `SignalRQueueDemo.Web` — Blazor Server, same three experiences

Public check-in, internal call-next, and queue display as Blazor pages — self-encapsulated, not a REST client of the API: check-in/call-next/complete call directly into a shared queue-service library also referenced by `SignalRQueueDemo.ApiService`, reusing the same `IQueueRepository` code rather than round-tripping to its own sibling process. A direct SignalR `HubConnection` to `QueueHub` still handles live updates, and doubles as the way Blazor tells the hub to broadcast after one of its own local writes (see [`docs/decisions.md`](docs/decisions.md)). This is the comparison stack: the UI stays simple on purpose, the point is comparing plumbing, not polish.

### 5. `SignalRQueueDemo.AppHost` — orchestrates all of it

`ApiService` and `Web` as project resources; Azurite (Blob + Table) as an emulator resource; the Azure SignalR Emulator only when the feature flag is on; `ServiceDefaults` (OpenTelemetry, health checks, service discovery) on every resource. Goal: **one command brings up everything with zero manual port/URL wiring.**

Each of the three Angular apps is containerized via `builder.AddDockerfile(name, "../SignalRQueueDemo.Angular", "Dockerfile").WithBuildArg("APP_NAME", ...)` — one parameterized `Dockerfile` (see [Containers](#containers-the-angular-apps) below), three resources differing only in which app the build arg selects. Each container gets `API_BASE_URL` injected from `apiService.GetEndpoint("http", KnownNetworkIdentifiers.LocalhostNetwork)`, and `apiService` in turn gets its `Cors:AllowedOrigins` overridden (via `Cors__AllowedOrigins__0/1/2` env vars) from each container's own endpoint the same way — both directions explicitly pinned to `KnownNetworkIdentifiers.LocalhostNetwork` because the *unqualified* `GetEndpoint(name)` resolves differently depending on which resource happens to consume it, and for a container consumer that turned out to be Aspire's internal container-network tunnel address (`http://aspire.dev.internal:{port}`) — unreachable from an actual browser. See `AppHost.cs`'s comments at both call sites and the dated entry in `docs/decisions.md` for the full story, verified against a live `aspire run`.

#### Containers: the Angular apps

`SignalRQueueDemo.Angular/Dockerfile` is one multi-stage build (`node:22-slim` → `nginx:alpine`) parameterized by an `APP_NAME` build arg, shared by all three apps — see the file's own header comment for why one Dockerfile replaces three near-duplicates and why the build context is the whole Angular workspace folder, not a per-app subfolder. `SignalRQueueDemo.Angular/docker/write-runtime-config.sh` runs as an nginx `/docker-entrypoint.d/` script at container start, overwriting `config.json` from the `API_BASE_URL` environment variable Aspire injects — never baked in at `docker build` time, since the API's address isn't known until `aspire run` assigns it. `SignalRQueueDemo.Angular/docker/nginx.conf` serves the SPA (`try_files ... /index.html` for client-side routing) and marks `config.json` `no-store` so a container restart's new address is never masked by a cached copy of the old one.

**Measured, not estimated** (`docker images` / `docker build` against this repo, three separate `docker build --build-arg APP_NAME=<app>` runs):

| App | Image size | Cold build (no cache) | Cold build (cached `npm ci` + workspace layers) |
|---|---|---|---|
| `public-checkin` | 92.8 MB | ~36 s | ~8 s |
| `internal-queue` | 92.8 MB | ~36 s (shares node/nginx base layers with the above) | ~11 s |
| `queue-display` | 92.8 MB | ~36 s (same) | ~10 s |

All three images are 92.8 MB — identical `node:22-slim`/`nginx:alpine` base layers, differing only in the final `COPY --from=build .../dist/<app>/browser` layer (a few hundred KB of compiled JS/CSS per app), so Docker's layer cache means only the first of the three `docker build` invocations pays the full `npm ci` (~14 s) and Angular compile cost. Container cold start (measured via `docker run` to the first successful `GET /` response) was ~1.6 s. Aspire's own dashboard reports a similar startup time when it builds and starts all three under `aspire run`, plus whatever time the underlying `docker build` takes on a machine with no prior layer cache (a few minutes total the very first time `aspire run` is used in a fresh checkout — after that, only source changes invalidate the cache).

### Scaling past self-hosted: the Azure SignalR story

The `UseAzureSignalR` config flag (default `false`, set independently in both `SignalRQueueDemo.AppHost/appsettings.json` and `SignalRQueueDemo.ApiService/appsettings.json` — they're separate processes with no shared config source) marks the escape hatch. Two distinct paths, both implemented and documented in code:

- **Default (server) mode** — the production scale-up path from ADR-0001: `AddAzureSignalR(connectionString)`, one line, in `AzureSignalRDefaultModeStub.Apply()`. **This never runs, even with the flag on** — the [Azure SignalR Local Emulator](https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-howto-emulator) supports *serverless mode only*; for default mode, self-hosted SignalR (what this POC always wires the kiosk/staff/display frontends to) *is* the correct local stand-in. The stub compiles and is reviewable, but `Program.cs` never calls it — see its remarks for why.
- **Serverless mode** — what the emulator *can* demonstrate: `AzureSignalRServerlessDemoService`, a hosted service registered only when the flag is on. It builds a `ServiceHubContext` via `Microsoft.Azure.SignalR.Management`'s `ServiceManagerBuilder`, connects a client directly to the emulator (serverless clients never touch an ASP.NET Core hub), pushes one synthetic `QueueUpdated` through it, and logs whether the client received it. This is an illustrative side-channel — the app's real kiosk/staff/display traffic keeps flowing through the self-hosted `QueueHub` regardless of this flag; nothing about the app's real behavior changes when it's on, only that this extra proof-of-round-trip runs at startup.

**Verified against a live emulator, not assumed:** flipping the flag to `true` in both appsettings.json files and running `aspire run` starts an `mcr.microsoft.com/signalr/signalr-emulator` container (`AddAzureSignalR("signalr", AzureSignalRServiceMode.Serverless).RunAsEmulator()` in `AppHost.cs`) alongside Azurite; the API stays fully healthy (`GET /health`, `GET /queue` unaffected). The emulator's own logs show a clean connect/disconnect cycle for the demo's hub. One gotcha worth flagging for the vendor team: **Azure SignalR hub names reject hyphens** (letters/digits/`` _`,.[] `` only per its REST API validation) — `negotiate`/connect silently accept a hyphenated name, but the send REST call returns an opaque `400` the moment a broadcast is attempted. `AzureSignalRServerlessDemoService.DemoHubName` uses `queueServerlessDemo` (camelCase, no hyphens) for exactly this reason. With the flag off, behavior is byte-for-byte what #3 (self-hosted hub) already does — no SignalR emulator container starts at all.

## Constraints (court environment — carried into the code, not just docs)

- **No public/third-party cloud calls of any kind** from this POC — no external LLM APIs, no OAuth against real tenants, nothing that reaches outside localhost/containers. Local dev-time POC only; Azure services appear as local emulators (Azurite, Azure SignalR Emulator) exclusively.
- **No real names, case numbers, or any court data** — seed with obviously fake data (e.g. "Ticket A-042", "Jane Test").
- **No secrets in source control** — connection strings come from config/user-secrets, never hardcoded; `.gitignore` covers local secret files.
- No code signing needed for a POC.

## Acceptance criteria

1. `aspire run` starts everything with no manual steps.
2. A check-in from `public-checkin` (or the Blazor public page) shows up live in `internal-queue` (or the Blazor internal page) via SignalR. The display app shows checked-in and serving users, and entries disappear when completed.
3. Calling "next" updates status and all frontends reflect it in real time.
4. Disconnecting a client mid-session and reconnecting shows it **catching up on missed state**, not just resuming live updates from that point forward.
5. The Azure SignalR feature-flag stub is visible in code/config, clearly commented, wired only to the local emulator (serverless mode — see above), never a real Azure resource.

## Implementation plan

Work is executed as an ordered, dependency-aware backlog of 14 work items, each with its own acceptance criteria and the documentation it must update. Rough shape:

1. Contracts → 2. API + SQLite → 3. Hub + reconnect catch-up → 4. Table Storage + Azurite → 5. Blob upload → 6. Auth/hardening → 7. Azure SignalR flag + emulator → 8. Angular workspace + shared lib → 9–11. The three Angular apps → 12. Dockerfiles + AppHost wiring → 13. Blazor experiences → 14. E2E verification + Angular-vs-Blazor comparison.

## Current repo status

.NET Aspire scaffold (`net10.0`, Aspire.AppHost.Sdk 13.4.6) with the queue API live behind `IQueueRepository`, now with two swappable backends, plus document upload/viewing behind `IDocumentRepository` and `DocumentBlobStore`. The Angular workspace (`SignalRQueueDemo.Angular/`) has its `shared` library and three app shells built, `public-checkin` (the kiosk check-in + live-position + document-upload UI) built on top of it, and all three apps now containerized and wired into `aspire run` — see [Angular workspace](#3-angular-workspace-signalrqueuedemoangular--three-apps-one-shared-library) and [Containers](#containers-the-angular-apps) above. The Blazor `Web` project and the other two Angular app UIs (`internal-queue`, `queue-display`) are still shells/not-yet-built — they run as containers already, but with placeholder content until their real UIs land.

| Project / path | Purpose |
|---|---|
| `SignalRQueueDemo.AppHost` | Aspire orchestrator — brings up every resource with one command: `ApiService`/`Web` as project resources, the three Angular apps as `AddDockerfile` container resources (`API_BASE_URL`/CORS wired via endpoint references — see [above](#5-signalrqueuedemoapphost--orchestrates-all-of-it)), the Azurite Table Storage and Blob Storage emulator resources, plus the Azure SignalR Emulator when `UseAzureSignalR=true` (see [Scaling past self-hosted](#scaling-past-self-hosted-the-azure-signalr-story)). |
| `SignalRQueueDemo.ApiService` | Minimal API: `GET /checkin/token`, `/checkin`, `/queue/call-next`, `/queue/{id}/complete`, `GET /queue`, `GET /queue/since/{sequenceNumber}`, backed by `IQueueRepository` → `SqliteQueueRepository` or `TableStorageQueueRepository` (config-selected); plus `POST /checkin/{id}/documents`, `GET /queue/{id}/documents`, `GET /queue/{id}/documents/{docId}`, backed by `IDocumentRepository` (same config-selected backend) and `DocumentBlobStore` (Azurite Blob Storage, not config-selected); staff routes and document viewing gated by `StaffAuthFilter`, the check-in path by a short-lived check-in token, all browser surfaces (REST + hub) behind the `KnownFrontends` CORS policy (see [Security model](#security-model)); plus the self-hosted `QueueHub` (`/hubs/queue`) broadcasting `QueueUpdated`, and the `UseAzureSignalR` escape hatch (`AzureSignalRDefaultModeStub`, `AzureSignalRServerlessDemoService`). |
| `SignalRQueueDemo.Contracts` | Shared DTOs/records/enums (QueueEntry, QueueStatus, QueueUpdated, QueueStateResponse, DocumentMetadata, etc.) — single source of truth for all wire shapes. |
| `SignalRQueueDemo.Web` | Blazor Server frontend (template default today; becomes the three Blazor experiences in later work). |
| `SignalRQueueDemo.Angular/` | Angular workspace: `projects/shared` library (TypeScript `Contracts` mirrors, `QueueApiService`, `QueueHubService`) plus `projects/public-checkin`, `projects/internal-queue`, `projects/queue-display` app shells (real UIs land in #9-#11). See [`SignalRQueueDemo.Angular/README.md`](SignalRQueueDemo.Angular/README.md). |
| `SignalRQueueDemo.ServiceDefaults` | Shared Aspire defaults — OpenTelemetry, health checks, service discovery. |
| `CLAUDE.md` | Coding + documentation standards for all contributors. |
| `docs/architecture.md` | Living architecture doc (Mermaid diagrams, trust boundaries, reconnect protocol). |
| `docs/architecture.drawio` | Editable diagram source (export to `architecture.drawio.png` — workflow in the doc). |
| `docs/decisions.md` | Dated log of implementation decisions not fully covered by the original spec. |

## Running it today

```
aspire run
# or: dotnet run --project SignalRQueueDemo.AppHost
```

Opens the Aspire dashboard with the API service, the Blazor Web app (still template defaults; real-time queue features land per the implementation plan), the Azurite storage emulator, and all three Angular apps as Docker containers (`public-checkin`, `internal-queue`, `queue-display` — see [Containers](#containers-the-angular-apps) above). The first `aspire run` in a fresh checkout takes longer than later ones: Docker has to build all three container images from scratch (`npm ci` + Angular compile, a few minutes total); every run after that reuses Docker's layer cache and only rebuilds what changed. Each Angular container's URL is shown in the Aspire dashboard's resource list — open any of the three to load that app in a browser; there is no fixed port to remember (unlike the dev-server ports below), since Aspire assigns each container's externally-reachable port at startup.

### Flipping the persistence provider

`Persistence:Provider` in `SignalRQueueDemo.ApiService/appsettings.json` (or an environment/user-secrets override) selects the `IQueueRepository` backend at startup — `Sqlite` (default) or `TableStorage`. `aspire run` always starts the Azurite Table Storage emulator regardless of which value is set, so switching providers needs no other change: stop the app, edit the setting, run `aspire run` again. The manual test script below (and the reconnect/catch-up walkthrough after it) exercises the exact same requests and produces the same responses against either backend — that equivalence is a core acceptance criterion for the Table Storage backend. Seed data is identical either way ("Jane Test"/A-042, "Sam Sample"/A-043).

Both backends give the same concurrency guarantees — distinct check-in positions, no double-serve on call-next, and a reconnect baseline that never runs ahead of the changes it returns — verified with concurrent request bursts against a live `aspire run` stack on each provider. See `docs/decisions.md` for how the Table Storage side achieves this without SQL transactions: a gap-free change-event log (the row insert *is* the sequence-number allocation) plus an ETag-serialized position counter, both reconciled to real state at startup.

### Flipping UseAzureSignalR

Set `UseAzureSignalR` to `true` in **both** `SignalRQueueDemo.AppHost/appsettings.json` and `SignalRQueueDemo.ApiService/appsettings.json` (they're independent flags read by independent processes — see [Scaling past self-hosted](#scaling-past-self-hosted-the-azure-signalr-story)), then `aspire run`. The Aspire dashboard shows a new `signalr` emulator resource starting alongside `storage`; the ApiService log (Aspire dashboard → `apiservice` → Console) shows either `UseAzureSignalR serverless demo succeeded...` or a timeout/error line from `AzureSignalRServerlessDemoService` a few seconds after startup. Flip it back to `false` afterward — that's the committed default, and leaving it on starts an extra container for no benefit once you're done exercising the path.

### Exercising the queue API manually

With the API running (`aspire run`, or `dotnet run --project SignalRQueueDemo.ApiService` standalone on `http://localhost:5410`), open `SignalRQueueDemo.ApiService/SignalRQueueDemo.ApiService.http` in an editor with REST Client support (VS Code's REST Client extension, or Visual Studio's built-in `.http` runner) and run requests top to bottom:

1. `GET /queue` — confirm the seeded entries ("Jane Test" / A-042, "Sam Sample" / A-043) show up as `Waiting`, and note the sequence number.
2. `POST /checkin` — check in a new fake visitor; response includes their position and the new sequence number.
3. `POST /queue/call-next` — the oldest `Waiting` entry (Jane Test) moves to `Serving`; response is the `QueueUpdated` broadcast payload. Calling it again with an empty queue returns `409 Conflict`.
4. `POST /queue/{id}/complete` — copy an entry id from a prior response into the `@entryId` variable, then complete it. A `Serving` entry moves to `Completed`; a wrong-status or unknown id returns `409`/`404` respectively.

This is the full check-in → call-next → complete lifecycle.

### Uploading and viewing documents

Documents upload to Blob Storage (Azurite locally) against a specific queue entry, and staff list/stream them back. Continuing the `.http` script above:

1. `POST /checkin/{entryId}/documents` — multipart upload with `file` as the form field name. Content type is checked against an allowlist (`application/pdf`, `image/jpeg`, `image/png`) and size against a 10 MB cap, both enforced server-side; an empty file, a wrong type, or a file modestly over the cap returns `400` with `ProblemDetails`; a grossly oversized body is cut off by a form-buffering backstop and returns `413`; an entry already holding the maximum of 10 documents returns `409`; and an unknown `entryId` returns `404`. The `.http` file references a committed sample PDF (`SampleDocuments/sample-intake-form.pdf` — synthetic test data only, no real court data) so the request works with no external file. The uploaded blob's storage name is always a server-generated GUID, never the client's filename — see `docs/decisions.md` for why.
2. `GET /queue/{entryId}/documents` — lists the entry's documents (display filename, content type, size, uploaded-at) from metadata tracked alongside the queue entry, without touching Blob Storage. Returns `404` for an unknown entry id.
3. `GET /queue/{entryId}/documents/{docId}` — streams the document's actual content back with its original content type and filename. Returns `404` for an unknown entry or document id.

**Limits, by design:** 10 MB per file (plus a form-buffering backstop so a public endpoint can't be made to spool an unbounded body), three allowed content types, at most 10 documents per queue entry, one Blob container per queue entry (`docs-{entryId}`), and container/blob names that are always server-generated — none of these are configurable via `appsettings.json`, matching the constants-not-config pattern already used for `MaxCatchUpEvents`/`MaxOptimisticConcurrencyAttempts` elsewhere in this codebase. See `docs/decisions.md` for why the size and per-entry limits are enforced the way they are.

**Auth status:** both `GET` endpoints above require the mock staff-auth `X-Staff-Key` header (alongside call-next/complete); the upload `POST` requires the same check-in token as `POST /checkin`. See [Security model](#security-model) below.

## Security model

Models the internal-vs-public trust boundary from ADR-0001 — **honestly documented as a demonstration, not real security.** No court data or production credentials ever touch this POC (see [Constraints](#constraints-court-environment--carried-into-the-code-not-just-docs)), so the bar here is "show the team we thought about the public-internet-facing side," not defend a real attacker.

**Staff endpoints** (`POST /queue/call-next`, `POST /queue/{id}/complete`, `GET /queue/{id}/documents`, `GET /queue/{id}/documents/{docId}`) — gated by `StaffAuthFilter`, an `IEndpointFilter` checking a static `X-Staff-Key` header (fixed-time comparison) against `StaffAuth:Key` in config. This models the Entra ID boundary ADR-0001 calls for on the internal portal; a real deployment replaces the filter with actual authentication (Entra ID + the schema-scoped SQL logins ADR-0001 names as a production requirement), it doesn't just rotate the key. Missing or wrong key returns `401` with `ProblemDetails`.

**Public check-in path** (`POST /checkin`, `POST /checkin/{id}/documents`) — a short-lived, stateless token (no server-side session or token store): `GET /checkin/token` issues an HMAC-signed, 5-minute token (`CheckInTokenService`, returned with `Cache-Control: no-store`); the kiosk echoes it back on `X-CheckIn-Token`. `POST /checkin` validates it via `CheckInTokenFilter`; the document upload validates it inline *before* it reads any of the multipart body, so an unauthenticated upload is rejected without the server buffering the file. Chosen over ASP.NET Core's built-in cookie-based antiforgery system because a public kiosk SPA on a different origin can't be assumed to carry a same-site cookie (see `docs/decisions.md`).

**Restricted CORS** (`Cors:AllowedOrigins` in config, policy `KnownFrontends`) — applied to **every browser-reachable surface**: the public *and* staff REST endpoints and the SignalR hub. Only listed origins can reach any of them from a browser. CORS is not the trust boundary (the staff key and check-in token are) — it's applied everywhere, including staff endpoints and the hub, precisely so the legitimate cross-origin frontends (all three Angular apps depend on the hub for live updates; `internal-queue` calls the staff endpoints) aren't refused by the browser before those real checks run, while an unlisted origin still is. The Angular apps don't exist as deployed origins yet (later work items), so this is seeded with their local dev-server ports (`http://localhost:4200`–`4202`, one per app so all three can run at once) as a placeholder. Missing `StaffAuth:Key` or `CheckInToken:SigningKey` fails the app at **startup**, not on the first request.

**Stated honestly — what this doesn't do:** a public SPA cannot hold a secret a determined attacker can't also read, so the check-in token proves "this caller fetched a token recently," not "this caller is the real kiosk app." CORS stops browser-mediated cross-origin abuse, not a direct `curl`/script request with a forged `Origin` header. Both raise the bar against drive-by scripted abuse; neither is authentication. `GET /queue` and `GET /queue/since/{sequenceNumber}` are read-only and CORS-restricted but carry no token requirement, matching the anti-forgery-style pattern's usual scope of protecting writes, not reads.

### Manually verifying the security hardening

Continuing the `.http` script (`SignalRQueueDemo.ApiService/SignalRQueueDemo.ApiService.http`):

1. Run `POST /queue/call-next` with no `X-Staff-Key` header — confirm `401` with `ProblemDetails`. Add the header (the `.http` file's `@staffKey` variable already matches the `appsettings.json` placeholder) and confirm it succeeds.
2. Run `POST /checkin` with no `X-CheckIn-Token` header — confirm `401`. Run `GET /checkin/token`, copy its `token` value into `@checkInToken`, retry `POST /checkin` — confirm it succeeds.
3. From a shell, send a CORS preflight for an unlisted origin: `curl -i -X OPTIONS http://localhost:5410/queue -H "Origin: http://evil.example" -H "Access-Control-Request-Method: GET"` — confirm the response carries no `Access-Control-Allow-Origin` header (the browser would block the real request that follows). Repeat with `-H "Origin: http://localhost:4200"` (the configured default) and confirm the header is present.

### Manually verifying the reconnect/catch-up protocol

The `.http` file alone can't hold a live SignalR connection open, so this needs a small standalone client — either a
throwaway `dotnet run` console app using `Microsoft.AspNetCore.SignalR.Client` (`HubConnectionBuilder().WithUrl("http://localhost:5410/hubs/queue").Build()`), or two browser tabs once a frontend exists. Steps:

1. Connect the client to `/hubs/queue`. Confirm it immediately receives `CurrentSequence(N)` — this is the "you're caught up as of right now" baseline sent from `QueueHub.OnConnectedAsync`.
2. From the `.http` file, run `POST /checkin`. Confirm the connected client receives a `QueueUpdated` push with `SequenceNumber = N + 1`.
3. Disconnect the client (kill the console app, or close the tab) — simulating a kiosk losing wifi or staff stepping away. Note the last sequence number it saw (`N + 1`).
4. With the client still disconnected, run `POST /queue/call-next` then `POST /queue/{id}/complete` from the `.http` file — two more state changes the disconnected client will miss entirely.
5. Reconnect the client. It receives a fresh `CurrentSequence` (now `N + 3`), proving live push alone would have skipped the two changes made while it was offline.
6. Call `GET /queue/since/{N + 1}` (edit `@sinceSeq` in the `.http` file). Confirm the response has `isSnapshot: false` and `changes` contains **exactly** the two missed events (call-next then complete, in that order) — this is the acceptance criterion for the reconnect/catch-up protocol.
7. Optional — exercise the snapshot fallback: call `GET /queue/since/999999` (an unrecognized/future sequence number). Confirm the response has `isSnapshot: true` with a full `snapshot` instead of `changes`.

### Running the public-checkin kiosk (Angular)

The `public-checkin` app is the visitor-facing kiosk: a check-in form, a live "you are #N in line" screen, and optional document upload — all consuming the same API and the shared `QueueHubService`. Run the API first (`aspire run`, or `dotnet run --project SignalRQueueDemo.ApiService` on `http://localhost:5410`), then from `SignalRQueueDemo.Angular/`:

```
npm ci
npm run start:public-checkin   # serves on http://localhost:4200
```

The API's `Cors:AllowedOrigins` already lists `http://localhost:4200`, so the browser calls and the hub connection are allowed. Manual test script:

1. Open `http://localhost:4200`. The check-in form shows a name field and a pre-filled, editable fake ticket number (e.g. `A-042`). Enter a fake name ("Jane Test") and submit.
2. The view switches to the live position screen ("You're checked in — #N in line"). N is derived from the authoritative queue snapshot, not the one-shot check-in response, so it stays correct as the queue moves.
3. From the `.http` file (or a second tool), run `POST /queue/call-next` repeatedly. Watch the kiosk's position count down **live** over SignalR — no refresh. When this visitor's own entry is called, the screen switches to "It's your turn"; when it's completed (`POST /queue/{id}/complete`), it switches to "Thank you" and auto-resets to a blank form after a short visible countdown.
4. **Document upload:** before completion, use "Choose file" to attach a PDF/JPEG/PNG (≤10 MB). A wrong type or oversized file is rejected instantly client-side (the same rules the server enforces — see [Uploading and viewing documents](#uploading-and-viewing-documents)); a valid file uploads and appears in the "attached" list. Confirm it landed with `GET /queue/{entryId}/documents` (staff-gated).
5. **Reconnect/catch-up (acceptance criterion):** while on the position screen, open DevTools → Network → set **Offline**. Run a `POST /queue/call-next` or two against the API. Set the network back to **Online** — the kiosk reconnects and the position corrects itself to the changes it missed while offline (a brief "Reconnecting… your place is still saved" note appears while it's degraded). This exercises the shared `QueueHubService`'s catch-up path end-to-end from a real browser.

Each Angular app has its own fixed dev-server port (`public-checkin` → 4200, `internal-queue` → 4201, `queue-display` → 4202), so you can run all three at once in separate terminals and watch a check-in on the kiosk ripple to the staff console and the public board live — see [`SignalRQueueDemo.Angular/README.md`](SignalRQueueDemo.Angular/README.md).

### Running the internal-queue staff console (Angular)

The `internal-queue` app is the staff queue management console: mock-authenticated with a static key (modeling Entra ID production auth), lets staff call the next person in line, mark entries complete, and view supporting documents. Run the API first (`aspire run`, or `dotnet run --project SignalRQueueDemo.ApiService` on `http://localhost:5410`), then from `SignalRQueueDemo.Angular/`:

```
npm ci
npm run start:internal-queue    # serves on http://localhost:4201
```

The API's `Cors:AllowedOrigins` already lists `http://localhost:4201`, so the browser calls and the hub connection are allowed. Manual test script:

1. Open `http://localhost:4201`. The staff sign-in form asks for a staff key. Enter the value from the `@staffKey` variable in `SignalRQueueDemo.ApiService/SignalRQueueDemo.ApiService.http` (the `StaffAuth:Key` placeholder in `appsettings.json`; it defaults to a simple test string).
2. The view switches to the live queue console. The left side shows "Now Serving" (the entry currently being served, or empty), the right side shows "Waiting" with the seeded entries ("Jane Test" / A-042, "Sam Sample" / A-043) sorted by check-in time.
3. From the `.http` file (or a second tool), run `POST /checkin` to add a new entry to the waiting list. Watch the staff console update **live** over SignalR — no refresh needed. The new entry appears in the Waiting list in the correct position.
4. Click "Call Next" on the console. Watch the oldest Waiting entry move to Serving **live** — the left side now shows who is being served and which staff member called them (`servedBy` — this internal-only detail models the staff context vs. the privacy-conscious public board).
5. Click "View documents" on any entry (Waiting or Serving). A list panel appears on the left; if the entry has attached documents (e.g. you've run `POST /checkin/{entryId}/documents` from the `.http` file with a sample PDF), click one to view it inline. Images render in an `<img>` tag, PDFs in an `<iframe>` with the browser's native viewer. **Object URL lifecycle:** Selecting a different document revokes the previous one; changing entries does the same; closing the panel revokes any open URL. This discipline prevents memory leaks on a long-running staff console.
6. Click "Complete" on a Serving entry. It immediately disappears from both the Serving and Waiting lists (Completed entries are excluded from the board). All connected frontends (another staff console, the public display board, any kiosk) see the change live.
7. **Wrong staff key:** refresh to return to sign-in and enter a wrong key. Sign-in itself doesn't validate (there's no verify endpoint), but the first queue action (Call Next / Complete) gets a `401` and bounces you straight back to the sign-in screen. Enter the correct key — the console resumes normally. (Viewing documents with a bad key shows an inline "sign in again" message rather than bouncing, since that's a read, not a queue mutation.)
8. **Reconnect/catch-up (acceptance criterion):** while viewing the console, open DevTools → Network → set **Offline**. Run `POST /checkin`, `POST /queue/call-next`, or `POST /queue/{id}/complete` against the API. Set the network back to **Online** — the console reconnects and the queue corrects itself to the changes it missed while offline (a "Reconnecting…" banner appears in the corner while degraded). This exercises the shared `QueueHubService`'s catch-up path end-to-end from a real browser.

### Running the queue-display board (Angular)

The `queue-display` app is the public waiting-room board: a full-screen read-only display of who is being served and who is still waiting, updated live via SignalR. Run the API first (`aspire run`, or `dotnet run --project SignalRQueueDemo.ApiService` on `http://localhost:5410`), then from `SignalRQueueDemo.Angular/`:

```
npm ci
npm run start:queue-display    # serves on http://localhost:4202
```

The API's `Cors:AllowedOrigins` already lists `http://localhost:4202`, so the browser calls and the hub connection are allowed. Manual test script:

1. Open `http://localhost:4202`. The board initially shows "Now Serving" (empty initially) on the left and "Waiting" with the seeded entries ("Jane Test" / A-042, "Sam Sample" / A-043) on the right, sorted by check-in time.
2. From the `.http` file (or a second tool), run `POST /checkin` to add a new entry to the waiting list. Watch the board update **live** over SignalR — no refresh needed.
3. Run `POST /queue/call-next` to move the oldest waiting entry to "Now Serving". Watch the board reorganize in real time — the entry moves from the "Waiting" list to the "Now Serving" section.
4. Call `POST /queue/{id}/complete` on a serving entry. It immediately disappears from the board (Completed entries are never displayed).
5. **Reconnect/catch-up (acceptance criterion):** while viewing the board, open DevTools → Network → set **Offline**. Run `POST /checkin`, `POST /queue/call-next`, or `POST /queue/{id}/complete` against the API. Set the network back to **Online** — the board reconnects and corrects itself to the changes it missed while offline (a "Reconnecting…" banner appears in the corner while degraded). This exercises the shared `QueueHubService`'s catch-up path end-to-end from a real browser.

**Court privacy reminder:** the board displays only ticket numbers (`A-042`, etc.), never names or identifying information — this is a public display.

## Angular vs. Blazor comparison

Filled in from actual observations, not boilerplate: dev experience, container image size and startup time, what service discovery looked like from each side, and the plumbing delta for the reconnect logic. This section is meant to help the team pick a direction.

## Open items (from the 2026-07-09 design review)

- Glen: self-hosted-SignalR POC + Azure SignalR emulator docs — due 2026-07-10.
- Glen: Azure resource naming-convention proposal, coordinated with Nathan Sargent and Syed, DASH 2.0 as pilot — due 2026-07-15.
- Ravi Ganji: GitHub access for the dev team to this repo — due 2026-07-11.
- Moueen Shaik: updated system design document — due 2026-07-12.

## Reference

- ADR-0001 (Accepted 2026-07-09): court OneDrive, `ECA Discovery\Solutions\DASH 2.0\ADR-0001 - DASH 2.0 Real-Time Messaging Topology.docx`.
- Architecture diagrams (court OneDrive, same folder): `Dash 2.0.png`, `DASH 2.0 Flow.png`, `Architectural Design Diagram Kiosk ver 3.1.png`, `DASH 2.0 Court User Portal - High Level Architecture Diagram 1.png`.
- [Azure SignalR Local Emulator (serverless only)](https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-howto-emulator) · [Aspire Azure SignalR integration](https://learn.microsoft.com/en-us/dotnet/aspire/real-time/azure-signalr-scenario).
- No production Azure resources or real court data belong in this repo — synthetic test data only, no secrets in source control, no public/third-party API calls.
