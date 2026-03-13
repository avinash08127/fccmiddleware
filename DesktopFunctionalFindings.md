# Desktop Functional Findings

> End-to-end functional audit of the FCC Desktop Edge Agent.

---

## Application Shell Module

### F-DSK-001
- **Title:** MainWindowViewModel is dead code — created but never bound to the view
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Incorrect UI state updates
- **Description:** `MainWindow.axaml` declares `x:DataType="vm:MainWindowViewModel"` (design-time annotation only), but the `MainWindow.axaml.cs` constructor never instantiates or assigns a `MainWindowViewModel` as `DataContext`. All status bar updates (connectivity, buffer depth, last sync) are done via direct named-control manipulation in the code-behind. The ViewModel contains identical connectivity subscription logic, buffer polling timer, and "last sync" computation — all completely unreachable at runtime.
- **Evidence:**
  - `MainWindow.axaml:10` — `x:DataType="vm:MainWindowViewModel"` (design-time only, no runtime effect)
  - `MainWindow.axaml.cs:33-55` — constructor resolves services and sets up timers directly
  - `MainWindowViewModel.cs:30-50` — duplicates the exact same service resolution and timer setup
  - No `DataContext = new MainWindowViewModel()` anywhere in `MainWindow.axaml.cs`
- **Impact:** Any future developer extending the ViewModel will be confused when changes have no effect. The dead code also doubles maintenance surface for connectivity display logic.
- **Recommended Fix:** Either (a) bind the ViewModel as DataContext and remove code-behind UI logic, or (b) delete MainWindowViewModel entirely and keep the code-behind approach.

---

### F-DSK-002
- **Title:** "Last Sync" time reflects connectivity status, not actual upload time
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Incorrect UI state updates
- **Description:** The status bar "Last sync" timestamp is set to `DateTimeOffset.UtcNow` whenever `_connectivity?.Current.IsInternetUp == true` during a buffer stats poll. This means the displayed time resets to "Just now" every 5 seconds as long as the internet is up — regardless of whether any transaction was actually uploaded. This gives operators a false impression that data is actively syncing.
- **Evidence:**
  - `MainWindow.axaml.cs:195-198` — `_lastSyncTime = DateTimeOffset.UtcNow` when internet is up
  - `MainWindowViewModel.cs:125-128` — identical logic in the dead ViewModel
- **Impact:** Field operators may not notice a stalled upload pipeline because the UI always says "Just now" when online.
- **Recommended Fix:** Source the last sync timestamp from `CloudUploadWorker` or a shared service that records the actual `DateTimeOffset` of the last successful upload.

---

### F-DSK-003
- **Title:** SplashWindow has hard-coded white background — ignores dark theme
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Inconsistent UI state updates
- **Description:** The splash screen sets `Background="White"` as a literal value. If the user or OS has a dark theme preference (and the app uses `RequestedThemeVariant="Default"` which inherits OS preference), the splash will flash bright white before the correctly-themed MainWindow appears.
- **Evidence:**
  - `SplashWindow.axaml:10` — `Background="White"`
  - `App.axaml:4` — `RequestedThemeVariant="Default"` (follows OS theme)
- **Impact:** Jarring visual flash for dark-theme users. Minor but noticeable UX issue.
- **Recommended Fix:** Replace `Background="White"` with `Background="{DynamicResource SystemControlBackgroundAltHighBrush}"` or use theme-aware resource.

---

### F-DSK-004
- **Title:** Restart Agent can fail to bind port — new process starts before old process releases resources
- **Module:** Application Shell
- **Severity:** High
- **Category:** Broken workflows
- **Description:** The "Restart Agent" tray menu handler starts a new process (`Process.Start`) before calling `ForceClose()` and `desktop.Shutdown()`. The old process still holds port 8585 (Kestrel) and potentially the SQLite database. The new process will attempt to bind port 8585 while the old process is still shutting down, likely causing a port conflict and crash on startup.
- **Evidence:**
  - `App.axaml.cs:154-158` — `Process.Start(...)` executes before `mainWindow.ForceClose()`
  - `Program.cs:147` — host `StopAsync()` only runs after Avalonia exits, which hasn't happened yet at the time the new process starts
