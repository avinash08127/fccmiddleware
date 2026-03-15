# Wave 2 Radix Adapter — Implementation Plan

> Sequenced implementation plan for all 12 items from `Wave2RadixAdapter.md`.
> Total estimated effort: **~10 days**

---

## Phase 1: Security Hardening (Day 1)

No dependencies. Must be done first — security issues gate everything else.

### Step 1.1 — Verify & Fix Edge Agent Constant-Time Signature Comparison

**Priority:** P0 | **Effort:** 1h
**Files:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixSignatureHelper.kt`

**Tasks:**
- [ ] Check if `validateSignature()` uses `String.equals()` or `MessageDigest.isEqual()`
- [ ] If using `String.equals()`, replace with `MessageDigest.isEqual(a.toByteArray(), b.toByteArray())` for constant-time comparison
- [ ] Add unit test in `RadixSignatureHelperTests.kt` that validates both matching and non-matching signatures (functional correctness, not timing — timing is a property of the API)

### Step 1.2 — Verify & Fix Desktop Agent XXE Protection

**Priority:** P0 | **Effort:** 1h
**Files:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixXmlParser.cs`

**Tasks:**
- [ ] Check `XmlReader`/`XmlDocument` settings for DTD processing and external entity resolution
- [ ] If not already set, add: `DtdProcessing = DtdProcessing.Prohibit`, `XmlResolver = null`
- [ ] Add unit test with a malicious XML payload containing `<!DOCTYPE>` entity to confirm it's rejected

---

## Phase 2: Desktop Agent Test Parity (Days 1–2)

No code dependencies on Phase 1. Can start in parallel. Establishes the test foundation before adding new features.

### Step 2.1 — Add Desktop Unit Tests for RadixXmlBuilder

**Priority:** P1 | **Effort:** 0.5d
**Files (new):**
- `src/desktop-edge-agent/tests/FccDesktopAgent.Core.Tests/Adapter/Radix/RadixXmlBuilderTests.cs`

**Test cases:**
- [ ] `BuildTransactionRequest` — verify CMD_CODE=10, token included, signature correct
- [ ] `BuildTransactionAck` — verify CMD_CODE=201
- [ ] `BuildModeChangeRequest` — verify CMD_CODE=20 with modes 0, 1, 2
- [ ] `BuildProductReadRequest` — verify CMD_CODE=55
- [ ] `BuildPreAuthRequest` — verify AUTH_DATA envelope with all optional customer fields
- [ ] `BuildPreAuthCancelRequest` — verify AUTH=FALSE
- [ ] `BuildHttpHeaders` — verify USN-Code, Operation, Content-Type for each operation type
- [ ] Signature whitespace sensitivity — verify that reformatting XML changes signature

### Step 2.2 — Add Desktop Unit Tests for RadixXmlParser

**Priority:** P1 | **Effort:** 0.5d
**Files (new):**
- `src/desktop-edge-agent/tests/FccDesktopAgent.Core.Tests/Adapter/Radix/RadixXmlParserTests.cs`
- `src/desktop-edge-agent/tests/FccDesktopAgent.Core.Tests/Adapter/Radix/Fixtures/` (XML test fixtures)

**Test cases:**
- [ ] Parse transaction response (RESP_CODE=201) — all TRN, RFID_CARD, DISCOUNT attributes
- [ ] Parse transaction response (RESP_CODE=205) — no transaction available
- [ ] Parse unsolicited transaction (RESP_CODE=30)
- [ ] Parse auth response (ACKCODE=0) — success
- [ ] Parse auth response (ACKCODE=251) — signature error
- [ ] Parse auth response (ACKCODE=258) — pump not ready
- [ ] Parse auth response (ACKCODE=260) — DSB offline
- [ ] Parse product response — multiple products
- [ ] Validate response signature — correct and incorrect
- [ ] Malformed XML — graceful error, no crash
- [ ] Empty/missing optional fields — defaults to empty string, no exceptions

### Step 2.3 — Add Desktop Unit Tests for RadixPushListener

**Priority:** P1 | **Effort:** 0.5d (can be light — test the channel logic, not HTTP)
**Files (new):**
- `src/desktop-edge-agent/tests/FccDesktopAgent.Core.Tests/Adapter/Radix/RadixPushListenerTests.cs`

