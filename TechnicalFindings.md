# Technical Findings

## Module: FleetManagement (Edge Agent Registration, Telemetry, Monitoring)

---

### FM-T01: GetAgents executes COUNT(*) on every page request
- **Severity**: Medium
- **Location**: `AgentsController.cs:104`
- **Trace**: Portal → `GET /api/v1/agents` → `query.CountAsync()`
- **Description**: The `GetAgents` endpoint executes a full `COUNT(*)` on the filtered query for `totalCount` alongside the paged data fetch. For large fleets with complex filters (especially the connectivity state subquery), this adds a second full table scan per request. Since the agent list auto-refreshes every 30 seconds, this doubles the query load on every refresh.
- **Impact**: Unnecessary database load. Cursor-based pagination doesn't need a total count for "load more" UX patterns.

### FM-T02: Telemetry snapshot query in GetAgents uses `.Contains()` on in-memory list of IDs
- **Severity**: Medium
- **Location**: `AgentsController.cs:119`
- **Trace**: Portal → `GET /api/v1/agents` → snapshot dictionary lookup
- **Description**: The code builds a dictionary of telemetry snapshots by querying `page.Select(agent => agent.Id).Contains(snapshot.DeviceId)`. Because `page` is a materialized `List<AgentRegistration>`, EF Core translates this to a `WHERE device_id IN (...)` clause with up to 501 literal GUID parameters. This is functionally correct but generates large SQL statements on every page.
- **Impact**: Increased SQL parsing overhead and plan cache pollution for pages with many agents.

### FM-T03: GetEvents uses in-memory filtering loop with 200-row batches for single-device events
- **Severity**: Medium
- **Location**: `AgentsController.cs:286-357`
- **Trace**: Portal → `GET /api/v1/agents/{id}/events` → while-loop batching
- **Description**: The events endpoint fetches audit events in batches of 200, deserializes each JSON payload, extracts the deviceId, and filters in memory. If a site has many agents generating events, this loop could iterate many batches before finding 20 events for a specific device. The `WHERE` clause only filters by `LegalEntityId`, `SiteCode`, and event type prefix — not by deviceId.
- **Impact**: O(N) scan over all audit events for a site to find events for one device. For high-traffic sites, this can produce slow responses and heavy DB read load.
- **Fix**: Add `deviceId` as an indexed column on `audit_events` or store it as a top-level field instead of buried in JSON payload.

### FM-T04: SubmitTelemetryHandler stores full telemetry payload as JSON + duplicates all fields individually
- **Severity**: Low
- **Location**: `SubmitTelemetryHandler.cs:100-127`
- **Trace**: Device → `POST /api/v1/agent/telemetry` → `SubmitTelemetryHandler`
- **Description**: The handler stores the entire telemetry payload in `PayloadJson` and then also copies 12+ individual fields (battery, connectivity, buffer depth, FCC host/port, etc.) to separate columns on the snapshot entity. The individual columns are used for filtering/display, but the full JSON is also stored for the detail view.
- **Impact**: Double storage per telemetry snapshot. Not a bug, but increases table bloat over time.

### FM-T05: SubmitTelemetryHandler creates one audit event per telemetry report
- **Severity**: Medium
- **Location**: `SubmitTelemetryHandler.cs:88-98`
- **Trace**: Device → `POST /api/v1/agent/telemetry` → audit event insert
- **Description**: Every telemetry submission (typically every 30-60 seconds per device) creates a full audit event row with a serialized JSON envelope. For a fleet of 100 devices, this produces ~144K-288K audit rows per day. The idempotency check on `correlationId` prevents duplicates from retries but doesn't throttle the volume of unique events.
- **Impact**: Rapid audit table growth. This is the highest-volume event source in the system.

