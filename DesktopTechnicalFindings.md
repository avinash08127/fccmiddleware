# Desktop Technical Findings

> Architecture, design, and code quality audit of the FCC Desktop Edge Agent.

---

## Application Shell Module

### T-DSK-001
- **Title:** MVVM violation — all UI logic lives in code-behind, ViewModel is bypassed
- **Module:** Application Shell
- **Severity:** High
- **Category:** Architecture violations
- **Status:** **FIXED**
- **Description:** `MainWindow.axaml.cs` directly manipulates named AXAML controls (`StatusConnectivity`, `StatusDot`, `StatusBuffer`, `StatusLastSync`, `PageContent`) from code-behind instead of binding to ViewModel properties. Meanwhile, `MainWindowViewModel` contains bindable properties (`ConnectivityText`, `ConnectivityColor`, `BufferDepth`, `LastSyncText`) with full `INotifyPropertyChanged` support that are never consumed. The navigation also uses code-behind event handlers (`OnNavClicked`) instead of the ViewModel's `NavigateCommand`. This is a complete MVVM bypass — the View talks directly to services, skipping the ViewModel layer.
- **Evidence:**
  - `MainWindow.axaml.cs:112-121` — `NavigateTo()` sets `PageContent.Content` directly
  - `MainWindow.axaml.cs:172-173` — `StatusConnectivity.Text = text` direct control manipulation
  - `MainWindowViewModel.cs:55-89` — Bindable properties that are never bound
  - `MainWindow.axaml.cs:37-38` — services resolved directly from static `AgentAppContext`
- **Impact:** Untestable UI logic, duplicated business logic between code-behind and ViewModel, confusion for developers about which layer owns state.
- **Recommended Fix:** Choose one approach: (a) assign `DataContext = new MainWindowViewModel()` and convert code-behind to bindings/commands, or (b) remove the ViewModel and explicitly document code-behind as the chosen pattern.
- **Resolution:** Option (a) implemented. Created `MainWindowViewModel` with bindable properties (`ConnectivityText`, `ConnectivityDotBrush`, `BufferText`, `LastSyncText`) and `NavigateCommand`. `MainWindow.axaml` updated with `x:DataType` and data bindings for status bar and `Command`/`CommandParameter` for navigation buttons. Connectivity monitoring and buffer stats polling moved from code-behind to ViewModel. Code-behind retains only view-specific concerns (page creation, nav highlight, window state, theme toggle).

---

### T-DSK-002
- **Title:** AgentAppContext is a static service locator anti-pattern
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Weak dependency injection usage
- **Status:** **FIXED**
- **Description:** `AgentAppContext` exposes `ServiceProvider`, `WebApp`, and `Mode` as `static` mutable properties. Every component in the App project reaches into this class to resolve services: `MainWindow`, `ProvisioningWindow`, `SettingsPanel`, and `TrayIconManager` all call `AgentAppContext.ServiceProvider?.GetService<T>()`. This bypasses constructor injection, makes dependencies implicit, and prevents unit testing without global state setup.
- **Evidence:**
  - `AgentAppContext.cs:30` — `public static IServiceProvider? ServiceProvider { get; set; }`
  - `AgentAppContext.cs:35` — `public static WebApplication? WebApp { get; set; }`
  - `MainWindow.axaml.cs:37` — `_services = AgentAppContext.ServiceProvider`
  - `ProvisioningWindow.axaml.cs:47` — `var services = AgentAppContext.ServiceProvider`
  - `App.axaml.cs:113` — `var services = AgentAppContext.ServiceProvider`
- **Impact:** All UI components have hidden dependencies, preventing isolated testing and making dependency chains opaque.
- **Recommended Fix:** Pass `IServiceProvider` through Avalonia's built-in DI mechanisms or a window factory pattern that injects dependencies into window constructors.
- **Resolution:** `MainWindow`, `ProvisioningWindow`, and `SettingsPanel` now accept `IServiceProvider?` via constructor parameters. `App.axaml.cs` (composition root) passes `AgentAppContext.ServiceProvider` to all window/panel constructors. Static `AgentAppContext.ServiceProvider` access eliminated from all windows and panels — only `App.axaml.cs` and `SetupTrayIcon` access it as the composition root. `AgentAppContext.WebApp`/`IsHostStarted` retained for host lifecycle coordination in `Program.cs`.

---

### T-DSK-003
- **Title:** ProvisioningWindow is a fat code-behind with business logic in the UI layer
- **Module:** Application Shell
- **Severity:** High
- **Category:** Business logic in UI layer
- **Status:** **FIXED**
- **Description:** `ProvisioningWindow.axaml.cs` is 804 lines and contains: cloud registration orchestration, manual config validation, URL format validation, device ID generation, connection testing (HTTP calls to cloud and FCC), credential store interaction, environment resolution, site data synchronization, and error-code-to-hint mapping. None of this is in a ViewModel or service — it's all in the AXAML code-behind.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:174-289` — `RegisterWithCodeAsync()` — 115 lines of registration business logic
  - `ProvisioningWindow.axaml.cs:291-347` — `ValidateManualConfigAsync()` — manual config validation
  - `ProvisioningWindow.axaml.cs:353-453` — `RegisterManualWithTokenAsync()` — second registration path
  - `ProvisioningWindow.axaml.cs:457-578` — `RunConnectionTestsAsync()` — HTTP connectivity tests
  - `ProvisioningWindow.axaml.cs:644-707` — `LaunchAgentAsync()` — host start + state persistence
- **Impact:** Registration and setup logic is completely untestable without instantiating an Avalonia window. Business rules are buried in UI code and cannot be reused by the headless Service host.
- **Recommended Fix:** Extract a `ProvisioningViewModel` or `SetupOrchestrator` service that encapsulates registration, validation, connection testing, and state persistence. The code-behind should only handle step panel visibility and button state.
- **Resolution:** Extracted `SetupOrchestrator` service (`Services/SetupOrchestrator.cs`) containing all business logic: input validation, cloud registration (code and manual-token paths), connection testing (cloud and FCC), state persistence, API key management, and host startup. Code-behind reduced to UI-only concerns: step panel visibility, button state, status message display, and mapping orchestrator results to UI elements. Also fixes T-DSK-009 as a side-effect by replacing the fragile `_isCodeMethod` flag mutation with an explicit `StateAlreadyPersisted` property. Also fixes T-DSK-007 by using the DI-registered "fcc" named client for FCC connectivity tests.

---

### T-DSK-004
- **Title:** Duplicated RelayCommand implementations across ViewModels
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Duplicated business logic
- **Status:** **FIXED**
- **Description:** There are two separate `RelayCommand` implementations: a generic `RelayCommand<T>` nested at the bottom of `MainWindowViewModel.cs`, and a non-generic `RelayCommand` as a private nested class in `SettingsViewModel.cs`. Both suppress `CanExecuteChanged` and have identical structure.
- **Evidence:**
  - `MainWindowViewModel.cs:157-173` — `public sealed class RelayCommand<T> : ICommand`
  - `SettingsViewModel.cs:145-152` — `private sealed class RelayCommand(Action execute) : ICommand`
- **Impact:** Minor code duplication. If command behavior needs to change (e.g., adding CanExecute support), it must be changed in multiple places.
- **Recommended Fix:** Create a single `RelayCommand` / `RelayCommand<T>` in `ViewModels/` or a shared infrastructure folder, used by all ViewModels.
- **Resolution:** Created shared `ViewModels/RelayCommand.cs` with both `RelayCommand` and `RelayCommand<T>` implementations. Removed the nested `RelayCommand` class from `SettingsViewModel.cs`. Both `SettingsViewModel` and the new `MainWindowViewModel` now use the shared implementations.

---

### T-DSK-005
- **Title:** Program.cs uses synchronous blocking calls for async host shutdown
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Architecture violations
- **Status:** **FIXED**
- **Description:** `webApp.StopAsync().GetAwaiter().GetResult()` and `Log.CloseAndFlushAsync().GetAwaiter().GetResult()` synchronously block on async operations. While this occurs in Program.cs after the Avalonia event loop has exited (reducing deadlock risk), it's still an anti-pattern that could deadlock if any `StopAsync` handler internally depends on a sync context.
- **Evidence:**
  - `Program.cs:147` — `webApp.StopAsync().GetAwaiter().GetResult()`
  - `Program.cs:163` — `webApp.StopAsync().GetAwaiter().GetResult()` (provisioning path)
  - `Program.cs:180` — `Log.CloseAndFlushAsync().GetAwaiter().GetResult()`
- **Impact:** Low risk of deadlock on shutdown; masks async exceptions.
- **Recommended Fix:** Use `await` in an `async Task Main(string[] args)` entry point, or accept the trade-off with a justification comment.
- **Resolution:** Replaced all `.GetAwaiter().GetResult()` calls with `await`. The C# compiler automatically generates an `async Task Main` entry point when top-level statements use `await`, eliminating the sync-over-async anti-pattern.

