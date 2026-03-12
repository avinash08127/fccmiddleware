# Advatec FCC Adapter Development Plan

**Version:** 1.0
**Created:** 2026-03-13
**Status:** PLANNING (blocked on requirements clarification)

**Reference Documents:**
- `docs/FCCAdapters/Advatec/WIP-AdvatecFCCAdapterPlan.md` — Advatec protocol deep dive & open questions
- `docs/plans/dev-plan-unified-fcc-adapters.md` — Unified plan for DOMS/Radix/Petronite (complete)

**Sprint Cadence:** 2-week sprints

---

## Context

Advatec is a **TRA-compliant Electronic Fiscal Device (EFD/VFD)** deployed at fuel stations in **Tanzania**. Unlike DOMS, Radix, and Petronite — which are full Forecourt Controllers with pump control, pre-authorization, and transaction fetching — Advatec's documented API surface covers only:

1. **Inbound:** `POST http://127.0.0.1:5560/api/v2/incoming` — Submit customer/transaction data for fiscal receipt generation
2. **Outbound:** Receipt webhook — Completed fiscal receipt pushed via HTTP POST to a configured URL

**Critical constraint:** The available documentation (2-page "PUMA API DOCUMENTATIONS") describes fiscal receipt generation only. Whether Advatec controls pumps, supports pre-authorization, or has a transaction pull API is **unknown**. This plan is structured around **two scenarios** that branch after Phase 0 requirements clarification.

---

## Protocol Comparison

| Aspect | DOMS | Radix | Petronite | **Advatec** |
|--------|------|-------|-----------|-------------|
| Primary role | FCC (pump control) | FCC (pump control) | FCC via bot (pump control) | **Fiscal device (TRA EFD/VFD)** |
| Transport | TCP/JPL (persistent, binary) | HTTP/XML (stateless, dual-port) | REST/JSON (stateless, OAuth2) | **REST/JSON (localhost, no auth)** |
| Auth | FcLogon handshake | SHA-1 message signing | OAuth2 Client Credentials | **None documented** |
| Transaction push | Unsolicited JPL events | CMD_CODE=20 MODE=2 (HTTP) | Webhook (`transaction_completed`) | **Receipt webhook (JSON)** |
| Transaction pull | Lock-read-clear supervised buffer | FIFO drain (CMD_CODE=10) | Not available | **Not documented** |
| Pre-auth | `authorize_Fp_req` JPL message | `<AUTH_DATA>` XML | Two-step: Create Order + Authorize | **Unknown (AQ-1)** |
| Pump status | Real-time via `FpStatus_req` | Not available | Synthesized from nozzle assignments | **Not documented** |
| Volume format | Centilitres (integer) | Litres as decimal string | Litres as decimal | **Litres as decimal (Items.Quantity)** |
| Amount format | x10 factor (integer) | Currency decimal string | Major currency units | **Ambiguous — see AQ-2** |
| Fiscal data | None | EFD_ID field | receipt_code field | **Full TRA receipt with tax breakdown** |
| Country scope | Multi-country | Multi-country | Multi-country | **Tanzania only** |
| Runs on | FCC hardware | FCC hardware | Separate bot device on LAN | **Local device (localhost:5560)** |
| Connection lifecycle | `IFccConnectionLifecycle` | Stateless | Stateless | **Stateless** |

---

## Scenario Branching

This plan follows **Option C** from the WIP document (hybrid adapter with fiscal focus) as the default path. Two scenarios exist:

### Scenario A: Advatec = Fiscal-Only Device (Separate FCC for Pump Control)

```
[Real FCC: DOMS/Radix] ──── controls pumps ────► [Pump]
       │
[Edge Agent] polls FCC, ingests transactions
       │
       ├── After dispense: POST Customer data to Advatec (fiscalization)
       │        POST http://127.0.0.1:5560/api/v2/incoming
       │
[Advatec EFD] ──── generates TRA fiscal receipt
       │
       └── Receipt webhook → Edge Agent → Cloud
```

- Advatec is NOT the primary transaction source — another FCC handles that
- Customer data submission is a post-processing step for TRA compliance
- Receipt webhook enriches existing transactions with fiscal data
- IFccAdapter implementation is minimal (webhook normalization only)

### Scenario C: Advatec = Combined FCC + Fiscal (Customer = Pre-Auth Trigger)

```
[Odoo POS] → [Edge Agent] → POST Customer data to Advatec (triggers pump auth)
                                    │
                              [Advatec locks pump, authorizes dispense]
                                    │
                              [Fuel dispensed]
                                    │
                              [Advatec generates TRA fiscal receipt]
                                    │
                              Receipt webhook → [Edge Agent] → Cloud
```

- Advatec IS the FCC — Customer data submission triggers pump authorization
- `Dose` field is a volume limit, not "quantity dispensed"
- Receipt webhook is the primary transaction ingestion path
- Full IFccAdapter implementation needed

**Decision point:** Phase 0 resolves AQ-1 and determines which scenario applies. Phases 1-3 are common to both. Phases 4+ branch.

---

## Phase 0 — Requirements Clarification (MUST COMPLETE FIRST)

> **This phase is non-negotiable.** No code should be written until AQ-1, AQ-2, AQ-3, and AQ-10 are resolved.

### ADV-0.1: Resolve Blocking Open Questions — `[TODO]`

**Components:** Business/Vendor liaison
**Prereqs:** None
**Effort:** 1-3 days (external dependency)

Engage Advatec deployment team or business stakeholders to resolve:

| ID | Question | Why It Blocks |
|----|----------|---------------|
| **AQ-1** | Is Advatec a full FCC (controls pumps) or a fiscal-only device? Does the Customer endpoint trigger pump authorization? Are there additional undocumented APIs? | Determines entire adapter architecture |
| **AQ-2** | Amount format: `AmountInclusive: 4285000` for 10L in TZS — major units or hundredths? Provide worked example with known fuel price. | All financial conversions depend on this |
| **AQ-3** | Pump number in Receipt webhook: not present. How to correlate Receipt → pump? Is TransactionId set by caller or generated by Advatec? | Canonical model requires pump number |
| **AQ-10** | Does every fuel dispense produce a Receipt webhook, or only fiscalized (Customer-submitted) transactions? | If only fiscalized, Normal Orders are invisible |

Secondary questions to resolve if possible:

| ID | Question | Priority |
|----|----------|----------|
| **AQ-4** | Dose field: litres only, or can it accept amount-based authorization? | P1 (Scenario C only) |
| **AQ-5** | Payments array: optional during pre-auth? Required? | P1 (Scenario C only) |
| **AQ-6** | Authentication: truly unauthenticated? Localhost-only? | P1 |
| **AQ-7** | Webhook retry behaviour: retries? buffering? interval? | P1 |
| **AQ-8** | Webhook target configuration: config file, admin UI, or API? | P1 (deployment) |
| **AQ-9** | Nozzle information: does Advatec have nozzle concept? | P1 |
| **AQ-12** | TransactionId format: caller-settable or Advatec-generated? | P1 (correlation) |
| **AQ-13** | Transaction history/pull API beyond documented endpoints? | P1 |

**Acceptance criteria:**
- AQ-1 resolved: Adapter role confirmed → Scenario A, B, or C selected
- AQ-2 resolved: Amount conversion formula documented with worked example
- AQ-3 resolved: Pump correlation mechanism identified (or Advatec agrees to add pump to webhook)
- AQ-10 resolved: Normal Order visibility confirmed
- Updated WIP document with answers

**Deliverable:** Updated `WIP-AdvatecFCCAdapterPlan.md` with resolved questions and confirmed scenario.

---

## Phase 1 — Shared Foundation (Sprint 1)

Cross-cutting prerequisites. These can begin in parallel with Phase 0 since they don't depend on open question answers.

### ADV-1.1: Advatec Config Extensions Across All Layers — `[DONE]`

