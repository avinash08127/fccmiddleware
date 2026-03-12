# Unified FCC Adapter Development Plan: DOMS + Radix + Petronite

**Consolidates:** `dev-plan-doms-adapter.md` (31 tasks), `dev-plan-radix-adapter.md` (30 tasks), `dev-plan-petronite-adapter.md` (19 tasks)

**Result:** 80 tasks → **58 tasks** after deduplication

**Sprint Cadence:** 2-week sprints

**Reference Documents:**
- `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — DOMS protocol deep dive
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — Radix protocol deep dive
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — Petronite protocol deep dive

---

## Context

Three separate development plans exist for implementing FCC adapters (DOMS, Radix, Petronite) across five components (Kotlin Edge Agent, .NET Desktop Agent, Cloud Backend, Portal, VirtualLab). The three plans have significant duplication in shared interface changes, config extensions, factory registrations, DB migrations, and Portal UI. This unified plan consolidates them into dependency-optimized phases, enabling maximum parallel execution across three vendor tracks.

---

## Current Implementation Status

| Component | DOMS | Radix | Petronite |
|-----------|------|-------|-----------|
| **Edge Agent (Kotlin)** | Stub (throws) | Stub + partial infra (SignatureHelper + DTOs complete, XML builder/parser empty) | Not started (enum exists) |
| **Desktop Agent (.NET)** | **Complete** (REST/HTTP) | Not started (factory throws `NotImplementedException`) | Not started (enum missing) |
| **Cloud Backend** | **Complete** | Not started | Not started |
| **VirtualLab** | Generic framework only | Generic framework only | Generic framework only |
| **Portal** | Basic config form | Vendor in dropdown, no specific fields | No UI |

---

## Protocol Comparison

| Aspect | DOMS | Radix | Petronite |
|--------|------|-------|-----------|
| Transport | TCP/JPL (persistent, binary framing) | HTTP/XML (stateless, dual-port) | REST/JSON (stateless, OAuth2) |
| Auth | FcLogon handshake | SHA-1 message signing + USN-Code header | OAuth2 Client Credentials |
| Transaction fetch | Lock-read-clear supervised buffer | FIFO drain: request → ACK → next | Push-only via webhook (no pull API) |
| Transaction ACK | `clear_FpSupTrans_req` (separate step) | `CMD_CODE=201` inline during fetch | Implicit (HTTP 200 on webhook) |
| Pre-auth | `authorize_Fp_req` JPL message | `<AUTH_DATA>` XML to port P | Two-step: Create Order + Authorize Pump |
| Pre-auth cancel | Not applicable (pump control) | `<AUTH>FALSE</AUTH>` XML | `POST /{id}/cancel` REST |
| Pump status | Real-time via `FpStatus_req` | Not available (no endpoint) | Synthesized from nozzle assignments |
| Volume format | Centilitres (integer) | Litres as decimal string ("15.54") | Litres as decimal (25.50) |
| Amount format | ×10 factor (integer) | Currency decimal string ("30000.0") | Major currency units (71400.00) |
| Connection lifecycle | `IFccConnectionLifecycle` (connect/disconnect) | Stateless (no lifecycle) | Stateless (no lifecycle) |
| Push mode | Unsolicited JPL events | `CMD_CODE=20` MODE=2 (unsolicited HTTP) | Webhook (`transaction_completed`) |

---

## Deduplication Summary

| Duplicate Area | Original Tasks | Resolution |
|---|---|---|
| `acknowledgeTransactions` on IFccAdapter | DOMS-0.3, RX-0.3 | **ELIMINATED** — already exists on both platforms |
| Config extensions (all vendor fields) | DOMS-0.5/0.6/0.7, RX-0.1, PN-0.1 (6 tasks) | **UNI-0.1** — one task, all vendors, all layers |
| FccVendor enum updates | RX-0.1, PN-0.1 | **UNI-0.1** — consolidated |
| PreAuthCommand customer fields | RX-0.1, PN-0.1 | **ELIMINATED** — already exist on both platforms |
| Factory registrations | DOMS, RX-0.2, PN-0.2 (3+ tasks) | **UNI-0.3** — one task, all vendors, all layers |
| DB migration | DOMS-5.2, RX-6.4 (2 tasks) | **UNI-3.4** — one migration for all vendors |
| Portal vendor config UI | DOMS-5.3, RX-6.5, PN-5.3 (3 tasks) | **UNI-3.5** — one component, conditional sections |
| Agent prompt documents | 3 separate docs | **UNI-0.5** — one unified doc |

---

## Phase 0 — Shared Foundation (Sprint 1)

Cross-cutting prerequisites that unblock all three vendor tracks.

### UNI-0.1: Unified Config & Enum Extensions Across All Layers

**Components:** Kotlin Edge Agent, .NET Desktop Agent, Cloud Backend
**Prereqs:** None
**Effort:** 1.5 days

Add ALL remaining vendor-specific config fields in one pass:

- **.NET Desktop `Enums.cs`**: Add `Petronite`, `Advatec` to `FccVendor`
- **.NET Desktop `FccConnectionConfig`**: Add DOMS TCP fields (`ConnectionProtocol`, `JplPort`, `FcAccessCode`, `DomsCountryCode`, `PosVersionId`, `HeartbeatIntervalSeconds`, `ReconnectBackoffMaxSeconds`, `ConfiguredPumps`) + Petronite OAuth fields (`ClientId`, `ClientSecret`, `WebhookSecret`, `OAuthTokenEndpoint`)
- **Kotlin `AgentFccConfig`**: Add DOMS TCP fields (`jplPort`, `fcAccessCode`, `domsCountryCode`, `posVersionId`, `heartbeatIntervalSeconds`, `reconnectBackoffMaxSeconds`, `configuredPumps`) + Petronite OAuth fields (`clientId`, `clientSecret`, `webhookSecret`, `oauthTokenEndpoint`)
- **Cloud `SiteFccConfig`**: Add DOMS TCP fields (`JplPort`, `DppPorts`, `FcAccessCode`, `DomsCountryCode`, `PosVersionId`, `HeartbeatIntervalSeconds`, `ReconnectBackoffMaxSeconds`, `ConfiguredPumps`) + Petronite OAuth fields (`ClientId`, `ClientSecret`, `WebhookSecret`, `OAuthTokenEndpoint`)
- All new fields nullable. `FcAccessCode`, `ClientId`, `ClientSecret` marked sensitive.

**Files:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/Enums.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/AdapterTypes.kt`
- `src/cloud/FccMiddleware.Domain/Models/Adapter/SiteFccConfig.cs`

