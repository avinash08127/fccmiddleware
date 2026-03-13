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
| **Description** | `IngestionOrchestrator` accepts `adapter` and `config` as constructor parameters (lines 61, 68) but immediately assigns them to `@Volatile internal var` fields of the same name (lines 72, 76). These fields are then overwritten by `wireRuntime()`. The Koin DI module at `AppModule.kt` line 240 constructs `IngestionOrchestrator` with only `bufferManager` and `syncStateDao`, leaving `adapter` and `config` as null defaults. This dual-initialization pattern (constructor + late-binding) creates confusion about ownership and lifecycle — the constructor parameters are never used by the DI graph. |
| **Evidence** | `ingestion/IngestionOrchestrator.kt` lines 60–76; `di/AppModule.kt` lines 240–244. |
| **Impact** | Code maintainability issue. Future developers may pass non-null values via constructor, not realizing they'll be overwritten by `wireRuntime()`. |
| **Recommended Fix** | Remove `adapter` and `config` from the constructor. Make them private with `wireRuntime()` as the sole setter. |

---

## AT-002: DiagnosticsActivity Has Direct DAO Injection — Fat Activity Anti-Pattern

| Field | Value |
|-------|-------|
| **ID** | AT-002 |
| **Title** | DiagnosticsActivity directly injects 7 DAO/manager dependencies instead of using a ViewModel |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Medium |
| **Category** | Fat ViewModels / Business Logic in UI |
| **Description** | `DiagnosticsActivity` injects `ConnectivityManager`, `SiteDataDao`, `TransactionBufferDao`, `SyncStateDao`, `AuditLogDao`, `ConfigManager`, and `StructuredFileLogger` directly via Koin. All data fetching, transformation, and formatting logic lives in the Activity's `refreshData()` method (lines 116–248). This violates the separation of concerns — the Activity is simultaneously a view, a data accessor, and a presenter. On configuration changes (e.g., locale), the entire data pipeline re-executes from scratch. |
| **Evidence** | `ui/DiagnosticsActivity.kt` lines 46–52: 7 `by inject()` statements. Lines 116–248: data fetching + UI formatting in one method. |
| **Impact** | The Activity is tightly coupled to data access, making it untestable and fragile to refactoring. No way to unit test the diagnostics data pipeline without an Activity context. |
| **Recommended Fix** | Extract a `DiagnosticsViewModel` that exposes a `StateFlow<DiagnosticsSnapshot>`. The Activity should only observe and render. |

---

## AT-003: Duplicated CoroutineScope Definitions — Service and DI Module

| Field | Value |
|-------|-------|
| **ID** | AT-003 |
| **Title** | EdgeAgentForegroundService creates its own CoroutineScope separate from the Koin-managed scope |
| **Module** | Cross-Cutting Infrastructure |
| **Severity** | Medium |
| **Category** | Duplicated Logic |
| **Description** | `EdgeAgentForegroundService` creates a private `serviceScope` with `SupervisorJob() + Dispatchers.IO + CoroutineExceptionHandler` (lines 61–64). The Koin `AppModule` also creates a `single<CoroutineScope>` with the same configuration (lines 66–72). All workers and handlers injected into the service use the Koin scope, but the service's own monitoring coroutines (`monitorReprovisioningState`, `monitorDecommissionedState`, `observeConfigForRuntimeUpdates`) use `serviceScope`. These two scopes have different lifecycles — `serviceScope` is cancelled in `onDestroy()`, but the Koin scope lives until the process dies. This creates a subtle divergence where service-level coroutines may be cancelled while worker coroutines continue. |
| **Evidence** | `service/EdgeAgentForegroundService.kt` lines 61–64: `serviceScope`. `di/AppModule.kt` lines 66–72: `single<CoroutineScope>`. |
| **Impact** | After `onDestroy()`, Koin-scoped coroutines (PreAuthHandler audit logs, CadenceController) may continue executing while the service thinks it has stopped. This can cause database writes after the service lifecycle ends. |
| **Recommended Fix** | Either inject the Koin scope into the service and cancel it in `onDestroy()`, or ensure all coroutines that should follow the service lifecycle use `serviceScope`. Consider making the Koin scope a child of `serviceScope`. |

---

## AT-004: StructuredFileLogger Gets Its Own CoroutineScope — Third Scope

