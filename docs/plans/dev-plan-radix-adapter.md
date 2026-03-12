# Radix FCC Adapter — Phased Development Plan

**Agent System Prompt:** Always prepend `docs/plans/agent-prompt-radix-adapter.md` when assigning any task below.

**Reference Document:** `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — the Radix protocol deep dive and integration analysis. Every task below references sections of that document.

**Sprint Cadence:** 2-week sprints

---

## Phase 0 — Shared Infrastructure & Config Changes (Sprint 1)

### RX-0.1: FccConnectionConfig & Enum Extensions

**Sprint:** 1
**Prereqs:** None
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §3.3 (what needs to be modified), §3.4 (configuration changes)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/Enums.cs` — current `FccVendor` enum (only has `Doms`)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccAdapter.cs` — edge adapter interface
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs` — current shared types including `FccConnectionConfig`, `PreAuthCommand`

**Task:**
Extend the shared adapter infrastructure to support Radix-specific configuration and addressing.

**Detailed instructions:**
1. Add `Radix` to the edge `FccVendor` enum in `Enums.cs`
2. Extend `FccConnectionConfig` with new fields needed by Radix:
   - `SharedSecret` (string) — SHA-1 signing password for message authentication
   - `UsnCode` (int) — Unique Station Number (1–999999), sent as `USN-Code` HTTP header
   - `AuthPort` (int?) — External Authorization port; transaction port is derived as `AuthPort + 1`
   - `FccPumpAddressMap` (dictionary or JSON) — maps canonical pump numbers to Radix `(PUMP_ADDR, FP)` pairs for the three-level addressing model
3. Extend `PreAuthCommand` with optional customer data fields (needed for Radix `<AUTH_DATA>` per §2.6):
   - `CustomerTaxId` (string?) — maps to Radix `<CUSTID>` when `CUSTIDTYPE=1`
   - `CustomerName` (string?) — maps to Radix `<CUSTNAME>`
   - `CustomerIdType` (int?) — maps to Radix `<CUSTIDTYPE>` (1=TIN, 2=DrivingLicense, 3=VotersNumber, 4=Passport, 5=NID, 6=NIL)
   - `CustomerPhone` (string?) — maps to Radix `<MOBILENUM>`
4. Verify `RawPayloadEnvelope.RawJson` can store XML content as a string (per §9.1 — Option B for MVP: store XML in `RawJson` field without rename)
5. Ensure all new fields on `FccConnectionConfig` are nullable/optional so that existing DOMS configurations are unaffected

**Acceptance criteria:**
- `FccVendor.Radix` is available in the edge enum
- `FccConnectionConfig` has all Radix-specific fields as optional properties
- `PreAuthCommand` has customer data fields (all nullable)
- Existing DOMS adapter code compiles and tests pass without changes
- No breaking changes to any existing adapter interface or shared type

---

### RX-0.2: Radix Adapter Factory Registration

**Sprint:** 1
**Prereqs:** RX-0.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs` — current factory (switch on `FccVendor`)
- `src/cloud/FccMiddleware.Infrastructure/Adapters/FccAdapterFactory.cs` — cloud factory (registry-based pattern)

**Task:**
Register the Radix adapter in both edge and cloud adapter factories. Wire up DI for the Radix adapter's dependencies.

**Detailed instructions:**
1. In the edge `FccAdapterFactory.Create()`, add a case for `FccVendor.Radix` → `new RadixAdapter(httpFactory, config, logger)` (the `RadixAdapter` class will be created in RX-1.1; for now, add the case and leave it throwing `NotImplementedException` until the class exists)
2. In the cloud `FccAdapterFactory`, register the `RadixCloudAdapter` for `FccVendor.RADIX` (similarly, stub with `NotImplementedException` until RX-5.1)
3. Add the new Radix project references to the factory projects' `.csproj` files once the projects exist (Phase 1 and Phase 5)

**Acceptance criteria:**
- Factory switch/registry includes Radix vendor
- Attempting to create a Radix adapter throws `NotImplementedException` (temporary until Phase 1 completes)
- Existing DOMS path is unaffected
- Unit test verifies factory recognizes `FccVendor.Radix`

---

## Phase 1 — Core Adapter Skeleton (Sprints 1–2)

### RX-1.1: Radix Directory Structure & Project Scaffold

**Sprint:** 1
**Prereqs:** RX-0.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §3.2 (new files list)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/` — reference directory structure to follow

**Task:**
Create the Radix adapter directory structure and empty class files following the established DOMS pattern.

**Detailed instructions:**
1. Create directory `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/`
2. Create empty/stub files:
   - `RadixAdapter.cs` — class implementing `IFccAdapter` with all methods throwing `NotImplementedException`
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
- `RadixAdapter` implements `IFccAdapter` with `NotImplementedException` stubs
- `dotnet build` succeeds with zero errors

---

### RX-1.2: SHA-1 Message Signing — RadixSignatureHelper

**Sprint:** 1
**Prereqs:** RX-1.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.2 (message signing, critical implementation notes)
- §2.4, §2.6 — examples showing where signatures appear in request/response XML

**Task:**
Implement the SHA-1 message signing utility that matches the Radix FDC's exact expectations. This is the most critical low-level building block — if signatures don't match character-for-character, **all Radix communication fails**.

**Detailed instructions:**
1. Create `RadixSignatureHelper` as a static utility class with methods:
   - `ComputeTransactionSignature(string reqContent, string sharedSecret) → string` — computes `SHA1(<REQ>...</REQ> + SECRET_PASSWORD)` for transaction management (port P+1). The input is the full content between and including `<REQ>` and `</REQ>` tags, concatenated with the shared secret **immediately** after `</REQ>` with no space.
   - `ComputeAuthSignature(string authDataContent, string sharedSecret) → string` — computes `SHA1(<AUTH_DATA>...</AUTH_DATA> + SECRET_PASSWORD)` for external authorization (port P). Same concatenation rule.
   - `ValidateSignature(string content, string expectedSignature, string sharedSecret) → bool` — verifies a response signature from the FDC
2. SHA-1 output should be lowercase hex string (40 characters)
3. Use `System.Security.Cryptography.SHA1` — encode the input string as UTF-8 bytes before hashing
4. **Critical:** Whitespace and special characters in the XML content matter — the hash must match character-for-character. Do NOT trim, normalize, or reformat the XML before signing.
5. Add XML doc comments explaining the signing protocol for each method

**Unit tests (in `RadixSignatureHelperTests.cs`):**
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

### RX-1.3: Radix Protocol DTOs — RadixProtocolDtos

