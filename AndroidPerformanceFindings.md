# Android Performance Findings — FCC Edge Agent

**Module**: FCC Edge Agent (Android)
**Audit date**: 2026-03-13
**Scope**: End-to-end trace — UI → State → Workers → Adapters → DB/Network

---

## AP-001: DiagnosticsActivity Queries 7 DAOs Every 5 Seconds

| Field | Value |
|-------|-------|
| **ID** | AP-001 |
| **Title** | Auto-refresh performs 9 separate DAO queries every 5 seconds including full table scans |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Medium |
| **Category** | Repeated Network/DB Calls |
| **Description** | `DiagnosticsActivity.refreshData()` executes 9 separate database queries every 5 seconds: `countForLocalApi()`, `syncStateDao.get()`, `auditLogDao.getRecent(15)`, `siteDataDao.getSiteInfo()`, `siteDataDao.getAllProducts().size`, `siteDataDao.getAllPumps().size`, `siteDataDao.getAllNozzles().size`, `fileLogger.getRecentDiagnosticEntries(30)`, and `fileLogger.totalLogSizeBytes()`. The `getAllProducts()`, `getAllPumps()`, and `getAllNozzles()` queries load entire tables into memory just to count `.size`. |
| **Evidence** | `ui/DiagnosticsActivity.kt` lines 124–133: 9 separate queries, three of which load full table contents. |
| **Impact** | Unnecessary memory allocation and GC pressure every 5 seconds. On a device with 500+ products, 50+ pumps, and 200+ nozzles, this loads hundreds of objects just to count them. |
| **Recommended Fix** | Replace `getAllProducts().size` with a `COUNT(*)` query (e.g., `@Query("SELECT COUNT(*) FROM local_products")`). Batch the individual queries into a single Room `@Transaction` method to reduce SQLite lock contention. Consider increasing the refresh interval to 10 seconds or using Room's `Flow`-based reactive queries that only emit when data changes. |

---

## AP-002: TransactionBufferManager.bufferTransaction Runs Two Queries Per Insert

