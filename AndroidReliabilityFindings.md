# Android Lifecycle & Reliability Findings

**Audit date:** 2026-03-13
**Scope:** `src/edge-agent/` — all Activities, ViewModels, Services, Workers, DI, and infrastructure components

---

## 1. Executive Summary

The Android edge-agent codebase demonstrates **strong lifecycle safety discipline**. All Activities use `lifecycleScope`, the sole ViewModel uses `viewModelScope`, and the foreground service manages its own `CoroutineScope` with proper cancellation. No `GlobalScope` usage was found. No Fragments are used (all UI is programmatic `View`-based). Configuration change handling is thorough across all stateful Activities.

Several minor-to-moderate findings were identified that could cause issues under edge conditions (process death, rapid rotation, low memory).

---

## 2. Component Inventory

| Component | Type | Lifecycle Scope | Status |
|---|---|---|---|
| `SplashActivity` | Activity | Handler + onDestroy cleanup | OK |
| `LauncherActivity` | Activity | Stateless router, immediate finish() | OK |
| `ProvisioningActivity` | Activity | lifecycleScope + ViewModel StateFlow | OK |
| `SettingsActivity` | Activity | lifecycleScope + savedInstanceState | OK |
| `DiagnosticsActivity` | Activity | lifecycleScope + onPause cancel | OK |
| `DecommissionedActivity` | Activity | Stateless (no coroutines) | OK |
| `ProvisioningViewModel` | AndroidViewModel | viewModelScope | OK |
| `EdgeAgentForegroundService` | Service | Custom CoroutineScope (SupervisorJob) | OK |
| `CloudUploadWorker` | Worker (manual) | Suspend function, no scope | OK |
| `PreAuthCloudForwardWorker` | Worker (manual) | Suspend function, semaphore-bounded | OK |
| `CleanupWorker` | Worker (manual) | Suspend function, no scope | OK |
| `ConfigPollWorker` | Worker (manual) | Suspend function, no scope | OK |
| `ConnectivityManager` | Infrastructure | Injected CoroutineScope | OK |
| `NetworkBinder` | Infrastructure | Network callbacks + StateFlow | OK |
| `TelemetryReporter` | Infrastructure | Suspend functions, no scope | OK |

---

## 3. Findings

### LR-001 — DecommissionedActivity: AlertDialog Not Tracked for Lifecycle Dismissal [LOW] — FIXED

**File:** `ui/DecommissionedActivity.kt:127-136`
**Issue:** The re-provision confirmation `AlertDialog` is created inline via `AlertDialog.Builder(...).show()` without storing a reference. If a configuration change (rotation) occurs while the dialog is visible, the dialog will leak its Activity reference. `SettingsActivity` correctly tracks `activeDialog` and dismisses in `onDestroy` — `DecommissionedActivity` does not.
**Impact:** Window-leaked `AlertDialog` causes a `WindowManager$BadTokenException` log entry (non-fatal on most devices, but technically a leak). On Android 14+, the OS may terminate the process more aggressively during memory pressure if leaked windows are detected.
**Fix:** Store the dialog reference and dismiss in `onDestroy`, consistent with `SettingsActivity`.
**Resolution:** Added `activeDialog` property, stored dialog reference on creation, dismiss in `onDestroy()`, and clear reference on positive/negative/cancel callbacks — consistent with `SettingsActivity` pattern.

---

### LR-002 — SettingsActivity: UI Update After Possible Destruction in Reset Flow [LOW] — FIXED

**File:** `ui/SettingsActivity.kt:316-327`
**Issue:** The "Reset to Cloud Defaults" flow launches `lifecycleScope.launch(Dispatchers.IO)` and then switches to `withContext(Dispatchers.Main)` to call `populateFields()` and `Toast.makeText()`. While `lifecycleScope` will cancel on destruction, there is a narrow window between `onStop` and `onDestroy` where the coroutine may still be active and `withContext(Dispatchers.Main)` may execute on a stopped-but-not-yet-destroyed Activity. This is technically safe (Views are still valid), but `Toast.makeText` with `this@SettingsActivity` context can produce a "leaked window" warning if the Activity finishes between the IO work and the Main dispatch.
**Impact:** Cosmetic log warnings. No crash.
**Recommendation:** Add `if (isFinishing || isDestroyed) return@withContext` guard before UI updates, consistent with `DiagnosticsActivity:142`.
**Resolution:** Added `if (isFinishing || isDestroyed) return@withContext` guard at the top of the `withContext(Dispatchers.Main)` block in `resetToCloudDefaults()`.