### FM-T06: Agent detail forkJoin fails entirely if any one call fails
- **Severity**: Medium
- **Location**: `agent-detail.component.ts:695-705`
- **Trace**: Portal → `forkJoin({ registration, telemetry, events })` → catchError
- **Description**: The agent detail page uses `forkJoin` to load registration, telemetry, and events in parallel. If any one request fails (e.g., telemetry returns 404 for a newly registered device that hasn't reported yet), the entire `forkJoin` errors and the `catchError` branch sets `error=true`, hiding all data including the registration and events that may have succeeded.
- **Impact**: Newly registered devices with no telemetry show a generic error page instead of partial data.
- **Fix**: Handle each observable independently or use `catchError` within each inner observable to allow partial results.

### FM-T07: `IgnoreQueryFilters()` used inconsistently across portal endpoints
- **Severity**: Low
- **Location**: `AgentsController.cs:68-70` (does NOT use it on AgentRegistrations initially), `AgentsController.cs:177` (DOES use it on GetAgentById)
- **Description**: `GetAgents` uses `.ForPortal(access, legalEntityId)` which may or may not apply `IgnoreQueryFilters()`. `GetAgentById` explicitly calls `IgnoreQueryFilters()`. `GetTelemetry` and `GetEvents` also use `IgnoreQueryFilters()`. If there are global query filters (e.g., for soft-delete or tenant isolation), `GetAgents` might return a different set of agents than `GetAgentById`, which always bypasses filters.
- **Impact**: Potential inconsistency where an agent visible via direct ID lookup is not visible in the list, or vice versa.

---

## Module: Transactions

---

### TX-T01: IngestTransactionHandler uses two separate SaveChangesAsync calls — split-brain risk
- **Severity**: High
- **Location**: `IngestTransactionHandler.cs:241, 269-270`
- **Trace**: Ingestion → `SaveChangesAsync()` (transaction + outbox) → `MatchAsync()` → `SaveChangesAsync()` (reconciliation)
- **Description**: The ingestion handler commits the transaction entity and outbox message in the first `SaveChangesAsync` (line 241), then runs reconciliation matching, then calls `SaveChangesAsync` again (line 270) to persist match results. These are two separate database commits — not a single atomic operation. If the second save fails (DB timeout, connection drop, process crash), the transaction is committed but reconciliation results are lost with no retry mechanism. The outbox pattern ensures the `TransactionIngested` event is published, but the reconciliation matching is fire-and-forget within the same request.
- **Impact**: Reconciliation records may be silently lost under transient failures, creating phantom unmatched exceptions that require manual investigation.
- **Fix**: Either combine both operations into a single `SaveChangesAsync`, or move reconciliation matching to an outbox-driven worker that retries on failure.

### TX-T02: Batch ingestion processes up to 500 items sequentially — O(N) latency
- **Severity**: High
- **Location**: `TransactionsController.cs:432-490` (`IngestBatchAsync`)
- **Trace**: FCC Push → `POST /api/v1/transactions/ingest` (with `transactions[]` array) → `IngestBatchAsync()` → sequential loop
- **Description**: When a batch payload contains multiple transactions, the controller iterates sequentially through each item (line 432), calling `SendIngestCommandAsync` for each one. Each iteration executes the full pipeline: adapter validation → normalization → Redis dedup → DB fuzzy match → S3 archive → DB persist → Redis cache set → reconciliation match. For a batch of 500 items, this creates 500 sequential MediatR dispatches with at minimum 500 DB round-trips (dedup + persist + reconciliation per item), 500 Redis operations, and 500 S3 puts.
- **Impact**: A 500-item batch at ~50ms per item would take ~25 seconds to complete, likely exceeding HTTP timeout. During this time, the request holds a DB connection and blocks the calling thread.
- **Fix**: Consider parallel processing with bounded concurrency (e.g., `Parallel.ForEachAsync` with `MaxDegreeOfParallelism`), or use a batched DB insert strategy.

### TX-T03: Portal transaction DTO projection is duplicated verbatim between list and detail endpoints
- **Severity**: Medium
- **Location**: `OpsTransactionsController.cs:176-207` (GetTransactions), `OpsTransactionsController.cs:271-302` (GetTransactionById)
- **Trace**: Portal → `GET /api/v1/ops/transactions` / `GET /api/v1/ops/transactions/{id}`
- **Description**: The `PortalTransactionDto` projection (`new PortalTransactionDto { Id = item.Id, ... }`) is copy-pasted across both endpoints with 25+ identical field assignments. If a new field is added to the Transaction entity and only updated in one projection, the list and detail views will show different data.
- **Impact**: Maintenance burden and divergence risk. Adding `RawPayloadJson` to the detail view but not the list (intentionally `null` in both now) is already a latent inconsistency.
- **Fix**: Extract the projection into a shared method or expression.

### TX-T04: OpsTransactionsController falls back to OFFSET pagination for non-default sort fields
- **Severity**: Medium
- **Location**: `OpsTransactionsController.cs:167-171`
- **Trace**: Portal → `GET /api/v1/ops/transactions?sortField=fccTransactionId&cursor=X`
- **Description**: When `sortField` is anything other than `completedAt` (or null), the controller uses OFFSET-based pagination via `DecodeOffset(cursor)` / `query.Skip(skip)`. For large result sets, OFFSET/LIMIT degrades to O(N) as the database must scan and discard `skip` rows before returning the page. At offset 10,000 on a table with millions of rows, this produces a slow sequential scan.
- **Impact**: Portal transaction browsing becomes progressively slower as users navigate to later pages when sorting by fccTransactionId, siteCode, volume, amount, or status.

### TX-T05: Content type detection relies on first character heuristic
- **Severity**: Low
- **Location**: `TransactionsController.cs:502-504`
- **Trace**: `SendIngestCommandAsync()` → `rawPayload.TrimStart().StartsWith('<')`
- **Description**: The content type is inferred by checking if the trimmed payload starts with `<`. While this correctly distinguishes XML from JSON in practice (JSON always starts with `{` or `[`), it couples a parsing assumption to a presentation-layer heuristic. A payload that starts with a BOM or unexpected whitespace character could be misclassified.
- **Impact**: Low practical risk since all known vendor payloads are well-formed, but the heuristic is fragile for future vendors.

### TX-T06: Edge agent CloudUploadWorker halves batch size on 413 but never logs the original size
- **Severity**: Low
- **Location**: `CloudUploadWorker.kt` — 413 PayloadTooLarge handling
- **Trace**: Edge Agent → `CloudUploadWorker` → upload → 413 → halve batch size
- **Description**: When the cloud API returns 413, the worker halves `currentBatchSize` and retries. However, the original batch size that triggered the 413 is not logged, making it difficult to tune the `maxBatchSize` configuration or diagnose sizing issues. The batch size resets to the configured maximum on the next successful upload cycle.
- **Impact**: Operational observability gap — hard to determine optimal batch sizes from logs.

### TX-T07: Redis dedup cache population is fire-and-forget after DB commit
- **Severity**: Low
- **Location**: `IngestTransactionHandler.cs:273-275`
- **Trace**: Ingestion → `SaveChangesAsync()` → `SetCacheAsync()` (Redis)
- **Description**: After committing the transaction to PostgreSQL, the handler populates the Redis dedup cache. If Redis is unavailable at this moment, the cache is not populated and subsequent dedup checks for this transaction will fall back to the slower PostgreSQL query path. The `SetCacheAsync` method catches and logs the exception (non-fatal), but the cache miss window persists until the next ingestion attempt for the same key.
- **Impact**: Transient Redis outages cause cache misses, increasing DB load for dedup checks during the outage window.

---

## Module: Reconciliation

---

### RC-T01: GetExceptions controller queries DB directly — bypasses MediatR handler that implements the same logic
- **Severity**: High
- **Location**: `OpsReconciliationController.cs:77-203`, `GetReconciliationExceptionsHandler.cs`
- **Trace**: Portal → `GET /api/v1/ops/reconciliation/exceptions` → inline DB query (NOT via MediatR)
- **Description**: The `GetExceptions` endpoint queries `_db.ReconciliationRecords` directly with inline LINQ, pagination, and enrichment logic (~130 lines). A separate `GetReconciliationExceptionsHandler` exists implementing the same query via `IReconciliationDbContext.FetchExceptionsPageAsync()`. The controller does NOT dispatch a `GetReconciliationExceptionsQuery` through MediatR. Two independent implementations exist with: (1) different cursor encoding — controller uses `PortalCursor.Encode/TryDecode`, handler uses its own Base64 encoding with `|` separator; (2) different enrichment — controller joins PreAuth and Transaction tables for `CurrencyCode`, `RequestedAmount`, `OdooOrderId`; handler returns raw projection. Any bug fix applied to one implementation won't affect the other.
- **Impact**: Maintenance divergence risk. Changes to query logic, filtering, or enrichment must be synchronized across two code paths.
- **Fix**: Either route the controller through MediatR, or remove the unused handler.

### RC-T02: No optimistic concurrency control on ReconciliationRecord — concurrent reviews cause last-write-wins
- **Severity**: High
- **Location**: `ReviewReconciliationHandler.cs:40-105`, `ReconciliationRecord.cs` (no RowVersion property)
- **Trace**: Portal → `POST .../approve` → handler → load → check status → modify → save (no concurrency token)
- **Description**: The `ReviewReconciliationHandler` loads a reconciliation record via `FindByIdAsync`, checks that `Status == VARIANCE_FLAGGED`, modifies the entity, publishes a domain event, then calls `SaveChangesAsync`. The `ReconciliationRecord` entity has no `RowVersion` or concurrency token. If two administrators simultaneously approve and reject the same record: both load the record with status `VARIANCE_FLAGGED`, both pass the status check, both publish their respective domain events (`ReconciliationApproved` and `ReconciliationRejected`), and both save. The second save silently overwrites the first. Result: the audit trail contains both an `APPROVED` and a `REJECTED` event, but the DB record reflects only the last write.
- **Impact**: Data integrity violation — contradictory audit events for the same record, with the final state determined by timing rather than business logic. In a compliance context, this could mean an approved variance appears rejected (or vice versa) with no error surfaced.
- **Fix**: Add a `RowVersion` concurrency token to `ReconciliationRecord` and configure it in EF Core. On `DbUpdateConcurrencyException`, return a 409 Conflict.

### RC-T03: Matching service uses `transaction.CompletedAt` without null guard in time-window query
- **Severity**: Medium
- **Location**: `ReconciliationMatchingService.cs:313-314`
- **Trace**: Ingestion → `MatchAsync()` → `ResolveCandidateAsync()` → `CompletedAt.AddMinutes()`
- **Description**: The pump+nozzle+time matching strategy calls `transaction.CompletedAt.AddMinutes(-window)` and `transaction.CompletedAt.AddMinutes(window)`. The `Transaction` entity's `CompletedAt` property is `DateTimeOffset?` (nullable) as evidenced by the DTO mapping `CompletedAt = transaction.CompletedAt`. If a transaction has a null `CompletedAt` (e.g., a transaction still in progress, or a vendor that doesn't populate the field), this code throws `NullReferenceException`. The first matching strategy (correlation ID) runs first and may short-circuit, but the time-window strategy has no guard.
- **Impact**: NRE crash during reconciliation matching for transactions without a `CompletedAt` value. The ingestion pipeline's outer catch would swallow this, but the reconciliation record would not be created.
- **Fix**: Skip the time-window strategy if `transaction.CompletedAt` is null.

