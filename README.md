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
| `POST /checkin` | public (hardened) | Creates a queue entry — name/ticket number, timestamp, status=`Waiting`. |
| `POST /checkin/{id}/documents` | public (hardened) | Uploads a supporting document to Blob Storage (Azurite locally). |
| `POST /queue/call-next` | mock staff auth | Moves the oldest `Waiting` entry to `Serving`. |
| `POST /queue/{id}/complete` | mock staff auth | Moves a `Serving` entry to `Completed`. |
| `GET /queue` | public (hardened) | Current queue state + latest sequence number. |
| `GET /queue/since/{sequenceNumber}` | public (hardened) | Reconnect catch-up: every change after that sequence number. |
| `GET /queue/{id}/documents` | mock staff auth | Lists/streams uploaded documents for an entry. |

> **Route correction from the original brief:** the brief specified `POST /queue/{id}/call-next`, but call-next selects the next entry itself — no id belongs in that route. Corrected to `POST /queue/call-next`; `{id}` remains on `complete`, which does act on a specific entry.

- **`QueueHub`** broadcasts `QueueUpdated` on every state change. Self-hosted in-process (ADR-0001 Option C), with a feature-flag path to Azure SignalR (below).
- **Reconnect resiliency:** every state change increments a **monotonic sequence number** persisted in a change-event log. Reconnecting clients call `GET /queue/since/{seq}` to replay what they missed — push-only delivery is never relied on.
- **Persistence:** behind an `IQueueRepository` interface with two signature-compatible implementations — **SQLite via EF Core** (default) and **Azure Table Storage** (against the Azurite emulator) — selected by config.
- **Auth model:** no auth on the public check-in path, but lightweight hardening (restricted CORS + short-lived anti-forgery token) to demonstrate the public-internet posture honestly. Staff endpoints use simple mock auth (`X-Staff-Key` header) to model the internal-vs-public trust boundary — no real Entra ID in the POC.

### 2. `SignalRQueueDemo.Contracts` — shared DTOs/records

Referenced by `ApiService` and `Web` (Blazor). The Angular workspace mirrors the same shapes as TypeScript types.

### 3. Angular workspace (`angular/`) — three apps, one shared library

| App | Purpose |
|---|---|
| `public-checkin` | Kiosk-style check-in form + document upload; shows "you're #N in line" live (SignalR + polling fallback). |
| `internal-queue` | Staff call-next console; live queue; views uploaded documents. |
| `queue-display` | Public waiting-room board (tickets, never names); serving/waiting status; completed entries disappear. |

A shared library holds the API client, the TypeScript contract mirrors, and the SignalR connection service with sequence-number tracking, reconnect catch-up, and polling fallback. Each app gets its own multi-stage `Dockerfile` (Node build stage → nginx static serving) with the API address injected at **runtime** from Aspire service discovery — never baked in at build time.

### 4. `SignalRQueueDemo.Web` — Blazor Server, same three experiences

Public check-in, internal call-next, and queue display as Blazor pages — no REST client needed; uses the shared Contracts and a direct SignalR `HubConnection` since it's already .NET. This is the comparison stack: the UI stays simple on purpose, the point is comparing plumbing, not polish.

### 5. `SignalRQueueDemo.AppHost` — orchestrates all of it

`ApiService` and `Web` as project resources; each Angular app containerized via `AddDockerfile` and wired with `WithReference(api)` / service-discovery env vars; Azurite (Blob + Table) as an emulator resource; the Azure SignalR Emulator only when the feature flag is on; `ServiceDefaults` (OpenTelemetry, health checks, service discovery) on every resource. Goal: **one command brings up everything with zero manual port/URL wiring.**

### Scaling past self-hosted: the Azure SignalR story

The `UseAzureSignalR` config flag (default `false`) marks the escape hatch. Two distinct paths, both documented in code:

- **Default (server) mode** — the production scale-up path from ADR-0001: `AddAzureSignalR(connectionString)`, one line. **The local emulator cannot exercise this mode** — the [Azure SignalR Local Emulator](https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-howto-emulator) supports *serverless mode only*; for default mode, self-hosted SignalR (what this POC runs) *is* the correct local stand-in. The `AddAzureSignalR` call ships as a clearly commented stub, not wired to any resource.
- **Serverless mode** — what the emulator *can* demonstrate: broadcasting through the [`Microsoft.Azure.SignalR.Management`](https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-quickstart-azure-functions-csharp) SDK (`ServiceManagerBuilder` → `ServiceHubContext`) against Aspire's `AddAzureSignalR(...).RunAsEmulator()` resource. The POC demonstrates this path when the flag is enabled so the team sees a real emulator round-trip, with comments explaining that production Option C scale-up would use default mode instead.

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

Work is driven by [GitHub issues #1–#14](https://github.com/glensouza/signalr-queue-demo/issues), ordered by dependency, each carrying its own acceptance criteria, the docs it must update, and a recommended Claude model (`model:haiku` / `model:sonnet` / `model:opus` labels) chosen to optimize token cost. Rough shape:

1. Contracts → 2. API + SQLite → 3. Hub + reconnect catch-up → 4. Table Storage + Azurite → 5. Blob upload → 6. Auth/hardening → 7. Azure SignalR flag + emulator → 8. Angular workspace + shared lib → 9–11. The three Angular apps → 12. Dockerfiles + AppHost wiring → 13. Blazor experiences → 14. E2E verification + Angular-vs-Blazor comparison.

## Current repo status

Fresh .NET Aspire starter scaffold (`net10.0`, Aspire.AppHost.Sdk 13.2.4) plus planning/standards docs. The sample Weather API/Blazor pages are template defaults, replaced starting at issue #2.

| Project / path | Purpose |
|---|---|
| `SignalRQueueDemo.AppHost` | Aspire orchestrator — brings up every resource with one command. |
| `SignalRQueueDemo.ApiService` | Minimal API (template default today; becomes queue endpoints + `QueueHub`). |
| `SignalRQueueDemo.Contracts` | Shared DTOs/records/enums (QueueEntry, QueueStatus, QueueUpdated, etc.) — single source of truth for all wire shapes. |
| `SignalRQueueDemo.Web` | Blazor Server frontend (template default today; becomes the three Blazor experiences). |
| `SignalRQueueDemo.ServiceDefaults` | Shared Aspire defaults — OpenTelemetry, health checks, service discovery. |
| `CLAUDE.md` | Coding + documentation standards for all contributors. |
| `docs/architecture.md` | Living architecture doc (Mermaid diagrams, trust boundaries, reconnect protocol). |
| `docs/architecture.drawio` | Editable diagram source (export to `architecture.drawio.png` — workflow in the doc). |

## Running it today

```
aspire run
# or: dotnet run --project SignalRQueueDemo.AppHost
```

Opens the Aspire dashboard with the API service and Blazor Web app (template defaults; real-time queue features land per the implementation plan). The final README will include the full manual demo script: check in → call next → kill and restart a client mid-session → confirm it catches up.

## Angular vs. Blazor comparison

Filled in at issue #14 from actual observations, not boilerplate: dev experience, container image size and startup time, what service discovery looked like from each side, and the plumbing delta for the reconnect logic. This section is meant to help the team pick a direction.

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
