# Desktop Reliability Findings

> Reliability, thread safety, and resource management audit of the FCC Desktop Edge Agent.

---

## Application Shell Module

### R-DSK-001
- **Title:** MainWindow.Dispose() may never be called — timer and event subscription leak
- **Module:** Application Shell
- **Severity:** High
- **Category:** Improper resource disposal
- **Description:** `MainWindow` implements `IDisposable` with a `Dispose()` method that cancels the CTS, unsubscribes from connectivity events, and disposes the timer. However, no code explicitly calls `Dispose()` on the MainWindow instance. The `OnClosing` method (force-close path) disposes child pages but does NOT call `Dispose()` on the window itself. Avalonia does not automatically call `IDisposable.Dispose()` on windows when they close — it relies on the developer to do so. The `_statusTimer` will continue firing on the ThreadPool after the window closes, attempting to create DI scopes on a potentially-disposed service provider.
- **Evidence:**
  - `MainWindow.axaml.cs:258-271` — `Dispose()` method exists with cleanup logic
  - `MainWindow.axaml.cs:68-95` — `OnClosing()` disposes pages but never calls `this.Dispose()`
  - `App.axaml.cs:166` — `mainWindow.ForceClose()` — no `Dispose()` call after
  - `App.axaml.cs:199` — decommission handler calls `mainWindow.ForceClose()` — no `Dispose()`
- **Impact:** `_statusTimer` continues firing after window close, hitting a disposed service provider → `ObjectDisposedException` crashes or silent failures. `_connectivity.StateChanged` subscription keeps the window alive in memory (event-based rooting).
- **Recommended Fix:** Call `Dispose()` explicitly in `ForceClose()` or in the `OnClosing` force-close path. Alternatively, move timer disposal into `OnClosing` when `_forceClose` is true.

---

### R-DSK-002
- **Title:** Double-dispose of child pages between OnClosing and Dispose
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Improper resource disposal
- **Description:** Both `OnClosing()` (lines 89-93) and `Dispose()` (lines 267-270) dispose the same page instances (`_dashboardPage`, `_transactionsPage`, `_configurationPage`, `_logsPage`). If both paths execute (OnClosing during force-close, then Dispose later), pages are disposed twice. While most `IDisposable` implementations tolerate this, it's not guaranteed by the contract and may cause issues if a page's Dispose has side effects.
- **Evidence:**
  - `MainWindow.axaml.cs:89-93` — pages disposed in OnClosing
  - `MainWindow.axaml.cs:267-270` — same pages disposed again in Dispose()
- **Impact:** Potential `ObjectDisposedException` if any page's Dispose is not idempotent.
- **Recommended Fix:** Dispose pages in only one place (preferably `Dispose()`), and set fields to null after disposal to guard against double-dispose.

---

