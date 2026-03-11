# Edge Agent — Phased Development Plan

**Agent System Prompt:** Always prepend `docs/plans/agent-prompt-edge-agent.md` when assigning any task below.

**Sprint Cadence:** 2-week sprints

---

## Phase 0 — Foundations (Sprints 1–2)

### EA-0.1: Android Project Scaffold

**Sprint:** 1
**Prereqs:** None
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-1-project-scaffolding.md` — §5.2 (Edge Agent section, the definitive scaffold spec)
- `WIP-HLD-Edge-Agent.md` — §5.3 (Package Structure tree)
- `docs/specs/foundation/coding-conventions.md` — Kotlin conventions

**Task:**
Create the complete Android project for the Edge Agent from scratch.

**Detailed instructions:**
1. Create a new Android project: `fcc-edge-agent`, package `com.fccmiddleware.edge`
2. Configure `settings.gradle.kts`: `rootProject.name = "fcc-edge-agent"`, include `:app`
3. Configure `app/build.gradle.kts`:
   - `compileSdk = 34`, `minSdk = 31` (Android 12), `targetSdk = 34`
   - Kotlin DSL, KSP plugin for Room annotation processing
   - Enable `kotlinx.serialization` plugin
4. Add all dependencies per scaffolding spec §5.2:
   - Ktor server (CIO engine), Ktor client (OkHttp), Ktor serialization (kotlinx-json)
   - Room (runtime, ktx, compiler via KSP)
   - Koin (android)
   - kotlinx-coroutines-android
   - kotlinx-serialization-json
   - androidx.security:security-crypto (EncryptedSharedPreferences)
5. Create the package tree per Edge Agent HLD §5.3:
   - `api/` — Ktor route files (stubs)
   - `adapter/common/` — IFccAdapter interface stub
   - `adapter/doms/` — DOMS adapter stub
   - `buffer/` — Room DB stub
   - `buffer/entity/` — Room entity stubs
   - `buffer/dao/` — DAO interface stubs
   - `sync/` — Cloud sync worker stubs
   - `connectivity/` — ConnectivityManager stub
   - `preauth/` — PreAuth handler stub
   - `ingestion/` — IngestionOrchestrator stub
   - `config/` — ConfigManager stub
   - `security/` — Keystore, encrypted prefs stubs
   - `service/` — Foreground service, boot receiver
   - `ui/` — Diagnostics screen stub
   - `di/` — Koin modules

**Acceptance criteria:**
- `./gradlew assembleDebug` succeeds
- All packages exist with placeholder Kotlin files
- Dependencies resolve correctly
- Kotlin version and Android Gradle plugin are compatible

---

### EA-0.2: Foreground Service & Boot Receiver

**Sprint:** 1
**Prereqs:** EA-0.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-1-project-scaffolding.md` — §5.2 foreground service section
- `WIP-HLD-Edge-Agent.md` — §4.1 (service lifecycle), §8 (reliability)

**Task:**
Implement the foreground service that keeps the Edge Agent running persistently.

**Detailed instructions:**
1. Create `EdgeAgentForegroundService` extending `Service`:
   - Return `START_STICKY` from `onStartCommand`
   - Create persistent notification channel: `fcc_edge_agent_channel`
   - Show persistent notification: "FCC Edge Agent Running" with status info
   - `foregroundServiceType = "dataSync"` in manifest
2. Create `BootReceiver` extending `BroadcastReceiver`:
   - Register for `RECEIVE_BOOT_COMPLETED` intent
   - Start the foreground service on boot
3. Update `AndroidManifest.xml` with:
   - Service declaration: `<service android:foregroundServiceType="dataSync">`
   - Receiver declaration with boot intent filter
   - Permissions: `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_DATA_SYNC`, `RECEIVE_BOOT_COMPLETED`, `INTERNET`, `ACCESS_NETWORK_STATE`, `ACCESS_WIFI_STATE`
4. The service is the central lifecycle owner — all workers (FCC poller, upload, etc.) will be started from here in later tasks

**Acceptance criteria:**
- Service starts and shows persistent notification on emulator (API 31+)
- Service survives app backgrounding
- Boot receiver starts service after device reboot
- Service returns START_STICKY for restart after kill

---

### EA-0.3: Room Database Setup

**Sprint:** 1
**Prereqs:** EA-0.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — §5.5 (Edge Agent Room/SQLite Schema — ALL entities, DAOs, indexes, WAL, retention, migration strategy)
- `db/ddl/002-edge-room-schema.sql` — reference DDL

**Task:**
Implement the complete Room database with all entities, DAOs, and configuration.

**Detailed instructions:**
1. Create `BufferDatabase` extending `RoomDatabase` in `buffer/`:
   - Annotate with `@Database(entities = [...], version = 1, exportSchema = true)`
   - Include all 5 entities
   - Enable schema export for migration testing
