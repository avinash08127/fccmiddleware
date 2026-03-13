# Android Technical Findings — FCC Edge Agent

**Module**: FCC Edge Agent (Android)
**Audit date**: 2026-03-13
**Scope**: End-to-end trace — UI → State → Workers → Adapters → DB/Network

---

## AT-001: IngestionOrchestrator Constructor Parameters Shadowed by Late-Bound Volatiles

| Field | Value |
|-------|-------|
| **ID** | AT-001 |
| **Title** | Constructor-injected adapter and config are shadowed by mutable @Volatile fields |
| **Module** | Transaction Management |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Status** | **RESOLVED** |
| **Description** | `IngestionOrchestrator` accepts `adapter` and `config` as constructor parameters (lines 61, 68) but immediately assigns them to `@Volatile internal var` fields of the same name (lines 72, 76). These fields are then overwritten by `wireRuntime()`. The Koin DI module at `AppModule.kt` line 240 constructs `IngestionOrchestrator` with only `bufferManager` and `syncStateDao`, leaving `adapter` and `config` as null defaults. This dual-initialization pattern (constructor + late-binding) creates confusion about ownership and lifecycle — the constructor parameters are never used by the DI graph. |
| **Evidence** | `ingestion/IngestionOrchestrator.kt` lines 60–76; `di/AppModule.kt` lines 240–244. |
| **Impact** | Code maintainability issue. Future developers may pass non-null values via constructor, not realizing they'll be overwritten by `wireRuntime()`. |
| **Recommended Fix** | Remove `adapter` and `config` from the constructor. Make them private with `wireRuntime()` as the sole setter. |
| **Resolution** | Removed `adapter` and `config` from the constructor. Fields are now `@Volatile private var` initialized to `null`, with `wireRuntime()` as the sole setter. Updated all test call sites (`IngestionOrchestratorTest`, `ConcurrencyRaceConditionTest`, `OfflineCrashRecoveryTest`) to use `.also { it.wireRuntime(adapter, config) }` instead. |

---

## AT-002: DiagnosticsActivity Has Direct DAO Injection — Fat Activity Anti-Pattern

| Field | Value |
|-------|-------|
| **ID** | AT-002 |
| **Title** | DiagnosticsActivity directly injects 7 DAO/manager dependencies instead of using a ViewModel |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Medium |
| **Category** | Fat ViewModels / Business Logic in UI |
| **Status** | **RESOLVED** |
| **Description** | `DiagnosticsActivity` injects `ConnectivityManager`, `SiteDataDao`, `TransactionBufferDao`, `SyncStateDao`, `AuditLogDao`, `ConfigManager`, and `StructuredFileLogger` directly via Koin. All data fetching, transformation, and formatting logic lives in the Activity's `refreshData()` method (lines 116–248). This violates the separation of concerns — the Activity is simultaneously a view, a data accessor, and a presenter. On configuration changes (e.g., locale), the entire data pipeline re-executes from scratch. |
| **Evidence** | `ui/DiagnosticsActivity.kt` lines 46–52: 7 `by inject()` statements. Lines 116–248: data fetching + UI formatting in one method. |
| **Impact** | The Activity is tightly coupled to data access, making it untestable and fragile to refactoring. No way to unit test the diagnostics data pipeline without an Activity context. |
| **Recommended Fix** | Extract a `DiagnosticsViewModel` that exposes a `StateFlow<DiagnosticsSnapshot>`. The Activity should only observe and render. |
| **Resolution** | Extracted `DiagnosticsViewModel` with all 7 dependencies, exposing `StateFlow<DiagnosticsSnapshot?>`. The Activity now has a single `by viewModel()` injection and a pure `renderSnapshot()` method. Auto-refresh scheduling moved to ViewModel (`startAutoRefresh`/`stopAutoRefresh`). The diagnostics data pipeline is now unit-testable without an Activity context. ViewModel registered in `AppModule.kt`. |

---

## AT-003: Duplicated CoroutineScope Definitions — Service and DI Module

| Field | Value |
|-------|-------|
| **ID** | AT-003 |
| **Title** | EdgeAgentForegroundService creates its own CoroutineScope separate from the Koin-managed scope |
| **Module** | Cross-Cutting Infrastructure |
| **Severity** | Medium |
| **Category** | Duplicated Logic |
| **Status** | **RESOLVED** |
| **Description** | `EdgeAgentForegroundService` creates a private `serviceScope` with `SupervisorJob() + Dispatchers.IO + CoroutineExceptionHandler` (lines 61–64). The Koin `AppModule` also creates a `single<CoroutineScope>` with the same configuration (lines 66–72). All workers and handlers injected into the service use the Koin scope, but the service's own monitoring coroutines (`monitorReprovisioningState`, `monitorDecommissionedState`, `observeConfigForRuntimeUpdates`) use `serviceScope`. These two scopes have different lifecycles — `serviceScope` is cancelled in `onDestroy()`, but the Koin scope lives until the process dies. This creates a subtle divergence where service-level coroutines may be cancelled while worker coroutines continue. |
| **Evidence** | `service/EdgeAgentForegroundService.kt` lines 61–64: `serviceScope`. `di/AppModule.kt` lines 66–72: `single<CoroutineScope>`. |
| **Impact** | After `onDestroy()`, Koin-scoped coroutines (PreAuthHandler audit logs, CadenceController) may continue executing while the service thinks it has stopped. This can cause database writes after the service lifecycle ends. |
| **Recommended Fix** | Either inject the Koin scope into the service and cancel it in `onDestroy()`, or ensure all coroutines that should follow the service lifecycle use `serviceScope`. Consider making the Koin scope a child of `serviceScope`. |
| **Resolution** | Removed the private `serviceScope` from `EdgeAgentForegroundService`. The service now injects the single Koin-managed `CoroutineScope` via `by inject()` and cancels it in `onDestroy()`. All coroutines — service monitors, workers, handlers, and logger — now share one scope with a unified lifecycle. The Koin scope in `AppModule` was promoted to the single authoritative scope, with its exception handler using lazy logger resolution to avoid circular dependencies. Updated `EdgeAgentForegroundServiceTest` to register a `CoroutineScope` in the test Koin module. |

---

## AT-004: StructuredFileLogger Gets Its Own CoroutineScope — Third Scope

| Field | Value |
|-------|-------|
| **ID** | AT-004 |
| **Title** | StructuredFileLogger creates a third independent CoroutineScope |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Low |
| **Category** | Duplicated Logic |
| **Status** | **RESOLVED** |
| **Description** | `StructuredFileLogger` is constructed in `AppModule.kt` line 56 with `CoroutineScope(SupervisorJob() + Dispatchers.IO)`. This is a third scope alongside `serviceScope` and the Koin `single<CoroutineScope>`. The logger's scope is never cancelled — it lives until process death. |
| **Evidence** | `di/AppModule.kt` lines 56–59: standalone `CoroutineScope` for logger. |
| **Impact** | The logger scope cannot be cancelled for testing and may leak in instrumented tests. Minor in production since the logger should live for the process lifetime. |
| **Recommended Fix** | Use a child scope of the Koin-managed scope, or explicitly cancel it when appropriate. |
| **Resolution** | The logger's scope is now a child scope of the Koin-managed scope via `SupervisorJob(parentScope.coroutineContext[Job])`. When the service cancels the Koin scope in `onDestroy()`, the logger's child scope is cancelled automatically via the Job hierarchy. Added a `close()` method to `StructuredFileLogger` that flushes and closes the file writer; the service calls it in `onDestroy()` before scope cancellation to ensure the last log entries are persisted. Reordered `AppModule` declarations so the Koin scope is created first, breaking the circular dependency by resolving the logger lazily in the exception handler. |

---

## AT-005: PreAuthHandler Uses Unstructured scope.launch for Audit Logging

| Field | Value |
|-------|-------|
| **ID** | AT-005 |
| **Title** | Audit log inserts fire-and-forget with no error propagation |
| **Module** | Pre-Authorization |
| **Severity** | Low |
| **Category** | Weak Error Handling |
| **Status** | **RESOLVED** |
| **Description** | `PreAuthHandler` uses `scope.launch { auditLogDao.insert(...) }` in multiple places (lines 267–278, 343–352, 410–419, 434–443) for audit logging. These launches are fire-and-forget — if the insert fails, the exception is caught by the Koin scope's `CoroutineExceptionHandler` and logged, but the audit record is silently lost. For a financial application, audit trail completeness is important. |
| **Evidence** | `preauth/PreAuthHandler.kt` lines 267–278: `scope.launch { auditLogDao.insert(...) }` |
| **Impact** | Audit trail gaps for pre-auth events. Financial regulators may require complete audit trails. |
| **Recommended Fix** | Add try/catch inside the `scope.launch` block with a fallback write to the file logger. Consider making audit logging synchronous for critical events. |
| **Resolution** | Added try/catch inside all 5 `scope.launch` audit log blocks in `PreAuthHandler` (PRE_AUTH_HANDLED, PRE_AUTH_CANCELLED, PRE_AUTH_DEAUTH_EXHAUSTED, PRE_AUTH_DEAUTH_RETRY_PENDING, PRE_AUTH_EXPIRED). On insert failure, the exception and audit event details are written to `AppLogger.e()`, which logs to both logcat and the `StructuredFileLogger` JSONL file. This ensures audit intent is never silently lost — if the DB insert fails, the structured file log provides a secondary audit trail. |

---

## AT-006: EdgeAgentForegroundService.onDestroy Does Not Stop FCC Adapter Connections

| Field | Value |
|-------|-------|
| **ID** | AT-006 |
| **Title** | Service onDestroy does not disconnect FCC adapter TCP/HTTP connections |
| **Module** | FCC Adapters / Service Lifecycle |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Status** | **RESOLVED** |
| **Description** | `EdgeAgentForegroundService.onDestroy()` stops the cadence controller, connectivity manager, network binder, local API, and WebSocket server, but does not explicitly disconnect the FCC adapter. For DOMS (persistent TCP), this leaves a TCP connection open until the OS closes it. For Radix and Advatec push listeners (Ktor CIO servers), these embedded servers are not stopped. The `serviceScope.cancel()` at line 169 cancels coroutines but does not close sockets or servers that own their own event loops. |
| **Evidence** | `service/EdgeAgentForegroundService.kt` lines 162–171: no adapter cleanup. |
| **Impact** | Resource leaks: TCP connections and embedded Ktor servers may persist after service destruction. The DOMS heartbeat manager and JPL TCP client may hold sockets open. |
| **Recommended Fix** | Add `fccRuntimeState.adapter?.let { if (it is IFccConnectionLifecycle) it.disconnect() }` in `onDestroy()`. Also stop `RadixPushListener` and `AdvatecWebhookListener` servers. |
| **Resolution** | Added `fccRuntimeState.clear()` in `onDestroy()` after `cadenceController.stop()` and before `appScope.cancel()`. This synchronously closes the adapter via `(adapter as Closeable).close()`: DomsJplAdapter cancels its adapter scope (cascading to TCP client and heartbeat manager), RadixAdapter stops its push listener and closes the HTTP client, AdvatecAdapter stops its webhook listener. Made `AdvatecAdapter` implement `Closeable` (it previously only had a `shutdown()` method) so `FccRuntimeState.closeCurrentAdapter()` can reach it through the same `Closeable` interface used by DomsJplAdapter and RadixAdapter. |

---

## AT-007: CircuitBreaker State Accessed Without Synchronization in Some Paths