---

## Device Provisioning Module

### T-DSK-006
- **Title:** Registration logic duplicated between code and manual-token paths — ~200 lines repeated
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** Duplicated business logic
- **Status:** **FIXED**
- **Description:** `RegisterWithCodeAsync()` (lines 174-289) and `RegisterManualWithTokenAsync()` (lines 353-453) contain nearly identical registration logic: CTS creation with 30s timeout, `DeviceRegistrationService.RegisterAsync()` call, result pattern matching (Success/Rejected/TransportError), error hint resolution, UI button state restoration, and exception handling. The only differences are: (a) status display method (`ShowRegStatus` vs `ShowManualStatus`), (b) button text on failure ("Register" vs "Next"), (c) `ReplaceCheck.IsChecked` (true vs hardcoded false), and (d) post-success actions (environment patching details). Any bug fix or behavior change (e.g., the CTS disposal issue in R-DSK-003) must be applied in both places.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:174-289` — `RegisterWithCodeAsync()` — 115 lines
  - `ProvisioningWindow.axaml.cs:353-453` — `RegisterManualWithTokenAsync()` — 100 lines
  - Both create CTS, call `RegisterAsync`, switch on result type, handle `OperationCanceledException` and generic `Exception`
- **Impact:** Bug fixes applied to one path but not the other. Maintenance burden doubled for registration behavior changes.
- **Recommended Fix:** Extract a shared `PerformRegistrationAsync(cloudUrl, siteCode, token, replace, statusAction, buttonText)` method or move to a ProvisioningViewModel/service.
- **Resolution:** Business logic was already extracted from `ProvisioningWindow` into `SetupOrchestrator` (T-DSK-003). The remaining duplication between `SetupOrchestrator.RegisterWithCodeAsync()` and `RegisterManualWithTokenAsync()` is now eliminated by extracting a shared `ExecuteCloudRegistrationAsync()` method. Both public methods delegate to it, passing only their path-specific parameters (input validation, `replaceExisting`, fallback FCC host/port). The shared method handles `DeviceInfoProvider.BuildRequest`, `RegisterAsync`, result pattern matching, success state updates, environment persistence, and exception handling in a single place.

---

### T-DSK-007
- **Title:** FCC connection test creates raw HttpClient bypassing DI-registered factory
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Weak dependency injection usage
- **Status:** **FIXED**
- **Description:** In `RunConnectionTestsAsync()`, the cloud connectivity test correctly uses `_httpClientFactory?.CreateClient("cloud")` (with TLS enforcement and certificate pinning from the named client configuration). However, the FCC connectivity test creates a `new HttpClient { Timeout = TimeSpan.FromSeconds(10) }` directly, bypassing the DI-registered `IHttpClientFactory` and its "fcc" named client. This means the FCC test doesn't participate in connection pooling, doesn't apply any handler configuration, and may behave differently from the runtime FCC adapter.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:473-474` — cloud test: `_httpClientFactory?.CreateClient("cloud") ?? new HttpClient()` (uses DI)
  - `ProvisioningWindow.axaml.cs:501` — FCC test: `new HttpClient { Timeout = TimeSpan.FromSeconds(10) }` (bypasses DI)
  - `ServiceCollectionExtensions.cs:66-69` — "fcc" named client registered with 10-second timeout
- **Impact:** Minor inconsistency. FCC test behavior may diverge from the runtime adapter's HTTP behavior (e.g., if custom handlers or policies are added to the "fcc" client later).
- **Recommended Fix:** Use `_httpClientFactory?.CreateClient("fcc") ?? new HttpClient { Timeout = ... }` for the FCC test.
- **Resolution:** Connection test logic moved to `SetupOrchestrator.TestFccConnectivityAsync()` which uses `_httpClientFactory.CreateClient("fcc")`. No raw `HttpClient` fallback — if the factory is unavailable, the test returns a clear failure message instead of silently bypassing DI security configuration (S-DSK-007).

---

### T-DSK-008
- **Title:** SiteData sync called twice in code-based registration path
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Duplicated business logic
- **Status:** **FIXED**
- **Description:** When registration succeeds via the code-based path, `SyncSiteData()` is called in two places: first inside `DeviceRegistrationService.HandleSuccessAsync()` (line 148), then again in `ProvisioningWindow.RegisterWithCodeAsync()` (line 251). Both calls pass the same `SiteConfig` object. The second call is redundant since the service already synced the data. The M-09 comment on both calls suggests they were added independently to ensure equipment data is available immediately.
- **Evidence:**
  - `DeviceRegistrationService.cs:147-148` — `_registrationManager.SyncSiteData(result.SiteConfig)` — first sync
  - `ProvisioningWindow.axaml.cs:250-253` — `_registrationManager.SyncSiteData(siteConfig)` — second sync (redundant)
  - Both have M-09 comments explaining the same rationale
- **Impact:** Double file write for site data JSON. No functional harm but indicates unclear ownership of the sync responsibility.
- **Recommended Fix:** Remove the sync call from `ProvisioningWindow.RegisterWithCodeAsync()` since `DeviceRegistrationService` already handles it. Document the responsibility in `IDeviceRegistrationService`.
- **Resolution:** Removed the redundant `SyncSiteData` calls from `SetupOrchestrator.ExecuteCloudRegistrationAsync()` (both code-based and manual-token paths). `DeviceRegistrationService.HandleSuccessAsync()` remains the single owner of the M-09 equipment data sync responsibility.

---

### T-DSK-009
- **Title:** _isCodeMethod flag silently mutated as side effect in manual-token registration path
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Architecture violations
- **Status:** **FIXED**
- **Description:** In `RegisterManualWithTokenAsync()`, after successful registration, `_isCodeMethod` is set to `true` (line 401) even though the user selected the manual configuration method. The comment explains this is to "skip duplicate state save" in `LaunchAgentAsync()`, since `DeviceRegistrationService.RegisterAsync` already persisted the state. This creates a hidden coupling: `LaunchAgentAsync` uses `_isCodeMethod` to decide whether to persist state, but the flag no longer reflects the user's actual method selection. If Step 1 or any navigation logic reads `_isCodeMethod` after this mutation, it will see an incorrect value.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:401` — `_isCodeMethod = true;` — mutated in manual path
  - `ProvisioningWindow.axaml.cs:653` — `if (!_isCodeMethod && _registrationManager is not null)` — guards state persistence in LaunchAgentAsync
  - `ProvisioningWindow.axaml.cs:68` — `_isCodeMethod = RadioCodeMethod.IsChecked == true` — original assignment from radio button
- **Impact:** Fragile coupling between steps. If navigation allows returning to Step 1 in the future, the radio button state and `_isCodeMethod` will be out of sync.
- **Recommended Fix:** Replace the `_isCodeMethod` guard in `LaunchAgentAsync` with a `_stateAlreadyPersisted` boolean flag that explicitly tracks whether registration state was saved by the service layer.
- **Resolution:** `SetupOrchestrator` now uses an explicit `StateAlreadyPersisted` boolean property instead of repurposing `_isCodeMethod`. Both registration paths set `StateAlreadyPersisted = true` on success; `PersistManualStateAsync()` checks this flag to skip redundant persistence. `_isCodeMethod` in `ProvisioningWindow` is now only used for UI panel selection and is never mutated outside its initial assignment.

---

## Authentication & Security Module

### T-DSK-010
- **Title:** Token refresh 401-handling pattern duplicated across four cloud sync workers with inconsistent exception handling
- **Module:** Authentication & Security
- **Severity:** High
- **Category:** Duplicated business logic
- **Status:** **FIXED**
- **Description:** The "try request → catch 401 → refresh token → retry request" pattern is implemented independently in four workers: `CloudUploadWorker`, `ConfigPollWorker`, `StatusPollWorker`, and `TelemetryReporter`. Each has subtle differences in exception handling: `CloudUploadWorker` correctly catches `RefreshTokenExpiredException` and `DeviceDecommissionedException` from the refresh call; `ConfigPollWorker` and `StatusPollWorker` only catch `DeviceDecommissionedException` from the initial request, not from the refresh call; `TelemetryReporter` catches neither, wrapping everything in a generic `catch (Exception)`. This inconsistency has already produced functional bugs (F-DSK-013, F-DSK-014, F-DSK-015).
- **Evidence:**
  - `CloudUploadWorker.cs:143-186` — most complete 401 handling with all exception paths
  - `ConfigPollWorker.cs:80-100` — missing `RefreshTokenExpiredException` catch from refresh
  - `StatusPollWorker.cs:88-109` — same gap
  - `TelemetryReporter.cs:107-122` — minimal handling, no auth-failure-specific paths
