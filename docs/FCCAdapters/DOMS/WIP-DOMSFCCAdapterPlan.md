# DOMS FCC Adapter — Integration Plan

Version: 0.1 (WIP)
Last Updated: 2026-03-12

------------------------------------------------------------------------

## 1. Executive Summary

This document analyzes the **real production-tested DOMS implementation** (`DOMSRealImplementation/`) against the Forecourt Middleware requirements and existing adapter contracts, then produces a concrete plan for implementing the DOMS FCC adapter across all system components.

### Critical Discovery: The Real DOMS Protocol is TCP/JPL, NOT REST

The existing adapter interface contracts (§5.5) assumed DOMS uses a **simple REST/JSON API** (`GET /transactions`, `POST /preauth`, etc.). The real production implementation reveals that DOMS uses a **TCP-based JPL (JSON Protocol Layer)** with binary framing:

| Aspect | Assumed (§5.5 Spec) | Real DOMS Protocol (DOMSRealImplementation) |
|--------|---------------------|---------------------------------------------|
| Transport | HTTP REST | **TCP socket** (persistent connection) |
| Port | HTTP port (e.g., 8080) | **TCP 8888** (JPL port) + ports 5001-5006 (DPP data) |
| Framing | HTTP request/response | **[STX] + JSON + [ETX]** binary delimiters |
| Authentication | `X-API-Key` header | **FcLogon_req** message with `FcAccessCode`, `CountryCode`, `PosVersionId` |
| Keepalive | N/A (HTTP stateless) | **[STX][ETX]** heartbeat every 30 seconds |
| Pre-auth | `POST /preauth` | **JPL message**: authorize_Fp_req |
| Transaction fetch | `GET /transactions?cursor=...` | **JPL message**: FpSupTrans_req (lock + read from supervised buffer) |
| Pump status | `GET /pump-status` | **FpStatus_req** (SubCodes 0-3 with supplemental parameters) |
| Transaction push | FCC POSTs JSON | **Unsolicited messages** on ports 5001-5006 (FpStatus, FpFuellingData, FpTransactionCompleted) |
| Pump addressing | `pumpNumber` (integer) | **FpId** (filling point ID, integer) with nozzle tracking via `NozzleId` supplemental param |
| Volume format | microlitres (long) | **Centilitres** (value × 100, e.g., 5.20L = 520) |
| Amount format | minor units (long) | **Value × 10** (e.g., 1000 TZS = 10000) |
| Connection model | Stateless (per-request) | **Stateful** (persistent TCP, must handle reconnection) |
| Mode management | Implicit | **Explicit**: supervised vs unsolicited transaction modes |

### Impact Assessment

This discovery means:

1. **Interface contracts need updating** — `ConnectionProtocol.TCP` already exists in enums but the `IFccAdapter` interface was designed around HTTP request/response. The TCP persistent connection model requires new patterns.
2. **Both edge agents need a TCP client** — Not HTTP. The Kotlin edge agent and .NET desktop agent must implement TCP socket management with STX/ETX framing.
3. **The "REST wrapper" approach is viable** — We can build a REST-to-JPL bridge (the DOMS Simulator in VirtualLab) that exposes the assumed REST API but translates to JPL internally. This would let the existing cloud adapter work for testing. For production, edge agents must speak native JPL.
4. **The cloud adapter's role is limited** — In `CLOUD_DIRECT` mode, DOMS doesn't push to HTTP endpoints. Real DOMS uses unsolicited TCP messages. The cloud adapter will primarily handle **edge-uploaded transactions** (already normalized to canonical format) rather than raw DOMS protocol.

### Architecture Decision: Dual-Protocol Strategy

Given the real protocol, the recommended approach is:

```
Production Path (Edge Agent — Native JPL/TCP):
  [DOMS FCC] ←TCP/JPL→ [Edge Agent DomsAdapter] → normalize → buffer/upload → [Cloud]

Testing Path (VirtualLab — REST Simulator):
  [DOMS REST Simulator] ←HTTP/JSON→ [Cloud DomsCloudAdapter] → normalize → store

Cloud Adapter Role (Production):
  Receives pre-normalized transactions from Edge Agent uploads
  Performs validation, dedup, and storage — NOT raw DOMS protocol handling
```

### Key Differences from Radix and Petronite

| Aspect | DOMS (Real) | Radix | Petronite |
|--------|-------------|-------|-----------|
| Transport | **TCP socket** (persistent) | REST (XML) | REST (JSON) |
| Framing | STX/ETX binary delimiters | HTTP | HTTP |
| Payload format | JSON (inside TCP frames) | XML | JSON |
| Authentication | FcLogon handshake | SHA-1 HMAC signing | OAuth2 bearer token |
| Pre-auth flow | Single JPL message | Single XML POST | Two-step (Create + Authorize) |
| Transaction fetch | Lock + read from supervised buffer | FIFO drain (CMD_CODE=10) | Not available (push-only) |
| Transaction push | Unsolicited TCP messages | XML POST with ACK | Webhook callback |
| Pump addressing | FpId (filling point) | PUMP_ADDR + FP + NOZ (three-tier) | nozzleId (internal Long ID) |
| Connection lifecycle | Persistent + heartbeat | Per-request | Per-request (token cached) |
| Pump state machine | 14 states (hex enum) | Simple | Simple |
| Volume encoding | centilitres (÷100) | litres | litres |
| Amount encoding | ×10 factor (÷10) | minor units | minor units |
| Port scheme | Multi-port (8888 + 5001-5006) | Dual port | Single port (8884) |

------------------------------------------------------------------------

## 2. DOMS Protocol Deep Dive (from DOMSRealImplementation)

### 2.1 Architecture — JPL over TCP

DOMS is a **Forecourt Controller** — hardware running firmware that directly controls fuel pumps. Communication uses the **JPL (JSON Protocol Layer)** protocol over TCP:

- **Main port (8888)**: JPL request/response channel for commands and solicited responses
- **Ports 5001-5006**: DPP (Data Push Protocol) channels for unsolicited event data
  - 5001, 5002: Supervised transaction events
  - 5003: Fallback console
  - 5004: Unsupervised transactions (locked mode)
  - 5005: Unsupervised transactions (unlocked mode)
  - 5006: Peripherals (dispensers, EPT terminals, price changes)

### 2.2 Connection Lifecycle

```
1. TCP Connect to host:8888
2. Start receive thread (background loop reading STX..ETX frames)
3. Start heartbeat thread (send [STX][ETX] every 30s)
4. Send FcLogon_req with credentials
5. Wait for FcLogon_resp (success/failure)
6. Begin operational message exchange
7. On disconnect: reconnect with backoff
```

### 2.3 Message Framing

Every message is a JSON object wrapped in binary delimiters:

```
[0x02] { "name": "...", "subCode": "...", "data": { ... } } [0x03]
  ^STX                    JSON payload                        ^ETX
```

Heartbeat is a minimal frame: `[0x02][0x03]` (STX immediately followed by ETX, no payload).

### 2.4 Authentication — FcLogon

```json
{
  "name": "FcLogon_req",
  "subCode": "00H",
  "data": {
    "FcAccessCode": "POS,RI,APPL_ID=10,UNSO_FPSTA_3,UNSO_TRBUFSTA_3",
    "CountryCode": "0045",
    "PosVersionId": "1234",
    "UnsolicitedApcList": [2]
  }
}
```

The `FcAccessCode` encodes capabilities:
- `POS` — POS client
- `RI` — Remote Interface
- `APPL_ID=10` — Application identifier
- `UNSO_FPSTA_3` — Request unsolicited FpStatus at SubCode 3 (full detail)
- `UNSO_TRBUFSTA_3` — Request unsolicited transaction buffer status at SubCode 3

### 2.5 Pump Status — FpStatus

**Request:**
```json
{ "name": "FpStatus_req", "subCode": "03H", "data": { "FpId": 1 } }
```

