# Radix FCC Adapter — Phased Development Plan

**Agent System Prompt:** Always prepend `docs/plans/agent-prompt-radix-adapter.md` when assigning any task below.

**Reference Document:** `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — the Radix protocol deep dive and integration analysis. Every task below references sections of that document.

**Sprint Cadence:** 2-week sprints

---

## Architecture Decision: HTTP/XML Protocol Strategy

The Radix protocol is **HTTP/XML with SHA-1 message signing**, unlike the stateful TCP/JPL protocol used by DOMS. This means:

1. **Radix is stateless** — each request is an independent HTTP POST. No persistent connection, no binary framing, no session state.
2. **Edge agents (Kotlin + .NET) both implement HTTP/XML** for production use against real Radix FDCs.
3. **Cloud adapter handles two paths** — pre-normalized data from Edge Agent uploads AND direct XML push from FDCs in `CLOUD_DIRECT` mode.
4. **No `IFccConnectionLifecycle` needed** — Radix adapters do NOT implement this TCP-only interface. They are stateless per-request.
5. **`acknowledgeTransactions()` is a no-op** — Radix ACKs are implicit in the CMD_CODE=201 ACK sent during the fetch loop, not a separate post-processing step.
6. **Dual-port communication** — Radix FDCs use port P for External Authorization and port P+1 for Transaction Management.

```
Production Path (Edge Agent — HTTP/XML):
  [Radix FDC] ←HTTP/XML→ [Edge Agent RadixAdapter] → normalize → buffer/upload → [Cloud]

Cloud Direct Path (FDC → Cloud):
  [Radix FDC] ←HTTP/XML→ [Cloud XML Ingestion Endpoint] → validate → normalize → store

Cloud Adapter Role (Production):
  Receives pre-normalized transactions from Edge Agent uploads
  Performs validation, dedup, and storage — NOT raw Radix protocol handling
  Also handles direct XML push from FDCs in CLOUD_DIRECT mode
```

---

## Current Implementation Status

| Component | Location | Status | Action |
|-----------|----------|--------|--------|
| Edge Agent (Kotlin) RadixAdapter | `src/edge-agent/.../adapter/radix/` | **Does not exist** | **Create** full HTTP/XML implementation |
| Desktop Agent (C#) RadixAdapter | `src/desktop-edge-agent/.../Adapter/Radix/` | **Does not exist** | **Create** full HTTP/XML implementation |
| Cloud RadixCloudAdapter | `src/cloud/FccMiddleware.Adapter.Radix/` | **Does not exist** | **Create** cloud adapter project |
| VirtualLab | `VirtualLab/src/` | **No Radix profile** | **Add** Radix FDC simulator |
| Portal FCC Config | `src/portal/src/app/features/site-config/` | **Generic** — RADIX in vendor dropdown but no vendor-specific fields | **Add** Radix-specific config fields |
| FccVendor enum | Cloud + Kotlin Edge | `RADIX` already exists | No change needed |
| FccVendor enum | Desktop Agent (.NET) | Only has `Doms` | **Add** `Radix` |

---

## Phase 0 — Shared Infrastructure & Config Changes (Sprint 1)

### RX-0.1: Config & Enum Extensions Across All Layers

**Sprint:** 1
**Component:** All (Kotlin Edge Agent, .NET Desktop Agent, Cloud Backend)
**Prereqs:** None
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §3.3 (what needs to be modified), §3.4 (configuration changes)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/Enums.kt` — current `FccVendor` enum (already has `RADIX`)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/AdapterTypes.kt` — current `AgentFccConfig`, `PreAuthCommand`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/Enums.cs` — current `FccVendor` enum (only has `Doms`)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs` — current `FccConnectionConfig`, `PreAuthCommand`
- `src/cloud/FccMiddleware.Domain/Models/Adapter/SiteFccConfig.cs` — current cloud config model

**Task:**
Extend the shared adapter infrastructure across ALL THREE layers to support Radix-specific configuration and addressing.

**Detailed instructions:**

**Layer 1 — .NET Desktop Agent (`src/desktop-edge-agent/`):**
1. Add `Radix` to the `FccVendor` enum in `Enums.cs` (Kotlin and Cloud already have it)
2. Extend `FccConnectionConfig` in `AdapterTypes.cs` with new Radix-specific nullable fields:
   - `SharedSecret` (string?) — SHA-1 signing password for message authentication
   - `UsnCode` (int?) — Unique Station Number (1–999999), sent as `USN-Code` HTTP header
   - `AuthPort` (int?) — External Authorization port; transaction port is derived as `AuthPort + 1`
   - `FccPumpAddressMap` (string?) — JSON dictionary mapping canonical pump numbers to Radix `(PUMP_ADDR, FP)` pairs for the three-level addressing model
3. Extend `PreAuthCommand` in `AdapterTypes.cs` with optional Radix customer data fields (needed for Radix `<AUTH_DATA>` per §2.6):
   - `CustomerTaxId` (string?) — maps to Radix `<CUSTID>` when `CUSTIDTYPE=1`
   - `CustomerName` (string?) — maps to Radix `<CUSTNAME>`
   - `CustomerIdType` (int?) — maps to Radix `<CUSTIDTYPE>` (1=TIN, 2=DrivingLicense, 3=VotersNumber, 4=Passport, 5=NID, 6=NIL)
   - `CustomerPhone` (string?) — maps to Radix `<MOBILENUM>`

**Layer 2 — Kotlin Edge Agent (`src/edge-agent/`):**
4. `FccVendor.RADIX` already exists in `Enums.kt` — no change needed
5. Extend `AgentFccConfig` in `AdapterTypes.kt` with new Radix-specific nullable fields:
   - `sharedSecret: String? = null` — SHA-1 signing password (`@Sensitive`)
   - `usnCode: Int? = null` — Unique Station Number (1–999999)
   - `authPort: Int? = null` — External Authorization port; transaction port = authPort + 1
   - `fccPumpAddressMap: String? = null` — JSON string mapping canonical pump numbers to Radix `(PUMP_ADDR, FP)` pairs
6. Extend `PreAuthCommand` in `AdapterTypes.kt` with optional Radix customer data fields:
   - `customerName: String? = null` — maps to Radix `<CUSTNAME>`
   - `customerIdType: Int? = null` — maps to Radix `<CUSTIDTYPE>`
   - `customerPhone: String? = null` — maps to Radix `<MOBILENUM>`
   - Note: `customerTaxId` already exists on `PreAuthCommand`

**Layer 3 — Cloud Backend (`src/cloud/`):**
7. Extend `SiteFccConfig` in `SiteFccConfig.cs` with new Radix-specific nullable properties:
   - `SharedSecret` (string?, `[Sensitive]`) — SHA-1 signing password
   - `UsnCode` (int?) — Unique Station Number (1–999999)
   - `AuthPort` (int?) — External Authorization port
   - `FccPumpAddressMap` (IReadOnlyDictionary<int, RadixPumpAddress>?) — maps canonical pump numbers to `(PumpAddr, Fp)` pairs

**Across all layers:**
8. Verify `RawPayloadEnvelope` can store XML content as a string:
   - Kotlin: `payload` field with `contentType = "text/xml"` — already supported
   - .NET Desktop: `RawJson` field — store XML string here per §9.1 Option B
   - Cloud: `Payload` field with `ContentType = "Application/xml"` — already supported
9. Ensure all new fields are nullable/optional so that existing DOMS configurations are unaffected

**Acceptance criteria:**
- `FccVendor.Radix` is available in the .NET Desktop enum (already exists in Kotlin + Cloud)
- `AgentFccConfig` (Kotlin) has all 4 Radix-specific fields as nullable with defaults
- `FccConnectionConfig` (.NET Desktop) has all 4 Radix-specific fields as nullable
- `SiteFccConfig` (Cloud) has all 4 Radix-specific fields as nullable
- `PreAuthCommand` has customer data fields on both Kotlin and .NET Desktop (all nullable)
- `sharedSecret` / `SharedSecret` is marked `@Sensitive` / `[SensitiveData]` on all layers
- Existing DOMS adapter code compiles and tests pass on all layers without changes
- No breaking changes to any existing adapter interface or shared type

---

### RX-0.2: Radix Adapter Factory Registration Across All Layers

**Sprint:** 1
**Component:** All (Kotlin Edge Agent, .NET Desktop Agent, Cloud Backend)
**Prereqs:** RX-0.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/FccAdapterFactory.kt` — Kotlin edge factory (IMPLEMENTED_VENDORS gate)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs` — .NET desktop factory (switch expression)
- `src/cloud/FccMiddleware.Infrastructure/Adapters/FccAdapterFactory.cs` — cloud factory (registry-based pattern)

**Task:**
Register the Radix adapter in all three adapter factories. Wire up DI for the Radix adapter's dependencies.

**Detailed instructions:**

**Kotlin Edge Agent:**
1. In `FccAdapterFactory.kt`, add a case in the `when (vendor)` block for `FccVendor.RADIX → RadixAdapter(config)` (the `RadixAdapter` class will be created in RX-1.1; for now, add the case and leave it throwing `UnsupportedOperationException` until the class exists)
2. Do NOT add `FccVendor.RADIX` to `IMPLEMENTED_VENDORS` yet — that happens when the adapter is complete

**.NET Desktop Agent:**
3. In `FccAdapterFactory.cs`, add a case in the switch expression for `FccVendor.Radix => new RadixAdapter(httpFactory, config, loggerFactory.CreateLogger<RadixAdapter>())` (stub with `NotImplementedException` until RX-2.1)

**Cloud Backend:**
4. In the cloud `FccAdapterFactory` DI registration (in `Program.cs`), register the `RadixCloudAdapter` for `FccVendor.RADIX`:
   ```csharp
   registry[FccVendor.RADIX] = cfg => new RadixCloudAdapter(httpClient, cfg);
   ```
   (stub with `NotImplementedException` until RX-6.1)
5. Add the new Radix project references to the factory projects' `.csproj` files once the projects exist (Phase 1, Phase 2, and Phase 6)

**Acceptance criteria:**
- All three factory switch/registry blocks include Radix vendor
- Attempting to create a Radix adapter throws `NotImplementedException` / `UnsupportedOperationException` (temporary until implementation completes)
- Existing DOMS path is unaffected on all platforms
- Unit test verifies each factory recognizes the Radix vendor

---

### RX-0.3: Interface Compatibility — acknowledgeTransactions No-Op

**Sprint:** 1
**Component:** Kotlin Edge Agent, .NET Desktop Agent
**Prereqs:** DOMS-0.3 (adds `acknowledgeTransactions` to `IFccAdapter`), DOMS-0.4 (mirrors to .NET)
**Estimated effort:** 0.25 day

**Read these artifacts before starting:**
- `docs/plans/dev-plan-doms-adapter.md` — DOMS-0.3 (acknowledgeTransactions), DOMS-0.4 (.NET mirror)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/IFccAdapter.kt` — current interface
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccAdapter.cs` — current interface

**Task:**
Ensure the Radix adapter stubs implement `acknowledgeTransactions()` as a no-op. Radix transaction acknowledgement is implicit (CMD_CODE=201 ACK sent during the fetch loop in RX-3.1), so this method returns `true` without action.

**Detailed instructions:**
1. This task depends on DOMS-0.3 and DOMS-0.4 completing first — those tasks add `acknowledgeTransactions()` to `IFccAdapter` on both platforms
2. When creating the Radix adapter stubs (RX-1.1 and RX-2.1), ensure they implement `acknowledgeTransactions()`:
   - **Kotlin:** `override suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean = true`
   - **.NET:** `public Task<bool> AcknowledgeTransactionsAsync(IReadOnlyList<string> transactionIds, CancellationToken ct) => Task.FromResult(true);`
3. Add KDoc/XML doc comment: "No-op for Radix — transaction acknowledgement is implicit via CMD_CODE=201 ACK sent during the fetch loop."
4. **Important:** The Radix adapter does NOT implement `IFccConnectionLifecycle` (DOMS-0.1) — it is HTTP-based and stateless. Do not add this interface.
5. The Radix adapter does NOT need `IFccEventListener` (DOMS-0.2) for pull mode. For push mode (Phase 5), the `RadixPushListener` feeds transactions directly into the ingestion pipeline without using this interface.

**Acceptance criteria:**
- `acknowledgeTransactions()` returns `true` without any network I/O
- Radix adapter does NOT implement `IFccConnectionLifecycle`
- KDoc/XML comments explain why this is a no-op
- All existing code compiles

---

### RX-0.4: Create Agent Prompt Document

**Sprint:** 1
**Component:** Documentation
**Prereqs:** None
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/plans/agent-prompt-doms-adapter.md` — DOMS agent prompt (template to follow)
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — Radix protocol details