**Test cases:**
- [ ] Valid push with correct USN-Code and signature — enqueued
- [ ] Invalid USN-Code — rejected
- [ ] Invalid signature — rejected
- [ ] Queue at capacity (10,000) — returns 503, dropped count increments
- [ ] Drain queue — returns all queued items, queue empty after drain
- [ ] Stop listener — clears queue

---

## Phase 3: Pump Status Parity (Day 2)

Depends on: Nothing. Small, self-contained.

### Step 3.1 — Backport Synthesized Pump Status to Desktop Agent

**Priority:** P1 | **Effort:** 0.5d
**Files:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixAdapter.cs`

**Tasks:**
- [ ] Change `PumpStatusCapability` from `NotSupported` to `Synthesized`
- [ ] Implement `GetPumpStatusAsync()` to return pump statuses derived from:
  - Configured pumps (from `fccPumpAddressMap`) → default state IDLE
  - Active pre-auth entries → state AUTHORIZED
- [ ] Match Edge agent behavior: iterate configured pumps, check if any have an active (non-expired) pre-auth entry
- [ ] Add unit test: no pre-auths → all IDLE; active pre-auth on pump 2 → pump 2 AUTHORIZED, others IDLE

### Step 3.2 — Add Error 207 Explicit Handling

**Priority:** P1 | **Effort:** 0.5h per agent
**Files:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixAdapter.cs`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixAdapter.kt`

**Tasks:**
- [ ] Add explicit check for `RESP_CODE=207` / `ACKCODE=207` with log message: `"FDC rejected request: product data error (207). Verify product ID and parameters."`
- [ ] Currently caught by generic "non-201 is error" fallback — make it specific

---

## Phase 4: Price Management — CMD 55/66 (Days 3–4)

Depends on: Phase 2 (tests exist to validate XML builder/parser changes).

### Step 4.1 — Define IFccPriceManagement Contract for Radix

**Priority:** P1 | **Effort:** 0.5h
**Files:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccPriceManagement.cs` (already exists)

**Tasks:**
- [ ] Review existing `IFccPriceManagement` interface: `GetCurrentPricesAsync()` and `UpdatePricesAsync()`
- [ ] Confirm `PriceUpdateCommand` can carry: product ID, name, price, active flag, enable flag
- [ ] If not, extend `PriceUpdateCommand` to include `ProductActive` and `ProductEnable` fields (Radix-specific)
- [ ] Review equivalent Kotlin interface in Edge agent

### Step 4.2 — Implement Price Read (CMD 55) in Both Adapters

**Priority:** P1 | **Effort:** 0.5d
**Files:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixAdapter.cs`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixAdapter.kt`

**Tasks:**
- [ ] Add `IFccPriceManagement` to RadixAdapter class declaration
- [ ] Implement `GetCurrentPricesAsync()`:
  - Build CMD_CODE=55 request via existing `BuildProductReadRequest()` (already used for heartbeat)
  - POST to port P+1 with `Operation: 2`
  - Parse response into `PriceSetSnapshot` with product list
- [ ] Note: only active products are returned by FDC (spec constraint)
- [ ] Add unit tests for price read response parsing

### Step 4.3 — Implement Price Write (CMD 66) in Both Adapters

**Priority:** P1 | **Effort:** 1d
**Files:**
- Desktop: `RadixAdapter.cs`, `RadixXmlBuilder.cs`, `RadixXmlParser.cs`
- Edge: `RadixAdapter.kt`, `RadixXmlBuilder.kt`, `RadixXmlParser.kt`

**Tasks:**
- [ ] Add `BuildProductWriteRequest()` to XmlBuilder:
  ```xml
  <HOST_REQ>
    <REQ>
      <CMD_CODE>66</CMD_CODE>
      <CMD_NAME>PROD and PRICE CHANGE</CMD_NAME>
      <TOKEN>{token}</TOKEN>
      <PRODUCT_ID>{id}</PRODUCT_ID>
      <PRODUCT_NAME>{name}</PRODUCT_NAME>
      <PRODUCT_PRICE>{price}</PRODUCT_PRICE>
      <PRODUCT_ACTIVE>{YES|NO}</PRODUCT_ACTIVE>
      <PRODUCT_ENABLE>{YES|NO}</PRODUCT_ENABLE>
    </REQ>
    <SIGNATURE>{sha1}</SIGNATURE>
  </HOST_REQ>
  ```