| Field | Value |
|-------|-------|
| **ID** | AT-004 |
| **Title** | StructuredFileLogger creates a third independent CoroutineScope |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Low |
| **Category** | Duplicated Logic |
| **Description** | `StructuredFileLogger` is constructed in `AppModule.kt` line 56 with `CoroutineScope(SupervisorJob() + Dispatchers.IO)`. This is a third scope alongside `serviceScope` and the Koin `single<CoroutineScope>`. The logger's scope is never cancelled — it lives until process death. |
| **Evidence** | `di/AppModule.kt` lines 56–59: standalone `CoroutineScope` for logger. |
| **Impact** | The logger scope cannot be cancelled for testing and may leak in instrumented tests. Minor in production since the logger should live for the process lifetime. |
| **Recommended Fix** | Use a child scope of the Koin-managed scope, or explicitly cancel it when appropriate. |

---

## AT-005: PreAuthHandler Uses Unstructured scope.launch for Audit Logging

| Field | Value |
|-------|-------|
| **ID** | AT-005 |
| **Title** | Audit log inserts fire-and-forget with no error propagation |
| **Module** | Pre-Authorization |
| **Severity** | Low |
| **Category** | Weak Error Handling |
| **Description** | `PreAuthHandler` uses `scope.launch { auditLogDao.insert(...) }` in multiple places (lines 267–278, 343–352, 410–419, 434–443) for audit logging. These launches are fire-and-forget — if the insert fails, the exception is caught by the Koin scope's `CoroutineExceptionHandler` and logged, but the audit record is silently lost. For a financial application, audit trail completeness is important. |
| **Evidence** | `preauth/PreAuthHandler.kt` lines 267–278: `scope.launch { auditLogDao.insert(...) }` |
| **Impact** | Audit trail gaps for pre-auth events. Financial regulators may require complete audit trails. |
| **Recommended Fix** | Add try/catch inside the `scope.launch` block with a fallback write to the file logger. Consider making audit logging synchronous for critical events. |

---

## AT-006: EdgeAgentForegroundService.onDestroy Does Not Stop FCC Adapter Connections

| Field | Value |
|-------|-------|
| **ID** | AT-006 |
| **Title** | Service onDestroy does not disconnect FCC adapter TCP/HTTP connections |
| **Module** | FCC Adapters / Service Lifecycle |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Description** | `EdgeAgentForegroundService.onDestroy()` stops the cadence controller, connectivity manager, network binder, local API, and WebSocket server, but does not explicitly disconnect the FCC adapter. For DOMS (persistent TCP), this leaves a TCP connection open until the OS closes it. For Radix and Advatec push listeners (Ktor CIO servers), these embedded servers are not stopped. The `serviceScope.cancel()` at line 169 cancels coroutines but does not close sockets or servers that own their own event loops. |
| **Evidence** | `service/EdgeAgentForegroundService.kt` lines 162–171: no adapter cleanup. |
| **Impact** | Resource leaks: TCP connections and embedded Ktor servers may persist after service destruction. The DOMS heartbeat manager and JPL TCP client may hold sockets open. |
| **Recommended Fix** | Add `fccRuntimeState.adapter?.let { if (it is IFccConnectionLifecycle) it.disconnect() }` in `onDestroy()`. Also stop `RadixPushListener` and `AdvatecWebhookListener` servers. |

---

## AT-007: CircuitBreaker State Accessed Without Synchronization in Some Paths

