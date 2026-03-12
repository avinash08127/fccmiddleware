# Radix FCC Adapter — Agent System Prompt

**Use this prompt as context when assigning ANY Radix adapter implementation task to an AI coding agent.**

---

## You Are Working On

The **Radix FCC Adapter** — the integration layer that enables the Forecourt Middleware to communicate with Radix Forecourt Controllers. Radix uses **HTTP POST with XML bodies** and **SHA-1 HMAC message signing** — fundamentally different from the TCP/JPL-based DOMS protocol. Radix operates on **two distinct ports** (authorization port P, transaction management port P+1) and uses a **three-level pump addressing model** (PUMP_ADDR + FP + NOZ).

The adapter is implemented across **five components**: the Kotlin Edge Agent (Android), the .NET Desktop Edge Agent (Windows), the Cloud Backend (.NET), VirtualLab (test simulator), and the Angular Portal (configuration UI).

## What This Adapter Does

1. **Speaks XML over HTTP** to Radix FCCs — all operations are POST requests with `Application/xml` bodies
2. **Signs every message** using SHA-1 hash of the XML content body concatenated with a shared secret password
3. **Operates on dual ports** — port P for external authorization (pre-auth), port P+1 for transaction management, products, day close, ATG, CSR
4. **Drains transactions via FIFO loop** — request oldest buffered transaction (CMD_CODE=10), ACK to dequeue (CMD_CODE=201), repeat until buffer empty (RESP_CODE=205)
5. **Sends pre-authorization commands** via `<FDCMS><AUTH_DATA>` XML to port P, with customer fiscal data (TIN, name, phone)
6. **Manages transaction mode explicitly** — CMD_CODE=20 sets ON_DEMAND (pull), UNSOLICITED (push), or OFF; must be issued on adapter startup
7. **Normalizes Radix data encoding** — decimal string volumes/amounts to microlitres/minor units, FDC_DATE+FDC_TIME to UTC ISO 8601, three-level pump addressing to canonical pump/nozzle
8. **Maps three-level pump addresses** — PUMP_ADDR (DSB/RDG unit) + FP (filling point) + NOZ (nozzle) to canonical pumpNumber + nozzleNumber via configurable `fccPumpAddressMap`
9. **Uses product read as heartbeat** — CMD_CODE=55 (read products/prices) is the liveness probe since Radix has no dedicated heartbeat endpoint

## Critical Protocol Facts

| Aspect | Radix | DOMS |
|--------|-------|------|
| Transport | HTTP POST (XML body) | TCP socket (persistent) |
| Auth | SHA-1 message signing + USN-Code header | FcLogon handshake |
| Connection model | **Stateless** (per-request HTTP) | Stateful (persistent TCP, must handle reconnection) |
| Ports | P (auth) + P+1 (transactions) | Single JPL port |
| Transaction fetch | FIFO drain: request -> ACK -> next (CMD_CODE=10) | Lock-read-clear supervised buffer |
| Transaction ACK | CMD_CODE=201 inline during fetch | clear_FpSupTrans_req (separate step) |
| Pre-auth | `<AUTH_DATA>` XML to port P | authorize_Fp_req JPL message |
| Mode management | CMD_CODE=20 (OFF/ON_DEMAND/UNSOLICITED) | Not applicable |
| Keepalive | N/A (HTTP stateless) | STX/ETX heartbeat every 30 seconds |
| Volume format | Litres as decimal string ("15.54") | Centilitres (integer) |
| Amount format | Currency units as decimal string ("30000.0") | x10 factor |
| Pump addressing | Three-level: PUMP_ADDR + FP + NOZ | FpId + NozzleId |
| Heartbeat | CMD_CODE=55 product read (no dedicated endpoint) | GET /heartbeat |
| Dedup key | Composed: `{FDC_NUM}-{FDC_SAVE_NUM}` | Single `transactionId` field |
| Fiscal data | EFD_ID (receipt), REG_ID (site TIN) | Field in transaction JSON |
| Customer ID | Typed: CUSTIDTYPE (1=TIN,2=DL,3=Voter,4=Passport,5=NID,6=NIL) | Generic customerTaxId |
| Pre-auth correlation | Numeric TOKEN (0-65535), echoed in dispense transaction | FCC echoes correlationId |