**Acceptance criteria:**
- All vendor enums complete on all platforms
- DOMS TCP + Petronite OAuth fields present on all 3 config models (Radix already done)
- All sensitive fields annotated correctly
- Existing DOMS and Radix code compiles without changes

---

### UNI-0.2: IFccConnectionLifecycle + IFccEventListener Interfaces

**Components:** Kotlin Edge Agent, .NET Desktop Agent
**Prereqs:** None
**Effort:** 1 day

Create two new interfaces (DOMS-only — Radix and Petronite adapters do NOT implement them):

**IFccConnectionLifecycle** (4 methods):
- `connect()` / `ConnectAsync()` — TCP connect + FcLogon
- `disconnect()` / `DisconnectAsync()` — clean shutdown
- `isConnected()` / `IsConnected` — check socket state
- `setEventListener()` / `SetEventListener()` — register callback handler

**IFccEventListener** (4 callbacks):
- `onPumpStatusChanged` — unsolicited FpStatus change
- `onTransactionAvailable` — transaction buffer update
- `onFuellingUpdate` — live fuelling volume/amount
- `onConnectionLost` — TCP connection dropped

**TransactionNotification** data class: `fpId`, `transactionBufferIndex`, `timestamp`

**Files (create new):**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/IFccConnectionLifecycle.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/IFccEventListener.kt`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccConnectionLifecycle.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccEventListener.cs`

**Acceptance criteria:**
- Interfaces compile on both platforms
- No changes to `IFccAdapter` itself
- Existing adapter code compiles without modification

---

### UNI-0.3: Unified Factory Registrations (All Vendors, All Layers)

**Components:** All three layers
**Prereqs:** UNI-0.1
**Effort:** 0.5 day

- **Kotlin**: Add `PETRONITE` case to `when` block (DOMS/RADIX already there)
- **.NET Desktop**: Add Petronite case (throw `NotImplementedException`)
- **Cloud**: Wire registry in `Program.cs` for DOMS (existing adapter), RADIX (stub), PETRONITE (stub)

**Files:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/FccAdapterFactory.kt`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs`
- `src/cloud/FccMiddleware.Infrastructure/Adapters/FccAdapterFactory.cs`
- `src/cloud/FccMiddleware.Api/Program.cs`

**Acceptance criteria:**
- All factories recognize all vendors
- Unimplemented vendors throw `NotImplementedException` / `AdapterNotImplementedException`
- Existing DOMS path unaffected

---

### UNI-0.4: CadenceController TCP Lifecycle Support

**Components:** Kotlin Edge Agent, .NET Desktop Agent
**Prereqs:** UNI-0.2
**Effort:** 1 day

Update CadenceController to detect `is IFccConnectionLifecycle` at runtime:
- If true: call `connect()` on startup, `disconnect()` on shutdown, wire `IFccEventListener` callbacks
- If false (Radix, Petronite): skip lifecycle management entirely
- Handle `onConnectionLost` → mark FCC unreachable, trigger reconnect
- Handle `onTransactionAvailable` → trigger immediate transaction fetch
- Call `acknowledgeTransactions()` after successful buffering (DOMS needs it; Radix/Petronite no-op)

**Acceptance criteria:**
- TCP adapters connected at startup, disconnected at shutdown
- Non-TCP adapters unaffected
- Unsolicited events handled correctly
- Unit tests with mock adapter

---

### UNI-0.5: Unified Agent Prompt Document

**Components:** Documentation
**Prereqs:** None
**Effort:** 0.5 day

Create `docs/plans/agent-prompt-unified-adapters.md` covering all three vendors' protocol specifics, architecture rules, project structures, and testing standards in one consolidated document.

---

## Phase 1 — Protocol Infrastructure (Sprints 1-3)

**Three vendor tracks run in PARALLEL.** Each is self-contained at the protocol level.

---

### Track A: DOMS TCP/JPL Protocol

#### DOMS-1.1: JPL Frame Codec (Kotlin)
**Prereqs:** UNI-0.2 | **Effort:** 1 day

STX/ETX binary frame codec: `encode(json) → ByteArray`, `encodeHeartbeat() → [0x02, 0x03]`, `decode(buffer) → DecodeResult` (sealed class: Frame/Heartbeat/Incomplete/Error). Handle multi-frame buffers, split TCP reads, edge cases.

**Reference:** `DOMSRealImplementation/DppMiddleWareService/ForecourtTcpWorker/Worker.cs` — lines 327-361 (ProcessReceived)
**Create:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/jpl/JplFrameCodec.kt`
**Tests:** At least 10 unit tests covering all edge cases.

#### DOMS-1.2: JPL Message Model + Pump State Enum (Kotlin)
**Prereqs:** None | **Effort:** 0.5 day

`JplMessage` data class (name, subCode, data as JsonObject). `DomsFpMainState` enum (14 pump states 0x00–0x0D) with `toCanonicalPumpState()` mapping and `fromHex()` parser. `DomsSupParam`, `DomsTransactionDto`.

**Reference:** `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §2.3, §2.5, §2.6
**Create:** `adapter/doms/jpl/JplMessage.kt`, `model/DomsFpMainState.kt`, `model/DomsSupParam.kt`, `model/DomsTransactionDto.kt`

#### DOMS-1.3: JPL TCP Client + Heartbeat Manager (Kotlin)
**Prereqs:** DOMS-1.1, DOMS-1.2 | **Effort:** 3 days

Persistent TCP socket with coroutine read loop on `Dispatchers.IO`. Response correlation via `ConcurrentHashMap<String, CompletableDeferred<JplMessage>>`. Automatic reconnection with exponential backoff (1s, 2s, 4s, ..., max from config). Heartbeat `[STX][ETX]` every `heartbeatIntervalSeconds` (default 30s). Dead connection detection at 3× heartbeat interval.

