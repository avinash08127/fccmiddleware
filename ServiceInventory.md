# Service Inventory — FCC Middleware Platform

> Structural inventory of all services across all sub-systems.

---

## Desktop Edge Agent — Core Services

| Service | Interface | Responsibility |
|---------|-----------|---------------|
| `PlatformCredentialStore` | `ICredentialStore` | Secure credential storage via DPAPI / Keychain / libsecret |
| `LocalOverrideManager` | — | Read/write local config overrides (overrides.json) |
| `SiteDataManager` | — | Persist and load site data (products, pumps, nozzles) from JSON |
| `RegistrationManager` | `IRegistrationManager` | Persist/load device registration state; overlay identity onto config |
| `DeviceRegistrationService` | `IDeviceRegistrationService` | Call cloud registration API to register the device |
| `ConfigManager` | `IConfigManager` | Cloud config lifecycle, options change token source |
| `FccAdapterFactory` | `IFccAdapterFactory` | Create vendor-specific FCC adapter instances |
| `PumpStatusService` | `IPumpStatusService` | Pump status management with single-flight protection and stale cache |
| `IngestionOrchestrator` | `IIngestionOrchestrator` | Orchestrate FCC adapter → canonical → buffer pipeline |
| `PreAuthHandler` | `IPreAuthHandler` | Handle pre-authorization workflows and state machine |
| `CloudUploadWorker` | `ICloudSyncService` | Upload buffered transactions to cloud backend |
| `StatusPollWorker` | `ISyncedToOdooPoller` | Poll cloud for SYNCED_TO_ODOO status updates |
| `ConfigPollWorker` | `IConfigPoller` | Poll cloud for configuration changes |
| `TelemetryReporter` | `ITelemetryReporter` | Report telemetry snapshots to cloud |
| `DeviceTokenProvider` | `IDeviceTokenProvider` | Manage JWT device tokens and refresh cycle |
| `ErrorCountTracker` | `IErrorCountTracker` | Track consecutive error counts for exponential backoff |
| `VersionCheckService` | `IVersionChecker` | Validate agent version against cloud minimum version |
| `VelopackUpdateService` | `IUpdateService` | Check for and stage application updates via Velopack |
| `OdooWebSocketServer` | — | WebSocket server for Odoo POS backward compatibility |
| `OdooWsMessageHandler` | — | Handle incoming Odoo WebSocket messages |
| `TransactionBufferManager` | — | CRUD operations for locally buffered transactions |
| `IntegrityChecker` | — | Validate buffer database integrity |
| `CertificatePinValidator` | — (static) | TLS certificate pinning validation |
| `CloudUrlGuard` | — | Validate cloud endpoint URLs against expected patterns |
| `AgentConfigurationValidator` | — (static) | Validate runtime configuration completeness |
| `DeviceInfoProvider` | — (static) | Build registration request from hardware info |
| `WindowStateService` | — (static) | Window state management utility |

---

## Desktop Edge Agent — FCC Adapters

| Service | Interface | Responsibility |
|---------|-----------|---------------|
| `DomsAdapter` | `IFccAdapter` | DOMS FCC integration via HTTP/REST |
| `DomsJplAdapter` | `IFccAdapter`, `IFccConnectionLifecycle` | DOMS FCC integration via TCP/JPL binary protocol |
| `RadixAdapter` | `IFccAdapter` | Radix FDC integration via HTTP/XML |
| `PetroniteAdapter` | `IFccAdapter` | Petronite integration via REST/JSON + OAuth |
| `AdvatecAdapter` | `IFccAdapter` | Advatec integration via HTTP/JSON + webhooks |
| `AdvatecFiscalizationService` | `IFiscalizationService` | Advatec fiscalization (receipt/tax) operations |
| `AdvatecWebhookListener` | — | Listen for Advatec webhook callbacks |
| `AdvatecApiClient` | — | HTTP client for Advatec API calls |
| `PetroniteWebhookListener` | — | Listen for Petronite webhook callbacks |
| `PetroniteOAuthClient` | — | OAuth2 token management for Petronite |
| `PetroniteNozzleResolver` | — | Resolve nozzle-to-product mappings for Petronite |
| `RadixPushListener` | — | Listen for Radix push notifications |
| `RadixXmlParser` | — | Parse Radix XML responses |
| `RadixXmlBuilder` | — | Build Radix XML requests |
| `RadixSignatureHelper` | — | HMAC signature generation/validation for Radix |
| `RadixHeartbeat` | — | Radix connection keepalive |
| `JplTcpClient` | — | TCP client for DOMS JPL protocol |
| `JplFrameCodec` | — | JPL frame encoding/decoding |
| `JplHeartbeatManager` | — | JPL connection keepalive |
| `DomsCanonicalMapper` | — | Map DOMS transactions to canonical model |
| `DomsTransactionParser` | — | Parse DOMS transaction messages |
| `DomsPumpStatusParser` | — | Parse DOMS pump status messages |
| `DomsSupParamParser` | — | Parse DOMS supplemental parameter messages |
| `DomsPreAuthHandler` | — | Handle DOMS pre-authorization protocol |
| `DomsLogonHandler` | — | Handle DOMS logon protocol |

---

## Desktop Edge Agent — Background Workers

