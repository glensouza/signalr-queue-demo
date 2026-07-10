# Decisions

One dated entry per architecture/implementation decision made along the way — what we chose, what we rejected, why. See `CLAUDE.md` § Workflow: "if an architecture decision arises that the spec doesn't cover and there's a materially better option, ask the owner; otherwise use judgment and record it here."

## 2026-07-10 — `POST /queue/call-next`, not `POST /queue/{id}/call-next`

**Context:** the original build spec wrote the call-next route as `POST /queue/{id}/call-next`, matching the shape of `POST /queue/{id}/complete`.

**Decision:** dropped the `{id}` segment — the route is `POST /queue/call-next`.

**Why:** call-next's entire job is to pick the next entry itself (oldest `Waiting`, by check-in time). The caller doesn't know which entry will be served next until the server tells them — there's no id to put in the URL. `complete` is different: the caller already knows which `Serving` entry they're finishing, so `{id}` stays there.

**Rejected:** keeping `{id}` and ignoring it server-side — adds a parameter that means nothing and invites a caller to (wrongly) assume they can pick who gets called next.

## 2026-07-10 — `EnsureCreatedAsync`, not EF Core migrations

**Decision:** `SqliteQueueRepository`'s `QueueDbContext` is stamped with `Database.EnsureCreatedAsync()` at startup (see `Program.cs`), not a migrations project.

**Why:** this is a POC scaffold with no schema history to preserve and no production deployment pipeline that needs incremental upgrades. Migrations require a design-time `IDesignTimeDbContextFactory`, a `Migrations/` folder, and `dotnet ef` tooling — all overhead with no payoff when every dev/demo run starts from a fresh (or seeded) `.db` file.

**Tradeoff, accepted on purpose:** `EnsureCreated` stamps the schema once and does nothing on later model changes — if a future issue adds a column, the already-created `App_Data/queue.db` won't pick it up. The fix in that case is deleting the local `.db` file (it's git-ignored and reseeds on next run), not migrating it. If this repo ever needs to preserve real data across schema changes, that's the trigger to switch to migrations.

**Rejected:** EF Core migrations — correct for production, unnecessary ceremony for a reference POC whose database is disposable by design.

## 2026-07-10 — SQLite `busy_timeout` + WAL on every connection

**Decision:** a `QueueConnectionInterceptor` runs `PRAGMA busy_timeout = 5000` and `PRAGMA journal_mode = WAL` on every connection open (registered via `AddInterceptors` in `Program.cs`).

**Why:** the API gives each HTTP request its own scoped `DbContext`/connection, and the demo deliberately drives concurrent writes — a kiosk checking in while staff calls the next entry, across the Angular apps and Blazor at once. Without `busy_timeout`, Microsoft.Data.Sqlite fails the losing writer of any overlap immediately with "database is locked" (SQLITE_BUSY) → an unhandled HTTP 500. A short busy timeout makes the second writer wait the (sub-millisecond) time for the first to finish instead. WAL additionally lets the queue-display board's reads run without blocking the writer. Verified with a 20-request concurrent write burst: all `200`, no locks.

**Why an interceptor, not a startup call:** `busy_timeout` is per-connection (not stored in the file), so a one-time PRAGMA at startup would be silently lost on every pooled connection opened afterward.

**Rejected:** relying on the driver default (0 = fail immediately) — surfaces as intermittent 500s during exactly the live demo scenario the POC exists to show.
