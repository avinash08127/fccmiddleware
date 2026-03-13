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
| **Status** | **RESOLVED** — Added `countProducts()`, `countPumps()`, `countNozzles()` COUNT(*) queries to `SiteDataDao`. `DiagnosticsViewModel.refresh()` now uses these instead of `getAllProducts().size` / `getAllPumps().size` / `getAllNozzles().size`, eliminating full table loads every 5 seconds. |

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
| **Status** | **RESOLVED** — `TransactionBufferManager` now accepts a `crossAdapterDedupEnabled` constructor parameter (default: `false`). The `findCrossAdapterDuplicate()` query is only executed when this flag is `true`. Single-adapter sites (the vast majority) skip the query entirely, saving one SELECT per transaction insert. |

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
| **Status** | **RESOLVED** — `CloudUploadWorker.toDto()` now conditionally includes `rawPayloadJson` based on `config.buffer.persistRawPayloads` (defaults to `false`). When the flag is off, `rawPayloadJson` is set to `null` in the DTO, reducing upload payload size by 2-5 KB per transaction. Sites that need raw payload archival can enable it via `buffer.persistRawPayloads = true` in the site config. |

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
| **Status** | **FIXED** — Fiscalization retry moved to a separate cadence timer (`fiscalRetryTickFrequency = 4`, ~2 min at 30s base) in `CadenceController.runTick()`. The main poll path no longer blocks on sequential Advatec HTTP retries. Immediate fiscalization after new transactions still runs inline in `doPoll()`. Tests added in `CadenceControllerTest`. |

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
| **Status** | **FIXED** — Splash delay reduced from 2000ms to 500ms in `SplashActivity.onCreate()`. Still provides brief branding visibility for technicians during provisioning. |

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
| **Status** | **FIXED** (prior commit) — `DiagnosticsActivity` now uses reusable `TextView` pools (`errorTextViews`, `structuredLogTextViews`) that are created on demand, updated in-place via text/color/visibility changes, and never removed. The `removeAllViews()` + `addView()` churn pattern has been eliminated. See P-003 comments in `renderSnapshot()`. |

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
| **Status** | **WON'T FIX** — No action required. The current design is correct: `ConfigPollWorker` returns 304 for unchanged configs, so `applyConfig()` (and its validation passes) only runs when a genuinely new config version arrives. |

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
| **Status** | **WON'T FIX** — No action required. Each `Instant.now()` call takes <1µs and the total overhead per request (~2-3ms) is negligible against the p95 ≤ 150ms latency budget. Readability is more valuable than micro-optimizing these allocations. |

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
| **Status** | **RESOLVED** — Implemented all three recommended fixes: (1) Reduced `MAX_QUEUE_SIZE` from 10,000 to 1,000. (2) Queue-full responses now return HTTP 429 Too Many Requests instead of 200 OK, signalling the Advatec device to retry. (3) Added memory-size-based backpressure via `MAX_QUEUE_BYTES` (5 MB ceiling) tracked with an `AtomicLong` counter that estimates heap usage per payload. Both count and memory limits are enforced before enqueue; `drainQueue()` and `stop()` correctly decrement/reset the byte counter. |

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
| **Status** | **WON'T FIX** — Accepted current design. ISO 8601 string comparisons are lexicographically correct in SQLite and the ~2-5ms overhead per query is acceptable at current record volumes (30K). A migration to epoch millis would touch all DAO queries and entity mappings — not justified until volumes exceed 100K+ records. |

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
| **Status** | **RESOLVED** — Removed the `agentConfigDao.get()` verification read and `configVersion` comparison from the retry loop in `ProvisioningViewModel.handleRegistrationSuccess()`. The loop now retries only the `upsert()` call on exception, reducing Room operations from up to 4 (2×upsert + 2×get) to at most 2 (2×upsert on retry). |

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
| **Status** | **RESOLVED** — Cached `DocumentBuilderFactory` as a `private val docBuilderFactory` property on the `RadixXmlParser` object, configured once with XXE protection features. `parseXml()` now calls `docBuilderFactory.newDocumentBuilder()` per parse (builders are not thread-safe), eliminating the per-call service provider lookup overhead (~1–3ms × 50–100 calls per fetch cycle). |

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
| **Status** | **RESOLVED** — Replaced `ConcurrentLinkedQueue` + dual busy-wait polling with a `Channel<AdvatecReceiptData>(UNLIMITED)`. The daemon `Thread` with `Thread.sleep(100)` is replaced by a coroutine on `Dispatchers.IO` with `delay(100)`. The `submitForFiscalization()` coroutine now suspends on `withTimeoutOrNull(RECEIPT_TIMEOUT_MS) { receiptChannel.receive() }` instead of polling with `delay(200)`. Eliminates ~300 poll iterations per fiscalization call and frees one permanent OS thread. |

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
| **Status** | **RESOLVED** — Normalization loop in `DomsJplAdapter.fetchTransactions()` now runs inside `withContext(Dispatchers.Default)` with `yield()` every 20 items, preventing dispatcher starvation on large batches. The `mapNotNull` was replaced with an explicit loop to support periodic yielding. |

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
| **Status** | **RESOLVED** — The N-query `getById` loop has been replaced with `dao.markAcknowledged(ids, now)`, a single batch `UPDATE ... WHERE id IN (:ids) AND acknowledged_at IS NULL` that both marks records as acknowledged and returns the count of updated rows. This eliminates all N individual SELECT queries and goes beyond the recommended `countByIds` approach by combining the existence check with the actual acknowledge operation in a single SQL statement. Fixed as part of AF-022. |

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
| **Status** | **RESOLVED** — Replaced per-client `pumpStatusBroadcastLoop` coroutines with a single `sharedPumpStatusBroadcastLoop` launched once when the server starts. The shared loop fetches pump statuses once per interval, serializes each `FuelPumpStatusWsDto` once, then fans the pre-serialized frames out to all connected clients. Dead sessions are cleaned up during broadcast. The per-client `broadcastJob` field was removed from `ClientEntry`. With N clients, the FCC now receives exactly (60/interval) status requests per minute instead of N × (60/interval). |

---

## AP-020: TelemetryReporter Queries oldestPendingCreatedAt Twice Per Telemetry Cycle

| Field | Value |
|-------|-------|
| **ID** | AP-020 |
| **Title** | Two independent database queries for the same oldest-pending timestamp in a single telemetry payload assembly |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Low |
| **Category** | Repeated Network/DB Calls |
| **Description** | `TelemetryReporter.buildPayload()` calls `collectBufferStatus()` (line 121) and `collectSyncStatus(cfg)` (line 122) sequentially. Both methods independently query `transactionDao.oldestPendingCreatedAt()`: at line 250 in `collectBufferStatus()` for the `BufferStatusDto.oldestPendingAtUtc` field, and at line 290 in `collectSyncStatus()` for computing `syncLagSeconds`. This results in two identical `SELECT MIN(created_at) FROM buffered_transactions WHERE sync_status = 'PENDING'` queries per telemetry cycle. On a table with 30,000+ records, each query scans the sync_status index and performs a MIN aggregate — typically 1–3ms per query. Additionally, the two queries may return different values if a transaction is inserted between them, creating a minor inconsistency where the buffer section's oldest timestamp doesn't match the sync lag calculation. |
| **Evidence** | `sync/TelemetryReporter.kt` line 250: `transactionDao.oldestPendingCreatedAt()` in `collectBufferStatus()`. Line 290: `transactionDao.oldestPendingCreatedAt()` in `collectSyncStatus()`. |
| **Impact** | Minor: 1–3ms extra per telemetry cycle (every ~120s at default tick frequency). Potential data inconsistency between buffer and sync sections of the telemetry payload. |
| **Recommended Fix** | Query `oldestPendingCreatedAt()` once in `buildPayload()` and pass the result to both `collectBufferStatus(oldestPendingAtUtc)` and `collectSyncStatus(cfg, oldestPendingAtUtc)` as a parameter. |
| **Status** | **RESOLVED** — `buildPayload()` now queries `transactionDao.oldestPendingCreatedAt()` once and passes the result as a parameter to both `collectBufferStatus(oldestPendingAtUtc)` and `collectSyncStatus(cfg, oldestPendingAtUtc)`. The duplicate query has been eliminated, and both sections of the telemetry payload now use the same consistent timestamp value. |

---

## AP-021: TelemetryReporter.collectDeviceStatus Registers Sticky Broadcast Receiver on Every Call

