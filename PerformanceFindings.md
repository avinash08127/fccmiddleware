# Performance Findings

## Module: FleetManagement (Edge Agent Registration, Telemetry, Monitoring)

---

### FM-P01: Agent list auto-refresh every 30s with COUNT(*) + full page fetch
- **Severity**: High
- **Location**: `agent-list.component.ts:536`, `AgentsController.cs:100-108`
- **Trace**: Portal → `interval(30_000)` → `getAgents()` → DB (count + data + snapshots)
- **Description**: The agent list component auto-refreshes every 30 seconds. Each refresh triggers: (1) `COUNT(*)` over filtered agents, (2) paginated data query, (3) telemetry snapshot dictionary lookup. With multiple admin users viewing the fleet page simultaneously, this creates sustained polling load. The 30-second interval fires even if the tab is in the background (no visibility API check).
- **Impact**: For 10 concurrent portal users, this produces 20 DB queries every 30 seconds (600+ queries/minute) from the fleet page alone.
- **Fix**: Use `document.visibilityState` to pause auto-refresh when the tab is hidden. Consider WebSocket/SSE for real-time updates or longer polling intervals.

### FM-P02: Agent detail auto-refresh fires 3 parallel API calls every 30s + diagnostic logs
- **Severity**: Medium
- **Location**: `agent-detail.component.ts:718-721, 714`
- **Trace**: Portal → `interval(30_000)` → `forkJoin(registration, telemetry, events)` + `loadDiagnosticLogs()`
- **Description**: The agent detail page auto-refreshes every 30 seconds with a `forkJoin` of 3 API calls (registration, telemetry, events) and then also triggers `loadDiagnosticLogs()` on each successful refresh. The registration and events data are unlikely to change every 30 seconds — only telemetry is time-sensitive.
- **Impact**: 4 API calls per detail page view per 30-second interval. Events and diagnostic logs fetches are mostly wasted.
- **Fix**: Separate the refresh intervals: telemetry every 30s, events and registration every 2-5 minutes, diagnostic logs on-demand only.

### FM-P03: GetEvents in-memory pagination loop can produce O(N) batch queries
- **Severity**: High
- **Location**: `AgentsController.cs:292-357`
- **Trace**: Portal → `GET /api/v1/agents/{id}/events` → while-loop with 200-row batches
- **Description**: The events endpoint uses a loop to find events for a specific device by fetching 200 audit events at a time, deserializing all JSON payloads, and filtering by deviceId in memory. For a site with thousands of audit events across many devices, the loop may execute many iterations (each requiring a DB round-trip and JSON deserialization) before finding 20 matching events. Worst case: a site with 10,000 events and only 5 belonging to the target device requires 50 batch queries.
- **Impact**: High latency and DB load for the events endpoint on busy sites. This runs every 30 seconds per open detail page.
- **Fix**: Add a `device_id` indexed column to `audit_events` for direct filtering in SQL.

### FM-P04: Telemetry endpoint creates an audit event row on every heartbeat
- **Severity**: High
- **Location**: `SubmitTelemetryHandler.cs:88-98`
- **Trace**: Device → `POST /api/v1/agent/telemetry` → audit event INSERT
- **Description**: Every telemetry submission (typically every 30-60 seconds per device) inserts a full audit event with the complete telemetry payload serialized as JSON. The telemetry snapshot table (upsert pattern) is efficient, but the audit events table grows unboundedly. For 100 devices reporting every 30 seconds: ~288,000 audit rows/day with full JSON payloads.
- **Impact**: Rapid audit table growth degrades query performance across all audit-related features (agent events, DLQ audit trail, etc.). Storage costs scale linearly with fleet size.
- **Fix**: Consider throttling audit events (e.g., one per device per 5 minutes) or using a separate time-series store for telemetry history.

### FM-P05: Missing database index on `agent_registrations.legal_entity_id`
- **Severity**: Medium
- **Location**: `AgentRegistrationConfiguration.cs:44-45`
- **Trace**: Portal → `GET /api/v1/agents` → `.ForPortal(access, legalEntityId)` → DB query
- **Description**: The `agent_registrations` table has an index on `(site_id, is_active)` but no index on `legal_entity_id`. The `GetAgents` endpoint filters by `legalEntityId` as the primary access scope. Without an index, the query scans the full table and filters in the DB engine.
- **Impact**: Slow queries on the agent list endpoint as the table grows, especially for multi-tenant deployments.
- **Fix**: Add a composite index on `(legal_entity_id, is_active, registered_at)` to cover the main listing query.

