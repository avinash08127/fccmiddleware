# Advatec FCC Adapter — Integration Plan

Version: 0.1 (WIP — Severely Documentation-Constrained)
Last Updated: 2026-03-12

------------------------------------------------------------------------

## 0. Critical Warning: Incomplete Vendor Documentation

**The available Advatec documentation (2-page "PUMA API DOCUMENTATIONS") describes only two data types — a Customer submission endpoint and a Receipt webhook. This covers fiscal receipt generation, NOT forecourt controller operations (pump control, pre-authorization, transaction ingestion, pump status).** The documentation gap between what the requirements assume (REQ-3: Advatec as a full FCC vendor) and what is actually documented is enormous.

This plan is written based on the available documentation plus analysis of the receipt payload structure. **Over half of the adapter interface methods cannot be planned** because the relevant Advatec APIs are either undocumented or may not exist. The open questions section (Section 10) is the most critical part of this document.

**Before any implementation can begin, the business team must clarify the fundamental question: Is Advatec a full FCC (pump control + fiscalization), or is it primarily a fiscal device (EFD/VFD) that handles TRA compliance?**

------------------------------------------------------------------------

## 1. Executive Summary

The Advatec system, as documented, is a **TRA-compliant Electronic Fiscal Device (EFD/VFD)** deployed at fuel stations in **Tanzania**. It runs locally on a device at the station (port `5560` on `127.0.0.1`) and provides two documented integration points:

1. **Inbound (POS → Advatec):** Submit customer/transaction data to generate a fiscal receipt with TRA. `POST http://127.0.0.1:5560/api/v2/incoming` with `DataType: "Customer"`.
2. **Outbound (Advatec → Webhook):** Completed fiscal receipt data pushed via HTTP POST to a pre-configured webhook URL. `DataType: "Receipt"`.

The current production integration sends receipts to a **Kissflow workflow** (`pumaenergy.kissflow.eu`). Our middleware would replace this webhook target.

### What the Documentation Covers vs What the Adapter Needs

| IFccAdapter Capability | Documented? | Available? | Notes |
|---|---|---|---|
| **NormalizeTransaction** (parse incoming transaction) | Partially | Yes — Receipt webhook | Receipt data can be normalized to CanonicalTransaction |
| **SendPreAuthAsync** (authorize a pump) | **No** | **Unknown** | No pump authorization API documented. The Customer endpoint submits data AFTER dispensing, not before. |
| **FetchTransactionsAsync** (pull transactions) | **No** | **Unknown** | No pull/fetch endpoint documented |
| **GetPumpStatusAsync** (pump state) | **No** | **Unknown** | No pump status endpoint documented |
| **HeartbeatAsync** (health check) | **No** | **Unknown** | No health endpoint documented |
| **CancelPreAuthAsync** (cancel authorization) | **No** | **Unknown** | No cancel endpoint documented |
| Push ingestion (webhook) | Yes | Yes | Receipt webhook is the documented ingestion path |
| Fiscalization | Yes | **Core capability** | This IS the primary function of the documented API |

### Key Differences from DOMS, Radix, and Petronite

| Aspect | DOMS | Radix | Petronite | Advatec |
|--------|------|-------|-----------|---------|
| Primary role | FCC (pump control) | FCC (pump control) | FCC via bot (pump control) | **Fiscal device (TRA EFD/VFD)** |
| Payload format | JSON | XML | JSON | JSON |
| Authentication | API key header | SHA-1 HMAC signing | OAuth2 Client Credentials | **None documented** |
| Pre-auth | Yes (single-step) | Yes (single-step) | Yes (two-step) | **Not documented** |
| Transaction push | FCC POSTs JSON | FCC POSTs XML with ACK | Webhook JSON | **Receipt webhook JSON** |
| Transaction pull | Cursor-based GET | FIFO drain (CMD_CODE=10) | Not available | **Not documented** |
| Fiscal receipt | Field in transaction | EFD_ID in TRN | receipt_code | **Full TRA receipt with verification URL** |
| Tax details | None | None | None | **Full per-item tax breakdown** |
| Payment info | None | None | payment_method field | **Full multi-payment breakdown** |
| TRA verification | None | None | None | **`ReceiptVCodeURL` → TRA online verification** |
| Runs on | FCC hardware | FCC hardware | Separate "bot" device on LAN | **Local device (localhost:5560)** |
| Country scope | Multi-country | Multi-country (Tanzania focused) | Multi-country | **Tanzania only** (TRA-specific) |

------------------------------------------------------------------------

## 2. Advatec Protocol Deep Dive

### 2.1 Architecture — EFD/VFD on Station LAN

Based on the documentation, the Advatec system is a **TRA-registered Electronic Fiscal Device** that:

- Runs on a local device at the station, accessible at `127.0.0.1:5560`
- Communicates with TRA (Tanzania Revenue Authority) to generate fiscal receipts
- Has a TRA-assigned serial number (`SerialNumber: "10TZ101807"`) and registration ID (`RegistrationId: "TZ0100557003"`)
- Generates receipts with TRA verification URLs (`https://virtual.tra.go.tz/efdmsrctverify/...`)
- Maintains Z-number tracking (daily fiscal reporting)

**The relationship between Advatec and pump control is unclear from the documentation.** Possibilities:

1. **Advatec IS the FCC** — Controls pumps AND handles fiscalization. The pump control APIs exist but are not in this document (this document only covers the PUMA-specific fiscal integration).
2. **Advatec is a fiscal-only device** — A separate FCC (e.g., another vendor or manual pumps) handles dispensing. Advatec only receives transaction data post-dispense and generates fiscal receipts.
3. **Advatec is a combined system** — Controls pumps via its own interface (e.g., the Advatec native app), and the documented API is the fiscal/integration layer exposed to external POS systems.

