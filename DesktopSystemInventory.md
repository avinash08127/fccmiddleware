# Desktop System Inventory — FCC Desktop Edge Agent

> Auto-generated structural inventory. Not a bug analysis.

---

## 1. Application Overview

| Property | Value |
|----------|-------|
| **Application Name** | FCC Desktop Edge Agent |
| **Primary Framework** | Avalonia 11 (.NET 10) |
| **Architecture Pattern** | MVVM (Avalonia) + Clean Architecture (Core / Api / App / Service) |
| **Solution File** | `src/desktop-edge-agent/FccDesktopAgent.sln` |
| **Host Modes** | GUI (Avalonia desktop app) + Headless (Windows Service / systemd) |
| **Local Database** | SQLite via EF Core (WAL mode) |
| **Auto-Update** | Velopack |
| **Logging** | Serilog (console + rolling file) |
| **Local API** | ASP.NET Core Minimal API (Kestrel, port 8585) |

---

## 2. Solution Projects

| Project | Type | Purpose |
|---------|------|---------|
| `FccDesktopAgent.Core` | Class Library (.NET 10) | Adapters, buffer, config, connectivity, ingestion, pre-auth, registration, security, sync, WebSocket |
| `FccDesktopAgent.Api` | Class Library (.NET 10) | Minimal API endpoints (transactions, pre-auth, pump status, OdooWS bridge) |
| `FccDesktopAgent.App` | Avalonia Desktop App (.NET 10) | GUI host: windows, view models, tray icon, Velopack update service |
| `FccDesktopAgent.Service` | Worker Service (.NET 10) | Headless host: Windows Service / systemd, CLI registration |
| `FccDesktopAgent.Core.Tests` | xUnit Test | 26 test files covering adapters, buffer, config, connectivity, ingestion, pre-auth, registration, security, sync, WebSocket |
| `FccDesktopAgent.Api.Tests` | xUnit Test | API endpoint tests (scaffold only) |
| `FccDesktopAgent.Integration.Tests` | xUnit Test | Offline scenario stress tests |
| `FccDesktopAgent.Benchmarks` | BenchmarkDotNet | Replay throughput, memory footprint, transaction query benchmarks |

---

## 3. Dependency Injection Configuration

DI is layered. Both host modes (`App` and `Service`) call the same `AddAgentCore()` and `AddAgentApi()` extension methods.

### Core Services (`ServiceCollectionExtensions.AddAgentCore`)

| Registration | Interface | Implementation | Lifetime |
|-------------|-----------|----------------|----------|
| Credential store | `ICredentialStore` | `PlatformCredentialStore` | Singleton |
| Local overrides | — | `LocalOverrideManager` | Singleton |
| Site data | — | `SiteDataManager` | Singleton |
| Registration | `IRegistrationManager` | `RegistrationManager` | Singleton |
| Device registration | `IDeviceRegistrationService` | `DeviceRegistrationService` | Singleton |
| Configuration | `IConfigManager` | `ConfigManager` | Singleton |
| Configuration binding | `IOptionsChangeTokenSource<AgentConfiguration>` | `ConfigManager` | Singleton |
| FCC adapter factory | `IFccAdapterFactory` | `FccAdapterFactory` | Singleton |
| Pump status | `IPumpStatusService` | `PumpStatusService` | Singleton |
| Ingestion | `IIngestionOrchestrator` | `IngestionOrchestrator` | Singleton |
| Cloud upload | `ICloudSyncService` | `CloudUploadWorker` | Singleton |
| Odoo status poll | `ISyncedToOdooPoller` | `StatusPollWorker` | Singleton |
| Config poll | `IConfigPoller` | `ConfigPollWorker` | Singleton |
| Error tracking | `IErrorCountTracker` | `ErrorCountTracker` | Singleton |
| Telemetry | `ITelemetryReporter` | `TelemetryReporter` | Singleton |
| Version check | `IVersionChecker` | `VersionCheckService` | Singleton |
| Device token | `IDeviceTokenProvider` | `DeviceTokenProvider` | Singleton |
| WebSocket server | — | `OdooWebSocketServer` | Singleton |
| Pre-auth handler | `IPreAuthHandler` | `PreAuthHandler` | Scoped |
| Buffer manager | — | `TransactionBufferManager` | Scoped |
| Integrity checker | — | `IntegrityChecker` | Scoped |

### Database Services (`AddAgentDatabase`)

| Registration | Implementation | Lifetime |
|-------------|----------------|----------|
| `AgentDbContext` | EF Core SQLite (WAL mode) | Scoped |

### Hosted Services (`AddAgentBackgroundWorkers` + `AddAgentConnectivity`)

