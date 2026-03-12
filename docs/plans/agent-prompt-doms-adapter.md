# DOMS FCC Adapter — Agent System Prompt

**Use this prompt as context when assigning ANY DOMS adapter implementation task to an AI coding agent.**

---

## You Are Working On

The **DOMS FCC Adapter** — the integration layer that enables the Forecourt Middleware to communicate with DOMS Forecourt Controllers. DOMS uses a **TCP-based JPL (JSON Protocol Layer)** protocol with binary framing — fundamentally different from the REST/JSON adapters used by Radix and Petronite.

The adapter is implemented across **five components**: the Kotlin Edge Agent (Android), the .NET Desktop Edge Agent (Windows), the Cloud Backend (.NET), VirtualLab (test simulator), and the Angular Portal (configuration UI).

## What This Adapter Does

1. **Speaks native TCP/JPL protocol** to DOMS FCCs over station LAN (Edge Agents)
2. **Manages persistent TCP connections** with STX/ETX binary framing, heartbeat keepalive, and automatic reconnection
3. **Authenticates via FcLogon handshake** (not REST API keys) with FcAccessCode, CountryCode, PosVersionId
4. **Polls supervised transaction buffers** using lock-read-clear semantics (FpSupTrans_req → process → clear_FpSupTrans_req)
5. **Sends pre-authorization commands** via authorize_Fp_req JPL messages
6. **Monitors 14-state pump state machine** (FpStatus with supplemental parameters)
7. **Receives unsolicited events** (FpStatus changes, transaction buffer updates, fuelling data) via the JPL channel
8. **Normalizes DOMS data encoding** (centilitres → microlitres, ×10 amounts → minor units, yyyyMMddHHmmss → ISO 8601 UTC)
9. **Simulates DOMS protocol** in VirtualLab for development/testing without physical hardware

## Critical Protocol Facts

| Aspect | DOMS (Real) | Other Adapters (Radix, Petronite) |
|--------|-------------|-----------------------------------|
| Transport | **TCP socket** (persistent connection) | REST (HTTP request/response) |
| Framing | **[STX] + JSON + [ETX]** (0x02...0x03) | HTTP |
| Authentication | **FcLogon_req** handshake | API key header / OAuth2 |
| Keepalive | **[STX][ETX]** heartbeat every 30 seconds | N/A (HTTP stateless) |
| Connection model | **Stateful** (persistent, must handle reconnection) | Stateless (per-request) |
| Pre-auth | **authorize_Fp_req** JPL message | HTTP POST |
| Transaction fetch | **FpSupTrans_req** (lock + read from supervised buffer) | HTTP GET |
| Transaction ACK | **clear_FpSupTrans_req** (explicit clear after processing) | Implicit (HTTP 200) or cursor advance |
| Pump addressing | **FpId** (filling point ID) + **NozzleId** (SupParam 09) | pumpNumber (integer) |
| Volume format | **Centilitres** (value × 100, e.g., 5.20L = 520) | Litres or microlitres |
| Amount format | **×10 factor** (e.g., 1000 TZS = 10000) | Minor units |
| Pump states | **14 states** (hex enum 0x00–0x0D) | Simple (Idle/Dispensing/etc.) |

## Technology Stack Per Component

| Component | Location | Language | Key Dependencies |
|-----------|----------|----------|------------------|
| Edge Agent Adapter | `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/` | Kotlin | java.net.Socket, kotlinx.coroutines, kotlinx.serialization |
| Desktop Agent Adapter | `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/` | C# (.NET 8) | System.Net.Sockets.TcpClient, System.Text.Json |
| Cloud Adapter | `src/cloud/FccMiddleware.Adapter.Doms/` | C# (.NET 8) | HttpClient (REST only — receives pre-normalized data from Edge) |
| VirtualLab Simulator | `VirtualLab/src/VirtualLab.Infrastructure/DomsJpl/` | C# (.NET 8) | System.Net.Sockets.TcpListener, System.Text.Json |
| Portal Config UI | `src/portal/src/app/features/site-config/` | TypeScript/Angular | Angular Reactive Forms |

## Project Structure — Edge Agent (Kotlin)