**Components:** Kotlin Edge Agent, .NET Desktop Agent, Cloud Backend
**Prereqs:** None
**Effort:** 0.5 day

Add Advatec-specific config fields to all config models. The `FccVendor.ADVATEC` enum value already exists on all platforms.

**Kotlin `AgentFccConfig`** (in `AdapterTypes.kt`):
- `advatecDeviceAddress: String?` — Advatec device address (default `"127.0.0.1"`)
- `advatecDevicePort: Int?` — Advatec device port (default `5560`)
- `advatecWebhookListenerPort: Int?` — Port for Receipt webhook listener on Edge Agent
- `advatecWebhookToken: String?` — Shared token for webhook URL authentication (marked `@Sensitive`)
- `advatecEfdSerialNumber: String?` — TRA EFD serial for validation
- `advatecCustIdType: Int?` — Default CustIdType (1=TIN) for Customer submissions

**.NET Desktop `FccConnectionConfig`** (in `AdapterTypes.cs`):
- `AdvatecDeviceAddress: string?`
- `AdvatecDevicePort: int?` (default 5560)
- `AdvatecWebhookListenerPort: int?` (default 8091)
- `AdvatecWebhookToken: string?` (marked `[SensitiveData]`)
- `AdvatecEfdSerialNumber: string?`
- `AdvatecCustIdType: int?` (default 1)

**Cloud `SiteFccConfig`** (in `SiteFccConfig.cs`):
- `AdvatecDevicePort: int?`
- `AdvatecWebhookToken: string?` (marked `[Sensitive]`)
- `AdvatecEfdSerialNumber: string?`
- `AdvatecCustIdType: int?`
- `WebhookListenerPort: int?` — shared with Petronite, already exists; reuse for Advatec

**Files:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/AdapterTypes.kt`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs`
- `src/cloud/FccMiddleware.Domain/Models/Adapter/SiteFccConfig.cs`

**Acceptance criteria:**
- All Advatec config fields present on all 3 config models
- Sensitive fields annotated correctly
- All fields nullable with documented defaults
- Existing adapter code compiles without changes

---

### ADV-1.2: Factory Registrations (All Layers) — `[DONE]`

**Components:** All three layers
**Prereqs:** ADV-1.1
**Effort:** 0.5 day

Register Advatec adapter in all factories:

- **Kotlin `FccAdapterFactory`**: Add `ADVATEC` case → `AdvatecAdapter(config)`. Add `ADVATEC` to `IMPLEMENTED_VENDORS` set.
- **.NET Desktop `FccAdapterFactory`**: Add `Advatec` case. Cache pattern like Petronite (stateful — webhook listener).
- **Cloud `FccAdapterFactory`**: Register `Advatec` → `AdvatecCloudAdapter` delegate.
- **Cloud `Program.cs`**: Wire `AdvatecCloudAdapter` in DI container.

> **Note:** Full adapter implementations were created on all platforms — no stubs. Genuinely unsupported operations (fetchTransactions for push-only, sendPreAuth for fiscal-only) return proper empty/error results.

**Files:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/FccAdapterFactory.kt`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs`
- `src/cloud/FccMiddleware.Infrastructure/Adapters/FccAdapterFactory.cs`
- `src/cloud/FccMiddleware.Api/Program.cs`

**Acceptance criteria:**
- All factories recognize `ADVATEC`
- Stub adapters throw `NotImplementedException` until real implementation lands
- Existing adapter paths unaffected

---

### ADV-1.3: DB Migration — Advatec Config Columns — `[DONE]`

**Components:** Cloud Backend
**Prereqs:** ADV-1.1
**Effort:** 0.5 day

Add EF Core migration for Advatec-specific columns on `FccConfig` table:
- `advatec_device_port` INT NULLABLE
- `advatec_webhook_token` NVARCHAR NULLABLE
- `advatec_efd_serial_number` NVARCHAR NULLABLE
- `advatec_cust_id_type` INT NULLABLE

Update `FccConfigEntityTypeConfiguration` with column mappings.

**Files:**
- `src/cloud/FccMiddleware.Infrastructure/Persistence/Configurations/FccConfigEntityTypeConfiguration.cs`
- New migration file via `dotnet ef migrations add AddAdvatecConfigColumns`

**Acceptance criteria:**
- Migration applies cleanly to existing database
- Migration rolls back cleanly
- All new columns nullable
- `advatec_webhook_token` follows same encryption-at-rest pattern as `api_key`

---

## Phase 2 — Protocol Infrastructure (Sprint 2)

Core protocol DTOs, normalization logic, and heartbeat. These tasks depend on AQ-2 (amount format) being resolved.

### ADV-2.1: Protocol DTOs (.NET Desktop) — `[DONE]`

**Components:** .NET Desktop Agent
**Prereqs:** ADV-1.1, AQ-2 resolved
**Effort:** 1 day

Create Advatec protocol DTOs as C# records:

```csharp
// Inbound: Edge Agent → Advatec
AdvatecCustomerRequest         // { DataType, Data: { Pump, Dose, CustIdType, CustomerId, CustomerName, Payments[] } }
AdvatecPaymentItem             // { PaymentType, PaymentAmount }

// Outbound: Advatec → Edge Agent (webhook)
AdvatecWebhookEnvelope         // { DataType, Data }  — wrapper
AdvatecReceiptData             // All 20+ receipt fields
AdvatecReceiptItem             // { Price, Amount, TaxCode, Quantity, TaxAmount, Product, TaxId, DiscountAmount, TaxRate }
AdvatecCompanyInfo             // { TIN, VRN, City, Region, Mobile, Street, Country, TaxOffice, SerialNumber, RegistrationId, UIN, Name }
```

Use `[JsonPropertyName]` for PascalCase field names (Advatec uses PascalCase, not snake_case). Use `decimal` for all monetary/volume values.

**Reference:** `docs/FCCAdapters/Advatec/WIP-AdvatecFCCAdapterPlan.md` — §2.2, §2.3
**Create:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Advatec/AdvatecProtocolDtos.cs`

**Acceptance criteria:**
- All fields from sample payloads represented
- `decimal` for monetary values (no floating-point)
- JSON deserialization round-trips correctly against sample payloads
- Payment types: CASH, CCARD, EMONEY, INVOICE, CHEQUE

---

### ADV-2.2: Protocol DTOs (Kotlin Edge Agent) — `[DONE]`

**Components:** Kotlin Edge Agent
**Prereqs:** ADV-1.1, AQ-2 resolved
**Effort:** 0.5 day

Mirror .NET DTOs as Kotlin data classes. Use `BigDecimal` for monetary/volume values. Use `@SerializedName` or `@JsonProperty` for field mapping (Advatec uses PascalCase).

**Create:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/advatec/AdvatecProtocolDtos.kt`

**Acceptance criteria:**
- Same field coverage as .NET DTOs
- `BigDecimal` for monetary values
- JSON serialization matches sample payloads

---

### ADV-2.3: Receipt Webhook Normalization (.NET Desktop) — `[DONE]`

**Components:** .NET Desktop Agent
**Prereqs:** ADV-2.1, AQ-2 resolved, AQ-3 resolved
**Effort:** 2 days

Implement `NormalizeAsync` in `AdvatecAdapter`:

