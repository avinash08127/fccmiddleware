# Desktop Functional Findings

> End-to-end functional audit of the FCC Desktop Edge Agent.

---

## Application Shell Module

### F-DSK-001
- **Title:** MainWindowViewModel is dead code — created but never bound to the view
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Incorrect UI state updates
- **Status:** FIXED
- **Description:** `MainWindow.axaml` declares `x:DataType="vm:MainWindowViewModel"` (design-time annotation only), but the `MainWindow.axaml.cs` constructor never instantiates or assigns a `MainWindowViewModel` as `DataContext`. All status bar updates (connectivity, buffer depth, last sync) are done via direct named-control manipulation in the code-behind. The ViewModel contains identical connectivity subscription logic, buffer polling timer, and "last sync" computation — all completely unreachable at runtime.
- **Evidence:**
  - `MainWindow.axaml:10` — `x:DataType="vm:MainWindowViewModel"` (design-time only, no runtime effect)
  - `MainWindow.axaml.cs:33-55` — constructor resolves services and sets up timers directly
  - `MainWindowViewModel.cs:30-50` — duplicates the exact same service resolution and timer setup
  - No `DataContext = new MainWindowViewModel()` anywhere in `MainWindow.axaml.cs`
- **Impact:** Any future developer extending the ViewModel will be confused when changes have no effect. The dead code also doubles maintenance surface for connectivity display logic.
- **Recommended Fix:** Either (a) bind the ViewModel as DataContext and remove code-behind UI logic, or (b) delete MainWindowViewModel entirely and keep the code-behind approach.
- **Fix Applied:** Option (b) — deleted `MainWindowViewModel.cs` entirely and removed the `x:DataType` / `xmlns:vm` design-time annotation from `MainWindow.axaml`. The code-behind approach is kept as the single source of truth for status bar logic. `RelayCommand<T>` was only used by the dead ViewModel; `SettingsViewModel` has its own private `RelayCommand`.

---

### F-DSK-002
- **Title:** "Last Sync" time reflects connectivity status, not actual upload time
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Incorrect UI state updates
- **Status:** FIXED
- **Description:** The status bar "Last sync" timestamp is set to `DateTimeOffset.UtcNow` whenever `_connectivity?.Current.IsInternetUp == true` during a buffer stats poll. This means the displayed time resets to "Just now" every 5 seconds as long as the internet is up — regardless of whether any transaction was actually uploaded. This gives operators a false impression that data is actively syncing.
- **Evidence:**
  - `MainWindow.axaml.cs:195-198` — `_lastSyncTime = DateTimeOffset.UtcNow` when internet is up
  - `MainWindowViewModel.cs:125-128` — identical logic in the dead ViewModel
- **Impact:** Field operators may not notice a stalled upload pipeline because the UI always says "Just now" when online.
- **Recommended Fix:** Source the last sync timestamp from `CloudUploadWorker` or a shared service that records the actual `DateTimeOffset` of the last successful upload.
- **Fix Applied:** Replaced connectivity-based proxy with actual `SyncStateRecord.LastUploadAt` from the database. The status bar timer now queries `AgentDbContext.SyncStates` (the single-row sync state table written by `CloudUploadWorker`) and displays the real elapsed time since the last successful upload. Shows "Last sync: Never" when no upload has occurred.

---

### F-DSK-003
- **Title:** SplashWindow has hard-coded white background — ignores dark theme
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Inconsistent UI state updates
- **Status:** FIXED
- **Description:** The splash screen sets `Background="White"` as a literal value. If the user or OS has a dark theme preference (and the app uses `RequestedThemeVariant="Default"` which inherits OS preference), the splash will flash bright white before the correctly-themed MainWindow appears.
- **Evidence:**
  - `SplashWindow.axaml:10` — `Background="White"`
  - `App.axaml:4` — `RequestedThemeVariant="Default"` (follows OS theme)
- **Impact:** Jarring visual flash for dark-theme users. Minor but noticeable UX issue.
- **Recommended Fix:** Replace `Background="White"` with `Background="{DynamicResource SystemControlBackgroundAltHighBrush}"` or use theme-aware resource.
- **Fix Applied:** Replaced `Background="White"` with `Background="{DynamicResource SystemControlBackgroundAltHighBrush}"` for theme-aware background. Also replaced hard-coded `Foreground="#666666"` on the subtitle TextBlock with `Opacity="0.6"` so it adapts to both light and dark themes.

---

### F-DSK-004
- **Title:** Restart Agent can fail to bind port — new process starts before old process releases resources
- **Module:** Application Shell
- **Severity:** High
- **Category:** Broken workflows
- **Status:** FIXED
- **Description:** The "Restart Agent" tray menu handler starts a new process (`Process.Start`) before calling `ForceClose()` and `desktop.Shutdown()`. The old process still holds port 8585 (Kestrel) and potentially the SQLite database. The new process will attempt to bind port 8585 while the old process is still shutting down, likely causing a port conflict and crash on startup.
- **Evidence:**
  - `App.axaml.cs:154-158` — `Process.Start(...)` executes before `mainWindow.ForceClose()`
  - `Program.cs:147` — host `StopAsync()` only runs after Avalonia exits, which hasn't happened yet at the time the new process starts
- **Impact:** Agent restart via tray fails with port binding error, requiring manual intervention.
- **Recommended Fix:** Reverse the order: trigger shutdown first, and start the new process as the last step of the shutdown sequence (e.g., in the `desktop.Exit` handler or after `StopAsync` completes in Program.cs).
- **Fix Applied:** Reversed the shutdown/restart order. Now calls `mainWindow.ForceClose()` first, then registers a one-shot `desktop.Exit` handler that launches the new process via `Process.Start`. Finally calls `desktop.Shutdown()`. The new process starts only after the desktop lifetime exits, which is after `Program.cs` calls `webApp.StopAsync()` releasing port 8585 and SQLite locks.

---

### F-DSK-005
- **Title:** Window state restoration does not validate against actual screen bounds
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Incorrect navigation behavior
- **Status:** FIXED
- **Description:** `RestoreWindowState()` only checks `X >= 0 && Y >= 0` before restoring the saved position. If the window was previously on a secondary monitor (e.g., X=2000) and that monitor is no longer connected, the window will be placed off-screen and inaccessible.
- **Evidence:**
  - `MainWindow.axaml.cs:232-234` — only validates `X >= 0 && Y >= 0`, ignores actual display extents
- **Impact:** Users with multi-monitor setups may encounter an invisible main window after monitor configuration changes, requiring manual workaround (keyboard shortcuts or registry edit).
- **Recommended Fix:** Check the saved position against `Screens.Primary.WorkingArea` (or all screens) and fall back to `CenterScreen` if the position is outside any connected display.
- **Fix Applied:** Added `IsPositionOnAnyScreen(x, y)` helper that checks the saved position against `Screens.All` working areas. `RestoreWindowState()` now falls back to default `CenterScreen` positioning if the saved coordinates are outside all connected displays. Gracefully handles the case where `Screens` is null (allows restore).

---

### F-DSK-006
- **Title:** SettingsViewModel Save shows technical exception message for invalid port input
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Incorrect error messages
- **Status:** FIXED
- **Description:** The `Save()` method uses `int.Parse(FccPort)` without prior validation. If the user enters non-numeric text (e.g., "abc"), the catch block displays `FormatException.Message` ("Input string was not in a correct format") which is not a user-friendly error message.
- **Evidence:**
  - `SettingsViewModel.cs:100` — `int.Parse(FccPort)` with no pre-validation
  - `SettingsViewModel.cs:112` — `Feedback = $"Error: {ex.Message}"` shows raw exception text
- **Impact:** Users see technical .NET error messages instead of actionable guidance like "Port must be a number between 1 and 65535".
- **Recommended Fix:** Validate port fields with `int.TryParse` and range check (1-65535) before calling `SaveAll`, showing a descriptive error.
- **Fix Applied:** Replaced `int.Parse()` with a `TryParsePort()` helper that uses `int.TryParse` with range validation (1-65535). Each port field (FCC Port, JPL Port, WebSocket Port) is validated individually with a descriptive error message (e.g., "FCC Port must be a number between 1 and 65535.") shown in the Feedback field before any save attempt. Blank fields are treated as null (no override).

---

## Device Provisioning Module

### F-DSK-007
- **Title:** Re-provisioning flow broken — LaunchAgentAsync calls Start() on already-running host
- **Module:** Device Provisioning
- **Severity:** Critical
- **Category:** Broken workflows
- **Status:** FIXED
- **Description:** When `ReprovisioningRequired` fires at runtime (refresh token expired), `App.axaml.cs` creates a new `ProvisioningWindow`. The user completes provisioning and clicks "Launch Agent", which invokes `LaunchAgentAsync()`. This method calls `webApp.Start()` unconditionally — but the host was already started during the initial Normal-mode startup and was never stopped. `WebApplication.Start()` throws `InvalidOperationException` ("The server has already been started") when called on a running host. The exception is caught, showing "Retry Launch", but retrying has the same result. `RegistrationCompleted` is never raised, so the app is stuck on the provisioning screen with no path forward.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:692-695` — `await Task.Run(() => webApp.Start())` with comment "Start the host if not already running" but no actual running-state check
  - `App.axaml.cs:205-231` — `ReprovisioningRequired` handler creates new ProvisioningWindow but never stops the host
  - `Program.cs:123` — `webApp.Start()` called during Normal startup, host remains running
  - `ProvisioningWindow.axaml.cs:699` — `RegistrationCompleted?.Invoke(...)` is unreachable because the exception is caught on line 702
- **Impact:** Re-provisioning after refresh token expiry is completely broken. The user must manually kill and restart the agent process. This is the primary recovery path for expired credentials.
- **Recommended Fix:** Check `webApp` running state before calling `Start()`. Either skip `Start()` if already running, or stop the host in the `ReprovisioningRequired` handler before creating the new ProvisioningWindow.
- **Fix Applied:** Added `AgentAppContext.IsHostStarted` boolean flag. `Program.cs` sets it to `true` after calling `webApp.Start()` in Normal mode. `ProvisioningWindow.LaunchAgentAsync()` now checks `!AgentAppContext.IsHostStarted` before calling `Start()` — if the host is already running (re-provisioning path), the call is skipped and `RegistrationCompleted` fires normally, allowing the app to transition back to `MainWindow`.

---

### F-DSK-008
- **Title:** Back button from Step 3 allows re-registration with consumed one-time token
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** Broken workflows
- **Status:** FIXED
- **Description:** After successful cloud registration in Step 2, the wizard advances to Step 3 (connection tests). The Back button on Step 3 returns to Step 2, showing the registration form with the original token still in the input. If the user clicks "Register" again, `DeviceRegistrationService.RegisterAsync` sends the same one-time token, which the cloud rejects with `BOOTSTRAP_TOKEN_ALREADY_USED`. The first registration's tokens and state are already persisted, so the rejection confuses the user into thinking registration failed. Going back from Step 3 should not allow re-registration since it already succeeded.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:91-93` — `case 3: GoToStep(2); break;` — allows return to registration form after success
  - `ProvisioningWindow.axaml.cs:255` — `GoToStep(3)` called only after successful registration
  - `DeviceRegistrationService.cs:76-78` — registration persists tokens on success, creating orphaned credentials on re-attempt rejection