- [ ] Header: `Operation: 2` (not 1)
- [ ] Implement `UpdatePricesAsync()`:
  - Spec constraint: **one product per request** — loop over products, send sequentially
  - Check RESP_CODE=201 for each; abort on first error
  - Handle 207 (product data error) explicitly
- [ ] Add unit tests: successful update, signature error, product data error
- [ ] Add VirtualLab simulator support for CMD 66 (Step 4.4)

### Step 4.4 — Add CMD 66 to VirtualLab Radix Simulator

**Priority:** P1 | **Effort:** 0.5d
**Files:**
- `VirtualLab/src/VirtualLab.Infrastructure/RadixSimulator/RadixSimulatorService.cs`
- `VirtualLab/src/VirtualLab.Infrastructure/RadixSimulator/RadixSimulatorState.cs`
- `VirtualLab/src/VirtualLab.Infrastructure/Radix/RadixSimulatorEndpoints.cs`

**Tasks:**
- [ ] Add CMD_CODE=66 handler to transaction port handler
- [ ] Parse PRODUCT_ID, PRODUCT_NAME, PRODUCT_PRICE, PRODUCT_ACTIVE, PRODUCT_ENABLE
- [ ] Update in-memory product catalog in RadixSimulatorState
- [ ] Return RESP_CODE=201 on success, 207 on invalid product ID
- [ ] Add error injection for 207 responses
- [ ] Add E2E test: write price → read price → verify changed

---

## Phase 5: Day Close — CMD 77 (Day 5)

Depends on: Phase 4 (XmlBuilder pattern established for new commands).

### Step 5.1 — Define Day Close Interface

**Priority:** P2 | **Effort:** 0.5h
**Files (new):**
- Desktop: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccDayClose.cs`
- Edge: equivalent Kotlin interface

**Tasks:**
- [ ] Define interface:
  ```csharp
  public interface IFccDayClose
  {
      Task<DayCloseResult> TriggerDayCloseAsync(DayCloseCommand command, CancellationToken ct);
  }
  ```
- [ ] `DayCloseCommand`: `bool CloseImmediate`, `TimeOnly? ScheduledTime`
- [ ] `DayCloseResult`: `bool Success`, `string? ErrorMessage`, `int? ErrorCode`

### Step 5.2 — Implement Day Close in Both Adapters

**Priority:** P2 | **Effort:** 0.5d
**Files:**
- Desktop: `RadixAdapter.cs`, `RadixXmlBuilder.cs`
- Edge: `RadixAdapter.kt`, `RadixXmlBuilder.kt`

**Tasks:**
- [ ] Add `IFccDayClose` to RadixAdapter class
- [ ] Add `BuildDayCloseRequest()` to XmlBuilder:
  ```xml
  <HOST_REQ>
    <REQ>
      <CMD_CODE>77</CMD_CODE>
      <CMD_NAME>DAY CLOSE</CMD_NAME>
      <CLOSE_IMMEDIATE>{YES|NO}</CLOSE_IMMEDIATE>
      <CLOSE_TIME>{HH:MM:SS}</CLOSE_TIME>
      <TOKEN>{token}</TOKEN>
    </REQ>
    <SIGNATURE>{sha1}</SIGNATURE>
  </HOST_REQ>
  ```
- [ ] Header: `Operation: 3`
- [ ] If `CloseImmediate=true`, set `CLOSE_IMMEDIATE=YES` (CLOSE_TIME ignored by FDC)
- [ ] Handle RESP_CODE=201 success and all error codes
- [ ] Add unit tests: immediate close, scheduled close, error responses
- [ ] Add CMD 77 to VirtualLab simulator

---

## Phase 6: ATG Tank Monitoring — CMD 30/35 (Days 6–7)

Depends on: Nothing (new interface). Can overlap with Phase 5.

### Step 6.1 — Define Tank Monitor Interface

**Priority:** P2 | **Effort:** 0.5h
**Files (new):**
- Desktop: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccTankMonitor.cs`
- Edge: equivalent Kotlin interface

**Tasks:**
- [ ] Define interface:
  ```csharp
  public interface IFccTankMonitor
  {
      Task<TankLevelSnapshot> GetTankLevelsAsync(CancellationToken ct);
      Task<DeliveryResult> GetLatestDeliveryAsync(CancellationToken ct);
  }
  ```
