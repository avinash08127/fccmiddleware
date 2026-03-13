# Android Functional Findings — FCC Edge Agent

**Module**: FCC Edge Agent (Android)
**Audit date**: 2026-03-13
**Scope**: End-to-end trace — UI → State → Workers → Adapters → DB/Network

---

## AF-001: ProvisioningActivity Token Saved in Instance State (Plaintext Leak via Bundle)

| Field | Value |
|-------|-------|
| **ID** | AF-001 |
| **Title** | Provisioning token persisted in plaintext via onSaveInstanceState |
| **Module** | Provisioning & Lifecycle |
| **Severity** | High |
| **Category** | Incorrect State Updates |
| **Description** | `ProvisioningActivity.onSaveInstanceState()` saves the provisioning token (`tokenInput.text`) into the `Bundle` under key `state_token`. Android Bundles are serialized to the Binder transaction buffer and can survive process death in the system-managed saved-state file. This means a one-time bootstrap token — which grants device registration rights — is written to persistent storage in plaintext. If the device is compromised or the state file is read by another app with root access, the token is exposed. |
| **Evidence** | `ui/ProvisioningActivity.kt` lines 175–177: `outState.putString(STATE_TOKEN, tokenInput.text.toString())` |
| **Impact** | One-time provisioning token could be exfiltrated from the saved-state file, potentially allowing unauthorized device registration. |
| **Recommended Fix** | Do not persist the token in `onSaveInstanceState`. Clear the `tokenInput` field if the process is being killed, or encrypt the token before saving it. Alternatively, accept that token re-entry is required after process death. |
| **Status** | **Fixed** — Removed `STATE_TOKEN` from `onSaveInstanceState()` and `onCreate()` restoration. The provisioning token is no longer persisted in the Bundle; users must re-enter it after process death. |

---

## AF-002: Correlation ID Stored on Static Thread-Local — Leaks Across Requests