**Reference:** `Worker.cs` — lines 218-247 (receive buffer), lines 154-180 (connection init)
**Create:** `adapter/doms/jpl/JplTcpClient.kt`, `adapter/doms/jpl/JplHeartbeatManager.kt`

#### DOMS-1.4: DOMS Canonical Mapper (Kotlin)
**Prereqs:** DOMS-1.2 | **Effort:** 1.5 days

Volume: centilitres × 10,000 = microlitres. Amount: ×10 value × 10 = minor units. Timestamp: `yyyyMMddHHmmss` + configured timezone → UTC ISO 8601. Pump offset from config. Product code mapping with fallback. **No floating-point arithmetic** — Long only. SupParam parser extracts volume (ParId=05), money (ParId=06), nozzleId (ParId=09).

**Reference:** `docs/FCCAdapters/DOMS/WIP-DOMSFCCAdapterPlan.md` — §5
**Create:** `adapter/doms/mapping/DomsCanonicalMapper.kt`, `adapter/doms/protocol/DomsSupParamParser.kt`
**Tests:** At least 15 unit tests for all conversion paths.

#### DOMS-1.5: JPL Frame Codec + Models + TCP Client (.NET)
**Prereqs:** DOMS-1.1 (Kotlin reference) | **Effort:** 3 days

Port frame codec using `ReadOnlySpan<byte>` / `Memory<byte>`. Port message models. Port TCP client using `System.Net.Sockets.TcpClient` + `NetworkStream` + `ConcurrentDictionary<string, TaskCompletionSource<JplMessage>>`. Port heartbeat manager. `CancellationToken` throughout. Same test fixtures as Kotlin.

**Create:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/Jpl/` (5+ files)

#### DOMS-1.6: DOMS Canonical Mapper (.NET)
**Prereqs:** DOMS-1.5 | **Effort:** 1 day

Mirror Kotlin mapper. Same conversion logic, `decimal` arithmetic, shared test vectors. No floating-point.

**Create:** `Adapter/Doms/Mapping/DomsCanonicalMapper.cs`, `Adapter/Doms/Protocol/DomsSupParamParser.cs`

---

### Track B: Radix HTTP/XML Protocol

#### RX-1.4: XML Request Builder (Kotlin) — IMPLEMENT EXISTING EMPTY FILE
**Prereqs:** None (RadixSignatureHelper + RadixProtocolDtos already complete) | **Effort:** 1.5 days

Implement the 6 builder methods in the currently-empty `RadixXmlBuilder.kt`:
1. `buildTransactionRequest(token, secret)` — CMD_CODE=10, CMD_NAME=TRN_REQ
2. `buildTransactionAck(token, secret)` — CMD_CODE=201, CMD_NAME=SUCCESS
3. `buildModeChangeRequest(mode, token, secret)` — CMD_CODE=20, MODE element
4. `buildProductReadRequest(token, secret)` — CMD_CODE=55, CMD_NAME=PRODUCT_REQ
5. `buildPreAuthRequest(params, secret)` — AUTH_DATA with all fields
6. `buildPreAuthCancelRequest(pump, fp, token, secret)` — AUTH=FALSE

**Critical:** Build inner content (`<REQ>` or `<AUTH_DATA>`) FIRST, compute SHA-1 via `RadixSignatureHelper`, THEN wrap in outer envelope with `<SIGNATURE>` or `<FDCSIGNATURE>` element.

**Reference:** `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.3, §2.4, §2.6, §2.12
**File:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixXmlBuilder.kt`

#### RX-1.5: XML Response Parser (Kotlin) — IMPLEMENT EXISTING EMPTY FILE
**Prereqs:** None (DTOs already complete) | **Effort:** 1.5 days

Implement parsers in the currently-empty `RadixXmlParser.kt`:
- `parseTransactionResponse(xml)` — handle RESP_CODE 201 (success), 205 (no transaction), 30 (unsolicited), and error codes 206/251/253/255
- `parseAuthResponse(xml)` — handle ACKCODE 0 (success), 251/255/256/258/260 (errors)
- `parseProductResponse(xml)` — product list from CMD_CODE=55
- Signature validation for both response types

**Reference:** `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.4, §2.5, §2.6, Appendix A
**File:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixXmlParser.kt`

#### RX-1.6: Heartbeat (Kotlin)
**Prereqs:** RX-1.4, RX-1.5 | **Effort:** 0.5 day

`heartbeat()` using CMD_CODE=55 (product read) on port P+1. 5-second hard timeout, never throws — returns Boolean. Signature error (RESP_CODE=251) logged as WARNING (config issue).

#### RX-2.1: Scaffold + SignatureHelper + DTOs (.NET Desktop)
**Prereqs:** UNI-0.1 | **Effort:** 1.5 days

Create `Adapter/Radix/` directory. Port `RadixSignatureHelper` (SHA-1 signing — same algorithm, same test vectors), `RadixProtocolDtos` (C# records mirroring Kotlin data classes). Stub `RadixAdapter.cs` with `NotImplementedException` (except `AcknowledgeTransactionsAsync` returns `true`).

**Create:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/` (5 files)

#### RX-2.4: XML Builder + Parser (.NET)
**Prereqs:** RX-2.1, RX-1.4/1.5 (Kotlin reference) | **Effort:** 2 days

Port XML builder/parser using `System.Xml.Linq`. Same signing order, same test fixtures for cross-platform consistency.

#### RX-2.5: Heartbeat (.NET)
**Prereqs:** RX-2.4 | **Effort:** 0.5 day

Port heartbeat. CMD_CODE=55, `IHttpClientFactory` pattern, 5-second timeout.

---

### Track C: Petronite REST/JSON Protocol

#### PN-1.1: Project Scaffold (.NET Desktop)
**Prereqs:** UNI-0.1 | **Effort:** 0.5 day

Create `Adapter/Petronite/` directory with stubs: `PetroniteAdapter.cs` (implementing IFccAdapter with `NotImplementedException`), `PetroniteProtocolDtos.cs`, `PetroniteOAuthClient.cs`, `PetroniteNozzleResolver.cs`. Create test directory and fixtures directory.