### RC-T04: UnmatchedReconciliationWorker loads site context separately for every record in the batch
- **Severity**: Medium
- **Location**: `UnmatchedReconciliationWorker.cs:79-97`, `ReconciliationMatchingService.cs:152-156`
- **Trace**: Worker → `ProcessBatchAsync()` → foreach → `RetryUnmatchedAsync()` → `FindSiteContextAsync()` (per record)
- **Description**: The worker processes up to 500 unmatched records per batch. For each record, `RetryUnmatchedAsync` calls `FindSiteContextAsync` which queries the database for site configuration. If 200 records belong to the same site, 200 identical queries are executed. No in-memory caching of site context is applied within a batch.
- **Impact**: O(N) redundant DB queries per batch where N could be up to 500. Most batches likely contain records from a small number of sites.
- **Fix**: Cache `ReconciliationSiteContext` per `(legalEntityId, siteCode)` within each batch processing cycle.

### RC-T05: GetExceptions enrichment queries use `IgnoreQueryFilters()` without tenant scope check
- **Severity**: Medium
- **Location**: `OpsReconciliationController.cs:130-140`
- **Trace**: Portal → `GetExceptions` → `_db.PreAuthRecords.IgnoreQueryFilters()` + `_db.Transactions.IgnoreQueryFilters()`
- **Description**: After fetching the tenant-scoped reconciliation records via `.ForPortal(access, legalEntityId)`, the controller fetches related PreAuth and Transaction records using `IgnoreQueryFilters()` and matching by IDs. While the IDs come from already-scoped records (so the data is transitively scoped), the enrichment queries themselves bypass all global query filters. If a reconciliation record's `PreAuthId` or `TransactionId` somehow points to an entity in a different tenant (data corruption), the cross-tenant data would be returned without access checks.
- **Impact**: Defense-in-depth concern — relies on referential integrity for tenant isolation rather than explicit checks.