## Technology Stack Per Component

| Component | Location | Language | Key Dependencies |
|-----------|----------|----------|------------------|
| Edge Agent Adapter | `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/` | Kotlin | java.net.HttpURLConnection or OkHttp, kotlinx.serialization, javax.xml.parsers (DOM/SAX), java.security.MessageDigest (SHA-1) |
| Desktop Agent Adapter | `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/` | C# (.NET 10) | HttpClient, System.Xml.Linq (XDocument/XElement), System.Security.Cryptography.SHA1 |
| Cloud Adapter | `src/cloud/FccMiddleware.Adapter.Radix/` | C# (.NET 10) | HttpClient (for pull), System.Xml.Linq (XML parsing), System.Security.Cryptography.SHA1 |
| VirtualLab Simulator | `VirtualLab/src/VirtualLab.Infrastructure/RadixFdc/` | C# (.NET 10) | ASP.NET Core Minimal API (XML endpoints), System.Xml.Linq |
| Portal Config UI | `src/portal/src/app/features/site-config/` | TypeScript/Angular | Angular Reactive Forms (Radix-specific fields: sharedSecret, usnCode, authPort, pumpAddressMap) |

## Project Structure — Edge Agent (Kotlin)

```
adapter/
├── common/
│   ├── IFccAdapter.kt                    # Core adapter interface (6 methods incl. acknowledgeTransactions)
│   ├── AdapterTypes.kt                  # Shared types (envelopes, cursors, commands, AgentFccConfig)
│   ├── CanonicalTransaction.kt          # Target normalization model
│   ├── PumpStatus.kt                    # Pump state model
│   ├── Enums.kt                         # FccVendor (DOMS, RADIX, ...), PumpState, etc.
│   └── FccAdapterFactory.kt             # Factory (IMPLEMENTED_VENDORS gate)
├── doms/
│   └── DomsAdapter.kt                   # DOMS adapter (reference implementation)
└── radix/
    ├── RadixAdapter.kt                  # Main adapter implementing IFccAdapter
    ├── RadixProtocol.kt                 # XML building, signing, HTTP transport
    ├── RadixDtos.kt                     # Request/response data classes (TRN, FDCACK, etc.)
    └── RadixPumpAddressMapper.kt        # Three-level address <-> canonical pump mapping
```

## Project Structure — Desktop Agent (.NET)

```
Adapter/
├── Common/
│   ├── IFccAdapter.cs                   # Core adapter interface
│   ├── AdapterTypes.cs                  # Shared types (FccConnectionConfig, PreAuthCommand, etc.)
│   ├── Enums.cs                         # FccVendor (Doms, Radix), PumpState, etc.
│   └── IFccAdapterFactory.cs            # Factory interface
├── FccAdapterFactory.cs                 # Concrete factory (switch expression)
├── Doms/
│   ├── DomsAdapter.cs                   # DOMS adapter (reference implementation)
│   └── DomsProtocolDtos.cs              # DOMS DTOs
└── Radix/
    ├── RadixAdapter.cs                  # Main adapter implementing IFccAdapter
    ├── RadixProtocolDtos.cs             # XML request/response DTOs
    ├── RadixSignatureHelper.cs          # SHA-1 message signing utility
    ├── RadixXmlBuilder.cs               # XML request body builder (HOST_REQ, FDCMS)
    └── RadixXmlParser.cs                # XML response parser (FDC_RESP, FDCACK)
```

## Project Structure — Cloud Backend (.NET)

```
src/cloud/
├── FccMiddleware.Adapter.Radix/
│   ├── RadixCloudAdapter.cs             # Cloud-side adapter implementing IFccAdapter
│   ├── Internal/
│   │   ├── RadixTransactionParser.cs    # XML parsing for push-received transactions
│   │   └── RadixSignatureValidator.cs   # Verify incoming push signatures
│   └── FccMiddleware.Adapter.Radix.csproj
└── FccMiddleware.Adapter.Radix.Tests/
    ├── RadixCloudAdapterTests.cs
    ├── RadixTransactionParserTests.cs
    └── Fixtures/                        # XML sample payloads
```