- [ ] `TankLevelSnapshot`: `DateTimeOffset ObservedAtUtc`, `IReadOnlyList<TankReading> Tanks`
- [ ] `TankReading`: `int Id`, `int ProductNumber`, `decimal VolumeLitres`, `decimal TcVolumeLitres`, `decimal UllageLitres`, `decimal HeightCm`, `decimal WaterCm`, `decimal TemperatureC`, `decimal WaterVolumeLitres`, `string AlarmMask`, `int AlarmCount`
- [ ] `DeliveryResult`: `bool Available`, `DeliveryData? Delivery`

### Step 6.2 — Implement ATG Tank Levels (CMD 30)

**Priority:** P2 | **Effort:** 1d
**Files:**
- Desktop: `RadixAdapter.cs`, `RadixXmlBuilder.cs`, `RadixXmlParser.cs`
- Edge: `RadixAdapter.kt`, `RadixXmlBuilder.kt`, `RadixXmlParser.kt`

**Tasks:**
- [ ] Add `BuildTankDataRequest()` to XmlBuilder (CMD_CODE=30, CMD_NAME=ATG_DATA_REQ)
- [ ] Header: `Operation: 4`
- [ ] Add `ParseTankDataResponse()` to XmlParser:
  - Parse `<VR_INV_AL_INFO DATE="" TIME="">` for observation timestamp
  - Parse `<TANK_DATA>` containing multiple `<TANK>` elements
  - Extract all attributes: ID, PROD_NUM, VOL, TC_VOL, ULLAGE, HEIGHT, WATER, TEMP, WATER_VOL, AL_MASK, AL_NUM
- [ ] Implement `GetTankLevelsAsync()` in RadixAdapter
- [ ] Add unit tests with fixture XML
- [ ] Add DTO: `RadixTankData` with all tank attributes

### Step 6.3 — Implement ATG Deliveries (CMD 35)

**Priority:** P2 | **Effort:** 0.5d
**Files:** Same as Step 6.2

**Tasks:**
- [ ] Add `BuildDeliveryRequest()` to XmlBuilder (CMD_CODE=35, CMD_NAME=ATG_DEL_REQ)
- [ ] Header: `Operation: 4`
- [ ] Add `ParseDeliveryResponse()` to XmlParser:
  - Handle RESP_CODE=201 with `<DELIVERY>` element (attributes TBD — spec says "whatever parameters available")
  - Handle RESP_CODE=205 "NO DEL AVAILABLE"
- [ ] Implement `GetLatestDeliveryAsync()`:
  - Send request, parse response
  - If delivery available, ACK with CMD_CODE=201 (same as transaction ACK)
  - If no delivery, return `DeliveryResult { Available = false }`
- [ ] Add unit tests

### Step 6.4 — Add ATG to VirtualLab Simulator

**Priority:** P2 | **Effort:** 1d
**Files:**
- `VirtualLab/src/VirtualLab.Infrastructure/RadixSimulator/RadixSimulatorService.cs`
- `VirtualLab/src/VirtualLab.Infrastructure/RadixSimulator/RadixSimulatorState.cs`

**Tasks:**
- [ ] Add tank state to RadixSimulatorState: list of tanks with configurable levels
- [ ] Add delivery buffer to RadixSimulatorState
- [ ] Handle CMD_CODE=30 on transaction port: return tank snapshot
- [ ] Handle CMD_CODE=35 on transaction port: return oldest delivery, await ACK
- [ ] Add inject-tank-level and inject-delivery management endpoints
- [ ] Add E2E tests: read tank levels, poll delivery, no delivery available

---

## Phase 7: CSR Data — CMD 40 (Day 8)

Depends on: Nothing. Lowest priority, simplest to implement.

### Step 7.1 — Implement CSR Data in Both Adapters

**Priority:** P3 | **Effort:** 1d
**Files:**
- Desktop: `RadixAdapter.cs`, `RadixXmlBuilder.cs`, `RadixXmlParser.cs`
- Edge: `RadixAdapter.kt`, `RadixXmlBuilder.kt`, `RadixXmlParser.kt`