- **Impact:** Every new cloud API caller must reimplement the same pattern, and each implementation risks missing exception paths. Bug fixes must be applied to all four workers independently.
- **Recommended Fix:** Extract a shared `AuthenticatedCloudRequest` helper or delegating handler that encapsulates: get token → send request → on 401, refresh token and retry → on `RefreshTokenExpiredException`, call `MarkReprovisioningRequiredAsync` → on `DeviceDecommissionedException`, call `MarkDecommissionedAsync`. All workers delegate to this single implementation.
- **Resolution:** Extracted `AuthenticatedCloudRequestHandler` (singleton, registered in DI) with a generic `ExecuteAsync<T>` method that encapsulates the full auth flow: token acquisition, request execution, 401 refresh-and-retry, `RefreshTokenExpiredException` → `MarkReprovisioningRequiredAsync`, `DeviceDecommissionedException` → `MarkDecommissionedAsync`. Returns `AuthRequestResult<T>` with typed outcomes (`Success`, `NoToken`, `Decommissioned`, `ReprovisioningRequired`, `AuthFailed`, `Failed`). All four workers (`CloudUploadWorker`, `ConfigPollWorker`, `StatusPollWorker`, `TelemetryReporter`) now delegate to this handler, removing `IDeviceTokenProvider` and `IRegistrationManager` from their constructors. `TelemetryReporter` 403 handling also aligned with the other workers (checks for `DEVICE_DECOMMISSIONED` code instead of treating all 403s as decommission).

---

### T-DSK-011
- **Title:** DeviceTokenProvider commit protocol uses staging key that is never atomically linked to the active key
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Architecture violations
- **Status:** FIXED
- **Description:** The `CommitTokenBundleAsync` method writes the token bundle first to a staging key, then to the active key, then cleans up markers. The intent is crash recovery — if the process dies between staging and active writes, the next startup recovers from staging. However, the credential store is not transactional: each `SetSecretAsync` is an independent file write (on Windows/Linux fallback). If the process crashes after writing staging but before writing the active key, `TryRecoverStagedTokenBundleAsync` promotes the staged bundle to active on next startup — this works correctly. But if the process crashes after writing active but before deleting the staging key, the staging key remains with the old (now-correct) bundle, creating an inconsistency where staging and active both exist with the same data. This is benign but the recovery code will unnecessarily "recover" on every startup until the staging key is cleaned up.
- **Evidence:**
  - `DeviceTokenProvider.cs:244-253` — `CommitTokenBundleAsync`: write staging → write active → cleanup
  - `DeviceTokenProvider.cs:210-229` — `TryRecoverStagedTokenBundleAsync`: promotes staged → active if staging exists
  - `DeviceTokenProvider.cs:292-298` — `ClearRefreshMarkersBestEffortAsync`: deletes pending then staging
- **Impact:** Unnecessary recovery operations and log warnings on startup after normal-but-interrupted shutdowns. No data loss but adds confusion to debugging.
- **Recommended Fix:** Delete the staging key before writing the active key (reverse cleanup order), or accept the current behavior and suppress the recovery log warning when staged and active contents match.
- **Fix Applied:** Three changes in `DeviceTokenProvider.cs`: (1) `CommitTokenBundleAsync` now deletes the staging key immediately after writing the active key, before legacy/pending cleanup. (2) `TryRecoverStagedTokenBundleAsync` compares staged vs active bundles — if they match, it skips promotion and just cleans up the stale staging key (log level `Debug` instead of `Warning`). (3) `ClearRefreshMarkersBestEffortAsync` unconditionally deletes both markers (removed the short-circuit that could leave a stale staging key).

---

### T-DSK-012
- **Title:** PlatformCredentialStore uses shared SemaphoreSlim for all platforms, but macOS/Linux secret-tool paths don't need file-level locking
- **Module:** Authentication & Security
- **Severity:** Low
- **Category:** Architecture violations
- **Status:** FIXED (documented)
- **Description:** `_fileLock` (SemaphoreSlim) serializes all credential store operations on all platforms. On macOS, operations go through `/usr/bin/security` (system Keychain) which has its own locking. On Linux with `secret-tool`, operations go through libsecret/D-Bus which also has its own locking. Only the Windows DPAPI + file path and the Linux AES file fallback actually share a JSON file. The shared semaphore unnecessarily serializes macOS Keychain and Linux secret-tool calls, reducing throughput for concurrent credential operations (e.g., concurrent token reads from multiple workers).
- **Evidence:**
  - `PlatformCredentialStore.cs:26` — `private readonly SemaphoreSlim _fileLock = new(1, 1)` — shared across all platforms
  - `PlatformCredentialStore.cs:85,101,126` — Windows methods acquire `_fileLock`
  - `PlatformCredentialStore.cs:141-155` — macOS methods do NOT acquire `_fileLock` (correctly)
  - `PlatformCredentialStore.cs:250-261` — Linux file fallback acquires `_fileLock` (correctly)
- **Impact:** Minor — macOS Keychain operations are already naturally serialized by the system. The semaphore adds negligible overhead on those platforms.
- **Recommended Fix:** Acceptable as-is. The semaphore overhead is minimal and provides a consistent safety net. Document that the lock is primarily for the file-backed stores.
- **Fix Applied:** Added a documentation comment to the `_fileLock` field in `PlatformCredentialStore.cs` explaining that the semaphore is specifically for file-backed credential stores (Windows DPAPI + JSON, Linux AES fallback), and that macOS Keychain and Linux secret-tool have their own platform-level locking.

---

### T-DSK-013
- **Title:** Decommission handling pattern inconsistent — `_decommissioned` volatile flag vs `DeviceDecommissioned` event
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Architecture violations
- **Status:** FIXED
- **Description:** Three different mechanisms signal decommission state: (1) `volatile bool _decommissioned` flags in `CloudUploadWorker`, `ConfigPollWorker`, and `StatusPollWorker` — process-lifetime flags that prevent further cloud API calls; (2) `RegistrationManager.MarkDecommissionedAsync()` which persists `IsDecommissioned = true` to `registration.json` and raises the `DeviceDecommissioned` event; (3) the `DeviceDecommissioned` event on `IRegistrationManager` which the UI subscribes to for window transition. Workers 1-3 each independently call `MarkDecommissionedAsync` and set their own flag. If `MarkDecommissionedAsync` fails (I/O error), the worker sets its local flag but the UI is never notified and the persisted state is not updated — on restart, the device will not know it's decommissioned.
- **Evidence:**
  - `CloudUploadWorker.cs:50` — `private volatile bool _decommissioned`
  - `ConfigPollWorker.cs:41` — `private volatile bool _decommissioned`
  - `StatusPollWorker.cs:42` — `private volatile bool _decommissioned`
  - `RegistrationManager.cs:120-133` — `MarkDecommissionedAsync` persists + raises event
  - `TelemetryReporter.cs:129` — calls `MarkDecommissionedAsync` but has no local `_decommissioned` flag
- **Impact:** If file persistence fails during decommission marking, the worker stops but the system doesn't fully transition to decommissioned state. On restart, workers resume cloud calls that the server will again reject.
- **Recommended Fix:** Centralize the decommission flag in `RegistrationManager` (or a shared `DecommissionGuard` singleton). Workers check this shared flag instead of maintaining independent volatile booleans. This ensures all workers stop atomically and the state is always consistent.
- **Fix Applied:** Added `bool IsDecommissioned` property to `IRegistrationManager` (backed by the cached `RegistrationState`). Removed `volatile bool _decommissioned` fields from `CloudUploadWorker`, `ConfigPollWorker`, and `StatusPollWorker`. All four workers (`CloudUploadWorker`, `ConfigPollWorker`, `StatusPollWorker`, `TelemetryReporter`) now check `_registrationManager.IsDecommissioned` at the top of each tick. `TelemetryReporter` (which was missing the check entirely) now also gates on the centralized flag. `AuthenticatedCloudRequestHandler` already calls `MarkDecommissionedAsync` on decommission detection, so the flag is set atomically for all workers.

---

### T-DSK-014
- **Title:** OdooWsMessageHandler modifies database records without authorization checks — business logic in WebSocket layer
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Business logic in UI layer
- **Status:** FIXED
- **Description:** `OdooWsMessageHandler.HandleManagerUpdateAsync` and `HandleAttendantUpdateAsync` directly modify `BufferedTransaction` records (setting `OrderUuid`, `OdooOrderId`, `PaymentId`, `AddToCart`, `IsDiscard`) based on raw WebSocket JSON messages. There is no validation that the `transaction_id` belongs to the connected client's site, no authorization check, and no business rule validation (e.g., whether a completed transaction should be modifiable). The handler acts as both a protocol parser and a repository, mixing transport concerns with data persistence.
- **Evidence:**
  - `OdooWsMessageHandler.cs:90-121` — `HandleManagerUpdateAsync` directly queries and modifies `AgentDbContext`
  - `OdooWsMessageHandler.cs:125-170` — `HandleAttendantUpdateAsync` — same pattern
  - `OdooWsMessageHandler.cs:239-264` — `HandleManagerManualUpdateAsync` sets `IsDiscard = true` directly