### R-DSK-003
- **Title:** ProvisioningWindow never disposes CancellationTokenSource
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Improper resource disposal
- **Description:** `_registrationCts` is created with a 30-second timeout (`new CancellationTokenSource(TimeSpan.FromSeconds(30))`) each time registration is attempted. The old CTS is cancelled (`_registrationCts?.Cancel()`) but never disposed. `CancellationTokenSource` with a timeout internally allocates a `Timer` — failing to dispose it leaks this timer until GC finalization. If the user retries registration multiple times, each attempt leaks one CTS+Timer.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:208-210` — `_registrationCts?.Cancel(); _registrationCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));` — old CTS not disposed
  - `ProvisioningWindow.axaml.cs:369-371` — same pattern in the manual-token path
  - No `IDisposable` implementation on `ProvisioningWindow` to clean up `_registrationCts`
- **Impact:** Each retry leaks a Timer handle (~100 bytes + kernel object). Minimal impact for typical usage (1-2 retries) but poor resource hygiene.
- **Recommended Fix:** Add `_registrationCts?.Dispose()` before creating a new CTS, and implement `IDisposable` on ProvisioningWindow to clean up on close.

---

### R-DSK-004
- **Title:** Race condition — decommission event handler can fire multiple times
- **Module:** Application Shell
- **Severity:** High
- **Category:** Race conditions
- **Description:** The `DeviceDecommissioned` event handler in `App.axaml.cs` creates a new `DecommissionedWindow`, sets `desktop.ShutdownMode`, and calls `mainWindow.ForceClose()` — all inside a `Dispatcher.UIThread.Post`. If the cloud sync worker fires `DeviceDecommissioned` multiple times (e.g., from consecutive failed uploads that all return DEVICE_DECOMMISSIONED), each invocation queues a new UI thread callback. The first callback closes `mainWindow`, and subsequent callbacks will: (a) create additional `DecommissionedWindow` instances, and (b) call `ForceClose()` on an already-closed window.
- **Evidence:**
  - `App.axaml.cs:186-201` — `registrationManager.DeviceDecommissioned += (_, _) => { ... }` — no guard against multiple fires
  - No `_isDecommissioned` flag or event unsubscription after first fire
- **Impact:** Multiple decommission windows appear simultaneously, and `ForceClose()` on a closed window may throw or behave unpredictably.
- **Recommended Fix:** Add a `bool _decommissioned` guard flag (set atomically inside the handler), or unsubscribe from the event after the first invocation.

---

### R-DSK-005
- **Title:** Race condition — re-provisioning event handler can fire multiple times
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Race conditions
- **Description:** The `ReprovisioningRequired` event handler (lines 205-231) has the same pattern as the decommission handler — no guard against multiple invocations. If the event fires more than once, multiple `ProvisioningWindow` instances are created, and `mainWindow.ForceClose()` is called multiple times.
- **Evidence:**
  - `App.axaml.cs:205-231` — `registrationManager.ReprovisioningRequired += (_, _) => { ... }` — no dedup guard
- **Impact:** Duplicate provisioning windows and potential crash from closing an already-closed window.
- **Recommended Fix:** Same as R-DSK-004: add a guard flag or unsubscribe after first fire.

---

### R-DSK-006
- **Title:** Tray icon context menu async handler has no error feedback path
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Crash-prone null handling
- **Description:** The `CheckForUpdatesRequested` handler in `App.axaml.cs` is declared as `async (_, _) =>` (async void via event handler). If `updateService.CheckForUpdatesAsync()` throws an unhandled exception (e.g., network timeout not caught by VelopackUpdateService), the exception will propagate as an unobserved exception on the UI thread, potentially crashing the application. The handler logs but does not wrap in try-catch.
- **Evidence:**
  - `App.axaml.cs:127-144` — `async (_, _) => { ... var result = await updateService.CheckForUpdatesAsync(); ... }` — no try-catch
  - While `VelopackUpdateService.CheckForUpdatesAsync` has its own try-catch, the event handler itself does not guard against `NullReferenceException` or other framework-level exceptions
- **Impact:** An unhandled exception in the async void handler will crash the entire application via `AppDomain.UnhandledException`.
- **Recommended Fix:** Wrap the entire handler body in a try-catch that logs and swallows exceptions, consistent with non-critical tray actions.

---

### R-DSK-007
- **Title:** Splash screen 2-second DispatcherTimer can race with slow DI container startup
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Race conditions
- **Description:** In `App.axaml.cs`, the splash screen uses a hard-coded 2-second `DispatcherTimer` before transitioning to the target window. The timer callback reads `AgentAppContext.Mode` and `AgentAppContext.ServiceProvider` which are set in `Program.cs` before `RunAvalonia()`. While these are set before Avalonia starts, there is no synchronization guarantee — on a very slow machine, the Avalonia framework initialization may complete and fire the timer before Program.cs has finished building the DI container (since `RunAvalonia` is called in the same synchronous flow). More concretely, the 2-second delay is arbitrary: if the host takes longer than 2 seconds to start (e.g., database migration on first run), the splash disappears before services are ready.
- **Evidence:**
  - `App.axaml.cs:34-37` — `DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }`
  - `Program.cs:99-100` — `AgentAppContext.ServiceProvider` and `WebApp` set before `RunAvalonia()`
  - `Program.cs:123` — `webApp.Start()` called before `RunAvalonia()` only in Normal mode
- **Impact:** In the Normal startup path, services ARE started before Avalonia, so the 2-second delay is just cosmetic. But in the Provisioning path, `webApp.Start()` is NOT called until after provisioning completes — so `AgentAppContext.ServiceProvider` exists but the host isn't running. The timer is safe but the arbitrary delay is brittle.
- **Recommended Fix:** Replace the fixed timer with a signal-based approach (e.g., `TaskCompletionSource` set after host startup) to ensure the splash stays until services are actually ready.

---

## Device Provisioning Module

### R-DSK-008
- **Title:** ProvisioningWindow → MainWindow transition in Dispatcher.Post has no error handling
- **Module:** Device Provisioning
- **Severity:** High
- **Category:** Crash-prone null handling
- **Description:** The `RegistrationCompleted` event handler in `App.axaml.cs` (both initial provisioning and re-provisioning paths) posts a callback to the UI thread that creates a new `MainWindow`, shows it, sets up the tray icon, and closes the provisioning window. None of this is wrapped in a try-catch. If the `MainWindow` constructor throws (e.g., a DI resolution failure, database migration error, or connectivity monitor initialization failure), the exception propagates as an unhandled exception on the UI thread, crashing the application. The provisioning window remains open but with the "Launch Agent" button disabled, giving the user no recovery path and no error message.
- **Evidence:**
  - `App.axaml.cs:88-100` — initial provisioning `RegistrationCompleted` handler — no try-catch in `Dispatcher.UIThread.Post`
  - `App.axaml.cs:217-226` — re-provisioning `RegistrationCompleted` handler — same pattern, no try-catch
  - `MainWindow.axaml.cs:33-55` — constructor resolves services, creates timers, subscribes to events — multiple potential throw sites
- **Impact:** Unhandled exception crashes the entire application after successful provisioning. The user successfully registered the device but the app exits before they can use it. On next launch, the agent enters Normal mode (registration succeeded) and may work — but the crash leaves a poor impression and logs an unclean shutdown.
- **Recommended Fix:** Wrap the `Dispatcher.UIThread.Post` callback body in a try-catch that logs the error and shows a user-friendly dialog (e.g., "Setup completed but the agent failed to start. Please restart the application.").

---

### R-DSK-009
- **Title:** RegistrationManager.SaveStateAsync has no file-level concurrency protection
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** Race conditions
- **Description:** `SaveStateAsync` performs a three-step write: (1) serialize to JSON, (2) write to `.tmp` file, (3) replace/move to target. The `_lock` object only protects the in-memory cache update (line 114) — the file operations have no mutual exclusion. If `MarkDecommissionedAsync` and `MarkReprovisioningRequiredAsync` are called concurrently (unlikely but possible from separate cloud sync worker threads), both call `LoadState()` (returns a clone), modify the clone, then call `SaveStateAsync`. Both write to the same `.tmp` file, and the `File.Replace` / `File.Move` calls can race, potentially throwing `IOException` or producing a corrupted state file.
- **Evidence:**
  - `RegistrationManager.cs:101-118` — `SaveStateAsync` — no lock around file operations
  - `RegistrationManager.cs:104` — `var tmpPath = path + ".tmp"` — shared temp path
  - `RegistrationManager.cs:107-112` — `WriteAllTextAsync` + `File.Replace` / `File.Move` — not atomic together
  - `RegistrationManager.cs:120-133` — `MarkDecommissionedAsync` calls `LoadState` then `SaveStateAsync`
  - `RegistrationManager.cs:135-150` — `MarkReprovisioningRequiredAsync` — same pattern
- **Impact:** Rare but possible corruption of `registration.json` if decommission and re-provisioning events race. Corrupted state causes the agent to fall back to "unregistered" on next startup, triggering an unnecessary re-provisioning flow.
- **Recommended Fix:** Extend the `_lock` (or use a `SemaphoreSlim` for async compatibility) to cover the entire `SaveStateAsync` operation, or use a dedicated async lock for file operations.

---

### R-DSK-010
- **Title:** ResetCopyButtonAsync can set control content after window closure
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Crash-prone null handling
- **Description:** `OnCopyApiKeyClicked` fires `_ = ResetCopyButtonAsync()` without awaiting it. `ResetCopyButtonAsync` does `await Task.Delay(2000)` then sets `CopyApiKeyButton.Content = "Copy"`. If the user clicks "Copy" and then clicks "Launch Agent" within 2 seconds, the provisioning window closes before the delay completes. The `Task.Delay` continuation then attempts to set `Content` on a control belonging to a closed/disposed window. Avalonia may silently ignore this or throw depending on the visual tree state.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:626` — `_ = ResetCopyButtonAsync()` — fire-and-forget
  - `ProvisioningWindow.axaml.cs:636-639` — `await Task.Delay(2000); CopyApiKeyButton.Content = "Copy";` — runs after potential window close
  - `App.axaml.cs:99` — `provisioningWindow.Close()` — called when RegistrationCompleted fires
- **Impact:** Potential `InvalidOperationException` or silent failure when setting content on a detached control. Unlikely to crash (Avalonia typically tolerates this) but represents a resource lifecycle issue.
- **Recommended Fix:** Use a `CancellationToken` tied to the window's lifetime, or check `this.IsVisible` / `this.IsClosed` before updating the control. Alternatively, track the reset task and cancel it in `LaunchAgentAsync` before raising `RegistrationCompleted`.

---

## Authentication & Security Module

### R-DSK-011
- **Title:** DeviceTokenProvider._refreshLock can cause deadlock if CancellationToken is cancelled while waiting
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Thread safety issues
- **Description:** `RefreshTokenAsync` acquires `_refreshLock.WaitAsync(ct)` with a cancellation token. If the token is cancelled while a caller is waiting on the semaphore (e.g., CadenceController shuts down), `WaitAsync` throws `OperationCanceledException` — the lock is never acquired and the caller exits correctly. However, the semaphore count remains at 0 (held by the previous caller). If the previous caller's `RefreshTokenCoreAsync` completes normally, the semaphore is released. But if the previous caller also throws `OperationCanceledException` (its own CancellationToken was cancelled), the finally block still releases the semaphore correctly. The real risk is if `RefreshTokenCoreAsync` throws an unexpected exception that is NOT caught by the finally block — but since the finally block is unconditional, this is safe. The pattern is correct but fragile: if anyone modifies the try-finally structure, the lock could be orphaned.
- **Evidence:**
  - `DeviceTokenProvider.cs:76-84` — `_refreshLock.WaitAsync(ct)` + try/finally `_refreshLock.Release()`
  - `DeviceTokenProvider.cs:32` — `private readonly SemaphoreSlim _refreshLock = new(1, 1)` — single permit
- **Impact:** Low — the current code is correct. The fragility risk is for future maintainers who might add early returns or restructure the try-finally.
- **Recommended Fix:** Acceptable as-is. Consider adding a comment explaining the critical lock semantics for future maintainers.

---

### R-DSK-012
- **Title:** PlatformCredentialStore._fileLock never disposed — SemaphoreSlim leaked for process lifetime
- **Module:** Authentication & Security
- **Severity:** Low
- **Category:** Improper resource disposal
- **Description:** `PlatformCredentialStore` contains a `SemaphoreSlim _fileLock = new(1, 1)` but does not implement `IDisposable`. The `SemaphoreSlim` is allocated for the process lifetime (registered as singleton in DI). While this is technically a resource leak, `SemaphoreSlim.Dispose()` primarily releases a `WaitHandle` that is only allocated when `AvailableWaitHandle` is accessed — which the code never does. The practical impact is zero.
- **Evidence:**
  - `PlatformCredentialStore.cs:26` — `private readonly SemaphoreSlim _fileLock = new(1, 1)` — no Dispose
  - `PlatformCredentialStore.cs:21` — `public sealed class PlatformCredentialStore : ICredentialStore` — no IDisposable
  - `ServiceCollectionExtensions.cs:44` — registered as singleton