**Task:**
Create `docs/plans/agent-prompt-radix-adapter.md` — the agent system prompt document referenced by every task in this plan. Follow the pattern established by the DOMS agent prompt.

**Detailed instructions:**
1. Create `docs/plans/agent-prompt-radix-adapter.md` following the structure of `agent-prompt-doms-adapter.md`
2. Include these sections:
   - **You Are Working On** — Radix FCC Adapter overview (HTTP/XML with SHA-1 signing, dual-port)
   - **What This Adapter Does** — 7-8 bullet points covering fetch loop, pre-auth, normalization, XML signing, push/pull modes
   - **Critical Protocol Facts** — comparison table: Radix vs DOMS vs other adapters (transport, auth, keepalive, etc.)
   - **Technology Stack Per Component** — Kotlin (OkHttp/java.net, XML parsers), .NET (HttpClient, System.Xml.Linq), Cloud, VirtualLab, Portal
   - **Project Structure** — directory trees for both Kotlin and .NET Radix adapter directories
   - **Key Architecture Rules** — HTTP stateless, dual-port (P auth, P+1 transactions), SHA-1 signing order, FIFO drain loop, MODE management, currency handling, volume conversion, timestamp conversion
   - **Radix Data Encoding Reference** — volume (litres → microlitres), amount (Radix decimal → minor units), timestamps (FDC_DATE+FDC_TIME → UTC), dedup key (FDC_NUM-FDC_SAVE_NUM)
   - **Must-Read Artifacts** — table of all relevant documents
   - **Current Implementation Status** — table of component statuses
   - **Existing Patterns to Follow** — references to DOMS adapter implementations
   - **Testing Standards** — JUnit 5 + MockK (Kotlin), xUnit + Moq (C#), XML fixture files
   - **Open Questions** — RQ-1 through RQ-8 from the WIP plan
3. Include the Radix-specific protocol comparison table:

| Aspect | Radix | DOMS |
|--------|-------|------|
| Transport | HTTP POST (XML body) | TCP socket (persistent) |
| Auth | SHA-1 message signing + USN-Code header | FcLogon handshake |
| Ports | P (auth) + P+1 (transactions) | Single JPL port |
| Transaction fetch | FIFO drain: request → ACK → next (CMD_CODE=10) | Lock-read-clear supervised buffer |
| Transaction ACK | CMD_CODE=201 inline during fetch | clear_FpSupTrans_req (separate step) |
| Pre-auth | `<AUTH_DATA>` XML to port P | authorize_Fp_req JPL message |
| Mode management | CMD_CODE=20 (OFF/ON_DEMAND/UNSOLICITED) | Not applicable |
| Volume format | Litres as decimal string ("15.54") | Centilitres (integer) |
| Amount format | Currency units as decimal string ("30000.0") | ×10 factor |
| Pump addressing | Three-level: PUMP_ADDR + FP + NOZ | FpId + NozzleId |

**Acceptance criteria:**
- `docs/plans/agent-prompt-radix-adapter.md` exists and follows the DOMS agent prompt structure
- All protocol-specific details are accurate per the WIP plan
- Technology stack covers all five components
- Project structure trees show the expected directory layout for both platforms
- Must-Read Artifacts table includes all relevant documents

---

## Phase 1 — Core Adapter Skeleton: Kotlin Edge Agent (Sprints 1–2)

### RX-1.1: Radix Directory Structure & Project Scaffold (Kotlin Edge Agent)

**Sprint:** 1
**Component:** Edge Agent (Kotlin)
**Prereqs:** RX-0.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §3.2 (new files list)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/` — reference directory structure to follow
- `docs/plans/agent-prompt-edge-agent.md` — Edge Agent conventions

**Task:**
Create the Radix adapter directory structure and empty class files for the Kotlin Edge Agent, following the established DOMS pattern.

**Detailed instructions:**
1. Create directory `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/`
2. Create stub files:
   - `RadixAdapter.kt` — class implementing `IFccAdapter` with all methods returning stub failures (not throwing — per IFccAdapter contract). Include `IS_IMPLEMENTED = false` companion constant. Include `acknowledgeTransactions()` returning `true` (no-op per RX-0.3).
   - `RadixProtocolDtos.kt` — placeholder for Radix-specific DTOs
   - `RadixSignatureHelper.kt` — placeholder for SHA-1 signing utility
   - `RadixXmlBuilder.kt` — placeholder for XML request construction
   - `RadixXmlParser.kt` — placeholder for XML response parsing
3. Create test directory `src/edge-agent/app/src/test/kotlin/com/fccmiddleware/edge/adapter/radix/`
4. Create empty test files:
   - `RadixAdapterTests.kt`
   - `RadixSignatureHelperTests.kt`
   - `RadixXmlParserTests.kt`
   - `RadixXmlBuilderTests.kt`
5. Create test fixtures directory `src/edge-agent/app/src/test/kotlin/com/fccmiddleware/edge/adapter/radix/fixtures/`
6. Ensure the project compiles with all stubs

**Acceptance criteria:**
- Directory structure at `adapter/radix/` mirrors the DOMS pattern
- All stub classes compile
- `RadixAdapter` implements `IFccAdapter` (including `acknowledgeTransactions`) with stub returns (not throws)
- `RadixAdapter` does NOT implement `IFccConnectionLifecycle` (HTTP-based, stateless)
- `IS_IMPLEMENTED = false` in companion object
- Gradle build succeeds with zero errors

---

### RX-1.2: SHA-1 Message Signing — RadixSignatureHelper (Kotlin Edge Agent)

**Sprint:** 1
**Component:** Edge Agent (Kotlin)
**Prereqs:** RX-1.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.2 (message signing, critical implementation notes)
- §2.4, §2.6 — examples showing where signatures appear in request/response XML

**Task:**
Implement the SHA-1 message signing utility that matches the Radix FDC's exact expectations. This is the most critical low-level building block — if signatures don't match character-for-character, **all Radix communication fails**.

**Detailed instructions:**
1. Create `RadixSignatureHelper` as an object (Kotlin singleton) with methods:
   - `computeTransactionSignature(reqContent: String, sharedSecret: String): String` — computes `SHA1(<REQ>...</REQ> + SECRET_PASSWORD)` for transaction management (port P+1). The input is the full content between and including `<REQ>` and `</REQ>` tags, concatenated with the shared secret **immediately** after `</REQ>` with no space.
   - `computeAuthSignature(authDataContent: String, sharedSecret: String): String` — computes `SHA1(<AUTH_DATA>...</AUTH_DATA> + SECRET_PASSWORD)` for external authorization (port P). Same concatenation rule.
   - `validateSignature(content: String, expectedSignature: String, sharedSecret: String): Boolean` — verifies a response signature from the FDC
2. SHA-1 output should be lowercase hex string (40 characters)
3. Use `java.security.MessageDigest.getInstance("SHA-1")` — encode the input string as UTF-8 bytes before hashing
4. **Critical:** Whitespace and special characters in the XML content matter — the hash must match character-for-character. Do NOT trim, normalize, or reformat the XML before signing.
5. Add KDoc comments explaining the signing protocol for each method

**Unit tests (in `RadixSignatureHelperTests.kt`):**
- Test with known input XML and secret → verify output hash matches expected value
- Test that whitespace differences in XML produce different hashes
- Test both `<REQ>` and `<AUTH_DATA>` signing paths
- Test signature validation (match and mismatch)
- Test with empty content + secret
- Test with special characters (Turkish, Arabic) in content
- Test that secret is appended immediately after closing tag with no separator

**Acceptance criteria:**
- SHA-1 computation produces correct lowercase hex output
- Transaction signing uses `<REQ>...</REQ>` content + secret concatenation
- Auth signing uses `<AUTH_DATA>...</AUTH_DATA>` content + secret concatenation
- Validation correctly accepts matching signatures and rejects mismatches
- All unit tests pass
- No whitespace normalization or XML reformatting in the signing path

---

### RX-1.3: Radix Protocol DTOs (Kotlin Edge Agent)

**Sprint:** 1
**Component:** Edge Agent (Kotlin)
**Prereqs:** RX-1.1
**Estimated effort:** 0.5–1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.4 (TRN element attributes), §2.5 (unsolicited TRN), §2.6 (AUTH_DATA/FDCACK), §2.11 (RFID_CARD, DISCOUNT), §2.12 (custom headers)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/DomsAdapter.kt` — DOMS adapter pattern to follow

**Task:**
Create all Radix-specific data transfer objects (DTOs) for parsed XML data.

**Detailed instructions:**
1. Create the following data classes in `RadixProtocolDtos.kt`:

   **Transaction data:**
   - `RadixTransactionData` — all `<TRN>` attributes: `amo`, `efdId`, `fdcDate`, `fdcTime`, `fdcName`, `fdcNum`, `fdcProd`, `fdcProdName`, `fdcSaveNum`, `fdcTank`, `fp`, `noz`, `price`, `pumpAddr`, `rdgDate`, `rdgTime`, `rdgId`, `rdgIndex`, `rdgProd`, `rdgSaveNum`, `regId`, `roundType`, `vol`
   - `RadixRfidCardData` — `<RFID_CARD>` attributes: `cardType`, `custContact`, `custId`, `custIdType`, `custName`, `discount`, `discountType`, `num`, `num10`, `payMethod`, `productEnabled`, `used`
   - `RadixDiscountData` — `<DISCOUNT>` attributes: `amoDiscount`, `amoNew`, `amoOrigin`, `discountType`, `priceDiscount`, `priceNew`, `priceOrigin`, `volOrigin`
   - `RadixCustomerData` — `<CUST_DATA>`: `used` (Int)

   **Response envelopes:**
   - `RadixTransactionResponse` — parsed `<FDC_RESP>`: `respCode` (Int), `respMsg` (String), `token` (String), `transaction` (RadixTransactionData?), `rfidCard` (RadixRfidCardData?), `discount` (RadixDiscountData?), `customerData` (RadixCustomerData?), `signature` (String)
   - `RadixAuthResponse` — parsed `<FDCMS><FDCACK>`: `date` (String), `time` (String), `ackCode` (Int), `ackMsg` (String), `signature` (String)
   - `RadixProductData` — product item from CMD_CODE=55 response: `id` (Int), `name` (String), `price` (String)
   - `RadixProductResponse` — list of products with `respCode`, `respMsg`

   **Request parameters (for builder input):**
   - `RadixPreAuthParams` — `pump` (Int), `fp` (Int), `authorize` (Boolean), `product` (Int), `presetVolume` (String), `presetAmount` (String), `customerName` (String?), `customerIdType` (Int?), `customerId` (String?), `mobileNumber` (String?), `discountValue` (Int?), `discountType` (String?), `token` (String)
   - `RadixModeChangeParams` — `mode` (Int: 0=OFF, 1=ON_DEMAND, 2=UNSOLICITED), `token` (String)

2. All DTOs should be Kotlin `data class` types (immutable via `val` properties)
3. Use `String` types for all Radix decimal values (`amo`, `vol`, `price`) — conversion to `Long` happens during normalization, not in DTOs
4. Use `String` types for date/time fields — parsing happens during normalization
5. Annotate with `@Serializable` if needed for serialization

**Acceptance criteria:**
- All DTOs cover every field from the Radix spec XML examples
- DTOs are immutable data classes
- Radix decimal fields remain as Strings (no premature conversion)
- Compiles cleanly with no warnings

---

### RX-1.4: XML Request Builder — RadixXmlBuilder (Kotlin Edge Agent)

**Sprint:** 2
**Component:** Edge Agent (Kotlin)
**Prereqs:** RX-1.2, RX-1.3
**Estimated effort:** 1–1.5 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.3 (mode change CMD_CODE=20), §2.4 (transaction request CMD_CODE=10, ACK CMD_CODE=201), §2.6 (pre-auth AUTH_DATA), §2.12 (custom headers)
- Appendix B (header quick reference)

**Task:**
Implement the XML request body builder for all Radix host-to-FDC commands. Each builder method must produce correctly structured XML and compute the SHA-1 signature.

**Detailed instructions:**
1. Create `RadixXmlBuilder` object with the following methods:

   **Transaction Management (Port P+1) — `<HOST_REQ>` envelope:**
   - `buildTransactionRequest(token: String, secret: String): String` — CMD_CODE=10, CMD_NAME=TRN_REQ
   - `buildTransactionAck(token: String, secret: String): String` — CMD_CODE=201, CMD_NAME=SUCCESS
   - `buildModeChangeRequest(mode: Int, token: String, secret: String): String` — CMD_CODE=20, CMD_NAME=MODE_CHANGE, with `<MODE>` element inside `<REQ>`
   - `buildProductReadRequest(token: String, secret: String): String` — CMD_CODE=55, CMD_NAME=PRODUCT_REQ

   **External Authorization (Port P) — `<FDCMS>` envelope:**
   - `buildPreAuthRequest(params: RadixPreAuthParams, secret: String): String` — `<AUTH_DATA>` with all fields from `RadixPreAuthParams`
   - `buildPreAuthCancelRequest(pump: Int, fp: Int, token: String, secret: String): String` — same structure with `<AUTH>FALSE</AUTH>`

2. XML must be built using `javax.xml.parsers.DocumentBuilderFactory` and `javax.xml.transform.TransformerFactory`, or `StringBuilder` — whichever produces output that exactly matches the Radix spec examples (whitespace matters for signing)
3. **Critical signing order:** Build the inner content (`<REQ>...</REQ>` or `<AUTH_DATA>...</AUTH_DATA>`) FIRST, convert to string, compute signature using `RadixSignatureHelper`, THEN wrap in outer envelope with `<SIGNATURE>` or `<FDCSIGNATURE>` element
4. Add a helper method `buildHttpHeaders(usnCode: Int, operation: String): Map<String, String>` that returns the required custom headers: `Content-Type: Application/xml`, `USN-Code: {usnCode}`, `Operation: {operation}`

**Unit tests (in `RadixXmlBuilderTests.kt`):**
- Each builder method produces well-formed XML
- Signature element is present and non-empty
- CMD_CODE and CMD_NAME values match expected for each command type
- Pre-auth request includes all non-null fields from params
- Pre-auth cancel has `<AUTH>FALSE</AUTH>`
- Mode change includes `<MODE>` element with correct value (0, 1, or 2)
- Headers method returns correct values for each operation type
- Round-trip test: build XML → extract inner content → recompute signature → matches

**Acceptance criteria:**
- All 6 builder methods produce XML matching the Radix spec examples
- Signatures are correctly computed and embedded
- Headers helper returns correct operation codes per Appendix B
- XML encoding is UTF-8
- All unit tests pass

---

### RX-1.5: XML Response Parser — RadixXmlParser (Kotlin Edge Agent)

**Sprint:** 2
**Component:** Edge Agent (Kotlin)
**Prereqs:** RX-1.3
**Estimated effort:** 1–1.5 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.4 (transaction response XML examples), §2.5 (unsolicited response), §2.6 (auth response), Appendix A (all response/error codes)

**Task:**
Implement the XML response parser for all Radix FDC responses. The parser must handle both success and error responses, and validate signatures on incoming messages.

**Detailed instructions:**
1. Create `RadixXmlParser` object with the following methods:

   **Transaction responses (`<FDC_RESP>`):**
   - `parseTransactionResponse(xml: String): RadixTransactionResponse` — parse `<ANS>` for RESP_CODE/RESP_MSG/TOKEN, `<TRN>` attributes, `<RFID_CARD>` attributes, `<DISCOUNT>` attributes, `<CUST_DATA>` attributes, and `<SIGNATURE>`
   - Handle RESP_CODE=201 (success with TRN data), RESP_CODE=205 (no transaction — return response with null Transaction), RESP_CODE=30 (unsolicited push), and error codes (206, 251, 253, 255)

   **Auth responses (`<FDCMS>`):**
   - `parseAuthResponse(xml: String): RadixAuthResponse` — parse `<FDCACK>` for DATE, TIME, ACKCODE, ACKMSG, and `<FDCSIGNATURE>`
   - Handle ACKCODE=0 (success) and error codes (251, 255, 256, 258, 260)

   **Product responses (`<FDC_RESP>` with product data):**
   - `parseProductResponse(xml: String): RadixProductResponse` — parse product list from CMD_CODE=55 response

   **Signature validation:**
   - `validateTransactionResponseSignature(xml: String, sharedSecret: String): Boolean` — extract content between `<TABLE>...</TABLE>`, validate against `<SIGNATURE>` using `RadixSignatureHelper`
   - `validateAuthResponseSignature(xml: String, sharedSecret: String): Boolean` — extract content, validate against `<FDCSIGNATURE>`

2. Use `javax.xml.parsers.DocumentBuilderFactory` for parsing (Android standard library)
3. Handle missing/empty attributes gracefully (many `<TRN>` attributes may be empty strings)
4. All `<TRN>` attribute values should be stored as Strings in `RadixTransactionData` — type conversion happens during normalization

**Test fixtures (in `fixtures/` directory):**
- `transaction-success.xml` — RESP_CODE=201 with full TRN data (copy from spec §2.4)
- `transaction-empty.xml` — RESP_CODE=205, no transaction
- `transaction-unsolicited.xml` — RESP_CODE=30 (push mode)
- `transaction-signature-error.xml` — RESP_CODE=251
- `auth-success.xml` — ACKCODE=0
- `auth-pump-not-ready.xml` — ACKCODE=258
- `auth-dsb-offline.xml` — ACKCODE=260
- `auth-signature-error.xml` — ACKCODE=251
- `products-success.xml` — product list response

**Unit tests (in `RadixXmlParserTests.kt`):**
- Parse transaction success → all TRN attributes populated correctly
- Parse transaction empty → Transaction is null, respCode is 205
- Parse unsolicited → respCode is 30, TRN data present
- Parse auth success → ackCode is 0, ackMsg is "Success"
- Parse auth errors → correct ackCode for each error type
- Parse with missing optional attributes → no exception, fields are null/empty
- Signature validation passes for correctly signed response
- Signature validation fails for tampered response
- Parse malformed XML → returns appropriate error (per Kotlin sealed result pattern)

**Acceptance criteria:**
- All response types parsed correctly from spec-based fixtures
- Error codes (201, 205, 30, 206, 251, 253, 255, 256, 258, 260) handled
- Missing attributes don't cause exceptions
- Signature validation works for both transaction and auth responses
- Malformed XML handled gracefully
- All unit tests pass

---

### RX-1.6: Heartbeat Implementation (Kotlin Edge Agent)

**Sprint:** 2
**Component:** Edge Agent (Kotlin)
**Prereqs:** RX-1.4, RX-1.5
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §8 (heartbeat strategy — CMD_CODE=55 product read)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/DomsAdapter.kt` — DOMS `heartbeat()` implementation (reference for timeout/error handling pattern)

**Task:**
Implement `RadixAdapter.heartbeat()` using the product/price read command (CMD_CODE=55) as a liveness probe. This is the simplest end-to-end Radix communication path and serves as the first integration point.

**Detailed instructions:**
1. In `RadixAdapter`, implement `heartbeat()`:
   - Build XML request using `RadixXmlBuilder.buildProductReadRequest(token, secret)`
   - Build headers using `RadixXmlBuilder.buildHttpHeaders(usnCode, "2")` (Operation=2 for products)
   - POST to `http://{host}:{transactionPort}` where `transactionPort = authPort + 1`
   - Parse response using `RadixXmlParser.parseProductResponse(xml)`
   - Return `true` if `RESP_CODE=201`, `false` otherwise
2. Apply the IFccAdapter heartbeat contract: 5-second hard timeout, never throw on FCC unreachability — return `false`
3. Catch and log transport errors (network, timeout) — return `false`
4. Catch and log signature errors (RESP_CODE=251) — return `false` but log as WARNING (config issue, not transient)
5. Use OkHttp `HttpClient` or `java.net.HttpURLConnection` for HTTP calls
6. Generate a sequential token for each heartbeat request (simple counter, wrap at 65535)

**Unit tests (in `RadixAdapterTests.kt`):**
- Successful heartbeat (mock returns RESP_CODE=201) → returns `true`
- FCC unreachable (mock throws `IOException`) → returns `false`
- Timeout (mock throws `SocketTimeoutException`) → returns `false`
- Signature error (mock returns RESP_CODE=251) → returns `false`, logged as warning
- Bad XML response → returns `false`

**Acceptance criteria:**
- Heartbeat uses CMD_CODE=55 on port P+1
- Returns `true` only on RESP_CODE=201
- Never throws — always returns `Boolean`
- 5-second timeout enforced
- Signature error logged as warning (distinct from transient failures)

---

## Phase 2 — Core Adapter Skeleton: .NET Desktop Agent (Sprints 2–3)

### RX-2.1: Radix Directory Structure & Scaffold (.NET Desktop Agent)

**Sprint:** 2
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** RX-0.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §3.2 (new files list)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/` — reference directory structure to follow
- `docs/plans/agent-prompt-desktop-edge-agent.md` — Desktop Agent conventions

**Task:**
Create the Radix adapter directory structure and empty class files for the .NET Desktop Agent, following the established DOMS pattern.

**Detailed instructions:**
1. Create directory `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/`
2. Create stub files:
   - `RadixAdapter.cs` — class implementing `IFccAdapter` with all methods throwing `NotImplementedException`. Include `CancelPreAuthAsync`. Include `AcknowledgeTransactionsAsync` returning `Task.FromResult(true)` (no-op per RX-0.3).
   - `RadixProtocolDtos.cs` — placeholder for Radix-specific DTOs
   - `RadixSignatureHelper.cs` — placeholder for SHA-1 signing utility
   - `RadixXmlBuilder.cs` — placeholder for XML request construction
   - `RadixXmlParser.cs` — placeholder for XML response parsing
3. Create test directory `src/desktop-edge-agent/tests/FccDesktopAgent.Core.Tests/Adapter/Radix/`
4. Create empty test files:
   - `RadixAdapterTests.cs`
   - `RadixSignatureHelperTests.cs`
   - `RadixXmlParserTests.cs`
   - `RadixXmlBuilderTests.cs`
5. Create test fixtures directory `src/desktop-edge-agent/tests/FccDesktopAgent.Core.Tests/Adapter/Radix/Fixtures/`
6. Ensure the project compiles with all stubs

**Acceptance criteria:**
- Directory structure matches the DOMS pattern
- All stub classes compile
- `RadixAdapter` implements `IFccAdapter` (including `CancelPreAuthAsync` and `AcknowledgeTransactionsAsync`) with `NotImplementedException` stubs (except `AcknowledgeTransactionsAsync` which returns `true`)
- `dotnet build` succeeds with zero errors

---

### RX-2.2: SHA-1 Message Signing — RadixSignatureHelper (.NET Desktop Agent)

**Sprint:** 2
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** RX-2.1, RX-1.2 (Kotlin reference)
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.2 (message signing)
- The Kotlin `RadixSignatureHelper.kt` from RX-1.2 (for parity)

**Task:**
Implement the SHA-1 message signing utility for .NET, mirroring the Kotlin implementation.

**Detailed instructions:**
1. Create `RadixSignatureHelper` as a static utility class with methods:
   - `ComputeTransactionSignature(string reqContent, string sharedSecret) → string`
   - `ComputeAuthSignature(string authDataContent, string sharedSecret) → string`
   - `ValidateSignature(string content, string expectedSignature, string sharedSecret) → bool`
2. Use `System.Security.Cryptography.SHA1` — encode the input string as UTF-8 bytes before hashing
3. Same signing rules as Kotlin: no whitespace normalization, lowercase hex output, immediate concatenation
4. Use the same test fixtures as Kotlin for cross-platform consistency

**Acceptance criteria:**
- Same signing behavior as Kotlin version for identical inputs
- All unit tests pass and match Kotlin test results
- No whitespace normalization in the signing path

---

### RX-2.3: Radix Protocol DTOs (.NET Desktop Agent)

**Sprint:** 2
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** RX-2.1, RX-1.3 (Kotlin reference)
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- The Kotlin `RadixProtocolDtos.kt` from RX-1.3 (for parity)

**Task:**
Create all Radix-specific DTOs for the .NET Desktop Agent. Same fields as Kotlin, using C# `record` types.

**Detailed instructions:**
1. Create all DTOs in `RadixProtocolDtos.cs` — mirror the Kotlin DTOs from RX-1.3
2. All DTOs should be C# `record` types (immutable)
3. Use `string` types for all Radix decimal values and date/time fields

**Acceptance criteria:**
- All DTOs match Kotlin counterparts field-for-field
- C# records with immutable properties
- Compiles cleanly

---

### RX-2.4: XML Request Builder & Response Parser (.NET Desktop Agent)

**Sprint:** 2–3
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** RX-2.2, RX-2.3, RX-1.4, RX-1.5 (Kotlin references)
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- The Kotlin `RadixXmlBuilder.kt` from RX-1.4 (for parity)
- The Kotlin `RadixXmlParser.kt` from RX-1.5 (for parity)

**Task:**
Implement the XML request builder and response parser for the .NET Desktop Agent. Mirror the Kotlin implementations.

**Detailed instructions:**
1. Create `RadixXmlBuilder.cs` — same 6 builder methods as Kotlin, using `System.Xml.Linq` (XDocument/XElement)
2. Create `RadixXmlParser.cs` — same parser methods as Kotlin, using `System.Xml.Linq`
3. Use the same test fixtures as Kotlin for cross-platform consistency
4. Same signing order: build inner content → compute signature → wrap in outer envelope

**Acceptance criteria:**
- All builder/parser methods produce/consume XML identically to Kotlin versions
- Same test fixtures produce same outputs
- Signatures match between platforms for identical inputs
- All unit tests pass

---

### RX-2.5: Heartbeat Implementation (.NET Desktop Agent)

**Sprint:** 3
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** RX-2.4, RX-1.6 (Kotlin reference)
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsAdapter.cs` — DOMS `HeartbeatAsync` implementation
- The Kotlin `RadixAdapter.heartbeat()` from RX-1.6 (for parity)

**Task:**
Implement `RadixAdapter.HeartbeatAsync()` for .NET, mirroring the Kotlin implementation.

**Detailed instructions:**
1. Same logic as Kotlin RX-1.6: CMD_CODE=55 on port P+1, 5-second timeout, never throw
2. Use a named `HttpClient` from `IHttpClientFactory` (client name: `"fcc"`, same as DOMS)
3. Same error handling: transport errors → `false`, signature errors → `false` with warning log

**Acceptance criteria:**
- Same behavior as Kotlin heartbeat
- Uses `IHttpClientFactory` pattern
- Never throws, 5-second timeout, signature errors logged as warnings

---

## Phase 3 — Transaction Fetch & Normalization (Sprints 3–4)

### RX-3.1: Transaction Fetch — Pull Mode (Kotlin Edge Agent)

**Sprint:** 3
**Component:** Edge Agent (Kotlin)
**Prereqs:** RX-1.4, RX-1.5, RX-1.6
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.3 (mode change), §2.4 (transaction fetch + ACK loop), §9.4 (FIFO vs cursor), §9.5 (mode management lifecycle)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/AdapterTypes.kt` — `TransactionBatch`, `FetchCursor`

**Task:**
Implement `RadixAdapter.fetchTransactions()` with the Radix-specific FIFO drain loop: request one transaction → ACK → request next → repeat until buffer empty or limit reached.

**Detailed instructions:**
1. **Mode management on startup:** The adapter must ensure the FDC is in ON_DEMAND mode (mode=1) before fetching. Add an internal method `ensureModeAsync(mode: Int)`:
   - Send CMD_CODE=20 with `<MODE>1</MODE>` (ON_DEMAND)
   - Parse response, verify RESP_CODE=201
   - Cache the mode state — only re-send if mode is unknown (first call) or if FCC connectivity was lost and restored
   - Log mode change at INFO level

2. **Fetch loop in `fetchTransactions(cursor)`:**
   - Call `ensureModeAsync(1)` — ensure ON_DEMAND mode
   - Initialize empty list for collected transactions
   - **Loop** (max iterations = `cursor.limit` or configurable default, e.g., 100):
     a. Build CMD_CODE=10 request via `RadixXmlBuilder.buildTransactionRequest(token, secret)`
     b. POST to `http://{host}:{transactionPort}` with Operation=1 header
     c. Parse response via `RadixXmlParser.parseTransactionResponse(xml)`
     d. If `RESP_CODE=205` (NO TRN AVAILABLE) → break loop, buffer is empty
     e. If `RESP_CODE=201` (SUCCESS) → wrap as `RawPayloadEnvelope` with `contentType = "text/xml"`, add to list, then send ACK:
        - Build CMD_CODE=201 ACK via `RadixXmlBuilder.buildTransactionAck(token, secret)`
        - POST ACK to same port
        - Parse ACK response — log warning if ACK fails but continue
     f. If error code → log and break loop
   - Return `TransactionBatch`:
     - `transactions` = empty list (raw payloads go into envelopes; normalization is separate per IFccAdapter contract)
     - `hasMore = true` if loop hit the limit (buffer may have more), `false` if RESP_CODE=205 was received
     - `nextCursorToken = "continue"` if `hasMore`, `null` otherwise (Radix FIFO has no cursor — the "cursor" is implicit buffer position)

3. **Token generation:** Maintain an internal counter for TOKEN values (0–65535, wrapping). Each request/ACK pair uses the same TOKEN value.

4. **Error handling:**
   - Network errors → return empty batch with `hasMore = false`
   - RESP_CODE=251 (signature error) → return failure (non-recoverable config problem)
   - RESP_CODE=253 (token error) → log warning, retry with new token
   - RESP_CODE=206 (mode error) → re-send mode change, retry

5. Store each fetched transaction's raw XML as the `RawPayloadEnvelope.payload` content

**Unit tests (in `RadixAdapterTests.kt`):**
- Fetch with 3 available transactions → returns batch of 3, each ACKed
- Fetch with 0 transactions (RESP_CODE=205 on first request) → returns empty batch
- Fetch hits limit (e.g., 2) with more available → `hasMore = true`
- ACK failure after successful fetch → transaction still in batch, warning logged
- Network error mid-loop → returns partial batch collected so far
- Mode change is sent on first fetch call
- Mode change is not re-sent on subsequent calls (cached)

**Acceptance criteria:**
- Fetch loop correctly drains FIFO buffer one transaction at a time
- Each transaction is ACKed before requesting the next
- Mode management ensures ON_DEMAND (mode=1) is set
- `hasMore` accurately reflects buffer state
- Token counter wraps at 65535
- Raw XML preserved in `RawPayloadEnvelope`
- Error handling follows the IFccAdapter contract (no throws on transient failures)

---

### RX-3.2: Transaction Fetch — Pull Mode (.NET Desktop Agent)

**Sprint:** 3
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** RX-2.4, RX-2.5, RX-3.1 (Kotlin reference)
**Estimated effort:** 1.5–2 days

**Read these artifacts before starting:**
- The Kotlin `RadixAdapter.fetchTransactions()` from RX-3.1 (for parity)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs` — `TransactionBatch`, `FetchCursor`

**Task:**
Implement `RadixAdapter.FetchTransactionsAsync()` for the .NET Desktop Agent, mirroring the Kotlin implementation.

**Detailed instructions:**
1. Same FIFO drain loop logic as Kotlin RX-3.1
2. Wrap raw XML in `RawPayloadEnvelope` using `RawJson` field (XML stored as string per §9.1 Option B)
3. Use `IHttpClientFactory` for HTTP calls with CancellationToken support throughout
4. Same mode management, token generation, and error handling as Kotlin

**Acceptance criteria:**
- Same fetch behavior as Kotlin version
- Raw XML stored in `RawPayloadEnvelope.RawJson`
- CancellationToken respected
- All unit tests pass

---

### RX-3.3: Transaction Normalization — NormalizeAsync (Kotlin Edge Agent)

**Sprint:** 3–4
**Component:** Edge Agent (Kotlin)
**Prereqs:** RX-1.3, RX-1.5, RX-0.1
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §4 (complete field mapping table), §2.7 (pump addressing), §2.8 (transaction ID / dedup key), §2.9 (fiscal data)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/CanonicalTransaction.kt` — target canonical model

**Task:**
Implement `RadixAdapter.normalize()` — parse a raw Radix XML payload and map it to a `CanonicalTransaction` following the field mapping in §4.

**Detailed instructions:**
1. In `normalize(rawPayload: RawPayloadEnvelope)`:
   - Extract XML from `rawPayload.payload` (it's XML stored as string)
   - Parse using `RadixXmlParser.parseTransactionResponse(xml)`
   - If no transaction data (RESP_CODE=205), return `NormalizationResult.Failure("INVALID_PAYLOAD", "No transaction in payload")`

2. **Field mappings (from §4):**
   - `fccTransactionId` = `"{FDC_NUM}-{FDC_SAVE_NUM}"` (composed dedup key per §2.8)
   - `siteCode` = from `rawPayload.siteCode`
   - `pumpNumber` = map `PUMP_ADDR` via pump mapping table from `AgentFccConfig.fccPumpAddressMap`. If pump mapping not found, use raw `PUMP_ADDR` value and log warning.
   - `nozzleNumber` = `NOZ` parsed as Int
   - `productCode` = map `FDC_PROD` via `AgentFccConfig.productCodeMapping`. If not found, use `FDC_PROD_NAME` as fallback.
   - `volumeMicrolitres` = parse `VOL` as `BigDecimal`, multiply by 1,000,000, convert to Long. E.g., `"15.54"` → `15_540_000L`
   - `amountMinorUnits` = parse `AMO` as `BigDecimal`, multiply by 100 (for currencies with 2 decimal places). **For TZS (0 decimal places), no multiplication needed.** Use configurable `currencyDecimalPlaces` from config (default: 0 for TZS). See Open Question RQ-1.
   - `unitPriceMinorPerLitre` = parse `PRICE` as `BigDecimal`, apply same currency conversion
   - `startedAt` = parse `FDC_DATE` + `FDC_TIME` as `"yyyy-MM-dd"` + `"HH:mm:ss"`, apply configured timezone from `AgentFccConfig.timezone` → convert to UTC ISO 8601 string
   - `completedAt` = parse `RDG_DATE` + `RDG_TIME` same way
   - `fiscalReceiptNumber` = `EFD_ID` (direct mapping; null/empty → null)
   - `fccVendor` = `"RADIX"` (hardcoded)
   - `attendantId` = `null` (not provided by Radix)
   - `schemaVersion` = `"1.0"`

3. **Volume conversion precision:** Use `BigDecimal` arithmetic for the multiplication to avoid floating-point precision loss: `BigDecimal(vol).multiply(BigDecimal(1_000_000)).toLong()`

4. **Timezone handling:** The adapter must know the site's timezone from `AgentFccConfig.timezone` to correctly convert FDC local times to UTC. Default to UTC if not configured, with a warning log.

5. **Raw payload preservation:** Set `rawPayloadJson` on the canonical transaction output to the original XML string.

**Unit tests (in `RadixAdapterTests.kt`):**
- Normalize standard transaction → all fields mapped correctly
- Volume conversion: `"15.54"` → `15_540_000L`
- Amount conversion with 0 decimal places (TZS): `"30000.0"` → `3000000L`
- FccTransactionId composition: `"100253410"` + `"368989"` → `"100253410-368989"`
- Fiscal receipt mapping: `"182AC9368989"` → `fiscalReceiptNumber`
- Empty `EFD_ID` → null `fiscalReceiptNumber`
- Timestamp conversion with East Africa timezone (UTC+3)
- Missing pump mapping → uses raw PUMP_ADDR, warning logged
- Missing product mapping → uses FDC_PROD_NAME, warning logged
- Empty/null VOL or AMO → returns `NormalizationResult.Failure`

**Acceptance criteria:**
- All §4 field mappings implemented correctly
- Dedup key composed from `FDC_NUM` + `FDC_SAVE_NUM`
- Volume in microlitres (Long), amount in minor units (Long) — no floating point
- Timezone conversion applied
- Fiscal receipt extracted
- Missing mappings degrade gracefully with warnings (not exceptions)
- Returns `NormalizationResult.Success` or `NormalizationResult.Failure` — never throws
- All unit tests pass

---

### RX-3.4: Transaction Normalization — NormalizeAsync (.NET Desktop Agent)

**Sprint:** 4
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** RX-2.3, RX-2.4, RX-3.3 (Kotlin reference)
**Estimated effort:** 1.5 days

**Read these artifacts before starting:**
- The Kotlin `RadixAdapter.normalize()` from RX-3.3 (for parity)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/CanonicalTransaction.cs` — target canonical model
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsAdapter.cs` — DOMS `NormalizeAsync` for reference pattern

**Task:**
Implement `RadixAdapter.NormalizeAsync()` for the .NET Desktop Agent, mirroring the Kotlin normalization.

**Detailed instructions:**
1. Same field mapping logic as Kotlin RX-3.3
2. Use `decimal` arithmetic for volume/amount conversions: `(long)(decimal.Parse(vol) * 1_000_000m)`
3. Parse XML from `rawPayload.RawJson` (XML stored as string)
4. Throw `FccAdapterException` with `IsRecoverable = false` on malformed input (per .NET IFccAdapter contract which returns `CanonicalTransaction`, not sealed result)

**Acceptance criteria:**
- Same normalization results as Kotlin for identical inputs
- `decimal` arithmetic for money and volume (no floating point)
- All unit tests match Kotlin test vectors

---

### RX-3.5: Mode Management Lifecycle (Both Platforms)

**Sprint:** 4
**Component:** Edge Agent (Kotlin), Desktop Edge Agent (.NET)
**Prereqs:** RX-3.1, RX-3.2
**Estimated effort:** 0.5–1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.3 (mode change command), §9.5 (mode management lifecycle)

**Task:**
Implement robust mode management for the Radix adapter — ensuring the FDC is always in the correct transaction mode.

**Detailed instructions:**
1. Add a `currentMode` state field to `RadixAdapter` (nullable Int — null means unknown)
2. Implement `ensureModeAsync(desiredMode: Int)` (already scaffolded in RX-3.1):
   - If `currentMode == desiredMode`, return immediately (no-op)
   - Build CMD_CODE=20 mode change request
   - POST to transaction port (P+1), Operation=1
   - Parse response:
     - RESP_CODE=201 → set `currentMode = desiredMode`, log at INFO
     - RESP_CODE=251 → non-recoverable (signature config issue)
     - Other error → log warning, set `currentMode = null` (force retry next time)
3. Add `resetModeState()` internal method — called when connectivity is lost/restored to force a mode re-send on next operation
4. Integrate into `fetchTransactions` — call `ensureModeAsync(1)` (ON_DEMAND)
5. Integrate into push mode setup (Phase 5) — call `ensureModeAsync(2)` (UNSOLICITED)
6. On adapter disposal/shutdown: optionally send `ensureModeAsync(0)` (OFF) — best-effort, do not throw if it fails
7. Mirror implementation on .NET Desktop Agent with same logic

**Acceptance criteria:**
- Mode is set to ON_DEMAND before first fetch
- Mode change is not re-sent unnecessarily (cached)
- Connectivity loss resets cached mode state
- Mode change errors are logged but don't crash the adapter
- Shutdown sends OFF mode (best-effort)
- Both Kotlin and .NET implementations behave identically

---

## Phase 4 — Pre-Authorization (Sprints 4–5)

### RX-4.1: SendPreAuth Implementation (Kotlin Edge Agent)

**Sprint:** 4
**Component:** Edge Agent (Kotlin)
**Prereqs:** RX-1.4, RX-1.5, RX-0.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.6 (external authorization), §5 (pre-auth field mapping), §6 (response mapping), §2.7 (pump addressing for pre-auth), §9.3 (TOKEN correlation)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/AdapterTypes.kt` — `PreAuthCommand`, `PreAuthResult`

**Task:**
Implement `RadixAdapter.sendPreAuth()` — build the Radix XML authorization request, send it to port P, parse the response, and map to `PreAuthResult`.

**Detailed instructions:**
1. **Pump address resolution:** The `PreAuthCommand.pumpNumber` is the canonical pump number. The adapter must resolve it to `(PUMP_ADDR, FP)` using `AgentFccConfig.fccPumpAddressMap`. If mapping not found, return `PreAuthResult(status = PreAuthResultStatus.ERROR, message = "Pump mapping not found for pump ${command.pumpNumber}")`.

2. **Product code reverse-mapping:** The `PreAuthCommand` does not include a product code on the Kotlin interface. If a product code is needed, use `0` (all products allowed). If the adapter needs to reverse-map, use the product code mapping from config.

3. **TOKEN generation and tracking (§9.3):**
   - Maintain a `ConcurrentHashMap<Int, ActivePreAuth>` keyed by TOKEN value
   - Generate the next available TOKEN (0–65535), skipping values already in the active map
   - Store the mapping: `TOKEN → { odooOrderId, pumpNumber, issuedAt }`
   - Clean up expired entries (older than 30 minutes) periodically
   - If TOKEN pool is exhausted (extremely unlikely — 65K concurrent pre-auths), return `PreAuthResult(status = ERROR, message = "Token pool exhausted")`

4. **Build and send request:**
   - Create `RadixPreAuthParams` from `PreAuthCommand` fields + resolved pump/FP + TOKEN
   - Map customer data: `PreAuthCommand.customerTaxId` → `CUSTID`, `customerIdType` → `CUSTIDTYPE`, `customerName` → `CUSTNAME`, `customerPhone` → `MOBILENUM`
   - Convert `amountMinorUnits` → Radix decimal string (apply reverse currency conversion)
   - Volume preset = `"0.00"` (we always authorize by amount per BR-6.1b)
   - Build XML via `RadixXmlBuilder.buildPreAuthRequest(params, secret)`
   - Build headers with `Operation: Authorize`
   - POST to `http://{host}:{authPort}` (port P, NOT P+1)

5. **Parse response and map to PreAuthResult (per §6):**
   - ACKCODE=0 → `PreAuthResult(status = AUTHORIZED, authorizationCode = TOKEN.toString())`
   - ACKCODE=251 → `PreAuthResult(status = ERROR, message = "Signature error")`
   - ACKCODE=255 → `PreAuthResult(status = ERROR, message = "Bad XML")`
   - ACKCODE=256 → `PreAuthResult(status = ERROR, message = "Bad header")`
   - ACKCODE=258 → `PreAuthResult(status = DECLINED, message = "Pump not ready")`
   - ACKCODE=260 → `PreAuthResult(status = DECLINED, message = "DSB offline")`
   - On failure, remove TOKEN from active map

6. **Error handling:**
   - Network errors → return `PreAuthResult(status = TIMEOUT, message = "FCC unreachable")`, remove TOKEN from active map

**Unit tests:**
- Successful pre-auth → `status = AUTHORIZED`, TOKEN stored in active map
- Pump not ready (ACKCODE=258) → `status = DECLINED`, TOKEN removed
- DSB offline (ACKCODE=260) → `status = DECLINED`, TOKEN removed
- Pump mapping not found → `status = ERROR` without sending request
- Customer data fields included when present
- Customer data fields omitted when null
- Amount conversion is correct for configured currency
- TOKEN wraps around at 65535
- Network error → `status = TIMEOUT`, TOKEN removed

**Acceptance criteria:**
- Pre-auth sent to port P with correct XML structure
- Pump address resolved from canonical → (PUMP_ADDR, FP)
- TOKEN tracking maintains active pre-auth map
- All ACKCODE error codes mapped correctly per §6
- Customer data fields properly included when present
- Network errors never throw — return failure result

---

### RX-4.2: SendPreAuth & CancelPreAuth Implementation (.NET Desktop Agent)

**Sprint:** 4–5
**Component:** Desktop Edge Agent (.NET)
**Prereqs:** RX-2.4, RX-4.1 (Kotlin reference)
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- The Kotlin `RadixAdapter.sendPreAuth()` from RX-4.1 (for parity)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccAdapter.cs` — includes `CancelPreAuthAsync`

**Task:**
Implement `RadixAdapter.SendPreAuthAsync()` and `RadixAdapter.CancelPreAuthAsync()` for the .NET Desktop Agent.

**Detailed instructions:**
1. **SendPreAuthAsync:** Same logic as Kotlin RX-4.1, using .NET types and `IHttpClientFactory`
2. **CancelPreAuthAsync (§2.6 cancellation: `<AUTH>FALSE</AUTH>`):**
   - Look up the `fccCorrelationId` (which is the TOKEN string) in the active pre-auth map to get the pump/FP
   - If TOKEN not found in active map → return `true` (idempotent — already cancelled or expired)
   - Build cancel XML via `RadixXmlBuilder.BuildPreAuthCancelRequest(pump, fp, token, secret)`
   - POST to port P with `Operation: Authorize` header
   - Parse `<FDCACK>` response:
     - ACKCODE=0 → remove TOKEN from active map, return `true`
     - ACKCODE=258 (pump not ready — may already be idle) → remove TOKEN, return `true` (treat as already cancelled)
     - Other errors → log warning, return `false`
   - Network error → log warning, return `false`

**Acceptance criteria:**
- SendPreAuth matches Kotlin behavior
- CancelPreAuth sends `AUTH=FALSE` to correct pump/FP
- Already-cancelled pre-auth returns `true` (idempotent)
- TOKEN removed from active map on success
- Network errors return `false` without throwing

---

### RX-4.3: Pre-Auth ↔ Dispense Correlation via TOKEN (Both Platforms)

**Sprint:** 5
**Component:** Edge Agent (Kotlin), Desktop Edge Agent (.NET)
**Prereqs:** RX-4.1, RX-3.3 / RX-3.4
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §9.3 (TOKEN correlation mechanism)

**Task:**
Implement the TOKEN-based correlation between pre-auth commands and their resulting dispense transactions. When a transaction is normalized, check if its TOKEN matches an active pre-auth to link them.

**Detailed instructions:**
1. In `normalize()` / `NormalizeAsync()`, after parsing the transaction:
   - Extract the TOKEN from `<ANS TOKEN="...">` in the transaction response
   - Look up TOKEN in the active pre-auth map
   - If found: set `correlationId` = TOKEN string, and `odooOrderId` = the `odooOrderId` from the active pre-auth record
   - Remove the TOKEN from the active map (pre-auth is now completed)
   - If not found: `correlationId` = TOKEN string (for potential later matching), `odooOrderId` = null

2. Add a `resolvePreAuthCorrelation(token: Int): ActivePreAuth?` internal method

3. Handle the edge case where TOKEN=0 (no pre-auth — Normal Order) — skip correlation lookup

**Unit tests:**
- Transaction with TOKEN matching active pre-auth → odooOrderId populated
- Transaction with TOKEN not in active map → odooOrderId is null
- Transaction with TOKEN=0 → no correlation attempt
- After correlation, TOKEN is removed from active map

**Acceptance criteria:**
- Pre-auth ↔ dispense transactions are linked via TOKEN
- OdooOrderId flows from pre-auth to transaction when TOKEN matches
- TOKEN=0 handled as Normal Order (no correlation)
- Active map is cleaned up after correlation
- Both Kotlin and .NET implementations behave identically

---

### RX-4.4: GetPumpStatusAsync — Limited Implementation (Both Platforms)

**Sprint:** 5
**Component:** Edge Agent (Kotlin), Desktop Edge Agent (.NET)
**Prereqs:** RX-1.1, RX-2.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §3.1 (`GetPumpStatusAsync` — no dedicated endpoint), Open Question RQ-6

**Task:**
Implement `RadixAdapter.getPumpStatus()` / `GetPumpStatusAsync()` as a no-op that returns an empty list. Radix does not expose real-time pump status.

**Detailed instructions:**
1. **Kotlin:** Return `emptyList<PumpStatus>()` — per §3.1 and RQ-6, there is no Radix pump status endpoint
2. **.NET:** Return `Task.FromResult<IReadOnlyList<PumpStatus>>(Array.Empty<PumpStatus>())`
3. Log at DEBUG level: "Pump status not supported by Radix FDC"

**Acceptance criteria:**
- Returns empty list without contacting FDC
- No exceptions thrown
- Debug log message present
- Both platforms behave identically

---

## Phase 5 — Push Mode Support (Sprints 5–6)

### RX-5.1: Edge Agent Push Listener — Unsolicited Transaction Reception

**Sprint:** 5–6
**Component:** Edge Agent (Kotlin), Desktop Edge Agent (.NET)
**Prereqs:** RX-1.5 / RX-2.4, RX-3.3 / RX-3.4, RX-3.5
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.5 (unsolicited push format), §7.2 (edge agent push reception), §9.5 (mode management for push)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/IFccAdapter.kt` — check if push listener is part of the adapter interface or a separate concern

**Task:**
Implement an HTTP listener in the Edge Agent that accepts unsolicited Radix transaction POSTs from the FDC in UNSOLICITED mode.

**Detailed instructions:**
1. Create `RadixPushListener` class — an HTTP server endpoint handler (NOT part of `IFccAdapter` — this is a separate infrastructure concern):
   - Listen on a configurable port accessible from the FCC's LAN
   - Accept `POST` requests with `Content-Type: Application/xml`

2. **Request processing pipeline:**
   a. Validate `USN-Code` header against configured `usnCode`
   b. Read XML body
   c. Validate signature using `RadixSignatureHelper`
   d. Parse using `RadixXmlParser.parseTransactionResponse(xml)` — expect RESP_CODE=30 (unsolicited)
   e. Wrap in `RawPayloadEnvelope` and feed into the existing ingestion pipeline (buffer manager or ingestion orchestrator)
   f. Build ACK response: XML `<HOST_REQ>` with CMD_CODE=201, signed — per §7.1
   g. Return ACK as HTTP 200 with `Content-Type: Application/xml`

3. **If ACK is not returned**, the FDC will retry after a timeout — this is the FDC's built-in reliability mechanism. The listener must respond promptly.

4. **Mode setup:** On listener startup, call `RadixAdapter.ensureModeAsync(2)` to set UNSOLICITED mode on the FDC

5. **Error handling:**
   - Invalid USN-Code → return 401 (do not ACK — FDC will retry, and we want to prevent spoofing)
   - Invalid signature → return 401
   - Parse error → log error, return 500 (FDC will retry)
   - Ingestion pipeline error → still ACK the FDC (we received the data; internal processing failure is our problem, not the FDC's). Buffer the raw XML for retry.

6. **Kotlin-specific:** Use Ktor embedded server or a lightweight HTTP server on `Dispatchers.IO`
7. **.NET-specific:** Use Kestrel minimal API or `HttpListener`
8. Wire the push listener into the Edge Agent's DI and startup lifecycle

**Acceptance criteria:**
- HTTP listener accepts Radix unsolicited POSTs
- USN-Code and signature validated
- Transactions parsed and fed into ingestion pipeline
- Proper XML ACK returned to FDC
- UNSOLICITED mode set on FDC at startup
- Invalid requests rejected with appropriate status codes
- FDC retry mechanism works (no ACK → FDC re-sends)

---

### RX-5.2: Push-Mode Hybrid Support

**Sprint:** 6
**Component:** Edge Agent (Kotlin), Desktop Edge Agent (.NET)
**Prereqs:** RX-5.1, RX-3.1 / RX-3.2
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.3 (mode values), §9.5 (mode lifecycle), Open Question RQ-5

**Task:**
Implement HYBRID mode support for Radix — UNSOLICITED as primary with periodic ON_DEMAND catch-up polling.

**Detailed instructions:**
1. For sites configured with `IngestionMode.HYBRID` (Kotlin) / `IngestionMode.BufferAlways` with hybrid flag (.NET):
   - Primary: Run push listener in UNSOLICITED mode (RX-5.1)
   - Catch-up: Periodically (configurable interval, e.g., every 5 minutes):
     a. Switch FDC to ON_DEMAND mode (CMD_CODE=20, MODE=1)
     b. Drain the FIFO buffer via `fetchTransactions()` (RX-3.1/RX-3.2)
     c. Switch back to UNSOLICITED mode (CMD_CODE=20, MODE=2)
   - **Risk:** Transactions arriving during the ON_DEMAND window may be missed by push. Mitigate by making the catch-up window short and draining quickly.

2. Create `RadixHybridModeOrchestrator` that coordinates the push listener and periodic pull:
   - Manages mode switching safely (mutex to prevent concurrent mode changes)
   - Tracks whether the push listener or the pull poller "owns" the current mode
   - Deduplication handles any duplicates from the overlap

3. **Alternative (simpler, recommended for MVP):** Stay in UNSOLICITED permanently. Use a manual trigger (API endpoint or health check failure) to temporarily switch to ON_DEMAND for catch-up, rather than periodic automatic switching. This avoids the mode-switching race condition entirely.

**Acceptance criteria:**
- HYBRID mode functions with push as primary and pull as catch-up
- Mode switching is serialized (no concurrent mode changes)
- Duplicate transactions (received both via push and pull) are handled by dedup
- Configuration allows selecting PUSH-only, PULL-only, or HYBRID

---

## Phase 6 — Cloud Adapter & Portal (Sprints 6–7)

### RX-6.1: Cloud Adapter Project Scaffold

**Sprint:** 6
**Component:** Cloud Backend (.NET)
**Prereqs:** Cloud adapter interface exists (from prior cloud backend work)
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §3.2 (cloud backend new files)
- `src/cloud/FccMiddleware.Adapter.Doms/` — reference project structure
- `src/cloud/FccMiddleware.Domain/Interfaces/IFccAdapter.cs` — cloud adapter interface

**Task:**
Create the Radix cloud adapter project structure.

**Detailed instructions:**
1. Create `src/cloud/FccMiddleware.Adapter.Radix/` project:
   - `FccMiddleware.Adapter.Radix.csproj` — reference `FccMiddleware.Domain`
   - `RadixCloudAdapter.cs` — stub implementing cloud `IFccAdapter`
   - `Internal/RadixTransactionParser.cs` — XML parsing for push-received transactions (can reuse or reference edge `RadixXmlParser` logic)
   - `Internal/RadixSignatureValidator.cs` — verify incoming push signatures
2. Create `src/cloud/FccMiddleware.Adapter.Radix.Tests/` project:
   - `RadixNormalizationTests.cs`
   - `RadixValidationTests.cs`
3. Add project reference in `FccMiddleware.sln`
4. Register in cloud `FccAdapterFactory` for `FccVendor.RADIX` (replace the stub from RX-0.2)

**Acceptance criteria:**
- Project compiles and is referenced in the solution
- Factory resolves `FccVendor.RADIX` → `RadixCloudAdapter`
- Stub methods throw `NotImplementedException`

---

### RX-6.2: Cloud Adapter — NormalizeTransaction & ValidatePayload

**Sprint:** 6–7
**Component:** Cloud Backend (.NET)
**Prereqs:** RX-6.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §4 (field mapping), §2.8 (dedup key), §2.9 (fiscal data)
- `src/cloud/FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs` — DOMS cloud normalization for reference pattern
- `src/cloud/FccMiddleware.Domain/Models/Adapter/` — cloud canonical models

**Task:**
Implement `RadixCloudAdapter.NormalizeTransaction()` and `ValidatePayload()` for the cloud side. The cloud adapter must handle TWO paths: (1) pre-normalized canonical data uploaded by Edge Agents, and (2) raw XML from FDCs in CLOUD_DIRECT mode.

**Detailed instructions:**
1. **`ValidatePayload(RawPayloadEnvelope)`:**
   - Check `envelope.Vendor == FccVendor.RADIX`
   - **Path A — Edge-uploaded canonical (ContentType = "application/json"):**
     - Parse JSON, validate required canonical fields are present
     - Skip vendor-specific normalization (already canonical)
     - Return `ValidationResult.Ok()`
   - **Path B — Direct XML push (ContentType = "Application/xml"):**
     - Attempt XML parse — if invalid XML, return `ValidationResult.Fail("INVALID_XML", ..., recoverable: false)`
     - Check for `<TRN>` element — if missing, return `ValidationResult.Fail("MISSING_TRANSACTION", ...)`
     - If signature validation is configured for this site, verify the signature using `RadixSignatureValidator`
   - Return `ValidationResult.Ok()` on success

2. **`NormalizeTransaction(RawPayloadEnvelope)`:**
   - **Path A — Edge-uploaded canonical:** Mostly passthrough — deserialize, verify/enrich with `legalEntityId` from site config, validate currency matches config
   - **Path B — Direct XML:** Same field mapping logic as edge `normalize()` (§4 mapping table)
   - Use `SiteFccConfig` for pump/nozzle mappings, product code mappings, timezone, and currency decimal places
   - Compose `fccTransactionId` = `"{FDC_NUM}-{FDC_SAVE_NUM}"`
   - Apply all conversions: volume → microlitres, amount → minor units, timestamps → UTC
   - Map `EFD_ID` → `fiscalReceiptNumber`

3. **Code reuse:** Consider extracting shared XML parsing logic into a `RadixCommon` internal class. For MVP, acceptable to duplicate with clear comments referencing the canonical mapping in §4.

**Unit tests:**
- Validate well-formed Radix XML → valid
- Validate malformed XML → invalid with `INVALID_XML`
- Validate XML without `<TRN>` → invalid with `MISSING_TRANSACTION`
- Validate edge-uploaded canonical JSON → valid
- Normalize all fields correctly (same test vectors as RX-3.3)
- Normalize with site-specific pump/nozzle/product mappings
- Normalize with timezone conversion
- Normalize edge-uploaded canonical → passthrough with enrichment

**Acceptance criteria:**
- Validation catches structural issues before normalization
- Normalization produces correct `CanonicalTransaction` matching edge adapter output
- Both edge-uploaded (canonical JSON) and direct (raw XML) paths work
- Signature validation works when configured
- All mapped fields match §4 specification

---

### RX-6.3: Cloud Push Ingress — XML Endpoint

**Sprint:** 7
**Component:** Cloud Backend (.NET)
**Prereqs:** RX-6.2, cloud ingestion pipeline exists
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §7.1 (cloud push ingress requirements), §2.12 (headers)
- Cloud API endpoint patterns in the existing codebase

**Task:**
Extend the cloud ingestion endpoint to accept Radix XML push payloads directly from FCCs in `CLOUD_DIRECT` mode.

**Detailed instructions:**
1. **Option A (preferred):** Make the existing `POST /api/v1/transactions/ingest` endpoint content-type-aware:
   - If `Content-Type: application/json` → existing DOMS JSON flow
   - If `Content-Type: Application/xml` → Radix XML flow
   - Resolve adapter from `USN-Code` header (look up registered FCC by USN code → get vendor + site)

2. **Option B:** Create a separate `POST /api/v1/transactions/ingest/radix` endpoint for XML payloads

3. **Radix XML ingestion pipeline:**
   a. Read XML body
   b. Extract `USN-Code` header → look up FCC registration to determine site and shared secret
   c. Validate signature using the site's `sharedSecret`
   d. Validate payload via `RadixCloudAdapter.ValidatePayload()`
   e. Normalize via `RadixCloudAdapter.NormalizeTransaction()`
   f. Feed into existing dedup → store → outbox pipeline
   g. **Build XML ACK response** — this is mandatory per Radix spec:
      ```xml
      <?xml version="1.0" encoding="UTF-8"?>
      <HOST_REQ>
          <REQ>
              <CMD_CODE>201</CMD_CODE>
              <CMD_NAME>SUCCESS</CMD_NAME>
              <TOKEN>{echoed_token}</TOKEN>
          </REQ>
          <SIGNATURE>{sha1_hash}</SIGNATURE>
      </HOST_REQ>
      ```
   h. Return ACK as `Content-Type: Application/xml` with HTTP 200

4. **If ingestion fails internally** (DB error, etc.), still return the XML ACK to prevent FDC retries, but log the error for internal retry/recovery

5. **USN-Code → site resolution:** Add a lookup table or query: `fcc_configs WHERE usn_code = @usnCode AND vendor = 'RADIX'` → returns site config with shared secret for signature validation

**Acceptance criteria:**
- Cloud endpoint accepts `Application/xml` content type
- USN-Code header used for FCC identification
- Signature validated against site's shared secret
- Transaction normalized and stored via standard pipeline
- XML ACK response returned to FDC
- Invalid USN-Code → 401
- Invalid signature → 401
- Malformed XML → 400 (but still XML error response for FDC)

---

### RX-6.4: Cloud Database Migration for Radix Config Fields

**Sprint:** 7
**Component:** Cloud Backend (.NET)
**Prereqs:** RX-0.1 (config model changes)
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/plans/dev-plan-doms-adapter.md` — DOMS-5.2 (DB migration pattern to follow)
- Cloud database migration patterns in the codebase

**Task:**
Add database migration for the new Radix-specific configuration fields on `SiteFccConfig` so they can be stored and pushed to edge agents.

**Detailed instructions:**
1. Create database migration adding nullable columns for Radix config fields:
   - `shared_secret` (NVARCHAR, nullable, encrypted) — SHA-1 signing password
   - `usn_code` (INT, nullable) — Unique Station Number
   - `auth_port` (INT, nullable) — External Authorization port
   - `fcc_pump_address_map` (JSONB/NVARCHAR, nullable) — pump number → (PUMP_ADDR, FP) mapping
2. `shared_secret` must follow the same encryption-at-rest pattern as the existing `api_key` field (AWS Secrets Manager credential ref)
3. Update the config push mechanism to include these fields when serving `GET /api/v1/agent/config`

**Acceptance criteria:**
- Migration runs cleanly on existing database
- All new columns are nullable (no breaking change to existing sites)
- `shared_secret` encrypted at rest (same pattern as `api_key`)
- Config push includes new fields for Radix sites
- Existing config push for non-Radix sites unaffected

---

### RX-6.5: Portal — Radix-Specific FCC Config Fields

**Sprint:** 7
**Component:** Portal (Angular)
**Prereqs:** RX-6.4
**Estimated effort:** 1.5 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-doms-adapter.md` — DOMS-5.3 (Portal config pattern to follow)
- `src/portal/src/app/features/site-config/fcc-config-form.component.ts` — existing FCC config form (already has RADIX in vendor dropdown)
- `docs/plans/agent-prompt-angular-portal.md` — Portal conventions

**Task:**
Add Radix-specific configuration fields to the Portal FCC configuration screen, shown conditionally when `fccVendor = RADIX`.

**Detailed instructions:**
1. When `fccVendor` is set to `RADIX`, show additional fields:
   - **Shared Secret** — Text input (masked/password style), label: "SHA-1 Signing Password"
   - **USN Code** — Number input with range 1-999999, label: "Unique Station Number (USN)"
   - **Auth Port** — Number input with range 1-65535, label: "Authorization Port (transaction port = Auth Port + 1)"
   - **Pump Address Map** — JSON editor or structured table input, label: "Pump Address Mapping (canonical pump → PUMP_ADDR, FP)"
     - Each row: canonical pump number (int), PUMP_ADDR (int), FP (int)
     - Add/remove row buttons
2. These fields are hidden when vendor is not RADIX
3. Add form validation:
   - USN Code: required for RADIX, range 1-999999
   - Auth Port: required for RADIX, range 1-65535
   - Shared Secret: required for RADIX (cannot be empty)
   - Pump Address Map: at least one entry required
4. Wire fields to the `SiteFccConfig` API model for save/load

**Acceptance criteria:**
- Radix-specific fields appear only when `fccVendor = RADIX`
- Shared Secret is masked (password input)
- Form validation works for all fields
- Pump Address Map supports add/remove rows
- Fields save and load correctly via API
- Non-RADIX vendor selections hide these fields
- Existing FCC config behavior unchanged for other vendors

---

## Phase 7 — VirtualLab Simulation (Sprint 7)

### RX-7.1: VirtualLab Radix FDC Simulator Profile

**Sprint:** 7
**Component:** VirtualLab (.NET)
**Prereqs:** RX-3.3 / RX-3.4, RX-4.1 / RX-4.2
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.4 (transaction XML), §2.6 (pre-auth XML), §2.2 (signing), all XML examples
- `VirtualLab/src/VirtualLab.Domain/Profiles/FccProfileContract.cs` — profile contract model
- `VirtualLab/src/VirtualLab.Infrastructure/FccProfiles/SeedProfileFactory.cs` — existing seed profile factory
- `docs/plans/agent-prompt-virtual-lab.md` — VirtualLab conventions

**Task:**
Create a Radix FDC simulator in VirtualLab for end-to-end testing. Use the existing `FccProfileContract` infrastructure where applicable, but note that Radix's dual-port HTTP/XML model may require a custom simulator beyond the generic profile factory.

**Detailed instructions:**
1. Create `VirtualLab.Infrastructure/RadixSimulator/RadixFdcSimulator.cs`:
   - `IHostedService` that starts TWO HTTP listeners: port P (auth) and port P+1 (transactions)
   - Validate incoming signatures using a configured shared secret
   - Respond with properly signed XML responses

2. **Simulated behaviors:**
   - **CMD_CODE=10 (transaction request):** Return pre-seeded transactions from a FIFO queue. Return RESP_CODE=205 when queue is empty.
   - **CMD_CODE=201 (ACK):** Remove the oldest transaction from the queue.
   - **CMD_CODE=20 (mode change):** Accept and echo back the requested mode. Track current mode.
   - **CMD_CODE=55 (product read):** Return a fixed product list.
   - **Pre-auth (AUTH_DATA):** Validate fields, respond with ACKCODE=0. Store the authorized pump/FP for later transaction generation.
   - **Pre-auth cancel (AUTH=FALSE):** Clear the authorized pump/FP.
   - **UNSOLICITED mode:** When mode=2, automatically POST transactions to a configured callback URL.

3. Seed data: pre-populate the FIFO queue with 5-10 diverse transactions covering different pump addresses, nozzles, products, amounts, and fiscal data

4. Configurable error injection: ability to return specific error codes (251, 258, 260) for testing error handling

5. Add REST API endpoints for test control:
   - `POST /api/radix-sim/start` — start simulator on configurable ports
   - `POST /api/radix-sim/stop` — stop simulator
   - `GET /api/radix-sim/status` — check simulator status
   - `POST /api/radix-sim/add-transaction` — inject a transaction into the FIFO queue
   - `POST /api/radix-sim/set-error-mode` — configure error injection

6. Register as `IHostedService` in VirtualLab `Program.cs`

**Acceptance criteria:**
- Simulator responds to all Radix commands with correctly structured and signed XML
- Pull mode (CMD_CODE=10 → ACK loop) works end-to-end
- Push mode (unsolicited POSTs) works end-to-end
- Pre-auth and cancellation work
- Error injection produces correct error responses
- End-to-end test: Edge Agent configured with Radix adapter → connects to VirtualLab simulator → fetches and normalizes transactions

---

## Phase 8 — Integration Testing & Hardening (Sprint 8)

### RX-8.1: End-to-End Integration Tests

**Sprint:** 8
**Component:** All
**Prereqs:** All previous phases
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §13 (acceptance criteria checklist)

**Task:**
Create comprehensive integration tests covering the full Radix adapter flow across both edge agent platforms and the cloud.

**Detailed instructions:**
1. **Pull mode E2E test (Kotlin):**
   - Configure Kotlin edge agent with Radix adapter pointed at VirtualLab simulator
   - Seed simulator with 5 transactions
   - Call `fetchTransactions()` → verify 5 transactions returned
   - Call `normalize()` on each → verify canonical fields
   - Verify all 5 ACKs were sent
   - Call `fetchTransactions()` again → verify empty batch (RESP_CODE=205)

2. **Pull mode E2E test (.NET Desktop):**
   - Same as above with .NET Desktop adapter

3. **Pre-auth E2E test (both platforms):**
   - Send pre-auth via `sendPreAuth()` / `SendPreAuthAsync()` → verify ACK from simulator
   - Seed a dispense transaction with matching TOKEN → fetch and normalize
   - Verify TOKEN-based correlation links pre-auth to dispense

4. **Push mode E2E test (edge):**
   - Start push listener
   - POST unsolicited transaction from VirtualLab simulator
   - Verify transaction received, parsed, and ACKed

5. **Cloud push E2E test:**
   - POST Radix XML to cloud ingestion endpoint
   - Verify transaction stored, XML ACK returned

6. **Error handling tests (both platforms):**
   - Signature mismatch → appropriate error response
   - Network timeout → graceful degradation
   - Malformed XML → non-recoverable error

7. **Cross-platform consistency tests:**
   - Same XML input → same canonical output on both Kotlin and .NET
   - Same signing inputs → same signature on both platforms

8. **Verify all §13 acceptance criteria** as a checklist

**Acceptance criteria:**
- All §13 acceptance criteria verified programmatically
- Pull mode drains FIFO correctly on both platforms
- Push mode receives and ACKs correctly
- Pre-auth → dispense correlation works via TOKEN
- Cloud XML endpoint works end-to-end
- Error handling is robust across all failure modes
- Cross-platform canonical output matches for identical inputs

---

### RX-8.2: Documentation & Open Question Resolution

**Sprint:** 8
**Component:** Documentation
**Prereqs:** All implementation phases
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §10 (open questions), §12 (risk register)

**Task:**
Document final decisions and resolve or escalate remaining open questions.

**Detailed instructions:**
1. Update the WIP plan document with:
   - Resolution status for each open question (RQ-1 through RQ-8)
   - Decisions made during implementation for §9 architectural choices
   - Any deviations from the original plan
2. Document the Radix adapter's configuration requirements (what fields are needed in config)
3. Document the pump address mapping format with examples
4. Document the product code mapping format with examples
5. Add operational notes:
   - How to configure a new Radix site
   - How to troubleshoot signature errors
   - How to monitor the FIFO buffer depth

**Acceptance criteria:**
- All open questions have a resolution or documented escalation
- Configuration format documented with examples
- Operational runbook for Radix sites
- WIP plan updated to reflect actual implementation

---

## Dependency Graph

```
Phase 0: Foundation (Sprint 1)
  RX-0.1 (Config all layers) ─────────────────────────────────┐
  RX-0.2 (Factory all layers) ← RX-0.1                       │
  RX-0.3 (acknowledgeTransactions) ← DOMS-0.3, DOMS-0.4     │
  RX-0.4 (Agent prompt doc) ─── independent                   │
                                                               │
Phase 1: Core Skeleton — Kotlin (Sprints 1-2)                │
  RX-1.1 (Kotlin scaffold) ← RX-0.1 ──┐                      │
  RX-1.2 (Kotlin signing)  ← RX-1.1   │                      │
  RX-1.3 (Kotlin DTOs)     ← RX-1.1   │ (independent)        │
  RX-1.4 (Kotlin XML build)← RX-1.2, RX-1.3                  │
  RX-1.5 (Kotlin XML parse)← RX-1.3                          │
  RX-1.6 (Kotlin heartbeat)← RX-1.4, RX-1.5                  │
                                                               │
Phase 2: Core Skeleton — .NET Desktop (Sprints 2-3)           │
  RX-2.1 (.NET scaffold)   ← RX-0.1 ──┐                      │
  RX-2.2 (.NET signing)    ← RX-2.1, RX-1.2 (reference)      │
  RX-2.3 (.NET DTOs)       ← RX-2.1, RX-1.3 (reference)      │
  RX-2.4 (.NET XML build/parse) ← RX-2.2, RX-2.3             │
  RX-2.5 (.NET heartbeat)  ← RX-2.4                          │
                                                               │
Phase 3: Transaction Fetch & Normalization (Sprints 3-4)      │
  RX-3.1 (Kotlin fetch)    ← RX-1.4, RX-1.5, RX-1.6         │
  RX-3.2 (.NET fetch)      ← RX-2.4, RX-2.5, RX-3.1 (ref)   │
  RX-3.3 (Kotlin normalize)← RX-1.3, RX-1.5, RX-0.1         │
  RX-3.4 (.NET normalize)  ← RX-2.3, RX-2.4, RX-3.3 (ref)   │
  RX-3.5 (Mode management) ← RX-3.1, RX-3.2                 │
                                                               │
Phase 4: Pre-Authorization (Sprints 4-5)                      │
  RX-4.1 (Kotlin pre-auth) ← RX-1.4, RX-1.5, RX-0.1         │
  RX-4.2 (.NET pre-auth)   ← RX-2.4, RX-4.1 (ref)           │
  RX-4.3 (TOKEN correlation)← RX-4.1, RX-3.3/RX-3.4         │
  RX-4.4 (Pump status)     ← RX-1.1, RX-2.1 (independent)   │
                                                               │
Phase 5: Push Mode (Sprints 5-6)                              │
  RX-5.1 (Push listener)   ← RX-1.5/RX-2.4, RX-3.3/RX-3.4  │
  RX-5.2 (Hybrid mode)     ← RX-5.1, RX-3.1/RX-3.2          │
                                                               │
Phase 6: Cloud Adapter & Portal (Sprints 6-7)                 │
  RX-6.1 (Cloud scaffold)  ← Cloud adapter interface exists   │
  RX-6.2 (Cloud normalize) ← RX-6.1                          │
  RX-6.3 (Cloud XML ingress)← RX-6.2, ingestion pipeline     │
  RX-6.4 (DB migration)    ← RX-0.1                          │
  RX-6.5 (Portal config)   ← RX-6.4                          │
                                                               │
Phase 7: VirtualLab (Sprint 7)                                │
  RX-7.1 (VL simulator)    ← RX-3.3/RX-3.4, RX-4.1/RX-4.2  │
                                                               │
Phase 8: Testing & Hardening (Sprint 8)                       │
  RX-8.1 (Integration tests)← All previous                   │
  RX-8.2 (Documentation)    ← All previous                   │
```

---

## Cross-Plan Dependencies (DOMS ↔ Radix)

| Radix Task | Depends On DOMS Task | Reason |
|------------|---------------------|--------|
| RX-0.3 | DOMS-0.3, DOMS-0.4 | `acknowledgeTransactions()` must be added to `IFccAdapter` before Radix can implement it |
| RX-1.1, RX-2.1 | DOMS-0.3, DOMS-0.4 | Radix adapter stubs must implement the full interface including new methods |

**Note:** DOMS-0.1 (`IFccConnectionLifecycle`) and DOMS-0.2 (`IFccEventListener`) do NOT block Radix — these are optional interfaces that Radix adapters do not implement.

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| SHA-1 signature computation doesn't match FDC exactly (whitespace, encoding) | Medium | High — all communication fails | Test against real FDC early (RX-1.2). Use spec examples as golden tests. Cross-platform signing consistency tests. |
| Currency/amount decimal interpretation is wrong | Medium | High — financial data corruption | Resolve RQ-1 before RX-3.3. Add validation logging. |
| Pump addressing model doesn't fit our two-level schema | Medium | Medium — requires schema migration | Resolve RQ-2 before RX-3.3. May need DB migration. |
| Radix FDC cannot push to cloud directly (LAN-only push) | Medium | Medium — forces RELAY mode for all Radix sites | Resolve RQ-4 before RX-6.3. |
| No real-time pump status from Radix | Confirmed | Low-Medium — degraded UI | Accepted in RX-4.4. Portal UI must handle gracefully. |
| TOKEN collision for pre-auth correlation | Low | High — incorrect matching | Tracked in RX-4.1 with 65K pool and cleanup. |
| FDC firmware variations across sites | Medium | Medium — XML format differences | Version check on connection. Alert if firmware < 3.49. |
| Cross-platform normalization inconsistency (Kotlin vs .NET) | Medium | Medium — data divergence | Cross-platform test vectors in RX-8.1. Shared XML fixtures. |
| DOMS interface changes (DOMS-0.3/0.4) delayed | Low | Medium — blocks Radix adapter stubs | DOMS Phase 0 tasks are simple; can be done independently. |

---

## Prerequisites Checklist

Before implementation begins:

- [ ] **DOMS-0.3 + DOMS-0.4 complete** — `acknowledgeTransactions()` added to `IFccAdapter` on both platforms (Critical for RX-0.3, RX-1.1, RX-2.1)
- [ ] **RQ-1 resolved** — Currency decimal handling confirmed (Critical for RX-3.3)
- [ ] **RQ-2 resolved** — Pump addressing model decided (Critical for RX-3.3, RX-4.1)
- [ ] **RQ-4 resolved** — Push endpoint capability confirmed (Important for RX-6.3)
- [ ] **Access to Radix FDC or simulator** for signature validation testing
- [ ] **FDC firmware version confirmed** ≥ 3.49
- [ ] **Cloud backend adapter interface exists** — needed for RX-6.x tasks
- [ ] **Agent prompt document created** (RX-0.4) — needed before assigning any task to an AI agent

---

## Changelog

### 2026-03-12 — v2.0: Updated for DOMS Plan Alignment

**Structural changes:**
- Added "Architecture Decision: HTTP/XML Protocol Strategy" section (mirrors DOMS plan pattern)
- Added "Current Implementation Status" table (mirrors DOMS plan pattern)
- Restructured phases to support dual-platform (Kotlin Edge Agent + .NET Desktop Agent)
- Renumbered all tasks to accommodate new Kotlin tasks and additional phases
- Added Phase 6 tasks for Portal and DB migration (previously missing)
- Moved VirtualLab to Phase 7, Integration Testing to Phase 8

**New tasks added:**
- RX-0.3: Interface Compatibility — acknowledgeTransactions no-op (DOMS interface dependency)
- RX-0.4: Create Agent Prompt Document (was referenced but never existed)
- RX-1.1 through RX-1.6: Full Kotlin Edge Agent core adapter skeleton (previously only .NET)
- RX-3.1: Kotlin transaction fetch (previously only .NET)
- RX-3.3: Kotlin normalization (previously only .NET)
- RX-4.1: Kotlin pre-auth (previously only .NET)
- RX-6.4: Cloud database migration for Radix config fields (previously missing)
- RX-6.5: Portal Radix-specific config fields (previously missing)

**Updated tasks:**
- RX-0.1: Expanded from .NET Desktop-only to all 3 layers (Kotlin AgentFccConfig, .NET FccConnectionConfig, Cloud SiteFccConfig). Fixed: FccVendor.RADIX already exists in Kotlin/Cloud enums, only needs adding to .NET Desktop.
- RX-0.2: Expanded from 2 factories to all 3 (added Kotlin FccAdapterFactory.kt)
- RX-2.1 (was RX-1.1): Now includes AcknowledgeTransactionsAsync returning true
- All .NET Desktop tasks: Added Kotlin reference prereq for cross-platform consistency
- RX-6.2: Added dual-path handling (edge-uploaded canonical + direct XML) per DOMS cloud adapter pattern

**Dependency updates:**
- Added cross-plan dependencies section (DOMS-0.3/0.4 → Radix RX-0.3)
- Updated prerequisite checklist to include DOMS interface tasks
- Updated risk register with cross-platform consistency risk

### 2026-03-12 — v1.0: Initial Version
- Original plan created from `WIP-RadixFCCAdapterPlan.md`
- 7 phases, 22 tasks (all targeting .NET Desktop Agent only)