| Field | Value |
|-------|-------|
| **ID** | AT-007 |
| **Title** | CircuitBreaker convenience aliases bypass Mutex synchronization |
| **Module** | Cloud Sync |
| **Severity** | Low |
| **Category** | Weak Error Handling |
| **Status** | **RESOLVED** |
| **Description** | `CloudUploadWorker` exposes `consecutiveFailureCount` and `nextRetryAt` as convenience getters that read directly from `CircuitBreaker` fields (lines 104–105, 179–180). The `CircuitBreaker` class uses a `Mutex` for state transitions, but these read-only accessors likely bypass it (reading `@Volatile` fields without the mutex). While individually safe for reads, combined use in log messages (lines 699, 536) creates a TOCTOU window where `consecutiveFailureCount` and `state` may represent different snapshots. |
| **Evidence** | `sync/CloudUploadWorker.kt` lines 104–105: `internal val consecutiveFailureCount: Int get() = uploadCircuitBreaker.consecutiveFailureCount`. |
| **Impact** | Minor: log messages may show inconsistent circuit breaker state. No functional impact. |
| **Recommended Fix** | Accept the minor inconsistency for logging (it's diagnostics-only), or provide a `CircuitBreaker.snapshot()` method that atomically reads all state under the mutex. |
| **Resolution** | Added `CircuitBreaker.Snapshot` data class and `suspend fun snapshot()` method that atomically reads `state`, `consecutiveFailureCount`, and `nextRetryAt` under the mutex. Updated both `handleUploadResult` and `handleStatusPollResult` transport-failure log messages in `CloudUploadWorker` to use `snapshot()` instead of reading fields individually. The convenience aliases (`consecutiveFailureCount`, `nextRetryAt`) are retained for test compatibility — they read `@Volatile` fields which is safe for individual reads; the snapshot is used only where multiple fields must be consistent. |

---

## AT-008: SettingsActivity Performs SharedPreferences I/O on Main Thread

| Field | Value |
|-------|-------|
| **ID** | AT-008 |
| **Title** | saveAndReconnect() calls LocalOverrideManager (SharedPreferences) on UI thread |
| **Module** | Site Configuration |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Status** | **RESOLVED** |
| **Description** | `SettingsActivity.saveAndReconnect()` calls `localOverrideManager.saveOverride()` and `localOverrideManager.clearOverride()` directly on the main thread (lines 236–264). `LocalOverrideManager` uses `EncryptedSharedPreferences` which performs AES encryption and file I/O synchronously. On slow storage or under memory pressure, this can cause ANRs (Application Not Responding). The `populateFields()` call at line 274 also reads from `ConfigManager.config` and `EncryptedPrefsManager` on the main thread. |
| **Evidence** | `ui/SettingsActivity.kt` lines 188–283: entire `saveAndReconnect()` runs on UI thread. |
| **Impact** | Potential ANR on devices with slow storage. EncryptedSharedPreferences is known to be slow (>100ms on some devices). |
| **Recommended Fix** | Move the save operations to a coroutine on `Dispatchers.IO`, then update the UI on the main thread. |
| **Resolution** | Split `populateFields()` into `collectFieldData()` (IO-safe read returning a `FieldData` data class) and `applyFieldData()` (Main-thread UI update). All three call sites now read on `Dispatchers.IO` and apply on Main: (1) `onCreate()` launches a coroutine that reads on IO then applies on Main with saved-instance-state restoration afterward; (2) `saveAndReconnect()` calls `collectFieldData()` while still on the IO dispatcher before switching to Main; (3) `resetToCloudDefaults()` follows the same pattern. Save/clear operations in `saveAndReconnect()` and `resetToCloudDefaults()` were already on `Dispatchers.IO` from a prior fix. |

---

## AT-009: LocalApiServer.reconfigure Stops and Restarts Server Non-Atomically

| Field | Value |
|-------|-------|
| **ID** | AT-009 |
| **Title** | Server reconfiguration has a window where no server is listening |
| **Module** | Local API |
| **Severity** | Medium |
| **Category** | Weak Error Handling |
| **Description** | `LocalApiServer.reconfigure()` calls `start()` which first calls `server?.stop(1_000, 2_000)` then creates and starts a new server. During the stop-start window (up to 3 seconds), the API endpoint is unreachable. If Odoo POS sends a pre-auth request during this window, it will get a connection refused error. There is no queuing mechanism or graceful transition. |
| **Evidence** | `api/LocalApiServer.kt` lines 136–138: `server?.stop(1_000, 2_000)` followed by `embeddedServer(...)`. |
| **Impact** | Pre-auth requests sent during server reconfiguration (triggered by config updates or settings changes) will fail with connection refused. |
| **Recommended Fix** | Start the new server on a temporary port, then atomically swap. Or keep the old server alive until the new one is listening. Alternatively, add a client-side retry mechanism in POS for connection refused errors. |
| **Resolution** | Refactored `start()` to build the new server configuration before stopping the old one. The old server is stopped with minimal grace/timeout (250ms/500ms instead of 1s/2s), and the new server is started immediately after. This reduces the unavailability window from ~3 seconds to milliseconds (just the time between socket close and rebind). The new server is fully configured before the old one stops, so only the socket bind needs to happen after shutdown. |

---

## AT-010: SlidingWindowCounter Rate Limiter Has Race Condition

| Field | Value |
|-------|-------|
| **ID** | AT-010 |
| **Title** | Rate limiter's window reset is not fully atomic — can briefly over-count |
| **Module** | Local API |
| **Severity** | Low |
| **Category** | Weak Error Handling |
| **Description** | `SlidingWindowCounter.tryAcquire()` uses `AtomicLong` for the window second and `AtomicInteger` for the counter, but these are not updated atomically together. The comment at line 463 acknowledges the race: "worst case two threads both reset, which slightly under-counts for one second." However, the opposite race is also possible: thread A reads `windowSecond` as current, thread B resets it, thread A then increments the old counter which was already reset — effectively double-counting in the new window. |
| **Evidence** | `api/LocalApiServer.kt` lines 452–469: non-atomic compound operation across two atomics. |
| **Impact** | Under high concurrency, the rate limiter may occasionally allow slightly more or fewer requests than the configured limit. Functionally acceptable for a LAN API. |
| **Recommended Fix** | Accept the minor inaccuracy (the comment acknowledges it), or use a `synchronized` block if exact enforcement is needed. |
| **Resolution** | Replaced the dual-atomic (`AtomicLong` + `AtomicInteger`) implementation with a `@Synchronized` method using plain `Long` and `Int` fields. This makes the window-second check and counter update fully atomic, eliminating both the over-count and under-count race conditions. Synchronization overhead is negligible for a LAN API with max 30 rps. |

---

## AT-011: Koin Module Registers IngestionOrchestrator Without transactionDao

| Field | Value |
|-------|-------|
| **ID** | AT-011 |
| **Title** | IngestionOrchestrator is missing transactionDao injection for fiscalization |
| **Module** | Transaction Management |
| **Severity** | Medium |
| **Category** | Incorrect Dependency Injection |
| **Description** | The Koin `appModule` constructs `IngestionOrchestrator` with only `bufferManager` and `syncStateDao` (lines 240–244). The constructor also accepts `transactionDao` (used for fiscalization marking at lines 349, 382, 443, 447) but it is not provided in the DI declaration. This means `transactionDao` is always null, and the entire post-dispense fiscalization flow (`retryPendingFiscalization()`, `markFiscalPending()`) is silently disabled — `txDao?.markFiscalPending(...)` becomes a no-op. |
| **Evidence** | `di/AppModule.kt` lines 240–244: `IngestionOrchestrator(bufferManager = get(), syncStateDao = get())`. `ingestion/IngestionOrchestrator.kt` line 67: `private val transactionDao: TransactionBufferDao? = null`. |
| **Impact** | Post-dispense fiscalization (ADV-7.3) is completely non-functional. Advatec transactions will never receive fiscal receipts through the retry mechanism. |
| **Recommended Fix** | Add `transactionDao = get()` to the `IngestionOrchestrator` construction in `AppModule.kt`. |
| **Resolution** | Already resolved in a prior fix. `AppModule.kt` lines 258–262 now include `transactionDao = get()` in the `IngestionOrchestrator` construction, enabling the post-dispense fiscalization flow. |

---

## AT-012: ConfigManager.applyConfig Validates Port 443 Only — Blocks Dev/Staging Non-Standard Ports

| Field | Value |
|-------|-------|
| **ID** | AT-012 |
| **Title** | URL validation rejects non-443 HTTPS ports, breaking development and staging environments |
| **Module** | Site Configuration |
| **Severity** | Medium |
| **Category** | Incorrect Validations (carried from config) |
| **Description** | `ConfigManager.validateExternalUrl()` at line 344 rejects any HTTPS URL with a port other than 443. The `CloudEnvironments` object defines `LOCAL` as `https://localhost:5001`. Any staging or development environment using non-standard HTTPS ports (common in k8s port-forwards or local development) will have its config rejected with `INSECURE_URL`. |
| **Evidence** | `config/ConfigManager.kt` lines 343–346: `if (port != -1 && port != 443)`. `config/CloudEnvironments.kt`: LOCAL maps to `https://localhost:5001`. |
| **Impact** | Development and staging configurations using non-standard HTTPS ports will be rejected. Local development is broken. |
| **Recommended Fix** | Allow non-standard ports for non-production environments (check the environment field), or whitelist known staging/dev domains. |
| **Resolution** | Relaxed the port validation in `validateExternalUrl()` to accept any valid port (1–65535) for HTTPS URLs instead of restricting to port 443 only. HTTPS on any port is still encrypted and authenticated — the HTTPS scheme requirement itself provides the security guarantee. The port restriction was over-aggressive SSRF protection that blocked legitimate dev/staging environments. |

---

## AT-013: CloudUploadWorker Constructor Has Too Many Nullable Parameters

| Field | Value |
|-------|-------|
| **ID** | AT-013 |
| **Title** | All 8 constructor parameters of CloudUploadWorker are nullable — API surface allows fully uninitialized instances |
| **Module** | Cloud Sync |
| **Severity** | Low |
| **Category** | Architecture Violations |
| **Status** | **RESOLVED** |
| **Description** | `CloudUploadWorker` accepts 8 nullable parameters (lines 64–73) including `bufferManager`, `cloudApiClient`, and `tokenProvider`. Every public method starts with 3 null-check guards. This "nullable-everything" pattern exists because the worker is registered in Koin before security modules are wired. However, the Koin module actually provides all dependencies as non-null singletons — the nullability is legacy from an earlier design phase. |
| **Evidence** | `sync/CloudUploadWorker.kt` lines 64–73: all parameters nullable. Lines 118–129: three null-check returns per method. |
| **Impact** | Code readability and maintainability. Each method has 6 lines of boilerplate null-checks before doing real work. |
| **Recommended Fix** | Make the core dependencies non-nullable since Koin provides them. Use `get()` not `getOrNull()` in the Koin module. Keep only `telemetryReporter` and `fileLogger` nullable if they truly are optional. |
| **Resolution** | Made `bufferManager`, `syncStateDao`, `cloudApiClient`, `tokenProvider`, and `configManager` non-nullable. Only `telemetryReporter` and `fileLogger` remain nullable as they are genuinely optional (diagnostics-only). Removed all null-check guards for the now non-nullable parameters from `uploadPendingBatch()`, `pollSyncedToOdooStatus()`, `reportTelemetry()`, `reportDiagnosticLogs()`, and the internal `updateLast*` helpers. Updated `CloudUploadWorkerTest` to remove the three null-guard tests and add the `configManager` mock. The Koin module already uses `get()` (not `getOrNull()`), so no DI changes were needed. |

---

## AT-014: ProvisioningViewModel Contains Inline Credential Storage and Config Encryption Logic

| Field | Value |
|-------|-------|
| **ID** | AT-014 |
| **Title** | handleRegistrationSuccess() mixes ViewModel coordination with security-critical business logic |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Medium |
| **Category** | Fat ViewModels / Business Logic in UI |
| **Status** | **RESOLVED** |
| **Description** | `ProvisioningViewModel.handleRegistrationSuccess()` (lines 122–223) performs 8 distinct responsibilities: (1) clear stale Keystore keys, (2) parse siteConfig JSON, (3) update CloudApiClient base URL, (4) encrypt and store tokens via KeystoreManager, (5) persist registration identity via EncryptedPrefsManager, (6) encrypt config with Keystore and encode to Base64, (7) store config in Room with write-verify retry, (8) sync site data. This is security-critical business logic (credential lifecycle management) embedded in a ViewModel. It is untestable without mocking 7 dependencies, and cannot be reused if a headless re-registration path is needed (e.g., automated provisioning via MDM). The desktop edge agent has an equivalent `RegistrationHandler` class for this logic. |
| **Evidence** | `ui/ProvisioningViewModel.kt` lines 122–223: `handleRegistrationSuccess()` with 8 responsibilities. Constructor injects 7 dependencies (lines 43–49). |
| **Impact** | The registration logic cannot be unit-tested without an Android Instrumentation test (AndroidViewModel requires Application context). Any future provisioning path (MDM push, API-triggered) must duplicate this logic. |
| **Recommended Fix** | Extract a `RegistrationHandler` class that owns the credential storage, config encryption, and Room persistence pipeline. The ViewModel should only call `registrationHandler.completeRegistration(qrData, result)` and observe the outcome. |
| **Resolution** | Extracted `RegistrationHandler` class in `com.fccmiddleware.edge.registration` package. It owns all 8 responsibilities: database clearing, Keystore/prefs cleanup, siteConfig parsing, base URL update, token storage, registration identity persistence, config encryption + Room upsert, and site data sync. `ProvisioningViewModel` now has only 3 dependencies (`cloudApiClient`, `encryptedPrefs`, `registrationHandler`) and delegates via `registrationHandler.completeRegistration(qrCloudBaseUrl, environment, response)`. The handler is UI-independent and reusable for headless provisioning paths. Registered as a Koin `single` in `AppModule`. Updated `ProvisioningViewModelTest` to use the new constructor. |

---

## AT-015: schemaVersion Parsing Uses Fragile String Splitting — Silent Fallback to 1

| Field | Value |
|-------|-------|
| **ID** | AT-015 |
| **Title** | AgentConfig schemaVersion parsed from string with silent fallback — version mismatch possible |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Low |
| **Category** | Weak Error Handling |
| **Description** | `ProvisioningViewModel.handleRegistrationSuccess()` at line 183 parses `schemaVersion` from the config DTO: `siteConfig.schemaVersion.substringBefore(".").toIntOrNull() ?: 1`. This parsing chain silently falls back to `1` when the input contains non-numeric prefixes (e.g., `"v2.0"` → `"v2"` → `null` → `1`), empty strings, or completely non-numeric values. The `AgentConfig` entity stores this as an `Int` column. When `ConfigManager.loadFromLocal()` later reads this value, it may interpret a v2 schema config as v1, leading to incorrect field mapping or validation bypass. |
| **Evidence** | `ui/ProvisioningViewModel.kt` line 183: `schemaVersion = siteConfig.schemaVersion.substringBefore(".").toIntOrNull() ?: 1`. |
| **Impact** | If the cloud sends a config with a non-standard schema version format, the agent silently treats it as schema v1. This could cause config fields to be misinterpreted, though the current codebase only uses schema v1. |
| **Recommended Fix** | Log a warning when the fallback to `1` is triggered, so operators can detect version format mismatches. Validate the `schemaVersion` format upstream in the DTO deserialization. |

---

## AT-016: CloudApiClient DI Creates Throwaway HTTP Client With Stub Hostname

| Field | Value |
|-------|-------|
| **ID** | AT-016 |
| **Title** | Pre-registration CloudApiClient pins certificates to "not-yet-provisioned" hostname — pins wasted |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Low |
| **Category** | Architecture Violations |
| **Description** | `AppModule.kt` at line 105 creates `HttpCloudApiClient` with `baseUrl = "https://not-yet-provisioned"` when the device is not yet registered (`encryptedPrefs.cloudBaseUrl` is null). The `buildKtorClient()` function at line 686 then extracts `"not-yet-provisioned"` as the hostname and binds all certificate pins to it. This OkHttp client and its CertificatePinner are thrown away on the first `updateBaseUrl()` call after registration (line 302 in `HttpCloudApiClient`). The initial client creation performs memory allocation, OkHttp builder configuration, and certificate pinner construction — all for a client that will never be used for its pinned hostname. Additionally, the `registerDevice()` method uses the `cloudBaseUrl` parameter to construct the URL, which means the registration call goes to the QR-provided URL (e.g., `api.fccmiddleware.io`) but the pinner only has pins for `not-yet-provisioned` — making the registration call effectively unpinned (see AS-011). |
| **Evidence** | `di/AppModule.kt` line 105: `val baseUrl = encryptedPrefs.cloudBaseUrl ?: "https://not-yet-provisioned"`. `sync/CloudApiClient.kt` lines 519, 686: URL and pinner construction. |
| **Impact** | Minor memory waste during initial app startup. The more significant impact is the unpinned registration call (reported separately as AS-011). |
| **Recommended Fix** | Use `null` as the initial base URL and defer HTTP client creation until the first real URL is known. Alternatively, create a lightweight stub client without certificate pinning for the pre-registration phase, and build the real pinned client during `handleRegistrationSuccess()`. |

---

## AT-017: DomsJplAdapter Creates Its Own Unmanaged CoroutineScope — Never Cancelled

| Field | Value |
|-------|-------|
| **ID** | AT-017 |
| **Title** | Adapter-private CoroutineScope lives until process death — fourth independent scope |
| **Module** | FCC Adapters (DOMS) |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Description** | `DomsJplAdapter` creates a private `adapterScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)` (line 46). This scope is never cancelled — not in `disconnect()`, not in `FccAdapterFactory`, and not in `EdgeAgentForegroundService.onDestroy()`. The heartbeat loop and TCP read loop coroutines launched on this scope via `JplTcpClient` and `JplHeartbeatManager` will run until the Android process is killed. This is a fourth independent CoroutineScope alongside `serviceScope`, the Koin scope, and the logger scope (see AT-003/AT-004). When the adapter is replaced via `FccAdapterFactory.resolve()` (AF-018), the old `adapterScope`'s coroutines continue running on the old TCP connection. |
| **Evidence** | `adapter/doms/DomsJplAdapter.kt` line 46: `private val adapterScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)`. `disconnect()` at lines 107–111 does not cancel `adapterScope`. |
| **Impact** | Orphaned coroutines after adapter replacement. TCP read loop and heartbeat manager from the old adapter continue consuming CPU and holding the old socket. |
| **Recommended Fix** | Cancel `adapterScope` in `disconnect()`: `adapterScope.cancel()`. Recreate it in `connect()` if needed. Alternatively, make the scope a child of the Koin-managed scope so it is cancelled when the service stops. |

---

## AT-018: RadixAdapter Implements Closeable but IFccAdapter Does Not — close() Never Called

| Field | Value |
|-------|-------|
| **ID** | AT-018 |
| **Title** | Closeable interface is invisible through the IFccAdapter contract — resources leak on adapter replacement |
| **Module** | FCC Adapters (Radix) |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Description** | `RadixAdapter` implements `Closeable` with a `close()` method that stops the push listener and closes the OkHttp `HttpClient`. However, `FccAdapterFactory.resolve()` returns `IFccAdapter`, which does not extend `Closeable` or declare any lifecycle cleanup method. When the service replaces the adapter (on config change), it receives the new `IFccAdapter` reference and the old one is garbage collected — but the OkHttp connection pool threads, the CIO push listener server, and the OkHttp dispatcher threads are NOT garbage-collected because they hold live thread references. Similarly, `AdvatecAdapter` has a `shutdown()` method that stops its webhook listener, but this is not on the `IFccAdapter` interface. `DomsJplAdapter` implements `IFccConnectionLifecycle.disconnect()`, but the caller must know to check for this interface at runtime. |
| **Evidence** | `adapter/radix/RadixAdapter.kt` line 53: `IFccAdapter, Closeable`. `adapter/common/IFccAdapter.kt`: no lifecycle method. `adapter/common/FccAdapterFactory.kt` line 27: returns `IFccAdapter`. |
| **Impact** | On every adapter replacement: OkHttp thread pool leak (Radix), CIO server thread leak (Radix/Advatec), webhook listener port held (Advatec). Over multiple config changes, thread count grows indefinitely. |
| **Recommended Fix** | Add a `suspend fun close()` method to `IFccAdapter` with a default no-op implementation. All adapters implement cleanup in `close()`. The service calls `adapter.close()` before replacing with a new adapter. |

---

## AT-019: AdvatecAdapter Uses Blocking HttpURLConnection on Coroutine Thread

| Field | Value |
|-------|-------|
| **ID** | AT-019 |
| **Title** | submitCustomerData uses legacy blocking HTTP I/O instead of async Ktor/OkHttp client |
| **Module** | FCC Adapters (Advatec) |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Description** | `AdvatecAdapter.submitCustomerData()` and `AdvatecFiscalizationService.submitCustomerData()` both use `java.net.HttpURLConnection` (legacy blocking I/O) to POST Customer data to the Advatec device. This blocks the calling coroutine's thread for the entire HTTP round-trip (connect timeout 10s + read timeout 10s = up to 20s). Since `sendPreAuth()` is called from the Koin-managed `CoroutineScope` (which typically uses `Dispatchers.Default`), a blocking 20-second call can starve the limited thread pool (equal to CPU core count). All other adapters (Radix, Petronite) use the non-blocking Ktor `HttpClient` for FCC communication. |
| **Evidence** | `adapter/advatec/AdvatecAdapter.kt` lines 595–640: `URL(url).openConnection() as HttpURLConnection`. `adapter/advatec/AdvatecFiscalizationService.kt` lines 260–305: identical pattern. Compare with `adapter/radix/RadixAdapter.kt`: uses `httpClient.post(url)` (Ktor). |
| **Impact** | Thread starvation under concurrent load. On a 4-core device with `Dispatchers.Default`, two concurrent Advatec timeouts block 50% of the default thread pool for up to 20 seconds. |
| **Recommended Fix** | Replace `HttpURLConnection` with the Ktor `HttpClient` (consistent with Radix/Petronite), or wrap the blocking call in `withContext(Dispatchers.IO)` to prevent default thread pool starvation. |

---

## AT-020: JplTcpClient Read Loop Has O(n²) ByteArray Concatenation

| Field | Value |
|-------|-------|
| **ID** | AT-020 |
| **Title** | TCP read loop creates new ByteArray on every read via array concatenation |
| **Module** | FCC Adapters (DOMS) |
| **Severity** | Low |
| **Category** | Duplicated Logic / Inefficiency |
| **Description** | `JplTcpClient.readLoop()` accumulates TCP data using `accumulated = accumulated + buffer.copyOfRange(0, bytesRead)` (line 206). The `+` operator on `ByteArray` creates a new array and copies both operands on every TCP read. For the typical steady-state (heartbeat every 30s), the overhead is negligible. But during a burst fetch of 50+ transactions, the FCC sends a stream of frames in rapid succession. Each read creates a copy of the accumulated buffer (which grows with each iteration), resulting in O(n²) memory allocation where n is the total bytes received. |
| **Evidence** | `adapter/doms/jpl/JplTcpClient.kt` line 206: `accumulated = accumulated + buffer.copyOfRange(0, bytesRead)`. |
| **Impact** | Minor GC pressure during burst reads. For a typical fetch of 50 transactions (~100KB total), the quadratic allocation creates ~5MB of garbage. Acceptable on modern devices but wasteful. |
| **Recommended Fix** | Replace `ByteArray` concatenation with a `ByteArrayOutputStream` or ring buffer that appends without copying the existing content. |

---

## AT-021: Duplicated getCurrencyFactor Logic Across Advatec Modules

| Field | Value |
|-------|-------|
| **ID** | AT-021 |
| **Title** | AdvatecAdapter and AdvatecFiscalizationService have independently maintained currency factor methods |
| **Module** | FCC Adapters (Advatec) |
| **Severity** | Low |
| **Category** | Duplicated Logic |
| **Description** | Both `AdvatecAdapter.getCurrencyFactor()` (line 744) and `AdvatecFiscalizationService.getCurrencyFactor()` (line 309) contain identical currency-to-factor mapping logic as private methods. Additionally, `PetroniteAdapter.getCurrencyDecimals()` (line 947) has a more comprehensive mapping with 23 zero-decimal currencies vs. Advatec's 5. If a currency mapping is corrected in one location, the other may be missed. The Advatec implementations also treat TZS as a zero-decimal currency (factor = 1), which is correct per ISO 4217, but `PetroniteAdapter` uses factor 100 for unlisted currencies while Advatec uses factor 100 — so TZS handling diverges between the two. |
| **Evidence** | `adapter/advatec/AdvatecAdapter.kt` lines 744–750. `adapter/advatec/AdvatecFiscalizationService.kt` lines 309–315. `adapter/petronite/PetroniteAdapter.kt` lines 947–953. |
| **Impact** | Maintenance risk: currency mapping corrections must be applied to all three locations. Inconsistent TZS handling between Petronite and Advatec could cause amount discrepancies for Tanzanian deployments. |
| **Recommended Fix** | Extract a shared `CurrencyHelper` utility into `adapter/common/` with a single `getCurrencyFactor(code: String): BigDecimal` method. All adapters delegate to it. |

---

## AT-022: Radix ACK Response Not Verified in Pull-Mode Fetch — Silent Dequeue Failures

| Field | Value |
|-------|-------|
| **ID** | AT-022 |
| **Title** | Transaction ACK (CMD_CODE=201) response is sent but never checked for success |
| **Module** | FCC Adapters (Radix) |
| **Severity** | Medium |
| **Category** | Weak Error Handling |
| **Description** | In `RadixAdapter.fetchTransactionsPull()`, after successfully parsing a transaction, the ACK is sent at lines 587–591 but the response is discarded (not even read or logged). If the ACK fails (e.g., signature error because the shared secret was rotated mid-batch, or the FCC returns RESP_CODE=251), the transaction is NOT dequeued from the FCC FIFO. However, it was already added to `transactions` and will be buffered locally. On the next fetch cycle, the same transaction is returned again by the FCC (since ACK failed), creating a duplicate. The edge dedup layer (`TransactionBufferManager.findCrossAdapterDuplicate`) may not catch this if the raw dedup key changes between fetches. |
| **Evidence** | `adapter/radix/RadixAdapter.kt` lines 587–591: `httpClient.post(url) { ... setBody(ackBody) }` — response not inspected. |
| **Impact** | Undetected ACK failures cause duplicate transactions in the buffer. Each duplicate is uploaded to the cloud and synced to Odoo, creating double financial entries. |
| **Recommended Fix** | Read and validate the ACK response. If the ACK fails, either (a) remove the transaction from the local batch so it will be re-fetched next cycle, or (b) log a warning and continue (accepting the duplicate risk but surfacing it for operators). |

---

## AT-023: Fiscalization Retry Manually Reconstructs CanonicalTransaction — Fragile and Incomplete

| Field | Value |
|-------|-------|
| **ID** | AT-023 |
| **Title** | retryPendingFiscalization manually maps BufferedTransaction → CanonicalTransaction without a shared mapper |
| **Module** | Transaction Management |
| **Severity** | Medium |
| **Category** | Duplicated Logic |
| **Description** | `IngestionOrchestrator.retryPendingFiscalization()` manually reconstructs a `CanonicalTransaction` from a `BufferedTransaction` entity field-by-field (lines 415–438). This is the reverse of `TransactionBufferManager.toEntity()` which maps `CanonicalTransaction → BufferedTransaction`. Neither direction shares a mapper — both are inline manual mappings. This creates a maintenance burden: any new field added to `CanonicalTransaction` must be added in three places: (1) the entity class, (2) `toEntity()`, and (3) the reconstruction in `retryPendingFiscalization()`. The reconstruction also uses incorrect defaults for several fields: `legalEntityId = ""` (not stored in the buffer, will be wrong if the fiscalization service uses it), `isDuplicate = false` (always), and `paymentType = "CASH"` in the `FiscalizationContext` (see AF-026). The reconstruction excludes fields not present in the buffer (e.g., `preAuthToken`, `odooOrderId`) which may be needed by future fiscalization requirements. |
| **Evidence** | `ingestion/IngestionOrchestrator.kt` lines 415–438: manual field-by-field reconstruction. `buffer/TransactionBufferManager.kt` lines 266–296: `toEntity()` — the forward mapping. No `BufferedTransaction.toCanonical()` extension exists. |
| **Impact** | Adding a new field to `CanonicalTransaction` without updating the reconstruction in `retryPendingFiscalization()` will silently use a Kotlin default or zero value, potentially causing incorrect fiscal receipts. |
| **Recommended Fix** | Add a `BufferedTransaction.toCanonical()` extension method in `TransactionBufferManager` that serves as the single reverse mapping. Use it in `retryPendingFiscalization()` and any future code that needs to reconstruct a `CanonicalTransaction` from the buffer. |

---

## AT-024: doPoll Catches Exception Including CancellationException — Breaks Structured Concurrency

| Field | Value |
|-------|-------|
| **ID** | AT-024 |
| **Title** | Main poll loop swallows CancellationException — prevents proper coroutine cancellation during service shutdown |
| **Module** | Transaction Management |
| **Severity** | Medium |
| **Category** | Weak Error Handling |
| **Description** | `IngestionOrchestrator.doPoll()` wraps the main fetch loop in a `try { ... } catch (e: Exception)` block (lines 269–341). In Kotlin, `CancellationException` is a subclass of `IllegalStateException` which extends `Exception`, so this catch block intercepts coroutine cancellation. When the `EdgeAgentForegroundService` stops and cancels its scope, the `doPoll()` coroutine catches the `CancellationException`, logs it as an error ("FCC poll failed after N cycle(s)"), and returns a `PollResult` instead of propagating the cancellation. This breaks structured concurrency — the calling `poll()` method continues past the catch, executes `lastScheduledPollElapsedMs = SystemClock.elapsedRealtime()`, and returns normally. The parent scope does not see the cancellation. Notably, the fiscalization retry code below (lines 454–456) correctly handles this: `catch (e: kotlin.coroutines.cancellation.CancellationException) { throw e }`. This inconsistency means the poll loop and the fiscalization retry have different cancellation behaviors within the same orchestrator. |
| **Evidence** | `ingestion/IngestionOrchestrator.kt` lines 339–341: `catch (e: Exception) { AppLogger.e(TAG, "FCC poll failed after $fetchCycles cycle(s)", e) }` — swallows CancellationException. Lines 454–456: `catch (e: kotlin.coroutines.cancellation.CancellationException) { throw e }` — correctly re-throws. |
| **Impact** | During service shutdown, the poll may complete partially and write stale cursor state to the database instead of being cancelled cleanly. The false error log ("FCC poll failed") may confuse operators diagnosing shutdown behavior. |
| **Recommended Fix** | Add a CancellationException re-throw before the general Exception catch: `catch (e: CancellationException) { throw e } catch (e: Exception) { AppLogger.e(...) }`. This matches the pattern already used in `retryPendingFiscalization()`. |

---

## AT-025: getBufferStats Maps Unknown Sync Status Strings to SyncStatus.PENDING

| Field | Value |
|-------|-------|
| **ID** | AT-025 |
| **Title** | Unknown sync_status values from database are silently mapped to PENDING — inflates PENDING count in telemetry |
| **Module** | Transaction Management |
| **Severity** | Low |
| **Category** | Weak Error Handling |
| **Description** | `TransactionBufferManager.getBufferStats()` maps database `sync_status` strings to `SyncStatus` enum values using `SyncStatus.entries.firstOrNull { it.name == row.syncStatus } ?: SyncStatus.PENDING` (lines 218–220). If the database contains a status string that does not match any enum entry (e.g., from a future schema migration, manual database edit, or a typo in a raw SQL UPDATE), the unknown value is silently counted as `PENDING`. This inflates the PENDING count in telemetry reports, potentially triggering false alerts about upload backlog. The fallback also means that `DEAD_LETTER` records (if the enum name ever changes) could be misreported as PENDING, masking a dead-letter accumulation problem. |
| **Evidence** | `buffer/TransactionBufferManager.kt` lines 216–223: `SyncStatus.entries.firstOrNull { ... } ?: SyncStatus.PENDING`. |
| **Impact** | Minor: telemetry PENDING count may be inflated if unknown status strings exist. Operators may see false upload backlog warnings. |
| **Recommended Fix** | Log a warning when the fallback to PENDING is triggered: `AppLogger.w(TAG, "Unknown sync_status '${row.syncStatus}' mapped to PENDING")`. Consider using a dedicated `UNKNOWN` status or excluding unrecognized statuses from the count. |

---

## AT-034: HttpCloudApiClient Catches All Exception Including CancellationException — Breaks Structured Concurrency

| Field | Value |
|-------|-------|
| **ID** | AT-034 |
| **Title** | Every CloudApiClient method swallows CancellationException — prevents proper coroutine cancellation during service shutdown |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Medium |
| **Category** | Weak Error Handling |
| **Description** | All 9 suspend methods in `HttpCloudApiClient` (`uploadBatch`, `getSyncedStatus`, `getConfig`, `submitTelemetry`, `forwardPreAuth`, `registerDevice`, `refreshToken`, `checkVersion`, `submitDiagnosticLogs`) wrap their HTTP calls in `try { ... } catch (e: Exception) { ... TransportError(e.message) }`. In Kotlin, `CancellationException` is a subclass of `IllegalStateException` which extends `Exception`, so this catch block intercepts coroutine cancellation. When `EdgeAgentForegroundService` stops and cancels `serviceScope`, all in-flight HTTP calls are cancelled by Ktor's coroutine scope. The `CancellationException` is caught, wrapped in a `TransportError` result, and returned to the caller. The caller (`CloudUploadWorker`, `ConfigPollWorker`, etc.) processes this as a normal transport failure — recording circuit breaker failures, incrementing backoff counters, and potentially updating telemetry error counts — instead of propagating the cancellation. This is the same pattern identified in AT-024 for `IngestionOrchestrator.doPoll()`, but affects ALL cloud communication paths. No `CancellationException` handling exists anywhere in the `sync/` package — a grep for `CancellationException` in the sync directory returns zero results. |
| **Evidence** | `sync/CloudApiClient.kt` lines 292, 351, 428, 464, 509, 539, 568, 596: `catch (e: Exception)` in every method. Grep for `CancellationException` in `sync/`: 0 results. Compare with `ingestion/IngestionOrchestrator.kt` line 454: correct `CancellationException` handling. |
| **Impact** | During service shutdown: (a) circuit breaker failure counts are incremented by phantom "failures" that are actually cancellations; (b) false error logs ("Upload failed", "Telemetry submission failed") appear in diagnostics; (c) `SyncState` may be written after the service lifecycle ends (the cancelled coroutine completes its error-handling path including DB writes); (d) telemetry error counters are inflated by cancellation events. |
| **Recommended Fix** | Add `CancellationException` re-throw before the general `Exception` catch in each method: `catch (e: CancellationException) { throw e } catch (e: Exception) { ... TransportError(...) }`. Alternatively, create a shared inline function: `private inline fun <T> safeApiCall(block: () -> T, onError: (Exception) -> T): T = try { block() } catch (e: CancellationException) { throw e } catch (e: Exception) { onError(e) }`. |

---

## AT-035: CloudUploadWorker SyncState Updates Use Read-Modify-Write Without @Transaction — Concurrent Field Clobber Risk

| Field | Value |
|-------|-------|
| **ID** | AT-035 |
| **Title** | updateLastUploadAt and updateLastStatusPollAt read-modify-write the full SyncState row without Room @Transaction — concurrent workers may lose field updates |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Medium |
| **Category** | Weak Error Handling |
| **Description** | `CloudUploadWorker.updateLastUploadAt()` (lines 879–899) and `updateLastStatusPollAt()` (lines 545–564) both follow the pattern: `val current = dao.get(); val updated = current?.copy(field = value); dao.upsert(updated)`. This read-modify-write is NOT wrapped in a Room `@Transaction`. Between the `get()` and `upsert()`, another coroutine can modify `SyncState`: (a) `TelemetryReporter.nextSequenceNumber()` increments `telemetrySequence` inside a `@Transaction` — but the upload worker's subsequent `upsert()` writes the stale `telemetrySequence` value from its earlier `get()`, rolling back the increment. (b) `ConfigPollWorker.updateSyncState()` (same pattern, reported in AT-033) can update `lastConfigVersion` and `lastConfigPullAt` — if it runs between the upload worker's `get()` and `upsert()`, the config worker's changes are overwritten. (c) `IngestionOrchestrator.advanceCursor()` can update `lastFccCursor` (reported in AF-003). All three workers run on the shared Koin `CoroutineScope(Dispatchers.IO)` and are triggered from `CadenceController.runTick()` — while they execute sequentially within a single tick, connectivity recovery (`onTransition → FULLY_ONLINE`) triggers multiple workers concurrently at lines 350–355. |
| **Evidence** | `sync/CloudUploadWorker.kt` lines 879–899: `updateLastUploadAt()` — no `@Transaction`. Lines 545–564: `updateLastStatusPollAt()` — no `@Transaction`. `sync/TelemetryReporter.kt` lines 347–354: `nextSequenceNumber()` — uses `@Transaction` on `SyncStateDao`. `runtime/CadenceController.kt` lines 341–356: `onTransition(FULLY_ONLINE)` triggers upload, forward, status poll, and telemetry concurrently in a single `scope.launch`. |
| **Impact** | On connectivity recovery (the highest-risk window): `telemetrySequence` may be rolled back, causing duplicate sequence numbers in telemetry — the cloud dedup on `(deviceId, sequenceNumber)` will discard the duplicate, losing telemetry data. `lastConfigVersion` may be rolled back, causing the next config poll to re-fetch an already-applied config version. |
| **Recommended Fix** | Replace read-modify-write with atomic column-level UPDATE queries in `SyncStateDao`: `@Query("UPDATE sync_state SET last_upload_at = :now, updated_at = :now WHERE id = 1") suspend fun updateUploadAt(now: String)` and `@Query("UPDATE sync_state SET last_status_poll_at = :now, updated_at = :now WHERE id = 1") suspend fun updateStatusPollAt(now: String)`. Use `INSERT OR IGNORE` to create the initial row if needed. This eliminates the read step entirely and prevents field clobbering. |

---

## AT-036: TelemetryReporter Queries oldestPendingCreatedAt Twice Per Telemetry Cycle

| Field | Value |
|-------|-------|
| **ID** | AT-036 |
| **Title** | collectBufferStatus and collectSyncStatus both independently query transactionDao.oldestPendingCreatedAt() — redundant database round-trip |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Low |
| **Category** | Duplicated Logic |
| **Description** | `TelemetryReporter.buildPayload()` calls `collectBufferStatus()` (line 121) and `collectSyncStatus(cfg)` (line 122) sequentially. Both methods independently query `transactionDao.oldestPendingCreatedAt()`: `collectBufferStatus()` at line 250 stores the result in `oldestPendingAtUtc` for the `BufferStatusDto.oldestPendingAtUtc` field. `collectSyncStatus()` at line 290 queries the same value again to compute `syncLagSeconds` (lines 294–303). The two queries may return different values if a new PENDING record is inserted between them (unlikely but possible), creating an inconsistency where the buffer's `oldestPendingAtUtc` does not match the sync lag calculation. More importantly, this is an unnecessary database round-trip that adds latency to the telemetry payload assembly. |
| **Evidence** | `sync/TelemetryReporter.kt` line 250: `transactionDao.oldestPendingCreatedAt()` in `collectBufferStatus()`. Line 290: `transactionDao.oldestPendingCreatedAt()` in `collectSyncStatus()`. |
| **Impact** | Minor: one extra SQLite query per telemetry cycle (~1ms). Potential data inconsistency between buffer and sync sections of the telemetry payload. |
| **Recommended Fix** | Query `oldestPendingCreatedAt()` once in `buildPayload()` and pass the result to both `collectBufferStatus()` and `collectSyncStatus()` as a parameter. |

---

## AT-037: BufferStatusDto Telemetry Omits DEAD_LETTER and ARCHIVED Breakdown — Cloud Cannot Detect Permanent Data Loss

| Field | Value |
|-------|-------|
| **ID** | AT-037 |
| **Title** | Telemetry buffer stats only track PENDING, UPLOADED, SYNCED_TO_ODOO, and FAILED — DEAD_LETTER and ARCHIVED counts are invisible |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Medium |
| **Category** | Weak Error Handling |
| **Description** | `TelemetryReporter.collectBufferStatus()` queries `transactionDao.countByStatus()` which returns counts grouped by all sync_status values (including DEAD_LETTER and ARCHIVED). However, the method only extracts four specific statuses: `PENDING` (line 243), `UPLOADED` (line 244), `SYNCED_TO_ODOO` (line 245), and `FAILED` (line 246). The `totalRecords` sum (line 247) includes all statuses, so DEAD_LETTER and ARCHIVED records ARE counted in the total but are NOT broken out individually. The `BufferStatusDto` schema has `fiscalPendingCount` and `fiscalDeadLetterCount` fields for fiscal-specific statuses, but no `deadLetterCount` or `archivedCount` field for upload dead letters. DEAD_LETTER records represent transactions permanently lost from the sync pipeline (after 20 failed upload attempts, per GAP-1). The cloud has no way to detect this data loss through telemetry — the total records count includes dead letters, but they appear as part of the aggregate, indistinguishable from legitimate records. If 500 out of 1000 total records are DEAD_LETTER, the cloud sees `totalRecords=1000, pendingUploadCount=500` and assumes a large upload backlog, when in reality half the records are permanently unrecoverable. Additionally, the `FAILED` status (line 246) does not appear to be used by any DAO query or state machine — the actual failed-upload status is `DEAD_LETTER`, making `failedCount` always 0. |
| **Evidence** | `sync/TelemetryReporter.kt` lines 242–247: only PENDING, UPLOADED, SYNCED_TO_ODOO, FAILED extracted. `sync/CloudApiModels.kt` lines 238–249: `BufferStatusDto` — no `deadLetterCount` or `archivedCount` field. `buffer/dao/TransactionBufferDao.kt` lines 163–168: `countByStatus()` returns all statuses including DEAD_LETTER. |
| **Impact** | Cloud monitoring cannot detect permanent transaction data loss. DEAD_LETTER accumulation (indicating a systemic upload issue) is invisible in telemetry. Operators cannot distinguish between a healthy backlog and a failing pipeline. |
| **Recommended Fix** | Add `deadLetterCount` and `archivedCount` fields to `BufferStatusDto`. Extract them from the `countMap`: `deadLetterCount = countMap["DEAD_LETTER"] ?: 0, archivedCount = countMap["ARCHIVED"] ?: 0`. Remove or rename `failedCount` if it is not used by any status. Add a cloud-side alert rule: if `deadLetterCount > 0`, trigger an operator notification. |

---

## AT-038: CleanupWorker Not Wired to CadenceController Despite Class KDoc Claiming It Is

| Field | Value |
|-------|-------|
| **ID** | AT-038 |
| **Title** | CleanupWorker class documentation says "Invoked by CadenceController" but no wiring exists — code-doc divergence |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Description** | `CleanupWorker`'s class-level KDoc at `buffer/CleanupWorker.kt` lines 14–17 states: "Invoked by [com.fccmiddleware.edge.runtime.CadenceController] on a configurable interval (default: 24 h, from `buffer.cleanupIntervalHours` in site config)." This documentation is incorrect — `CadenceController` does not import, inject, or reference `CleanupWorker` anywhere. The class has a complete, well-tested implementation with retention-based cleanup, quota enforcement, disk space monitoring, and stale-UPLOADED reversion. The test file `CleanupWorkerTest.kt` exercises all code paths. But the runtime wiring that would connect this implementation to the cadence loop was never added. This is a code-documentation divergence that goes beyond a stale comment — it indicates a missing integration step in the development process. The `CadenceController` constructor accepts 11 nullable parameters for various workers (lines 49–68) but `CleanupWorker` is not among them. The `CadenceConfig` data class defines tick frequencies for sync, telemetry, and config poll, but not for cleanup. This suggests CleanupWorker integration was planned but not completed. |
| **Evidence** | `buffer/CleanupWorker.kt` lines 14–17: "Invoked by CadenceController on a configurable interval." `runtime/CadenceController.kt`: no `CleanupWorker` import or constructor parameter. `runtime/CadenceController.kt` lines 71–87: `CadenceConfig` — no `cleanupTickFrequency`. Functional impact reported separately as AF-034. |
| **Impact** | Developers reading the CleanupWorker documentation may assume cleanup is operational when it is not. The missing wiring means all data hygiene features are dead code. |
| **Recommended Fix** | Add `cleanupWorker: CleanupWorker? = null` to the `CadenceController` constructor and `cleanupTickFrequency: Int = 2880` (24h at 30s ticks) to `CadenceConfig`. Wire it in `AppModule.kt`. In `runTick()`, invoke cleanup on the appropriate tick modulus. Update the `computeTickModulus()` LCM calculation to include the cleanup frequency. |

---

## AT-039: DiagnosticsActivity.refreshData Launches Untracked Coroutines — Overlapping Refreshes Possible

| Field | Value |
|-------|-------|
| **ID** | AT-039 |
| **Title** | Auto-refresh loop fires refreshData() which launches a new untracked lifecycleScope coroutine each time — no cancellation of previous refresh |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Low |
| **Category** | Improper Flow/LiveData Usage |
| **Description** | `DiagnosticsActivity.startAutoRefresh()` launches a single coroutine that calls `delay(5000)` then `refreshData()` in a loop. However, `refreshData()` itself calls `lifecycleScope.launch { ... }` (line 122), spawning a NEW coroutine for each refresh. The auto-refresh loop does not wait for the previous `refreshData()` coroutine to complete — it just fires and forgets. If the IO-bound work inside `refreshData()` (9 DAO queries + file logger reads, per AP-001) takes longer than 5 seconds (possible under heavy disk I/O on Urovo i9100 or during SQLite lock contention), the next tick spawns a second concurrent refresh. Two or more refresh coroutines can then update the UI interleaved: coroutine A reads buffer=100 at t=0, coroutine B reads buffer=50 at t=5, coroutine B updates UI to "50", coroutine A finishes its IO and updates UI back to "100" (stale data). The `isFinishing || isDestroyed` guard (line 142) prevents writes to a dead Activity but does not prevent overlapping writes to a live one. |
| **Evidence** | `ui/DiagnosticsActivity.kt` line 101–106: `while (isActive) { delay(REFRESH_INTERVAL_MS); refreshData() }` — no await. Line 122: `lifecycleScope.launch { ... }` — new untracked coroutine per call. |
| **Impact** | Low: the IO block typically completes in <50ms, so overlap is rare. Under extreme disk contention (SQLite WAL checkpoint + integrity check + upload batch), concurrent refreshes could cause the UI to flicker between stale and current values. |
| **Recommended Fix** | Make `refreshData()` a `suspend fun` and call it directly from the auto-refresh loop instead of launching a new coroutine: `while (isActive) { delay(REFRESH_INTERVAL_MS); refreshData() }` where `refreshData()` uses `withContext(Dispatchers.IO)` internally and updates the UI on the caller's dispatcher. This ensures sequential execution. Alternatively, store the refresh `Job` and cancel it before launching a new one. |

---

## AT-040: StructuredFileLogger.getOrCreateWriter Force-Unwraps currentWriter — NPE When File Creation Fails

| Field | Value |
|-------|-------|
| **ID** | AT-040 |
| **Title** | getOrCreateWriter() returns currentWriter!! which throws NPE if the initial file creation fails |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Low |
| **Category** | Weak Error Handling |
| **Description** | `StructuredFileLogger.getOrCreateWriter()` at line 229 returns `currentWriter!!`. On the first invocation, `currentDate` is null, so the day-rollover branch executes: close old writer (null, no-op), create a new `BufferedWriter(FileWriter(file, true))`, assign to `currentWriter`. If the `FileWriter` constructor throws (e.g., disk full returns `IOException`, permissions error), `currentWriter` remains null (it was never assigned). The `currentWriter!!` then throws `NullPointerException`. This NPE is caught by the caller's `catch (e: Exception)` in `writeEntry()` (line 210), so it doesn't crash — but the NPE masks the real error (disk full). The catch block logs to Logcat: `"Failed to write log entry"` with the NPE, not the original IOException. Subsequent calls retry (`today != currentDate` is still true), but each retry throws the same masked NPE until disk space is freed. The `crash()` method at line 112 also calls `getOrCreateWriter()` — if the first-ever crash log hits a disk-full device, the NPE is caught by the outer try-catch (line 116) and the crash entry is silently lost. |
| **Evidence** | `logging/StructuredFileLogger.kt` line 229: `return currentWriter!!`. Lines 218–228: day-rollover branch where `BufferedWriter(FileWriter(file, true))` can throw before `currentWriter` is assigned. Lines 210–215: `catch (e: Exception)` catches the resulting NPE. Lines 111–118: `crash()` catch block silently swallows the NPE. |
| **Impact** | Low: disk-full conditions on the Urovo i9100 eMMC would cause all log writes to fail with a misleading NPE stack trace. The actual root cause (disk full) is not surfaced. Crash logs on a disk-full device are silently lost. |
| **Recommended Fix** | Replace the force-unwrap with a null-safe return and explicit error handling: `return currentWriter ?: throw IOException("Failed to create log writer for $today")`. This surfaces the real error. Alternatively, assign `currentWriter = null` explicitly before the `BufferedWriter` constructor and check for null: `val writer = currentWriter; if (writer == null) { Log.e(..., "No writer available"); return }`. |

---

## AT-041: PumpStatusCache.get() Catches All Exception Including CancellationException — Breaks Structured Concurrency

| Field | Value |
|-------|-------|
| **ID** | AT-041 |
| **Title** | PumpStatusCache live FCC fetch swallows CancellationException — same structured concurrency violation as AT-024 and AT-034 |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Medium |
| **Category** | Weak Error Handling |
| **Description** | `PumpStatusCache.get()` wraps the live FCC adapter call in `try { withTimeoutOrNull(liveTimeoutMs) { adapter.getPumpStatus() } } catch (_: Exception) { null }` (lines 108–112). The `catch (_: Exception)` intercepts `CancellationException` in addition to adapter errors. When `EdgeAgentForegroundService.onDestroy()` cancels `serviceScope`, any in-flight `getPumpStatus()` call receives a `CancellationException`. Instead of propagating the cancellation, the cache catches it, returns a stale fallback result, and the Ktor route handler sends an HTTP 200 response to the POS — even though the service is shutting down. This is the third instance of the CancellationException-swallowing pattern (after AT-024 in `IngestionOrchestrator.doPoll()` and AT-034 in `HttpCloudApiClient` methods). The `withTimeoutOrNull` correctly handles internal timeouts (returns null), but external cancellation should propagate. |
| **Evidence** | `api/PumpStatusRoutes.kt` lines 108–112: `catch (_: Exception) { null }` in `PumpStatusCache.get()`. Same pattern as `sync/CloudApiClient.kt` line 292 (AT-034) and `ingestion/IngestionOrchestrator.kt` line 339 (AT-024). |
| **Impact** | During service shutdown, the pump-status route handler completes instead of being cancelled, potentially writing a response to a closing Ktor server socket. Low practical impact since Ktor handles socket lifecycle independently, but the swallowed cancellation delays clean shutdown and may log false "stale fallback" messages. |
| **Recommended Fix** | Add `CancellationException` re-throw before the general `Exception` catch: `catch (e: CancellationException) { throw e } catch (_: Exception) { null }`. This is consistent with the fix applied to `IngestionOrchestrator.retryPendingFiscalization()` (lines 454–456) which already handles this correctly. Consider a codebase-wide audit for `catch (e: Exception)` in suspend functions — the AT-024/AT-034/AT-041 pattern appears to be systemic. |

---

## AT-042: IntegrityChecker KDoc Claims Startup Execution — Second Instance of "Built But Never Wired" Pattern

| Field | Value |
|-------|-------|
| **ID** | AT-042 |
| **Title** | IntegrityChecker follows the same dead-code wiring pattern as CleanupWorker (AT-038) — architectural gap in DI registration vs. runtime invocation |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Description** | `IntegrityChecker` exhibits the identical "registered-but-not-invoked" pattern as `CleanupWorker` (AT-038): (1) class is implemented with full functionality, (2) class KDoc documents when/how it should be called ("Runs `PRAGMA integrity_check` on startup"), (3) Koin DI registration exists (`AppModule.kt` line 149), (4) a test suite validates all code paths (`IntegrityCheckerTest.kt`), (5) but the runtime wiring that actually calls the class is missing. The `EdgeAgentForegroundService` does not inject or reference `IntegrityChecker`. Neither does `FccEdgeApplication`, `CadenceController`, or any other production class. This is the second confirmed instance of this pattern in the codebase. Combined with AT-038, it suggests a systematic gap: the development workflow includes implementation, testing, and DI registration, but does not verify that the registered component is actually invoked from the appropriate lifecycle entry point. There is no integration test or startup assertion that validates `runCheck()` was called. |
| **Evidence** | `buffer/IntegrityChecker.kt` lines 12–17: KDoc "Runs `PRAGMA integrity_check` on startup." `di/AppModule.kt` line 149: `single { IntegrityChecker(get(), get(), androidContext()) }`. `service/EdgeAgentForegroundService.kt`: 0 references. Grep for `IntegrityChecker` in production code (excluding `di/AppModule.kt` and the class itself): 0 results. Compare with AT-038: same pattern for `CleanupWorker`. |
| **Impact** | The codebase has a recurring architectural gap where critical infrastructure components are dead code. Developers and auditors reading the KDoc assume these components are active. The integrity check failure (AF-038) and cleanup failure (AF-034) compound: without cleanup, the database grows unbounded; without integrity checks, corruption in that growing database goes undetected. |
| **Recommended Fix** | Add a startup verification step to `EdgeAgentForegroundService.onStartCommand()` that explicitly calls all critical-path workers registered in DI. Consider adding a `@StartupRequired` annotation or an `ApplicationStartupVerifier` that asserts all startup-documented components were invoked within the first 30 seconds after service start. At minimum, add a checklist to the development workflow: "If a component's KDoc says 'called on startup' or 'invoked by X', verify the call site exists." |

---

## AT-043: OdooWsMessageHandler Bypasses TransactionBufferManager — Direct DAO Mutation Without Business Layer

| Field | Value |
|-------|-------|
| **ID** | AT-043 |
| **Title** | WebSocket message handler directly injects and mutates TransactionBufferDao, bypassing the TransactionBufferManager business layer |
| **Module** | POS Integration (Odoo) |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Description** | `OdooWsMessageHandler` receives `TransactionBufferDao` directly (line 33) and calls `updateOdooFields()`, `updateAddToCart()`, `markDiscarded()`, `getByFccTransactionId()`, `getUnsyncedForWs()`, and `getAllForWs()` without going through `TransactionBufferManager`. All other modules that access the transaction buffer — `IngestionOrchestrator`, `CloudUploadWorker`, `TelemetryReporter` — use `TransactionBufferManager` as the single point of access. This manager provides cross-adapter dedup, audit logging hooks, buffer statistics tracking, and a consistent API surface. The WebSocket handler bypasses all of these. Consequences: (1) When `markDiscarded()` is called via `manager_manual_update`, no audit log entry is written — all other state transitions that affect financial records are audited (e.g., `PreAuthHandler` writes audit entries for every pre-auth event). (2) When `updateOdooFields()` sets `odooOrderId`, `TransactionBufferManager` has no visibility into this change — its `getBufferStats()` method cannot distinguish between transactions with and without Odoo order references. (3) Future business rules added to the manager (e.g., validation that `odooOrderId` format matches a regex, or that `addToCart` cannot be set on DEAD_LETTER records) are automatically bypassed by the WebSocket path. |
| **Evidence** | `websocket/OdooWsMessageHandler.kt` line 33: `private val transactionDao: TransactionBufferDao` — direct DAO injection. `di/AppModule.kt` lines 308–311: `OdooWebSocketServer(transactionDao = get(), ...)` — DAO passed directly. `websocket/OdooWsMessageHandler.kt` lines 99, 110, 119, 150, 159, 168, 304: direct DAO calls. Compare with `ingestion/IngestionOrchestrator.kt`: uses `TransactionBufferManager.bufferTransaction()`. |
| **Impact** | Audit trail gaps for WebSocket-initiated state changes. Future business rules added to `TransactionBufferManager` are silently bypassed. Inconsistent data access patterns make the codebase harder to reason about. |
| **Recommended Fix** | Add WebSocket-specific methods to `TransactionBufferManager`: `updateOdooFields()`, `markDiscarded()`, `getForWs()`. These methods should delegate to the DAO but also perform audit logging and any applicable validation. Update `OdooWsMessageHandler` to accept `TransactionBufferManager` instead of `TransactionBufferDao`. |

---

## AT-044: pumpStatusBroadcastLoop Checks serviceScope.isActive Instead of Coroutine's Own Active State

| Field | Value |
|-------|-------|
| **ID** | AT-044 |
| **Title** | Broadcast loop condition checks parent scope activity, not the launched coroutine's cancellation — misleading control flow |
| **Module** | POS Integration (Odoo) |
| **Severity** | Low |
| **Category** | Improper Flow/LiveData Usage |
| **Description** | `pumpStatusBroadcastLoop()` at line 274 uses `while (serviceScope.isActive)` as its loop condition. This coroutine is launched per-client via `serviceScope.launch { pumpStatusBroadcastLoop(session) }` (line 184). When a client disconnects, `onClientDisconnected()` at line 205 calls `broadcastJob.cancel()`, which cancels the specific coroutine job — NOT the `serviceScope`. After cancellation, `serviceScope.isActive` remains `true` because the parent scope has other active children. The loop condition is always `true` from this coroutine's perspective. The actual termination happens through a different mechanism: `delay(intervalMs)` at line 275 throws `CancellationException`, which is caught by `catch (e: Exception)` at line 284, causing a `break`. This works correctly but relies on catching `CancellationException` as a general `Exception` — the same anti-pattern identified in AT-024, AT-034, and AT-041. The `break` terminates the loop, and the coroutine returns normally. The parent scope never sees the cancellation because the exception is swallowed. If the code between `while (serviceScope.isActive)` and `delay(intervalMs)` ever becomes non-trivial (e.g., adding a cache check that doesn't suspend), a cancelled coroutine could execute that code one extra time before the next `delay` throws. |
| **Evidence** | `websocket/OdooWebSocketServer.kt` line 274: `while (serviceScope.isActive)` — checks parent, not self. Line 184: `serviceScope.launch { pumpStatusBroadcastLoop(session) }` — per-client launch. Line 205: `broadcastJob.cancel()` — cancels child job, not parent scope. Lines 284–286: `catch (e: Exception) { ... break }` — CancellationException swallowed. |
| **Impact** | Low: the current behavior is functionally correct but the code reads misleadingly. A future maintainer may assume the `while` condition handles cancellation, when in reality it's the exception catch that does. |
| **Recommended Fix** | Replace `while (serviceScope.isActive)` with `while (true)` to make the termination mechanism explicit, or use `while (coroutineContext.isActive)` which checks the coroutine's own Job. Add a `CancellationException` re-throw before the general `Exception` catch to follow structured concurrency: `catch (e: CancellationException) { throw e } catch (e: Exception) { AppLogger.d(...); break }`. |

