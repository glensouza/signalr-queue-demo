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

**Tradeoff, accepted on purpose:** `EnsureCreated` stamps the schema once and does nothing on later model changes — if a future change adds a column, the already-created `App_Data/queue.db` won't pick it up. The fix in that case is deleting the local `.db` file (it's git-ignored and reseeds on next run), not migrating it. If this repo ever needs to preserve real data across schema changes, that's the trigger to switch to migrations.

**Rejected:** EF Core migrations — correct for production, unnecessary ceremony for a reference POC whose database is disposable by design.

## 2026-07-10 — SQLite `busy_timeout` + WAL on every connection

**Decision:** a `QueueConnectionInterceptor` runs `PRAGMA busy_timeout = 5000` and `PRAGMA journal_mode = WAL` on every connection open (registered via `AddInterceptors` in `Program.cs`).

**Why:** the API gives each HTTP request its own scoped `DbContext`/connection, and the demo deliberately drives concurrent writes — a kiosk checking in while staff calls the next entry, across the Angular apps and Blazor at once. Without `busy_timeout`, Microsoft.Data.Sqlite fails the losing writer of any overlap immediately with "database is locked" (SQLITE_BUSY) → an unhandled HTTP 500. A short busy timeout makes the second writer wait the (sub-millisecond) time for the first to finish instead. WAL additionally lets the queue-display board's reads run without blocking the writer. Verified with a 20-request concurrent write burst: all `200`, no locks.

**Why an interceptor, not a startup call:** `busy_timeout` is per-connection (not stored in the file), so a one-time PRAGMA at startup would be silently lost on every pooled connection opened afterward.

**Rejected:** relying on the driver default (0 = fail immediately) — surfaces as intermittent 500s during exactly the live demo scenario the POC exists to show.

## 2026-07-10 — Broadcasts go through one `QueueBroadcaster` choke point, best-effort, at the endpoint layer

**Decision:** all three mutations broadcast `QueueUpdated` by calling a single injected `QueueBroadcaster.BroadcastAsync(update)` from `QueueEndpoints`, right after the successful `IQueueRepository` call returns — not from inside `SqliteQueueRepository`, and not by each handler touching `IHubContext` itself. `QueueBroadcaster` wraps `IHubContext<QueueHub, IQueueHubClient>`.

**Why the endpoint layer, not the repository:** `IQueueRepository` is deliberately storage-only (see its type doc) so the Azure Table Storage implementation stays a drop-in swap — it shouldn't need to know about SignalR. Broadcasting after the repository call also guarantees the push never fires until *after* `SaveChangesAsync` has committed, so a client that reacts by immediately calling `GET /queue/since/{seq}` always sees that change already persisted — no window where the broadcast outruns its data.

**Why one `QueueBroadcaster` and not three inline `IHubContext` calls:** the three handlers broadcast identically; a single method is the one place to change *how* broadcasting works — group/target filtering when the trust boundary lands, the Azure SignalR path, logging. The future Blazor `NotifyMutation` hub method (see "Blazor is self-encapsulated" below) calls the same broadcaster, so there's exactly one broadcast implementation regardless of what triggered the write.

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

**Follow-up for the Blazor work:** the queue-service/repository code (`IQueueRepository` and implementations) currently lives inside `SignalRQueueDemo.ApiService/Persistence`. Building Blazor's pages will require extracting that into a shared library both projects reference — noted here so it isn't a surprise when that work starts.

## 2026-07-10 — Table Storage: gap-free sequence via optimistic insert, position via an ETag counter, both reconciled at startup

**Context:** `SqliteQueueRepository`'s monotonic sequence number comes for free from SQLite's autoincrement rowid, generated inside the same `SaveChangesAsync` transaction as the entry mutation it belongs to (see the `EnsureCreatedAsync`/transaction decisions above); it is gap-free and, because that transaction also brackets the catch-up read, a client's catch-up baseline can never run ahead of the events it was handed. Check-in's reported position similarly relies on that transaction's write lock to count `Waiting` rows consistently. Azure Table Storage has no autoincrement and no cross-table transactions, so `TableStorageQueueRepository` needed different mechanisms — and a first cut using an ETag-incremented counter for the sequence number turned out to reintroduce exactly the anomaly SQLite's transaction prevents (see below).

**Decision — sequence numbers by optimistic insert into the change-event log, not a counter.** `AppendChangeEventAsync` allocates a number by *inserting the change-event row itself*: take the next candidate, `AddEntity` it (its zero-padded RowKey is the number), and on `409 Conflict` (a concurrent writer already took that number) advance and retry. A number therefore cannot exist until its row does, which makes the log **gap-free and strictly in-order** — event N cannot be written until N-1 exists to conflict against. A `Sequence` counter row survives only as an O(1) *start hint* so allocation needn't scan the log; it may lag the true max but never leads it, so a stale hint costs at most a couple of extra retries, never a wrong or skipped number.