### FM-P06: Missing database index on `agent_telemetry_snapshots.legal_entity_id`
- **Severity**: Medium
- **Location**: `AgentsController.cs:116-120`
- **Trace**: Portal → `GET /api/v1/agents` → telemetry snapshot lookup `WHERE legal_entity_id = X AND device_id IN (...)`
- **Description**: The snapshot lookup in `GetAgents` filters by `LegalEntityId` first, then by a list of device IDs. If the snapshots table lacks an index on `legal_entity_id`, this requires a sequential scan.
- **Impact**: Slow snapshot lookups as fleet size grows.

### FM-P07: Bootstrap token TOCTOU re-check queries the database twice on every generation
- **Severity**: Low
- **Location**: `GenerateBootstrapTokenHandler.cs:39-41, 93-94`
- **Trace**: Portal → `POST /api/v1/admin/bootstrap-tokens` → count check → save → re-count
- **Description**: The handler counts active tokens before and after save to detect TOCTOU races. The second count query runs on every successful token generation, even though races are rare.
- **Impact**: Extra DB query on every bootstrap token generation. Low volume so minimal practical impact.

---

## Module: Transactions

---

### TX-P01: Sequential batch ingestion creates O(N) database round-trips per batch
- **Severity**: Critical
- **Location**: `TransactionsController.cs:432-490` (`IngestBatchAsync`), `IngestTransactionHandler.cs:58-287`
- **Trace**: FCC Push → `POST /api/v1/transactions/ingest` (batch) → sequential loop → N × (Redis + DB dedup + S3 archive + DB persist + Redis cache + reconciliation)
- **Description**: Each item in a batch triggers the full ingestion pipeline sequentially. Per item: 1 Redis GET (dedup), 1 DB query (fuzzy match), 1 S3 PUT (archive), 1 DB INSERT + outbox (persist), 1 DB query (reconciliation), 1 DB SAVE (reconciliation result), 1 Redis SET (cache). That's ~7 I/O operations per item. A 500-item batch produces ~3,500 sequential I/O operations.
- **Impact**: A 500-item batch at ~50ms average per item takes ~25 seconds, likely exceeding default HTTP timeouts (30s). During this time the request holds a DB connection and a worker thread. Multiple concurrent batch requests can exhaust the connection pool.
- **Fix**: Parallelize with bounded concurrency, or implement batched DB operations (bulk insert + single outbox commit).

### TX-P02: OpsTransactionsController executes COUNT(*) on first-page requests over partitioned table
- **Severity**: High
- **Location**: `OpsTransactionsController.cs:153`
- **Trace**: Portal → `GET /api/v1/ops/transactions?legalEntityId=X` (no cursor) → `query.CountAsync()`
- **Description**: On the initial page load (when `cursor is null`), the controller executes a full `COUNT(*)` over the filtered query before fetching the data page. The `transactions` table is range-partitioned by `CreatedAt` (monthly). Without date range filters, the count query must scan all partitions. With typical filters (legalEntityId + optional siteCode/status), the count scans all matching partitions.
- **Impact**: For tenants with millions of transactions across many monthly partitions, the count query can take 1-5 seconds. This blocks the first page load and runs on every new navigation to the transaction list.
- **Fix**: Compute `totalCount` only on explicit user request (e.g., separate "count" endpoint), or use an approximate count for the initial page.

### TX-P03: OFFSET-based pagination for non-default sort fields degrades with page depth
- **Severity**: Medium
- **Location**: `OpsTransactionsController.cs:167-171`
- **Trace**: Portal → `GET /api/v1/ops/transactions?sortField=siteCode&cursor=encoded_offset`
- **Description**: When sorting by `fccTransactionId`, `siteCode`, `volumeMicrolitres`, `amountMinorUnits`, or `status`, the controller uses OFFSET/LIMIT pagination instead of keyset. PostgreSQL must scan and discard `offset` rows before returning the page. At offset 10,000, the DB reads and discards 10,000 rows before returning 50.
- **Impact**: Page 200+ queries become progressively slower, potentially timing out for large datasets. Each page request re-scans from the beginning.