---

### LR-003 — SettingsActivity: Save Flow Also Missing Destruction Guard [LOW] — FIXED

**File:** `ui/SettingsActivity.kt:255-305`
**Issue:** Same pattern as LR-002. The `saveAndReconnect()` method launches on `Dispatchers.IO` and then `withContext(Dispatchers.Main)` for UI updates without an `isFinishing`/`isDestroyed` guard.
**Impact:** Same as LR-002 — cosmetic, no crash.
**Resolution:** Added `if (isFinishing || isDestroyed) return@withContext` guard in both the success and error `withContext(Dispatchers.Main)` blocks in `saveAndReconnect()`.

---

### LR-004 — DI Singleton CoroutineScope Is Never Cancelled [MEDIUM] — FIXED

**File:** `di/AppModule.kt:66-72`
**Issue:** The shared `single<CoroutineScope>` (SupervisorJob + Dispatchers.IO) is created as a Koin singleton and is never cancelled. This scope is used by `ConnectivityManager`, `PreAuthHandler`, `NetworkBinder`, `OdooWebSocketServer`, `LocalApiServer`, and `CadenceController`. Since the `Application` process lives as long as the Android process, this is **intentional and correct** for a foreground-service-based architecture. However, if the service is destroyed via `stopSelf()` (decommission/re-provision paths), the shared scope remains active and its child coroutines continue running.
**Impact:** After `EdgeAgentForegroundService.onDestroy()` calls `serviceScope.cancel()`, the service's own coroutines are cancelled — but `ConnectivityManager` and `CadenceController` still run in the shared DI scope. `CadenceController.stop()` and `ConnectivityManager.stop()` are called explicitly in `onDestroy`, which cancels their `probeJobs`/loop jobs. The risk is that any new coroutine launched in the shared scope after service teardown (e.g., by a network callback) will execute without a service context.
**Recommendation:** Consider adding a `stop()` / `cancelChildren()` call on the shared scope when the service enters the decommission/re-provision path, or document the current design as intentional (the process is expected to terminate shortly after navigation to Provisioning/Decommissioned).
**Resolution:** Changed `appScope.cancel()` to `appScope.coroutineContext[Job]?.cancelChildren()` in `EdgeAgentForegroundService.onDestroy()`. This cancels all running child coroutines but keeps the Koin singleton scope alive and usable if the service restarts via START_STICKY after decommission/re-provision paths.

---

### LR-005 — ConnectivityManager Scope Lifetime Mismatch [LOW] — FIXED

**File:** `di/AppModule.kt:180-210`, `connectivity/ConnectivityManager.kt:93-98`
**Issue:** `ConnectivityManager` receives the shared DI `CoroutineScope` and launches probe loops in it. When `stop()` is called, it cancels `probeJobs` (the parent Job of both probe loops). This is correct. However, the scope reference itself is still alive, meaning `start()` can be called again on the same scope. This is **safe by design** but could surprise a developer who expects `stop()` to be terminal.
**Impact:** None in current usage — `start()`/`stop()` are called exactly once per service lifetime.
**Resolution:** Added a `stopped` flag that makes `stop()` terminal. Once `stop()` is called, subsequent `start()` calls are ignored with a warning log. This prevents accidental restart on the same scope and makes the lifecycle contract explicit.

---

### LR-006 — NetworkBinder: Callbacks Not Unregistered on Process Death [LOW]

**File:** `connectivity/NetworkBinder.kt:79-99`
**Issue:** `NetworkBinder.start()` registers two `NetworkCallback`s with the system `ConnectivityManager`. These are unregistered in `NetworkBinder.stop()`, which is called from `EdgeAgentForegroundService.onDestroy()`. If the process is killed by the OOM killer (bypassing `onDestroy`), the callbacks are implicitly cleaned up by the OS. This is **correct and expected behavior** — Android unregisters all callbacks when the process dies. No leak.
**Impact:** None. Noted for completeness.

---

### LR-007 — TelemetryReporter: Sticky Broadcast Battery Intent [INFO]

**File:** `sync/TelemetryReporter.kt:165`
**Issue:** `context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))` uses the sticky broadcast pattern to read battery state. This is the standard Android approach and does **not** register a persistent receiver. The `null` receiver means "return the current sticky Intent immediately." No lifecycle cleanup is needed.
**Impact:** None. This is correct usage.