### RC-T06: `VariancePercent` stored as percentage but named ambiguously — conversion to BPS in DTO is fragile
- **Severity**: Low
- **Location**: `ReconciliationMatchingService.cs:411`, `OpsReconciliationController.cs:169, 366`
- **Trace**: Matching → `variancePercent = absoluteVariance * 100 / authorizedAmount` → stored → DTO → `VarianceBps = VariancePercent * 100`
- **Description**: `VariancePercent` is stored as a percentage (e.g., 2.5 meaning 2.5%). The DTO conversion `VariancePercent * 100` converts to basis points (250 BPS). The column name `variance_percent` and property name `VariancePercent` don't indicate whether the value is a percentage or a ratio. Both the list and detail endpoints perform the same `* 100` conversion, but the frontend model calls the field `varianceBps` while internally dividing by 100 to display as percentage (`(ex.varianceBps / 100).toFixed(2)`). The double conversion chain (percent → BPS → percent) is roundabout and error-prone.
- **Impact**: Maintenance risk — a developer unfamiliar with the chain could apply the conversion incorrectly.

## Module: PreAuthorization (Odoo POS → Edge Agent → FCC Device → Cloud)

---

### PA-T01: Desktop idempotency index is unconditional UNIQUE — prevents terminal record coexistence
- **Severity**: Medium
- **Location**: Desktop `BufferEntityConfiguration.cs:113-115` (ix_par_idemp), Cloud `PreAuthRecordConfiguration.cs:68-71`
- **Trace**: Desktop SQLite schema → UNIQUE(OdooOrderId, SiteCode) vs Cloud PostgreSQL → partial UNIQUE with `.HasFilter("status IN ('PENDING','AUTHORIZED','DISPENSING')")`
- **Description**: The cloud PostgreSQL database uses a filtered/partial unique index on `(odoo_order_id, site_code)` that only applies to non-terminal statuses, allowing terminal records (COMPLETED, CANCELLED, EXPIRED, FAILED) to coexist with new active records for the same order. SQLite does not support partial indexes, so the desktop agent uses an unconditional `UNIQUE(OdooOrderId, SiteCode)` index. This forces the desktop handler to reset terminal records in-place (see PA-F02) rather than creating new ones alongside, causing a fundamental architectural divergence between edge and cloud persistence models.
- **Impact**: Desktop cannot maintain pre-auth history per order. The cloud's multi-record-per-order design is incompatible with the desktop's single-record-per-order constraint.
- **Fix**: Consider a composite workaround: add a `generation` counter to the desktop record and include it in the unique index, or archive terminal records to a separate table before re-use.

