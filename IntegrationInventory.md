# Integration Inventory — FCC Middleware Platform

> Structural inventory of all external integrations, messaging, background workers, device protocols, and file handling.

---

## 1. Device Integration (FCC / Forecourt Controllers)

### Desktop Edge Agent — FCC Adapters

| Vendor | Adapter Class | Protocol | Transport | Key Components |
|--------|--------------|----------|-----------|----------------|
| **DOMS (HTTP)** | `DomsAdapter` | REST/JSON | HTTP | Single-class adapter |
| **DOMS (JPL)** | `DomsJplAdapter` | JPL binary framing | TCP | `JplTcpClient`, `JplFrameCodec`, `JplHeartbeatManager`, `JplMessage` |
| **Radix FDC** | `RadixAdapter` | XML/HTTP | HTTP | `RadixPushListener`, `RadixXmlParser`, `RadixXmlBuilder`, `RadixSignatureHelper`, `RadixHeartbeat` |
| **Petronite** | `PetroniteAdapter` | REST/JSON + OAuth2 | HTTPS | `PetroniteWebhookListener`, `PetroniteOAuthClient`, `PetroniteNozzleResolver` |
| **Advatec** | `AdvatecAdapter` | REST/JSON + Webhooks | HTTP | `AdvatecWebhookListener`, `AdvatecApiClient`, `AdvatecFiscalizationService` |

### DOMS JPL Protocol Details

| Component | Purpose | Location |
|-----------|---------|----------|
| `JplTcpClient` | Persistent TCP connection to FCC | `Adapter/Doms/Jpl/JplTcpClient.cs` |
| `JplFrameCodec` | STX/ETX frame encoding/decoding | `Adapter/Doms/Jpl/JplFrameCodec.cs` |
| `JplHeartbeatManager` | Connection keepalive at configurable interval | `Adapter/Doms/Jpl/JplHeartbeatManager.cs` |
| `DomsLogonHandler` | FCC logon sequence | `Adapter/Doms/Protocol/DomsLogonHandler.cs` |
| `DomsPreAuthHandler` | Pre-auth protocol handling | `Adapter/Doms/Protocol/DomsPreAuthHandler.cs` |
| `DomsTransactionParser` | Transaction message parsing | `Adapter/Doms/Protocol/DomsTransactionParser.cs` |
| `DomsPumpStatusParser` | Pump status message parsing | `Adapter/Doms/Protocol/DomsPumpStatusParser.cs` |
| `DomsSupParamParser` | Supplemental parameter parsing | `Adapter/Doms/Protocol/DomsSupParamParser.cs` |
| `DomsCanonicalMapper` | DOMS → canonical transaction mapping | `Adapter/Doms/Mapping/DomsCanonicalMapper.cs` |

### Legacy DOMS Implementation — Direct FCC Connection

| Component | Protocol | Details |
|-----------|----------|---------|
| `ForecourtClient` | TCP/JPL | Direct TCP connection to DOMS FCC, STX(0x02)+JSON+ETX(0x03) framing |
| `Worker` | — | BackgroundService managing FCC connection lifecycle |
| `DppHexParser` | Hex | Parse DPP hex-encoded messages |
| `DppMessageClassifier` | — | Classify DPP message types |

### FCC Operations (Legacy DOMS)

| Operation | Description |
|-----------|-------------|
| `FcLogon` | Authenticate with FCC |
| `RequestFcPriceSet` | Request current fuel prices |
| `SendDynamicPriceUpdate` | Update fuel prices dynamically |
| `RequestAllPumpStatus` | Poll all pump statuses |
| `RequestLockAndReadTransaction` | Lock and read a transaction |
| `ClearSupervisedTransaction` | Clear supervised transaction |
| `EmergencyBlock` | Emergency stop (E-Stop) |
| `UnblockPump` | Cancel emergency stop |
| `SoftLock` / `Unlock` | Close / open pump |
| `GetFpStatus` | Get individual pump status |

---

## 2. Cloud Backend Integration

### Desktop Agent → Cloud API

| Integration | Protocol | Auth | Purpose |
|-------------|----------|------|---------|
| Device registration | HTTPS | Bootstrap token | Register device with cloud |
| Token refresh | HTTPS | Device refresh token | Refresh JWT access token |
| Transaction upload | HTTPS | Device JWT | Upload buffered transactions |
| Config poll | HTTPS | Device JWT | Poll for configuration changes |
| Telemetry report | HTTPS | Device JWT | Report telemetry snapshots |
| Status poll | HTTPS | Device JWT | Poll for SYNCED_TO_ODOO status |
| Version check | HTTPS | Device JWT | Validate agent version |