**Response (SubCode 3 — full detail):**
```json
{
  "name": "FpStatus_resp",
  "subCode": "03H",
  "data": {
    "FpId": 1,
    "SmId": "01",
    "FpMainState": "02",
    "FpSubStates": "00",
    "FpLockId": "00",
    "FcGradeId": "01",
    "FpDescriptor": "Pump 1",
    "SupParams": [
      { "ParId": "05", "Value": "00000520" },
      { "ParId": "06", "Value": "00012000" },
      { "ParId": "09", "Value": "01" }
    ]
  }
}
```

**Supplemental Parameters (ParId):**

| ParId | Name | Description | Conversion |
|-------|------|-------------|------------|
| 01 | FpSubStates2 | Extended sub-states | Raw hex |
| 02 | FpAvailableSms | Service mode IDs | Array |
| 03 | FpAvailableGrades | Available fuel grades | Array |
| 04 | FpGradeOptionNo | Current grade option | Integer |
| 05 | FuellingDataVol_e | Current volume | ÷ 100 (centilitres → litres) |
| 06 | FuellingDataMon_e | Current money due | ÷ 10 |
| 07 | AttendantAccountId | Attendant ID | String |
| 08 | FpBlockingStatus | Block status | Hex |
| 09 | NozzleId | Active nozzle | Integer (ASCII decoded) |
| 15 | MinPresetValues | Minimum preset per grade | Array |

### 2.6 Pump State Machine (FpMainState)

```
0x00 = Unconfigured
0x01 = Closed
0x02 = Idle          ← Ready for authorization
0x03 = Error
0x04 = Calling       ← Nozzle lifted, requesting authorization
0x05 = PreAuthorized ← Authorization accepted, waiting for dispense
0x06 = Starting
0x07 = StartingPaused
0x08 = StartingTerminated
0x09 = Fuelling      ← Actively dispensing
0x0A = FuellingPaused
0x0B = FuellingTerminated
0x0C = Unavailable   ← Blocked/out of service
0x0D = UnavailableAndCalling
```

**State mapping to canonical PumpState:**

| DOMS FpMainState | Canonical PumpState |
|------------------|-------------------|
| 0x00 Unconfigured | Offline |
| 0x01 Closed | Offline |
| 0x02 Idle | Idle |
| 0x03 Error | Error |
| 0x04 Calling | Calling |
| 0x05 PreAuthorized | Authorized |
| 0x06 Starting | Dispensing |
| 0x07 StartingPaused | Paused |
| 0x08 StartingTerminated | Completed |
| 0x09 Fuelling | Dispensing |
| 0x0A FuellingPaused | Paused |
| 0x0B FuellingTerminated | Completed |
| 0x0C Unavailable | Offline |
| 0x0D UnavailableAndCalling | Offline |

### 2.7 Pump Control Operations

| Operation | JPL Message | Purpose |
|-----------|-------------|---------|
| Authorize | authorize_Fp_req | Pre-authorize pump for amount |
| Emergency Stop | estop_Fp_req | Hard-stop pump immediately |
| Cancel Emergency | cancel_FpEstop_req | Resume after emergency stop |
| Soft Lock | close_Fp_req | Soft-lock (close) pump |
| Unlock | open_Fp_req | Re-open pump |
| Query Status | FpStatus_req | Get current pump state |
| Read Transaction | FpSupTrans_req | Lock and read supervised transaction |
| Clear Transaction | clear_FpSupTrans_req | Clear transaction after processing |
| Set Prices | change_FcPriceSet_req | Update fuel prices |

### 2.8 Transaction Retrieval

DOMS uses a **supervised transaction buffer**. Retrieving transactions is a multi-step process:

1. **Buffer status** arrives as unsolicited `FpSupTransBufStatus_resp` on ports 5001-5002
2. To read a transaction: send `FpSupTrans_req` (locks the entry in the buffer)
3. DOMS responds with `FpSupTrans_resp` containing full transaction data
4. After processing: send `clear_FpSupTrans_req` (removes from buffer, equivalent to ACK)

This is similar to Radix's FIFO drain but with explicit lock/read/clear semantics.

### 2.9 Unsolicited Messages (DPP)

DOMS pushes real-time events without a request:

| EXTC | Message | Description |
|------|---------|-------------|
| 0x95 | FpStatus | Pump state changes (idle → calling → fuelling → completed) |
| 0x96 | FpSupTransBufStatus | Transaction buffer update (new transaction available) |
| 0x06 | FpAuthorize | Authorization event |
| 0x07 | FpFuellingData | Live fuelling data (volume/amount during dispense) |
| 0x08 | FpTransactionCompleted | Transaction completed |
| 0x09 | FpTotals | Pump totals/counters |

### 2.10 Data Encoding Conventions

| Data Type | DOMS Encoding | Conversion to Canonical |
|-----------|--------------|------------------------|
| Volume | centilitres (×100) | ÷ 100 → litres → × 1,000,000 → microlitres |
| Amount | ×10 factor | ÷ 10 → major units → × 100 → minor units |
| Pump ID | FpId (integer) | Direct map to `fcc_pump_number` |
| Nozzle ID | SupParam ParId=09 | Integer, maps to `fcc_nozzle_number` |
| Timestamps | Not ISO 8601 (see price format: "20260217095113") | Parse as `yyyyMMddHHmmss`, convert to UTC DateTimeOffset |
| Hex values | "02", "0x09" | Parse as hex byte |

------------------------------------------------------------------------

## 3. Interface Contract Gaps and Required Updates

The real DOMS protocol reveals several gaps in the existing adapter interface contracts that must be addressed before implementation.

### 3.1 Cloud IFccAdapter — No Changes Required

The cloud adapter's role in production is **not to speak DOMS protocol directly**. It receives pre-normalized transactions uploaded by the Edge Agent. The existing interface is sufficient:
- `ValidatePayload()` — validates edge-uploaded canonical payloads
- `NormalizeTransaction()` — passthrough normalization (already canonical)
- `FetchTransactionsAsync()` — used only in VirtualLab testing (REST simulator)
- The cloud adapter works with the DOMS REST Simulator for integration testing

### 3.2 Edge IFccAdapter — Needs TCP Connection Lifecycle

**Current interface (both Kotlin and .NET):**
```
normalize(), sendPreAuth(), getPumpStatus(), heartbeat(), fetchTransactions(), cancelPreAuth()
```

**Missing for TCP-based adapters:**

| Gap | What's Missing | Proposed Addition |
|-----|---------------|-------------------|
| **Connection lifecycle** | No `connect()`/`disconnect()` — HTTP adapters are stateless, TCP needs persistent connection | Add `IFccConnectionLifecycle` interface (see §3.3) |
| **Unsolicited message handling** | No callback mechanism for push events from FCC | Add `IFccEventListener` interface with `onFccEvent()` callback |
| **Transaction acknowledgement** | `fetchTransactions()` returns batch but has no ACK step — DOMS requires explicit `clear_FpSupTrans_req` after processing | Add `acknowledgeTransactions(ids)` method or ACK callback in `TransactionBatch` |
| **Pump control (extended)** | Only `cancelPreAuth()` exists — DOMS supports emergency stop, soft lock, unlock | Add `IFccPumpControl` interface (optional, MVP stretch) |

### 3.3 Proposed New Interfaces

**IFccConnectionLifecycle** (for stateful/TCP adapters):

```kotlin
// Kotlin (Edge Agent)
interface IFccConnectionLifecycle {
    suspend fun connect(): Boolean
    suspend fun disconnect()
    fun isConnected(): Boolean
    fun setEventListener(listener: IFccEventListener?)
}

interface IFccEventListener {
    suspend fun onPumpStatusChanged(fpId: Int, status: PumpStatus)
    suspend fun onTransactionAvailable(notification: TransactionNotification)
    suspend fun onFuellingUpdate(fpId: Int, volume: Long, amount: Long)
    suspend fun onConnectionLost(reason: String)
}
```