| Field | Value |
|-------|-------|
| **ID** | AP-002 |
| **Title** | Cross-adapter dedup query runs on every transaction insert — even when only one adapter is active |
| **Module** | Transaction Management |
| **Severity** | Medium |
| **Category** | Repeated Network/DB Calls |
| **Description** | `TransactionBufferManager.bufferTransaction()` always calls `dao.findCrossAdapterDuplicate()` (line 50) before the insert, regardless of whether the site has multiple adapters configured. For sites with a single FCC vendor (the vast majority), this query will never find a match but still executes a SELECT with 5 WHERE conditions against the `buffered_transactions` table on every transaction. With 30,000+ buffered records, this adds measurable latency to each ingestion cycle. |
| **Evidence** | `buffer/TransactionBufferManager.kt` lines 48–65: `findCrossAdapterDuplicate()` called unconditionally. |
| **Impact** | An extra SQLite query per transaction ingestion. At 50 transactions per poll cycle, that's 50 extra queries. With a well-indexed table the overhead is small (~1-2ms each), but it accumulates. |
| **Recommended Fix** | Short-circuit the cross-adapter dedup when only one adapter is configured (check the site config's vendor field). Alternatively, move the dedup check to a Room `@Insert(onConflict = IGNORE)` with a composite unique index instead of a separate query. |

---

## AP-003: CloudUploadWorker Builds Upload Request With Full rawPayloadJson

| Field | Value |
|-------|-------|
| **ID** | AP-003 |
| **Title** | Upload batches include full raw FCC payloads — payload size grows linearly with batch size |
| **Module** | Cloud Sync |
| **Severity** | Medium |
| **Category** | Large Payload Processing |
| **Description** | `CloudUploadWorker.buildUploadRequest()` maps each `BufferedTransaction` to a `CloudTransactionDto` that includes `rawPayloadJson` (line 864). Raw payloads from DOMS JPL can be 2-5 KB of JSON per transaction. At a batch size of 50, the upload payload can reach 100-250 KB. The 413 PayloadTooLarge handling (line 675) halves the batch size on failure, but the initial attempt may time out on slow mobile connections before the server rejects it. Additionally, the raw payload is redundant — the cloud already has the normalized canonical fields. |
| **Evidence** | `sync/CloudUploadWorker.kt` line 864: `rawPayloadJson = rawPayloadJson`. |
| **Impact** | Upload batches are 2-5x larger than necessary. On metered mobile connections, this increases data costs and latency. |
| **Recommended Fix** | Make `rawPayloadJson` opt-in via site config (e.g., `sync.includeRawPayload = true`). Default to excluding it since the canonical fields are sufficient for cloud processing. Alternatively, compress the raw payload with gzip before including it. |

---

## AP-004: IngestionOrchestrator Fiscalization Retry Queries Entire Pending Set

| Field | Value |
|-------|-------|
| **ID** | AP-004 |
| **Title** | retryPendingFiscalization queries and processes up to 10 pending records every cadence tick |
| **Module** | Transaction Management |
| **Severity** | Low |
| **Category** | Repeated Network/DB Calls |
| **Description** | `IngestionOrchestrator.retryPendingFiscalization()` at line 382 queries `getPendingFiscalization(MAX_FISCAL_ATTEMPTS, backoffThreshold, 10)` which returns up to 10 records. For each record, it then performs per-record backoff calculation (line 394), constructs a full `CanonicalTransaction` (lines 415–438), calls `fiscService.submitForFiscalization()`, and updates the database. This runs on every cadence tick (30 seconds) even when there are no pending fiscalization records, though the empty query returns quickly. |
| **Evidence** | `ingestion/IngestionOrchestrator.kt` lines 370–465: entire retry loop. |
| **Impact** | Low: the query returns quickly when empty. When records exist, the sequential HTTP calls to the Advatec device (one per record) add latency to the cadence tick. |
| **Recommended Fix** | Batch the fiscalization submissions if the Advatec API supports it. Consider running fiscalization retries on a separate timer to avoid blocking the main cadence tick. |

---

## AP-005: SplashActivity Uses Handler.postDelayed — Delays App Startup by 2 Seconds

| Field | Value |
|-------|-------|
| **ID** | AP-005 |
| **Title** | Fixed 2-second splash delay adds unnecessary latency to agent startup |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Low |
| **Category** | Blocking Calls on Main Thread |
| **Description** | `SplashActivity.onCreate()` posts a 2-second delayed runnable to navigate to `LauncherActivity`. This is a branding splash — it serves no functional purpose. For a headless service agent that needs to start monitoring fuel pumps as quickly as possible, a 2-second delay on every cold start is unnecessary. The foreground service does not start until after the splash, launcher routing, and provisioning check complete. |
| **Evidence** | `ui/SplashActivity.kt` line 37: `handler.postDelayed(navigateRunnable, 2000)`. |
| **Impact** | 2-second delay before the foreground service starts on every app launch (including boot). |
| **Recommended Fix** | Reduce the splash delay to 500ms or remove it entirely for a headless agent. The splash is only seen by technicians during initial provisioning. Consider making it configurable. |

---

## AP-006: DiagnosticsActivity Creates New TextViews on Every Refresh Cycle

| Field | Value |
|-------|-------|
| **ID** | AP-006 |
| **Title** | Audit log and structured log containers are rebuilt with new TextViews every 5 seconds |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Low |
| **Category** | Large UI Recompositions |
| **Description** | `DiagnosticsActivity.refreshData()` calls `recentErrorsContainer.removeAllViews()` and `structuredLogsContainer.removeAllViews()` then creates new `TextView` instances for each log entry on every refresh (lines 210–243). With 15 audit log entries and 30 structured log entries, this creates and destroys 45 `TextView` objects every 5 seconds. Each `TextView` allocation triggers layout measurement and view inflation costs. |
| **Evidence** | `ui/DiagnosticsActivity.kt` lines 210–243: `removeAllViews()` + `addView(TextView(...))` in a loop, every 5 seconds. |
| **Impact** | Minor GC pressure and layout thrashing. On low-end Android devices, this could cause dropped frames during refresh. |
| **Recommended Fix** | Reuse existing `TextView` instances: maintain a pool of pre-created views, update their text/color properties, and show/hide as needed. Alternatively, use a `RecyclerView` with `DiffUtil` for the log lists. |

---

## AP-007: ConfigManager.validateNumericBounds Runs on Every Config Poll

| Field | Value |
|-------|-------|
| **ID** | AP-007 |
| **Title** | Full config validation (numeric bounds, URL, runtime prerequisites) runs on every poll response |
| **Module** | Site Configuration |
| **Severity** | Low |
| **Category** | Repeated Processing |
| **Description** | `ConfigManager.applyConfig()` runs 5 validation passes (numeric bounds, URL security, runtime prerequisites, FCC support, provisioning fields) on every incoming config, even when the config version has not changed. The `config version <= current` check at line 95 short-circuits for stale configs, but `If-None-Match` handling in `ConfigPollWorker` means unchanged configs return 304 and `applyConfig` is not called. This finding is low-impact since the validation only runs on actual config changes. |
| **Evidence** | `config/ConfigManager.kt` lines 81–198: 5 validation passes. |
| **Impact** | Negligible: validation only runs on actual config changes (not on 304 responses). |
| **Recommended Fix** | No action required. The current design is correct — validation runs only when a new config version arrives. |

---

## AP-008: PreAuthHandler Creates UUID and Instant.now() Multiple Times Per Request

| Field | Value |
|-------|-------|
| **ID** | AP-008 |
| **Title** | Pre-auth handler creates multiple Instant.now() and UUID allocations per request path |
| **Module** | Pre-Authorization |
| **Severity** | Low |
| **Category** | Inefficient Processing |
| **Description** | `PreAuthHandler.handle()` calls `Instant.now().toString()` 4 times (lines 149, 150, 241, 272) and `UUID.randomUUID().toString()` once (line 151). Each `Instant.now()` call involves a system call. Each `toString()` allocates a new string. For the p95 <= 150ms overhead target, these micro-allocations are irrelevant, but they add up across thousands of pre-auth requests per day. |
| **Evidence** | `preauth/PreAuthHandler.kt` lines 149, 150, 241, 272: multiple `Instant.now().toString()` calls. |
| **Impact** | Negligible: each call takes <1ms. Total overhead per request is ~2-3ms. |
| **Recommended Fix** | No action required. The current approach is readable and the overhead is well within the latency budget. |

---

## AP-009: AdvatecWebhookListener Queue Has No Backpressure Mechanism

| Field | Value |
|-------|-------|
| **ID** | AP-009 |
| **Title** | Advatec webhook queue allows 10,000 entries with no memory bound or backpressure |
| **Module** | FCC Adapters (Advatec) |
| **Severity** | Medium |
| **Category** | Large Payload Processing |
| **Description** | `AdvatecWebhookListener` uses a `ConcurrentLinkedQueue` with a maximum of 10,000 entries. Each entry is a deserialized `AdvatecWebhookEnvelope` containing receipt data. If the ingestion orchestrator falls behind (e.g., database is slow), the queue can grow to 10,000 entries in memory. With each envelope containing receipt items (line items, amounts, etc.), this could consume significant heap memory on a constrained Android device. The queue has no backpressure — it accepts entries up to the cap and silently drops new ones. |
| **Evidence** | Adapter exploration: "Enqueues to ConcurrentLinkedQueue. Max queue 10,000. Always responds 200 OK." |
| **Impact** | On a high-volume forecourt with slow ingestion, 10,000 queued webhook payloads could consume 10-50 MB of heap memory, triggering GC pauses or OOM on low-memory devices. |
| **Recommended Fix** | Reduce the queue cap to 1,000. Return HTTP 429 or 503 when the queue is full instead of 200 (so the Advatec device knows to retry). Add memory-size-based limits in addition to count-based limits. |

---

## AP-010: Room Queries Use String Timestamps for Date Comparisons

| Field | Value |
|-------|-------|
| **ID** | AP-010 |
| **Title** | ISO 8601 string comparisons in Room queries are less efficient than epoch milliseconds |
| **Module** | Transaction Management / Pre-Authorization |
| **Severity** | Low |
| **Category** | Inefficient Database Access |
| **Description** | All timestamps in the Room database are stored as ISO 8601 UTC strings (e.g., `"2026-03-13T10:30:00Z"`). Queries like `getExpiring(now)`, `getPendingFiscalization(maxAttempts, backoffThreshold, limit)`, `revertStaleUploaded(cutoff, now)`, and `deleteOldestArchived(batch)` perform string comparisons for date filtering. SQLite string comparisons on ISO 8601 dates are lexicographically correct but slower than integer comparisons on epoch milliseconds. With 30,000+ records, this adds ~2-5ms per query compared to integer comparisons. |
| **Evidence** | `buffer/TransactionBufferManager.kt` line 251: `Instant.now().minus(staleDays.toLong(), ChronoUnit.DAYS).toString()` — string cutoff for date comparison. `preauth/PreAuthHandler.kt` line 379: `Instant.now().toString()` for expiry check. |
| **Impact** | Minor query performance overhead. Acceptable for the current record volumes but may become significant at scale (100K+ records). |
| **Recommended Fix** | Accept current design for now. If performance becomes an issue, migrate timestamps to `Long` (epoch millis) with a Room migration. This would require updating all DAO queries and entity mappings. |

---

## AP-011: ProvisioningViewModel Write-Verify Pattern Doubles Room I/O During Registration

| Field | Value |
|-------|-------|
| **ID** | AP-011 |
| **Title** | Config persistence uses write-then-read verification with retry loop — 4 Room operations for one write |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Low |
| **Category** | Repeated Network/DB Calls |
| **Description** | `ProvisioningViewModel.handleRegistrationSuccess()` at lines 188–203 implements a write-verify-retry pattern for `AgentConfig`: call `agentConfigDao.upsert(entity)`, then `agentConfigDao.get()`, compare `configVersion`, and retry up to 2 times with a 200ms delay. This means up to 4 Room operations (2 × upsert + 2 × get) for a single config write. Room's `@Insert(onConflict = REPLACE)` is already atomic — if `upsert()` succeeds without throwing, the data is persisted. The verification read is redundant. On devices with slow eMMC storage (common on Urovo i9100 terminals), the extra reads add 5–10ms to the registration flow. |
| **Evidence** | `ui/ProvisioningViewModel.kt` lines 188–203: `agentConfigDao.upsert(entity)` followed by `agentConfigDao.get()` in a retry loop. |
| **Impact** | Minor: 5–10ms extra latency during a one-time registration flow. The retry loop adds resilience against transient SQLite lock contention, which has value on WAL-mode databases under concurrent access. |
| **Recommended Fix** | Remove the verification read. Trust Room's `@Insert(onConflict = REPLACE)` — if it doesn't throw, the write succeeded. Keep the retry for the `upsert()` call itself (catch and retry on exception), but remove `agentConfigDao.get()` and the version comparison. |

---

## AP-012: Radix XML Parser Creates New DocumentBuilderFactory Per Parse — Expensive Service Lookup

| Field | Value |
|-------|-------|
| **ID** | AP-012 |
| **Title** | DocumentBuilderFactory.newInstance() called on every XML parse — redundant service provider lookup |
| **Module** | FCC Adapters (Radix) |
| **Severity** | Medium |
| **Category** | Repeated Processing |
| **Description** | `RadixXmlParser.parseXml()` calls `DocumentBuilderFactory.newInstance()` on every invocation (line 237). `DocumentBuilderFactory.newInstance()` performs a service provider lookup (JAR scanning via `ServiceLoader`) on each call, which takes 1–3ms on Android. During a FIFO drain of 50 transactions, this is called at least 50 times (once per transaction in `fetchTransactionsPull`), plus additional calls for signature validation. The factory instance is configured with identical XXE protection settings every time. The `DocumentBuilderFactory` is safe to reuse as long as `DocumentBuilder` instances are not shared across threads (they are not — `parseXml` creates a new builder each call). |
| **Evidence** | `adapter/radix/RadixXmlParser.kt` lines 237–241: `val factory = DocumentBuilderFactory.newInstance()` inside `parseXml()`, called per-parse. |
| **Impact** | 50–150ms of unnecessary overhead per fetch cycle on 50-transaction batches (1–3ms × 50–100 parse calls). On Urovo i9100 devices with slow CPUs, this can push the total fetch time beyond cadence tick targets. |
| **Recommended Fix** | Cache the `DocumentBuilderFactory` as a companion object property, configured once: `private val docBuilderFactory = DocumentBuilderFactory.newInstance().apply { setFeature(...) }`. Create a new `DocumentBuilder` per call (builders are not thread-safe, but factories are). |

---

## AP-013: AdvatecFiscalizationService Uses Busy-Wait Polling for Receipt With Background Thread

| Field | Value |
|-------|-------|
| **ID** | AP-013 |
| **Title** | Fiscalization receipt polling uses Thread.sleep busy-wait and coroutine delay loop in parallel |
| **Module** | FCC Adapters (Advatec) |
| **Severity** | Medium |
| **Category** | Blocking Calls on Main Thread |
| **Description** | `AdvatecFiscalizationService` uses two concurrent polling mechanisms: (1) a daemon `Thread` (lines 212–237) that runs `Thread.sleep(100)` in a loop to drain webhook payloads from the listener and enqueue parsed receipts, and (2) the `submitForFiscalization()` coroutine (lines 152–168) that runs `delay(200)` in a loop to poll the receipt queue. The receipt typically arrives 5–30 seconds after submission. During this wait, the daemon thread executes 50–300 iterations of its polling loop, and the coroutine executes 25–150 iterations of its polling loop — all to check an empty `ConcurrentLinkedQueue`. Additionally, the daemon thread uses `Thread.sleep()` (not coroutine `delay()`), tying up an OS thread that could serve other work. The daemon thread is never stopped (it checks `initialized` but this is only set to `false` in `shutdown()`). |
| **Evidence** | `adapter/advatec/AdvatecFiscalizationService.kt` line 227: `Thread.sleep(100)` in daemon thread. Lines 152–168: `delay(POLL_INTERVAL_MS)` in coroutine. Line 212: `Thread(...)` daemon for drain. |
| **Impact** | Wasted CPU cycles during the 5–30 second receipt wait: ~300 poll iterations per fiscalization call. The daemon thread holds an OS thread indefinitely. |
| **Recommended Fix** | Replace the polling pattern with a `Channel<AdvatecReceiptData>` or `CompletableDeferred<AdvatecReceiptData>`. The webhook drain coroutine sends to the channel; `submitForFiscalization` suspends on `channel.receive()` with a timeout. This eliminates both busy-wait loops and the daemon thread. |

---

## AP-014: DOMS fetchTransactions Normalizes All Transactions Sequentially on Caller's Dispatcher

| Field | Value |
|-------|-------|
| **ID** | AP-014 |
| **Title** | Normalization of 50+ DOMS transactions runs sequentially without explicit dispatcher — blocks cadence tick |
| **Module** | FCC Adapters (DOMS) |
| **Severity** | Low |
| **Category** | Blocking Calls on Main Thread |
| **Description** | `DomsJplAdapter.fetchTransactions()` normalizes all received transactions via `domsTxns.mapNotNull { DomsCanonicalMapper.mapToCanonical(...) }` (lines 253–261). Each normalization call involves timezone conversion (`ZoneId.of`, `LocalDateTime.parse`, `atZone`, `toInstant`), two `UUID.randomUUID()` calls (which access `/dev/urandom`), and product code map lookups. With 50+ transactions per batch, this adds ~50–100ms of CPU work. The function runs on whatever dispatcher the caller provides — typically `Dispatchers.Default` via `IngestionOrchestrator`. While not a blocking I/O call, the sequential CPU-bound normalization prevents the coroutine from yielding during the batch, potentially delaying other cadence-driven operations (pre-auth checks, telemetry) scheduled on the same dispatcher. |
| **Evidence** | `adapter/doms/DomsJplAdapter.kt` lines 253–261: sequential `mapNotNull` normalization. `adapter/doms/mapping/DomsCanonicalMapper.kt` line 55: `java.time.Instant.now()`, lines 45–50: timezone parsing, line 59: `UUID.randomUUID()`. |
| **Impact** | Low: 50–100ms CPU time per fetch is within the cadence budget (30s tick). Only becomes an issue if batch sizes grow significantly (200+ transactions) or the device CPU is heavily loaded. |
| **Recommended Fix** | No immediate action required. If batch sizes grow, consider yielding periodically with `yield()` inside the loop, or processing normalization on `Dispatchers.Default` explicitly (with `withContext`) to ensure the work is on the correct dispatcher. |

---

## AP-015: Acknowledge Endpoint Loads Full Entity N Times for Existence Check

| Field | Value |
|-------|-------|
| **ID** | AP-015 |
| **Title** | POST /api/v1/transactions/acknowledge executes N individual SELECT * queries to count existing records |
| **Module** | Transaction Management |
| **Severity** | Medium |
| **Category** | Repeated Network/DB Calls |
| **Description** | The acknowledge endpoint iterates through `request.transactionIds` and calls `dao.getById(id)` for each ID: `val found = request.transactionIds.count { id -> dao.getById(id) != null }` (line 160). Each `getById()` executes `SELECT * FROM buffered_transactions WHERE id = :id`, returning the full `BufferedTransaction` entity including `rawPayloadJson` (2–5 KB per record). For a batch of 50 transaction IDs, this executes 50 separate SELECT queries and loads 100–250 KB of entity data into memory, only to check for null and discard the entire object. A single `SELECT COUNT(*) FROM buffered_transactions WHERE id IN (:ids)` would accomplish the same result with one query and no entity materialization. Additionally, beyond the functional issue (AF-022: the endpoint is a no-op), even if the endpoint were implemented correctly, the N-query pattern would still be inefficient for marking records. |
| **Evidence** | `api/TransactionRoutes.kt` line 160: `val found = request.transactionIds.count { id -> dao.getById(id) != null }`. `buffer/dao/TransactionBufferDao.kt` line 63: `@Query("SELECT * FROM buffered_transactions WHERE id = :id")` — returns full entity with rawPayloadJson. |
| **Impact** | For a batch of 50 IDs: 50 SQLite round-trips, ~250 KB of data loaded and immediately discarded. On the Urovo i9100 with slow eMMC, each query takes 1–3 ms, so the endpoint takes 50–150 ms — approaching the p95 target for the local API. |
| **Recommended Fix** | Replace with a single count query: add `@Query("SELECT COUNT(*) FROM buffered_transactions WHERE id IN (:ids)") suspend fun countByIds(ids: List<String>): Int` to `TransactionBufferDao`. Use this instead of N individual `getById` calls. When the acknowledge endpoint is fixed (AF-022), use a batch UPDATE instead. |

---

## AP-016: Per-Client Pump Status Broadcast Duplicates FCC Adapter Queries

| Field | Value |
|-------|-------|
| **ID** | AP-016 |
| **Title** | OdooWebSocketServer launches a separate pump status polling coroutine per connected client |
| **Module** | Transaction Management / POS Integration |
| **Severity** | Medium |
| **Category** | Repeated Network/DB Calls |
| **Description** | `OdooWebSocketServer.onClientConnected()` launches a dedicated `pumpStatusBroadcastLoop` coroutine per connected WebSocket client (line 184). Each coroutine independently calls `adapter.getPumpStatus()` every 3 seconds (configurable). With 5 connected POS terminals, this results in 5 independent `getPumpStatus()` calls per 3 seconds — all going to the same FCC adapter. For DOMS, each `getPumpStatus()` sends a JPL request over the TCP connection; for Radix, it sends an HTTP request. The responses are identical across all 5 calls since pump status changes on a timescale of seconds, not sub-seconds. The calls could be deduplicated: fetch statuses once per interval and fan out to all connected clients. |
| **Evidence** | `websocket/OdooWebSocketServer.kt` lines 183–187: `val broadcastJob = serviceScope.launch { pumpStatusBroadcastLoop(session) }` — one coroutine per client. Lines 272–289: `pumpStatusBroadcastLoop` calls `adapter.getPumpStatus()` independently per client. |
| **Impact** | With N connected POS terminals, the FCC adapter receives N × (60/interval) status requests per minute instead of (60/interval). For 5 clients at 3-second intervals: 100 requests/minute instead of 20. On DOMS TCP (shared connection), this increases JPL frame traffic 5×. On Radix HTTP, this creates 5× the HTTP connections to the FCC. |
| **Recommended Fix** | Replace per-client broadcast loops with a single shared broadcast coroutine. Fetch pump statuses once per interval, then iterate over all connected clients to send the result. Example: `serviceScope.launch { while(isActive) { delay(intervalMs); val statuses = adapter.getPumpStatus(); for (session in clients.keys) { sendStatuses(session, statuses) } } }`. Remove the per-client `broadcastJob`. |