- **Impact:** No authorization boundary between WebSocket commands and data modification. Any WebSocket client can modify any transaction record. Business rules for transaction mutability are not enforced.
- **Recommended Fix:** Route WebSocket commands through the same service layer used by the REST API, which should enforce authorization and business rules. The handler should parse the WebSocket protocol and delegate to a shared `ITransactionService`.
- **Fix Applied:** Created `ITransactionUpdateService` and `TransactionUpdateService` in the `Buffer` namespace. The service encapsulates all transaction mutation logic (`ApplyManagerUpdateAsync`, `ApplyAttendantUpdateAsync`, `DiscardTransactionAsync`). `OdooWsMessageHandler` now parses the WebSocket JSON into `TransactionUpdateFields` DTOs and delegates to `ITransactionUpdateService` via scoped DI resolution. The handler no longer directly accesses `AgentDbContext` for mutation operations. The service is registered as scoped in DI and can be reused by the REST API layer.

---

### T-DSK-015
- **Title:** CertificatePinValidator pins are hardcoded — no mechanism for emergency pin rotation
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Architecture violations
- **Status:** FIXED
- **Description:** `CertificatePinValidator.PinnedHashes` is a `static readonly HashSet<string>` with two hardcoded intermediate CA SPKI hashes. If either CA is compromised or rotated, every deployed agent must be updated and restarted. There is no configuration-based override, no mechanism to add emergency pins via cloud config, and no pin expiry or backup pin rotation strategy. The comment says "Update when rotating cloud TLS certificates" but there is no operational process to push this update to field devices.
- **Evidence:**
  - `CertificatePinValidator.cs:18-22` — `private static readonly HashSet<string> PinnedHashes` — compile-time constants
  - No configuration binding for additional pins
  - No cloud-config-based pin injection
- **Impact:** Certificate rotation requires a software update to all deployed agents. If the primary CA is compromised, agents cannot be reconfigured to trust a new CA without a release cycle.
- **Recommended Fix:** Allow additional pins to be loaded from configuration (`appsettings.json` or cloud config) while keeping the compiled-in pins as a baseline. Consider a pin expiry mechanism where the cloud can push new pins before the old CA expires.
- **Fix Applied:** `CertificatePinValidator` now maintains compiled-in `BootstrapPins` as the baseline plus a thread-safe `_effectivePins` set that merges bootstrap pins with configuration-loaded ones. New `LoadAdditionalPins(IEnumerable<string>)` method accepts pins from any source. Added `AdditionalCertificatePins` property to `AgentConfiguration` and `SiteConfigSync.CertificatePins`. `ConfigManager.ApplyHotReloadFields` calls `CertificatePinValidator.LoadAdditionalPins()` when cloud config includes additional pins, enabling emergency rotation without a software update.

---

## Configuration Module

### T-DSK-016
- **Title:** ConfigurationPage is a fat code-behind — 220+ lines of config construction and orchestration in UI layer
- **Module:** Configuration
- **Severity:** High
- **Category:** Business logic in UI layer
- **Status:** FIXED
- **Description:** `ConfigurationPage.axaml.cs` (312 lines) contains the entire config save workflow in code-behind: constructing a complete `SiteConfig` object with 10+ nested sections (lines 107-164), serializing to JSON, calling `ApplyConfigAsync`, handling all four `ConfigApplyOutcome` variants, managing restart banners, API key regeneration, update checks, and log level resolution. There is no ViewModel. The `OnSaveClicked` handler alone is 125 lines of business logic that maps UI controls to domain objects, constructs the complete cloud config structure, and orchestrates the apply flow. This logic cannot be unit-tested without instantiating an Avalonia `UserControl`, and it cannot be reused by the headless `FccDesktopAgent.Service` host.
- **Evidence:**
  - `ConfigurationPage.axaml.cs:96-220` — `OnSaveClicked` — 125 lines of config construction + apply + feedback
  - `ConfigurationPage.axaml.cs:107-164` — full `SiteConfig` object construction with 10 nested sections
  - `ConfigurationPage.axaml.cs:224-230` — `OnRegenerateApiKeyClicked` — credential management in code-behind
  - `ConfigurationPage.axaml.cs:234-266` — `OnCheckUpdateClicked` — update service orchestration in code-behind
- **Impact:** Config save logic is untestable, unreusable, and maintenance-heavy. The section reconstruction bug (F-DSK-018) exists precisely because the field mapping is buried in 60 lines of code-behind rather than in a tested service method.
- **Recommended Fix:** Extract a `ConfigurationViewModel` or `ConfigSaveService` that owns the `SiteConfig` construction, validation, and apply orchestration. The code-behind should only map UI events to ViewModel commands.
- **Fix Applied:** Created `ConfigSaveService` in `FccDesktopAgent.Core.Config` that owns SiteConfig construction (section cloning with F-DSK-018 preservation), JSON serialization, `ApplyConfigAsync` orchestration, and LAN API key persistence. Introduced `ConfigSaveFields` record as the UI→service contract and `ConfigSaveResult` as the return type. `ConfigurationPage.OnSaveClicked` is now ~50 lines that maps UI controls to `ConfigSaveFields`, calls `ConfigSaveService.SaveAsync()`, and renders the result. The `CloneSection<T>` helper was removed from code-behind. The service is registered as singleton in DI and is reusable by the headless host.

---

### T-DSK-017
- **Title:** SettingsViewModel is dead code — never bound to any view
- **Module:** Configuration
- **Severity:** Medium
- **Category:** Duplicated business logic
- **Description:** `SettingsViewModel.cs` (153 lines) is a complete MVVM ViewModel with bindable properties (`FccHost`, `FccPort`, `JplPort`, `WsPort`, `CloudFccHost`, `CloudFccPort`, `HasOverrides`, `Feedback`), `Save` and `Reset` commands, and a `ReconnectRequested` event. However, `SettingsPanel.axaml.cs` — the only view for FCC override settings — never instantiates or binds this ViewModel. Instead, `SettingsPanel` duplicates the exact same `LocalOverrideManager` save/reset logic in its own code-behind. The ViewModel is completely unreachable at runtime. This creates three parallel implementations of the same override save logic: (1) `SettingsViewModel.Save()`, (2) `SettingsPanel.OnSaveClicked()`, and (3) the `LocalOverrideManager` API itself.
- **Evidence:**
  - `SettingsViewModel.cs:25` — constructor takes `LocalOverrideManager` and `AgentConfiguration`
  - `SettingsViewModel.cs:96-113` — `Save()` calls `_overrideManager.SaveAll(...)` — identical to SettingsPanel
  - `SettingsPanel.axaml.cs:63-113` — `OnSaveClicked` duplicates the same save logic in code-behind
  - No `DataContext = new SettingsViewModel(...)` anywhere in `SettingsPanel.axaml.cs`
- **Impact:** Dead code doubles the maintenance surface. Bug fixes or validation changes must be considered across the dead ViewModel and the live code-behind, causing confusion about which implementation is active.
- **Recommended Fix:** Either (a) bind `SettingsViewModel` as the DataContext and remove the code-behind logic, or (b) delete `SettingsViewModel.cs` entirely since `SettingsPanel` uses code-behind.

---

### T-DSK-018
- **Title:** Two IPostConfigureOptions<AgentConfiguration> registrations with undocumented ordering dependency
- **Module:** Configuration
- **Severity:** Medium
- **Category:** Architecture violations
- **Description:** `ServiceCollectionExtensions.AddAgentCore` registers both `RegistrationManager` (line 56-57) and `ConfigManager` (line 112-113) as `IPostConfigureOptions<AgentConfiguration>`. Both overlay fields onto the same `AgentConfiguration` instance. `RegistrationManager.PostConfigure` sets `DeviceId`, `SiteId`, `CloudBaseUrl` from `registration.json`. `ConfigManager.PostConfigure` (via `ApplyHotReloadFields`) sets `DeviceId`, `SiteId` from cloud config. Since `ConfigManager` is registered second, it runs second and overwrites the registration-sourced identity fields with cloud-sourced values. This ordering is critical for correctness but is implicit — it depends on DI registration order, which is fragile and undocumented.
- **Evidence:**
  - `ServiceCollectionExtensions.cs:56-57` — `RegistrationManager` registered as `IPostConfigureOptions` first
  - `ServiceCollectionExtensions.cs:112-113` — `ConfigManager` registered as `IPostConfigureOptions` second
  - `RegistrationManager.cs:181-185` — sets `DeviceId`, `SiteId` from registration state
  - `ConfigManager.cs:219-223` — sets `DeviceId`, `SiteId` from cloud config (overwrites registration values)