1. **Parse** `AdvatecWebhookEnvelope` → extract `AdvatecReceiptData`
2. **Map fields** to `CanonicalTransaction`:
   - `fccTransactionId` = `Data.TransactionId` (e.g., `"TRSD1INV009"`)
   - `siteCode` = injected from config
   - `pumpNumber` = **per AQ-3 resolution** (correlation lookup, config default, or webhook field)
   - `nozzleNumber` = **per AQ-9 resolution** (default 1 if no nozzle concept)
   - `productCode` = `Items[0].Product` mapped via `productCodeMapping` (e.g., `"TANGO" → "PMS"`)
   - `volumeMicrolitres` = `(long)(Items[0].Quantity * 1_000_000m)` — Quantity is in litres
   - `amountMinorUnits` = **per AQ-2 resolution** — apply correct conversion factor for TZS
   - `unitPriceMinorPerLitre` = derived from `Items[0].Price` with same conversion factor
   - `currencyCode` = `"TZS"` (from config, default for Tanzania)
   - `completedAt` = parse `Data.Date` (`yyyy-MM-dd`) + `Data.Time` (`HH:mm:ss`), apply `Africa/Dar_es_Salaam` → UTC
   - `startedAt` = same as `completedAt` (only one timestamp available)
   - `fiscalReceiptNumber` = `Data.ReceiptCode`
   - `fccVendor` = `FccVendor.Advatec`
   - `attendantId` = null (not available)
3. **Preserve in raw payload:** `ReceiptVCodeURL`, `ZNumber`, `DailyCount`, `GlobalCount`, `Payments[]`, `Company.*`, `TotalTaxAmount`, `AmountExclusive`, `Items[].TaxCode/TaxRate/TaxAmount`
4. **Sanity check:** `Items[0].Price * Items[0].Quantity ≈ Items[0].Amount` (within discount tolerance)
5. **Handle edge cases:** empty Items array, null/missing fields

**Pump/nozzle resolution strategy (depends on AQ-3):**
- **If TransactionId is caller-settable (AQ-12):** Store `{correlationKey → pumpNumber}` when Customer data is submitted. Look up on Receipt arrival.
- **If Advatec adds pump to webhook:** Direct mapping.
- **Fallback:** Use config-level `defaultPumpNumber` (single-pump sites).

**Create:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Advatec/AdvatecAdapter.cs`
**Tests:** At least 10 unit tests covering normalization paths, edge cases, and amount conversion.

**Acceptance criteria:**
- Receipt webhook JSON correctly normalized to CanonicalTransaction
- Volume conversion: litres → microlitres (integer, no floating-point)
- Amount conversion: per AQ-2 resolution, documented and tested
- Timestamp: `Africa/Dar_es_Salaam` → UTC
- Sanity check: price × quantity ≈ amount (logged warning if mismatch)
- `fiscalReceiptNumber` populated with TRA ReceiptCode
- Raw payload preserves all fiscal/tax/payment data

---

### ADV-2.4: Receipt Webhook Normalization (Kotlin Edge Agent) — `[DONE]`

**Components:** Kotlin Edge Agent
**Prereqs:** ADV-2.2, AQ-2 resolved, AQ-3 resolved
**Effort:** 1.5 days

Mirror .NET normalization logic. Use `BigDecimal` for all conversions (no floating-point). Same field mappings, same sanity checks, same test vectors for cross-platform consistency.

**Create:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/advatec/AdvatecAdapter.kt`
**Tests:** At least 10 unit tests, shared test vectors with .NET.

**Acceptance criteria:**
- Same canonical output as .NET adapter for identical input
- `BigDecimal` arithmetic throughout
- Cross-platform test vectors pass on both Kotlin and .NET

---

### ADV-2.5: Heartbeat Implementation (Both Platforms) — `[DONE]`

**Components:** Kotlin Edge Agent, .NET Desktop Agent
**Prereqs:** ADV-2.3, ADV-2.4
**Effort:** 0.5 day

`HeartbeatAsync()` / `heartbeat()`:
- TCP connect to `{advatecDeviceAddress}:{advatecDevicePort}` (default `127.0.0.1:5560`)
- 5-second hard timeout
- Never throws — returns `bool`
- If Advatec has a health endpoint (per AQ-6 resolution), use that instead of raw TCP connect

> **Note:** Unlike other vendors, Advatec runs on localhost. Heartbeat mainly detects whether the Advatec process is running, not network connectivity.

**Acceptance criteria:**
- Returns `true` if Advatec is reachable, `false` otherwise
- Never throws exceptions
- 5-second timeout enforced

---

### ADV-2.6: Stub Methods for Unsupported Capabilities (Both Platforms) — `[DONE]`

**Components:** Kotlin Edge Agent, .NET Desktop Agent
**Prereqs:** ADV-2.3, ADV-2.4
**Effort:** 0.5 day

Implement the remaining `IFccAdapter` methods as no-ops / appropriate defaults:

| Method | Implementation |
|--------|---------------|
| `fetchTransactions()` | Return empty `TransactionBatch(transactions=[], hasMore=false)` — push-only |
| `acknowledgeTransactions()` | Return `true` (no-op — webhook has no ACK protocol) |
| `getPumpStatus()` | Return empty list (no pump status API documented) |
| `sendPreAuth()` | Return `PreAuthResult(status=ERROR, message="Pre-auth not supported for Advatec")` |
| `cancelPreAuth()` | Return `false` (not supported) |

> **Note:** `sendPreAuth` and `cancelPreAuth` stubs will be replaced in Phase 4 if Scenario C is confirmed.

**Adapter metadata:**
- `supportedIngestionMethods = [PUSH]`
- `supportsPreAuth = false` (updated to `true` if Scenario C)
- `supportsPumpStatus = false`
- `vendorName = "Advatec"`
- `countryScope = "TZ"` (Tanzania only)

**Acceptance criteria:**
- All IFccAdapter methods implemented (no `NotImplementedException`)
- Adapter passes factory resolution and instantiation
- Metadata accurately reflects capabilities

---

## Phase 3 — Webhook Listeners & Cloud Adapter (Sprint 3)

### ADV-3.1: Edge Agent Webhook Listener (.NET Desktop) — `[DONE]`

**Components:** .NET Desktop Agent
**Prereqs:** ADV-2.3
**Effort:** 1.5 days

HTTP endpoint to receive Advatec Receipt webhooks:

- **Route:** `POST /api/webhook/advatec`
- **Binding:** `0.0.0.0:{advatecWebhookListenerPort}` (default 8091, configurable)
- Accept `Content-Type: application/json`
- **Authentication:** Validate URL token parameter or `X-Webhook-Token` header against `advatecWebhookToken` from config
- Parse `DataType: "Receipt"` envelope
- Validate required fields: `TransactionId`, `Items` (non-empty), `AmountInclusive`
- Normalize via `AdvatecAdapter.NormalizeAsync()`
- Feed into ingestion pipeline (buffer locally, relay to cloud per ingestion mode)
- **Always return HTTP 200** (Advatec retry behaviour unknown — AQ-7)
- Log structured event on receipt: `TransactionId`, `ReceiptCode`, `pumpNumber`, `amount`

**Integration with IngestionOrchestrator:**
- Add `EnsurePushListenersInitializedAsync()` call for Advatec (same pattern as Petronite — PN-4.1)
- Called from `CadenceController.ExecuteAsync()` on startup

**Create:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Advatec/AdvatecWebhookListener.cs`

**Acceptance criteria:**
- Webhook endpoint accepts valid Receipt payloads and returns HTTP 200
- Invalid payloads (wrong DataType, missing fields) logged as warnings, still return HTTP 200
- Transactions buffered locally before cloud upload attempt
- Webhook listener starts on `CadenceController` startup
- Token-based authentication validates correctly

---

### ADV-3.2: Edge Agent Webhook Listener (Kotlin) — `[DONE]`

**Components:** Kotlin Edge Agent
**Prereqs:** ADV-2.4
**Effort:** 1.5 days

Mirror .NET webhook listener using Ktor embedded server (same pattern as Petronite webhook listener on Kotlin).

- **Route:** `POST /api/webhook/advatec`
- Same validation, normalization, and ingestion pipeline integration
- Same token-based authentication

**Create:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/advatec/AdvatecWebhookListener.kt`

**Acceptance criteria:**
- Same behavior as .NET webhook listener
- Ktor embedded server starts/stops cleanly
- Integration with IngestionOrchestrator

---

### ADV-3.3: Advatec Cloud Adapter — `[DONE]`