**Create:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Petronite/`

#### PN-1.2: OAuth2 Client Credentials
**Prereqs:** PN-1.1 | **Effort:** 2 days

`PetroniteOAuthClient`:
- Token acquisition: `POST /oauth/token`, `Content-Type: application/x-www-form-urlencoded`, `Authorization: Basic <Base64(clientId:clientSecret)>`, `grant_type=client_credentials`
- Token caching with TTL from `expires_in`
- Proactive refresh when remaining TTL < threshold (default 10 minutes)
- Thread-safe via `SemaphoreSlim(1,1)`
- `InvalidateToken()` for 401 handling with single retry

**Reference:** `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.2, §9.4

#### PN-1.3: Protocol DTOs
**Prereqs:** PN-1.1 | **Effort:** 1 day

All Petronite DTOs as C# records: `PetroniteTokenResponse`, `PetroniteNozzleAssignment` (Pump + Nozzles), `PetroniteCreateOrderRequest/Response`, `PetroniteAuthorizeRequest/Response`, `PetroniteCancelResponse`, `PetronitePendingOrdersResponse`, `PetroniteWebhookPayload`, `PetroniteTransactionData`, `PetroniteFieldError`. Use `[JsonPropertyName]` for snake_case fields. Use `decimal` for monetary/volume values.

**Reference:** `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.2–§2.11, Appendix D

#### PN-1.4: Nozzle ID Resolver
**Prereqs:** PN-1.2, PN-1.3 | **Effort:** 1.5 days

`PetroniteNozzleResolver`:
- `InitializeAsync()` — fetch `GET /nozzles/assigned`, build bidirectional mapping
- Forward: `(fccPumpNumber, fccNozzleNumber) → petroniteNozzleId`
- Reverse: `petroniteNozzleId → (canonicalPumpNumber, canonicalNozzleNumber)`
- Thread-safe immutable snapshot pattern
- Periodic refresh (default 30 minutes)
- Config/API mismatch produces warning, API data is authoritative

**Reference:** `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.3, §9.3

#### PN-1.5: Heartbeat
**Prereqs:** PN-1.2, PN-1.4 | **Effort:** 0.5 day

`HeartbeatAsync()` using `GET /nozzles/assigned` as liveness probe. OAuth token validated. 5-second timeout. 401 → invalidate + retry once. Never throws — returns `bool`.

---

## Phase 2 — Full Adapter Implementations (Sprints 3-5)

Three parallel tracks continue. Each vendor completes fetch, normalization, pre-auth, and push support.

---

### Track A: DOMS Full Adapter

#### DOMS-2.1: Protocol Handlers (Kotlin)
**Prereqs:** DOMS-1.2, DOMS-1.3 | **Effort:** 2.5 days

Create 4 protocol handler classes:
1. `DomsLogonHandler` — build FcLogon_req (FcAccessCode, CountryCode, PosVersionId, UnsolicitedApcList), parse FcLogon_resp
2. `DomsPumpStatusParser` — build FpStatus_req (SubCodes 00H–03H), parse FpStatus_resp with supplemental params, map DomsFpMainState → PumpState
3. `DomsTransactionParser` — build FpSupTrans_req (lock), clear_FpSupTrans_req (clear), parse FpSupTrans_resp, parse FpSupTransBufStatus entries
4. `DomsPreAuthHandler` — build authorize_Fp_req, parse authorize_Fp_resp

**Reference:** `Worker.cs` — FcLogon (lines 618-637), FpStatus (lines 721-845), transactions (lines 1029-1068)
**Create:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/protocol/` (4 files)
**Tests:** At least 20 test cases across all handlers.

#### DOMS-2.2: Full DomsAdapter Implementation (Kotlin)
**Prereqs:** DOMS-1.3, DOMS-1.4, DOMS-2.1 | **Effort:** 3 days

Replace the stub `DomsAdapter.kt`. Implements both `IFccAdapter` and `IFccConnectionLifecycle`:
- `connect()` — TCP connect → FcLogon_req → verify success → start heartbeat
- `disconnect()` — stop heartbeat → close TCP
- `fetchTransactions()` — check supervised buffer → lock (FpSupTrans_req) → parse → return RawPayloadEnvelopes
- `acknowledgeTransactions()` — for each id: send clear_FpSupTrans_req
- `normalize()` — delegate to DomsCanonicalMapper (no network I/O, <500ms)
- `sendPreAuth()` — build authorize_Fp_req, send, parse response
- `getPumpStatus()` — for each configured pump: send FpStatus_req, parse response
- `heartbeat()` — return `isConnected()`
- Set `IS_IMPLEMENTED = true`, add to `IMPLEMENTED_VENDORS`

#### DOMS-2.3: Protocol Handlers (.NET)
**Prereqs:** DOMS-1.5, DOMS-2.1 (Kotlin reference) | **Effort:** 2 days

Port all 4 protocol handlers to .NET. `CancellationToken` throughout. Same test fixtures.

**Create:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/Protocol/` (4 files)

#### DOMS-2.4: DomsJplAdapter (.NET — new class alongside REST)
**Prereqs:** DOMS-1.5, DOMS-1.6, DOMS-2.3 | **Effort:** 3 days

Create **new** `DomsJplAdapter.cs` implementing `IFccAdapter` + `IFccConnectionLifecycle`. Mirrors Kotlin adapter. Existing `DomsAdapter.cs` (REST) remains untouched for VirtualLab testing.

**Create:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsJplAdapter.cs`

#### DOMS-2.5: Factory Dual-Protocol Selection (Both Platforms)
**Prereqs:** DOMS-2.2, DOMS-2.4 | **Effort:** 0.5 day

Update both factories: `ConnectionProtocol == "TCP"` → DomsJplAdapter, else → DomsAdapter (REST, default for backward compatibility).

---

### Track B: Radix Full Adapter

#### RX-3.1: Transaction Fetch — FIFO Drain Loop (Kotlin)
**Prereqs:** RX-1.4, RX-1.5, RX-1.6 | **Effort:** 2.5 days

`fetchTransactions()`:
1. `ensureModeAsync(1)` — set ON_DEMAND mode (CMD_CODE=20), cache mode state
2. Loop (max = limit): CMD_CODE=10 request → parse → if RESP_CODE=205 break → else ACK CMD_CODE=201 → continue
3. Token counter (0–65535, wrapping), same TOKEN for request/ACK pair
4. Error handling: RESP_CODE=251 (signature, non-recoverable), 253 (token, retry), 206 (mode, re-send)
5. Return `TransactionBatch` with `hasMore = true` if limit hit

**Reference:** `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.3, §2.4, §9.4, §9.5