2. Create Room entities per §5.5.1 of DB schema spec:
   - `BufferedTransaction` — all columns from spec (id, fccTransactionId, siteCode, pumpNumber, nozzleNumber, productCode, volumeMicrolitres, amountMinorUnits, unitPriceMinorPerLitre, currencyCode, startedAt, completedAt, fiscalReceiptNumber, fccVendor, attendantId, status, syncStatus, ingestionSource, rawPayloadJson, correlationId, uploadAttempts, lastUploadAttemptAt, lastUploadError, schemaVersion, createdAt, updatedAt)
   - `PreAuthRecord` (edge) — all columns from spec
   - `SyncState` — single-row table (id=1)
   - `AgentConfig` — single-row table (id=1)
   - `AuditLog` — local audit trail
3. All timestamps as `TEXT` (ISO 8601 UTC). Booleans as `INTEGER` (0/1). UUIDs as `TEXT`.
4. Create Room type converters for any custom types
5. Create indexes per §5.5.2:
   - `ix_bt_dedup`: `(fcc_transaction_id, site_code)` UNIQUE
   - `ix_bt_sync_status`: `(sync_status, created_at)`
   - `ix_bt_local_api`: `(sync_status, pump_number, completed_at DESC)`
   - `ix_bt_cleanup`: `(sync_status, updated_at)`
   - `ix_par_idemp`: `(odoo_order_id, site_code)` UNIQUE
   - `ix_par_unsent`: `(is_cloud_synced, created_at)`
   - `ix_par_expiry`: `(status, expires_at)`
   - `ix_al_time`: `(created_at)`
6. Create DAO interfaces per §5.5.3:
   - `TransactionBufferDao` — insert, getPendingForUpload, getForLocalApi, getById, updateSyncStatus, markSyncedToOdoo, deleteOldSynced, countByStatus
   - `PreAuthDao` — insert, getByOdooOrderId, getUnsynced, updateStatus, markCloudSynced, getExpiring
   - `SyncStateDao` — get, upsert
   - `AgentConfigDao` — get, upsert
   - `AuditLogDao` — insert, getRecent
7. Enable WAL mode via database builder: `.setJournalMode(JournalMode.WRITE_AHEAD_LOGGING)`
8. Set `OnConflictStrategy.IGNORE` on dedup-key inserts per spec

**Acceptance criteria:**
- Room schema export generates valid JSON in `app/schemas/`
- All DAOs compile and have correct `@Query` annotations
- WAL mode enabled
- Unique indexes prevent duplicate inserts (Room in-memory DB test)
- `getPendingForUpload` returns records in `createdAt ASC` order
- `getForLocalApi` excludes SYNCED_TO_ODOO records

---

### EA-0.4: Domain Models & IFccAdapter Interface

**Sprint:** 1
**Prereqs:** EA-0.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/canonical/canonical-transaction.schema.json` — transaction model
- `schemas/canonical/pre-auth-record.schema.json` — pre-auth model
- `schemas/canonical/pump-status.schema.json` — pump state model
- `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md` — §5.1 Edge Kotlin interface (the definitive adapter contract)

**Task:**
Create Kotlin domain models and the FCC adapter interface.

**Detailed instructions:**
1. Create `CanonicalTransaction` data class in `adapter/common/` matching the JSON schema — use `@Serializable` annotation
2. Create `PreAuthRecord` data class (domain, not Room entity)
3. Create `PumpStatus` data class
4. Create all shared enums matching the Cloud Backend:
   - `TransactionStatus`, `PreAuthStatus`, `SyncStatus` (Edge-only: PENDING, UPLOADED, SYNCED_TO_ODOO, ARCHIVED)
   - `IngestionMode`, `FccVendor`, `ConnectivityState`
5. Create `IFccAdapter` interface per §5.1 of adapter contracts:
   - `suspend fun normalize(rawPayload: RawPayloadEnvelope): CanonicalTransaction`
   - `suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult`
   - `suspend fun getPumpStatus(): List<PumpStatus>`
   - `suspend fun heartbeat(): Boolean`
   - `suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch`
6. Create supporting types: `RawPayloadEnvelope`, `FetchCursor`, `TransactionBatch`, `PreAuthCommand`, `PreAuthResult`
7. Create `FccAdapterFactory` interface: `fun resolve(vendor: FccVendor, config: AgentFccConfig): IFccAdapter`

**Acceptance criteria:**
- All models match their JSON schema counterparts field-for-field
- `IFccAdapter` interface matches §5.1 of adapter contracts spec exactly
- All functions are `suspend` (coroutine-compatible)
- `SyncStatus` is separate from `TransactionStatus` (per state machine spec §5.3)
- Models use `Long` for money, `String` for timestamps/UUIDs

---

### EA-0.5: Ktor Local API Scaffold

**Sprint:** 2
**Prereqs:** EA-0.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-1-project-scaffolding.md` — §5.2 Ktor section
- `schemas/openapi/edge-agent-local-api.yaml` — all 7 local API endpoints

**Task:**
Scaffold the Ktor embedded HTTP server with stub routes.

