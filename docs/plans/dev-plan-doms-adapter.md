# DOMS FCC Adapter — Phased Development Plan

**Agent System Prompt:** Always prepend `docs/plans/agent-prompt-doms-adapter.md` when assigning any task below.

**Reference Document:** `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — the DOMS protocol deep dive and integration analysis. Every task below references sections of that document.

**Sprint Cadence:** 2-week sprints

---

## Architecture Decision: Dual-Protocol Strategy

The real DOMS protocol is **TCP/JPL with binary framing**, not REST/JSON. This means:

1. **Edge agents (Kotlin + .NET) must implement native TCP/JPL** for production use against real DOMS hardware.
2. **Existing REST-based adapters (Desktop .NET + Cloud) remain** for VirtualLab testing.
3. **The factory selects TCP vs REST** based on `ConnectionProtocol` in site config.
4. **The cloud adapter never speaks TCP/JPL** — it receives pre-normalized data from Edge Agent uploads.
5. **VirtualLab needs a TCP/JPL simulator** so edge agents can be tested without physical DOMS hardware.

```
Production Path (Edge Agent — Native JPL/TCP):
  [DOMS FCC] ←TCP/JPL→ [Edge Agent DomsAdapter] → normalize → buffer/upload → [Cloud]

Testing Path (VirtualLab — REST Simulator):
  [DOMS REST Simulator] ←HTTP/JSON→ [Cloud DomsCloudAdapter] → normalize → store

Cloud Adapter Role (Production):
  Receives pre-normalized transactions from Edge Agent uploads
  Performs validation, dedup, and storage — NOT raw DOMS protocol handling
```

---

## Current Implementation Status

| Component | Location | Status | Action |
|-----------|----------|--------|--------|
| Edge Agent (Kotlin) DomsAdapter | `src/edge-agent/.../adapter/doms/DomsAdapter.kt` | **STUB** — all methods throw | **Replace** with full TCP/JPL implementation |
| Desktop Agent (C#) DomsAdapter | `src/desktop-edge-agent/.../Adapter/Doms/DomsAdapter.cs` | **COMPLETE** — REST-based, fully tested | **Keep** as-is; add new `DomsJplAdapter` alongside |
| Cloud DomsCloudAdapter | `src/cloud/FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs` | **COMPLETE** — REST-based, fully tested | **Minor update** for edge-upload validation |
| VirtualLab | `VirtualLab/src/` | **No DOMS TCP/JPL** — REST profiles only | **Add** TCP/JPL simulator |
| Portal FCC Config | `src/portal/src/app/features/site-config/` | **Basic** — generic form | **Add** DOMS-specific fields |
| Edge Agent Interfaces | `src/edge-agent/.../adapter/common/` | **Complete** for REST adapters | **Extend** with TCP lifecycle + event listener |

---

## Phase 0 — Foundation: Interface Updates & Shared Types (Sprint 1)

### DOMS-0.1: Add IFccConnectionLifecycle Interface (Edge Agent — Kotlin)

**Sprint:** 1
**Component:** Edge Agent (Kotlin)
**Prereqs:** None (EA-0.4 must be complete — interfaces already exist)
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §3.2 (interface gaps), §3.3 (proposed interfaces)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/IFccAdapter.kt` — current interface
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/AdapterTypes.kt` — current shared types

**Task:**
Add the TCP connection lifecycle interface for stateful/TCP-based FCC adapters. This is needed because HTTP-based adapters (Radix, Petronite) are stateless per-request, but DOMS requires a persistent TCP connection with explicit connect/disconnect.

**Detailed instructions:**
1. Create `adapter/common/IFccConnectionLifecycle.kt`:
   ```kotlin
   interface IFccConnectionLifecycle {
       suspend fun connect(): Boolean
       suspend fun disconnect()
       fun isConnected(): Boolean
       fun setEventListener(listener: IFccEventListener?)
   }
   ```
2. This interface is **optional** — only TCP-based adapters implement it. HTTP adapters do NOT need to implement it.
3. Do NOT modify `IFccAdapter` to extend this. Keep them separate — the factory and CadenceController check `is IFccConnectionLifecycle` at runtime.
4. Add KDoc explaining the purpose: persistent connection management for stateful FCC protocols (TCP/JPL).

**Acceptance criteria:**
- `IFccConnectionLifecycle.kt` compiles with 4 method signatures
- Interface is in `adapter/common/` package alongside `IFccAdapter`
- No changes to `IFccAdapter` itself
- Existing adapter code (DomsAdapter stub, factory) compiles without modification

---

### DOMS-0.2: Add IFccEventListener Interface (Edge Agent — Kotlin)

**Sprint:** 1
**Component:** Edge Agent (Kotlin)
**Prereqs:** None
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §2.9 (unsolicited messages), §3.3 (IFccEventListener)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/PumpStatus.kt` — PumpStatus model

**Task:**
Add the event listener interface for receiving unsolicited push events from stateful FCC connections. DOMS pushes FpStatus changes, transaction buffer updates, and live fuelling data without a request.

**Detailed instructions:**
1. Create `adapter/common/IFccEventListener.kt`:
   ```kotlin
   interface IFccEventListener {
       suspend fun onPumpStatusChanged(fpId: Int, status: PumpStatus)
       suspend fun onTransactionAvailable(notification: TransactionNotification)
       suspend fun onFuellingUpdate(fpId: Int, volumeMicro: Long, amountMinor: Long)
       suspend fun onConnectionLost(reason: String)
   }
   ```
2. All callbacks are `suspend` for coroutine compatibility.
3. `onConnectionLost` is called when TCP connection drops — the adapter may auto-reconnect, but the orchestrator needs to know.

**Acceptance criteria:**
- `IFccEventListener.kt` compiles with 4 callback method signatures
- All callbacks are `suspend fun`
- Uses existing `PumpStatus` type from `adapter/common/`
- Uses `TransactionNotification` from DOMS-0.3

---

### DOMS-0.3: Add acknowledgeTransactions and TransactionNotification (Edge Agent — Kotlin)

**Sprint:** 1
**Component:** Edge Agent (Kotlin)
**Prereqs:** None
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §2.8 (transaction retrieval — lock/read/clear), §3.2 (interface gaps)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/IFccAdapter.kt` — current interface
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/AdapterTypes.kt` — current types

**Task:**
Add `acknowledgeTransactions()` to `IFccAdapter` and add the `TransactionNotification` data class. DOMS requires explicit transaction clearing after processing (clear_FpSupTrans_req). Without this, transactions remain locked in the DOMS supervised buffer.

**Detailed instructions:**
1. Add to `IFccAdapter.kt`:
   ```kotlin
   suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean
   ```
2. Add to `AdapterTypes.kt`:
   ```kotlin
   @Serializable
   data class TransactionNotification(
       val fpId: Int,
       val transactionBufferIndex: Int,
       val timestamp: String // ISO 8601 UTC
   )
   ```
3. Update the DomsAdapter stub to include `acknowledgeTransactions()` — it should throw `UnsupportedOperationException` like the other stub methods.
4. **Default behavior for REST adapters**: Radix and Petronite should return `true` (no-op) since their transaction acknowledgement is implicit (HTTP 200 or cursor advance).
5. Document in `IFccAdapter` KDoc: "Required for FCC protocols with explicit transaction clearing (e.g., DOMS supervised buffer). REST-based adapters may return true as a no-op."

**Acceptance criteria:**
- `acknowledgeTransactions()` is on the `IFccAdapter` interface
- `TransactionNotification` data class is in `AdapterTypes.kt`
- DomsAdapter stub updated with the new method (throws)
- All existing code compiles (FccAdapterFactory, tests, etc.)
- No breaking changes to any existing adapter

---

### DOMS-0.4: Mirror Interface Updates to Desktop Agent (.NET)