- **Impact:** No practical impact. The semaphore lives for the process lifetime and is cleaned up on process exit.
- **Recommended Fix:** Acceptable as-is. Implementing `IDisposable` for a singleton is unnecessary since disposal coincides with process exit.

---

### R-DSK-013
- **Title:** Token refresh pending marker creates false-positive unrecoverable state after interrupted network call
- **Module:** Authentication & Security
- **Severity:** High
- **Category:** Race conditions
- **Description:** `RefreshTokenCoreAsync` writes a `RefreshPendingKey` marker BEFORE sending the HTTP refresh request. If the HTTP call fails due to a transient network error (not `OperationCanceledException`), the method returns `null` without deleting the pending marker (line 137). On the next refresh attempt, `HasUnrecoverablePendingRefreshAsync()` finds the pending marker and no staging key, concluding that a previous refresh "may have completed on the server" — it throws `RefreshTokenExpiredException`, forcing the device into re-provisioning. But the refresh never reached the server; it failed due to a transient network error. The pending marker was written, but the response was never received, so no staging bundle was written. The logic incorrectly interprets a failed outbound request as a potentially-completed server-side operation.
- **Evidence:**
  - `DeviceTokenProvider.cs:127` — `await MarkRefreshPendingAsync(ct)` — written before HTTP call
  - `DeviceTokenProvider.cs:134-138` — `catch (Exception) { return null; }` — does NOT delete pending marker
  - `DeviceTokenProvider.cs:96-103` — `HasUnrecoverablePendingRefreshAsync` returns true → throws `RefreshTokenExpiredException`
  - `DeviceTokenProvider.cs:231-239` — `HasUnrecoverablePendingRefreshAsync`: pending exists + no staging = unrecoverable
- **Impact:** A transient network error during token refresh can permanently force the device into re-provisioning, even though the refresh token is still valid on the server. The field technician must re-provision the device with a new bootstrap token for a non-existent problem.
- **Recommended Fix:** Delete the pending marker in the `catch` block for transient HTTP failures (line 137). The pending marker should only persist when the HTTP response was received (meaning the server may have consumed the refresh token) but the local staging write failed. Add: `await DeleteSecretBestEffortAsync(RefreshPendingKey, ct)` before `return null` on network errors.

---

### R-DSK-014
- **Title:** PetroniteOAuthClient.GetAccessTokenAsync has no retry — single failure expires all cached tokens
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Crash-prone null handling
- **Description:** `PetroniteOAuthClient.GetAccessTokenAsync()` calls `RequestTokenAsync(ct)` inside the semaphore. If the token request fails with an `FccAdapterException` (isRecoverable: true), the exception propagates to the caller. The cached token remains at its previous value (which may have expired). On the next call, the fast path check fails (token expired), the semaphore is acquired, the double-check also fails, and a new token request is attempted. There is no automatic retry with backoff for transient failures. If the Petronite OAuth endpoint has a brief outage, every FCC operation (pre-auth, transaction submission) fails for the duration of the outage plus the next polling interval.
- **Evidence:**
  - `PetroniteOAuthClient.cs:48-76` — `GetAccessTokenAsync` — single attempt, no retry
  - `PetroniteOAuthClient.cs:104-178` — `RequestTokenAsync` — throws on any failure
  - `PetroniteOAuthClient.cs:139-145` — transient failures (network) throw `isRecoverable: true` but not retried
- **Impact:** Petronite FCC operations fail for the duration of any OAuth endpoint outage, even if the outage is sub-second.
- **Recommended Fix:** Add a short retry loop (2-3 attempts with exponential backoff) inside `GetAccessTokenAsync` for recoverable errors, or allow the caller to retry with a fresh token after invalidation.

---

### R-DSK-015
- **Title:** Linux credential store HasSecretToolAsync check is cached permanently — misses post-install availability
- **Module:** Authentication & Security
- **Severity:** Low
- **Category:** Crash-prone null handling
- **Description:** `_linuxHasSecretTool` is a nullable bool that is checked once via `which secret-tool` and then cached for the process lifetime. If `secret-tool` is installed while the agent is running (e.g., package manager install during a maintenance window), the agent continues using the less-secure AES file fallback for the entire process lifetime. Conversely, if `secret-tool` is removed, the agent continues trying to use it and failing.
- **Evidence:**
  - `PlatformCredentialStore.cs:29` — `private bool? _linuxHasSecretTool` — nullable cached flag
  - `PlatformCredentialStore.cs:387-406` — `HasSecretToolAsync` — checks once, caches permanently
- **Impact:** Minor — `secret-tool` availability rarely changes during process lifetime. The AES file fallback is functional if less secure.
- **Recommended Fix:** Acceptable as-is. Adding periodic re-checks would add complexity for a marginal benefit. Document that an agent restart is needed after installing `secret-tool` to upgrade credential storage.

---

### R-DSK-016
- **Title:** RegistrationManager.LoadState not thread-safe — double-read race on first access
- **Module:** Authentication & Security
- **Severity:** Low
- **Category:** Thread safety issues
- **Description:** `LoadState()` checks `_cached` inside a lock, returns if non-null. If null, it releases the lock, reads from disk, then re-acquires the lock to set `_cached`. Between the first lock release and the file read completion, another thread can enter `LoadState`, also find `_cached` null, and also read from disk. Both threads then race to set `_cached`. The second write wins, which is fine since both read the same file. However, the file is read twice instead of once, and two `RegistrationState` objects are deserialized unnecessarily.
- **Evidence:**
  - `RegistrationManager.cs:57-61` — lock check for `_cached` — releases lock before file read
  - `RegistrationManager.cs:63-64` — file existence check and read outside any lock
  - `RegistrationManager.cs:77` — `lock (_lock) _cached = state` — second acquisition to set cache
- **Impact:** Harmless double-read on first access. No data corruption since both reads return the same file content.
- **Recommended Fix:** Move the entire file read inside the lock, or use `Lazy<RegistrationState>` for thread-safe one-time initialization. The current behavior is correct but wastes a small amount of I/O on first access.

---

## Configuration Module

### R-DSK-017
- **Title:** ConfigManager.ApplyConfigAsync has TOCTOU race — version check and config update are not atomic
- **Module:** Configuration
- **Severity:** Medium
- **Category:** Race conditions
- **Description:** `ApplyConfigAsync` checks the config version under `_lock` (lines 72-83), then releases the lock to perform validation, database storage, and change detection (lines 86-126). It re-acquires the lock at line 115 to update `_current` and `_currentVersion`. Between the first lock release and the second acquisition, another thread can call `ApplyConfigAsync` with a different config version. Both threads pass the version check (each sees the same `_current`), both store to the database (the last writer wins), and both update `_current` under separate lock acquisitions. This is a TOCTOU (time-of-check-time-of-use) race. The race is possible because `ConfigPollWorker` runs on the `CadenceController` background thread while `ConfigurationPage.OnSaveClicked` runs on the UI thread — both call `ApplyConfigAsync` concurrently.
- **Evidence:**
  - `ConfigManager.cs:72-83` — version check under `_lock`, releases lock on line 83
  - `ConfigManager.cs:112` — `StoreConfigAsync` runs WITHOUT lock protection
  - `ConfigManager.cs:115-121` — re-acquires `_lock` to update `_current`
  - `ConfigManager.cs:68` — `ApplyConfigAsync` is `public` — callable from any thread
  - `ConfigPollWorker.cs:121-122` — calls `ApplyConfigAsync` from background thread
  - `ConfigurationPage.axaml.cs:173` — calls `ApplyConfigAsync` from UI thread