```csharp
// C# (Desktop Edge Agent)
public interface IFccConnectionLifecycle
{
    Task<bool> ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    bool IsConnected { get; }
    void SetEventListener(IFccEventListener? listener);
}

public interface IFccEventListener
{
    Task OnPumpStatusChangedAsync(int fpId, PumpStatus status, CancellationToken ct);
    Task OnTransactionAvailableAsync(TransactionNotification notification, CancellationToken ct);
    Task OnFuellingUpdateAsync(int fpId, long volumeMicro, long amountMinor, CancellationToken ct);
    Task OnConnectionLostAsync(string reason, CancellationToken ct);
}
```

**Transaction Acknowledgement** — add to existing `IFccAdapter`:

```kotlin
// Kotlin: add to IFccAdapter
suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean
```

```csharp
// C#: add to IFccAdapter
Task<bool> AcknowledgeTransactionsAsync(IReadOnlyList<string> transactionIds, CancellationToken ct);
```

**Note:** HTTP-based adapters (Petronite, Radix) return no-op `true` for acknowledge and throw `UnsupportedOperationException` for connect/disconnect. The `IFccConnectionLifecycle` interface is optional — only TCP adapters implement it.

### 3.4 SiteFccConfig / AgentFccConfig Updates

The existing `SiteFccConfig` has `ConnectionProtocol` (REST/TCP/SOAP) but the adapter factory doesn't use it for connection management. Updates needed:

| Field | Current | Change |
|-------|---------|--------|
| `ConnectionProtocol` | Exists but unused by factory | Factory must check this to decide stateful vs stateless adapter |
| `JplPort` | Not present | Add: TCP port for JPL channel (default: 8888) |
| `DppPorts` | Not present | Add: List of DPP data ports (default: [5001-5006]) |
| `FcAccessCode` | Not present | Add: DOMS logon credential string |
| `CountryCode` | Not present (exists at legal entity level) | Add: DOMS-specific country code for FcLogon |
| `PosVersionId` | Not present | Add: DOMS POS version identifier |
| `HeartbeatIntervalSeconds` | Not present | Add: configurable (default: 30) |
| `ReconnectBackoffMaxSeconds` | Not present | Add: max reconnect delay (default: 60) |

These can be stored in an `ExtendedConfig` JSON field or as explicit typed fields.

------------------------------------------------------------------------

## 4. Implementation Plan — Component by Component

### 4.0 Implementation Phases

```
Phase 1: Foundation (Interfaces + TCP Core)
  ├── Update adapter interfaces (§3.3)
  ├── Build DOMS TCP/JPL client library (shared between edge agents)
  └── Build DOMS Simulator in VirtualLab

Phase 2: Edge Agent Adapters (Production Path)
  ├── Kotlin Edge Agent DomsAdapter (Android HHT)
  └── .NET Desktop Edge Agent DomsAdapter (Windows)

Phase 3: Cloud Integration
  ├── Update cloud DomsCloudAdapter for edge-uploaded payloads
  └── Portal UI for DOMS-specific configuration

Phase 4: Testing & Validation
  ├── VirtualLab end-to-end scenarios
  ├── Integration tests
  └── Production validation against real DOMS hardware
```

------------------------------------------------------------------------

### 4.1 DOMS Simulator (VirtualLab)

**Location:** `VirtualLab/src/`
**Purpose:** Simulate a DOMS FCC for development and testing without physical hardware.

The VirtualLab already has a profile-driven simulator but it's HTTP/REST-based. For DOMS we need **two simulator modes**:

#### 4.1.1 REST Simulator (Already Partially Supported)

The existing VirtualLab profile system can simulate DOMS via the `doms-like` seed profile. This supports:
- `GET /health` → heartbeat
- `GET /transactions` → pull transactions
- `POST /preauth/create` → create pre-auth
- `POST /preauth/authorize` → authorize pre-auth
- `GET /pump-status` → pump status

**Work needed:**
- Verify the `doms-like` seed profile matches the DOMS REST contract from §5.5
- Add DOMS-specific validation rules (volume > 0, amount > 0, etc.)
- Add DOMS-specific field mappings (centilitres → microlitres)
- Update response templates to match real DOMS data shapes
- Add DOMS pump state machine simulation (14-state model)

| File | Change |
|------|--------|
| `VirtualLab.Infrastructure/FccProfiles/SeedProfileFactory.cs` | Update `doms-like` profile with accurate DOMS field names, validation rules, and state machine |
| `VirtualLab.Infrastructure/Forecourt/ForecourtSimulationService.cs` | Add DOMS-specific pump state transitions (14-state model) |
| `VirtualLab.Domain/Enums/` | Add `DomsPumpState` enum or map to existing states |

#### 4.1.2 TCP/JPL Simulator (New — Required for Edge Agent Testing)

A new TCP server component that speaks the real DOMS JPL protocol. This is essential for testing the edge agent adapters without physical DOMS hardware.

**New files to create:**

```
VirtualLab/src/VirtualLab.Infrastructure/DomsJpl/
├── DomsJplServer.cs              # TCP listener, accepts connections
├── DomsJplSession.cs             # Per-connection session handler
├── DomsJplFrameCodec.cs          # STX/ETX framing encode/decode
├── DomsJplMessageRouter.cs       # Route messages to handlers
├── Handlers/
│   ├── FcLogonHandler.cs         # Authentication handshake
│   ├── FpStatusHandler.cs        # Pump status query/response
│   ├── FpAuthorizeHandler.cs     # Pre-auth command handling
│   ├── FpSupTransHandler.cs      # Transaction lock/read/clear
│   ├── FpControlHandler.cs       # E-stop, close, open
│   └── FcPriceSetHandler.cs      # Price update handling
├── Models/
│   ├── JplMessage.cs             # Message envelope { name, subCode, data }
│   ├── FpMainState.cs            # 14-state enum
│   └── SupplementalParam.cs      # ParId-based parameter model
└── UnsolicitedPusher.cs          # Background: pushes FpStatus, FpTransactionCompleted events
```

**Key implementation details:**

```csharp
// DomsJplFrameCodec.cs
public static class DomsJplFrameCodec
{
    private const byte STX = 0x02;
    private const byte ETX = 0x03;

    public static byte[] Encode(JplMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var frame = new byte[jsonBytes.Length + 2];
        frame[0] = STX;
        Buffer.BlockCopy(jsonBytes, 0, frame, 1, jsonBytes.Length);
        frame[^1] = ETX;
        return frame;
    }

    public static JplMessage? TryDecode(byte[] buffer, out int consumed)
    {
        // Find STX..ETX, extract JSON, deserialize
    }
}
```

**VirtualLab API additions:**

| Endpoint | Purpose |
|----------|---------|
| `POST /api/doms-jpl/start` | Start the TCP/JPL simulator on a configurable port |
| `POST /api/doms-jpl/stop` | Stop the TCP/JPL simulator |
| `GET /api/doms-jpl/status` | Check simulator status, connected clients |
| `POST /api/doms-jpl/push-event` | Manually trigger an unsolicited event (for testing) |
| `POST /api/doms-jpl/set-pump-state` | Set a pump's state (for scenario testing) |

**Registration in `Program.cs`:**
```csharp
// Add DOMS JPL simulator as a hosted service
builder.Services.AddSingleton<DomsJplServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DomsJplServer>());
```

#### 4.1.3 VirtualLab UI Changes (Angular)

**Location:** `VirtualLab/ui/virtual-lab/src/app/features/`

| Component | Change |
|-----------|--------|
| `fcc-profiles/` | Add DOMS-specific profile editor with JPL port config, FcAccessCode, DPP ports, pump state machine visualizer |
| `forecourt-designer/` | Add DOMS pump state machine visualization (14 states with transitions) |
| `live-console/` | Add JPL message inspector: show raw STX/ETX frames, decoded JSON, message classification |
| `dashboard/` | Add DOMS JPL simulator status card (connected clients, message counts, heartbeat status) |