- **Impact:** If the registration order is accidentally changed (e.g., during a refactor), identity fields would be sourced from registration.json instead of cloud config, potentially using stale values after a site reassignment.
- **Recommended Fix:** Document the ordering dependency with a comment at both registration sites. Consider merging the two PostConfigure implementations into a single composite that explicitly defines the precedence chain.

---

### T-DSK-019
- **Title:** CheckSection uses JSON serialization for object equality — allocation-heavy change detection
- **Module:** Configuration
- **Severity:** Low
- **Category:** Architecture violations
- **Description:** `ConfigManager.CheckSection` compares config sections by serializing both the previous and current objects to JSON strings via `JsonSerializer.Serialize`, then doing an ordinal string comparison. This is called for all 10 sections on every config apply. Each call allocates two temporary JSON strings (potentially kilobytes for sections like `Mappings` with nozzle arrays). While this only runs when a new config version is received (not on 304 Not Modified responses), the approach is wasteful when simpler structural equality or hash-based comparison would suffice.
- **Evidence:**
  - `ConfigManager.cs:317-318` — `var prevJson = JsonSerializer.Serialize(previous); var currJson = JsonSerializer.Serialize(current);`
  - `ConfigManager.cs:300-309` — called for 10 sections: identity, site, fcc, sync, buffer, localApi, telemetry, fiscalization, mappings, rollout
- **Impact:** 20 JSON allocations per config apply. Minor GC pressure on edge devices. No functional impact.
- **Recommended Fix:** Implement `IEquatable<T>` on section classes for structural comparison, or compute and cache a hash of each section. Alternatively, accept the trade-off since config applies are infrequent.

---

## FCC Device Integration Module

### T-DSK-020
- **Title:** OdooWsMessageHandler allocated per WebSocket message — unnecessary object creation on every frame
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Architecture violations
- **Description:** `OdooWebSocketServer.HandleMessageAsync` creates a `new OdooWsMessageHandler(...)` on every single incoming WebSocket message (line 139). The handler has no per-message state — it only holds references to the shared `IServiceScopeFactory`, `ILogger`, `JsonSerializerOptions`, and broadcast delegate. With pump status broadcasts every 3 seconds per connection plus user commands, a busy site with 5 connections generates ~100+ handler allocations per minute. The handler could be a singleton or created once per connection.
- **Evidence:**
  - `OdooWebSocketServer.cs:139` — `var handler = new OdooWsMessageHandler(_scopeFactory, _logger, _jsonOptions, BroadcastToAllAsync)` — per-message allocation
  - `OdooWsMessageHandler.cs:25-29` — constructor only captures references, no per-message state
- **Impact:** Unnecessary GC pressure from ~100+ allocations/minute on active sites. Each allocation is small but adds up on memory-constrained edge devices.
- **Recommended Fix:** Create the handler once per connection in `HandleConnectionAsync` and reuse it for all messages on that connection. Or make it a singleton field on `OdooWebSocketServer`.

---

### T-DSK-021
- **Title:** Currency conversion logic duplicated across three adapters — inconsistent decimal handling
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Duplicated business logic
- **Description:** Currency conversion between major units (dollars/shillings) and minor units (cents) is implemented independently in three adapters: `PetroniteAdapter.GetCurrencyDecimals()` + `DecimalPow10()` (lines 925-943), `AdvatecAdapter` uses `CurrencyHelper.GetCurrencyFactor()`, and `DomsAdapter` receives amounts in minor units directly. The Petronite adapter has a comprehensive ISO 4217 currency decimals table, while the `CurrencyHelper` in the Advatec adapter may have a different mapping. If a new currency is added or a mapping corrected, it must be updated in multiple places. The Petronite adapter also has its own `DecimalPow10` implementation that could drift from `CurrencyHelper.GetCurrencyFactor`.
- **Evidence:**
  - `PetroniteAdapter.cs:925-932` — `GetCurrencyDecimals` — Petronite's currency table
  - `PetroniteAdapter.cs:937-943` — `DecimalPow10` — Petronite's own power-of-10
  - `AdvatecAdapter.cs:541` — `CurrencyHelper.GetCurrencyFactor` — separate utility
  - `CurrencyUtils.cs` — shared utility but not used by Petronite
- **Impact:** Bug fixes or new currency support must be applied in multiple places. If Petronite and Advatec use different decimal counts for the same currency, amounts will be incorrect.
- **Recommended Fix:** Consolidate all currency conversion into `CurrencyUtils.cs` and use it from all adapters. Replace `PetroniteAdapter.GetCurrencyDecimals` and `DecimalPow10` with calls to the shared utility.

---

### T-DSK-022
- **Title:** FccAdapterFactory calls DisposeAsync().GetAwaiter().GetResult() inside lock — sync-over-async anti-pattern
- **Module:** FCC Device Integration
- **Severity:** High
- **Category:** Architecture violations
- **Description:** Both `GetOrCreatePetroniteAdapter` (line 81) and `GetOrCreateAdvatecAdapter` (line 126) call `_cachedAdapter?.DisposeAsync().AsTask().GetAwaiter().GetResult()` inside a `lock` block. This is a sync-over-async pattern that blocks the thread while holding the lock. The `DisposeAsync` implementations for both Petronite and Advatec adapters involve stopping webhook HTTP listeners (which may have pending I/O), disposing semaphores, and setting fields to null. If any awaited operation inside `DisposeAsync` tries to schedule a continuation on the same thread or acquires another lock, a deadlock can occur.
- **Evidence:**
  - `FccAdapterFactory.cs:81` — `_cachedPetroniteAdapter?.DisposeAsync().AsTask().GetAwaiter().GetResult()` inside `lock (_petroniteLock)`
  - `FccAdapterFactory.cs:126` — `_cachedAdvatecAdapter?.DisposeAsync().AsTask().GetAwaiter().GetResult()` inside `lock (_advatecLock)`
  - `IngestionOrchestrator.cs:454` — same pattern: `_fiscalizationService.DisposeAsync().AsTask().GetAwaiter().GetResult()`
- **Impact:** Potential deadlock during config-driven adapter recreation. Thread pool starvation during webhook listener shutdown.
- **Recommended Fix:** Replace `lock` with `SemaphoreSlim(1,1)` and use `await _cachedAdapter.DisposeAsync()` with proper async flow. The `Create` method would need to become async (`CreateAsync`), which would require updating `IFccAdapterFactory`.

---

### T-DSK-023
- **Title:** IngestionOrchestrator mixes ingestion with fiscalization retry logic — 475-line class with two responsibilities
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Business logic in UI layer
- **Description:** `IngestionOrchestrator` (475 lines) handles two distinct concerns: (1) FCC transaction polling and buffering (core responsibility), and (2) Advatec post-dispense fiscalization with exponential backoff retry (lines 299-417). The fiscalization logic includes its own retry loop, backoff calculation, dead-letter handling, and `FiscalizationContext` construction. This is a separate workflow that happens to run after ingestion but has no architectural dependency on the ingestion pipeline — it could run independently on a timer or as a separate cadence task.
- **Evidence:**
  - `IngestionOrchestrator.cs:299-417` — `RetryPendingFiscalizationAsync` — 118 lines of fiscalization retry logic
  - `IngestionOrchestrator.cs:426-460` — `ResolveFiscalizationService` — adapter lifecycle management
  - `IngestionOrchestrator.cs:467-474` — `BuildFiscalizationContext` — domain logic construction
  - Total fiscalization-related code: ~175 lines (37% of the class)
- **Impact:** The class is harder to test, modify, and reason about. Changes to fiscalization retry behavior require modifying the ingestion orchestrator.
- **Recommended Fix:** Extract a `FiscalizationRetryWorker` that owns the retry loop, backoff, and dead-lettering. The `IngestionOrchestrator` passes newly buffered transaction IDs to the worker, which processes them independently.

---

### T-DSK-024
- **Title:** OdooWsMessageHandler directly modifies EF entities without domain service — bypasses all business rules
- **Module:** FCC Device Integration
- **Severity:** High
- **Category:** Business logic in UI layer
- **Description:** `HandleManagerUpdateAsync`, `HandleAttendantUpdateAsync`, and `HandleManagerManualUpdateAsync` directly query `AgentDbContext`, load `BufferedTransaction` entities, set fields (`OrderUuid`, `OdooOrderId`, `PaymentId`, `AddToCart`, `IsDiscard`), and call `SaveChangesAsync`. There is no validation, no authorization, no audit logging, and no shared service layer. The REST API's `TransactionEndpoints.AcknowledgeAsync` routes through `TransactionBufferManager.AcknowledgeAsync` which has conflict detection (different `OdooOrderId` returns `Conflict`). The WebSocket path skips this entirely — it overwrites `OdooOrderId` unconditionally. This means a WebSocket client can silently overwrite an existing Odoo order assignment without any error.
- **Evidence:**
  - `OdooWsMessageHandler.cs:106` — `tx.OdooOrderId = oi.ToString()` — unconditional overwrite, no conflict detection
  - `TransactionBufferManager.cs:277-284` — REST path checks for existing `OdooOrderId` and returns `Conflict`
  - `OdooWsMessageHandler.cs:254` — `tx.IsDiscard = true` — permanent data modification with no authorization