**Detailed instructions:**
1. Create `LocalApiServer` class in `api/`:
   - Configure Ktor CIO engine on port 8585 (localhost)
   - Install `ContentNegotiation` with `kotlinx.serialization` JSON
   - Install `StatusPages` for error handling
2. Create 4 route files with placeholder 501 responses:
   - `TransactionRoutes.kt`:
     - `GET /api/transactions` — list buffered transactions
     - `GET /api/transactions/{id}` — get by ID
     - `POST /api/transactions/acknowledge` — Odoo POS acknowledges
   - `PreAuthRoutes.kt`:
     - `POST /api/preauth` — submit pre-auth
     - `POST /api/preauth/cancel` — cancel pre-auth
   - `PumpStatusRoutes.kt`:
     - `GET /api/pump-status` — live pump statuses
   - `StatusRoutes.kt`:
     - `GET /api/status` — agent status and connectivity
3. Start the server from the foreground service

**Acceptance criteria:**
- Ktor server starts on port 8585
- All 7 endpoints return 501 (Not Implemented) with structured JSON
- `GET /api/status` returns 200 with placeholder status (this one should work)
- ContentNegotiation serializes/deserializes JSON correctly

---

### EA-0.6: Koin DI Setup

**Sprint:** 2
**Prereqs:** EA-0.1, EA-0.3, EA-0.5
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-1-project-scaffolding.md` — §5.2 Koin section

**Task:**
Configure Koin dependency injection.

**Detailed instructions:**
1. Create `FccEdgeApplication` extending `Application`:
   - Call `startKoin { androidContext(this@FccEdgeApplication); modules(appModule) }`
2. Create `di/AppModule.kt` with Koin declarations:
   - `single { BufferDatabase.create(get()) }` — Room database
   - `single { get<BufferDatabase>().transactionBufferDao() }` — DAOs
   - `single { get<BufferDatabase>().preAuthDao() }`
   - `single { get<BufferDatabase>().syncStateDao() }`
   - `single { LocalApiServer(get(), get()) }` — Ktor server
   - Stubs for future: ConnectivityManager, ConfigManager, etc.
3. Register `FccEdgeApplication` in `AndroidManifest.xml`

**Acceptance criteria:**
- Koin modules resolve without runtime errors on startup
- Database and DAOs are injectable
- Ktor server is injectable and starts correctly

---

### EA-0.7: CI Pipeline Setup

**Sprint:** 2
**Prereqs:** EA-0.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-2-repo-branching-and-cicd.md` — CI/CD spec for Edge Agent

**Task:**
Create the CI pipeline for the Edge Agent.

**Detailed instructions:**
1. Create `.github/workflows/ci.yml` with:
   - Trigger: push to `main`, PRs targeting `main`
   - Steps: checkout → setup JDK 17 → Gradle cache → `assembleDebug` → unit tests → Room schema export verification
2. Configure Gradle wrapper and ensure reproducible builds
3. Add lint configuration (ktlint or detekt)

**Acceptance criteria:**
- CI passes on clean checkout
- APK builds successfully in CI
- Unit tests run
- Room schema export validated

---

## Phase 2 — Edge Agent Core (Sprints 4–7)

### EA-2.1: DOMS FCC Adapter (LAN)

**Sprint:** 4
**Prereqs:** EA-0.4
**Estimated effort:** 3–4 days

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md` — §5.5 DOMS MVP Adapter Contract (protocol, endpoints, auth, payload format)
- `schemas/canonical/canonical-transaction.schema.json` — normalization target
- `schemas/canonical/pump-status.schema.json` — pump status model
- `schemas/canonical/pre-auth-record.schema.json` — pre-auth command/result model

**Task:**
Implement the DOMS FCC adapter for LAN communication.

**Detailed instructions:**
1. Create `DomsFccAdapter` implementing `IFccAdapter` in `adapter/doms/`:
2. **`fetchTransactions(cursor)`**:
   - HTTP GET to `http://{hostAddress}:{port}/api/v1/transactions?since={ISO8601}&cursor={token}&limit={n}`
   - Auth: `X-API-Key` header from config
   - Parse JSON response containing `transactions[]`, `nextCursor`, `hasMore`
   - Normalize each transaction to `CanonicalTransaction`
   - Handle HTTP errors: 401/403 → non-recoverable auth error; 408/429/5xx → recoverable
3. **`normalize(rawPayload)`**:
   - Parse DOMS JSON transaction object
   - Map all fields to `CanonicalTransaction` per the schema
   - Volume in microlitres, amount in minor units — convert if DOMS uses different units
   - Preserve `fccTransactionId` as opaque string
4. **`sendPreAuth(command)`**:
   - HTTP POST to `http://{host}:{port}/api/v1/preauth`
   - Body: derived from `PreAuthCommand` (pumpNumber, amountMinorUnits, currencyCode, odooOrderId, customerTaxId)
   - Parse response into `PreAuthResult` (status, authorizationCode, expiresAtUtc, message)
