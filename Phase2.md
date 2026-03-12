# Phase 2 — Odoo Backward Compatibility, Network Strategy & Site Configuration

**Version:** 1.0
**Created:** 2026-03-13
**Status:** PLANNING
**Scope:** Android Edge-Agent, Desktop Edge-Agent, Portal, Cloud Backend

---

## Table of Contents

1. [Context & Goals](#1-context--goals)
2. [Current State Analysis](#2-current-state-analysis)
3. [Phase 2.1 — WebSocket Server for Odoo Backward Compatibility](#3-phase-21--websocket-server-for-odoo-backward-compatibility)
4. [Phase 2.2 — Network Strategy (WiFi for FCC, Mobile Data for Cloud)](#4-phase-22--network-strategy-wifi-for-fcc-mobile-data-for-cloud)
5. [Phase 2.3 — Local FCC Connection Configuration & Settings Page](#5-phase-23--local-fcc-connection-configuration--settings-page)
6. [Phase 2.4 — Cloud URL & Route Configuration / Environment Switching](#6-phase-24--cloud-url--route-configuration--environment-switching)
7. [Phase 2.5 — Post-Registration Site Data Fetch & Persistent Storage](#7-phase-25--post-registration-site-data-fetch--persistent-storage)
8. [Cross-Cutting Concerns](#8-cross-cutting-concerns)
9. [Implementation Order & Dependencies](#9-implementation-order--dependencies)
10. [Risk Register](#10-risk-register)

---

## 1. Context & Goals

The FCC Middleware platform currently operates with the edge-agent acting as a **TCP client** connecting to the DOMS FCC over LAN using the JPL binary protocol (STX/ETX-framed JSON). Odoo POS communicates with the edge-agent via its local REST API (`http://127.0.0.1:8585/api/v1/...`).

**The Problem:** In the legacy DOMSRealImplementation deployment, Odoo communicated with the middleware over a **WebSocket**. Migrating to the new FCC Middleware platform requires Odoo changes unless we expose the same WebSocket interface. Phase 2 ensures zero Odoo changes by exposing a backward-compatible WebSocket server on the edge-agents.

### Phase 2 Goals

| # | Goal | Rationale |
|---|------|-----------|
| P2.1 | Expose a WebSocket server on both edge-agents that mimics the DOMSRealImplementation WebSocket protocol | Odoo requires zero changes to adopt the new middleware |
| P2.2 | Route FCC traffic over WiFi and cloud traffic preferentially over mobile data | FCC is on station LAN (WiFi); cloud needs reliable internet (mobile data preferred) |
| P2.3 | Allow per-device FCC IP/port override with a settings page | Each site may have a different FCC IP; field techs need to configure locally |
| P2.4 | Support environment-based cloud URL/route configuration | Dev/staging/prod URLs must be configurable without rebuilding |
| P2.5 | Fetch and persist site master data (FCC type, products, pumps, nozzles) on registration | Device must operate offline with full site context immediately after registration |

---

## 2. Current State Analysis

### 2.1 How the Edge-Agent Communicates Today

```
┌─────────────┐    REST (port 8585)     ┌───────────────────┐    TCP/JPL     ┌──────────┐
│  Odoo POS   │ ───────────────────────► │  Android/Desktop  │ ─────────────► │ DOMS FCC │
│  (on device) │ ◄─────────────────────── │    Edge-Agent     │ ◄───────────── │  (LAN)   │
└─────────────┘    HTTP responses        └───────────────────┘  STX/ETX frames └──────────┘
                                                │
                                                │ HTTPS (bearer JWT)
                                                ▼
                                         ┌─────────────┐
                                         │  Cloud API  │
                                         └─────────────┘
```

### 2.2 What DOMSRealImplementation Exposed (Legacy)

The legacy DOMSRealImplementation project (`DOMSRealImplementation/DppMiddleWareService/`) exposed a **Fleck-based WebSocket Secure (WSS) server** that Odoo POS connected to.

**Transport:** `wss://<host>:<port>` — host/port from config (`WebSocketServer:Host` / `WebSocketServer:Port`), typically **port 8443** on the station LAN IP (e.g., `wss://10.175.1.2:8443`). TLS 1.2/1.3 with a PFX certificate loaded from disk and installed into the Trusted Root store.

**Client tracking:** `ConcurrentDictionary<IWebSocketConnection, bool>` — all connected clients are broadcast targets.

**Capabilities exposed to Odoo:**
- **Fetch unsynced transactions** — Odoo sends `{ mode: "latest", pump_id, nozzle_id, emp, CreatedDate }`, server responds with `{ type: "latest", data: [PumpTransactions] }`
- **Fetch all transactions** — `{ mode: "all" }` → `{ type: "all_transactions", data: [...] }`
- **Update transaction state** — `{ mode: "manager_update", transaction_id, update: { state, order_uuid, order_id, payment_id, add_to_cart, status_sync } }`; server broadcasts `{ type: "transaction_update", data: {updated tx} }` to all clients
- **Cart acknowledgment** — `{ mode: "attendant_update", transaction_id, update: { order_uuid, order_id, state, add_to_cart, payment_id } }` → broadcast `{ type: "transaction_update", data: {tx} }`
- **Fuel pump status** — `{ mode: "FuelPumpStatus" }` triggers immediate status fetch; additionally, server auto-broadcasts `FuelPumpStatusDto` objects to each connected client every **3 seconds** via a per-connection timer
- **Pump unblock** — `{ mode: "fp_unblock", ... }` sends FcRelease to FCC
- **Attendant pump count** — `{ mode: "attendant_pump_count_update", data: [{PumpNumber, EmpTagNo, NewMaxTransaction}] }` → ack `{ type: "attendant_pump_count_update_ack", data: {pump_number, emp_tag_no, max_limit, status} }`
- **Discard transaction** — `{ mode: "manager_manual_update", ... }`
- **Add transaction** — `{ mode: "add_transaction", ... }`

**Key DTOs (snake_case JSON):**

```json
// PumpTransactions (server → Odoo)
{
  "id": 1, "transaction_id": "...", "pump_id": 1, "nozzle_id": 1,
  "attendant": "...", "product_id": "...", "qty": 10.5, "unit_price": 1.50,
  "total": 15.75, "state": "...", "start_time": "...", "end_time": "...",
  "order_uuid": "...", "sync_status": 0, "odoo_order_id": "...",
  "add_to_cart": false, "payment_id": null
}
// FuelPumpStatusDto (broadcasted every 3s per client, camelCase)
{
  "pump_number": 1, "nozzle_number": 1, "status": "idle",
  "reading": 0.0, "volume": 0.0, "litre": 0.0, "amount": 0.0,
  "attendant": "...", "count": 0, "FpGradeOptionNo": 0,
  "unit_price": null, "isOnline": true
}
```

**Source files:** `WebSocketServerHostedService.cs` (server + FleckWebSocketAdapter), `Services/TransactionService.cs` (query + response builder), `Models/PumpTransactions.cs` + `FpStatusResponse.cs` (DTOs).

### 2.3 Key Files in Current Implementation

| Component | File | Current Role |
|-----------|------|-------------|
| **DOMS JPL Adapter** | `edge-agent/.../adapter/doms/DomsJplAdapter.kt` | TCP client connecting TO the FCC; handles logon, heartbeat, transactions, pre-auth, pump status |
| **JPL TCP Client** | `edge-agent/.../adapter/doms/jpl/JplTcpClient.kt` | Persistent TCP connection with coroutine read loop, request-response correlation, unsolicited message dispatch |
| **JPL Frame Codec** | `edge-agent/.../adapter/doms/jpl/JplFrameCodec.kt` | STX/ETX binary frame encoding/decoding |
| **Local API Server** | `edge-agent/.../api/LocalApiServer.kt` | Ktor CIO HTTP server on port 8585 (REST API for Odoo POS) |
| **Connectivity Manager** | `edge-agent/.../connectivity/ConnectivityManager.kt` | Dual-probe (internet + FCC) state machine; does NOT differentiate WiFi vs mobile data |
| **Config Manager** | `edge-agent/.../config/ConfigManager.kt` | Manages cloud-pushed config; persists to Room with AES-256-GCM encryption |
| **Config DTO** | `edge-agent/.../config/EdgeAgentConfigDto.kt` | Full config structure including FCC, sync, mappings (products, nozzles), local API settings |
| **Provisioning** | `edge-agent/.../ui/ProvisioningActivity.kt` | QR/manual registration; stores tokens, identity, initial config; does NOT fetch site master data separately |
| **Cloud API Client** | `edge-agent/.../sync/CloudApiClient.kt` | All cloud HTTP calls (upload, config poll, telemetry, registration, pre-auth forward) |
| **Desktop Registration** | `desktop-edge-agent/.../Registration/RegistrationManager.cs` | Reads/writes `registration.json`; overlays identity into `AgentConfiguration` |
| **Desktop CadenceController** | `desktop-edge-agent/.../Runtime/CadenceController.cs` | Single cadence loop dispatching FCC poll, cloud upload, status poll, config poll, telemetry |
| **Cloud AgentController** | `cloud/.../Controllers/AgentController.cs` | Device registration, config distribution, token refresh, decommission |
| **Cloud Site Entity** | `cloud/.../Domain/Entities/Site.cs` | Site master data with Pumps, FccConfigs, AgentRegistrations |
| **Cloud FccConfig Entity** | `cloud/.../Domain/Entities/FccConfig.cs` | FCC connection config including vendor-specific fields (DOMS JPL, Radix, Petronite) |
| **Cloud Product/Pump/Nozzle** | `cloud/.../Domain/Entities/{Product,Pump,Nozzle}.cs` | Site equipment master data with Odoo↔FCC number mappings |
| **SiteConfigResponse** | `cloud/.../Contracts/Config/SiteConfigResponse.cs` | Full config contract delivered to agents including MappingsDto (products, nozzles) |

### 2.4 What Already Exists That Phase 2 Builds On

| Capability | Status | Location |
|-----------|--------|----------|
| Unsolicited message dispatch (FpStatusChanged, TransactionAvailable, FuellingUpdate) | **Exists** | `DomsJplAdapter.handleUnsolicitedMessage()` — dispatches via `IFccEventListener` |
| `IFccEventListener` interface | **Exists** | `edge-agent/.../adapter/common/IFccAdapter.kt` — `onPumpStatusChanged`, `onTransactionAvailable`, `onFuellingUpdate` |
| Product/nozzle mappings in config | **Exists** | `MappingsDto.products[]` and `MappingsDto.nozzles[]` in `EdgeAgentConfigDto` |
| Product/Pump/Nozzle entities in cloud DB | **Exists** | `Product.cs`, `Pump.cs`, `Nozzle.cs` with Odoo↔FCC number mappings |
| Site config delivered on registration | **Exists** | `AgentController.Register()` returns `SiteConfig` in registration response |
| Local API server infrastructure | **Exists** | Ktor CIO server with auth, rate-limiting, correlation IDs |
| Encrypted local config storage | **Exists** | Room + AES-256-GCM via Android Keystore |
| Desktop provisioning flow | **Exists** | `RegistrationManager` + `ProvisioningWindow.axaml` |

---

## 3. Phase 2.1 — WebSocket Server for Odoo Backward Compatibility

### 3.1 Architecture

```
┌──────────────┐   WebSocket (port N)    ┌────────────────────┐   TCP/JPL    ┌──────────┐
│   Odoo POS   │ ◄─────────────────────► │   Edge-Agent       │ ────────────► │ DOMS FCC │
│              │   (same as legacy DOMS) │                    │ ◄──────────── │  (LAN)   │
│              │                          │  ┌──────────────┐  │              └──────────┘
│              │   REST (port 8585)       │  │ WS Bridge    │  │
│              │ ◄──────────────────────► │  │ Server       │  │
└──────────────┘   (existing, kept)       │  └──────────────┘  │
                                          └────────────────────┘
```

The edge-agent will expose a **WebSocket server** that acts as a bridge between Odoo and the FCC adapter. This is additive — the existing REST API on port 8585 remains unchanged.

### 3.2 WebSocket Protocol Contract (From DOMSRealImplementation Source)

The WebSocket server must replicate the **exact** DOMSRealImplementation message format. Inbound messages use a `mode` field for routing; outbound messages use a `type` field. All transaction data uses **snake_case** JSON keys matching the `PumpTransactions` DTO.

> **Source of truth:** `DOMSRealImplementation/DppMiddleWareService/WebSocketServerHostedService.cs` (FleckWebSocketAdapter.OnMessage handler)

#### Odoo → Server (Commands via `mode` field)

| `mode` Value | Payload Fields | Handler | Response |
|-------------|---------------|---------|----------|
| `latest` | `pump_id?`, `nozzle_id?`, `emp?`, `CreatedDate?` | Fetch unsynced transactions filtered by pump/nozzle/attendant/date | `{ type: "latest", data: [PumpTransactions] }` |
| `all` | (none) | Fetch all transactions | `{ type: "all_transactions", data: [PumpTransactions] }` |
| `manager_update` | `transaction_id`, `update: { state?, order_uuid?, order_id?, payment_id?, status_sync?, add_to_cart? }` | Update transaction fields in DB | Broadcast `{ type: "transaction_update", data: {tx} }` to all clients |
| `attendant_update` | `transaction_id`, `update: { order_uuid, order_id, state, add_to_cart?, payment_id? }` | Update order linkage + cart status | Broadcast `{ type: "transaction_update", data: {tx} }` to all clients |
| `FuelPumpStatus` | (none) | Get current fuel pump status | Array of `FuelPumpStatusDto` objects |
| `fp_unblock` | `fp_id` | Release (unblock) a fuel point via FCC | FCC command forwarded |
| `attendant_pump_count_update` | `data: [{ PumpNumber, EmpTagNo, NewMaxTransaction }]` | Update per-attendant pump transaction limits | Per-item ack: `{ type: "attendant_pump_count_update_ack", data: { pump_number, emp_tag_no, max_limit, status } }` |
| `manager_manual_update` | Transaction discard payload | Mark transaction as discarded | DB update |
| `add_transaction` | Transaction data | Insert new transaction record | DB insert |

#### Server → Odoo (Push Events)

| Event | Trigger | Payload Format |
|-------|---------|---------------|
| Transaction query response | Client sends `mode: "latest"` | `{ type: "latest", data: [PumpTransactions] }` |
| All transactions response | Client sends `mode: "all"` | `{ type: "all_transactions", data: [PumpTransactions] }` |
| Transaction update broadcast | Any client updates a transaction | `{ type: "transaction_update", data: PumpTransactions }` — broadcast to ALL connected clients |
| Fuel pump status broadcast | **Automatic every 3 seconds** per connection (Timer-based) | Individual `FuelPumpStatusDto` objects (NOT wrapped in type/data envelope) |
| Attendant pump count ack | Client sends `attendant_pump_count_update` | `{ type: "attendant_pump_count_update_ack", data: { pump_number, emp_tag_no, max_limit, status: "updated" } }` |
| Generic broadcast | `BroadcastToAllClients(type, data)` | `{ type: "<type>", data: {snake_case_converted_data} }` — keys auto-converted via regex `([a-z])([A-Z])` → `$1_$2` |
| Error | Parse failure or unknown mode | `{ status: "error", message: "..." }` |

#### PumpTransactions DTO (snake_case, matches `DppMiddleWareService/Models/PumpTransactions.cs`)

```json
{
  "id": 1,
  "transaction_id": "TXN-001",
  "pump_id": 1,
  "nozzle_id": 1,
  "attendant": "EMP001",
  "product_id": "DIESEL",
  "qty": 45.230,
  "unit_price": 1.450,
  "total": 65.58,
  "state": "completed",
  "start_time": "2026-03-13T10:00:00",
  "end_time": "2026-03-13T10:03:45",
  "order_uuid": "uuid-123",
  "sync_status": 0,
  "odoo_order_id": "SO001",
  "add_to_cart": false,
  "payment_id": null
}
```

#### FuelPumpStatusDto (broadcast every 3 seconds, mixed case from legacy)

```json
{
  "pump_number": 1,
  "nozzle_number": 1,
  "status": "idle",
  "reading": 0.0,
  "volume": 0.0,
  "litre": 0.0,
  "amount": 0.0,
  "attendant": "EMP001",
  "count": 0,
  "FpGradeOptionNo": 0,
  "unit_price": null,
  "isOnline": true
}
```

#### Key Behavioral Notes

1. **Per-connection timer:** Each connected client gets a `Timer` that fires every 3 seconds, fetching and sending `FuelPumpStatusDto` items individually (not batched).
2. **Broadcast on mutation:** When any client updates a transaction (`manager_update`, `attendant_update` with `add_to_cart`), the updated transaction is broadcast to ALL connected clients via `_serverRef.Clients`.
3. **Snake-case conversion:** `BroadcastToAllClients()` applies regex-based PascalCase→snake_case conversion on keys.
4. **No heartbeat/ping at WebSocket level:** The legacy implementation does not send WebSocket ping frames; liveness is determined by Fleck's `IsAvailable` check before each send.
5. **Error handling:** Unknown `mode` values get `{ status: "error", message: "Unknown mode '<mode>'" }`; the connection is never closed by the server on error.

### 3.3 Implementation Plan — Android Edge-Agent

**New files:**

| File | Purpose |
|------|---------|
| `edge-agent/.../websocket/OdooWebSocketServer.kt` | Ktor WebSocket server module; manages connected sessions |
| `edge-agent/.../websocket/OdooWsBridge.kt` | Translates between adapter events and WebSocket JSON messages |
| `edge-agent/.../websocket/OdooWsMessageHandler.kt` | Parses incoming Odoo commands and dispatches to adapter |
| `edge-agent/.../websocket/OdooWsModels.kt` | Serializable data classes for all WebSocket message types |

**Modified files:**

| File | Change |
|------|--------|
| `LocalApiServer.kt` | Add WebSocket route at `/ws` on the same Ktor server (port 8585), OR start a second server on a configurable WebSocket port |
| `EdgeAgentConfigDto.kt` | Add `websocket` section to config: `{ enabled, port, path }` |
| `ConfigManager.kt` | Validate new WebSocket config fields |
| `EdgeAgentForegroundService.kt` | Wire `OdooWebSocketServer` lifecycle (start/stop with service) |
| `CadenceController.kt` (Android) | Forward `IFccEventListener` events to `OdooWsBridge` |
| `AppModule.kt` (DI) | Register `OdooWebSocketServer` and `OdooWsBridge` as singletons |

**Key design decisions:**

1. **Separate WSS server on port 8443 (matching legacy):** The DOMSRealImplementation ran WSS on port 8443 (configurable). To achieve zero Odoo changes, the edge-agent MUST expose a WebSocket server on the **same port** Odoo is already configured to connect to. This means a **second server** alongside the REST API on port 8585. The port is configurable via `websocket.port` in case a site used a different port. On Android, use Ktor WebSocket support; on Desktop, use Kestrel WebSocket middleware.

2. **WSS with TLS:** The legacy used `wss://` with a PFX certificate. The new implementation must also support WSS. On Android: use a self-signed certificate generated at first boot and stored in Android Keystore; on Desktop: use the Windows certificate store or a PFX file (same as legacy). For initial testing, `ws://` (non-TLS) on localhost is acceptable; LAN mode requires TLS.

3. **Message routing via `mode` field:** Incoming messages use `mode` (not `type`) per legacy. The `FleckWebSocketAdapter.OnMessage` switch statement must be replicated exactly.

4. **Per-connection 3-second pump status timer:** Each connected client gets a dedicated timer that broadcasts `FuelPumpStatusDto` objects every 3 seconds. This is critical for Odoo's real-time pump display.

5. **Broadcast on mutation:** Transaction updates (`manager_update`, `attendant_update`) must broadcast `{ type: "transaction_update", data: {tx} }` to ALL connected clients, not just the sender.

6. **Client tracking:** Use `ConcurrentHashMap<WebSocketSession, Boolean>` (Kotlin) / `ConcurrentDictionary<WebSocket, bool>` (C#) matching the legacy pattern.

7. **Error handling:** Unknown `mode` → `{ status: "error", message: "Unknown mode '<mode>'" }`. Never close the connection on error.

8. **Authentication:** Localhost bypass (same-device Odoo). LAN mode: the legacy implementation had NO auth on the WebSocket — it relied on the station LAN being trusted. For security improvement, optionally support `X-Api-Key` as a query parameter on the WebSocket upgrade URL, but make it configurable to maintain backward compatibility with unmodified Odoo.

### 3.4 Implementation Plan — Desktop Edge-Agent

**New files:**

| File | Purpose |
|------|---------|
| `FccDesktopAgent.Core/WebSocket/OdooWebSocketServer.cs` | ASP.NET Core WebSocket middleware using `System.Net.WebSockets` |
| `FccDesktopAgent.Core/WebSocket/OdooWsBridge.cs` | Event-to-message translator (same logic as Android) |
| `FccDesktopAgent.Core/WebSocket/OdooWsMessageHandler.cs` | Incoming command dispatcher |
| `FccDesktopAgent.Core/WebSocket/OdooWsModels.cs` | Shared message models |

**Modified files:**

| File | Change |
|------|--------|
| `FccDesktopAgent.Api/Program.cs` or `Endpoints/` | Register WebSocket middleware at `/ws` path |
| `FccDesktopAgent.Core/Config/AgentConfiguration.cs` | Add WebSocket config section |
| `FccDesktopAgent.Core/Runtime/ServiceCollectionExtensions.cs` | Wire WebSocket services in DI |

The desktop agent can use `Kestrel` WebSocket support since it already runs an HTTP API via ASP.NET Core minimal APIs.

### 3.5 Configuration Schema Addition

```json
{
  "websocket": {
    "enabled": true,
    "port": 8443,
    "useTls": true,
    "certificatePath": null,
    "certificatePassword": null,
    "bindAddress": "0.0.0.0",
    "maxConnections": 10,
    "pumpStatusBroadcastIntervalSeconds": 3,
    "requireApiKeyForLan": false
  }
}
```

The WebSocket server runs as a **separate server** on `websocket.port` (default 8443, matching legacy DOMSRealImplementation). The `bindAddress` defaults to `0.0.0.0` (all interfaces) because Odoo connects from the LAN, not just localhost. When `useTls` is true, a certificate is required (auto-generated on Android, PFX on desktop).

---

## 4. Phase 2.2 — Network Strategy (WiFi for FCC, Mobile Data for Cloud)

### 4.1 Problem Statement

The Android edge-agent runs on Urovo i9100 HHT devices at fuel stations. These devices have:
- **WiFi** — connected to the station's local network where the FCC lives
- **Mobile data (SIM)** — provides internet connectivity to the cloud

Currently, `ConnectivityManager.kt` uses generic probes without network binding. Android routes all traffic over the default network (usually WiFi when connected). This means cloud traffic may fail if the WiFi network has no internet gateway, and FCC traffic may fail if the OS prefers mobile data.

### 4.2 Android Network Binding Strategy

Android provides `android.net.ConnectivityManager.requestNetwork()` with `NetworkRequest` and `NetworkCallback` to bind specific traffic to specific network interfaces.

**Strategy:**

```
┌─────────────────────────────────────────────────────┐
│                  Edge-Agent (Android)                │
│                                                     │
│  ┌─────────────┐     WiFi Network     ┌──────────┐  │
│  │ FCC Adapter  │ ──────────────────► │ FCC LAN  │  │
│  │ (JplTcpClient)│  (bound socket)   │ 10.0.0.x │  │
│  └─────────────┘                      └──────────┘  │
│                                                     │
│  ┌─────────────┐   Mobile Data (preferred)          │
│  │ CloudApiClient│ ──────────────────► Cloud API    │
│  │ (OkHttp)    │   or WiFi (fallback)              │
│  └─────────────┘                                    │
│                                                     │
│  Priority for cloud: Mobile > WiFi > Offline        │
│  Priority for FCC:   WiFi always (LAN-only)         │
└─────────────────────────────────────────────────────┘
```

### 4.3 Implementation Plan — Android

**New files:**

| File | Purpose |
|------|---------|
| `edge-agent/.../connectivity/NetworkBinder.kt` | Manages Android `NetworkRequest` for WiFi and mobile; exposes `wifiNetwork` and `cloudNetwork` as `StateFlow<Network?>` |
| `edge-agent/.../connectivity/BoundSocketFactory.kt` | OkHttp `SocketFactory` that binds to a specific `Network` for cloud calls |

**Modified files:**

| File | Change |
|------|--------|
| `ConnectivityManager.kt` | Inject `NetworkBinder`; use `wifiNetwork` for FCC probe and `cloudNetwork` for internet probe |
| `JplTcpClient.kt` | Accept optional `java.net.Network` parameter; call `network.bindSocket(socket)` before `socket.connect()` to force FCC traffic over WiFi |
| `CloudApiClient.kt` | Pass `BoundSocketFactory` (bound to mobile data network) to OkHttp engine; fallback to WiFi if mobile unavailable |
| `AppModule.kt` | Create `NetworkBinder` singleton; inject into adapter and cloud client |
| `EdgeAgentForegroundService.kt` | Start `NetworkBinder` with service lifecycle |

**`NetworkBinder.kt` pseudocode:**

```kotlin
class NetworkBinder(private val context: Context, private val scope: CoroutineScope) {
    private val cm = context.getSystemService(ConnectivityManager::class.java)

    val wifiNetwork = MutableStateFlow<Network?>(null)
    val mobileNetwork = MutableStateFlow<Network?>(null)

    /** The preferred network for cloud traffic: mobile > wifi > null */
    val cloudNetwork: StateFlow<Network?> = combine(mobileNetwork, wifiNetwork) { mobile, wifi ->
        mobile ?: wifi
    }.stateIn(scope, SharingStarted.Eagerly, null)

    fun start() {
        // Request WiFi network
        cm.requestNetwork(
            NetworkRequest.Builder()
                .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                .build(),
            wifiCallback
        )
        // Request Mobile network
        cm.requestNetwork(
            NetworkRequest.Builder()
                .addTransportType(NetworkCapabilities.TRANSPORT_CELLULAR)
                .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .build(),
            mobileCallback
        )
    }

    fun stop() {
        cm.unregisterNetworkCallback(wifiCallback)
        cm.unregisterNetworkCallback(mobileCallback)
    }
}
```

### 4.4 Desktop Edge-Agent

The desktop agent runs on Windows PCs which typically have a single wired or WiFi connection to the station LAN with internet access. Network binding is **not required** for the desktop agent — the OS routes FCC and cloud traffic over the same interface. No changes needed unless future deployments use dual-NIC setups.

**If dual-NIC is needed later:** Use `HttpClientHandler.Properties` with socket binding or route table configuration. Defer to Phase 3.

### 4.5 Offline Resilience

The existing `ConnectivityManager` already handles offline gracefully:
- **FCC offline:** FCC poll suspended, buffer continues serving cached data via local API
- **Internet offline:** Cloud upload suspended, transactions buffered in Room/SQLite
- **Both offline:** All work suspended, local API continues serving buffer

Phase 2.2 enhances this by:
- Adding a third state: `MOBILE_DOWN_WIFI_ONLY` — FCC works, cloud falls back to WiFi (may or may not have internet)
- The `ConnectivityState` enum gains no new values; the change is in **which physical network** carries each probe

---

## 5. Phase 2.3 — Local FCC Connection Configuration & Settings Page

### 5.1 Problem Statement

Each site has a potentially different IP address and port for its FCC. The cloud-pushed config (`fcc.hostAddress`, `fcc.port`) provides defaults, but field technicians need to override these locally when:
- The FCC IP changes after network reconfiguration
- A replacement FCC is installed with a different address
- Testing with a simulator at a different address

### 5.2 Configuration Hierarchy

```
Priority (highest to lowest):
1. Device-local override (stored in EncryptedSharedPreferences / local settings file)
2. Cloud-pushed config (fcc.hostAddress, fcc.port from SiteConfig)
3. Factory defaults (127.0.0.1:4000 for DOMS, etc.)
```

### 5.3 Implementation Plan — Android

**New files:**

| File | Purpose |
|------|---------|
| `edge-agent/.../ui/SettingsActivity.kt` | New settings screen accessible from DiagnosticsActivity or system tray |
| `edge-agent/.../config/LocalOverrideManager.kt` | Reads/writes local overrides from EncryptedSharedPreferences; merges with cloud config |

**Settings fields:**

| Field | Default Source | Override Key | Validation |
|-------|---------------|-------------|------------|
| FCC IP Address | `fcc.hostAddress` from cloud | `override_fcc_host` | Valid IPv4/hostname |
| FCC Port | `fcc.port` from cloud | `override_fcc_port` | 1–65535 |
| FCC JPL Port (DOMS) | `fcc.jplPort` from cloud | `override_fcc_jpl_port` | 1–65535 |
| FCC Auth Credential | `fcc.credentialRef` | `override_fcc_credential` | Non-empty |
| WebSocket Port | `websocket.port` from cloud | `override_ws_port` | 1–65535 |
| Cloud Base URL | `sync.cloudBaseUrl` from cloud | (read-only display) | — |

**Modified files:**

| File | Change |
|------|--------|
| `EdgeAgentConfigDto.kt` | `toAgentFccConfig()` checks `LocalOverrideManager` before using cloud values |
| `EncryptedPrefsManager.kt` | Add override storage properties |
| `DiagnosticsActivity.kt` | Add "Settings" button to navigate to `SettingsActivity` |
| `CadenceController.kt` | On settings change, trigger adapter reconnect |

**Settings page layout (Android):**

```
┌─────────────────────────────────────────┐
│         FCC Middleware Settings          │
├─────────────────────────────────────────┤
│                                         │
│  FCC Connection                         │
│  ─────────────                          │
│  IP Address:   [ 192.168.1.100 ]        │
│  Port:         [ 4000           ]       │
│  JPL Port:     [ 4000           ]       │
│  Access Code:  [ ••••••••       ]       │
│                                         │
│  WebSocket                              │
│  ─────────                              │
│  Port:         [ 8585           ]       │
│  Enabled:      [✓]                      │
│                                         │
│  Cloud (read-only)                      │
│  ─────────────────                      │
│  URL: https://api.fccmiddleware.io      │
│  Env: PRODUCTION                        │
│  Device ID: abc-123...                  │
│  Site: SITE-001                         │
│                                         │
│  [ Save & Reconnect ]  [ Reset to Cloud │
│                          Defaults ]     │
└─────────────────────────────────────────┘
```

### 5.4 Implementation Plan — Desktop Edge-Agent

**Modified files:**

| File | Change |
|------|--------|
| `FccDesktopAgent.Core/Config/AgentConfiguration.cs` | Add `FccHostOverride`, `FccPortOverride` properties with `[JsonIgnore]` |
| `FccDesktopAgent.Core/Config/LocalOverrideManager.cs` (new) | Read/write `overrides.json` from agent data directory |
| `FccDesktopAgent.App/Views/MainWindow.axaml` | Add Settings tab/panel |
| `FccDesktopAgent.App/ViewModels/SettingsViewModel.cs` (new) | MVVM bindings for settings form |

The desktop agent stores overrides in `%APPDATA%/FccDesktopAgent/overrides.json`, loaded by `LocalOverrideManager` and merged at config resolution time.

### 5.5 Cloud API Routes Configuration

In addition to FCC overrides, the settings should display (read-only) all cloud API routes the device uses:

| Route | Purpose | Current Path |
|-------|---------|-------------|
| Registration | Device provisioning | `POST /api/v1/agent/register` |
| Config Poll | Fetch site config | `GET /api/v1/agent/config` |
| Token Refresh | JWT rotation | `POST /api/v1/agent/token/refresh` |
| Transaction Upload | Sync transactions | `POST /api/v1/transactions/upload` |
| Synced Status | Odoo sync confirmation | `GET /api/v1/transactions/synced-status` |
| Pre-Auth Forward | Forward pre-auth to cloud | `POST /api/v1/preauth` |
| Telemetry | Device health reporting | `POST /api/v1/agent/telemetry` |
| Diagnostic Logs | Error log upload | `POST /api/v1/agent/diagnostic-logs` |
| Version Check | Compatibility check | `GET /api/v1/agent/version-check` |

---

## 6. Phase 2.4 — Cloud URL & Route Configuration / Environment Switching

### 6.1 Problem Statement

Currently the cloud URL is set during registration (from QR code or manual entry) and can be overridden by `sync.cloudBaseUrl` in the cloud-pushed config. There is no way to switch between environments (dev/staging/production) without re-provisioning.

### 6.2 Environment-Based URL Strategy

**Option A: Environment Selector (Recommended)**

Add an `environment` field to the registration flow and config. The device resolves cloud URLs from a built-in environment map:

```kotlin
object CloudEnvironments {
    val ENVIRONMENTS = mapOf(
        "PRODUCTION" to CloudEnv(
            baseUrl = "https://api.fccmiddleware.io",
            displayName = "Production",
        ),
        "STAGING" to CloudEnv(
            baseUrl = "https://api-staging.fccmiddleware.io",
            displayName = "Staging",
        ),
        "DEVELOPMENT" to CloudEnv(
            baseUrl = "https://api-dev.fccmiddleware.io",
            displayName = "Development",
        ),
        "LOCAL" to CloudEnv(
            baseUrl = "https://localhost:5001",
            displayName = "Local Dev",
        ),
    )
}
```

The QR code payload gains an optional `env` field:
```json
{ "v": 2, "sc": "SITE-001", "cu": "https://...", "pt": "token", "env": "STAGING" }
```

When `env` is present, the device uses the environment map. When absent (v1 QR), the explicit `cu` URL is used (backward compatible).

**Option B: Full Route Customization**

Allow overriding individual route paths. This is overkill for most deployments but useful if the cloud API is behind a reverse proxy with non-standard paths.

```json
{
  "cloudRoutes": {
    "register": "/api/v1/agent/register",
    "config": "/api/v1/agent/config",
    "tokenRefresh": "/api/v1/agent/token/refresh",
    "transactionUpload": "/api/v1/transactions/upload",
    "syncedStatus": "/api/v1/transactions/synced-status",
    "preauth": "/api/v1/preauth",
    "telemetry": "/api/v1/agent/telemetry",
    "diagnosticLogs": "/api/v1/agent/diagnostic-logs",
    "versionCheck": "/api/v1/agent/version-check"
  }
}
```

**Recommendation:** Implement Option A first. Option B can be deferred until a concrete need arises.

### 6.3 Implementation Plan

**Android:**

| File | Change |
|------|--------|
| `ProvisioningActivity.kt` | Add environment dropdown to manual entry screen; parse `env` from v2 QR codes |
| `EncryptedPrefsManager.kt` | Store `environment` alongside `cloudBaseUrl` |
| `CloudApiClient.kt` | Resolve base URL from environment map when `environment` is set |
| `SettingsActivity.kt` | Display current environment (read-only after provisioning) |

**Desktop:**

| File | Change |
|------|--------|
| `ProvisioningWindow.axaml` | Add environment combo box |
| `RegistrationManager.cs` | Store `Environment` in `RegistrationState` |
| `AgentConfiguration.cs` | Add `Environment` property; resolve `CloudBaseUrl` from map when set |

**Portal:**

| File | Change |
|------|--------|
| `agent-detail.component.ts` | Display agent's environment in detail view |
| `site-detail.component.ts` | Show environment in bootstrap token generation form |
| `agent.service.ts` | Include environment in bootstrap token API call |

**Cloud Backend:**

| File | Change |
|------|--------|
| `GenerateBootstrapTokenRequest` | Add optional `Environment` field |
| `DeviceRegistrationApiResponse` | Include resolved environment in response |
| `SiteConfigResponse` | Add `environment` field to `SyncDto` |

---

## 7. Phase 2.5 — Post-Registration Site Data Fetch & Persistent Storage

### 7.1 Problem Statement

When a device registers successfully, the cloud returns a `siteConfig` that includes product mappings and nozzle mappings inside the `MappingsDto`. However:

1. **No explicit site equipment fetch** — Products, pumps, and nozzles are embedded in the config but not stored as separate queryable entities on the device
2. **FCC type not prominently stored** — The FCC vendor/model is in the config but not surfaced for display or local decision-making
3. **No offline-first master data** — If the config poll fails, the device relies on whatever was last cached

### 7.2 What the Cloud Already Has

The cloud database contains rich site equipment data:

```
Site ─┬── FccConfig (vendor, host, port, protocol, credentials)
      ├── Pump[] ─── Nozzle[] ─── Product
      └── AgentRegistration[]
```

The `SiteConfigResponse` already delivers:
- `fcc.*` — FCC vendor, model, host, port, protocol, credentials
- `mappings.products[]` — `{ fccProductCode, canonicalProductCode, displayName, active }`
- `mappings.nozzles[]` — `{ odooPumpNumber, fccPumpNumber, odooNozzleNumber, fccNozzleNumber, productCode }`

### 7.3 Implementation Plan

#### 7.3.1 Android — Dedicated Site Data Room Tables

**New Room entities:**

| Entity | Table | Purpose |
|--------|-------|---------|
| `SiteInfo` | `site_info` | Site identity, FCC type, operating model |
| `LocalProduct` | `local_products` | Product catalog for display and validation |
| `LocalPump` | `local_pumps` | Pump list with Odoo↔FCC number mapping |
| `LocalNozzle` | `local_nozzles` | Nozzle-product assignments |

**New files:**

| File | Purpose |
|------|---------|
| `edge-agent/.../buffer/entity/SiteInfo.kt` | Room entity for site info |
| `edge-agent/.../buffer/entity/LocalProduct.kt` | Room entity for products |
| `edge-agent/.../buffer/entity/LocalPump.kt` | Room entity for pumps |
| `edge-agent/.../buffer/entity/LocalNozzle.kt` | Room entity for nozzles |
| `edge-agent/.../buffer/dao/SiteDataDao.kt` | DAO for all site data CRUD |
| `edge-agent/.../config/SiteDataManager.kt` | Extracts site data from config and persists to Room |

**Modified files:**

| File | Change |
|------|--------|
| `ProvisioningActivity.kt` | After registration success, call `SiteDataManager.syncFromConfig(siteConfig)` |
| `ConfigPollWorker.kt` | After successful config apply, call `SiteDataManager.syncFromConfig(newConfig)` |
| `EdgeAgentDatabase.kt` (Room DB) | Add new entity classes to `@Database` annotation; migration |
| `DiagnosticsActivity.kt` | Display site info, product count, pump count, nozzle count |

**Data flow:**

```
Registration Success
    │
    ▼
siteConfig (from cloud response)
    │
    ▼
SiteDataManager.syncFromConfig(config)
    ├── Extract identity → SiteInfo (upsert)
    ├── Extract fcc.vendor, fcc.model → SiteInfo.fccType (upsert)
    ├── Extract mappings.products → LocalProduct[] (replace all)
    ├── Extract mappings.nozzles → LocalPump[] + LocalNozzle[] (replace all)
    └── Log summary: "Site data synced: N products, M pumps, K nozzles"
```

#### 7.3.2 Desktop — Equivalent Local Storage

**New files:**

| File | Purpose |
|------|---------|
| `FccDesktopAgent.Core/MasterData/SiteDataManager.cs` | Extracts and persists site data from config |
| `FccDesktopAgent.Core/MasterData/Models/` | `SiteInfo.cs`, `LocalProduct.cs`, `LocalPump.cs`, `LocalNozzle.cs` |

Storage: JSON file (`site-data.json`) in the agent data directory, loaded into memory at startup.

#### 7.3.3 Cloud API — No Changes Required

The cloud already delivers all necessary data in `SiteConfigResponse`. No new endpoints are needed.

If in the future we want a dedicated **site equipment API** (e.g., for a richer settings page that shows pump names, nozzle status, product prices), we can add:

| Endpoint | Purpose |
|---------|---------|
| `GET /api/v1/agent/site-equipment` | Returns pumps, nozzles, products for the registered site |

This is **deferred** — the config payload is sufficient for Phase 2.

#### 7.3.4 Portal — Site Equipment Display Enhancement

**Modified files:**

| File | Change |
|------|--------|
| `site-detail.component.ts` | Show pump/nozzle/product counts; link to FCC config detail |
| `agent-detail.component.ts` | Show the agent's locally stored site data version (from telemetry) |

---

## 8. Cross-Cutting Concerns

### 8.1 Security

| Concern | Mitigation |
|---------|-----------|
| WebSocket messages may contain pre-auth amounts (PII-adjacent) | WebSocket server only on localhost by default; LAN mode requires API key |
| Local override of FCC credentials | Stored in EncryptedSharedPreferences (Android) / DPAPI (Windows); never logged |
| Environment switching could be used to redirect to rogue server | Environment map is compiled into the app; custom URLs require manual entry with HTTPS enforcement |
| Network binding bypasses OS proxy settings | Acceptable for station deployment; add config flag `respectSystemProxy` for enterprise environments |

### 8.2 Backward Compatibility

| Item | Guarantee |
|------|-----------|
| Existing REST API (port 8585) | **No changes** — all existing routes, auth, rate-limiting preserved |
| Existing config schema | **Additive only** — new sections (`websocket`, `overrides`) have defaults; old configs work |
| QR code v1 format | **Fully supported** — `env` field is optional; `cu` URL is always used when `env` is absent |
| Cloud API contract | **No breaking changes** — new fields are optional; existing responses unchanged |

### 8.3 Testing Strategy

| Component | Test Type | Tool |
|-----------|----------|------|
| WebSocket server | Unit + integration | Ktor test engine + WebSocket client |
| Network binding | Manual on Urovo device | `adb shell` network diagnostics |
| Settings page | UI test | Espresso (Android) / Avalonia integration test |
| Config override merge | Unit test | JUnit5 / xUnit |
| Site data persistence | Unit test | Room in-memory DB / SQLite |
| Environment resolution | Unit test | Parameterized test per environment |

---

## 9. Implementation Order & Dependencies

```
Phase 2.5 (Site Data Persistence)     ──┐
   No dependencies; can start immediately │
                                          │
Phase 2.3 (Settings Page)            ────┤── Can proceed in parallel
   Depends on: LocalOverrideManager       │
                                          │
Phase 2.4 (Environment Switching)    ────┤
   Depends on: ProvisioningActivity mods  │
                                          │
Phase 2.2 (Network Binding)          ────┤
   Depends on: NetworkBinder class        │
   Risk: Needs physical device testing    │
                                          │
Phase 2.1 (WebSocket Server)         ────┘
   Depends on: None (additive)
   Risk: Needs DOMSRealImplementation protocol spec
```

### Recommended Sprint Plan

| Sprint | Deliverables | Risk |
|--------|-------------|------|
| **Sprint 1** (Week 1-2) | P2.5 — Site data Room tables + SiteDataManager + sync on registration + config poll | Low |
| **Sprint 2** (Week 3-4) | P2.3 — LocalOverrideManager + SettingsActivity (Android) + Settings view (Desktop) | Low |
| **Sprint 3** (Week 5-6) | P2.4 — Environment map + QR v2 parsing + ProvisioningActivity environment selector | Low |
| **Sprint 4** (Week 7-8) | P2.2 — NetworkBinder + socket binding for FCC and cloud; physical device testing | Medium |
| **Sprint 5-6** (Week 9-12) | P2.1 — OdooWebSocketServer + OdooWsBridge + message models + integration testing with Odoo | High (needs Odoo-side validation) |

### Critical Path

The WebSocket server (P2.1) is the highest-risk item because:
1. ~~It requires the exact DOMSRealImplementation WebSocket protocol specification~~ **RESOLVED** — Legacy source is in `DOMSRealImplementation/` and Section 3.2 documents the exact protocol from `FleckWebSocketAdapter`
2. Integration testing requires a running Odoo instance to validate that the replicated protocol satisfies the Odoo POS WebSocket client
3. The WebSocket must handle the same edge cases as the legacy implementation (per-connection timers, broadcast to all clients, snake_case conversion)
4. TLS certificate management differs between Android (Keystore) and Desktop (PFX file) — needs per-platform implementation

**Action required before Sprint 5:** Set up an Odoo POS test instance to validate WebSocket integration. The protocol is now fully documented from source.

---

## 10. Risk Register

| # | Risk | Impact | Likelihood | Mitigation |
|---|------|--------|-----------|------------|
| R1 | ~~DOMSRealImplementation WebSocket protocol differs from assumed format~~ **RESOLVED** — Legacy source code (`DOMSRealImplementation/`) is in-repo and fully analyzed. Protocol contract in Section 3.2 is verified against actual `FleckWebSocketAdapter` message handler | N/A | N/A | Protocol documented; remaining risk is Odoo-side client variations between deployments |
| R2 | Android network binding breaks on certain device models/OS versions | FCC or cloud traffic fails on specific hardware | Low | Test on Urovo i9100 specifically; implement fallback to default routing if binding fails |
| R3 | WebSocket increases memory/battery usage on HHT devices | Device performance degradation | Low | Lazy initialization (only start WS when a client connects); aggressive idle timeout |
| R4 | Local FCC overrides drift from cloud config, causing silent failures | Transactions lost or misrouted | Medium | Display "override active" warning on diagnostics; log override vs cloud value on every reconnect |
| R5 | Environment switching allows connecting to wrong cloud in production | Data leakage across environments | Low | Compile environment map into app; require re-provisioning to switch; display environment prominently in UI |
| R6 | Site data Room migration fails on existing devices | App crashes on update | Low | Use Room `fallbackToDestructiveMigration` for site data tables only (they're repopulated from cloud on next config poll) |

---

## Appendix A: File Impact Summary

### New Files (17)

| # | Path | Component |
|---|------|-----------|
| 1 | `edge-agent/.../websocket/OdooWebSocketServer.kt` | Android |
| 2 | `edge-agent/.../websocket/OdooWsBridge.kt` | Android |
| 3 | `edge-agent/.../websocket/OdooWsMessageHandler.kt` | Android |
| 4 | `edge-agent/.../websocket/OdooWsModels.kt` | Android |
| 5 | `edge-agent/.../connectivity/NetworkBinder.kt` | Android |
| 6 | `edge-agent/.../connectivity/BoundSocketFactory.kt` | Android |
| 7 | `edge-agent/.../ui/SettingsActivity.kt` | Android |
| 8 | `edge-agent/.../config/LocalOverrideManager.kt` | Android |
| 9 | `edge-agent/.../config/CloudEnvironments.kt` | Android |
| 10 | `edge-agent/.../buffer/entity/SiteInfo.kt` | Android |
| 11 | `edge-agent/.../buffer/entity/LocalProduct.kt` | Android |
| 12 | `edge-agent/.../buffer/entity/LocalPump.kt` | Android |
| 13 | `edge-agent/.../buffer/entity/LocalNozzle.kt` | Android |
| 14 | `edge-agent/.../buffer/dao/SiteDataDao.kt` | Android |
| 15 | `edge-agent/.../config/SiteDataManager.kt` | Android |
| 16 | `desktop-edge-agent/.../WebSocket/OdooWebSocketServer.cs` | Desktop |
| 17 | `desktop-edge-agent/.../WebSocket/OdooWsBridge.cs` | Desktop |

### Modified Files (20+)

| Component | Files Modified |
|-----------|---------------|
| Android Edge-Agent | `LocalApiServer.kt`, `EdgeAgentConfigDto.kt`, `ConfigManager.kt`, `ConnectivityManager.kt`, `JplTcpClient.kt`, `CloudApiClient.kt`, `AppModule.kt`, `EdgeAgentForegroundService.kt`, `CadenceController.kt`, `ProvisioningActivity.kt`, `DiagnosticsActivity.kt`, `EncryptedPrefsManager.kt`, `ConfigPollWorker.kt` |
| Desktop Edge-Agent | `AgentConfiguration.cs`, `ServiceCollectionExtensions.cs`, `ProvisioningWindow.axaml`, `RegistrationManager.cs`, `MainWindow.axaml` |
| Cloud Backend | `GenerateBootstrapTokenRequest.cs`, `DeviceRegistrationApiResponse.cs`, `SiteConfigResponse.cs` |
| Portal | `agent-detail.component.ts`, `site-detail.component.ts`, `agent.service.ts` |