### PA-T02: Pre-auth state machine rules duplicated across three codebases without shared validation
- **Severity**: Medium
- **Location**: Cloud `PreAuthRecord.cs:87-112`, Android `PreAuthHandler.kt:76-80, 108, 302-360`, Desktop `PreAuthHandler.cs:62-65, 290-306`
- **Trace**: State transition validation → Cloud: formal `Transition()` method with pattern matching → Android: inline `NON_TERMINAL_STATUSES` set + when blocks → Desktop: inline `is` pattern matching
- **Description**: The cloud domain entity has a formal `Transition()` method with an exhaustive state machine (`PreAuthRecord.cs:89-105`). Android uses a hardcoded `NON_TERMINAL_STATUSES` string set and inline `when` blocks. Desktop uses inline `is` pattern matching on enum values. The three implementations must stay in sync manually. A state machine rule change (e.g., adding DISPENSING → PENDING fallback) requires coordinated updates across C# cloud, Kotlin Android, and C# desktop codebases with no compile-time cross-validation.
- **Impact**: State machine divergence risk. If one platform allows a transition the others reject, pre-auth forwarding produces 409 conflicts on the cloud.
- **Fix**: Extract the state machine into a shared specification (e.g., JSON definition) and generate or validate transition tables at build time.

### PA-T03: Android Room entity stores pre-auth status as raw String — no enum enforcement at persistence layer
- **Severity**: Medium
- **Location**: Android `PreAuthRecord.kt` (Room entity), `PreAuthHandler.kt:108, 164, 239, 243, 303, 308, 390`
- **Trace**: Room `pre_auth_records.status` (TEXT column) → `PreAuthHandler` reads/writes via `.name` string comparisons
- **Description**: The Room entity stores `status` as a plain `String` column, not a Room `@TypeConverter`-backed enum. All status comparisons use `existing.status in NON_TERMINAL_STATUSES` where `NON_TERMINAL_STATUSES = setOf(PreAuthStatus.PENDING.name, ...)`. This works because `.name` produces uppercase enum names matching the stored strings, but a typo in the set definition, a refactored enum name, or a migration that writes lowercase would silently bypass dedup and state checks. Desktop uses typed `PreAuthStatus` enum with EF `HasConversion<string>()`, catching mismatches at compile time.
- **Impact**: Status string mismatches would silently bypass dedup, allowing duplicate FCC calls for the same order. No compile-time safety.
- **Fix**: Add a Room `@TypeConverter` for `PreAuthStatus` enum or use `@ColumnInfo(typeAffinity = TEXT)` with a converter to enforce enum constraints.