#### RX-3.2: Transaction Fetch — FIFO Drain (.NET)
**Prereqs:** RX-2.4, RX-3.1 (Kotlin reference) | **Effort:** 2 days

Port FIFO drain loop. `IHttpClientFactory`, `CancellationToken` throughout. Same logic.

#### RX-3.3: Transaction Normalization (Kotlin)
**Prereqs:** RX-1.5 | **Effort:** 2.5 days

`normalize()`: parse raw XML → `CanonicalTransaction`:
- `fccTransactionId` = `"{FDC_NUM}-{FDC_SAVE_NUM}"` (composed dedup key)
- `volumeMicrolitres` = `BigDecimal(VOL) * 1_000_000` → Long
- `amountMinorUnits` = `BigDecimal(AMO) * 10^currencyDecimalPlaces` → Long
- `startedAt`/`completedAt` = parse FDC_DATE+FDC_TIME / RDG_DATE+RDG_TIME with timezone → UTC
- `pumpNumber` = resolve via `fccPumpAddressMap`, fallback to raw PUMP_ADDR
- `productCode` = map via `productCodeMapping`, fallback to FDC_PROD_NAME
- `fiscalReceiptNumber` = EFD_ID (null if empty)

**Reference:** `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §4

#### RX-3.4: Transaction Normalization (.NET)
**Prereqs:** RX-2.4, RX-3.3 (Kotlin reference) | **Effort:** 1.5 days

Port normalization using `decimal` arithmetic. Same field mappings and test vectors.

#### RX-3.5: Mode Management Lifecycle (Both Platforms)
**Prereqs:** RX-3.1, RX-3.2 | **Effort:** 1 day

`currentMode` state caching, `ensureModeAsync(desiredMode)` (no-op if cached matches), `resetModeState()` on connectivity loss, MODE=0 on shutdown (best-effort).

#### RX-4.1: SendPreAuth (Kotlin)
**Prereqs:** RX-1.4, RX-1.5 | **Effort:** 2 days

Resolve canonical pump → `(PUMP_ADDR, FP)` via `fccPumpAddressMap`. Generate TOKEN (0–65535), track in `ConcurrentHashMap<Int, ActivePreAuth>`. Build AUTH_DATA XML with customer fields, POST to port P. Map ACKCODE: 0=AUTHORIZED, 258=DECLINED (pump not ready), 260=DECLINED (DSB offline), 251=ERROR (signature).

#### RX-4.2: SendPreAuth + CancelPreAuth (.NET)
**Prereqs:** RX-2.4, RX-4.1 (Kotlin reference) | **Effort:** 2 days

Port pre-auth. Add `CancelPreAuthAsync`: look up TOKEN → build `<AUTH>FALSE</AUTH>` XML → POST to port P. ACKCODE=0 or 258 → true (cancelled or already idle). TOKEN tracking in `ConcurrentDictionary`.

#### RX-4.3: TOKEN Correlation (Both Platforms)
**Prereqs:** RX-4.1, RX-3.3 | **Effort:** 1 day

In normalize(): extract TOKEN from `<ANS TOKEN="...">`, look up in active pre-auth map, set `correlationId` and `odooOrderId`. TOKEN=0 → Normal Order (skip lookup). Remove TOKEN from map after correlation.

#### RX-5.1: Push Listener — Unsolicited Mode (Both Platforms)
**Prereqs:** RX-3.3, RX-3.5 | **Effort:** 2.5 days

HTTP listener on configurable LAN-accessible port. Accept Radix unsolicited POSTs (RESP_CODE=30). Validate USN-Code header + signature. Parse transaction, feed into ingestion pipeline. Return XML ACK (CMD_CODE=201, signed). Set UNSOLICITED mode (CMD_CODE=20, MODE=2) on startup.

---

### Track C: Petronite Full Adapter

#### PN-2.1: Webhook Normalization
**Prereqs:** PN-1.3, PN-1.4 | **Effort:** 2 days

`NormalizeAsync()`: parse `PetroniteWebhookPayload` JSON → `CanonicalTransaction`:
- `fccTransactionId` = `"{data.Id}"`
- `volumeMicrolitres` = `(long)(data.Volume * 1_000_000m)`
- `amountMinorUnits` = `(long)(data.Amount * 10^currencyDecimalPlaces)`
- `completedAt` = parse `data.Day` + `data.Hour` with timezone → UTC
- `nozzleNumber` = reverse-map `data.Nozzle` (Petronite nozzle ID → canonical) via resolver
- `fiscalReceiptNumber` = `data.ReceiptCode`
- `PaymentMethod == "PUMA_ORDER"` detection preserved in raw payload

**Reference:** `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §4

#### PN-2.2: FetchTransactions No-Op + GetPumpStatus Synthesized
**Prereqs:** PN-1.4 | **Effort:** 0.5 day

`FetchTransactionsAsync` → empty batch (push-only, no pull API). `GetPumpStatusAsync` → synthesize from cached nozzle assignments + pending orders (Authorized if PUMA pending, else Idle). Source = `EdgeSynthesized`.

#### PN-3.1: Two-Step Pre-Auth — Create Order (Step 1)
**Prereqs:** PN-1.2, PN-1.4 | **Effort:** 2 days

`SendPreAuthAsync`:
1. Resolve nozzle ID via `PetroniteNozzleResolver.ResolvePetroniteNozzleId()`
2. Build `PetroniteCreateOrderRequest`: customerName, customerId, type="PUMA_ORDER", nozzleId, authorizeType="Amount", dose (minor → major conversion)
3. POST to `/direct-authorize-requests/create` with bearer token
4. Map response: HTTP 200 + status=STARTED → Accepted=true, FccCorrelationId=orderId
5. Track in `ConcurrentDictionary<long, ActivePreAuth>`