**Why the counter-based first cut was wrong (the bug this replaced):** incrementing a `Sequence` counter and *then* writing the event row are two non-atomic steps. A concurrent `GetChangesSinceAsync` could read the counter at N while event N's row wasn't written yet — and, because two writers' event-row writes race, event N could even be persisted before event N-1. Either way the catch-up read could return a `SequenceNumber` ahead of (or with a gap below) the `Changes` it actually contained; a reconnecting client that folds that number in as its new baseline **permanently skips the missing event**. SQLite avoids this with its transaction; the counter did not. Optimistic-insert allocation removes the failure at the root (the log is always gap-free and in-order), and `GetChangesSinceAsync` now derives the returned `SequenceNumber` from the last event it actually returns, never from the hint — so it can never hand back a baseline ahead of the diff. Verified against a live Azurite emulator: under 20 concurrent check-ins interleaved with catch-up reads, every non-empty diff's `SequenceNumber ≤ max(seq in Changes)` and the log stayed contiguous `1..N`.

**Decision — check-in position via an ETag counter, closing the earlier position race.** Position is the running `WaitingCount` singleton row, adjusted by `AdjustCounterAsync` (read value + ETag, `UpdateEntity(ifMatch: ETag)`, retry on `412`). This replaced a query-time `COUNT(Waiting)` that two simultaneous check-ins could both read pre-insert and both report the same position; the ETag precondition now serializes concurrent check-ins into distinct positions. Verified: 5 concurrent check-ins on an empty queue → positions exactly 1–5, both via direct calls and via concurrent HTTP `POST /checkin` against the running `aspire run` stack.

**Contention bound + backoff.** All of these retry loops share `MaxOptimisticConcurrencyAttempts = 32`. The binding case is the single hot `WaitingCount` row: N truly-simultaneous check-ins each need up to N ETag retries (one winner per round), so the bound is the largest instantaneous check-in burst the position counter tolerates — 32 is comfortable headroom over this POC's handful of kiosks. Retries also jitter-back off (`BackoffAsync`) so contenders de-synchronize instead of thundering-herd re-colliding. (A 20-way burst exhausted an earlier bound of 10; 32 + backoff handles it cleanly.)

**Residual crash-window drift, and how it's healed.** `WaitingCount` (and the `Sequence` hint) can still drift if a caller commits a counter update and then crashes before writing the row it was for — a single-`await`-wide window. Rather than accept that as a standing inconsistency, `TableStorageQueueSeedData.EnsureTablesAndSeedAsync` **reconciles both counters to the real persisted state on every startup** (`WaitingCount` = actual count of `Waiting` rows, `Sequence` hint = actual max event number), single-threaded before the app serves traffic. On a normal restart it's a harmless idempotent recompute; after a crash it self-heals. The sequence *authority* is the gap-free log regardless, so a lagging hint is also self-correcting at runtime. Verified: corrupting `WaitingCount` to 999 and the `Sequence` hint to 1, then re-running startup, restored both to the real values and the next check-in reported a correct position and `maxSeq+1`.

**Same ETag pattern reused for call-next/complete, on the entry row itself:** two staff members racing to call the same next entry — the loser's `UpdateEntity` gets `412`, and retrying re-runs "find the oldest Waiting entry", which now skips the just-served row and picks the next. Verified with a 5-way concurrent `CallNextAsync` burst (direct and HTTP): all 5 succeeded, 5 distinct entries, no double-serve.

**Rejected:** (1) the ETag-counter sequence number — reintroduced the catch-up anomaly above. (2) A per-request distributed lock (e.g. a lease blob) to fully serialize writes like SQLite's file lock — defeats the point of demonstrating Table Storage (no lock infrastructure to run), and optimistic insert + the ETag position counter already give the guarantees that matter for less machinery.

## 2026-07-10 — Document metadata gets its own `IDocumentRepository`, backed by whichever store `IQueueRepository` is using

**Context:** document metadata (display filename, content type, size, uploaded-at) needs to be tracked "alongside the queue entry so the staff console can list without hitting blob storage."