```
adapter/
├── common/
│   ├── IFccAdapter.kt                    # Core adapter interface (5 methods)
│   ├── IFccConnectionLifecycle.kt        # NEW: TCP lifecycle (connect/disconnect/isConnected)
│   ├── IFccEventListener.kt             # NEW: Unsolicited event callbacks
│   ├── AdapterTypes.kt                  # Shared types (envelopes, cursors, commands)
│   ├── CanonicalTransaction.kt          # Target normalization model
│   ├── PumpStatus.kt                    # Pump state model
│   ├── Enums.kt                         # FccVendor, PumpState, etc.
│   └── FccAdapterFactory.kt             # Factory (IMPLEMENTED_VENDORS gate)
└── doms/
    ├── DomsAdapter.kt                   # Main adapter (IFccAdapter + IFccConnectionLifecycle)
    ├── jpl/
    │   ├── JplTcpClient.kt              # TCP socket management + reconnection
    │   ├── JplFrameCodec.kt             # STX/ETX encode/decode
    │   ├── JplMessageRouter.kt          # Route responses to waiting callers
    │   ├── JplMessage.kt                # { name, subCode, data } model
    │   └── JplHeartbeatManager.kt       # 30s heartbeat sender + timeout detection
    ├── protocol/
    │   ├── DomsLogonHandler.kt          # FcLogon_req/resp
    │   ├── DomsPumpStatusParser.kt      # FpStatus_resp parsing (all SubCodes)
    │   ├── DomsTransactionParser.kt     # FpSupTrans_resp parsing
    │   ├── DomsPreAuthHandler.kt        # authorize_Fp_req/resp
    │   └── DomsSupParamParser.kt        # Supplemental parameter (ParId) decoding
    ├── model/
    │   ├── DomsFpMainState.kt           # 14-state enum
    │   ├── DomsSupParam.kt              # { parId, value }
    │   └── DomsTransactionDto.kt        # Raw DOMS transaction fields
    └── mapping/
        └── DomsCanonicalMapper.kt       # DOMS → CanonicalTransaction conversion
```

## Project Structure — Desktop Agent (.NET)

```
Adapter/Doms/
├── DomsAdapter.cs                       # EXISTING: REST-based (keep for VirtualLab testing)
├── DomsProtocolDtos.cs                  # EXISTING: REST DTOs (keep)
├── DomsJplAdapter.cs                    # NEW: TCP/JPL adapter (production)
├── Jpl/
│   ├── JplTcpClient.cs                  # TCP client wrapper with reconnection
│   ├── JplFrameCodec.cs                 # STX/ETX framing
│   ├── JplMessageRouter.cs             # Response correlation
│   ├── JplMessage.cs                    # Message model
│   └── JplHeartbeatManager.cs           # 30s keepalive
├── Protocol/
│   ├── DomsLogonHandler.cs
│   ├── DomsPumpStatusParser.cs
│   ├── DomsTransactionParser.cs
│   ├── DomsPreAuthHandler.cs
│   └── DomsSupParamParser.cs
├── Model/
│   ├── DomsFpMainState.cs               # 14-state enum
│   └── DomsJplTransactionDto.cs         # JPL transaction fields (distinct from REST DTOs)
└── Mapping/
    └── DomsCanonicalMapper.cs           # DOMS JPL → canonical conversion
```

## Key Architecture Rules