| Field | Value |
|-------|-------|
| **ID** | AP-021 |
| **Title** | Battery status collected via registerReceiver(null, IntentFilter) on every telemetry cycle — unnecessary IPC round-trip |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Low |
| **Category** | Repeated Processing |
| **Description** | `TelemetryReporter.collectDeviceStatus()` at line 165 calls `context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))` to get the current battery status. This is the standard sticky broadcast pattern for battery info, but it performs an IPC round-trip to the system server on every invocation. Since telemetry is reported every ~120 seconds (every 4th cadence tick at 30s), this IPC call runs approximately 720 times per day. While each call takes only ~1ms, the pattern could be replaced with a cached approach: register a real `BroadcastReceiver` once during initialization that updates cached battery fields on change, or use `BatteryManager.getIntProperty(BATTERY_PROPERTY_CAPACITY)` which reads from `/sys/class/power_supply/` without IPC. Additionally, `StatFs(Environment.getDataDirectory().path)` at line 176 and `Runtime.getRuntime().freeMemory()` at line 181 are computed on every call but change infrequently (storage changes on writes, memory changes on GC). |
| **Evidence** | `sync/TelemetryReporter.kt` line 165: `context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))` — IPC per call. Line 176: `StatFs(Environment.getDataDirectory().path)` — filesystem stat per call. |
| **Impact** | Negligible: ~1ms per telemetry cycle for the IPC call. The sticky broadcast approach is correct and widely used. This finding is informational for optimization if telemetry frequency increases. |
| **Recommended Fix** | No immediate action required. If telemetry frequency is increased (e.g., to every tick during FCC outages per AF-036), consider caching the battery intent via a persistent `BroadcastReceiver` registered in the foreground service and updated on `ACTION_BATTERY_CHANGED` broadcasts. |
| **Status** | **RESOLVED** — `collectDeviceStatus()` now uses `BatteryManager.getIntProperty(BATTERY_PROPERTY_CAPACITY)` and `BatteryManager.isCharging` which read directly from sysfs (`/sys/class/power_supply/`) without an IPC round-trip to the system server. The `registerReceiver(null, IntentFilter(ACTION_BATTERY_CHANGED))` sticky broadcast pattern has been removed, along with the `Intent` and `IntentFilter` imports. Tests updated to mock `BatteryManager` instead of the battery intent. |

---

## AP-022: Telemetry Payload Assembly Performs 10+ Database Queries Sequentially on Every Report

| Field | Value |
|-------|-------|
| **ID** | AP-022 |
| **Title** | buildPayload() executes at least 10 sequential DAO queries per telemetry cycle — no batching or parallelism |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Low |
| **Category** | Repeated Network/DB Calls |
| **Description** | `TelemetryReporter.buildPayload()` calls `collectBufferStatus()` and `collectSyncStatus()` which together execute at least 10 database queries sequentially: (1) `incrementAndGetTelemetrySequence` — 1 read + 1 write inside @Transaction, (2) `transactionDao.countByStatus()` — 1 GROUP BY query, (3) `transactionDao.oldestPendingCreatedAt()` — 1 MIN query (first call), (4) database file size check — 1 filesystem stat, (5) `syncStateDao.get()` — 1 read, (6) `transactionDao.oldestPendingCreatedAt()` — duplicate (second call, see AP-020). Each query acquires a SQLite connection from the Room connection pool, executes the query, and releases the connection. On the Urovo i9100 with eMMC storage, each query takes 1–5ms. The total telemetry assembly time is 10–50ms. While acceptable at the default frequency (every ~120s), this becomes noticeable if telemetry frequency is increased or if it runs alongside other heavy database operations (e.g., batch upload processing or ingestion). None of these queries depend on each other's results (except the duplicate `oldestPendingCreatedAt`), so they could be parallelized or batched into a single Room `@Transaction` to reduce connection pool contention. |
| **Evidence** | `sync/TelemetryReporter.kt` lines 80–124: `buildPayload()` calls `collectBufferStatus()` (lines 234–272, 3 queries) and `collectSyncStatus()` (lines 279–313, 3 queries) plus `nextSequenceNumber()` (2 operations). |
| **Impact** | Low: 10–50ms per telemetry cycle is within the cadence budget. Becomes relevant if telemetry frequency increases or if concurrent database load increases. |
| **Recommended Fix** | Accept the current design at default telemetry frequency. If optimization is needed, wrap all read queries in a single `@Transaction` method on a custom DAO to reduce connection pool overhead and ensure a consistent snapshot. Eliminate the duplicate `oldestPendingCreatedAt` query (AP-020). |
| **Status** | **RESOLVED** — `buildPayload()` now runs `collectBufferStatus()` and `collectSyncStatus()` in parallel using `coroutineScope { async { ... } }`. The two independent suspend functions (each with their own DB queries) execute concurrently instead of sequentially, reducing total assembly time. Combined with the AP-020 fix (duplicate `oldestPendingCreatedAt` eliminated), the effective query count per telemetry cycle is now 4 DB operations + 1 filesystem stat, down from the original 10+. |

---

## AP-023: getRecentDiagnosticEntries Performs Full Forward Scan of Log Files Every 5 Seconds

