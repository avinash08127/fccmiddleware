# Desktop Technical Findings

> Architecture, design, and code quality audit of the FCC Desktop Edge Agent.

---

## Application Shell Module

### T-DSK-001
- **Title:** MVVM violation — all UI logic lives in code-behind, ViewModel is bypassed
- **Module:** Application Shell
- **Severity:** High
- **Category:** Architecture violations
- **Description:** `MainWindow.axaml.cs` directly manipulates named AXAML controls (`StatusConnectivity`, `StatusDot`, `StatusBuffer`, `StatusLastSync`, `PageContent`) from code-behind instead of binding to ViewModel properties. Meanwhile, `MainWindowViewModel` contains bindable properties (`ConnectivityText`, `ConnectivityColor`, `BufferDepth`, `LastSyncText`) with full `INotifyPropertyChanged` support that are never consumed. The navigation also uses code-behind event handlers (`OnNavClicked`) instead of the ViewModel's `NavigateCommand`. This is a complete MVVM bypass — the View talks directly to services, skipping the ViewModel layer.
- **Evidence:**
  - `MainWindow.axaml.cs:112-121` — `NavigateTo()` sets `PageContent.Content` directly
  - `MainWindow.axaml.cs:172-173` — `StatusConnectivity.Text = text` direct control manipulation
  - `MainWindowViewModel.cs:55-89` — Bindable properties that are never bound
  - `MainWindow.axaml.cs:37-38` — services resolved directly from static `AgentAppContext`
- **Impact:** Untestable UI logic, duplicated business logic between code-behind and ViewModel, confusion for developers about which layer owns state.
- **Recommended Fix:** Choose one approach: (a) assign `DataContext = new MainWindowViewModel()` and convert code-behind to bindings/commands, or (b) remove the ViewModel and explicitly document code-behind as the chosen pattern.

---

### T-DSK-002
- **Title:** AgentAppContext is a static service locator anti-pattern
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Weak dependency injection usage
- **Description:** `AgentAppContext` exposes `ServiceProvider`, `WebApp`, and `Mode` as `static` mutable properties. Every component in the App project reaches into this class to resolve services: `MainWindow`, `ProvisioningWindow`, `SettingsPanel`, and `TrayIconManager` all call `AgentAppContext.ServiceProvider?.GetService<T>()`. This bypasses constructor injection, makes dependencies implicit, and prevents unit testing without global state setup.
- **Evidence:**
  - `AgentAppContext.cs:30` — `public static IServiceProvider? ServiceProvider { get; set; }`
  - `AgentAppContext.cs:35` — `public static WebApplication? WebApp { get; set; }`
  - `MainWindow.axaml.cs:37` — `_services = AgentAppContext.ServiceProvider`
  - `ProvisioningWindow.axaml.cs:47` — `var services = AgentAppContext.ServiceProvider`
  - `App.axaml.cs:113` — `var services = AgentAppContext.ServiceProvider`
- **Impact:** All UI components have hidden dependencies, preventing isolated testing and making dependency chains opaque.
- **Recommended Fix:** Pass `IServiceProvider` through Avalonia's built-in DI mechanisms or a window factory pattern that injects dependencies into window constructors.

---

### T-DSK-003
- **Title:** ProvisioningWindow is a fat code-behind with business logic in the UI layer
- **Module:** Application Shell
- **Severity:** High
- **Category:** Business logic in UI layer
- **Description:** `ProvisioningWindow.axaml.cs` is 804 lines and contains: cloud registration orchestration, manual config validation, URL format validation, device ID generation, connection testing (HTTP calls to cloud and FCC), credential store interaction, environment resolution, site data synchronization, and error-code-to-hint mapping. None of this is in a ViewModel or service — it's all in the AXAML code-behind.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:174-289` — `RegisterWithCodeAsync()` — 115 lines of registration business logic
  - `ProvisioningWindow.axaml.cs:291-347` — `ValidateManualConfigAsync()` — manual config validation
  - `ProvisioningWindow.axaml.cs:353-453` — `RegisterManualWithTokenAsync()` — second registration path
  - `ProvisioningWindow.axaml.cs:457-578` — `RunConnectionTestsAsync()` — HTTP connectivity tests
  - `ProvisioningWindow.axaml.cs:644-707` — `LaunchAgentAsync()` — host start + state persistence