## Key Architecture Rules

1. **HTTP stateless — no connection lifecycle.** Radix adapter does NOT implement `IFccConnectionLifecycle`. Each operation is an independent HTTP request. No persistent connection, no reconnection logic, no heartbeat sender.
2. **Dual-port scheme.** Port P (from `authPort` config) handles pre-auth commands. Port P+1 (derived as `authPort + 1`) handles transactions, products, mode changes, and all other operations. The adapter computes the transaction port internally.
3. **SHA-1 signing order matters.** For transaction management: `SHA1(<REQ>...</REQ> + SECRET_PASSWORD)` — the `<REQ>` tags are included, password concatenated immediately after `</REQ>` with no space. For pre-auth: `SHA1(<AUTH_DATA>...</AUTH_DATA> + SECRET_PASSWORD)`. Whitespace and special characters must match character-for-character.
4. **FIFO drain loop for pull mode.** Each `CMD_CODE=10` returns the oldest unacknowledged transaction. ACK with `CMD_CODE=201` to dequeue before requesting the next. Loop until `RESP_CODE=205` (buffer empty) or configurable max-per-fetch limit reached. The `FetchCursor` concept maps to: `hasMore = true` if limit reached, `hasMore = false` if buffer empty.
5. **Mode management on startup.** Issue `CMD_CODE=20` to set ON_DEMAND (1) or UNSOLICITED (2) based on ingestion config. Must be re-issued after any FCC restart/reconnection.
6. **Currency: `Long` minor units.** NEVER floating point for money. Radix decimal string amounts must be parsed with `BigDecimal`/`decimal` to avoid precision loss, then converted to minor units using the configured currency's decimal places.
7. **Volume: `Long` microlitres.** Radix litres (decimal string "15.54") -> parse as decimal -> multiply by 1,000,000 -> cast to long.
8. **Timestamps: UTC ISO 8601.** Radix `FDC_DATE="yyyy-MM-dd"` + `FDC_TIME="HH:mm:ss"` -> parse with configured FCC timezone -> convert to UTC.
9. **Three-level pump addressing.** Radix `PUMP_ADDR` + `FP` -> canonical `pumpNumber` via `fccPumpAddressMap` config. For pre-auth, reverse-map canonical pump number back to `(PUMP_ADDR, FP)` pair. `NOZ` -> `nozzleNumber` directly.
10. **Transaction dedup key.** Compose `fccTransactionId = "{FDC_NUM}-{FDC_SAVE_NUM}"`. Both fields together form a globally unique identifier.
11. **Pre-auth TOKEN for correlation.** Radix uses a numeric TOKEN (0-65535) as the only pre-auth-to-dispense correlation mechanism. The adapter must manage a per-FCC TOKEN registry to avoid collisions.
12. **acknowledgeTransactions() is a no-op.** Transaction acknowledgement is implicit — ACK (CMD_CODE=201) is sent inline during the fetch loop, not via a separate method call.
13. **No real-time pump status.** Radix spec does not expose pump state. `getPumpStatus()` returns an empty list. Adapter metadata should report `supportsPumpStatus = false`.
14. **Verify response signatures.** FDC responses contain signatures. The adapter should verify them to detect tampering/misconfiguration. Signature mismatch should log a warning but not block processing (defensive).

## Radix Data Encoding Reference

### Volume: Litres (decimal string) -> Microlitres
```
Radix raw (litres string)  ->  Canonical (microlitres)
Step 1: Parse "15.54" as decimal/BigDecimal
Step 2: Multiply by 1,000,000
Step 3: Cast to long
Example: 15.54 * 1,000,000 = 15,540,000 microlitres
```

### Amount: Decimal String -> Minor Units
```
Radix raw (currency string)  ->  Canonical (minor units)
Step 1: Parse "30000.0" as decimal/BigDecimal
Step 2: Multiply by 10^(currency decimal places)
         For TZS (0 decimals): 30000.0 * 1 = 30000 minor units
         For USD (2 decimals): 300.50 * 100 = 30050 minor units
Step 3: Cast to long
NOTE: Currency decimal places come from config. Getting this wrong corrupts all financial data.
```