---

## AT-045: encodeToJsonElement Double-Serializes via String Round-Trip Instead of Using Standard Library

| Field | Value |
|-------|-------|
| **ID** | AT-045 |
| **Title** | Custom Json.encodeToJsonElement() extension serializes to String then re-parses, shadowing the efficient standard library function |
| **Module** | POS Integration (Odoo) |
| **Severity** | Low |
| **Category** | Duplicated Logic |
| **Description** | `OdooWsMessageHandler` defines a private extension function at lines 350–353: `private inline fun <reified T> Json.encodeToJsonElement(value: T): JsonElement { val jsonString = encodeToString(value); return parseToJsonElement(jsonString) }`. This performs a double-serialization: object → JSON String (allocation + UTF-8 encoding) → JsonElement (re-parsing the string). The kotlinx.serialization standard library provides `kotlinx.serialization.json.Json.encodeToJsonElement<T>(value)` which goes directly from object → JsonElement without the intermediate String step. The custom extension SHADOWS the standard library function by using the same name, making it indistinguishable at the call site. Every outbound message that embeds a DTO inside a response object goes through this double path: `handleLatest` (line 65), `handleManagerUpdate` (line 122), `handleAttendantUpdate` (lines 153, 171), and `handleFuelPumpStatus` (line 194 via `encodeToString`). The redundant string allocation and re-parsing adds memory pressure and CPU overhead proportional to message frequency. |
| **Evidence** | `websocket/OdooWsMessageHandler.kt` lines 350–353: custom `Json.encodeToJsonElement()` that calls `encodeToString()` then `parseToJsonElement()`. Compare with `kotlinx.serialization.json.Json.encodeToJsonElement()` from the standard library — same signature, different (efficient) implementation. |
| **Impact** | Every WebSocket response message incurs one unnecessary String allocation and one unnecessary JSON parse. For a forecourt with 5 terminals receiving pump status broadcasts every 3 seconds for 8 pumps, this is ~800 redundant serialization round-trips per minute. Individual overhead is <1ms each, total ~0.8s/minute of unnecessary CPU work. |
| **Recommended Fix** | Delete the custom extension function (lines 350–353). The standard `kotlinx.serialization.json.Json.encodeToJsonElement()` import from the library is already available and has the same signature. All call sites will transparently use the efficient standard implementation. Verify with a unit test that the output is identical. |