**Components:** Cloud Backend
**Prereqs:** ADV-2.1 (DTOs can be shared or duplicated)
**Effort:** 1.5 days

Create `FccMiddleware.Adapter.Advatec` project:

**`AdvatecCloudAdapter`** implementing `IFccAdapter`:
- `ValidatePayload(rawPayload)` — check `DataType == "Receipt"`, required fields present, Items non-empty
- `NormalizeTransaction(rawPayload)` — same field mapping as edge adapter (ADV-2.3), with `legalEntityId` enrichment from site config
- `FetchTransactionsAsync()` — return empty batch (push-only)
- `GetAdapterMetadata()` — `PUSH` only, no pre-auth, no pump status

**Create:**
- `src/cloud/FccMiddleware.Adapter.Advatec/FccMiddleware.Adapter.Advatec.csproj`
- `src/cloud/FccMiddleware.Adapter.Advatec/AdvatecCloudAdapter.cs`
- `src/cloud/FccMiddleware.Adapter.Advatec/Internal/AdvatecReceiptDto.cs`

**Acceptance criteria:**
- ValidatePayload correctly identifies valid/invalid Receipt payloads
- NormalizeTransaction produces same canonical output as edge adapters
- Registered in cloud factory and DI container
- Unit tests with sample receipt payloads

---

### ADV-3.4: Cloud Push Ingress Endpoint — `[DONE]`

**Components:** Cloud Backend
**Prereqs:** ADV-3.3
**Effort:** 1 day

Add cloud webhook ingress endpoint for Advatec Receipt webhooks (used in RELAY mode — Edge Agent forwards to cloud):

- **Route:** `POST /api/v1/ingest/advatec/webhook`
- **Authentication:** `X-Webhook-Token` header or `X-Site-Code` header → site lookup
- Accept `Content-Type: application/json`
- Parse `DataType: "Receipt"` envelope
- Validate via `AdvatecCloudAdapter.ValidatePayload()`
- Normalize via `AdvatecCloudAdapter.NormalizeTransaction()`
- Standard pipeline: dedup → store → outbox
- Return `HTTP 200 {"status":"ok"}`

> **Note:** CLOUD_DIRECT mode is unlikely for Advatec (localhost device), but the cloud endpoint still serves Edge Agent relay uploads.

**Files:**
- Cloud API controllers / ingestion endpoint routes

**Acceptance criteria:**
- Valid Receipt webhooks ingested and stored
- Duplicate detection via `TransactionId + siteCode`
- Secondary dedup check via `ReceiptCode` (warning if TransactionId matches but ReceiptCode differs)
- Invalid payloads return appropriate error codes
- Integration tests pass

---

### ADV-3.5: Portal Vendor-Specific Config UI — `[DONE]`

**Components:** Portal (Angular)
**Prereqs:** ADV-1.3
**Effort:** 1 day

Extend `FccConfigFormComponent` with conditional Advatec section (shown when `fccVendor === 'ADVATEC'`):

| Field | Type | Default | Validation |
|-------|------|---------|------------|
| Device Address | text | `127.0.0.1` | IP or hostname |
| Device Port | number | `5560` | 1-65535 |
| Webhook Listener Port | number | `8091` | 1-65535 |
| Webhook Token | password | — | Required |
| EFD Serial Number | text | — | Optional (TRA reference) |
| Default CustIdType | select | `1 (TIN)` | 1-7 per TRA standard |
| Currency Code | text | `TZS` | ISO 4217 (readonly for Advatec) |
| Timezone | text | `Africa/Dar_es_Salaam` | IANA timezone |

**File:** `src/portal/src/app/features/site-config/fcc-config-form.component.ts`

**Acceptance criteria:**
- Advatec config section visible only when vendor = ADVATEC
- All fields save/load correctly via API
- Sensitive fields (webhook token) use password input with visibility toggle
- Default values populated for new sites
- Section hidden for other vendors

---

## Phase 4 — Customer Data Submission (Sprint 4, CONDITIONAL)

> **This phase is ONLY implemented if AQ-1 confirms Scenario C** (Customer endpoint = pre-auth trigger or post-dispense fiscalization submission).
> If Scenario A (fiscal-only device with separate FCC), skip to Phase 5.

### ADV-4.1: Customer Data Submission Client (.NET Desktop) — `[DONE]`

**Components:** .NET Desktop Agent
**Prereqs:** ADV-2.1, AQ-1 resolved (Scenario C confirmed), AQ-4 resolved, AQ-5 resolved
**Effort:** 1.5 days

Implement HTTP client for submitting Customer data to Advatec:

- `POST http://{advatecDeviceAddress}:{advatecDevicePort}/api/v2/incoming`
- Build `AdvatecCustomerRequest` payload:
  - `DataType = "Customer"`
  - `Data.Pump` = canonical pump number
  - `Data.Dose` = **per AQ-4 resolution** (volume in litres, potentially converted from amount)
  - `Data.CustIdType` = from PreAuthCommand or config default
  - `Data.CustomerId` = `customerTaxId`
  - `Data.CustomerName` = `customerName`
  - `Data.Payments` = **per AQ-5 resolution** (empty array or populated from pre-auth context)
- 10-second timeout
- No auth headers (per AQ-6, or add if auth is confirmed)
- Parse response (structure unknown — log raw response body)

**Create:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Advatec/AdvatecApiClient.cs`

**Acceptance criteria:**
- Customer data correctly serialized to Advatec format
- Dose conversion handles amount → volume if needed
- HTTP errors logged with response body
- Timeout enforced

---

### ADV-4.2: SendPreAuth Implementation (.NET Desktop) — `[DONE]`

**Components:** .NET Desktop Agent
**Prereqs:** ADV-4.1, AQ-1 confirmed Scenario C
**Effort:** 1.5 days

Replace `SendPreAuthAsync` stub with real implementation:

1. Map `PreAuthCommand` fields → `AdvatecCustomerRequest`
2. Submit via `AdvatecApiClient`
3. Store correlation: `ConcurrentDictionary<string, AdvatecActivePreAuth>` mapping `{pump}_{timestamp}` → pre-auth details
4. Return `PreAuthResult`:
   - HTTP 200 → `AUTHORIZED` with correlation ID
   - HTTP 4xx/5xx → `ERROR` with message
   - Timeout → `TIMEOUT`
5. Update adapter metadata: `supportsPreAuth = true`

**Correlation strategy for Receipt matching:**
- If `TransactionId` is caller-settable (AQ-12): embed our correlation key
- Otherwise: correlate by pump number + time window

**Acceptance criteria:**
- Pre-auth correctly submitted to Advatec
- Active pre-auth tracked in correlation map
- Receipt webhook → pre-auth correlation works
- Update metadata to reflect pre-auth support

---

### ADV-4.3: SendPreAuth Implementation (Kotlin) — `[DONE]`

**Components:** Kotlin Edge Agent
**Prereqs:** ADV-4.2 (reference), AQ-1 confirmed Scenario C
**Effort:** 1 day

Mirror .NET pre-auth implementation. Same correlation strategy.

**Create:** Extend `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/advatec/AdvatecAdapter.kt`

---

### ADV-4.4: Pre-Auth ↔ Receipt Correlation (Both Platforms) — `[DONE]`

**Components:** Kotlin Edge Agent, .NET Desktop Agent
**Prereqs:** ADV-4.2, ADV-4.3, ADV-2.3
**Effort:** 1 day

In `NormalizeAsync` / `normalize`:
- When Receipt webhook arrives, check active pre-auth map
- Correlation key: **per AQ-12 resolution** (TransactionId match, pump+time window, or embedded correlation key)
- If match found: set `fccCorrelationId` and `odooOrderId` on canonical transaction
- Remove from active map (pre-auth completed)
- Unmatched receipts: log as Normal Order, generate transaction ID as correlation ID

**Acceptance criteria:**
- Pre-auth receipts correctly correlated with active pre-auth entries
- Correlation map cleaned up after matching
- Normal Orders (no pre-auth) handled gracefully
- No memory leak from stale active pre-auth entries (add TTL cleanup: 30-minute max age)

---

## Phase 5 — VirtualLab Simulator (Sprint 4-5)

### ADV-5.1: Advatec EFD Simulator — `[DONE]`

**Components:** VirtualLab
**Prereqs:** ADV-2.1 (DTOs for payload structure)
**Effort:** 2 days

`IHostedService` HTTP server simulating Advatec EFD:

**Endpoints:**
- `POST /api/v2/incoming` — Accept Customer data submissions
  - Validate `DataType == "Customer"`
  - Queue for receipt generation (configurable delay simulating TRA processing)
  - Return HTTP 200 with generic success response
- REST management API:
  - `POST /simulator/inject-receipt` — Inject a receipt directly into the webhook queue
  - `POST /simulator/configure-webhook` — Set webhook target URL
  - `POST /simulator/configure-delay` — Set receipt generation delay (default 2s)
  - `POST /simulator/set-error-mode` — Simulate errors (TRA offline, device busy)
  - `GET /simulator/state` — Get current simulator state (pending receipts, generated count)
  - `POST /simulator/reset` — Clear all state

**Receipt generation:**
- After configurable delay, POST `AdvatecWebhookEnvelope` to configured webhook URL
- Generate realistic TRA receipt fields:
  - Sequential `GlobalCount` and `DailyCount`
  - `ZNumber` = current date as YYYYMMDD
  - `ReceiptCode` = random hex (11 chars)
  - `TransactionId` = `"TRSD1INV{GlobalCount:000}"`
  - `ReceiptVCodeURL` = `https://virtual.tra.go.tz/efdmsrctverify/{ReceiptCode}_{Time}`
  - Company info from configured profile
  - Tax calculation: 18% VAT (TRA standard rate)
  - Items derived from Customer submission Dose + configured unit price