### Timestamps
```
Radix format: FDC_DATE="2021-03-03" FDC_TIME="21:17:53" (local to FCC timezone)
Step 1: Combine into "2021-03-03T21:17:53"
Step 2: Parse as LocalDateTime
Step 3: Apply FCC timezone from config (e.g., "Africa/Dar_es_Salaam")
Step 4: Convert to UTC Instant/DateTimeOffset
```

### Dedup Key
```
fccTransactionId = "{FDC_NUM}-{FDC_SAVE_NUM}"
Example: FDC_NUM="100253410", FDC_SAVE_NUM="368989"
         fccTransactionId = "100253410-368989"
```

### Pump Address Mapping
```
Radix three-level  ->  Canonical two-level:
  PUMP_ADDR + FP  ->  pumpNumber  (via fccPumpAddressMap config)
  NOZ             ->  nozzleNumber (direct)

Pre-auth reverse mapping:
  canonical pumpNumber  ->  (PUMP_ADDR, FP) pair  (via fccPumpAddressMap config)
```

### Pre-Auth Error Code Mapping
```
Radix ACKCODE  ->  PreAuthResult:
  0   SUCCESS         ->  Accepted=true
  251 SIGNATURE ERROR ->  Accepted=false, ErrorCode="SIGNATURE_ERROR" (non-recoverable, config issue)
  255 BAD XML FORMAT  ->  Accepted=false, ErrorCode="BAD_XML" (non-recoverable, code bug)
  256 BAD HEADER      ->  Accepted=false, ErrorCode="BAD_HEADER" (non-recoverable, code bug)
  258 PUMP NOT READY  ->  Accepted=false, ErrorCode="PUMP_NOT_READY" (recoverable, transient)
  260 DSB OFFLINE     ->  Accepted=false, ErrorCode="DSB_OFFLINE" (recoverable, transient)
```

## Must-Read Artifacts (Before ANY Task)

| Artifact | Path | What It Tells You |
|----------|------|-------------------|
| Radix Integration Plan | `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` | Protocol deep dive, field mappings, open questions, architecture decisions |
| Radix Dev Plan | `docs/plans/dev-plan-radix-adapter.md` | Task sequencing, dependencies, acceptance criteria |
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
| DOMS Agent Prompt | `docs/plans/agent-prompt-doms-adapter.md` | DOMS adapter patterns to follow (structural reference) |
| State Machines | `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` | Connectivity, pre-auth, sync state machines |
| Database Schema | `docs/specs/data-models/tier-1-4-database-schema-design.md` | Edge Room + Cloud DB schemas |
| Security Plan | `docs/specs/security/tier-2-5-security-implementation-plan.md` | Credential handling, keystore |

## Current Implementation Status

