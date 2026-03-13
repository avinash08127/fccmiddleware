# Desktop Module Clusters — FCC Desktop Edge Agent

> Architecture-level clustering of the FCC Desktop Edge Agent into business-capability modules.
> Source: `DesktopSystemInventory.md`, `WindowInventory.md`

---

## Module Summary

| Module | Windows / Pages | Services | Repositories | External Integrations |
|--------|:-:|:-:|:-:|:-:|
| Application Shell | 3 | 3 | 0 | 0 |
| Device Provisioning | 2 | 3 | 1 | 1 |
| Authentication & Security | 0 | 5 | 0 | 1 |
| Configuration | 2 | 4 | 1 | 1 |
| FCC Device Integration | 0 | 7 | 0 | 5 |
| Site Master Data | 0 | 1 | 2 | 0 |
| Transaction Management | 1 | 4 | 2 | 0 |
| Pre-Authorization | 1 | 2 | 1 | 1 |
| Cloud Sync | 0 | 3 | 0 | 2 |
| Monitoring & Diagnostics | 2 | 5 | 1 | 1 |
| Odoo Integration | 0 | 3 | 0 | 1 |
| Local REST API | 0 | 5 | 0 | 0 |
| VirtualLab (Test Simulator) | 10 | 0 | 0 | 0 |
| Legacy DOMS | 3 | 1 | 0 | 1 |
| **Totals** | **24** | **46** | **8** | **14** |

---

## Module Details

### 1. Application Shell

The outer frame of the desktop application — window lifecycle, tray icon, and auto-updates.

| Layer | Component |
|-------|-----------|
| Windows | `MainWindow`, `SplashWindow`, `App` (Application Root) |
| ViewModels | `MainWindowViewModel`, `ViewModelBase` |
| Services | `TrayIconManager`, `VelopackUpdateService` (`IUpdateService`), `WindowStateService` |
| Infrastructure | `AgentAppContext` (static shared state), `RelayCommand<T>` |

---

### 2. Device Provisioning

Device registration, decommission lifecycle, and first-time setup.

| Layer | Component |
|-------|-----------|
| Windows | `ProvisioningWindow`, `DecommissionedWindow` |
| Services | `RegistrationManager` (`IRegistrationManager`), `DeviceRegistrationService` (`IDeviceRegistrationService`), `DeviceInfoProvider` |
| Repositories | `RegistrationState` (file-persisted) |
| Models | `RegistrationState` (IsRegistered, IsDecommissioned) |
| External | Cloud Registration API |
| Startup Modes | `Normal` → dashboard, `Provisioning` → wizard, `Decommissioned` → blocked |

---

### 3. Authentication & Security

Credential storage, transport security, and API authentication.

| Layer | Component |
|-------|-----------|
| Services | `PlatformCredentialStore` (`ICredentialStore`), `CertificatePinValidator`, `CloudUrlGuard`, `ApiKeyMiddleware`, `DeviceTokenProvider` (`IDeviceTokenProvider`) |
| Attributes | `SensitiveDataAttribute`, `SensitiveDataDestructuringPolicy` |
| External | Cloud JWT token endpoint (device token refresh) |
| Exceptions | `RefreshTokenExpiredException` |

---

### 4. Configuration

Agent configuration, local overrides, and remote config sync.

| Layer | Component |
|-------|-----------|
| Pages | `ConfigurationPage`, `SettingsPanel` |
| ViewModels | `SettingsViewModel` |
| Services | `ConfigManager` (`IConfigManager`), `LocalOverrideManager`, `ConfigPollWorker` (`IConfigPoller`), `AgentConfigurationValidator` |
| Repositories | `AgentConfigRecord` (EF Core entity) |
| Models | `AgentConfiguration`, `DesktopFccRuntimeConfiguration`, `SiteConfig`, `WebSocketServerOptions`, `LocalApiOptions`, `CloudEnvironments` |
| Config Files | `appsettings.json`, `Directory.Build.props` |
| External | Cloud Config API (polled) |

---

### 5. FCC Device Integration