1. **TCP/JPL is the production protocol.** Edge agents MUST speak native TCP/JPL to real DOMS hardware. REST is only for VirtualLab testing.
2. **REST adapters remain.** The existing REST-based Desktop `DomsAdapter.cs` and Cloud `DomsCloudAdapter.cs` remain functional for VirtualLab simulator testing. Do NOT delete them.
3. **Factory selects protocol.** `FccAdapterFactory` selects TCP (`DomsJplAdapter`) vs REST (`DomsAdapter`) based on `ConnectionProtocol` in config.
4. **Persistent connection lifecycle.** TCP adapters implement `IFccConnectionLifecycle` (connect/disconnect/isConnected). HTTP adapters return no-op for these.
5. **Unsolicited event handling.** TCP adapters support `IFccEventListener` for push events. HTTP adapters do not implement this.
6. **Transaction acknowledgement is mandatory.** DOMS requires explicit `clear_FpSupTrans_req` after processing each transaction. The new `acknowledgeTransactions()` method handles this.
7. **Currency: `Long` minor units.** NEVER floating point for money. DOMS ×10 encoding → minor units: multiply by 10.
8. **Volume: `Long` microlitres.** DOMS centilitres → microlitres: multiply by 10,000.
9. **Timestamps: UTC ISO 8601.** DOMS `yyyyMMddHHmmss` → parse with FCC timezone from config → convert to UTC.
10. **Coroutine scoping (Kotlin).** Use structured concurrency. TCP read loop and heartbeat run as child coroutines of the adapter scope.
11. **No transaction left behind.** Lock-read-clear must be atomic per transaction. If processing fails, do NOT clear — let DOMS retry.
12. **Heartbeat is background.** The `heartbeat()` adapter method returns `isConnected()` status. Actual STX/ETX heartbeats are sent by `JplHeartbeatManager` on a 30s timer.
13. **Reconnection with backoff.** On TCP disconnect: reconnect with exponential backoff (1s, 2s, 4s, ..., max 60s configurable). Re-send FcLogon on reconnect.
14. **Cloud adapter does NOT speak TCP/JPL.** In production, the cloud receives pre-normalized transactions uploaded by Edge Agents. The cloud adapter validates and stores them.

## DOMS Data Encoding Reference

### Volume: Centilitres → Microlitres
```
DOMS raw (centilitres)  →  Canonical (microlitres)
Shortcut: DOMS × 10,000 = microlitres
Example: 520 × 10,000 = 5,200,000 microlitres (5.20 litres)
```

### Amount: ×10 Factor → Minor Units
```
DOMS raw (×10 factor)  →  Canonical (minor units)
Shortcut: DOMS × 10 = minor units
Example: 10000 × 10 = 100,000 minor units (1,000.00 TZS)
NOTE: Currency-dependent. Must use currency-specific conversion from config.
```

### Timestamps
```
DOMS format: "20260217095113" (yyyyMMddHHmmss, local to FCC timezone)
Step 1: Parse as LocalDateTime with pattern "yyyyMMddHHmmss"
Step 2: Apply FCC timezone from config (e.g., "Africa/Dar_es_Salaam")
Step 3: Convert to UTC Instant/DateTimeOffset
```

### Pump State Mapping
```
DOMS FpMainState → Canonical PumpState:
  0x00 Unconfigured     → Offline
  0x01 Closed           → Offline
  0x02 Idle             → Idle
  0x03 Error            → Error
  0x04 Calling          → Calling
  0x05 PreAuthorized    → Authorized
  0x06 Starting         → Dispensing
  0x07 StartingPaused   → Paused
  0x08 StartingTerminated → Completed
  0x09 Fuelling         → Dispensing
  0x0A FuellingPaused   → Paused
  0x0B FuellingTerminated → Completed
  0x0C Unavailable      → Offline
  0x0D UnavailableAndCalling → Offline
```

### Pump/Nozzle Mapping
```
DOMS FpId → fcc_pump_number (middleware) → pump_number (Odoo)
DOMS NozzleId (SupParam 09) → fcc_nozzle_number → odoo_nozzle_number
PumpNumberOffset from config is applied during normalization.
```

## Must-Read Artifacts (Before ANY Task)

| Artifact | Path | What It Tells You |
|----------|------|-------------------|
| DOMS Integration Plan | `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` | Protocol deep dive, interface gaps, architecture decision, conversion reference |
| Real DOMS Implementation | `DOMSRealImplementation/DppMiddleWareService/` | Production-tested TCP/JPL client, message formats, parsers |
| FCC Adapter Contracts | `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md` | IFccAdapter interface for all platforms |
| Canonical Transaction Schema | `schemas/canonical/canonical-transaction.schema.json` | Normalization target |
| Pre-Auth Record Schema | `schemas/canonical/pre-auth-record.schema.json` | Pre-auth lifecycle model |
| Pump Status Schema | `schemas/canonical/pump-status.schema.json` | Pump state model |
| Edge Agent HLD | `WIP-HLD-Edge-Agent.md` | Edge agent architecture, all flows |
| Edge Agent Dev Plan | `docs/plans/dev-plan-edge-agent.md` | Edge agent task sequencing, guardrails |
| Edge Agent Prompt | `docs/plans/agent-prompt-edge-agent.md` | Edge agent conventions, structure, rules |
| Desktop Agent Prompt | `docs/plans/agent-prompt-desktop-edge-agent.md` | Desktop agent conventions |
| Cloud Backend Prompt | `docs/plans/agent-prompt-cloud-backend.md` | Cloud conventions |
| VirtualLab Prompt | `docs/plans/agent-prompt-virtual-lab.md` | VirtualLab conventions |
| State Machines | `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` | Connectivity, pre-auth, sync state machines |
| Database Schema | `docs/specs/data-models/tier-1-4-database-schema-design.md` | Edge Room + Cloud DB schemas |
| Security Plan | `docs/specs/security/tier-2-5-security-implementation-plan.md` | Credential handling, keystore |