- **Impact:** Concurrent config applies can result in `_current` and the database having different config versions. A cloud-pushed config could be overwritten by a UI save (or vice versa) without version conflict detection. On next startup, the agent loads the database version (last writer wins), which may not match what either caller intended.
- **Recommended Fix:** Hold the lock for the entire `ApplyConfigAsync` operation (including validation, DB write, and `_current` update), or use a `SemaphoreSlim` for async locking. Alternatively, use optimistic concurrency: re-check the version after the DB write and rollback if it changed.

---

### R-DSK-018
- **Title:** LocalOverrideManager has race between Load cache read and Persist cache write
- **Module:** Configuration
- **Severity:** Medium
- **Category:** Thread safety issues
- **Description:** `LocalOverrideManager.Load()` reads the file outside the lock (line 213-214: `File.ReadAllText`), then acquires `_lock` to set `_cached` (line 216). `Persist()` acquires `_lock` to set `_cached` (line 230), then writes the file outside the lock (line 234). If a concurrent read and write occur, the sequence can be: (1) `Persist()` writes new data to `_cached` and file, (2) `Load()` reads old file content (before `Persist` writes), (3) `Load()` overwrites `_cached` with stale data (after `Persist` set it to new data). The file has the correct new data, but `_cached` holds stale data. All subsequent property reads (`FccHost`, `FccPort`, etc.) return stale values until the next `Persist` or `ClearAllOverrides`. This race window exists because `SettingsPanel.OnSaveClicked` runs on the UI thread calling `SaveAll` (→ `Persist`), while `DesktopFccRuntimeConfiguration.Resolve` can read overrides from the `CadenceController` background thread.
- **Evidence:**
  - `LocalOverrideManager.cs:197-226` — `Load()` reads file outside lock, sets `_cached` inside lock
  - `LocalOverrideManager.cs:228-242` — `Persist()` sets `_cached` inside lock, writes file outside lock
  - `LocalOverrideManager.cs:47-51` — `GetEffectiveFccHost` calls `Load()` — called from background threads
  - `DesktopFccRuntimeConfiguration.cs:179` — `overrideManager?.GetEffectiveFccHost(...)` — called from CadenceController
- **Impact:** After saving overrides from the UI, the background thread may see stale override values for an indeterminate period. The adapter could connect to the old FCC host while the UI shows the new override as active.
- **Recommended Fix:** Move the file read inside the `_lock` block in `Load()`, or use a `ReaderWriterLockSlim` to allow concurrent reads while serializing writes. Alternatively, `Persist()` should write the file inside the lock to prevent the interleaving.

---

### R-DSK-019
- **Title:** SettingsPanel.ClearFeedbackAsync can post to detached control — no disposal guard
- **Module:** Configuration
- **Severity:** Low
- **Category:** Crash-prone null handling
- **Description:** `SettingsPanel.ClearFeedbackAsync()` awaits `Task.Delay(5000)` then posts `FeedbackText.Text = string.Empty` to the UI thread. Unlike `ConfigurationPage` which checks a `_disposed` flag before posting (line 303), `SettingsPanel` has no such guard and does not implement `IDisposable`. If the user navigates away from the settings panel within 5 seconds of saving, the `Task.Delay` continuation runs and attempts to update `FeedbackText` on a control that may have been detached from the visual tree. Avalonia typically tolerates updates on detached controls, but the behavior is not guaranteed and depends on the visual tree state.
- **Evidence:**
  - `SettingsPanel.axaml.cs:156-160` — `ClearFeedbackAsync()` — no disposal/detachment check
  - `ConfigurationPage.axaml.cs:299-304` — `ClearFeedbackAsync()` — correctly checks `_disposed` flag
  - `SettingsPanel.axaml.cs:12` — `public sealed partial class SettingsPanel : UserControl` — no `IDisposable`
- **Impact:** Potential `InvalidOperationException` or silent no-op when updating a detached control. Low probability but inconsistent with the `ConfigurationPage` pattern.
- **Recommended Fix:** Add a `bool _disposed` flag and `IDisposable` implementation to `SettingsPanel`, mirroring the `ConfigurationPage` pattern. Check the flag in `ClearFeedbackAsync` before posting the UI update.

---

## FCC Device Integration Module

### R-DSK-020
- **Title:** FccAdapterFactory sync-over-async DisposeAsync inside lock risks deadlock
- **Module:** FCC Device Integration
- **Severity:** High
- **Category:** Thread safety issues
- **Description:** `GetOrCreatePetroniteAdapter` and `GetOrCreateAdvatecAdapter` call `DisposeAsync().AsTask().GetAwaiter().GetResult()` inside a `lock` block. `DisposeAsync()` on `PetroniteAdapter` calls `_webhookListener.StopAsync()` which is an async HTTP listener shutdown. Blocking on an async operation inside a `lock` (which is a non-reentrant mutual-exclusion primitive) prevents any other thread from entering the lock while the async operation completes. If the async operation's continuation is posted to the same synchronization context (e.g., a UI thread or a single-threaded scheduler), a classic deadlock occurs: the lock holder is waiting for the continuation, and the continuation is waiting for the lock to be released. Even without a sync context, blocking the thread inside a lock wastes a thread pool thread for the duration of the async listener shutdown (which may involve socket close timeouts up to 5 seconds).
- **Evidence:**
  - `FccAdapterFactory.cs:75-86` — `lock (_petroniteLock) { _cachedPetroniteAdapter?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }`
  - `FccAdapterFactory.cs:119-134` — `lock (_advatecLock) { _cachedAdvatecAdapter?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }`
  - `PetroniteAdapter.cs:760-770` — `DisposeAsync()` calls `_webhookListener.StopAsync()` — genuinely async I/O
- **Impact:** Potential deadlock if called from a synchronization context with thread affinity. Thread pool starvation in high-concurrency scenarios if the listener shutdown takes seconds. The CadenceController calls `Create()` on the background thread, which currently has no sync context, so the deadlock risk is low in practice — but the pattern is inherently fragile.
- **Recommended Fix:** Replace `lock` with `SemaphoreSlim(1,1)` and use `await DisposeAsync()` inside an async-compatible critical section. Alternatively, move disposal outside the lock: capture the old adapter reference inside the lock, replace the cached reference, release the lock, then dispose asynchronously.

---

### R-DSK-021
- **Title:** Petronite _activePreAuths has no size limit or TTL cleanup — unbounded memory growth
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Improper resource disposal
- **Description:** `PetroniteAdapter._activePreAuths` is a `ConcurrentDictionary<string, ActivePreAuth>` that grows every time `SendPreAuthAsync` is called with a successful authorization. Entries are removed only when: (a) a matching `transaction.completed` webhook arrives and `NormalizeAsync` calls `TryRemove`, or (b) `CancelPreAuthAsync` is called. If a pre-auth is authorized but the customer never dispenses (walks away), and the Petronite backend doesn't send a cancellation webhook, the entry remains in memory indefinitely. The `ReconcileOnStartupAsync` method only runs once at adapter initialization and re-adopts pending orders — it does not purge stale entries from `_activePreAuths` during normal operation. Over days/weeks of operation, orphaned pre-auth entries accumulate.
- **Evidence:**
  - `PetroniteAdapter.cs:47` — `private readonly ConcurrentDictionary<string, ActivePreAuth> _activePreAuths = new()` — no size limit
  - `PetroniteAdapter.cs:482` — `_activePreAuths[authResponse.OrderId] = activePreAuth` — adds on every successful pre-auth
  - `PetroniteAdapter.cs:155` — `_activePreAuths.TryRemove(tx.OrderId, out var preAuth)` — only removal path during normal operation (requires webhook)
  - `ActivePreAuth` record includes `CreatedAt` (line 972) but no code checks it for staleness during operation