------------------------------------------------------------------------

### 4.2 Edge Agent — Kotlin/Android (Primary Production Path)

**Location:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/`

The Kotlin Edge Agent is the **primary production adapter** for DOMS. It runs on the Urovo i9100 HHT and communicates with the DOMS FCC over station LAN using native TCP/JPL.

#### 4.2.1 Interface Updates

**File:** `adapter/common/IFccAdapter.kt`

Add the new lifecycle and event interfaces:

```kotlin
// New file: adapter/common/IFccConnectionLifecycle.kt
interface IFccConnectionLifecycle {
    suspend fun connect(): Boolean
    suspend fun disconnect()
    fun isConnected(): Boolean
    fun setEventListener(listener: IFccEventListener?)
}

// New file: adapter/common/IFccEventListener.kt
interface IFccEventListener {
    suspend fun onPumpStatusChanged(fpId: Int, status: PumpStatus)
    suspend fun onTransactionAvailable(notification: TransactionNotification)
    suspend fun onFuellingUpdate(fpId: Int, volumeMicro: Long, amountMinor: Long)
    suspend fun onConnectionLost(reason: String)
}
```

Add `acknowledgeTransactions()` to existing `IFccAdapter.kt`:

```kotlin
// Add to IFccAdapter
suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean
```

Update `AdapterTypes.kt`:

```kotlin
// New data class
data class TransactionNotification(
    val fpId: Int,
    val transactionBufferIndex: Int,
    val timestamp: Instant
)
```

**File:** `adapter/common/FccAdapterFactory.kt`

Update to include DOMS in `IMPLEMENTED_VENDORS` once implemented:

```kotlin
private val IMPLEMENTED_VENDORS = setOf(FccVendor.DOMS)
```

#### 4.2.2 TCP/JPL Client Library

**New files:**

```
adapter/doms/
├── DomsAdapter.kt                # Main adapter (replace stub)
├── jpl/
│   ├── JplTcpClient.kt           # TCP socket management + reconnection
│   ├── JplFrameCodec.kt          # STX/ETX encode/decode
│   ├── JplMessageRouter.kt       # Route responses to waiting callers
│   ├── JplMessage.kt             # { name, subCode, data } model
│   └── JplHeartbeatManager.kt    # 30s heartbeat sender + timeout detection
├── protocol/
│   ├── DomsLogonHandler.kt       # FcLogon_req/resp
│   ├── DomsPumpStatusParser.kt   # FpStatus_resp parsing (all SubCodes)
│   ├── DomsTransactionParser.kt  # FpSupTrans_resp / FpTransactionCompleted parsing
│   ├── DomsPreAuthHandler.kt     # authorize_Fp_req/resp
│   ├── DomsPumpControlHandler.kt # estop, close, open
│   └── DomsSupParamParser.kt     # Supplemental parameter (ParId) decoding
├── model/
│   ├── DomsFpMainState.kt        # 14-state enum
│   ├── DomsSupParam.kt           # { parId, value }
│   └── DomsTransactionDto.kt     # Raw DOMS transaction fields
└── mapping/
    └── DomsCanonicalMapper.kt    # DOMS → CanonicalTransaction conversion
```

**Key implementation: JplTcpClient.kt**

```kotlin
class JplTcpClient(
    private val host: String,
    private val port: Int,
    private val scope: CoroutineScope
) {
    private var socket: Socket? = null
    private var inputStream: InputStream? = null
    private var outputStream: OutputStream? = null
    private val pendingRequests = ConcurrentHashMap<String, CompletableDeferred<JplMessage>>()
    private var eventListener: IFccEventListener? = null

    suspend fun connect(): Boolean { /* TCP connect, start read loop */ }
    suspend fun disconnect() { /* Clean shutdown */ }
    suspend fun send(message: JplMessage): JplMessage { /* Send + await response */ }
    fun sendFireAndForget(message: JplMessage) { /* Heartbeat, etc. */ }

    // Background coroutine: reads STX..ETX frames from socket
    private suspend fun readLoop() {
        val buffer = ByteArray(8192)
        while (isActive) {
            val bytesRead = inputStream?.read(buffer) ?: break
            processBuffer(buffer, bytesRead)
        }
    }

    // Decode frame, route to pending request or event listener
    private suspend fun processFrame(frame: ByteArray) {
        val message = JplFrameCodec.decode(frame)
        if (message.isHeartbeat()) return // [STX][ETX] only
        if (message.isSolicited()) {
            pendingRequests[message.correlationKey()]?.complete(message)
        } else {
            // Unsolicited: route to event listener
            routeUnsolicitedMessage(message)
        }
    }
}
```

**Key implementation: DomsAdapter.kt (replace stub)**

```kotlin
class DomsAdapter(
    private val config: AgentFccConfig,
    private val tcpClient: JplTcpClient,
    private val mapper: DomsCanonicalMapper
) : IFccAdapter, IFccConnectionLifecycle {

    override suspend fun connect(): Boolean {
        if (!tcpClient.connect()) return false
        val logonResp = tcpClient.send(buildLogonMessage())
        return logonResp.isSuccess()
    }

    override suspend fun heartbeat(): Boolean {
        return tcpClient.isConnected()
        // Heartbeat is handled by JplHeartbeatManager (30s interval)
    }

    override suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult {
        val jplMsg = JplMessage(
            name = "authorize_Fp_req",
            subCode = "00H",
            data = mapOf(
                "FpId" to command.fccPumpNumber,
                "PresetType" to "Amount",
                "PresetValue" to (command.requestedAmountMinorUnits / 10), // canonical → DOMS encoding
                // ...
            )
        )
        val resp = tcpClient.send(jplMsg)
        return DomsPreAuthHandler.parseResponse(resp)
    }

    override suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch {
        // 1. Check supervised buffer status
        // 2. For each available: send FpSupTrans_req (lock)
        // 3. Parse response
        // 4. Return batch (caller must call acknowledgeTransactions after processing)
        val transactions = mutableListOf<RawPayloadEnvelope>()
        // ... iterate buffer entries ...
        return TransactionBatch(transactions, nextCursor, hasMore)
    }

    override suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean {
        // Send clear_FpSupTrans_req for each transaction
        transactionIds.forEach { id ->
            tcpClient.send(JplMessage("clear_FpSupTrans_req", "00H", mapOf("TransId" to id)))
        }
        return true
    }

    override suspend fun getPumpStatus(): List<PumpStatus> {
        // Query FpStatus for each configured pump
        val statuses = mutableListOf<PumpStatus>()
        for (fpId in config.configuredPumps) {
            val resp = tcpClient.send(JplMessage("FpStatus_req", "03H", mapOf("FpId" to fpId)))
            statuses.add(DomsPumpStatusParser.parse(resp, config))
        }
        return statuses
    }

    override suspend fun normalize(rawPayload: RawPayloadEnvelope): CanonicalTransaction {
        return mapper.toCanonical(rawPayload, config)
    }
}
```

**Key implementation: DomsCanonicalMapper.kt**

```kotlin
class DomsCanonicalMapper {
    fun toCanonical(rawPayload: RawPayloadEnvelope, config: AgentFccConfig): CanonicalTransaction {
        val dto = Json.decodeFromString<DomsTransactionDto>(rawPayload.payload)

        // Volume: DOMS centilitres → microlitres
        // e.g., 520 (5.20L in centilitres) → 5200000 microlitres
        val volumeMicro = dto.volumeRaw * 10_000L  // centilitres × 10000 = microlitres

        // Amount: DOMS ×10 encoding → minor units
        // e.g., 10000 (÷10 = 1000 major) → depends on currency
        val amountMinor = dto.amountRaw * 10L  // DOMS ×10 → ×100 (minor units)

        // Product code mapping
        val productCode = config.productCodeMapping[dto.gradeId] ?: dto.gradeId

        return CanonicalTransaction(
            fccTransactionId = dto.transactionId,
            siteCode = rawPayload.siteCode,
            pumpNumber = dto.fpId,
            nozzleNumber = dto.nozzleId,
            productCode = productCode,
            volumeMicrolitres = volumeMicro,
            amountMinorUnits = amountMinor,
            unitPriceMinorPerLitre = computeUnitPrice(amountMinor, volumeMicro),
            currencyCode = config.currencyCode,
            startedAt = parseDomsTimestamp(dto.startTime),
            completedAt = parseDomsTimestamp(dto.endTime),
            fccVendor = FccVendor.DOMS,
            // ...
        )
    }