| Hosted Service | Purpose |
|---------------|---------|
| `ConnectivityManager` | Network probe loop (also `IConnectivityMonitor`) |
| `CadenceController` | Central recurring work scheduler — delegates to upload, config poll, telemetry, status poll |
| `CleanupWorker` | Purges expired buffered transactions |

### API Services (`AddAgentApi`)

| Registration | Implementation |
|-------------|----------------|
| `LocalApiOptions` | Bound from config |
| `ApiKeyMiddleware` | API key validation |

### App-Only Services (GUI host)

| Registration | Interface | Implementation |
|-------------|-----------|----------------|
| Auto-update | `IUpdateService` | `VelopackUpdateService` |

### Named HTTP Clients

| Name | Timeout | Purpose |
|------|---------|---------|
| `fcc` | 10 seconds | LAN calls to physical FCC devices |
| `cloud` | 30 seconds | Internet calls to cloud backend (TLS 1.2+, certificate pinning) |

---

## 4. UI Structure (Avalonia)

### Windows

| Window | Class | File |
|--------|-------|------|
| Main Window | `MainWindow` | `Views/MainWindow.axaml` |
| Splash Window | `SplashWindow` | `Views/SplashWindow.axaml` |
| Provisioning Window | `ProvisioningWindow` | `Views/ProvisioningWindow.axaml` |
| Decommissioned Window | `DecommissionedWindow` | `Views/DecommissionedWindow.axaml` |

### Pages (UserControls)

| Page | Class | File |
|------|-------|------|
| Dashboard | `DashboardPage` | `Views/Pages/DashboardPage.axaml` |
| Configuration | `ConfigurationPage` | `Views/Pages/ConfigurationPage.axaml` |
| Transactions | `TransactionsPage` | `Views/Pages/TransactionsPage.axaml` |
| Pre-Auth | `PreAuthPage` | `Views/Pages/PreAuthPage.axaml` |
| Logs | `LogsPage` | `Views/Pages/LogsPage.axaml` |

### Panels (UserControls)

| Panel | Class | File |
|-------|-------|------|
| Settings Panel | `SettingsPanel` | `Views/SettingsPanel.axaml` |

### Other UI Components

| Component | Class | Purpose |
|-----------|-------|---------|
| Tray Icon Manager | `TrayIconManager` | System tray icon management |
| App Context | `AgentAppContext` | Static shared state (ServiceProvider, WebApp, StartupMode) |
| Window State Service | `WindowStateService` | Static window state utility |

---

## 5. Presentation Layer (View Models)

| ViewModel | Base | File |
|-----------|------|------|
| `ViewModelBase` | `INotifyPropertyChanged` | `ViewModels/ViewModelBase.cs` |
| `MainWindowViewModel` | `ViewModelBase`, `IDisposable` | `ViewModels/MainWindowViewModel.cs` |
| `SettingsViewModel` | `ViewModelBase` | `ViewModels/SettingsViewModel.cs` |

Command infrastructure: `RelayCommand<T>` (ICommand implementation) defined in `MainWindowViewModel.cs`.

---

## 6. FCC Adapter Layer (Device Integration)

### Adapter Interfaces

| Interface | Purpose |
|-----------|---------|
| `IFccAdapter` | Core adapter contract (connect, poll, subscribe) |
| `IFccAdapterFactory` | Creates vendor-specific adapter instances |
| `IFccConnectionLifecycle` | Connection lifecycle management |
| `IFccEventListener` | Event callback handler |
| `IFiscalizationService` | Fiscalization (receipt/tax) operations |
| `IPreAuthMatcher` | Pre-authorization matching logic |

### Adapter Implementations

| Adapter | Vendor | Protocol | Key Files |
|---------|--------|----------|-----------|
| `DomsAdapter` | DOMS | HTTP/REST | `Adapter/Doms/DomsAdapter.cs` |
| `DomsJplAdapter` | DOMS JPL | TCP/Binary (JPL framing) | `Adapter/Doms/DomsJplAdapter.cs`, `Jpl/JplTcpClient.cs`, `Jpl/JplFrameCodec.cs`, `Jpl/JplHeartbeatManager.cs` |
| `RadixAdapter` | Radix FDC | HTTP/XML | `Adapter/Radix/RadixAdapter.cs`, `RadixPushListener.cs`, `RadixXmlParser.cs`, `RadixXmlBuilder.cs`, `RadixSignatureHelper.cs` |
| `PetroniteAdapter` | Petronite | REST/JSON + OAuth | `Adapter/Petronite/PetroniteAdapter.cs`, `PetroniteWebhookListener.cs`, `PetroniteOAuthClient.cs`, `PetroniteNozzleResolver.cs` |
| `AdvatecAdapter` | Advatec | HTTP/JSON + Webhooks | `Adapter/Advatec/AdvatecAdapter.cs`, `AdvatecWebhookListener.cs`, `AdvatecApiClient.cs`, `AdvatecFiscalizationService.cs` |

