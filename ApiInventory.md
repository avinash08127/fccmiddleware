# API Inventory — FCC Edge Agent (Android)

**Project**: `fcc-edge-agent`
**Last scanned**: 2026-03-13

---

## 1. Cloud API (Outbound — Ktor Client → Cloud Backend)

Cloud API calls are made by `CloudApiClient` using Ktor Client + OkHttp engine.
Base URL is configurable via site config (`sync.cloudBaseUrl`).
All requests use Bearer JWT auth via `DeviceTokenProvider`.

| API | Service | Endpoint | Method | Caller |
|-----|---------|----------|--------|--------|
| register | CloudApiClient | `/api/v1/agent/register` | POST | ProvisioningActivity |
| uploadTransactions | CloudApiClient | `/api/v1/transactions/upload` | POST | CloudUploadWorker |
| getConfig | CloudApiClient | `/api/v1/agent/config` | GET | ConfigPollWorker |
| refreshToken | CloudApiClient | `/api/v1/agent/token/refresh` | POST | KeystoreDeviceTokenProvider |
| reportTelemetry | CloudApiClient | `/api/v1/agent/telemetry` | POST | TelemetryReporter |
| forwardPreAuth | CloudApiClient | `/api/v1/preauth/forward` | POST | PreAuthCloudForwardWorker |
| checkVersion | CloudApiClient | `/api/v1/agent/version-check` | GET | CadenceController |

### Cloud API Request/Response Models (`sync/CloudApiModels.kt`)

| Model | Direction | Used By |
|-------|-----------|---------|
| `CloudUploadRequest` | Request | uploadTransactions |
| `CloudTransactionDto` | Request (nested) | uploadTransactions |
| `CloudUploadResponse` | Response | uploadTransactions |
| `CloudUploadAccepted` | Response (nested) | uploadTransactions |
| `CloudUploadDuplicate` | Response (nested) | uploadTransactions |
| `CloudUploadRejected` | Response (nested) | uploadTransactions |
| `CloudConfigResponse` | Response | getConfig |
| `RegistrationRequest` | Request | register |
| `RegistrationResponse` | Response | register |
| `TokenRefreshRequest` | Request | refreshToken |
| `TokenRefreshResponse` | Response | refreshToken |
| `TelemetryPayload` | Request | reportTelemetry |
| `TelemetryMetrics` | Request (nested) | reportTelemetry |
| `PreAuthForwardRequest` | Request | forwardPreAuth |
| `PreAuthForwardResponse` | Response | forwardPreAuth |
| `VersionCheckResponse` | Response | checkVersion |

---

## 2. Local REST API (Inbound — Ktor Server CIO, port 8585)

Embedded HTTP server consumed by Odoo POS and other LAN clients.
Auth: Localhost bypass; LAN requires `X-Api-Key` header.
Rate limiting and correlation ID middleware included.

| API | Service | Endpoint | Method | Route File |
|-----|---------|----------|--------|------------|
| getStatus | LocalApiServer | `/api/v1/status` | GET | StatusRoutes.kt |
| listTransactions | LocalApiServer | `/api/v1/transactions` | GET | TransactionRoutes.kt |
| getTransaction | LocalApiServer | `/api/v1/transactions/{id}` | GET | TransactionRoutes.kt |
| acknowledgeTransactions | LocalApiServer | `/api/v1/transactions/acknowledge` | POST | TransactionRoutes.kt |
| pullTransactions | LocalApiServer | `/api/v1/transactions/pull` | POST | TransactionRoutes.kt |
| submitPreAuth | LocalApiServer | `/api/v1/preauth` | POST | PreAuthRoutes.kt |
| cancelPreAuth | LocalApiServer | `/api/v1/preauth/cancel` | POST | PreAuthRoutes.kt |
| getPumpStatus | LocalApiServer | `/api/v1/pump-status` | GET | PumpStatusRoutes.kt |

### Local API Request/Response Models (`api/ApiModels.kt`)

| Model | Direction | Used By |
|-------|-----------|---------|
| `AgentStatusResponse` | Response | getStatus |
| `LocalTransaction` | Response (nested) | listTransactions, getTransaction |
| `TransactionListResponse` | Response | listTransactions |
| `BatchAcknowledgeRequest` | Request | acknowledgeTransactions |
| `BatchAcknowledgeResponse` | Response | acknowledgeTransactions |
| `ManualPullRequest` | Request | pullTransactions |
| `ManualPullResponse` | Response | pullTransactions |
| `PumpStatusResponse` | Response | getPumpStatus |
| `PumpStatus` | Response (nested) | getPumpStatus |
| `CancelPreAuthRequest` | Request | cancelPreAuth |
| `CancelPreAuthResponse` | Response | cancelPreAuth |
| `ErrorResponse` | Response | All error responses |