**Reference:** `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.4, §5, §9.1

#### PN-3.2: Authorize Pump (Step 2)
**Prereqs:** PN-3.1 | **Effort:** 1.5 days

Petronite-specific `AuthorizePumpAsync(fccCorrelationId, truckNumber)` (not on IFccAdapter). Look up order in active map. POST to `/direct-authorize-requests/authorize` with requestId. Handle 400 (nozzle not lifted) as recoverable. Edge Agent PreAuth handler calls this via type-check.

#### PN-3.3: Cancel Pre-Auth
**Prereqs:** PN-3.1 | **Effort:** 0.5 day

POST to `/{id}/cancel`. HTTP 200 + status=CANCELLED → true, remove from map. HTTP 404 → true (idempotent). HTTP 400 → false (already dispensing). Network error → false.

#### PN-3.4: Startup Reconciliation — Pending Order Recovery
**Prereqs:** PN-3.1, PN-3.3 | **Effort:** 1 day

On adapter init: `GET /direct-authorize-requests/pending`. For each STARTED order: if older than 30 minutes → auto-cancel; if recent → re-adopt into active map. Non-fatal on API failure. Structured log summary.

#### PN-3.5: Pre-Auth ↔ Dispense Correlation
**Prereqs:** PN-3.1, PN-2.1 | **Effort:** 0.5 day

In NormalizeAsync: check `PaymentMethod == "PUMA_ORDER"` → look up in active map → set `FccCorrelationId` and `OdooOrderId`. Remove from map (pre-auth completed). Unlinked PUMA_ORDER gets transaction ID as FccCorrelationId.

#### PN-4.1: Edge Webhook Listener
**Prereqs:** PN-2.1 | **Effort:** 2 days

HTTP endpoint `POST /api/webhook/petronite` on configurable LAN-accessible port. Validate `X-Webhook-Secret` header. Parse JSON, validate required fields. Feed into ingestion pipeline. **Always return HTTP 200** (even on internal errors — Petronite retry behavior undocumented).

#### PN-4.2: Ingestion Mode Validation
**Prereqs:** PN-2.2 | **Effort:** 0.5 day

Reject PULL mode for Petronite (no pull API). Warn on HYBRID (functions as PUSH-only). Warn on CLOUD_DIRECT (bot is typically LAN-only). Adapter metadata reports `SupportedIngestionMethods = [PUSH]`.

---

## Phase 3 — Cloud Adapters, DB Migration, Portal (Sprints 5-7)

Consolidated cloud/portal work after edge adapters are functional.

### UNI-3.1: Radix Cloud Adapter
**Prereqs:** Track B Phase 2 complete | **Effort:** 2.5 days

Create `FccMiddleware.Adapter.Radix/` project:
- `ValidatePayload` — dual path: canonical JSON from edge uploads (validate required fields) + raw XML from CLOUD_DIRECT (parse XML, check for `<TRN>`, validate signature)
- `NormalizeTransaction` — Path A (edge-uploaded): passthrough with enrichment. Path B (direct XML): same field mapping as edge RX-3.3
- `GetAdapterMetadata` — PUSH+PULL, pre-auth yes, pump status no

**Create:** `src/cloud/FccMiddleware.Adapter.Radix/`

### UNI-3.2: Petronite Cloud Adapter
**Prereqs:** Track C Phase 2 complete | **Effort:** 2 days

Create `FccMiddleware.Adapter.Petronite/` project:
- `ValidatePayload` — check event type, required fields, parse JSON
- `NormalizeTransaction` — same field mapping as edge PN-2.1
- `GetAdapterMetadata` — PUSH-only, pre-auth yes, pump status no

**Create:** `src/cloud/FccMiddleware.Adapter.Petronite/`

### UNI-3.3: Cloud Push Ingress Endpoints
**Prereqs:** UNI-3.1, UNI-3.2 | **Effort:** 2.5 days

**Radix:** Make existing ingest endpoint content-type-aware: `Content-Type: Application/xml` → Radix XML flow. USN-Code header → site lookup → signature validation → normalize → dedup/store/outbox → **XML ACK response** (CMD_CODE=201, signed).

**Petronite:** Add `POST /api/v1/ingest/petronite/webhook`. X-Site-Code or X-Webhook-Secret → site lookup → validate → normalize → dedup/store/outbox → HTTP 200 `{"status":"ok"}`.

### UNI-3.4: Unified DB Migration (All Vendor Config Fields)
**Prereqs:** UNI-3.1, UNI-3.2 | **Effort:** 1 day

Single migration adding nullable columns:
- DOMS: `jpl_port` INT, `dpp_ports` JSONB, `fc_access_code` NVARCHAR (encrypted), `doms_country_code`, `pos_version_id`, `heartbeat_interval_seconds`, `reconnect_backoff_max_seconds`, `configured_pumps` JSONB
- Petronite: `client_id`, `client_secret` (encrypted), `webhook_secret`, `oauth_token_endpoint`
- Radix fields already exist — verify in migration
- `fc_access_code` and `client_secret` follow same encryption-at-rest pattern as `api_key`

### UNI-3.5: Portal Vendor-Specific Config UI
**Prereqs:** UNI-3.4 | **Effort:** 2 days

Extend `FccConfigFormComponent` with conditional sections based on `fccVendor`:
- **DOMS**: Protocol selector (REST/TCP), JPL Port (default 8888), Access Code (masked), Country Code, POS Version ID, Heartbeat Interval (15-60s), Configured Pumps (multi-int)
- **Radix**: Shared Secret (masked), USN Code (1-999999), Auth Port (1-65535), Pump Address Map (table: canonical pump → PUMP_ADDR, FP)
- **Petronite**: Client ID, Client Secret (masked), Webhook Secret (masked), OAuth Token Endpoint
- Validation rules per vendor. Sections hidden when vendor doesn't match.

**File:** `src/portal/src/app/features/site-config/fcc-config-form.component.ts`

### UNI-3.6: Cloud DOMS Adapter Update
**Prereqs:** UNI-0.1 | **Effort:** 1 day

Update existing `DomsCloudAdapter` to handle edge-uploaded canonical JSON from TCP adapter (pre-normalized, mostly passthrough with `legalEntityId` enrichment). Register DOMS in cloud factory.

---

## Phase 4 — VirtualLab Simulators (Sprints 6-7)

**All three run in PARALLEL.**

### VL-4.1: DOMS TCP/JPL Simulator
**Effort:** 3 days

`IHostedService` TCP server:
- FcLogon (validate credentials, mark session authenticated)
- FpStatus (configurable pump states via simulator API)
- FpSupTrans (lock/read/clear from in-memory supervised buffer)
- AuthorizeFp/DeauthorizeFp (pump state transitions)
- Heartbeat (STX/ETX echo)
- Unsolicited event pusher
- REST API: start/stop, set-pump-state, add-transaction, push-event

**Create:** `VirtualLab/src/VirtualLab.Infrastructure/DomsJpl/`

### VL-4.2: Radix FDC HTTP/XML Simulator
**Effort:** 2.5 days

`IHostedService` dual-port HTTP:
- Port P+1: CMD_CODE=10 (FIFO buffer), CMD_CODE=201 (ACK removes from buffer), CMD_CODE=20 (mode change), CMD_CODE=55 (product list)
- Port P: AUTH_DATA (pre-auth), AUTH=FALSE (cancel)
- SHA-1 signature validation and generation
- UNSOLICITED mode: auto-POST to callback URL
- REST API: start/stop, add-transaction, set-error-mode

**Create:** `VirtualLab/src/VirtualLab.Infrastructure/RadixSimulator/`

### VL-4.3: Petronite Bot REST/JSON Simulator
**Effort:** 2.5 days

`IHostedService` REST server:
- `POST /oauth/token` — mock OAuth2 with configurable expiry
- `GET /nozzles/assigned` — pre-configured pump/nozzle assignments
- `POST /direct-authorize-requests/create` — create pending order
- `POST /direct-authorize-requests/authorize` — configurable nozzle-lift delay
- `POST /{id}/cancel`, `GET /pending`, `GET /{id}/details`
- Auto-webhook POST to callback URL after authorize (configurable delay)
- Error injection: OAuth 401, bad nozzle, nozzle-not-lifted

**Create:** `VirtualLab/src/VirtualLab.Infrastructure/PetroniteSimulator/`

---

## Phase 5 — Integration Testing & Hardening (Sprints 7-8)

### TEST-5.1: DOMS End-to-End Tests
**Prereqs:** DOMS-2.2, DOMS-2.4, VL-4.1 | **Effort:** 2.5 days

6 test scenarios against VirtualLab TCP simulator:
1. JPL Handshake: connect → FcLogon → verify → disconnect
2. Pre-Auth Flow: authorize pump → state transitions → verify result
3. Transaction Retrieval: fetch → canonical conversion → acknowledge (clear)
4. Unsolicited Events: status change → event listener receives
5. Reconnection: kill TCP → auto-reconnect → re-logon → no data loss
6. Multi-Pump Status: 4+ pumps → independent state machines

Cloud ingestion test: edge-uploaded canonical → validate → store → SYNCED_TO_ODOO

### TEST-5.2: Radix End-to-End Tests
**Prereqs:** RX-5.1, UNI-3.1, VL-4.2 | **Effort:** 2.5 days

7 test scenarios:
1. Heartbeat (CMD_CODE=55) → true
2. FIFO drain: 5 transactions → all fetched and ACKed → next fetch empty
3. Normalization: all canonical fields correct
4. Pre-auth + TOKEN correlation: authorize → fetch dispense → linked
5. Push mode: unsolicited POST → received and ACKed
6. Mode switching: ON_DEMAND ↔ UNSOLICITED
7. **Cross-platform**: same XML input → same canonical output on Kotlin and .NET

Cloud XML ingress: POST raw XML → stored → XML ACK returned

### TEST-5.3: Petronite End-to-End Tests
**Prereqs:** PN-4.1, UNI-3.2, VL-4.3 | **Effort:** 2 days

7 test scenarios:
1. OAuth flow: acquire → cache → refresh → bot restart (401) → re-auth
2. Nozzle discovery: initialize → mapping populated
3. Two-step pre-auth: create → authorize → webhook → correlated
4. Webhook: receive → normalize → correct canonical fields
5. Cancellation: create → cancel → verify cancelled
6. Startup reconciliation: stale orders auto-cancelled
7. Error handling: bad credentials, unreachable bot, nozzle not found

Cloud webhook: POST → stored → HTTP 200

### TEST-5.4: Cross-Vendor Regression + Hardening
**Prereqs:** TEST-5.1, TEST-5.2, TEST-5.3 | **Effort:** 1.5 days

- DOMS REST adapter still works alongside new TCP adapter
- Factory protocol selection (TCP vs REST for DOMS)
- Portal config save/load for all vendors
- Cloud factory resolves all 4 vendors
- DB migration applies and rolls back cleanly
- No regressions in existing DOMS REST adapter tests

### TEST-5.5: Documentation & Open Questions
**Prereqs:** All | **Effort:** 1 day

- Protocol comparison table (final)
- Per-vendor configuration reference with examples
- Troubleshooting guide per vendor
- Operational runbook for adding new sites
- Update WIP plan documents with resolution status for all open questions

---

## Summary

| Phase | Tasks | Effort (dev-days) | Sprints | Parallelism |
|-------|-------|--------------------|---------|-------------|
| **Phase 0**: Shared Foundation | 5 | 4.5 | Sprint 1 | Mostly sequential |
| **Phase 1**: Protocol Infrastructure | 16 | 22 | Sprints 1-3 | 3 parallel vendor tracks |
| **Phase 2**: Full Adapters | 21 | 30 | Sprints 3-5 | 3 parallel vendor tracks |
| **Phase 3**: Cloud + Portal | 6 | 11 | Sprints 5-7 | Partially parallel |
| **Phase 4**: VirtualLab Simulators | 3 | 8 | Sprints 6-7 | Fully parallel |
| **Phase 5**: Testing & Hardening | 5 | 9.5 | Sprints 7-8 | Partially parallel |
| **TOTAL** | **58** | **~86 dev-days** | **8 sprints (16 weeks)** | |

**Critical path:** DOMS track is longest due to TCP complexity. With 3 parallel streams, **~5-6 sprints** wall-clock.

**Sprint allocation at max parallelism (3 agents/developers):**
| Sprint | Work |
|--------|------|
| Sprint 1 | Phase 0 foundation + begin all 3 Phase 1 tracks |
| Sprint 2 | Continue Phase 1 tracks |
| Sprint 3 | Complete Phase 1, begin Phase 2 tracks |
| Sprint 4 | Continue Phase 2 tracks |
| Sprint 5 | Complete Phase 2, begin Phase 3 (cloud/portal) |
| Sprint 6 | Phase 3 continues, Phase 4 simulators begin |
| Sprint 7 | Complete Phase 3+4, begin Phase 5 testing |
| Sprint 8 | Complete Phase 5, hardening |

---

## Dependency Graph

```
Phase 0 (Sprint 1):
  UNI-0.1 ──┬──> UNI-0.3 (factories)
             │
  UNI-0.2 ──┼──> UNI-0.4 (CadenceController)
             │
  UNI-0.5 ──┘   (parallel, no deps)