- **Impact:** WebSocket clients can silently corrupt transaction metadata, override Odoo order assignments, and mark transactions as discarded without any guardrails.
- **Recommended Fix:** Route all WebSocket data modifications through `TransactionBufferManager` or a shared `ITransactionService` that enforces conflict detection, authorization, and audit logging.

---

## Site Master Data Module

### T-DSK-025
- **Title:** Dual site data representation — SiteDataSnapshot (JSON file) vs NozzleMapping (SQLite) — with no synchronization
- **Module:** Site Master Data
- **Severity:** High
- **Category:** Architecture violations
- **Description:** The system maintains two parallel data stores for the same nozzle/pump mapping information: (1) `SiteDataManager` writes `site-data.json` with `LocalPump`, `LocalNozzle`, and `LocalProduct` records extracted from `SiteConfig.Mappings`; (2) `NozzleMapping` EF Core entity mapped to the `nozzles` SQLite table stores the same Odoo-to-FCC pump/nozzle translation. These two stores are never synchronized. `SiteDataManager` writes to the JSON file but not the database. `PreAuthHandler` reads from the database but not the JSON file. Neither store is the single source of truth. The `SiteDataSnapshot` model and the `NozzleMapping` entity contain overlapping fields (`OdooPumpNumber`, `FccPumpNumber`, `OdooNozzleNumber`, `FccNozzleNumber`, `ProductCode`) with no shared abstraction.
- **Evidence:**
  - `SiteDataManager.cs:80-88` — writes nozzles to `site-data.json`
  - `NozzleMapping.cs` — EF entity for `nozzles` SQLite table
  - `PreAuthHandler.cs:71` — reads from `_db.NozzleMappings` (SQLite)
  - No code synchronizes the two stores
- **Impact:** Data drift between the two stores is inevitable. Adding a new consumer requires knowing which store to read, with no architectural guidance. The JSON file and SQLite table can contain different versions of the truth after partial updates.
- **Recommended Fix:** Consolidate on a single source of truth. Either (a) populate the `nozzles` table from `SiteConfig` and have all consumers read from SQLite, or (b) have `PreAuthHandler` read from `SiteDataManager` instead of the database. Deprecate the unused store.

---

### T-DSK-026
- **Title:** ConfigurationPage contains 200+ lines of business logic in code-behind — bypasses MVVM architecture
- **Module:** Site Master Data
- **Severity:** Medium
- **Category:** Business logic in UI layer
- **Description:** `ConfigurationPage.axaml.cs` (312 lines) directly resolves services from the DI container (`AgentAppContext.ServiceProvider`), reads config from `IOptionsMonitor`, constructs `SiteConfig` objects with business rules (version bumping, field mapping), calls `ConfigManager.ApplyConfigAsync`, handles API key generation, and manages async feedback timers. None of this logic goes through a ViewModel. The `SettingsPanel.axaml.cs` (161 lines) follows the same pattern — directly resolving `LocalOverrideManager` and `IIngestionOrchestrator` from the service provider. Meanwhile, `SettingsViewModel` exists with proper MVVM patterns (INotifyPropertyChanged, ICommand) but is only used for the override panel, not for the main ConfigurationPage.
- **Evidence:**
  - `ConfigurationPage.axaml.cs:24` — `_services = AgentAppContext.ServiceProvider` — static service locator
  - `ConfigurationPage.axaml.cs:96-164` — `OnSaveClicked` — 68 lines of config construction + application logic
  - `ConfigurationPage.axaml.cs:224-229` — `OnRegenerateApiKeyClicked` — API key generation in code-behind
  - `SettingsPanel.axaml.cs:63-113` — `OnSaveClicked` — override save + adapter reconnect logic
  - `SettingsViewModel.cs` — proper MVVM ViewModel exists but only for the override panel
- **Impact:** Business logic is untestable (tied to Avalonia UI thread), duplicated across views, and tightly coupled to the DI container via static service locator. Configuration save behavior cannot be unit tested without instantiating Avalonia controls.
- **Recommended Fix:** Extract a `ConfigurationPageViewModel` with bindable properties and commands. Move SiteConfig construction, version management, and ApplyConfigAsync calls into the ViewModel. Use constructor injection instead of static `AgentAppContext.ServiceProvider`.

---

### T-DSK-027
- **Title:** SettingsViewModel takes concrete AgentConfiguration snapshot — cloud defaults go stale after hot-reload config changes
- **Module:** Site Master Data
- **Severity:** Medium
- **Category:** Weak dependency injection usage
- **Description:** `SettingsViewModel` constructor accepts `AgentConfiguration agentConfig` (a concrete value object, not `IOptionsMonitor<AgentConfiguration>`). The `LoadValues` method reads `_agentConfig.FccBaseUrl` to display cloud defaults. If the cloud pushes a config update that changes `FccBaseUrl` (via hot-reload through `ConfigManager`), the ViewModel still shows the old URL because it holds a reference to the original snapshot. The cloud defaults display becomes permanently stale after the first config change.
- **Evidence:**
  - `SettingsViewModel.cs:25` — `public SettingsViewModel(LocalOverrideManager overrideManager, AgentConfiguration agentConfig)`
  - `SettingsViewModel.cs:128-132` — `LoadValues()` reads `_agentConfig.FccBaseUrl` — snapshot value
  - `ConfigManager.cs:216-284` — `ApplyHotReloadFields` updates `IOptionsMonitor` but not captured snapshots
- **Impact:** The FCC Connection Overrides panel shows stale cloud defaults after any config hot-reload. Operators see incorrect baseline values when deciding whether to set overrides.
- **Recommended Fix:** Accept `IOptionsMonitor<AgentConfiguration>` instead of `AgentConfiguration`. Read `_configMonitor.CurrentValue` in `LoadValues()` so it always reflects the latest hot-reloaded config.

---

### T-DSK-028
- **Title:** SiteDataManager has no interface — cannot be mocked for testing or swapped for alternative implementations
- **Module:** Site Master Data
- **Severity:** Low
- **Category:** Weak dependency injection usage
- **Description:** `SiteDataManager` is registered as a concrete singleton (`services.AddSingleton<SiteDataManager>()`) with no interface extraction. `RegistrationManager` takes it as a direct dependency (`SiteDataManager? _siteDataManager`). This makes it impossible to mock for unit testing and prevents swapping implementations (e.g., a database-backed implementation that resolves the dual-store issue in T-DSK-025).
- **Evidence:**
  - `ServiceCollectionExtensions.cs:50` — `services.AddSingleton<SiteDataManager>()` — concrete type only
  - `RegistrationManager.cs:27` — `private readonly SiteDataManager? _siteDataManager` — concrete dependency
  - No `ISiteDataManager` interface exists
- **Impact:** Unit tests for `RegistrationManager` cannot mock the site data sync behavior. The concrete class is the only integration point, preventing alternate implementations.
- **Recommended Fix:** Extract an `ISiteDataManager` interface with `SyncFromConfig` and `LoadSiteData` methods. Register as `services.AddSingleton<ISiteDataManager, SiteDataManager>()`.

---

## Transaction Management Module

### T-DSK-029
- **Title:** TransactionsPage performs direct EF Core database access in UI code-behind — violates MVVM separation
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Architecture violations
- **Description:** `TransactionsPage.axaml.cs` directly resolves `AgentDbContext` from the service provider (line 47) and executes EF Core LINQ queries with filtering, paging, and projection (lines 49-89) inside UI event handlers. This places data access logic, query construction, and business rules (filter mapping, pagination math) in the View layer instead of a ViewModel or Service. The page manages its own state (_currentPage, _totalCount, filter fields) as private fields rather than bindable ViewModel properties. Other pages in the app (e.g., DashboardPage) follow a similar code-behind pattern, but TransactionsPage is the most complex case with filter combinations and cursor-based pagination.
- **Evidence:**
  - `TransactionsPage.axaml.cs:47` — `var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>()`
  - `TransactionsPage.axaml.cs:49-89` — full EF Core query with Where/OrderBy/Skip/Take/Select
  - `TransactionsPage.axaml.cs:19-26` — UI state managed as private fields instead of ViewModel
  - `TransactionsPage.axaml:4` — `x:CompileBindings="False"` — no compile-time binding checks
