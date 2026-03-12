# Petronite FCC Adapter — Integration Plan

Version: 0.1 (WIP)
Last Updated: 2026-03-12

------------------------------------------------------------------------

## 1. Executive Summary

This document analyzes the Petronite PUMA REST API against the Forecourt Middleware requirements and produces a concrete plan for implementing the Petronite FCC adapter. Petronite differs from DOMS and Radix in several fundamental ways: it uses **OAuth2 Client Credentials authentication**, has a **two-step pre-authorization flow** (Create Order → Authorize Pump), identifies nozzles by **Petronite-internal Long IDs** (not simple numbers), and delivers completed transactions exclusively via **webhook callbacks** (push-only, no documented pull API).

The Petronite system is not a traditional FCC in the hardware sense. The **"Petronite bot"** is an intermediary software application running on a device on the station LAN. It sits between the POS and the physical pump hardware, managing pump locks, authorization, and transaction recording. Our adapter communicates with this bot via its REST API on port `8884`.

### Key Differences from DOMS and Radix

| Aspect | DOMS (Current) | Radix | Petronite |
|--------|---------------|-------|-----------|
| Payload format | JSON | XML | JSON |
| Authentication | API key header (`X-API-Key`) | SHA-1 HMAC signing | OAuth2 Client Credentials (bearer token) |
| Pre-auth flow | Single `POST /preauth` | Single XML `<AUTH_DATA>` POST | **Two-step**: `POST /create` (locks pump) + `POST /authorize` (starts dispensing) |
| Transaction fetch (Pull) | `GET /transactions?cursor=...` | `POST` CMD_CODE=10 (FIFO drain) | **Not available** — no documented pull endpoint |
| Transaction push | FCC POSTs JSON to cloud | FCC POSTs XML with ACK required | **Webhook callback** — Petronite POSTs JSON to pre-configured endpoint |
| Push acknowledgement | HTTP 200 | XML ACK (CMD_CODE=201) required | HTTP 200 (standard webhook) |
| Pump addressing | `pumpNumber` + `nozzleNumber` (integers) | `PUMP_ADDR` + `FP` + `NOZ` (three-level) | `nozzleId` (Petronite-internal Long ID, e.g., 1204) |
| Nozzle discovery | Static config | Static config | **Dynamic**: `GET /nozzles/assigned` returns daily assignments |
| Port scheme | Single base URL | Dual (P and P+1) | Single port (8884) |
| Heartbeat | `GET /heartbeat` | Product read (CMD_CODE=55) | `GET /nozzles/assigned` or token refresh |
| Fiscal receipt | Field in transaction JSON | `EFD_ID` in `<TRN>` | `receipt_code` in webhook payload |
| Pre-auth correlation | `fccCorrelationId` echoed | `TOKEN` (0–65535) echoed in dispense | Petronite `id` (Long) returned on create, echoed in webhook |
| Authorization types | Amount only | Amount or Volume preset | **Three types**: "Volume", "Amount", "FullTank" |
| Token/session management | None (stateless API key) | None (per-request signing) | OAuth2 token with ~12h TTL (43199s), must refresh |
| Cancel pre-auth | `DELETE /preauth/{id}` | Same endpoint with `AUTH=FALSE` | `POST /direct-authorize-requests/{id}/cancel` |
| Mode management | Implicit | Explicit CMD_CODE=20 | Implicit (always push via webhook) |

------------------------------------------------------------------------

## 2. Petronite Protocol Deep Dive

### 2.1 Architecture — The "Petronite Bot"

Unlike DOMS/Radix which are firmware running directly on the FCC hardware, Petronite operates through a **"bot"** — a software application running on a device (likely a Raspberry Pi or similar) connected to the station LAN. The bot:

- Controls the physical pumps via its own hardware interface
- Exposes a REST API on port `8884` for external systems (our adapter)
- Manages its own order/authorization workflow
- Sends webhook callbacks when transactions complete
- Has its own mobile app for attendant operations (the Petronite App)

Our adapter replaces/supplements the Petronite App as the authorization controller, using the same REST API.

### 2.2 Authentication — OAuth2 Client Credentials

All API calls require a bearer token obtained via OAuth2 Client Credentials flow:

**Token Request:**
```
POST http://<bot_ip>:8884/oauth/token
Headers:
  Content-Type: application/x-www-form-urlencoded
  Authorization: Basic <Base64(client_id:client_secret)>
Body:
  grant_type=client_credentials
```

**Token Response:**
```json
{
  "access_token": "eyJhbGciOiJIUz...",
  "token_type": "bearer",
  "expires_in": 43199,
  "scope": "read write"
}
```

**Key implementation concerns:**
- Token TTL is ~12 hours (43199 seconds). The adapter must cache the token and refresh proactively before expiry.
- All subsequent API calls use: `Authorization: Bearer <access_token>`
- The Postman collection also shows `?access_token=<token>` as a query parameter alternative — the adapter should use the header method (more secure, standard practice).
- If the bot restarts, existing tokens may be invalidated — the adapter must handle 401 responses by re-authenticating.

**Credentials storage:**
- `client_id` and `client_secret` map to our `authCredentials` field (REQ-3 BR-3.5 — must be encrypted at rest).
- These are configured per FCC during site setup.

### 2.3 Nozzle Discovery — `GET /nozzles/assigned`

Before sending authorization requests, the adapter must know the Petronite-internal nozzle IDs. These are obtained dynamically:

**Request:** `GET /nozzles/assigned`

**Response:**
```json
[
  {
    "pump": {
      "id": 1,
      "pumpName": "Pump 1",
      "pumpNumber": 1
    },
    "nozzles": [
      { "id": 123, "name": "Nozzle 1", "grade": "PMS" },
      { "id": 124, "name": "Nozzle 2", "grade": "AGO" }
    ]
  }
]
```

**Critical mapping issue:** Petronite uses internal `nozzleId` (Long, e.g., 123, 1204) for the create-order endpoint, NOT a simple nozzle number. Our middleware's `nozzles` table stores `fcc_nozzle_number` (integer). We need to map:
- `nozzles.fcc_nozzle_number` → Petronite `nozzleId`
- `pumps.fcc_pump_number` → Petronite `pump.pumpNumber`

The nozzle IDs appear to be database-level IDs in the Petronite system and may change if the bot is reconfigured. The adapter should call `GET /nozzles/assigned` on startup and periodically to refresh this mapping.

**Pump addressing comparison:**

| Our Model | Petronite Model | Notes |
|-----------|----------------|-------|
| `pumps.fcc_pump_number` | `pump.pumpNumber` | Direct integer mapping — likely 1:1 |
| `nozzles.fcc_nozzle_number` | `nozzle.id` (NOT `nozzle.name`) | The `id` is what the create-order endpoint requires |
| `nozzles.productId` → product code | `nozzle.grade` (e.g., "PMS", "AGO") | Product already in a friendly format |

### 2.4 Two-Step Pre-Authorization Flow

This is the **most significant architectural difference** from DOMS and Radix. Petronite requires two separate API calls to authorize a pump:

**Step 1 — Create Order (locks the pump/nozzle):**
```
POST /direct-authorize-requests/create
Body:
{
  "customerName": "Petronite Company LTD",
  "customerId": "112233",
  "type": "PUMA_ORDER",
  "nozzleId": 123,
  "authorizeType": "Amount",
  "dose": 50000
}
```

Response:
```json
{
  "data": {
    "id": 1001,
    "customerName": "Petronite Company LTD",
    "status": "STARTED",
    "type": "PUMA_ORDER",
    "pumpAuthorizeType": "Amount",
    "createdAt": "2026-02-26 20:00:00"
  },
  "message": "Direct authorize request created successfully",
  "errors": null
}
```

After this call, the pump/nozzle is **locked** — normal automatic authorization (nozzle lift) is prevented. The pump waits for explicit POS authorization.

**Step 2 — Authorize Pump (starts dispensing):**
```
POST /direct-authorize-requests/authorize
Body:
{
  "requestId": 1001,
  "truckNumber": "T123ABC"
}
```

**Precondition:** The physical nozzle must be picked up (lifted) BEFORE this call. The API will fail if the nozzle is down.

Response:
```json
{
  "data": {
    "id": 1001,
    "status": "COMPLETED"
  },
  "message": "Pump authorized successfully",
  "errors": null
}
```

**Why two steps?**
1. **Normal dispensing** (non-PUMA): Attendant lifts nozzle → pump auto-authorizes → fuel dispenses. This is the equivalent of our "Normal Order."
2. **PUMA dispensing** (pre-auth): Create Order → pump locks → attendant lifts nozzle → POS sends Authorize → fuel dispenses. This is the equivalent of our "Pre-Auth Order."

The lock mechanism prevents the pump from auto-authorizing when the nozzle is lifted, ensuring the POS controls the transaction.

**Implication for `SendPreAuthAsync`:** Our `IFccAdapter.SendPreAuthAsync` is a single-call interface. The Petronite adapter must orchestrate both steps internally. However, there is a physical timing dependency — the nozzle must be lifted between Step 1 and Step 2. This creates two possible approaches (see Section 9.1).

### 2.5 Authorization Types

Petronite supports three authorization types:

| `authorizeType` | `dose` Required | Description | Our Mapping |
|-----------------|----------------|-------------|-------------|
| `"Amount"` | Yes (currency value) | Authorize by monetary amount | **Primary** — matches BR-6.1b (pre-auth always by amount) |
| `"Volume"` | Yes (litres) | Authorize by volume | Not used in our system (volume authorization not supported per BR-6.1b) |
| `"FullTank"` | No (null) | No limit — dispense until tank is full or nozzle is hung up | Could be used for Normal Orders with pre-lock, but not standard pre-auth |

Our adapter will always use `"Amount"` for pre-auth, passing `requestedAmount` as the `dose` value.

### 2.6 Transaction Delivery — Webhook Callbacks (Push-Only)

Petronite delivers completed transaction data via webhook — there is **no documented pull/polling API** for retrieving historical transactions.

**Webhook Event:** `transaction_completed`

**Payload (POSTed to pre-configured endpoint):**
```json
{
  "event": "transaction_completed",
  "data": {
    "id": 15001,
    "volume": 25.50,
    "price": 2800.00,
    "amount": 71400.00,
    "pump": 1,
    "nozzle": 123,
    "grade": "PMS",
    "customer_id": "112233",
    "customer_name": "Petronite Company LTD",
    "day": "2026-02-26",
    "hour": "20:05:15",
    "receipt_code": "RCPT-987654",
    "payment_method": "PUMA_ORDER",
    "truckNumber": "T123ABC"
  }
}
```

**Key implications:**
- **No Pull mode possible.** Petronite is strictly push-only. `transactionMode` must be `PUSH` for all Petronite sites.
- **No Edge Agent catch-up poll.** The Edge Agent cannot poll the Petronite bot for missed transactions. The catch-up safety net (REQ-15.2) does not apply for Petronite.
- **Webhook must be registered on the Petronite bot.** The callback URL is configured on the Petronite bot during setup — either pointing to the cloud middleware or the Edge Agent LAN IP.
- **No explicit ACK mechanism.** The Petronite bot considers HTTP 200 from the webhook endpoint as acknowledgment. If the endpoint is unreachable, Petronite behavior on retry is **undocumented** (see Open Questions).

### 2.7 Transaction ID / Deduplication Key

Petronite provides a numeric `id` in the webhook payload.

**Primary dedup key:** `id` from webhook + `siteCode`
- `id` = Petronite's internal transaction/order ID (e.g., 15001)
- Compose as: `fccTransactionId = "{id}"` (string representation)

**Secondary dedup check (fallback):** `pump` + `nozzle` + `day` + `hour` + `amount`

### 2.8 Pending Orders Recovery

Petronite exposes an API to list pending authorization requests:

**Request:** `GET /direct-authorize-requests/pending`

**Response:**
```json
{
  "data": [
    {
      "id": 1001,
      "customerName": "Petronite Company LTD",
      "status": "STARTED",
      "type": "PUMA_ORDER",
      "nozzle": { "id": 123, "name": "Nozzle 1" },
      "pumpAuthorizeType": "FullTank",
      "dose": null,
      "createdAt": "2026-02-26 20:00:00"
    }
  ],
  "message": "Success",
  "errors": null
}
```

This is useful for:
- Recovering from Edge Agent crashes mid-authorization
- Detecting orphaned pre-auth locks that need to be cancelled
- Startup reconciliation — checking if any pre-auths are stuck in STARTED state

### 2.9 Order Details

**Request:** `GET /direct-authorize-requests/{id}/details`

**Response:**
```json
{
  "data": {
    "id": 1001,
    "customerName": "Petronite Company LTD",
    "customerIdDescription": "112233",
    "status": "STARTED",
    "type": "PUMA_ORDER"
  },
  "message": "Success",
  "errors": null
}
```

### 2.10 Cancellation

**Request:** `POST /direct-authorize-requests/{id}/cancel`

**Response:**
```json
{
  "data": {
    "id": 1001,
    "status": "CANCELLED"
  },
  "message": "Order cancelled successfully",
  "errors": null
}
```

Cancellation is only valid for orders in `STARTED` status (pending authorization). Once the pump is authorized and dispensing has begun, cancellation is not possible via the API.

### 2.11 Error Handling

Petronite uses standard HTTP status codes with a consistent response wrapper:

