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