---

## AT-046: OdooWebSocketServer and OdooWsMessageHandler Lack Unit Tests for Message Handling Logic

| Field | Value |
|-------|-------|
| **ID** | AT-046 |
| **Title** | Only DTO serialization is tested — no unit tests exist for message routing, rate limiting, authentication, broadcast, or handler business logic |
| **Module** | POS Integration (Odoo) |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Description** | The POS Integration module has a single test file: `OdooWsModelsTest.kt` with 11 tests covering DTO serialization round-trips and field name compatibility. No tests exist for: (1) `OdooWebSocketServer` — connection authentication, rate limiting, max connections enforcement, reconfiguration, start/stop lifecycle. (2) `OdooWsMessageHandler` — message routing, `handleManagerUpdate` double-update logic, `handleAttendantUpdate` conditional broadcast, `handleFpUnblock` adapter interaction, error handling for invalid JSON. (3) `broadcastToAll` — dead session cleanup, concurrent modification during broadcast. (4) `toWsDto()` — monetary conversion correctness (the bug identified in AF-041 would have been caught by a test with a zero-decimal currency). The handler contains multiple complex behaviors: conditional broadcast logic in `handleAttendantUpdate`, rate limiting with sliding windows, and FCC adapter calls in `handleFpUnblock`. All are untested. Contrast with the pre-auth module (`PreAuthHandlerTest`, `PreAuthEdgeCaseTest` — 50+ test methods) and the adapter module (6 test classes totaling 100+ methods). The WebSocket module handles direct financial data mutations (setting `odooOrderId`, marking discards) with zero test coverage on the business logic. |
| **Evidence** | `websocket/OdooWsModelsTest.kt`: 11 tests, all DTO serialization. No `OdooWebSocketServerTest.kt` or `OdooWsMessageHandlerTest.kt` exists. Compare with `preauth/PreAuthHandlerTest.kt`: 30+ test methods. Compare with `adapter/radix/RadixAdapterTests.kt`: 25+ test methods. |
| **Impact** | Bugs in message routing, rate limiting, broadcast logic, and handler business logic (including AF-041, AF-042, AF-044, AF-045, AF-046, AF-047) were not caught during development. Future changes to the WebSocket protocol have no regression safety net. |
| **Recommended Fix** | Add `OdooWsMessageHandlerTest.kt` covering: (a) `handleManagerUpdate` — verify DAO calls and broadcast; (b) `handleAttendantUpdate` — verify single broadcast when both fields present (AF-042 regression); (c) `toWsDto()` — test with TZS (zero-decimal) currency to catch AF-041; (d) `handleFpUnblock` — verify adapter interaction and error response; (e) `handleAddTransaction` — verify response sent (AF-043). Add `OdooWebSocketServerTest.kt` covering: (a) authentication rejection without shared secret; (b) rate limiting enforcement; (c) max connections rejection. |