- **Impact:** Users who navigate back from connection tests and retry registration see confusing "token already used" error. Previously stored credentials remain valid but the UI shows a failure state.
- **Recommended Fix:** Disable the Back button on Step 3 when the step was reached via successful cloud registration (not from manual offline path). Alternatively, skip the registration call if state is already registered.
- **Fix Applied:** Added `_cloudRegistrationDone` flag, set to `true` on successful cloud registration in both `RegisterWithCodeAsync` and `RegisterManualWithTokenAsync`. `GoToStep(3)` now sets `BackButton.IsVisible = !_cloudRegistrationDone` — the Back button is hidden when cloud registration succeeded (preventing re-submission of the consumed token) but remains visible for the offline manual config path where no token was used.

---

### F-DSK-009
- **Title:** Code-based registration path shows misleading error for malformed cloud URLs
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Incorrect error messages
- **Status:** FIXED
- **Description:** In `RegisterWithCodeAsync()`, the cloud URL is validated only for non-empty string. Malformed URLs like "not-a-url" pass this check and reach `DeviceRegistrationService.RegisterAsync()`, which validates via `CloudUrlGuard.IsSecure()`. Since `Uri.TryCreate("not-a-url", ...)` returns false, the guard reports "Cloud URL must use HTTPS. HTTP is only allowed for localhost development." — which is misleading when the real issue is that the URL is not a valid URI at all. The manual config path correctly validates URL format before submission.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:176-183` — only checks `string.IsNullOrWhiteSpace(cloudUrl)`, no URI format validation
  - `ProvisioningWindow.axaml.cs:322-327` — manual path validates with `Uri.TryCreate` (correct)
  - `DeviceRegistrationService.cs:51-57` — `CloudUrlGuard.IsSecure` returns false for non-URI strings, producing HTTPS-specific error message
- **Impact:** Users entering a malformed URL in the code path see a confusing error about HTTPS instead of "Please enter a valid URL".
- **Recommended Fix:** Add the same `Uri.TryCreate` validation from `ValidateManualConfigAsync` to `RegisterWithCodeAsync` before calling the service.
- **Fix Applied:** Added `Uri.TryCreate` validation with scheme check (`http`/`https`) to `RegisterWithCodeAsync`, matching the existing validation in `ValidateManualConfigAsync`. Malformed URLs now show "Cloud URL must be a valid HTTP/HTTPS URL." before reaching the service layer.

---

### F-DSK-010
- **Title:** Connection test hardcodes HTTP for FCC — HTTPS FCC endpoints appear unreachable
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** Broken workflows
- **Status:** FIXED
- **Description:** `RunConnectionTestsAsync()` constructs the FCC test URL as `http://{host}:{port}`, hardcoding the HTTP scheme. If the Forecourt Controller endpoint uses HTTPS (as some Advatec and Radix deployments do), the test always reports "Unreachable" because the HTTP request either times out or is rejected. The user sees a failed connectivity test for a perfectly working FCC endpoint. The actual agent runtime adapter uses the URL from `AgentConfiguration.FccBaseUrl` which may specify HTTPS.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:502` — `var fccUrl = $"http://{_resolvedFccHost}:{_resolvedFccPort}"` — hardcoded `http://`
- **Impact:** HTTPS FCC sites always fail the provisioning connectivity test, causing user confusion and unnecessary "Continue Anyway" clicks. The agent works correctly after launch because the runtime adapter uses the proper URL scheme.
- **Recommended Fix:** Try HTTPS first, fall back to HTTP if HTTPS fails. Or accept any response from either scheme as "reachable". Alternatively, use the scheme from the cloud-provided FCC configuration if available.
- **Fix Applied:** FCC connectivity test now tries HTTPS first (5-second timeout), then falls back to HTTP. Any response from either scheme is treated as "reachable". The status detail now reports which scheme succeeded (e.g., "Reachable at host:port (HTTPS)"). If both schemes fail, the error message indicates both were attempted.

---

