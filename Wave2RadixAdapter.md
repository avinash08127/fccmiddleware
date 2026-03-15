# Wave 2 – Radix FCC Adapter: Implementation Review & Gap Analysis

> Reviewed against: `Spec_FDC_RESTAPI v1.3.1 (27.02.2026)` — extracted to `docs/FCCAdapters/Radix/RadixAPIContract.md`

---

## 1. Implementation Coverage Matrix

| Contract Operation | CMD_CODE | Desktop Agent (C#) | Edge Agent (Kotlin) | Notes |
|--------------------|----------|:-------------------:|:-------------------:|-------|
| **Transaction: Mode Change** | 20 | Implemented | Implemented | Both support ON_DEMAND (1), UNSOLICITED (2), OFF (0) |
| **Transaction: Poll (ON_DEMAND)** | 10 | Implemented | Implemented | FIFO drain loop with ACK |
| **Transaction: ACK** | 201 | Implemented | Implemented | Sent inline after each successful fetch |
| **Transaction: Unsolicited Push** | RESP_CODE=30 | Implemented | Implemented | Push listener on port P+2 |
| **Products: Read** | 55 | Implemented | Implemented | Also used as heartbeat probe |
| **Products: Write/Change** | 66 | **NOT IMPLEMENTED** | **NOT IMPLEMENTED** | Contract defined but no adapter method |
| **Day Close** | 77 | **NOT IMPLEMENTED** | **NOT IMPLEMENTED** | Contract defined but no adapter method |
| **External Authorization (Pre-Auth)** | AUTH_DATA | Implemented | Implemented | Full field support including optional customer fields |
| **External Auth Cancel** | AUTH=FALSE | Implemented | Implemented | Sends AUTH_DATA with AUTH=FALSE |
| **ATG: Tank Levels** | 30 | **NOT IMPLEMENTED** | **NOT IMPLEMENTED** | No interface exists for ATG data |
| **ATG: Deliveries** | 35 | **NOT IMPLEMENTED** | **NOT IMPLEMENTED** | No interface exists for ATG data |
| **CSR Data** | 40 | **NOT IMPLEMENTED** | **NOT IMPLEMENTED** | No interface exists for CSR data |
| **Heartbeat** | 55 (reuse) | Implemented | Implemented | Piggybacks on product read |
| **Pump Status** | N/A | Not Supported | Synthesized | Radix protocol has no pump status endpoint |
| **Pump Control** | N/A | **NOT IMPLEMENTED** | **NOT IMPLEMENTED** | No IFccPumpControl on Radix adapter |
| **Price Management** | 55/66 | **NOT IMPLEMENTED** | **NOT IMPLEMENTED** | No IFccPriceManagement on Radix adapter |
| **Totals** | N/A | **NOT IMPLEMENTED** | **NOT IMPLEMENTED** | No IFccTotalsProvider on Radix adapter |

**Score: 8/16 operations implemented (50%)**

The implemented operations cover the **critical transaction and pre-auth flows**. The unimplemented operations (products write, day close, ATG, CSR) are all host-initiated management commands that are not part of the core data ingestion pipeline.

---

## 2. Contract Correctness Analysis

### 2.1 Signature Calculation — CORRECT

| Aspect | Spec | Desktop (C#) | Edge (Kotlin) | Verdict |
|--------|------|:------------:|:-------------:|---------|
| Transaction signing | `SHA1(<REQ>...</REQ> + secret)` | `SHA1(reqContent + secret)` | `SHA1(reqContent + secret)` | **Correct** |
| Auth signing | `SHA1(<AUTH_DATA>...</AUTH_DATA> + secret)` | `SHA1(authContent + secret)` | `SHA1(authContent + secret)` | **Correct** |
| Include XML tags in hash | Yes (inclusive) | Yes | Yes | **Correct** |
| No space between content and secret | Required | Concatenated directly | Concatenated directly | **Correct** |
| Constant-time comparison | Security best practice | `CryptographicOperations.FixedTimeEquals` | Not confirmed | **Desktop OK; Edge: verify** |

### 2.2 Port Assignment — CORRECT

| Aspect | Spec | Implementation | Verdict |
|--------|------|----------------|---------|
| Auth port | Configured port (P) | `AuthPort` config field | **Correct** |
| Transaction port | P + 1 | `AuthPort + 1` | **Correct** |
| Push listener | N/A (spec doesn't define) | P + 2 (implementation choice) | **Acceptable** — spec only defines FDC→Host push, not the listener port |

### 2.3 HTTP Headers — CORRECT

| Header | Spec Value | Implementation | Verdict |
|--------|-----------|----------------|---------|
| `Content-Type` | `Application/xml` | `Application/xml` | **Correct** |
| `USN-Code` | 1–999999 | From config `UsnCode` | **Correct** |
| `Operation` | 1/2/3/4/5/Authorize | Mapped per command type | **Correct** |
| `Content-Length` | Required | Set by HTTP client automatically | **Correct** |

### 2.4 XML Envelope Structure — CORRECT

| Message Type | Spec Envelope | Implementation | Verdict |
|-------------|---------------|----------------|---------|
| Host → FDC (transaction) | `<HOST_REQ><REQ>...</REQ><SIGNATURE>` | Matches | **Correct** |
| FDC → Host (transaction) | `<FDC_RESP><TABLE>...<SIGNATURE>` | Parsed correctly | **Correct** |
| Host → FDC (auth) | `<FDCMS><AUTH_DATA>...<FDCSIGNATURE>` | Matches | **Correct** |
| FDC → Host (auth) | `<FDCMS><FDCACK>...<FDCSIGNATURE>` | Parsed correctly | **Correct** |
| Host ACK | `<HOST_REQ><REQ><CMD_CODE>201</CMD_CODE>...` | Matches | **Correct** |

### 2.5 Response Code Handling — CORRECT

| Code | Spec Meaning | Handled? | Notes |
|------|-------------|----------|-------|
| 0 | Auth success | Yes | External auth only |
| 201 | OK / Success | Yes | Transaction + products |
| 205 | No data available | Yes | Stops FIFO drain |
| 30 | Unsolicited push | Yes | Push listener handles |
| 206 | Transaction mode error | Yes | Logged, returned as error |
| 251 | Signature error | Yes | Both adapters handle |
| 253 | Token error | Yes | Logged |
| 255 | Bad XML | Yes | Logged |
| 256 | Bad header | Yes | Auth response only |
| 258 | Pump not ready | Yes | Auth response only |
| 260 | DSB offline | Yes | Auth response only |
| 207 | Product data error | **Partial** | Defined in contract but not explicitly handled in adapters |
| 252 | CMD code error | **Partial** | CSR-specific, not implemented |

### 2.6 Token Handling

| Aspect | Spec | Desktop (C#) | Edge (Kotlin) | Verdict |
|--------|------|:------------:|:-------------:|---------|
| Transaction token range | Max 16 digits | Sequential counter | Sequential counter | **Correct** |
| Auth token range | 0–65535 | 1–65535 (wraps) | 1–65535 (wraps) | **Correct** (0 reserved for "no pre-auth") |
| Unique per sequence | Recommended | Interlocked increment | AtomicInteger | **Correct** |
| Token correlation | Match response to request | ConcurrentDictionary by token | ConcurrentHashMap by token | **Correct** |

---

## 3. Critical Findings

### 3.1 FINDING: Products Write (CMD 66) Not Implemented — MEDIUM

The spec defines `CMD_CODE=66` for writing/changing product names, prices, and active/enable flags. Neither adapter implements `IFccPriceManagement`.

**Impact:** Cannot remotely update fuel prices on Radix FCC from the cloud platform.

**Recommendation:** Implement `IFccPriceManagement` on `RadixAdapter` using CMD_CODE 55 (read) and 66 (write). The XML builder already has `OPERATION_PRODUCTS = "2"` defined. This is a natural extension.

**Contract fields required:**
- `PRODUCT_ID`, `PRODUCT_NAME`, `PRODUCT_PRICE`, `PRODUCT_ACTIVE`, `PRODUCT_ENABLE`
- One product per request (spec constraint)

### 3.2 FINDING: Day Close (CMD 77) Not Implemented — LOW

The spec defines `CMD_CODE=77` with `CLOSE_IMMEDIATE` and `CLOSE_TIME` parameters. No adapter interface exists for day close operations.

**Impact:** Cannot trigger remote day close from cloud. Operators must use FDC panel.

**Recommendation:** Defer unless operators explicitly need remote day close. Consider adding to `IFccPumpControl` or a new `IFccDayClose` interface if required.

### 3.3 FINDING: ATG Tank Data (CMD 30/35) Not Implemented — MEDIUM

The spec provides tank-level monitoring (volume, temperature, water level, ullage, alarms) and delivery tracking. No adapter interface exists.

**Impact:** Cannot monitor tank levels or detect deliveries for Radix sites. This data is valuable for:
- Low-fuel alerts
- Delivery reconciliation
- Leak detection (volume vs. sales discrepancy)

**Recommendation:** Define `IFccTankMonitor` interface and implement for Radix. The data model is well-specified with `TANK_DATA` containing ID, PROD_NUM, VOL, TC_VOL, ULLAGE, HEIGHT, WATER, TEMP, WATER_VOL, AL_MASK, AL_NUM.

### 3.4 FINDING: CSR Data (CMD 40) Not Implemented — LOW

CSR (Current Status Report) data is binary ASCII from the VFD/COLLECT system. Format is opaque and underdocumented.

**Impact:** Minimal — CSR data is typically used for low-level diagnostics. Not a priority for cloud monitoring.

**Recommendation:** Defer. Implement only if specific customer requests CSR visibility.

### 3.5 FINDING: Response Signature Validation Inconsistency — LOW RISK

The spec shows FDC responses include `<SIGNATURE>` computed over `<TABLE>...</TABLE>`. Both adapters validate response signatures, but the spec is ambiguous about whether the signature covers `<TABLE>` or `<ANS>` content.

**Desktop:** `ValidateTransactionResponseSignature()` — validates `<TABLE>` block
**Edge:** `validateTransactionResponseSignature()` — validates `<TABLE>` block

Both are consistent with each other, which is the important thing. If real FDC devices use a different signing scope, this will surface as `251` errors during integration testing.

### 3.6 FINDING: Push Listener Port (P+2) Is an Implementation Convention — INFO

The spec defines unsolicited mode where FDC pushes to the host, but does NOT specify which port the host listens on. Both adapters use `AuthPort + 2` as the push listener port. This works because the host registers its callback URL with the FDC during mode change.

**Risk:** If the FDC firmware expects the host to listen on a specific port (e.g., P+1), the push listener won't receive data. This should be validated during hardware integration testing.

### 3.7 FINDING: Error 207 (PROD_DATA_ERROR) Not Explicitly Handled — LOW

Error code 207 (`REST_API_ACK_CODE_PROD_DATA_ERROR`) is defined in the Products & Prices section but is caught by the generic "non-201 is error" fallback in both adapters. This is acceptable but could produce unclear error messages.

### 3.8 FINDING: Heartbeat Piggybacks on CMD 55 (Product Read) — ACCEPTABLE

The Radix protocol has no dedicated heartbeat/ping endpoint. Both adapters use `CMD_CODE=55` (read products) as a liveness probe. This is a pragmatic choice that also validates the FCC's product catalog is accessible.

**Risk:** If the FDC has no products configured, it may return an error that is misinterpreted as a heartbeat failure. Both adapters handle this by checking for `RESP_CODE=201` only (not requiring product data).

---

## 4. Desktop vs. Edge Agent Parity

| Capability | Desktop (C#) | Edge (Kotlin) | Parity? |
|-----------|:------------:|:-------------:|---------|
| PULL mode (ON_DEMAND) | Yes | Yes | Aligned |
| PUSH mode (UNSOLICITED) | Yes (RadixPushListener) | Yes (RadixPushListener) | Aligned |
| HYBRID mode | Yes | Yes | Aligned |
| Pre-auth send | Yes | Yes | Aligned |
| Pre-auth cancel | Yes | Yes | Aligned |
| Pre-auth token correlation | ConcurrentDictionary | ConcurrentHashMap | Aligned |
| Pre-auth TTL | 30 minutes | 30 minutes | Aligned |
| Heartbeat (CMD 55) | 5s timeout | 5s timeout | Aligned |
| Signature: constant-time compare | Yes (FixedTimeEquals) | **Not confirmed** | **VERIFY** |
| Push listener back-pressure | Channel\<T\> max 10,000 | Queue max 10,000 | Aligned |
| Push listener overflow response | 503 | 503 | Aligned |
| Max fetch batch | 200 | 200 | Aligned |
| Token wrap | 65536 | 65536 | Aligned |
| Pump status | NotSupported | Synthesized from pre-auth state | **DIVERGENT** |
| XML parser: XXE protection | Not confirmed | Disabled external entities | **VERIFY desktop** |
| Normalization: volume | decimal → microlitres | BigDecimal → microlitres | Aligned |
| Normalization: amount | decimal → minor units | BigDecimal → minor units | Aligned |
| Normalization: dedup key | `{FDC_NUM}-{FDC_SAVE_NUM}` | `{FDC_NUM}-{FDC_SAVE_NUM}` | Aligned |
| Normalization: timezone | Config timezone → UTC | Config timezone → UTC | Aligned |
| Product code mapping | Config map | Config map | Aligned |
| Pump address mapping | JSON config (fccPumpAddressMap) | JSON config (fccPumpAddressMap) | Aligned |

### Key Divergence: Pump Status

- **Desktop:** `PumpStatusCapability = NotSupported` — returns empty list.
- **Edge:** Synthesizes pump status from active pre-auth state (AUTHORIZED/IDLE).

The Edge approach is more useful since it provides visibility into which pumps have active authorizations. **Recommendation:** Backport the synthesized pump status approach to the Desktop agent for consistency.

---

## 5. Security Audit

| Check | Status | Notes |
|-------|--------|-------|
| SHA-1 usage documented | Yes | S-DSK-017 / S-008: protocol mandate, confined to LAN |
| SHA-1 upgrade path | Documented | Will upgrade to SHA-256 if Radix firmware supports it |
| Constant-time signature comparison | Desktop: Yes; Edge: Verify | Prevents timing side-channel attacks |
| XXE protection in XML parser | Edge: Yes; Desktop: Verify | Prevents XML external entity injection |
| Shared secret storage | Credential store (preferred), config fallback | S-DSK-012 credential resolution |
| HTTPS on cloud URLs | Separate concern (enforced) | S-DSK-019 |
| Token collision logging | Yes (L-09) | 16-bit modulo wrap detection |
| Input validation on USN-Code | Config validation (1–999999) | Prevents header injection |

**Action items:**
1. **Verify Edge agent constant-time comparison** — if using `String.equals()`, replace with constant-time comparison to prevent timing attacks on signature validation.
2. **Verify Desktop agent XXE protection** — ensure `XmlReader` settings disable DTD processing and external entity resolution.

---

## 6. VirtualLab Simulator Coverage

The VirtualLab Radix simulator (`RadixSimulatorService` + `RadixSimulatorState`) covers:

| Capability | Simulated? | Notes |
|-----------|:----------:|-------|
| Transaction port (P+1) | Yes | CMD 10, 20, 55, 201 |
| Auth port (P) | Yes | AUTH_DATA pre-auth |
| SHA-1 signature validation | Yes | Configurable (can disable for testing) |
| FIFO transaction buffer | Yes | Inject + drain |
| Product catalog | Yes | Default 4 products |
| Error injection | Yes | Queue-based error simulation |
| Unsolicited push (FDC → Host) | Yes | Can post to callback URL |
| Products write (CMD 66) | **No** | |
| Day close (CMD 77) | **No** | |
| ATG tank data (CMD 30) | **No** | |
| ATG deliveries (CMD 35) | **No** | |
| CSR data (CMD 40) | **No** | |

Simulator coverage matches adapter implementation coverage — both are missing the same 5 operations.

---

## 7. Test Coverage Assessment

### Desktop Agent Tests

| Test Area | Coverage | Files |
|-----------|----------|-------|
| SHA-1 signature computation | Unit tests | `RadixSignatureHelperTests.cs` |
| E2E (via VirtualLab) | Integration | `RadixSimulatorE2ETests.cs` |
| XML builder | **No dedicated tests** | — |
| XML parser | **No dedicated tests** | — |
| Push listener | **No dedicated tests** | — |
| Pre-auth correlation | **No dedicated tests** | — |
| Normalization edge cases | **No dedicated tests** | — |

### Edge Agent Tests

| Test Area | Coverage | Files |
|-----------|----------|-------|
| SHA-1 signature computation | Unit tests | `RadixSignatureHelperTests.kt` |
| XML builder | Unit tests | `RadixXmlBuilderTests.kt` |
| XML parser | Unit tests | `RadixXmlParserTests.kt` |
| Adapter (heartbeat, pre-auth, fetch) | Unit tests | `RadixAdapterTests.kt` |
| Test fixtures | XML fixtures | `src/test/resources/fixtures/*.xml` |
| Push listener | **Within adapter tests** | — |

**Finding:** Edge agent has significantly better unit test coverage than Desktop agent. Desktop relies primarily on E2E integration tests through VirtualLab, which is good for confidence but doesn't catch XML builder/parser edge cases in isolation.

**Recommendation:** Add unit tests for `RadixXmlBuilder.cs`, `RadixXmlParser.cs`, and `RadixPushListener.cs` in the Desktop agent to match Edge agent coverage.

---

## 8. Recommended Wave 2 Actions

### P0 — Must Fix

| # | Action | Effort | Rationale |
|---|--------|--------|-----------|
| 1 | Verify Edge agent constant-time signature comparison | 1h | Security: timing attack vector |
| 2 | Verify Desktop agent XXE protection in XML parser | 1h | Security: XXE injection vector |

### P1 — Should Implement

| # | Action | Effort | Rationale |
|---|--------|--------|-----------|
| 3 | Implement `IFccPriceManagement` (CMD 55 read + CMD 66 write) on both adapters | 2d | Remote price management is a key operator requirement |
| 4 | Backport synthesized pump status from Edge to Desktop | 0.5d | Parity: Desktop returns empty, Edge synthesizes from pre-auth |
| 5 | Add Desktop unit tests for XmlBuilder, XmlParser, PushListener | 1d | Test gap: Desktop relies solely on E2E |
| 6 | Add error 207 explicit handling with clear error message | 0.5h | Clarity: currently caught by generic fallback |

### P2 — Nice to Have

| # | Action | Effort | Rationale |
|---|--------|--------|-----------|
| 7 | Implement ATG tank monitoring (CMD 30) | 2d | Tank levels enable low-fuel alerts and leak detection |
| 8 | Implement ATG delivery tracking (CMD 35) | 1d | Delivery reconciliation |
| 9 | Implement Day Close (CMD 77) | 0.5d | Remote day close for operators |
| 10 | Add ATG + delivery to VirtualLab simulator | 1d | Required for testing items 7–8 |

### P3 — Defer

| # | Action | Effort | Rationale |
|---|--------|--------|-----------|
| 11 | Implement CSR data (CMD 40) | 1d | Low demand; opaque binary format |
| 12 | Verify push listener port convention with real FDC hardware | 0.5d | P+2 is convention, spec doesn't specify |

---

## 9. Summary

**Overall assessment: The Radix adapter implementation is solid for the core transaction and pre-auth flows.** Both Desktop and Edge agents correctly implement the signature calculation, XML envelope structure, HTTP headers, response code handling, and token management. The dual-port architecture (auth on P, transactions on P+1) is correctly modeled.

**The main gaps are in management operations** (price write, day close, ATG, CSR) which are not part of the critical data path but would add operational value. Price management (CMD 66) is the highest-priority gap as it enables remote price updates from the cloud portal.

**Parity between Desktop and Edge is excellent** — the only meaningful divergence is pump status synthesis (Edge does it, Desktop doesn't). Test coverage is better on the Edge side and should be improved for Desktop.

**Security posture is good** with SHA-1 usage documented as a protocol limitation, credential store integration, and back-pressure protection on push listeners. Two verification items (constant-time comparison on Edge, XXE protection on Desktop) should be checked promptly.