---

## AT-047: ConnectivityTransitionListener Interface Declared But Never Used — Dead Code

| Field | Value |
|-------|-------|
| **ID** | AT-047 |
| **Title** | ConnectivityTransitionListener fun interface is declared and wired into constructor but no consumer implements or registers it |
| **Module** | Connectivity |
| **Severity** | Low |
| **Category** | Duplicated Logic |
| **Description** | `ConnectivityManager.kt` declares a `fun interface ConnectivityTransitionListener` at lines 289–291 with a single method `onTransition(from, to)`. The `ConnectivityManager` constructor accepts an optional `listener` parameter (line 48) and calls `listener?.onTransition(prevState, newState)` inside `deriveAndEmitStateUnlocked` (line 216). However, the DI module passes `listener = null` (line 206 of `AppModule.kt`), and no other code path sets or provides a listener. `CadenceController` (the only consumer that reacts to connectivity transitions) explicitly documents at line 47 that it uses `StateFlow` observation only: "M-10: Observes [ConnectivityManager.state] StateFlow only — does NOT implement [ConnectivityTransitionListener]. The listener callback was redundant with the StateFlow collection and caused double-trigger on recovery transitions." The interface, the constructor parameter, and the conditional call are dead code. The listener call also executes inside the mutex (via `deriveAndEmitStateUnlocked` called from `processProbeResult` which holds `mutex.withLock`), which would be a deadlock risk if a listener implementation ever tried to call back into ConnectivityManager — but since the listener is always null, this is a latent rather than active risk. |
| **Evidence** | `connectivity/ConnectivityManager.kt` lines 289–291: `fun interface ConnectivityTransitionListener { fun onTransition(...) }`. Line 48: `private val listener: ConnectivityTransitionListener? = null`. Line 216: `listener?.onTransition(prevState, newState)`. `di/AppModule.kt` line 206: `listener = null`. `runtime/CadenceController.kt` line 47: "does NOT implement ConnectivityTransitionListener". |
| **Impact** | Dead code increases maintenance burden and misleads developers into thinking a listener-based notification pattern is active. The latent deadlock risk (listener called inside mutex) is a trap for future developers who might try to use the listener pattern without realizing it executes under lock. |
| **Recommended Fix** | Remove the `ConnectivityTransitionListener` interface, the `listener` constructor parameter, and the `listener?.onTransition()` call. All transition observation is correctly handled via `StateFlow` collection in `CadenceController.observeConnectivityTransitions()`. If a callback pattern is ever needed again, it should be invoked OUTSIDE the mutex to prevent deadlock. |