- **Impact:** Gradual memory leak proportional to the rate of abandoned pre-authorizations. Each `ActivePreAuth` is small (~200 bytes), so the leak is slow. On a busy site with many abandoned pre-auths, the dictionary could grow to thousands of entries over weeks, consuming hundreds of KB — not critical but symptomatic of a missing lifecycle cleanup.
- **Recommended Fix:** Add a periodic purge (e.g., in `FetchTransactionsAsync` or on a timer) that removes entries older than `StaleOrderThreshold` (30 minutes). When purging, optionally call `CancelPreAuthAsync` for each stale entry to release the pump authorization on the Petronite side.

---

### R-DSK-022
- **Title:** PumpStatusBroadcastLoopAsync breaks permanently on any non-cancellation exception
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Crash-prone null handling
- **Description:** The per-connection `PumpStatusBroadcastLoopAsync` in `OdooWebSocketServer` catches `OperationCanceledException` (breaks) and all other exceptions (logs at Debug level and also breaks). This means any transient error — a momentary database unavailability in the pump status service, a serialization error for an unexpected pump state, or a `WebSocketException` during `SendAsync` — permanently kills the broadcast loop for that connection. The client stops receiving pump status updates for the remainder of the connection's lifetime, with no recovery mechanism. The Debug-level log means this failure is likely invisible to operators.
- **Evidence:**
  - `OdooWebSocketServer.cs:189-220` — `PumpStatusBroadcastLoopAsync` — `catch (Exception ex) { _logger.LogDebug(...); break; }`
  - `OdooWebSocketServer.cs:211` — `break` after any non-cancellation exception — loop exits permanently
  - `OdooWebSocketServer.cs:214` — `LogDebug` — low visibility for a connection-degrading failure
- **Impact:** Connected Odoo POS clients silently stop receiving pump status updates after any transient error. The cashier sees stale pump states but has no indication that live updates have stopped. The connection remains open (receive loop continues), so the client doesn't reconnect.
- **Recommended Fix:** Replace `break` with `continue` for transient/recoverable exceptions, allowing the loop to retry on the next interval. Add a consecutive-failure counter and only break after N consecutive failures (e.g., 5). Upgrade the log level to `LogWarning` to make the failure visible. Keep `break` for `WebSocketException` (connection is dead).

---

### R-DSK-023
- **Title:** IngestionOrchestrator._pollLock SemaphoreSlim never disposed
- **Module:** FCC Device Integration
- **Severity:** Low
- **Category:** Improper resource disposal
- **Description:** `IngestionOrchestrator` allocates a `SemaphoreSlim _pollLock = new(1, 1)` but does not implement `IDisposable` or `IAsyncDisposable`. The orchestrator is registered as a singleton in the DI container. When the host shuts down, the DI container disposes singletons that implement `IDisposable` — but since `IngestionOrchestrator` doesn't implement it, the `SemaphoreSlim` is never explicitly disposed. Additionally, the `_fiscalizationService` (an `IAsyncDisposable` `AdvatecFiscalizationService`) cached in the orchestrator is never disposed on shutdown.
- **Evidence:**
  - `IngestionOrchestrator.cs:41` — `private readonly SemaphoreSlim _pollLock = new(1, 1)` — never disposed
  - `IngestionOrchestrator.cs:47` — `private AdvatecFiscalizationService? _fiscalizationService` — never disposed
  - `IngestionOrchestrator.cs:26` — `public sealed class IngestionOrchestrator : IIngestionOrchestrator` — no `IDisposable`
- **Impact:** The `SemaphoreSlim` leak is negligible (no `WaitHandle` allocated). The `_fiscalizationService` leak is more concerning: if the `AdvatecFiscalizationService` owns HTTP resources or webhook listeners, these are abandoned on shutdown rather than gracefully closed.
- **Recommended Fix:** Implement `IAsyncDisposable` on `IngestionOrchestrator` to dispose `_pollLock` and `_fiscalizationService`. Register it as a singleton that the host can dispose on shutdown.

---

### R-DSK-024
- **Title:** OdooWebSocketServer.Dispose doesn't await WebSocket close — may lose pending data
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Improper resource disposal
- **Description:** `OdooWebSocketServer.Dispose()` iterates `_clients`, cancels each connection's CTS, and disposes it. However, it does NOT close the WebSocket connections with `CloseAsync`. The `HandleConnectionAsync` finally block does attempt `CloseAsync` after CTS cancellation, but since `Dispose()` is synchronous, it returns before the async close handshake completes. The `_clients.Clear()` call at line 265 removes all entries, which means when the `HandleConnectionAsync` finally block runs, its `_clients.TryRemove` returns false (already removed), but the close handshake still proceeds on the now-cancelled token (`CancellationToken.None` is used for the close). The net effect: `Dispose()` returns while close handshakes are still in-flight, and the host may terminate before they complete.
- **Evidence:**
  - `OdooWebSocketServer.cs:258-266` — `Dispose()` — cancels CTS and clears clients synchronously, no `CloseAsync`
  - `OdooWebSocketServer.cs:89-96` — `HandleConnectionAsync` finally — attempts `CloseAsync` with `CancellationToken.None`
  - `OdooWebSocketServer.cs:22` — `IDisposable` not `IAsyncDisposable` — cannot await async cleanup
- **Impact:** On application shutdown, connected WebSocket clients may not receive the close frame, causing them to detect a connection reset instead of a clean disconnect. Odoo POS reconnection logic may behave differently for reset vs. clean close (e.g., longer reconnection delay or error toast).
- **Recommended Fix:** Implement `IAsyncDisposable` instead of (or in addition to) `IDisposable`. In `DisposeAsync()`, send `CloseAsync` to all open clients with a short timeout, then cancel the CTS for any that haven't responded. Alternatively, if synchronous `Dispose` is required, accept the limitation and document it.

---

### R-DSK-025
- **Title:** CancellationTokenSource leak in OdooWebSocketServer per-connection lifecycle
- **Module:** FCC Device Integration
- **Severity:** Low
- **Category:** Improper resource disposal
- **Description:** In `HandleConnectionAsync`, a `CancellationTokenSource` is created via `CancellationTokenSource.CreateLinkedTokenSource(ct)` (line 70) and stored in `_clients[webSocket] = cts`. In the `finally` block, `cts.Cancel()` is called but `cts.Dispose()` is not. The CTS is only disposed in `OdooWebSocketServer.Dispose()` when the server shuts down. For long-running servers with many connect/disconnect cycles, each disconnected client's CTS remains undisposed until server shutdown. `CancellationTokenSource.CreateLinkedTokenSource` allocates an internal registration on the parent token — not disposing it leaks this registration for the lifetime of the parent token.
- **Evidence:**
  - `OdooWebSocketServer.cs:70` — `var cts = CancellationTokenSource.CreateLinkedTokenSource(ct)` — created per connection
  - `OdooWebSocketServer.cs:84-86` — `finally { cts.Cancel(); _clients.TryRemove(webSocket, out _); }` — no `cts.Dispose()`
  - `OdooWebSocketServer.cs:260-264` — `Dispose()` — `cts.Dispose()` called only at server shutdown
- **Impact:** Each disconnected WebSocket client leaks a `CancellationTokenSource` registration (~100 bytes + kernel object) until server shutdown. For a server handling dozens of connections per day, this accumulates to a few KB — minimal practical impact but poor resource hygiene.
- **Recommended Fix:** Add `cts.Dispose()` in the `finally` block of `HandleConnectionAsync`, after `cts.Cancel()` and after removing from `_clients`. Ensure the CTS is not referenced elsewhere after removal.