**Decision:** a new `IDocumentRepository` interface, separate from `IQueueRepository`, with a `SqliteDocumentRepository` (sharing `QueueDbContext`/connection with `SqliteQueueRepository`) and a `TableStorageDocumentRepository` (a new `QueueDocuments` table, partitioned by entry id, alongside `TableStorageQueueRepository`'s three tables). Registered in `Program.cs`'s existing `Persistence:Provider` switch, one new line per case.

**Why "alongside the entry" means "same database, own interface," not "same interface":** literal — the metadata store for a given provider is the same SQLite file or the same Azurite Table Storage account the queue entry lives in, so a staff console list is one local read, never a live Blob container enumeration. It's a separate *interface* from `IQueueRepository`, though, because document metadata isn't part of the queue mutation/broadcast pipeline `IQueueRepository` exists to abstract (no sequence number, no `QueueUpdated` broadcast) — folding it in would make that interface responsible for two unrelated concerns and complicate a future backend swap that only wants to change one of them.

**Rejected:** deriving the staff document list from a live Blob Storage container listing instead of a metadata store — the requirement explicitly rules this out ("so the staff console can list without hitting blob storage"), and it would also lose the display filename/content-type/size fields Blob Storage's own listing API doesn't return without a per-blob properties fetch.

## 2026-07-10 — Blob Storage: container-per-entry, always-randomized blob names, blob write before metadata write

**Decision:** `DocumentBlobStore` creates one Blob container per queue entry (`docs-{entryId}`), lazily on first upload for that entry — not one shared container for every document, and not pre-provisioned per check-in. Every blob name is a fresh `Guid.NewGuid()` (`DocumentBlobStore.NewBlobName`), never the client-supplied filename. The upload endpoint writes the blob first, then the `IDocumentRepository` metadata row.

**Why container-per-entry:** scopes the storage boundary to the unit a future authorization check would actually reason about ("can this staff session see this visitor's documents"), so that boundary is cheap to enforce later even though this implementation itself ships no per-container ACLs — the alternative (one shared container, one metadata table doing all the filtering) would put 100% of the access-control burden on code that can be bypassed by talking to Blob Storage directly.

**Why always-randomized blob names:** a client-controlled storage path on a public, unauthenticated upload endpoint is a path-traversal and same-name-collision vector — "never trust the client filename" applies to more than just cosmetic file naming. The original filename survives only as `DocumentMetadata.FileName`, shown to staff, never used to address storage.

**Why blob-before-metadata, not metadata-before-blob or a two-phase write:** Blob Storage and the metadata store aren't written transactionally together (different services, no distributed transaction), so one order has to be "less bad" than the other. Blob-first means a metadata-write failure after a successful upload leaves an orphaned, harmless, cleanable blob nobody will ever list. Metadata-first would instead let a subsequent blob-write failure leave a metadata row promising content that was never stored — a staff member sees the document in the list, opens it, and gets a 500 (`DocumentBlobStore.OpenReadAsync` returning null is exactly this case, reported distinctly from "document doesn't exist"). The first failure mode is invisible to staff; the second isn't.

**Rejected:** a two-phase commit / saga across Blob Storage and the metadata store to make the pairing atomic — real engineering for a real DMS integration, but out of proportion to a POC whose blob-first ordering already makes the one failure mode that can occur harmless to the staff-facing experience.

## 2026-07-10 — Upload content-type allowlist checked against the client's `Content-Type` header, not file magic bytes

**Decision:** `POST /checkin/{id}/documents` rejects any upload whose reported `Content-Type` isn't `application/pdf`, `image/jpeg`, or `image/png`, checked against the header the client sends with the multipart part — not by sniffing the file's actual bytes for a PDF/JPEG/PNG signature.

**Why:** a header check is a couple of lines and catches the overwhelmingly common case (a legitimate kiosk client sending a real content type for a real file). Byte-sniffing catches a client that lies about its own header, but for this POC's threat model — a public, no-real-court-data demo endpoint, not a production DMS integration — that's added complexity (a small format-detection library or hand-rolled magic-byte table) for a risk this reference implementation would rather document honestly than half-solve.

**Documented limitation, not a silent gap:** a malicious client can still send `Content-Type: application/pdf` with, say, an executable's bytes inside; the allowlist check will pass it. This is called out at the validation site in `DocumentEndpoints.cs` and here, matching the project's "raise the bar, document what it doesn't do" theme already established for the public-endpoint hardening.

**Rejected:** magic-byte sniffing at upload time — the more correct answer for a production DMS-facing endpoint, and worth revisiting if this POC's upload path is ever adapted past reference-implementation status.

## 2026-07-10 — Document viewing endpoints ship unauthenticated; mock staff auth gates them separately

**Decision:** `GET /queue/{id}/documents` and `GET /queue/{id}/documents/{docId}` have no auth check in this change — a `TODO` comment on `DocumentEndpoints.MapDocumentEndpoints` marks them for the `X-Staff-Key` endpoint filter that the mock staff-auth work adds.

**Why:** the mock staff-auth filter is built as its own piece of work and already covers document viewing among the endpoints it gates (alongside call-next/complete) — building a second, throwaway auth mechanism here just to have *something* would mean that filter immediately replaces it, doubling the work for no benefit. These endpoints are designed to sit behind that shared filter, and the endpoint list is coordinated with it.

**Rejected:** inventing a lightweight ad hoc check (e.g. a hardcoded header) scoped to just these two endpoints — indistinguishable from real auth to a reader skimming the code, and directly contradicts CLAUDE.md's documentation standard of never leaving a security-relevant gap undocumented.

## 2026-07-11 — `POST /checkin/{id}/documents` calls `.DisableAntiforgery()`

**Context:** verifying the document upload end-to-end against a live Azurite emulator, every upload 500'd with `System.InvalidOperationException: ... contains anti-forgery metadata, but a middleware was not found that supports anti-forgery.` ASP.NET Core's minimal APIs auto-attach antiforgery-validation metadata to any endpoint binding `IFormFile`, and refuse to serve the route at all unless `app.UseAntiforgery()` (ASP.NET Core's cookie-based antiforgery system) is registered.

**Decision:** `POST /checkin/{id}/documents` calls `.DisableAntiforgery()` to opt out of that built-in system, with an inline comment at the call site explaining why.

**Why not just add `app.UseAntiforgery()` instead:** ASP.NET Core's built-in antiforgery system is cookie-based — it pairs a same-site cookie with a token, which assumes a browser session with cookies flowing back to the same origin. The public-endpoint hardening work already specifies a different, stateless mechanism for this exact endpoint: a short-lived HMAC token from `GET /checkin/token` that the kiosk fetches and echoes back on `POST`, chosen specifically because a public kiosk SPA (behind a different origin, per the CORS hardening in the same work) can't be assumed to carry a same-site cookie. Wiring up the built-in system now would mean ripping it back out for that token instead, for zero benefit in between.

**Verified:** caught by manually exercising the upload endpoint against a live Azurite container (`docker ps` showed the emulator already running from a prior `aspire run`) before this fix, and confirmed fixed by re-running the same request afterward — this is exactly the kind of gap `dotnet build` alone doesn't catch, since the failure only happens at request-routing time, not compile time.

**Rejected:** leaving `app.UseAntiforgery()` unregistered and hoping the later hardening work catches this — would have shipped the document upload with its own acceptance criterion ("Upload via `.http` file succeeds") unverified and failing.

## 2026-07-11 — Upload hardening: a buffering backstop and a per-entry document cap, on top of the per-file size check

**Context:** the per-file size check (`IFormFile.Length > 10 MB → 400`) has two gaps on a public, unauthenticated endpoint. First, `IFormFile.Length` isn't known until the framework has *already buffered the whole body*, so the check bounds what reaches Blob Storage but not how much a caller can make the server spool per request. Second, nothing bounds the *number* of uploads against a single entry, so an actor who learns one entry id (returned in plaintext by `GET /queue`) could push unlimited 10 MB blobs against it.

**Decision — a buffering backstop via `WithFormOptions`.** The upload endpoint sets `bufferBodyLengthLimit` and `multipartBodyLengthLimit` to the file cap plus 64 KB of envelope headroom. A grossly oversized body is now cut off by the form reader *before* the handler runs, instead of spooled in full. Because that path throws inside form binding (an `InvalidDataException` the framework wraps in a `BadHttpRequestException`) rather than reaching the handler's tidy check, a small `UploadLimitExceptionHandler` maps it to a clean `413 Payload Too Large` `ProblemDetails` instead of the opaque 500 it would otherwise produce. The handler's own `Length` check still runs for a file only modestly over the cap, giving the more precise "exceeds the 10 MB upload limit" message; the backstop covers the far larger bodies that never reach it.

**Decision — a soft per-entry document cap (`MaxDocumentsPerEntry = 10`).** The upload endpoint counts an entry's existing documents and returns `409 Conflict` once it is at capacity. Deliberately *soft* — checked, not transactionally enforced, so two truly simultaneous uploads could both pass it — because a hard guarantee would need a transaction across the metadata store for no real benefit at this scale, where a visitor legitimately attaches at most a handful of supporting documents. The point is a bound on the storage-exhaustion vector, not an exact invariant.

**Why these are honest, documented limits, not security theater:** the buffering backstop still lets a caller spend the ~10 MB headroom per request, and the per-entry cap is soft — both are POC-appropriate bars that raise the cost of abuse without claiming to be airtight, consistent with the rest of the public-endpoint hardening.

**Rejected:** raising the per-file cap into the buffering limit so the two never disagree — would let the clean, precise in-handler message cover more of the range but leave the buffering unbounded, which is the exact gap the backstop exists to close. Rejected a hard, transactional per-entry cap — real machinery for a bound that a soft check already delivers at this scale.