**Sprint:** 1
**Prereqs:** RX-1.1
**Estimated effort:** 0.5–1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.4 (TRN element attributes), §2.5 (unsolicited TRN), §2.6 (AUTH_DATA/FDCACK), §2.11 (RFID_CARD, DISCOUNT), §2.12 (custom headers)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsProtocolDtos.cs` — DOMS DTO pattern to follow

**Task:**
Create all Radix-specific data transfer objects (DTOs) for parsed XML data.

**Detailed instructions:**
1. Create the following record types in `RadixProtocolDtos.cs`:

   **Transaction data:**
   - `RadixTransactionData` — all `<TRN>` attributes: `Amo`, `EfdId`, `FdcDate`, `FdcTime`, `FdcName`, `FdcNum`, `FdcProd`, `FdcProdName`, `FdcSaveNum`, `FdcTank`, `Fp`, `Noz`, `Price`, `PumpAddr`, `RdgDate`, `RdgTime`, `RdgId`, `RdgIndex`, `RdgProd`, `RdgSaveNum`, `RegId`, `RoundType`, `Vol`
   - `RadixRfidCardData` — `<RFID_CARD>` attributes: `CardType`, `CustContact`, `CustId`, `CustIdType`, `CustName`, `Discount`, `DiscountType`, `Num`, `Num10`, `PayMethod`, `ProductEnabled`, `Used`
   - `RadixDiscountData` — `<DISCOUNT>` attributes: `AmoDiscount`, `AmoNew`, `AmoOrigin`, `DiscountType`, `PriceDiscount`, `PriceNew`, `PriceOrigin`, `VolOrigin`
   - `RadixCustomerData` — `<CUST_DATA>`: `Used` (int)

   **Response envelopes:**
   - `RadixTransactionResponse` — parsed `<FDC_RESP>`: `RespCode` (int), `RespMsg` (string), `Token` (string), `Transaction` (RadixTransactionData?), `RfidCard` (RadixRfidCardData?), `Discount` (RadixDiscountData?), `CustomerData` (RadixCustomerData?), `Signature` (string)
   - `RadixAuthResponse` — parsed `<FDCMS><FDCACK>`: `Date` (string), `Time` (string), `AckCode` (int), `AckMsg` (string), `Signature` (string)
   - `RadixProductData` — product item from CMD_CODE=55 response: `Id` (int), `Name` (string), `Price` (string)
   - `RadixProductResponse` — list of products with `RespCode`, `RespMsg`

   **Request parameters (for builder input):**
   - `RadixPreAuthParams` — `Pump` (int), `Fp` (int), `Authorize` (bool), `Product` (int), `PresetVolume` (string), `PresetAmount` (string), `CustomerName` (string?), `CustomerIdType` (int?), `CustomerId` (string?), `MobileNumber` (string?), `DiscountValue` (int?), `DiscountType` (string?), `Token` (string)
   - `RadixModeChangeParams` — `Mode` (int: 0=OFF, 1=ON_DEMAND, 2=UNSOLICITED), `Token` (string)

2. All DTOs should be C# `record` types (immutable)
3. Use string types for all Radix decimal values (`Amo`, `Vol`, `Price`) — conversion to long happens during normalization, not in DTOs
4. Use string types for date/time fields — parsing happens during normalization

**Acceptance criteria:**
- All DTOs cover every field from the Radix spec XML examples
- DTOs are immutable records
- Radix decimal fields remain as strings (no premature conversion)
- Compiles cleanly with no warnings

---

### RX-1.4: XML Request Builder — RadixXmlBuilder

**Sprint:** 2
**Prereqs:** RX-1.2, RX-1.3
**Estimated effort:** 1–1.5 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.3 (mode change CMD_CODE=20), §2.4 (transaction request CMD_CODE=10, ACK CMD_CODE=201), §2.6 (pre-auth AUTH_DATA), §2.12 (custom headers)
- Appendix B (header quick reference)

**Task:**
Implement the XML request body builder for all Radix host-to-FDC commands. Each builder method must produce correctly structured XML and compute the SHA-1 signature.

**Detailed instructions:**
1. Create `RadixXmlBuilder` with the following static methods:

   **Transaction Management (Port P+1) — `<HOST_REQ>` envelope:**
   - `BuildTransactionRequest(string token, string secret) → string` — CMD_CODE=10, CMD_NAME=TRN_REQ
   - `BuildTransactionAck(string token, string secret) → string` — CMD_CODE=201, CMD_NAME=SUCCESS
   - `BuildModeChangeRequest(int mode, string token, string secret) → string` — CMD_CODE=20, CMD_NAME=MODE_CHANGE, with `<MODE>` element inside `<REQ>`
   - `BuildProductReadRequest(string token, string secret) → string` — CMD_CODE=55, CMD_NAME=PRODUCT_REQ

   **External Authorization (Port P) — `<FDCMS>` envelope:**
   - `BuildPreAuthRequest(RadixPreAuthParams params, string secret) → string` — `<AUTH_DATA>` with all fields from `RadixPreAuthParams`
   - `BuildPreAuthCancelRequest(int pump, int fp, string token, string secret) → string` — same structure with `<AUTH>FALSE</AUTH>`

2. XML must be built using `System.Xml.Linq` (XDocument/XElement) or string formatting — whichever produces output that exactly matches the Radix spec examples (whitespace matters for signing)
3. **Critical signing order:** Build the inner content (`<REQ>...</REQ>` or `<AUTH_DATA>...</AUTH_DATA>`) FIRST, convert to string, compute signature using `RadixSignatureHelper`, THEN wrap in outer envelope with `<SIGNATURE>` or `<FDCSIGNATURE>` element
4. Add a helper method `BuildHttpHeaders(int usnCode, string operation) → Dictionary<string, string>` that returns the required custom headers: `Content-Type: Application/xml`, `USN-Code: {usnCode}`, `Operation: {operation}`

**Unit tests (in `RadixXmlBuilderTests.cs`):**
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

### RX-1.5: XML Response Parser — RadixXmlParser

**Sprint:** 2
**Prereqs:** RX-1.3
**Estimated effort:** 1–1.5 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.4 (transaction response XML examples), §2.5 (unsolicited response), §2.6 (auth response), Appendix A (all response/error codes)

**Task:**
Implement the XML response parser for all Radix FDC responses. The parser must handle both success and error responses, and validate signatures on incoming messages.

**Detailed instructions:**
1. Create `RadixXmlParser` with the following static methods:

   **Transaction responses (`<FDC_RESP>`):**
   - `ParseTransactionResponse(string xml) → RadixTransactionResponse` — parse `<ANS>` for RESP_CODE/RESP_MSG/TOKEN, `<TRN>` attributes, `<RFID_CARD>` attributes, `<DISCOUNT>` attributes, `<CUST_DATA>` attributes, and `<SIGNATURE>`
   - Handle RESP_CODE=201 (success with TRN data), RESP_CODE=205 (no transaction — return response with null Transaction), RESP_CODE=30 (unsolicited push), and error codes (206, 251, 253, 255)

   **Auth responses (`<FDCMS>`):**
   - `ParseAuthResponse(string xml) → RadixAuthResponse` — parse `<FDCACK>` for DATE, TIME, ACKCODE, ACKMSG, and `<FDCSIGNATURE>`
   - Handle ACKCODE=0 (success) and error codes (251, 255, 256, 258, 260)

   **Product responses (`<FDC_RESP>` with product data):**
   - `ParseProductResponse(string xml) → RadixProductResponse` — parse product list from CMD_CODE=55 response

   **Signature validation:**
   - `ValidateTransactionResponseSignature(string xml, string sharedSecret) → bool` — extract content between `<TABLE>...</TABLE>`, validate against `<SIGNATURE>` using `RadixSignatureHelper`
   - `ValidateAuthResponseSignature(string xml, string sharedSecret) → bool` — extract content, validate against `<FDCSIGNATURE>`

2. Use `System.Xml.Linq` for parsing
3. Handle missing/empty attributes gracefully (many `<TRN>` attributes may be empty strings)
4. All `<TRN>` attribute values should be stored as strings in `RadixTransactionData` — type conversion happens during normalization

**Test fixtures (in `Fixtures/` directory):**
- `transaction-success.xml` — RESP_CODE=201 with full TRN data (copy from spec §2.4)
- `transaction-empty.xml` — RESP_CODE=205, no transaction
- `transaction-unsolicited.xml` — RESP_CODE=30 (push mode)
- `transaction-signature-error.xml` — RESP_CODE=251
- `auth-success.xml` — ACKCODE=0
- `auth-pump-not-ready.xml` — ACKCODE=258
- `auth-dsb-offline.xml` — ACKCODE=260
- `auth-signature-error.xml` — ACKCODE=251
- `products-success.xml` — product list response

**Unit tests (in `RadixXmlParserTests.cs`):**
- Parse transaction success → all TRN attributes populated correctly
- Parse transaction empty → Transaction is null, RespCode is 205
- Parse unsolicited → RespCode is 30, TRN data present
- Parse auth success → AckCode is 0, AckMsg is "Success"
- Parse auth errors → correct AckCode for each error type
- Parse with missing optional attributes → no exception, fields are null/empty
- Signature validation passes for correctly signed response
- Signature validation fails for tampered response
- Parse malformed XML → throws `FccAdapterException` with `IsRecoverable = false`

**Acceptance criteria:**
- All response types parsed correctly from spec-based fixtures
- Error codes (201, 205, 30, 206, 251, 253, 255, 256, 258, 260) handled
- Missing attributes don't cause exceptions
- Signature validation works for both transaction and auth responses
- Malformed XML produces a non-recoverable `FccAdapterException`
- All unit tests pass

---

### RX-1.6: Heartbeat Implementation

**Sprint:** 2
**Prereqs:** RX-1.4, RX-1.5
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §8 (heartbeat strategy — CMD_CODE=55 product read)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsAdapter.cs` — DOMS `HeartbeatAsync` implementation (reference for timeout/error handling pattern)