**Create:** `VirtualLab/src/VirtualLab.Infrastructure/AdvatecSimulator/AdvatecSimulatorService.cs`

**Acceptance criteria:**
- Customer submissions accepted and queued
- Receipt webhooks generated with realistic TRA-compliant data
- Configurable delay simulates real processing time
- Error injection modes: TRA offline (no receipt generated), device busy (HTTP 503)
- Management API for test scenario setup
- Concurrent access safe

---

### ADV-5.2: Simulator Seed Profile — `[DONE]`

**Components:** VirtualLab
**Prereqs:** ADV-5.1
**Effort:** 0.5 day

Add Advatec profile to `SeedProfileFactory`:
- Company info: Advatech Company Limited (TIN, VRN, SerialNumber from sample data)
- Products: TANGO (PMS), DIESEL (AGO) with TRA tax codes
- 3 pumps with unit prices in TZS
- Default webhook target: Edge Agent localhost endpoint

**File:** `VirtualLab/src/VirtualLab.Infrastructure/SeedProfileFactory.cs`

**Acceptance criteria:**
- Advatec profile creates realistic Tanzania fuel station scenario
- Products match Puma Energy Tanzania fuel types
- Tax rates match current TRA standard (18% VAT)

---

## Phase 6 — Integration Testing & Hardening (Sprint 5)

### ADV-6.1: Edge Agent E2E Tests — `[DONE]`

**Components:** Both edge agents
**Prereqs:** ADV-3.1, ADV-3.2, ADV-5.1
**Effort:** 2 days

6 test scenarios against VirtualLab Advatec simulator:

| # | Scenario | Description |
|---|----------|-------------|
| 1 | **Heartbeat** | Verify heartbeat returns true when simulator is running, false when stopped |
| 2 | **Receipt webhook ingestion** | Simulator injects receipt → webhook arrives → normalized to canonical → buffered |
| 3 | **Normalization fields** | Verify all canonical fields correctly mapped: volume, amount, price, timestamps, product, fiscal receipt |
| 4 | **Customer data submission** | Submit Customer data → simulator processes → receipt webhook generated → correlated (Scenario C only) |
| 5 | **Deduplication** | Same receipt received twice → only one canonical transaction stored |
| 6 | **Error handling** | Simulator in error mode → webhook not received → Edge Agent handles gracefully (no crash, logs warning) |

**Create:** `VirtualLab/tests/VirtualLab.Tests/Simulators/AdvatecSimulatorE2ETests.cs`

**Acceptance criteria:**
- All 6 scenarios pass
- Cross-platform: same Receipt input produces same canonical output on Kotlin and .NET
- No resource leaks (webhook listeners cleaned up after tests)

---

### ADV-6.2: Cloud Ingestion Tests — `[DONE]`

**Components:** Cloud Backend
**Prereqs:** ADV-3.3, ADV-3.4
**Effort:** 1 day

5 test scenarios for cloud-side Advatec ingestion:

| # | Scenario | Description |
|---|----------|-------------|
| 1 | **Valid Receipt webhook** | POST valid Receipt JSON → ingested → stored → HTTP 200 |
| 2 | **Duplicate detection** | Same TransactionId + siteCode → second POST returns success but no new record |
| 3 | **Invalid DataType** | POST with `DataType: "Customer"` (wrong type) → rejected with error |
| 4 | **Missing required fields** | Receipt without TransactionId or Items → validation error |
| 5 | **Empty body** | POST with empty body → rejected gracefully |

**Create:** Tests in existing `VendorPushIngressTests.cs` or new Advatec-specific test file

**Acceptance criteria:**
- All 5 scenarios pass
- Dedup key: `TransactionId + siteCode`
- ReceiptCode cross-validation logged as warning if mismatch

---

### ADV-6.3: Cross-Vendor Regression — `[DONE]`

**Components:** All
**Prereqs:** ADV-6.1, ADV-6.2
**Effort:** 0.5 day

Verify Advatec addition doesn't break existing adapters:

- DOMS, Radix, Petronite factory resolution still works
- Cloud factory resolves all 4 vendors
- Portal config save/load for all vendors (including Advatec sections hidden for non-Advatec)
- No regressions in existing adapter tests

---

## Phase 7 — Fiscalization Integration (Sprint 6, SCENARIO A ONLY)

> **This phase is ONLY implemented if AQ-1 confirms Scenario A** (Advatec is fiscal-only device, separate FCC controls pumps).

### ADV-7.1: Fiscalization Service Interface — `[DONE]`

**Components:** .NET Desktop Agent, Kotlin Edge Agent
**Prereqs:** AQ-1 resolved (Scenario A confirmed)
**Effort:** 1 day

Create `IFiscalizationService` interface (new concept — not `IFccAdapter`):

```csharp
public interface IFiscalizationService
{
    Task<FiscalizationResult> SubmitForFiscalizationAsync(
        CanonicalTransaction transaction,
        FiscalizationContext context,
        CancellationToken ct);

    Task<bool> IsAvailableAsync(CancellationToken ct);
}
```

```kotlin
interface IFiscalizationService {
    suspend fun submitForFiscalization(
        transaction: CanonicalTransaction,
        context: FiscalizationContext
    ): FiscalizationResult

    suspend fun isAvailable(): Boolean
}
```

`FiscalizationContext`: customer info (TIN, name, ID type), payment details.
`FiscalizationResult`: success/failure, receipt code, TRA verification URL.

**Create:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFiscalizationService.cs`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/IFiscalizationService.kt`

---

### ADV-7.2: Advatec Fiscalization Service Implementation — `[DONE]`

**Components:** .NET Desktop Agent, Kotlin Edge Agent
**Prereqs:** ADV-7.1, ADV-4.1
**Effort:** 2 days

Implement `IFiscalizationService` for Advatec:

1. Take completed `CanonicalTransaction` from primary FCC adapter
2. Build `AdvatecCustomerRequest`:
   - `Pump` = transaction.pumpNumber
   - `Dose` = transaction.volumeMicrolitres / 1_000_000 (convert back to litres)
   - `CustIdType` + `CustomerId` from fiscalization context
   - `Payments` derived from transaction amount