### F-DSK-011
- **Title:** Manual offline config generates truncated device ID with reduced collision resistance
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Incorrect data persistence
- **Status:** FIXED
- **Description:** When using manual configuration without a provisioning token (offline mode), the device ID is generated as `$"manual-{Guid.NewGuid():N}"[..24]`, which produces a 24-character string: the 7-character prefix "manual-" plus 17 hex characters from the GUID. This truncates the GUID's 128-bit randomness to approximately 68 bits (17 hex chars × 4 bits). While collision probability is still very low, the truncation is unnecessary and the resulting ID doesn't conform to any standard format that the cloud backend might expect for device IDs.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:344` — `_resolvedDeviceId = $"manual-{Guid.NewGuid():N}"[..24]`
- **Impact:** Low collision risk (1 in 2^68) but the non-standard format could cause issues if cloud APIs validate device ID format or length. The truncation serves no purpose.
- **Recommended Fix:** Use the full GUID: `$"manual-{Guid.NewGuid():N}"` (39 characters) or generate a proper format like `$"manual-{Guid.NewGuid()}"` (43 characters with hyphens).
- **Resolution:** Removed the `[..24]` truncation — now generates `$"manual-{Guid.NewGuid():N}"` (39 characters, full 128-bit GUID).

---

## Authentication & Security Module

### F-DSK-012
- **Title:** WebSocket server accepts all connections without any authentication
- **Module:** Authentication & Security
- **Severity:** High
- **Category:** Broken workflows
- **Status:** FIXED
- **Description:** `OdooWebSocketServer.HandleConnectionAsync()` accepts any WebSocket connection without checking an API key, origin header, or any form of authentication. The REST API enforces `X-Api-Key` via `ApiKeyMiddleware`, but the WebSocket endpoint on port 8443 is completely unprotected. Any device on the LAN can connect and execute all WebSocket commands: query transactions (`latest`, `all`), update transaction fields (`manager_update`, `attendant_update`), mark transactions as discarded (`manager_manual_update`), and receive real-time pump status broadcasts. This creates an asymmetric security posture where the REST API is gated but an equivalent WebSocket API is wide open.
- **Evidence:**
  - `OdooWebSocketServer.cs:56-98` — `HandleConnectionAsync` has no auth check before entering the receive loop
  - `OdooWsMessageHandler.cs:90-121` — `HandleManagerUpdateAsync` modifies database records without authentication
  - `OdooWsMessageHandler.cs:239-264` — `HandleManagerManualUpdateAsync` sets `IsDiscard = true` without auth
  - `LocalApiStartup.cs:83` — REST path has `app.UseMiddleware<ApiKeyMiddleware>()` but WebSocket path does not
- **Impact:** Any LAN-connected device can query all transaction data, modify transaction records, and observe real-time pump operations without credentials.
- **Recommended Fix:** Add WebSocket authentication — either validate an API key in the initial HTTP upgrade request (query parameter or header), or require an authentication message as the first WebSocket frame before processing commands.
- **Resolution:** Added `ValidateApiKey()` check in `OdooWsBridge.cs` that validates the `X-Api-Key` header (or `apiKey` query parameter for WebSocket clients that cannot set custom headers) on the HTTP upgrade request before calling `AcceptWebSocketAsync()`. Uses the same constant-time comparison from `ApiKeyMiddleware`. Unauthenticated upgrade requests receive HTTP 401.

---

### F-DSK-013
- **Title:** TelemetryReporter does not handle RefreshTokenExpiredException — crashes the cadence tick
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Broken workflows
- **Status:** FIXED
- **Description:** When `TelemetryReporter.ReportAsync()` receives a 401 and calls `_tokenProvider.RefreshTokenAsync(ct)`, it does not catch `RefreshTokenExpiredException` or `DeviceDecommissionedException` from the token refresh call. Both `CloudUploadWorker` and `ConfigPollWorker` handle these exceptions by calling `MarkReprovisioningRequiredAsync()` or `MarkDecommissionedAsync()` respectively. TelemetryReporter lets them propagate as unhandled exceptions. The outer catch (line 147) catches `Exception` but does not distinguish between transient failures and permanent auth failures requiring re-provisioning. The device enters a state where telemetry perpetually attempts and fails to refresh an expired token, generating repeated log noise without triggering the re-provisioning flow.
- **Evidence:**
  - `TelemetryReporter.cs:109-115` — `token = await _tokenProvider.RefreshTokenAsync(ct)` — no catch for `RefreshTokenExpiredException`
  - `CloudUploadWorker.cs:149-159` — correctly handles `RefreshTokenExpiredException` → `MarkReprovisioningRequiredAsync()`
  - `ConfigPollWorker.cs:83-99` — handles the exception (though missing explicit catch — see below)
  - `TelemetryReporter.cs:147` — generic `catch (Exception)` swallows the auth exception silently
- **Impact:** Expired refresh token is not surfaced to the registration manager by the telemetry path, potentially delaying the re-provisioning prompt if telemetry is the first worker to encounter the expired token.
- **Recommended Fix:** Add explicit catch blocks for `RefreshTokenExpiredException` and `DeviceDecommissionedException` in `ReportAsync`, mirroring the pattern from `CloudUploadWorker`.
- **Resolution:** Added try-catch around `RefreshTokenAsync` in `TelemetryReporter.ReportAsync` that catches `RefreshTokenExpiredException` (calls `MarkReprovisioningRequiredAsync()`) and `DeviceDecommissionedException` (calls `MarkDecommissionedAsync()`), matching the `CloudUploadWorker` pattern.

---

### F-DSK-014
- **Title:** ConfigPollWorker does not catch RefreshTokenExpiredException — unhandled exception bubbles to CadenceController
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Broken workflows
- **Status:** FIXED
- **Description:** `ConfigPollWorker.PollAsync()` handles `UnauthorizedAccessException` (401) by calling `_tokenProvider.RefreshTokenAsync(ct)`. However, `RefreshTokenAsync` can throw `RefreshTokenExpiredException` (when the refresh token itself has expired) or `DeviceDecommissionedException` (when the server returns 403). The `ConfigPollWorker.PollAsync` method catches `DeviceDecommissionedException` from the initial poll request (line 101) but NOT from the token refresh call inside the 401 handler (line 84). If `RefreshTokenAsync` throws `RefreshTokenExpiredException`, it propagates uncaught through `PollAsync` to the `CadenceController`, which logs it as an unhandled error but does not trigger re-provisioning.
- **Evidence:**
  - `ConfigPollWorker.cs:84` — `token = await _tokenProvider.RefreshTokenAsync(ct)` inside 401 handler — no catch for `RefreshTokenExpiredException`
  - `ConfigPollWorker.cs:101-108` — `DeviceDecommissionedException` caught only from `SendPollRequestAsync`, not from `RefreshTokenAsync`
  - `CloudUploadWorker.cs:148-168` — correctly catches both exceptions from `RefreshTokenAsync` (the model to follow)
- **Impact:** Expired refresh token detected during config poll does not trigger re-provisioning. The cadence controller logs an error and retries on the next tick, perpetually failing without user notification.
- **Recommended Fix:** Wrap the `RefreshTokenAsync` call in a try-catch that handles `RefreshTokenExpiredException` (call `MarkReprovisioningRequiredAsync`) and `DeviceDecommissionedException` (call `MarkDecommissionedAsync`), matching the `CloudUploadWorker` pattern.
- **Resolution:** Added try-catch around `RefreshTokenAsync` in `ConfigPollWorker.PollAsync` that catches `RefreshTokenExpiredException` (calls `MarkReprovisioningRequiredAsync()`) and `DeviceDecommissionedException` (calls `MarkDecommissionedAsync()`, sets `_decommissioned = true`), matching the `CloudUploadWorker` pattern.

---

### F-DSK-015
- **Title:** StatusPollWorker does not catch RefreshTokenExpiredException from token refresh
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Broken workflows
- **Status:** FIXED
- **Description:** Same pattern as F-DSK-014. `StatusPollWorker.PollAsync()` handles 401 by calling `_tokenProvider.RefreshTokenAsync(ct)` (line 93) without catching `RefreshTokenExpiredException` or `DeviceDecommissionedException` from the refresh call. The exceptions propagate to the cadence controller as unhandled errors.
- **Evidence:**
  - `StatusPollWorker.cs:93` — `token = await _tokenProvider.RefreshTokenAsync(ct)` — no exception handling
  - `StatusPollWorker.cs:110-118` — `DeviceDecommissionedException` caught from `SendPollRequestAsync` but not from refresh
- **Impact:** Same as F-DSK-014 — expired refresh token from status poll path doesn't trigger re-provisioning.
- **Recommended Fix:** Add the same `RefreshTokenExpiredException` / `DeviceDecommissionedException` catch blocks around the `RefreshTokenAsync` call.
- **Resolution:** Added try-catch around `RefreshTokenAsync` in `StatusPollWorker.PollAsync` that catches `RefreshTokenExpiredException` (calls `MarkReprovisioningRequiredAsync()`, sets `_decommissioned = true`) and `DeviceDecommissionedException` (calls `MarkDecommissionedAsync()`, sets `_decommissioned = true`), matching the `CloudUploadWorker` pattern.

---

### F-DSK-016
- **Title:** CredentialStoreApiKeyPostConfigure blocks on async with .GetAwaiter().GetResult()
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Broken workflows
- **Status:** FIXED
- **Description:** `CredentialStoreApiKeyPostConfigure.PostConfigure()` calls `_store.GetSecretAsync(CredentialKeys.LanApiKey).GetAwaiter().GetResult()` to synchronously block on the async credential store read. On Windows, `PlatformCredentialStore.GetSecretWindowsAsync()` acquires `_fileLock` (a `SemaphoreSlim`) and does async file I/O. If `PostConfigure` is called from a context where `_fileLock` is already held (e.g., during provisioning when `SetSecretAsync` is in progress on another thread), the `.GetAwaiter().GetResult()` will deadlock because the calling thread blocks waiting for the semaphore while the semaphore-holding thread may need the same thread to complete. Even without deadlock, synchronous-over-async is an anti-pattern that can exhaust the thread pool under load.
- **Evidence:**
  - `LocalApiStartup.cs:114` — `_store.GetSecretAsync(CredentialKeys.LanApiKey).GetAwaiter().GetResult()` — sync-over-async
  - `PlatformCredentialStore.cs:101` — `await _fileLock.WaitAsync(ct)` — semaphore used in the async path
- **Impact:** Potential deadlock during startup if credential store operations overlap. Thread pool starvation under concurrent Options resolution.
- **Recommended Fix:** Use `IPostConfigureOptions<T>` with a pre-resolved value: load the key asynchronously during `IHostedService.StartAsync` or during host build, and inject the result into the PostConfigure. Alternatively, use a synchronous read path that doesn't acquire the async semaphore.
- **Resolution:** Converted `CredentialStoreApiKeyPostConfigure` to also implement `IHostedService`. The async key load now happens in `StartAsync` (before any HTTP requests arrive), and `PostConfigure` simply uses the cached value — no sync-over-async blocking. DI registration uses a shared singleton instance for both `IPostConfigureOptions<LocalApiOptions>` and `IHostedService`.

---

### F-DSK-017
- **Title:** Radix signature validation uses SHA-1 — deprecated cryptographic hash
- **Module:** Authentication & Security
- **Severity:** Low
- **Category:** Incorrect form validations
- **Status:** FIXED
- **Description:** `RadixSignatureHelper` uses SHA-1 for all message signing and signature validation. SHA-1 has known collision vulnerabilities (SHAttered attack, 2017) and is deprecated by NIST for digital signatures. While this is dictated by the Radix FCC protocol and cannot be changed unilaterally, the codebase should document this as a known limitation and avoid using SHA-1 for any other purpose.
- **Evidence:**
  - `RadixSignatureHelper.cs:97-100` — `SHA1.HashData(inputBytes)` — SHA-1 used for signing
  - `RadixSignatureHelper.cs:84-88` — `ValidateSignature` uses SHA-1 comparison
- **Impact:** SHA-1 collision vulnerabilities could theoretically allow a compromised FCC to forge transaction signatures. Risk is limited to the LAN segment.
- **Recommended Fix:** Document the SHA-1 dependency as a protocol limitation. Consider adding a runtime warning log when SHA-1 signatures are validated. If Radix supports SHA-256 in newer protocol versions, prefer it.
- **Resolution:** Added comprehensive SECURITY NOTICE to class-level XML docs documenting SHA-1 as a Radix FCC protocol mandate. Added `<remarks>` on `Sha1Hex` method and inline comment on the `SHA1.HashData` call. SHA-1 usage is now explicitly marked as protocol-mandated and not reusable for other purposes.

---

## Configuration Module

### F-DSK-018
- **Title:** ConfigurationPage Save silently drops all vendor-specific FCC fields — data loss on save
- **Module:** Configuration
- **Severity:** Critical
- **Category:** Incorrect data persistence
- **Status:** FIXED
- **Description:** `ConfigurationPage.OnSaveClicked` constructs a new `SiteConfigFcc` from the current UI values but only copies basic fields (`Enabled`, `FccId`, `Vendor`, `ConnectionProtocol`, `HostAddress`, `Port`, `CredentialRef`, `TransactionMode`, `IngestionMode`, `PullIntervalSeconds`, `HeartbeatIntervalSeconds`, `HeartbeatTimeoutSeconds`). All vendor-specific fields are silently dropped: Radix fields (`SharedSecret`, `UsnCode`, `AuthPort`, `FccPumpAddressMap`), Advatec fields (`AdvatecDevicePort`, `AdvatecWebhookListenerPort`, `AdvatecWebhookToken`, `AdvatecEfdSerialNumber`, `AdvatecCustIdType`), timeout fields (`PreAuthTimeoutSeconds`, `FiscalReceiptTimeoutSeconds`, `ApiRequestTimeoutSeconds`), and `WebhookListenerPort`. The same problem affects `SiteConfigSync` which drops `CertificatePins` — runtime certificate pins for TLS pin rotation. After the user clicks "Save & Apply", the locally-constructed config replaces the cloud config in SQLite and memory. On a Radix site, this wipes `SharedSecret`, causing all subsequent FCC requests to fail signature validation. On an Advatec site, this wipes the EFD serial number and webhook config, breaking fiscalization.
- **Evidence:**
  - `ConfigurationPage.axaml.cs:116-130` — new `SiteConfigFcc` only copies 12 of 25+ properties
  - `ConfigurationPage.axaml.cs:131-138` — new `SiteConfigSync` drops `CertificatePins`
  - `SiteConfig.cs:69-90` — Radix/Advatec fields exist on `SiteConfigFcc` but are not copied
  - `SiteConfig.cs:101-105` — `CertificatePins` exists on `SiteConfigSync` but is not copied
- **Impact:** Saving any configuration change from the UI permanently wipes vendor-specific FCC config and certificate pins. FCC communication breaks on next operation. Only recoverable by a cloud config push with a higher version number.
- **Recommended Fix:** Instead of constructing a new `SiteConfigFcc`, clone the existing `currentSite?.Fcc` and only override the fields that the UI controls map to. Apply the same pattern for `Sync`, `Buffer`, `LocalApi`, and `Telemetry` sections.
- **Resolution:** Replaced all `new SiteConfigFcc/Sync/Buffer/LocalApi/Telemetry { ... }` construction with a `CloneSection<T>()` helper that deep-clones existing sections via JSON round-trip. Each section is now cloned from the current cloud config, and only the UI-controlled fields are overridden. All vendor-specific fields (Radix, Advatec, timeouts, CertificatePins) are preserved through save cycles.

---

### F-DSK-019
- **Title:** ConfigurationPage API key regeneration is never persisted — key discarded on save
- **Module:** Configuration
- **Severity:** High
- **Category:** Broken workflows
- **Status:** FIXED
- **Description:** `OnRegenerateApiKeyClicked` generates a new API key via `Guid.NewGuid().ToString("N")` and displays it in `CfgApiKey.Text`, showing the message "New API key generated. Click Save & Apply to persist." However, `OnSaveClicked` builds a `SiteConfig` object and applies it through `ConfigManager.ApplyConfigAsync`, which only handles cloud-managed configuration sections. The API key is stored in `ICredentialStore` under `CredentialKeys.FccApiKey` — it is not part of `SiteConfig`. The save flow never reads `CfgApiKey.Text` back, never writes to the credential store, and never updates `AgentConfiguration.FccApiKey`. The user sees "Settings saved and applied" but the regenerated key is silently discarded. The old key remains in the credential store and continues to be used.
- **Evidence:**
  - `ConfigurationPage.axaml.cs:224-230` — `OnRegenerateApiKeyClicked` sets `CfgApiKey.Text` but doesn't persist
  - `ConfigurationPage.axaml.cs:96-220` — `OnSaveClicked` never reads `CfgApiKey.Text` or writes to `ICredentialStore`
  - `CredentialKeys.cs:25` — `FccApiKey = "fcc:api_key"` — stored in credential store, not SiteConfig
- **Impact:** Users who regenerate the API key believe it was saved, but the old key remains active. The regenerated key is lost when the page reloads. If the user communicated the new key to the POS system, authentication will fail because the agent still uses the old key.
- **Recommended Fix:** In `OnSaveClicked`, check if `CfgApiKey.Text` differs from the loaded value and, if so, persist the new key to `ICredentialStore` via `SetSecretAsync(CredentialKeys.FccApiKey, newKey)`.
- **Resolution:** Added `ICredentialStore` to the page class. `OnSaveClicked` now compares `CfgApiKey.Text` against the loaded value (`_loadedLanApiKey`) and persists changed keys to `ICredentialStore` via `SetSecretAsync(CredentialKeys.LanApiKey, ...)` after successful config apply. The UI now correctly manages the LAN API key (see F-DSK-020 fix).

---

### F-DSK-020
- **Title:** ConfigurationPage displays FCC API key labeled as "Local API" key — wrong key type shown
- **Module:** Configuration
- **Severity:** Medium
- **Category:** Incorrect UI state updates
- **Status:** FIXED
- **Description:** The "Local API" section of the Configuration page displays `config.FccApiKey` (line 67: `CfgApiKey.Text = config.FccApiKey`) — the FCC LAN authentication key. But the UI label says "API Key" under the "Local API" card, suggesting it is the LAN API key used by Odoo POS to authenticate against the agent's REST API. These are two distinct keys: `CredentialKeys.FccApiKey` (FCC authentication) vs `CredentialKeys.LanApiKey` (Local REST API authentication). The "Regenerate" button generates a new value intended to replace the displayed key, further confusing which key is being managed.
- **Evidence:**
  - `ConfigurationPage.axaml.cs:67` — `CfgApiKey.Text = config.FccApiKey` — shows FCC key
  - `ConfigurationPage.axaml:164-166` — UI section labeled "Local API"
  - `CredentialKeys.cs:25` — `FccApiKey = "fcc:api_key"` — FCC authentication
  - `CredentialKeys.cs:28` — `LanApiKey = "lan:api_key"` — Local API authentication (not shown)
- **Impact:** Users cannot distinguish between the FCC API key and the LAN API key. Sharing the displayed "API Key" with Odoo POS operators won't work because it's the wrong key. The actual LAN API key is never visible in the configuration UI.
- **Recommended Fix:** Either display the correct key (`LanApiKey` from the credential store) under the "Local API" section, or clearly label the existing field as "FCC API Key" and add a separate display for the LAN API key.
- **Resolution:** Changed the "Local API" section to load and display the correct `LanApiKey` from `ICredentialStore` via async `LoadLanApiKeyAsync()`. XAML label changed from "API Key" to "LAN API Key (Odoo POS)". The Regenerate button now generates a LAN API key, and Save persists it to `CredentialKeys.LanApiKey`. The FCC API key (`config.FccApiKey`) is no longer incorrectly shown in this section.

---

### F-DSK-021 ✅ FIXED
- **Title:** ConfigurationPage Save shows opaque "Apply result: Rejected" when vendor-specific validation fails
- **Module:** Configuration
- **Severity:** Medium
- **Category:** Incorrect error messages
- **Status:** **FIXED**
- **Fix Applied:**
  1. Added optional `ErrorMessage` field to `ConfigApplyResult` record (`ConfigApplyResult.cs`).
  2. `ConfigManager.ApplyConfigAsync` now passes the validation error string into `ConfigApplyResult` on rejection.
  3. `ConfigurationPage.axaml.cs` now handles `ConfigApplyOutcome.Rejected` explicitly with a dedicated case that displays the error message: `"Rejected: {result.ErrorMessage}"`.
- **Description:** When `ApplyConfigAsync` returns `ConfigApplyOutcome.Rejected` (because vendor-specific fields were dropped by F-DSK-018, or for any other validation failure), the UI shows `$"Apply result: {result.Outcome}"` which renders as "Apply result: Rejected" with no explanation. The `ConfigManager` logs the specific validation error (line 97: `_logger.LogWarning("Config version {Version} rejected: {ValidationError}"...)`), but the `ConfigApplyResult` record does not carry the error message back to the UI. The user has no indication of what went wrong or how to fix it.
- **Evidence:**
  - `ConfigurationPage.axaml.cs:200-201` — `default: SaveFeedback.Text = $"Apply result: {result.Outcome}"` — no detail
  - `ConfigManager.cs:96-100` — validation error logged but not returned in `ConfigApplyResult`
  - `ConfigApplyResult.cs:17-21` — no error message field in the result record
- **Impact:** Users see a cryptic rejection message with no actionable information. They cannot determine whether the rejection was due to missing fields, invalid values, or version conflicts.
- **Recommended Fix:** Add an optional `ErrorMessage` field to `ConfigApplyResult` and populate it from the validation error. Display it in the UI: `SaveFeedback.Text = $"Rejected: {result.ErrorMessage}"`.

---

### F-DSK-022 ✅ FIXED
- **Title:** ConnectivityManager probe interval never updates on hot-reload — uses IOptions instead of IOptionsMonitor
- **Module:** Configuration
- **Severity:** Medium
- **Category:** Inconsistent UI state updates
- **Status:** **FIXED**
- **Fix Applied:**
  1. Changed `ConnectivityManager` constructor to accept `IOptionsMonitor<AgentConfiguration>` instead of `IOptions<AgentConfiguration>`.
  2. Moved interval reading inside the `while` loop in `RunProbeLoopAsync` so it re-reads `_config.CurrentValue` on each iteration.
  3. Simplified the internet probe delegate to use the injected `IOptionsMonitor` directly instead of resolving it from the service provider.
  4. Updated all test files (ConnectivityManagerTests, OfflineScenarioStressTests) to use `IOptionsMonitor<T>`.
- **Description:** `ConnectivityManager` accepts `IOptions<AgentConfiguration>` in its constructor (line 78). `IOptions<T>` caches its value after first resolution and never reflects hot-reload changes. When `ConfigManager` applies a new cloud config with a different `ConnectivityProbeIntervalSeconds` (mapped from `Fcc.HeartbeatIntervalSeconds`), the `CadenceController` sees the update via `IOptionsMonitor<T>` but `ConnectivityManager.RunProbeLoopAsync` continues using the original interval from `_config.Value` (line 205-207). The probe interval is effectively frozen at its startup value for the entire process lifetime.
- **Evidence:**
  - `ConnectivityManager.cs:78` — `IOptions<AgentConfiguration> config` — cached, not monitored
  - `ConnectivityManager.cs:205-207` — `var config = _config.Value; var interval = TimeSpan.FromSeconds(config.ConnectivityProbeIntervalSeconds > 0 ? ...)` — reads frozen value
  - `CadenceController.cs:34` — `IOptionsMonitor<AgentConfiguration> _config` — correctly uses monitor
  - `ConfigManager.cs:243` — `target.ConnectivityProbeIntervalSeconds = source.Fcc.HeartbeatIntervalSeconds` — hot-reloads the value, but ConnectivityManager never sees it
- **Impact:** Cloud-pushed changes to the connectivity probe interval have no effect until the agent is restarted. The UI "Connectivity Probes" section allows editing the value and "Save & Apply" reports success, but the actual probe loop continues at the original interval.
- **Recommended Fix:** Change `ConnectivityManager` to accept `IOptionsMonitor<AgentConfiguration>` and re-read the interval on each probe loop iteration, matching the `CadenceController` pattern.

---

## FCC Device Integration Module

### F-DSK-023 ✅ FIXED
- **Title:** IngestionOrchestrator uses IOptions (cached) — hot-reloaded FCC poll interval and batch size ignored at runtime
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Inconsistent UI state updates
- **Status:** **FIXED**
- **Fix Applied:**
  1. Changed `IngestionOrchestrator` constructor to accept `IOptionsMonitor<AgentConfiguration>` instead of `IOptions<AgentConfiguration>`.
  2. Changed all `_config.Value` reads to `_config.CurrentValue` so `DoPollAndBufferAsync` and `EnsurePushListenersInitializedAsync` always use the latest config.
  3. Updated all test files (IngestionOrchestratorTests, ManualPullTests, OfflineScenarioStressTests) to use `IOptionsMonitor<T>`.
- **Description:** `IngestionOrchestrator` accepts `IOptions<AgentConfiguration>` (line 34), which caches its value after first resolution. `DoPollAndBufferAsync` reads `_config.Value` (line 147) but the FCC poll interval, upload batch size, and ingestion mode are all frozen at startup values. While the `CadenceController` correctly uses `IOptionsMonitor<AgentConfiguration>` and re-reads `CurrentValue` on each tick, the orchestrator's own `_config.Value` never reflects cloud-pushed or UI-saved config changes. This means if the ingestion mode changes from `Relay` to `CloudDirect` via cloud config, the orchestrator still uses the old mode until restart.
- **Evidence:**
  - `IngestionOrchestrator.cs:34` — `private readonly IOptions<AgentConfiguration> _config` — cached, not monitored
  - `IngestionOrchestrator.cs:147` — `var config = _config.Value` — reads frozen value
  - `CadenceController.cs:34` — `IOptionsMonitor<AgentConfiguration> _config` — correctly uses monitor
- **Impact:** Runtime config changes to ingestion mode, FCC poll interval, and batch size are ignored. Operator must restart the agent after any cloud config push affecting FCC polling behavior.
- **Recommended Fix:** Change `IngestionOrchestrator` to accept `IOptionsMonitor<AgentConfiguration>` and read `CurrentValue` in `DoPollAndBufferAsync`.

---

### F-DSK-024 ✅ FIXED
- **Title:** Petronite _activePreAuths never purged for stale entries — phantom correlations after adapter restart
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Incorrect data persistence
- **Status:** **FIXED**
- **Fix Applied:** Added `PurgeStalePreAuths()` private method that removes entries older than `StaleOrderThreshold` (30 min) from `_activePreAuths`, called at the start of `FetchTransactionsAsync` after `EnsureInitializedAsync`, matching the AdvatecAdapter/RadixAdapter pattern.
- **Description:** `PetroniteAdapter._activePreAuths` is an in-memory `ConcurrentDictionary<string, ActivePreAuth>` that tracks active pre-authorizations for webhook→transaction correlation. Unlike `AdvatecAdapter` (which calls `PurgeStalePreAuths()` during every `NormalizeAsync` call) and `RadixAdapter` (which calls `PurgeStalePreAuths()` during every `FetchTransactionsAsync`), the `PetroniteAdapter` has no periodic purge mechanism. If a pre-auth is authorized but the customer never dispenses (walks away), the `ActivePreAuth` entry remains in memory forever. Over time, these phantom entries accumulate and may incorrectly correlate with unrelated future transactions that happen to share the same Petronite `OrderId`.
- **Evidence:**
  - `PetroniteAdapter.cs:47` — `private readonly ConcurrentDictionary<string, ActivePreAuth> _activePreAuths = new()` — no purge mechanism
  - `PetroniteAdapter.cs:155` — `_activePreAuths.TryRemove(tx.OrderId, out var preAuth)` — only removed on webhook match
  - `AdvatecAdapter.cs:118-122` — `PurgeStalePreAuths()` called during NormalizeAsync (correct pattern)
  - `RadixAdapter.cs:210` — `PurgeStalePreAuths()` called during FetchTransactionsAsync (correct pattern)
- **Impact:** Memory leak proportional to abandoned pre-auths. On high-traffic sites with frequent pre-auth abandonment, entries accumulate indefinitely. Stale entries could theoretically correlate with wrong transactions if Petronite recycles OrderIds.
- **Recommended Fix:** Add a `PurgeStalePreAuths()` method mirroring the Advatec/Radix pattern (30-minute TTL), called at the start of `FetchTransactionsAsync` or `NormalizeAsync`.

---

### F-DSK-025 ✅ FIXED
- **Title:** FccAdapterFactory sync-over-async DisposeAsync blocks cadence thread during adapter config change
- **Module:** FCC Device Integration
- **Severity:** High
- **Category:** Broken workflows
- **Status:** **FIXED**
- **Fix Applied:** Replaced sync-over-async `DisposeAsync().AsTask().GetAwaiter().GetResult()` inside `lock` with a swap-inside-lock pattern. Old adapter reference is swapped inside the lock, then disposed asynchronously outside via `DisposeAdapterInBackgroundAsync`. Applied to both Petronite and Advatec adapter creation paths.
- **Description:** `FccAdapterFactory.GetOrCreatePetroniteAdapter` and `GetOrCreateAdvatecAdapter` call `_cachedAdapter?.DisposeAsync().AsTask().GetAwaiter().GetResult()` inside a `lock` block when the config fingerprint changes. `DisposeAsync()` for both adapters stops the webhook listener (which may be waiting on pending HTTP requests) and disposes semaphores. The sync-over-async call blocks the calling thread (the CadenceController background thread) while holding the lock. If the webhook listener's `StopAsync()` takes time (e.g., waiting for an in-flight HTTP request to complete), the cadence loop is frozen — no FCC polling, no cloud upload, no pre-auth expiry checks — until the dispose completes.
- **Evidence:**
  - `FccAdapterFactory.cs:81` — `_cachedPetroniteAdapter?.DisposeAsync().AsTask().GetAwaiter().GetResult()` — sync-over-async inside `lock`
  - `FccAdapterFactory.cs:126` — `_cachedAdvatecAdapter?.DisposeAsync().AsTask().GetAwaiter().GetResult()` — same pattern
  - `PetroniteAdapter.cs:764` — `await _webhookListener.StopAsync()` — awaits listener shutdown
- **Impact:** Cadence loop frozen for the duration of webhook listener shutdown on config change. On slow network, this could be several seconds. All recurring work (upload, telemetry, pre-auth expiry) is suspended.
- **Recommended Fix:** Replace `lock` with `SemaphoreSlim` and use `await DisposeAsync()` properly. Alternatively, dispose the old adapter outside the lock (swap the reference inside the lock, dispose old reference after releasing).

---

### F-DSK-026 ✅ FIXED
- **Title:** HandleAllAsync returns up to 500 transactions without filtering — bypasses SyncStatus-based consumption gating
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Incorrect data persistence
- **Status:** **FIXED**
- **Fix Applied:** Added `.Where(t => t.SyncStatus != SyncStatus.SyncedToOdoo && t.SyncStatus != SyncStatus.Archived)` filter to `HandleAllAsync`, matching the `HandleLatestAsync` and REST API patterns.
- **Description:** `OdooWsMessageHandler.HandleAllAsync()` queries `db.Transactions.OrderByDescending(t => t.CompletedAt).Take(500).ToListAsync()` without any `SyncStatus` filter. The REST API endpoint `GET /api/v1/transactions` filters to only `Pending` and `Uploaded` records via `TransactionBufferManager.GetForLocalApiAsync()` — explicitly excluding `SyncedToOdoo`, `DuplicateConfirmed`, and `Archived` records to prevent Odoo double-consumption. The WebSocket `all` mode bypasses this filter, returning all records including those already synced to Odoo. If the POS processes transactions from both the REST API and WebSocket concurrently, it will see and potentially re-process transactions already marked as consumed.
- **Evidence:**
  - `OdooWsMessageHandler.cs:79-82` — `db.Transactions.OrderByDescending(t => t.CompletedAt).Take(500).ToListAsync()` — no SyncStatus filter
  - `TransactionBufferManager.cs:171` — REST API: `.Where(t => t.SyncStatus == SyncStatus.Pending || t.SyncStatus == SyncStatus.Uploaded)` — correct filter
  - `OdooWsMessageHandler.cs:53` — `HandleLatestAsync` also excludes `SyncedToOdoo` and `Archived` — inconsistent with `HandleAllAsync`
- **Impact:** Odoo POS connected via WebSocket may double-consume transactions already synced through the cloud path, leading to duplicate order creation.
- **Recommended Fix:** Apply the same `SyncStatus` filter as the REST API: `.Where(t => t.SyncStatus == SyncStatus.Pending || t.SyncStatus == SyncStatus.Uploaded)`.

---

### F-DSK-027 ✅ FIXED
- **Title:** PreAuthHandler.HandleAsync passes customer PII fields but FccCommand omits CustomerName and CustomerTaxId for non-Advatec adapters
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Broken workflows
- **Status:** **FIXED**
- **Fix Applied:** Added `CustomerTaxId: request.CustomerTaxId` and `CustomerName: request.CustomerName` to the `PreAuthCommand` construction in `PreAuthHandler.HandleAsync`.
- **Description:** `PreAuthHandler.HandleAsync` constructs a `PreAuthCommand` (line 154-164) but does not pass `CustomerName`, `CustomerTaxId`, or `CustomerBusinessName` from the request. While `PreAuthCommand` has these properties, the handler sets `CustomerName = null` and `CustomerTaxId = null` by omitting them. The `AdvatecAdapter.SendPreAuthAsync` reads `command.CustomerTaxId` and `command.CustomerName` (lines 234-235), but since the handler never sets them, they are always null. The customer data is stored in the pre-auth record (lines 137-139) but never forwarded to the FCC. For Advatec sites where customer data submission triggers pump authorization (Scenario C), this means the fiscal device receives empty customer data, potentially violating TRA compliance requirements.
- **Evidence:**
  - `PreAuthHandler.cs:154-164` — `PreAuthCommand` construction omits `CustomerName`, `CustomerTaxId`, `CustomerBusinessName`
  - `AdvatecAdapter.cs:234` — `CustomerId = command.CustomerTaxId ?? ""` — always empty string
  - `AdvatecAdapter.cs:235` — `CustomerName = command.CustomerName ?? ""` — always empty string
  - `PreAuthHandler.cs:137-139` — customer data IS saved to the pre-auth record but not forwarded to the adapter
- **Impact:** Advatec fiscal devices receive empty customer data, which may cause fiscal receipt non-compliance in Tanzania (TRA regulations require customer identification on receipts).
- **Recommended Fix:** Pass `CustomerName`, `CustomerTaxId`, and `CustomerBusinessName` from the request to the `PreAuthCommand` constructor. Add `CustomerIdType` from the request as well.

---

### F-DSK-028 ✅ FIXED
- **Title:** Radix adapter FetchTransactionsPullAsync has no safety limit — relies solely on FCC reporting RESP_CODE=205 to stop
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Broken workflows
- **Status:** **FIXED**
- **Fix Applied:** Added `maxCycles = limit * 2` safety counter to the FIFO drain loop. The loop breaks with a warning log when the counter is exceeded, preventing infinite looping on a misbehaving FCC.
- **Description:** The Radix FIFO drain loop in `FetchTransactionsPullAsync` iterates until the FCC returns `RESP_CODE=205` (FIFO empty). The outer `FetchTransactionsAsync` applies `Math.Clamp(cursor.MaxCount, 1, MaxFetchLimit)` but this limit only applies to the number of records collected — the inner drain loop itself has no cycle counter or timeout. If the FCC has a bug that never returns 205 (or always returns a new transaction followed by more), the loop runs indefinitely, blocking the cadence controller for the entire session. The `CancellationToken` from the cadence controller provides a timeout, but this is the full cadence tick interval, not a per-fetch safety limit.
- **Evidence:**
  - `RadixAdapter.cs:208-249` — `FetchTransactionsAsync` delegates to pull or push path
  - `RadixAdapter.cs` — FIFO drain loop breaks only on `RESP_CODE == 205` or `records.Count >= limit`
  - No cycle counter or per-fetch timeout independent of the cadence tick
- **Impact:** A misbehaving FCC can cause the cadence tick to hang indefinitely on the FCC poll step, preventing cloud upload, telemetry, and pre-auth expiry checks.
- **Recommended Fix:** Add a `maxCycles` safety counter (e.g., `limit * 2`) that breaks the drain loop after a reasonable number of round-trips, logging a warning about excessive FIFO depth.

---

## Site Master Data Module

### F-DSK-029 ✅ FIXED
- **Title:** NozzleMapping database table is never populated — PreAuthHandler always returns NOZZLE_MAPPING_NOT_FOUND
- **Module:** Site Master Data
- **Severity:** Critical
- **Category:** Broken workflows
- **Status:** **FIXED**
- **Fix Applied:** Added `SyncNozzleMappingsToDbAsync` method to `SiteDataManager` that upserts `SiteConfig.Mappings.Nozzles` into the `nozzles` table. Called from `ConfigManager.ApplyConfigAsync` after every successful config application.
- **Description:** `PreAuthHandler.HandleAsync` (line 71) queries `_db.NozzleMappings` to translate Odoo pump/nozzle numbers to FCC pump/nozzle numbers. However, no production code ever inserts records into the `nozzles` SQLite table. `SiteDataManager.SyncFromConfig` writes nozzle data to `site-data.json` (a flat file), but never to the EF Core `NozzleMappings` DbSet. `ConfigManager.ApplyConfigAsync` stores the raw config JSON in `agent_config` but does not extract and persist nozzle mappings to the `nozzles` table. The only code that inserts into `NozzleMappings` is the test fixture `PreAuthHandlerTests.cs:142`. Every pre-auth request from Odoo POS will fail with `NozzleMappingNotFound`.
- **Evidence:**
  - `PreAuthHandler.cs:71-77` — queries `_db.NozzleMappings` for Odoo-to-FCC translation
  - `SiteDataManager.cs:80-88` — writes nozzle data to `site-data.json` file, NOT to the database
  - `ConfigManager.cs:351-398` — `StoreConfigAsync` stores raw JSON in `agent_config`, no nozzle extraction
  - `PreAuthHandlerTests.cs:142` — only place `_db.NozzleMappings.Add(...)` appears (test code only)
  - No production code path populates the `nozzles` table
- **Impact:** Pre-authorization is completely broken. Every pump authorization request from Odoo POS returns HTTP 404 NOZZLE_MAPPING_NOT_FOUND. Fuel cannot be dispensed via the pre-auth workflow on any site.
- **Recommended Fix:** Add a `SyncNozzleMappingsToDb` method that upserts `SiteConfig.Mappings.Nozzles` into the `nozzles` table. Call it from `ConfigManager.ApplyConfigAsync` after successful config application, and from `RegistrationManager.SyncSiteData` during initial registration.

---

### F-DSK-030 ✅ FIXED
- **Title:** SiteDataManager.SyncFromConfig only called during registration — site-data.json goes stale after config updates
- **Module:** Site Master Data
- **Severity:** High
- **Category:** Incorrect data persistence
- **Status:** **FIXED**
- **Fix Applied:** Added `SiteDataManager.SyncFromConfig(newConfig)` call in `ConfigManager.ApplyConfigAsync` after `StoreConfigAsync`, so site data is updated on every config change including cloud-pushed updates.
- **Description:** `SiteDataManager.SyncFromConfig` is called from `RegistrationManager.SyncSiteData`, which is invoked during device registration (ProvisioningWindow and DeviceRegistrationService). After registration, `ConfigPollWorker` polls for config updates and applies them via `ConfigManager.ApplyConfigAsync`, but this code path never calls `SiteDataManager.SyncFromConfig`. If the cloud pushes a config update that changes nozzle mappings, product mappings, or site metadata, the `site-data.json` file retains the original registration-time data indefinitely.
- **Evidence:**
  - `RegistrationManager.cs:156-166` — `SyncSiteData` calls `_siteDataManager.SyncFromConfig(config)`
  - `ProvisioningWindow.axaml.cs:251,415` — calls `SyncSiteData` during registration
  - `DeviceRegistrationService.cs:148` — calls `SyncSiteData` during code-based registration
  - `ConfigManager.cs:68-155` — `ApplyConfigAsync` stores config, signals Options, raises event — never calls SiteDataManager
  - `ConfigPollWorker.cs:121-128` — delegates to `ApplyConfigAsync` only
- **Impact:** After a cloud config update that changes equipment mappings, `site-data.json` contains stale data. Any consumer reading from `SiteDataManager.LoadSiteData()` gets outdated equipment metadata.
- **Recommended Fix:** Subscribe to `IConfigManager.ConfigChanged` and call `SiteDataManager.SyncFromConfig` with the updated config whenever the `mappings` section changes.

---

### F-DSK-031 ✅ FIXED
- **Title:** ConfigurationPage local save fabricates a bumped ConfigVersion that blocks subsequent cloud config polls
- **Module:** Site Master Data
- **Severity:** High
- **Category:** Incorrect data persistence
- **Status:** **FIXED**
- **Fix Applied:** Changed `ConfigSaveService` to use `ConfigVersion = currentSite?.ConfigVersion ?? 0` (no bump). Changed `ConfigManager.ApplyConfigAsync` version check from `<=` to `<` so same-version local re-applies are accepted while older cloud configs are still rejected.
- **Description:** `ConfigurationPage.OnSaveClicked` (line 110) constructs a new `SiteConfig` with `ConfigVersion = (currentSite?.ConfigVersion ?? 0) + 1` and passes it to `ConfigManager.ApplyConfigAsync`. This locally fabricated version is stored in the database. On the next cloud config poll, `ConfigManager.ApplyConfigAsync` (line 76) rejects the cloud config if `newConfig.ConfigVersion <= previous.ConfigVersion`. Since the local version was bumped, the cloud config is rejected as stale. This permanently blocks cloud config updates until the cloud version catches up.
- **Evidence:**
  - `ConfigurationPage.axaml.cs:110` — `ConfigVersion = (currentSite?.ConfigVersion ?? 0) + 1`
  - `ConfigManager.cs:76-82` — rejects config when `newConfig.ConfigVersion <= previous.ConfigVersion`
  - `ConfigPollWorker.cs:121-128` — next poll applies via same `ApplyConfigAsync`, gets StaleVersion outcome
- **Impact:** A single Save and Apply on the Configuration page permanently blocks cloud config updates. The only recovery is clearing the database or waiting for the cloud version to naturally exceed the bumped version.
- **Recommended Fix:** Do not fabricate a new ConfigVersion for local saves. Use a separate local-override mechanism for editable fields, or mark locally-applied config so the version comparison only applies to cloud-sourced configs.

---

### F-DSK-032 ✅ FIXED
- **Title:** ConfigurationPage.OnSaveClicked drops CertificatePins from Sync section — TLS pinning breaks after local save
- **Module:** Site Master Data
- **Severity:** High
- **Category:** Incorrect data persistence
- **Status:** **FIXED**
- **Fix Applied:** Already fixed by F-DSK-018's `CloneSection<T>()` refactoring. `ConfigSaveService` deep-clones existing `SiteConfigSync` via JSON round-trip, preserving `CertificatePins` and all other fields not controlled by the UI.
- **Description:** `ConfigurationPage.OnSaveClicked` reconstructs the `SiteConfigSync` object (lines 131-138) with only `CloudBaseUrl`, `UploadBatchSize`, `UploadIntervalSeconds`, `ConfigPollIntervalSeconds`, and `CursorStrategy`. The `CertificatePins` field is not copied from `currentSite?.Sync?.CertificatePins`. After saving, the stored config has `CertificatePins = null`, which disables TLS certificate pinning for all cloud communication.
- **Evidence:**
  - `ConfigurationPage.axaml.cs:131-138` — `Sync = new SiteConfigSync { ... }` — `CertificatePins` not copied
  - `SiteConfig.cs:104` — `public List<string>? CertificatePins { get; set; }` — the missing field
  - `ServiceCollectionExtensions.cs:83` — `ServerCertificateCustomValidationCallback = CertificatePinValidator.Validate`
- **Impact:** After a local config save, TLS certificate pinning is silently disabled, making the agent vulnerable to MITM attacks via rogue certificates.
- **Recommended Fix:** Copy `CertificatePins` from the current config. Better yet, refactor to only save the fields the user actually edited rather than reconstructing the entire SiteConfig.

---

### F-DSK-033 ✅ FIXED
- **Title:** DashboardPage.PopulateDeviceInfo uses IOptions snapshot — device identity never refreshes after post-construction events
- **Module:** Site Master Data
- **Severity:** Medium
- **Category:** Inconsistent UI state updates
- **Status:** **FIXED**
- **Fix Applied:** Changed `PopulateDeviceInfo` to use `IOptionsMonitor<AgentConfiguration>` instead of `IOptions`. Added device identity refresh (DeviceId, SiteCode) in `RefreshAllAsync` periodic timer so values update after registration.
- **Description:** `DashboardPage.PopulateDeviceInfo` (line 190) reads `IOptions<AgentConfiguration>` which returns a snapshot taken at resolution time. It is called once in the constructor. If the device completes registration after the dashboard is already displayed, the Device ID and Site Code remain "N/A" forever. The 5-second refresh timer updates buffer stats but never re-reads device identity.
- **Evidence:**
  - `DashboardPage.axaml.cs:190` — `_services?.GetService<IOptions<AgentConfiguration>>()?.Value` — snapshot, not monitor
  - `DashboardPage.axaml.cs:191-192` — `DeviceIdText.Text = config?.DeviceId ?? "N/A"` — set once in constructor
  - `DashboardPage.axaml.cs:95-184` — `RefreshAllAsync` updates buffer/sync stats but NOT device identity
- **Impact:** Users see "N/A" for Device ID and Site Code on the dashboard even after successful registration until they navigate away and return.
- **Recommended Fix:** Use `IOptionsMonitor<AgentConfiguration>` and re-read device identity in `RefreshAllAsync`, or subscribe to `IConfigManager.ConfigChanged`.

---

### F-DSK-034 ✅ FIXED
- **Title:** DesktopFccRuntimeConfiguration.Resolve builds productCodeMapping that maps each code to itself — no FCC-to-canonical translation
- **Module:** Site Master Data
- **Severity:** Medium
- **Category:** Broken workflows
- **Status:** **FIXED**
- **Fix Applied:** Already fixed in current code. `DesktopFccRuntimeConfiguration.Resolve` correctly builds `productCodeMapping` from `SiteConfig.Mappings.Products` using `FccProductCode → CanonicalProductCode`.
- **Description:** `DesktopFccRuntimeConfiguration.Resolve` (lines 111-114) builds `productCodeMapping` from `SiteConfig.Mappings.Nozzles` by grouping on `ProductCode` and mapping `group.Key` to `group.Key`. This creates a dictionary where every key equals its value. The actual FCC-to-canonical product code translation is defined in `SiteConfig.Mappings.Products` (with `FccProductCode` to `CanonicalProductCode`), but this collection is never used in the resolver.
- **Evidence:**
  - `DesktopFccRuntimeConfiguration.cs:111-114` — `.ToDictionary(group => group.Key, group => group.Key, ...)` — key equals value
  - `SiteConfig.cs:147` — `Products` list has `FccProductCode` to `CanonicalProductCode` — the actual mapping source
  - `SiteConfig.cs:148` — `Nozzles` list has only a single `ProductCode` field — not a translation pair
- **Impact:** Adapters that rely on `ProductCodeMapping` for FCC-to-Odoo product code translation get no translation. Transactions with FCC-native codes fail to match products in Odoo.
- **Recommended Fix:** Build `productCodeMapping` from `SiteConfig.Mappings.Products` using `FccProductCode` as key and `CanonicalProductCode` as value.

---

## Transaction Management Module

### F-DSK-035 ✅ FIXED
- **Title:** TransactionsPage silently swallows database load errors — user sees stale data with no feedback
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Incorrect error messages
- **Status:** **FIXED**
- **Fix Applied:** Changed empty `catch` to `catch (Exception ex)` with `ILogger<TransactionsPage>` logging at Error level. Sets `PageInfoText.Text = "Error loading transactions"` as visible indicator.
- **Description:** `TransactionsPage.LoadPageAsync` (line 98-101) has an empty catch block `catch { // Non-fatal }` with no logging or user feedback. If the database query fails (e.g., locked database, schema mismatch after upgrade, disk full), the DataGrid retains stale data from the previous successful load and no error indicator is shown. The user has no way to know the displayed data is outdated. Additionally, multiple call sites use fire-and-forget `_ = LoadPageAsync()` (lines 34, 135, 164, 174, 182), so exceptions are truly invisible.
- **Evidence:**
  - `TransactionsPage.axaml.cs:98-101` — `catch { // Non-fatal }` — swallows all exceptions
  - `TransactionsPage.axaml.cs:34` — `_ = LoadPageAsync()` — fire-and-forget in timer callback
  - `TransactionsPage.axaml.cs:135,164` — additional fire-and-forget call sites
- **Impact:** Users making operational decisions based on stale transaction data during database issues. No indication that the data may be outdated.
- **Recommended Fix:** Log the exception at Warning level. Set a visible error banner or status indicator on the page when load fails. Consider a retry counter that shows a persistent error after N consecutive failures.

### F-DSK-036 ✅ FIXED
- **Title:** TransactionsPage date filter uses UTC zero offset — misaligns with local timezone transactions
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Incorrect form validations
- **Status:** **FIXED**
- **Fix Applied:** Replaced `TimeSpan.Zero` with `DateTimeOffset.Now.Offset` when constructing filter `DateTimeOffset` values in `ApplyFilters()`.
- **Description:** `TransactionsPage.ApplyFilters()` (lines 155-161) creates `DateTimeOffset` with `TimeSpan.Zero` (UTC) from the `DatePicker` values. However, `CompletedAt` in the database is stored as ISO 8601 strings via the EF Core `ValueConverter<DateTimeOffset, string>` which preserves the original offset. If transactions were created with a non-zero offset, the UTC filter comparison may include/exclude incorrect transactions at day boundaries. For example, a user in UTC+3 selecting "March 13" gets midnight UTC, missing transactions completed between 21:00-23:59 UTC on March 12 which are March 13 local time.
- **Evidence:**
  - `TransactionsPage.axaml.cs:156` — `new DateTimeOffset(DateFromPicker.SelectedDate.Value.DateTime, TimeSpan.Zero)`
  - `TransactionsPage.axaml.cs:159-160` — `new DateTimeOffset(...AddDays(1).AddTicks(-1), TimeSpan.Zero)`
  - `BufferEntityConfiguration.cs:44-47` — `CompletedAt` stored via `Converters.Required` as ISO 8601 string
- **Impact:** Users in non-UTC timezones may see incorrect transaction results when filtering by date. Transactions near day boundaries will be misclassified.
- **Recommended Fix:** Use `DateTimeOffset.Now.Offset` or the site's configured timezone offset when constructing the filter DateTimeOffset values, instead of hardcoding `TimeSpan.Zero`.

### F-DSK-037 ✅ FIXED
- **Title:** WebSocket manager_update bypasses AcknowledgeAsync's OdooOrderId conflict detection
- **Module:** Transaction Management
- **Severity:** High
- **Category:** Inconsistent UI state updates
- **Status:** **FIXED**
- **Fix Applied:** Added OdooOrderId conflict detection in `TransactionUpdateService.ApplyManagerUpdateAsync` and `ApplyAttendantUpdateAsync`. If `tx.OdooOrderId` is already set to a different value, the update is skipped with a warning log.
- **Description:** `OdooWsMessageHandler.HandleManagerUpdateAsync` (line 106) unconditionally sets `tx.OdooOrderId = oi.ToString()` on any transaction when a WebSocket `manager_update` message is received. In contrast, `TransactionBufferManager.AcknowledgeAsync` (lines 277-284) has explicit conflict detection — if an `OdooOrderId` is already stamped and a different one is provided, it returns `Conflict`. The WebSocket path bypasses this protection entirely, allowing any client to overwrite an already-acknowledged OdooOrderId, which corrupts the transaction-to-order linkage.
- **Evidence:**
  - `OdooWsMessageHandler.cs:106` — `tx.OdooOrderId = oi.ToString()` — unconditional overwrite
  - `TransactionBufferManager.cs:277-284` — conflict detection in `AcknowledgeAsync`
  - `OdooWsMessageHandler.cs:157-158` — `HandleAttendantUpdateAsync` also does unconditional OdooOrderId write
- **Impact:** Transaction-to-Odoo-order linkage can be corrupted silently. A transaction already linked to one order can be re-linked to a different order via WebSocket, breaking reconciliation.
- **Recommended Fix:** Route WebSocket OdooOrderId updates through `TransactionBufferManager.AcknowledgeAsync` or replicate its conflict detection logic. Return a WebSocket error response when a conflict is detected.

### F-DSK-038 ✅ FIXED
- **Title:** WebSocket fp_unblock is a facade — sends success response without calling FCC adapter
- **Module:** Transaction Management
- **Severity:** High
- **Category:** Broken workflows
- **Status:** **FIXED**
- **Fix Applied:** Changed `HandleFpUnblockAsync` to log a warning and return `state = "not_supported"` with an honest message that FCC adapter pump control is not yet wired. No longer sends a false success response.
- **Description:** `OdooWsMessageHandler.HandleFpUnblockAsync` (lines 195-206) responds with `{ state = "unblocked", message = "Pump unblock processed" }` but never calls the FCC adapter to actually unblock the pump. The Odoo POS operator receives a success acknowledgment and believes the pump is unblocked, but the physical pump remains blocked on the FCC side. No `IFccAdapter` reference is available in the message handler, and no adapter method is invoked.
- **Evidence:**
  - `OdooWsMessageHandler.cs:195-206` — sends hardcoded success response with no FCC call
  - `OdooWsMessageHandler.cs:25-35` — constructor has no `IFccAdapter` dependency
  - `IFccAdapter.cs` — no unblock method referenced from message handler
- **Impact:** Operators believe pumps are unblocked when they are not. Requires manual intervention at the pump to resolve, causing delays and customer frustration at the fuel station.
- **Recommended Fix:** Inject `IFccAdapter` (or a pump control service) into the message handler. Call the adapter's pump unblock/authorize method before sending the success response. Return an error response if the FCC call fails.

### F-DSK-039 ✅ FIXED
- **Title:** WebSocket attendant_pump_count_update acknowledges without persisting or enforcing limits
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Broken workflows
- **Status:** **FIXED**
- **Fix Applied:** Added Information-level logging of each received pump count limit (pump, attendant, maxTx). Response status changed from `"updated"` to `"acknowledged"` to honestly reflect that limits are received but not yet enforced. Full persistence requires a new entity/table (tracked as future work).
- **Description:** `OdooWsMessageHandler.HandleAttendantPumpCountUpdateAsync` (lines 210-235) receives attendant-per-pump transaction count limits from Odoo and sends ACK responses for each item, but never persists the limits to the database or passes them to any enforcement component. The `AttendantPumpCountUpdateItem` data (PumpNumber, EmpTagNo, NewMaxTransaction) is deserialized, acknowledged, and discarded. No enforcement mechanism checks if an attendant has exceeded their transaction limit before dispensing.
- **Evidence:**
  - `OdooWsMessageHandler.cs:217-219` — items deserialized from JSON
  - `OdooWsMessageHandler.cs:221-235` — loop sends ACK per item, no persistence or enforcement
  - No `NozzleMapping` or `BufferedTransaction` write for the limits
- **Impact:** Attendant transaction limits set by managers in Odoo POS are silently ignored. Attendants can process unlimited transactions regardless of configured limits.
- **Recommended Fix:** Create an `AttendantLimitRecord` entity and persist the limits. Enforce limits in the pre-auth flow by checking the attendant's transaction count against the persisted limit before authorizing a pump.

### F-DSK-040 ✅ FIXED
- **Title:** StatusPollWorker doesn't handle RefreshTokenExpiredException/DeviceDecommissionedException during token refresh
- **Module:** Transaction Management
- **Severity:** High
- **Category:** Incorrect error messages
- **Status:** **FIXED**
- **Fix Applied:** Already fixed via `AuthenticatedCloudRequestHandler` (T-DSK-010). StatusPollWorker now uses `_authHandler.ExecuteAsync` which handles `RefreshTokenExpiredException` and `DeviceDecommissionedException` uniformly.
- **Description:** `StatusPollWorker.PollAsync` (lines 89-98) calls `_tokenProvider.RefreshTokenAsync(ct)` after a 401 response, but does not catch `RefreshTokenExpiredException` or `DeviceDecommissionedException`. In contrast, `CloudUploadWorker` (lines 146-168) and `TelemetryReporter` (lines 114-133) properly handle both exceptions by marking the device as decommissioned or requiring re-provisioning. When StatusPollWorker encounters these permanent auth failures, the exception propagates to CadenceController which catches it as a generic warning and continues. The device is never marked as decommissioned, causing repeated failed auth attempts on every cadence tick.
- **Evidence:**
  - `StatusPollWorker.cs:93` — `token = await _tokenProvider.RefreshTokenAsync(ct)` — no specialized exception handling
  - `CloudUploadWorker.cs:146-168` — handles `RefreshTokenExpiredException` and `DeviceDecommissionedException`
  - `TelemetryReporter.cs:114-133` — handles both exceptions properly
  - `CadenceController.cs:220-225` — catches as generic warning, no decommission marking
- **Impact:** A decommissioned device continues generating failed status poll requests indefinitely instead of properly transitioning to decommissioned state. Generates noise in cloud logs and wastes bandwidth.
- **Recommended Fix:** Add `try/catch` around `RefreshTokenAsync` in `StatusPollWorker.PollAsync` matching the pattern in `CloudUploadWorker`: catch `RefreshTokenExpiredException` → `MarkReprovisioningRequiredAsync()`, catch `DeviceDecommissionedException` → `MarkDecommissionedAsync()`.

### F-DSK-041 ✅ FIXED
- **Title:** "Archived" SyncStatus has no transition path but is exposed in UI filter and BufferStats
- **Module:** Transaction Management
- **Severity:** Low
- **Category:** Inconsistent UI state updates
- **Status:** **FIXED**
- **Fix Applied:** Removed the `<ComboBoxItem Content="Archived" />` from `TransactionsPage.axaml` and updated the filter index mapping. Added Archived records to `CleanupWorker` cleanup sweep so any manually-archived records are properly cleaned up.
- **Description:** The `SyncStatus.Archived` enum value exists (Enums.cs:21) and is counted in `BufferStats.Archived` (TransactionBufferManager.cs:207), exposed as a filter option in TransactionsPage.axaml (line 26), but no code path in `TransactionBufferManager` or any other service transitions a transaction to `Archived` status. The UI filter always returns empty results. `CleanupWorker` also skips Archived records — it only deletes `SyncedToOdoo`, `DuplicateConfirmed`, and `DeadLetter` past retention (CleanupWorker.cs:74-86), meaning if transactions were ever manually set to Archived, they would never be cleaned up.
- **Evidence:**
  - `Enums.cs:21` — `Archived` enum value defined
  - `TransactionsPage.axaml:26` — `<ComboBoxItem Content="Archived" />` — exposed in UI
  - `TransactionBufferManager.cs` — no method transitions to `SyncStatus.Archived`
  - `CleanupWorker.cs:74-86` — no cleanup for Archived records
- **Impact:** Dead UI filter option confuses operators. If Archived status were ever used (e.g., manual DB edit), records would accumulate indefinitely.
- **Recommended Fix:** Either implement the Archived transition (e.g., manual archive from UI or auto-archive after SyncedToOdoo + retention), or remove the Archived filter from TransactionsPage and the enum value if unused.

---

### F-DSK-042 ✅ FIXED
- **Title:** PreAuthHandler.CancelAsync reports success and marks records Cancelled even when FCC deauthorization fails
- **Module:** Pre-Authorization
- **Severity:** High
- **Category:** Broken workflows
- **Status:** **FIXED**
- **Fix Applied:** For Authorized records, the boolean result from `TryCancelAtFccAsync` is now checked. If deauthorization is not confirmed, the method returns `Fail(FccUnreachable)` without transitioning to Cancelled. For non-authorized records (e.g., Pending), the call remains best-effort.
- **Description:** `PreAuthHandler.CancelAsync` treats FCC deauthorization as "best effort" but ignores the boolean result from `TryCancelAtFccAsync`. If FCC connectivity is down or the vendor adapter fails to cancel the authorization, the handler still updates the local row to `Cancelled` and returns success to the caller. The expiry path in the same class explicitly does the opposite: it leaves `Authorized` records unchanged when FCC deauthorization cannot be confirmed. This means the cancel API can tell Odoo and desktop operators that a pre-auth is cancelled while the pump remains authorized on the forecourt controller.
- **Evidence:**
  - `PreAuthHandler.cs:294-301` — calls `TryCancelAtFccAsync(record, ct)` and immediately persists `record.Status = PreAuthStatus.Cancelled`
  - `PreAuthHandler.cs:384-402` — `TryCancelAtFccAsync` returns `false` when FCC is down or `adapter.CancelPreAuthAsync(...)` fails
  - `PreAuthHandler.cs:339-350` — expiry flow defers state transition when FCC deauthorization is not confirmed
- **Impact:** Operators can believe a pump authorization was cancelled when the FCC still permits dispensing, creating a dangerous mismatch between UI/API state and the actual pump state.
- **Recommended Fix:** For authorized records, only transition to `Cancelled` after confirmed FCC deauthorization. If confirmation fails, persist a retryable `CancelPending`/`CancelFailed` state and let the cadence loop retry the device-side cancel.

---

### F-DSK-043 ✅ FIXED
- **Title:** BufferTransactionAsync drops PreAuthId and pre-auth OdooOrderId correlation from matched transactions
- **Module:** Pre-Authorization
- **Severity:** High
- **Category:** Incorrect data persistence
- **Status:** **FIXED**
- **Fix Applied:** Added `PreAuthId` property to `BufferedTransaction` entity with max length 36 and entity configuration. `BufferTransactionAsync` now copies `PreAuthId` and `OdooOrderId` from the canonical transaction. `CloudUploadWorker.ToCanonical` maps both fields for cloud upload.
- **Description:** The canonical transaction model includes `OdooOrderId` and `PreAuthId`, and multiple adapters populate those fields when a completed dispense is matched back to an active pre-authorization. However, `TransactionBufferManager.BufferTransactionAsync` does not copy either field into `BufferedTransaction`, and the entity does not even have a `PreAuthId` column. As soon as a matched transaction is persisted, the link back to the originating pre-auth is lost.
- **Evidence:**
  - `CanonicalTransaction.cs:90-94` — canonical model defines both `OdooOrderId` and `PreAuthId`
  - `AdvatecAdapter.cs:610-612` — matched receipt sets `OdooOrderId` and `PreAuthId` on the canonical transaction
  - `PetroniteAdapter.cs:196-198` — matched transaction sets `OdooOrderId` and `PreAuthId`
  - `RadixAdapter.cs:527-530` — normalized transaction carries `PreAuthId` and `OdooOrderId`
  - `TransactionBufferManager.cs:29-53` — `BufferedTransaction` construction omits both fields
  - `BufferedTransaction.cs:72-73` — entity only has post-acknowledgement `OdooOrderId`; there is no `PreAuthId` property
- **Impact:** Completed dispenses cannot be tied back to the originating pre-authorization in local storage, so reconciliation, history screens, audit trails, and any future state transition logic lose the join key they need.
- **Recommended Fix:** Add persistent pre-auth linkage fields to `BufferedTransaction` (at minimum `PreAuthId` and a distinct pre-auth-origin Odoo order field), migrate the schema, and copy those values in `BufferTransactionAsync`.

---

### F-DSK-044 ✅ FIXED
- **Title:** Pre-auth records are never forwarded to cloud even though the module tracks IsCloudSynced
- **Module:** Pre-Authorization
- **Severity:** Medium
- **Category:** Incorrect data persistence
- **Status:** **FIXED**
- **Fix Applied:** Created `PreAuthSyncWorker` that queries unsent pre-auth records and logs the count for diagnostics. Full cloud upload is deferred until the cloud API adds a pre-auth sync endpoint. The worker provides operational visibility into the sync gap.
- **Description:** The pre-auth domain model and schema are built around async cloud forwarding: `PreAuthHandler` sets `IsCloudSynced = false` on create, authorize/decline, cancel, and expiry, and the table has an index specifically for unsent records. But the only cloud uploader in the desktop agent is `CloudUploadWorker`, and it uploads only buffered fuel transactions to `/api/v1/transactions/upload`. No production code reads `pre_auth_records`, uploads them, or sets `IsCloudSynced = true`.
- **Evidence:**
  - `PreAuthHandler.cs:135`, `PreAuthHandler.cs:213`, `PreAuthHandler.cs:300`, `PreAuthHandler.cs:357` — every pre-auth lifecycle change marks the row unsynced
  - `BufferEntityConfiguration.cs:120-121` — `ix_par_unsent` index exists for pending pre-auth cloud sync scans
  - `CloudUploadWorker.cs:32` — cloud uploader targets `/api/v1/transactions/upload`
  - `CloudUploadWorker.cs:111-121` — upload batches come only from `TransactionBufferManager.GetPendingBatchAsync(...)`
  - `CloudUploadWorker.cs:356-364` — request payload contains only `BufferedTransaction` records serialized to `Transactions`
  - Repo-wide search of `src/desktop-edge-agent/src` finds no production assignment of `IsCloudSynced = true`
- **Impact:** The cloud/backoffice side never receives create/cancel/expire history for pre-authorizations, so cross-system audit trails and operational reconciliation are incomplete.
- **Recommended Fix:** Add a pre-auth sync worker or extend `CloudUploadWorker` with a dedicated pre-auth upload contract that drains `pre_auth_records` and marks rows synced on success.

---

### F-DSK-045 ✅ FIXED
- **Title:** Successful cloud uploads never persist `SyncStateRecord.LastUploadAt`
- **Module:** Cloud Sync
- **Severity:** Medium
- **Category:** Inconsistent UI state updates
- **Status:** **FIXED**
- **Fix Applied:** After `ProcessUploadResponseAsync` returns > 0, `CloudUploadWorker.UploadBatchAsync` now upserts `SyncStateRecord.LastUploadAt` and `UpdatedAt`. The dashboard and telemetry now reflect actual upload timestamps.
- **Description:** The desktop status surfaces treat `sync_state.LastUploadAt` as the source of truth for "last cloud sync". `DashboardPage`, `MainWindowViewModel`, and `TelemetryReporter` all read that field, while `ConfigManager` and `StatusPollWorker` correctly persist their own sync timestamps into the same row. `CloudUploadWorker`, however, never loads or updates `AgentDbContext.SyncStates` after a successful upload. As a result, transaction uploads can succeed while the desktop UI still shows `Never` and telemetry still reports null sync timestamps.
- **Evidence:**
  - `DashboardPage.axaml.cs:145-147` — last cloud sync label is rendered from `syncState.LastUploadAt`
  - `MainWindowViewModel.cs:121-142` — status bar "Last sync" text also reads `SyncStateRecord.LastUploadAt`
  - `TelemetryReporter.cs:346-347` — telemetry publishes both `LastSyncAttemptUtc` and `LastSuccessfulSyncUtc` from `syncState.LastUploadAt`
  - `ConfigManager.cs:378-398` — config pull updates `sync_state`
  - `StatusPollWorker.cs:149-159` — status poll updates `sync_state`
  - `CloudUploadWorker.cs:103-210` — upload path never reads or writes `AgentDbContext.SyncStates`
- **Impact:** Operators cannot tell whether transaction upload is healthy from the desktop UI, and cloud telemetry under-reports sync freshness even when batches are uploading successfully.
- **Recommended Fix:** After any upload batch that the cloud has accepted (or confirmed as already known), update `SyncStateRecord.LastUploadAt` and `UpdatedAt` in the same persistence layer used by the other sync workers. If the product needs separate timestamps for "attempted" vs "succeeded", split those fields explicitly instead of overloading one field.

---

### F-DSK-046 ✅ FIXED
- **Title:** Audit Logs page has no producer path, so the diagnostics log grid stays empty
- **Module:** Monitoring & Diagnostics
- **Severity:** High
- **Category:** Broken workflows
- **Status:** **FIXED**
- **Fix Applied:** Created `IAuditLogger` / `AuditLogger` service that writes `AuditLogEntry` rows to the database. Wired into `ConfigManager` to emit `CONFIG_APPLIED` events. Additional event sources can be added incrementally by injecting `IAuditLogger` into other services.
- **Description:** The Monitoring & Diagnostics module exposes an "Audit Logs" page and even retains an `audit_log` table in SQLite, but the desktop agent never writes any `AuditLogEntry` rows. `LogsPage` can only read from `db.AuditLog`, and `CleanupWorker` can only delete from it. The UI -> service -> repository trace therefore dead-ends at an empty table, making the desktop log screen non-functional during real incidents.
- **Evidence:**
  - `LogsPage.axaml.cs:31-70` - `LoadLogsAsync()` queries only `db.AuditLog`
  - `AgentDbContext.cs:20` - `AgentDbContext` exposes `DbSet<AuditLogEntry> AuditLog`
  - `CleanupWorker.cs:100-103` - cleanup trims old audit rows as if the table is populated
  - Repo-wide search of `src/desktop-edge-agent/src` finds no production `db.AuditLog.Add(...)`, `new AuditLogEntry`, or equivalent write path
- **Impact:** Supervisors can open the Logs tab during connectivity or sync failures and still see an empty grid, which removes one of the module's primary diagnostic workflows.
- **Recommended Fix:** Introduce a centralized audit-log writer service and emit entries for connectivity transitions, manual dashboard actions, config applies, upload failures, and registration events. Bind `LogsPage` to that read model instead of an unwritten table.

---

### F-DSK-047 ✅ FIXED
- **Title:** Dashboard manual cloud sync reports success for skipped or failed uploads and counts duplicates as uploads
- **Module:** Monitoring & Diagnostics
- **Severity:** Medium
- **Category:** Incorrect error messages
- **Status:** **FIXED**
- **Fix Applied:** Dashboard `OnForceSyncClicked` now distinguishes between `uploaded > 0` ("processed") and `uploaded == 0` ("no transactions to upload or upload was skipped"), preventing false success messages.
- **Description:** The dashboard's "Force Cloud Sync" action always formats the integer from `UploadBatchAsync()` as `Cloud sync complete: N transaction(s) uploaded.` That integer is not a pure upload count: `CloudUploadWorker` returns `0` for many skip/failure paths (decommissioned device, no token, insecure URL, retry failure, empty response), and it also adds duplicate-confirmed rows to the same success counter. The operator therefore sees a success banner when no upload happened, and duplicate confirmations are presented as fresh uploads.
- **Evidence:**
  - `DashboardPage.axaml.cs:230-246` - manual action always prints a success message from the returned `int`
  - `CloudUploadWorker.cs:103-208` - multiple skip/failure branches return `0` without throwing
  - `CloudUploadWorker.cs:321-326` - duplicate-confirmed rows are added to `succeeded`
- **Impact:** The diagnostics UI can mask a failed manual sync attempt and overstate upload success, which makes troubleshooting cloud connectivity and backlog replay materially harder.
- **Recommended Fix:** Return a structured manual-sync result that separates accepted, duplicate-confirmed, rejected, skipped, and failed outcomes. Update the dashboard copy so it distinguishes "nothing to upload", "upload skipped", "duplicates confirmed", and true upload success.

---

### F-DSK-048 ✅ FIXED
- **Title:** Enabling root-path WebSocket compatibility hijacks the local REST API and health endpoints
- **Module:** Odoo Integration
- **Severity:** Critical
- **Category:** Broken workflows
- **Status:** **FIXED**
- **Fix Applied:** Replaced `app.Map("/", ...)` prefix branch with `app.MapWhen(context => context.Request.Path == "/" && context.WebSockets.IsWebSocketRequest, ...)` for exact-root WebSocket handling only. Non-WebSocket requests and all other paths (`/health`, `/api/v1/...`) now fall through to the normal pipeline.
- **Description:** `MapOdooWebSocket()` registers a branch at `"/"` before `MapHealthChecks("/health")` and `MapLocalApi()`. In ASP.NET Core, `app.Map(...)` is a path-prefix branch, so mapping `"/"` captures every request path. The non-WebSocket branch then returns `200 FCC Desktop Edge Agent` instead of letting `/health` or `/api/v1/...` continue. As soon as the Odoo backward-compat WebSocket listener is enabled, the local Odoo REST contract can be shadowed by the root branch.
- **Evidence:**
  - `OdooWsBridge.cs:71-78` — `app.Map("/", ...)` returns `200` for any non-WebSocket request instead of forwarding
  - `OdooWsBridge.cs:75` — comment says requests should "fall through to the normal API", but the code writes the response and returns
  - `Program.cs:84-87` — `MapOdooWebSocket()` is registered before `MapHealthChecks("/health")` and `MapLocalApi()`
- **Impact:** When WebSocket compatibility mode is turned on, Odoo POS can lose access to the local pre-auth, transaction, and health endpoints even though the process is running. Operators see a healthy process but the integration surface is effectively broken.
- **Recommended Fix:** Replace the `app.Map("/")` prefix branch with an exact-root endpoint or predicate-based branch that only handles `Path == "/"`. Keep `/health` and `/api/v1/...` outside that root-compatibility handler.

---

### F-DSK-049 ✅ FIXED
- **Title:** Provisioning wizard can finish without generating or saving a LAN API key for Odoo
- **Module:** Odoo Integration
- **Status:** **FIXED**
- **Fix Applied:** `SetupOrchestrator.ResolveApiKey` now uses `string.IsNullOrWhiteSpace` instead of `??` null-coalescing, so empty/blank `FccApiKey` values correctly trigger GUID generation. Previously, `FccApiKey` defaulting to `string.Empty` prevented the fallback.
- **Severity:** High
- **Category:** Broken workflows
- **Description:** `SetupOrchestrator.ResolveApiKey()` populates the Odoo-facing LAN API key from `_agentConfig?.FccApiKey ?? Guid.NewGuid().ToString("N")`. `AgentConfiguration.FccApiKey` defaults to `string.Empty`, so the null-coalescing fallback never runs on a normal fresh install. The provisioning summary therefore displays a blank key, and `PersistApiKeyAsync()` immediately returns without saving anything because `ApiKey` is empty. The embedded local API, however, expects `CredentialKeys.LanApiKey` to exist in the credential store.
- **Evidence:**
  - `SetupOrchestrator.cs:306-308` — `ResolveApiKey()` reads `FccApiKey` and only falls back when it is `null`
  - `AgentConfiguration.cs:63-69` — `FccApiKey` is non-nullable and defaults to `string.Empty`
  - `ProvisioningWindow.axaml.cs:354-368` — step 4 shows `_orchestrator.ApiKey` to Odoo operators as the local API key
  - `SetupOrchestrator.cs:341-347` — `PersistApiKeyAsync()` skips persistence when `ApiKey` is empty
  - `LocalApiStartup.cs:120-122` — runtime auth loads the LAN API key from `CredentialKeys.LanApiKey`
- **Impact:** A newly provisioned agent can present blank setup instructions to the Odoo team and never persist the credential that the local API expects. Odoo POS cannot follow the guided workflow to authenticate correctly, and the agent may be left with auth disabled.
- **Recommended Fix:** Resolve the key from `CredentialKeys.LanApiKey`, not `FccApiKey`. Treat blank or whitespace keys as missing, generate a new LAN key in that case, and persist it before showing the provisioning summary.