- **Impact:** Agent restart via tray fails with port binding error, requiring manual intervention.
- **Recommended Fix:** Reverse the order: trigger shutdown first, and start the new process as the last step of the shutdown sequence (e.g., in the `desktop.Exit` handler or after `StopAsync` completes in Program.cs).

---

### F-DSK-005
- **Title:** Window state restoration does not validate against actual screen bounds
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Incorrect navigation behavior
- **Description:** `RestoreWindowState()` only checks `X >= 0 && Y >= 0` before restoring the saved position. If the window was previously on a secondary monitor (e.g., X=2000) and that monitor is no longer connected, the window will be placed off-screen and inaccessible.
- **Evidence:**
  - `MainWindow.axaml.cs:232-234` — only validates `X >= 0 && Y >= 0`, ignores actual display extents
- **Impact:** Users with multi-monitor setups may encounter an invisible main window after monitor configuration changes, requiring manual workaround (keyboard shortcuts or registry edit).
- **Recommended Fix:** Check the saved position against `Screens.Primary.WorkingArea` (or all screens) and fall back to `CenterScreen` if the position is outside any connected display.

---

### F-DSK-006
- **Title:** SettingsViewModel Save shows technical exception message for invalid port input
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Incorrect error messages
- **Description:** The `Save()` method uses `int.Parse(FccPort)` without prior validation. If the user enters non-numeric text (e.g., "abc"), the catch block displays `FormatException.Message` ("Input string was not in a correct format") which is not a user-friendly error message.
- **Evidence:**
  - `SettingsViewModel.cs:100` — `int.Parse(FccPort)` with no pre-validation
  - `SettingsViewModel.cs:112` — `Feedback = $"Error: {ex.Message}"` shows raw exception text
- **Impact:** Users see technical .NET error messages instead of actionable guidance like "Port must be a number between 1 and 65535".
- **Recommended Fix:** Validate port fields with `int.TryParse` and range check (1-65535) before calling `SaveAll`, showing a descriptive error.

---

## Device Provisioning Module

### F-DSK-007
- **Title:** Re-provisioning flow broken — LaunchAgentAsync calls Start() on already-running host
- **Module:** Device Provisioning
- **Severity:** Critical
- **Category:** Broken workflows
- **Description:** When `ReprovisioningRequired` fires at runtime (refresh token expired), `App.axaml.cs` creates a new `ProvisioningWindow`. The user completes provisioning and clicks "Launch Agent", which invokes `LaunchAgentAsync()`. This method calls `webApp.Start()` unconditionally — but the host was already started during the initial Normal-mode startup and was never stopped. `WebApplication.Start()` throws `InvalidOperationException` ("The server has already been started") when called on a running host. The exception is caught, showing "Retry Launch", but retrying has the same result. `RegistrationCompleted` is never raised, so the app is stuck on the provisioning screen with no path forward.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:692-695` — `await Task.Run(() => webApp.Start())` with comment "Start the host if not already running" but no actual running-state check
  - `App.axaml.cs:205-231` — `ReprovisioningRequired` handler creates new ProvisioningWindow but never stops the host
  - `Program.cs:123` — `webApp.Start()` called during Normal startup, host remains running
  - `ProvisioningWindow.axaml.cs:699` — `RegistrationCompleted?.Invoke(...)` is unreachable because the exception is caught on line 702
- **Impact:** Re-provisioning after refresh token expiry is completely broken. The user must manually kill and restart the agent process. This is the primary recovery path for expired credentials.
- **Recommended Fix:** Check `webApp` running state before calling `Start()`. Either skip `Start()` if already running, or stop the host in the `ReprovisioningRequired` handler before creating the new ProvisioningWindow.

---

### F-DSK-008
- **Title:** Back button from Step 3 allows re-registration with consumed one-time token
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** Broken workflows
- **Description:** After successful cloud registration in Step 2, the wizard advances to Step 3 (connection tests). The Back button on Step 3 returns to Step 2, showing the registration form with the original token still in the input. If the user clicks "Register" again, `DeviceRegistrationService.RegisterAsync` sends the same one-time token, which the cloud rejects with `BOOTSTRAP_TOKEN_ALREADY_USED`. The first registration's tokens and state are already persisted, so the rejection confuses the user into thinking registration failed. Going back from Step 3 should not allow re-registration since it already succeeded.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:91-93` — `case 3: GoToStep(2); break;` — allows return to registration form after success
  - `ProvisioningWindow.axaml.cs:255` — `GoToStep(3)` called only after successful registration
  - `DeviceRegistrationService.cs:76-78` — registration persists tokens on success, creating orphaned credentials on re-attempt rejection