### TX-P04: Fuzzy match query scans a wide range without covering index
- **Severity**: Medium
- **Location**: `FccMiddlewareDbContext.cs:154-171` (`HasFuzzyMatchAsync`)
- **Trace**: Ingestion → `IngestTransactionHandler` → `HasFuzzyMatchAsync()` → DB query
- **Description**: The fuzzy match query filters on `(LegalEntityId, SiteCode, PumpNumber, NozzleNumber, AmountMinorUnits, CompletedAt range, Status = PENDING)`. Without a composite index covering these columns, PostgreSQL falls back to the partition index (on `CreatedAt`) or a sequential scan within the active partition. The ±5-second `CompletedAt` window is narrow, but the column used (`CompletedAt`) may not be the partition key (`CreatedAt`), reducing partition pruning effectiveness.
- **Impact**: Each ingested transaction triggers this query. At high ingestion rates (>100 tx/s), the cumulative query load is significant.
- **Fix**: Add a composite index: `CREATE INDEX ix_transactions_fuzzy ON transactions (legal_entity_id, site_code, pump_number, nozzle_number, amount_minor_units, completed_at) WHERE status = 'PENDING'`.

### TX-P05: SiteFccConfigProvider.GetByWebhookSecretAsync loads all Petronite configs into memory for comparison
- **Severity**: Medium
- **Location**: `SiteFccConfigProvider.cs` (`GetByWebhookSecretAsync`)
- **Trace**: Petronite webhook → `TransactionsController.IngestPetroniteWebhook()` → `GetByWebhookSecretAsync()`
- **Description**: The webhook secret lookup loads all active FCC configs for the Petronite vendor with a non-null `WebhookSecret`, then performs constant-time comparison in memory. If there are many Petronite sites, this loads all their configurations per webhook request. (In contrast, the Advatec lookup uses a SHA-256 hash index for O(1) database lookup.)
- **Impact**: At 100+ Petronite sites, each webhook request fetches and deserializes 100+ config rows before finding the match. At high webhook rates, this creates sustained DB and memory pressure.
- **Fix**: Apply the same SHA-256 hash indexing pattern used for Advatec tokens — store a `WebhookSecretHash` column with an index, filter by hash in SQL, then confirm with constant-time comparison.

### TX-P06: S3 raw payload archive is synchronous within the request pipeline
- **Severity**: Low
- **Location**: `IngestTransactionHandler.cs:222-233`
- **Trace**: Ingestion → `ArchiveAsync()` → S3 PutObject (await) → continue pipeline
- **Description**: The S3 raw payload archive is awaited within the ingestion request. While the operation is non-fatal (failures are caught and logged), successful S3 puts add latency to every ingestion request. S3 PutObject latency is typically 50-200ms, adding to the per-item cost.
- **Impact**: Each ingested transaction pays an additional 50-200ms for S3 archival. For batch ingestion (TX-P01), this compounds across all items.
- **Fix**: Queue S3 archival as a background task or batch it per request, with the raw payload reference populated after the fact via an outbox-driven worker.

---

## Module: Reconciliation

---

### RC-P01: GetExceptions executes COUNT(*) on every page request over unpartitioned table
- **Severity**: High
- **Location**: `OpsReconciliationController.cs:114`
- **Trace**: Portal → `GET /api/v1/ops/reconciliation/exceptions` → `query.CountAsync()` on every request
- **Description**: The `GetExceptions` endpoint executes `await query.CountAsync(cancellationToken)` on every page request, including subsequent pages (cursor-based). The count query applies the same filters (legal entity, status, site code, date range) and scans the `reconciliation_records` table. Unlike the partitioned `transactions` table, `reconciliation_records` is unpartitioned, so the count scans all rows matching the filter. The count result is returned as `TotalCount` in the response metadata, but cursor-based pagination doesn't need a total count — the `HasMore` flag is sufficient.
- **Impact**: For tenants with hundreds of thousands of reconciliation records, the count query adds 100-500ms per page request. The frontend calls this on tab switch, filter change, and page navigation.
- **Fix**: Remove `totalCount` from cursor-paginated responses, or make it optional (only compute on the first page or when explicitly requested).