    private fun parseDomsTimestamp(raw: String): Instant {
        // "20260217095113" → 2026-02-17T09:51:13Z
        val formatter = DateTimeFormatter.ofPattern("yyyyMMddHHmmss")
        return LocalDateTime.parse(raw, formatter)
            .atZone(ZoneId.of("UTC"))
            .toInstant()
    }
}
```

#### 4.2.3 Integration with Edge Agent Runtime

The existing CadenceController and sync infrastructure need updates:

| File | Change |
|------|--------|
| `runtime/CadenceController.kt` | Check if adapter implements `IFccConnectionLifecycle`; call `connect()` at startup, handle reconnection |
| `di/AppModule.kt` | Register `DomsAdapter` with Koin DI; provide `JplTcpClient` as singleton scoped to adapter lifecycle |
| `ingestion/` | Handle unsolicited transaction notifications from `IFccEventListener.onTransactionAvailable()` |
| `sync/CloudUploadWorker.kt` | No change — uploads canonical transactions regardless of source adapter |
| `config/` | Add DOMS-specific config fields (JplPort, DppPorts, FcAccessCode, etc.) to `AgentFccConfig` |

#### 4.2.4 Android-Specific Considerations

| Concern | Approach |
|---------|----------|
| **TCP on Android** | Use `java.net.Socket` with Kotlin coroutines (`Dispatchers.IO`). Android allows TCP on background threads. |
| **Background persistence** | The TCP connection must survive screen-off. Use Android `ForegroundService` with persistent notification. |
| **WiFi LAN requirement** | TCP only works over WiFi (not cellular). Must validate `NetworkCapabilities.TRANSPORT_WIFI` for FCC connection. |
| **Battery impact** | 30s heartbeat is reasonable. DPP ports (5001-5006) need assessment — can we use a single JPL connection instead? |
| **Reconnection** | On WiFi disconnect/reconnect: re-establish TCP, re-send FcLogon, re-subscribe to unsolicited events. |

------------------------------------------------------------------------

### 4.3 Desktop Edge Agent — .NET (Windows)

**Location:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/`

The .NET Desktop Edge Agent mirrors the Kotlin adapter but for Windows deployment.

#### 4.3.1 Interface Updates

Same additions as Kotlin (§4.2.1) but in C#:

**New file:** `Adapter/Common/IFccConnectionLifecycle.cs`
**New file:** `Adapter/Common/IFccEventListener.cs`

Add `AcknowledgeTransactionsAsync()` to existing `IFccAdapter.cs`.

#### 4.3.2 TCP/JPL Client Library

**New files:**

```
Adapter/Doms/
├── DomsAdapter.cs                  # Replace current HTTP-based implementation
├── Jpl/
│   ├── JplTcpClient.cs             # TcpClient wrapper with reconnection
│   ├── JplFrameCodec.cs            # STX/ETX framing
│   ├── JplMessageRouter.cs         # Response correlation
│   ├── JplMessage.cs               # Message model
│   └── JplHeartbeatManager.cs      # 30s keepalive
├── Protocol/
│   ├── DomsLogonHandler.cs
│   ├── DomsPumpStatusParser.cs
│   ├── DomsTransactionParser.cs
│   ├── DomsPreAuthHandler.cs
│   └── DomsSupParamParser.cs
├── Model/
│   ├── DomsFpMainState.cs
│   └── DomsTransactionDto.cs
└── Mapping/
    └── DomsCanonicalMapper.cs
```

**Key difference from current DomsAdapter.cs:** The current desktop adapter uses `IHttpClientFactory` and REST endpoints. This must be **replaced** with TCP/JPL:

| Current (HTTP) | New (TCP/JPL) |
|----------------|---------------|
| `_httpFactory.CreateClient("fcc")` | `JplTcpClient` (persistent connection) |
| `SendAsync(HttpMethod.Post, "/api/v1/preauth", ...)` | `tcpClient.SendAsync(JplMessage("authorize_Fp_req", ...))` |
| `SendAsync(HttpMethod.Get, "/api/v1/transactions", ...)` | `tcpClient.SendAsync(JplMessage("FpSupTrans_req", ...))` |
| `SendAsync(HttpMethod.Get, "/api/v1/pump-status", ...)` | `tcpClient.SendAsync(JplMessage("FpStatus_req", ...))` |
| `SendAsync(HttpMethod.Get, "/api/v1/heartbeat", ...)` | `tcpClient.IsConnected` (heartbeat is background) |
| Stateless per-request | Stateful persistent connection |

**Important:** The current `DomsProtocolDtos.cs` records (for REST JSON shapes) remain useful for the REST simulator test path but must NOT be used for real DOMS communication. Create new DTOs in `Model/` for JPL messages.

#### 4.3.3 Factory Update

**File:** `Adapter/FccAdapterFactory.cs`

```csharp
public IFccAdapter Create(FccVendor vendor, FccConnectionConfig config) => vendor switch
{
    FccVendor.Doms => config.ConnectionProtocol switch
    {
        "TCP" => new DomsJplAdapter(/* JplTcpClient, mapper */),
        "REST" => new DomsAdapter(_httpFactory, config, _loggerFactory.CreateLogger<DomsAdapter>()),
        _ => throw new ArgumentException($"Unsupported protocol for DOMS: {config.ConnectionProtocol}")
    },
    _ => throw new ArgumentException($"Unknown FCC vendor: {vendor}", nameof(vendor))
};
```

This allows both TCP (production) and REST (testing with VirtualLab simulator) modes.

------------------------------------------------------------------------

### 4.4 Cloud Backend

**Location:** `src/cloud/`

#### 4.4.1 DomsCloudAdapter Updates

**File:** `FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs`

The cloud adapter does **NOT** need TCP/JPL support. Its responsibilities:

1. **Validate edge-uploaded payloads** — These are already in canonical format from the Edge Agent
2. **Normalize edge-uploaded payloads** — Mostly passthrough, with optional field enrichment
3. **Fetch transactions from DOMS REST Simulator** — VirtualLab testing only
4. **No direct FCC communication in production** — Edge Agent handles that

**Changes needed:**

| Area | Change |
|------|--------|
| `ValidatePayload()` | Add validation for edge-uploaded canonical payloads (verify required fields present) |
| `NormalizeTransaction()` | Add handling for edge-uploaded format (already canonical, just verify/enrich) |
| `FetchTransactionsAsync()` | Keep as-is for REST simulator; add note that this is test-only |
| `GetAdapterMetadata()` | Keep `SupportsPreAuth = false`, `SupportsPumpStatus = false` |

**New file:** `FccMiddleware.Adapter.Doms/Internal/DomsEdgeUploadDto.cs`
- DTO for transactions uploaded by the Edge Agent (may differ slightly from REST simulator format)

#### 4.4.2 Ingestion Pipeline

**File:** `FccMiddleware.Application/Ingestion/`

The cloud ingestion pipeline must handle two DOMS data paths:

```
Path 1: Edge Agent Upload (Production)
  POST /api/v1/agent/transactions/upload
  → Body: list of CanonicalTransaction (already normalized by Edge Agent)
  → Cloud validates, deduplicates, stores as PENDING

Path 2: REST Simulator Push (VirtualLab Testing)
  POST /api/v1/fcc/doms/webhook  (or generic push endpoint)
  → Body: DOMS REST format (§5.5)
  → Cloud validates via DomsCloudAdapter, normalizes, deduplicates, stores as PENDING
```

Both paths converge at deduplication (REQ-13) and then storage.

#### 4.4.3 Configuration Updates

**File:** `FccMiddleware.Domain/Models/Adapter/SiteFccConfig.cs`

Add DOMS-specific configuration fields:

```csharp
// Existing fields remain unchanged

// DOMS TCP/JPL specific (used by edge agent, stored in cloud for config push)
public int? JplPort { get; init; }                    // Default: 8888
public List<int>? DppPorts { get; init; }             // Default: [5001-5006]
public string? FcAccessCode { get; init; }            // DOMS logon credential
public string? DomsCountryCode { get; init; }         // DOMS-specific country code
public string? PosVersionId { get; init; }            // DOMS POS version ID
public int? HeartbeatIntervalSeconds { get; init; }   // Default: 30
public int? ReconnectBackoffMaxSeconds { get; init; } // Default: 60
```

**File:** `FccMiddleware.Infrastructure/Config/` or migration

Add these fields to the database schema and the config push mechanism so they reach the Edge Agent.

------------------------------------------------------------------------

### 4.5 Portal Frontend (Angular)

**Location:** `src/portal/src/app/features/`

#### 4.5.1 FCC Configuration Screen

When `fccVendor = DOMS`, the configuration form must show DOMS-specific fields:

| Field | UI Control | Notes |
|-------|-----------|-------|
| JPL Port | Number input (default: 8888) | TCP port for JPL channel |
| FcAccessCode | Text input (masked/sensitive) | DOMS logon credential |
| DOMS Country Code | Text input | e.g., "0045" |
| POS Version ID | Text input | e.g., "1234" |
| Heartbeat Interval | Slider (15-60s, default: 30) | Keepalive frequency |
| Reconnect Max Backoff | Slider (30-300s, default: 60) | Max reconnect delay |

#### 4.5.2 DOMS Monitoring Dashboard

| Widget | Data Source |
|--------|------------|
| TCP Connection Status | Agent telemetry (connected/disconnected/reconnecting) |
| Heartbeat Timeline | Agent telemetry (last heartbeat timestamps) |
| Pump State Grid | Agent telemetry (14-state with color coding) |
| JPL Message Log | Agent telemetry (recent messages, filterable by type) |
| Unsolicited Event Feed | Agent telemetry (real-time FpStatus changes) |

#### 4.5.3 Pump State Machine Visualizer

A visual component showing the DOMS 14-state pump state machine with:
- Current state highlighted
- State transition arrows
- Time-in-state indicators
- Error state alerting

------------------------------------------------------------------------

## 5. Data Conversion Reference

### 5.1 Volume Conversions

```
DOMS raw (centilitres, ×100)  →  Litres  →  Canonical (microlitres)

Example: DOMS sends 520
  520 ÷ 100 = 5.20 litres
  5.20 × 1,000,000 = 5,200,000 microlitres

Shortcut: DOMS × 10,000 = microlitres
  520 × 10,000 = 5,200,000 ✓
```

### 5.2 Amount Conversions

```
DOMS raw (×10 factor)  →  Major units  →  Canonical (minor units / cents)

Example: DOMS sends 10000 (for 1,000 TZS)
  10000 ÷ 10 = 1,000.00 TZS (major units)
  1,000.00 × 100 = 100,000 minor units (cents)

Shortcut: DOMS × 10 = minor units
  10000 × 10 = 100,000 ✓

NOTE: Currency-dependent. TZS uses ×100 for minor. Some currencies differ.
Must use currency-specific conversion from legal entity config.
```

### 5.3 Timestamp Conversion

```
DOMS format: "20260217095113" (yyyyMMddHHmmss, local to FCC timezone)
Canonical format: ISO 8601 UTC → "2026-02-17T09:51:13Z" (after timezone conversion)

Steps:
  1. Parse as LocalDateTime using pattern "yyyyMMddHHmmss"
  2. Apply FCC timezone from config (e.g., Africa/Dar_es_Salaam)
  3. Convert to UTC DateTimeOffset/Instant
```

### 5.4 Pump/Nozzle Mapping

```
DOMS FpId (filling point)  →  fcc_pump_number (middleware DB)  →  pump_number (Odoo)
DOMS NozzleId (SupParam 09) →  fcc_nozzle_number  →  odoo_nozzle_number

Pre-auth direction (Odoo → FCC):
  Odoo pump_number → lookup pumps table → fcc_pump_number → DOMS FpId

Transaction direction (FCC → Odoo):
  DOMS FpId → fcc_pump_number → lookup pumps table → pump_number (Odoo)
```

------------------------------------------------------------------------

## 6. Testing Strategy

### 6.1 Unit Tests

| Component | Tests | Location |
|-----------|-------|----------|
| JplFrameCodec (Kotlin) | Encode/decode STX/ETX frames, heartbeat detection, malformed frames | `edge-agent/app/src/test/` |
| JplFrameCodec (.NET) | Same as Kotlin | `desktop-edge-agent/tests/` |
| DomsCanonicalMapper | Volume conversion (centilitres → microlitres), amount conversion, timestamp parsing, product code mapping, pump state mapping | Both edge agents |
| DomsSupParamParser | Parse all 15 ParId types, handle missing/malformed params | Both edge agents |
| DomsPumpStatusParser | Map all 14 FpMainState values to canonical PumpState | Both edge agents |
| DomsCloudAdapter | Validate edge-uploaded payloads, normalize passthrough | `cloud/FccMiddleware.Adapter.Doms.Tests/` |

### 6.2 Integration Tests

| Scenario | Components | Description |
|----------|-----------|-------------|
| JPL Handshake | Edge Agent + VirtualLab JPL Simulator | TCP connect → FcLogon → verify session established |
| Pre-Auth Flow | Edge Agent + VirtualLab | Send authorize_Fp_req → verify pump state transitions → verify pre-auth record queued to cloud |
| Transaction Retrieval | Edge Agent + VirtualLab | Trigger dispense in simulator → Edge Agent fetches from buffer → verify canonical conversion → verify cloud upload |
| Unsolicited Events | Edge Agent + VirtualLab | Simulator pushes FpStatus change → Edge Agent receives via event listener → verify PumpStatus update |
| Reconnection | Edge Agent + VirtualLab | Kill TCP connection → verify automatic reconnect with backoff → verify re-logon |
| End-to-End | Edge Agent + VirtualLab + Cloud | Dispense → Edge Agent ingests → uploads to cloud → Odoo polls → acknowledges → SYNCED_TO_ODOO |

### 6.3 VirtualLab Scenario Definitions

Add DOMS-specific scenarios to the scenario runner:

| Scenario | Steps |
|----------|-------|
| `doms-basic-dispense` | Start idle pump → lift nozzle (Calling) → authorize (PreAuthorized) → dispense (Fuelling) → complete → verify transaction in buffer |
| `doms-preauth-flow` | Create pre-auth in Odoo → Edge Agent sends authorize_Fp_req → pump authorized → dispense → reconciliation |
| `doms-reconnect` | Disconnect TCP mid-session → verify Edge Agent reconnects → verify no data loss |
| `doms-multi-pump` | Simultaneous operations on 4+ pumps → verify independent state machines |
| `doms-emergency-stop` | Dispense in progress → emergency stop → verify pump blocked → unblock → verify resumed |

### 6.4 Test Fixtures

**New JSON fixtures needed:**