---

## AT-048: deriveAndEmitStateUnlocked Holds Mutex Across Suspend DAO Call — Blocks Concurrent Probe

| Field | Value |
|-------|-------|
| **ID** | AT-048 |
| **Title** | Connectivity state derivation holds the probe mutex while performing a Room DAO insert — concurrent probe result processing is blocked on disk I/O |
| **Module** | Connectivity |
| **Severity** | Medium |
| **Category** | Weak Error Handling |
| **Description** | `processProbeResult` acquires the mutex at line 141 (`mutex.withLock { ... }`) and, when a state change occurs, calls `deriveAndEmitStateUnlocked()` at line 179. Inside `deriveAndEmitStateUnlocked`, line 204 calls `auditLogDao.insert(AuditLog(...))` — a Room `@Insert` suspend function that performs disk I/O. The mutex is held for the entire duration of the DAO insert. Since both the internet and FCC probe loops call `processProbeResult` after each probe, the losing probe loop is blocked on the mutex while the winning loop's audit log insert completes. On the Urovo i9100 device with eMMC flash (typical write latency 5–50ms under load, spikes to 200ms during WAL checkpoint), the mutex hold time extends from <1ms (pure state derivation) to 5–200ms (including disk I/O). If both probes complete within a 200ms window (probability ~0.7% per cycle at 30s intervals), the second probe's result processing is delayed by the full insert duration. The method name `deriveAndEmitStateUnlocked` is also misleading — the "Unlocked" suffix conventionally means "call WITHOUT holding the lock", but the method is actually called WITH the lock held (the KDoc comment says "call with mutex held"). This naming inversion could mislead future developers. |
| **Evidence** | `connectivity/ConnectivityManager.kt` line 141: `mutex.withLock { ... }`. Line 179: `deriveAndEmitStateUnlocked()` called inside the lock. Line 204: `auditLogDao.insert(AuditLog(...))` — Room suspend function performing disk I/O while mutex is held. |
| **Impact** | When a connectivity state transition coincides with the other probe completing, the second probe's result processing is delayed by 5–200ms (disk I/O duration). Functional impact is minimal (probe results are not time-critical at 30s intervals), but the architectural pattern of holding a coroutine mutex across I/O is an anti-pattern that could become problematic if probe intervals are reduced or additional I/O is added inside the lock. |
| **Recommended Fix** | Move the audit log write and listener callback outside the mutex. Capture the `prevState` and `newState` inside the lock, release the lock, then perform the DAO insert and listener notification: `val (prev, new) = mutex.withLock { /* derive state, return pair */ }; if (new != null) { auditLogDao.insert(...); listener?.onTransition(prev, new) }`. Rename `deriveAndEmitStateUnlocked` to `deriveNewStateLocked` to reflect its actual calling convention. |