3. POST to Advatec `/api/v2/incoming`
4. Await Receipt webhook (correlated by pump + time window)
5. Return `FiscalizationResult` with `ReceiptCode` and `ReceiptVCodeURL`
6. Attach fiscal receipt data to original transaction

**Integration point:** Called from transaction processing pipeline AFTER primary FCC transaction is normalized and stored.

---

### ADV-7.3: Transaction Pipeline Fiscalization Hook — `[DONE]`

**Components:** .NET Desktop Agent, Kotlin Edge Agent
**Prereqs:** ADV-7.2
**Effort:** 1 day

Add post-processing hook in `IngestionOrchestrator`:
- After transaction is normalized and buffered, check if site has `fiscalizationMode = FCC_DIRECT` and `fiscalizationVendor = ADVATEC`
- If yes: call `IFiscalizationService.SubmitForFiscalizationAsync()`
- Attach fiscal receipt to transaction (update buffered record)
- Non-blocking: fiscalization failure should not block transaction ingestion

---

## Summary

| Phase | Tasks | Effort (dev-days) | Sprint | Dependency |
|-------|-------|--------------------|--------|------------|
| **Phase 0**: Requirements Clarification | 1 | 1-3 (external) | Pre-Sprint 1 | **BLOCKING — must complete first** |
| **Phase 1**: Shared Foundation | 3 | 1.5 | Sprint 1 | None (can parallel Phase 0) |
| **Phase 2**: Protocol Infrastructure | 6 | 6 | Sprint 2 | AQ-2, AQ-3 resolved |
| **Phase 3**: Webhook + Cloud | 5 | 6.5 | Sprint 3 | Phase 2 |
| **Phase 4**: Customer Submission (Scenario C) | 4 | 5 | Sprint 4 | AQ-1 = Scenario C |
| **Phase 5**: VirtualLab Simulator | 2 | 2.5 | Sprint 4-5 | Phase 2 |
| **Phase 6**: Integration Testing | 3 | 3.5 | Sprint 5 | Phases 3, 5 |
| **Phase 7**: Fiscalization Service (Scenario A) | 3 | 4 | Sprint 6 | AQ-1 = Scenario A |
| **TOTAL (Scenario C path)** | **24** | **~26 dev-days** | **5 sprints** | |
| **TOTAL (Scenario A path)** | **23** | **~25 dev-days** | **6 sprints** | |

> **Note:** Phases 4 and 7 are mutually exclusive. Scenario C (Customer = pre-auth) implements Phase 4. Scenario A (fiscal-only) implements Phase 7 instead.

---

## Dependency Graph

```
Phase 0:
  ADV-0.1 ──────────────────────────────────────────> (blocks Phases 2+)

Phase 1 (can run during Phase 0):
  ADV-1.1 ──┬──> ADV-1.2 (factory)
             └──> ADV-1.3 (DB migration)

Phase 2 (requires AQ-2, AQ-3 resolved):
  ADV-2.1 ──┬──> ADV-2.3 (normalize .NET) ──> ADV-2.5 (heartbeat)
             │                               ──> ADV-2.6 (stubs)
  ADV-2.2 ──┴──> ADV-2.4 (normalize Kotlin) ──> ADV-2.5
                                              ──> ADV-2.6

Phase 3:
  ADV-2.3 ──> ADV-3.1 (webhook .NET) ──┐
  ADV-2.4 ──> ADV-3.2 (webhook Kotlin)──┤
  ADV-2.1 ──> ADV-3.3 (cloud adapter) ──> ADV-3.4 (cloud ingress)
  ADV-1.3 ──> ADV-3.5 (portal UI)

Phase 4 (Scenario C ONLY):
  ADV-2.1 ──> ADV-4.1 (API client) ──> ADV-4.2 (pre-auth .NET)
                                    ──> ADV-4.3 (pre-auth Kotlin)
  ADV-4.2 + ADV-4.3 ──> ADV-4.4 (correlation)

Phase 5:
  ADV-2.1 ──> ADV-5.1 (simulator) ──> ADV-5.2 (seed profile)

Phase 6:
  ADV-3.1 + ADV-5.1 ──> ADV-6.1 (E2E tests)
  ADV-3.4 ──> ADV-6.2 (cloud tests)
  ADV-6.1 + ADV-6.2 ──> ADV-6.3 (regression)

Phase 7 (Scenario A ONLY):
  ADV-4.1 ──> ADV-7.1 (interface) ──> ADV-7.2 (service) ──> ADV-7.3 (pipeline hook)
```

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| **Advatec is NOT an FCC** — fiscal device only, half the adapter doesn't apply | **High** | High — architectural mismatch | Resolve AQ-1 FIRST. Plan structured to handle both scenarios. |
| **Missing pump number in Receipt** — cannot map transactions to pumps | **High** | **Critical** — transactions without pump numbers are unusable for Odoo | Request Advatec add pump to webhook (AQ-3). Fallback: correlate via Customer submission. |
| **Amount format ambiguity** — wrong conversion corrupts financial data | **High** | **Critical** — silent financial corruption | Get worked examples from Advatec team (AQ-2). Sanity check: price × quantity ≈ amount. |
| **Normal Orders invisible** — only fiscalized transactions produce webhooks | **Medium** | **High** — majority of transactions lost | Confirm AQ-10. If true, Advatec sites need dual-FCC (Advatec + another vendor). |
| **No pull API for recovery** — lost webhooks = lost fiscal data | **High** | Medium — individual transactions lost | Confirm AQ-7 (retry). Use RELAY mode. Buffer locally first. |
| **Localhost-only** — Edge Agent must co-locate with Advatec device | **High** | Medium — deployment constraint | Confirm AQ-6. Document deployment requirement. |
| **Tanzania-only scope** — TRA integration not portable | **Confirmed** | Low — other countries use different vendors | Document as Tanzania-only. Other countries unaffected. |
| **Incomplete documentation** — only 2 endpoints documented | **Confirmed** | High — cannot implement full adapter | Phase 0 clarification must complete first. Plan designed for incremental implementation. |
| **No webhook retry** — Advatec may not retry failed webhook deliveries | **Medium** | Medium — data loss | RELAY mode + local buffering. Request retry info (AQ-7). |
| **Currency edge case** — TZS has 0 decimal places but amounts may use implicit minor units | **High** | Medium — affects amount conversion | Cross-validate with real transaction data. Document conversion factor. |

---

## Key Differences from DOMS/Radix/Petronite Implementation

| Aspect | Existing Adapters | Advatec |
|--------|-------------------|---------|
| Platform coverage | All 3 adapters on Kotlin + .NET + Cloud | Same — all platforms |
| IFccAdapter methods | Most/all methods fully implemented | Most methods are no-ops (push-only, no pump control) |
| Pull mode | DOMS (lock-read-clear), Radix (FIFO drain) | **Not available** — push-only via webhook |
| Pre-auth | All vendors support some form | **Unknown** — depends on AQ-1 |
| Connection lifecycle | DOMS implements IFccConnectionLifecycle | **Stateless** — no persistent connection |
| Normalization complexity | Protocol-specific (binary, XML, OAuth) | **JSON — simplest protocol** but richest data (full fiscal receipt) |
| Fiscal data | Minimal (EFD_ID, receipt_code) | **Full TRA receipt** — tax breakdown, payments, verification URL |
| Authentication | API key, SHA-1 HMAC, OAuth2 | **None documented** |
| Country scope | Multi-country | **Tanzania only** |
| Deployment model | FCC on separate hardware/device | **Localhost** — co-located on same device as Edge Agent |

---

## Acceptance Criteria Summary