| Field | Value |
|-------|-------|
| **ID** | AF-002 |
| **Title** | AppLogger.correlationId is set globally — correlation IDs leak between concurrent local API requests |
| **Module** | Transaction Management / Local API |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Description** | `LocalApiServer.configureCorrelationId()` sets `AppLogger.correlationId` as a global static/companion property during the Ktor `Setup` phase. When multiple concurrent HTTP requests arrive (e.g., POS transaction query + pre-auth), the second request overwrites the correlation ID set by the first. All log lines emitted by the first request's handlers after that point carry the wrong correlation ID, making distributed tracing unreliable. |
| **Evidence** | `api/LocalApiServer.kt` lines 172–177: `AppLogger.correlationId = correlationId` (global assignment in a concurrent server) |
| **Impact** | Log correlation is broken under concurrent load; debugging production issues across POS and cloud becomes significantly harder. |
| **Recommended Fix** | Use a coroutine-local `CoroutineContext` element (e.g., `ThreadContextElement` or Ktor's `CallId` plugin) to scope the correlation ID to each request's coroutine. |
| **Status** | **Fixed** — Replaced global `correlationId` property with a `ThreadLocal`-backed implementation. Added `CorrelationIdElement` (a `ThreadContextElement`) to scope each Ktor request's correlation ID to its coroutine. The interceptor now uses `withContext(CorrelationIdElement(correlationId))` instead of global assignment. |

---

## AF-003: IngestionOrchestrator Uses Stale SyncState for Multi-Batch Cursor Advance

| Field | Value |
|-------|-------|
| **ID** | AF-003 |
| **Title** | Cursor advance uses initial SyncState snapshot for all iterations in a poll cycle |
| **Module** | Transaction Management |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Description** | In `IngestionOrchestrator.doPoll()`, `initialSyncState` is read once at line 246. The `advanceCursor()` helper at line 510 calls `current?.copy(lastFccCursor = newCursorValue)` where `current` is always this initial snapshot. In a multi-batch poll cycle (up to 10 iterations), every cursor write overwrites the previous cursor with the initial snapshot's fields (e.g., `lastUploadAt`, `lastStatusPollAt`). If `CloudUploadWorker` updated `lastUploadAt` between iterations, that update is silently rolled back. |
| **Evidence** | `ingestion/IngestionOrchestrator.kt` lines 246, 300–301, 504–522: `advanceCursor(dao, initialSyncState, newCursorValue)` always uses the snapshot from line 246. |
| **Impact** | SyncState fields set by other workers may be silently reverted, causing repeated uploads or missed status polls. The risk increases when the FCC has a deep backlog (multiple fetch cycles per poll). |
| **Recommended Fix** | Re-read the current `SyncState` from the DAO inside `advanceCursor()` rather than using the stale initial snapshot, or use an atomic `UPDATE ... SET lastFccCursor = ? WHERE id = 1` query that does not touch other columns. |
| **Status** | **Fixed** — `advanceCursor()` now re-reads the current `SyncState` from the DAO on each call instead of using the stale `initialSyncState` snapshot. The `current` parameter was removed; only `dao` and `newCursorValue` are passed. Other fields (e.g., `lastUploadAt`) are no longer silently rolled back during multi-batch poll cycles. |

---

## AF-004: DecommissionedActivity Clears Credentials But Does Not Stop Running Service

| Field | Value |
|-------|-------|
| **ID** | AF-004 |
| **Title** | Re-provisioning from DecommissionedActivity does not stop EdgeAgentForegroundService |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Medium |
| **Category** | Broken Workflows |
| **Description** | `DecommissionedActivity.startReProvisioning()` calls `keystoreManager.clearAll()` and `encryptedPrefs.clearAll()` then navigates to `ProvisioningActivity`. However, `EdgeAgentForegroundService` may still be running (it was stopped by `monitorDecommissionedState()` but START_STICKY could restart it). If the service restarts between credential clearing and re-provisioning completion, it will read cleared/null credentials and crash or enter an undefined state. |
| **Evidence** | `ui/DecommissionedActivity.kt` lines 60–67: no `stopService()` call before clearing credentials. |
| **Impact** | Race condition between service restart and credential clearing could cause crashes or undefined behavior during re-provisioning. |
| **Recommended Fix** | Explicitly stop `EdgeAgentForegroundService` in `startReProvisioning()` before clearing credentials. Consider also setting a `reprovisioningInProgress` flag in `EncryptedPrefsManager` that the service checks on restart. |
| **Status** | **Fixed** — Added `stopService(Intent(this, EdgeAgentForegroundService::class.java))` call before `keystoreManager.clearAll()` and `encryptedPrefs.clearAll()` in `startReProvisioning()`. This prevents START_STICKY from restarting the service with cleared credentials. |

---

## AF-005: Pre-Auth Expiry Check Can Create Zombie Authorized Pumps on Adapter Disconnect

| Field | Value |
|-------|-------|
| **ID** | AF-005 |
| **Title** | Pre-auth expiry deauth failure leaves AUTHORIZED records indefinitely when adapter reconnects to a different session |
| **Module** | Pre-Authorization |
| **Severity** | Medium |
| **Category** | Broken Workflows |
| **Description** | In `PreAuthHandler.runExpiryCheck()`, when FCC deauth fails for an AUTHORIZED record, the record is left as AUTHORIZED so the next cadence tick retries. However, if the FCC adapter was reconnected to a new TCP session (e.g., after a DOMS reconnect), the FCC may no longer have the pre-auth in its session. The deauth will keep failing, and the record will never transition to EXPIRED, creating a permanently stuck AUTHORIZED record in the database. There is no maximum retry count for deauth attempts. |
| **Evidence** | `preauth/PreAuthHandler.kt` lines 386–421: infinite deauth retry loop with no attempt counter. |
| **Impact** | Stuck AUTHORIZED records consume pre-auth slots and are never cleaned up, potentially blocking future pre-auths for the same pump. |
| **Recommended Fix** | Add a maximum deauth retry count (e.g., 5). After exhausting retries, force-expire the record with a diagnostic message indicating deauth was not confirmed. The FCC's own TTL will expire the pre-auth naturally. |
| **Status** | **Fixed** — Added `MAX_DEAUTH_RETRIES = 5` constant and an in-memory `ConcurrentHashMap<String, Int>` counter in `PreAuthHandler`. `runExpiryCheck()` now tracks deauth attempts per record ID. After 5 consecutive failures, the record is force-expired with a `PRE_AUTH_DEAUTH_EXHAUSTED` audit log entry and a diagnostic failure reason. The counter is cleaned up on success or exhaustion. |

---

## AF-006: Manual FCC Pull Does Not Return Pump-Filtered Results

| Field | Value |
|-------|-------|
| **ID** | AF-006 |
| **Title** | POST /api/v1/transactions/pull pumpNumber parameter is logged but not used for filtering |
| **Module** | Transaction Management |
| **Severity** | Low |
| **Category** | UI Not Reflecting Backend State |
| **Description** | `IngestionOrchestrator.pollNow(pumpNumber)` accepts a `pumpNumber` parameter, but the adapter's `fetchTransactions()` returns all transactions since the last cursor regardless of pump. The `pumpNumber` is only logged. POS calling `POST /transactions/pull` with a pump filter expects only that pump's transactions to be returned, but the API returns the full batch. |
| **Evidence** | `ingestion/IngestionOrchestrator.kt` lines 218–231: `pumpNumber` passed to `pollNow` is only used in log message. |
| **Impact** | Minor: POS receives transactions for all pumps when it requested a specific one. Not a data loss issue since the GET endpoint filters correctly. |
| **Recommended Fix** | Document that `pumpNumber` is informational in the pull endpoint. Alternatively, filter the `PollResult.newCount` by pump number so the response indicates how many relevant transactions were found. |
| **Status** | **Fixed** — Added `pumpMatchCount: Int?` field to `PollResult` and `ManualPullResponse`. When `pumpNumber` is provided, `doPoll()` counts newly buffered transactions matching that pump. The response now includes `pumpMatchCount` so POS knows how many relevant transactions were found, while still buffering all pumps' data to prevent data loss. |

---

## AF-007: Settings Save Does Not Validate Conflicting Port Assignments

| Field | Value |
|-------|-------|
| **ID** | AF-007 |
| **Title** | SettingsActivity allows saving duplicate port numbers across FCC Port, JPL Port, and WebSocket Port |
| **Module** | Site Configuration |
| **Severity** | Low |
| **Category** | Inconsistent Form Handling |
| **Description** | `SettingsActivity.saveAndReconnect()` validates each port individually (range 1–65535) but does not check for conflicts between FCC Port, FCC JPL Port, and WebSocket Port. A technician could set all three to the same port number, causing bind failures at runtime. |
| **Evidence** | `ui/SettingsActivity.kt` lines 194–225: each port validated independently, no cross-field check. |
| **Impact** | Runtime bind failures with confusing error messages when duplicate ports are used. Requires manual diagnosis. |
| **Recommended Fix** | Add a cross-field validation step that rejects duplicate port assignments across the three port fields. |
| **Status** | **Fixed** — Added cross-field port conflict validation in `SettingsActivity.saveAndReconnect()`. After individual port range checks, all non-empty port values are collected and checked for duplicates. Conflicting ports produce a clear error message (e.g., "FCC JPL Port conflicts with FCC Port (both use port 10001)"). |

---

## AF-008: CloudUploadWorker buildUploadRequest Uses Empty legalEntityId

| Field | Value |
|-------|-------|
| **ID** | AF-008 |
| **Title** | Upload request sends empty legalEntityId when JWT claim is unavailable, causing cloud rejection loop |
| **Module** | Cloud Sync |
| **Severity** | Medium |
| **Category** | Broken Workflows |
| **Description** | `CloudUploadWorker.buildUploadRequest()` gets `legalEntityId` from `provider.getLegalEntityId()` which may return null (e.g., JWT not yet decoded, or claim missing). When null, an empty string is used. The cloud API will reject every transaction in the batch with a validation error, incrementing `uploadAttempts` on all records. After 20 failures, all records are dead-lettered — permanently lost from the sync pipeline. |
| **Evidence** | `sync/CloudUploadWorker.kt` line 830: `val legalEntityId = provider.getLegalEntityId() ?: ""`. |
| **Impact** | All transactions buffered before the JWT is decoded (or if the `lei` claim is missing) will be dead-lettered after 20 failed upload attempts. |
| **Recommended Fix** | Skip the upload batch when `legalEntityId` is null or empty, logging a warning. Do not increment `uploadAttempts` for configuration-level errors. |
| **Status** | **Fixed** — `uploadPendingBatch()` now checks `provider.getLegalEntityId()` before calling `doUpload()`. When null or empty, the batch is skipped with a warning log and records remain PENDING without incrementing `uploadAttempts`. This prevents the dead-letter spiral when JWT claims are not yet available. |

---

## AF-009: Config Hot-Reload Detects Restart-Required Changes But Never Restarts

| Field | Value |
|-------|-------|
| **ID** | AF-009 |
| **Title** | detectRestartRequiredChanges logs warnings but changes are silently applied as hot-reload |
| **Module** | Site Configuration |
| **Severity** | Low |
| **Category** | UI Not Reflecting Backend State |
| **Description** | `ConfigManager.applyConfig()` calls `detectRestartRequiredChanges()` which identifies fields like `fcc.vendor`, `fcc.hostAddress`, and `sync.cloudBaseUrl` as requiring a restart. However, the config is still written to `_config.value` and the `EdgeAgentForegroundService.observeConfigForRuntimeUpdates()` collector calls `applyRuntimeConfig()` which fully applies these changes (rebuilds the adapter, updates the cloud URL, etc.). The "restart required" warning is misleading since the changes ARE hot-applied. |
| **Evidence** | `config/ConfigManager.kt` lines 147–155: warning logged but config still applied at line 175. `service/EdgeAgentForegroundService.kt` lines 176–181: collector applies all config changes. |
| **Impact** | Misleading log messages for operators. No actual functional issue since changes are applied correctly. |
| **Recommended Fix** | Either remove the restart-required warning for fields that are actually hot-reloaded, or split the classification to only warn for fields that truly cannot be hot-reloaded. |
| **Status** | **Fixed** — Renamed `detectRestartRequiredChanges()` to `detectSignificantFieldChanges()` and changed the misleading WARN-level "restart needed" log to an INFO-level "hot-applied" log. All listed fields (fcc.vendor, fcc.hostAddress, sync.cloudBaseUrl, etc.) are fully hot-reloaded by the service's `applyRuntimeConfig()`. |

---

## AF-010: DiagnosticsActivity Shows Stale Data for First 5 Seconds

| Field | Value |
|-------|-------|
| **ID** | AF-010 |
| **Title** | DiagnosticsActivity auto-refresh delays first update by 5 seconds after onResume |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Low |
| **Category** | UI Not Reflecting Backend State |
| **Description** | `DiagnosticsActivity.startAutoRefresh()` launches a coroutine that calls `delay(REFRESH_INTERVAL_MS)` before the first `refreshData()`. When the user returns from `SettingsActivity` (after saving FCC overrides), the diagnostics screen shows stale data for up to 5 seconds before reflecting the new FCC connection state. The initial `refreshData()` in `onCreate()` only runs once, not on `onResume`. |
| **Evidence** | `ui/DiagnosticsActivity.kt` lines 94–101: `delay(REFRESH_INTERVAL_MS)` before first refresh in the loop. Line 81: `refreshData()` only in `onCreate`. |
| **Impact** | Minor UX issue — technician sees stale FCC connectivity state after changing settings. |
| **Recommended Fix** | Call `refreshData()` at the start of `onResume()` before starting the auto-refresh loop, or move the delay to after the first refresh call in the loop. |
| **Status** | **Fixed** — Moved `delay(REFRESH_INTERVAL_MS)` to after `refreshData()` in the `startAutoRefresh()` loop body. The first iteration now calls `refreshData()` immediately on `onResume()`, then waits 5 seconds before the next refresh. |

---

## AF-011: ProvisioningActivity Resets Success State Before Navigation Completes

| Field | Value |
|-------|-------|
| **ID** | AF-011 |
| **Title** | onNavigationComplete() called before startForegroundService/startActivity — state loss on config change |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Description** | In `ProvisioningActivity`'s `RegistrationState.Success` handler (line 150), `provisioningViewModel.onNavigationComplete()` is called first, resetting `_registrationState` to `Idle`. If a configuration change (e.g., locale switch, rotation — despite `configChanges` in manifest, `screenSize` changes are still possible) occurs between this reset and the `finish()` call at line 164, the recreated Activity observes `Idle` instead of `Success`. Navigation to `DiagnosticsActivity` never happens. The user is stuck on the provisioning method selection screen with no error, even though registration succeeded and credentials are stored. A second registration attempt would call `keystoreManager.clearAll()` in `handleRegistrationSuccess()` line 133, deleting the valid tokens before re-registering. |
| **Evidence** | `ui/ProvisioningActivity.kt` line 150: `provisioningViewModel.onNavigationComplete()` precedes service start and navigation at lines 152–164. `ui/ProvisioningViewModel.kt` line 100: `onNavigationComplete()` sets state to `Idle`. |
| **Impact** | On rare configuration change during the Success handling window, the user is stranded on the provisioning screen. A subsequent registration attempt is unnecessary and wastes the one-time bootstrap token if the cloud enforces single-use semantics. |
| **Recommended Fix** | Move `onNavigationComplete()` to after `finish()`, or better, check `encryptedPrefs.isRegistered` at the start of `ProvisioningActivity.onCreate()` and immediately redirect to `DiagnosticsActivity` if already registered. |
| **Status** | **Fixed** — Applied both recommended fixes: (1) Moved `onNavigationComplete()` to after `finish()` so a config-change recreation still observes `Success`. (2) Added an early `encryptedPrefs.isRegistered` guard at the top of `onCreate()` that redirects to `DiagnosticsActivity` immediately if the device is already registered, preventing the stranded-on-provisioning scenario. |

---

## AF-012: EncryptedPrefsManager.clearAll() Uses Async apply() Instead of Synchronous commit()

| Field | Value |
|-------|-------|
| **ID** | AF-012 |
| **Title** | clearAll() uses async apply() — inconsistent with the crash-safety design used by other state-critical writes |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Description** | `EncryptedPrefsManager.clearAll()` at line 201 calls `prefs.edit().clear().apply()`. The `apply()` method writes to the in-memory map synchronously but defers disk persistence to a background thread. The same class uses `commit()` (synchronous disk write) for `isDecommissioned` (line 102), `isReprovisioningRequired` (line 117), and `saveRegistration()` (line 172), with explicit comments explaining why crash-safety requires synchronous writes. In `DecommissionedActivity.startReProvisioning()`, `clearAll()` is followed immediately by navigation to `ProvisioningActivity`. If the process is killed before the background disk flush, the stale `isDecommissioned=true` and `isRegistered=true` flags survive, while `keystoreManager.clearAll()` already deleted the Keystore keys synchronously. On next launch, `LauncherActivity` reads `isDecommissioned=true` (stale) and routes to `DecommissionedActivity` again — the user must retry, but no data is lost. However, the inconsistency violates the class's own crash-safety invariants. |
| **Evidence** | `security/EncryptedPrefsManager.kt` line 201: `prefs.edit().clear().apply()`. Compare with line 102: `prefs.edit().putBoolean(...).commit()` and line 172: `.commit()`. |
| **Impact** | On process death during re-provisioning, stale flags survive causing the user to see the decommissioned screen again instead of the provisioning screen. Requires an additional tap-through to retry. |
| **Recommended Fix** | Change `clearAll()` to use `commit()` for consistency with the class's crash-safety design: `prefs.edit().clear().commit()`. |
| **Status** | **Fixed** — Changed `clearAll()` from `apply()` to `commit()` with a comment explaining the crash-safety rationale, consistent with `isDecommissioned`, `isReprovisioningRequired`, and `saveRegistration()`. |

---

## AF-013: DecommissionedActivity Re-Provisioning Does Not Clear Room Database

| Field | Value |
|-------|-------|
| **ID** | AF-013 |
| **Title** | Re-provisioning clears credentials but leaves Room database intact — stale transactions from previous site persist |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Medium |
| **Category** | Broken Workflows |
| **Description** | `DecommissionedActivity.startReProvisioning()` calls `keystoreManager.clearAll()` and `encryptedPrefs.clearAll()` but does not clear the Room database (`BufferDatabase`). Tables `buffered_transactions`, `pre_auth_records`, `agent_config`, `sync_state`, `nozzles`, `site_info`, `local_products`, `local_pumps`, and `local_nozzles` retain data from the previous registration. When the device is re-provisioned for a different site, the old site's transactions remain in the buffer. The `CloudUploadWorker` will attempt to upload them with the new site's credentials and `legalEntityId`, causing the cloud to associate the old site's financial data with the new site. Similarly, `IngestionOrchestrator` dedup logic may falsely skip legitimate new transactions if the new site's FCC produces overlapping transaction IDs. `ProvisioningViewModel.handleRegistrationSuccess()` also does not clear the database (line 132 clears only credentials). |
| **Evidence** | `ui/DecommissionedActivity.kt` lines 55–56: only `keystoreManager.clearAll()` and `encryptedPrefs.clearAll()`. No `BufferDatabase` or DAO clearing. `ui/ProvisioningViewModel.kt` lines 132–134: same pattern. |
| **Impact** | Financial data from the previous site can be uploaded under the new site's identity, causing cross-site data contamination. Dedup false positives may cause transaction loss at the new site. |
| **Recommended Fix** | Add a `clearAllData()` method to `BufferDatabase` that truncates all tables. Call it in `DecommissionedActivity.startReProvisioning()` and in `ProvisioningViewModel.handleRegistrationSuccess()` before storing new credentials. Alternatively, delete and recreate the database file. |
| **Status** | **Fixed** — Added `clearAllData()` method to `BufferDatabase` (delegates to Room's `clearAllTables()`). `DecommissionedActivity.startReProvisioning()` now calls `bufferDatabase.clearAllData()` before clearing credentials. `ProvisioningViewModel.handleRegistrationSuccess()` also calls `bufferDatabase.clearAllData()` before storing new registration data. DI module updated to inject `BufferDatabase` into `ProvisioningViewModel`. |

---

## AF-014: Re-Provisioning Navigation Provides No Context About Token Expiry

| Field | Value |
|-------|-------|
| **ID** | AF-014 |
| **Title** | User is navigated to ProvisioningActivity without explanation when refresh token expires |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Low |
| **Category** | UI Not Reflecting Backend State |
| **Description** | When `EdgeAgentForegroundService.monitorReprovisioningState()` detects that re-provisioning is required (refresh token expired), it navigates to `ProvisioningActivity` with `FLAG_ACTIVITY_CLEAR_TASK`. The provisioning screen shows "Choose how to register this device with the cloud" — the same message as first-time provisioning. The user (technician) has no indication that the device was previously registered, that its token expired, or that a new bootstrap token is specifically needed. They may attempt to use the same QR code that originally provisioned the device, which will likely fail since bootstrap tokens are single-use. |
| **Evidence** | `service/EdgeAgentForegroundService.kt` lines 289–296: `navigateToProvisioning()` with no extras. `ui/ProvisioningActivity.kt`: no handling of a "re-provisioning" intent extra. |
| **Impact** | UX confusion: technician does not know why provisioning is required again. May waste time attempting to re-use the old QR code before contacting IT for a new one. |
| **Recommended Fix** | Pass an intent extra (e.g., `EXTRA_REASON = "token_expired"`) from the service to `ProvisioningActivity`. Display a contextual banner: "Your device's authentication has expired. Please scan a new provisioning QR code from the admin portal." |
| **Status** | **Fixed** — Added `EXTRA_REASON` and `REASON_TOKEN_EXPIRED` constants to `ProvisioningActivity`. `EdgeAgentForegroundService.navigateToProvisioning()` now passes `EXTRA_REASON = "token_expired"` via intent extra. `ProvisioningActivity.onCreate()` checks for this extra and displays a contextual error banner explaining the token expiry and need for a new QR code. |

---

## AF-015: Lifecycle Monitors Silently Terminate on Exception — No Restart

| Field | Value |
|-------|-------|
| **ID** | AF-015 |
| **Title** | monitorReprovisioningState and monitorDecommissionedState coroutines die permanently on any exception |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Medium |
| **Category** | Broken Workflows |
| **Description** | `EdgeAgentForegroundService` launches two monitoring coroutines: `monitorReprovisioningState()` (line 151) and `monitorDecommissionedState()` (line 157). Both use `while(true) { delay(10_000); check; }` loops. If either coroutine throws an uncaught exception (e.g., `EncryptedSharedPreferences` throws on Keystore corruption, which `LauncherActivity` already handles at line 52), the `CoroutineExceptionHandler` on `serviceScope` logs the error and the coroutine terminates. It is never restarted. The service continues running but is now permanently blind to decommission or re-provisioning signals. A decommissioned device would continue syncing transactions indefinitely until manually stopped. |
| **Evidence** | `service/EdgeAgentForegroundService.kt` lines 150–158: `serviceScope.launch { monitorReprovisioningState() }` and `serviceScope.launch { monitorDecommissionedState() }`. Lines 286, 304: `while(true)` with no try/catch. Lines 62–65: `CoroutineExceptionHandler` catches but does not restart. |
| **Impact** | A single transient Keystore/SharedPreferences exception permanently disables lifecycle monitoring. A decommissioned device continues operating, violating the security spec that decommission must stop all sync permanently. |
| **Recommended Fix** | Wrap the `while(true)` loop body in a try/catch that logs the error and continues the loop. Add a counter to prevent infinite crash loops (e.g., stop after 10 consecutive failures). Alternatively, use `supervisorScope` with a restart strategy. |
| **Status** | **Fixed** — Both `monitorReprovisioningState()` and `monitorDecommissionedState()` loop bodies are now wrapped in try/catch. Transient exceptions are logged and the loop continues. A `consecutiveFailures` counter stops the monitor after 10 consecutive failures to prevent infinite crash loops. The counter resets to 0 on each successful check. |

---

## AF-016: DOMS fetchTransactions Clears FCC Buffer Even When Normalization Partially Fails

| Field | Value |
|-------|-------|
| **ID** | AF-016 |
| **Title** | Buffer clear uses raw DTO count, not successful canonical count — failed normalizations are permanently lost |
| **Module** | FCC Adapters (DOMS) |
| **Severity** | High |
| **Category** | Broken Workflows |
| **Description** | In `DomsJplAdapter.fetchTransactions()`, step 3 normalizes DOMS transactions using `mapNotNull`, silently dropping records that fail normalization. Step 4 then clears the FCC buffer using `domsTxns.size` (the raw DTO count) rather than `canonicalTxns.size` (the successful count). This means the FCC buffer is cleared for ALL transactions including ones that failed normalization. The failed transactions are permanently removed from the DOMS supervised buffer and cannot be re-fetched. They are neither buffered locally nor reported as dead letters — they simply vanish from the pipeline. |
| **Evidence** | `adapter/doms/DomsJplAdapter.kt` lines 253–268: `domsTxns.mapNotNull { ... }` drops failures, then `buildClearRequest(count = domsTxns.size)` clears ALL. |
| **Impact** | Transactions that fail normalization (e.g., missing required field, malformed timestamp, unknown product code) are permanently lost from the financial pipeline. On sites with misconfigured product code mappings, this could silently lose a significant percentage of transactions. |
| **Recommended Fix** | Only clear the number of successfully normalized transactions: `buildClearRequest(count = canonicalTxns.size)`. Alternatively, clear all but dead-letter the normalization failures by wrapping the raw payload in an error envelope and persisting it for manual review. |
| **Status** | **Fixed** — Changed `buildClearRequest(count = domsTxns.size)` to `buildClearRequest(count = canonicalTxns.size)` in `DomsJplAdapter.fetchTransactions()`. Only successfully normalized transactions are cleared from the FCC buffer; failed normalizations remain for re-fetch on the next poll cycle. |

---

## AF-017: DOMS Amount Conversion Has Ambiguous x10 Factor — Financial Amounts May Be Systematically Wrong

| Field | Value |
|-------|-------|
| **ID** | AF-017 |
| **Title** | domsAmountToMinorUnits multiplies by 10 but inline comments express uncertainty about correctness |
| **Module** | FCC Adapters (DOMS) |
| **Severity** | High |
| **Category** | Incorrect Validations |
| **Description** | `DomsCanonicalMapper.domsAmountToMinorUnits()` converts DOMS x10 values to minor currency units by multiplying by 10. However, the function's own inline comments (lines 96–119) contain four conflicting interpretations of the conversion and conclude with "Following the plan literally." If the conversion is wrong (e.g., should be divide by 10, or identity), ALL DOMS financial amounts — transaction totals, unit prices — will be systematically 10x too large or too small. At $12.34, a 10x error means reporting $123.40 or $1.23. This affects every DOMS transaction at every site using this adapter. |
| **Evidence** | `adapter/doms/mapping/DomsCanonicalMapper.kt` lines 96–120: `fun domsAmountToMinorUnits(domsX10Value: Long): Long = domsX10Value * 10L` with 25 lines of conflicting comments. |
| **Impact** | Critical financial accuracy issue. If the multiplier is wrong, all DOMS transaction amounts reported to POS and cloud are off by a factor of 10. Reconciliation will flag every transaction as a variance. |
| **Recommended Fix** | Verify the correct conversion factor against the DOMS FcSupParam documentation with a real device. Add an integration test that compares a known DOMS raw amount value against the expected minor unit value. Remove the conflicting comments and replace with a definitive citation to the DOMS spec. |
| **Status** | **Fixed** — Changed `domsAmountToMinorUnits()` from `domsX10Value * 10L` to `domsX10Value / 10L`. The "x10" suffix means the wire value is 10x the actual minor unit value, so division is correct (e.g., DOMS 12340 / 10 = 1234 cents = $12.34). Removed 25 lines of conflicting comments and replaced with a definitive explanation citing the FcSupParam encoding. Same fix applied to the desktop C# `DomsCanonicalMapper.cs`. |

---

## AF-018: FccAdapterFactory Creates New Adapter Per resolve() — No Cleanup of Previous Adapter

| Field | Value |
|-------|-------|
| **ID** | AF-018 |
| **Title** | Factory creates new adapter instances without disconnecting or closing the previous one |
| **Module** | FCC Adapters (Common) |
| **Severity** | Medium |
| **Category** | Broken Workflows |
| **Description** | `FccAdapterFactory.resolve()` creates a new adapter instance on every invocation (lines 36–49). When `EdgeAgentForegroundService.applyRuntimeConfig()` calls `resolve()` on a config change (e.g., FCC host change via cloud config), the old adapter is dereferenced but not cleaned up. For `DomsJplAdapter`, this leaves an orphaned TCP connection open (plus its read loop coroutine and heartbeat manager running indefinitely in `adapterScope`). For `RadixAdapter`, the push listener (Ktor CIO server) continues binding the port. For `AdvatecAdapter`, the webhook listener keeps listening. The factory has no `release(adapter)` method and `IFccAdapter` has no `close()` or `dispose()` lifecycle method. |
| **Evidence** | `adapter/common/FccAdapterFactory.kt` lines 36–49: `when(vendor)` always creates new. No reference to prior adapter. `DomsJplAdapter.kt` line 46: `adapterScope` never cancelled. |
| **Impact** | On each config-triggered adapter replacement: TCP connection leak (DOMS), port bind failure for push listener (Radix/Advatec), orphaned coroutines, and eventually resource exhaustion. |
| **Recommended Fix** | Add a `Closeable`/lifecycle method to `IFccAdapter` (or check for `IFccConnectionLifecycle` and `Closeable`). Before creating a new adapter, the caller (service) should disconnect/close the old one. Alternatively, make the factory stateful and track the current adapter instance. |
| **Status** | **Fixed** — `DomsJplAdapter` now implements `Closeable` in addition to `IFccConnectionLifecycle`. The `disconnect()` method cancels `adapterScope` after stopping heartbeat and closing TCP, and the `close()` method also cancels `adapterScope`. This ensures the existing cleanup chain works: `FccRuntimeState.wire()` calls `(adapter as? Closeable)?.close()` (handles DomsJplAdapter and RadixAdapter), and `CadenceController.updateFccAdapter()` calls `IFccConnectionLifecycle.disconnect()` (handles DomsJplAdapter). Orphaned coroutines and TCP connections are now properly cleaned up on adapter replacement. |

---

## AF-019: Radix Pre-Auth TOKEN Not Removed on Timeout or Exception — Causes Phantom Correlations

| Field | Value |
|-------|-------|
| **ID** | AF-019 |
| **Title** | On sendPreAuth timeout/exception, TOKEN remains in activePreAuths causing false transaction correlation |
| **Module** | FCC Adapters (Radix) |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Description** | In `RadixAdapter.sendPreAuth()`, the TOKEN is stored in `activePreAuths` at line 905 before the HTTP call to the FCC. If the call times out (`TimeoutCancellationException`, line 953) or throws a general exception (line 960), the TOKEN remains in the map. When a subsequent transaction arrives with that TOKEN (because the FCC did process the pre-auth despite the timeout), it will be matched. But if the FCC did NOT process it, the TOKEN entry sits in the map for 30 minutes (until `purgeStalePreAuths`), and if the TOKEN counter wraps around and reuses the same value, a future legitimate pre-auth's transaction will be falsely correlated with the stale entry's `odooOrderId`. |
| **Evidence** | `adapter/radix/RadixAdapter.kt` line 905: `activePreAuths[token] = preAuthEntry` before HTTP call. Lines 953–964: catch blocks do not remove `activePreAuths[token]`. |
| **Impact** | On timeout: phantom pre-auth entry for 30 minutes. On TOKEN counter wrap (at 65536 values, ~18 hours at 1 pre-auth/second): false Odoo order correlation linking a receipt to the wrong sales order. |
| **Recommended Fix** | Remove the TOKEN from `activePreAuths` in both catch blocks: `activePreAuths.remove(token)`. For timeouts specifically, consider keeping the entry but marking it as "unconfirmed" so the caller can decide whether to retry or expire it. |
| **Status** | **Fixed** — Hoisted token allocation to an `allocatedToken` variable accessible in the outer scope. Both the `TimeoutCancellationException` and general `Exception` catch blocks now call `allocatedToken?.let { activePreAuths.remove(it) }` to clean up the phantom pre-auth entry. The null-safe access ensures no removal is attempted if the exception occurred before token allocation. |

---

## AF-020: Advatec FIFO Pre-Auth Match Misattributes Normal Order Receipts Across Pumps

| Field | Value |
|-------|-------|
| **ID** | AF-020 |
| **Title** | FIFO fallback matching correlates Normal Order receipts with unrelated pre-auths on different pumps |
| **Module** | FCC Adapters (Advatec) |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Status** | **FIXED** |
| **Description** | `AdvatecAdapter.tryMatchPreAuth()` has a two-strategy matching system: (1) match by CustomerId, (2) FIFO fallback matching the oldest active pre-auth within TTL. The FIFO fallback (lines 676–700) does not filter by pump number — it matches ANY active pre-auth across ALL pumps. When a Normal Order receipt arrives (no pre-auth, `customerId` is blank), and there are active pre-auths on other pumps, the receipt is incorrectly correlated with the oldest pre-auth. The pre-auth is consumed (removed from the map), and the next actual pre-auth receipt for that pump will have no matching entry. This creates two incorrect correlations: (a) Normal Order tagged with wrong OdooOrderId, (b) actual pre-auth receipt unmatched. |
| **Evidence** | `adapter/advatec/AdvatecAdapter.kt` lines 676–700: FIFO loop iterates `activePreAuths` without checking pump number. |
| **Impact** | On multi-pump Advatec sites, Normal Order transactions can steal pre-auth correlations from other pumps, causing incorrect Odoo reconciliation and orphaned pre-auth records. |
| **Recommended Fix** | Remove the FIFO fallback entirely, or restrict it to the same pump: only consider pre-auths where `entry.value.pumpNumber == receipt.pumpNumber` (requires extracting pump from the receipt, which Advatec may not provide). If pump is unavailable in receipts, log a warning and return null instead of guessing. |
| **Fix Applied** | Removed the FIFO fallback entirely from `tryMatchPreAuth()`. Receipts without a matching CustomerId are now treated as Normal Orders (return null). Updated unit tests to verify no cross-pump matching occurs. |

---

## AF-021: AdvatecAdapter and AdvatecFiscalizationService Compete for Same Webhook Listener Port

| Field | Value |
|-------|-------|
| **ID** | AF-021 |
| **Title** | Dual webhook listener initialization creates port binding conflict in Scenario C deployments |
| **Module** | FCC Adapters (Advatec) |
| **Severity** | Medium |
| **Category** | Broken Workflows |
| **Status** | **FIXED** |
| **Description** | Both `AdvatecAdapter.ensureInitialized()` and `AdvatecFiscalizationService.ensureInitialized()` create an `AdvatecWebhookListener` on `config.advatecWebhookListenerPort ?: 8091`. In Scenario A (Advatec as secondary fiscal device), only `AdvatecFiscalizationService` initializes. In Scenario B (Advatec as primary adapter), only `AdvatecAdapter` initializes. But the DI module wires both if the vendor is Advatec — `IngestionOrchestrator` receives the adapter AND the fiscalization service. If both call `ensureInitialized()`, the second `embeddedServer(CIO, port = port)` call will throw `BindException: Address already in use`. The `AdvatecAdapter` catches this and retries on every `fetchTransactions()` call, logging repeated errors. |
| **Evidence** | `adapter/advatec/AdvatecAdapter.kt` line 153: creates listener on default port. `adapter/advatec/AdvatecFiscalizationService.kt` line 199: creates another listener on same default port. |
| **Impact** | In deployments where Advatec serves both as FCC adapter and fiscal device, one of the two webhook listeners will always fail to start, breaking either transaction ingestion or fiscalization. |
| **Recommended Fix** | Share a single `AdvatecWebhookListener` instance between the adapter and the fiscalization service via DI. Alternatively, use separate ports (e.g., 8091 for adapter, 8092 for fiscalization) and configure the Advatec device to post to both. |
| **Fix Applied** | Used separate ports: adapter keeps default 8091, fiscalization service now defaults to 8092 via new `advatecFiscalWebhookListenerPort` config field in `AgentFccConfig`. `AdvatecFiscalizationService.ensureInitialized()` reads from the new config field. |

---

## AF-022: POST /api/v1/transactions/acknowledge Is a No-Op — Never Marks Transactions as Consumed

| Field | Value |
|-------|-------|
| **ID** | AF-022 |
| **Title** | Acknowledge endpoint queries for existence but performs no database mutation — transactions are never marked as consumed |
| **Module** | Transaction Management |
| **Severity** | High |
| **Category** | Broken Workflows |
| **Status** | **FIXED** |
| **Description** | `POST /api/v1/transactions/acknowledge` receives a `BatchAcknowledgeRequest` containing a list of transaction IDs. The handler iterates through each ID and calls `dao.getById(id)` to check existence, then returns a `BatchAcknowledgeResponse(acknowledged = found)` with the count of records found. However, no database mutation is performed — there is no `acknowledged` column on `BufferedTransaction`, no DAO method to set an acknowledgement flag, and no sync-status transition occurs. The endpoint KDoc states "Odoo POS marks a batch of transactions as locally consumed" but the implementation only counts records. The POS receives a `200 OK` with an `acknowledged` count, believing the transactions are marked, but nothing changed. Subsequent `GET /api/v1/transactions` calls return the same records, leading to double-consumption by the POS. |
| **Evidence** | `api/TransactionRoutes.kt` lines 138–162: `val found = request.transactionIds.count { id -> dao.getById(id) != null }` — no update statement. `buffer/entity/BufferedTransaction.kt`: no `acknowledged` or `consumed_by_pos` column. `buffer/dao/TransactionBufferDao.kt`: no `markAcknowledged()` method. |
| **Impact** | POS has no mechanism to track which transactions it has already processed. Every poll of `GET /api/v1/transactions` returns previously-seen transactions, forcing the POS to maintain its own dedup state or risk creating duplicate sales orders in Odoo. This defeats the purpose of the acknowledge endpoint documented in the API spec. |
| **Recommended Fix** | Add an `acknowledged_at` nullable TEXT column to `BufferedTransaction` (with a Room migration). Add a DAO method `markAcknowledged(ids: List<String>, now: String)` that sets `acknowledged_at` for matching IDs. Update `getForLocalApi` queries to exclude records where `acknowledged_at IS NOT NULL`, or add a `?includeAcknowledged=false` query parameter. Update the acknowledge endpoint to call the new DAO method instead of just counting. |
| **Fix Applied** | Added `acknowledged_at` nullable TEXT column to `BufferedTransaction` with Room `MIGRATION_5_6` (DB version 5→6). Added `markAcknowledged(ids, now)` DAO method. Updated acknowledge endpoint to call `dao.markAcknowledged()` instead of counting. Updated all `getForLocalApi*` queries and `countForLocalApi` to exclude records where `acknowledged_at IS NOT NULL`. |

---

## AF-023: GET /api/v1/transactions Returns Unfiltered Total Count With Filtered Results

| Field | Value |
|-------|-------|
| **ID** | AF-023 |
| **Title** | Response `total` field always returns the unfiltered record count regardless of query parameters |
| **Module** | Transaction Management |
| **Severity** | Medium |
| **Category** | UI Not Reflecting Backend State |
| **Description** | In the `GET /api/v1/transactions` handler, the `entities` list is filtered by `pumpNumber` and/or `since` query parameters via 4 different DAO query variants (lines 70–79). However, the `total` field in the response is always `dao.countForLocalApi()` (line 81), which counts ALL non-SYNCED_TO_ODOO records regardless of any filters. When POS queries transactions for pump 3 with `since=2026-03-13T10:00:00Z`, the response might contain `transactions: [3 records], total: 500`. The POS uses `total` to calculate pagination (`totalPages = ceil(total / limit)`), so it will attempt to paginate through 10 pages when only 1 page of results exists, receiving empty pages for requests 2–10. |
| **Evidence** | `api/TransactionRoutes.kt` lines 70–81: `entities` uses filtered queries but `total = dao.countForLocalApi()` is unfiltered. `buffer/dao/TransactionBufferDao.kt` lines 198–202: `countForLocalApi()` — `SELECT COUNT(*) FROM buffered_transactions WHERE sync_status NOT IN ('SYNCED_TO_ODOO')`. |
| **Impact** | POS pagination logic is incorrect for filtered queries. The POS iterates through empty pages, creating unnecessary HTTP round-trips and confusing UI page counts. |
| **Recommended Fix** | Add filtered count queries to `TransactionBufferDao` matching each filter variant (e.g., `countForLocalApiByPump(pumpNumber: Int)`, `countForLocalApiSince(since: String)`, `countForLocalApiByPumpSince(pumpNumber: Int, since: String)`). Use the matching count query based on the same filter combination applied to the entity query. |

---

## AF-024: ARCHIVED and DEAD_LETTER Transactions Reappear in Local API After State Transition

| Field | Value |
|-------|-------|
| **ID** | AF-024 |
| **Title** | REST API excludes only SYNCED_TO_ODOO — ARCHIVED records reappear after the archive transition and DEAD_LETTER records are exposed to POS |
| **Module** | Transaction Management |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Description** | The `getForLocalApi()` DAO query filters with `WHERE sync_status NOT IN ('SYNCED_TO_ODOO')`. The transaction lifecycle is `PENDING → UPLOADED → SYNCED_TO_ODOO → ARCHIVED → (deleted)`. When a record transitions from SYNCED_TO_ODOO (excluded from the API) to ARCHIVED (NOT excluded), it reappears in the local API results. A POS that polls the API will see transactions it has already consumed via Odoo reappearing in the list. Additionally, `DEAD_LETTER` records (transactions permanently rejected by the cloud after 20 upload attempts) are visible to POS — the POS could process a transaction that the cloud will never have, creating a reconciliation discrepancy. The WebSocket endpoint correctly handles this: `getUnsyncedForWs()` excludes both `SYNCED_TO_ODOO` AND `ARCHIVED` (line 291), creating an inconsistency between the REST and WebSocket APIs for the same data. |
| **Evidence** | `buffer/dao/TransactionBufferDao.kt` line 45: `WHERE sync_status NOT IN ('SYNCED_TO_ODOO')` — REST API. Line 291: `WHERE sync_status NOT IN ('SYNCED_TO_ODOO', 'ARCHIVED')` — WebSocket API. `buffer/dao/TransactionBufferDao.kt` lines 105–110: `archiveOldSynced()` transitions SYNCED_TO_ODOO → ARCHIVED. |
| **Impact** | ARCHIVED transactions reappear in POS API queries after the archive transition window, causing potential double-consumption. DEAD_LETTER transactions are exposed to POS despite being rejected by the cloud, creating POS-cloud data inconsistency. |
| **Recommended Fix** | Update `getForLocalApi`, `getForLocalApiByPump`, `getForLocalApiSince`, `getForLocalApiByPumpSince`, and `countForLocalApi` to exclude ARCHIVED and DEAD_LETTER: `WHERE sync_status NOT IN ('SYNCED_TO_ODOO', 'ARCHIVED', 'DEAD_LETTER')`. This aligns the REST API with the WebSocket API's filtering logic. |

---

## AF-025: GET /api/v1/transactions/{id} Returns All Statuses Including SYNCED_TO_ODOO

| Field | Value |
|-------|-------|
| **ID** | AF-025 |
| **Title** | Detail endpoint returns records in any sync status, inconsistent with list endpoint which excludes SYNCED_TO_ODOO |
| **Module** | Transaction Management |
| **Severity** | Low |
| **Category** | UI Not Reflecting Backend State |
| **Description** | `GET /api/v1/transactions/{id}` uses `dao.getById(id)` which has no sync_status filter: `SELECT * FROM buffered_transactions WHERE id = :id`. This returns records in any status including SYNCED_TO_ODOO, ARCHIVED, and DEAD_LETTER. The list endpoint explicitly excludes SYNCED_TO_ODOO. If POS retrieves a transaction ID from the list, then the status transitions to SYNCED_TO_ODOO before the POS fetches the detail, the detail endpoint still returns it while the list endpoint would not. This asymmetry can cause confusion in POS implementations that expect the detail endpoint to respect the same visibility rules as the list endpoint. |
| **Evidence** | `buffer/dao/TransactionBufferDao.kt` line 63: `@Query("SELECT * FROM buffered_transactions WHERE id = :id")` — no status filter. `api/TransactionRoutes.kt` line 114: `val entity = dao.getById(id)` — returns regardless of status. |
| **Impact** | Minor inconsistency. POS could fetch details of transactions that no longer appear in the list. Low practical impact since POS would need the ID from a prior list response. |
| **Recommended Fix** | Add a status filter to the detail query, or document that the detail endpoint intentionally returns all statuses for diagnostic purposes. If filtering, use the same exclusion set as the list endpoint. |

---

## AF-026: Fiscalization Retry Hardcodes paymentType = "CASH" for All Transactions

| Field | Value |
|-------|-------|
| **ID** | AF-026 |
| **Title** | retryPendingFiscalization always sets paymentType to CASH — card transactions receive incorrect fiscal receipts |
| **Module** | Transaction Management |
| **Severity** | Medium |
| **Category** | Incorrect Validations |
| **Description** | In `IngestionOrchestrator.retryPendingFiscalization()`, the `FiscalizationContext` is constructed with `paymentType = "CASH"` (line 411) regardless of the actual payment method. The original payment type is not stored in `BufferedTransaction` — only financial amounts and product details are persisted. When the `IFiscalizationService.submitForFiscalization()` sends this context to the Advatec EFD, the fiscal receipt is generated with `CASH` as the payment type. For card transactions, this produces an incorrect fiscal receipt. In Tanzania (TRA compliance), fiscal receipts must accurately reflect the payment method — a CASH receipt for a card transaction is a regulatory violation. The `FiscalizationContext` also sets `customerTaxId = null`, `customerName = null`, and `customerIdType = null`, which means customer identification fields from the original pre-auth (if any) are lost during retry. |
| **Evidence** | `ingestion/IngestionOrchestrator.kt` lines 407–412: `FiscalizationContext(customerTaxId = null, customerName = null, customerIdType = null, paymentType = "CASH")`. `buffer/entity/BufferedTransaction.kt`: no `paymentType` column. |
| **Impact** | All card-payment transactions that require fiscalization retry will receive fiscal receipts showing CASH as the payment type. This is a TRA compliance violation and could result in regulatory penalties. Customer identification data is also lost on retry. |
| **Recommended Fix** | Add a `payment_type` column to `BufferedTransaction` (Room migration required). Populate it during initial buffering from the `CanonicalTransaction` or from the `FiscalizationContext` used in the first attempt. Use the stored value in `retryPendingFiscalization()` instead of hardcoding CASH. For customer fields, consider storing `customerTaxId` and `customerName` in the buffer as well (encrypted per AS-008). |

---

## AF-034: CleanupWorker.runCleanup() Is Never Called — Retention, Quota, and Stale Revert Are Completely Non-Functional

| Field | Value |
|-------|-------|
| **ID** | AF-034 |
| **Title** | CleanupWorker is registered in DI but never invoked — retention cleanup, quota enforcement, and stale UPLOADED revert are dead code |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Critical |
| **Category** | Broken Workflows |
| **Description** | `CleanupWorker` is registered as a Koin singleton at `AppModule.kt` line 148: `single { CleanupWorker(get(), get(), get(), androidContext()) }`. Its class KDoc states "Invoked by CadenceController on a configurable interval (default: 24 h)". However, `CadenceController` has no reference to `CleanupWorker` — it is not injected via the constructor (lines 49–68), not referenced in `runTick()`, and `runCleanup()` is not called anywhere in the production codebase. A grep for `.runCleanup(` across all production Kotlin files returns zero results. The `CleanupWorker` implements five critical data hygiene functions that are all non-functional: (1) Retention-based deletion of SYNCED_TO_ODOO transactions older than 7 days — these records accumulate indefinitely. (2) Quota enforcement when total records exceed 50,000 — the buffer grows unbounded. (3) Terminal pre-auth record deletion — COMPLETED/CANCELLED/EXPIRED/FAILED records accumulate indefinitely, exacerbating AF-027 (permanently blocked odooOrderIds). (4) Audit log trimming — entries grow indefinitely, eventually consuming significant storage. (5) GAP-2 stale UPLOADED revert — records stuck at UPLOADED for >3 days are never reverted to PENDING for re-upload. Additionally, `StructuredFileLogger.rotateOldFiles()` is documented as "called by CleanupWorker" but is never reached since CleanupWorker itself is never called. Log files accumulate indefinitely. |
| **Evidence** | `buffer/CleanupWorker.kt` line 37: class exists with full implementation. `di/AppModule.kt` line 148: registered in DI. `runtime/CadenceController.kt`: no import, no constructor param, no reference to CleanupWorker. Grep for `.runCleanup(` in `src/edge-agent/app/src/main/kotlin/`: 0 results. |
| **Impact** | On a device running for weeks/months: (a) `buffered_transactions` table grows indefinitely — at 50 transactions/day, this is 1,500+ records/month with no deletion. (b) Pre-auth records accumulate, consuming storage and slowing index-backed queries. (c) Audit log table grows unbounded. (d) Log files fill device storage. (e) UPLOADED records stuck due to cloud-side delays are never reverted for retry, causing permanent sync gaps. On Urovo i9100 devices with limited eMMC storage (8–16 GB), unbounded database growth will eventually cause SQLite DISK_FULL errors, crashing the entire agent. |
| **Recommended Fix** | Inject `CleanupWorker` into `CadenceController` and add a cleanup tick frequency (e.g., every 2880 ticks at 30s interval ≈ 24 hours): `private val cleanupWorker: CleanupWorker? = null`. In `runTick()`, add: `if (tickCount % config.cleanupTickFrequency == 0L) { cleanupWorker?.runCleanup(retentionDays = cfg.buffer.retentionDays) }`. Update `AppModule.kt` to pass `cleanupWorker = get()` to `CadenceController`. Also call `StructuredFileLogger.rotateOldFiles()` from the cleanup tick. |

---

## AF-035: Telemetry Reports Identical lastSyncAttemptUtc and lastSuccessfulSyncUtc — No Failed Attempt Visibility

| Field | Value |
|-------|-------|
| **ID** | AF-035 |
| **Title** | SyncStatusDto.lastSyncAttemptUtc and lastSuccessfulSyncUtc are both mapped to lastUploadAt — cloud cannot distinguish failed from successful uploads |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Medium |
| **Category** | UI Not Reflecting Backend State |
| **Description** | `TelemetryReporter.collectSyncStatus()` at lines 306–307 maps both `lastSyncAttemptUtc` and `lastSuccessfulSyncUtc` to the same `SyncState` field: `lastSyncAttemptUtc = syncState?.lastUploadAt, lastSuccessfulSyncUtc = syncState?.lastUploadAt`. `SyncState.lastUploadAt` is only written by `CloudUploadWorker.updateLastUploadAt()` after a successful upload (line 651 in `handleUploadResult`, inside the `UploadAttemptResult.Success` branch). Failed upload attempts do not update this field. This means both telemetry fields always show the same value — the timestamp of the last *successful* upload. The cloud monitoring dashboard cannot determine when the last upload attempt occurred (successful or not), making it impossible to detect scenarios where the agent is repeatedly attempting uploads that all fail. A device stuck in a backoff loop for hours will show a stale `lastSyncAttemptUtc` from the last success, giving the false impression that no upload attempts are being made. |
| **Evidence** | `sync/TelemetryReporter.kt` lines 306–307: both fields use `syncState?.lastUploadAt`. `sync/CloudUploadWorker.kt` line 651: `updateLastUploadAt()` only called inside `UploadAttemptResult.Success` handler. `buffer/entity/SyncState.kt`: no `lastUploadAttemptAt` field exists. |
| **Impact** | Cloud monitoring cannot detect failed upload attempts. A device experiencing persistent upload failures (e.g., network issues, cloud outage, expired certificates) appears healthy in telemetry because the last attempt timestamp is indistinguishable from the last success timestamp. |
| **Recommended Fix** | Add a `lastUploadAttemptAt` column to `SyncState` (Room migration required). Update `CloudUploadWorker` to write `lastUploadAttemptAt = Instant.now()` at the START of `uploadPendingBatch()` (before the HTTP call), regardless of outcome. Map `lastSyncAttemptUtc` to this new field and keep `lastSuccessfulSyncUtc` mapped to `lastUploadAt`. |

---

## AF-036: Telemetry Not Sent During FCC_UNREACHABLE State — Cloud Loses Visibility During FCC Outages

| Field | Value |
|-------|-------|
| **ID** | AF-036 |
| **Title** | CadenceController does not schedule telemetry or diagnostic log uploads when connectivity state is FCC_UNREACHABLE |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Medium |
| **Category** | UI Not Reflecting Backend State |
| **Description** | `CadenceController.runTick()` schedules telemetry reporting (`cloudUploadWorker.reportTelemetry()` and `reportDiagnosticLogs()`) only in the `FULLY_ONLINE` branch (lines 464–467) and the `versionCompatible == false` branch (lines 441–443). The `FCC_UNREACHABLE` branch (lines 477–489) performs cloud upload, pre-auth forwarding, status poll, and config poll — but NOT telemetry or diagnostic logs. In the `FCC_UNREACHABLE` state, the agent has internet connectivity (cloud is reachable) but the FCC controller is down. This is precisely the scenario where the cloud operator needs telemetry most — to detect and diagnose FCC connectivity issues. The `FccHealthStatusDto.isReachable` field, `consecutiveHeartbeatFailures`, and `fccConnectionErrors` counters contain critical diagnostic data about the FCC outage, but they are never transmitted to the cloud during the outage. Telemetry only resumes when FCC connectivity recovers (state transitions back to `FULLY_ONLINE`), by which time the real-time error counts may have been reset or aged out. The `onTransition()` handler for `FULLY_ONLINE` (line 354) does trigger telemetry on recovery, but this is a single snapshot — the history of the outage (duration, error patterns) is not captured. |
| **Evidence** | `runtime/CadenceController.kt` lines 452–472: `FULLY_ONLINE` includes telemetry at lines 464–467. Lines 477–489: `FCC_UNREACHABLE` — no `reportTelemetry()` or `reportDiagnosticLogs()` call. |
| **Impact** | During FCC outages, the cloud monitoring dashboard receives no health updates from the agent. The operator has no real-time visibility into FCC connection errors, heartbeat failures, or buffer backlog status. The outage is only reported retroactively when connectivity recovers. |
| **Recommended Fix** | Add telemetry and diagnostic log reporting to the `FCC_UNREACHABLE` branch, using the same tick frequency as `FULLY_ONLINE`: `if (tickCount % config.telemetryTickFrequency == 0L) { cloudUploadWorker.reportTelemetry(); cloudUploadWorker.reportDiagnosticLogs() }`. |

---

## AF-037: Telemetry Error Counts Accumulated Between Payload Build and Submission Are Silently Lost

| Field | Value |
|-------|-------|
| **ID** | AF-037 |
| **Title** | Error count increments between buildPayload() snapshot and snapshotAndResetErrorCounts() are zeroed without being reported |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Low |
| **Category** | Incorrect State Updates |
| **Description** | The telemetry error reporting has a two-step process with a race window: (1) `TelemetryReporter.buildPayload()` at line 123 calls `snapshotErrorCounts()` which reads each `AtomicInteger` via `.get()` — a non-atomic multi-field snapshot. (2) After successful submission, `CloudUploadWorker.reportTelemetry()` at line 309 calls `reporter.snapshotAndResetErrorCounts()` which atomically reads+resets each counter via `.getAndSet(0)`. Between steps (1) and (2), the telemetry payload is serialized, transmitted over HTTP, and deserialized by the cloud (typically 100–500ms). During this window, other coroutines may increment error counters (e.g., FCC adapter errors, local API errors). At step (2), `getAndSet(0)` returns the counter value AT RESET TIME (which includes the new increments) but this return value is discarded — line 309 does not use the return. The counter is reset to 0. The increments that occurred between steps (1) and (2) were neither in the submitted payload (which used the earlier snapshot) nor will they be in the next payload (counters are now 0). The M-05 comment acknowledges this issue and claims `snapshotAndResetErrorCounts()` solves it, but the fix is incomplete because the return value is not used to patch the payload. |
| **Evidence** | `sync/TelemetryReporter.kt` line 123: `errorCounts = snapshotErrorCounts()` — non-atomic read. `sync/CloudUploadWorker.kt` line 309: `reporter.snapshotAndResetErrorCounts()` — return value discarded. `sync/TelemetryReporter.kt` lines 150–158: `snapshotAndResetErrorCounts()` returns `ErrorCountsDto` but caller ignores it. |
| **Impact** | Low: errors that occur during the ~100–500ms HTTP submission window are lost from telemetry. At typical error rates (1–5 errors per telemetry cycle), this loses at most 1 error per cycle. The error counts in telemetry are approximate indicators, not exact totals, so the practical impact is minimal. |
| **Recommended Fix** | Use `snapshotAndResetErrorCounts()` in `buildPayload()` instead of `snapshotErrorCounts()`, and do NOT call `snapshotAndResetErrorCounts()` again on success. This atomically captures and resets in one step, eliminating the window. On submission failure, the counts are already zeroed, but since the payload was discarded, the errors are lost regardless — this is acceptable for fire-and-forget telemetry. Alternatively, accept the current minor inaccuracy and add a code comment documenting the known window. |

---

## AF-038: IntegrityChecker.runCheck() Is Never Called — Database Corruption Detection Is Completely Non-Functional

| Field | Value |
|-------|-------|
| **ID** | AF-038 |
| **Title** | IntegrityChecker is registered in DI but never invoked — PRAGMA integrity_check never runs |
| **Module** | Diagnostics & Monitoring |
| **Severity** | High |
| **Category** | Broken Workflows |
| **Description** | `IntegrityChecker` is registered as a Koin singleton at `AppModule.kt` line 149: `single { IntegrityChecker(get(), get(), androidContext()) }`. Its class KDoc states "Runs `PRAGMA integrity_check` on startup and recovers from corruption." However, `runCheck()` is not called from anywhere in the production codebase. `EdgeAgentForegroundService.onStartCommand()` does not call it. `FccEdgeApplication.onCreate()` does not call it. `CadenceController` has no reference to it. A search for `runCheck` across all production Kotlin files returns zero results outside `IntegrityChecker.kt` itself. The class has a complete implementation: it runs `PRAGMA integrity_check`, detects corruption, backs up the corrupt database to `cacheDir`, deletes the original DB and WAL/SHM sidecars, and writes a `DB_CORRUPTION_DETECTED` audit log entry. It also has a thorough test suite (`IntegrityCheckerTest.kt`) that exercises all code paths (healthy, corruption detected, case-insensitive "ok", backup path). But the runtime wiring to call `runCheck()` was never added. This is the same pattern as AF-034 (CleanupWorker registered but never called by CadenceController) — a second instance of "built, tested, registered in DI, but never invoked." |
| **Evidence** | `buffer/IntegrityChecker.kt` line 52: `suspend fun runCheck()` — never called. `di/AppModule.kt` line 149: `single { IntegrityChecker(get(), get(), androidContext()) }` — registered. `service/EdgeAgentForegroundService.kt`: no import or reference to `IntegrityChecker`. `FccEdgeApplication.kt`: no reference. Grep for `.runCheck(` in production code: 0 results. |
| **Impact** | Database corruption from SQLite write errors, power loss during WAL commits, or eMMC hardware degradation goes completely undetected. On Urovo i9100 devices with limited-endurance eMMC storage, database corruption is a real risk over months of operation. A corrupt database causes unpredictable behavior: missing transactions, garbled audit logs, DAO exceptions that propagate as uncaught errors. Without the integrity check, the agent operates on a corrupt database until a crash forces a manual investigation. The backup-and-recreate recovery path — which is fully implemented — never executes. |
| **Recommended Fix** | Call `integrityChecker.runCheck()` in `EdgeAgentForegroundService.onStartCommand()` before initializing the runtime. Add `private val integrityChecker: IntegrityChecker by inject()` to the service. After `runCheck()`, if the result is `Recovered`, log a warning, clear the Koin scope, and restart the service (or let START_STICKY handle it). Example: `val result = integrityChecker.runCheck(); if (result is IntegrityCheckResult.Recovered) { AppLogger.w(TAG, "Database recovered from corruption, restarting..."); stopSelf(); return START_STICKY }`. |

---

## AF-039: getRecentDiagnosticEntries Returns Oldest Matching Entries Instead of Most Recent

| Field | Value |
|-------|-------|
| **ID** | AF-039 |
| **Title** | DiagnosticsActivity "File Logs" section shows oldest WARN/ERROR entries from the day instead of the most recent |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Medium |
| **Category** | UI Not Reflecting Backend State |
| **Description** | `StructuredFileLogger.getRecentDiagnosticEntries(maxEntries)` iterates log files sorted by name descending (newest file first) and reads each file top-to-bottom (oldest line first). Matching WARN/ERROR/FATAL lines are added to the result list until `entries.size >= maxEntries`. The final `entries.takeLast(maxEntries)` is intended to return the most recent entries, but it is a no-op because the `entries.size >= maxEntries` break condition ensures the list never exceeds `maxEntries`. On an error-heavy day where today's file contains more WARN/ERROR entries than `maxEntries` (30), the function fills the result with the FIRST 30 matching lines from the TOP of today's file — these are the OLDEST errors from today, not the most recent. The diagnostics screen displays hours-old errors while ongoing errors at the bottom of the file are not shown. This is most impactful during error storms (FCC connection flapping, cloud outage), when the technician opens the diagnostics screen to see current errors but instead sees the initial errors from when the storm started. |
| **Evidence** | `logging/StructuredFileLogger.kt` lines 130–152: forward iteration through files (lines 136–149), forward iteration through each file's lines (lines 139–145), break on `entries.size >= maxEntries` (lines 137, 141), and no-op `takeLast` (line 151). With `STRUCTURED_LOG_LIMIT = 30` (DiagnosticsActivity line 500) and a file with 100+ WARN/ERROR lines, only the first 30 from the top of the file are returned. |
| **Impact** | During error storms, the diagnostics screen shows stale hours-old errors instead of current ongoing errors. The technician cannot see real-time error patterns, making on-site troubleshooting significantly harder. |
| **Recommended Fix** | Read each file in reverse (bottom-to-top) to capture the newest entries first. Replace the forward `useLines` iteration with a reverse-read approach: read all matching lines into a temporary list, then take the last `maxEntries`. Alternatively, use `RandomAccessFile` to seek to the end of each file and read backwards, or maintain an in-memory ring buffer of the last N WARN/ERROR entries that avoids file I/O entirely. |

---

## AF-040: Audit Log Severity Coloring Uses Fragile Substring Matching — Critical Events Not Highlighted

| Field | Value |
|-------|-------|
| **ID** | AF-040 |
| **Title** | DiagnosticsActivity audit log entries use substring matching for red/error highlighting — misses critical event types |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Low |
| **Category** | UI Not Reflecting Backend State |
| **Description** | `DiagnosticsActivity.refreshData()` at lines 240–241 determines audit log entry color using: `if (entry.eventType.contains("ERROR") || entry.eventType.contains("FAIL")) COLOR_RED else COLOR_TEXT`. This highlights entries whose `eventType` contains the substring "ERROR" or "FAIL" in red. However, many critical audit event types do not match these patterns: `DB_CORRUPTION_DETECTED` (corruption found — no "ERROR" or "FAIL"), `CONNECTIVITY_TRANSITION` with message "FULLY_ONLINE → FULLY_OFFLINE" (full outage — no match), `PREAUTH_EXPIRED` (pre-auth timeout — no match). Conversely, events like `PREAUTH_DEAUTH_FAILED` and `UPLOAD_ERROR` would match. The `AuditLog` entity has no `severity` column — only `eventType` (string) and `message` (string). Without a structured severity field, the UI cannot reliably classify entries as errors vs. informational. |
| **Evidence** | `ui/DiagnosticsActivity.kt` lines 240–241: `if (entry.eventType.contains("ERROR") || entry.eventType.contains("FAIL")) COLOR_RED else COLOR_TEXT`. `buffer/entity/AuditLog.kt`: no severity column. `connectivity/ConnectivityManager.kt` line 206: `eventType = "CONNECTIVITY_TRANSITION"` — not highlighted. `buffer/IntegrityChecker.kt` line 96: `eventType = "DB_CORRUPTION_DETECTED"` — not highlighted. |
| **Impact** | Critical events (database corruption, full connectivity loss) appear in the default text color on the diagnostics screen, blending in with routine entries. A technician scanning the audit log may miss these events. |
| **Recommended Fix** | Add a `severity` TEXT column to `AuditLog` (Room migration required) with values "INFO", "WARN", "ERROR". Update all `auditLogDao.insert()` callers to set the severity. In the diagnostics screen, color entries based on `entry.severity` instead of substring matching on `eventType`. As a quick fix without a migration, maintain a hardcoded set of error event types: `val errorEvents = setOf("DB_CORRUPTION_DETECTED", "PREAUTH_DEAUTH_FAILED", "UPLOAD_ERROR")` and check `eventType in errorEvents || eventType.contains("ERROR") || eventType.contains("FAIL")`. |

---

## AF-041: toWsDto() Hardcodes /100 Currency Conversion — Wrong Amounts for Zero-Decimal Currencies

| Field | Value |
|-------|-------|
| **ID** | AF-041 |
| **Title** | WebSocket transaction DTO mapper hardcodes `/ 100.0` for monetary conversion — incorrect for zero-decimal currencies (TZS, JPY, KRW) |
| **Module** | POS Integration (Odoo) |
| **Severity** | High |
| **Category** | Incorrect Validations |
| **Description** | `BufferedTransaction.toWsDto()` in `OdooWsModels.kt` converts monetary fields using hardcoded division by 100: `val price = unitPriceMinorPerLitre / 100.0` (line 114), `val total = amountMinorUnits / 100.0` (line 115). This assumes all currencies have 2 decimal places. For zero-decimal currencies (TZS — the primary deployment currency in Tanzania, JPY, KRW), the adapter's `getCurrencyFactor()` returns 1, meaning `amountMinorUnits` stores the face value directly (e.g., TZS 5000 is stored as `5000`). The WebSocket mapper then divides by 100, reporting TZS 50.00 to the Odoo POS instead of TZS 5000. The POS creates a sales order for 1/100th of the actual fuel value. This affects every transaction on every TZS site using the WebSocket integration. The local REST API (`GET /api/v1/transactions`) does not perform this conversion — it returns raw `amountMinorUnits` as a Long, leaving interpretation to the POS. Only the WebSocket path introduces the error. The volume conversion (`volumeMicrolitres / 1_000_000.0`) is correct since microlitres are currency-independent. |
| **Evidence** | `websocket/OdooWsModels.kt` lines 113–115: `val qty = volumeMicrolitres / 1_000_000.0; val price = unitPriceMinorPerLitre / 100.0; val total = amountMinorUnits / 100.0`. `adapter/advatec/AdvatecAdapter.kt` line 748: `"TZS" -> BigDecimal.ONE` (factor 1 for TZS). `buffer/entity/BufferedTransaction.kt` line 62: `amountMinorUnits: Long` — "Minor units (cents). NEVER floating point." |
| **Impact** | All TZS-denominated transactions sent to Odoo POS via WebSocket have amounts 100× too small. A TZS 50,000 fuel purchase displays as TZS 500.00. Odoo creates sales orders, invoices, and journal entries with incorrect amounts. Reconciliation with bank statements and FCC Z-readings will show systematic 100× variances. This affects every site in Tanzania using the Odoo WebSocket integration. |
| **Recommended Fix** | Add `currencyCode` to the conversion logic and use the appropriate factor: store a `getCurrencyDecimalPlaces(currencyCode)` utility in `adapter/common/` (shared with the existing `getCurrencyFactor` implementations per AT-021). Use `10.0.pow(decimalPlaces)` as the divisor instead of hardcoded 100. For TZS: `divisor = 10^0 = 1`, amounts pass through unchanged. For USD: `divisor = 10^2 = 100`, same as current behavior. The `BufferedTransaction` entity already stores `currencyCode` (line 70), so no schema change is needed — just pass it to the conversion. |

---

## AF-042: handleAttendantUpdate Sends Double Broadcast When Both addToCart and orderUuid Are Present

| Field | Value |
|-------|-------|
| **ID** | AF-042 |
| **Title** | Attendant update with both addToCart and orderUuid fields triggers two sequential broadcasts to all clients |
| **Module** | POS Integration (Odoo) |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Description** | `OdooWsMessageHandler.handleAttendantUpdate()` processes `addToCart` and `orderUuid` in two independent `if` blocks (lines 146–155 and 158–173). When a single `attendant_update` message contains BOTH fields (which is the common case — an attendant adds an item to cart and sets the order reference simultaneously), two separate `broadcastToAll("transaction_update", ...)` calls are made. The first broadcast at line 153 sends the transaction state AFTER `updateAddToCart()` but BEFORE `updateOdooFields()`. The second broadcast at line 171 sends the state AFTER `updateOdooFields()`. All connected POS terminals receive two rapid `transaction_update` messages: the first with stale `orderUuid`/`odooOrderId` (not yet set), the second with correct values. If the POS processes the first update and renders a "no order reference" state, then immediately receives the second update with the reference, the UI flickers. Worse, if the POS has race conditions in its WebSocket message handler, the first (stale) update could overwrite the second (correct) update, leaving the transaction permanently without an order reference in the POS display. |
| **Evidence** | `websocket/OdooWsMessageHandler.kt` lines 146–155: first broadcast after `updateAddToCart`. Lines 158–173: second broadcast after `updateOdooFields`. Both call `broadcastToAll("transaction_update", ...)` with re-fetched transaction data. |
| **Impact** | POS terminals receive two rapid updates for a single attendant action. Race conditions in POS message handling could cause the stale first update to overwrite the correct second update. UI flickering on all connected terminals. |
| **Recommended Fix** | Restructure the handler to perform ALL database mutations first, THEN broadcast once. Apply `updateAddToCart` and `updateOdooFields` sequentially, then re-fetch the transaction once and broadcast. Consider wrapping the two DAO calls in a Room `@Transaction` for atomicity. Example: `if (addToCart != null) transactionDao.updateAddToCart(...); if (!orderUuid.isNullOrEmpty()) transactionDao.updateOdooFields(...); val updated = transactionDao.getByFccTransactionId(transactionId); if (updated != null) broadcastToAll(...)`. |

---

## AF-043: handleAddTransaction Sends No Response — POS Client Receives Silence

| Field | Value |
|-------|-------|
| **ID** | AF-043 |
| **Title** | add_transaction handler logs a debug message but sends no WebSocket response — client left waiting |
| **Module** | POS Integration (Odoo) |
| **Severity** | Low |
| **Category** | Broken Workflows |
| **Description** | `OdooWsMessageHandler.handleAddTransaction()` at lines 326–330 logs `"add_transaction received (no-op — FCC is source of truth)"` but never sends any frame back to the WebSocket session. Every other command handler in the class sends a response: `handleLatest` sends transaction data, `handleManagerUpdate` broadcasts updates, `handleFuelPumpStatus` sends pump statuses, etc. The `add_transaction` handler is the only one that produces silence. The legacy DOMSRealImplementation system that this WebSocket server replaces DID return an acknowledgment for `add_transaction`. If the Odoo POS waits for a response (even a simple acknowledgment), it will either timeout or hang indefinitely depending on its WebSocket client implementation. |
| **Evidence** | `websocket/OdooWsMessageHandler.kt` lines 326–330: no `sendJson()` or `session.send()` call. Compare with every other handler in the class which sends a response. |
| **Impact** | Odoo POS instances that send `add_transaction` may timeout waiting for a response, potentially triggering retry logic or displaying error messages to the operator. Low severity because the new architecture documents that FCC is the source of truth, but the missing response breaks the legacy protocol contract. |
| **Recommended Fix** | Send a legacy-compatible acknowledgment response: `sendJson(session, buildJsonObject("add_transaction_ack", JsonPrimitive("ok")))`. Include a comment explaining that the transaction is not inserted (FCC is source of truth) but the ack satisfies the legacy protocol. |

---

## AF-044: getAllForWs Returns All Statuses Including SYNCED_TO_ODOO and DEAD_LETTER

| Field | Value |
|-------|-------|
| **ID** | AF-044 |
| **Title** | WebSocket "all" mode returns transactions in every sync status including already-synced and permanently-failed records |
| **Module** | POS Integration (Odoo) |
| **Severity** | Medium |
| **Category** | UI Not Reflecting Backend State |
| **Description** | `TransactionBufferDao.getAllForWs()` uses `SELECT * FROM buffered_transactions ORDER BY completed_at DESC LIMIT 500` with NO `sync_status` filter. This returns records in ALL states: PENDING, UPLOADED, SYNCED_TO_ODOO, ARCHIVED, and DEAD_LETTER. In contrast, the "latest" mode (`getUnsyncedForWs`) correctly excludes `SYNCED_TO_ODOO` and `ARCHIVED`. When Odoo POS sends `mode: "all"`, it receives: (a) SYNCED_TO_ODOO records that were already processed through the Odoo ERP pipeline — the POS may attempt to re-process them, creating duplicate sales orders. (b) DEAD_LETTER records that permanently failed cloud upload — the POS processes them locally while the cloud never has them, creating a reconciliation gap. (c) ARCHIVED records already past their retention window. Additionally, the "latest" mode (`getUnsyncedForWs`) excludes SYNCED_TO_ODOO and ARCHIVED but does NOT exclude DEAD_LETTER (line 291: `NOT IN ('SYNCED_TO_ODOO', 'ARCHIVED')`), so DEAD_LETTER records are also visible through the "latest" mode. |
| **Evidence** | `buffer/dao/TransactionBufferDao.kt` lines 309–313: `getAllForWs()` — `SELECT * FROM buffered_transactions ORDER BY completed_at DESC LIMIT 500` — no status filter. Lines 289–297: `getUnsyncedForWs()` — `WHERE sync_status NOT IN ('SYNCED_TO_ODOO', 'ARCHIVED')` — missing DEAD_LETTER. |
| **Impact** | POS "all" mode shows 500 records including already-processed, archived, and dead-letter transactions. Operators may re-enter already-synced transactions in Odoo. DEAD_LETTER records processed by POS but absent from cloud create financial discrepancies. |
| **Recommended Fix** | Add sync_status filtering to `getAllForWs()`: `WHERE sync_status NOT IN ('SYNCED_TO_ODOO', 'ARCHIVED', 'DEAD_LETTER')`. Add DEAD_LETTER to the `getUnsyncedForWs()` exclusion list as well: `WHERE sync_status NOT IN ('SYNCED_TO_ODOO', 'ARCHIVED', 'DEAD_LETTER')`. This aligns both WebSocket query modes with the intended POS workflow: only show transactions that are active in the sync pipeline. |

---

## AF-045: manager_update and attendant_update Do Not Validate Transaction Existence — Silent No-Op on Invalid IDs

| Field | Value |
|-------|-------|
| **ID** | AF-045 |
| **Title** | Update handlers execute DAO mutations against non-existent transaction IDs with no error feedback |
| **Module** | POS Integration (Odoo) |
| **Severity** | Medium |
| **Category** | Inconsistent Form Handling |
| **Description** | `handleManagerUpdate()` and `handleAttendantUpdate()` take `transaction_id` from the inbound JSON and pass it directly to `transactionDao.updateOdooFields()`, `updateAddToCart()`, and `markDiscarded()` without verifying the transaction exists. Room `@Query("UPDATE ... WHERE fcc_transaction_id = :transactionId")` silently succeeds with 0 affected rows when no matching record exists. The handler then calls `transactionDao.getByFccTransactionId(transactionId)` which returns null, and the broadcast is silently skipped. The POS client receives NO response — no error, no confirmation, no broadcast. From the POS perspective, the command was sent but the server went silent. This is indistinguishable from a network failure or server crash. In `handleManagerUpdate`, if the `isOnlyAddToCart` check passes (line 116), the handler returns immediately before the existence check, making the no-op completely invisible. The `handleManagerManualUpdate()` handler has the same issue: `markDiscarded()` on a non-existent ID succeeds silently, but at least it sends a response. |
| **Evidence** | `websocket/OdooWsMessageHandler.kt` lines 89–123: `handleManagerUpdate()` — no existence check before DAO calls. Lines 134–173: `handleAttendantUpdate()` — same pattern. `buffer/dao/TransactionBufferDao.kt` line 325: `UPDATE ... WHERE fcc_transaction_id = :transactionId` — returns 0 rows if not found, but return value is `Unit` (void). |
| **Impact** | POS sends update commands for transactions that don't exist (stale IDs, typos, race conditions where the transaction was archived) and receives no feedback. The POS may display the transaction as "updated" in its local state while the edge agent never made any change. Multi-terminal workflows where one terminal archives a transaction while another tries to update it are particularly affected. |
| **Recommended Fix** | Check existence before mutation: `val existing = transactionDao.getByFccTransactionId(transactionId); if (existing == null) { sendJson(session, WsErrorResponse(message = "Transaction not found: $transactionId")); return }`. Alternatively, change the DAO `UPDATE` queries to return `Int` (affected row count) and check the return value. |

---

## AF-046: attendant_pump_count_update Sends "updated" ACK Without Persisting Any Data

| Field | Value |
|-------|-------|
| **ID** | AF-046 |
| **Title** | Attendant pump count update handler acknowledges success but performs no database mutation — limits not enforced |
| **Module** | POS Integration (Odoo) |
| **Severity** | Medium |
| **Category** | Broken Workflows |
| **Description** | `handleAttendantPumpCountUpdate()` at lines 265–291 processes each item in the inbound array and sends a `WsAttendantPumpCountAck(status = "updated")` response for each. However, no database mutation occurs. The comment at line 276 states "In the legacy system, this updated an attendant pump count table." The new architecture has no attendant pump count table and no equivalent tracking mechanism. The POS receives `status: "updated"` for every item, believing the per-attendant transaction limits are enforced. In reality, the limits are not stored or checked anywhere. If Odoo POS uses this mechanism to enforce that each attendant can only process N transactions per shift, the enforcement is completely non-functional. Attendants can process unlimited transactions. |
| **Evidence** | `websocket/OdooWsMessageHandler.kt` lines 265–291: loop iterates items, constructs `WsAttendantPumpCountAck(status = "updated")`, sends ack. No DAO call, no state mutation. No attendant pump count entity or table exists in the Room database schema. |
| **Impact** | Per-attendant transaction limits configured in Odoo POS are not enforced. Attendants can process unlimited transactions despite the POS displaying configured limits. This may violate operational policies where attendant limits prevent fraud or manage shift handovers. |
| **Recommended Fix** | Either implement the attendant pump count tracking (add a Room entity, DAO, and enforce limits in transaction ingestion), or send an honest response: `status: "not_supported"` with a message explaining that the new architecture does not enforce attendant limits. If the limits are no longer part of the business requirements, document the deprecation and coordinate with the Odoo POS team. |

---

## AF-047: manager_manual_update Does Not Broadcast to Other Clients — Stale State on Multi-Terminal Setups

| Field | Value |
|-------|-------|
| **ID** | AF-047 |
| **Title** | Manual approval/discard response sent only to requesting client — other terminals show stale transaction state |
| **Module** | POS Integration (Odoo) |
| **Severity** | Medium |
| **Category** | UI Not Reflecting Backend State |
| **Description** | `handleManagerManualUpdate()` at lines 300–315 calls `transactionDao.markDiscarded(transactionId, now)` to set `is_discard = 1`, then sends the response only to the requesting session via `sendJson(session, response)`. It does NOT call `broadcastToAll()`. In contrast, `handleManagerUpdate()` broadcasts updates to all connected clients (line 122), and `handleAttendantUpdate()` broadcasts both cart and order changes (lines 153, 171). This inconsistency means that when a manager approves/discards a transaction on Terminal A, Terminals B through E continue showing the transaction in its previous state. The stale state persists until the next `mode: "latest"` poll from each terminal, or until the POS's own refresh cycle triggers. In a busy forecourt with 5+ terminals, this can take 10–30 seconds depending on the POS polling configuration. During this window, another operator on Terminal B may attempt to process the same (now-discarded) transaction, creating confusion. |
| **Evidence** | `websocket/OdooWsMessageHandler.kt` lines 300–315: `sendJson(session, response)` — unicast only. No `broadcastToAll()` call. Compare with lines 118–123 (`handleManagerUpdate`): `broadcastToAll("transaction_update", ...)`. Compare with lines 149–154 (`handleAttendantUpdate`): `broadcastToAll("transaction_update", ...)`. |
| **Impact** | Multi-terminal POS setups show stale transaction states after manual approvals. Operators may attempt to process discarded transactions, causing confusion and requiring manual correction. |
| **Recommended Fix** | Add a `broadcastToAll` call after `markDiscarded`: `broadcastToAll("transaction_update", wsJson.encodeToJsonElement(response))` or re-fetch the updated transaction and broadcast its current state. This aligns with the broadcast pattern used by `handleManagerUpdate` and `handleAttendantUpdate`. |

---

## AF-048: txIdCounter Resets on Process Restart — Odoo POS Sees Duplicate Integer IDs

| Field | Value |
|-------|-------|
| **ID** | AF-048 |
| **Title** | Auto-incrementing WebSocket transaction `id` field resets to 0 on every process restart — collides with previous session IDs |
| **Module** | POS Integration (Odoo) |
| **Severity** | Low |
| **Category** | Incorrect State Updates |
| **Description** | `OdooWsModels.kt` line 104 defines `private val txIdCounter = AtomicInteger(0)`. This counter is used in `BufferedTransaction.toWsDto()` at line 126: `id = txIdCounter.incrementAndGet()`. The counter lives in process memory and resets to 0 on every process restart (service restart, device reboot, crash recovery). After a restart, the WebSocket server sends `id: 1, id: 2, id: 3, ...` which overlap with IDs from the previous session. The `id` field maps to the legacy `PumpTransactions.id` integer field which the Odoo POS parses by name (per the DTO comments). If the Odoo POS uses this `id` field for local dedup, ordering, or record identity (common for integer primary keys), it will incorrectly merge, overwrite, or skip transactions that share IDs with the previous session. The `transaction_id` field (string, FCC-assigned) is the actual unique identifier, but the legacy POS code may rely on the integer `id` for display ordering or selection. |
| **Evidence** | `websocket/OdooWsModels.kt` line 104: `private val txIdCounter = AtomicInteger(0)` — process-scoped, resets on restart. Line 126: `id = txIdCounter.incrementAndGet()` — starts from 1 on every restart. |
| **Impact** | After process restart, POS terminals that cache transaction data from the previous session may see ID collisions. If the POS uses the integer `id` for record identity, different transactions will appear to be the same record. Low severity because the `transaction_id` string field is the canonical identifier and most POS implementations use it for business logic. |
| **Recommended Fix** | Initialize `txIdCounter` from the database at startup: `val maxId = transactionDao.getMaxWsId(); txIdCounter.set(maxId ?: 0)`. Alternatively, use a timestamp-based ID (e.g., `System.currentTimeMillis().toInt()`) or derive the integer from the database `rowId`. If the legacy POS only uses `transaction_id` for business logic, document that `id` is session-scoped and not stable across restarts. |

---

## AF-049: NetworkBinder.onLost Unconditionally Nulls WiFi/Mobile State — Breaks on WiFi Roaming

| Field | Value |
|-------|-------|
| **ID** | AF-049 |
| **Title** | NetworkBinder.onLost sets state to null without verifying which network was lost — stale null on WiFi handoff |
| **Module** | Connectivity |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Description** | `NetworkBinder.wifiCallback.onLost()` at line 58 sets `_wifiNetwork.value = null` unconditionally — it does not check whether the lost `Network` object is the same as the currently stored `_wifiNetwork.value`. The same applies to `mobileCallback.onLost()` at line 70. On Android, during WiFi roaming or when the device transitions between access points, the system may call `onAvailable(networkB)` for the new AP before calling `onLost(networkA)` for the old AP. The sequence is: (1) `onAvailable(networkA)` → `_wifiNetwork = A`; (2) WiFi roam begins; (3) `onAvailable(networkB)` → `_wifiNetwork = B`; (4) `onLost(networkA)` → `_wifiNetwork = null` (BUG: should only null if `A == current`). After step 4, `_wifiNetwork` is null even though WiFi network B is available. Downstream effects: `ConnectivityManager.logProbeNetwork` reports "no WiFi" for the FCC probe. `FccAdapterFactory.resolve()` for DOMS returns a `socketBinder` that reads `binder.wifiNetwork.value` — this returns null, so the DOMS TCP socket goes unbound and routes over default OS routing (potentially mobile data) instead of the station LAN. The FCC probe may succeed via default routing, so `ConnectivityState` shows `FULLY_ONLINE`, masking the fact that FCC traffic is on the wrong network. The WiFi state only recovers when a new `onAvailable` fires (WiFi toggles or reconnects) or when `onCapabilitiesChanged` (not implemented) fires for networkB. |
| **Evidence** | `connectivity/NetworkBinder.kt` lines 57–60: `override fun onLost(network: Network) { _wifiNetwork.value = null }` — no check of `network == _wifiNetwork.value`. Lines 69–72: same pattern for mobile. Compare with Android documentation: "onLost is called for each network individually; the callback should only clear state if the lost network matches the currently tracked network." |
| **Impact** | On devices that perform WiFi roaming (enterprise APs with multiple BSSIDs, common in fuel stations with large coverage areas), FCC adapter sockets may be incorrectly unbound from WiFi and route over mobile data. DOMS JPL TCP connections to FCC hardware on the station LAN would fail (FCC not reachable via mobile). The failure manifests as intermittent FCC_UNREACHABLE states during AP handoff, lasting until the next `onAvailable` callback. |
| **Recommended Fix** | Guard `onLost` to only null the flow if the lost network matches the current value: `if (_wifiNetwork.value == network) { _wifiNetwork.value = null }`. Apply the same pattern to `mobileCallback.onLost`: `if (_mobileNetwork.value == network) { _mobileNetwork.value = null }`. |

---

## AF-051: storeTokens() Is Not Atomic — Device Token Persisted While Refresh Token Encryption Fails

| Field | Value |
|-------|-------|
| **ID** | AF-051 |
| **Title** | storeTokens() writes device token to EncryptedSharedPreferences before refresh token encryption — partial token state on Keystore error |
| **Module** | Security |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Description** | `KeystoreDeviceTokenProvider.storeTokens()` at lines 181–198 performs a two-step write: (1) encrypt device token via `keystoreManager.storeSecret(ALIAS_DEVICE_JWT, ...)`, then persist the blob via `encryptedPrefs.storeDeviceTokenBlob()` (line 184); (2) encrypt refresh token via `keystoreManager.storeSecret(ALIAS_REFRESH_TOKEN, ...)`, then persist (line 192). If step 1 succeeds but step 2 fails (Keystore temporarily unavailable between the two calls — e.g., due to a StrongBox timeout, concurrent key generation, or TEE resource exhaustion), the method returns `false` at line 195. However, the device token blob from step 1 was already written to `EncryptedSharedPreferences` via `apply()` (line 135 in `EncryptedPrefsManager`). The caller (`refreshAccessToken()` at line 113) sees `false`, logs an error, and returns `false` to the caller. No rollback of the device token blob occurs. The system is now in an inconsistent state: the NEW device token is stored, but the OLD refresh token remains. On the next cloud API call, `getAccessToken()` returns the new device token (valid). When the new token eventually expires (24h), `refreshAccessToken()` uses the old refresh token. If the cloud enforces single-use refresh tokens (standard OAuth practice), the old token was invalidated when the cloud issued the new pair — the refresh fails, and the device enters unnecessary re-provisioning. |
| **Evidence** | `sync/KeystoreDeviceTokenProvider.kt` lines 181–198: `storeTokens()` — sequential write with no rollback. Line 184: `encryptedPrefs.storeDeviceTokenBlob(...)` writes before step 2 is attempted. Line 195: early return `false` without reverting the device token blob. `security/EncryptedPrefsManager.kt` line 135: `storeDeviceTokenBlob` uses `apply()` — already queued for async disk write. |
| **Impact** | On Keystore transient failures during token refresh, the device enters a split-token state where the access token is valid but the refresh token is stale. After the access token expires (24h), the stale refresh token fails, forcing unnecessary re-provisioning. This requires a new bootstrap token from the portal — a manual process involving IT coordination. |
| **Recommended Fix** | Make `storeTokens()` atomic: encrypt BOTH tokens first, then persist BOTH blobs. If either encryption fails, persist nothing. Pattern: `val devEnc = keystoreManager.storeSecret(...deviceToken); val refEnc = keystoreManager.storeSecret(...refreshToken); if (devEnc == null || refEnc == null) return false; encryptedPrefs.storeDeviceTokenBlob(...); encryptedPrefs.storeRefreshTokenBlob(...)`. This ensures either both blobs are written or neither is. |

---

## AF-052: Local FCC Overrides Survive Re-Provisioning — Old Site's FCC Config Applied to New Site

| Field | Value |
|-------|-------|
| **ID** | AF-052 |
| **Title** | Re-provisioning clears credentials and identity but does not clear LocalOverrideManager — old site's FCC host, port, and credential overrides leak to new site |
| **Module** | Security / Site Configuration |
| **Severity** | High |
| **Category** | Broken Workflows |
| **Description** | `DecommissionedActivity.startReProvisioning()` at lines 55–56 calls `keystoreManager.clearAll()` and `encryptedPrefs.clearAll()`. `ProvisioningViewModel.handleRegistrationSuccess()` at lines 133–134 performs the same two clears. Neither calls `localOverrideManager.clearAllOverrides()`. `LocalOverrideManager` uses a SEPARATE `EncryptedSharedPreferences` file (`fcc_local_overrides`, line 30) from `EncryptedPrefsManager` (`fcc_edge_secure_prefs`, line 32). Clearing `EncryptedPrefsManager` does not touch `LocalOverrideManager`'s file. After re-provisioning, `ConfigManager.toAgentFccConfig()` calls `localOverrideManager.getOverriddenFccConfig(cloudConfig)` (line 157 in `LocalOverrideManager`) which overlays saved overrides onto the new site's cloud-delivered config. The result: (a) `fccHost` — the new site's FCC is at `192.168.1.200` but the old override `192.168.1.100` takes precedence. The adapter connects to the old site's FCC (if reachable) or fails to connect entirely. (b) `fccCredential` — the old site's FCC access code is used to authenticate against the new site's FCC, which will fail with an authentication error. The FCC adapter enters a permanent reconnect loop. (c) `fccPort` / `jplPort` — wrong ports cause connection failures. (d) `wsPort` — the WebSocket server binds to the old port, making it unreachable for the new site's POS terminals. The technician on-site will see FCC_UNREACHABLE errors and must manually navigate to `SettingsActivity` to clear each override — assuming they know overrides exist from the previous installation. There is no indication on the diagnostics screen that local overrides are active and conflicting with the cloud config. |
| **Evidence** | `ui/DecommissionedActivity.kt` lines 55–56: `keystoreManager.clearAll()` and `encryptedPrefs.clearAll()` — no `localOverrideManager` call. `ui/ProvisioningViewModel.kt` lines 133–134: same pattern. `config/LocalOverrideManager.kt` line 30: `PREFS_FILE = "fcc_local_overrides"` — separate file. Lines 157–163: `getOverriddenFccConfig()` applies overrides unconditionally. Grep for `clearAllOverrides` in `DecommissionedActivity`, `ProvisioningViewModel`: 0 results. |
| **Impact** | After re-provisioning a device to a different site, the agent attempts to connect to the OLD site's FCC hardware with wrong credentials, wrong host/port, and wrong WebSocket port. All FCC operations (transaction ingestion, pre-auth, pump status) fail. The new site is non-functional until a technician manually clears overrides in the Settings screen. This is particularly dangerous because the failure mode (FCC_UNREACHABLE) is the same as a genuine network issue — the technician may not realize local overrides from a previous installation are the root cause. |
| **Recommended Fix** | Add `localOverrideManager.clearAllOverrides()` in both `DecommissionedActivity.startReProvisioning()` (after `encryptedPrefs.clearAll()`) and `ProvisioningViewModel.handleRegistrationSuccess()` (after `encryptedPrefs.clearAll()`). Inject `LocalOverrideManager` into `DecommissionedActivity` via Koin: `private val localOverrideManager: LocalOverrideManager by inject()`. Also add a diagnostic indicator: when local overrides are active, the DiagnosticsActivity should display "Local overrides active: FCC host, port" so technicians can identify override conflicts. |

---

## AF-053: saveRegistration() Does Not Check commit() Return Value — Silent Registration Persistence Failure

| Field | Value |
|-------|-------|
| **ID** | AF-053 |
| **Title** | saveRegistration() ignores the boolean result from SharedPreferences.commit() — registration data silently lost on disk-full or I/O error |
| **Module** | Security |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Description** | `EncryptedPrefsManager.saveRegistration()` at lines 163–172 chains `.commit()` which returns `Boolean` indicating whether the write was committed to persistent storage. The return value is silently discarded — `saveRegistration` has a `Unit` return type. `SharedPreferences.Editor.commit()` returns `false` when the underlying disk write fails (disk full, I/O error, EncryptedSharedPreferences AES encryption failure). On the Urovo i9100 with limited eMMC storage, disk-full conditions are a real risk (exacerbated by AF-034 where CleanupWorker never runs, causing unbounded database growth). When `saveRegistration()` silently fails: (1) `isRegistered` is `true` in the in-memory SharedPreferences map (commit updates memory even if disk write fails), (2) `isRegistered` is `false` (or absent) on disk. The caller (`ProvisioningViewModel.handleRegistrationSuccess()`) proceeds to start the foreground service, which reads `isRegistered=true` from the in-memory map and operates normally. On process restart (e.g., device reboot), `LauncherActivity` reads `isRegistered=false` from disk and routes to `ProvisioningActivity`. The user must re-register, but the tokens stored in Keystore at lines 149 are still valid. A second registration with the same bootstrap token fails if the cloud enforces single-use tokens. The user is stranded: registered in Keystore but not in SharedPreferences, with no way to re-register (token consumed) or proceed (not flagged as registered on disk). |
| **Evidence** | `security/EncryptedPrefsManager.kt` lines 163–172: `prefs.edit()...commit()` — return value discarded. Compare with `isDecommissioned` setter (line 102) which also uses `commit()` but discards the return. Kotlin `Unit` return type means the caller cannot detect failure. |
| **Impact** | On disk-full conditions: registration data lost on process restart, user stranded between Keystore (tokens present) and SharedPreferences (not registered). Requires IT intervention to either clear app data (losing tokens) and re-issue a bootstrap token, or free disk space and re-register. |
| **Recommended Fix** | Change `saveRegistration()` to return `Boolean` — the return value of `.commit()`. The caller (`ProvisioningViewModel.handleRegistrationSuccess()`) should check the result and throw an error if persistence failed: `if (!encryptedPrefs.saveRegistration(...)) throw IllegalStateException("Failed to persist registration data — disk may be full")`. This surfaces the error before the service starts. |

---

## AF-050: recoveryThreshold + FULLY_OFFLINE Initial State Creates ~30s Cold-Start Delay

| Field | Value |
|-------|-------|
| **ID** | AF-050 |
| **Title** | ConnectivityManager requires 2 consecutive probe successes to transition from initial FULLY_OFFLINE — first ~30s of service life are idle |
| **Module** | Connectivity |
| **Severity** | Medium |
| **Category** | Broken Workflows |
| **Description** | `ConnectivityManager` initializes `_state` to `FULLY_OFFLINE` (line 64) with `internetUp = false` and `fccUp = false`. Recovery from DOWN to UP requires `config.recoveryThreshold` (default 2) consecutive successes (lines 151–153, 165–167). The first probe runs immediately at t=0 (per spec). On success, `consecSuccesses` reaches 1 — still below threshold. The second probe runs at t=30s+jitter. On success, `consecSuccesses` reaches 2 and `up` flips to true. The state transition fires at ~30s. During this 30s window, `CadenceController.runTick()` checks `connectivityManager.state.value`, sees `FULLY_OFFLINE`, and enters the FULLY_OFFLINE branch (line 491) which performs NO work — no FCC polling, no cloud upload, no config poll, nothing. The CadenceController does observe the transition via `observeConnectivityTransitions()`, so when the state flips to `FULLY_ONLINE` at ~30s, `onTransition` fires and triggers immediate replay (line 350). But FCC polling only happens on the next regular cadence tick — which uses the FULLY_OFFLINE interval (60s) for the first tick before the state change adjusts the interval. Effective cold-start delay: FCC transactions generated in the first ~30s are not buffered until the first post-transition FCC poll. The `recoveryThreshold` was added to prevent oscillation (H-10 anti-oscillation pattern), which is correct for DOWN→UP recovery. But applying the same threshold to the initial FULLY_OFFLINE→UP transition is unnecessarily conservative — the initial state has no prior "DOWN" evidence to protect against oscillation. |
| **Evidence** | `connectivity/ConnectivityManager.kt` line 64: `MutableStateFlow(ConnectivityState.FULLY_OFFLINE)`. Lines 151–153: `if (internetConsecSuccesses >= config.recoveryThreshold) { internetUp = true }`. Line 56: `val probeIntervalMs: Long = 30_000L`. `runtime/CadenceController.kt` lines 491–493: FULLY_OFFLINE branch performs no work. |
| **Impact** | Every service startup (boot, process restart, START_STICKY restart) has a ~30s window where no productive work occurs. FCC transactions generated during this window are buffered by the FCC hardware but not pulled by the edge agent. On a busy forecourt, 0–1 transactions may be delayed by up to 60s (until the first cadence tick after transition). |
| **Recommended Fix** | Skip the recovery threshold on the initial transition from FULLY_OFFLINE. Add an `isInitialProbe` flag that bypasses the threshold on the first round of probes: `if (isInitialProbe || consecSuccesses >= config.recoveryThreshold) { up = true }`. Set `isInitialProbe = false` after the first successful transition. Alternatively, set `recoveryThreshold` to 1 for the initial state (before any state transition has occurred), then switch to 2 after the first transition. |