**Task:**
Implement `RadixAdapter.HeartbeatAsync()` using the product/price read command (CMD_CODE=55) as a liveness probe. This is the simplest end-to-end Radix communication path and serves as the first integration point.

**Detailed instructions:**
1. In `RadixAdapter`, implement `HeartbeatAsync(CancellationToken)`:
   - Build XML request using `RadixXmlBuilder.BuildProductReadRequest(token, secret)`
   - Build headers using `RadixXmlBuilder.BuildHttpHeaders(usnCode, "2")` (Operation=2 for products)
   - POST to `http://{host}:{transactionPort}` where `transactionPort = authPort + 1`
   - Parse response using `RadixXmlParser.ParseProductResponse(xml)`
   - Return `true` if `RESP_CODE=201`, `false` otherwise
2. Apply the IFccAdapter heartbeat contract: 5-second hard timeout, never throw on FCC unreachability — return `false`
3. Catch and log transport errors (network, timeout) — return `false`
4. Catch and log signature errors (RESP_CODE=251) — return `false` but log as WARNING (config issue, not transient)
5. Use a named `HttpClient` from `IHttpClientFactory` (client name: `"fcc"`, same as DOMS)
6. Generate a sequential token for each heartbeat request (simple counter, wrap at 65535)

**Unit tests (in `RadixAdapterTests.cs`):**
- Successful heartbeat (mock returns RESP_CODE=201) → returns `true`
- FCC unreachable (mock throws `HttpRequestException`) → returns `false`
- Timeout (mock throws `TaskCanceledException`) → returns `false`
- Signature error (mock returns RESP_CODE=251) → returns `false`, logged as warning
- Bad XML response → returns `false`

**Acceptance criteria:**
- Heartbeat uses CMD_CODE=55 on port P+1
- Returns `true` only on RESP_CODE=201
- Never throws — always returns `bool`
- 5-second timeout enforced
- Signature error logged as warning (distinct from transient failures)

---

## Phase 2 — Transaction Fetch & Normalization (Sprints 2–3)

### RX-2.1: Transaction Fetch — Pull Mode (CMD_CODE=10 Loop)