---

## 3. WebSocket API (Inbound — Ktor Server WebSocket)

WebSocket server for real-time Odoo POS integration.
Managed by `OdooWebSocketServer` + `OdooWsMessageHandler`.

| Message Type | Direction | Purpose |
|-------------|-----------|---------|
| `latest` | Client → Server | Request latest unsynced transactions |
| `all` | Client → Server | Request all transactions |
| `manager_update` | Client → Server | Manager acknowledges transaction |
| `attendant_update` | Client → Server | Attendant acknowledges transaction |
| `FuelPumpStatus` | Client → Server | Request pump status |
| `fp_unblock` | Client → Server | Unblock a pump |
| Push broadcast | Server → Client | Real-time transaction/pump updates |

### WebSocket Models (`websocket/OdooWsModels.kt`)

| Model | Direction |
|-------|-----------|
| `PumpTransactionWsDto` | Server → Client |
| `FuelPumpStatusWsDto` | Server → Client |
| `OdooWsInboundMessage` | Client → Server |
| `OdooWsOutboundMessage` | Server → Client |

---

## 4. FCC Adapter Protocols (LAN — Edge Agent → FCC Hardware)

These are vendor-specific protocol interactions, not REST APIs.

### 4.1 DOMS (TCP/JPL Binary Protocol)

| Operation | Protocol Message | Direction |
|-----------|-----------------|-----------|
| Logon | `FcLogon_req` / `FcLogon_resp` | Agent → FCC |
| Heartbeat | `Heartbeat_req` / `Heartbeat_resp` | Agent ↔ FCC |
| Fetch Transactions | `GetSvTransaction_req` / `GetSvTransaction_resp` | Agent → FCC |
| Pre-Auth | `authorize_Fp_req` / `authorize_Fp_resp` | Agent → FCC |
| Cancel Pre-Auth | `deauthorize_Fp_req` / `deauthorize_Fp_resp` | Agent → FCC |
| Pump Status | `GetFpMainState_req` / `GetFpMainState_resp` | Agent → FCC |
| Sup Params | `GetSupParam_req` / `GetSupParam_resp` | Agent → FCC |
| Pump Status Push | `FpMainStateChange_ntf` | FCC → Agent |
| Transaction Push | `SvTransactionAvailable_ntf` | FCC → Agent |
| Fuelling Update | `FuellingUpdate_ntf` | FCC → Agent |

### 4.2 Radix (XML over TCP)

| Operation | CMD_CODE | Direction |
|-----------|----------|-----------|
| Fetch Transactions | `100` (GET_DATA) | Agent → FCC |
| Acknowledge | `201` (ACK) | Agent → FCC |
| Pre-Auth | `200` (AUTH_DATA with AUTH=TRUE) | Agent → FCC |
| Cancel Pre-Auth | `200` (AUTH_DATA with AUTH=FALSE) | Agent → FCC |
| Heartbeat | TCP connect probe | Agent → FCC |
| Push Listener | Unsolicited `100` response | FCC → Agent |

### 4.3 Petronite (REST + OAuth2)

| Operation | HTTP | Endpoint | Direction |
|-----------|------|----------|-----------|
| Get Token | POST | OAuth2 token endpoint | Agent → FCC |
| Fetch Transactions | GET | `/transactions` | Agent → FCC |
| Pre-Auth | POST | `/preauth` | Agent → FCC |
| Cancel Pre-Auth | POST | `/{orderId}/cancel` | Agent → FCC |
| Heartbeat | GET | Health endpoint | Agent → FCC |
| Webhook | POST | Local webhook listener | FCC → Agent |

### 4.4 Advatec (REST + Webhook)

| Operation | HTTP | Endpoint | Direction |
|-----------|------|----------|-----------|
| Submit Customer | POST | `/api/Z_Report` or similar | Agent → EFD |
| Heartbeat | TCP connect probe | Agent → EFD |
| Receipt Webhook | POST | Local webhook listener | EFD → Agent |