### PA-T04: Cloud PreAuthExpiryWorker deauthorizes FCC pumps AFTER marking records as EXPIRED — crash creates orphaned FCC authorizations
- **Severity**: High
- **Location**: `PreAuthExpiryWorker.cs:111-114`
- **Trace**: ExpireBatchAsync → `db.SaveChangesAsync(ct)` (line 111) → `TryDeauthorizePumpAsync` (line 114) per DISPENSING record
- **Description**: The worker first saves ALL records as EXPIRED in a single `SaveChangesAsync` call (line 111), then iterates over DISPENSING records to attempt FCC pump deauthorization (lines 113-114). If the worker crashes or the pod is recycled between the save and the deauth calls, the database shows EXPIRED but the FCC pump remains authorized — a "zombie" authorization. The Android edge agent takes the opposite approach (`PreAuthHandler.kt:390-421`): it deauthorizes BEFORE marking expired, and if deauth fails, leaves the record as AUTHORIZED for retry on the next cycle.
- **Impact**: FCC pumps may remain authorized after the pre-auth has expired in the database, allowing unauthorized fuel dispensing until the FCC's own TTL expires.
- **Fix**: Move deauthorization before the batch save, or process each DISPENSING record individually: deauth → transition → save → next record.

### PA-T05: Expiry deauthorization strategy diverges across three platforms — inconsistent "zombie auth" prevention
- **Severity**: Medium
- **Location**: Android `PreAuthHandler.kt:386-424`, Desktop `PreAuthHandler.cs:342-359`, Cloud `PreAuthExpiryWorker.cs:94-114`
- **Trace**: Expiry check → FCC deauth attempt → mark EXPIRED
- **Description**: Three different strategies for the same operation: (1) **Android**: For AUTHORIZED records, attempts deauth first; if deauth fails, does NOT mark as expired — retries next cycle. For PENDING/DISPENSING, marks expired directly. (2) **Desktop**: Attempts deauth for ALL expiring records (best-effort), then ALWAYS marks as expired regardless of deauth result. (3) **Cloud**: Marks ALL records as EXPIRED first, then attempts deauth only for previously-DISPENSING records.
- **Impact**: Inconsistent "zombie authorization" prevention. Android is strictest (never expires without successful deauth for AUTHORIZED), desktop is most lenient (always expires), cloud is in between. An FCC pump authorized by one agent type may behave differently during expiry than the same scenario on another agent type.
- **Fix**: Align all three platforms on a single strategy. The Android approach (deauth-then-expire with retry) is safest for preventing zombie authorizations.