### RC-P02: GetExceptions loads full entity rows + two enrichment queries per page
- **Severity**: Medium
- **Location**: `OpsReconciliationController.cs:115-140`
- **Trace**: Portal → GetExceptions → `ToListAsync()` (full entities) → `PreAuthRecords.Where(IN)` → `Transactions.Where(IN)`
- **Description**: The endpoint materializes full `ReconciliationRecord` entities (all 25+ columns) into memory, then executes two additional queries to load PreAuth and Transaction records for enrichment. For a page of 100 records, this means: (1) main query returns 101 full rows, (2) PreAuth query with up to 100 IDs in `IN (...)` clause, (3) Transaction query with up to 100 IDs in `IN (...)` clause. The PreAuth and Transaction entities are also loaded fully (all columns), but only a few fields are used in the DTO projection.
- **Impact**: Three DB round-trips per page request. The full entity materialization loads columns like `RawPayload`, `CustomerTaxId`, `CustomerName` (PII fields) into server memory even though they're not returned in the DTO.
- **Fix**: Use a SQL projection to select only needed columns. Consider a single query with JOINs instead of 3 separate queries.

### RC-P03: UnmatchedReconciliationWorker executes redundant site context queries — no per-batch caching
- **Severity**: Medium
- **Location**: `UnmatchedReconciliationWorker.cs:79-97`, `ReconciliationMatchingService.cs:146-177`
- **Trace**: Worker → `ProcessBatchAsync()` → foreach(500 items) → `RetryUnmatchedAsync()` → `FindSiteContextAsync()`
- **Description**: Each record retry calls `FindSiteContextAsync(legalEntityId, siteCode, options, ct)`, which queries the database for the site's reconciliation configuration. With a batch size of 500 and records typically clustered by site (same site code), the same site context is queried hundreds of times per batch. No caching exists at the worker or service level.
- **Impact**: For a batch of 500 records from 5 sites, 500 DB queries are executed instead of 5. At the default 60-second poll interval, this wastes ~495 queries per cycle during periods with many unmatched records.
- **Fix**: Add a `Dictionary<(Guid, string), ReconciliationSiteContext>` cache scoped to the batch processing method.

### RC-P04: Matching service executes up to 3 candidate queries sequentially per transaction
- **Severity**: Medium
- **Location**: `ReconciliationMatchingService.cs:283-341` (`ResolveCandidateAsync`)
- **Trace**: Ingestion → `MatchAsync()` → `ResolveCandidateAsync()` → correlation → pump+nozzle+time → Odoo order
- **Description**: The candidate resolution tries three matching strategies sequentially: (1) correlation ID candidates, (2) pump+nozzle+time window candidates, (3) Odoo order ID candidates. Each strategy executes a database query. In the worst case (no match on any), all three queries run. The time-window query (`FindPumpNozzleTimeCandidatesAsync`) searches PreAuth records within a configurable window (default ±15 minutes) and may scan a significant range. These queries run during the HTTP request pipeline for every ingested transaction at a pre-auth-enabled site.
- **Impact**: Up to 3 additional DB queries per ingested transaction, executed synchronously within the ingestion request. At high ingestion rates (>100 tx/s), this adds significant DB load.
- **Fix**: Consider running the first two strategies in parallel since they're independent. Add covering indexes on pre_auth_records for the candidate query patterns.

### RC-P05: Frontend list component loads sites with pageSize=500 on every legal entity change
- **Severity**: Low
- **Location**: `reconciliation-list.component.ts:722-732`
- **Trace**: Portal → `onLegalEntityChange()` → `siteService.getSites({ legalEntityId, pageSize: 500 })`
- **Description**: Every time the user selects a legal entity in the reconciliation list, the component fetches up to 500 sites for the filter dropdown. This request is not cached or debounced. If the user switches between entities rapidly, multiple overlapping requests are fired. Additionally, the `takeUntilDestroyed` subscription means in-flight requests from a previous entity selection are NOT cancelled.
- **Impact**: Redundant API calls on entity switching. For legal entities with many sites, the 500-site payload is non-trivial.
- **Fix**: Use `switchMap` for the site loading to cancel in-flight requests, and cache results per entity ID.

## Module: PreAuthorization (Odoo POS → Edge Agent → FCC Device → Cloud)

---

