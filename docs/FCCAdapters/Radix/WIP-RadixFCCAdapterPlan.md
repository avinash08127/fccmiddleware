# Radix FCC Adapter — Integration Plan

Version: 0.1 (WIP)
Last Updated: 2026-03-12

------------------------------------------------------------------------

## 1. Executive Summary

This document analyzes the Radix FDC REST API v1.3.1 specification against the Forecourt Middleware requirements and produces a concrete plan for implementing the Radix FCC adapter. The Radix protocol differs fundamentally from the assumed DOMS protocol: it uses **XML over REST** with **SHA-1 HMAC message signing**, operates on **two distinct ports** (authorization port vs. transaction management port), and uses a **unique addressing model** (`PUMP` = DSB/RDG unit, `FP` = filling point within that unit, `NOZ` = nozzle) rather than the simple pump/nozzle model.

### Key Differences from DOMS (Assumed Protocol)

| Aspect | DOMS (Assumed) | Radix (Actual Spec) |
|--------|---------------|---------------------|
| Payload format | JSON | XML (`Application/xml`) |
| Authentication | API key header (`X-API-Key`) | SHA-1 signature of message body + shared secret |
| Pre-auth endpoint | `POST /preauth` JSON | `POST` with `Operation: Authorize` header, XML `<FDCMS>` body |
| Transaction fetch | `GET /transactions?cursor=...` | `POST` with `CMD_CODE=10` (request oldest buffered transaction) |
| Push mode | FCC POSTs JSON to cloud | FCC POSTs XML `<FDC_RESP>` with `RESP_CODE="30"` (unsolicited) |
| Mode management | Implicit | Explicit: `CMD_CODE=20` sets ON_DEMAND (1), UNSOLICITED (2), or OFF (0) |
| Transaction cursor | Cursor token / timestamp | FIFO buffer — one transaction at a time, oldest first, ACK to dequeue |
| Pump addressing | `pumpNumber` + `nozzleNumber` | `PUMP_ADDR` (DSB/RDG number) + `FP` (filling point) + `NOZ` (nozzle) |
| Port scheme | Single base URL | Two ports: `EAS_PORT` for authorization, `EAS_PORT+1` for transactions |
| Heartbeat | `GET /heartbeat` returning JSON | No dedicated heartbeat endpoint; use products read (CMD_CODE=55) or transaction request |
| Fiscal receipt | Field in transaction JSON | `EFD_ID` and `REG_ID` fields in `<TRN>` element |
| Price/amount format | Minor currency units (long) | Decimal strings (e.g., `AMO="30000.0"`, `PRICE="1930"`) |
| Volume format | Litres (decimal) | Litres as string (e.g., `VOL="15.54"`) |
| Ack/Dequeue | Implicit via cursor advancement | Explicit: Host must POST `CMD_CODE=201` to ACK and remove from FCC buffer |
| Customer ID types | Generic `customerTaxId` | Typed system: `CUSTIDTYPE` 1=TIN, 2=DrivingLicense, 3=VotersNumber, 4=Passport, 5=NID, 6=NIL |

------------------------------------------------------------------------

## 2. Radix Protocol Deep Dive

### 2.1 Protocol Architecture

The Radix FDC exposes a REST API server on the station LAN. All communication uses `POST` requests with XML bodies. Every message is authenticated via SHA-1 signature.

**Port Scheme:**
- **Port `P`** (configured as `External Authorization Server` port in FDC): Used for **pre-auth (external authorization)** commands.
- **Port `P+1`**: Used for **transaction management**, **product/price management**, **day close**, **ATG data**, and **CSR data**.

Example: If EAS port = 5002, then pre-auth goes to `:5002` and transaction management goes to `:5003`.

### 2.2 Message Signing (All Operations)

Every request and response is signed. The signature is a SHA-1 hash of:

**For Transaction Management (Port P+1):**
```
SHA1( <REQ>...</REQ> + SECRET_PASSWORD )
```
- Content between `<REQ>` and `</REQ>` tags (inclusive) concatenated with the shared secret (password) immediately after `</REQ>` with no space.

**For External Authorization (Port P):**
```
SHA1( <AUTH_DATA>...</AUTH_DATA> + SECRET_PASSWORD )
```
- Content between `<AUTH_DATA>` and `</AUTH_DATA>` tags (inclusive) concatenated with the shared secret.

**Critical implementation notes:**
- Whitespace and special characters matter — the SHA-1 must match character-for-character.
- The FDC validates signatures and returns `RESP_CODE=251` (SIGNATURE_ERR) on mismatch.
- Responses from FDC also contain signatures that the adapter should verify.

### 2.3 Transaction Modes (CMD_CODE=20)

Radix requires explicit mode configuration via a `MODE_CHANGE` command:

| Mode | Value | Description |
|------|-------|-------------|
| ON_DEMAND | 1 | Host must request transactions one at a time (`CMD_CODE=10`). FCC holds transactions in buffer until requested. This is **Pull Mode**. |
| UNSOLICITED | 2 | FCC POSTs transactions to the host automatically as they occur. This is **Push Mode**. |
| OFF | 0 | Transaction transfer disabled. |

**Mapping to our IngestionMode:**
- `PULL` → Set Radix to `ON_DEMAND` (mode 1)
- `PUSH` → Set Radix to `UNSOLICITED` (mode 2)
- `HYBRID` → Set Radix to `UNSOLICITED` (mode 2) + periodic ON_DEMAND polling as catch-up

**Important:** The mode change command must be issued **on adapter startup** and after any FCC restart/reconnection.

### 2.4 Transaction Fetch (Pull Mode — CMD_CODE=10)

In ON_DEMAND mode, the host requests transactions one at a time:

**Request:**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
    <REQ>
        <CMD_CODE>10</CMD_CODE>
        <CMD_NAME>TRN_REQ</CMD_NAME>
        <TOKEN>12345</TOKEN>
    </REQ>
    <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