- **Impact:** Registration and setup logic is completely untestable without instantiating an Avalonia window. Business rules are buried in UI code and cannot be reused by the headless Service host.
- **Recommended Fix:** Extract a `ProvisioningViewModel` or `SetupOrchestrator` service that encapsulates registration, validation, connection testing, and state persistence. The code-behind should only handle step panel visibility and button state.

---

### T-DSK-004
- **Title:** Duplicated RelayCommand implementations across ViewModels
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Duplicated business logic
- **Description:** There are two separate `RelayCommand` implementations: a generic `RelayCommand<T>` nested at the bottom of `MainWindowViewModel.cs`, and a non-generic `RelayCommand` as a private nested class in `SettingsViewModel.cs`. Both suppress `CanExecuteChanged` and have identical structure.
- **Evidence:**
  - `MainWindowViewModel.cs:157-173` — `public sealed class RelayCommand<T> : ICommand`
  - `SettingsViewModel.cs:145-152` — `private sealed class RelayCommand(Action execute) : ICommand`
- **Impact:** Minor code duplication. If command behavior needs to change (e.g., adding CanExecute support), it must be changed in multiple places.
- **Recommended Fix:** Create a single `RelayCommand` / `RelayCommand<T>` in `ViewModels/` or a shared infrastructure folder, used by all ViewModels.

---

### T-DSK-005
- **Title:** Program.cs uses synchronous blocking calls for async host shutdown
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Architecture violations
- **Description:** `webApp.StopAsync().GetAwaiter().GetResult()` and `Log.CloseAndFlushAsync().GetAwaiter().GetResult()` synchronously block on async operations. While this occurs in Program.cs after the Avalonia event loop has exited (reducing deadlock risk), it's still an anti-pattern that could deadlock if any `StopAsync` handler internally depends on a sync context.
- **Evidence:**
  - `Program.cs:147` — `webApp.StopAsync().GetAwaiter().GetResult()`
  - `Program.cs:163` — `webApp.StopAsync().GetAwaiter().GetResult()` (provisioning path)
  - `Program.cs:180` — `Log.CloseAndFlushAsync().GetAwaiter().GetResult()`
- **Impact:** Low risk of deadlock on shutdown; masks async exceptions.
- **Recommended Fix:** Use `await` in an `async Task Main(string[] args)` entry point, or accept the trade-off with a justification comment.

---

## Device Provisioning Module

### T-DSK-006
- **Title:** Registration logic duplicated between code and manual-token paths — ~200 lines repeated
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** Duplicated business logic
- **Description:** `RegisterWithCodeAsync()` (lines 174-289) and `RegisterManualWithTokenAsync()` (lines 353-453) contain nearly identical registration logic: CTS creation with 30s timeout, `DeviceRegistrationService.RegisterAsync()` call, result pattern matching (Success/Rejected/TransportError), error hint resolution, UI button state restoration, and exception handling. The only differences are: (a) status display method (`ShowRegStatus` vs `ShowManualStatus`), (b) button text on failure ("Register" vs "Next"), (c) `ReplaceCheck.IsChecked` (true vs hardcoded false), and (d) post-success actions (environment patching details). Any bug fix or behavior change (e.g., the CTS disposal issue in R-DSK-003) must be applied in both places.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:174-289` — `RegisterWithCodeAsync()` — 115 lines
  - `ProvisioningWindow.axaml.cs:353-453` — `RegisterManualWithTokenAsync()` — 100 lines
  - Both create CTS, call `RegisterAsync`, switch on result type, handle `OperationCanceledException` and generic `Exception`
- **Impact:** Bug fixes applied to one path but not the other. Maintenance burden doubled for registration behavior changes.
- **Recommended Fix:** Extract a shared `PerformRegistrationAsync(cloudUrl, siteCode, token, replace, statusAction, buttonText)` method or move to a ProvisioningViewModel/service.

---

### T-DSK-007
- **Title:** FCC connection test creates raw HttpClient bypassing DI-registered factory
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Weak dependency injection usage
- **Description:** In `RunConnectionTestsAsync()`, the cloud connectivity test correctly uses `_httpClientFactory?.CreateClient("cloud")` (with TLS enforcement and certificate pinning from the named client configuration). However, the FCC connectivity test creates a `new HttpClient { Timeout = TimeSpan.FromSeconds(10) }` directly, bypassing the DI-registered `IHttpClientFactory` and its "fcc" named client. This means the FCC test doesn't participate in connection pooling, doesn't apply any handler configuration, and may behave differently from the runtime FCC adapter.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:473-474` — cloud test: `_httpClientFactory?.CreateClient("cloud") ?? new HttpClient()` (uses DI)
  - `ProvisioningWindow.axaml.cs:501` — FCC test: `new HttpClient { Timeout = TimeSpan.FromSeconds(10) }` (bypasses DI)
  - `ServiceCollectionExtensions.cs:66-69` — "fcc" named client registered with 10-second timeout