5. **`getPumpStatus()`**:
   - HTTP GET to `http://{host}:{port}/api/v1/pump-status`
   - Parse array of pump-nozzle status objects
   - Map to `List<PumpStatus>`
6. **`heartbeat()`**:
   - HTTP GET to `http://{host}:{port}/api/v1/heartbeat`
   - Return `true` if 200 OK with `{ "status": "UP" }`
   - 5-second timeout
7. Use Ktor HTTP client with OkHttp engine for all requests
8. Create sample DOMS JSON fixtures for tests

**Acceptance criteria:**
- All 5 `IFccAdapter` methods implemented
- Normalization maps all fields correctly from DOMS format
- Pre-auth sends correct payload and parses response
- Heartbeat returns true/false correctly
- HTTP errors classified as recoverable vs non-recoverable per spec
- Unit tests with mock HTTP responses for each method
- Timeout handling works (5s for heartbeat, configurable for others)

---

### EA-2.2: SQLite Buffer — Write, Query, Cleanup

**Sprint:** 4
**Prereqs:** EA-0.3
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — §5.5.3 DAO definitions, §5.5.5 Retention and cleanup
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.3 Edge Sync Record State Machine

**Task:**
Implement the buffer management logic on top of Room DAOs.

**Detailed instructions:**
1. Create `TransactionBufferManager` in `buffer/`:
   - `bufferTransaction(tx: CanonicalTransaction)` — insert with local dedup check (IGNORE on unique constraint), set `syncStatus = PENDING`
   - `getPendingBatch(batchSize: Int)` — get oldest PENDING records for upload
   - `markUploaded(ids: List<String>)` — set `syncStatus = UPLOADED`
   - `markDuplicateConfirmed(ids: List<String>)` — set `syncStatus = DUPLICATE_CONFIRMED`
   - `markSyncedToOdoo(fccTransactionIds: List<String>)` — set `syncStatus = SYNCED_TO_ODOO`
   - `getForLocalApi(pumpNumber: Int?, limit: Int, offset: Int)` — exclude SYNCED_TO_ODOO
   - `getBufferStats()` — count by status for telemetry
2. Create `CleanupWorker`:
   - Run periodically (from `buffer.cleanupIntervalHours` in config, default 24h)
   - Delete SYNCED_TO_ODOO transactions older than `retentionDays` (default 7)
   - Delete terminal pre-auth records older than `retentionDays`
   - Trim audit log older than `retentionDays`
3. Create `IntegrityChecker`:
   - Run on app startup
   - Execute `PRAGMA integrity_check`
   - If corruption detected: backup DB file, delete, let Room recreate, log event for cloud telemetry

**Acceptance criteria:**
- Local dedup prevents duplicate inserts silently
- Upload batch returns records in `createdAt ASC` order
- Local API excludes SYNCED_TO_ODOO records
- Cleanup deletes old records correctly
- Integrity check detects and recovers from corruption
- Room in-memory DB tests for all operations

---

### EA-2.3: Connectivity Manager

**Sprint:** 5
**Prereqs:** EA-0.4, EA-2.1
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.4 Connectivity State Machine (THE definitive spec: states, probes, thresholds, module behaviour, side effects)
- `WIP-HLD-Edge-Agent.md` — §3.2 (operating modes), §4.2 (connectivity manager design)

**Task:**
Implement the dual-probe connectivity state machine.

**Detailed instructions:**
1. Create `ConnectivityManager` in `connectivity/`:
2. Implement two independent probes:
   - **Internet probe**: HTTP GET to cloud `GET /health`, 5s timeout, every 30s (configurable)
   - **FCC probe**: Call adapter `heartbeat()`, 5s timeout, every 30s (configurable)
3. State derivation from probe results:
   - Both UP → `FULLY_ONLINE`
   - Internet DOWN + FCC UP → `INTERNET_DOWN`
   - Internet UP + FCC DOWN → `FCC_UNREACHABLE`
   - Both DOWN → `FULLY_OFFLINE`
4. DOWN detection: **3 consecutive failures** required before transitioning to DOWN
5. UP recovery: **1 success** immediately transitions back to UP
6. Initialize in `FULLY_OFFLINE` on app start, run both probes immediately
7. Expose `StateFlow<ConnectivityState>` for other components to observe
8. Log audit events on every state transition
9. Side effects on transition (per §5.4 transition table):
   - Any → INTERNET_DOWN: log, increment telemetry counter, stop upload worker
   - Any → FCC_UNREACHABLE: log, alert diagnostics screen, stop FCC poller
   - Any → FULLY_OFFLINE: log, stop all cloud+FCC workers, local API continues
   - Any → FULLY_ONLINE: log, trigger immediate buffer replay + status poll + telemetry send
   - INTERNET_DOWN → FULLY_ONLINE: activate replay worker
   - FCC_UNREACHABLE → FULLY_ONLINE: resume FCC poller from last cursor