### PA-P01: Cloud PreAuthExpiryWorker issues sequential FCC deauthorization calls after batch save — up to 500 serial HTTP round-trips
- **Severity**: High
- **Location**: `PreAuthExpiryWorker.cs:92-114`
- **Trace**: `ExpireBatchAsync()` → `ToListAsync()` (up to 500 records) → `SaveChangesAsync()` → `foreach` → `TryDeauthorizePumpAsync()` per DISPENSING record
- **Description**: The worker loads up to 500 expired records in a single query (line 86: `Take(_options.BatchSize)`), transitions them all to EXPIRED, saves in one `SaveChangesAsync()` call, then iterates over DISPENSING records calling `TryDeauthorizePumpAsync()` sequentially (lines 113-114). Each deauth call involves `ISiteFccConfigProvider.GetBySiteCodeAsync()` (DB lookup), `IFccAdapterFactory.Resolve()` (adapter construction), and `IFccPumpDeauthorizationAdapter.DeauthorizePumpAsync()` (HTTP call). After a prolonged network partition or FCC outage, a large number of DISPENSING records could accumulate, causing the deauth loop to take minutes per batch.
- **Impact**: Long-running expiry batches delay the next poll cycle, block other expired records from being processed, and could cause the worker to appear hung.
- **Fix**: Process deauth calls concurrently (e.g., `Task.WhenAll` with bounded parallelism), or reduce batch size for DISPENSING records.

### PA-P02: Android expiry check loads ALL expired records without batch limit
- **Severity**: Medium
- **Location**: `PreAuthHandler.kt:380` (`preAuthDao.getExpiring(now)`)
- **Trace**: CadenceController → `runExpiryCheck()` → `preAuthDao.getExpiring(now)` → Room query → full result set → iterate
- **Description**: The `getExpiring()` DAO method returns ALL pre-auth records with `status IN (PENDING, AUTHORIZED, DISPENSING) AND expires_at <= now` without any `LIMIT` clause. Under normal conditions this set is small (< 10 records), but after a prolonged FCC outage or network partition, hundreds of records could expire simultaneously. All would be loaded into memory at once. For AUTHORIZED records, the handler attempts FCC deauth before marking expired (lines 390-421), making the loop duration proportional to the expired count.
- **Impact**: Memory pressure and potential OOM on Android devices with limited RAM. Long-running expiry loop could block the cadence controller from executing other periodic tasks.
- **Fix**: Add a `LIMIT` parameter to `getExpiring()` (e.g., 50) and process in batches, matching the cloud worker's batching pattern.

### PA-P03: Desktop expiry check loads ALL expired records without batch limit
- **Severity**: Medium
- **Location**: `PreAuthHandler.cs:329-335`
- **Trace**: Periodic task → `RunExpiryCheckAsync()` → `_db.PreAuths.Where(...).ToListAsync()` → full result set
- **Description**: Same unbounded-load pattern as Android. The desktop handler queries all expired pre-auth records without `.Take()` or pagination (lines 329-335). The subsequent loop calls `TryCancelAtFccAsync()` for each record (line 349), which involves adapter creation and an HTTP call per record. A large backlog would cause the loop to run for an extended period.
- **Impact**: Prolonged expiry processing blocks the calling thread/timer from performing other agent tasks. Unlike the Android handler which skips failed deauths for retry, the desktop handler always marks expired regardless, so the duration scales linearly with expired count.
- **Fix**: Add `.Take(batchSize)` and `.OrderBy(p => p.ExpiresAt)` to the query, processing in configurable batches.

### PA-P04: Android cloud forward worker processes pre-auth records serially — no batch endpoint or parallelism
- **Severity**: Medium
- **Location**: `PreAuthCloudForwardWorker.kt:125-179`
- **Trace**: CadenceController → `forwardUnsyncedPreAuths()` → `for (record in unsynced)` → `doForward()` per record → HTTP POST → next
- **Description**: Each unsynced pre-auth record is forwarded individually via a single HTTP POST to `/api/v1/preauth` (no batch endpoint). Records are processed serially in the `for` loop (line 125). The cloud API only accepts single records per call (unlike the transaction upload endpoint which supports batching). For a backlog of 200 records at 100ms per round-trip, forwarding takes 20+ seconds. The worker stops processing remaining records on any transport failure (line 177: `return`), so a single timeout can delay the entire batch.
- **Impact**: Large pre-auth backlogs (after connectivity restoration) take a long time to drain. Combined with the 20-record batch size (line 110), the worker processes at most 20 records per cadence cycle.
- **Fix**: Consider adding a batch forward endpoint to the cloud API (similar to `/api/v1/transactions/upload`), or process records with bounded concurrency (e.g., 3-5 parallel HTTP calls).