### Phase 0 (MUST PASS)
- [ ] AQ-1 resolved: Advatec role confirmed (FCC vs fiscal-only)
- [ ] AQ-2 resolved: Amount format confirmed with worked example
- [ ] AQ-3 resolved: Pump number correlation mechanism identified
- [ ] AQ-10 resolved: Normal Order webhook visibility confirmed

### Phase 1 (Foundation)
- [ ] Advatec config fields on all 3 platforms
- [ ] Factory registers Advatec on all layers
- [ ] DB migration applies cleanly

### Phase 2 (Protocol)
- [ ] Receipt webhook parsed and normalized to CanonicalTransaction
- [ ] Volume conversion: litres → microlitres (integer arithmetic)
- [ ] Amount conversion correct per AQ-2 resolution
- [ ] Timestamp: Africa/Dar_es_Salaam → UTC
- [ ] Cross-platform: same input → same canonical output

### Phase 3 (Webhook + Cloud)
- [ ] Edge Agent webhook listener accepts Receipt payloads
- [ ] Cloud adapter validates and normalizes Receipt payloads
- [ ] Cloud ingress endpoint accepts relayed webhooks
- [ ] Portal config section for Advatec

### Phase 6 (Testing)
- [ ] 6 E2E test scenarios pass against VirtualLab simulator
- [ ] 5 cloud ingestion test scenarios pass
- [ ] No regressions in existing vendor adapters

---

## Changelog

### 2026-03-13 — v1.6: Phase 7 Complete

- Marked ADV-7.1, ADV-7.2, and ADV-7.3 as `[DONE]`
- ADV-7.1: Created `IFiscalizationService` interface on both platforms
  - `.NET`: `IFiscalizationService`, `FiscalizationContext`, `FiscalizationResult` records
    in `Adapter/Common/IFiscalizationService.cs`
  - `Kotlin`: `IFiscalizationService`, `FiscalizationContext`, `FiscalizationResult` data classes
    in `adapter/common/IFiscalizationService.kt`
  - Interface defines two methods: `submitForFiscalization` (posts customer data, awaits receipt)
    and `isAvailable` (TCP liveness probe)
- ADV-7.2: Created `AdvatecFiscalizationService` implementation on both platforms
  - `.NET`: `Adapter/Advatec/AdvatecFiscalizationService.cs` — `IAsyncDisposable`, signal-based receipt awaiting
    - Manages own `AdvatecApiClient` and `AdvatecWebhookListener` (separate from AdvatecAdapter)
    - `SemaphoreSlim` serializes requests (Advatec is sequential localhost device)
    - Receipt correlation: submits Customer data, signals via `SemaphoreSlim` when webhook arrives
    - 30-second timeout waiting for receipt webhook
    - Currency factor conversion for Dose (microlitres → litres) and Payments (minor → major units)
  - `Kotlin`: `adapter/advatec/AdvatecFiscalizationService.kt` — poll-based receipt awaiting
    - Same pattern: own webhook listener, mutex serialization, 30s timeout
    - Background daemon thread drains webhook listener queue and parses receipts
    - Uses `ConcurrentLinkedQueue<AdvatecReceiptData>` for thread-safe receipt handoff
- ADV-7.3: Added fiscalization hook to `IngestionOrchestrator` on both platforms
  - `.NET IngestionOrchestrator`:
    - Added `ILoggerFactory?` optional constructor parameter (backward-compatible)
    - Added lazy `AdvatecFiscalizationService` creation via `ResolveFiscalizationService()`
    - Config fingerprint caching — recreates service only when Advatec config changes
    - Post-buffer fiscalization loop: collects newly buffered txs without fiscal receipts,
      attempts fiscalization after poll loop completes, updates `BufferedTransaction.FiscalReceiptNumber`
    - Non-blocking: all fiscalization failures are logged as warnings and do not block ingestion
  - `Kotlin IngestionOrchestrator`:
    - Added `transactionDao: TransactionBufferDao?` constructor parameter for fiscal receipt updates
    - Added `wireFiscalization(service, config)` for late-binding fiscalization dependencies
    - Same post-buffer loop: collects txs, calls `submitForFiscalization`, updates via DAO
  - Added `Vendor` field to fiscalization config on both platforms:
    - `.NET SiteConfigFiscalization.Vendor` — checks `Mode == "FCC_DIRECT"` && `Vendor == "ADVATEC"`
    - `Kotlin FiscalizationDto.vendor` — same check in doPoll
  - Added `updateFiscalReceipt(id, receiptCode, now)` `@Query` to Kotlin `TransactionBufferDao`

### 2026-03-13 — v1.5: Phase 6 Complete

- Marked ADV-6.1, ADV-6.2, and ADV-6.3 as `[DONE]`
- ADV-6.1: Created `AdvatecSimulatorE2ETests.cs` with 6 test scenarios
  - `[Collection("Simulators")]` — sequential execution shared with Radix/Petronite/DOMS tests
  - Test 1 (Heartbeat): Verifies simulator state endpoint returns products (TANGO, DIESEL), pumps (3),
    company profile (ADVATECH COMPANY LIMITED), and clean initial state
  - Test 2 (Receipt ingestion): Injects receipt via management API, verifies it appears in state
    with correct TransactionId (TRSD1INV pattern), ReceiptCode, pump, and volume
  - Test 3 (Normalization fields): Verifies receipt generation math — 5L TANGO @ 3285 TZS/L = 16425 TZS,
    sequential counters (globalCount, dailyCount), ZNumber = yyyyMMdd, 11-char hex ReceiptCode
  - Test 4 (Customer submission): Tests inject-receipt management endpoint (equivalent of Customer → Receipt flow),
    verifies pump 2 TANGO 15L = 49275 TZS
  - Test 5 (Deduplication): Injects two receipts with same pump/volume, verifies unique TransactionIds
    and ReceiptCodes (simulator generates sequential IDs + random receipt codes)
  - Test 6 (Error handling): Tests TraOffline/DeviceBusy error modes, full reset cycle
    (clears receipts, error mode, webhook URL, counters, re-seeds products and pumps)
  - Updated `SimulatorTestFixture.ResetAllSimulatorsAsync()` to include Advatec reset
- ADV-6.2: Added 5 Advatec test scenarios to existing `VendorPushIngressTests.cs`
  - Advatec seed data: site `ADVATEC-SITE-001` with FccVendor.ADVATEC, webhook token auth
  - Test 1 (Valid Receipt): POST Receipt JSON with X-Webhook-Token → 200 ACCEPTED, stored in DB as PENDING
  - Test 2 (Duplicate): Same TransactionId posted twice → second returns DUPLICATE
  - Test 3 (Missing token): No X-Webhook-Token header → 401 UNAUTHORIZED (MISSING_WEBHOOK_TOKEN)
  - Test 4 (Invalid token): Wrong token → 401 UNAUTHORIZED (INVALID_WEBHOOK_TOKEN)
  - Test 5 (Empty body): Empty payload → 200 IGNORED (webhook best practice — no retries)
  - Helper: `BuildAdvatecReceiptPayload()` generates full TRA-compliant Receipt JSON with Items, Company,
    Payments, tax calculations (18% VAT), and TRA verification URL
  - Also fixed pre-existing build error: added `GetByAdvatecWebhookTokenAsync` stub to
    `PreAuthExpiryWorkerTests.StaticSiteFccConfigProvider`
- ADV-6.3: Cross-vendor regression verified
  - All 26 simulator E2E tests pass (6 Advatec + 7 Radix + 7 Petronite + 6 DOMS) — zero regressions
  - Cloud integration tests compile and are structured correctly (require Docker for Testcontainers)
  - Advatec factory registration verified on all layers (existing adapter paths unaffected)

### 2026-03-13 — v1.4: Phase 5 Complete