**This must be resolved before implementation** — see AQ-1.

### 2.2 Customer Data Submission (Inbound)

**Endpoint:** `POST http://127.0.0.1:5560/api/v2/incoming`

**No authentication documented** — the request does not include any API key, token, or auth header. This may be acceptable since it's localhost-only, but needs confirmation.

**Payload:**
```json
{
  "DataType": "Customer",
  "Data": {
    "Pump": 3,
    "Dose": 12.5,
    "CustIdType": 1,
    "CustomerId": "999999999",
    "CustomerName": "NJAMA",
    "Payments": [
      { "PaymentType": "CASH", "PaymentAmount": 1000.98 },
      { "PaymentType": "CCARD", "PaymentAmount": 1000.98 }
    ]
  }
}
```

**Field Analysis:**

| Field | Type | Our Mapping | Notes |
|-------|------|-------------|-------|
| `Pump` | Integer | `pumpNumber` | Pump where transaction occurred — implies Advatec knows about pumps |
| `Dose` | Decimal | Volume (litres) | "Quantity dispensed" — past tense, suggesting this is POST-dispense data |
| `CustIdType` | Integer | Customer ID type | TRA standard: 1=TIN, 2=DL, 3=Voters, 4=Passport, 5=NID, 6=NIL, 7=MeterNo. Same numbering as Radix. |
| `CustomerId` | String | `customerTaxId` | Customer identification number |
| `CustomerName` | String | `customerName` | |
| `Payments` | Array | (not in canonical model) | **Split payment support** — CASH, CCARD, EMONEY, INVOICE, CHEQUE |
| `Payments[].PaymentAmount` | Decimal | (not in canonical model) | Amount per payment method |

**Critical observation:** The `Dose` field is described as "Quantity dispensed" and the Payments include actual amounts paid. This strongly suggests this data is submitted **AFTER fuel is dispensed**, not as a pre-authorization request. This is a fiscalization submission, not a pre-auth.

**Possible adapter use:** If Advatec does NOT control pumps, then after a Normal Order is dispensed (via a separate FCC or manually), the Edge Agent could submit the Customer data to Advatec to trigger fiscal receipt generation. This would make Advatec a **fiscalization integration**, not an FCC adapter per se.

### 2.3 Receipt Webhook (Outbound)

**Currently configured to:** `https://pumaenergy.kissflow.eu/integration/2/AcuVVWae50gm/webhook/...`

**Payload:**
```json
{
  "DataType": "Receipt",
  "Data": {
    "Date": "2025-10-01",
    "Time": "10:41:38",
    "ZNumber": 20251001,
    "ReceiptCode": "6BF00C46747",
    "TransactionId": "TRSD1INV009",
    "CustomerIdType": 1,
    "CustomerIdType_": "TIN",
    "CustomerId": "133353353",
    "CustomerName": "DEFAULT CUSTOMER",
    "CustomerPhone": "0778855998",
    "TotalDiscountAmount": 1500,
    "DailyCount": 18,
    "GlobalCount": 46747,
    "ReceiptNumber": 46747,
    "AmountInclusive": 4285000,
    "AmountExclusive": 3958216.1,
    "TotalTaxAmount": 326783.9,
    "AmountPaid": 4285000,
    "Items": [
      {
        "Price": 214325,
        "Amount": 2142250,
        "TaxCode": "A",
        "Quantity": 10,
        "TaxAmount": 326783.9,
        "Product": "TANGO",
        "TaxId": 1,
        "DiscountAmount": 1000,
        "TaxRate": 18
      }
    ],
    "Company": {
      "TIN": "153133352",
      "VRN": "40005444W",
      "City": "DAR ES SALAAM",
      "Region": "Ilala",
      "Mobile": "+255 757 944 365",
      "Street": "ILALA PLOT NO. 810/8200",
      "Country": "TANZANIA",
      "TaxOffice": "Tax Office Ilala",
      "SerialNumber": "10TZ101807",
      "RegistrationId": "TZ0100557003",
      "UIN": "09VFDWEBAPI10131759015313335210TZ101807",
      "Name": "ADVATECH COMPANY LIMITED."
    },
    "ReceiptVCodeURL": "https://virtual.tra.go.tz/efdmsrctverify/6BF00C46747_104138"
  }
}
```

**This is the richest transaction payload of any vendor** — it includes full fiscal data, tax breakdown, payment methods, company information, and TRA verification URL.

### 2.4 Amount Format Analysis

From the sample receipt:
- `Items[0].Price = 214325`, `Items[0].Quantity = 10`, `Items[0].Amount = 2142250`
- `214325 × 10 = 2143250`, with `DiscountAmount = 1000`: `2143250 - 1000 = 2142250` ✓
- `AmountInclusive = 4285000`, `TotalTaxAmount = 326783.9`
- `AmountInclusive - TotalTaxAmount = 4285000 - 326783.9 = 3958216.1 = AmountExclusive` ✓

**Tanzania Shilling (TZS)** has 0 effective decimal places (cents are not used). If these amounts are in TZS:
- Price per litre = 214,325 TZS — **unreasonably high** (fuel in Tanzania is ~3,000-3,500 TZS/L in 2025-2026)
- If divided by 100: 2,143.25 TZS/L — **too low**

**If the amounts are in a minor unit (hundredths):**
- Price = 214325 / 100 = 2,143.25 TZS/L — plausible if this is slightly older pricing
- AmountInclusive = 4285000 / 100 = 42,850 TZS for 10 litres — plausible

**But TZS doesn't use minor units in practice.** The amount interpretation is ambiguous and **must be confirmed** — see AQ-2.

### 2.5 CustIdType System

Advatec uses the **TRA-standard customer identification type codes**, identical to Radix:

| Code | Type | Our Mapping |
|------|------|-------------|
| 1 | TIN (Tax ID Number) | `customerTaxId` — primary use case for fiscalized invoices |
| 2 | Driving License | Extended customer ID |
| 3 | Voters Number | Extended customer ID |
| 4 | Passport | Extended customer ID |
| 5 | NID (National ID) | Extended customer ID |
| 6 | NIL (No ID) | Default when no customer ID |
| 7 | Meter No | Utility-specific |

This is a Tanzania/TRA standard, not Advatec-specific. The same numbering appears in Radix's `CUSTIDTYPE` field.

### 2.6 Multi-Payment Support

Advatec is the only vendor that supports **split payments** in the transaction data:

```json
"Payments": [
  { "PaymentType": "CASH", "PaymentAmount": 1000.98 },
  { "PaymentType": "CCARD", "PaymentAmount": 1000.98 }
]
```

Payment types: `CASH`, `CCARD`, `EMONEY`, `INVOICE`, `CHEQUE`

Our canonical model does not have payment fields. For MVP, the payment data would be preserved in the raw payload. If payment tracking is needed, the canonical model or a supplementary record would need extension.

### 2.7 Company Data

The Receipt includes full company details — this is the **site operator's company information** registered with TRA:

| Field | Example | Our Mapping |
|-------|---------|-------------|
| `Company.TIN` | "153133352" | `companyTaxPayerId` from site config (REQ-2) |
| `Company.VRN` | "40005444W" | VRN — TRA-specific, not in our model |
| `Company.SerialNumber` | "10TZ101807" | EFD device serial — informational |
| `Company.RegistrationId` | "TZ0100557003" | TRA registration — informational |
| `Company.Name` | "ADVATECH COMPANY LIMITED." | Operator name — matches site config |

This data is static per site and can be validated against our site configuration.

------------------------------------------------------------------------

## 3. Inferred Integration Flow

Based on the available documentation, the most likely integration flow for Advatec sites is:

### Scenario A: Advatec as Fiscal-Only Device (Separate FCC for Pump Control)

```
[Separate FCC] ──── controls pumps ────► [Pump]
       │
       ├── Push transaction to Cloud Middleware (via DOMS/Radix/other adapter)
       │
[Edge Agent] polls FCC over LAN
       │
       ├── After dispense: sends Customer data to Advatec
       │        POST http://127.0.0.1:5560/api/v2/incoming
       │
[Advatec EFD] ──── generates fiscal receipt with TRA ────► Receipt webhook
       │
       └── Webhook → Cloud Middleware (or Edge Agent)
```

In this scenario:
- Transaction ingestion comes from the **real FCC** (DOMS, Radix, etc.)
- Advatec is called as a **post-processing step** for fiscalization
- The Advatec "adapter" is actually a **fiscalization service**, not an `IFccAdapter`
- This would be implemented under `fiscalizationMode = FCC_DIRECT` but the "FCC" doing fiscalization is the Advatec EFD, not the pump controller

### Scenario B: Advatec as Full FCC (Undocumented Pump Control APIs)

```
[Advatec System] ──── controls pumps AND fiscalizes ────► [Pump]
       │
       ├── Customer data submitted for pre-auth (via undocumented API)
       │
       ├── Receipt webhook with dispense data + fiscal receipt
       │
[Edge Agent] ◄──── webhook ────── [Advatec]
       │
       └── Upload to Cloud Middleware
```

In this scenario:
- Advatec handles everything — pump control + fiscalization
- There MUST be additional APIs for pre-auth, pump status, etc. (not in this document)
- The documented Customer endpoint might be a pre-auth trigger, not a post-dispense submission

### Scenario C: Advatec as Pump + Fiscal (Customer = Pre-Auth Trigger)

If the Customer data submission actually **triggers** pump authorization:

```
[Odoo POS] → [Edge Agent] → POST Customer data to Advatec
                                    │
                              [Advatec locks pump, authorizes]
                                    │
                              [Fuel dispensed]
                                    │
                              [Advatec generates TRA fiscal receipt]
                                    │
                              Receipt webhook → [Edge Agent/Cloud]
```