### PA-P05: Desktop PreAuthHandler uses EF tracked query for dedup check on the hot path
- **Severity**: Low
- **Location**: `PreAuthHandler.cs:55-58`
- **Trace**: Odoo POS → `POST /api/v1/preauth` → `HandleAsync()` → `_db.PreAuths.FirstOrDefaultAsync()` (tracked) → dedup check
- **Description**: The dedup query at line 55-58 loads the existing record as a tracked entity (`FirstOrDefaultAsync` without `AsNoTracking()`). The comment explains: "Fetch tracked (not AsNoTracking) so we can update in-place for terminal re-requests." This is correct for the terminal re-request case (record is reset in-place), but the common case is either no existing record (new pre-auth) or a non-terminal dedup hit (return immediately). In both common cases, the EF change tracker overhead is unnecessary. On the hot path (p95 target: ≤ 50ms local overhead), this adds measurable overhead.
- **Impact**: Minor latency increase on the most common code path (new pre-auth creation). EF change tracking adds identity resolution and snapshot overhead per-entity.
- **Fix**: Use `AsNoTracking()` for the dedup check, then re-query tracked only when a terminal record needs resetting.

### PA-P06: Cloud ForwardPreAuthHandler uses two separate DB round-trips for the common create path
- **Severity**: Low
- **Location**: `ForwardPreAuthHandler.cs:49-59, 179-187`
- **Trace**: Edge Agent → `POST /api/v1/preauth` → `FindByDedupKeyAsync()` (round-trip 1) → no existing record → `AddPreAuthRecord()` → `SaveChangesAsync()` (round-trip 2)
- **Description**: On the common "new pre-auth" path, the handler first executes `FindByDedupKeyAsync` to check for an existing record (line 49-50), then creates and saves a new record (lines 179-187). This requires two database round-trips. The cloud PostgreSQL filtered unique index (`ix_preauth_idemp`) is designed to handle concurrent inserts via unique constraint violations, so the dedup lookup could be optimized into an `INSERT ... ON CONFLICT DO NOTHING` / `RETURNING` pattern that completes in a single round-trip.
- **Impact**: Each pre-auth forward requires two DB round-trips instead of one. At high ingestion rates, the extra round-trip adds measurable latency.
- **Fix**: Consider using a PostgreSQL `INSERT ... ON CONFLICT` pattern or deferred dedup (insert optimistically, handle constraint violation) to reduce to a single round-trip for the common case.

---

## Module: Onboarding (Registration & Provisioning)

---