**Sprint:** 2–3
**Prereqs:** RX-1.4, RX-1.5, RX-1.6
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.3 (mode change), §2.4 (transaction fetch + ACK loop), §9.4 (FIFO vs cursor), §9.5 (mode management lifecycle)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs` — `TransactionBatch`, `FetchCursor`

**Task:**
Implement `RadixAdapter.FetchTransactionsAsync()` with the Radix-specific FIFO drain loop: request one transaction → ACK → request next → repeat until buffer empty or limit reached.

**Detailed instructions:**
1. **Mode management on startup:** The adapter must ensure the FDC is in ON_DEMAND mode (mode=1) before fetching. Add an internal method `EnsureModeAsync(int mode, CancellationToken)`:
   - Send CMD_CODE=20 with `<MODE>1</MODE>` (ON_DEMAND)
   - Parse response, verify RESP_CODE=201
   - Cache the mode state — only re-send if mode is unknown (first call) or if FCC connectivity was lost and restored
   - Log mode change at INFO level

2. **Fetch loop in `FetchTransactionsAsync`:**
   - Call `EnsureModeAsync(1, ct)` — ensure ON_DEMAND mode
   - Initialize empty list for collected transactions
   - **Loop** (max iterations = `FetchCursor.Limit` or configurable default, e.g., 100):
     a. Build CMD_CODE=10 request via `RadixXmlBuilder.BuildTransactionRequest(token, secret)`
     b. POST to `http://{host}:{transactionPort}` with Operation=1 header
     c. Parse response via `RadixXmlParser.ParseTransactionResponse(xml)`
     d. If `RESP_CODE=205` (NO TRN AVAILABLE) → break loop, buffer is empty
     e. If `RESP_CODE=201` (SUCCESS) → add transaction to list, then send ACK:
        - Build CMD_CODE=201 ACK via `RadixXmlBuilder.BuildTransactionAck(token, secret)`
        - POST ACK to same port
        - Parse ACK response — log warning if ACK fails but continue
     f. If error code → log and break loop
   - Wrap collected transactions as `RawPayloadEnvelope` objects inside a `TransactionBatch`
   - Set `TransactionBatch.HasMore = true` if loop hit the limit (buffer may have more), `false` if RESP_CODE=205 was received
   - `TransactionBatch.NextCursorToken` = `"continue"` if `HasMore`, `null` otherwise (Radix FIFO has no cursor — the "cursor" is implicit buffer position)

3. **Token generation:** Maintain an internal counter for TOKEN values (0–65535, wrapping). Each request/ACK pair uses the same TOKEN value.

4. **Error handling:**
   - Network errors → return empty batch, do not throw
   - RESP_CODE=251 (signature error) → throw `FccAdapterException(IsRecoverable: false)` — this is a config problem
   - RESP_CODE=253 (token error) → log warning, retry with new token
   - RESP_CODE=206 (mode error) → re-send mode change, retry

5. Store each fetched transaction's raw XML as the `RawPayloadEnvelope.RawJson` content (the XML string goes into the `RawJson` field per §9.1 Option B)

**Unit tests (in `RadixAdapterTests.cs`):**
- Fetch with 3 available transactions → returns batch of 3, each ACKed
- Fetch with 0 transactions (RESP_CODE=205 on first request) → returns empty batch
- Fetch hits limit (e.g., 2) with more available → `HasMore = true`
- ACK failure after successful fetch → transaction still in batch, warning logged
- Network error mid-loop → returns partial batch collected so far
- Mode change is sent on first fetch call
- Mode change is not re-sent on subsequent calls (cached)

**Acceptance criteria:**
- Fetch loop correctly drains FIFO buffer one transaction at a time
- Each transaction is ACKed before requesting the next
- Mode management ensures ON_DEMAND (mode=1) is set
- `HasMore` accurately reflects buffer state
- Token counter wraps at 65535
- Raw XML preserved in `RawPayloadEnvelope`
- Error handling follows the IFccAdapter contract (no throws on transient failures)

---

### RX-2.2: Transaction Normalization — NormalizeAsync

**Sprint:** 3
**Prereqs:** RX-1.3, RX-1.5, RX-0.1
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §4 (complete field mapping table), §2.7 (pump addressing), §2.8 (transaction ID / dedup key), §2.9 (fiscal data)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/CanonicalTransaction.cs` — target canonical model
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsAdapter.cs` — DOMS `NormalizeAsync` for reference pattern

**Task:**
Implement `RadixAdapter.NormalizeAsync()` — parse a raw Radix XML payload and map it to a `CanonicalTransaction` following the field mapping in §4.