## Current Implementation Status

| Component | Status | Notes |
|-----------|--------|-------|
| Edge Agent (Kotlin) DomsAdapter | **STUB** — all methods throw | Needs full TCP/JPL implementation |
| Desktop Agent (C#) DomsAdapter | **COMPLETE** — REST-based | Keep as-is; add new `DomsJplAdapter` for TCP |
| Cloud DomsCloudAdapter | **COMPLETE** — REST-based | Minor updates for edge-upload validation |
| VirtualLab Simulator | **No DOMS TCP/JPL** | REST profiles exist; need TCP simulator |
| Portal FCC Config | **Basic** — generic form | Needs DOMS-specific fields (JPL port, FcAccessCode, etc.) |
| Test Suites | **Desktop + Cloud have tests** | Edge Agent needs all-new tests |

## Existing Patterns to Follow

### Desktop DomsAdapter (REST — reference for structure)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsAdapter.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsProtocolDtos.cs`
- `src/desktop-edge-agent/tests/FccDesktopAgent.Core.Tests/Adapter/Doms/DomsAdapterTests.cs`

### Cloud DomsCloudAdapter (reference for validation/normalization)
- `src/cloud/FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs`
- `src/cloud/FccMiddleware.Adapter.Doms.Tests/`

### Real DOMS Protocol (reference for TCP/JPL)
- `DOMSRealImplementation/DppMiddleWareService/ForecourtTcpWorker/Worker.cs` — TCP client, framing, message handling
- `DOMSRealImplementation/DppMiddleWareService/Helpers/ParserHelper/FpStatusParser.cs` — Status parsing
- `DOMSRealImplementation/DppMiddleWareService/Helpers/ParserHelper/FpSupTransBufStatusParser.cs` — Transaction parsing
- `DOMSRealImplementation/DppMiddleWareService/Helpers/Enums.cs` — State enumerations

## Testing Standards

- **Unit tests**: JUnit 5 + MockK (Kotlin), xUnit + Moq/NSubstitute (C#)
- **Frame codec tests**: Encode/decode STX/ETX frames, heartbeat detection, malformed frames, multi-message buffers
- **Mapper tests**: All data conversions (volume, amount, timestamp, pump state, supplemental params)
- **TCP client tests**: Connection, reconnection, heartbeat timeout, message correlation
- **Integration tests**: Edge Agent ↔ VirtualLab JPL Simulator end-to-end
- **Shared fixtures**: JSON message samples for all JPL request/response types
- **Reference validation**: Compare parser output against DOMSRealImplementation behavior

## Open Questions (Check Before Implementation)

| ID | Question | Impact |
|----|----------|--------|
| DQ-1 | Can all unsolicited events be received via JPL port 8888 using `UnsolicitedApcList`, or are DPP ports 5001-5006 required? | Single-port simplifies implementation |
| DQ-2 | Exact authorize_Fp_req message format for amount-based pre-auth? | Blocks pre-auth implementation |
| DQ-3 | Does deployed DOMS firmware match DOMSRealImplementation protocol? | Blocks production validation |
| DQ-4 | Is the ×10 amount encoding consistent across all DOMS deployments? | Financial data integrity |
| DQ-5 | Can we get access to a test DOMS FCC for live protocol validation? | Blocks hardware testing |
| DQ-6 | Does Urovo i9100 handle persistent TCP reliably under field conditions? | May need Android workarounds |