### Cloud API Endpoints (Agent-Facing)

| Controller | Purpose | Location |
|-----------|---------|----------|
| `AgentController` | Agent registration, config, token refresh | `Controllers/AgentController.cs` |
| `TransactionsController` | Transaction ingestion from agents | `Controllers/TransactionsController.cs` |

### Cloud API Endpoints (Portal-Facing)

| Controller | Purpose | Location |
|-----------|---------|----------|
| `AgentsController` | Agent management | `Controllers/AgentsController.cs` |
| `OpsTransactionsController` | Transaction queries | `Controllers/OpsTransactionsController.cs` |
| `OpsReconciliationController` | Reconciliation management | `Controllers/OpsReconciliationController.cs` |
| `SitesController` | Site configuration | `Controllers/SitesController.cs` |
| `DlqController` | Dead letter queue management | `Controllers/DlqController.cs` |
| `AdminSettingsController` | Admin settings | `Controllers/AdminSettingsController.cs` |

---

## 3. WebSocket Integration

### Desktop Agent — Odoo WebSocket Server

| Component | Protocol | Purpose | Location |
|-----------|----------|---------|----------|
| `OdooWebSocketServer` | WebSocket (ws:// or wss://) | Backward-compat integration with Odoo POS | `WebSocket/OdooWebSocketServer.cs` |
| `OdooWsMessageHandler` | WebSocket | Handle incoming Odoo messages | `WebSocket/OdooWsMessageHandler.cs` |
| `OdooWsBridge` | HTTP → WebSocket | Bridge REST calls to WebSocket | `Api/Endpoints/OdooWsBridge.cs` |

### WebSocket Message Models

| Model | Purpose | Location |
|-------|---------|----------|
| `PumpTransactionWsDto` | Transaction notification to POS | `WebSocket/OdooWsModels.cs` |
| `FuelPumpStatusWsDto` | Pump status notification to POS | `WebSocket/OdooWsModels.cs` |
| `WsErrorResponse` | Error response to POS | `WebSocket/OdooWsModels.cs` |

### Legacy DOMS — WebSocket Server

| Component | Library | Purpose | Location |
|-----------|---------|---------|----------|
| `WebSocketServerHostedService` | Fleck | WSS server for POS/attendant clients | `WebSocketServerHostedService.cs` |
| `FleckWebSocketAdapter` | Fleck | Timer-based pump status broadcast (3s interval) | `WebSocketServerHostedService.cs` |

---

## 4. VirtualLab — FCC Simulators

| Simulator | Protocol | Port Config | Purpose |
|-----------|----------|-------------|---------|
| `DomsJplSimulatorService` | TCP/JPL binary | Configurable | Simulate DOMS JPL FCC for agent testing |
| `RadixSimulatorService` | HTTP/XML dual-port | Configurable | Simulate Radix FDC for agent testing |
| `PetroniteSimulatorService` | REST/JSON | Configurable | Simulate Petronite FCC for agent testing |
| `AdvatecSimulatorService` | HTTP/JSON | Configurable | Simulate Advatec EFD/VFD for agent testing |

### VirtualLab — SignalR

| Hub | URL | Purpose |
|-----|-----|---------|
| `LabLiveHub` | `/hubs/live` | Real-time lab event broadcasting |

### VirtualLab — Callback System

| Component | Purpose |
|-----------|---------|
| `CallbackDeliveryService` | Deliver simulated callbacks to targets |
| `CallbackCaptureService` | Capture and replay callbacks |
| `CallbackRetryWorker` | Retry failed callback deliveries |

---

## 5. External Service Integrations

### Cloud Backend — External Services

| Service | Technology | Purpose | Location |
|---------|-----------|---------|----------|
| PostgreSQL | EF Core | Primary data store | `FccMiddlewareDbContext` |
| Redis | StackExchange.Redis | Deduplication, throttling, caching | `RedisDeduplicationService`, `RedisRegistrationThrottleService` |
| AWS S3 | AWS SDK | Raw payload archival | `S3RawPayloadArchiver` |

### Legacy DOMS — External Services

| Service | Technology | Purpose |
|---------|-----------|---------|
| SQL Server | ADO.NET | Primary data store |
| Odoo ERP | Implicit (via sync) | Transaction sync target |

### Portal — External Services

| Service | Technology | Purpose |
|---------|-----------|---------|
| Azure AD / Entra ID | MSAL | Portal user authentication |
| Cloud Backend API | HTTP | All data operations |

---

## 6. Background Workers & Scheduled Tasks

### Desktop Agent

| Worker | Schedule | Purpose |
|--------|----------|---------|
| `CadenceController` | Configurable intervals | Orchestrates: upload, config poll, telemetry, status poll |
| `CleanupWorker` | Periodic | Purge expired buffered transactions |
| `ConnectivityManager` | Continuous probe | Network reachability monitoring |

### Legacy DOMS

| Worker | Schedule | Purpose |
|--------|----------|---------|
| `Worker` | Continuous | FCC TCP connection, logon, pump status polling |
| `WebSocketServerHostedService` | Continuous | WSS server for POS clients |
| `PopupService` | 5-second refresh | Attendant monitor UI refresh |
| `FleckWebSocketAdapter` | 3-second broadcast | Pump status broadcast to WebSocket clients |
| `OdduSyncWorker` | Defined but not registered | Supervised buffer sync |
| `OfflineSyncWorker` | Defined but not registered | Offline transaction sync heartbeat |

### Cloud Backend

| Worker | Schedule | Purpose |
|--------|----------|---------|
| `UnmatchedReconciliationWorker` | Periodic | Process unmatched reconciliation records |

### VirtualLab

| Worker | Schedule | Purpose |
|--------|----------|---------|
| `CallbackRetryWorker` | Periodic | Retry failed callbacks |
| `PreAuthExpiryWorker` | Periodic | Expire timed-out pre-auth sessions |

---

## 7. File Handling

### Desktop Agent

| Area | Storage | Purpose |
|------|---------|---------|
| SQLite database | `%LOCALAPPDATA%/FccDesktopAgent/` | Transaction buffer, config, audit |
| Logs | `%LOCALAPPDATA%/FccDesktopAgent/logs/` | Rolling file logs (14-day retention) |
| Config overrides | `overrides.json` | Local configuration tuning |
| Site data | JSON file | Products, pumps, nozzles cache |
| Registration state | Local filesystem | Device identity persistence |
| Credentials | DPAPI / Keychain / libsecret | Device tokens, API keys |

### Cloud Backend

| Area | Storage | Purpose |
|------|---------|---------|
| Raw payloads | AWS S3 | Transaction payload archival |
| Database | PostgreSQL | Primary operational data |
| Cache | Redis | Deduplication, throttling |

### Legacy DOMS

| Area | Storage | Purpose |
|------|---------|---------|
| Database | SQL Server | All operational data |
| Logs | SQL Server (`Logs` table) | Application logs |
| TLS certificates | Local filesystem | WebSocket TLS |
| Configuration | `appsettings.json` | Application settings |

---

## 8. Security Integration

### Desktop Agent

| Component | Mechanism | Purpose |
|-----------|-----------|---------|
| `CertificatePinValidator` | TLS certificate pinning | Prevent MITM on cloud communication |
| `CloudUrlGuard` | URL allowlist | Restrict cloud endpoint URLs |
| `PlatformCredentialStore` | DPAPI/Keychain/libsecret | Secure credential storage |
| `ApiKeyMiddleware` | API key header | Local REST API authentication |
| `SensitiveDataDestructuringPolicy` | Serilog policy | Redact sensitive data in logs |
| HTTP client TLS | TLS 1.2+ enforced | All cloud communication |

### Cloud Backend

| Component | Mechanism | Purpose |
|-----------|-----------|---------|
| `DeviceTokenService` | JWT | Device authentication |
| `PortalAccessResolver` | RBAC | Portal user authorization |
| `RedisRegistrationThrottleService` | Rate limiting | Prevent registration abuse |

### Legacy DOMS

| Component | Mechanism | Purpose |
|-----------|-----------|---------|
| WebSocket TLS | Certificate (path + thumbprint) | Secure POS communication |
| FCC access code | `FcAccessCode` in config | FCC authentication |

---

## 9. Networking Summary

| Connection | Source | Target | Protocol | Auth |
|-----------|--------|--------|----------|------|
| FCC LAN | Desktop Agent | FCC Device | TCP/JPL, HTTP/XML, REST/JSON | Vendor-specific |
| Cloud upload | Desktop Agent | Cloud API | HTTPS (TLS 1.2+, cert pinning) | JWT device token |
| WebSocket (Odoo) | Odoo POS | Desktop Agent | ws:// or wss:// | None / TLS |
| Local API | POS / Integrator | Desktop Agent | HTTP (port 8585) | API key |
| Portal | Browser | Cloud API | HTTPS | MSAL / Azure AD |
| FCC LAN (legacy) | DOMS Worker | FCC Device | TCP (port 8888) | Access code |
| WSS (legacy) | POS | DOMS Worker | wss:// (Fleck) | TLS certificate |