### PA-T06: Android cloud forward worker treats all HTTP 409 as successful sync — invalid transition conflicts silently dropped
- **Severity**: High
- **Location**: `PreAuthCloudForwardWorker.kt:198-200`
- **Trace**: PreAuthCloudForwardWorker → `doForward()` → `CloudPreAuthForwardResult.Conflict` → `ForwardAttemptResult.Success` → `markCloudSynced`
- **Description**: The worker maps any `CloudPreAuthForwardResult.Conflict` (HTTP 409) to `ForwardAttemptResult.Success` with the comment "409 means cloud already has this record — treat as synced." However, the cloud controller returns 409 for TWO distinct cases: `CONFLICT.INVALID_TRANSITION` (invalid state transition) and `CONFLICT.RACE_CONDITION` (concurrent insert, retryable). An invalid transition 409 means the cloud rejected the status update, but the worker marks the record as `is_cloud_synced = 1`, permanently losing the status change. This can happen when the cloud expiry worker transitions a record to EXPIRED while the edge agent tries to forward an AUTHORIZED update.
- **Impact**: Pre-auth status changes (AUTHORIZED, CANCELLED, EXPIRED) can be silently lost when the cloud rejects them as invalid transitions. The edge believes the record is synced; the cloud has a different status.
- **Fix**: Distinguish 409 error codes. Treat `CONFLICT.RACE_CONDITION` (retryable) as a transport failure for retry. Treat `CONFLICT.INVALID_TRANSITION` by logging a warning and marking synced (since the cloud has a more advanced state) or routing to a local DLQ.

---

## Module: Onboarding (Registration & Provisioning)

---