| Field | Value |
|-------|-------|
| **ID** | AT-007 |
| **Title** | CircuitBreaker convenience aliases bypass Mutex synchronization |
| **Module** | Cloud Sync |
| **Severity** | Low |
| **Category** | Weak Error Handling |
| **Description** | `CloudUploadWorker` exposes `consecutiveFailureCount` and `nextRetryAt` as convenience getters that read directly from `CircuitBreaker` fields (lines 104–105, 179–180). The `CircuitBreaker` class uses a `Mutex` for state transitions, but these read-only accessors likely bypass it (reading `@Volatile` fields without the mutex). While individually safe for reads, combined use in log messages (lines 699, 536) creates a TOCTOU window where `consecutiveFailureCount` and `state` may represent different snapshots. |
| **Evidence** | `sync/CloudUploadWorker.kt` lines 104–105: `internal val consecutiveFailureCount: Int get() = uploadCircuitBreaker.consecutiveFailureCount`. |
| **Impact** | Minor: log messages may show inconsistent circuit breaker state. No functional impact. |
| **Recommended Fix** | Accept the minor inconsistency for logging (it's diagnostics-only), or provide a `CircuitBreaker.snapshot()` method that atomically reads all state under the mutex. |

---

## AT-008: SettingsActivity Performs SharedPreferences I/O on Main Thread

| Field | Value |
|-------|-------|
| **ID** | AT-008 |
| **Title** | saveAndReconnect() calls LocalOverrideManager (SharedPreferences) on UI thread |
| **Module** | Site Configuration |
| **Severity** | Medium |
| **Category** | Architecture Violations |
| **Description** | `SettingsActivity.saveAndReconnect()` calls `localOverrideManager.saveOverride()` and `localOverrideManager.clearOverride()` directly on the main thread (lines 236–264). `LocalOverrideManager` uses `EncryptedSharedPreferences` which performs AES encryption and file I/O synchronously. On slow storage or under memory pressure, this can cause ANRs (Application Not Responding). The `populateFields()` call at line 274 also reads from `ConfigManager.config` and `EncryptedPrefsManager` on the main thread. |
| **Evidence** | `ui/SettingsActivity.kt` lines 188–283: entire `saveAndReconnect()` runs on UI thread. |
| **Impact** | Potential ANR on devices with slow storage. EncryptedSharedPreferences is known to be slow (>100ms on some devices). |
| **Recommended Fix** | Move the save operations to a coroutine on `Dispatchers.IO`, then update the UI on the main thread. |

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

---

## AT-013: CloudUploadWorker Constructor Has Too Many Nullable Parameters

| Field | Value |
|-------|-------|
| **ID** | AT-013 |
| **Title** | All 8 constructor parameters of CloudUploadWorker are nullable — API surface allows fully uninitialized instances |
| **Module** | Cloud Sync |
| **Severity** | Low |
| **Category** | Architecture Violations |
| **Description** | `CloudUploadWorker` accepts 8 nullable parameters (lines 64–73) including `bufferManager`, `cloudApiClient`, and `tokenProvider`. Every public method starts with 3 null-check guards. This "nullable-everything" pattern exists because the worker is registered in Koin before security modules are wired. However, the Koin module actually provides all dependencies as non-null singletons — the nullability is legacy from an earlier design phase. |
| **Evidence** | `sync/CloudUploadWorker.kt` lines 64–73: all parameters nullable. Lines 118–129: three null-check returns per method. |
| **Impact** | Code readability and maintainability. Each method has 6 lines of boilerplate null-checks before doing real work. |
| **Recommended Fix** | Make the core dependencies non-nullable since Koin provides them. Use `get()` not `getOrNull()` in the Koin module. Keep only `telemetryReporter` and `fileLogger` nullable if they truly are optional. |

---

## AT-014: ProvisioningViewModel Contains Inline Credential Storage and Config Encryption Logic

| Field | Value |
|-------|-------|
| **ID** | AT-014 |
| **Title** | handleRegistrationSuccess() mixes ViewModel coordination with security-critical business logic |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Medium |
| **Category** | Fat ViewModels / Business Logic in UI |
| **Description** | `ProvisioningViewModel.handleRegistrationSuccess()` (lines 122–223) performs 8 distinct responsibilities: (1) clear stale Keystore keys, (2) parse siteConfig JSON, (3) update CloudApiClient base URL, (4) encrypt and store tokens via KeystoreManager, (5) persist registration identity via EncryptedPrefsManager, (6) encrypt config with Keystore and encode to Base64, (7) store config in Room with write-verify retry, (8) sync site data. This is security-critical business logic (credential lifecycle management) embedded in a ViewModel. It is untestable without mocking 7 dependencies, and cannot be reused if a headless re-registration path is needed (e.g., automated provisioning via MDM). The desktop edge agent has an equivalent `RegistrationHandler` class for this logic. |
| **Evidence** | `ui/ProvisioningViewModel.kt` lines 122–223: `handleRegistrationSuccess()` with 8 responsibilities. Constructor injects 7 dependencies (lines 43–49). |
| **Impact** | The registration logic cannot be unit-tested without an Android Instrumentation test (AndroidViewModel requires Application context). Any future provisioning path (MDM push, API-triggered) must duplicate this logic. |
| **Recommended Fix** | Extract a `RegistrationHandler` class that owns the credential storage, config encryption, and Room persistence pipeline. The ViewModel should only call `registrationHandler.completeRegistration(qrData, result)` and observe the outcome. |

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