- **Impact:** Users who navigate back from connection tests and retry registration see confusing "token already used" error. Previously stored credentials remain valid but the UI shows a failure state.
- **Recommended Fix:** Disable the Back button on Step 3 when the step was reached via successful cloud registration (not from manual offline path). Alternatively, skip the registration call if state is already registered.

---

### F-DSK-009
- **Title:** Code-based registration path shows misleading error for malformed cloud URLs
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Incorrect error messages
- **Description:** In `RegisterWithCodeAsync()`, the cloud URL is validated only for non-empty string. Malformed URLs like "not-a-url" pass this check and reach `DeviceRegistrationService.RegisterAsync()`, which validates via `CloudUrlGuard.IsSecure()`. Since `Uri.TryCreate("not-a-url", ...)` returns false, the guard reports "Cloud URL must use HTTPS. HTTP is only allowed for localhost development." — which is misleading when the real issue is that the URL is not a valid URI at all. The manual config path correctly validates URL format before submission.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:176-183` — only checks `string.IsNullOrWhiteSpace(cloudUrl)`, no URI format validation
  - `ProvisioningWindow.axaml.cs:322-327` — manual path validates with `Uri.TryCreate` (correct)
  - `DeviceRegistrationService.cs:51-57` — `CloudUrlGuard.IsSecure` returns false for non-URI strings, producing HTTPS-specific error message
- **Impact:** Users entering a malformed URL in the code path see a confusing error about HTTPS instead of "Please enter a valid URL".
- **Recommended Fix:** Add the same `Uri.TryCreate` validation from `ValidateManualConfigAsync` to `RegisterWithCodeAsync` before calling the service.

---

### F-DSK-010
- **Title:** Connection test hardcodes HTTP for FCC — HTTPS FCC endpoints appear unreachable
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** Broken workflows
- **Description:** `RunConnectionTestsAsync()` constructs the FCC test URL as `http://{host}:{port}`, hardcoding the HTTP scheme. If the Forecourt Controller endpoint uses HTTPS (as some Advatec and Radix deployments do), the test always reports "Unreachable" because the HTTP request either times out or is rejected. The user sees a failed connectivity test for a perfectly working FCC endpoint. The actual agent runtime adapter uses the URL from `AgentConfiguration.FccBaseUrl` which may specify HTTPS.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:502` — `var fccUrl = $"http://{_resolvedFccHost}:{_resolvedFccPort}"` — hardcoded `http://`
- **Impact:** HTTPS FCC sites always fail the provisioning connectivity test, causing user confusion and unnecessary "Continue Anyway" clicks. The agent works correctly after launch because the runtime adapter uses the proper URL scheme.
- **Recommended Fix:** Try HTTPS first, fall back to HTTP if HTTPS fails. Or accept any response from either scheme as "reachable". Alternatively, use the scheme from the cloud-provided FCC configuration if available.

---

### F-DSK-011
- **Title:** Manual offline config generates truncated device ID with reduced collision resistance
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Incorrect data persistence
- **Description:** When using manual configuration without a provisioning token (offline mode), the device ID is generated as `$"manual-{Guid.NewGuid():N}"[..24]`, which produces a 24-character string: the 7-character prefix "manual-" plus 17 hex characters from the GUID. This truncates the GUID's 128-bit randomness to approximately 68 bits (17 hex chars × 4 bits). While collision probability is still very low, the truncation is unnecessary and the resulting ID doesn't conform to any standard format that the cloud backend might expect for device IDs.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:344` — `_resolvedDeviceId = $"manual-{Guid.NewGuid():N}"[..24]`
- **Impact:** Low collision risk (1 in 2^68) but the non-standard format could cause issues if cloud APIs validate device ID format or length. The truncation serves no purpose.
- **Recommended Fix:** Use the full GUID: `$"manual-{Guid.NewGuid():N}"` (39 characters) or generate a proper format like `$"manual-{Guid.NewGuid()}"` (43 characters with hyphens).