---

## Site Master Data Module

### R-DSK-026
- **Title:** SiteDataManager file I/O race — concurrent SyncFromConfig write and LoadSiteData read can corrupt data
- **Module:** Site Master Data
- **Severity:** High
- **Category:** Race conditions
- **Description:** `SiteDataManager.SyncFromConfig` (line 92) writes to `site-data.json` via `File.WriteAllText` while `LoadSiteData` (line 122) reads from the same file via `File.ReadAllText`. Neither method holds a lock during the file I/O operation. `SyncFromConfig` acquires `_lock` only for the in-memory cache update (line 94), not during the file write. `LoadSiteData` acquires `_lock` to check the cache (lines 107-111), but reads the file outside the lock (line 122). If `SyncFromConfig` is writing to the file at the exact moment `LoadSiteData` reads it, `File.ReadAllText` may see a partially written file, causing `JsonSerializer.Deserialize` to fail with `JsonException`. The catch block (line 135) returns `null`, silently treating valid site data as absent.
- **Evidence:**
  - `SiteDataManager.cs:92` — `File.WriteAllText(path, json)` — not under lock
  - `SiteDataManager.cs:94` — `lock (_lock) _cached = snapshot` — lock only protects cache, not file
  - `SiteDataManager.cs:122` — `var json = File.ReadAllText(path)` — not under lock
  - `SiteDataManager.cs:135` — `catch (Exception ex) when (ex is JsonException or IOException)` — returns null on partial read
- **Impact:** During concurrent access, `LoadSiteData` may return `null` (as if no site data exists) even though valid data was being written. Consumers see a transient absence of equipment metadata.
- **Recommended Fix:** Hold `_lock` during both file I/O operations in `SyncFromConfig` and `LoadSiteData`. Alternatively, write to a temp file and atomically rename (on Unix) or use `FileStream` with exclusive access.

---

### R-DSK-027
- **Title:** DashboardPage.RefreshAllAsync silently swallows all exceptions — dashboard shows stale data with no visual indication
- **Module:** Site Master Data
- **Severity:** Medium
- **Category:** Crash-prone null handling
- **Description:** `DashboardPage.RefreshAllAsync` (line 180) has a bare `catch { }` that swallows all exceptions including critical ones (database corruption, `ObjectDisposedException` after navigation, out-of-memory). When the refresh fails, the dashboard continues showing the last-known values (or "--" placeholders) with no visual indicator that data is stale. The `catch { /* non-fatal */ }` comment treats all exceptions as non-fatal, but database corruption (`SqliteException`) or disposed context (`ObjectDisposedException`) may indicate a persistent failure that will never self-resolve.
- **Evidence:**
  - `DashboardPage.axaml.cs:180-183` — `catch { /* non-fatal — stats will refresh next cycle */ }`
  - `DashboardPage.axaml.cs:44` — timer fires every 5 seconds, each failure is silently swallowed
  - `DashboardPage.axaml.cs:134` — `catch { /* non-fatal */ }` — storage metrics also silently swallowed
  - No `_logger` field in DashboardPage — exceptions are not even logged
- **Impact:** Persistent refresh failures (e.g., database corruption) silently render the dashboard useless. Operators see frozen "0" counts and stale timestamps with no indication that data is not updating. No diagnostics are available since exceptions are not logged.
- **Recommended Fix:** Add logging for refresh failures (inject `ILogger<DashboardPage>`). After N consecutive failures, display a visual warning banner on the dashboard. At minimum, log the exception so diagnostics are available.

---

### R-DSK-028
- **Title:** DashboardPage Timer callback invokes async method via fire-and-forget — unobserved exception if RefreshAllAsync throws before try-catch
- **Module:** Site Master Data
- **Severity:** Medium
- **Category:** Thread safety issues
- **Description:** `DashboardPage` constructor (line 44) creates a `System.Threading.Timer` with callback `_ => _ = RefreshAllAsync()`. The `_ =` discards the Task. While `RefreshAllAsync` has a try-catch, the Timer callback runs on a thread pool thread. If `RefreshAllAsync` throws before entering the try block (e.g., during `_services.CreateScope()` after the DashboardPage has been disposed), the unobserved Task exception fires `TaskScheduler.UnobservedTaskException` on the next GC. In .NET 8+ this does not crash the process (unlike .NET 4.0), but it generates a diagnostic warning and may mask real issues.
- **Evidence:**
  - `DashboardPage.axaml.cs:44` — `_refreshTimer = new Timer(_ => _ = RefreshAllAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5))`
  - `DashboardPage.axaml.cs:97` — `if (_services is null) return;` — early return before try-catch
  - `DashboardPage.axaml.cs:99-183` — try-catch does not cover `_services.CreateScope()` failure path
  - `DashboardPage.axaml.cs:319-323` — `Dispose()` disposes timer but timer may have already queued a callback
- **Impact:** After page disposal, a queued timer callback may attempt to create a scope from a disposed service provider, generating an unobserved `ObjectDisposedException`. While non-fatal in .NET 8+, it pollutes diagnostics.
- **Recommended Fix:** Add a `CancellationTokenSource` that is cancelled in `Dispose()`. Check `_disposed` or `_cts.IsCancellationRequested` at the start of `RefreshAllAsync`. Wrap the entire method body in try-catch, not just the inner portion.

---

### R-DSK-029
- **Title:** ConfigurationPage.ClearFeedbackAsync fire-and-forget can throw after page disposal
- **Module:** Site Master Data
- **Severity:** Low
- **Category:** Improper resource disposal
- **Description:** `ConfigurationPage.OnSaveClicked` (line 218) and `OnRegenerateApiKeyClicked` (line 229) use `_ = ClearFeedbackAsync()` to schedule a 5-second delayed feedback clear. `ClearFeedbackAsync` checks `_disposed` before posting to the UI thread (line 303), but the check and the `Dispatcher.UIThread.Post` are not atomic. If `Dispose()` runs between the `_disposed` check and the `Post` call, the `Post` may execute after the control is detached from the visual tree, which is a no-op in Avalonia but represents poor lifecycle management. Additionally, `Task.Delay(5000)` keeps the Task alive for 5 seconds after the page may have been disposed.
- **Evidence:**
  - `ConfigurationPage.axaml.cs:218` — `_ = ClearFeedbackAsync()` — fire-and-forget
  - `ConfigurationPage.axaml.cs:299-304` — `ClearFeedbackAsync` — checks `_disposed` then posts to UI thread
  - `ConfigurationPage.axaml.cs:306-311` — `Dispose()` — sets `_disposed = true` and unsubscribes event
  - TOCTOU between `_disposed` check and `Dispatcher.UIThread.Post`
- **Impact:** Minimal practical impact — Avalonia handles posts to disposed controls gracefully. However, the pattern leaks Tasks and violates structured concurrency, making lifecycle bugs harder to diagnose.
- **Recommended Fix:** Use a `CancellationTokenSource` cancelled in `Dispose()`. Pass the token to `Task.Delay(5000, _cts.Token)` so the delay is cancelled immediately on disposal.

---

### R-DSK-030
- **Title:** Concurrent duplicate pre-auth submissions can escape dedup and fail with 500
- **Module:** Pre-Authorization
- **Severity:** High
- **Category:** Race conditions
- **Description:** `PreAuthHandler.HandleAsync` implements idempotency as a read-then-insert flow. Two concurrent requests for the same `(OdooOrderId, SiteCode)` can both see no active record, both create `Pending` rows, and both attempt to save. The filtered unique index correctly rejects the second insert, but the handler does not catch that `DbUpdateException`. The global exception middleware then converts the failure into a generic 500 response instead of returning the already-created pre-auth record.
- **Evidence:**
  - `PreAuthHandler.cs:53-60` — dedup is a separate read query over active records
  - `PreAuthHandler.cs:123-147` — new active record is inserted and saved without unique-conflict handling
  - `BufferEntityConfiguration.cs:114-118` — `ix_par_idemp` enforces uniqueness for active `(OdooOrderId, SiteCode)` rows
  - `LocalApiStartup.cs:68-87` — uncaught exceptions are returned as `INTERNAL_ERROR` 500 responses