**Response (transaction available — RESP_CODE=201):**
```xml
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="12345" />
    <TRN
      AMO="30000.0"        <!-- Amount in local currency (decimal string) -->
      EFD_ID="182AC9368989" <!-- Fiscal receipt ID (from EFD/VFD device) -->
      FDC_DATE="2021-03-03" FDC_TIME="21:17:53"
      FDC_NAME="10TZ100449"  <!-- FDC station name -->
      FDC_NUM="100253410"    <!-- FDC serial number -->
      FDC_PROD="0"           <!-- Product number (FCC internal index) -->
      FDC_PROD_NAME="UNLEADED"
      FDC_SAVE_NUM="368989"  <!-- Transaction save number (unique per FDC) -->
      FDC_TANK=""
      FP="0"                 <!-- Filling Point within the DSB/RDG -->
      NOZ="0"                <!-- Nozzle number -->
      PRICE="1930"           <!-- Unit price (in local currency, decimal string) -->
      PUMP_ADDR="0"          <!-- DSB/RDG unit address -->
      RDG_DATE="2021-03-03" RDG_TIME="21:17:53"
      RDG_ID="0" RDG_INDEX="0" RDG_PROD="0" RDG_SAVE_NUM="1066"
      REG_ID="TZ0100551361"  <!-- Tax registration ID (site TIN) -->
      ROUND_TYPE="0"
      VOL="15.54"            <!-- Volume in litres (decimal string) -->
    />
    <RFID_CARD ... />
    <DISCOUNT ... />
    <CUST_DATA USED="0">
  </TABLE>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</FDC_RESP>
```

**Response (no transaction — RESP_CODE=205):**
```xml
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="205" RESP_MSG="NO TRN AVAILABLE" TOKEN="12345" />
    <TRN /> <RFID_CARD /> <DISCOUNT />
  </TABLE>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</FDC_RESP>
```

**Host ACK (required to dequeue transaction from FCC buffer):**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
    <REQ>
        <CMD_CODE>201</CMD_CODE>
        <CMD_NAME>SUCCESS</CMD_NAME>
        <TOKEN>12345</TOKEN>
    </REQ>
    <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

**Critical difference from DOMS:** Radix uses a FIFO buffer. Each `CMD_CODE=10` returns the oldest unacknowledged transaction. The host must ACK (`CMD_CODE=201`) to remove it from the buffer before the next one is returned. This means **pull mode requires a request-ACK loop**, not a batch cursor fetch.

### 2.5 Unsolicited Transaction Push (Push Mode)

In UNSOLICITED mode, the FDC POSTs transactions to the host endpoint:

```xml
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="30" RESP_MSG="UNSOL_TRN" TOKEN="12345" />
    <TRN ... />   <!-- Same fields as Pull mode -->
    <RFID_CARD ... />
    <DISCOUNT ... />
    <CUST_DATA USED="0">
  </TABLE>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</FDC_RESP>
```

**Host must ACK with `CMD_CODE=201`** — identical to the Pull mode ACK. If the host does not respond, the FDC retries the same transaction after a timeout.

**Key implication for `CLOUD_DIRECT` ingestion mode:** The FCC pushes XML to the cloud endpoint. The cloud must expose an XML-accepting push ingress endpoint for Radix, not just JSON.

### 2.6 External Authorization (Pre-Auth) — Port P

**Request:**
```xml
<?xml version="1.0" encoding="utf-8"?>
<FDCMS>
  <AUTH_DATA>
    <PUMP>3</PUMP>                     <!-- DSB/RDG unit number -->
    <FP>1</FP>                         <!-- Filling point within DSB/RDG -->
    <AUTH>TRUE</AUTH>                   <!-- TRUE=authorize, FALSE=cancel -->
    <PROD>2</PROD>                     <!-- Product number (FCC internal, 0=all) -->
    <PRESET_VOLUME>0.00</PRESET_VOLUME>  <!-- Volume preset (litres). 0=not used. -->
    <PRESET_AMOUNT>2000</PRESET_AMOUNT>  <!-- Amount preset (local currency) -->
    <CUSTNAME>TECHOLOGY Ltd.</CUSTNAME>  <!-- Optional: customer/company name -->
    <CUSTIDTYPE>1</CUSTIDTYPE>           <!-- Optional: 1=TIN, 2=DL, 3=VotersNum, 4=Passport, 5=NID, 6=NIL -->
    <CUSTID>12345678</CUSTID>            <!-- Optional: customer ID value -->
    <MOBILENUM>25588776655</MOBILENUM>   <!-- Optional: customer phone -->
    <DISC_VALUE>10</DISC_VALUE>          <!-- Optional: discount value -->
    <DISC_TYPE>VALUE</DISC_TYPE>         <!-- Optional: PERCENT or VALUE -->
    <TOKEN>123456</TOKEN>                <!-- Optional: 0-65535, echoed in dispense transaction -->
  </AUTH_DATA>
  <FDCSIGNATURE>{sha1_hash}</FDCSIGNATURE>
</FDCMS>
```

**Response (success — ACKCODE=0):**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<FDCMS>
    <FDCACK>
        <DATE>2021-03-01</DATE>
        <TIME>09:38:42</TIME>
        <ACKCODE>0</ACKCODE>
        <ACKMSG>Success</ACKMSG>
    </FDCACK>
    <FDCSIGNATURE>{sha1_hash}</FDCSIGNATURE>