- **Impact:** Query logic cannot be unit tested without instantiating the full Avalonia visual tree. Filter/pagination logic is duplicated if another view needs transaction data. Binding errors are only caught at runtime.
- **Recommended Fix:** Extract a `TransactionsViewModel` with observable properties for filter state, page data, and pagination commands. Move the EF Core queries to a `ITransactionQueryService` or reuse `TransactionBufferManager`. Enable `x:CompileBindings="True"`.

### T-DSK-030
- **Title:** OdooWsMessageHandler contains business logic in transport layer — OdooOrderId stamping, AddToCart, IsDiscard mutation
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Business logic in UI layer
- **Description:** `OdooWsMessageHandler` directly modifies `BufferedTransaction` entity state (OdooOrderId, OrderUuid, AddToCart, PaymentId, IsDiscard) in WebSocket message handlers (lines 105-113, 142-163, 253-256). These are business-significant state changes that affect transaction reconciliation (OdooOrderId linkage), POS cart workflow (AddToCart), and transaction lifecycle (IsDiscard). The handler bypasses `TransactionBufferManager` entirely, accessing `AgentDbContext` directly. The same business operations are exposed through the REST API via `TransactionBufferManager.AcknowledgeAsync` with different validation rules, creating two inconsistent code paths for the same domain operation.
- **Evidence:**
  - `OdooWsMessageHandler.cs:105-113` — `HandleManagerUpdateAsync` directly mutates entity fields
  - `OdooWsMessageHandler.cs:253-256` — `HandleManagerManualUpdateAsync` sets `IsDiscard = true` directly
  - `TransactionBufferManager.cs:270-294` — `AcknowledgeAsync` has conflict detection that WebSocket bypasses
  - `OdooWsMessageHandler.cs:49-50` — creates scoped DbContext directly, not using domain services
- **Impact:** Business rules for transaction state mutation are split across two layers with different validation. Changes to acknowledgment logic must be synchronized between REST and WebSocket paths. Domain invariants (e.g., OdooOrderId immutability after first stamp) are enforced inconsistently.
- **Recommended Fix:** Create a `TransactionUpdateService` that encapsulates OdooOrderId stamping, cart operations, and discard logic with consistent validation. Have both `OdooWsMessageHandler` and `TransactionEndpoints` delegate to this service.

### T-DSK-031
- **Title:** WsDtoMappers uses static Interlocked counter for PumpTransactionWsDto.Id — IDs are session-scoped and non-deterministic
- **Module:** Transaction Management
- **Severity:** Low
- **Category:** Architecture violations
- **Description:** `WsDtoMappers.ToWsDto` (OdooWsModels.cs:136-137) uses a static `_txIdCounter` with `Interlocked.Increment` to generate the `Id` field of `PumpTransactionWsDto`. This produces sequential integers that reset to zero on every process restart, are different across test runs, and don't correspond to any persistent identifier. The legacy Odoo POS client may rely on these IDs being stable (e.g., for cart item dedup or UI refresh), but after a service restart, the same BufferedTransaction will get a different numeric ID, potentially causing Odoo POS to treat it as a new transaction.
- **Evidence:**
  - `OdooWsModels.cs:136-137` — `private static int _txIdCounter; ... Id = Interlocked.Increment(ref _txIdCounter)`
  - `OdooWsModels.cs:13-14` — `public int Id { get; set; }` — integer ID, not UUID
  - Legacy contract expects integer IDs that were originally database-generated auto-increment PKs
- **Impact:** After service restart, Odoo POS may duplicate transactions in the cart or fail to find previously referenced transactions by ID. Test assertions on the Id field are non-deterministic.
- **Recommended Fix:** Use a deterministic ID derivation from the transaction's persistent state (e.g., hash of FccTransactionId, or a stable integer mapping persisted in the database) instead of a volatile in-process counter.

### T-DSK-032
- **Title:** IngestionOrchestrator.ResolveFiscalizationService uses blocking .GetAwaiter().GetResult() in async context
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Poor exception handling
- **Description:** `IngestionOrchestrator.ResolveFiscalizationService` (line 454) calls `_fiscalizationService.DisposeAsync().AsTask().GetAwaiter().GetResult()` — a synchronous blocking call on an async method. This is invoked from within the async `DoPollAndBufferAsync` call chain. While the current implementation likely completes synchronously (HTTP client disposal), if `DisposeAsync` ever awaits (e.g., flushing pending requests, closing TCP connections), this would block a thread pool thread and could deadlock in environments with a limited SynchronizationContext.
- **Evidence:**
  - `IngestionOrchestrator.cs:454` — `_fiscalizationService.DisposeAsync().AsTask().GetAwaiter().GetResult()`
  - `IngestionOrchestrator.cs:426` — method returns `IFiscalizationService?` (not async)
  - Called from `DoPollAndBufferAsync` (line 189) which is async
- **Impact:** Potential thread pool starvation or deadlock if `DisposeAsync` becomes truly async. Currently safe but fragile — a change in `AdvatecFiscalizationService.DisposeAsync` could break the poll loop.
- **Recommended Fix:** Make `ResolveFiscalizationService` async (`ResolveAsync`) and `await _fiscalizationService.DisposeAsync()`. Since it's only called from async code, this is a straightforward change.

### T-DSK-033
- **Title:** Dual status enums (TransactionStatus + SyncStatus) on BufferedTransaction require manual synchronization at every transition point
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Duplicated business logic
- **Description:** `BufferedTransaction` carries two parallel status fields: `TransactionStatus Status` (canonical lifecycle) and `SyncStatus SyncStatus` (edge upload tracking). Every status transition must update both fields in lockstep. `TransactionBufferManager` does this manually in 5 separate methods: `MarkUploadedAsync` (Pending→Synced), `MarkDuplicateConfirmedAsync` (→Duplicate), `MarkSyncedToOdooAsync` (→SyncedToOdoo), `DeadLetterExhaustedAsync` (→StalePending), `RevertStaleUploadedAsync` (→Pending). The mapping between SyncStatus and TransactionStatus transitions is encoded as inline comments (`// M-08: Update Status...`) rather than enforced by a state machine or mapping function.
- **Evidence:**
  - `TransactionBufferManager.cs:100-104` — `MarkUploadedAsync`: sets both SyncStatus and Status with inline comment
  - `TransactionBufferManager.cs:117-120` — `MarkDuplicateConfirmedAsync`: parallel status update
  - `TransactionBufferManager.cs:157-160` — `MarkSyncedToOdooAsync`: parallel update
  - `TransactionBufferManager.cs:309-313` — `DeadLetterExhaustedAsync`: parallel update
  - `TransactionBufferManager.cs:335-337` — `RevertStaleUploadedAsync`: parallel update
  - `BufferedTransaction.cs:49-52` — both Status and SyncStatus on same entity
- **Impact:** Adding a new status transition requires updating two fields in sync. If one is missed (e.g., a new method that updates SyncStatus but forgets Status), the cloud upload payload will carry an inconsistent lifecycle status. The bug would be silent — no compile-time or runtime check would catch it.
- **Recommended Fix:** Create a `TransactionStateTransition` helper that maps each `SyncStatus` to its corresponding `TransactionStatus` and provides a single `ApplyTransition(SyncStatus target)` method. Alternatively, derive `TransactionStatus` from `SyncStatus` at serialization time instead of storing both.

---

### T-DSK-034
- **Title:** PreAuthCommand is missing OdooOrderId, so three adapters overload FccCorrelationId with unrelated meaning
- **Module:** Pre-Authorization
- **Severity:** High
- **Category:** Architecture violations
- **Description:** `PreAuthCommand` carries `PreAuthId` and `FccCorrelationId`, but no explicit `OdooOrderId`. The submit path in `PreAuthHandler` creates the command with `FccCorrelationId: null`, then the Advatec, Petronite, and Radix adapters each repurpose `command.FccCorrelationId` as a stand-in for `OdooOrderId` in their active pre-auth trackers. The contract does not document that convention, and because the handler passes `null`, the adapters lose the very order identifier they are trying to preserve.
- **Evidence:**
  - `AdapterTypes.cs:24-43` — `PreAuthCommand` has no `OdooOrderId` field
  - `PreAuthHandler.cs:154-164` — submit path constructs the command with `FccCorrelationId: null`
  - `AdvatecAdapter.cs:255-259` — stores `OdooOrderId: command.FccCorrelationId`
  - `PetroniteAdapter.cs:475-479` — stores `OdooOrderId: command.FccCorrelationId`
  - `RadixAdapter.cs:568-573` — stores `OdooOrderId: command.FccCorrelationId`