### OB-T01: Desktop DeviceTokenProvider stores device token and refresh token non-atomically — crash triggers server-side reuse detection
- **Severity**: High
- **Location**: `DeviceTokenProvider.cs:141-143` (`RefreshTokenCoreAsync`)
- **Trace**: CloudUploadWorker/ConfigPollWorker → 401 → `RefreshTokenAsync()` → `RefreshTokenCoreAsync()` → `SetSecretAsync(TokenKey, ...)` (line 141) → `SetSecretAsync(RefreshTokenKey, ...)` (line 143)
- **Description**: After a successful token refresh, the provider stores the new device token (line 141) and new refresh token (line 143) in two separate `SetSecretAsync` calls. On the cloud, both old tokens are revoked and new tokens are issued atomically in a single `SaveChangesAsync`. If the desktop agent process crashes, is killed, or the credential store write fails between lines 141 and 143: the new device JWT is stored locally, but the old refresh token remains. On next startup, the device uses the new JWT (valid for 24 hours). When it next needs to refresh, it sends the OLD refresh token. The cloud's `RefreshDeviceTokenHandler` (BUG-010) detects this as refresh token reuse — the old token has `RevokedAt is not null` and `ExpiresAt > now` — and revokes ALL active tokens for the device per RFC 6819 §5.2.2.3. The device becomes unable to authenticate. The same non-atomic pattern exists in `StoreTokensAsync` (lines 51-52) during initial registration, though that path is less susceptible since it's a one-time operation.
- **Impact**: A process crash at the wrong moment during token refresh permanently locks the device out, requiring manual re-provisioning. The server interprets the stale refresh token as a compromise indicator and revokes all tokens.
- **Fix**: Write both tokens to a single atomic credential entry (e.g., a JSON object with both tokens) or use a two-phase approach: write new tokens to a staging key, then atomically swap. Alternatively, persist a "pending refresh" marker before the HTTP call and reconcile on startup.

### OB-T02: Concurrent decommission requests produce duplicate audit events — no optimistic concurrency on AgentRegistration
- **Severity**: Medium
- **Location**: `DecommissionDeviceHandler.cs:30-70`
- **Trace**: Portal Admin A → `POST /api/v1/admin/agent/{deviceId}/decommission` + Portal Admin B → same endpoint concurrently
- **Description**: The `DecommissionDeviceHandler` loads the agent registration, checks `device.IsActive`, sets `IsActive = false`, revokes refresh tokens, creates an audit event, and calls `SaveChangesAsync`. The `AgentRegistration` entity has no `RowVersion` or concurrency token (unlike `BootstrapToken` which uses PostgreSQL `xmin` and `DeviceRefreshToken` which uses `RevokedAt`). If two admin users simultaneously decommission the same device: both load the device with `IsActive = true`, both pass the `!device.IsActive` check, both create `DEVICE_DECOMMISSIONED` audit events, and both save successfully. The second save silently overwrites the first (last-write-wins on `DeactivatedAt`, `UpdatedAt`). Two audit events are published for a single decommission.
- **Impact**: Duplicate audit trail for a single decommission action. If downstream systems consume audit events for alerting or compliance, they may process the decommission twice.
- **Fix**: Add a concurrency token to `AgentRegistration` (e.g., `xmin` like BootstrapToken) and use `TrySaveChangesAsync`. Return 409 Conflict on concurrent modification.

### OB-T03: Android ANDROID_ID null fallback generates a random deviceSerialNumber on every attempt — inconsistent fleet records
- **Severity**: Low
- **Location**: `ProvisioningViewModel.kt:110-111`
- **Trace**: Android provisioning → `buildRegistrationRequest()` → `Settings.Secure.getString(...) ?: "unknown-${UUID.randomUUID()...}"`
- **Description**: If `Settings.Secure.ANDROID_ID` returns null (possible on some AOSP builds, emulators, or rooted devices), the fallback generates `"unknown-${UUID.randomUUID().toString().take(8)}"`. This random value changes on every registration attempt. If the first registration attempt fails (e.g., network error) and the user retries, the `deviceSerialNumber` in the cloud registration will differ between attempts. While the cloud uses bootstrap token hash (not serial number) for dedup, the `agent_registrations.device_serial_number` column stores whatever value was sent, leading to unpredictable fleet inventory records.
- **Impact**: Fleet management portal may show random serial numbers for devices where ANDROID_ID is unavailable. Multiple failed-then-succeeded registration attempts could create confusion if serial numbers are used for device identification in support workflows.
- **Fix**: Generate the random fallback once and persist it in `EncryptedPrefsManager` so it remains stable across registration attempts.