Vendor-specific adapters for communicating with physical Forecourt Controllers.

| Layer | Component |
|-------|-----------|
| Interfaces | `IFccAdapter`, `IFccAdapterFactory`, `IFccConnectionLifecycle`, `IFccEventListener`, `IFiscalizationService`, `IPreAuthMatcher` |
| Services | `FccAdapterFactory`, `DomsAdapter`, `DomsJplAdapter`, `RadixAdapter`, `PetroniteAdapter`, `AdvatecAdapter`, `PumpStatusService` (`IPumpStatusService`) |
| Support | `JplTcpClient`, `JplFrameCodec`, `JplHeartbeatManager`, `RadixPushListener`, `RadixXmlParser`, `RadixXmlBuilder`, `RadixSignatureHelper`, `PetroniteWebhookListener`, `PetroniteOAuthClient`, `PetroniteNozzleResolver`, `AdvatecWebhookListener`, `AdvatecApiClient`, `AdvatecFiscalizationService` |
| Models | `CanonicalTransaction`, `PumpStatus`, `CurrencyHelper`, `FccAdapterException` |
| External | DOMS (HTTP/REST), DOMS JPL (TCP/Binary), Radix FDC (HTTP/XML), Petronite (REST+OAuth), Advatec (HTTP+Webhooks) |

---

### 6. Site Master Data

Products, pumps, and nozzle mappings — the reference data layer.

| Layer | Component |
|-------|-----------|
| Services | `SiteDataManager` |
| Repositories | `NozzleMapping` (EF Core entity), `SiteDataSnapshot` (JSON-persisted) |
| Models | `LocalNozzle`, `LocalProduct`, `LocalPump`, `SiteInfo` |

---

### 7. Transaction Management

Ingestion pipeline, local buffering, integrity checking, and cleanup.

| Layer | Component |
|-------|-----------|
| Pages | `TransactionsPage` |
| Services | `IngestionOrchestrator` (`IIngestionOrchestrator`), `TransactionBufferManager`, `IntegrityChecker`, `CleanupWorker` |
| Repositories | `BufferedTransaction` (EF Core entity), `AgentDbContext` (SQLite/WAL) |
| Infrastructure | `AgentDbContextFactory`, `SqliteWalModeInterceptor`, `AgentDataDirectory` |
| Migrations | `InitialCreate`, `AddPreAuthFailureReason`, `AddWebSocketOdooFields` |

---

### 8. Pre-Authorization

Pre-auth request/response lifecycle tied to pump reservations.

| Layer | Component |
|-------|-----------|
| Pages | `PreAuthPage` |
| Services | `PreAuthHandler` (`IPreAuthHandler`), `PreAuthStateMachine` |
| Repositories | `PreAuthRecord` / `BufferedPreAuth` (EF Core entity) |
| Models | `OdooPreAuthRequest`, `PreAuthRecord` |
| External | Cloud Pre-Auth API |

---

### 9. Cloud Sync

Uploading buffered transactions and polling for status updates.

| Layer | Component |
|-------|-----------|
| Services | `CloudUploadWorker` (`ICloudSyncService`), `StatusPollWorker` (`ISyncedToOdooPoller`), `VersionCheckService` (`IVersionChecker`) |
| Models | `UploadModels.cs` (request/response DTOs), `SyncedStatusModels.cs` |
| Exceptions | `DeviceDecommissionedException` |
| HTTP Clients | `cloud` (30s timeout, TLS 1.2+, cert pinning) |
| External | Cloud Upload API, Odoo Status API |

---

### 10. Monitoring & Diagnostics

Connectivity monitoring, telemetry, error tracking, background scheduling, and operational dashboards.

| Layer | Component |
|-------|-----------|
| Pages | `DashboardPage`, `LogsPage` |
| Services | `ConnectivityManager` (`IConnectivityMonitor`), `CadenceController`, `TelemetryReporter` (`ITelemetryReporter`), `ErrorCountTracker` (`IErrorCountTracker`), `CleanupWorker` |
| Repositories | `AuditLogEntry` (EF Core entity) |
| Models | `TelemetryModels.cs` (payload DTOs) |
| External | Cloud Telemetry API |
| Note | `CadenceController` is the central scheduler — delegates to upload, config poll, telemetry, and status poll services |