</FDCMS>
```

**Response error codes:**
| Code | Meaning |
|------|---------|
| 0 | SUCCESS |
| 251 | SIGNATURE ERROR |
| 255 | BAD XML FORMAT |
| 256 | BAD HEADER FORMAT |
| 258 | PUMP NOT READY |
| 260 | DSB IS OFFLINE |

**Pre-Auth Cancellation:**
Same endpoint, same body structure, but with `<AUTH>FALSE</AUTH>`. This cancels an active authorization on the specified pump/FP.

### 2.7 Pump Addressing — Critical Mapping Issue

Radix uses a **three-level addressing model**, not two-level:

| Radix Field | Meaning | Maps to Canonical |
|-------------|---------|-------------------|
| `PUMP_ADDR` | DSB/RDG unit address (0-15) — the physical dispensing unit hardware | Part of FCC pump mapping |
| `FP` | Filling Point within the DSB/RDG (typically 0 or 1 — a DSB often has 2 sides) | Part of FCC pump mapping |
| `NOZ` | Nozzle number within the filling point | `fcc_nozzle_number` |

**In our middleware canonical model, we use `pumpNumber` + `nozzleNumber`.** The Radix adapter must combine `PUMP_ADDR` and `FP` into a single canonical `pumpNumber`, or we need to store all three values in the nozzle mapping table.

**Proposed mapping strategy:**
- `fcc_pump_number` in our `pumps` table = `PUMP_ADDR` (the DSB/RDG address)
- We add a `fcc_filling_point` field to the mapping (or encode it: e.g., `fcc_pump_number = PUMP_ADDR * 10 + FP`)
- `fcc_nozzle_number` in our `nozzles` table = `NOZ`
- For pre-auth: the adapter must split the canonical pump number back into `PUMP` (DSB/RDG) and `FP` values

**This is a schema/mapping discussion that needs resolution before implementation.** See Open Questions.

### 2.8 Transaction ID / Deduplication Key

Radix does not provide a single unique "transaction ID" field like DOMS. The dedup key must be composed from:

**Primary candidate:** `FDC_NUM` + `FDC_SAVE_NUM`
- `FDC_NUM` = FDC serial number (unique per FDC device)
- `FDC_SAVE_NUM` = Transaction save number (unique per FDC, sequential)
- Together: globally unique across all sites

**Alternative/fallback:** `FDC_NAME` + `RDG_SAVE_NUM`
- `FDC_NAME` = FDC station name (configured)
- `RDG_SAVE_NUM` = RDG-level save number

**Mapping:** `fccTransactionId` = `"{FDC_NUM}-{FDC_SAVE_NUM}"` (composed string).

### 2.9 Fiscal Data

Radix provides fiscal data in the transaction response:
- `EFD_ID` = Electronic Fiscal Device receipt ID (the fiscal receipt number from TRA/MRA)
- `REG_ID` = Tax registration ID (site-level TIN)

For pre-auth, customer fiscal data is sent via:
- `CUSTIDTYPE` = 1 (TIN) to indicate the ID type
- `CUSTID` = the actual TIN value
- `CUSTNAME` = customer/company name

**Mapping:**
- `EFD_ID` → `fiscalReceiptNumber` in canonical model
- `REG_ID` → informational (site TIN, not per-transaction)
- Pre-auth `CUSTIDTYPE=1` + `CUSTID` ← `customerTaxId` from PreAuthCommand

### 2.10 Additional Radix Capabilities (Not in DOMS)

| Capability | CMD_CODE | Operation | Middleware Use |
|------------|----------|-----------|---------------|
| Read Products/Prices | 55 | 2 | Heartbeat/health check (read products confirms FDC is responsive). Also useful for price validation. |
| Write Products/Prices | 66 | 2 | Not in MVP scope. Could be used for remote price updates in future. |
| Day Close | 77 | 3 | Not in MVP scope. Could be used for shift management. |
| ATG Tank Data | 30 | 4 | Not in MVP scope. Tank-level monitoring is a future feature. |
| ATG Deliveries | 35 | 4 | Not in MVP scope. |
| CSR Data | 40 | 5 | Not in MVP scope. |

### 2.11 RFID Card and Discount Data

Each Radix transaction includes `<RFID_CARD>` and `<DISCOUNT>` elements:

**RFID_CARD fields:** `CARD_TYPE`, `CUST_CONTACT`, `CUST_ID`, `CUST_IDTYPE`, `CUST_NAME`, `DISCOUNT`, `DISCOUNT_TYPE`, `NUM`, `NUM_10`, `PAY_METHOD`, `PRODUCT_ENABLED`, `USED`

**DISCOUNT fields:** `AMO_DISCOUNT`, `AMO_NEW`, `AMO_ORIGIN`, `DISCOUNT_TYPE`, `PRICE_DISCOUNT`, `PRICE_NEW`, `PRICE_ORIGIN`, `VOL_ORIGIN`

**For MVP:** Store in raw payload for audit. The canonical model does not have discount/RFID fields. If needed later, we can extend `CanonicalTransaction` or create supplementary records.

### 2.12 Custom Headers

Every Radix request requires custom HTTP headers:

| Header | Description | Value |
|--------|-------------|-------|
| `Content-Type` | Always XML | `Application/xml` |
| `USN-Code` | Unique Station Number (1-999999) | Configured per FCC |
| `Operation` | Operation type code | `1` (transaction), `2` (products), `3` (day close), `4` (ATG), `5` (CSR), `Authorize` (pre-auth) |
| `Content-Length` | Body length in bytes | Standard HTTP header |

------------------------------------------------------------------------

## 3. Integration Points Analysis

### 3.1 Adapter Interface Implementation

The Radix adapter must implement `IFccAdapter` with these Radix-specific behaviours:

| Interface Method | Radix Implementation | Complexity |
|-----------------|---------------------|------------|
| `NormalizeAsync` | Parse XML `<TRN>` element; map `PUMP_ADDR`+`FP`→pump, `NOZ`→nozzle; compose `fccTransactionId` from `FDC_NUM`+`FDC_SAVE_NUM`; convert decimal strings to microlitres/minor units; extract `EFD_ID` as fiscal receipt | Medium |
| `SendPreAuthAsync` | Build XML `<FDCMS><AUTH_DATA>` body; compute SHA-1 signature; POST to port P; parse `<FDCACK>` response; handle error codes (251, 255, 256, 258, 260) | Medium |
| `GetPumpStatusAsync` | **No dedicated pump status endpoint in Radix spec.** Closest option: read product/price data (CMD_CODE=55) confirms FDC responsiveness, but doesn't give per-pump state. We may need to **synthesize pump status from recent transactions** or return an empty/limited status. | High — spec gap |
| `HeartbeatAsync` | Use product read (CMD_CODE=55, port P+1) as liveness probe. If `RESP_CODE=201`, FDC is alive. | Low |
| `FetchTransactionsAsync` | **Loop:** Send `CMD_CODE=10` → receive oldest transaction → ACK with `CMD_CODE=201` → repeat until `RESP_CODE=205` (no more). Wrap all collected transactions into `TransactionBatch`. The "cursor" for Radix is implicit (FIFO buffer position). | Medium-High |

### 3.2 What Needs to Be Created (New Files)

#### Desktop Edge Agent (.NET)

| File | Description |
|------|-------------|
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixAdapter.cs` | Main adapter implementing `IFccAdapter` |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixProtocolDtos.cs` | Radix XML request/response DTOs |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixSignatureHelper.cs` | SHA-1 message signing utility |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixXmlBuilder.cs` | XML request body builder (HOST_REQ, FDCMS envelopes) |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixXmlParser.cs` | XML response parser (FDC_RESP, FDCMS responses) |
| `src/desktop-edge-agent/tests/.../Adapter/Radix/RadixAdapterTests.cs` | Unit tests |
| `src/desktop-edge-agent/tests/.../Adapter/Radix/RadixSignatureHelperTests.cs` | Signature computation tests |
| `src/desktop-edge-agent/tests/.../Adapter/Radix/RadixXmlParserTests.cs` | XML parsing tests with real payloads |