| Service | Base | Responsibility |
|---------|------|---------------|
| `CadenceController` | `BackgroundService` | Central recurring work scheduler (upload, config, telemetry, status) |
| `CleanupWorker` | `BackgroundService` | Purge expired buffered transactions |
| `ConnectivityManager` | `IHostedService` | Network reachability probe loop |

---

## Desktop Edge Agent — API Services

| Service | Responsibility |
|---------|---------------|
| `ApiKeyMiddleware` | Validate API key on incoming local REST requests |
| `TransactionEndpoints` | Local REST: query/manage buffered transactions |
| `PreAuthEndpoints` | Local REST: pre-authorization operations |
| `PumpStatusEndpoints` | Local REST: pump status queries |
| `StatusEndpoints` | Local REST: agent status/health |
| `OdooWsBridge` | Local REST: HTTP-to-WebSocket bridge for Odoo |

---

## Legacy DOMS Implementation — Services

| Service | Interface | Responsibility |
|---------|-----------|---------------|
| `TransactionService` | `ITransactionService` | Manage fuel transactions (parse, persist, sync) |
| `ParserService` | `IParserService` | Parse DPP hex messages from DOMS FCC |
| `LoggingService` | `ILoggingService` | Application logging to SQL Server |
| `ForecourtClient` | — (Singleton) | TCP client for DOMS FCC (connect, logon, poll, heartbeat) |
| `PopupService` | `BackgroundService` | Attendant monitoring UI + NotifyIcon tray |
| `WebSocketServerHostedService` | `IHostedService` | WebSocket server (Fleck) for POS/attendant clients |
| `DppHexParser` | — | Parse DPP hex-encoded messages |
| `DppMessageClassifier` | — | Classify incoming DPP message types |
| `Helper` | — | General utilities |
| `TableHelper` | — | Table formatting utilities |
| `FpMainStateHelper` | — | Fuel pump main state interpretation |
| `FpStatusParser` | — | Parse FP status responses |
| `FpSupTransBufStatusParser` | — | Parse FP supervised transaction buffer status |
| `FCCMiddleWareInstaller` | — | Installation/configuration utility |

---

## Cloud Backend — Application Services

| Service | Responsibility |
|---------|---------------|
| `IngestTransactionHandler` | Process and store incoming transactions from agents |
| `ReconciliationMatchingService` | Match FCC transactions with POS/ERP records |
| `GetReconciliationExceptionsHandler` | Query unmatched/exception reconciliation records |
| `ReviewReconciliationHandler` | Process reconciliation review decisions |
| `GenerateBootstrapTokenHandler` | Generate device bootstrap/provisioning tokens |
| `DecommissionDeviceHandler` | Decommission a registered device |
| `RefreshDeviceTokenHandler` | Refresh device JWT tokens |
| `SubmitTelemetryHandler` | Process telemetry snapshots from agents |
| `DeviceTokenService` | JWT token generation and validation |
| `PortalAccessResolver` | Resolve portal user permissions |
| `PortalCursor` | Cursor-based pagination for portal queries |
| `SiteFccConfigProvider` | Provide site FCC configuration |
| `RedisDeduplicationService` | Redis-based message deduplication |
| `RedisRegistrationThrottleService` | Rate-limit device registration attempts |
| `S3RawPayloadArchiver` | Archive raw payloads to S3 |
| `UnmatchedReconciliationWorker` | Background worker for unmatched reconciliation |

---

## VirtualLab — Services

| Service | Interface | Responsibility |
|---------|-----------|---------------|
| `VirtualLabManagementService` | `IVirtualLabManagementService` | Manage lab environments, sites, pumps |
| `FccProfileService` | `IFccProfileService` | Manage FCC simulator profiles |
| `ForecourtSimulationService` | `IForecourtSimulationService` | Simulate nozzle lift/hang, dispensing, transactions |
| `PreAuthSimulationService` | `IPreAuthSimulationService` | Simulate pre-authorization flows |
| `CallbackCaptureService` | `ICallbackCaptureService` | Capture and replay callback events |
| `CallbackDeliveryService` | — | Deliver callbacks to configured targets |
| `ObservabilityService` | `IObservabilityService` | Query transactions and event logs |
| `ScenarioService` | `IScenarioService` | Manage test scenarios |
| `ContractValidationService` | `IContractValidationService` | Validate API contracts |
| `VirtualLabTelemetry` | `IVirtualLabTelemetry` | Lab telemetry/metrics |
| `DiagnosticProbeService` | — | Health and performance diagnostics |
| `VirtualLabSeedService` | `IVirtualLabSeedService` | Seed initial test data |

---

## VirtualLab — FCC Simulators

| Simulator | Protocol | Responsibility |
|-----------|----------|---------------|
| `DomsJplSimulatorService` | TCP/JPL binary | Simulate DOMS JPL forecourt controller |
| `RadixSimulatorService` | HTTP/XML | Simulate Radix FDC forecourt controller |
| `PetroniteSimulatorService` | REST/JSON | Simulate Petronite forecourt controller |
| `AdvatecSimulatorService` | HTTP/JSON | Simulate Advatec EFD/VFD device |

---

## VirtualLab — Background Workers

| Worker | Responsibility |
|--------|---------------|
| `CallbackRetryWorker` | Retry failed callback deliveries |
| `PreAuthExpiryWorker` | Expire timed-out pre-auth sessions |