| Status Code | Meaning | Recoverable | Adapter Handling |
|-------------|---------|-------------|------------------|
| 200 | Success | N/A | Process response |
| 400 | Bad Request (missing/invalid params) | No — code/config bug | Log error, return failure |
| 401 | Unauthorized (token expired/invalid) | Yes — refresh token | Re-authenticate, retry once |
| 403 | Forbidden (insufficient scope) | No — config issue | Log error, return failure |
| 404 | Not Found (nozzle/order ID doesn't exist) | No — mapping issue | Log error, return failure |
| 500 | Internal Server Error | Yes — transient | Retry with backoff |

**Error response format:**
```json
{
  "data": null,
  "message": "Validation failed",
  "errors": [
    { "field": "customerId", "defaultMessage": "Customer ID is required." }
  ]
}
```

### 2.12 Fiscal Data

The webhook payload includes:
- `receipt_code` — Maps to `fiscalReceiptNumber` in our canonical model. This appears to be the Petronite-internal receipt reference, not necessarily a government fiscal receipt (e.g., from TRA/MRA).
- `customer_id` and `customer_name` — Echoed back from the create-order request.

**Open question:** Does the Petronite bot integrate with a fiscal device (EFD/VFD) for government tax reporting? If so, does `receipt_code` contain the government fiscal receipt number, or is it an internal Petronite reference? This affects whether `fiscalizationMode = FCC_DIRECT` is applicable for Petronite sites (see PQ-5).

------------------------------------------------------------------------

## 3. Integration Points Analysis

### 3.1 Adapter Interface Implementation

The Petronite adapter must implement `IFccAdapter` with these Petronite-specific behaviours:

| Interface Method | Petronite Implementation | Complexity |
|-----------------|-------------------------|------------|
| `NormalizeAsync` / `NormalizeTransaction` | Parse JSON webhook payload; map `pump`→pump number, `nozzle`→nozzle number via mapping table; compose `fccTransactionId` from `id`; convert `volume`/`amount`/`price` to microlitres/minor units; extract `receipt_code` as fiscal receipt | Low — JSON is straightforward |
| `SendPreAuthAsync` | **Two-step orchestration**: (1) `POST /create` with customer data, nozzleId, authorizeType="Amount", dose; (2) `POST /authorize` with requestId. Must handle the physical nozzle-lift timing between steps. | **High** — two-step flow with physical dependency |
| `GetPumpStatusAsync` | Use `GET /nozzles/assigned` for pump/nozzle structure + `GET /pending` for authorization state. Synthesize pump status from these. No real-time dispensing status. | Medium — synthesized, not real-time |
| `HeartbeatAsync` | Call `GET /nozzles/assigned` — if 200, bot is alive. Also validates OAuth2 token is still valid. | Low |
| `FetchTransactionsAsync` | **Not implementable** — no pull API. Return empty `TransactionBatch` with `hasMore = false`. Petronite is push-only via webhook. | N/A — not supported |
| `CancelPreAuthAsync` | `POST /direct-authorize-requests/{id}/cancel`. Requires mapping `fccCorrelationId` → Petronite request ID. | Low |

### 3.2 What Needs to Be Created (New Files)

#### Desktop Edge Agent (.NET)

| File | Description |
|------|-------------|
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Petronite/PetroniteAdapter.cs` | Main adapter implementing `IFccAdapter` — orchestrates two-step pre-auth, normalization, heartbeat |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Petronite/PetroniteProtocolDtos.cs` | JSON request/response DTOs (CreateOrderRequest, AuthorizeRequest, WebhookPayload, NozzleAssignment, etc.) |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Petronite/PetroniteOAuthClient.cs` | OAuth2 token lifecycle — acquire, cache, proactive refresh, retry on 401 |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Petronite/PetroniteNozzleResolver.cs` | Caches `GET /nozzles/assigned` response; resolves Odoo nozzle numbers → Petronite nozzle IDs; refreshes periodically |
| `src/desktop-edge-agent/tests/.../Adapter/Petronite/PetroniteAdapterTests.cs` | Unit tests for adapter |
| `src/desktop-edge-agent/tests/.../Adapter/Petronite/PetroniteOAuthClientTests.cs` | OAuth2 token management tests |
| `src/desktop-edge-agent/tests/.../Adapter/Petronite/PetroniteNozzleResolverTests.cs` | Nozzle mapping tests |

#### Cloud Backend (.NET)

| File | Description |
|------|-------------|
| `src/cloud/FccMiddleware.Adapter.Petronite/PetroniteCloudAdapter.cs` | Cloud-side adapter — mainly webhook normalization and validation |
| `src/cloud/FccMiddleware.Adapter.Petronite/Internal/PetroniteWebhookDto.cs` | Webhook payload DTOs |
| `src/cloud/FccMiddleware.Adapter.Petronite/Internal/PetroniteWebhookValidator.cs` | Validate incoming webhook payloads (authentication, required fields) |
| `src/cloud/FccMiddleware.Adapter.Petronite/FccMiddleware.Adapter.Petronite.csproj` | Project file |

#### Edge Agent (Kotlin/Java — Android)

| File | Description |
|------|-------------|
| `src/edge-agent/app/src/main/kotlin/.../adapter/petronite/PetroniteAdapter.kt` | Kotlin adapter — two-step pre-auth, normalization |
| `src/edge-agent/app/src/main/kotlin/.../adapter/petronite/PetroniteOAuthClient.kt` | OAuth2 token management |
| `src/edge-agent/app/src/main/kotlin/.../adapter/petronite/PetronitreDtos.kt` | Request/response data classes |
| `src/edge-agent/app/src/main/kotlin/.../adapter/petronite/PetroniteNozzleResolver.kt` | Nozzle ID resolution |

### 3.3 What Needs to Be Modified (Existing Files)

| File | Change | Reason |
|------|--------|--------|
| `FccDesktopAgent.Core/Adapter/Common/Enums.cs` | Add `Petronite` to `FccVendor` enum | Vendor registration |
| `FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs` | Add `ClientId` (string) and `ClientSecret` (string) to `FccConnectionConfig`. Consider adding `CustomerName`, `CustomerTaxId` to `PreAuthCommand` if not already present. | OAuth2 credentials; customer data for create-order |
| `FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs` | Add `FccVendor.Petronite` case to the switch statement | Factory registration |
| Cloud `FccMiddleware.Domain/Enums/FccVendor.cs` | Add `PETRONITE` enum value | Cloud-side vendor registration |
| Cloud `FccMiddleware.Infrastructure/Adapters/FccAdapterFactory.cs` (or `Program.cs` registration) | Register `PetroniteCloudAdapter` in the factory registry | Cloud vendor resolution |
| Cloud push ingress controller | Add webhook endpoint (or handler) that accepts Petronite's `transaction_completed` JSON format and routes to the adapter | Petronite push ingestion |
| `SiteFccConfig` / `FccConnectionConfig` | Add optional `ClientId`, `ClientSecret` fields for OAuth2-based adapters. The existing `ApiKey` field is insufficient for OAuth2 which requires both client ID and secret. | Petronite authentication config |
| VirtualLab `SeedProfileFactory.cs` | Add a `petronite-like` FCC simulator profile | Testing support |

### 3.4 Configuration Changes

**New/modified fields in `FccConnectionConfig` / `SiteFccConfig`:**

| Field | Type | Description | Required For |
|-------|------|-------------|-------------|
| `clientId` | Encrypted String | OAuth2 client ID for Petronite bot | All Petronite operations |
| `clientSecret` | Encrypted String | OAuth2 client secret for Petronite bot | All Petronite operations |
| `webhookSecret` | String (nullable) | Shared secret for validating incoming webhooks (if Petronite supports HMAC webhook signing) | Cloud push ingress validation |
| `oauthTokenEndpoint` | String (nullable) | Override path for token endpoint (default: `/oauth/token`) | Edge cases where bot uses non-standard path |

**Note:** The existing `HostAddress` and `Port` fields are sufficient for constructing the base URL (`http://{host}:{port}`). The default port is `8884`.

**Note:** `ClientId` and `ClientSecret` MUST be stored encrypted (REQ-3 BR-3.5). They replace the `ApiKey` concept used by DOMS.

------------------------------------------------------------------------

## 4. Field Mapping: Webhook Payload → CanonicalTransaction

| Petronite Webhook Field | Type (Petronite) | Canonical Field | Type (Canonical) | Conversion |
|------------------------|-----------------|-----------------|------------------|------------|
| `data.id` | Long | `fccTransactionId` | String | `"{id}"` (string representation) |
| (from config) | — | `siteCode` | String | Injected from adapter config |
| `data.pump` | Integer | `pumpNumber` | Int | Map via pump table: Petronite pump number → Odoo pump number. Likely 1:1 but must go through mapping. |
| `data.nozzle` | Long | `nozzleNumber` | Int | Map via nozzle table: Petronite nozzle ID → `odoo_nozzle_number`. **This is the critical mapping** — Petronite `nozzle` in webhook = nozzle ID from create-order, NOT a simple sequential number. |
| `data.grade` | String | `productCode` | String | Direct if Petronite uses standard codes (PMS, AGO). Otherwise via `productCodeMapping`. |
| `data.volume` | Double | `volumeMicrolitres` | Long | `(long)(volume * 1_000_000m)` — Petronite appears to use litres with 2 decimal places |
| `data.amount` | Double | `amountMinorUnits` | Long | Depends on currency. For ZMW (2 decimal places): `(long)(amount * 100m)`. **Needs confirmation** — see PQ-1. |
| `data.price` | Double | `unitPriceMinorPerLitre` | Long | Same currency conversion as `amount`. |
| `data.day` + `data.hour` | String + String | `startedAt` / `completedAt` | DateTimeOffset | Parse `"yyyy-MM-dd"` + `"HH:mm:ss"`, apply configured timezone → UTC. **Note:** Only one timestamp provided — use for both `startedAt` and `completedAt` (or set `startedAt` = null). |
| `data.receipt_code` | String | `fiscalReceiptNumber` | String? | Direct mapping. May be null for non-fiscalized transactions. |
| — | — | `fccVendor` | Enum | `FccVendor.Petronite` |
| `data.customer_id` | String | (stored in raw payload) | — | Not in canonical model; preserved in raw payload for pre-auth reconciliation |
| `data.customer_name` | String | (stored in raw payload) | — | Not in canonical model; preserved in raw payload |
| `data.payment_method` | String | (stored in raw payload) | — | "PUMA_ORDER" = pre-auth, "CUSTOMER_ORDER" = also pre-auth variant. Anything else = normal order. Useful for reconciliation logic. |
| `data.truckNumber` | String | (stored in raw payload) | — | Vehicle number; maps to `vehicleNumber` if we extend canonical model |

**Fields NOT provided by Petronite webhook:**
- `attendantId` — Not available. Null.
- `startedAt` (dispense start time) — Only completion time provided (`day` + `hour`). Use completion time for both or leave `startedAt` null.
- `endDateTime` — Same as `day` + `hour`.
- `currencyCode` — Not in webhook payload. Injected from site/legal entity config.

------------------------------------------------------------------------

## 5. Pre-Auth Field Mapping: PreAuthCommand → Petronite Create Order

| PreAuthCommand Field | Petronite Create Order Field | Conversion |
|---------------------|----------------------------|------------|
| `CustomerTaxId` (or `customerId` from Odoo) | `customerId` | Direct mapping — this is a required field in Petronite |
| `CustomerName` | `customerName` | Direct mapping (optional in Petronite) |
| — | `type` | Hardcoded `"PUMA_ORDER"` (the workflow type for POS-controlled authorization) |
| `FccNozzleNumber` | `nozzleId` | Map from our `fcc_nozzle_number` → Petronite nozzle ID via the nozzle resolver. **This is the Petronite-internal ID**, not a simple number. |
| — | `authorizeType` | `"Amount"` (we always authorize by amount per BR-6.1b) |
| `RequestedAmountMinorUnits` | `dose` | Convert from minor units to major units (decimal): e.g., `RequestedAmountMinorUnits / 100.0` for ZMW. **Petronite expects the dose in major currency units** (based on webhook response format). |
| `VehicleNumber` | (sent in Step 2 as `truckNumber`) | Passed to the Authorize Pump call, not the Create Order call |

**Step 2 mapping (Authorize Pump):**

| Source | Petronite Authorize Field | Notes |
|--------|--------------------------|-------|
| Create Order response `data.id` | `requestId` | The Petronite order ID returned in Step 1 |
| `PreAuthCommand.VehicleNumber` | `truckNumber` | Optional, depends on Petronite configuration |

------------------------------------------------------------------------

## 6. Pre-Auth Response Mapping: Petronite → PreAuthResult

### Step 1 (Create Order) Response:

| Petronite Field | PreAuthResult Field | Conversion |
|----------------|--------------------| ------------|
| `data.id` | `FccCorrelationId` | String representation: `"{id}"`. This ID is used for all subsequent operations (authorize, cancel, details). |
| `data.status` = "STARTED" | `Accepted` | `true` if status = "STARTED" |
| HTTP 200 | `Accepted` | `true` |
| HTTP 400/404/500 | `Accepted` | `false` |
| `message` (on error) | `ErrorMessage` | Direct mapping |
| `errors[].defaultMessage` | `ErrorMessage` | Concatenate error messages |

### Step 2 (Authorize) Response:

| Petronite Field | Effect | Notes |
|----------------|--------|-------|
| `data.status` = "COMPLETED" | Pre-auth transitions to `AUTHORIZED` state | Pump is now dispensing |
| HTTP error | Pre-auth remains in `PENDING` state | May need retry (nozzle not lifted yet) |

**Note on PreAuthResult timing:** The adapter returns `PreAuthResult` after Step 1 (Create Order) succeeds. Step 2 (Authorize) may happen separately, potentially after a delay while the attendant lifts the nozzle. See Section 9.1 for the architectural decision on how to handle this.

------------------------------------------------------------------------

## 7. Push Mode Integration (Webhook)

### 7.1 Cloud Webhook Ingress

In `CLOUD_DIRECT` ingestion mode, the Petronite bot sends webhooks directly to the cloud middleware. This requires:

1. **New webhook endpoint** on the cloud middleware:
   - Accept `Content-Type: application/json`
   - Route: e.g., `POST /api/v1/ingest/petronite/webhook` or generic `POST /api/v1/ingest/webhook` with vendor detection from payload
   - Validate the payload (required fields, event type = `transaction_completed`)
   - Authenticate the webhook (shared secret in header, IP allowlisting, or other mechanism — see PQ-3)
   - Parse and normalize to canonical model
   - Respond with HTTP 200 to acknowledge receipt
   - Process through standard dedup → store pipeline

2. **The Petronite bot must be configured with the cloud endpoint URL.** This is a one-time setup step during site provisioning.

3. **No special response format needed** — unlike Radix, Petronite just expects a standard HTTP 200.

### 7.2 Edge Agent Webhook Reception (RELAY / BUFFER_ALWAYS Modes)

When the Edge Agent is the webhook target:
1. The Edge Agent already runs an HTTP server on the LAN for its local API (`localhost:8585`)
2. A separate endpoint (or the same server on a different route, accessible from the Petronite bot's IP) must accept webhook POSTs
3. Parse, normalize, buffer locally
4. Respond with HTTP 200

**Webhook target configuration per ingestion mode:**

| Ingestion Mode | Webhook Target | Notes |
|---------------|----------------|-------|
| `CLOUD_DIRECT` | Cloud middleware public URL | Petronite bot must have internet access (or VPN) to reach the cloud. **This may not be possible** if the bot is on an isolated LAN. |
| `RELAY` | Edge Agent LAN IP (e.g., `http://192.168.1.50:8586/webhook`) | Edge Agent relays to cloud when internet is available |
| `BUFFER_ALWAYS` | Edge Agent LAN IP | Edge Agent always buffers first |

**Important:** Unlike DOMS/Radix where the FCC hardware may have its own internet connection (SIM, VPN), the Petronite bot is typically a LAN-only device. **The default ingestion mode for Petronite sites should likely be `RELAY` or `BUFFER_ALWAYS`**, not `CLOUD_DIRECT`. See PQ-4.

### 7.3 Missed Transaction Recovery

Since there is no pull API, if a webhook delivery fails and Petronite does not retry:
- The transaction is **lost** from the automated pipeline
- Manual recovery would require checking the Petronite bot's own interface/logs
- This is a significant reliability concern — see Risk Register

**Mitigation options:**
1. Confirm Petronite's webhook retry behaviour (PQ-2)
2. Edge Agent as the webhook target (RELAY mode) — more reliable than cloud target since it's on the same LAN
3. Request Petronite to add a transaction history/pull API (PQ-6)

------------------------------------------------------------------------

## 8. Heartbeat / Health Check Strategy

Petronite does not have a dedicated heartbeat endpoint. Options:

| Strategy | Endpoint | Pro | Con |
|----------|---------|-----|-----|
| **Nozzle assigned** (recommended) | `GET /nozzles/assigned` | Confirms bot is responsive, validates token, returns useful data | Slightly heavier than a simple ping |
| **Token refresh** | `POST /oauth/token` | Confirms auth layer works | Doesn't confirm application layer is healthy |
| **Pending orders** | `GET /direct-authorize-requests/pending` | Confirms core API works | May return large payloads on busy sites |

**Recommendation:** Use `GET /nozzles/assigned` as the heartbeat. It is read-only, confirms the bot application layer is healthy, validates the OAuth2 token, and returns nozzle data that can be used to refresh the nozzle mapping cache.

------------------------------------------------------------------------

## 9. Differences Requiring Architectural Decisions

### 9.1 Two-Step Pre-Auth Handling

**The core challenge:** Our `IFccAdapter.SendPreAuthAsync` expects a single call that returns a `PreAuthResult`. Petronite requires two calls with a physical action (nozzle lift) in between.

| Option | Approach | Impact |
|--------|---------|--------|
| **A. Create-only in SendPreAuthAsync** | `SendPreAuthAsync` only calls Step 1 (Create Order). Returns `PreAuthResult` with `Accepted=true` and `FccCorrelationId = petroniteOrderId`. The Authorize step is handled separately — either by the attendant in the Petronite app, or via a new adapter method called when the Edge Agent detects the nozzle is ready. | Cleanest interface match. Requires the authorization step to happen outside the adapter's `SendPreAuthAsync` flow. The Edge Agent or Odoo POS must handle Step 2 timing. |
| **B. Create + Poll + Authorize in SendPreAuthAsync** | `SendPreAuthAsync` calls Step 1, then polls the Petronite bot or waits for a configurable timeout for the nozzle to be lifted, then calls Step 2. Returns final result. | Single-call semantics match the interface. But the call may block for 30+ seconds waiting for the nozzle lift. Risk of timeout. |
| **C. Extend IFccAdapter with a two-phase pre-auth** | Add a new method like `Task<PreAuthResult> ConfirmPreAuthAsync(string fccCorrelationId, CancellationToken ct)` that handles Step 2. `SendPreAuthAsync` handles Step 1 only. | Most flexible. Requires interface change that affects all adapters. |

**Recommendation:** **Option A** for MVP. `SendPreAuthAsync` performs Step 1 only and returns the Petronite order ID as the correlation ID. The Edge Agent's PreAuth handler then calls a Petronite-specific authorize method (e.g., via a separate adapter method or a Petronite-specific code path) when the attendant signals readiness (e.g., via a button in Odoo POS that triggers the authorize call). For DOMS/Radix, the existing single-step flow remains unchanged.

**Alternative:** If Option A creates too much complexity in the Edge Agent's PreAuth flow, use **Option B** with a configurable poll interval (e.g., check nozzle status every 2 seconds for up to 60 seconds) and return failure if the nozzle is not lifted in time. The attendant can retry.

### 9.2 No Pull Mode — Ingestion Constraints

Petronite has no transaction fetch API. This affects:

| Feature | Impact | Mitigation |
|---------|--------|------------|
| `FetchTransactionsAsync` | Returns empty. Cannot be used. | Adapter reports `SupportedIngestionMethods = [PUSH]` in metadata |
| `transactionMode = PULL` | Not valid for Petronite | Config validation must reject `PULL` or `HYBRID` for Petronite sites |
| Edge Agent catch-up poll (REQ-15.2) | Cannot catch up on missed webhooks | Must rely on Petronite's own webhook retry + RELAY mode for reliability |
| `CLOUD_DIRECT` ingestion mode | Risky if Petronite bot cannot reach cloud | Default to `RELAY` for Petronite sites |

**Metadata reporting:**
```csharp
public AdapterInfo GetAdapterMetadata() => new()
{
    Vendor = FccVendor.Petronite,
    AdapterVersion = "1.0.0",
    SupportedIngestionMethods = [IngestionMethod.PUSH],
    SupportsPreAuth = true,
    SupportsPumpStatus = false,  // synthesized only
    Protocol = "REST"
};
```

### 9.3 Nozzle ID Resolution Strategy

Petronite's `nozzleId` is an internal database ID (Long), not a simple sequential number. This creates a mapping challenge:

| Option | Approach | Impact |
|--------|---------|--------|
| **A. Store Petronite nozzle ID in `fcc_nozzle_number`** | Treat the Petronite `nozzle.id` (e.g., 123, 1204) as the `fcc_nozzle_number` value in our nozzle mapping table. | Simple. Works if Petronite IDs are stable. May be confusing since they're not sequential "numbers". |
| **B. Dynamic resolution via nozzle API** | On startup and periodically, call `GET /nozzles/assigned` and build an in-memory mapping: `(pumpNumber, nozzleName/grade) → petroniteNozzleId`. Use pump number + product to resolve. | More resilient to Petronite ID changes. Adds complexity and a runtime dependency. |
| **C. Hybrid: Static config + dynamic validation** | Store Petronite nozzle IDs in `fcc_nozzle_number` (synced from Odoo). On startup, validate against `GET /nozzles/assigned`. Alert if mismatch. | Best of both worlds. Configuration errors caught early. |

**Recommendation:** **Option C** — store Petronite nozzle IDs in the existing `fcc_nozzle_number` field during Databricks sync. The adapter calls `GET /nozzles/assigned` on startup and validates the mapping. If a mismatch is detected (e.g., after Petronite bot reconfiguration), raise an alert.

### 9.4 OAuth2 Token Lifecycle

The adapter must manage the OAuth2 token across its lifetime:

- **Acquisition:** On first API call or startup, obtain a token
- **Caching:** Store in memory, reuse until close to expiry
- **Proactive refresh:** Refresh when remaining TTL < configurable threshold (e.g., 10 minutes before expiry)
- **Reactive refresh:** On HTTP 401, clear cached token, re-authenticate, retry the original request once
- **Thread safety:** Token refresh must be thread-safe (multiple concurrent adapter calls may trigger refresh simultaneously)

This is best encapsulated in a `PetroniteOAuthClient` class used by the adapter.

### 9.5 Webhook Authentication

The current Petronite documentation does not describe how webhook calls are authenticated. The cloud or Edge Agent endpoint receiving webhooks needs to verify they are genuinely from the Petronite bot and not spoofed.

| Option | Mechanism | Notes |
|--------|----------|-------|
| **A. Shared secret in header** | Petronite includes `X-Webhook-Secret: <shared_secret>` in webhook calls | Standard pattern. Needs Petronite to support it. |
| **B. HMAC signature** | Petronite signs the webhook body with a shared secret | More secure. Needs Petronite to support it. |
| **C. IP allowlisting** | Only accept webhooks from the Petronite bot's known IP | Simple but fragile (IP changes). |
| **D. Mutual TLS** | Client certificate validation | Overkill for LAN. |
| **E. None (trust LAN)** | Accept any webhook on the LAN endpoint | Acceptable for RELAY mode (LAN-only) but not for CLOUD_DIRECT (public endpoint) |

**Recommendation:** For MVP, use IP allowlisting (edge agent is on same LAN) + a configurable shared secret header if Petronite supports it. See PQ-3.

### 9.6 Normal Order Detection vs Pre-Auth Order

Petronite's webhook payload includes `payment_method`:
- `"PUMA_ORDER"` = transaction was initiated via the POS (pre-auth flow)
- Other values (unclear what Petronite uses for normal dispenses) = attendant lifted nozzle and dispensed without POS involvement

The adapter must use `payment_method` to determine whether to attempt pre-auth reconciliation (REQ-8) or treat the transaction as a Normal Order (REQ-7).

**Open question:** What `payment_method` value does Petronite use for normal (non-PUMA) dispenses? See PQ-7.

------------------------------------------------------------------------

## 10. Open Questions

| ID | Question | Impact | Proposed Answer |
|----|----------|--------|----------------|
| PQ-1 | **Currency/amount format in webhook:** `amount: 71400.00` and `price: 2800.00` — are these in major currency units (e.g., ZMW 71400.00 = 7140000 ngwee)? Or are they already in minor units? The `.00` suffix suggests major units. | Determines conversion to `amountMinorUnits`. Getting this wrong corrupts all financial data. | **Likely major currency units** based on the example: volume 25.50L × price 2800.00 = amount 71400.00. This math only works in major units. Confirm with Petronite/deployment team. |
| PQ-2 | **Webhook retry behaviour:** When the webhook target is unreachable, does Petronite retry? If so, how many times, with what interval, and for how long? | Critical for reliability. If Petronite does not retry, missed webhooks = lost transactions. | **Needs confirmation from Petronite team.** If no retry exists, RELAY mode (LAN webhook → Edge Agent) becomes mandatory for reliability. |
| PQ-3 | **Webhook authentication:** Does Petronite support signing webhooks (HMAC, shared secret header)? Or is the webhook unauthenticated? | Security. An unauthenticated cloud webhook endpoint is vulnerable to spoofing. | **Needs confirmation.** For RELAY mode (LAN), IP allowlisting is acceptable for MVP. For CLOUD_DIRECT, stronger auth is needed. |
| PQ-4 | **Petronite bot internet connectivity:** Can the Petronite bot reach the public internet (to push webhooks to cloud), or is it strictly LAN-only? | Determines whether `CLOUD_DIRECT` is feasible. If LAN-only, must use `RELAY` or `BUFFER_ALWAYS`. | **Likely LAN-only** — the documentation says "POS and Petronite bot will be running on the same LAN/WIFI network" and all examples use LAN IPs/localhost. Default to `RELAY` for Petronite. |
| PQ-5 | **Fiscal device integration:** Does the Petronite bot integrate with a government fiscal device (EFD/VFD)? Is `receipt_code` a government fiscal receipt number (like Radix's `EFD_ID`) or an internal Petronite reference? | Determines whether `fiscalizationMode = FCC_DIRECT` is valid for Petronite sites, and whether `receipt_code` can be trusted as a fiscal receipt in our canonical model. | **Needs confirmation from Petronite/deployment team.** |
| PQ-6 | **Transaction history API:** Does Petronite have an undocumented endpoint for fetching historical transactions (e.g., `GET /transactions`), or is webhook truly the only way to get transaction data? | If a pull API exists, the Edge Agent can do catch-up polling, dramatically improving reliability. | **Ask Petronite team.** The documentation only shows the webhook, but the bot clearly stores transaction data (it has an Orders screen with completed orders). |
| PQ-7 | **Normal Order `payment_method`:** What `payment_method` value does Petronite use for normal (non-PUMA) dispenses? Is it `"CASH"`, `"NORMAL"`, or something else? And are normal dispenses even sent via webhook, or only PUMA orders? | Critical for distinguishing pre-auth reconciliation targets from Normal Orders. If normal dispenses are NOT sent via webhook, Petronite sites cannot have Normal Order ingestion at all. | **Needs confirmation.** If only PUMA_ORDER transactions are webhhooked, then Petronite is pre-auth-only from a middleware perspective, and Normal Orders at Petronite sites must be handled manually. |
| PQ-8 | **Nozzle ID stability:** How stable are the `nozzle.id` values returned by `GET /nozzles/assigned`? Do they change when the Petronite bot is reconfigured, or are they permanent database IDs? | Affects whether we can store them statically in the nozzle mapping table or need dynamic resolution. | **Likely stable** (database IDs). But validate on startup using Option C from Section 9.3. |
| PQ-9 | **Authorize step nozzle-lift detection:** Is there an API to check if the nozzle is physically lifted, or does the Authorize endpoint simply fail with a 400 if the nozzle is down? | Affects Option B in Section 9.1 (poll-then-authorize strategy). | **Likely fails with 400.** The adapter should retry Step 2 with short delays if it gets a 400 indicating nozzle not ready. |
| PQ-10 | **Concurrent orders on same pump:** Can multiple PUMA orders be created for the same pump/nozzle simultaneously (queued), or does the pump lock prevent a second Create Order? | Affects idempotency handling and error recovery. | **Likely prevented** — the pump lock from Step 1 should block a second Create Order. Confirm with Petronite team. |

------------------------------------------------------------------------

## 11. Implementation Plan

### Phase 1: Core Adapter Skeleton (Edge Agent — Desktop .NET)

**Goal:** Basic Petronite communication — OAuth2 token management, nozzle discovery, heartbeat.

1. **Create `PetroniteOAuthClient`** — OAuth2 Client Credentials flow
   - Token acquisition via `POST /oauth/token`
   - Token caching with proactive refresh (refresh at 90% of TTL)
   - Thread-safe token refresh (single refresh in flight)
   - Retry on 401 (clear cache, re-authenticate, retry once)
   - Unit tests with mock HTTP server

2. **Create `PetroniteNozzleResolver`** — Nozzle ID mapping
   - Fetch `GET /nozzles/assigned` and cache
   - Method: `ResolvePetroniteNozzleId(int fccPumpNumber, int fccNozzleNumber) → long petroniteNozzleId`
   - Periodic refresh (configurable, e.g., every 30 minutes)
   - Startup validation against configured nozzle mapping
   - Unit tests

3. **Create `PetroniteProtocolDtos`** — JSON data classes
   - `PetroniteTokenResponse` (access_token, token_type, expires_in, scope)
   - `PetronisteNozzleAssignment` (pump, nozzles)
   - `PetroniteCreateOrderRequest` / `PetroniteCreateOrderResponse`
   - `PetroniteAuthorizeRequest` / `PetroniteAuthorizeResponse`
   - `PetroniteWebhookPayload` (event, data with all fields)
   - `PetronitePendingOrder`
   - `PetroniteErrorResponse` (data, message, errors[])

4. **Implement `HeartbeatAsync`** — call `GET /nozzles/assigned`, return true on 200

5. **Register Petronite in `FccVendor` enum and adapter factory**

6. **Unit tests** for OAuth client and nozzle resolver

### Phase 2: Transaction Normalization (Webhook)

**Goal:** Parse Petronite webhook payloads and normalize to canonical model.

1. **Implement `NormalizeAsync` / `NormalizeTransaction`**
   - Parse JSON webhook payload
   - Map `pump` → canonical pump number via nozzle mapping
   - Map `nozzle` (Petronite nozzle ID) → canonical nozzle number via nozzle mapping
   - Map `grade` → product code via `productCodeMapping` (or direct if Petronite uses standard codes)
   - Convert `volume` to microlitres
   - Convert `amount` and `price` to minor units (after PQ-1 is resolved)
   - Parse `day` + `hour` → DateTimeOffset with timezone
   - Extract `receipt_code` as fiscal receipt number
   - Compose `fccTransactionId` from `id`

2. **Implement `ValidatePayload`** (cloud adapter)
   - Check event type = `transaction_completed`
   - Check required fields: id, volume, amount, pump, nozzle, grade, day, hour
   - Validate vendor match

3. **Implement `FetchTransactionsAsync`** — return empty `TransactionBatch` (push-only)

4. **Integration tests** with sample webhook payloads

### Phase 3: Pre-Authorization (Two-Step)

**Goal:** Send pre-auth commands to Petronite bot.

1. **Implement `SendPreAuthAsync`** (Step 1 — Create Order)
   - Resolve Petronite nozzle ID from FCC pump/nozzle numbers
   - Build `PetroniteCreateOrderRequest`: customerId, customerName, type="PUMA_ORDER", nozzleId, authorizeType="Amount", dose
   - POST to `/direct-authorize-requests/create`
   - Parse response, extract `id` as `FccCorrelationId`
   - Return `PreAuthResult` with `Accepted = true` on success

2. **Implement authorize method** (Step 2 — Authorize Pump)
   - Build `PetroniteAuthorizeRequest`: requestId (from Step 1), truckNumber
   - POST to `/direct-authorize-requests/authorize`
   - Handle nozzle-not-lifted error (400) — allow retry
   - Expose via adapter-specific method or general adapter mechanism (per Section 9.1 decision)

3. **Implement `CancelPreAuthAsync`**
   - POST to `/direct-authorize-requests/{id}/cancel`
   - `fccCorrelationId` = Petronite order ID

4. **Implement startup reconciliation**
   - On startup, call `GET /direct-authorize-requests/pending`
   - Log/alert any orphaned orders
   - Optionally auto-cancel orders older than a configurable threshold

5. **Tests with various error scenarios** (bad nozzle ID, token expired, nozzle not lifted)

### Phase 4: Push Mode — Webhook Endpoint

**Goal:** Handle incoming webhooks from Petronite bot.

1. **Cloud: Webhook ingress endpoint**
   - Accept `Content-Type: application/json`
   - Route: `POST /api/v1/ingest/petronite/webhook`
   - Validate payload (event type, required fields)
   - Authenticate (per PQ-3 resolution)
   - Normalize via `PetroniteCloudAdapter.NormalizeTransaction`
   - Process through standard dedup → store pipeline
   - Respond with HTTP 200

2. **Edge Agent: LAN webhook listener** (for RELAY/BUFFER_ALWAYS modes)
   - HTTP endpoint on the Edge Agent's LAN-accessible server
   - Route: e.g., `POST /api/webhook/petronite`
   - Parse, normalize, buffer locally
   - Respond with HTTP 200

3. **Webhook target configuration in Petronite bot setup**
   - Document the provisioning step: configure webhook URL on the Petronite bot pointing to cloud or Edge Agent

### Phase 5: VirtualLab Simulation Profile

**Goal:** Create a testable Petronite bot simulator.

1. **Add `petronite-like` profile** to `SeedProfileFactory`
   - OAuth2 token endpoint mock
   - Nozzle assignment response
   - Create/Authorize/Cancel order endpoints
   - Webhook callback simulation (simulate `transaction_completed` webhook after authorize)
   - Two-step pre-auth flow with configurable nozzle-lift delay

------------------------------------------------------------------------

## 12. Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| **Webhook delivery failure = lost transactions** — No pull API means no catch-up mechanism. If a webhook fails and Petronite doesn't retry, the transaction is lost. | Medium | **High** — data loss | Use RELAY mode (LAN webhook → Edge Agent) as default for Petronite. Confirm Petronite retry behaviour (PQ-2). Request a pull API from Petronite (PQ-6). |
| **Two-step pre-auth timing — nozzle not lifted** — Step 2 (Authorize) requires the nozzle to be physically lifted. If the attendant is slow or forgets, the authorize call fails. | High | Medium — poor UX, pre-auth stuck in STARTED | Implement retry logic with timeout. Provide a UI affordance in Odoo POS ("Lift nozzle and press Authorize"). Alert after timeout. Auto-cancel stale STARTED orders. |
| **Normal Orders not sent via webhook** — If Petronite only webhooks PUMA_ORDER transactions, Normal Orders are invisible to the middleware. | Medium | **High** — majority of transactions at most sites are Normal Orders | Confirm PQ-7. If normal orders are excluded, Petronite sites would need a different ingestion strategy or Petronite must enable normal order webhooks. |
| **OAuth2 token invalidation on bot restart** — If the Petronite bot restarts, existing tokens may be invalidated without the standard expiry. | Medium | Low — brief communication interruption | Adapter handles 401 by re-authenticating. Minimal disruption. |
| **Nozzle ID changes after Petronite bot reconfiguration** — If Petronite reassigns nozzle IDs, our static mapping becomes stale. | Low | Medium — pre-auth sent to wrong nozzle, transactions mapped incorrectly | Startup validation against `GET /nozzles/assigned`. Alert on mismatch. |
| **Currency format ambiguity** — Getting the amount/price conversion wrong silently corrupts financial data. | Medium | **High** — financial data corruption | Confirm PQ-1 with Petronite team before go-live. Add sanity-check logging (e.g., verify `volume × price ≈ amount`). |
| **Petronite bot is LAN-only — CLOUD_DIRECT not feasible** | High | Medium — must use RELAY mode | Default to RELAY for all Petronite sites. Document this in deployment guide. |
| **Webhook endpoint spoofing** — Without webhook authentication, anyone on the network could send fake transaction webhooks | Low (LAN) / Medium (cloud) | High — fake transactions created | Implement webhook authentication (PQ-3). For LAN/RELAY mode, IP allowlisting is acceptable. |

------------------------------------------------------------------------

## 13. Acceptance Criteria

- [ ] Petronite adapter passes all applicable `IFccAdapter` interface contract requirements
- [ ] OAuth2 token acquisition, caching, and proactive refresh work correctly
- [ ] OAuth2 token is re-acquired transparently on 401 (bot restart scenario)
- [ ] `GET /nozzles/assigned` correctly resolves Petronite nozzle IDs and validates against configured mapping
- [ ] Heartbeat correctly reports Petronite bot reachability
- [ ] Pre-auth Step 1 (Create Order) correctly creates a PUMA_ORDER with the right nozzle ID, authorize type, and dose
- [ ] Pre-auth Step 2 (Authorize) correctly triggers pump authorization after nozzle lift
- [ ] Pre-auth cancellation works via `POST /{id}/cancel`
- [ ] Webhook payloads are correctly parsed and normalized to canonical transactions
- [ ] `fccTransactionId` composed from Petronite `id` enables deduplication
- [ ] Pump/nozzle numbers in webhook are correctly mapped to Odoo numbers via the nozzle mapping table
- [ ] Amount and volume conversion to minor units / microlitres is correct (confirmed against PQ-1)
- [ ] Timestamp conversion correctly applies configured timezone
- [ ] Fiscal receipt (`receipt_code`) is correctly extracted and mapped
- [ ] `payment_method` correctly distinguishes pre-auth (PUMA_ORDER) from Normal Orders
- [ ] Cloud webhook ingress accepts Petronite `transaction_completed` payloads and responds with HTTP 200
- [ ] Edge Agent LAN webhook listener accepts and buffers Petronite webhooks in RELAY mode
- [ ] Adapter metadata correctly reports capabilities: `supportsPreAuth=true`, `supportsPumpStatus=false`, `supportedMethods=[PUSH]`
- [ ] `FetchTransactionsAsync` returns empty result (pull not supported)
- [ ] Config validation rejects `transactionMode=PULL` or `HYBRID` for Petronite sites
- [ ] Orphaned pending orders are detected on startup and handled (alert/auto-cancel)
- [ ] VirtualLab has a working Petronite simulation profile for testing

------------------------------------------------------------------------

## 14. Dependencies and Prerequisites

Before implementation begins, resolve:

1. **PQ-1 (Currency format)** — **Critical.** Cannot correctly normalize financial data without this.
2. **PQ-2 (Webhook retry)** — **Critical.** Determines reliability strategy and default ingestion mode.
3. **PQ-4 (Bot internet connectivity)** — **Important.** Determines whether CLOUD_DIRECT is even possible.
4. **PQ-7 (Normal Order webhooks)** — **Critical.** Determines whether Petronite sites can ingest Normal Orders at all.
5. **PQ-3 (Webhook authentication)** — **Important for cloud endpoint security.**
6. **Access to a real Petronite bot** (or a test instance) for integration testing.
7. **Petronite test credentials** — Client ID: `puma`, Secret: provided in documentation (for test environment only).

------------------------------------------------------------------------

## Appendix A: Complete Petronite API Endpoint Reference

| Endpoint | Method | Purpose | Auth |
|----------|--------|---------|------|
| `/oauth/token` | POST | Obtain bearer token (OAuth2 Client Credentials) | Basic auth (client_id:client_secret) |
| `/nozzles/assigned` | GET | List assigned pump/nozzle IDs for the day | Bearer token |
| `/direct-authorize-requests/create` | POST | Create a PUMA authorization order (locks pump) | Bearer token |
| `/direct-authorize-requests/pending` | GET | List all pending authorization requests | Bearer token |
| `/direct-authorize-requests/{id}/details` | GET | View details of a specific authorization request | Bearer token |
| `/direct-authorize-requests/authorize` | POST | Trigger pump authorization (requires nozzle lifted) | Bearer token |
| `/direct-authorize-requests/{id}/cancel` | POST | Cancel a pending authorization request | Bearer token |
| *Webhook callback* | POST (outbound) | Transaction completed notification sent TO your endpoint | TBD (see PQ-3) |

## Appendix B: Petronite Order Status Lifecycle

```
[Create Order] → STARTED → [Authorize Pump] → COMPLETED → (dispensing) → transaction_completed webhook
                     │
                     └── [Cancel Order] → CANCELLED
```

- `STARTED`: Order created, pump locked, awaiting authorization
- `COMPLETED`: Pump authorized, dispensing allowed (returned by Authorize endpoint; webhook has the actual dispense data)
- `CANCELLED`: Order cancelled before authorization

## Appendix C: Petronite vs Our Pre-Auth State Mapping

| Petronite Status | Event | Our PreAuth State | Notes |
|-----------------|-------|-------------------|-------|
| `STARTED` | Create Order success | `PENDING` | Pump locked, awaiting nozzle lift + authorize |
| (no status change) | Nozzle lifted | (no change) | Physical event, not API-visible |
| `COMPLETED` | Authorize Pump success | `AUTHORIZED` | Pump is dispensing |
| (webhook received) | `transaction_completed` | `COMPLETED` | Final dispense data available for reconciliation |
| `CANCELLED` | Cancel Order | `CANCELLED` | Pump lock released |
| (timeout) | No authorize within timeout | `EXPIRED` | Adapter must handle — auto-cancel via API or internal timeout |

## Appendix D: Response Wrapper Format

All Petronite API responses (except OAuth token) use this wrapper:

```json
{
  "data": <payload or null>,
  "message": "<human-readable message>",
  "errors": <null or array of { "field": "...", "defaultMessage": "..." }>
}
```

The adapter should check `errors` array presence and `data` nullity to detect failures, not just HTTP status code, as some errors may return 200 with an error in the wrapper.