**Acceptance criteria:**
- State correctly derived from probe results
- 3-failure threshold prevents flapping
- Single success recovers immediately
- StateFlow emits correct states
- Transition side effects trigger (mock workers to verify)
- Unit tests for all state transitions
- Test: rapid probe alternation doesn't cause flapping

---

### EA-2.4: Local REST API — Full Implementation

**Sprint:** 5–6
**Prereqs:** EA-0.5, EA-2.2, EA-2.3
**Estimated effort:** 3–4 days

**Read these artifacts before starting:**
- `schemas/openapi/edge-agent-local-api.yaml` — THE definitive API spec (all endpoints, request/response shapes)
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — §5.5.3 DAO queries (what data to return)
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.3 LAN API key validation
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.3 local API visibility rules

**Task:**
Implement all 7 local REST API endpoints served by Ktor.

**Detailed instructions:**
1. **`GET /api/transactions`** — list buffered transactions
   - Query params: `pumpNumber`, `since`, `limit`, `offset`
   - Exclude SYNCED_TO_ODOO records (per §5.3 of state machines spec)
   - Return paginated response with transaction list
2. **`GET /api/transactions/{id}`** — get single transaction by ID
   - Return full transaction detail including raw payload
3. **`POST /api/transactions/acknowledge`** — Odoo POS marks transactions as consumed
   - Accept `{ transactionIds: [string] }`
   - This is a local-only operation — marks records for Odoo POS tracking
4. **`GET /api/pump-status`** — live pump statuses
   - Call adapter `getPumpStatus()` in real-time
   - If FCC_UNREACHABLE: return last-known status with `stale: true` flag
5. **`POST /api/preauth`** — submit pre-authorization (delegates to PreAuthHandler — EA-2.5)
   - Accept `PreAuthCommand` JSON
   - Return `PreAuthResult` JSON
6. **`POST /api/preauth/cancel`** — cancel pre-authorization
   - Accept `{ odooOrderId, siteCode }`
7. **`GET /api/status`** — agent status
   - Return: connectivity state, buffer stats, FCC heartbeat age, last sync timestamp, app version, uptime
8. Implement LAN API key authentication:
   - Requests from localhost (127.0.0.1) bypass auth
   - Requests from LAN IPs require `X-Api-Key` header
   - Validate against stored LAN API key (constant-time comparison)

**Acceptance criteria:**
- All 7 endpoints match `edge-agent-local-api.yaml` spec
- Localhost requests work without API key
- LAN requests require valid API key
- Transaction list excludes SYNCED_TO_ODOO records
- Pump status returns stale data when FCC unreachable
- Status endpoint returns correct connectivity state and buffer stats
- Ktor test application tests for each endpoint

---

### EA-2.5: Pre-Auth Handler