### Common Adapter Components

| Component | Purpose |
|-----------|---------|
| `FccAdapterFactory` | Creates the correct adapter from `FccVendor` enum |
| `CanonicalTransaction` | Vendor-neutral transaction model |
| `PumpStatus` / `PumpStatusService` | Pump state management with stale cache |
| `PreAuthRecord` | Pre-authorization record model |
| `CurrencyHelper` | Currency conversion utilities |
| `FccAdapterException` | Adapter-specific exception type |

---

## 7. Data Layer

### Database

| Component | Type | Details |
|-----------|------|---------|
| `AgentDbContext` | EF Core DbContext | SQLite database in WAL mode |
| `AgentDbContextFactory` | Design-time factory | For EF migrations |
| `SqliteWalModeInterceptor` | DbConnectionInterceptor | Enables WAL mode on connection open |

### Entities

| Entity | Purpose |
|--------|---------|
| `BufferedTransaction` | Locally buffered fuel transactions |
| `PreAuthRecord` (BufferedPreAuth) | Buffered pre-authorization records |
| `AgentConfigRecord` | Persisted agent configuration |
| `AuditLogEntry` | Local audit log entries |
| `NozzleMapping` | Nozzle-to-product mappings |

### Migrations

| Migration | Description |
|-----------|-------------|
| `InitialCreate` | Initial schema |
| `AddPreAuthFailureReason` | Adds failure reason to pre-auth records |
| `AddWebSocketOdooFields` | Adds WebSocket/Odoo-related fields |

### Buffer Management

| Component | Purpose |
|-----------|---------|
| `TransactionBufferManager` | CRUD for buffered transactions |
| `IntegrityChecker` | Validates buffer integrity |
| `AgentDataDirectory` | Cross-platform data directory resolution |

---

## 8. Sync / Cloud Communication Layer

| Component | Interface | Purpose |
|-----------|-----------|---------|
| `CloudUploadWorker` | `ICloudSyncService` | Uploads buffered transactions to cloud backend |
| `ConfigPollWorker` | `IConfigPoller` | Polls cloud for configuration changes |
| `StatusPollWorker` | `ISyncedToOdooPoller` | Polls for SYNCED_TO_ODOO status updates |
| `TelemetryReporter` | `ITelemetryReporter` | Reports telemetry snapshots to cloud |
| `DeviceTokenProvider` | `IDeviceTokenProvider` | Manages JWT device tokens (refresh cycle) |
| `ErrorCountTracker` | `IErrorCountTracker` | Tracks consecutive error counts for backoff |
| `VersionCheckService` | `IVersionChecker` | Validates agent version against cloud minimum |

### Sync Models

| File | Models |
|------|--------|
| `Sync/Models/UploadModels.cs` | Upload request/response DTOs |
| `Sync/Models/TelemetryModels.cs` | Telemetry payload DTOs |
| `Sync/Models/SyncedStatusModels.cs` | Synced status DTOs |

### Exceptions

| Exception | Purpose |
|-----------|---------|
| `DeviceDecommissionedException` | Thrown when cloud reports device decommissioned |
| `RefreshTokenExpiredException` | Thrown when JWT refresh token expires |

---

## 9. Background Processing

| Worker | Base | Purpose |
|--------|------|---------|
| `CadenceController` | `BackgroundService` | Central scheduler — orchestrates upload, config poll, telemetry, status poll at configurable intervals |
| `CleanupWorker` | `BackgroundService` | Purges old buffered transactions past retention period |
| `ConnectivityManager` | `IHostedService` | Network reachability probe loop |

The `CadenceController` delegates to:
- `ICloudSyncService` (upload)
- `IConfigPoller` (config)
- `ITelemetryReporter` (telemetry)
- `ISyncedToOdooPoller` (Odoo status)

---

## 10. Security

| Component | Purpose |
|-----------|---------|
| `PlatformCredentialStore` | DPAPI (Windows) / Keychain (macOS) / libsecret (Linux) credential storage |
| `CertificatePinValidator` | TLS certificate pinning for cloud communication |
| `CloudUrlGuard` | Validates cloud URLs against expected patterns |
| `SensitiveDataAttribute` | Marks properties for redaction in logs |
| `SensitiveDataDestructuringPolicy` | Serilog destructuring policy for sensitive data |
| `ApiKeyMiddleware` | API key authentication for local REST API |