---

## AT-049: ConnectivityManager.stop() Does Not Reset State — Stale State Persists Across Restart Cycles

| Field | Value |
|-------|-------|
| **ID** | AT-049 |
| **Title** | stop() cancels probe jobs but does not reset state flows or counters — subsequent start() inherits stale state |
| **Module** | Connectivity |
| **Severity** | Low |
| **Category** | Weak Error Handling |
| **Description** | `ConnectivityManager.stop()` at lines 102–105 only cancels `probeJobs` and logs a message. It does NOT reset: `_state` (remains at whatever the last derived state was), `internetUp` / `fccUp` (remain true/false from the last probe), `internetConsecFailures` / `fccConsecFailures` / `internetConsecSuccesses` / `fccConsecSuccesses` (retain their last values), `lastInternetProbeMs` / `lastFccProbeMs` / `lastFccSuccessMs` (retain last timestamps). When `start()` is called again (which `EdgeAgentForegroundService` does on each `onStartCommand` via `CadenceController.start()` → connectivity start), the state machine resumes from the previous state rather than re-initializing to `FULLY_OFFLINE`. `ConnectivityManager` is a Koin `single` — it persists for the lifetime of the process. The `EdgeAgentForegroundService` uses `START_STICKY`, so Android may restart the service within the same process without recreating Koin singletons. In this scenario: (1) service runs, connectivity reaches `FULLY_ONLINE`; (2) `onDestroy()` calls `connectivityManager.stop()`; (3) network changes while probes are stopped; (4) `onStartCommand()` calls `connectivityManager.start()` — state is still `FULLY_ONLINE` from step 1 even though actual connectivity may have changed. The CadenceController would act on the stale state (e.g., attempting cloud uploads when internet is actually down) until the first probe completes and updates the state. |
| **Evidence** | `connectivity/ConnectivityManager.kt` lines 102–105: `fun stop() { probeJobs?.cancel() }` — no state reset. Line 64: `_state` is a `MutableStateFlow` that retains its last value. `di/AppModule.kt` line 167: `single { ConnectivityManager(...) }` — singleton survives service restart within same process. |
| **Impact** | After a service stop/start cycle within the same process, the connectivity state may be stale for up to 30s (until the first probe completes). During this window, `CadenceController` may attempt operations (cloud upload, FCC poll) against a network that is no longer available, wasting resources and generating error logs. Low severity because the probe self-corrects within one cycle. |
| **Recommended Fix** | Add state reset to `stop()`: `_state.value = ConnectivityState.FULLY_OFFLINE; internetUp = false; fccUp = false; internetConsecFailures = 0; fccConsecFailures = 0; internetConsecSuccesses = 0; fccConsecSuccesses = 0`. Alternatively, reset in `start()` before launching probe loops. This ensures every start begins with a clean `FULLY_OFFLINE` state per the spec: "Initialize in FULLY_OFFLINE on app start." |