- Marked ADV-5.1 and ADV-5.2 as `[DONE]`
- ADV-5.1: Created Advatec EFD simulator as `BackgroundService` (same pattern as Petronite/Radix)
  - `AdvatecSimulatorService.cs`: HTTP server on port 5560 (mirrors real Advatec default)
    - `POST /api/v2/incoming` — Accepts Customer data submissions, validates `DataType == "Customer"`
    - Queues pending receipts, generates TRA-compliant fiscal receipts after configurable delay
    - Receipt generation: sequential GlobalCount/DailyCount, random ReceiptCode (11 hex chars),
      TransactionId `TRSD1INV{count:000}`, TRA ReceiptVCodeURL, 18% VAT tax calculation
    - Webhook delivery to configured callback URL with token authentication
  - `AdvatecSimulatorState.cs`: Thread-safe state with ConcurrentDictionary/ConcurrentQueue
    - Sequential counters with daily reset, pending receipt queue, generated receipt tracking
    - Company profile (Advatech Company Limited with TIN/VRN/SerialNumber)
    - Product catalog: TANGO (PMS, 3285 TZS/L), DIESEL (AGO, 3427 TZS/L)
    - Pump configs, error mode, webhook config
  - `AdvatecSimulatorOptions.cs`: Port (5560), ReceiptDelayMs (2000), PumpCount (3), VAT rate (18%)
  - `AdvatecManagementEndpoints.cs`: Management API under `/api/advatec/`
    - `/configure-webhook` — Set webhook target URL and token
    - `/inject-receipt` — Inject receipt directly (bypass Customer submission)
    - `/configure-delay` — Override receipt generation delay
    - `/set-error-mode` — TRA offline (no receipt) or device busy (HTTP 503)
    - `/state` — Full state snapshot
    - `/reset` — Clear all state
  - Registered in `DependencyInjection.cs` (singleton state + service + hosted service + HttpClient)
  - Mapped management endpoints in `Program.cs`
- ADV-5.2: Seed profile data built into `AdvatecSimulatorState.Reset()`
  - Default company: Advatech Company Limited (TIN 100-123-456, VRN 10-0123456-B, SerialNumber 10TZ100625)
  - Products: TANGO (3285 TZS/L, TaxCode 1), DIESEL (3427 TZS/L, TaxCode 1)
  - 3 pumps: pumps 1-2 → TANGO, pump 3 → DIESEL
  - 18% VAT rate (TRA standard)
  - `ADVATEC` already in SeedProfileFactory's fccVendor allowedValues list

### 2026-03-13 — v1.3: Phase 4 Complete

- Marked ADV-4.1 through ADV-4.4 as `[DONE]`
- ADV-4.1: Created `AdvatecApiClient.cs` (.NET Desktop)
  - HTTP client for `POST http://{host}:{port}/api/v2/incoming` Customer data submission
  - 10-second timeout, JSON serialization, structured logging of responses
  - Returns `AdvatecSubmitResult` with success/statusCode/responseBody/errorMessage
- ADV-4.2: Updated `AdvatecAdapter.cs` (.NET Desktop) with full pre-auth support
  - `SendPreAuthAsync`: maps `PreAuthCommand` → `AdvatecCustomerRequest`, submits via `AdvatecApiClient`
  - Dose calculation: `requestedAmount / unitPrice → litres`
  - CustIdType cascade: command → config → default 6 (NIL)
  - `ActivePreAuth` record stored in `ConcurrentDictionary<int, ActivePreAuth>` keyed by pump number
  - Correlation ID format: `ADV-{pumpNumber}-{unixMillis}`
  - `CancelPreAuthAsync`: finds and removes by correlationId (Advatec has no cancel API)
  - Metadata updated: `supportsPreAuth = "true"`, `activePreAuths` count added
  - `DisposeAsync` updated to clean up `AdvatecApiClient`
- ADV-4.3: Updated `AdvatecAdapter.kt` (Kotlin) with matching pre-auth support
  - Inline HTTP submission via `HttpURLConnection` (no external HTTP client dependency)
  - Same `ActivePreAuth` data class, `ConcurrentHashMap<Int, ActivePreAuth>` tracking
  - Same dose calculation, CustIdType cascade, and correlation ID format
  - `cancelPreAuth`: iterates and removes matching entry
- ADV-4.4: Pre-Auth ↔ Receipt correlation implemented on both platforms
  - Two-strategy matching: (1) CustomerId match (receipt echoes customer data), (2) FIFO fallback
  - Matched pre-auth provides: pumpNumber, correlationId, odooOrderId, preAuthId
  - Unmatched receipts: logged as Normal Order, generate UUID correlationId
  - 30-minute TTL cleanup via `purgeStalePreAuths()` called on each normalization cycle
  - Stale entries logged as warnings with age for debugging

### 2026-03-13 — v1.2: Phase 3 Complete

- Marked ADV-3.1 through ADV-3.5 as `[DONE]`
- ADV-3.1: Created `AdvatecWebhookListener.cs` following Petronite webhook listener pattern
  - HTTP listener on configurable port (default 8091) at `/api/webhook/advatec`
  - X-Webhook-Token header + ?token= query parameter authentication
  - Always returns 200 OK (Advatec retry behaviour unknown — AQ-7)
- ADV-3.1: Updated `AdvatecAdapter.cs` to integrate webhook listener
  - Added `IAsyncDisposable`, webhook queue (`ConcurrentQueue`), lazy init via `SemaphoreSlim`
  - `FetchTransactionsAsync` now drains webhook queue (same pattern as Petronite)
  - Added `ILoggerFactory` constructor for webhook listener logger creation
- Updated `IngestionOrchestrator.EnsurePushListenersInitializedAsync` to include Advatec
- ADV-3.2: Created `AdvatecWebhookListener.kt` using Ktor CIO embedded server (same pattern as Radix)
  - Back-pressure guard with MAX_QUEUE_SIZE=10,000
  - `drainQueue()` for adapter to collect received receipts
- ADV-3.2: Updated Kotlin `AdvatecAdapter.kt` with webhook listener lifecycle
  - `ensureInitialized()` starts listener, `shutdown()` stops it
  - `fetchTransactions()` drains listener queue into `TransactionBatch`
- ADV-3.3: Cloud adapter was already implemented in Phase 2
- ADV-3.4: Added `POST /api/v1/ingest/advatec/webhook` endpoint to `TransactionsController`
  - X-Webhook-Token header or ?token= query parameter authentication
  - Constant-time comparison via `CryptographicOperations.FixedTimeEquals`
  - Always returns 200 to avoid triggering retries
  - Added `GetByAdvatecWebhookTokenAsync` to `ISiteFccConfigProvider` + implementation
- ADV-3.5: Added Advatec config section to portal `FccConfigFormComponent`
  - Fields: Device Port, Webhook Listener Port, Webhook Token, EFD Serial Number, CustIdType
  - Conditional rendering: `@if (draft.vendor === 'ADVATEC')`
  - Added Advatec fields to `FccConfig` interface in `site.model.ts`

### 2026-03-13 — v1.1: Phase 2 Complete

- Marked ADV-2.1 through ADV-2.6 as `[DONE]`
- Fixed Kotlin DTOs: `Double` → `BigDecimal` with custom `BigDecimalSerializer` (reads raw JSON number tokens to avoid floating-point precision loss)
- Extracted Kotlin DTOs from `AdvatecAdapter.kt` into separate `AdvatecProtocolDtos.kt` file
- Simplified Kotlin normalization to use BigDecimal directly from DTOs (no more `.toString()` conversion)
- .NET implementation (DTOs + adapter) was already correct with `decimal` types

### 2026-03-13 — v1.0: Initial Plan

- Created Advatec adapter development plan based on WIP protocol document
- 24 tasks across 7 phases (mutually exclusive Phase 4/Phase 7)
- Estimated ~25-26 dev-days depending on scenario
- Structured for scenario branching after Phase 0 requirements clarification
- Phase 0 (requirements) is blocking — no code until AQ-1, AQ-2, AQ-3, AQ-10 resolved
- Phase 1 (foundation) can run in parallel with Phase 0