- **Impact:** POS retries or duplicate user actions can turn an otherwise idempotent pre-auth request into a spurious server error even though one authorization succeeded. The caller receives failure and may retry again, increasing operational confusion.
- **Recommended Fix:** Catch unique-constraint `DbUpdateException`, reload the active record by `(OdooOrderId, SiteCode)`, and return it as the deduplicated result. If stronger guarantees are needed, wrap the dedup/insert path in a transaction with appropriate isolation or use an UPSERT-style pattern.

---

## Transaction Management Module

### R-DSK-031
- **Title:** CancellationTokenSource leaked on WebSocket disconnect — not disposed until server shutdown
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Improper resource disposal
- **Description:** `OdooWebSocketServer.HandleConnectionAsync` (lines 70-71) creates a `CancellationTokenSource` per connection and stores it in the `_clients` ConcurrentDictionary. When a connection disconnects (line 86), `_clients.TryRemove(webSocket, out _)` removes the entry but discards the CTS via `out _` — it is never disposed. The CTS is only disposed in `OdooWebSocketServer.Dispose()` (lines 260-265) which runs at server shutdown. In a high-churn environment where Odoo POS clients reconnect frequently (e.g., page refreshes, network drops), each disconnected CTS leaks its internal `ManualResetEvent` kernel handle until the agent process exits.
- **Evidence:**
  - `OdooWebSocketServer.cs:71` — `_clients[webSocket] = cts` — CTS stored
  - `OdooWebSocketServer.cs:85` — `cts.Cancel()` — cancelled but not disposed
  - `OdooWebSocketServer.cs:86` — `_clients.TryRemove(webSocket, out _)` — CTS discarded without Dispose
  - `OdooWebSocketServer.cs:260-265` — `Dispose()` only cleans up connections still in the dictionary
- **Impact:** Kernel handle leak proportional to the number of WebSocket connect/disconnect cycles. Over hours of operation with frequent POS reconnections, this can exhaust OS handle limits.
- **Recommended Fix:** Change `_clients.TryRemove(webSocket, out _)` to `_clients.TryRemove(webSocket, out var removedCts)` and call `removedCts?.Dispose()` after cancellation.

### R-DSK-032
- **Title:** OdooWebSocketServer.Dispose cancels connections but does not await in-flight handlers — potential use-after-dispose
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Improper resource disposal
- **Description:** `OdooWebSocketServer.Dispose()` (lines 259-266) iterates `_clients`, cancels each CTS, disposes it, then clears the dictionary. However, the `HandleConnectionAsync` tasks are still running when `Cancel()` is called — they haven't had a chance to observe the cancellation and exit cleanly. The `ReceiveLoopAsync` and `PumpStatusBroadcastLoopAsync` tasks may still be using the WebSocket or scoped services when the CTS is disposed. Additionally, calling `cts.Dispose()` while a linked token is still being observed can throw `ObjectDisposedException` in the handler tasks.
- **Evidence:**
  - `OdooWebSocketServer.cs:260-265` — `foreach` cancels and disposes CTS without awaiting tasks
  - `OdooWebSocketServer.cs:75` — `pumpStatusTask` is a fire-and-forget Task not tracked for shutdown
  - `OdooWebSocketServer.cs:79` — `ReceiveLoopAsync` is awaited per-connection but not from Dispose
  - No `Task` collection or `Task.WhenAll` for graceful shutdown
- **Impact:** `ObjectDisposedException` or `OperationCanceledException` thrown in handler tasks during shutdown. Scope-based services (`AgentDbContext`) may be accessed after scope disposal.
- **Recommended Fix:** Track connection handler Tasks in a concurrent collection. In `Dispose`, cancel all CTS tokens, then `await Task.WhenAll(connectionTasks)` with a timeout before disposing CTS instances. Implement `IAsyncDisposable` instead of `IDisposable`.

### R-DSK-033
- **Title:** Concurrent WebSocket connections can race on same BufferedTransaction without optimistic concurrency
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Race conditions
- **Description:** `OdooWsMessageHandler.HandleManagerUpdateAsync` and `HandleAttendantUpdateAsync` each create a scoped `AgentDbContext`, load a `BufferedTransaction` by `FccTransactionId`, mutate fields (OdooOrderId, OrderUuid, AddToCart, PaymentId), and save. When two WebSocket clients send concurrent updates for the same transaction (e.g., manager and attendant both update the same transaction simultaneously), both scopes read the same entity state, both apply their mutations, and the last `SaveChangesAsync` wins — silently overwriting the other's changes. No `RowVersion`/`ConcurrencyToken` is configured on `BufferedTransaction`, so EF Core cannot detect the conflict.
- **Evidence:**
  - `OdooWsMessageHandler.cs:98-113` — `HandleManagerUpdateAsync` reads, mutates, saves without concurrency check
  - `OdooWsMessageHandler.cs:134-169` — `HandleAttendantUpdateAsync` reads, mutates, saves without concurrency check
  - `BufferEntityConfiguration.cs:22-73` — no `.IsConcurrencyToken()` or `.IsRowVersion()` configured
  - `OdooWebSocketServer.cs:75-79` — each connection runs its own receive loop concurrently
- **Impact:** Lost updates when concurrent clients modify the same transaction. For example, a manager setting OdooOrderId can be silently overwritten by an attendant setting AddToCart at the same moment.
- **Recommended Fix:** Add a `RowVersion` (byte[]) or `ConcurrencyStamp` (string) property to `BufferedTransaction`, configure it as a concurrency token, and handle `DbUpdateConcurrencyException` with a retry-or-merge strategy.

### R-DSK-034
- **Title:** IntegrityChecker.RecoverCorruptDatabaseAsync calls EnsureCreatedAsync which bypasses migration history
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** State management
- **Description:** `IntegrityChecker.RecoverCorruptDatabaseAsync` (line 109) deletes the corrupt database file and calls `_db.Database.EnsureCreatedAsync(ct)` to recreate it. `EnsureCreatedAsync` creates tables from the current model snapshot but does NOT create the `__EFMigrationsHistory` table or apply migrations. This means after recovery: (1) the database schema may differ from what migrations expect, (2) future migration runs will fail because they think no migrations have been applied, (3) any data-seeding logic in migrations is skipped. The integrity checker uses the same `AgentDbContext` instance that was connected to the now-deleted database, which may have stale connection state.
- **Evidence:**
  - `IntegrityChecker.cs:109` — `await _db.Database.EnsureCreatedAsync(ct)` — bypasses migrations
  - `IntegrityChecker.cs:101-106` — deletes .db, -wal, and -shm files
  - `IntegrityChecker.cs:93` — `await _db.Database.CloseConnectionAsync()` — closes connection but reuses same DbContext
  - `AgentDbContext.cs:22-25` — `OnModelCreating` applies configurations from assembly
- **Impact:** After corruption recovery, the database is in a liminal state — tables exist but migration history is empty. The next application update that adds a migration will fail or produce a schema divergence.
- **Recommended Fix:** Replace `EnsureCreatedAsync` with `MigrateAsync` to recreate the database through the migration pipeline, ensuring `__EFMigrationsHistory` is populated and all migration-based seed data is applied.