#### Cloud Backend (.NET)

| File | Description |
|------|-------------|
| `src/cloud/FccMiddleware.Adapter.Radix/RadixCloudAdapter.cs` | Cloud-side adapter implementing `IFccAdapter` |
| `src/cloud/FccMiddleware.Adapter.Radix/Internal/RadixTransactionParser.cs` | XML parsing for push-received transactions |
| `src/cloud/FccMiddleware.Adapter.Radix/Internal/RadixSignatureValidator.cs` | Verify incoming push signatures |
| `src/cloud/FccMiddleware.Adapter.Radix/FccMiddleware.Adapter.Radix.csproj` | Project file |

#### Edge Agent (Kotlin/Java — Android)

| File | Description |
|------|-------------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixAdapter.kt` | Kotlin adapter implementing `IFccAdapter` |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixProtocol.kt` | XML building, parsing, signing |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixDtos.kt` | Request/response data classes |

### 3.3 What Needs to Be Modified (Existing Files)

| File | Change | Reason |
|------|--------|--------|
| `FccDesktopAgent.Core/Adapter/Common/Enums.cs` | Add `Radix` to `FccVendor` enum | Vendor registration |
| `FccDesktopAgent.Core/Adapter/Common/FccConnectionConfig.cs` | Add `SharedSecret` (string), `UsnCode` (int), `AuthPort` (int?) fields | Radix needs shared secret for signing, USN-Code header, and separate auth port |
| `FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs` | Add `CustomerTaxId`, `CustomerName`, `CustomerIdType` to `PreAuthCommand` | Radix pre-auth supports these directly in `<AUTH_DATA>` |
| `FccDesktopAgent.Core/Adapter/Common/IFccAdapterFactory.cs` | Register Radix adapter in factory implementation | Vendor resolution |
| `FccDesktopAgent.Core/Adapter/Common/RawPayloadEnvelope` | Ensure `RawJson` can hold XML content (rename to `RawPayload` or add `ContentType` field) | Radix payloads are XML, not JSON |
| Cloud `Program.cs` (adapter registration) | Register `RadixCloudAdapter` in the factory registry | Cloud vendor resolution |
| Cloud push ingress endpoint | Accept `Application/xml` content type for Radix push payloads | Radix FDC pushes XML |
| VirtualLab `SeedProfileFactory.cs` | Add a `radix-like` FCC simulator profile | Testing support |

### 3.4 Configuration Changes

**New fields needed in `FccConnectionConfig` / `SiteFccConfig`:**

| Field | Type | Description | Required For |
|-------|------|-------------|-------------|
| `sharedSecret` | Encrypted String | SHA-1 signing password | All Radix operations |
| `usnCode` | Integer (1-999999) | Unique Station Number (sent in header) | All Radix operations |
| `authPort` | Integer | External Authorization port (typically `port` value); transaction port = `authPort + 1` | Clarifying port scheme |
| `transactionPort` | Integer | Derived: `authPort + 1` | Transaction management |
| `fccPumpAddressMap` | JSON | Maps canonical pump numbers to Radix `(PUMP_ADDR, FP)` pairs | Pre-auth addressing |

**Note:** The `sharedSecret` MUST be stored encrypted (REQ-3 BR-3.5 requires `authCredentials` encrypted at rest). This replaces/augments the `ApiKey` field used by DOMS.

------------------------------------------------------------------------

## 4. Field Mapping: Radix TRN → CanonicalTransaction

| Radix `<TRN>` Attribute | Type (Radix) | Canonical Field | Type (Canonical) | Conversion |
|-------------------------|-------------|-----------------|------------------|------------|
| `FDC_NUM` + `FDC_SAVE_NUM` | String + String | `fccTransactionId` | String | Compose: `"{FDC_NUM}-{FDC_SAVE_NUM}"` |
| (from config) | — | `siteCode` | String | Injected from `FccConnectionConfig.SiteCode` |
| `PUMP_ADDR` | Integer string | `pumpNumber` | Int | Map via pump table: `fcc_pump_number` → `pump_number` |
| `NOZ` | Integer string | `nozzleNumber` | Int | Map via nozzle table: `fcc_nozzle_number` → `odoo_nozzle_number` |
| `FDC_PROD` / `FDC_PROD_NAME` | Integer string / String | `productCode` | String | Map via `productCodeMapping`: Radix product index → canonical code (PMS, AGO, IK) |
| `VOL` | Decimal string | `volumeMicrolitres` | Long | Parse decimal, multiply by 1,000,000, cast to long |
| `AMO` | Decimal string | `amountMinorUnits` | Long | Parse decimal. **Determine decimal places from currency** (e.g., TZS has 0 decimal places so `30000.0` = `3000000` cents? **Need clarification** — see Open Questions) |
| `PRICE` | Decimal string | `unitPriceMinorPerLitre` | Long | Parse decimal. Same currency consideration as `AMO`. |
| `FDC_DATE` + `FDC_TIME` | Date + Time strings | `startedAt` | DateTimeOffset | Parse `"yyyy-MM-dd"` + `"HH:mm:ss"`, apply configured timezone → UTC |
| `RDG_DATE` + `RDG_TIME` | Date + Time strings | `completedAt` | DateTimeOffset | Parse `"yyyy-MM-dd"` + `"HH:mm:ss"`, apply configured timezone → UTC |
| `EFD_ID` | String | `fiscalReceiptNumber` | String? | Direct mapping. Null/empty = no fiscal receipt. |
| — | — | `fccVendor` | String | Hardcoded `"RADIX"` |
| — | — | `attendantId` | String? | Not provided by Radix. Null. |
| `FDC_DATE` + `FDC_TIME` | — | `startedAt` | DateTimeOffset | FDC timestamp of the transaction |
| `RDG_DATE` + `RDG_TIME` | — | `completedAt` | DateTimeOffset | RDG (register) timestamp |

**Additional fields preserved in raw payload but not in canonical model:**
- `FDC_NAME` — FDC station name
- `FDC_TANK` — Tank reference
- `FP` — Filling point (needed for pump mapping)
- `RDG_ID`, `RDG_INDEX`, `RDG_PROD`, `RDG_SAVE_NUM` — Register-level data
- `REG_ID` — Site tax registration
- `ROUND_TYPE` — Rounding type
- `RFID_CARD.*` — All RFID card data
- `DISCOUNT.*` — All discount data
- `CUST_DATA` — Customer data block

------------------------------------------------------------------------

## 5. Pre-Auth Field Mapping: PreAuthCommand → Radix AUTH_DATA

| PreAuthCommand Field | Radix `<AUTH_DATA>` Element | Conversion |
|---------------------|---------------------------|------------|
| `FccPumpNumber` | `<PUMP>` | Map: canonical pump → Radix DSB/RDG address (from pump table or config map) |
| (derived from pump mapping) | `<FP>` | Filling point within the DSB/RDG — must come from config/mapping |
| — | `<AUTH>` | `TRUE` for authorize, `FALSE` for cancel |
| `ProductCode` | `<PROD>` | Reverse-map: canonical product code → Radix product index via `productCodeMapping` |
| — | `<PRESET_VOLUME>` | `0.00` (we always authorize by amount, per BR-6.1b) |
| `RequestedAmountMinorUnits` | `<PRESET_AMOUNT>` | Convert from minor units to Radix decimal format |
| `CustomerName` (new field needed) | `<CUSTNAME>` | Direct mapping, optional |
| `CustomerIdType` (new) | `<CUSTIDTYPE>` | Map: `TIN`→1, `DRIVING_LICENSE`→2, etc. Default `1` for TIN when `customerTaxId` present |
| `CustomerTaxId` (new field needed) | `<CUSTID>` | Direct mapping, optional |
| `CustomerPhone` (new, optional) | `<MOBILENUM>` | Direct mapping, optional |
| — | `<DISC_VALUE>` | Not used in MVP (always 0 or omitted) |
| — | `<DISC_TYPE>` | Not used in MVP |
| `FccCorrelationId` / generated | `<TOKEN>` | Map to a 0-65535 numeric token. This token is echoed in the resulting dispense transaction. Used for pre-auth ↔ dispense correlation. |

**Cancellation:** Same endpoint with `<AUTH>FALSE</AUTH>`. Requires knowing the pump/FP that was authorized.

------------------------------------------------------------------------

## 6. Pre-Auth Response Mapping: Radix FDCACK → PreAuthResult

| Radix `<FDCACK>` Element | PreAuthResult Field | Conversion |
|--------------------------|--------------------| ------------|
| `ACKCODE` | `Accepted` | `0` → `true`, anything else → `false` |
| `ACKMSG` | `ErrorMessage` | Direct mapping on failure |
| `ACKCODE` | `ErrorCode` | String representation: `"RADIX_251"` for signature error, etc. |
| `DATE` + `TIME` | (logged) | FDC timestamp of acknowledgment |
| — | `FccCorrelationId` | The `TOKEN` value sent in the request (echoed) |
| — | `FccAuthorizationCode` | Not provided by Radix. Null. |

**Radix-specific error code mapping:**
| ACKCODE | Meaning | PreAuthResult | Recoverable |
|---------|---------|---------------|-------------|
| 0 | Success | `Accepted=true` | N/A |
| 251 | Signature error | `Accepted=false, ErrorCode="SIGNATURE_ERROR"` | No — config issue |
| 255 | Bad XML format | `Accepted=false, ErrorCode="BAD_XML"` | No — code bug |
| 256 | Bad header format | `Accepted=false, ErrorCode="BAD_HEADER"` | No — code bug |
| 258 | Pump not ready | `Accepted=false, ErrorCode="PUMP_NOT_READY"` | Yes — transient |
| 260 | DSB is offline | `Accepted=false, ErrorCode="DSB_OFFLINE"` | Yes — transient |

------------------------------------------------------------------------

## 7. Push Mode Integration (Unsolicited Transactions)

### 7.1 Cloud Push Ingress

In `CLOUD_DIRECT` ingestion mode, the Radix FDC pushes unsolicited transactions directly to the cloud middleware. This requires:

1. **New XML push endpoint** on the cloud middleware (or a content-type-aware handler on the existing push endpoint).
2. The endpoint must:
   - Accept `Content-Type: Application/xml`
   - Validate the `USN-Code` header against known FCC registrations
   - Validate the `SIGNATURE` using the site's configured `sharedSecret`
   - Parse the XML `<FDC_RESP>` body
   - Extract the `<TRN>` element and normalize to canonical model
   - Respond with an ACK (`CMD_CODE=201`) XML body — **mandatory, or FDC will retry**
   - Process through the standard dedup → normalize → store pipeline

3. **The response format must match what Radix expects** (XML HOST_REQ with CMD_CODE=201):
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

### 7.2 Edge Agent Push Reception (RELAY / BUFFER_ALWAYS Modes)

When the Edge Agent is the push target (RELAY or BUFFER_ALWAYS), it must:
1. Expose an HTTP listener on the LAN that accepts Radix unsolicited POSTs
2. Validate signature, parse XML, normalize, buffer locally
3. ACK the FDC with the XML response

This requires the Edge Agent to run an **HTTP server** (not just an HTTP client). The current Edge Agent already has a local REST API (`localhost:8585`), so this listener would be on a **separate port** accessible from the FCC's LAN IP.

------------------------------------------------------------------------

## 8. Heartbeat / Health Check Strategy

Radix does not have a dedicated heartbeat endpoint. Options:

| Strategy | Command | Pro | Con |
|----------|---------|-----|-----|
| **Product Read** (recommended) | `CMD_CODE=55`, Operation=2 | Confirms FDC is responsive and authenticated; returns product list as bonus validation | Slightly heavier than a simple ping |
| **Transaction Request** | `CMD_CODE=10`, Operation=1 | Confirms transaction path is working | May dequeue a transaction if one is available (must be handled) |
| **Mode Read** | `CMD_CODE=20`, Operation=1 with current mode | Confirms FDC command processing | Changes FDC state (mode) — undesirable for heartbeat |
| **TCP Connect** | Raw socket connect to port | Lightest weight | Only proves network reachability, not FDC application health |

**Recommendation:** Use `CMD_CODE=55` (product/price read) as the heartbeat. It is read-only, confirms authentication works, and has no side effects.

------------------------------------------------------------------------

## 9. Differences Requiring Architectural Decisions

### 9.1 RawPayloadEnvelope Content Type

The current `RawPayloadEnvelope` has a field called `RawJson`. Radix payloads are XML. Options:

| Option | Change | Impact |
|--------|--------|--------|
| **A. Rename to `RawPayload`** | Rename `RawJson` → `RawPayload` across codebase | Breaking change for existing code. Cleaner long-term. |
| **B. Store XML as string in `RawJson`** | No rename. Just put XML string in the field. | Minimal change. Field name is misleading. |
| **C. Add `ContentType` field** | Add `ContentType` to envelope, keep `RawJson` as `RawPayload` | Best of both worlds but more changes. |

**Recommendation:** Option B for MVP speed, with a TODO to refactor to Option C post-MVP.

### 9.2 Dual-Port Architecture

Radix uses two ports. `FccConnectionConfig` currently has a single `BaseUrl`. Options:

| Option | Change |
|--------|--------|
| **A. Two separate config fields** | Add `authBaseUrl` and `transactionBaseUrl` |
| **B. Derive from single port** | Store `authPort`, derive `transactionPort = authPort + 1` inside adapter |
| **C. Adapter-internal logic** | Adapter receives one config and internally manages two HttpClients |

**Recommendation:** Option B — store the authorization port in config; the adapter computes `transactionPort = authPort + 1` per the Radix spec. Add an `authPort` field to `FccConnectionConfig` (nullable, Radix-only).

### 9.3 Pre-Auth Token as Correlation ID

Radix's `<TOKEN>` field (0-65535 integer) is echoed back in the dispense transaction. This is the **only mechanism for correlating a pre-auth to its dispense transaction** in Radix.

**Implications:**
- The adapter must generate a unique TOKEN per pre-auth (within the 0-65535 range)
- The adapter must maintain a local mapping: `TOKEN → odooOrderId / preAuthId`
- TOKEN values wrap around at 65535 — must handle collisions (e.g., by tracking active pre-auths and reusing expired tokens)
- When a dispense transaction arrives, check if its TOKEN matches an active pre-auth

**This is different from the current pre-auth correlation approach (REQ-8 BR-8.2)** which assumes the FCC echoes the `odooOrderId` or provides a correlation ID. Radix only echoes a numeric TOKEN.

### 9.4 FIFO Transaction Buffer vs Cursor-Based Fetch

DOMS (assumed) returns batches with cursor pagination. Radix returns one transaction at a time from a FIFO buffer, requiring explicit ACK to dequeue.

**Impact on `FetchTransactionsAsync`:**
- Cannot request a batch — must loop: request → parse → ACK → request → ...
- Must handle the case where the FCC has many buffered transactions (could take many round-trips)
- Should implement a configurable max-per-fetch limit (e.g., process up to 100 transactions per poll cycle)
- The `FetchCursor` concept doesn't naturally map — Radix's "cursor" is the implicit FIFO position. The adapter can return `nextCursorToken = "continue"` with `hasMore = true` if it hit the per-cycle limit, or `hasMore = false` if `RESP_CODE=205` (buffer empty).

### 9.5 Mode Management Lifecycle

The adapter must manage the Radix transaction mode:
- On startup: Set mode to ON_DEMAND (1) or UNSOLICITED (2) based on config
- On reconnection after FCC restart: Re-send mode change (FCC may have reset to default)
- On adapter shutdown: Optionally set mode to OFF (0)

This is a lifecycle concern that doesn't exist for DOMS.

------------------------------------------------------------------------

## 10. Open Questions

| ID | Question | Impact | Proposed Answer |
|----|----------|--------|----------------|
| RQ-1 | **Currency decimal handling:** Radix `AMO="30000.0"` and `PRICE="1930"` — are these in major currency units (e.g., TZS 30000) or already in minor units? The `.0` suffix on AMO suggests major units. PRICE without decimals could be minor or major. | Determines conversion to `amountMinorUnits`. Getting this wrong silently corrupts all financial data. | **Needs confirmation from Radix/deployment team.** For TZS (0 decimal places), major = minor. For currencies with 2 decimal places, multiply by 100. |
| RQ-2 | **Pump address mapping:** How to map the three-level Radix addressing (`PUMP_ADDR` + `FP` + `NOZ`) to our two-level canonical model (`pumpNumber` + `nozzleNumber`)? | Affects pump/nozzle mapping tables, pre-auth command building, and transaction normalization. | Store `fcc_pump_address` and `fcc_filling_point` separately in the pump mapping table. Adapter resolves internally. The Odoo-facing `pump_number` remains a simple integer. |
| RQ-3 | **Token-based correlation limits:** With only 65536 possible TOKEN values (0-65535), how do we handle high-volume pre-auth sites where tokens may need to be reused? | Could cause incorrect pre-auth ↔ dispense matching if a token is reused while a previous pre-auth with the same token is still active. | Maintain a per-FCC active-token registry. Only reuse tokens from completed/expired/cancelled pre-auths. Alert if pool is exhausted (extremely unlikely — would require 65K concurrent pre-auths). |
| RQ-4 | **Push endpoint configuration on FDC:** What URL does the Radix FDC push unsolicited transactions to? Is it configurable on the FDC, or does the FDC push to the EAS IP that was allowed in its config? | Determines whether we can point the FDC's push target at the cloud directly (CLOUD_DIRECT) or must use the Edge Agent as relay. | **Needs confirmation from Radix/deployment team.** If the FDC pushes to the EAS IP, then CLOUD_DIRECT requires the FDC to be configured with the cloud's public IP as the EAS. If the FDC only pushes to LAN IPs, then RELAY/BUFFER_ALWAYS must be used. |
| RQ-5 | **Does Radix FDC support HYBRID mode?** Can we switch between ON_DEMAND and UNSOLICITED dynamically, or is it one or the other? | Determines whether HYBRID mode is feasible for Radix. | The spec says mode change is "allowed in all modes" — so switching is possible. For HYBRID: set UNSOLICITED as primary, periodically switch to ON_DEMAND for catch-up, then switch back. However, this adds complexity and risk of lost transactions during mode switches. **Alternative:** Stay in UNSOLICITED mode and use the Edge Agent's LAN poll (which switches to ON_DEMAND temporarily) as the catch-up mechanism. |
| RQ-6 | **Pump status:** Radix spec does not expose real-time pump status. Do we need to synthesize it, or can Radix sites operate without the `GET /api/pump-status` capability? | Affects the `GetPumpStatusAsync` implementation and Odoo POS pump display. | Return empty list from adapter. Set `supportsPumpStatus = false` in adapter metadata. Odoo POS must handle the absence of real-time pump data gracefully. |
| RQ-7 | **FDC_PROD to canonical product code mapping:** Is `FDC_PROD` a 0-based index or a product ID? How stable is it across FCC reconfigurations? | Determines how to build `productCodeMapping` config. | Use `CMD_CODE=55` (read products) to fetch the current product list on startup and build the mapping dynamically. Store as `productCodeMapping` in site config. |
| RQ-8 | **CUST_DATA element:** The spec shows `<CUST_DATA USED="0">` in transaction responses. When USED="1", what fields are present? Does it echo back the pre-auth customer data? | Determines if we can extract customer data from Normal Order transactions. | **Needs spec clarification.** For now, assume `USED="0"` means no customer data on Normal Orders (expected — customer data is only on pre-auth fiscalized transactions). |

------------------------------------------------------------------------

## 11. Implementation Plan

### Phase 1: Core Adapter Skeleton (Edge Agent — Desktop .NET)

**Goal:** Basic Radix communication — signing, XML building/parsing, heartbeat.

1. **Create `RadixSignatureHelper`** — SHA-1 computation matching Radix spec exactly
   - Test against known input/output from spec examples
   - Handle both `<REQ>...</REQ>` and `<AUTH_DATA>...</AUTH_DATA>` signing

2. **Create `RadixXmlBuilder`** — XML request construction
   - `BuildModeChangeRequest(mode, token, secret)` → signed XML
   - `BuildTransactionRequest(token, secret)` → signed XML
   - `BuildTransactionAck(token, secret)` → signed XML
   - `BuildPreAuthRequest(command, secret)` → signed XML
   - `BuildPreAuthCancelRequest(pump, fp, token, secret)` → signed XML

3. **Create `RadixXmlParser`** — XML response parsing
   - `ParseTransactionResponse(xml)` → `RadixTransactionResponse` (ANS + TRN + RFID + DISCOUNT)
   - `ParseAuthResponse(xml)` → `RadixAuthResponse` (FDCACK)
   - `ParseProductResponse(xml)` → `RadixProductResponse`
   - Validate response signatures

4. **Create `RadixProtocolDtos`** — Radix-specific data classes
   - `RadixTransactionData` (all TRN attributes)
   - `RadixRfidCardData`, `RadixDiscountData`
   - `RadixAuthResponse` (ACKCODE, ACKMSG, DATE, TIME)
   - `RadixProductData` (ID, NAME, PRICE)

5. **Unit tests** for all of the above using real XML examples from the spec

### Phase 2: Transaction Fetch & Normalization

**Goal:** Pull transactions from Radix FDC and normalize to canonical model.

1. **Implement `FetchTransactionsAsync`**
   - Ensure mode is set to ON_DEMAND (CMD_CODE=20, MODE=1)
   - Loop: CMD_CODE=10 → parse → ACK → repeat until 205 or max limit
   - Wrap results in `TransactionBatch`

2. **Implement `NormalizeAsync`**
   - Parse XML from `RawPayloadEnvelope.RawJson` (stored as XML string)
   - Apply field mapping (section 4 above)
   - Apply pump/nozzle mapping from config
   - Apply product code mapping from config
   - Convert amounts and volumes to minor units / microlitres
   - Apply timezone conversion for timestamps

3. **Implement `HeartbeatAsync`**
   - CMD_CODE=55 (product read) on port P+1
   - Return true if RESP_CODE=201

4. **Register Radix in `FccVendor` enum and adapter factory**

5. **Integration tests** with mock HTTP server returning Radix XML payloads

### Phase 3: Pre-Authorization

**Goal:** Send pre-auth commands to Radix FDC.

1. **Implement `SendPreAuthAsync`**
   - Build XML `<FDCMS><AUTH_DATA>` body
   - Resolve pump → (PUMP_ADDR, FP) from config mapping
   - Reverse-map product code → Radix product index
   - Generate and track TOKEN for correlation
   - POST to port P
   - Parse `<FDCACK>` response
   - Map error codes to `PreAuthResult`

2. **Implement pre-auth cancellation** (AUTH=FALSE)

3. **Implement TOKEN tracking** — local registry of active tokens for pre-auth ↔ dispense correlation

4. **Add customer data fields** to `PreAuthCommand` if not already present

5. **Tests with various error scenarios** (pump not ready, DSB offline, signature error)

### Phase 4: Push Mode Support

**Goal:** Handle unsolicited transaction pushes from Radix FDC.

1. **Cloud: XML push ingress endpoint**
   - Accept `Application/xml` content type
   - Validate USN-Code header
   - Validate SIGNATURE
   - Parse and normalize
   - Respond with proper XML ACK

2. **Edge Agent: LAN push listener** (for RELAY/BUFFER_ALWAYS modes)
   - HTTP server on configurable port
   - Same logic as cloud but stores in local buffer

3. **Mode management on startup**
   - Set UNSOLICITED mode (CMD_CODE=20, MODE=2) for push-configured sites
   - Handle FDC restarts (re-send mode change on reconnect)

### Phase 5: VirtualLab Simulation Profile

**Goal:** Create a testable Radix FDC simulator.

1. **Add `radix-like` profile** to `SeedProfileFactory`
   - XML payloads with proper structure
   - Signature validation/generation
   - ON_DEMAND and UNSOLICITED mode simulation
   - Pre-auth simulation with TOKEN echo

------------------------------------------------------------------------

## 12. Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| SHA-1 signature computation doesn't match FDC exactly (whitespace, encoding) | Medium | High — all communication fails | Test against real FDC early. Use spec examples as golden tests. |
| Currency/amount decimal interpretation is wrong | Medium | High — financial data corruption | Get explicit confirmation from Radix vendor or deployment team. Add validation logging. |
| Pump addressing model doesn't fit our two-level schema | Medium | Medium — requires schema migration | Resolve RQ-2 before implementation. May need DB migration for pump mapping tables. |
| Radix FDC cannot push to cloud directly (LAN-only push) | Medium | Medium — forces RELAY mode for all Radix sites | Confirm RQ-4. If LAN-only, design for RELAY as default for Radix. |
| No real-time pump status from Radix | Confirmed | Low-Medium — Odoo POS pump display degraded | Accept limitation. Design UI to handle "pump status unavailable" gracefully. |
| TOKEN collision for pre-auth correlation | Low | High — incorrect pre-auth ↔ dispense matching | Track active tokens. Alert on exhaustion. With max 65K tokens and typical pre-auth durations of <30 min, collisions are extremely unlikely. |
| FDC firmware variations across sites | Medium | Medium — XML format differences | Version check on connection. Alert if firmware is below 3.49. |

------------------------------------------------------------------------

## 13. Acceptance Criteria

- [ ] Radix adapter passes all `IFccAdapter` interface contract requirements
- [ ] SHA-1 signatures computed by the adapter match the Radix FDC's expectations (verified against spec examples)
- [ ] Pull mode: transactions are fetched one-by-one and ACKed correctly (FIFO drain)
- [ ] Push mode: unsolicited transactions are received, validated (signature + USN), parsed, ACKed, and normalized
- [ ] Pre-auth: authorization commands are correctly built, signed, and sent; responses are correctly parsed
- [ ] Pre-auth cancellation works (`AUTH=FALSE`)
- [ ] Field mapping produces correct canonical transactions (verified with known test vectors)
- [ ] Pump/nozzle mapping correctly translates between Radix three-level addressing and canonical two-level model
- [ ] Product code mapping correctly translates Radix product indices to canonical product codes
- [ ] Timestamp conversion correctly applies configured timezone
- [ ] Fiscal receipt (`EFD_ID`) is correctly extracted and mapped
- [ ] TOKEN-based pre-auth ↔ dispense correlation works correctly
- [ ] Cloud push ingress accepts Radix XML payloads and responds with proper XML ACK
- [ ] Error codes (251, 255, 256, 258, 260) are correctly mapped and handled
- [ ] Mode change commands (ON_DEMAND, UNSOLICITED, OFF) work correctly
- [ ] Adapter metadata correctly reports capabilities (`supportsPreAuth=true`, `supportsPumpStatus=false`)
- [ ] VirtualLab has a working Radix simulation profile for testing

------------------------------------------------------------------------

## 14. Dependencies and Prerequisites

Before implementation begins, resolve:

1. **RQ-1 (Currency decimals)** — Critical. Cannot correctly normalize financial data without this.
2. **RQ-2 (Pump addressing)** — Critical. May require DB schema changes.
3. **RQ-4 (Push endpoint)** — Important. Determines default ingestion mode for Radix sites.
4. **Access to a real Radix FDC** (or Radix simulator) for integration testing.
5. **FDC firmware version confirmation** — Must be >= 3.49.

------------------------------------------------------------------------

## Appendix A: Complete Radix Response/Error Code Reference

### Transaction Management (Port P+1)

| Code | Type | Meaning |
|------|------|---------|
| 201 | Response | OK / SUCCESS |
| 205 | Response | NO TRANSACTION AVAILABLE |
| 30 | Response | UNSOLICITED TRANSACTION (push from FDC) |
| 206 | Error | TRANSACTION MODE ERROR |
| 251 | Error | SIGNATURE ERROR |
| 253 | Error | TOKEN ERROR |
| 255 | Error | BAD XML FORMAT |

### Product/Price Management (Port P+1)

| Code | Type | Meaning |
|------|------|---------|
| 201 | Response | SUCCESS / ACCEPTED |
| 207 | Error | PRODUCT DATA ERROR |
| 251 | Error | SIGNATURE ERROR |
| 255 | Error | BAD XML FORMAT |
| 256 | Error | BAD HEADER FORMAT |
| 258 | Error | PUMP NOT READY |
| 260 | Error | DSB IS OFFLINE |

### External Authorization (Port P)

| Code | Type | Meaning |
|------|------|---------|
| 0 | Response | OK / SUCCESS |
| 251 | Error | SIGNATURE ERROR |
| 255 | Error | BAD XML FORMAT |
| 256 | Error | BAD HEADER FORMAT |
| 258 | Error | PUMP NOT READY |
| 260 | Error | DSB IS OFFLINE |

### Command Codes (Host → FDC)

| Code | Operation | Description |
|------|-----------|-------------|
| 10 | 1 | HOST REQUEST FOR TRANSACTION (pull one from buffer) |
| 20 | 1 | MODE CHANGE (set ON_DEMAND/UNSOLICITED/OFF) |
| 30 | 4 | HOST REQUEST - TANK DATA (ATG) |
| 35 | 4 | HOST REQUEST - ATG DELIVERY |
| 40 | 5 | HOST REQUEST - CSR DATA |
| 55 | 2 | HOST REQUESTS - READ PRICES AND PRODUCTS |
| 66 | 2 | HOST WRITES - CHANGE/WRITE PRICES AND PRODUCTS |
| 77 | 3 | HOST WRITES - DAY CLOSE |
| 201 | 1 | HOST ACK - SUCCESS (acknowledges received transaction) |

## Appendix B: Header Quick Reference

| Operation | `Content-Type` | `USN-Code` | `Operation` Header |
|-----------|---------------|------------|-------------------|
| Transaction Management | `Application/xml` | Station number | `1` |
| Products/Prices | `Application/xml` | Station number | `2` |
| Day Close | `Application/xml` | Station number | `3` |
| ATG Data | `Application/xml` | Station number | `4` |
| CSR Data | `Application/xml` | Station number | `5` |
| External Authorization | `Application/xml` | Station number | `Authorize` |
