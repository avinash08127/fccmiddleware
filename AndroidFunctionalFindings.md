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

---

## AF-020: Advatec FIFO Pre-Auth Match Misattributes Normal Order Receipts Across Pumps

| Field | Value |
|-------|-------|
| **ID** | AF-020 |
| **Title** | FIFO fallback matching correlates Normal Order receipts with unrelated pre-auths on different pumps |
| **Module** | FCC Adapters (Advatec) |
| **Severity** | Medium |
| **Category** | Incorrect State Updates |
| **Description** | `AdvatecAdapter.tryMatchPreAuth()` has a two-strategy matching system: (1) match by CustomerId, (2) FIFO fallback matching the oldest active pre-auth within TTL. The FIFO fallback (lines 676–700) does not filter by pump number — it matches ANY active pre-auth across ALL pumps. When a Normal Order receipt arrives (no pre-auth, `customerId` is blank), and there are active pre-auths on other pumps, the receipt is incorrectly correlated with the oldest pre-auth. The pre-auth is consumed (removed from the map), and the next actual pre-auth receipt for that pump will have no matching entry. This creates two incorrect correlations: (a) Normal Order tagged with wrong OdooOrderId, (b) actual pre-auth receipt unmatched. |
| **Evidence** | `adapter/advatec/AdvatecAdapter.kt` lines 676–700: FIFO loop iterates `activePreAuths` without checking pump number. |
| **Impact** | On multi-pump Advatec sites, Normal Order transactions can steal pre-auth correlations from other pumps, causing incorrect Odoo reconciliation and orphaned pre-auth records. |
| **Recommended Fix** | Remove the FIFO fallback entirely, or restrict it to the same pump: only consider pre-auths where `entry.value.pumpNumber == receipt.pumpNumber` (requires extracting pump from the receipt, which Advatec may not provide). If pump is unavailable in receipts, log a warning and return null instead of guessing. |

---

## AF-021: AdvatecAdapter and AdvatecFiscalizationService Compete for Same Webhook Listener Port

| Field | Value |
|-------|-------|
| **ID** | AF-021 |
| **Title** | Dual webhook listener initialization creates port binding conflict in Scenario C deployments |
| **Module** | FCC Adapters (Advatec) |
| **Severity** | Medium |
| **Category** | Broken Workflows |
| **Description** | Both `AdvatecAdapter.ensureInitialized()` and `AdvatecFiscalizationService.ensureInitialized()` create an `AdvatecWebhookListener` on `config.advatecWebhookListenerPort ?: 8091`. In Scenario A (Advatec as secondary fiscal device), only `AdvatecFiscalizationService` initializes. In Scenario B (Advatec as primary adapter), only `AdvatecAdapter` initializes. But the DI module wires both if the vendor is Advatec — `IngestionOrchestrator` receives the adapter AND the fiscalization service. If both call `ensureInitialized()`, the second `embeddedServer(CIO, port = port)` call will throw `BindException: Address already in use`. The `AdvatecAdapter` catches this and retries on every `fetchTransactions()` call, logging repeated errors. |
| **Evidence** | `adapter/advatec/AdvatecAdapter.kt` line 153: creates listener on default port. `adapter/advatec/AdvatecFiscalizationService.kt` line 199: creates another listener on same default port. |
| **Impact** | In deployments where Advatec serves both as FCC adapter and fiscal device, one of the two webhook listeners will always fail to start, breaking either transaction ingestion or fiscalization. |
| **Recommended Fix** | Share a single `AdvatecWebhookListener` instance between the adapter and the fiscalization service via DI. Alternatively, use separate ports (e.g., 8091 for adapter, 8092 for fiscalization) and configure the Advatec device to post to both. |

---

## AF-022: POST /api/v1/transactions/acknowledge Is a No-Op — Never Marks Transactions as Consumed

| Field | Value |
|-------|-------|
| **ID** | AF-022 |
| **Title** | Acknowledge endpoint queries for existence but performs no database mutation — transactions are never marked as consumed |
| **Module** | Transaction Management |
| **Severity** | High |
| **Category** | Broken Workflows |
| **Description** | `POST /api/v1/transactions/acknowledge` receives a `BatchAcknowledgeRequest` containing a list of transaction IDs. The handler iterates through each ID and calls `dao.getById(id)` to check existence, then returns a `BatchAcknowledgeResponse(acknowledged = found)` with the count of records found. However, no database mutation is performed — there is no `acknowledged` column on `BufferedTransaction`, no DAO method to set an acknowledgement flag, and no sync-status transition occurs. The endpoint KDoc states "Odoo POS marks a batch of transactions as locally consumed" but the implementation only counts records. The POS receives a `200 OK` with an `acknowledged` count, believing the transactions are marked, but nothing changed. Subsequent `GET /api/v1/transactions` calls return the same records, leading to double-consumption by the POS. |
| **Evidence** | `api/TransactionRoutes.kt` lines 138–162: `val found = request.transactionIds.count { id -> dao.getById(id) != null }` — no update statement. `buffer/entity/BufferedTransaction.kt`: no `acknowledged` or `consumed_by_pos` column. `buffer/dao/TransactionBufferDao.kt`: no `markAcknowledged()` method. |
| **Impact** | POS has no mechanism to track which transactions it has already processed. Every poll of `GET /api/v1/transactions` returns previously-seen transactions, forcing the POS to maintain its own dedup state or risk creating duplicate sales orders in Odoo. This defeats the purpose of the acknowledge endpoint documented in the API spec. |
| **Recommended Fix** | Add an `acknowledged_at` nullable TEXT column to `BufferedTransaction` (with a Room migration). Add a DAO method `markAcknowledged(ids: List<String>, now: String)` that sets `acknowledged_at` for matching IDs. Update `getForLocalApi` queries to exclude records where `acknowledged_at IS NOT NULL`, or add a `?includeAcknowledged=false` query parameter. Update the acknowledge endpoint to call the new DAO method instead of just counting. |

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