| Component | Status | Notes |
|-----------|--------|-------|
| Shared Config (RX-0.1) | **COMPLETE** | FccVendor.Radix, Radix-specific fields on AgentFccConfig/FccConnectionConfig/SiteFccConfig |
| Factory Registration (RX-0.2) | **COMPLETE** | All three factories throw NotImplementedException/UnsupportedOperationException |
| Interface Compatibility (RX-0.3) | **COMPLETE** | acknowledgeTransactions() no-op on both edge platforms |
| Edge Agent (Kotlin) RadixAdapter | **NOT STARTED** | RX-1.x tasks |
| Desktop Agent (C#) RadixAdapter | **NOT STARTED** | RX-2.x tasks |
| Transaction Fetch Loop | **NOT STARTED** | RX-3.x tasks (FIFO drain loop) |
| Pre-Auth | **NOT STARTED** | RX-4.x tasks |
| Push Mode | **NOT STARTED** | RX-5.x tasks |
| Cloud RadixCloudAdapter | **NOT STARTED** | RX-6.x tasks |
| VirtualLab Simulator | **NOT STARTED** | RX-7.x tasks |
| Portal Config UI | **NOT STARTED** | RX-8.x tasks |

## Existing Patterns to Follow

### Desktop DomsAdapter (REST — reference for structure)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsAdapter.cs` — Full IFccAdapter implementation with HttpClient
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsProtocolDtos.cs` — Protocol-specific DTOs
- `src/desktop-edge-agent/tests/FccDesktopAgent.Core.Tests/Adapter/Doms/DomsAdapterTests.cs` — Adapter unit tests

### Cloud DomsCloudAdapter (reference for validation/normalization)
- `src/cloud/FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs` — Cloud adapter with normalization and validation
- `src/cloud/FccMiddleware.Adapter.Doms.Tests/` — Cloud adapter tests with fixture files

### Edge Agent DomsAdapter (Kotlin — reference for stub structure)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/DomsAdapter.kt` — Kotlin adapter stub

### Shared Adapter Infrastructure
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/` — Kotlin adapter interfaces, types, factory
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/` — .NET adapter interfaces, types, factory
- `src/cloud/FccMiddleware.Infrastructure/Adapters/FccAdapterFactory.cs` — Cloud registry-based factory

## Testing Standards

- **Unit tests**: JUnit 5 + MockK (Kotlin), xUnit + Moq/NSubstitute (C#)
- **XML fixture files**: Store sample Radix XML payloads (transaction responses, pre-auth ACKs, error responses, push payloads) as test fixtures
- **Signature tests**: Verify SHA-1 computation with known input/output pairs from the Radix spec
- **Parser tests**: XML parsing of `<TRN>`, `<FDCACK>`, `<FDC_RESP>` elements with all field variants (empty, missing, malformed)
- **Mapper tests**: All data conversions (volume decimal -> microlitres, amount decimal -> minor units, timestamps -> UTC, pump address mapping)
- **FIFO drain tests**: Verify request-ACK loop behavior: single transaction, multiple transactions, empty buffer, mid-loop errors
- **Mode management tests**: Verify CMD_CODE=20 is sent on startup with correct mode value
- **Integration tests**: Edge Agent <-> VirtualLab Radix simulator end-to-end
- **Naming convention**: `Radix*Tests.cs` / `Radix*Test.kt`, test methods describe scenario and expected outcome

## Open Questions (Check Before Implementation)

| ID | Question | Impact |
|----|----------|--------|
| RQ-1 | **Currency decimal handling:** Radix `AMO="30000.0"` and `PRICE="1930"` — are these in major currency units or minor units? | Determines conversion to `amountMinorUnits`. Getting this wrong silently corrupts all financial data. |
| RQ-2 | **Pump address mapping:** How to map three-level Radix addressing (PUMP_ADDR + FP + NOZ) to two-level canonical model (pumpNumber + nozzleNumber)? | Affects pump/nozzle mapping tables, pre-auth command building, and transaction normalization. |
| RQ-3 | **Token-based correlation limits:** With only 65536 possible TOKEN values (0-65535), how to handle high-volume pre-auth sites? | Could cause incorrect pre-auth/dispense matching if a token is reused while a previous pre-auth is still active. |
| RQ-4 | **Push endpoint configuration on FDC:** What URL does the Radix FDC push unsolicited transactions to? Is it configurable? | Determines whether CLOUD_DIRECT mode is feasible or must use RELAY/BUFFER_ALWAYS. |
| RQ-5 | **Does Radix FDC support HYBRID mode?** Can we switch between ON_DEMAND and UNSOLICITED dynamically? | Determines whether HYBRID ingestion mode is feasible for Radix sites. |
| RQ-6 | **Pump status:** Radix spec does not expose real-time pump status. Can Radix sites operate without it? | Affects GetPumpStatusAsync implementation and Odoo POS pump display. |
| RQ-7 | **FDC_PROD mapping:** Is FDC_PROD a 0-based index or a product ID? How stable is it across reconfigurations? | Determines how to build productCodeMapping config. |
| RQ-8 | **CUST_DATA element:** When USED="1", what fields are present? Does it echo back pre-auth customer data? | Determines if customer data can be extracted from normal order transactions. |