**Sprint:** 6
**Prereqs:** EA-2.1, EA-2.2, EA-2.3
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/specs/data-models/tier-1-1-pre-auth-record-spec.md` — pre-auth lifecycle
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.2 Pre-Auth, §5.4 what happens per connectivity state
- `docs/specs/reconciliation/tier-2-2-deduplication-strategy.md` — §5.5 Pre-auth dedup on Edge
- `schemas/canonical/pre-auth-record.schema.json` — all fields
- `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md` — `sendPreAuth` method

**Task:**
Implement the pre-auth handler — relays pre-auth commands from Odoo POS to FCC via LAN.

**Detailed instructions:**
1. Create `PreAuthHandler` in `preauth/`:
2. On pre-auth request from Odoo POS (via local API):
   a. **Local dedup check**: Query `PreAuthDao.getByOdooOrderId(odooOrderId, siteCode)`
      - If exists with non-terminal status (PENDING, AUTHORIZED, DISPENSING): return existing record
      - If exists with terminal status (COMPLETED, CANCELLED, EXPIRED, FAILED): allow new request
   b. **Check connectivity**: If `FCC_UNREACHABLE` or `FULLY_OFFLINE`, reject with `FCC_UNREACHABLE` error
   c. Create local `PreAuthRecord` with `status = PENDING`
   d. Call adapter `sendPreAuth(command)` — sends to FCC over LAN
   e. Update local record based on FCC response:
      - AUTHORIZED → set status, authorizationCode, expiresAtUtc
      - DECLINED/TIMEOUT/ERROR → set status FAILED with failure reason
   f. Mark `isCloudSynced = false` — queued for cloud forwarding
   g. Return result to Odoo POS immediately
3. Pre-auth is ALWAYS via LAN, regardless of internet state
4. Handle FCC timeouts (configurable, default 30s)
5. Implement pre-auth cancellation:
   - Find record by `odooOrderId`
   - If PENDING or AUTHORIZED: attempt FCC deauthorization, set status CANCELLED
   - If DISPENSING: cannot cancel (pump is active)
6. Implement pre-auth expiry checker (periodic):
   - Query pre-auths past `expiresAt` that are still PENDING/AUTHORIZED/DISPENSING
   - Transition to EXPIRED, attempt FCC deauthorization (best-effort)

**Acceptance criteria:**
- Pre-auth sent to FCC via LAN and result returned to Odoo POS
- Local dedup prevents duplicate pre-auths for same order
- Terminal-status records allow re-request
- FCC_UNREACHABLE properly rejects pre-auth
- Cancellation works for PENDING/AUTHORIZED
- Expiry checker cleans up old pre-auths
- Record marked for cloud sync
- Unit tests for all dedup/connectivity/status scenarios

---

### EA-2.6: Ingestion Orchestrator

**Sprint:** 6–7
**Prereqs:** EA-2.1, EA-2.2, EA-2.3
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `WIP-HLD-Edge-Agent.md` — §4.3 (ingestion orchestrator), §3.2 (operating modes), §6 (ingestion modes)
- `schemas/config/edge-agent-config.schema.json` — `ingestionMode` field
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.3 Edge Sync, §5.4 Connectivity (module behaviour table)

**Task:**
Implement the ingestion orchestrator that routes transactions based on ingestion mode and connectivity state.

**Detailed instructions:**
1. Create `IngestionOrchestrator` in `ingestion/`:
2. Create `FccPoller` — periodic task that:
   - Uses adapter `fetchTransactions(cursor)` to poll FCC over LAN
   - Runs on a configurable interval (`pullIntervalSeconds` from config)
   - Advances cursor using `SyncState.lastFccCursor`
   - For each transaction returned: pass to buffer manager
3. Implement ingestion mode routing:
   - **CLOUD_DIRECT**: FCC pushes to cloud directly. Agent runs safety-net LAN poller that:
     - Polls FCC on a longer interval (e.g., 5 minutes)
     - Buffers transactions locally
     - Uploads to cloud (cloud dedup handles the dual-path)
   - **RELAY**: Agent is primary receiver.
     - Polls FCC on normal interval (e.g., 30s)
     - Buffers locally, uploads to cloud
   - **BUFFER_ALWAYS**: Agent always buffers first.
     - Same as RELAY but explicit local-first semantics
4. Respect connectivity state:
   - FULLY_ONLINE: poll FCC + buffer + upload
   - INTERNET_DOWN: poll FCC + buffer (no upload)
   - FCC_UNREACHABLE: no polling (existing buffer accessible, upload continues if internet up)
   - FULLY_OFFLINE: nothing (local API serves stale buffer)
5. On connectivity recovery (INTERNET_DOWN → FULLY_ONLINE):
   - Trigger immediate upload of all PENDING records

**Acceptance criteria:**
- FCC poller fetches transactions on schedule
- Cursor advances correctly between polls
- Ingestion mode affects polling interval and behavior
- Connectivity state properly stops/resumes polling
- Recovery triggers immediate upload
- Unit tests for each mode + connectivity combination

---

## Phase 3 — Cloud ↔ Edge Integration (Sprints 6–8)

### EA-3.1: Cloud Upload Worker

**Sprint:** 7
**Prereqs:** EA-2.2, EA-2.3
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `WIP-HLD-Edge-Agent.md` — §4.4 (cloud sync engine), §5.3 (upload flow)
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/transactions/upload` (what cloud expects)
- `docs/specs/reconciliation/tier-2-2-deduplication-strategy.md` — §5.4 Edge pre-filtering, per-record response handling
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.3 PENDING → UPLOADED transition
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.1 device JWT auth

**Task:**
Implement the cloud upload worker that sends buffered transactions to the cloud.

**Detailed instructions:**
1. Create `CloudUploadWorker` in `sync/`:
2. Runs periodically when internet is available (`syncIntervalSeconds` from config)
3. Upload algorithm:
   a. Query PENDING records ordered by `createdAt ASC` (oldest first)
   b. Batch into groups of `uploadBatchSize` (from config, default 50)
   c. Send batch to `POST /api/v1/transactions/upload` with device JWT
   d. Process per-record response:
      - `status: "created"` → mark `syncStatus = UPLOADED`
      - `status: "skipped", reason: "DUPLICATE"` → mark `syncStatus = DUPLICATE_CONFIRMED` (never retry)
   e. On HTTP failure: increment `uploadAttempts`, set `lastUploadError`, retry on next cycle
4. **NEVER skip past a failed record** — retry the oldest PENDING batch first
5. Handle JWT expiry: on 401, trigger token refresh, retry
6. Handle 403 DEVICE_DECOMMISSIONED: stop all sync, show alert
7. Implement exponential backoff for repeated failures (1s, 2s, 4s, 8s, max 60s)
8. Suspend when connectivity state is INTERNET_DOWN or FULLY_OFFLINE
9. Resume immediately on FULLY_ONLINE recovery (triggered by ConnectivityManager)
10. Use Ktor HTTP client with OkHttp engine for cloud requests

**Acceptance criteria:**
- Uploads PENDING records in chronological order
- Per-record response handled correctly (UPLOADED vs DUPLICATE_CONFIRMED)
- Failed records retried with backoff
- Never skips past failed record
- JWT refresh on 401
- Decommission handling on 403
- Suspends when offline, resumes on recovery
- Unit tests for upload logic, response handling, retry logic