---

### 11. Odoo Integration

WebSocket-based backward-compatibility layer for Odoo POS systems.

| Layer | Component |
|-------|-----------|
| Services | `OdooWebSocketServer`, `OdooWsMessageHandler`, `OdooWsBridge` (HTTP-to-WS bridge) |
| External | Odoo POS (WebSocket protocol) |

---

### 12. Local REST API

The local Kestrel API surface exposed to POS and other on-site consumers.

| Layer | Component |
|-------|-----------|
| Endpoints | `TransactionEndpoints`, `PreAuthEndpoints`, `PumpStatusEndpoints`, `StatusEndpoints`, `OdooWsBridge` |
| Infrastructure | `LocalApiOptions`, `ApiKeyMiddleware` |
| Host | ASP.NET Core Minimal API (Kestrel, port 8585) |

---

### 13. VirtualLab (Test Simulator)

Angular-based testing/simulation environment — separate from the production agent.

| Layer | Component |
|-------|-----------|
| Pages | `DashboardComponent`, `SitesComponent`, `FccProfilesComponent`, `ForecourtDesignerComponent`, `LiveConsoleComponent`, `PreauthConsoleComponent`, `TransactionsComponent`, `LogsComponent`, `ScenariosComponent`, `SettingsComponent` |
| Shell | `AppComponent`, `AppShellComponent` |

---

### 14. Legacy DOMS

WinForms-based predecessor implementation — kept for reference.

| Layer | Component |
|-------|-----------|
| Windows | `AttendantMonitorWindow`, `PopupService` (NotifyIcon), `NativePopup` (P/Invoke MessageBox) |
| Services | `ForecourtTcpWorker` |
| External | DOMS FCC (TCP) |

---

## Dependency Map (Module → Module)

```
Application Shell
 ├── Device Provisioning (startup routing)
 ├── Configuration (settings panel)
 └── Monitoring & Diagnostics (dashboard)

Device Provisioning
 └── Authentication & Security (cloud registration auth)

FCC Device Integration
 └── Site Master Data (nozzle/product lookups)

Transaction Management
 ├── FCC Device Integration (adapter → canonical pipeline)
 ├── Site Master Data (enrichment)
 └── Cloud Sync (upload trigger)

Pre-Authorization
 ├── FCC Device Integration (pump reservation)
 └── Cloud Sync (forward to cloud)

Cloud Sync
 └── Authentication & Security (device tokens)

Monitoring & Diagnostics
 ├── Cloud Sync (telemetry upload)
 └── Transaction Management (buffer stats)

Odoo Integration
 ├── Pre-Authorization (pre-auth requests)
 └── Local REST API (WS bridge endpoint)

Local REST API
 ├── Transaction Management (query buffer)
 ├── Pre-Authorization (pre-auth ops)
 ├── FCC Device Integration (pump status)
 └── Authentication & Security (API key validation)
```

---

## Test Coverage by Module

| Module | Test Files | Test Project |
|--------|:-:|------------|
| FCC Device Integration | 2 | Core.Tests/Adapter |
| Transaction Management | 4 | Core.Tests/Buffer |
| Configuration | 1 | Core.Tests/Config |
| Monitoring & Diagnostics | 3 | Core.Tests/Connectivity |
| Transaction Management (ingestion) | 2 | Core.Tests/Ingestion |
| Pre-Authorization | 1 | Core.Tests/PreAuth |
| Device Provisioning | 2 | Core.Tests/Registration |
| Authentication & Security | 3 | Core.Tests/Security |
| Cloud Sync | 6 | Core.Tests/Sync |
| Odoo Integration | 1 | Core.Tests/WebSocket |
| Cross-module (offline) | 1 | Integration.Tests |
| Performance | 4 | Benchmarks |