Phase 1 (Sprints 1-3) — 3 PARALLEL tracks:

  DOMS:  DOMS-1.1 ──> DOMS-1.2 ──> DOMS-1.3 (TCP client)
                    └─> DOMS-1.4 (mapper)
         DOMS-1.5 ──> DOMS-1.6 (.NET mirror)

  RADIX: RX-1.4 + RX-1.5 ──> RX-1.6 (heartbeat)
         RX-2.1 ──> RX-2.4 ──> RX-2.5 (.NET track)

  PETRO: PN-1.1 ──> PN-1.2 + PN-1.3 ──> PN-1.4 ──> PN-1.5

Phase 2 (Sprints 3-5) — 3 PARALLEL tracks:

  DOMS:  DOMS-2.1 ──> DOMS-2.2 (Kotlin full adapter)
         DOMS-2.3 ──> DOMS-2.4 ──> DOMS-2.5 (.NET + factory)

  RADIX: RX-3.1 + RX-3.3 ──> RX-3.5 (mode mgmt)
         RX-4.1 ──> RX-4.3 ──> RX-5.1 (push)
         RX-3.2 + RX-3.4 ──> RX-4.2 (.NET mirror)

  PETRO: PN-2.1 + PN-3.1 ──> PN-3.2, PN-3.3, PN-3.4, PN-3.5
         PN-4.1, PN-4.2 (independent)