---

### EA-3.2: SYNCED_TO_ODOO Status Poller

**Sprint:** 7
**Prereqs:** EA-3.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `GET /api/v1/transactions/synced-status` endpoint
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.3 UPLOADED → SYNCED_TO_ODOO transition

**Task:**
Implement the worker that polls cloud for SYNCED_TO_ODOO status updates.

**Detailed instructions:**
1. Create `StatusPollWorker` in `sync/`:
2. Periodically poll `GET /api/v1/transactions/synced-status?since={lastPollTimestamp}`
3. For each `fccTransactionId` returned: call `TransactionBufferDao.markSyncedToOdoo()`
4. Update `SyncState.lastStatusPollAt`
5. Runs on `statusPollIntervalSeconds` from config (default 60s)
6. Only runs when internet is available
7. Records at SYNCED_TO_ODOO are excluded from local API responses

**Acceptance criteria:**
- Correctly transitions UPLOADED → SYNCED_TO_ODOO locally
- Last poll timestamp advances correctly
- Marked records excluded from local API
- Suspends when offline

---

### EA-3.3: Config Poll Worker

**Sprint:** 7
**Prereqs:** EA-0.6
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `GET /api/v1/agent/config` endpoint
- `schemas/config/edge-agent-config.schema.json` — config model
- `docs/specs/config/tier-2-4-edge-agent-configuration-schema.md` — hot-reload vs restart fields

**Task:**
Implement the config pull worker.

**Detailed instructions:**
1. Create `ConfigPollWorker` in `sync/`:
2. Periodically poll `GET /api/v1/agent/config` with `If-None-Match: {currentConfigVersion}`
3. On 304: no-op
4. On 200: parse new config, store in `AgentConfig` table, apply changes
5. Create `ConfigManager` that:
   - Holds current config in memory
   - On config update: hot-reload changed values (poll intervals, batch sizes, log level)
   - Identify fields requiring restart (FCC host/port, Ktor port) — log warning, don't force restart
6. Use `SyncState.lastConfigVersion` to track version

**Acceptance criteria:**
- Config pulled and stored locally
- Hot-reloadable fields take effect immediately
- 304 response handled efficiently (no re-parse)
- Config version tracked in sync state

---

### EA-3.4: Telemetry Reporter

**Sprint:** 7
**Prereqs:** EA-2.3, EA-2.2
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/agent/telemetry` endpoint
- `schemas/canonical/telemetry-payload.schema.json` — all telemetry fields
- `docs/specs/data-models/tier-1-1-telemetry-payload-spec.md` — field definitions

**Task:**
Implement telemetry reporting to cloud.

**Detailed instructions:**
1. Create `TelemetryReporter` in `sync/`:
2. Collect telemetry data:
   - Battery level (Android BatteryManager)
   - Storage free (StatFs)
   - Buffer depth (count by sync status from DAO)
   - FCC heartbeat age (time since last successful heartbeat)
   - Last sync timestamp
   - Sync lag (oldest PENDING record age)
   - App version (BuildConfig)
   - Error counts (accumulated since last report)
   - Connectivity state
3. Send `POST /api/v1/agent/telemetry` on `telemetryIntervalSeconds` (default 300s)
4. Fire-and-forget: if send fails, skip (no buffering of telemetry)
5. Only send when internet is available

**Acceptance criteria:**
- All telemetry fields populated from real device data
- Battery, storage, buffer stats accurate
- Sends on schedule, skips when offline
- No telemetry buffering (fire-and-forget)

---

### EA-3.5: Device Registration & Provisioning Flow

**Sprint:** 8
**Prereqs:** EA-0.6
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.1 Device Registration Flow (THE definitive spec)
- `docs/specs/data-models/tier-1-1-device-registration-spec.md` — registration models
- `schemas/canonical/device-registration.schema.json` — QR payload, request/response

**Task:**
Implement the provisioning/registration UI and flow.

**Detailed instructions:**
1. Create a provisioning Activity/Fragment:
   - QR code scanner (use CameraX or ZXing)
   - Scan extracts: `bootstrapToken`, `cloudBaseUrl`, `siteCode`
2. On QR scan:
   a. Extract bootstrap data
   b. Collect device fingerprint: serial number, model, OS version, app version
   c. Call `POST /api/v1/agent/register` with `{ bootstrapToken, siteCode, deviceFingerprint, appVersion }`
   d. On success: receive `{ deviceId, deviceToken, refreshToken, config }`
   e. Store `deviceToken` + `refreshToken` in Android Keystore
   f. Store `deviceId`, `siteCode`, `legalEntityId`, `cloudBaseUrl`, `fccHost`, `fccPort` in EncryptedSharedPreferences
   g. Store initial config in `AgentConfig` table
   h. Start the Edge Agent foreground service
3. Implement token refresh interceptor in Ktor HTTP client:
   - On 401: call `POST /api/v1/agent/token/refresh` with stored refresh token
   - On new tokens: update Keystore
   - On refresh failure: enter `REGISTRATION_REQUIRED` state, show alert
4. Handle 403 DEVICE_DECOMMISSIONED: stop all sync, show "Device Decommissioned" screen
5. On app startup: check for existing registration. If registered, start service. If not, show provisioning screen.

**Acceptance criteria:**
- QR scan extracts bootstrap data correctly
- Registration call succeeds and tokens stored securely
- Keystore used for token storage (not plain SharedPreferences)
- Token refresh works transparently on 401
- Decommission handling shows appropriate UI
- First launch shows provisioning; subsequent launches start service directly

---

### EA-3.6: Pre-Auth Cloud Forwarding

**Sprint:** 8
**Prereqs:** EA-2.5, EA-3.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/preauth` endpoint
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — §5.5.1 `is_cloud_synced` field

