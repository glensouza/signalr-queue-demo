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

## 2026-07-10 — Broadcasts go through one `QueueBroadcaster` choke point, best-effort, at the endpoint layer

**Decision:** all three mutations broadcast `QueueUpdated` by calling a single injected `QueueBroadcaster.BroadcastAsync(update)` from `QueueEndpoints`, right after the successful `IQueueRepository` call returns — not from inside `SqliteQueueRepository`, and not by each handler touching `IHubContext` itself. `QueueBroadcaster` wraps `IHubContext<QueueHub, IQueueHubClient>`.

**Why the endpoint layer, not the repository:** `IQueueRepository` is deliberately storage-only (see its type doc) so the Azure Table Storage implementation (#4) stays a drop-in swap — it shouldn't need to know about SignalR. Broadcasting after the repository call also guarantees the push never fires until *after* `SaveChangesAsync` has committed, so a client that reacts by immediately calling `GET /queue/since/{seq}` always sees that change already persisted — no window where the broadcast outruns its data.

**Why one `QueueBroadcaster` and not three inline `IHubContext` calls:** the three handlers broadcast identically; a single method is the one place to change *how* broadcasting works — group/target filtering when the trust boundary lands (#6), the Azure SignalR path (#7), logging. The future Blazor `NotifyMutation` hub method (see "Blazor is self-encapsulated" below) calls the same broadcaster, so there's exactly one broadcast implementation regardless of what triggered the write.

**Why best-effort (a failed push never fails the caller):** the write is already committed by the time we broadcast, so `QueueBroadcaster` catches and logs a broadcast exception instead of letting it propagate. Letting it bubble would turn a successful check-in into an HTTP 500 whose caller retries — double-checking-in a visitor, or skipping a waiting person on a retried call-next. ADR-0001's "a notification must never silently drop" is honored not by failing the caller but by the catch-up protocol: the committed change is replayed via `GET /queue/since/{seq}` on the client's next reconnect.

**Known limitation, accepted on purpose:** two broadcasts from concurrent requests aren't guaranteed to arrive in sequence-number order — a check-in and a call-next landing at nearly the same instant can commit in one order but have their sends scheduled by the runtime in the other. Likewise a live `QueueUpdated` can reach a just-connected client before its `CurrentSequence` baseline, because SignalR admits a connection to the broadcast set before `OnConnectedAsync` runs. Both are made harmless the same way: `QueueHub`/`IQueueHubClient` XML docs tell clients to fold every sequence number in with `max()` — the sequence number, not arrival order, is authoritative — so `CurrentSequence` is a floor, not a reset. Serializing all broadcasts through one writer to force strict ordering would be unnecessary ceremony for a demo of dozens of concurrent connections, not thousands.

**Rejected:** a single background dispatcher/channel serializing all broadcasts — solves a problem (strict push ordering) the reconnect/catch-up protocol already solves a different, more robust way.

## 2026-07-10 — GET /queue/since/{seq} catch-up cutoff: 200 events, else a full snapshot

**Decision:** `SqliteQueueRepository.GetChangesSinceAsync` returns the raw diff of `QueueChangeEvent`s when there are 200 or fewer, and a full `QueueSnapshot` (`QueueChangesSinceResponse.IsSnapshot = true`) otherwise — same fallback for a sequence number that's negative or ahead of the latest one the server has ever issued (`MaxCatchUpEvents` in `SqliteQueueRepository`).

**Why 200:** arbitrary but generous for this POC's scale (dozens–hundreds of entries per day, per the ADR-0001 performance expectations) — a client would need to miss roughly a full day of check-in/call-next/complete activity to hit it. The point isn't the exact number, it's having *a* bound: past it, a snapshot is a single fixed-size payload instead of a diff that grows without limit the longer a client stays disconnected, and it's the same end state the client would end up computing from the diff anyway.

**Why an unrecognized sequence number gets the same treatment:** sequence numbers only start at 1 and increase, so a negative or future value can't have come from a client that legitimately talked to this server before — most likely a dev `.db` file reset since it last connected (see the `EnsureCreated` decision above). Returning an error here would force every client to special-case "my sequence number was rejected"; falling back to a snapshot means a client always gets *something* to resync from, no special-casing required.

**Rejected:** erroring (400/409) on an unrecognized sequence number — correct in spirit, but pushes the recovery logic onto every client instead of handling it once, in the one place that already knows how to build a snapshot.

## 2026-07-10 — Each mutation and the catch-up read run in one explicit transaction, for consistent reads

**Decision:** `SqliteQueueRepository` wraps each mutation (`CheckInAsync`, and the `RecordChangeAndBuildUpdateAsync` tail shared by call-next/complete) and the catch-up read (`GetChangesSinceAsync`) in a single explicit `BeginTransactionAsync`. Check-in also *counts the visitor's position after* the insert rather than before.

**Why (three concurrency bugs a code review surfaced):**
- **Position:** `position = count(Waiting)` used to run *before* the insert, in its own statement. Two simultaneous check-ins both read the same pre-insert count and were both told "you're #N". Counting *after* the insert, inside the write lock `SaveChangesAsync` already holds, makes the new entry part of the count and forces a concurrent check-in to be either already-committed-and-counted or still-blocked-on-the-lock — so positions come out distinct. Verified: 5 concurrent check-ins → positions 3,4,5,6,7, no collision.
- **Broadcast `Summary`:** the snapshot for the `QueueUpdated` payload is read *after* `SaveChangesAsync`. Without a transaction, a change committed by another request in between made the seq-N broadcast carry a `Summary` reflecting seq-N+1 state — a payload internally inconsistent with its own sequence number. Reading the snapshot while still holding the write lock pins it to exactly this change.
- **Catch-up read:** `GetChangesSinceAsync` issues `MAX`, `COUNT`, and the events fetch as separate statements. Without a transaction, a commit between them let the response's `SequenceNumber` lag the highest sequence number in its own `Changes` list. One read transaction gives all three queries a single consistent SQLite snapshot (WAL keeps a read transaction's view stable for its duration, and read transactions never block writers).

**Why this is cheap here:** it's one `BEGIN`/`COMMIT` per request over a handful of rows, riding on the WAL + `busy_timeout` setup already chosen above — a second writer waits the sub-millisecond for the first, no `SQLITE_BUSY`. Verified under a 5-way concurrent check-in burst: all `200`, no locks.

**Rejected:** leaving the reads in autocommit and documenting the races as "self-healing" — they mostly are (a client folding in `max()` recovers), but a reference implementation the vendor team copies shouldn't ship a broadcast payload that contradicts its own sequence number when making it consistent costs one transaction.

## 2026-07-10 — Blazor is self-encapsulated: shared library, not REST calls to the API

**Context:** `SignalRQueueDemo.Web` and `SignalRQueueDemo.ApiService` run as separate Aspire process resources. The build spec says Blazor should be self-encapsulated and make no calls to the API — check-in/call-next/complete reuse the same .NET code the API uses via a shared library, rather than Blazor acting as just another REST client like the Angular apps.

**Decision:** Blazor's check-in/call-next/complete pages call directly into a shared queue-service library that both `SignalRQueueDemo.ApiService` and `SignalRQueueDemo.Web` reference — no HTTP calls to the API's REST endpoints. Blazor still opens its own SignalR `HubConnection` to `QueueHub` (hosted in `ApiService`) for live updates, same as the original spec's "direct `HubConnection` since it's already .NET."

**Why:** self-encapsulation is the actual value proposition being evaluated for Blazor Server in this comparison — same-runtime code reuse with no network hop to its own sibling process, versus Angular which has no choice but to go over REST. Making Blazor call the API's REST endpoints instead would make it functionally indistinguishable from a same-origin Angular app and defeat the point of the comparison.

**Broadcast wrinkle, and how it's resolved:** `QueueUpdated` broadcasting lives in one place — `QueueBroadcaster`, invoked from `QueueEndpoints` (see "Broadcasts go through one `QueueBroadcaster` choke point" above) — specifically so `IQueueRepository` stays storage-only. Because `ApiService` and `Web` are separate processes, a Blazor-initiated write via the shared library commits to the database but doesn't reach that broadcast on its own — other connected clients (Angular apps, other Blazor sessions) would silently miss it. Fix: Blazor reuses the `HubConnection` it already holds for receiving pushes to also *send* — after a local write it invokes a hub method (e.g. `NotifyMutation(sequenceNumber)`); `QueueHub` handles that call server-side and pushes through the same `QueueBroadcaster` to every client exactly as an API-originated change would. This keeps the broadcast trigger inside SignalR rather than adding a REST call, so "no API calls" still holds.

**Rejected:** Blazor calling the REST API for mutations like Angular does — simpler to reason about, but throws away the reason to pick Blazor Server for this comparison in the first place.

**Follow-up for issue #13:** the queue-service/repository code (`IQueueRepository` and implementations) currently lives inside `SignalRQueueDemo.ApiService/Persistence`. Building Blazor's pages will require extracting that into a shared library both projects reference — noted here so it isn't a surprise when #13 starts.