**Detailed instructions:**
1. In `NormalizeAsync(RawPayloadEnvelope envelope, CancellationToken ct)`:
   - Extract XML from `envelope.RawJson` (it's XML stored as string per §9.1 Option B)
   - Parse using `RadixXmlParser.ParseTransactionResponse(xml)`
   - If no transaction data (RESP_CODE=205), throw `FccAdapterException("No transaction in payload", IsRecoverable: false)`

2. **Field mappings (from §4):**
   - `FccTransactionId` = `"{FDC_NUM}-{FDC_SAVE_NUM}"` (composed dedup key per §2.8)
   - `SiteCode` = from `envelope.SiteCode` (injected from config)
   - `PumpNumber` = map `PUMP_ADDR` via pump mapping table: `fcc_pump_number → canonical pump_number`. For MVP, use simple lookup from `FccConnectionConfig.FccPumpAddressMap`. **If pump mapping not found, use raw `PUMP_ADDR` value and log warning.**
   - `NozzleNumber` = `NOZ` parsed as int (nozzle mapping follows same pattern as pump)
   - `ProductCode` = map `FDC_PROD` via product code mapping from config. If not found, use `FDC_PROD_NAME` as fallback.
   - `VolumeMicrolitres` = parse `VOL` as decimal, multiply by 1,000,000, cast to long. E.g., `"15.54"` → `15_540_000L`
   - `AmountMinorUnits` = parse `AMO` as decimal, multiply by 100 (assuming major→minor for currencies with 2 decimal places). **For TZS (0 decimal places), no multiplication needed.** Use a configurable `currencyDecimalPlaces` from site config (default: 0 for TZS). See Open Question RQ-1.
   - `UnitPriceMinorPerLitre` = parse `PRICE` as decimal, apply same currency conversion
   - `StartedAt` = parse `FDC_DATE` + `FDC_TIME` as `"yyyy-MM-dd"` + `"HH:mm:ss"`, apply configured timezone from site config → convert to UTC `DateTimeOffset`
   - `CompletedAt` = parse `RDG_DATE` + `RDG_TIME` same way
   - `FiscalReceiptNumber` = `EFD_ID` (direct mapping; null/empty → null)
   - `FccVendor` = `"RADIX"` (hardcoded)
   - `AttendantId` = `null` (not provided by Radix)
   - `SchemaVersion` = `"1.0"`

3. **Volume conversion precision:** Use `decimal` arithmetic for the multiplication to avoid floating-point precision loss (same pattern as DOMS adapter: `(long)(decimal.Parse(vol) * 1_000_000m)`)

4. **Timezone handling:** The adapter must know the site's timezone (from `FccConnectionConfig` or site config) to correctly convert FDC local times to UTC. Default to UTC if not configured, with a warning log.

5. **Raw payload preservation:** The full XML response (including RFID_CARD, DISCOUNT, CUST_DATA) is already stored in `RawPayloadEnvelope.RawJson` — no additional work needed for audit trail.

**Unit tests (in `RadixAdapterTests.cs`):**
- Normalize standard transaction → all fields mapped correctly
- Volume conversion: `"15.54"` → `15_540_000L`
- Amount conversion with 0 decimal places (TZS): `"30000.0"` → `3000000L` (assuming minor = major * 100? Or major = minor for TZS? **Follow config**)
- FccTransactionId composition: `"100253410"` + `"368989"` → `"100253410-368989"`
- Fiscal receipt mapping: `"182AC9368989"` → `FiscalReceiptNumber`
- Empty `EFD_ID` → null `FiscalReceiptNumber`
- Timestamp conversion with East Africa timezone (UTC+3)
- Missing pump mapping → uses raw PUMP_ADDR, warning logged
- Missing product mapping → uses FDC_PROD_NAME, warning logged
- Empty/null VOL or AMO → throws `FccAdapterException(IsRecoverable: false)`

**Acceptance criteria:**
- All §4 field mappings implemented correctly
- Dedup key composed from `FDC_NUM` + `FDC_SAVE_NUM`
- Volume in microlitres (long), amount in minor units (long) — no floating point
- Timezone conversion applied
- Fiscal receipt extracted
- Missing mappings degrade gracefully with warnings (not exceptions)
- All unit tests pass

---

### RX-2.3: Mode Management Lifecycle

**Sprint:** 3
**Prereqs:** RX-2.1
**Estimated effort:** 0.5–1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.3 (mode change command), §9.5 (mode management lifecycle)

**Task:**
Implement robust mode management for the Radix adapter — ensuring the FDC is always in the correct transaction mode.

**Detailed instructions:**
1. Add a `_currentMode` state field to `RadixAdapter` (nullable int — null means unknown)
2. Implement `EnsureModeAsync(int desiredMode, CancellationToken)`:
   - If `_currentMode == desiredMode`, return immediately (no-op)
   - Build CMD_CODE=20 mode change request
   - POST to transaction port (P+1), Operation=1
   - Parse response:
     - RESP_CODE=201 → set `_currentMode = desiredMode`, log at INFO
     - RESP_CODE=251 → throw non-recoverable (signature config issue)
     - Other error → log warning, set `_currentMode = null` (force retry next time)
3. Add `ResetModeState()` internal method — called when connectivity is lost/restored to force a mode re-send on next operation
4. Integrate into `FetchTransactionsAsync` (already done in RX-2.1) — call `EnsureModeAsync(1, ct)` (ON_DEMAND)
5. Integrate into push mode setup (Phase 4) — call `EnsureModeAsync(2, ct)` (UNSOLICITED)
6. On adapter disposal/shutdown: optionally send `EnsureModeAsync(0, ct)` (OFF) — best-effort, do not throw if it fails

**Acceptance criteria:**
- Mode is set to ON_DEMAND before first fetch
- Mode change is not re-sent unnecessarily (cached)
- Connectivity loss resets cached mode state
- Mode change errors are logged but don't crash the adapter
- Shutdown sends OFF mode (best-effort)

---

## Phase 3 — Pre-Authorization (Sprints 3–4)

### RX-3.1: SendPreAuthAsync Implementation

**Sprint:** 3–4
**Prereqs:** RX-1.4, RX-1.5, RX-0.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.6 (external authorization), §5 (pre-auth field mapping), §6 (response mapping), §2.7 (pump addressing for pre-auth), §9.3 (TOKEN correlation)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs` — `PreAuthCommand`, `PreAuthResult`

**Task:**
Implement `RadixAdapter.SendPreAuthAsync()` — build the Radix XML authorization request, send it to port P, parse the response, and map to `PreAuthResult`.

**Detailed instructions:**
1. **Pump address resolution:** The `PreAuthCommand.FccPumpNumber` is the canonical pump number. The adapter must resolve it to `(PUMP_ADDR, FP)` using `FccConnectionConfig.FccPumpAddressMap`. If mapping not found, log error and return `PreAuthResult(Accepted: false, ErrorCode: "PUMP_MAPPING_NOT_FOUND")`.

2. **Product code reverse-mapping:** The `PreAuthCommand.ProductCode` (e.g., "PMS") must be reverse-mapped to the Radix product index (e.g., 0). Use the product code mapping from config. If `ProductCode` is null/empty, use `0` (all products allowed). If mapping not found, log error and return failure.

3. **TOKEN generation and tracking (§9.3):**
   - Maintain a `ConcurrentDictionary<int, ActivePreAuth>` keyed by TOKEN value
   - Generate the next available TOKEN (0–65535), skipping values already in the active map
   - Store the mapping: `TOKEN → { odooOrderId, pumpNumber, issuedAt }`
   - Clean up expired entries (older than 30 minutes) periodically
   - If TOKEN pool is exhausted (extremely unlikely — 65K concurrent pre-auths), return failure with `ErrorCode: "TOKEN_POOL_EXHAUSTED"`

4. **Build and send request:**
   - Create `RadixPreAuthParams` from `PreAuthCommand` fields + resolved pump/FP + TOKEN
   - Map customer data: `PreAuthCommand.CustomerTaxId` → `CUSTID`, `CustomerIdType` → `CUSTIDTYPE`, `CustomerName` → `CUSTNAME`, `CustomerPhone` → `MOBILENUM`
   - Convert `RequestedAmountMinorUnits` → Radix decimal string (apply reverse currency conversion)
   - Volume preset = `"0.00"` (we always authorize by amount per BR-6.1b)
   - Build XML via `RadixXmlBuilder.BuildPreAuthRequest(params, secret)`
   - Build headers with `Operation: Authorize`
   - POST to `http://{host}:{authPort}` (port P, NOT P+1)

5. **Parse response and map to PreAuthResult (per §6):**
   - ACKCODE=0 → `PreAuthResult(Accepted: true, FccCorrelationId: TOKEN.ToString(), ErrorCode: null)`
   - ACKCODE=251 → `PreAuthResult(Accepted: false, ErrorCode: "SIGNATURE_ERROR")` (non-recoverable)
   - ACKCODE=255 → `PreAuthResult(Accepted: false, ErrorCode: "BAD_XML")` (non-recoverable)
   - ACKCODE=256 → `PreAuthResult(Accepted: false, ErrorCode: "BAD_HEADER")` (non-recoverable)
   - ACKCODE=258 → `PreAuthResult(Accepted: false, ErrorCode: "PUMP_NOT_READY")` (recoverable)
   - ACKCODE=260 → `PreAuthResult(Accepted: false, ErrorCode: "DSB_OFFLINE")` (recoverable)
   - On failure, remove TOKEN from active map

6. **Error handling:**
   - Network errors → return `PreAuthResult(Accepted: false, ErrorCode: "FCC_UNREACHABLE")`, remove TOKEN from active map
   - Timeout → same as network error

**Unit tests:**
- Successful pre-auth → `Accepted = true`, TOKEN stored in active map
- Pump not ready (ACKCODE=258) → `Accepted = false`, TOKEN removed
- DSB offline (ACKCODE=260) → `Accepted = false`, TOKEN removed
- Pump mapping not found → failure without sending request
- Product mapping not found → failure without sending request
- Customer data fields included when present
- Customer data fields omitted when null
- Amount conversion is correct for configured currency
- TOKEN wraps around at 65535
- Network error → failure, TOKEN removed

**Acceptance criteria:**
- Pre-auth sent to port P with correct XML structure
- Pump address resolved from canonical → (PUMP_ADDR, FP)
- Product code reverse-mapped to Radix index
- TOKEN tracking maintains active pre-auth map
- All ACKCODE error codes mapped correctly per §6
- Customer data fields properly included
- Network errors never throw — return failure result

---

### RX-3.2: Pre-Auth Cancellation — CancelPreAuthAsync

**Sprint:** 4
**Prereqs:** RX-3.1
**Estimated effort:** 0.5–1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.6 (cancellation: `<AUTH>FALSE</AUTH>`)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccAdapter.cs` — `CancelPreAuthAsync` contract (idempotent)

**Task:**
Implement `RadixAdapter.CancelPreAuthAsync()` — send a cancel authorization command to the Radix FDC.

**Detailed instructions:**
1. Look up the `fccCorrelationId` (which is the TOKEN string) in the active pre-auth map to get the pump/FP
2. If TOKEN not found in active map → return `true` (idempotent — already cancelled or expired)
3. Build cancel XML via `RadixXmlBuilder.BuildPreAuthCancelRequest(pump, fp, token, secret)`
4. POST to port P with `Operation: Authorize` header
5. Parse `<FDCACK>` response:
   - ACKCODE=0 → remove TOKEN from active map, return `true`
   - ACKCODE=258 (pump not ready — may already be idle) → remove TOKEN, return `true` (treat as already cancelled)
   - Other errors → log warning, return `false`
6. Network error → log warning, return `false`

**Acceptance criteria:**
- Cancel sends `AUTH=FALSE` to correct pump/FP
- Already-cancelled pre-auth returns `true` (idempotent)
- TOKEN removed from active map on success
- Network errors return `false` without throwing

---

### RX-3.3: Pre-Auth ↔ Dispense Correlation via TOKEN

**Sprint:** 4
**Prereqs:** RX-3.1, RX-2.2
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §9.3 (TOKEN correlation mechanism)

**Task:**
Implement the TOKEN-based correlation between pre-auth commands and their resulting dispense transactions. When a transaction is normalized, check if its TOKEN matches an active pre-auth to link them.

**Detailed instructions:**
1. In `NormalizeAsync`, after parsing the transaction:
   - Extract the TOKEN from `<ANS TOKEN="...">` in the transaction response
   - Look up TOKEN in the active pre-auth map
   - If found: set `CanonicalTransaction.FccCorrelationId` = TOKEN string, and `CanonicalTransaction.OdooOrderId` = the `odooOrderId` from the active pre-auth record
   - Remove the TOKEN from the active map (pre-auth is now completed)
   - If not found: `FccCorrelationId` = TOKEN string (for potential later matching), `OdooOrderId` = null

2. Add a `ResolvePreAuthCorrelation(int token) → ActivePreAuth?` internal method

3. Handle the edge case where TOKEN=0 (no pre-auth — Normal Order) — skip correlation lookup

**Unit tests:**
- Transaction with TOKEN matching active pre-auth → OdooOrderId populated
- Transaction with TOKEN not in active map → OdooOrderId is null
- Transaction with TOKEN=0 → no correlation attempt
- After correlation, TOKEN is removed from active map (not reusable until cleanup)

**Acceptance criteria:**
- Pre-auth ↔ dispense transactions are linked via TOKEN
- OdooOrderId flows from pre-auth to transaction when TOKEN matches
- TOKEN=0 handled as Normal Order (no correlation)
- Active map is cleaned up after correlation

---

### RX-3.4: GetPumpStatusAsync — Limited Implementation

**Sprint:** 4
**Prereqs:** RX-1.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §3.1 (`GetPumpStatusAsync` — no dedicated endpoint), Open Question RQ-6

**Task:**
Implement `RadixAdapter.GetPumpStatusAsync()` as a no-op that returns an empty list. Radix does not expose real-time pump status.

**Detailed instructions:**
1. Return `Task.FromResult<IReadOnlyList<PumpStatus>>(Array.Empty<PumpStatus>())` — per §3.1 and RQ-6, there is no Radix pump status endpoint
2. Log at DEBUG level: "Pump status not supported by Radix FDC"
3. The adapter metadata (future) should report `supportsPumpStatus = false`

**Acceptance criteria:**
- Returns empty list without contacting FDC
- No exceptions thrown
- Debug log message present

---

## Phase 4 — Push Mode Support (Sprints 4–5)

### RX-4.1: Edge Agent Push Listener — Unsolicited Transaction Reception

**Sprint:** 4–5
**Prereqs:** RX-1.5, RX-2.2, RX-2.3
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.5 (unsolicited push format), §7.2 (edge agent push reception), §9.5 (mode management for push)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccAdapter.cs` — check if push listener is part of the adapter interface or a separate concern

**Task:**
Implement an HTTP listener in the Edge Agent that accepts unsolicited Radix transaction POSTs from the FDC in UNSOLICITED mode.

**Detailed instructions:**
1. Create `RadixPushListener` class — an HTTP server endpoint handler (NOT part of `IFccAdapter` — this is a separate infrastructure concern):
   - Listen on a configurable port accessible from the FCC's LAN (e.g., `RadixPushListenerPort` in config)
   - Accept `POST` requests with `Content-Type: Application/xml`

2. **Request processing pipeline:**
   a. Validate `USN-Code` header against configured `usnCode`
   b. Read XML body
   c. Validate signature using `RadixSignatureHelper`
   d. Parse using `RadixXmlParser.ParseTransactionResponse(xml)` — expect RESP_CODE=30 (unsolicited)
   e. Wrap in `RawPayloadEnvelope` and feed into the existing ingestion pipeline (buffer manager or ingestion orchestrator)
   f. Build ACK response: XML `<HOST_REQ>` with CMD_CODE=201, signed — per §7.1
   g. Return ACK as HTTP 200 with `Content-Type: Application/xml`

3. **If ACK is not returned**, the FDC will retry after a timeout — this is the FDC's built-in reliability mechanism. The listener must respond promptly.

4. **Mode setup:** On listener startup, call `RadixAdapter.EnsureModeAsync(2, ct)` to set UNSOLICITED mode on the FDC

5. **Error handling:**
   - Invalid USN-Code → return 401 (do not ACK — FDC will retry, and we want to prevent spoofing)
   - Invalid signature → return 401
   - Parse error → log error, return 500 (FDC will retry)
   - Ingestion pipeline error → still ACK the FDC (we received the data; internal processing failure is our problem, not the FDC's). Buffer the raw XML for retry.

6. Wire the push listener into the Edge Agent's DI and startup lifecycle

**Acceptance criteria:**
- HTTP listener accepts Radix unsolicited POSTs
- USN-Code and signature validated
- Transactions parsed and fed into ingestion pipeline
- Proper XML ACK returned to FDC
- UNSOLICITED mode set on FDC at startup
- Invalid requests rejected with appropriate status codes
- FDC retry mechanism works (no ACK → FDC re-sends)

---

### RX-4.2: Push-Mode Hybrid Support

**Sprint:** 5
**Prereqs:** RX-4.1, RX-2.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.3 (mode values), §9.5 (mode lifecycle), Open Question RQ-5

**Task:**
Implement HYBRID mode support for Radix — UNSOLICITED as primary with periodic ON_DEMAND catch-up polling.

**Detailed instructions:**
1. For sites configured with `IngestionMethod.Hybrid`:
   - Primary: Run push listener in UNSOLICITED mode (RX-4.1)
   - Catch-up: Periodically (configurable interval, e.g., every 5 minutes):
     a. Switch FDC to ON_DEMAND mode (CMD_CODE=20, MODE=1)
     b. Drain the FIFO buffer via `FetchTransactionsAsync` (RX-2.1)
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

## Phase 5 — Cloud Adapter (Sprints 5–6)

### RX-5.1: Cloud Adapter Project Scaffold

**Sprint:** 5
**Prereqs:** CB-1.1 (cloud adapter interface exists)
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
4. Register in cloud `FccAdapterFactory` for `FccVendor.RADIX`

**Acceptance criteria:**
- Project compiles and is referenced in the solution
- Factory resolves `FccVendor.RADIX` → `RadixCloudAdapter`
- Stub methods throw `NotImplementedException`

---

### RX-5.2: Cloud Adapter — NormalizeTransaction & ValidatePayload

**Sprint:** 5–6
**Prereqs:** RX-5.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §4 (field mapping), §2.8 (dedup key), §2.9 (fiscal data)
- `src/cloud/FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs` — DOMS cloud normalization for reference pattern
- `src/cloud/FccMiddleware.Domain/Models/Adapter/` — cloud canonical models

**Task:**
Implement `RadixCloudAdapter.NormalizeTransaction()` and `ValidatePayload()` for the cloud side.

**Detailed instructions:**
1. **`ValidatePayload(RawPayloadEnvelope)`:**
   - Check `envelope.Vendor == FccVendor.RADIX`
   - Check payload is non-null/non-empty
   - Attempt XML parse — if invalid XML, return `ValidationResult(isValid: false, errorCode: "INVALID_XML", recoverable: false)`
   - Check for `<TRN>` element — if missing, return `ValidationResult(isValid: false, errorCode: "MISSING_TRANSACTION")`
   - If signature validation is configured for this site, verify the signature using `RadixSignatureValidator`
   - Return `ValidationResult(isValid: true)` on success

2. **`NormalizeTransaction(RawPayloadEnvelope)`:**
   - Same field mapping logic as edge `NormalizeAsync` (§4 mapping table)
   - Use `SiteFccConfig` (from envelope context or resolved via site code) for pump/nozzle mappings, product code mappings, timezone, and currency decimal places
   - Compose `fccTransactionId` = `"{FDC_NUM}-{FDC_SAVE_NUM}"`
   - Apply all conversions: volume → microlitres, amount → minor units, timestamps → UTC
   - Map `EFD_ID` → `fiscalReceiptNumber`

3. **Code reuse consideration:** The XML parsing and field mapping logic is largely identical between edge and cloud. Consider extracting shared parsing logic into a `RadixCommon` internal class or shared package. For MVP, acceptable to duplicate with clear comments referencing the canonical mapping in §4.

**Unit tests:**
- Validate well-formed Radix XML → valid
- Validate malformed XML → invalid with `INVALID_XML`
- Validate XML without `<TRN>` → invalid with `MISSING_TRANSACTION`
- Normalize all fields correctly (same test vectors as RX-2.2)
- Normalize with site-specific pump/nozzle/product mappings
- Normalize with timezone conversion

**Acceptance criteria:**
- Validation catches structural issues before normalization
- Normalization produces correct `CanonicalTransaction` matching edge adapter output
- Signature validation works when configured
- All mapped fields match §4 specification

---

### RX-5.3: Cloud Push Ingress — XML Endpoint

**Sprint:** 6
**Prereqs:** RX-5.2, CB-1.2 (ingestion pipeline exists)
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §7.1 (cloud push ingress requirements), §2.12 (headers)
- `schemas/openapi/cloud-api.yaml` — existing push endpoint definition (JSON-based for DOMS)

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
   f. Feed into existing dedup → store → outbox pipeline (CB-1.2)
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

## Phase 6 — VirtualLab Simulation (Sprint 6)

### RX-6.1: VirtualLab Radix FDC Simulator Profile

**Sprint:** 6
**Prereqs:** RX-2.2, RX-3.1
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §2.4 (transaction XML), §2.6 (pre-auth XML), §2.2 (signing), all XML examples
- `VirtualLab/` — existing VirtualLab project structure and seed profiles

**Task:**
Create a Radix FDC simulator profile in VirtualLab for end-to-end testing.

**Detailed instructions:**
1. Add a `radix-like` profile to `SeedProfileFactory` (or equivalent VirtualLab configuration)
2. The simulator must:
   - Expose two HTTP ports: port P (auth) and port P+1 (transactions)
   - Validate incoming signatures using a configured shared secret
   - Respond with properly signed XML responses

3. **Simulated behaviors:**
   - **CMD_CODE=10 (transaction request):** Return pre-seeded transactions from a FIFO queue. Return RESP_CODE=205 when queue is empty.
   - **CMD_CODE=201 (ACK):** Remove the oldest transaction from the queue.
   - **CMD_CODE=20 (mode change):** Accept and echo back the requested mode. Track current mode.
   - **CMD_CODE=55 (product read):** Return a fixed product list.
   - **Pre-auth (AUTH_DATA):** Validate fields, respond with ACKCODE=0. Store the authorized pump/FP for later transaction generation.
   - **Pre-auth cancel (AUTH=FALSE):** Clear the authorized pump/FP.
   - **UNSOLICITED mode:** When mode=2, automatically POST transactions to a configured callback URL.

4. Seed data: pre-populate the FIFO queue with 5-10 diverse transactions covering different pump addresses, nozzles, products, amounts, and fiscal data

5. Configurable error injection: ability to return specific error codes (251, 258, 260) for testing error handling

**Acceptance criteria:**
- Simulator responds to all Radix commands with correctly structured and signed XML
- Pull mode (CMD_CODE=10 → ACK loop) works end-to-end
- Push mode (unsolicited POSTs) works end-to-end
- Pre-auth and cancellation work
- Error injection produces correct error responses
- End-to-end test: Edge Agent configured with Radix adapter → connects to VirtualLab simulator → fetches and normalizes transactions

---

## Phase 7 — Integration Testing & Hardening (Sprint 7)

### RX-7.1: End-to-End Integration Tests

**Sprint:** 7
**Prereqs:** All previous phases
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Radix/WIP-RadixFCCAdapterPlan.md` — §13 (acceptance criteria checklist)

**Task:**
Create comprehensive integration tests covering the full Radix adapter flow.

**Detailed instructions:**
1. **Pull mode E2E test:**
   - Configure edge agent with Radix adapter pointed at mock HTTP server
   - Seed mock with 5 transactions
   - Call `FetchTransactionsAsync` → verify 5 transactions returned
   - Call `NormalizeAsync` on each → verify canonical fields
   - Verify all 5 ACKs were sent
   - Call `FetchTransactionsAsync` again → verify empty batch (RESP_CODE=205)

2. **Pre-auth E2E test:**
   - Send pre-auth via `SendPreAuthAsync` → verify ACK from mock
   - Seed a dispense transaction with matching TOKEN → fetch and normalize
   - Verify TOKEN-based correlation links pre-auth to dispense

3. **Push mode E2E test (edge):**
   - Start push listener
   - POST unsolicited transaction from mock FCC
   - Verify transaction received, parsed, and ACKed

4. **Cloud push E2E test:**
   - POST Radix XML to cloud ingestion endpoint
   - Verify transaction stored, XML ACK returned

5. **Error handling tests:**
   - Signature mismatch → appropriate error response
   - Network timeout → graceful degradation
   - Malformed XML → non-recoverable error

6. **Verify all §13 acceptance criteria** as a checklist

**Acceptance criteria:**
- All §13 acceptance criteria verified programmatically
- Pull mode drains FIFO correctly
- Push mode receives and ACKs correctly
- Pre-auth → dispense correlation works via TOKEN
- Cloud XML endpoint works end-to-end
- Error handling is robust across all failure modes

---

### RX-7.2: Documentation & Open Question Resolution

**Sprint:** 7
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
2. Document the Radix adapter's configuration requirements (what fields are needed in `appsettings.json` or site config)
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
Phase 0: RX-0.1 → RX-0.2
              ↓
Phase 1: RX-1.1 → RX-1.2 → RX-1.4 ──→ RX-1.6
              ↓       ↓         ↑
           RX-1.3 ←──┘    RX-1.5
              ↓              ↓
Phase 2: RX-2.1 ←──── RX-1.4, RX-1.5
              ↓
           RX-2.2 ←── RX-1.3, RX-1.5
              ↓
           RX-2.3 ←── RX-2.1
              ↓
Phase 3: RX-3.1 ←── RX-1.4, RX-1.5, RX-0.1
              ↓
           RX-3.2 ←── RX-3.1
              ↓
           RX-3.3 ←── RX-3.1, RX-2.2

           RX-3.4     (independent)

Phase 4: RX-4.1 ←── RX-1.5, RX-2.2, RX-2.3
              ↓
           RX-4.2 ←── RX-4.1, RX-2.1

Phase 5: RX-5.1 ←── CB-1.1
              ↓
           RX-5.2 ←── RX-5.1
              ↓
           RX-5.3 ←── RX-5.2, CB-1.2

Phase 6: RX-6.1 ←── RX-2.2, RX-3.1

Phase 7: RX-7.1 ←── All previous
           RX-7.2 ←── All previous
```

---

## Risk Register (from WIP plan §12)

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| SHA-1 signature computation doesn't match FDC exactly (whitespace, encoding) | Medium | High — all communication fails | Test against real FDC early (RX-1.2). Use spec examples as golden tests. |
| Currency/amount decimal interpretation is wrong | Medium | High — financial data corruption | Resolve RQ-1 before RX-2.2. Add validation logging. |
| Pump addressing model doesn't fit our two-level schema | Medium | Medium — requires schema migration | Resolve RQ-2 before RX-2.2. May need DB migration. |
| Radix FDC cannot push to cloud directly (LAN-only push) | Medium | Medium — forces RELAY mode for all Radix sites | Resolve RQ-4 before RX-5.3. |
| No real-time pump status from Radix | Confirmed | Low-Medium — degraded UI | Accepted in RX-3.4. UI must handle gracefully. |
| TOKEN collision for pre-auth correlation | Low | High — incorrect matching | Tracked in RX-3.1 with 65K pool and cleanup. |
| FDC firmware variations across sites | Medium | Medium — XML format differences | Version check on connection. Alert if firmware < 3.49. |

---

## Prerequisites Checklist

Before implementation begins:

- [ ] **RQ-1 resolved** — Currency decimal handling confirmed (Critical for RX-2.2)
- [ ] **RQ-2 resolved** — Pump addressing model decided (Critical for RX-2.2, RX-3.1)
- [ ] **RQ-4 resolved** — Push endpoint capability confirmed (Important for RX-5.3)
- [ ] **Access to Radix FDC or simulator** for signature validation testing
- [ ] **FDC firmware version confirmed** ≥ 3.49
- [ ] **Cloud backend Phase 1 complete** (CB-1.1, CB-1.2) — needed for RX-5.x tasks