- **Impact:** Minor inconsistency. FCC test behavior may diverge from the runtime adapter's HTTP behavior (e.g., if custom handlers or policies are added to the "fcc" client later).
- **Recommended Fix:** Use `_httpClientFactory?.CreateClient("fcc") ?? new HttpClient { Timeout = ... }` for the FCC test.

---

### T-DSK-008
- **Title:** SiteData sync called twice in code-based registration path
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Duplicated business logic
- **Description:** When registration succeeds via the code-based path, `SyncSiteData()` is called in two places: first inside `DeviceRegistrationService.HandleSuccessAsync()` (line 148), then again in `ProvisioningWindow.RegisterWithCodeAsync()` (line 251). Both calls pass the same `SiteConfig` object. The second call is redundant since the service already synced the data. The M-09 comment on both calls suggests they were added independently to ensure equipment data is available immediately.
- **Evidence:**
  - `DeviceRegistrationService.cs:147-148` — `_registrationManager.SyncSiteData(result.SiteConfig)` — first sync
  - `ProvisioningWindow.axaml.cs:250-253` — `_registrationManager.SyncSiteData(siteConfig)` — second sync (redundant)
  - Both have M-09 comments explaining the same rationale
- **Impact:** Double file write for site data JSON. No functional harm but indicates unclear ownership of the sync responsibility.
- **Recommended Fix:** Remove the sync call from `ProvisioningWindow.RegisterWithCodeAsync()` since `DeviceRegistrationService` already handles it. Document the responsibility in `IDeviceRegistrationService`.

---

### T-DSK-009
- **Title:** _isCodeMethod flag silently mutated as side effect in manual-token registration path
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Architecture violations
- **Description:** In `RegisterManualWithTokenAsync()`, after successful registration, `_isCodeMethod` is set to `true` (line 401) even though the user selected the manual configuration method. The comment explains this is to "skip duplicate state save" in `LaunchAgentAsync()`, since `DeviceRegistrationService.RegisterAsync` already persisted the state. This creates a hidden coupling: `LaunchAgentAsync` uses `_isCodeMethod` to decide whether to persist state, but the flag no longer reflects the user's actual method selection. If Step 1 or any navigation logic reads `_isCodeMethod` after this mutation, it will see an incorrect value.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:401` — `_isCodeMethod = true;` — mutated in manual path
  - `ProvisioningWindow.axaml.cs:653` — `if (!_isCodeMethod && _registrationManager is not null)` — guards state persistence in LaunchAgentAsync
  - `ProvisioningWindow.axaml.cs:68` — `_isCodeMethod = RadioCodeMethod.IsChecked == true` — original assignment from radio button
- **Impact:** Fragile coupling between steps. If navigation allows returning to Step 1 in the future, the radio button state and `_isCodeMethod` will be out of sync.
- **Recommended Fix:** Replace the `_isCodeMethod` guard in `LaunchAgentAsync` with a `_stateAlreadyPersisted` boolean flag that explicitly tracks whether registration state was saved by the service layer.