---

## 11. WebSocket Layer

| Component | Purpose |
|-----------|---------|
| `OdooWebSocketServer` | WebSocket server for backward-compat Odoo POS integration |
| `OdooWsMessageHandler` | Handles incoming WebSocket messages |
| `OdooWsBridge` (API endpoint) | HTTP-to-WebSocket bridge endpoint |

---

## 12. Configuration

### Configuration Classes

| Class | Purpose |
|-------|---------|
| `AgentConfiguration` | Main agent config (bound from `Agent` section) |
| `AgentConfigurationValidator` | Validates config completeness |
| `ConfigManager` | Manages config lifecycle, implements IOptionsChangeTokenSource |
| `LocalOverrideManager` | Manages local override JSON file |
| `CloudEnvironments` | Cloud environment URL definitions |
| `DesktopFccRuntimeConfiguration` | Runtime FCC configuration |
| `SiteConfig` | Site configuration model (identity, pumps, products) |
| `WebSocketServerOptions` | WebSocket server configuration |
| `LocalApiOptions` | Local API port/key configuration |

### Configuration Files

| File | Purpose |
|------|---------|
| `FccDesktopAgent.App/appsettings.json` | GUI host configuration |
| `Directory.Build.props` | Shared MSBuild properties |

---

## 13. Registration & Provisioning

| Component | Purpose |
|-----------|---------|
| `RegistrationManager` | Persists and loads registration state |
| `RegistrationState` | Current device registration state (IsRegistered, IsDecommissioned) |
| `DeviceRegistrationService` | Calls cloud registration API |
| `DeviceInfoProvider` | Builds registration request from device hardware info |

### Startup Modes

| Mode | Trigger | Behavior |
|------|---------|----------|
| `Normal` | Device registered | Start all services, show dashboard |
| `Provisioning` | Device not registered | Show provisioning wizard |
| `Decommissioned` | Device decommissioned | Show decommission window, block all services |

---

## 14. Local REST API Endpoints

| Endpoint | File | Purpose |
|----------|------|---------|
| Transactions | `Endpoints/TransactionEndpoints.cs` | Query/manage buffered transactions |
| Pre-Auth | `Endpoints/PreAuthEndpoints.cs` | Pre-authorization operations |
| Pump Status | `Endpoints/PumpStatusEndpoints.cs` | Pump status queries |
| Status | `Endpoints/StatusEndpoints.cs` | Agent status/health |
| Odoo WS Bridge | `Endpoints/OdooWsBridge.cs` | WebSocket bridge for Odoo POS |
| Health | Built-in | `GET /health` health check |

---

## 15. Master Data

| Component | Purpose |
|-----------|---------|
| `SiteDataManager` | Persists products/pumps/nozzles from config to local JSON |
| `LocalNozzle` | Nozzle model |
| `LocalProduct` | Product model |
| `LocalPump` | Pump model |
| `SiteDataSnapshot` | Point-in-time site data snapshot |
| `SiteInfo` | Site identification info |

---

## 16. Ingestion Pipeline

| Component | Interface | Purpose |
|-----------|-----------|---------|
| `IngestionOrchestrator` | `IIngestionOrchestrator` | Orchestrates FCC adapter → canonical → buffer pipeline |
| `PreAuthHandler` | `IPreAuthHandler` | Handles pre-authorization workflows |
| `PreAuthStateMachine` | — | State machine for pre-auth lifecycle |
| `OdooPreAuthRequest` | — | Pre-auth request model from Odoo |

---

## 17. Test Coverage Map

| Test Project | Area | Test Count |
|-------------|------|-----------|
| Core.Tests/Adapter | DOMS adapter, Radix signature | 2 files |
| Core.Tests/Buffer | Buffered transactions, cleanup, integrity, manager | 4 files |
| Core.Tests/Config | Config manager | 1 file |
| Core.Tests/Connectivity | Cadence controller, connectivity manager | 3 files |
| Core.Tests/Ingestion | Ingestion orchestrator, manual pull | 2 files |
| Core.Tests/PreAuth | Pre-auth handler | 1 file |
| Core.Tests/Registration | Device registration, registration manager | 2 files |
| Core.Tests/Security | Cross-platform, credential store, hardening | 3 files |
| Core.Tests/Sync | Cloud upload, config poll, device token refresh, error count, status poll, telemetry | 6 files |
| Core.Tests/WebSocket | Odoo WS models | 1 file |
| Integration.Tests | Offline scenario stress | 1 file |
| Benchmarks | Throughput, memory, queries | 4 files |