- **Impact:** Adapter behavior depends on an undocumented misuse of a field with a different meaning, making transaction reconciliation fragile and duplicating the same workaround in three vendor implementations.
- **Recommended Fix:** Add explicit business-context fields such as `OdooOrderId` to `PreAuthCommand`, populate them from `OdooPreAuthRequest`, and remove the adapter-side overloading of `FccCorrelationId`.

---

### T-DSK-035
- **Title:** Desktop Pre-Authorization surface has no ViewModel or repository seam
- **Module:** Pre-Authorization
- **Severity:** Medium
- **Category:** Weak dependency injection usage
- **Description:** The desktop Pre-Authorization module does not follow the intended `Window -> ViewModel -> Service -> Repository -> Database/API` layering. `MainWindow` navigates directly to a lazily instantiated `PreAuthPage` in code-behind; the page itself is only placeholder text with no bindings or commands; and the only substantive service implementation (`PreAuthHandler`) talks straight to `AgentDbContext` instead of a dedicated query/repository abstraction. The result is a module with no testable UI flow and no reusable read model for the desktop screen.
- **Evidence:**
  - `MainWindow.axaml.cs:110-123` — code-behind directly creates `new PreAuthPage()`
  - `PreAuthPage.axaml:13-15` — page explicitly states active pre-auth/history support is deferred
  - `PreAuthPage.axaml.cs:5-8` — page only calls `InitializeComponent()`
  - `PreAuthHandler.cs:22-42` — service injects `AgentDbContext` directly, not a pre-auth repository/query service
  - `src/desktop-edge-agent/src/FccDesktopAgent.App/ViewModels` contains no PreAuth-specific view model
- **Impact:** The desktop pre-auth module has no real end-to-end UI trace to the service/data layers, which makes the screen untestable, prevents reuse of query logic, and leaves operators without a first-class desktop workflow.
- **Recommended Fix:** Introduce a `PreAuthPageViewModel` backed by explicit query/command services and a dedicated pre-auth repository, then bind the page to that ViewModel instead of keeping the module in window code-behind.

---

### T-DSK-036
- **Title:** Dashboard manual sync bypasses the CadenceController and calls the upload worker directly
- **Module:** Cloud Sync
- **Severity:** Medium
- **Category:** Architecture violations
- **Description:** The cloud upload path is documented as a cadence-controlled worker: `CloudUploadWorker` is meant to be called by `CadenceController` on internet-up ticks, with connectivity gating and sequencing enforced in one place. `DashboardPage` breaks that boundary by resolving the singleton `ICloudSyncService` directly from the service locator and invoking `UploadBatchAsync()` from UI code-behind. That gives the module two execution entry points for the same background workflow and leaves orchestration concerns leaking into the view layer.
- **Evidence:**
  - `CloudUploadWorker.cs:20-29` — class comment says the worker is called by `CadenceController` on each internet-up tick
  - `CadenceController.cs:185-205` — scheduler owns the normal cloud-upload dispatch path
  - `DashboardPage.axaml.cs:238-245` — UI code resolves `ICloudSyncService` and calls `UploadBatchAsync()` directly
  - `ServiceCollectionExtensions.cs:99-100` — `ICloudSyncService` is a singleton background worker instance, not a UI-specific command service
- **Impact:** Connectivity rules and sequencing assumptions are no longer centralized. Any future change to cloud-sync scheduling, throttling, or connectivity policy must now account for both the cadence loop and ad hoc UI callers.
- **Recommended Fix:** Introduce a thin application-service command for "request manual cloud sync" that remains owned by the orchestration layer. The dashboard should invoke that command and render a structured result instead of calling the worker directly.

---

### T-DSK-037
- **Title:** Monitoring & Diagnostics pages bypass the ViewModel and repository layers entirely
- **Module:** Monitoring & Diagnostics
- **Severity:** Medium
- **Category:** Business logic in UI layer
- **Description:** The Monitoring & Diagnostics cluster does not implement the intended `Window -> ViewModel -> Service -> Repository -> Database/API` layering. `MainWindow` navigates directly to `DashboardPage` and `LogsPage`; both pages resolve services from the composition-root service provider and execute orchestration/data-access logic in code-behind. `DashboardPage` directly touches connectivity, ingestion, cloud sync, EF Core, and file/drive inspection. `LogsPage` directly builds EF Core queries against `AgentDbContext`. There is no page-specific ViewModel or repository seam for either diagnostics surface.
- **Evidence:**
  - `MainWindow.axaml.cs:99-108` - navigation directly instantiates `new DashboardPage()` and `new LogsPage()`
  - `DashboardPage.axaml.cs:28-45` - constructor resolves `AgentAppContext.ServiceProvider` and starts its own timer
  - `DashboardPage.axaml.cs:95-183` - page queries `TransactionBufferManager`, `AgentDbContext`, and local storage metrics directly
  - `DashboardPage.axaml.cs:202-281` - manual FCC poll / cloud sync / archive operations are implemented in view code-behind
  - `LogsPage.axaml.cs:21-68` - page resolves the service provider and builds EF Core queries in the view
  - `src/desktop-edge-agent/src/FccDesktopAgent.App/ViewModels` contains only `MainWindowViewModel`, `SettingsViewModel`, `RelayCommand`, and `ViewModelBase`
- **Impact:** The module is hard to unit test, orchestration rules leak into the UI layer, and the requested end-to-end trace collapses into `Window -> code-behind -> DbContext/service` instead of the documented architecture.
- **Recommended Fix:** Add `DashboardPageViewModel` and `LogsPageViewModel` backed by application services/read models. Move query construction, manual command orchestration, and polling lifecycle out of code-behind.

---

### T-DSK-038
- **Title:** CleanupWorker reintroduces a second recurring scheduler outside the CadenceController
- **Module:** Monitoring & Diagnostics
- **Severity:** Medium
- **Category:** Architecture violations
- **Description:** The runtime architecture explicitly says there should be one cadence controller and no independent timer loops for recurring work. The implementation then registers `CleanupWorker` as a separate hosted service with its own startup delay and long-running `while`/`Task.Delay` loop. That splits scheduling responsibilities between `CadenceController` and another scheduler, which is the exact drift the architecture comments say to avoid.
- **Evidence:**
  - `CadenceController.cs:15-18` - class comment states `ONE cadence controller. No independent timer loops.`
  - `ServiceCollectionExtensions.cs:176-187` - comments say "Do not add independent timer loops here", then register both `CadenceController` and `CleanupWorker`
  - `CleanupWorker.cs:11-15` - worker is defined as a separate periodic background worker
  - `CleanupWorker.cs:31-52` - `ExecuteAsync()` runs its own delay loop outside the cadence scheduler
- **Impact:** Recurring work is no longer centralized. Future scheduling, observability, throttling, and shutdown behavior must be reasoned about across two different loops instead of one orchestration point.
- **Recommended Fix:** Fold cleanup dispatch into `CadenceController` as another coalesced task, or explicitly document cleanup as the sole exception and give it the same scheduling/observability contract as the cadence loop.

---

### T-DSK-039
- **Title:** Local API port is modeled in three different configuration paths with no single source of truth
- **Module:** Odoo Integration
- **Severity:** High
- **Category:** Architecture violations
- **Description:** The desktop Odoo integration splits the local API port across three independent models. Kestrel binds to `LocalApiOptions.Port` from raw host configuration, while the UI edits `SiteConfig.LocalApi.LocalhostPort`, and the rest of the app reads `AgentConfiguration.LocalApiPort`. These values are not wired together at runtime. The result is a split-brain configuration model where the UI and provisioning summary can advertise one port while the server listens on another.
- **Evidence:**
  - `LocalApiOptions.cs:13-14` — `LocalApiOptions.Port` defines the Kestrel listener port
  - `Program.cs:67` — Kestrel reads `builder.Configuration["LocalApi:Port"]`, not `AgentConfiguration.LocalApiPort`
  - `AgentConfiguration.cs:45-46` — app/runtime surfaces use a separate `LocalApiPort` property
  - `ConfigManager.cs:277-281` — cloud/UI config updates only `AgentConfiguration.LocalApiPort`
  - `ConfigSaveService.cs:56-57` — the configuration UI persists only `SiteConfig.LocalApi.LocalhostPort`
  - `SetupOrchestrator.cs:311` — provisioning summary reads `AgentConfiguration.LocalApiPort` for operator instructions
- **Impact:** Odoo operators and the desktop UI can be told to use a port that the embedded server never actually binds. Every future change to local API routing or restart behavior has to reason about multiple configuration stores instead of one contract.
- **Recommended Fix:** Collapse the listener port onto one source of truth. Prefer resolving Kestrel from the same effective `AgentConfiguration.LocalApiPort` that the UI and provisioning flow expose, and remove the duplicate `LocalApiOptions.Port` path or bind it from the resolved runtime configuration only.