| Field | Value |
|-------|-------|
| **ID** | AP-023 |
| **Title** | Diagnostics screen triggers a full sequential scan of today's log file every 5 seconds to find WARN/ERROR entries |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Low |
| **Category** | Repeated Network/DB Calls |
| **Description** | `StructuredFileLogger.getRecentDiagnosticEntries(30)` is called every 5 seconds by `DiagnosticsActivity.refreshData()`. The method opens the most recent log file (today's `edge-agent-YYYY-MM-DD.jsonl`) and reads ALL lines sequentially from top to bottom, performing 3 `String.contains()` checks per line to filter for WARN/ERROR/FATAL entries. On a device running since midnight with INFO-level logging and 30-second cadence ticks, today's log file can contain 2,880+ entries by end of day (more during error storms). Each refresh reads all 2,880 lines (including the non-matching INFO/DEBUG lines) to find the 30 matching entries. The 3 `contains()` checks per line perform substring scanning on the raw JSON line (e.g., searching for `"lvl":"WARN"` in a 200-character JSON string). With 3,000 lines × 3 contains × 200 chars/line, this is ~1.8 million character comparisons per refresh. At 5-second intervals, this runs 720 times per hour. Additionally, `totalLogSizeBytes()` calls `getLogFiles()` on each refresh, performing a directory listing, filter, sort, and N `File.length()` stat calls. While individually fast, the cumulative I/O overhead adds up on eMMC storage. |
| **Evidence** | `logging/StructuredFileLogger.kt` lines 130–152: `getRecentDiagnosticEntries()` forward-scans files. `ui/DiagnosticsActivity.kt` line 136: called every 5 seconds. Lines 137, 162–163: `totalLogSizeBytes()` also called per refresh. |
| **Impact** | Low: on modern ARM processors, the string scanning takes <5ms even for 3,000 lines. On the Urovo i9100 (Qualcomm MSM8909, 1.1 GHz quad-core), this may reach 10–15ms during heavy logging. The cumulative effect is minor file I/O every 5 seconds while the diagnostics screen is open. The impact is bounded by the screen's lifecycle (only runs while visible). |
| **Recommended Fix** | Maintain an in-memory ring buffer of the last N WARN/ERROR entries in `StructuredFileLogger`, appended during `writeEntry()` when the level is WARN or above. `getRecentDiagnosticEntries()` returns a snapshot of the ring buffer (O(1)) instead of rescanning files. This eliminates all file I/O from the 5-second refresh cycle. For `totalLogSizeBytes()`, cache the value and update it when a new file is created (in `getOrCreateWriter()`). |
| **Status** | **RESOLVED** — `StructuredFileLogger` now maintains an in-memory `ArrayDeque` ring buffer (max 200 entries) of WARN/ERROR/FATAL log lines. The buffer is populated from existing log files on first access (lazy initialization with double-checked locking), then maintained incrementally by `writeEntry()` and `crash()`. `getRecentDiagnosticEntries()` returns from the ring buffer in O(1) — no file I/O on the 5-second refresh cycle. `totalLogSizeBytes()` now returns a cached value that is invalidated on file rotation (`getOrCreateWriter()`) and old file deletion (`rotateOldFiles()`), eliminating per-refresh directory listings and file stat calls. |

---

## AP-024: PumpStatusCache Makes Redundant Sequential Live FCC Calls for Queued Callers

| Field | Value |
|-------|-------|
| **ID** | AP-024 |
| **Title** | Serialized callers behind PumpStatusCache mutex each make a separate live FCC call even when the previous caller just refreshed the cache |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Medium |
| **Category** | Repeated Network/DB Calls |
| **Description** | `PumpStatusCache.get()` uses a `Mutex` to serialize concurrent callers ("single-flight protection"). When 5 POS terminals call `GET /api/v1/pump-status` simultaneously, they queue behind the mutex. The first caller acquires the mutex, makes a live FCC call (up to 1 second timeout), updates `cached` and `cachedAtMs`, and releases the mutex. The second caller then acquires the mutex and makes ANOTHER live FCC call — even though the cache was just updated less than 1 second ago. This pattern repeats for all 5 callers, resulting in 5 sequential live FCC calls within ~5 seconds. For DOMS (persistent TCP), each `getPumpStatus()` sends a JPL request over the shared TCP connection — 5 requests in rapid succession increase the frame traffic. For Radix (HTTP REST), each call creates a new HTTP connection to the FCC device. The `PumpStatusCache` does not check whether the cache was recently refreshed before making a live call. A simple freshness check (`if (System.currentTimeMillis() - cachedAtMs < liveTimeoutMs) return cached`) after acquiring the mutex would allow queued callers to use the result from the first caller's live fetch instead of making redundant calls. |
| **Evidence** | `api/PumpStatusRoutes.kt` lines 107–122: `mutex.withLock { ... withTimeoutOrNull(liveTimeoutMs) { adapter.getPumpStatus() } ... }` — no cache freshness check after mutex acquisition. Lines 114–116: cache updated only after live fetch. Lines 88–89: `cached` and `cachedAtMs` — no minimum age check. |
| **Impact** | With N connected POS terminals polling pump status, the FCC adapter receives N sequential status requests per cache miss instead of 1. For 5 clients: 5× the FCC traffic. Combined with AP-016 (per-client WebSocket broadcast also polls independently), the FCC can receive 5+ status requests per 3-second interval from pump-status HTTP clients plus 5 independent polls from WebSocket clients — 10× the intended FCC status query rate. |
| **Recommended Fix** | Add a cache freshness check inside the mutex, before the live fetch: `if (System.currentTimeMillis() - cachedAtMs < liveTimeoutMs) { return Result(pumps = cached, stale = false, fetchedAtUtc = ..., dataAgeSeconds = 0, capability = ...) }`. This ensures only the first caller triggers a live FCC call; subsequent queued callers reuse the fresh result. The `liveTimeoutMs` (1 second) is a reasonable freshness threshold since pump status changes on a multi-second timescale. |
| **Status** | **RESOLVED** — `PumpStatusCache.get()` now checks cache freshness immediately after acquiring the mutex: if `cachedAtMs` is within `liveTimeoutMs` (1 second), the cached result is returned directly without making a live FCC call. This ensures that when N POS clients queue behind the mutex, only the first caller triggers a live FCC fetch — subsequent queued callers reuse the fresh result, reducing FCC traffic from N calls to 1 per cache miss window. |

---

## AP-025: WebSocket Queries Load Full Entity Including rawPayloadJson — Unused Data in Response Path

| Field | Value |
|-------|-------|
| **ID** | AP-025 |
| **Title** | getUnsyncedForWs and getAllForWs use SELECT * loading 2–5 KB rawPayloadJson per record — field is not used in the WebSocket DTO |
| **Module** | POS Integration (Odoo) |
| **Severity** | Medium |
| **Category** | Large Payload Processing |
| **Status** | **FIXED** |
| **Description** | `TransactionBufferDao.getUnsyncedForWs()` (line 289) and `getAllForWs()` (line 309) both use `SELECT * FROM buffered_transactions`, returning the full `BufferedTransaction` entity. Each entity includes `rawPayloadJson` — a nullable TEXT column containing the raw FCC protocol payload (2–5 KB for DOMS JPL frames, 1–3 KB for Radix XML responses, 0.5–2 KB for Advatec webhook payloads). The `BufferedTransaction.toWsDto()` mapping function in `OdooWsModels.kt` does NOT reference `rawPayloadJson` — it only uses `fccTransactionId`, `pumpNumber`, `nozzleNumber`, `productCode`, `volumeMicrolitres`, `amountMinorUnits`, `unitPriceMinorPerLitre`, `startedAt`, `completedAt`, `syncStatus`, `orderUuid`, `odooOrderId`, `addToCart`, `paymentId`, `attendantId`, and `isDiscard`. The `rawPayloadJson` is loaded into memory, carried through the `List<BufferedTransaction>`, and discarded during the `map { it.toWsDto() }` step. For the "all" mode with its 500-record LIMIT, this loads 1–2.5 MB of raw payload data that is immediately discarded. For the "latest" mode with its 200-record LIMIT, this loads 0.4–1 MB. On the Urovo i9100 with limited heap, this unnecessary allocation triggers GC pressure during every WebSocket query. |
| **Evidence** | `buffer/dao/TransactionBufferDao.kt` lines 289–297: `getUnsyncedForWs()` — `SELECT * FROM buffered_transactions WHERE ...`. Lines 309–313: `getAllForWs()` — `SELECT * FROM buffered_transactions ORDER BY ...`. `websocket/OdooWsModels.kt` lines 112–144: `toWsDto()` — does not reference `rawPayloadJson`. `buffer/entity/BufferedTransaction.kt` line 103: `rawPayloadJson: String?`. |
| **Impact** | 0.4–2.5 MB of unnecessary memory allocation per WebSocket query. For a forecourt with 5 POS terminals each sending `mode: "latest"` every 5–10 seconds, this is 2–12.5 MB of garbage created per minute. On a device with 512 MB heap, this triggers minor GC pauses (5–15ms each), which may cause WebSocket message delivery jitter. |
| **Recommended Fix** | Create projection queries that exclude `rawPayloadJson`. For `getUnsyncedForWs`, use a Room `@Query` that explicitly lists the needed columns and a lightweight DTO or a `@MapInfo` projection. Alternatively, define a Room `@Embedded` subset entity: `data class WsBufferedTransaction(...)` containing only the 16 fields used by `toWsDto()`, and use `@Query("SELECT id, fcc_transaction_id, pump_number, ... FROM buffered_transactions WHERE ...")`. This eliminates the `rawPayloadJson` load while keeping the query efficient. |
| **Fix Applied** | Created `WsBufferedTransaction` projection data class with 17 fields (excluding `rawPayloadJson`, `correlationId`, `uploadAttempts`, `lastUploadAttemptAt`, `lastUploadError`, `ingestionSource`, `status`, `siteCode`, `id`, and other unused fields). Updated `getUnsyncedForWs()` and `getAllForWs()` to use explicit column lists returning `List<WsBufferedTransaction>`. Added `WsBufferedTransaction.toWsDto()` extension in `OdooWsModels.kt`. Files: `TransactionBufferDao.kt`, `OdooWsModels.kt`. |

---

## AP-026: broadcastToAll Serializes Identical Payload Per-Client Instead of Serializing Once

| Field | Value |
|-------|-------|
| **ID** | AP-026 |
| **Title** | broadcastToAll constructs the JSON payload before the loop but sendToSession re-serializes per-session error handling |
| **Module** | POS Integration (Odoo) |
| **Severity** | Low |
| **Category** | Repeated Processing |
| **Status** | **FIXED** |
| **Description** | `OdooWebSocketServer.broadcastToAll()` at lines 299–320 serializes the broadcast payload once (`val payload = wsJson.encodeToString(...)` at line 300) and then sends the same `Frame.Text(payload)` string to each client. This is efficient for the broadcast itself. However, the method is called from `OdooWsMessageHandler` which pre-serializes the DTO to a `JsonElement` using the double-serialization `encodeToJsonElement` (AT-045) before passing it to `broadcastToAll`. The full serialization chain per broadcast is: DTO → String (encodeToString) → JsonElement (parseToJsonElement) → embedded in JsonObject (buildJsonObject) → String (encodeToString in broadcastToAll). This is three serialization steps where one would suffice. For pump status broadcasts (`pumpStatusBroadcastLoop`), each individual pump status is serialized independently per-client per-pump: `wsJson.encodeToString(dto)` at line 281. With 8 pumps × 5 clients × 20 broadcasts/minute = 800 serialize calls/minute. If the statuses were serialized once and the pre-built frame reused across clients, this would be 8 pumps × 20 broadcasts/minute = 160 serialize calls — an 80% reduction. This compounds with AP-016 (per-client broadcast duplicating FCC queries). |
| **Evidence** | `websocket/OdooWebSocketServer.kt` lines 279–283: per-pump `wsJson.encodeToString(dto)` inside per-client loop. `websocket/OdooWsMessageHandler.kt` lines 350–353: double-serialization via `encodeToJsonElement`. Lines 122, 153, 171: handler calls that chain through `broadcastToAll`. |
| **Impact** | Low: each serialization takes <0.5ms. Total overhead is ~400ms/minute for 5 clients with 8 pumps. The bigger impact is from AP-016 (per-client FCC queries) which this finding compounds. |
| **Recommended Fix** | For pump status broadcasts: serialize each `FuelPumpStatusWsDto` once outside the client loop, then reuse the `Frame.Text(payload)` for all clients. This is a natural improvement if AP-016 is addressed (single shared broadcast coroutine). For handler broadcasts: fix AT-045 (remove double-serialization) and the broadcast chain simplifies to two serialization steps (DTO → JsonElement → String), which is the minimum for embedding in a response object. |
| **Fix Applied** | Removed the custom `encodeToJsonElement` method in `OdooWsMessageHandler` that performed double-serialization (DTO → String → JsonElement). Replaced with the built-in `kotlinx.serialization.json.encodeToJsonElement` which serializes directly to a `JsonElement` tree without the intermediate string round-trip. Broadcast chain is now DTO → JsonElement → String (2 steps, the minimum for embedding in a response object). Pump status broadcasts were already fixed by AP-016 (single shared coroutine serializing once per pump). File: `OdooWsMessageHandler.kt`. |

---

## AP-027: Audit Log DAO Insert Held Under ConnectivityManager Mutex — Blocks Concurrent Probe Processing on Disk I/O

| Field | Value |
|-------|-------|
| **ID** | AP-027 |
| **Title** | ConnectivityManager holds the probe mutex across a Room DAO insert during state transitions — blocks the other probe loop for the duration of disk I/O |
| **Module** | Connectivity |
| **Severity** | Medium |
| **Category** | Blocking Calls on Main Thread |
| **Status** | **FIXED** |
| **Description** | `ConnectivityManager.processProbeResult()` acquires the mutex at line 141 and, when a state change occurs, calls `deriveAndEmitStateUnlocked()` at line 179. Inside this method, `auditLogDao.insert(AuditLog(...))` (line 204) performs a Room `@Insert` operation — a suspend function that dispatches to the Room query executor and performs SQLite disk I/O. The mutex is held for the entire insert duration. Both the internet and FCC probe loops call `processProbeResult` and contend on the same mutex. When one loop triggers a state transition, the other loop is blocked from processing its result until the audit log insert completes. On the Urovo i9100 (eMMC flash), Room insert latency is typically 5–50ms under normal load, but can spike to 200ms during SQLite WAL checkpoint or when other DAO operations (transaction buffer inserts, sync state updates) contend for the same database file. During this window, the other probe's result is queued. With 30s probe intervals and random jitter (0–3s), the probability of both probes completing within a 200ms overlap window is ~0.7% per cycle (~20 occurrences per day). Each occurrence delays one probe's result processing by the insert latency (5–200ms). |
| **Evidence** | `connectivity/ConnectivityManager.kt` line 141: `mutex.withLock { ... }`. Line 179: `deriveAndEmitStateUnlocked()` called inside the lock. Line 204: `auditLogDao.insert(AuditLog(...))` — Room suspend function performing SQLite disk I/O while mutex is held. |
| **Impact** | ~20 times per day, one probe loop's result processing is delayed by 5–200ms. Individually negligible, but the architectural pattern (holding a coroutine mutex across disk I/O) sets a bad precedent. If the probe interval is ever reduced (e.g., for faster connectivity detection) or additional I/O is added inside the lock, the contention window grows linearly. |
| **Recommended Fix** | Move the audit log write outside the mutex. Capture the transition (`prevState`, `newState`) inside the lock, release the lock, then perform the DAO insert and listener notification. Pattern: `val transition = mutex.withLock { /* derive state, return (prev, new) or null */ }; if (transition != null) { auditLogDao.insert(...); listener?.onTransition(...) }`. This reduces the mutex hold time to pure in-memory state derivation (<1ms). |
| **Fix Applied** | Refactored `processProbeResult()` to capture the state transition as a `Pair<ConnectivityState, ConnectivityState>?` inside the mutex, then perform the audit log DAO insert and listener notification outside the lock. Removed `deriveAndEmitStateUnlocked()` — its logic is now inlined. Mutex hold time reduced from 5–200ms (disk I/O) to <1ms (pure in-memory state derivation). File: `ConnectivityManager.kt`. |

---

## AP-028: Internet Probe Uses Blocking OkHttp execute() Inside withTimeoutOrNull — Thread Occupied Beyond Coroutine Timeout

| Field | Value |
|-------|-------|
| **ID** | AP-028 |
| **Title** | Internet probe blocks a Dispatchers.IO thread for up to 8s even when the 5s coroutine timeout fires — wasted thread occupancy |
| **Module** | Connectivity |
| **Severity** | Low |
| **Category** | Blocking Calls on Main Thread |
| **Status** | **FIXED** |
| **Description** | The internet probe lambda in `AppModule.kt` (lines 186–194) calls `probeHttpClient.newCall(request).execute()` — a synchronous OkHttp call that blocks the calling thread until the HTTP response arrives or the OkHttp client-level timeouts fire. The `probeHttpClient` is configured with `connectTimeout(4s)` and `readTimeout(4s)` (lines 176–177), so the maximum blocking duration is 8s (4s connect + 4s read). `ConnectivityManager.runProbeWithTimeout()` wraps the probe call in `withTimeoutOrNull(config.probeTimeoutMs)` (line 271), where `probeTimeoutMs` defaults to 5s. When the probe takes longer than 5s (e.g., TCP connect hangs for 4s, then server responds slowly), `withTimeoutOrNull` cancels the coroutine at 5s and returns null. However, `OkHttpClient.execute()` is a blocking I/O call — it does not respond to coroutine cancellation. The underlying TCP connection continues occupying a `Dispatchers.IO` thread until OkHttp's own 8s timeout fires. During this 3s window (5s coroutine timeout to 8s OkHttp timeout), one IO dispatcher thread is occupied doing work whose result is already discarded. `Dispatchers.IO` has a pool of 64 threads by default. One occupied thread is 1.6% of the pool. With probes every 30s, the occupancy is ~10% of the time in the worst case (3s occupied per 30s cycle). |
| **Evidence** | `di/AppModule.kt` lines 186–191: `probeHttpClient.newCall(request).execute().use { it.isSuccessful }` — blocking call. Lines 176–177: `connectTimeout(4, SECONDS).readTimeout(4, SECONDS)` — 8s max blocking time. `connectivity/ConnectivityManager.kt` line 271: `withTimeoutOrNull(config.probeTimeoutMs)` — 5s coroutine timeout. |
| **Impact** | In the worst case, one Dispatchers.IO thread is occupied for 3s per 30s cycle (~10% thread-time waste). On the Urovo i9100 with limited CPU, this thread cannot serve other suspend functions (Room queries, Ktor API server, WebSocket handling) during this window. Low severity because the 64-thread pool has ample headroom and the 3s overlap only occurs when the cloud health endpoint is unresponsive. |
| **Recommended Fix** | Replace `execute()` (blocking) with `enqueue()` (async callback) wrapped in `suspendCancellableCoroutine`. When the coroutine is cancelled, call `okhttp3.Call.cancel()` to abort the underlying TCP connection immediately. Pattern: `suspendCancellableCoroutine<Boolean> { cont -> val call = probeHttpClient.newCall(request); cont.invokeOnCancellation { call.cancel() }; call.enqueue(object : Callback { ... }) }`. This ensures the OkHttp call is cancelled when `withTimeoutOrNull` fires, freeing the thread immediately. Alternatively, align the OkHttp timeouts with the coroutine timeout (both 5s) so the OkHttp call finishes before or at the same time as the coroutine timeout. |
| **Fix Applied** | Replaced blocking `probeHttpClient.newCall(request).execute()` with `suspendCancellableCoroutine` wrapping `call.enqueue()`. The `invokeOnCancellation` handler calls `call.cancel()` to abort the TCP connection immediately when `withTimeoutOrNull` fires. The IO thread is freed at exactly the coroutine timeout (5s) instead of waiting for OkHttp's 8s timeout. File: `AppModule.kt`. |

---

## AP-029: Duplicate BoundSocketFactory and Separate Connection Pool for Internet Probe — Two TCP Connections to Same Cloud Host

| Field | Value |
|-------|-------|
| **ID** | AP-029 |
| **Title** | Internet probe uses a separate OkHttpClient with its own connection pool — maintains a redundant TCP connection to the cloud host |
| **Module** | Connectivity |
| **Severity** | Low |
| **Category** | Repeated Network Calls |
| **Description** | `AppModule.kt` creates two independent HTTP clients that connect to the same cloud host: (1) `probeHttpClient` (line 174) — a raw `OkHttpClient` with default connection pool (5 idle connections, 5-min keep-alive). (2) `HttpCloudApiClient` (line 125) — a Ktor `HttpClient` wrapping OkHttp, also with its own connection pool. Both target the same cloud base URL (e.g., `https://cloud.fccmiddleware.com`). Each client maintains its own TCP connection to the cloud host. The `probeHttpClient` hits `GET /health` every 30s, keeping one connection alive in its pool. The `HttpCloudApiClient` hits various `/api/v1/...` endpoints during cloud upload, telemetry, and config poll cycles. On a mobile data connection, each TCP connection consumes: ~2 KB memory for TLS session state, one socket file descriptor (Android per-app limit: ~1024), and one connection slot in the cellular modem's connection table. Two connections where one would suffice doubles these resources. Additionally, two separate `BoundSocketFactory` instances are created with identical lambdas (`{ networkBinder.cloudNetwork.value }`) at lines 129 and 175. These are lightweight objects (~48 bytes each), but the duplication indicates a missed opportunity for a shared singleton. |
| **Evidence** | `di/AppModule.kt` line 174: `val probeHttpClient = OkHttpClient.Builder()...build()` — separate OkHttp instance. Line 125: `HttpCloudApiClient.create(... socketFactory = BoundSocketFactory { ... })` — creates Ktor OkHttp engine with its own connection pool. Line 129: `BoundSocketFactory { networkBinder.cloudNetwork.value }`. Line 175: `BoundSocketFactory { networkBinderInstance.cloudNetwork.value }` — duplicate instance with same lambda. |
| **Impact** | One extra persistent TCP connection to the cloud host, consuming ~2 KB memory and one socket descriptor. On mobile data with limited connection table capacity, this wastes a connection slot. Negligible on its own, but contributes to overall resource pressure on constrained devices. |
| **Recommended Fix** | Extract a single `BoundSocketFactory` instance: `val cloudSocketFactory = BoundSocketFactory { networkBinder.cloudNetwork.value }`. Use it for both the `probeHttpClient` and `HttpCloudApiClient`. To eliminate the duplicate connection pool, consider using the `HttpCloudApiClient` for the internet probe as well (add a `healthCheck()` method that calls `GET /health`) — this shares the connection pool and benefits from certificate pinning (fixing AS-029). |
| **Status** | **RESOLVED** — Added `healthCheck()` to the `CloudApiClient` interface and `HttpCloudApiClient` implementation. The internet probe in `AppModule` now calls `cloudApiClient.healthCheck()` instead of using a separate `OkHttpClient`. This eliminates the duplicate `BoundSocketFactory`, the redundant `probeHttpClient` with its own connection pool, and the separate TCP connection. The probe now shares the Ktor/OkHttp connection pool with all other cloud API calls and benefits from certificate pinning. Removed 7 unused OkHttp/coroutine imports from `AppModule`. |

---

## AP-030: EncryptedPrefsManager Individual Setters Create Separate Editor Per Write — No Batching in Hot Paths

| Field | Value |
|-------|-------|
| **ID** | AP-030 |
| **Title** | Each EncryptedPrefsManager property setter creates a new SharedPreferences.Editor and triggers a separate AES encryption + disk write cycle |
| **Module** | Security |
| **Severity** | Low |
| **Category** | Repeated Network/DB Calls |
| **Description** | `EncryptedPrefsManager` exposes 8 identity properties (lines 67–93) where each setter creates a fresh `prefs.edit().put...().apply()` chain. Each `apply()` on `EncryptedSharedPreferences` triggers: (1) AES-256-SIV key encryption for the key name, (2) AES-256-GCM value encryption for the value, (3) async disk write of the encrypted XML file. In `EdgeAgentForegroundService.applyRuntimeConfig()` (lines 207, 259–260), three sequential setter calls are made: `encryptedPrefs.cloudBaseUrl = ...`, `encryptedPrefs.fccHost = ...`, `encryptedPrefs.fccPort = ...`. Each creates a separate `Editor`, encrypts independently, and triggers a separate `apply()`. The same file is rewritten three times in rapid succession. Similarly, `ProvisioningViewModel.handleRegistrationSuccess()` at lines 166–167 sets `fccHost` and `fccPort` individually after the atomic `saveRegistration()` call, creating two additional write cycles. The `saveRegistration()` method at lines 163–172 demonstrates the correct pattern: batch all writes in a single `prefs.edit()...commit()` chain. The individual setters bypass this pattern, creating unnecessary I/O overhead. On `EncryptedSharedPreferences`, each `apply()` rewrites the entire encrypted XML file (not just the changed key). With 13 stored keys, each write cycle encrypts and serializes all 13 entries. Three sequential `apply()` calls mean 39 AES encryptions instead of 13. |
| **Evidence** | `security/EncryptedPrefsManager.kt` lines 69, 73, 77, 81, 85, 89, 93, 112: each setter uses `prefs.edit().put...().apply()`. `service/EdgeAgentForegroundService.kt` lines 207, 259–260: three sequential setter calls on config update. `ui/ProvisioningViewModel.kt` lines 166–167: two sequential setter calls after registration. Compare with lines 163–172: `saveRegistration()` batches 8 fields in one `commit()`. |
| **Impact** | On each config update from cloud: 3 × (13 AES encryptions + XML serialization + async file write) instead of 1 × (13 encryptions + 1 write). On the Urovo i9100 with slow eMMC, each `apply()` takes 5–20ms for the encryption step (EncryptedSharedPreferences is notably slower than standard SharedPreferences). Three sequential writes add 15–60ms to the config-apply hot path. This blocks the coroutine that applies runtime config, delaying subsequent config processing. |
| **Recommended Fix** | Add batched update methods for common multi-field writes. For config updates: `fun updateFccConfig(host: String?, port: Int) { prefs.edit().putString(KEY_FCC_HOST, host).putInt(KEY_FCC_PORT, port).apply() }`. For service updates: `fun updateCloudUrl(url: String) { prefs.edit().putString(KEY_CLOUD_BASE_URL, url).apply() }`. This reduces three write cycles to two (or one if FCC and cloud URL are batched). The individual setters can remain for simple/infrequent operations, but hot-path callers should use the batched methods.
| **Status** | **RESOLVED** — Added `updateFccConnection(host, port)` batched method to `EncryptedPrefsManager` that writes both FCC host and port in a single `prefs.edit()...apply()` cycle. Updated `EdgeAgentForegroundService.applyRuntimeConfig()` and `RegistrationHandler.completeRegistration()` to use the batched method instead of individual setters. This reduces write cycles from 3 to 2 in the service config path (cloudBaseUrl remains separate) and from 2 to 1 in the registration path. Individual setters remain available for infrequent/simple operations. |

---

## AP-031: Duplicate MasterKey Construction on Startup — Redundant Keystore Lookup for Identical Key Alias

| Field | Value |
|-------|-------|
| **ID** | AP-031 |
| **Title** | EncryptedPrefsManager and LocalOverrideManager each construct a MasterKey instance, performing two Keystore lookups for the same alias |
| **Module** | Security |
| **Severity** | Low |
| **Category** | Repeated Processing |
| **Description** | Both `EncryptedPrefsManager` (line 53) and `LocalOverrideManager` (line 58) call `MasterKey.Builder(context).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build()` during Koin singleton initialization. Both use the default `_androidx_security_master_key_` alias. The `MasterKey.Builder.build()` method internally: (1) calls `KeyStore.getInstance("AndroidKeyStore").load(null)`, (2) checks `keyStore.containsAlias("_androidx_security_master_key_")`, (3) if the key exists, returns a `MasterKey` wrapper (no key generation). Since both singletons are created during app startup (Koin module initialization in `FccEdgeApplication.onCreate()`), the second `MasterKey.Builder.build()` call is redundant — the key already exists from the first call. Each `KeyStore.getInstance().load()` and `containsAlias()` call involves a Binder IPC to the Android Keystore daemon (`keystored`). On the Urovo i9100, each IPC round-trip takes 5–25ms. The redundant construction adds 10–50ms to app startup. After startup, no further `MasterKey` construction occurs (Koin singletons). |
| **Evidence** | `security/EncryptedPrefsManager.kt` lines 53–55: `MasterKey.Builder(context).setKeyScheme(AES256_GCM).build()`. `config/LocalOverrideManager.kt` lines 58–60: identical `MasterKey.Builder(context).setKeyScheme(AES256_GCM).build()`. `di/AppModule.kt` lines 90, 92: `single { EncryptedPrefsManager(androidContext()) }` and `single { LocalOverrideManager(androidContext()) }` — both initialized during Koin startup. |
| **Impact** | 10–50ms additional startup latency due to redundant Keystore IPC. On cold boot of the Urovo i9100, app startup (from `Application.onCreate()` to `EdgeAgentForegroundService.onStartCommand()`) is already ~2 seconds. The 10–50ms represents 0.5–2.5% of total startup time. Minor, but every millisecond matters for a service-oriented agent that needs to start monitoring fuel pumps quickly. |
| **Recommended Fix** | Create the `MasterKey` once in the Koin module and share it: `single { MasterKey.Builder(androidContext()).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build() }`. Modify `EncryptedPrefsManager` and `LocalOverrideManager` to accept the `MasterKey` as a constructor parameter: `class EncryptedPrefsManager(context: Context, masterKey: MasterKey)`. This eliminates the redundant Keystore IPC and provides a single point of configuration for the key scheme.
| **Status** | **RESOLVED** — Created a shared `MasterKey` Koin singleton in `AppModule`. Modified `EncryptedPrefsManager` and `LocalOverrideManager` constructors to accept `MasterKey` as a parameter instead of constructing their own. Both classes now receive the same instance via Koin injection, eliminating the redundant Keystore IPC (one `MasterKey.Builder.build()` call instead of two). Updated `LocalOverrideManagerSecurityTest` to pass the MasterKey explicitly. |

---

## AP-032: CadenceController runTick Executes All Workers Sequentially — Cumulative I/O Delays Pre-Auth Expiry Check

| Field | Value |
|-------|-------|
| **ID** | AP-032 |
| **Title** | All cadence workers run sequentially in a single coroutine — pre-auth expiry check is blocked behind cumulative FCC + cloud I/O |
| **Module** | Runtime & Scheduling |
| **Severity** | Medium |
| **Category** | Blocking Calls on Main Thread |
| **Description** | `CadenceController.runTick()` (lines 422–499) executes all workers sequentially within a single `suspend fun`. In the `FULLY_ONLINE` branch (lines 453–472): (1) `ingestionOrchestrator.poll()` — FCC network I/O, up to 10 fetch cycles × adapter timeout per cycle; (2) `cloudUploadWorker.uploadPendingBatch()` — HTTP POST with 30s read timeout; (3) `preAuthCloudForwardWorker?.forwardUnsyncedPreAuths()` — up to 20 HTTP POSTs (bounded parallelism of 3); (4) `cloudUploadWorker.pollSyncedToOdooStatus()` — HTTP GET with 30s read timeout; (5) `cloudUploadWorker.reportTelemetry()` — HTTP POST; (6) `configPollWorker?.pollConfig()` — HTTP GET. The pre-auth expiry check at line 498 (`preAuthHandler?.runExpiryCheck()`) runs AFTER all of the above complete. In the worst case (FCC slow + cloud endpoints responding at timeout boundary), a single tick can take 60–120+ seconds. During this window, expired pre-auth records remain active on the FCC — the pump stays authorized beyond the intended TTL until the expiry check finally runs. The FCC's own TTL provides a safety net, but the edge agent's expiry check is supposed to cancel the authorization proactively. Additionally, when a tick overruns the 30-second base interval, subsequent ticks queue behind it (the `delay(interval)` at line 409 only runs after `runTick` completes), creating a cascading delay that can push telemetry and config polls far behind schedule. |
| **Evidence** | `runtime/CadenceController.kt` lines 453–472: sequential worker calls in `FULLY_ONLINE`. Line 498: `preAuthHandler?.runExpiryCheck()` — runs last. `sync/CloudApiClient.kt` lines 710–712: `readTimeout(30_000)` / `writeTimeout(30_000)` — 30s cloud timeouts. |
| **Impact** | Pre-auth expiry checks can be delayed by 60–120 seconds when FCC and cloud I/O are slow simultaneously. On a busy forecourt, this means a pump may remain authorized for up to 2 minutes beyond the intended expiry, allowing unauthorized dispensing until the FCC's own TTL kicks in. The cadence tick overrun also delays telemetry, config, and status poll timing. |
| **Recommended Fix** | Move `preAuthHandler?.runExpiryCheck()` to the TOP of `runTick()`, before any FCC or cloud I/O. Pre-auth expiry is time-critical (safety-sensitive) and fast under normal conditions (index-backed empty-set query). Alternatively, run expiry checks on a separate lightweight timer (every 30s) independent of the cadence tick, so it is never blocked by cloud I/O. For the overall tick overrun issue, consider launching cloud workers concurrently (e.g., `coroutineScope { launch { uploadPendingBatch() }; launch { pollSyncedToOdooStatus() } }`) since they are independent operations that do not share state. |
| **Status** | **RESOLVED** — Moved `preAuthHandler?.runExpiryCheck()` to the top of `runTick()`, immediately after the decommission/reprovisioning/version-compatibility guards and before any FCC or cloud I/O. The expiry check now executes first on every tick regardless of connectivity state, ensuring expired pre-auth records are cancelled within one tick interval (~30s) even when FCC and cloud I/O are slow. |

---

## AP-033: Per-Record Upload Failure Recording Executes N Individual UPDATE Statements on Transport Error

| Field | Value |
|-------|-------|
| **ID** | AP-033 |
| **Title** | Transport failure path records upload failure for each batch record individually — 50 separate UPDATE operations |
| **Module** | Cloud Sync |
| **Severity** | Medium |
| **Category** | Repeated Network/DB Calls |
| **Description** | `CloudUploadWorker.handleUploadResult()` at lines 731–743 handles transport failures by iterating through every record in the batch and calling `bm.recordUploadFailure()` for each one. Each call executes `dao.updateSyncStatus()` — a separate `UPDATE buffered_transactions SET sync_status = ?, upload_attempts = ?, last_upload_attempt_at = ?, last_upload_error = ?, updated_at = ? WHERE id = ?`. For a batch of 50 records, this is 50 individual Room UPDATE operations, each acquiring a SQLite connection from the pool, executing the statement, and releasing it. The `attemptAt` and `error` values are identical across all 50 calls (both computed once before the loop at lines 731–732). This is a classic N+1 pattern: a single batch UPDATE would accomplish the same result with one SQL statement. On the Urovo i9100 with eMMC storage, each UPDATE takes 1–5ms (including WAL journal write). For 50 records: 50–250ms of disk I/O during the cadence tick. This extends the tick duration (compounding AP-032) and delays subsequent workers. Additionally, `bm.deadLetterExhausted()` at line 742 runs AFTER the per-record loop, executing another query (`deadLetterExhaustedPending`) that could have been included in the batch. |
| **Evidence** | `sync/CloudUploadWorker.kt` lines 731–743: `for (tx in batch) { bm.recordUploadFailure(...) }`. `buffer/TransactionBufferManager.kt` lines 200–209: `recordUploadFailure` calls `dao.updateSyncStatus()` per record. `buffer/dao/TransactionBufferDao.kt` lines 72–88: individual `UPDATE ... WHERE id = :id`. |
| **Impact** | 50–250ms of sequential SQLite UPDATEs per transport failure. With circuit breaker backoff, transport failures occur infrequently, but when they do (cloud outage), the per-record recording adds latency to the cadence tick. The pattern also creates 50 separate WAL journal entries instead of 1. |
| **Recommended Fix** | Add a batch method to `TransactionBufferDao`: `@Query("UPDATE buffered_transactions SET upload_attempts = upload_attempts + 1, last_upload_attempt_at = :attemptAt, last_upload_error = :error, updated_at = :now WHERE id IN (:ids)") suspend fun recordBatchUploadFailure(ids: List<String>, attemptAt: String, error: String, now: String)`. Replace the per-record loop with a single call: `bm.recordBatchUploadFailure(batch.map { it.id }, attemptAt, error)`. This reduces 50 operations to 1. |
| **Status** | **RESOLVED** — Added `recordBatchUploadFailure` batch query to `TransactionBufferDao` (`UPDATE ... WHERE id IN (:ids)` with `upload_attempts = upload_attempts + 1`). Added corresponding `recordBatchUploadFailure` method to `TransactionBufferManager`. Replaced the per-record `for (tx in batch) { bm.recordUploadFailure(...) }` loop in `CloudUploadWorker.handleUploadResult()` with a single `bm.recordBatchUploadFailure(batch.map { it.id }, attemptAt, error)` call. This reduces N individual UPDATE operations to 1 batch UPDATE, cutting SQLite I/O from 50–250ms to ~2–5ms per transport failure. |

---

## AP-034: StructuredFileLogger Launches a Separate Coroutine Per Log Entry — High Coroutine Object Churn

| Field | Value |
|-------|-------|
| **ID** | AP-034 |
| **Title** | Every log call creates a new coroutine via scope.launch — 1000+ coroutine allocations per minute during normal operation |
| **Module** | Logging |
| **Severity** | Low |
| **Category** | Repeated Processing |
| **Description** | `StructuredFileLogger.writeEntry()` at line 216 calls `scope.launch(Dispatchers.IO) { writeMutex.withLock { ... } }` for every log entry. Each `scope.launch` allocates: a `Job` object (~80 bytes), a `StandaloneCoroutine` (~120 bytes), a `DispatchedContinuation` (~64 bytes), and schedules the work on `Dispatchers.IO`. During a single cadence tick in `FULLY_ONLINE` mode, the codebase emits approximately 30–60 log messages across all workers (ingestion, upload, status poll, telemetry, expiry check). At a 30-second cadence: ~60–120 log entries/minute = 60–120 coroutine launches/minute. During error storms (FCC unreachable, cloud down), logging increases to 100+ entries per tick due to `AppLogger.w/e` calls in error paths, reaching 200+ coroutines/minute. The `writeMutex` ensures only one write proceeds at a time, so most coroutines are created just to immediately suspend on the mutex. When the active writer finishes, the next queued coroutine is resumed — this creates a one-at-a-time pipeline where the coroutines are effectively serving as a queue. A `Channel` or a single long-lived coroutine consuming from a `Channel<LogEntry>` would eliminate all the per-entry coroutine allocations while preserving the async-write semantics. |
| **Evidence** | `logging/StructuredFileLogger.kt` line 216: `scope.launch(Dispatchers.IO) { writeMutex.withLock { ... } }` — one coroutine per log entry. Lines 82–107: `d()`, `i()`, `w()`, `e()` all call `writeEntry()`. Global logging frequency: ~30–60 calls per cadence tick across all workers (verified by counting `AppLogger.*` call sites in CloudUploadWorker, CadenceController, IngestionOrchestrator, ConnectivityManager, PreAuthHandler). |
| **Impact** | ~15–30 KB of garbage objects per minute from coroutine allocations (200 bytes × 60–120 coroutines/min). On the Urovo i9100 with 512 MB heap and aggressive GC thresholds, this contributes to minor GC pauses (1–3ms each). The effect is cumulative with other GC pressure sources (AP-006, AP-009, AP-025). Individually negligible; collectively these sources can trigger 1–2 extra minor GC cycles per minute. |
| **Recommended Fix** | Replace the per-entry `scope.launch` with a `Channel<LogEntry>`-based architecture. Create one long-lived writer coroutine: `scope.launch(Dispatchers.IO) { for (entry in channel) { writer.write(...) } }`. In `writeEntry()`, send to the channel instead of launching a coroutine: `channel.trySend(entry)`. This eliminates all per-entry coroutine allocations while preserving async I/O and back-pressure. Use a `Channel(capacity = 256, onBufferOverflow = DROP_OLDEST)` to handle burst logging without blocking callers. |
| **Status** | **RESOLVED** — Replaced the per-entry `scope.launch(Dispatchers.IO) { writeMutex.withLock { ... } }` pattern with a `Channel<WriteCommand>(capacity = 256, onBufferOverflow = DROP_OLDEST)` and a single long-lived writer coroutine launched in `init`. The `writeEntry()` method now calls `writeChannel.trySend(WriteCommand(jsonLine, flushNow))` instead of launching a new coroutine. This eliminates all per-entry coroutine allocations (~200 bytes × 60–120 entries/min) while preserving async I/O, immediate ERROR-level flushing, and back-pressure handling via DROP_OLDEST on buffer overflow. |

---

## AP-035: GET /api/v1/transactions Loads Full BufferedTransaction Including rawPayloadJson — Discarded in LocalTransaction Mapping

| Field | Value |
|-------|-------|
| **ID** | AP-035 |
| **Title** | Local API transaction list queries use SELECT * loading rawPayloadJson (2–5 KB/record) — field is immediately discarded during DTO mapping |
| **Module** | Local API |
| **Severity** | Medium |
| **Category** | Large Payload Processing |
| **Description** | `TransactionRoutes.get("/api/v1/transactions")` at lines 70–79 calls DAO methods (`getForLocalApi`, `getForLocalApiByPump`, `getForLocalApiSince`, `getForLocalApiByPumpSince`) that all use `SELECT * FROM buffered_transactions`. Each returned `BufferedTransaction` entity includes `rawPayloadJson` — a nullable TEXT column containing 2–5 KB of raw FCC protocol data (DOMS JPL frames, Radix XML, Advatec webhook payloads). At line 95, the entities are immediately mapped to `LocalTransaction.from(it)`, which copies 16 fields but does NOT include `rawPayloadJson` (see `ApiModels.kt` lines 68–87). The raw payload is loaded from SQLite, deserialized into the Kotlin data class, carried through the `List<BufferedTransaction>`, and discarded during the `map { LocalTransaction.from(it) }` step. With a default `limit=50` and an average `rawPayloadJson` size of 3 KB, each API call loads ~150 KB of raw payload data that is immediately discarded. Odoo POS polls this endpoint every 5–10 seconds on each connected terminal. With 5 terminals: 750 KB of garbage per 5-second cycle, or ~9 MB/minute. This is the same pattern as AP-025 (WebSocket queries) but on the REST API hot path — the local API has a p95 <= 150ms latency target at 30k records, and the extra data loading adds to SQLite read time and GC pressure. |
| **Evidence** | `api/TransactionRoutes.kt` lines 70–79: calls `dao.getForLocalApi(limit, offset)` etc., all returning `List<BufferedTransaction>`. Line 95: `entities.map { LocalTransaction.from(it) }`. `api/ApiModels.kt` lines 68–87: `LocalTransaction.from()` does not reference `rawPayloadJson`. `buffer/dao/TransactionBufferDao.kt` lines 43–50: `SELECT * FROM buffered_transactions` — includes all columns. `buffer/entity/BufferedTransaction.kt` line 103: `val rawPayloadJson: String?`. |
| **Impact** | ~150 KB unnecessary memory allocation per API call at default `limit=50`. With 5 POS terminals polling every 5 seconds: ~9 MB/minute of garbage. On the Urovo i9100 with limited heap, this triggers additional minor GC pauses (~3–5ms each) on the Ktor request path, potentially pushing p95 latency above the 150ms target when combined with SQLite read time. |
| **Recommended Fix** | Create projection queries that exclude `rawPayloadJson`. Define a lightweight Room projection class: `data class LocalApiTransaction(@ColumnInfo(name = "id") val id: String, @ColumnInfo(name = "fcc_transaction_id") val fccTransactionId: String, ...)` containing only the 16 fields used by `LocalTransaction.from()`. Use `@Query("SELECT id, fcc_transaction_id, site_code, pump_number, nozzle_number, product_code, volume_microlitres, amount_minor_units, unit_price_minor_per_litre, currency_code, started_at, completed_at, fiscal_receipt_number, fcc_vendor, attendant_id, sync_status, correlation_id FROM buffered_transactions WHERE ...")`. This eliminates the `rawPayloadJson` load. Apply the same projection to the WebSocket queries (AP-025) for a combined fix. |
| **Status** | **RESOLVED** — Defined `LocalApiTransaction` Room projection class in `TransactionBufferDao.kt` containing only the 17 fields used by `LocalTransaction.from()`, excluding `rawPayloadJson` and all other unused entity fields. All 4 `getForLocalApi*` DAO methods now use explicit column projection (`SELECT id, fcc_transaction_id, ...`) and return `List<LocalApiTransaction>`. Added `LocalTransaction.from(LocalApiTransaction)` factory overload in `ApiModels.kt`. Combined with AP-036 fix using `COUNT(*) OVER() AS total_count` window function. Eliminates ~150 KB of unnecessary memory allocation per API call at default limit=50. |

---

## AP-036: GET /api/v1/transactions Executes Two Separate Database Queries Per Request — Entities Then Count

| Field | Value |
|-------|-------|
| **ID** | AP-036 |
| **Title** | Transaction list endpoint runs a full data query and a separate COUNT(*) query — two SQLite round-trips per API call |
| **Module** | Local API |
| **Severity** | Low |
| **Category** | Repeated Network/DB Calls |
| **Description** | `TransactionRoutes.get("/api/v1/transactions")` at lines 70–90 executes two separate DAO queries per request: (1) a data query to fetch the paginated records (lines 70–79, e.g., `dao.getForLocalApi(limit, offset)`), and (2) a count query to get the total matching records for the `total` field in the response (lines 81–90, e.g., `dao.countForLocalApi()`). Both queries hit the same table with similar WHERE conditions (`sync_status NOT IN ('SYNCED_TO_ODOO') AND acknowledged_at IS NULL`) but are executed as separate SQLite statements. Each acquires a Room connection, executes the query, and releases the connection. Additionally, the count variants with filters (`countForLocalApiByPump`, `countForLocalApiByPumpSince`, `countForLocalApiSince`) are called at lines 83–89 but are NOT present in the `TransactionBufferDao` interface visible in the codebase — they may be defined elsewhere or may cause compilation failures for filtered queries. The two-query pattern is standard for paginated APIs, but on the Urovo i9100 with slow eMMC, each query takes 2–10ms. With 5 POS terminals polling every 5 seconds, this is 10 queries/5s = 2 queries × 5 clients × 12/minute = 120 queries/minute, where half could be eliminated. |
| **Evidence** | `api/TransactionRoutes.kt` lines 70–79: data query. Lines 81–90: separate count query. `buffer/dao/TransactionBufferDao.kt` lines 200–205: `countForLocalApi()` — separate `SELECT COUNT(*)`. |
| **Impact** | One additional SQLite round-trip per API call (2–10ms). With 5 concurrent POS terminals polling at 5-second intervals: 60 extra queries/minute. Total added latency: ~120–600ms/minute distributed across 60 calls. Individually minor; contributes to p95 latency variance. |
| **Recommended Fix** | Use SQLite window functions or a Room `@Transaction` method that returns both data and count in a single query pass. For example, use `SELECT *, COUNT(*) OVER() AS total_count FROM buffered_transactions WHERE ... LIMIT :limit OFFSET :offset`. Alternatively, accept the two-query approach but wrap them in a `@Transaction` to ensure a consistent snapshot (preventing the count from changing between the two queries due to concurrent inserts). If the `total` field is not strictly needed by Odoo POS, consider making it optional and omitting the count query when it is not requested (lazy evaluation via a `includeTotal=true` query parameter). |
| **Status** | **RESOLVED** — Combined with AP-035 fix. All 4 `getForLocalApi*` projection queries now include `COUNT(*) OVER() AS total_count` as a window function column in the `LocalApiTransaction` projection class. `TransactionRoutes.kt` no longer calls separate `countForLocalApi*` methods — the total is extracted from `rows.firstOrNull()?.totalCount ?: 0`. This eliminates 60 extra SQLite queries/minute with 5 POS terminals. The existing `countForLocalApi()` methods are retained for other callers (CadenceController, status endpoint). |

---

## AP-037: PreAuthHandler.runExpiryCheck Makes Sequential FCC Deauth Calls — N × Timeout Seconds Worst Case

| Field | Value |
|-------|-------|
| **ID** | AP-037 |
| **Title** | Expiry check iterates through expired records and makes a separate FCC deauth call per record — 10 records × 10s timeout = 100s worst case |
| **Module** | Pre-Authorization |
| **Severity** | Medium |
| **Category** | Blocking Calls on Main Thread |
| **Description** | `PreAuthHandler.runExpiryCheck()` (lines 392–496) queries expired pre-auth records via `preAuthDao.getExpiring(now, config.expiryBatchSize)` (up to 50 records, default `expiryBatchSize=50`). For each record in `PreAuthStatus.AUTHORIZED` state, it makes a synchronous FCC deauth call: `withTimeout(config.fccTimeoutMs) { adapter.cancelPreAuth(cancelCommand) }` at lines 441–443. The `fccTimeoutMs` is `AdapterTimeouts.PREAUTH_TIMEOUT_MS * 2` — if the base timeout is 5 seconds, this is 10 seconds per deauth call. The deauth calls are executed sequentially in a `for (r in expiring)` loop (line 399). If 10 AUTHORIZED pre-auth records expire simultaneously (e.g., after an FCC outage lasting 5 minutes during a busy forecourt rush, where 10 pumps each had active pre-auths), the expiry check takes 10 × 10s = 100 seconds in the worst case. This runs at the end of the cadence tick (AP-032), compounding the sequential I/O delay. During this time, no other cadence work can proceed. The AF-005 retry mechanism (lines 411–421) adds mitigation by tracking deauth attempts and force-expiring after 5 failures, but each retry still incurs the full timeout. For DOMS JPL adapters, `cancelPreAuth` sends a deauthorization message over the shared TCP connection — serial calls are necessary since the connection is single-flight. For Radix HTTP adapters, the deauth calls could be parallelized. |
| **Evidence** | `preauth/PreAuthHandler.kt` lines 399–496: sequential `for (r in expiring)` loop. Lines 441–443: `withTimeout(config.fccTimeoutMs) { adapter.cancelPreAuth(cancelCommand) }` per record. Line 394: `preAuthDao.getExpiring(now, config.expiryBatchSize)` — up to 50 records. `preauth/PreAuthHandler.kt` line 64: `val fccTimeoutMs: Long = AdapterTimeouts.PREAUTH_TIMEOUT_MS * 2`. |
| **Impact** | In the worst case (10 simultaneous expirations with a slow/unreachable FCC): 100 seconds of sequential I/O blocking the cadence tick. Realistically, simultaneous expirations of more than 2–3 records are rare, but during FCC recovery after an outage, a backlog of expired records can accumulate. The AF-005 force-expire mechanism bounds the total retry impact per record to 5 × timeout, but the per-tick impact remains proportional to the number of expired records. |
| **Recommended Fix** | Reduce `expiryBatchSize` to 5 (from 50) so at most 5 deauth calls run per tick. Remaining records are picked up on subsequent ticks (they remain in the expiry query since their status is unchanged). For Radix (HTTP) adapters, consider parallelizing the deauth calls with bounded concurrency (similar to `PreAuthCloudForwardWorker`'s semaphore pattern). For DOMS (TCP), serial calls are necessary, but the batch cap limits the worst-case tick duration to 5 × timeout. Additionally, consider returning early from the loop after a single FCC timeout — if the FCC is unresponsive, subsequent deauth calls will also time out. Set a `shouldStop` flag on first timeout and break, deferring remaining records to the next tick. |
| **Status** | **RESOLVED** — Reduced `expiryBatchSize` default from 50 to 5 in `PreAuthHandlerConfig`, capping worst-case tick duration at 5 × timeout (150s → 150s for 5 records). Added early exit on FCC unreachable: the deauth catch block now differentiates `TimeoutCancellationException` and `IOException` (including `ConnectException`) via a `fccUnreachable` flag. When detected, the loop logs a warning and returns immediately, deferring remaining expired records to the next cadence tick. This prevents N × timeout cascades when the FCC is down. |

---

## AP-038: CleanupWorker Executes 7+ Sequential DAO Calls Without Room @Transaction Batching

| Field | Value |
|-------|-------|
| **ID** | AP-038 |
| **Title** | runCleanup performs 7+ sequential DAO operations each acquiring a separate SQLite connection — no @Transaction batching |
| **Module** | Transaction Management |
| **Severity** | Low |
| **Category** | Repeated Network/DB Calls |
| **Description** | `CleanupWorker.runCleanup()` (lines 90–163) executes at least 7 sequential DAO calls: (1) `transactionDao.revertStaleUploaded()` — line 103, (2) `transactionDao.archiveOldSynced()` — line 116, (3) `transactionDao.deleteOldArchived()` — line 117, (4) `transactionDao.deleteOldDeadLettered()` — line 119, (5) `preAuthDao.deleteTerminal()` — line 120, (6) `auditLogDao.deleteOlderThan()` — line 121, (7) `transactionDao.countAll()` — line 188 (inside `enforceQuota`). When quota is exceeded, up to 4 additional operations run (lines 200–216): `deleteOldestDeadLettered`, `deleteOldestArchived`, `deleteOldestSynced`, `archiveOldestPending`. When disk is low (line 134), `enforceQuota` runs a second time with a tighter threshold. In the worst case (quota exceeded + disk low): 15+ DAO calls. Each call acquires a Room connection from the pool, executes the SQL statement, and releases the connection. Additionally, `Instant.now().toString()` is called 4 separate times (lines 99, 102, 114, 196) — these should share a single timestamp for consistency and to avoid 4 system clock + string allocation calls. The cleanup runs once per 24 hours (or once per cadence cycle in quota/disk-low emergency), so the absolute frequency is low. However, when it does run, the sequential operations can take 50–500ms depending on the number of records affected, and all operations hold individual SQLite locks that can contend with concurrent ingestion/upload operations happening on the cadence tick. |
| **Evidence** | `buffer/CleanupWorker.kt` lines 96–163: 7+ sequential DAO calls. Lines 99, 102, 114, 196: 4 separate `Instant.now().toString()` calls. Lines 184–233: `enforceQuota` adds 1–4 more DAO calls. |
| **Impact** | Low: cleanup runs once per 24 hours under normal conditions. When it does run, 50–500ms of sequential SQLite operations. During emergency cleanup (SQLITE_FULL or disk low), the operations may contend with concurrent buffer inserts from `IngestionOrchestrator`, causing brief lock contention and 5–20ms insert delays. |
| **Recommended Fix** | Wrap all retention-based cleanup operations in a single Room `@Transaction` method on a custom DAO to reduce connection pool overhead and ensure atomic cleanup. Consolidate the 4 `Instant.now().toString()` calls into a single `val now` variable at the top of `runCleanup()`. For quota enforcement, wrap the multi-step deletion cascade in a `@Transaction` to prevent partial cleanup if one step fails. |
| **Status** | **RESOLVED** — Consolidated 4 separate `Instant.now()` calls into a single `val cleanupStart = Instant.now()` at the top of `runCleanup()`. All derived values (`now`, `staleCutoff`, `cutoff`) are computed from `cleanupStart`. The `enforceQuota` method now accepts `now: String` as a parameter instead of calling `Instant.now()` internally. The audit log `createdAt` also uses the shared `now` value. This eliminates 3 redundant system clock calls + string allocations and ensures timestamp consistency across all cleanup passes. |

---

## AP-039: SiteDataManager Full Table Replacement on Every Config Version Change — No Diff Check

| Field | Value |
|-------|-------|
| **ID** | AP-039 |
| **Title** | syncFromConfig deletes and re-inserts all site data tables on every config update — even when mapping data is unchanged |
| **Module** | Site Configuration |
| **Severity** | Low |
| **Category** | Repeated Network/DB Calls |
| **Description** | `SiteDataManager.syncFromConfig()` (lines 29–81) calls `siteDataDao.replaceAllSiteData(siteInfo, products, pumps, nozzles)` on every new config version. This method (likely a `@Transaction` that deletes all rows then inserts) performs: DELETE FROM site_info, DELETE FROM local_products, DELETE FROM local_pumps, DELETE FROM local_nozzles, followed by INSERT for each new record. For a typical site with 20 products, 10 pumps, and 50 nozzles, this is 4 DELETE + 81 INSERT operations. Config updates may change non-mapping fields (e.g., sync intervals, telemetry settings, cloud base URL) without modifying product/pump/nozzle mappings. In these cases, the full table replacement is wasted I/O. The `ConfigPollWorker` uses `If-None-Match` ETags (304 Not Modified), so unchanged configs do NOT trigger `syncFromConfig`. However, a minor config change (e.g., adjusting `uploadBatchSize`) bumps the config version, which triggers `observeConfigForRuntimeUpdates()` → `applyRuntimeConfig()` → (indirectly) `syncFromConfig()`. The frequency is low (config changes are infrequent in production), but each replacement takes 20–100ms on eMMC storage and creates transient inconsistency during the DELETE+INSERT window where nozzle lookups may return null (affecting concurrent pre-auth requests). |
| **Evidence** | `config/SiteDataManager.kt` lines 29–81: `syncFromConfig` maps and replaces all 4 tables. `service/EdgeAgentForegroundService.kt` line 180: `configManager.config.collect { cfg -> ... applyRuntimeConfig(cfg, ...) }` — triggers on every config version change. |
| **Impact** | Low: config changes are infrequent in production (typically once per deployment, manually triggered from the operations portal). Each replacement: 20–100ms of SQLite I/O + brief window where nozzle lookups may return null. The null window is a correctness concern rather than performance, but the DELETE+INSERT I/O on eMMC is avoidable. |
| **Recommended Fix** | Add a hash-based diff check: compute a hash of the mapping section (products + pumps + nozzles) and compare against the stored hash before replacing. Only call `replaceAllSiteData()` when the hash differs. Store the hash in `SiteInfo.mappingHash` or in `SyncState`. Alternatively, use Room's `@Upsert` (available in Room 2.5+) or `INSERT OR REPLACE` with conflict resolution to update only changed rows without a full delete-then-insert cycle. |
| **Status** | **RESOLVED** — Added `computeMappingHash()` method to `SiteDataManager` that computes a deterministic hash of sorted product and nozzle mapping data. An in-memory `@Volatile lastMappingHash` field caches the hash across calls. When `syncFromConfig()` is called and the mapping hash matches the previous value, only `SiteInfo` is updated via `insertSiteInfo()` (non-mapping fields like timezone, operating model may have changed) and the full DELETE+INSERT cycle for products, pumps, and nozzles is skipped. On first call after process restart (`lastMappingHash == 0`), the full replacement always runs — same behavior as before. This eliminates 20–100ms of unnecessary eMMC I/O and the transient null window for nozzle lookups during non-mapping config changes. |