Phase 3 (Sprints 5-7):
  UNI-3.1 + UNI-3.2 ──> UNI-3.3 (cloud endpoints)
                     ──> UNI-3.4 (DB migration) ──> UNI-3.5 (portal)
  UNI-3.6 (independent — DOMS cloud update)

Phase 4 (Sprints 6-7):
  VL-4.1 | VL-4.2 | VL-4.3 (all parallel)

Phase 5 (Sprints 7-8):
  TEST-5.1 | TEST-5.2 | TEST-5.3 (parallel)
       └─────> TEST-5.4 ──> TEST-5.5
```

---

## Risk Register

| Risk | Impact | Mitigation |
|------|--------|------------|
| Real DOMS protocol differs from DOMSRealImplementation reference | High — wrong messages | Validate against actual DOMS hardware ASAP; reference is production-tested |
| SHA-1 signature whitespace mismatch (Radix) | High — all communication fails | Test against real FDC early; golden test vectors; cross-platform consistency |
| Webhook delivery failure = lost Petronite transactions | High — data loss | RELAY mode default; confirm retry behavior (PQ-2); request pull API (PQ-6) |
| Currency/amount conversion factors wrong (all vendors) | High — financial corruption | Confirm with real hardware; add sanity-check `volume × price ≈ amount` |
| TCP persistent connection + Android battery/lifecycle | Medium — service killed | ForegroundService (already in place); test on Urovo i9100 specifically |
| Normal Orders not sent via Petronite webhook (PQ-7) | High — most transactions invisible | Confirm before implementation; may need alternative strategy |
| Two-step pre-auth timing — nozzle not lifted (Petronite) | Medium — stuck orders | Retry logic; auto-cancel stale orders (PN-3.4) |
| Cross-platform normalization inconsistency (Kotlin vs .NET) | Medium — data divergence | Shared test fixtures; cross-platform E2E tests (TEST-5.2, TEST-5.4) |
| Radix FDC firmware variations across sites | Medium — XML differences | Version check on connection; alert if firmware < 3.49 |

---

## Prerequisites Checklist

Before implementation begins:

- [ ] **DOMS**: Access to real DOMS hardware or DOMSRealImplementation validated
- [ ] **Radix RQ-1**: Currency decimal handling confirmed (Critical for RX-3.3)
- [ ] **Radix RQ-2**: Pump addressing model decided (Critical for RX-3.3, RX-4.1)
- [ ] **Petronite PQ-1**: Currency/amount format confirmed (Critical for PN-2.1)
- [ ] **Petronite PQ-7**: Normal Order webhook behavior confirmed (Critical)
- [ ] **Petronite PQ-2**: Webhook retry behavior confirmed
- [ ] Access to real Radix FDC or test instance for signature validation
- [ ] Petronite test credentials (Client ID + Secret)
- [ ] Cloud backend Phase 1 complete (CB-1.1, CB-1.2) for Phase 3 tasks

---

## Changelog

### 2026-03-12 — v1.0: Initial Unified Plan

- Consolidated from `dev-plan-doms-adapter.md` (31 tasks), `dev-plan-radix-adapter.md` (30 tasks), `dev-plan-petronite-adapter.md` (19 tasks)
- Reduced 80 tasks → 58 tasks via deduplication of shared infrastructure
- 6 phases, 8 sprints, ~86 dev-days total
- Organized for maximum parallelism: 3 concurrent vendor tracks in Phases 1-2
- Key eliminations: acknowledgeTransactions (already exists), PreAuthCommand fields (already exist), per-vendor config tasks (consolidated)
- Key merges: DB migration (one for all vendors), Portal config (one component), factory registrations (one task per layer), agent prompt (one doc)