In this scenario:
- Sending Customer data = pre-auth (locks pump, sets dose limit)
- The `Dose` field becomes the volume limit, not "quantity dispensed"
- After dispensing, the Receipt webhook contains the actual transaction
- No separate authorize step (unlike Petronite's two-step)

**This scenario is plausible** because:
- The Customer endpoint sends `Pump` and `Dose` — you wouldn't need the pump number for a post-dispense fiscal submission
- The endpoint is called `/api/v2/incoming` — generic enough for commands
- But `Dose` is described as "Quantity dispensed (e.g., fuel liters)" — past tense

**The scenario must be confirmed** — see AQ-1.

------------------------------------------------------------------------

## 4. Field Mapping: Receipt Webhook → CanonicalTransaction

Regardless of which scenario applies, the Receipt webhook will be our primary transaction ingestion source. Here's the mapping:

| Advatec Receipt Field | Type | Canonical Field | Type (Canonical) | Conversion |
|---|---|---|---|---|
| `TransactionId` | String | `fccTransactionId` | String | Direct mapping. Example: `"TRSD1INV009"` |
| (from config) | — | `siteCode` | String | Injected from adapter config |
| (not in receipt) | — | `pumpNumber` | Int | **Not directly in Receipt.** Must be correlated from the Customer submission (same TransactionId?) or from additional data. See AQ-3. |
| (not in receipt) | — | `nozzleNumber` | Int | **Not in Receipt.** Same issue as pump. |
| `Items[0].Product` | String | `productCode` | String | Map via `productCodeMapping`: e.g., `"TANGO" → "PMS"`. Fuel receipts likely have a single item. |
| `Items[0].Quantity` | Decimal | `volumeMicrolitres` | Long | `(long)(Quantity * 1_000_000m)` — Quantity is in litres |
| `AmountInclusive` | Decimal | `amountMinorUnits` | Long | Currency conversion needed — see AQ-2. If already in minor units: direct. If in major TZS: multiply by 1 (TZS has 0 decimal places). |
| `Items[0].Price` | Decimal | `unitPriceMinorPerLitre` | Long | Same currency conversion as amount |
| (from config) | — | `currencyCode` | String | `"TZS"` — Tanzania only |
| `Date` + `Time` | String + String | `startedAt` / `completedAt` | DateTimeOffset | Parse `"yyyy-MM-dd"` + `"HH:mm:ss"`, apply `Africa/Dar_es_Salaam` timezone → UTC. Only one timestamp available — use for `completedAt`. |
| `ReceiptCode` | String | `fiscalReceiptNumber` | String | Direct mapping. This is the TRA fiscal receipt code. |
| — | — | `fccVendor` | Enum | `FccVendor.ADVATEC` |
| — | — | `attendantId` | String? | Not available. Null. |
| `CustomerIdType` + `CustomerId` | Int + String | (raw payload) | — | Preserved for pre-auth reconciliation |
| `CustomerName` | String | (raw payload) | — | Preserved |
| `ReceiptVCodeURL` | String | (raw payload) | — | TRA verification URL — valuable for compliance audit |

**Missing critical fields in Receipt webhook:**
- **Pump number** — The Receipt does not include which pump dispensed the fuel. The Customer submission has `Pump`, but the Receipt does not echo it back. This makes pump/nozzle mapping impossible without correlation.
- **Nozzle number** — Not present in either direction.
- **Start time** — Only completion time available.

### Additional Fields Available (Not in Canonical Model)

| Receipt Field | Value | Potential Use |
|---|---|---|
| `ZNumber` | Daily Z report number | Fiscal audit trail |
| `DailyCount` | Receipt sequence for the day | Duplicate detection secondary key |
| `GlobalCount` | Lifetime receipt sequence | Alternative dedup key |
| `ReceiptNumber` | Printed receipt number | Audit |
| `AmountExclusive` | Amount before tax | Tax reporting |
| `TotalTaxAmount` | Total tax | Tax reporting |
| `TotalDiscountAmount` | Discount | Financial reconciliation |
| `Items[].TaxCode`, `TaxRate`, `TaxAmount` | Per-item tax breakdown | Detailed tax reporting |
| `Payments[]` | Payment method breakdown | Payment reconciliation |
| `Company.*` | Operator registration details | Compliance validation |
| `ReceiptVCodeURL` | TRA verification link | Regulatory audit |

------------------------------------------------------------------------

## 5. Customer Data Field Mapping: PreAuthCommand → Advatec Customer

**If Scenario C applies** (Customer submission = pre-auth trigger):

| PreAuthCommand Field | Advatec Customer Field | Conversion |
|---|---|---|
| `FccPumpNumber` | `Pump` | Direct integer mapping |
| `RequestedAmountMinorUnits` / `UnitPriceMinorPerLitre` | `Dose` | **Problem:** Our pre-auth is by amount (BR-6.1b), but Advatec `Dose` is in litres. Must convert: `Dose = RequestedAmountMinorUnits / UnitPriceMinorPerLitre`. Or Advatec may support amount-based dosing (undocumented). See AQ-4. |
| `CustomerTaxId` | `CustomerId` | Direct mapping |
| — | `CustIdType` | `1` (TIN) when customerTaxId is present. Map from our ID type if we add support. |
| `CustomerName` | `CustomerName` | Direct mapping |
| — | `Payments` | **Unclear.** Payments are for post-dispense fiscal receipts. In a pre-auth flow, payment may not be known yet. See AQ-5. |

**If Scenario A applies** (Customer submission = post-dispense fiscal):

The Customer endpoint is called AFTER the transaction is already completed and ingested via the real FCC. Our middleware would:
1. Receive the transaction from the actual FCC adapter (DOMS/Radix)
2. Call the Advatec Customer endpoint to trigger fiscal receipt generation
3. Receive the Receipt webhook with the fiscal receipt data
4. Attach the fiscal receipt to the already-stored transaction

This would NOT be an `IFccAdapter` implementation — it would be a **fiscalization service** called by the transaction processing pipeline.

------------------------------------------------------------------------

## 6. Push Mode — Receipt Webhook

### 6.1 Webhook Endpoint

The Advatec system pushes completed receipts to a pre-configured URL. Currently configured to Kissflow; we would reconfigure to our endpoint.

**Current target:** `https://pumaenergy.kissflow.eu/integration/2/.../webhook/...`
**New target:** Cloud middleware or Edge Agent endpoint

**Requirements:**
- Accept `Content-Type: application/json`
- Parse the `DataType: "Receipt"` wrapper
- Extract the `Data` object
- Normalize to canonical model
- Respond with HTTP 200

**No special ACK format needed** — standard HTTP 200 response.

### 6.2 Webhook Authentication

**Not documented.** The current Kissflow webhook URL has a long token embedded in the path, suggesting URL-based authentication. Options for our endpoint:

| Option | Mechanism | Notes |
|---|---|---|
| **URL token** | Embed a secret token in the webhook URL path | Matches current Kissflow pattern. Simple but the token is visible in logs. |
| **Shared secret header** | Advatec includes a secret header | Needs Advatec to support it. |
| **IP allowlisting** | Only accept from Advatec device IP | Acceptable for LAN (localhost). |

### 6.3 Ingestion Mode Implications

Since Advatec runs on `127.0.0.1:5560`, it's on the **same device** as the POS or Edge Agent. This means:
- **CLOUD_DIRECT is NOT possible** — Advatec cannot push to a cloud URL (localhost only)
- **RELAY is the natural fit** — Edge Agent receives webhook locally, relays to cloud
- **BUFFER_ALWAYS also works** — Edge Agent buffers locally first

**Default ingestion mode for Advatec: `RELAY`** (Edge Agent receives locally, forwards to cloud).

### 6.4 No Pull Mode

No transaction fetch/pull endpoint is documented. Like Petronite, Advatec appears to be **push-only via webhook**.

------------------------------------------------------------------------

## 7. Integration Points Analysis

### 7.1 Adapter Interface Implementation

| Interface Method | Advatec Implementation | Status |
|---|---|---|
| `NormalizeAsync` / `NormalizeTransaction` | Parse Receipt webhook JSON → canonical model. Map Items[0] to volume/amount/price. Compose fccTransactionId from TransactionId. | **Implementable** with caveats (missing pump/nozzle) |
| `SendPreAuthAsync` | **Scenario C:** POST Customer data → Advatec `/api/v2/incoming`. **Scenario A:** Not applicable (separate FCC). | **Blocked on AQ-1** |
| `FetchTransactionsAsync` | **Not implementable** — no pull API documented. Return empty batch. | Push-only |
| `GetPumpStatusAsync` | **Not implementable** — no pump status API. Return empty list. | Not available |
| `HeartbeatAsync` | Attempt a GET/POST to `127.0.0.1:5560` and check for response. No documented health endpoint — may need to use `/api/v2/incoming` with a benign request or TCP connect check. | **Best-effort** |
| `CancelPreAuthAsync` | **Not documented.** If Scenario C, no cancel mechanism is described. | **Blocked on AQ-1** |
| `ValidatePayload` | Check DataType = "Receipt", required fields present | Implementable |

### 7.2 What Needs to Be Created (New Files)

#### Desktop Edge Agent (.NET)

| File | Description |
|---|---|
| `Adapter/Advatec/AdvatecAdapter.cs` | Main adapter — webhook normalization, Customer data submission (if Scenario C) |
| `Adapter/Advatec/AdvatecProtocolDtos.cs` | JSON DTOs: CustomerRequest, ReceiptWebhook, PaymentItem, ReceiptItem, CompanyInfo |
| Tests | Unit tests for normalization and Customer submission |

#### Cloud Backend (.NET)

| File | Description |
|---|---|
| `FccMiddleware.Adapter.Advatec/AdvatecCloudAdapter.cs` | Cloud-side — Receipt webhook normalization and validation |
| `FccMiddleware.Adapter.Advatec/Internal/AdvatecReceiptDto.cs` | Receipt webhook payload DTOs |
| `FccMiddleware.Adapter.Advatec/FccMiddleware.Adapter.Advatec.csproj` | Project file |

### 7.3 What Needs to Be Modified (Existing Files)

| File | Change | Reason |
|---|---|---|
| `Enums.cs` (Edge Agent) | Add `Advatec` to `FccVendor` enum | Already exists in cloud as `ADVATEC` |
| `FccAdapterFactory.cs` (Edge + Cloud) | Register Advatec adapter | Factory resolution |
| Cloud push ingress | Accept Advatec Receipt webhook format (JSON with DataType wrapper) | Webhook ingestion |
| `SiteFccConfig` / `FccConnectionConfig` | May need additional fields for Advatec-specific config (EFD serial, TRA registration) | Site provisioning |
| VirtualLab `SeedProfileFactory.cs` | Add Advatec simulation profile | Testing |

------------------------------------------------------------------------

## 8. Deduplication Strategy

### Primary Key

`TransactionId` + `siteCode` — where `TransactionId` comes from the Receipt webhook (e.g., `"TRSD1INV009"`).

### Secondary/Alternative Keys

| Candidate | Fields | Reliability |
|---|---|---|
| `ReceiptCode` + `siteCode` | TRA fiscal receipt code — globally unique within TRA | High — TRA assigns these |
| `GlobalCount` + `Company.SerialNumber` | Lifetime sequence + device serial | High — monotonically increasing per device |
| `DailyCount` + `Date` + `Company.SerialNumber` | Daily sequence + date + device | Medium — resets daily |

**Recommendation:** Use `TransactionId` as primary. Use `ReceiptCode` as a validation cross-check (if they diverge, something is wrong).

------------------------------------------------------------------------

## 9. Differences Requiring Architectural Decisions

### 9.1 Advatec's Role: FCC Adapter vs Fiscalization Service

This is the **single most important architectural decision** for Advatec integration.

| Option | Architecture | When to Use |
|---|---|---|
| **A. Advatec as IFccAdapter** | Standard adapter implementing `IFccAdapter`. Handles both pump control (via undocumented APIs) and fiscalization. Receipt webhook is the transaction push path. | If Advatec IS the FCC — controls pumps AND fiscalizes. Requires additional API documentation. |
| **B. Advatec as IFiscalizationService** | New interface. Called by the transaction pipeline after a Normal Order arrives from a different FCC. Submits Customer data to Advatec, receives Receipt webhook, attaches fiscal data to the existing transaction. | If Advatec is a fiscal-only device. The site would have TWO integrations: one FCC adapter (DOMS/Radix) + Advatec for fiscalization. |
| **C. Hybrid — Adapter with fiscal focus** | Implements `IFccAdapter` but most methods return empty/unsupported. Primary value is the Receipt webhook normalization. Customer data submission is a separate capability exposed for fiscal workflows. | Pragmatic middle ground that avoids a new interface. |

**Recommendation:** Start with **Option C** — implement as an `IFccAdapter` where `NormalizeTransaction` handles Receipt webhooks. Other methods return empty/false as documented. Add the Customer data submission as an Advatec-specific method. Once AQ-1 is resolved, evolve toward Option A or B.

### 9.2 Missing Pump/Nozzle in Receipt Webhook

The Receipt webhook does not include pump or nozzle numbers. This is a fundamental gap for our canonical model which requires both.

| Option | Approach | Trade-off |
|---|---|---|
| **A. Correlate with Customer submission** | When Edge Agent sends Customer data (Pump field), store a mapping: `TransactionId → pumpNumber`. When Receipt arrives, look up the pump. | Only works if the TransactionId is known at Customer submission time, or if there's another correlation key. |
| **B. Default pump number** | For Advatec-only sites, set pump = 0 or a sentinel value. | Breaks our data model assumptions. Odoo needs real pump numbers. |
| **C. Request Advatec to add pump to Receipt** | Ask Advatec to include the Pump field in their webhook payload. | Best solution. Requires vendor cooperation. |
| **D. Single-pump sites** | If Advatec is only used at single-pump sites, pump is always 1. | Very limiting assumption. |

**Recommendation:** Pursue **Option C** first (ask Advatec to include pump in webhook). Fall back to **Option A** if we control the Customer submission flow.

### 9.3 Amount/Currency Interpretation

The sample receipt shows `AmountInclusive: 4285000` for 10 litres of fuel in Tanzania (TZS). This could be:
- TZS 4,285,000 (major units) — ~$1,600 USD, impossibly expensive for 10L of fuel
- TZS 42,850.00 (amounts are in hundredths) — ~$16 USD, plausible
- Some internal unit specific to Advatec

**This must be resolved before implementation** — see AQ-2.

### 9.4 Dose Field: Litres vs Amount

Our pre-auth is always by **amount** (BR-6.1b). The Advatec Customer endpoint takes a `Dose` field described as "Quantity dispensed (fuel liters)" — this is **volume**, not amount.

If the Customer endpoint is used for pre-auth, we must either:
1. Convert our requested amount to volume: `dose = requestedAmount / unitPrice`
2. Determine if Advatec accepts an amount-based Dose (undocumented)
3. Accept that Advatec pre-auth is volume-based (diverges from BR-6.1b)

**See AQ-4.**

------------------------------------------------------------------------

## 10. Open Questions

| ID | Question | Impact | Priority |
|---|---|---|---|
| **AQ-1** | **Is Advatec a full FCC (controls pumps) or a fiscal-only device (EFD/VFD)?** Does the documented Customer endpoint trigger pump authorization, or is it a post-dispense fiscal submission? Are there additional Advatec APIs for pump control, pump status, pre-auth, and transaction fetch that are not in this document? | **Blocks entire adapter design.** Determines whether we build an `IFccAdapter` or an `IFiscalizationService`. | **P0 — Must resolve before any implementation** |
| **AQ-2** | **Amount format:** `AmountInclusive: 4285000` for 10L of fuel in TZS — is this in TZS major units (4.285M TZS, implausible), hundredths (42,850 TZS, plausible), or some other unit? What about `Items[].Price: 214325`? | **Corrupts all financial data if wrong.** | **P0** |
| **AQ-3** | **Pump number in Receipt webhook:** The Receipt webhook does not include the pump number. How do we determine which pump dispensed the fuel? Is there a correlation key between the Customer submission (which has `Pump`) and the Receipt? | Without pump number, we cannot correctly store or reconcile transactions. | **P0** |
| **AQ-4** | **Dose field semantics:** Is `Dose` always in litres (volume)? Can it be used for amount-based authorization? If Advatec is used for pre-auth, how do we authorize by amount when the field expects litres? | Our system requires amount-based pre-auth (BR-6.1b). Volume-based Dose conflicts with this. | **P1** (only relevant if Scenario B/C) |
| **AQ-5** | **Payments in Customer submission during pre-auth:** If Customer data is sent before dispensing (pre-auth), the payment details are not yet known. Is the Payments array optional? Can it be empty? Or is it only used post-dispense? | Determines Customer submission payload structure for pre-auth use case. | **P1** |
| **AQ-6** | **Authentication to Advatec API:** The documentation shows no auth headers. Is the API truly unauthenticated (relying on localhost-only access)? Or is there auth that's not in this document? | Security. If unauthenticated and only on localhost, the Edge Agent must run on the same device as Advatec. | **P1** |
| **AQ-7** | **Webhook retry behavior:** When the configured webhook endpoint is unreachable, does Advatec retry? How many times? What interval? Does it buffer receipts locally? | Reliability. If no retry, missed webhooks = lost fiscal data. | **P1** |
| **AQ-8** | **Webhook target configuration:** How is the webhook URL configured on the Advatec device? Is it a config file, admin UI, or API call? Can multiple webhook targets be configured? | Deployment/provisioning. We need to change from Kissflow to our endpoint. | **P1** |
| **AQ-9** | **Nozzle information:** The Customer endpoint has `Pump` but no nozzle field. The Receipt has neither. Does Advatec have a nozzle concept? Does it know which nozzle/product was used? | Affects nozzle mapping table requirements. | **P1** |
| **AQ-10** | **Normal order visibility:** At Advatec sites, does every fuel dispense (including normal orders without customer data) generate a Receipt webhook? Or only transactions where Customer data was submitted first? | If only fiscalized transactions generate receipts, Normal Orders are invisible to the middleware via Advatec. | **P0** |
| **AQ-11** | **Multi-item receipts:** For fuel transactions, is `Items` always a single-item array? Or can a single receipt cover multiple fuel products? | Affects normalization logic — do we take Items[0] only, or iterate? | **P2** |
| **AQ-12** | **Receipt `TransactionId` format:** What determines the `TransactionId` (e.g., `"TRSD1INV009"`)? Is it generated by Advatec, by TRA, or can it be set by the caller in the Customer submission? | If we can set it, we can embed our correlation ID for pre-auth matching. | **P1** |
| **AQ-13** | **Does Advatec have a transaction history/pull API** beyond what's in this document? For example, an endpoint to fetch past receipts by date range. | Would enable catch-up/recovery. Without it, we're strictly push-only. | **P1** |

------------------------------------------------------------------------

## 11. Implementation Plan

### Phase 0: Requirements Clarification (MUST COMPLETE FIRST)

**Goal:** Resolve AQ-1, AQ-2, AQ-3, and AQ-10 — these are blocking questions.

1. **Meet with Advatec deployment team** or request complete API documentation
2. **Determine Advatec's role** at sites — FCC vs fiscal-only device (AQ-1)
3. **Obtain amount format clarification** with worked examples (AQ-2)
4. **Confirm pump number availability** in Receipt webhook or correlation mechanism (AQ-3)
5. **Confirm Normal Order visibility** — does every dispense produce a Receipt? (AQ-10)
6. **Request full API documentation** if additional endpoints exist (pump control, status, fetch)

**Deliverable:** Updated version of this plan with Scenario A, B, or C confirmed and open questions resolved.

### Phase 1: Receipt Webhook Normalization

**Goal:** Parse and normalize Advatec Receipt webhooks into canonical transactions.

**Prerequisite:** AQ-2 (amount format) and AQ-3 (pump number) resolved.

1. **Create `AdvatecProtocolDtos`** — Receipt payload DTOs
   - `AdvatecWebhookEnvelope` (DataType, Data)
   - `AdvatecReceiptData` (all receipt fields)
   - `AdvatecReceiptItem` (Product, Quantity, Price, Amount, Tax fields)
   - `AdvatecCompanyInfo` (TIN, VRN, SerialNumber, etc.)
   - `AdvatecCustomerRequest` (Pump, Dose, CustIdType, CustomerId, CustomerName, Payments)

2. **Implement `NormalizeTransaction`**
   - Parse JSON Receipt webhook
   - Extract Items[0] for fuel transaction data
   - Convert volume/amount/price to canonical units (per AQ-2 resolution)
   - Map `TransactionId` → `fccTransactionId`
   - Map `ReceiptCode` → `fiscalReceiptNumber`
   - Handle pump/nozzle mapping (per AQ-3 resolution)
   - Apply timezone conversion (`Africa/Dar_es_Salaam` → UTC)

3. **Implement `ValidatePayload`**
   - Check DataType = "Receipt"
   - Check required fields: TransactionId, Items (non-empty), AmountInclusive
   - Validate Items[0] has Product, Quantity, Price

4. **Register Advatec in `FccVendor` enum and adapter factory**

5. **Unit tests** with sample receipt payloads

### Phase 2: Webhook Endpoint

**Goal:** Receive Advatec Receipt webhooks in the Edge Agent and Cloud.

1. **Edge Agent webhook listener**
   - Route: `POST /api/webhook/advatec`
   - Accept JSON, validate DataType = "Receipt"
   - Normalize via adapter
   - Buffer locally, relay to cloud per ingestion mode
   - Respond with HTTP 200

2. **Cloud webhook ingress** (if CLOUD_DIRECT is ever viable — unlikely since Advatec is localhost)
   - Route: `POST /api/v1/ingest/advatec/webhook`
   - Same validation and normalization
   - Standard dedup → store pipeline

3. **Webhook target configuration documentation**
   - Document how to reconfigure Advatec's webhook from Kissflow to Edge Agent endpoint

### Phase 3: Customer Data Submission (Conditional)

**Goal:** Submit Customer/payment data to Advatec for fiscalization.

**Only if Scenario A or C is confirmed (AQ-1).**

1. **Implement Customer data submission**
   - Build `AdvatecCustomerRequest` payload
   - POST to `http://127.0.0.1:5560/api/v2/incoming`
   - Map PreAuthCommand fields to Advatec Customer fields
   - Handle Dose conversion (amount → volume if needed, per AQ-4)

2. **Implement `SendPreAuthAsync`** (if Scenario C — Customer = pre-auth trigger)
   - Submit Customer data
   - Track correlation: store pump + expected TransactionId mapping
   - Return PreAuthResult

3. **Implement `HeartbeatAsync`**
   - TCP connect to `127.0.0.1:5560` or lightweight API call

4. **Tests with mock Advatec API**

### Phase 4: VirtualLab Simulation

**Goal:** Create testable Advatec simulator.

1. **Add `advatec-like` profile** to `SeedProfileFactory`
   - Customer data acceptance endpoint
   - Receipt webhook generation (delayed, simulating dispense + TRA receipt)
   - TRA-like receipt fields with verification URL

------------------------------------------------------------------------

## 12. Risk Register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| **Advatec is NOT an FCC** — Only a fiscal device with no pump control. Half the adapter capabilities don't apply. | **High** | High — architectural misalignment, wasted effort if built as full IFccAdapter | Resolve AQ-1 FIRST. If fiscal-only, design as fiscalization service, not FCC adapter. |
| **Missing pump number in Receipt webhook** — Cannot map transactions to pumps. | **High** | **High** — transactions without pump numbers are unusable for Odoo order creation | Request Advatec to add pump field to webhook (AQ-3). Alternatively, correlate via Customer submission. |
| **Amount format ambiguity** — Getting the conversion wrong corrupts financial data. | **High** | **High** — silent financial data corruption | Get worked examples from Advatec team (AQ-2). Cross-validate: `Items.Price × Items.Quantity ≈ Items.Amount`. |
| **Normal Orders invisible** — If only fiscalized (Customer-submitted) transactions produce Receipt webhooks, most transactions are never seen by middleware. | **Medium** | **High** — majority of transactions lost | Confirm AQ-10. If true, Advatec sites must have a second FCC for Normal Order ingestion. |
| **No pull API for recovery** — Lost webhooks cannot be recovered. | **High** | Medium — data loss for individual transactions | Confirm AQ-7 (retry behaviour) and AQ-13 (history API). Use RELAY mode for LAN reliability. |
| **localhost-only access** — Advatec only listens on 127.0.0.1. Edge Agent must run on the same device. | **High** | Medium — deployment constraint | Confirm AQ-6. If localhost-only, Edge Agent and Advatec must be co-located (or Advatec bound to LAN IP). |
| **Tanzania-only scope** — Advatec's TRA integration is Tanzania-specific. May not be usable in other countries. | **High** | Low — other countries use different FCCs/fiscal devices | Document as Tanzania-only. Other countries will use DOMS/Radix/Petronite. |
| **Incomplete documentation** — Current docs cover ~20% of what's needed for a full adapter. | **Confirmed** | High — cannot implement without more information | Phase 0 (requirements clarification) must complete before any code is written. |

------------------------------------------------------------------------

## 13. Acceptance Criteria

### Phase 0 (Clarification) — Must Pass Before Implementation
- [ ] AQ-1 resolved: Advatec's role (FCC vs fiscal device) is confirmed and documented
- [ ] AQ-2 resolved: Amount format is confirmed with worked examples
- [ ] AQ-3 resolved: Pump number availability in Receipt webhook (or correlation mechanism) is confirmed
- [ ] AQ-10 resolved: Normal Order visibility via Receipt webhook is confirmed

### Phase 1 (Normalization)
- [ ] Receipt webhook payloads are correctly parsed and normalized to CanonicalTransaction
- [ ] `TransactionId` correctly maps to `fccTransactionId` for deduplication
- [ ] `ReceiptCode` correctly maps to `fiscalReceiptNumber`
- [ ] Volume conversion (Items.Quantity → microlitres) is correct
- [ ] Amount conversion to minor units is correct (per AQ-2 resolution)
- [ ] Timestamp conversion applies `Africa/Dar_es_Salaam` timezone correctly
- [ ] Product code mapping translates Advatec product names to canonical codes
- [ ] Adapter metadata reports `supportedMethods=[PUSH]`, `supportsPreAuth` per AQ-1 resolution

### Phase 2 (Webhook)
- [ ] Edge Agent accepts Advatec Receipt webhooks on LAN endpoint
- [ ] Receipts are buffered and relayed to cloud in RELAY mode
- [ ] HTTP 200 response is returned to Advatec on successful receipt
- [ ] Invalid payloads (wrong DataType, missing fields) are rejected with logging

### Phase 3 (Customer Submission — Conditional)
- [ ] Customer data is correctly submitted to Advatec for fiscalization (if applicable)
- [ ] PreAuthCommand fields are correctly mapped to Advatec Customer fields
- [ ] Dose conversion handles amount-to-volume translation (if needed)
- [ ] Heartbeat correctly detects Advatec device availability

------------------------------------------------------------------------

## Appendix A: Complete Advatec API Endpoint Reference

| Endpoint | Method | Direction | Purpose | Auth |
|---|---|---|---|---|
| `http://127.0.0.1:5560/api/v2/incoming` | POST | POS → Advatec | Submit Customer transaction data | None documented |
| (configurable webhook URL) | POST | Advatec → Webhook target | Push completed fiscal receipt | URL-based token (current Kissflow pattern) |

**That's it.** Two endpoints. Everything else is unknown.

## Appendix B: TRA-Specific Reference

| TRA Concept | Advatec Field | Description |
|---|---|---|
| EFD Serial Number | `Company.SerialNumber` | Unique device identifier registered with TRA |
| TRA Registration ID | `Company.RegistrationId` | Device registration with TRA |
| UIN | `Company.UIN` | Unique Identification Number (composite) |
| VRN | `Company.VRN` | Value Added Tax Registration Number |
| Z-Number | `ZNumber` | Daily fiscal closing report number |
| Fiscal Receipt Code | `ReceiptCode` | Unique code per receipt, verifiable on TRA website |
| Verification URL | `ReceiptVCodeURL` | `https://virtual.tra.go.tz/efdmsrctverify/{code}_{time}` |
| Tax Code "A" | `Items[].TaxCode` | Standard VAT rate (18%) |

## Appendix C: CustIdType Cross-Reference (TRA Standard)

| Code | Type | Used By |
|---|---|---|
| 1 | TIN (Tax ID Number) | Advatec, Radix |
| 2 | Driving License | Advatec, Radix |
| 3 | Voters Number | Advatec, Radix |
| 4 | Passport | Advatec, Radix |
| 5 | NID (National ID) | Advatec, Radix |
| 6 | NIL (No ID) | Advatec, Radix |
| 7 | Meter No | Advatec only |

## Appendix D: Comparison with Kissflow Integration (Current State)

The current production flow for Advatec receipts:

```
[Advatec EFD] → webhook → [Kissflow pumaenergy.kissflow.eu] → (workflow processing)
```

Our middleware will replace this:

```
[Advatec EFD] → webhook → [Edge Agent :8586/api/webhook/advatec] → [Cloud Middleware]
                                                                          ↓
                                                                   [Standard pipeline]
                                                                   dedup → normalize → store
                                                                          ↓
                                                                   [Odoo polls PENDING]
```

The Kissflow webhook URL token (`KVcFERsUvYRvNm17Eg38ntW1YAxlt3GTaAO-JOb6gII0QyCLNVkyQea8t2McJuxClBt2wJt6ikeKPyvoTmI8rg`) suggests URL-path-based authentication. Our endpoint should implement similar tokenized URLs or header-based auth.