### R-DSK-035
- **Title:** StatusPollWorker RefreshTokenAsync exceptions propagate unhandled — repeated auth failures on every cadence tick
- **Module:** Transaction Management
- **Severity:** High
- **Category:** Thread safety issues
- **Description:** `StatusPollWorker.PollAsync` (lines 89-98) calls `_tokenProvider.RefreshTokenAsync(ct)` after a 401 response but does not catch `RefreshTokenExpiredException` or `DeviceDecommissionedException`. Unlike `CloudUploadWorker` (lines 146-168) and `TelemetryReporter` (lines 114-133) which handle both exceptions by marking the device as decommissioned and setting a `_decommissioned` flag, `StatusPollWorker` lets these exceptions propagate to `CadenceController` where they are caught as generic warnings (line 222). The device is never marked as decommissioned, and `StatusPollWorker` has no `_decommissioned` volatile flag to short-circuit future calls. This results in repeated failed token refreshes and 401 errors on every cadence tick indefinitely.
- **Evidence:**
  - `StatusPollWorker.cs:93` — `token = await _tokenProvider.RefreshTokenAsync(ct)` — no specialized exception handling
  - `StatusPollWorker.cs:41-42` — `private volatile bool _decommissioned` exists but is only set on direct 403 response
  - `CloudUploadWorker.cs:146-168` — handles `RefreshTokenExpiredException` and `DeviceDecommissionedException`
  - `TelemetryReporter.cs:114-133` — handles both exceptions
  - `CadenceController.cs:220-225` — catches as generic warning without decommission marking
- **Impact:** Decommissioned devices generate continuous failed auth requests (one per cadence tick, typically every 30 seconds) indefinitely. Cloud backend receives persistent 401/refresh traffic from a device that should be silent.
- **Recommended Fix:** Add `try/catch` blocks around `RefreshTokenAsync` in `StatusPollWorker.PollAsync` matching the pattern in `CloudUploadWorker`: catch `RefreshTokenExpiredException` to call `MarkReprovisioningRequiredAsync()` and set `_decommissioned = true`, catch `DeviceDecommissionedException` to call `MarkDecommissionedAsync()` and set `_decommissioned = true`.

---

### R-DSK-036
- **Title:** `CloudUploadWorker` has no single-flight guard, so manual and scheduled uploads can race the same batch
- **Module:** Cloud Sync
- **Severity:** High
- **Category:** Race conditions
- **Description:** `ICloudSyncService` is registered as a singleton and can be invoked both by `CadenceController` and the dashboard's manual "Force Cloud Sync" action. `UploadBatchAsync()` does not serialize callers or reserve rows before reading them; it simply queries the oldest pending records with `AsNoTracking()`. Two overlapping calls can therefore upload the same transactions twice. If the first request is accepted and the second comes back as `DUPLICATE`, the second path rewrites those local rows to `DuplicateConfirmed`, and `StatusPollWorker` will never advance them because it only transitions `Uploaded` rows to `SyncedToOdoo`.
- **Evidence:**
  - `ServiceCollectionExtensions.cs:99-100` — `ICloudSyncService` is a singleton `CloudUploadWorker`
  - `CadenceController.cs:193-199` — background cadence loop invokes `UploadBatchAsync()`
  - `DashboardPage.axaml.cs:238-245` — manual dashboard action invokes the same singleton `UploadBatchAsync()`
  - `TransactionBufferManager.cs:79-86` — pending batch selection has no reservation/locking; it is a plain `AsNoTracking()` query
  - `CloudUploadWorker.cs:321-324` — duplicate outcomes call `MarkDuplicateConfirmedAsync`
  - `TransactionBufferManager.cs:115-120` — duplicate confirmation updates matching rows regardless of their current sync state
  - `TransactionBufferManager.cs:155-160` — status poll advances only rows currently at `SyncStatus.Uploaded`
  - `src/cloud/tests/FccMiddleware.IntegrationTests/Ingestion/UploadTransactionBatchTests.cs:129-156` — cloud upload endpoint returns `DUPLICATE` for a record that was accepted in an earlier upload
- **Impact:** Overlapping manual and scheduled uploads can inflate cloud traffic, produce duplicate upload requests, and strand local rows in `DuplicateConfirmed` where they never complete the normal `Uploaded -> SyncedToOdoo` lifecycle.
- **Recommended Fix:** Add a single-flight `SemaphoreSlim` (or a reservation state) around `UploadBatchAsync()` so only one batch upload can run at a time. In parallel, make `MarkDuplicateConfirmedAsync()` conditional on `Pending` rows only, or allow the status poller to advance duplicate-confirmed rows that represent already-uploaded cloud records.

---

### R-DSK-037
- **Title:** ConnectivityManager publishes the last FCC heartbeat timestamp without synchronization
- **Module:** Monitoring & Diagnostics
- **Severity:** Low
- **Category:** Thread safety issues
- **Description:** `ConnectivityManager` correctly publishes its main connectivity state through a volatile `ConnectivitySnapshot`, but the separate `_lastFccSuccessAt` field is written on the probe-loop thread and read concurrently from the UI and telemetry paths without any lock or atomic wrapper. Because `DateTimeOffset?` is a multi-field value type, this auxiliary heartbeat timestamp is not safely published the way `_current` is.
- **Evidence:**
  - `ConnectivityManager.cs:38-39` - `_lastFccSuccessAt` is stored as a plain `DateTimeOffset?`
  - `ConnectivityManager.cs:53` - `LastFccSuccessAtUtc` exposes the field directly
  - `ConnectivityManager.cs:272-274` - probe loop writes `_lastFccSuccessAt = DateTimeOffset.UtcNow`
  - `DashboardPage.axaml.cs:83-89` and `DashboardPage.axaml.cs:170-175` - dashboard reads `LastFccSuccessAtUtc`
  - `TelemetryReporter.cs:280-287` - telemetry reads the same field to compute heartbeat age
- **Impact:** During connectivity flaps or heavy UI polling, the dashboard and telemetry can observe stale or inconsistent FCC heartbeat ages, which undermines the reliability of outage diagnostics.
- **Recommended Fix:** Fold heartbeat metadata into the immutable `ConnectivitySnapshot`, or publish/read it via an atomic representation such as `long ticks` guarded by `Volatile.Read`/`Volatile.Write` or a lock.

---

### R-DSK-038
- **Title:** Cross-client WebSocket broadcasts are cancelled by the initiating client's disconnect token
- **Module:** Odoo Integration
- **Severity:** Medium
- **Category:** Race conditions
- **Description:** The WebSocket broadcast path reuses the cancellation token from the client that triggered the update. That token is created per connection and cancelled when that client disconnects. If the initiating socket drops while `BroadcastToAllAsync()` is mid-fan-out, the shared token cancels the remaining sends and other connected Odoo terminals silently miss the update.
- **Evidence:**
  - `OdooWebSocketServer.cs:71-80` — each connection gets its own linked cancellation token that is passed through message handling
  - `OdooWebSocketServer.cs:86` — the per-connection token is cancelled as soon as that client disconnects
  - `OdooWsMessageHandler.cs:112` — manager-update broadcasts pass the initiating connection token into `_broadcastToAll(...)`
  - `OdooWsMessageHandler.cs:131-132` — attendant-update broadcasts do the same
  - `OdooWebSocketServer.cs:244-255` — `BroadcastToAllAsync()` uses that caller token for every recipient `SendAsync(...)`
- **Impact:** Multi-terminal Odoo sessions can diverge: one terminal commits an order/cart update, then disconnects, and some peer terminals never receive the corresponding broadcast even though the database row is already changed.
- **Recommended Fix:** Decouple cross-client broadcasts from the caller's socket token. Use a server-lifetime token or `CancellationToken.None` for the fan-out loop, and handle per-recipient failures independently so one client disconnect cannot cancel delivery to the rest.