### OB-P01: GetAgentConfigHandler eagerly loads full Site→Pumps→Nozzles→Products hierarchy on every config poll
- **Severity**: Medium
- **Location**: `GetAgentConfigHandler.cs` — `Handle()` → `GetFccConfigWithSiteDataAsync()`; `FccMiddlewareDbContext.cs:497-519` (IAgentConfigDbContext implementation)
- **Trace**: Edge Agent → `GET /api/v1/agent/config` (every 5 min per device) → `GetAgentConfigHandler` → `GetFccConfigWithSiteDataAsync()` → `.Include(fc => fc.Site).ThenInclude(s => s.Pumps).ThenInclude(p => p.Nozzles).ThenInclude(n => n.Product)` + `.Include(fc => fc.LegalEntity)`
- **Description**: Every config poll eagerly loads the entire entity graph: `FccConfig → Site → Pumps (active) → Nozzles (active) → Products + LegalEntity`. For a site with 50 pumps × 4 nozzles each = 200 nozzle entities plus 200 product lookups, this materializes ~450 entities per request. The ETag optimization (returning 304 Not Modified when `configVersion` hasn't changed) bypasses the eager load, but only if the client sends the correct `If-None-Match` header. On the first poll after agent startup, or whenever the ETag is missing, the full graph is loaded. With 100 devices polling every 5 minutes (20 polls/min), this is 20 full eager loads per minute during rolling restarts or connectivity recovery. The `BuildMappingsDto` method then iterates through all of this in-memory.
- **Impact**: High database load during fleet-wide restarts or connectivity recovery. Each full load generates a multi-join query across 5 tables. The materialized entities consume server memory proportional to site size × concurrent requests.
- **Fix**: Cache the assembled `SiteConfigResponse` per `(siteCode, legalEntityId, configVersion)` in a short-lived memory cache (e.g., 60 seconds). All devices at the same site receive the same config, so a single DB load can serve all concurrent requests. Alternatively, store the pre-serialized config JSON in the `fcc_configs` table and return it directly when the config version matches.

### OB-P02: `bootstrap_tokens` table lacks composite index for active token count query
- **Severity**: Medium
- **Location**: `FccMiddlewareDbContext.cs` — `CountActiveBootstrapTokensForSiteAsync()`, `BootstrapTokenConfiguration.cs`
- **Trace**: Portal → `POST /api/v1/admin/bootstrap-tokens` → `GenerateBootstrapTokenHandler` → `CountActiveBootstrapTokensForSiteAsync(siteCode, legalEntityId)` → table scan
- **Description**: The `CountActiveBootstrapTokensForSiteAsync` method counts bootstrap tokens `WHERE SiteCode = @p0 AND LegalEntityId = @p1 AND Status = 'ACTIVE'`. The `BootstrapTokenConfiguration` defines only two indexes: primary key on `Id` and a unique index on `TokenHash` (`ix_bootstrap_token_hash`). There is no index covering the `(site_code, legal_entity_id, status)` predicate used by the count query. Additionally, the TOCTOU re-check (H-02 in GenerateBootstrapTokenHandler) runs this count query a second time after every successful token generation.
- **Impact**: As the bootstrap_tokens table grows (tokens are never deleted — they transition to USED/EXPIRED/REVOKED), the count query degrades. With thousands of historical tokens, each generation requires a sequential scan of the table. At 2 count queries per generation (pre-check + TOCTOU re-check), the cost doubles.
- **Fix**: Add a partial index: `CREATE INDEX ix_bootstrap_tokens_active ON bootstrap_tokens (site_code, legal_entity_id) WHERE status = 'ACTIVE'`. Since only a small fraction of tokens are ACTIVE at any time, the partial index will be compact and efficient.

### OB-P03: No background job to purge expired/revoked `device_refresh_tokens` — unbounded table growth
- **Severity**: Medium
- **Location**: `DeviceRefreshToken.cs`, `DeviceRefreshTokenConfiguration.cs`, `ModuleClusters.md` (Onboarding background jobs: "None — registration is event-driven")
- **Trace**: Token refresh → `RefreshDeviceTokenHandler` → revokes old token (sets `RevokedAt`) → creates new token → old token remains in table indefinitely
- **Description**: Every token refresh creates a new `DeviceRefreshToken` row and revokes the previous one (sets `RevokedAt`). Tokens are never deleted. With 100 devices refreshing tokens daily (24-hour JWT expiry → 1 refresh/device/day), the table grows by ~100 rows/day. After one year: ~36,500 rows, of which ~36,400 are revoked. The reuse detection query (`FindRefreshTokenByHashAsync`) searches by hash against the unique index (`ix_refresh_token_hash`), so read performance is O(1). However, the `GetActiveRefreshTokensForDeviceAsync` query (used during decommission and reuse detection) scans `WHERE DeviceId = @p AND RevokedAt IS NULL` using the composite index `ix_refresh_token_device(device_id, revoked_at)`. As revoked tokens accumulate per device, the index grows. More importantly, the table's storage footprint, vacuum overhead, and backup size grow linearly without bound.
- **Impact**: Unbounded table growth increases PostgreSQL vacuum time, backup sizes, and storage costs. For a fleet of 1,000 devices over 3 years: ~1.1M rows, mostly dead weight.
- **Fix**: Add a periodic cleanup job (e.g., daily) that deletes `device_refresh_tokens WHERE RevokedAt IS NOT NULL AND RevokedAt < NOW() - INTERVAL '90 days'` (matching the token's 90-day expiry window). Tokens revoked more than 90 days ago can never trigger reuse detection since they're also expired.