```
edge-agent fixtures:
├── doms-jpl-logon-request.json
├── doms-jpl-logon-response-success.json
├── doms-jpl-logon-response-failure.json
├── doms-jpl-fpstatus-idle.json
├── doms-jpl-fpstatus-fuelling.json
├── doms-jpl-fpstatus-completed.json
├── doms-jpl-authorize-request.json
├── doms-jpl-authorize-response-success.json
├── doms-jpl-authorize-response-failure.json
├── doms-jpl-transaction-buffer-status.json
├── doms-jpl-transaction-read.json
├── doms-jpl-heartbeat.bin  (0x02 0x03)
└── doms-jpl-unsolicited-transaction-completed.json
```

------------------------------------------------------------------------

## 7. Migration Path from Current Implementation

### 7.1 What to Keep

| Component | Current State | Action |
|-----------|--------------|--------|
| Cloud `DomsCloudAdapter.cs` | REST-based, works with VirtualLab simulator | **Keep as-is** — used for VirtualLab testing path |
| Cloud adapter tests | REST validation and normalization tests | **Keep** — add new tests for edge-upload validation |
| Desktop `DomsProtocolDtos.cs` | REST DTOs | **Keep** alongside new JPL DTOs — REST mode remains for VirtualLab testing |
| Edge agent `DomsAdapter.kt` (stub) | All methods throw | **Replace** with full JPL implementation |
| VirtualLab `doms-like` profile | REST simulator profile | **Keep and enhance** — primary testing tool |

### 7.2 What to Replace/Add

| Component | Action |
|-----------|--------|
| Desktop `DomsAdapter.cs` | Refactor: HTTP → TCP/JPL. Keep REST as fallback option via factory |
| Edge agent `DomsAdapter.kt` | Full implementation from stub |
| Edge agent adapter interfaces | Add `IFccConnectionLifecycle`, `IFccEventListener`, `acknowledgeTransactions()` |
| Desktop agent adapter interfaces | Same additions as Kotlin |
| VirtualLab | Add TCP/JPL simulator alongside existing REST simulator |
| Portal | Add DOMS-specific config fields and monitoring widgets |

### 7.3 Backward Compatibility

- The REST-based path (VirtualLab simulator) remains functional. No existing tests break.
- The factory pattern (`FccAdapterFactory`) selects TCP vs REST based on `ConnectionProtocol`.
- Edge agents that haven't been updated continue to use the REST simulator for development.
- The new interfaces (`IFccConnectionLifecycle`, `IFccEventListener`) are optional — HTTP-based adapters don't implement them.

------------------------------------------------------------------------

## 8. Task Breakdown and Sequencing

### Phase 1: Foundation (Estimated: ~10 tasks)

| # | Task | Component | Depends On | Priority |
|---|------|-----------|-----------|----------|
| 1.1 | Add `IFccConnectionLifecycle` interface | Edge Agent (Kotlin) | — | P0 |
| 1.2 | Add `IFccEventListener` interface | Edge Agent (Kotlin) | — | P0 |
| 1.3 | Add `acknowledgeTransactions()` to `IFccAdapter` | Edge Agent (Kotlin) | — | P0 |
| 1.4 | Mirror interface updates to Desktop Agent (.NET) | Desktop Agent | 1.1-1.3 | P0 |
| 1.5 | Add DOMS TCP/JPL config fields to `SiteFccConfig` | Cloud Domain | — | P0 |
| 1.6 | Add DOMS TCP/JPL config fields to `AgentFccConfig` | Edge Agent (Kotlin) | — | P0 |
| 1.7 | Build `JplFrameCodec` (Kotlin) | Edge Agent | — | P0 |
| 1.8 | Build `JplFrameCodec` (.NET) | Desktop Agent | — | P0 |
| 1.9 | Build `DomsCanonicalMapper` (Kotlin) with unit tests | Edge Agent | — | P0 |
| 1.10 | Build `DomsCanonicalMapper` (.NET) with unit tests | Desktop Agent | — | P0 |

### Phase 2: DOMS Simulator (Estimated: ~8 tasks)

| # | Task | Component | Depends On | Priority |
|---|------|-----------|-----------|----------|
| 2.1 | Build `DomsJplServer` TCP listener | VirtualLab | 1.7 | P0 |
| 2.2 | Build `DomsJplSession` with frame codec | VirtualLab | 2.1 | P0 |
| 2.3 | Implement `FcLogonHandler` | VirtualLab | 2.2 | P0 |
| 2.4 | Implement `FpStatusHandler` (14-state model) | VirtualLab | 2.2 | P0 |
| 2.5 | Implement `FpAuthorizeHandler` (pre-auth) | VirtualLab | 2.4 | P0 |
| 2.6 | Implement `FpSupTransHandler` (transaction buffer) | VirtualLab | 2.4 | P0 |
| 2.7 | Implement `UnsolicitedPusher` (event push) | VirtualLab | 2.4 | P1 |
| 2.8 | Add VirtualLab API endpoints for JPL simulator control | VirtualLab | 2.1 | P1 |

### Phase 3: Edge Agent Implementation (Estimated: ~12 tasks)

| # | Task | Component | Depends On | Priority |
|---|------|-----------|-----------|----------|
| 3.1 | Build `JplTcpClient` with reconnection | Edge Agent (Kotlin) | 1.7 | P0 |
| 3.2 | Build `JplHeartbeatManager` | Edge Agent (Kotlin) | 3.1 | P0 |
| 3.3 | Build `JplMessageRouter` | Edge Agent (Kotlin) | 3.1 | P0 |
| 3.4 | Implement `DomsLogonHandler` | Edge Agent (Kotlin) | 3.1 | P0 |
| 3.5 | Implement `DomsPumpStatusParser` (all SubCodes) | Edge Agent (Kotlin) | 1.9 | P0 |
| 3.6 | Implement `DomsTransactionParser` | Edge Agent (Kotlin) | 1.9 | P0 |
| 3.7 | Implement `DomsPreAuthHandler` | Edge Agent (Kotlin) | 3.4 | P0 |
| 3.8 | Replace `DomsAdapter.kt` stub with full implementation | Edge Agent (Kotlin) | 3.1-3.7 | P0 |
| 3.9 | Update `FccAdapterFactory.kt` (add DOMS to IMPLEMENTED_VENDORS) | Edge Agent (Kotlin) | 3.8 | P0 |
| 3.10 | Update `CadenceController.kt` for TCP lifecycle | Edge Agent (Kotlin) | 3.8 | P0 |
| 3.11 | Integration test: Edge Agent ↔ VirtualLab JPL Simulator | Edge Agent + VirtualLab | 3.8, 2.6 | P0 |
| 3.12 | Mirror full implementation to Desktop Agent (.NET) | Desktop Agent | 3.8 | P0 |

### Phase 4: Cloud & Portal (Estimated: ~6 tasks)

| # | Task | Component | Depends On | Priority |
|---|------|-----------|-----------|----------|
| 4.1 | Update `DomsCloudAdapter` for edge-upload validation | Cloud | 1.5 | P1 |
| 4.2 | Add DOMS config fields to cloud DB migration | Cloud | 1.5 | P1 |
| 4.3 | Add DOMS config fields to config push mechanism | Cloud | 4.2 | P1 |
| 4.4 | Add DOMS-specific fields to Portal FCC config screen | Portal | 4.2 | P1 |
| 4.5 | Add DOMS pump state visualizer to Portal monitoring | Portal | — | P2 |
| 4.6 | Add JPL message inspector to VirtualLab UI | VirtualLab UI | 2.8 | P2 |

### Phase 5: Testing & Validation (Estimated: ~5 tasks)

| # | Task | Component | Depends On | Priority |
|---|------|-----------|-----------|----------|
| 5.1 | Write unit tests for all parsers and mappers | All | 3.8 | P0 |
| 5.2 | Write VirtualLab scenario definitions (§6.3) | VirtualLab | 2.6, 3.8 | P0 |
| 5.3 | End-to-end test: Edge Agent → Cloud → Odoo poll | All | 3.11, 4.1 | P0 |
| 5.4 | Performance validation against benchmark guardrails | All | 5.3 | P1 |
| 5.5 | Validation against real DOMS hardware (if available) | Edge Agent | 3.8 | P0 (when hardware available) |