---

### LR-008 — ProvisioningActivity: Flow Collection Without repeatOnLifecycle [INFO]

**File:** `ui/ProvisioningActivity.kt:161-198`
**Issue:** The StateFlow collection uses `lifecycleScope.launch { flow.collect {} }` rather than the `repeatOnLifecycle(STARTED)` pattern recommended by Google's lifecycle-aware collection guidance. With `lifecycleScope.launch`, the collector remains active even when the Activity is in the background (between `onStop` and `onDestroy`).
**Impact:** For `StateFlow` this is a non-issue — `StateFlow` does not emit unless the value changes, so there is no wasted work from background collection. The `ProvisioningViewModel` only emits during active registration. This is a documentation-level note only. The current pattern is functionally correct and marginally simpler.

---

### LR-009 — DiagnosticsActivity: TextViews Accumulate in Pool Without Bound [LOW] — FIXED

**File:** `ui/DiagnosticsActivity.kt:217-280`
**Issue:** The P-003 optimization uses `errorTextViews` and `structuredLogTextViews` lists that grow monotonically. If a single refresh cycle returns 30 structured log entries, 30 TextViews are created and retained. If subsequent refreshes return 0 entries, those TextViews are hidden (`GONE`) but remain in the list and in the view hierarchy. Over long sessions, this pool could grow if the max entry count fluctuates.
**Impact:** Minimal in practice — the pools are bounded by `RECENT_LOG_LIMIT` (15) and `STRUCTURED_LOG_LIMIT` (30). The maximum waste is ~45 hidden TextViews, which is negligible on any device.
**Resolution:** Added trimming logic that removes excess TextViews from both the pool lists and the view hierarchy when fewer entries are needed. Pools now shrink to exactly the needed count on each refresh, eliminating monotonic growth while preserving the P-003 reuse optimization.

---

### LR-010 — Service Monitor Loops: `while(true)` Without Scope Cancellation Check [LOW] — FIXED

**File:** `service/EdgeAgentForegroundService.kt:290, 319`
**Issue:** `monitorReprovisioningState()` and `monitorDecommissionedState()` use `while (true)` with `delay()`. When the `serviceScope` is cancelled (in `onDestroy`), the `delay()` call will throw `CancellationException`, which is the correct Kotlin coroutines cancellation mechanism. However, the `catch (e: Exception)` block on lines 302 and 334 catches `Exception`, which is a superclass of `CancellationException`. In Kotlin coroutines, catching `CancellationException` and not rethrowing it **prevents cancellation**.
**Impact:** When `serviceScope.cancel()` is called in `onDestroy`, the monitor loops will catch the `CancellationException`, increment `consecutiveFailures`, log an error, and then call `delay()` again — which will immediately throw another `CancellationException`. After 10 consecutive "failures" (which are really cancellation exceptions), the loop will exit via the `MAX_CONSECUTIVE_MONITOR_FAILURES` guard. The loops do eventually stop, but they produce 10 spurious error log entries and delay actual cancellation by the time it takes to catch and re-enter 10 iterations.
**Resolution:** Added `if (e is kotlinx.coroutines.CancellationException) throw e` at the top of both catch blocks in `monitorReprovisioningState()` and `monitorDecommissionedState()`. Cancellation is now immediate with no spurious log entries.

---

### LR-011 — StructuredFileLogger DI Scope Has Orphaned CoroutineScope [LOW]

**File:** `di/AppModule.kt:56-59`
**Issue:** `StructuredFileLogger` is constructed with its own `CoroutineScope(SupervisorJob() + Dispatchers.IO)`. This scope is separate from the shared DI scope and is never cancelled. Since `StructuredFileLogger` is a process-lifetime singleton used by the crash handler, this is **intentionally correct** — it must outlive all other components. However, it means the logger scope will never be cancelled, which is by design.
**Impact:** None. This is intentional. The logger must survive even after the service scope is cancelled.

---

### LR-012 — SettingsActivity: Saves FCC Access Code in savedInstanceState Bundle [MEDIUM] — FIXED