---

## AT-050: NetworkBinder.started Flag Is Not Thread-Safe — Plain var Without Synchronization

| Field | Value |
|-------|-------|
| **ID** | AT-050 |
| **Title** | NetworkBinder.started guard uses a plain Boolean var — concurrent start/stop calls can bypass the duplicate-call protection |
| **Module** | Connectivity |
| **Severity** | Low |
| **Category** | Weak Error Handling |
| **Description** | `NetworkBinder.started` at line 45 is a plain `var started = false`. The `start()` method checks `if (started) return` (line 80) and sets `started = true` (line 96). The `stop()` method checks `if (!started) return` (line 108) and sets `started = false` (line 125). These reads and writes are not atomic — there is no `@Volatile` annotation, no `AtomicBoolean`, and no synchronization. If `start()` is called concurrently from two threads, both may read `started == false`, both proceed past the guard, and both register network callbacks — resulting in duplicate callback registrations. The `ConnectivityManager.registerNetworkCallback` documentation states that registering the same callback twice throws `IllegalArgumentException`, which would crash the service. In the current codebase, `start()` is called from `EdgeAgentForegroundService.onStartCommand()` which runs on the main thread, so concurrent calls are unlikely. However, `NetworkBinder` is a public class and its `start()`/`stop()` methods are public — future callers may invoke them from different threads. Compare with `EdgeAgentForegroundService.serviceStarted` which uses `AtomicBoolean` for the same duplicate-call guard pattern. |
| **Evidence** | `connectivity/NetworkBinder.kt` line 45: `private var started = false` — plain var. Line 80: `if (started) { ... return }` — non-atomic read. Line 96: `started = true` — non-atomic write. Compare with `service/EdgeAgentForegroundService.kt` line 71: `private val serviceStarted = AtomicBoolean(false)` — atomic guard for same pattern. |
| **Impact** | If `start()` is ever called concurrently (e.g., from a test or future refactor), duplicate `registerNetworkCallback` calls throw `IllegalArgumentException`, crashing the foreground service. Low severity because the current call site is main-thread-only. |
| **Recommended Fix** | Replace `private var started = false` with `private val started = AtomicBoolean(false)`. In `start()`: `if (!started.compareAndSet(false, true)) return`. In `stop()`: `if (!started.compareAndSet(true, false)) return`. This matches the pattern used by `EdgeAgentForegroundService.serviceStarted` and is safe for concurrent access. |

---

## AT-051: KeystoreManager.rotateKey() Is Implemented But Never Called — Key Rotation Is Dead Code

| Field | Value |
|-------|-------|
| **ID** | AT-051 |
| **Title** | KeystoreManager.rotateKey() exists with a complete implementation and test coverage but is never invoked from production code |
| **Module** | Security |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Description** | `KeystoreManager.rotateKey()` at lines 131–143 implements a full key rotation lifecycle: decrypt the current secret with the existing key, delete the old key, generate a new key, and re-encrypt the plaintext under the new key. The method has clear error handling (lines 133–135: decrypt failure, line 140: general exception). However, a search for `rotateKey` across all production Kotlin files returns only the definition itself — zero call sites. No periodic key rotation is scheduled in `CadenceController`, no config option triggers it, and no cloud-initiated key rotation command exists. The security spec for hardware-backed Keystore keys (§5.1) likely specifies periodic rotation to limit the exposure window if a key is compromised. Without rotation, the same AES-256-GCM keys (`fcc-middleware-device-jwt`, `fcc-middleware-refresh-token`, `fcc-middleware-fcc-cred`, `fcc-middleware-lan-key`, `fcc-middleware-config-integrity`) are used for the entire lifetime of the device registration — potentially months or years. This is the FOURTH instance of the "built, tested, but never invoked" pattern in the codebase, following CleanupWorker (AF-034/AT-038), IntegrityChecker (AF-038/AT-042), and SensitiveFieldFilter (AS-030). |
| **Evidence** | `security/KeystoreManager.kt` lines 131–143: `fun rotateKey(alias: String, currentEncrypted: ByteArray): ByteArray?` — complete implementation. Grep for `rotateKey` in production code (excluding `KeystoreManager.kt`): 0 results. `runtime/CadenceController.kt`: no reference to `KeystoreManager` or key rotation. `config/EdgeAgentConfigDto.kt`: no key rotation interval field. |
| **Impact** | Keystore AES-256-GCM keys are never rotated. If a key is compromised (e.g., through a side-channel attack on software-backed Keystore, or a vulnerability in the TEE firmware), all secrets encrypted under that key remain exposed until the device is re-provisioned. The implemented rotation mechanism — which correctly handles the decrypt-delete-reencrypt lifecycle — is wasted. |
| **Recommended Fix** | Add a key rotation tick to `CadenceController`: inject `KeystoreManager` and `EncryptedPrefsManager`, and on a configurable interval (e.g., every 30 days, checked once per 24h cleanup tick), rotate each alias. Add a `lastKeyRotationAt` field to `EncryptedPrefsManager` (or `SyncState`) to track when rotation was last performed. The rotation should be resilient: if any alias rotation fails, log the error and continue with the remaining aliases. Alternatively, add a cloud-initiated rotation command (via config update or telemetry response) that triggers on-demand rotation when the cloud detects a security event. |

---

## AT-052: SensitiveFieldFilter Claims "Reflection Results Are Cached Per Class" — No Cache Exists

| Field | Value |
|-------|-------|
| **ID** | AT-052 |
| **Title** | SensitiveFieldFilter class doc says "Reflection results are cached per class" but the implementation performs full reflection on every call |
| **Module** | Security |
| **Severity** | Low |
| **Category** | Duplicated Logic |
| **Description** | `SensitiveFieldFilter` at line 17 documents "Thread-safe. Reflection results are cached per class." However, the `redact()` method at lines 35–69 performs full Kotlin reflection on every invocation: `klass.memberProperties` iterates all properties (line 49), `prop.javaField?.isAnnotationPresent(Sensitive::class.java)` checks annotations (line 51), `klass.primaryConstructor?.parameters` inspects constructor params (lines 42–47), and `prop.isAccessible = true` sets accessibility (line 56). No `ConcurrentHashMap`, lazy property, or companion-level cache exists. Each `redact()` call independently resolves the class's sensitive properties via reflection. Since `SensitiveFieldFilter` is not currently used in production (see AS-030), this has no runtime impact. However, if the filter is wired into the logging pipeline as recommended, every log call for a `@Sensitive`-annotated object would incur the full reflection overhead: `klass.memberProperties` alone takes 0.5–2ms per call on ARM devices due to Kotlin reflection's metadata loading. For a hot path like `CloudUploadWorker.uploadPendingBatch()` (called every 30s), this would add measurable latency. |
| **Evidence** | `security/SensitiveFieldFilter.kt` line 17: "Reflection results are cached per class." Lines 35–69: no caching — full reflection on every `redact()` call. No `ConcurrentHashMap` or `by lazy` anywhere in the class. |
| **Impact** | Doc-code divergence. If the filter is ever wired into production logging, the uncached reflection would add 0.5–2ms per log call, creating a performance regression for high-frequency log paths. |
| **Recommended Fix** | Add a reflection cache: `private val sensitivePropsCache = ConcurrentHashMap<KClass<*>, Set<String>>()`. In `redact()`, compute and cache the set of sensitive property names per class on first access. Subsequent calls for the same class use the cached set, reducing reflection overhead to a hash lookup. Update the doc to accurately describe the caching behavior. |

---

## AT-053: EncryptedPrefsManager and LocalOverrideManager Duplicate MasterKey Construction — Identical Boilerplate in Two Locations

| Field | Value |
|-------|-------|
| **ID** | AT-053 |
| **Title** | Two security classes independently construct MasterKey with identical parameters — duplicated cryptographic initialization code |
| **Module** | Security / Site Configuration |
| **Severity** | Low |
| **Category** | Duplicated Logic |
| **Description** | Both `EncryptedPrefsManager` (lines 52–63) and `LocalOverrideManager` (lines 57–68) contain identical `MasterKey` construction code: `MasterKey.Builder(context).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build()` followed by `EncryptedSharedPreferences.create(context, PREFS_FILE, masterKey, PrefKeyEncryptionScheme.AES256_SIV, PrefValueEncryptionScheme.AES256_GCM)`. Both use the same key scheme, the same encryption schemes, and the same MasterKey alias (`_androidx_security_master_key_` — the AndroidX default). The `MasterKey.Builder.build()` method performs a Keystore lookup on each invocation (checking if the key already exists before generating). Since both classes are Koin singletons initialized during app startup, the two `MasterKey.Builder.build()` calls result in two sequential Keystore I/O operations for the same key alias. On the Urovo i9100, each Keystore lookup takes 10–50ms, adding 20–100ms of redundant startup latency. Additionally, if the MasterKey construction logic ever needs updating (e.g., migrating to `AES256_GCM` key scheme with `setRequestStrongBoxBacked(true)`), the change must be applied in both locations. |
| **Evidence** | `security/EncryptedPrefsManager.kt` lines 52–63: MasterKey + EncryptedSharedPreferences construction. `config/LocalOverrideManager.kt` lines 57–68: identical pattern. Both use `AES256_GCM` key scheme, `AES256_SIV` key encryption, `AES256_GCM` value encryption. |
| **Impact** | Redundant Keystore lookup on startup (10–50ms). Maintenance risk: cryptographic configuration changes must be applied to two locations. |
| **Recommended Fix** | Extract a shared `SecurePrefsFactory` utility in the `security/` package: `object SecurePrefsFactory { fun create(context: Context, fileName: String): SharedPreferences { val masterKey = MasterKey.Builder(context)...; return EncryptedSharedPreferences.create(context, fileName, masterKey, ...) } }`. Both `EncryptedPrefsManager` and `LocalOverrideManager` delegate to this factory. The `MasterKey` can be cached as a lazy property: `private val masterKey by lazy { MasterKey.Builder(...).build() }` — ensuring the Keystore lookup happens only once.
