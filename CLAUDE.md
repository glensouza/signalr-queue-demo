# CLAUDE.md

Guidance for Claude Code (and any AI model/agent) working in this repository. Follow it exactly — this repo is a **reference implementation** that will be handed to a vendor dev team, so clarity and documentation outrank cleverness everywhere.

## What this project is

Proof-of-concept for the DASH 2.0 walk-in check-in / queue system at San Bernardino Superior Court. One shared ASP.NET Core minimal API + self-hosted SignalR hub, consumed by **two competing frontend stacks** (Angular in containers vs. Blazor Server) so the vendor team can compare them. Everything is orchestrated by a single .NET Aspire AppHost. The full build spec is in `Claude Code starting prompt — DASH 2.md`; the architectural decision record context is in `README.md`.

## Hard constraints (court environment — never violate)

- **No public/third-party cloud calls** from the running POC: no external LLM APIs, no OAuth against real tenants, nothing outside localhost/containers. Azure services are represented by **local emulators only** (Azurite for Storage, Azure SignalR Emulator for SignalR).
- **No real court data.** Seed and test with obviously fake data: "Ticket A-042", "Jane Test", "Sam Sample".
- **No secrets in source control.** Connection strings and keys come from config/user-secrets/environment. If you add a config knob that could hold a secret, add a placeholder + comment showing where the real value goes, and verify `.gitignore` covers any local secret files.
- Target **.NET 10 LTS**. Do not downgrade any project.

## Commands

```bash
# Run everything (API + Blazor + Angular containers + emulators + dashboard)
aspire run                      # or: dotnet run --project SignalRQueueDemo.AppHost

# Build / test
dotnet build SignalRQueueDemo.slnx
dotnet test

# Angular (from SignalRQueueDemo.Angular/ — one workspace, three apps + shared lib)
npm ci
npm run build                   # shared lib + all three apps
npm run start:public-checkin    # or start:internal-queue / start:queue-display
```

## Solution layout

| Project / folder | Purpose |
|---|---|
| `SignalRQueueDemo.AppHost` | Aspire orchestrator — the only thing you run. |
| `SignalRQueueDemo.ApiService` | Minimal API + self-hosted `QueueHub` (SignalR). |
| `SignalRQueueDemo.Shared` | DTOs/records/enums, `IQueueRepository`/`IDocumentRepository` + both backends, staff-key/document-upload helpers — referenced by both `ApiService` and `Web` so a change to a wire shape or a repository method affects both from one place. Angular mirrors the DTO shapes in TypeScript. |
| `SignalRQueueDemo.Web` | Blazor Server implementation of the three experiences. |
| `SignalRQueueDemo.ServiceDefaults` | Aspire defaults: OpenTelemetry, health checks, service discovery. |
| `SignalRQueueDemo.Angular` | Angular workspace (one CLI multi-project workspace): `projects/shared` library + three app projects below. |
| `SignalRQueueDemo.Angular/projects/public-checkin` | Kiosk check-in app (no auth, containerized). |
| `SignalRQueueDemo.Angular/projects/internal-queue` | Staff call-next console (mock auth, containerized). |
| `SignalRQueueDemo.Angular/projects/queue-display` | Public queue status display (containerized). |
| `docs/` | Architecture docs and diagrams — see "Documentation is part of every change". |

Keep the `SignalRQueueDemo.*` project-name prefix for new .NET projects (matches the existing scaffold).

## C# coding standards

These match the owner's established style (see `sbsc-portfolio-viewer` for precedent). Apply them to all new and edited C# code.

- **Explicit types, not `var`**: `WebApplicationBuilder builder = ...`, `string text = ...`. Use the target-typed `new()` on the right side when the type is on the left: `using FileStream stream = new(path, ...)`.
- **`this.` prefix** for instance members: `this.statePath`, `this.queueRepository`.
- **File-scoped namespaces** (`namespace SignalRQueueDemo.ApiService.Services;`).
- **Primary constructors** for DI: `public sealed class QueueService(IQueueRepository repository)`.
- **`sealed`** on classes not designed for inheritance (default for services).
- **Records** for DTOs/contracts; `required` members and collection expressions (`[]`) where they fit.
- **Nullable reference types on** everywhere; no `!` suppressions without a comment explaining why it's safe.
- Private fields are lowerCamelCase without underscore prefix (`private readonly string statePath;`).
- Async all the way down; accept `CancellationToken ct = default` on public async methods.

## Documentation standards (the most important section)

This code will be read by developers with fresh eyes who weren't in any design meeting. Every piece of code must answer "why is this here?" on its own.

- **XML doc comments** (`/// <summary>`) on every public type and public member. The summary explains **why the thing exists and what tradeoff it embodies**, not a restatement of its name. Good example from the precedent project:
  > *"Kept in a sidecar file rather than the DB on purpose: the app creates its schema with EnsureCreated, which won't add a new table to an already-created database — a sidecar sidesteps that entirely."*
- **Inline comments carry rationale and constraints** the code can't show: why a fallback exists, what breaks if you remove a line, which court constraint forced a choice. Never narrate what the next line does.
- Angular/TypeScript: JSDoc on services, components, and non-obvious functions, same "why" standard.
- Decision points get a short `// DECISION:` comment at the site plus a line in the README's decisions section.

### Documentation is part of every change

Every task/PR that changes behavior must also update the matching docs **in the same change** — a code change without its doc update is incomplete:

- `README.md` — how to run, what each project is, manual test script, Angular-vs-Blazor observations, decisions log.
- `docs/architecture.md` — component/flow descriptions with Mermaid diagrams (these render on GitHub).
- `docs/architecture.drawio` — the editable diagram source. Update it whenever a resource, connection, or trust boundary changes, then re-export `docs/architecture.drawio.png` (VS Code "Draw.io Integration" extension: open the `.drawio` file, File → Export As PNG, or edit `docs/architecture.drawio.png` directly — the extension round-trips PNG with embedded XML).
- `docs/decisions.md` — one dated entry per architecture decision made during implementation ("what we chose, what we rejected, why").

## Real-time / queue domain rules

- Every queue state change increments a **monotonic sequence number** and is broadcast as `QueueUpdated`. Reconnecting clients catch up via `GET /queue/since/{sequenceNumber}` — never rely on push-only delivery.
- Persistence goes through `IQueueRepository`; SQLite (EF Core) and Azure Table Storage (Azurite) implementations must stay signature-compatible and swappable via config.
- The `UseAzureSignalR` feature flag defaults to `false` (self-hosted hub, per ADR-0001). The Azure SignalR path targets the **local emulator only** and must be clearly commented as a stub.
- Public endpoints (check-in, upload, display) have no user auth but demonstrate lightweight hardening (CORS + anti-forgery/API-key pattern, documented honestly about what it does and doesn't protect). Staff endpoints use simple mock auth to model the internal-vs-public trust boundary — no real Entra ID.

## Workflow

- Work from the GitHub issues in this repo; they are ordered and declare dependencies. Reference the issue number in commits (`Closes #N`).
- Commit directly to `main` is fine for this POC unless the issue says otherwise; keep commits scoped to one issue.
- Before closing an issue, verify end-to-end: `aspire run` must still bring everything up, and the acceptance criteria listed on the issue must actually be observed, not assumed.
- If an architecture decision arises that the spec doesn't cover and there's a materially better option, ask the owner; otherwise use judgment and record it in `docs/decisions.md`.