**Task:**
Implement forwarding of local pre-auth records to cloud.

**Detailed instructions:**
1. Create `PreAuthCloudForwardWorker` in `sync/`:
2. Query `PreAuthDao.getUnsynced(limit)` — records where `isCloudSynced = false`
3. Send each to `POST /api/v1/preauth` on cloud
4. On success: `PreAuthDao.markCloudSynced(id)`
5. On failure: increment `cloudSyncAttempts`, retry on next cycle
6. Runs periodically when internet is available

**Acceptance criteria:**
- Unsynced pre-auth records forwarded to cloud
- Synced flag updated on success
- Retry on failure with attempt counter
- Suspends when offline

---

## Phase 6 — Hardening & Production Readiness (Sprints 10–12)

### EA-6.1: Offline Scenario Stress Testing

**Sprint:** 10
**Prereqs:** All Phase 2 + Phase 3 tasks
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/specs/testing/testing-strategy.md` — offline scenario testing section
- `WIP-HLD-Edge-Agent.md` — §8.6 (resilience), §9 (risk analysis)

**Task:**
Design and execute offline scenario tests.

**Detailed instructions:**
1. Test scenarios:
   - Internet drop during upload batch (partial success) → verify retry resumes correctly
   - FCC LAN drop during poll → verify graceful degradation
   - 1-hour internet outage → verify buffer captures all transactions, replay succeeds
   - 24-hour internet outage → verify 30,000+ records buffered without OOM or battery drain
   - 7-day simulated outage → verify buffer integrity and replay ordering
   - Power loss during SQLite write → verify WAL recovery
   - App kill by Android → verify foreground service restarts
2. Use emulator with network controls or Urovo hardware with airplane mode
3. Verify: zero transaction loss in all scenarios
4. Measure: battery consumption, memory usage, SQLite performance at 30K records

**Acceptance criteria:**
- Zero transactions lost in any scenario
- Buffer handles 30,000+ records without OOM
- WAL mode recovers from simulated power loss
- Upload replay maintains chronological order after recovery
- Battery consumption acceptable for 8-hour shift

---

### EA-6.2: Security Hardening

**Sprint:** 11
**Prereqs:** EA-3.5
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.3 Edge Agent Security

**Task:**
Verify and harden Edge Agent security.

**Detailed instructions:**
1. Verify Android Keystore integration: tokens stored, non-exportable
2. Verify EncryptedSharedPreferences: all sensitive config encrypted
3. Verify log redaction: no sensitive fields in logcat output
4. Verify LAN API key validation: non-localhost requests require key
5. Verify certificate pinning configuration (if cloud domain is ready)
6. Verify `@Sensitive` annotation prevents field serialization to logs

**Acceptance criteria:**
- No plaintext tokens/credentials accessible outside Keystore
- Log output contains no sensitive data
- LAN API key enforcement works
- Certificate pinning rejects mismatched certs (test with self-signed)

---

### EA-6.3: Diagnostics UI

**Sprint:** 11
**Prereqs:** All Phase 2 + Phase 3 tasks
**Estimated effort:** 1–2 days

**Read these artifacts before starting:**
- `WIP-HLD-Edge-Agent.md` — §4.7 (diagnostics screen), §15.8 (UI)
- `docs/specs/error-handling/tier-3-5-observability-monitoring-design.md` — Edge Agent telemetry fields

**Task:**
Implement the diagnostics screen for site supervisors.

**Detailed instructions:**
1. Create a simple diagnostics Activity accessible from the notification:
   - Connectivity state (color-coded: green/yellow/red)
   - FCC connection status + last heartbeat time
   - Buffer stats: PENDING count, UPLOADED count, SYNCED_TO_ODOO count
   - Last cloud sync timestamp + sync lag
   - Battery level, storage free
   - App version, device ID
   - Recent audit log entries (last 20)
2. LAN API key QR display (for non-primary HHT distribution)
3. Manual actions: force sync, force FCC poll, clear cache

**Acceptance criteria:**
- All status fields displaying real-time data
- Color-coded connectivity indicator
- QR code for LAN API key displays correctly
- Manual sync/poll triggers work