**File:** `ui/SettingsActivity.kt:103-106`
**Issue:** `STATE_FCC_ACCESS_CODE` is persisted in `onSaveInstanceState`. Unlike `ProvisioningActivity` which explicitly avoids saving the provisioning token (AF-001), `SettingsActivity` saves the FCC access code in the Bundle. Bundles are serialized to the Binder transaction buffer and can survive process death in plaintext on disk (in the system's recent tasks serialization).
**Impact:** The FCC access code is a LAN-level credential (used to authenticate with the forecourt controller over the station WiFi). While lower sensitivity than a cloud token, it could be extracted from a Bundle dump on a rooted device. This contradicts the security posture established by AF-001 in `ProvisioningActivity`.
**Resolution:** Removed `STATE_FCC_ACCESS_CODE` from both `onSaveInstanceState()` and the `savedInstanceState` restoration in `onCreate()`. After process death/rotation, the access code field will be re-populated from `LocalOverrideManager` (if an override exists) or left blank (requiring re-entry). Consistent with AF-001 security posture.

---

## 4. Positive Patterns Observed

| Pattern | Evidence |
|---|---|
| **No GlobalScope usage** | Grep across entire codebase: zero occurrences |
| **No Fragment lifecycle** | Zero Fragments — all UI is programmatic Views, eliminating Fragment lifecycle bugs entirely |
| **ViewModel survives rotation** | `ProvisioningViewModel` uses `viewModelScope`; in-flight registration survives Activity recreation (T-003) |
| **Service re-entrancy guard** | `AtomicBoolean serviceStarted` prevents duplicate `onStartCommand` initialization (T-004) |
| **SupervisorJob in service scope** | Child coroutine failures don't cascade (T-008) |
| **CoroutineExceptionHandler** | Both service scope and DI scope log uncaught exceptions |
| **Proper Handler cleanup** | `SplashActivity.onDestroy()` removes pending callbacks |
| **Dialog lifecycle management** | `SettingsActivity` tracks `activeDialog` and dismisses in `onDestroy` |
| **isFinishing/isDestroyed guard** | `DiagnosticsActivity.refreshData()` checks before UI updates |
| **Token not in Bundle** | AF-001: `ProvisioningActivity` explicitly omits provisioning token from `savedInstanceState` |
| **Service stop before credential clear** | AF-004: Service stopped before credentials are cleared in decommission flow |
| **Database clear before credentials** | AF-013: Room database cleared before credentials to prevent cross-site contamination |
| **Degraded mode instead of stopSelf** | H-05: Runtime readiness failure enters degraded mode rather than triggering START_STICKY restart loop |
| **Circuit breakers on all workers** | M-08: CloudUploadWorker, PreAuthCloudForwardWorker, ConfigPollWorker all use circuit breaker with backoff |
| **Global crash handler** | `FccEdgeApplication` installs uncaught exception handler that writes to persistent log before process kill |
| **Network callback cleanup** | `NetworkBinder.stop()` unregisters both WiFi and mobile callbacks with try/catch for already-unregistered |

---

## 5. Summary Table

| ID | Severity | Component | Finding | Status |
|---|---|---|---|---|
| LR-001 | LOW | DecommissionedActivity | AlertDialog not tracked for lifecycle dismissal | **FIXED** |
| LR-002 | LOW | SettingsActivity | UI update after possible destruction (reset flow) | **FIXED** |
| LR-003 | LOW | SettingsActivity | UI update after possible destruction (save flow) | **FIXED** |
| LR-004 | MEDIUM | AppModule DI | Singleton CoroutineScope never cancelled after service teardown | **FIXED** |
| LR-005 | LOW | ConnectivityManager | Scope lifetime mismatch — stop() now terminal | **FIXED** |
| LR-006 | LOW | NetworkBinder | Callbacks cleaned up by OS on process death (correct) | By design |
| LR-007 | INFO | TelemetryReporter | Sticky broadcast battery read (correct) | By design |
| LR-008 | INFO | ProvisioningActivity | Flow collection without repeatOnLifecycle (acceptable for StateFlow) | Accepted |
| LR-009 | LOW | DiagnosticsActivity | TextView pool trimmed on each refresh | **FIXED** |
| LR-010 | LOW | EdgeAgentForegroundService | CancellationException now rethrown in monitor loops | **FIXED** |
| LR-011 | LOW | AppModule DI | Logger scope orphaned (intentional) | By design |
| LR-012 | MEDIUM | SettingsActivity | FCC access code removed from savedInstanceState Bundle | **FIXED** |

**Overall risk: LOW.** All actionable findings have been fixed. The codebase demonstrates mature lifecycle handling with no crash-prone patterns or context leaks. Both MEDIUM findings (LR-004, LR-012) have been resolved.