**Tasks:**
- [ ] Define interface or add to existing:
  ```csharp
  public interface IFccCsrProvider
  {
      Task<CsrResult> GetCurrentStatusReportAsync(CancellationToken ct);
  }
  ```
- [ ] `CsrResult`: `bool Available`, `string? RawAsciiData`, `DateTimeOffset? ObservedAtUtc`
- [ ] Add `BuildCsrRequest()` to XmlBuilder (CMD_CODE=40, CMD_NAME=CSR_DATA_REQ)
- [ ] Header: `Operation: 5`
- [ ] Add `ParseCsrResponse()` to XmlParser:
  - Parse `<CSR>` element content as raw string (binary ASCII)
  - Handle RESP_CODE=201 (data available) and 205 (no new CSR)
  - ACK with CMD_CODE=201 after successful receipt
- [ ] Note: DSB/RDG count is 16 (0–15) for this API, not 12
- [ ] Add unit tests
- [ ] Add CSR to VirtualLab simulator (CMD 40 handler with configurable state)

---

## Phase 8: Hardware Integration Validation (Days 9–10)

Depends on: All previous phases complete.

### Step 8.1 — Verify Push Listener Port Convention with Real FDC

**Priority:** P3 | **Effort:** 0.5d
**Files:** None (testing only)

**Tasks:**
- [ ] Connect to a real Radix FDC on the LAN
- [ ] Set mode to UNSOLICITED (CMD_CODE=20, MODE=2)
- [ ] Verify that FDC pushes to the expected callback URL/port
- [ ] If FDC uses a different port than P+2, update `RadixPushListener` configuration
- [ ] Document findings in `docs/FCCAdapters/Radix/RadixHardwareTestResults.md`

### Step 8.2 — End-to-End Validation of All Operations

**Priority:** P3 | **Effort:** 1.5d

**Test matrix against real FDC hardware:**
- [ ] Mode change: OFF → ON_DEMAND → UNSOLICITED → OFF
- [ ] Transaction poll (ON_DEMAND): fetch, ACK, verify FIFO drain
- [ ] Transaction push (UNSOLICITED): verify push listener receives
- [ ] Pre-auth: authorize, dispense, verify token in transaction
- [ ] Pre-auth cancel: authorize then cancel, verify pump deauthorized
- [ ] Price read (CMD 55): verify product list matches FDC config
- [ ] Price write (CMD 66): change price, read back, verify changed
- [ ] Day close (CMD 77): immediate and scheduled
- [ ] ATG tank levels (CMD 30): verify tank data matches probe readings
- [ ] ATG deliveries (CMD 35): trigger delivery, poll, verify
- [ ] CSR data (CMD 40): read status report
- [ ] Signature error injection: wrong secret → verify 251 response
- [ ] Concurrent operations: pre-auth while fetching transactions

---

## Execution Summary

```
Day 1:  Phase 1 (security) + Phase 2 start (tests)
Day 2:  Phase 2 complete (tests) + Phase 3 (pump status parity)
Day 3:  Phase 4 start (price management — interface + read)
Day 4:  Phase 4 complete (price write + VirtualLab)
Day 5:  Phase 5 (day close)
Day 6:  Phase 6 start (ATG — interface + tank levels)
Day 7:  Phase 6 complete (deliveries + VirtualLab)
Day 8:  Phase 7 (CSR data)
Day 9:  Phase 8 start (hardware integration)
Day 10: Phase 8 complete (full test matrix)
```

### Parallelization Opportunities

- Phase 1 + Phase 2 can run in parallel (different files)
- Phase 3 can overlap with Phase 2 (independent)
- Phase 5 + Phase 6 can overlap if two developers are available
- Desktop and Edge implementations within each phase can be parallelized across developers

### Risk Register

| Risk | Mitigation |
|------|-----------|
| Real FDC firmware behaves differently than spec | Phase 8 hardware validation; keep VirtualLab as reference |
| CMD 66 price write requires FDC configuration we don't have | Test with VirtualLab first; coordinate with site operator for FDC access |
| ATG delivery `<DELIVERY>` attributes are underdocumented ("whatever parameters") | Parse all attributes dynamically as key-value pairs; log unknown attributes |
| Push listener port P+2 assumption may be wrong | Phase 8.1 validates; fallback: make port configurable |
| CSR binary ASCII format is opaque | Store raw data; defer parsing to cloud-side analytics |