**Sprint:** 1
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** DOMS-0.1, DOMS-0.2, DOMS-0.3 (for interface design reference)
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §3.3 (C# interface definitions)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccAdapter.cs` — current .NET interface
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs` — current types
- The Kotlin interfaces from DOMS-0.1 through DOMS-0.3 (for parity)

**Task:**
Mirror the Kotlin interface additions to the .NET Desktop Agent codebase.

**Detailed instructions:**
1. Create `Adapter/Common/IFccConnectionLifecycle.cs`:
   ```csharp
   public interface IFccConnectionLifecycle
   {
       Task<bool> ConnectAsync(CancellationToken ct);
       Task DisconnectAsync(CancellationToken ct);
       bool IsConnected { get; }
       void SetEventListener(IFccEventListener? listener);
   }
   ```
2. Create `Adapter/Common/IFccEventListener.cs`:
   ```csharp
   public interface IFccEventListener
   {
       Task OnPumpStatusChangedAsync(int fpId, PumpStatus status, CancellationToken ct);
       Task OnTransactionAvailableAsync(TransactionNotification notification, CancellationToken ct);
       Task OnFuellingUpdateAsync(int fpId, long volumeMicro, long amountMinor, CancellationToken ct);
       Task OnConnectionLostAsync(string reason, CancellationToken ct);
   }
   ```
3. Add `AcknowledgeTransactionsAsync()` to `IFccAdapter.cs`:
   ```csharp
   Task<bool> AcknowledgeTransactionsAsync(IReadOnlyList<string> transactionIds, CancellationToken ct);
   ```
4. Add `TransactionNotification` record to `AdapterTypes.cs`.
5. Update existing `DomsAdapter.cs` to implement the new method — return `true` (no-op for REST mode, since DOMS REST simulator uses cursor-based progression).
6. **Important:** The existing `DomsAdapter.cs` does NOT implement `IFccConnectionLifecycle` — that is only for the new `DomsJplAdapter`.

**Acceptance criteria:**
- `IFccConnectionLifecycle.cs` and `IFccEventListener.cs` created in `Adapter/Common/`
- `IFccAdapter.cs` has `AcknowledgeTransactionsAsync()` method
- Existing `DomsAdapter.cs` compiles with the new method (no-op implementation)
- All existing tests pass without modification
- Interface parity with Kotlin Edge Agent

---

### DOMS-0.5: Add DOMS TCP/JPL Config Fields to AgentFccConfig (Edge Agent — Kotlin)

**Sprint:** 1
**Component:** Edge Agent (Kotlin)
**Prereqs:** None
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §3.4 (config updates)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/AdapterTypes.kt` — current `AgentFccConfig`
- `DOMSRealImplementation/DppMiddleWareService/appsettings.json` — real DOMS config example

**Task:**
Extend `AgentFccConfig` with DOMS-specific TCP/JPL configuration fields needed by the edge agent to connect to a DOMS FCC.

**Detailed instructions:**
1. Add the following fields to `AgentFccConfig` in `AdapterTypes.kt`:
   - `jplPort: Int? = null` — TCP port for JPL channel (default: 8888)
   - `fcAccessCode: String? = null` — DOMS logon credential string (e.g., "POS,RI,APPL_ID=10,UNSO_FPSTA_3,UNSO_TRBUFSTA_3")
   - `domsCountryCode: String? = null` — DOMS-specific country code for FcLogon (e.g., "0045")
   - `posVersionId: String? = null` — DOMS POS version identifier (e.g., "1234")
   - `heartbeatIntervalSeconds: Int? = null` — configurable heartbeat interval (default: 30)
   - `reconnectBackoffMaxSeconds: Int? = null` — max reconnect delay (default: 60)
   - `configuredPumps: List<Int>? = null` — list of FpId values to poll for status
2. All fields must be nullable/optional so that non-DOMS configs are unaffected.
3. Mark `fcAccessCode` with `@Sensitive` annotation (it is a credential).
4. Add `@Serializable` annotations so these fields survive config push from cloud.

**Acceptance criteria:**
- `AgentFccConfig` has all 7 new nullable fields with sensible defaults
- `fcAccessCode` is marked `@Sensitive`
- All fields are `@Serializable`
- Existing code compiles — no breaking changes
- Non-DOMS adapter tests pass without modification

---

### DOMS-0.6: Add DOMS TCP/JPL Config Fields to SiteFccConfig (Cloud)

**Sprint:** 1
**Component:** Cloud Backend (.NET)
**Prereqs:** None
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §3.4 (config updates), §4.4.3 (cloud config)
- `src/cloud/FccMiddleware.Domain/Models/Adapter/SiteFccConfig.cs` — current config model

**Task:**
Extend `SiteFccConfig` with DOMS-specific TCP/JPL configuration fields. These are stored in the cloud and pushed to edge agents via config sync.

**Detailed instructions:**
1. Add the following properties to `SiteFccConfig`:
   ```csharp
   public int? JplPort { get; init; }                    // Default: 8888
   public IReadOnlyList<int>? DppPorts { get; init; }    // Default: [5001-5006]
   public string? FcAccessCode { get; init; }            // DOMS logon credential
   public string? DomsCountryCode { get; init; }         // DOMS country code
   public string? PosVersionId { get; init; }            // DOMS POS version ID
   public int? HeartbeatIntervalSeconds { get; init; }   // Default: 30
   public int? ReconnectBackoffMaxSeconds { get; init; } // Default: 60
   public IReadOnlyList<int>? ConfiguredPumps { get; init; } // FpId list
   ```
2. `FcAccessCode` should follow the same sensitive-data pattern as `ApiKey` in the existing config.
3. All fields nullable so existing non-DOMS sites are unaffected.
4. Add JSON serialization attributes if needed for API responses.

**Acceptance criteria:**
- `SiteFccConfig` has all 8 new nullable properties
- `FcAccessCode` treated as sensitive data (same pattern as `ApiKey`)
- Existing cloud adapter tests and cloud code compile without changes
- New fields serialize/deserialize correctly in JSON

---

### DOMS-0.7: Add DOMS Config Fields to Desktop FccConnectionConfig (.NET)

**Sprint:** 1
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** None
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs` — current `FccConnectionConfig`
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §3.4

**Task:**
Extend `FccConnectionConfig` with DOMS TCP/JPL fields needed by the Desktop Agent to connect to a DOMS FCC.

**Detailed instructions:**
1. Add to `FccConnectionConfig`:
   - `ConnectionProtocol` (string?) — "TCP" or "REST" (default: "REST" for backward compat)
   - `JplPort` (int?) — TCP port (default: 8888)
   - `FcAccessCode` (string?, `[SensitiveData]`) — DOMS logon credential
   - `DomsCountryCode` (string?) — DOMS country code
   - `PosVersionId` (string?) — POS version ID
   - `HeartbeatIntervalSeconds` (int?) — default: 30
   - `ReconnectBackoffMaxSeconds` (int?) — default: 60
   - `ConfiguredPumps` (IReadOnlyList<int>?) — FpId list
2. All nullable with no breaking changes to existing DOMS REST adapter.

**Acceptance criteria:**
- `FccConnectionConfig` extended with 8 new nullable fields
- `FcAccessCode` marked as sensitive
- Existing `DomsAdapter` and `DomsAdapterTests` compile and pass without changes
- No breaking change to factory or DI registration

---

## Phase 1 — TCP/JPL Protocol Core (Sprint 2)

### DOMS-1.1: JPL Frame Codec (Edge Agent — Kotlin)

**Sprint:** 2
**Component:** Edge Agent (Kotlin)
**Prereqs:** DOMS-0.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §2.3 (message framing), §2.4 (heartbeat)
- `DOMSRealImplementation/DppMiddleWareService/ForecourtTcpWorker/Worker.cs` — lines 327-361 (ProcessReceived), lines 683-706 (SendJplMessage)

**Task:**
Implement the STX/ETX binary frame codec for encoding and decoding JPL messages on the Kotlin Edge Agent. This is the lowest-level protocol component — all higher-level message handling depends on it.

**Detailed instructions:**
1. Create `adapter/doms/jpl/JplFrameCodec.kt`:
   - Constants: `STX = 0x02.toByte()`, `ETX = 0x03.toByte()`
   - `encode(json: String): ByteArray` — wraps JSON string in `[STX] + UTF-8 bytes + [ETX]`
   - `encodeHeartbeat(): ByteArray` — returns `[STX, ETX]` (2 bytes, no payload)
   - `decode(buffer: ByteArray, offset: Int, length: Int): DecodeResult` — scans for STX..ETX frames in a byte buffer
   - `DecodeResult` — sealed class:
     - `Frame(json: String, consumed: Int)` — successfully extracted a JSON frame
     - `Heartbeat(consumed: Int)` — STX immediately followed by ETX
     - `Incomplete` — no complete frame found (need more data)
     - `Error(message: String, consumed: Int)` — malformed frame
2. Handle edge cases:
   - Multiple frames in a single buffer (process sequentially)
   - Frame split across TCP reads (return `Incomplete`, caller accumulates)
   - STX without matching ETX (return `Incomplete`)
   - Nested STX within payload (should not happen in valid JPL, but handle gracefully)
   - Empty buffer
3. Reference `Worker.cs` ProcessReceived method for the real implementation pattern.

**Acceptance criteria:**
- Encodes JSON to `[STX][JSON bytes][ETX]` correctly
- Encodes heartbeat as exactly `[0x02, 0x03]`
- Decodes single frame from buffer
- Decodes multiple consecutive frames from buffer
- Returns `Heartbeat` for `[STX][ETX]` frame
- Returns `Incomplete` for partial frame
- Handles UTF-8 encoding correctly (multi-byte characters)
- Unit tests for all edge cases (at least 10 test cases)
- No external dependencies (pure Kotlin byte manipulation)

---

### DOMS-1.2: JPL Message Model (Edge Agent — Kotlin)

**Sprint:** 2
**Component:** Edge Agent (Kotlin)
**Prereqs:** None
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §2.3 (message structure), §2.4-2.9 (message examples)
- `DOMSRealImplementation/DppMiddleWareService/Models/DppMessage.cs` — message model

**Task:**
Create the JPL message model and DOMS-specific data models used throughout the adapter.

**Detailed instructions:**
1. Create `adapter/doms/jpl/JplMessage.kt`:
   ```kotlin
   @Serializable
   data class JplMessage(
       val name: String,          // e.g., "FcLogon_req", "FpStatus_resp"
       val subCode: String,       // e.g., "00H", "03H"
       val data: JsonObject       // dynamic payload
   ) {
       fun isRequest(): Boolean = name.endsWith("_req")
       fun isResponse(): Boolean = name.endsWith("_resp")
       fun correlationKey(): String = name.replace("_resp", "").replace("_req", "")
   }
   ```
2. Create `adapter/doms/model/DomsFpMainState.kt`:
   - Enum with all 14 pump states (0x00–0x0D)
   - `toCanonicalPumpState(): PumpState` mapping method
   - `companion object { fun fromHex(hex: String): DomsFpMainState }` — parse "02", "0x09", etc.
3. Create `adapter/doms/model/DomsSupParam.kt`:
   ```kotlin
   @Serializable
   data class DomsSupParam(
       @SerialName("ParId") val parId: String,
       @SerialName("Value") val value: String
   )
   ```
4. Create `adapter/doms/model/DomsTransactionDto.kt`:
   - Fields matching the real DOMS FpSupTrans_resp payload
   - Raw DOMS types (strings, encoded values) — conversion happens in mapper

**Acceptance criteria:**
- `JplMessage` serializes/deserializes correctly with `kotlinx.serialization`
- `DomsFpMainState` maps all 14 states to canonical `PumpState`
- `DomsFpMainState.fromHex()` handles both "02" and "0x02" formats
- `DomsSupParam` matches the real DOMS supplemental parameter format
- Unit tests for state mapping (all 14 states) and hex parsing

---

### DOMS-1.3: JPL TCP Client (Edge Agent — Kotlin)

**Sprint:** 2
**Component:** Edge Agent (Kotlin)
**Prereqs:** DOMS-1.1
**Estimated effort:** 2-3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §2.2 (connection lifecycle), §4.2.2 (JplTcpClient design)
- `DOMSRealImplementation/DppMiddleWareService/ForecourtTcpWorker/Worker.cs` — TCP connection management, receive loop, reconnection

**Task:**
Implement the TCP client that manages a persistent socket connection to the DOMS FCC, handles message framing, routes responses to waiting callers, and supports automatic reconnection.

**Detailed instructions:**
1. Create `adapter/doms/jpl/JplTcpClient.kt`:
   - Constructor: `(host: String, port: Int, scope: CoroutineScope)`
   - Uses `java.net.Socket` with `Dispatchers.IO`
   - **Connection management:**
     - `connect(): Boolean` — TCP connect, start read loop coroutine
     - `disconnect()` — clean shutdown, cancel read loop
     - `isConnected(): Boolean` — check socket state
   - **Message exchange:**
     - `send(message: JplMessage): JplMessage` — send request, await response with timeout (default 10s)
     - `sendFireAndForget(message: JplMessage)` — heartbeat, no response expected
   - **Read loop** (background coroutine):
     - Reads from socket `InputStream` into accumulation buffer
     - Uses `JplFrameCodec.decode()` to extract frames
     - Routes frames to either pending request completions or unsolicited event listener
   - **Response correlation:**
     - `ConcurrentHashMap<String, CompletableDeferred<JplMessage>>` keyed by `correlationKey()`
     - `send()` creates deferred, sends frame, awaits with timeout, removes from map on completion/timeout
   - **Reconnection:**
     - On socket read error: set `isConnected = false`, notify event listener via `onConnectionLost()`
     - Auto-reconnect with exponential backoff (1s, 2s, 4s, ..., max from config)
     - On reconnect success: re-send FcLogon (caller responsibility via event listener)
   - **Unsolicited message routing:**
     - Messages that don't match a pending request are routed to `IFccEventListener`
     - Use `DppMessageClassifier` logic from the real implementation to classify unsolicited vs solicited
2. **Android-specific considerations:**
   - All socket I/O on `Dispatchers.IO` (never Main thread)
   - Socket operations are cancellable via coroutine scope
   - Buffer size: 8192 bytes (matches real implementation)
3. Use structured concurrency: read loop is a child job of the provided scope.

**Acceptance criteria:**
- TCP connect/disconnect works
- Send/receive with response correlation works
- Read loop extracts multiple frames from TCP stream
- Heartbeat frames (STX/ETX) are silently consumed
- Response timeout works (CompletableDeferred with withTimeout)
- Unsolicited messages route to event listener
- Socket errors set isConnected = false and notify listener
- Reconnection with exponential backoff works
- Unit tests with mock socket or test TCP server

**Key notes:**
- Reference `Worker.cs` lines 218-247 for receive buffer pattern
- Reference `Worker.cs` lines 154-180 for connection initialization
- The real implementation uses background threads; our Kotlin version uses coroutines

---

### DOMS-1.4: JPL Heartbeat Manager (Edge Agent — Kotlin)

**Sprint:** 2
**Component:** Edge Agent (Kotlin)
**Prereqs:** DOMS-1.1, DOMS-1.3
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §2.2 (heartbeat every 30s), §2.3 (heartbeat frame)
- `DOMSRealImplementation/DppMiddleWareService/ForecourtTcpWorker/Worker.cs` — heartbeat sending logic

**Task:**
Implement the background heartbeat manager that sends `[STX][ETX]` keepalive frames at a configurable interval (default 30 seconds).

**Detailed instructions:**
1. Create `adapter/doms/jpl/JplHeartbeatManager.kt`:
   - Sends `JplFrameCodec.encodeHeartbeat()` via `JplTcpClient.sendFireAndForget()` every `heartbeatIntervalSeconds`
   - Runs as a coroutine within the adapter scope
   - `start(interval: Int)` / `stop()` lifecycle methods
   - Detects missed responses: if no data received for `3 × heartbeatInterval`, consider connection dead
   - On detected dead connection: invoke `JplTcpClient` reconnection logic
2. The heartbeat is **separate from** the `IFccAdapter.heartbeat()` method — that method simply returns `isConnected()`.

**Acceptance criteria:**
- Heartbeat sent at configured interval
- Heartbeat is exactly `[0x02, 0x03]` (2 bytes)
- Dead connection detected after 3 missed intervals
- Start/stop lifecycle works cleanly
- Unit tests verify timing and detection logic

---

### DOMS-1.5: JPL Frame Codec (.NET Desktop Agent)

**Sprint:** 2
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** DOMS-0.4
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §2.3 (framing), §4.3.2 (desktop TCP/JPL)
- The Kotlin `JplFrameCodec.kt` from DOMS-1.1 (for parity)

**Task:**
Implement the STX/ETX binary frame codec for the .NET Desktop Agent. Mirrors the Kotlin implementation.

**Detailed instructions:**
1. Create `Adapter/Doms/Jpl/JplFrameCodec.cs`:
   - Same encode/decode/heartbeat logic as Kotlin version
   - Use `ReadOnlySpan<byte>` / `Memory<byte>` for efficient buffer handling
   - `DecodeResult` as C# discriminated union (abstract record with subtypes)
2. Use the same test fixtures as the Kotlin version for cross-platform consistency.

**Acceptance criteria:**
- Same encode/decode behavior as Kotlin version
- Handles all edge cases (multi-frame, partial, heartbeat, malformed)
- Unit tests mirror Kotlin test cases
- Uses efficient .NET memory patterns (Span, Memory)

---

### DOMS-1.6: JPL TCP Client (.NET Desktop Agent)

**Sprint:** 2
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** DOMS-1.5
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §4.3.2 (desktop TCP/JPL)
- The Kotlin `JplTcpClient.kt` from DOMS-1.3 (for parity)

**Task:**
Implement the TCP client for the .NET Desktop Agent. Mirrors Kotlin but uses .NET async patterns.

**Detailed instructions:**
1. Create `Adapter/Doms/Jpl/JplTcpClient.cs`:
   - Uses `System.Net.Sockets.TcpClient` and `NetworkStream`
   - Async read loop using `await stream.ReadAsync()`
   - Response correlation using `ConcurrentDictionary<string, TaskCompletionSource<JplMessage>>`
   - Reconnection with exponential backoff
   - CancellationToken support throughout
2. Create `Adapter/Doms/Jpl/JplHeartbeatManager.cs` — mirrors Kotlin version.
3. Create `Adapter/Doms/Jpl/JplMessageRouter.cs` — routes solicited vs unsolicited messages.
4. Create `Adapter/Doms/Jpl/JplMessage.cs` — message model matching Kotlin `JplMessage.kt`.

**Acceptance criteria:**
- TCP connect/disconnect with async/await
- Send/receive with response correlation (TaskCompletionSource)
- Read loop with frame accumulation
- Heartbeat manager (30s interval, dead detection)
- Reconnection with backoff
- CancellationToken respected throughout
- Unit tests with test TCP server

---

### DOMS-1.7: DOMS Canonical Mapper (Edge Agent — Kotlin)

**Sprint:** 2
**Component:** Edge Agent (Kotlin)
**Prereqs:** DOMS-1.2
**Estimated effort:** 1.5 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §5 (data conversion reference — volume, amount, timestamp, pump/nozzle)
- `schemas/canonical/canonical-transaction.schema.json` — target format
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/CanonicalTransaction.kt` — target model
- `DOMSRealImplementation/DppMiddleWareService/Helpers/ParserHelper/FpStatusParser.cs` — real volume/money parsing

**Task:**
Implement the DOMS-to-canonical data mapper that converts raw DOMS JPL transaction data into `CanonicalTransaction` format with correct unit conversions.

**Detailed instructions:**
1. Create `adapter/doms/mapping/DomsCanonicalMapper.kt`:
   - `toCanonical(rawPayload: RawPayloadEnvelope, config: AgentFccConfig): CanonicalTransaction`
   - **Volume conversion:** DOMS centilitres × 10,000 = microlitres
   - **Amount conversion:** DOMS ×10 value × 10 = minor units (currency-dependent — use config)
   - **Unit price:** Compute from `amountMinor / (volumeMicro / 1_000_000)` or from DOMS price data
   - **Timestamp:** Parse `yyyyMMddHHmmss` → apply FCC timezone from config → convert to UTC ISO 8601
   - **Pump/Nozzle:** FpId → pumpNumber (apply pumpNumberOffset from config), NozzleId from SupParam 09
   - **Product code:** Use `config.productCodeMapping[gradeId]` with fallback to raw gradeId
2. Create `adapter/doms/protocol/DomsSupParamParser.kt`:
   - Parse the `SupParams` array from FpStatus responses
   - Extract named values by ParId (volume=05, money=06, nozzleId=09, etc.)
   - Handle missing parameters gracefully (return null)
3. Unit tests must cover:
   - Volume conversion: 520 centilitres → 5,200,000 microlitres
   - Amount conversion: 10000 (×10) → 100,000 minor units
   - Timestamp parsing: "20260217095113" → "2026-02-17T09:51:13Z" (UTC)
   - Product code mapping: mapped and unmapped codes
   - Pump offset: FpId 3 with offset 0 → pump 3; FpId 3 with offset 1 → pump 2
   - Edge cases: zero volume, missing nozzle, unknown grade

**Acceptance criteria:**
- All data conversions match §5 of the DOMS plan exactly
- Volume: centilitres × 10,000 = microlitres (verified)
- Amount: ×10 × 10 = minor units (verified)
- Timestamp: yyyyMMddHHmmss parsed with timezone, converted to UTC
- Product code mapping works with fallback
- Pump offset applied correctly
- At least 15 unit tests covering all conversion paths and edge cases
- No floating-point arithmetic for money or volume (Long only)

---

### DOMS-1.8: DOMS Canonical Mapper (.NET Desktop Agent)

**Sprint:** 2
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** DOMS-1.6
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §5 (data conversion reference)
- The Kotlin `DomsCanonicalMapper.kt` from DOMS-1.7 (for parity)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/CanonicalTransaction.cs` — target model

**Task:**
Implement the DOMS canonical mapper for the .NET Desktop Agent. Same conversion logic as Kotlin.

**Detailed instructions:**
1. Create `Adapter/Doms/Mapping/DomsCanonicalMapper.cs` — mirrors Kotlin version.
2. Create `Adapter/Doms/Protocol/DomsSupParamParser.cs` — supplemental parameter parsing.
3. Create `Adapter/Doms/Model/DomsFpMainState.cs` — 14-state enum with canonical mapping.
4. Create `Adapter/Doms/Model/DomsJplTransactionDto.cs` — JPL transaction DTO (distinct from existing REST `DomsProtocolDtos.cs`).
5. Use the same test fixtures and expected values as Kotlin tests.

**Acceptance criteria:**
- Same conversion results as Kotlin mapper for identical inputs
- All 14 pump states map correctly
- Shared test fixtures produce identical canonical output
- No floating-point arithmetic for money or volume

---

## Phase 2 — Edge Agent DOMS Adapter: Full Implementation (Sprints 3–4)

### DOMS-2.1: DOMS Protocol Handlers (Edge Agent — Kotlin)

**Sprint:** 3
**Component:** Edge Agent (Kotlin)
**Prereqs:** DOMS-1.2, DOMS-1.3
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §2.4 (FcLogon), §2.5 (FpStatus), §2.7 (pump control), §2.8 (transactions)
- `DOMSRealImplementation/DppMiddleWareService/ForecourtTcpWorker/Worker.cs` — FcLogon (lines 618-637), FpStatus handling (lines 721-845), transaction handling (lines 1029-1068)
- `DOMSRealImplementation/DppMiddleWareService/Helpers/ParserHelper/FpStatusParser.cs` — status parsing
- `DOMSRealImplementation/DppMiddleWareService/Helpers/ParserHelper/FpSupTransBufStatusParser.cs` — transaction buffer parsing

**Task:**
Implement the DOMS-specific protocol handlers that build JPL request messages and parse JPL response messages for each operation.

**Detailed instructions:**
1. Create `adapter/doms/protocol/DomsLogonHandler.kt`:
   - `buildLogonRequest(config: AgentFccConfig): JplMessage` — constructs FcLogon_req with FcAccessCode, CountryCode, PosVersionId, UnsolicitedApcList
   - `parseLogonResponse(response: JplMessage): Boolean` — checks for success/failure
   - Reference real implementation: `Worker.cs` FcLogon method
2. Create `adapter/doms/protocol/DomsPumpStatusParser.kt`:
   - `buildStatusRequest(fpId: Int, subCode: String = "03H"): JplMessage` — FpStatus_req
   - `parseStatusResponse(response: JplMessage, config: AgentFccConfig): PumpStatus` — extract FpMainState, FpId, supplemental params (volume, money, nozzle, grade)
   - Map `DomsFpMainState` to canonical `PumpState` using the enum mapping
   - Extract live fuelling data from SupParams 05 (volume) and 06 (money)
   - Reference: `FpStatusParser.cs` lines 37-88
3. Create `adapter/doms/protocol/DomsTransactionParser.kt`:
   - `buildTransactionLockRequest(fpId: Int, transSeqNo: String, posId: Int): JplMessage` — FpSupTrans_req
   - `buildTransactionClearRequest(fpId: Int, transSeqNo: String, posId: Int, volume: String, money: String): JplMessage` — clear_FpSupTrans_req
   - `parseTransactionResponse(response: JplMessage): DomsTransactionDto` — extract all transaction fields
   - `parseBufferStatus(response: JplMessage): List<BufferEntry>` — parse FpSupTransBufStatus entries
   - Reference: `Worker.cs` lines 1029-1068 and `FpSupTransBufStatusParser.cs`
4. Create `adapter/doms/protocol/DomsPreAuthHandler.kt`:
   - `buildAuthorizeRequest(command: PreAuthCommand, config: AgentFccConfig): JplMessage` — authorize_Fp_req
   - `parseAuthorizeResponse(response: JplMessage): PreAuthResult` — success/failure
   - **Note:** The exact authorize_Fp_req format is based on the DOMS plan §2.7 and may need validation against real hardware (see DQ-2).

**Acceptance criteria:**
- FcLogon request matches the format in `Worker.cs` (FcAccessCode, CountryCode, PosVersionId, UnsolicitedApcList)
- FpStatus parsing extracts FpMainState, all supplemental params, maps to canonical PumpState
- Transaction lock/read/clear request formats match `Worker.cs`
- Transaction response parsing extracts volume, amount, grade, pump, nozzle, timestamps
- Pre-auth request built correctly from PreAuthCommand
- Unit tests for each handler with JSON fixtures matching real DOMS message formats
- At least 20 test cases across all handlers

---

### DOMS-2.2: Replace DomsAdapter Stub with Full Implementation (Edge Agent — Kotlin)

**Sprint:** 3–4
**Component:** Edge Agent (Kotlin)
**Prereqs:** DOMS-1.3, DOMS-1.4, DOMS-1.7, DOMS-2.1
**Estimated effort:** 3–4 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §4.2.2 (DomsAdapter design), all of §2 (protocol)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/DomsAdapter.kt` — current stub (to be replaced)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/IFccAdapter.kt` — interface contract
- `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md` — §5.1 adapter contract

**Task:**
Replace the DomsAdapter stub with a full TCP/JPL implementation that speaks native DOMS protocol over LAN.

**Detailed instructions:**
1. Rewrite `adapter/doms/DomsAdapter.kt` to implement both `IFccAdapter` and `IFccConnectionLifecycle`:
   ```kotlin
   class DomsAdapter(
       private val config: AgentFccConfig,
       private val tcpClient: JplTcpClient,
       private val mapper: DomsCanonicalMapper,
       private val logonHandler: DomsLogonHandler,
       private val statusParser: DomsPumpStatusParser,
       private val transactionParser: DomsTransactionParser,
       private val preAuthHandler: DomsPreAuthHandler
   ) : IFccAdapter, IFccConnectionLifecycle
   ```
2. **`connect()`**: TCP connect → send FcLogon_req → verify FcLogon_resp success → start heartbeat
3. **`disconnect()`**: Stop heartbeat → close TCP socket
4. **`isConnected()`**: Return `tcpClient.isConnected()`
5. **`heartbeat()`**: Return `isConnected()` (actual heartbeats are background via JplHeartbeatManager)
6. **`fetchTransactions(cursor)`**:
   - Check supervised buffer status (if known from unsolicited messages)
   - For each available entry: send `FpSupTrans_req` (lock), parse response
   - Return batch of `RawPayloadEnvelope` with transaction data
   - Caller must call `acknowledgeTransactions()` after successful processing
7. **`acknowledgeTransactions(ids)`**:
   - For each id: send `clear_FpSupTrans_req` to release from DOMS buffer
   - Return true if all cleared successfully
8. **`normalize(rawPayload)`**:
   - Delegate to `DomsCanonicalMapper.toCanonical()`
   - Return `NormalizationResult.Success` or `NormalizationResult.Failure`
   - Must complete within 500ms, no network I/O
9. **`sendPreAuth(command)`**:
   - Build `authorize_Fp_req` via `DomsPreAuthHandler`
   - Send via TCP client, await response
   - Parse into `PreAuthResult`
10. **`getPumpStatus()`**:
    - For each `fpId` in `config.configuredPumps`: send `FpStatus_req`, parse response
    - Return list of `PumpStatus`
11. **`setEventListener(listener)`**: Store reference, pass unsolicited messages to listener
12. Update `IS_IMPLEMENTED` constant to `true`

**Acceptance criteria:**
- All 6 `IFccAdapter` methods fully implemented (no throws)
- All 4 `IFccConnectionLifecycle` methods implemented
- `IS_IMPLEMENTED = true`
- Connection lifecycle: connect → logon → operational → disconnect
- Transaction fetch uses lock-read pattern; acknowledge uses clear
- Normalization delegates to mapper (no network I/O)
- Pre-auth sends authorize_Fp_req and parses response
- Pump status queries each configured pump
- Unsolicited events forwarded to event listener
- Unit tests with mocked JplTcpClient for each method

---

### DOMS-2.3: Update FccAdapterFactory (Edge Agent — Kotlin)

**Sprint:** 4
**Component:** Edge Agent (Kotlin)
**Prereqs:** DOMS-2.2
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/FccAdapterFactory.kt` — current factory

**Task:**
Add DOMS to the `IMPLEMENTED_VENDORS` set and update DI registration so the factory resolves `DomsAdapter` with all its dependencies.

**Detailed instructions:**
1. Add `FccVendor.DOMS` to `IMPLEMENTED_VENDORS` set in `FccAdapterFactory.kt`
2. Update the `when (vendor)` block to inject `JplTcpClient`, `DomsCanonicalMapper`, and all protocol handlers into `DomsAdapter`
3. Update `di/AppModule.kt` Koin module to register:
   - `JplTcpClient` as scoped singleton (one per adapter lifecycle)
   - `DomsCanonicalMapper` as singleton
   - All protocol handlers as singletons
   - `DomsAdapter` with all dependencies injected

**Acceptance criteria:**
- `FccAdapterFactory.resolve(FccVendor.DOMS, config)` returns a fully-wired `DomsAdapter`
- No more `AdapterNotImplementedException` for DOMS
- Koin module resolves all dependencies without runtime errors
- Existing non-DOMS paths unaffected

---

### DOMS-2.4: Update CadenceController for TCP Lifecycle (Edge Agent — Kotlin)

**Sprint:** 4
**Component:** Edge Agent (Kotlin)
**Prereqs:** DOMS-2.2
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt` — current cadence controller
- `docs/plans/dev-plan-edge-agent.md` — EA-2.3 (connectivity manager), EA-2.6 (ingestion orchestrator)

**Task:**
Update the CadenceController to handle TCP connection lifecycle for stateful adapters (DOMS).

**Detailed instructions:**
1. At startup, check if the resolved adapter implements `IFccConnectionLifecycle`:
   ```kotlin
   if (adapter is IFccConnectionLifecycle) {
       adapter.connect()
   }
   ```
2. On `FCC_UNREACHABLE` → attempt reconnection on next cadence tick
3. On connectivity recovery (`FULLY_ONLINE` or `INTERNET_DOWN` with FCC UP): ensure TCP connection is active; reconnect if not
4. On shutdown: call `adapter.disconnect()` if applicable
5. Register the CadenceController or IngestionOrchestrator as the `IFccEventListener` to receive unsolicited events:
   - `onTransactionAvailable()` → trigger immediate transaction fetch
   - `onPumpStatusChanged()` → update cached pump status
   - `onConnectionLost()` → mark FCC as unreachable, trigger connectivity state update
6. After `fetchTransactions()` returns, call `acknowledgeTransactions()` for successfully buffered transactions.

**Acceptance criteria:**
- TCP adapters are connected at startup
- Reconnection attempted on FCC unreachable
- Clean disconnect on shutdown
- Unsolicited events handled (transaction fetch triggered, status updated, connection loss detected)
- Transaction acknowledgement called after successful buffering
- Non-TCP adapters (future Radix/Petronite) unaffected by this logic
- Unit tests for lifecycle management with mock adapter

---

## Phase 3 — Desktop Agent DOMS JPL Adapter (Sprint 4–5)

### DOMS-3.1: DOMS Protocol Handlers (.NET Desktop Agent)

**Sprint:** 4
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** DOMS-1.6, DOMS-1.8
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- The Kotlin protocol handlers from DOMS-2.1 (for parity)
- `DOMSRealImplementation/DppMiddleWareService/ForecourtTcpWorker/Worker.cs` — message building/parsing

**Task:**
Implement the DOMS protocol handlers for the .NET Desktop Agent. Same logic as Kotlin handlers.

**Detailed instructions:**
1. Create in `Adapter/Doms/Protocol/`:
   - `DomsLogonHandler.cs` — FcLogon request/response
   - `DomsPumpStatusParser.cs` — FpStatus request/response with all SubCodes
   - `DomsTransactionParser.cs` — FpSupTrans lock/read/clear
   - `DomsPreAuthHandler.cs` — authorize_Fp request/response
   - `DomsSupParamParser.cs` — supplemental parameter parsing
2. Use same test fixtures as Kotlin for cross-platform consistency.

**Acceptance criteria:**
- All handlers produce identical JPL messages to Kotlin counterparts
- All response parsers extract the same fields
- Unit tests with shared JSON fixtures
- CancellationToken support throughout

---

### DOMS-3.2: DomsJplAdapter — Full TCP/JPL Implementation (.NET Desktop Agent)

**Sprint:** 5
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** DOMS-3.1, DOMS-1.6
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §4.3 (desktop adapter)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsAdapter.cs` — existing REST adapter (reference for interface contract)
- The Kotlin `DomsAdapter.kt` from DOMS-2.2 (for parity)

**Task:**
Create a new `DomsJplAdapter` class that implements `IFccAdapter` and `IFccConnectionLifecycle` using native TCP/JPL protocol. This exists alongside the existing REST-based `DomsAdapter`.

**Detailed instructions:**
1. Create `Adapter/Doms/DomsJplAdapter.cs`:
   - Implements `IFccAdapter` and `IFccConnectionLifecycle`
   - Same structure as Kotlin `DomsAdapter` but with C# async patterns
   - All methods use CancellationToken
2. Keep existing `DomsAdapter.cs` (REST) untouched — it remains for VirtualLab testing.
3. The factory selects between them based on `ConnectionProtocol`:
   ```csharp
   FccVendor.Doms => config.ConnectionProtocol switch
   {
       "TCP" => new DomsJplAdapter(...),
       _ => new DomsAdapter(httpFactory, config, logger)  // REST (default for backward compat)
   }
   ```

**Acceptance criteria:**
- `DomsJplAdapter` implements all 6 `IFccAdapter` methods + 4 `IFccConnectionLifecycle` methods
- TCP connect → FcLogon → operational → disconnect lifecycle works
- Transaction lock-read-clear flow works
- Pre-auth sends authorize_Fp_req correctly
- Pump status queries each configured pump
- Heartbeat manager runs in background
- Reconnection with backoff works
- Unit tests with test TCP server
- Existing REST `DomsAdapter` and all its tests remain untouched

---

### DOMS-3.3: Update FccAdapterFactory (.NET Desktop Agent)

**Sprint:** 5
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** DOMS-3.2
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs` — current factory

**Task:**
Update the desktop adapter factory to select TCP vs REST for DOMS based on `ConnectionProtocol`.

**Detailed instructions:**
1. Update `FccAdapterFactory.Create()` for `FccVendor.Doms`:
   - If `config.ConnectionProtocol == "TCP"`: return `new DomsJplAdapter(...)`
   - Else: return existing `new DomsAdapter(...)` (REST, default)
2. Inject `JplTcpClient` dependencies when creating `DomsJplAdapter`
3. Update DI registration to support both adapter types

**Acceptance criteria:**
- `ConnectionProtocol = "TCP"` → `DomsJplAdapter` created
- `ConnectionProtocol = "REST"` or null → existing `DomsAdapter` created (backward compatible)
- Both adapter types resolve correctly from factory
- Existing REST adapter tests pass without modification

---

## Phase 4 — VirtualLab TCP/JPL Simulator (Sprints 5–6)

### DOMS-4.1: VirtualLab JPL Server & Frame Codec

**Sprint:** 5
**Component:** VirtualLab (.NET)
**Prereqs:** DOMS-1.5 (for frame codec reference)
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §4.1.2 (TCP/JPL simulator design)
- `docs/plans/agent-prompt-virtual-lab.md` — VirtualLab conventions
- `VirtualLab/src/VirtualLab.Infrastructure/` — existing infrastructure patterns

**Task:**
Build the core TCP server and STX/ETX frame codec for the VirtualLab DOMS JPL simulator. This allows edge agents to be tested against a simulated DOMS FCC without physical hardware.

**Detailed instructions:**
1. Create `VirtualLab.Infrastructure/DomsJpl/DomsJplServer.cs`:
   - `IHostedService` that starts a `TcpListener` on configurable port (default: 8888)
   - Accepts multiple concurrent connections
   - Creates a `DomsJplSession` per connection
   - Tracks active sessions for monitoring
2. Create `VirtualLab.Infrastructure/DomsJpl/DomsJplSession.cs`:
   - Per-connection handler with read loop
   - Uses `DomsJplFrameCodec` for framing
   - Routes decoded messages to `DomsJplMessageRouter`
   - Handles heartbeat frames (respond with heartbeat)
   - Tracks session state: authenticated, active pumps, etc.
3. Create `VirtualLab.Infrastructure/DomsJpl/DomsJplFrameCodec.cs`:
   - Reuse the frame codec from DOMS-1.5 or create server-side equivalent
   - Must handle both encoding (server responses) and decoding (client requests)

**Acceptance criteria:**
- TCP server starts and accepts connections on configured port
- STX/ETX framing works for both directions
- Multiple concurrent sessions supported
- Heartbeat frames handled (respond with heartbeat)
- Server shutdown cleanly disconnects all sessions
- Integration test: connect, send frame, receive response, disconnect

---

### DOMS-4.2: VirtualLab FcLogon & FpStatus Handlers

**Sprint:** 5
**Component:** VirtualLab (.NET)
**Prereqs:** DOMS-4.1
**Estimated effort:** 1.5 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §2.4 (FcLogon), §2.5 (FpStatus), §2.6 (pump state machine)
- `VirtualLab/src/VirtualLab.Infrastructure/FccProfiles/SeedProfileFactory.cs` — existing simulation patterns

**Task:**
Implement the FcLogon authentication handler and FpStatus pump status handler in the VirtualLab JPL simulator.

**Detailed instructions:**
1. Create `DomsJpl/Handlers/FcLogonHandler.cs`:
   - Validate FcAccessCode from request
   - Return FcLogon_resp with success/failure
   - Mark session as authenticated
   - Store requested unsolicited message preferences from `UnsolicitedApcList`
2. Create `DomsJpl/Handlers/FpStatusHandler.cs`:
   - Handle `FpStatus_req` with SubCode 00H through 03H
   - Return FpStatus_resp with simulated pump state and supplemental parameters
   - Pump state driven by `DomsJplPumpSimulator` (simulated state machine)
3. Create `DomsJpl/Models/JplMessage.cs` — server-side message model
4. Create `DomsJpl/Models/FpMainState.cs` — 14-state enum for simulator

**Acceptance criteria:**
- FcLogon succeeds with valid credentials, fails with invalid
- Unauthenticated messages rejected
- FpStatus returns correct state for simulated pumps
- SubCode 03H returns full detail with supplemental parameters
- Pump states are configurable via simulator API

---

### DOMS-4.3: VirtualLab Transaction Buffer & Pre-Auth Handlers

**Sprint:** 6
**Component:** VirtualLab (.NET)
**Prereqs:** DOMS-4.2
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §2.7 (pump control), §2.8 (transactions)

**Task:**
Implement the supervised transaction buffer and pre-authorization handlers in the VirtualLab JPL simulator.

**Detailed instructions:**
1. Create `DomsJpl/Handlers/FpSupTransHandler.cs`:
   - `FpSupTrans_req` — lock a transaction in the buffer, return full transaction data
   - `clear_FpSupTrans_req` — clear (acknowledge) a locked transaction
   - Maintain an in-memory supervised transaction buffer per session
   - Transactions are added when simulated dispenses complete
2. Create `DomsJpl/Handlers/FpAuthorizeHandler.cs`:
   - `authorize_Fp_req` — authorize a pump for pre-paid amount
   - Transition pump state: Idle → Calling → PreAuthorized
   - Return success/failure response
3. Create `DomsJpl/Handlers/FpControlHandler.cs`:
   - `estop_Fp_req`, `cancel_FpEstop_req`, `close_Fp_req`, `open_Fp_req`
   - Transition pump states accordingly
4. Create `DomsJpl/UnsolicitedPusher.cs`:
   - Background task that pushes FpStatus changes and FpSupTransBufStatus updates
   - Only sends to sessions that requested unsolicited messages via FcLogon

**Acceptance criteria:**
- Transaction buffer stores simulated completed transactions
- Lock/read/clear flow works correctly (lock prevents double-read, clear removes)
- Pre-auth transitions pump state machine
- Pump control (e-stop, close, open) changes pump state
- Unsolicited events pushed to subscribed sessions
- Integration test: connect → logon → authorize → simulate dispense → read transaction → clear

---

### DOMS-4.4: VirtualLab API Endpoints & Registration

**Sprint:** 6
**Component:** VirtualLab (.NET)
**Prereqs:** DOMS-4.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §4.1.2 (VirtualLab API additions)
- `VirtualLab/src/VirtualLab.Api/` — existing API patterns

**Task:**
Add REST API endpoints to control and monitor the VirtualLab DOMS JPL simulator, and register it as a hosted service.

**Detailed instructions:**
1. Add REST endpoints:
   - `POST /api/doms-jpl/start` — start TCP simulator on configurable port
   - `POST /api/doms-jpl/stop` — stop TCP simulator
   - `GET /api/doms-jpl/status` — check simulator status, connected clients
   - `POST /api/doms-jpl/push-event` — manually trigger unsolicited event (for testing)
   - `POST /api/doms-jpl/set-pump-state` — set a pump's state (for scenario testing)
   - `POST /api/doms-jpl/add-transaction` — inject a transaction into the supervised buffer
2. Register `DomsJplServer` in `Program.cs`:
   ```csharp
   builder.Services.AddSingleton<DomsJplServer>();
   builder.Services.AddHostedService(sp => sp.GetRequiredService<DomsJplServer>());
   ```
3. Add configuration section for DOMS JPL simulator settings (port, pump count, etc.)

**Acceptance criteria:**
- All API endpoints work correctly
- Simulator starts/stops via API
- Status endpoint shows connected clients and pump states
- Push-event endpoint triggers unsolicited message to connected clients
- Set-pump-state allows test scenarios to control pump behavior
- Add-transaction injects transactions for fetch testing
- Simulator registers as hosted service and starts with VirtualLab

---

## Phase 5 — Cloud & Portal Updates (Sprint 6–7)

### DOMS-5.1: Update DomsCloudAdapter for Edge-Upload Validation

**Sprint:** 6
**Component:** Cloud Backend (.NET)
**Prereqs:** DOMS-0.6
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §4.4.1 (cloud adapter updates)
- `src/cloud/FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs` — current implementation
- `src/cloud/FccMiddleware.Adapter.Doms.Tests/` — existing tests

**Task:**
Update the cloud DomsCloudAdapter to handle transactions uploaded by Edge Agents (already in canonical format) in addition to the existing REST simulator path.

**Detailed instructions:**
1. Add a validation path for edge-uploaded payloads:
   - Edge-uploaded transactions arrive as pre-normalized `CanonicalTransaction` JSON
   - `ValidatePayload()` should detect this format (check for `fccVendor: "DOMS"` + canonical field presence)
   - Validate required canonical fields are present and valid
   - Skip vendor-specific normalization (already canonical)
2. `NormalizeTransaction()` for edge-uploaded payloads:
   - Mostly passthrough — data is already in canonical format
   - Verify/enrich: add `legalEntityId` from site config if missing, validate currency matches config
3. Keep existing REST simulator path untouched for VirtualLab testing
4. Add unit tests for the edge-upload validation path

**Acceptance criteria:**
- Edge-uploaded canonical payloads validated and accepted
- Existing REST simulator path unchanged
- Validation catches missing required fields in edge-uploaded data
- Normalization is passthrough for pre-normalized data
- New unit tests for edge-upload path
- All existing tests pass without modification

---

### DOMS-5.2: Cloud Database Migration for DOMS Config Fields

**Sprint:** 6
**Component:** Cloud Backend (.NET)
**Prereqs:** DOMS-0.6
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §4.4.3 (config updates)
- Cloud database migration patterns in the codebase

**Task:**
Add database migration for the new DOMS-specific configuration fields on `SiteFccConfig` so they can be stored and pushed to edge agents.

**Detailed instructions:**
1. Create database migration adding nullable columns for DOMS config fields:
   - `jpl_port` (INT, nullable)
   - `dpp_ports` (JSONB/NVARCHAR, nullable — array of ints)
   - `fc_access_code` (NVARCHAR, nullable, encrypted)
   - `doms_country_code` (NVARCHAR, nullable)
   - `pos_version_id` (NVARCHAR, nullable)
   - `heartbeat_interval_seconds` (INT, nullable)
   - `reconnect_backoff_max_seconds` (INT, nullable)
   - `configured_pumps` (JSONB/NVARCHAR, nullable — array of ints)
2. `fc_access_code` must follow the same encryption-at-rest pattern as the existing `api_key` field.
3. Update the config push mechanism to include these fields when serving `GET /api/v1/agent/config`.

**Acceptance criteria:**
- Migration runs cleanly on existing database
- All new columns are nullable (no breaking change)
- `fc_access_code` encrypted at rest
- Config push includes new fields
- Existing config push for non-DOMS sites unaffected

---

### DOMS-5.3: Portal — DOMS-Specific FCC Config Fields

**Sprint:** 7
**Component:** Portal (Angular)
**Prereqs:** DOMS-5.2
**Estimated effort:** 1.5 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §4.5.1 (FCC config screen)
- `src/portal/src/app/features/site-config/fcc-config-form.component.ts` — existing FCC config form
- `docs/plans/agent-prompt-angular-portal.md` — Portal conventions

**Task:**
Add DOMS-specific configuration fields to the Portal FCC configuration screen, shown conditionally when `fccVendor = DOMS`.

**Detailed instructions:**
1. When `fccVendor` is set to `DOMS`, show additional fields:
   - **JPL Port** — Number input (default: 8888), label: "TCP/JPL Port"
   - **FcAccessCode** — Text input (masked/password style), label: "DOMS Access Code"
   - **DOMS Country Code** — Text input, label: "Country Code (e.g., 0045)"
   - **POS Version ID** — Text input, label: "POS Version ID"
   - **Heartbeat Interval** — Number input with range 15-60 (default: 30), label: "Heartbeat Interval (seconds)"
   - **Reconnect Max Backoff** — Number input with range 30-300 (default: 60), label: "Max Reconnect Delay (seconds)"
   - **Configured Pumps** — Multi-value input (list of integers), label: "Pump IDs (FpId values)"
2. These fields are hidden when vendor is not DOMS.
3. Add form validation: JPL port must be 1-65535, heartbeat interval 15-60, etc.
4. Wire fields to the `SiteFccConfig` API model for save/load.

**Acceptance criteria:**
- DOMS-specific fields appear only when `fccVendor = DOMS`
- FcAccessCode is masked (password input)
- Form validation works for all fields
- Fields save and load correctly via API
- Non-DOMS vendor selections hide these fields
- Existing FCC config behavior unchanged for other vendors

---

### DOMS-5.4: Portal — DOMS Pump State Monitoring (Stretch)

**Sprint:** 7
**Component:** Portal (Angular)
**Prereqs:** DOMS-5.3
**Estimated effort:** 2 days
**Priority:** P2 (Stretch goal)

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §4.5.2 (monitoring dashboard), §4.5.3 (state machine visualizer)

**Task:**
Add DOMS-specific monitoring widgets to the Portal: TCP connection status, pump state grid with 14-state model, and JPL message log.

**Detailed instructions:**
1. **TCP Connection Status Widget**: Show connected/disconnected/reconnecting state from agent telemetry
2. **Pump State Grid**: Show each pump's current state with color coding:
   - Green: Idle, PreAuthorized
   - Blue: Fuelling, Starting
   - Yellow: Calling, Paused
   - Red: Error, Unavailable
   - Gray: Offline, Unconfigured, Closed
3. **Heartbeat Timeline**: Show last heartbeat timestamps from telemetry
4. Data sourced from agent telemetry reports (already implemented in EA-3.4)

**Acceptance criteria:**
- Connection status shows real-time state from telemetry
- Pump grid shows all 14 states with appropriate colors
- Heartbeat timeline shows recent heartbeat timestamps
- Widgets update when telemetry data refreshes

---

## Phase 6 — Testing & Validation (Sprints 7–8)

### DOMS-6.1: DOMS JPL Test Fixtures

**Sprint:** 7
**Component:** All (shared fixtures)
**Prereqs:** DOMS-2.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §6.4 (test fixtures)
- `DOMSRealImplementation/` — for real message format reference
- `src/desktop-edge-agent/tests/.../Adapter/Doms/Fixtures/` — existing REST fixture pattern

**Task:**
Create comprehensive JSON test fixtures for all DOMS JPL message types, shared across Kotlin and .NET test suites.

**Detailed instructions:**
1. Create fixture files for both Edge Agent and Desktop Agent test directories:
   - `doms-jpl-logon-request.json` — FcLogon_req
   - `doms-jpl-logon-response-success.json` — FcLogon_resp (success)
   - `doms-jpl-logon-response-failure.json` — FcLogon_resp (failure)
   - `doms-jpl-fpstatus-idle.json` — FpStatus_resp (Idle, SubCode 03H)
   - `doms-jpl-fpstatus-fuelling.json` — FpStatus_resp (Fuelling with live volume/amount)
   - `doms-jpl-fpstatus-completed.json` — FpStatus_resp (FuellingTerminated)
   - `doms-jpl-authorize-request.json` — authorize_Fp_req
   - `doms-jpl-authorize-response-success.json` — authorize_Fp_resp (success)
   - `doms-jpl-authorize-response-failure.json` — authorize_Fp_resp (failure/declined)
   - `doms-jpl-transaction-buffer-status.json` — FpSupTransBufStatus_resp
   - `doms-jpl-transaction-read.json` — FpSupTrans_resp (full transaction)
   - `doms-jpl-transaction-clear-request.json` — clear_FpSupTrans_req
   - `doms-jpl-unsolicited-status-change.json` — unsolicited FpStatus
   - `doms-jpl-unsolicited-transaction-completed.json` — unsolicited FpTransactionCompleted
2. All fixtures must match the real DOMS message format from DOMSRealImplementation.
3. Include edge cases: zero volume transaction, all 14 pump states, missing supplemental params.

**Acceptance criteria:**
- All 14+ fixture files created
- Fixtures match real DOMS protocol format (validated against DOMSRealImplementation)
- Fixtures are parseable by both Kotlin and .NET parsers
- Edge case fixtures included

---

### DOMS-6.2: Integration Tests — Edge Agent ↔ VirtualLab JPL Simulator

**Sprint:** 7–8
**Component:** Edge Agent (Kotlin) + VirtualLab
**Prereqs:** DOMS-2.2, DOMS-4.3
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §6.2 (integration tests), §6.3 (scenario definitions)

**Task:**
Write integration tests that verify the Kotlin Edge Agent DOMS adapter against the VirtualLab JPL simulator over real TCP.

**Detailed instructions:**
1. **JPL Handshake Test**: Connect → FcLogon → verify session established → disconnect
2. **Pre-Auth Flow Test**: Connect → authorize pump → verify pump state transitions → verify pre-auth result
3. **Transaction Retrieval Test**: Connect → simulator adds transaction → Edge Agent fetches → verify canonical conversion → acknowledge
4. **Unsolicited Events Test**: Connect → simulator pushes FpStatus change → Edge Agent event listener receives → verify PumpStatus update
5. **Reconnection Test**: Connect → kill TCP → verify automatic reconnect → verify re-logon → verify no data loss
6. **Multi-Pump Status Test**: Connect → query status for 4+ pumps → verify independent state machines
7. For each test:
   - Start VirtualLab JPL simulator programmatically on a random port
   - Create `DomsAdapter` configured for that port
   - Execute test scenario
   - Verify assertions
   - Stop simulator

**Acceptance criteria:**
- All 6 integration test scenarios pass
- Tests run against real TCP (not mocked sockets)
- Tests are repeatable and isolated (random port per test)
- Reconnection test verifies backoff behavior
- Transaction retrieval verifies full canonical conversion including volume/amount/timestamp
- Tests can run in CI (no physical hardware dependency)

---

### DOMS-6.3: Integration Tests — Desktop Agent ↔ VirtualLab JPL Simulator

**Sprint:** 8
**Component:** Desktop Edge Agent (.NET) + VirtualLab
**Prereqs:** DOMS-3.2, DOMS-4.3
**Estimated effort:** 1.5 days

**Read these artifacts before starting:**
- DOMS-6.2 integration tests (for parity)

**Task:**
Write integration tests for the .NET Desktop Agent `DomsJplAdapter` against the VirtualLab JPL simulator. Same scenarios as DOMS-6.2 but for .NET.

**Detailed instructions:**
1. Mirror the 6 integration test scenarios from DOMS-6.2 in xUnit.
2. Use `TestServer` pattern: start VirtualLab JPL simulator on random port per test.
3. Verify both `DomsJplAdapter` (TCP) and existing `DomsAdapter` (REST) — ensure REST adapter still works against REST simulator.

**Acceptance criteria:**
- All 6 scenarios pass for `DomsJplAdapter` (TCP)
- Existing `DomsAdapter` (REST) tests still pass
- Tests isolated (random ports, no shared state)
- Can run in CI

---

### DOMS-6.4: End-to-End Test — Edge Agent → Cloud → Odoo Poll

**Sprint:** 8
**Component:** All
**Prereqs:** DOMS-6.2, DOMS-5.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §6.2 (end-to-end scenario)

**Task:**
Write an end-to-end integration test covering the full transaction lifecycle: DOMS FCC → Edge Agent → Cloud → Odoo poll → SYNCED_TO_ODOO.

**Detailed instructions:**
1. Test flow:
   - Start VirtualLab JPL simulator
   - Configure Edge Agent to connect to simulator
   - Simulator completes a dispense (adds transaction to supervised buffer)
   - Edge Agent ingests transaction via fetchTransactions → acknowledges via clear
   - Edge Agent normalizes and buffers locally
   - Edge Agent uploads to cloud via CloudUploadWorker
   - Cloud validates and stores transaction (PENDING)
   - Simulated Odoo polls cloud API → transaction transitions to SYNCED_TO_ODOO
   - Edge Agent polls SYNCED_TO_ODOO status → local record updated
2. Verify data integrity at each step:
   - Volume correctly converted (centilitres → microlitres)
   - Amount correctly converted (×10 → minor units)
   - Timestamps correctly converted to UTC
   - Pump/nozzle numbers correctly mapped
   - Product code correctly mapped

**Acceptance criteria:**
- Full lifecycle completes without errors
- Data integrity verified at each step
- Transaction acknowledged (cleared) in DOMS buffer
- Final cloud record matches expected canonical values
- SYNCED_TO_ODOO status propagated back to Edge Agent
- Test can run in CI (all components in-process or local)

---

### DOMS-6.5: VirtualLab DOMS Scenario Definitions

**Sprint:** 8
**Component:** VirtualLab
**Prereqs:** DOMS-4.3
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §6.3 (scenario definitions)

**Task:**
Create DOMS-specific scenario definitions for the VirtualLab scenario runner.

**Detailed instructions:**
1. `doms-basic-dispense` — Start idle → lift nozzle (Calling) → authorize (PreAuthorized) → dispense (Fuelling) → complete → verify transaction in buffer
2. `doms-preauth-flow` — Create pre-auth → authorize_Fp_req → pump authorized → dispense → transaction created → reconciliation
3. `doms-reconnect` — Disconnect TCP mid-session → verify Edge Agent reconnects → verify no data loss
4. `doms-multi-pump` — Simultaneous operations on 4+ pumps → verify independent state machines
5. `doms-emergency-stop` — Dispense in progress → estop_Fp_req → verify pump blocked → cancel_FpEstop_req → verify unblocked

**Acceptance criteria:**
- All 5 scenario definitions created
- Scenarios can be executed via VirtualLab API
- Each scenario has clear expected state transitions and data outputs
- Scenarios are repeatable and deterministic

---

## Task Dependency Graph

```
Phase 0: Foundation (Sprint 1)
  DOMS-0.1 ─┐
  DOMS-0.2 ─┤ (independent, parallel)
  DOMS-0.3 ─┤
  DOMS-0.5 ─┤
  DOMS-0.6 ─┤
  DOMS-0.7 ─┘
  DOMS-0.4 ← DOMS-0.1, 0.2, 0.3 (mirror to .NET)

Phase 1: TCP/JPL Core (Sprint 2)
  DOMS-1.1 (Kotlin frame codec) ─┐
  DOMS-1.2 (Kotlin models) ──────┤ (independent)
  DOMS-1.5 (.NET frame codec) ───┤
  DOMS-1.7 (Kotlin mapper) ──────┘
  DOMS-1.3 (Kotlin TCP client) ← DOMS-1.1
  DOMS-1.4 (Kotlin heartbeat) ← DOMS-1.3
  DOMS-1.6 (.NET TCP client) ← DOMS-1.5
  DOMS-1.8 (.NET mapper) ← DOMS-1.6

Phase 2: Edge Agent Full (Sprints 3-4)
  DOMS-2.1 (Kotlin handlers) ← DOMS-1.2, 1.3
  DOMS-2.2 (Kotlin adapter) ← DOMS-1.3, 1.4, 1.7, 2.1
  DOMS-2.3 (Kotlin factory) ← DOMS-2.2
  DOMS-2.4 (Kotlin cadence) ← DOMS-2.2

Phase 3: Desktop Agent JPL (Sprints 4-5)
  DOMS-3.1 (.NET handlers) ← DOMS-1.6, 1.8
  DOMS-3.2 (.NET JPL adapter) ← DOMS-3.1, 1.6
  DOMS-3.3 (.NET factory) ← DOMS-3.2

Phase 4: VirtualLab Simulator (Sprints 5-6)
  DOMS-4.1 (VL server) ← DOMS-1.5
  DOMS-4.2 (VL logon+status) ← DOMS-4.1
  DOMS-4.3 (VL transactions) ← DOMS-4.2
  DOMS-4.4 (VL API) ← DOMS-4.1

Phase 5: Cloud & Portal (Sprints 6-7)
  DOMS-5.1 (Cloud adapter) ← DOMS-0.6
  DOMS-5.2 (Cloud migration) ← DOMS-0.6
  DOMS-5.3 (Portal config) ← DOMS-5.2
  DOMS-5.4 (Portal monitoring) ← DOMS-5.3 [P2 stretch]

Phase 6: Testing (Sprints 7-8)
  DOMS-6.1 (Fixtures) ← DOMS-2.1
  DOMS-6.2 (Kotlin integration) ← DOMS-2.2, 4.3
  DOMS-6.3 (.NET integration) ← DOMS-3.2, 4.3
  DOMS-6.4 (End-to-end) ← DOMS-6.2, 5.1
  DOMS-6.5 (VL scenarios) ← DOMS-4.3
```

---

## Risk Register

| Risk | Impact | Mitigation |
|------|--------|------------|
| Real DOMS protocol differs from DOMSRealImplementation | High — wrong messages | Validate against actual DOMS hardware ASAP; DOMSRealImplementation is production-tested reference |
| TCP persistent connection on Android battery/lifecycle | Medium — service killed by OS | Use ForegroundService (already in place); test on Urovo i9100 specifically |
| Multi-port DPP (5001-5006) complexity | Medium — unnecessary work | Investigate if single JPL port handles all events via `UnsolicitedApcList` (DQ-1) |
| Volume/amount conversion factors vary by firmware | High — financial data corruption | Parameterize conversion factors in config; validate with test transactions on real hardware |
| authorize_Fp_req format uncertainty | Medium — blocks pre-auth | Start with best guess from plan §2.7; validate against hardware (DQ-2) |
| VirtualLab JPL simulator complexity delays testing | Medium — testing blocked | Start with minimal subset (logon, status, transaction read); add features incrementally |

---

## Changelog

### 2026-03-12

- Initial version created from `WIP-DOMSFCCAdapterPlan.md` with detailed task breakdown
- 6 phases, 31 tasks total
- Phase 0: 7 foundation tasks (interface updates, config fields)
- Phase 1: 8 TCP/JPL protocol core tasks (Kotlin + .NET frame codecs, TCP clients, mappers)
- Phase 2: 4 Edge Agent tasks (protocol handlers, full adapter, factory, cadence controller)
- Phase 3: 3 Desktop Agent tasks (handlers, JPL adapter, factory)
- Phase 4: 4 VirtualLab simulator tasks (server, handlers, API)
- Phase 5: 4 Cloud & Portal tasks (adapter update, migration, config UI, monitoring)
- Phase 6: 5 testing tasks (fixtures, integration tests, E2E, scenarios)