------------------------------------------------------------------------

## 9. Risk Register

| Risk | Impact | Mitigation |
|------|--------|------------|
| Real DOMS protocol details may differ from DOMSRealImplementation | High — wrong message formats | Validate against actual DOMS hardware ASAP; DOMSRealImplementation is production-tested reference |
| TCP persistent connection on Android may have battery/lifecycle issues | Medium — service killed by OS | Use `ForegroundService` with persistent notification; test on Urovo i9100 specifically |
| Multi-port DPP (5001-5006) may not be needed if JPL port handles all events | Medium — unnecessary complexity | Investigate: can we subscribe to all events via JPL port only using `UnsolicitedApcList`? DOMSRealImplementation suggests yes. |
| Volume/amount conversion factors may vary by DOMS firmware version | High — financial data corruption | Parameterize conversion factors in config; validate with test transactions on real hardware |
| JplTcpClient shared between Kotlin and .NET creates maintenance burden | Low — protocol is stable | Use same test fixtures; share protocol documentation; consider code generation |
| VirtualLab JPL simulator complexity | Medium — delays testing | Start with minimal subset (logon, status, authorize, transaction read); add features incrementally |

------------------------------------------------------------------------

## 10. Open Questions

| ID | Question | Status | Impact |
|----|----------|--------|--------|
| DQ-1 | Can all unsolicited events be received via the main JPL port (8888) using `UnsolicitedApcList`, or are DPP ports (5001-5006) required? | Open | Simplifies implementation if single-port |
| DQ-2 | What is the exact authorize_Fp_req message format for amount-based pre-auth? DOMSRealImplementation focuses on status/transactions but doesn't show a complete pre-auth example. | Open | Blocks pre-auth implementation |
| DQ-3 | Does the DOMS firmware version deployed in our sites match the DOMSRealImplementation protocol? | Open | Blocks production validation |
| DQ-4 | Is the `×10` amount encoding consistent across all DOMS deployments (TZ, MW, etc.)? | Open | Financial data integrity |
| DQ-5 | Can we get access to a DOMS FCC in a test environment for live protocol validation? | Open | Blocks Phase 5.5 |
| DQ-6 | Does the Urovo i9100 handle persistent TCP connections reliably under field conditions (WiFi roaming, screen-off, power save)? | Open | May need Android-specific workarounds |

------------------------------------------------------------------------

## 11. File Change Summary

### New Files

| File | Component | Purpose |
|------|-----------|---------|
| `edge-agent/.../adapter/common/IFccConnectionLifecycle.kt` | Edge Agent | TCP lifecycle interface |
| `edge-agent/.../adapter/common/IFccEventListener.kt` | Edge Agent | Unsolicited event callback |
| `edge-agent/.../adapter/doms/jpl/JplTcpClient.kt` | Edge Agent | TCP client with reconnection |
| `edge-agent/.../adapter/doms/jpl/JplFrameCodec.kt` | Edge Agent | STX/ETX framing |
| `edge-agent/.../adapter/doms/jpl/JplMessageRouter.kt` | Edge Agent | Response correlation |
| `edge-agent/.../adapter/doms/jpl/JplMessage.kt` | Edge Agent | Message model |
| `edge-agent/.../adapter/doms/jpl/JplHeartbeatManager.kt` | Edge Agent | 30s keepalive |
| `edge-agent/.../adapter/doms/protocol/DomsLogonHandler.kt` | Edge Agent | FcLogon handshake |
| `edge-agent/.../adapter/doms/protocol/DomsPumpStatusParser.kt` | Edge Agent | FpStatus parsing |
| `edge-agent/.../adapter/doms/protocol/DomsTransactionParser.kt` | Edge Agent | Transaction parsing |
| `edge-agent/.../adapter/doms/protocol/DomsPreAuthHandler.kt` | Edge Agent | Pre-auth handling |
| `edge-agent/.../adapter/doms/protocol/DomsSupParamParser.kt` | Edge Agent | Supplemental params |
| `edge-agent/.../adapter/doms/model/DomsFpMainState.kt` | Edge Agent | 14-state enum |
| `edge-agent/.../adapter/doms/model/DomsTransactionDto.kt` | Edge Agent | Raw DOMS fields |
| `edge-agent/.../adapter/doms/mapping/DomsCanonicalMapper.kt` | Edge Agent | DOMS → canonical |
| `desktop-edge-agent/.../Adapter/Common/IFccConnectionLifecycle.cs` | Desktop Agent | TCP lifecycle |
| `desktop-edge-agent/.../Adapter/Common/IFccEventListener.cs` | Desktop Agent | Event callbacks |
| `desktop-edge-agent/.../Adapter/Doms/Jpl/*.cs` | Desktop Agent | JPL client (mirrors Kotlin) |
| `desktop-edge-agent/.../Adapter/Doms/Protocol/*.cs` | Desktop Agent | Protocol handlers |
| `desktop-edge-agent/.../Adapter/Doms/Model/*.cs` | Desktop Agent | DOMS models |
| `desktop-edge-agent/.../Adapter/Doms/Mapping/*.cs` | Desktop Agent | DOMS mapper |
| `VirtualLab/.../DomsJpl/DomsJplServer.cs` | VirtualLab | TCP simulator server |
| `VirtualLab/.../DomsJpl/DomsJplSession.cs` | VirtualLab | Per-connection handler |
| `VirtualLab/.../DomsJpl/DomsJplFrameCodec.cs` | VirtualLab | Frame codec |
| `VirtualLab/.../DomsJpl/DomsJplMessageRouter.cs` | VirtualLab | Message routing |
| `VirtualLab/.../DomsJpl/Handlers/*.cs` | VirtualLab | Message handlers (6 files) |
| `VirtualLab/.../DomsJpl/Models/*.cs` | VirtualLab | JPL models |
| `VirtualLab/.../DomsJpl/UnsolicitedPusher.cs` | VirtualLab | Event push engine |

### Modified Files

| File | Component | Change |
|------|-----------|--------|
| `edge-agent/.../adapter/doms/DomsAdapter.kt` | Edge Agent | **Replace** stub with full TCP/JPL implementation |
| `edge-agent/.../adapter/common/IFccAdapter.kt` | Edge Agent | Add `acknowledgeTransactions()` |
| `edge-agent/.../adapter/common/FccAdapterFactory.kt` | Edge Agent | Add DOMS to IMPLEMENTED_VENDORS |
| `edge-agent/.../adapter/common/AdapterTypes.kt` | Edge Agent | Add `TransactionNotification`, DOMS config fields |
| `edge-agent/.../runtime/CadenceController.kt` | Edge Agent | Handle TCP connection lifecycle |
| `edge-agent/.../di/AppModule.kt` | Edge Agent | Register DomsAdapter + JplTcpClient in Koin |
| `desktop-edge-agent/.../Adapter/Common/IFccAdapter.cs` | Desktop Agent | Add `AcknowledgeTransactionsAsync()` |
| `desktop-edge-agent/.../Adapter/FccAdapterFactory.cs` | Desktop Agent | Add TCP/REST selection for DOMS |
| `cloud/.../Domain/Models/Adapter/SiteFccConfig.cs` | Cloud | Add DOMS-specific config fields |
| `cloud/.../Adapter.Doms/DomsCloudAdapter.cs` | Cloud | Add edge-upload validation path |
| `VirtualLab/.../Api/Program.cs` | VirtualLab | Register DomsJplServer hosted service |
| `VirtualLab/.../FccProfiles/SeedProfileFactory.cs` | VirtualLab | Update doms-like profile |
| `portal/.../features/` | Portal | Add DOMS config fields to FCC config screen |
