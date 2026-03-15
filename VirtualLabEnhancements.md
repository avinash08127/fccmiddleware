# Virtual Lab Enhancements — Vendor-Specific FCC Protocol Simulators

## Goal

Enable the Virtual Lab to accept pre-auth requests (and transaction fetches) from **all 5 FCC adapters** in the desktop edge agent, so that end-to-end testing can be performed without real FCC hardware.

Currently, the Virtual Lab exposes only a generic REST interface at `/fcc/{siteCode}/preauth/{create|authorize|cancel}`. Each adapter speaks a vendor-specific protocol with different URLs, field names, data formats, and transports. This plan adds vendor-faithful simulators to the Virtual Lab.

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                        VIRTUAL LAB                              │
│                                                                 │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────┐  │
│  │ Generic REST  │  │ DOMS JPL TCP │  │ PreAuthSimulation    │  │
│  │ /fcc/{site}/  │  │ (existing)   │  │ Service (existing)   │  │
│  │  preauth/*    │  │ port 4711    │  │                      │  │
│  └──────┬───────┘  └──────┬───────┘  └──────────┬───────────┘  │
│         │                 │                      │              │
│  ┌──────┴───────┐  ┌──────┴───────┐  ┌──────────┴───────────┐  │
│  │ NEW: DOMS    │  │              │  │                      │  │
│  │ REST Shim    │──┤              ├──┤  Shared PreAuth      │  │
│  │ /api/v1/     │  │              │  │  Session Store       │  │
│  │  preauth     │  │              │  │  (VirtualLabDb)      │  │
│  ├──────────────┤  │              │  │                      │  │
│  │ NEW:Petronite│  │              │  │                      │  │
│  │ /direct-     │──┤              ├──┤                      │  │
│  │  authorize-  │  │              │  │                      │  │
│  │  requests/*  │  │              │  │                      │  │
│  ├──────────────┤  │              │  │                      │  │
│  │ NEW:Advatec  │  │              │  │                      │  │
│  │ :5560        │──┤              ├──┤                      │  │
│  │ JSON device  │  │              │  │                      │  │
│  ├──────────────┤  │              │  │                      │  │
│  │ NEW: Radix   │  │              │  │                      │  │
│  │ :7098 XML    │──┤              ├──┤                      │  │
│  │ Auth + Txn   │  │              │  │                      │  │
│  └──────────────┘  └──────────────┘  └──────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

All vendor shims translate their protocol into calls to the existing `IPreAuthSimulationService` (and `IForecourtSimulationService` for transactions), so the shared session store, state machine, expiry logic, failure injection, and scenario engine all remain centralised.

---

## Task Breakdown

### VLE-01: DOMS REST Protocol Simulator

**Priority:** P0 (simplest, highest coverage)
**Estimated Complexity:** Small
**Depends on:** None

#### Problem

The DOMS REST adapter (`DomsAdapter.cs`) sends:
```
POST {baseUrl}/api/v1/preauth
Header: X-API-Key: {key}
Content-Type: application/json

{
  "PreAuthId": "PA-123",
  "PumpNumber": 1,
  "NozzleNumber": 2,
  "ProductCode": "PMS",
  "AmountMinorUnits": 50000,
  "UnitPriceMinorPerLitre": 2500,
  "CurrencyCode": "ZAR",
  "VehicleNumber": "ABC123",
  "CorrelationId": "corr-001"
}
```

And expects back:
```json
{
  "Accepted": true,
  "CorrelationId": "corr-001",
  "AuthorizationCode": "AUTH-XYZ",
  "ErrorCode": null,
  "Message": "Pre-auth accepted",
  "ExpiresAtUtc": "2026-03-15T10:35:00Z"
}
```

The Virtual Lab has no endpoint matching `/api/v1/preauth`, and uses `amount` in major units vs `AmountMinorUnits`.

#### Files to Create

**1. `VirtualLab/src/VirtualLab.Infrastructure/DomsRest/DomsRestSimulatorEndpoints.cs`**

A static class with `MapDomsRestSimulatorEndpoints(this IEndpointRouteBuilder app)` extension method.

Route group: `/doms/{siteCode}` (site code in path to allow multi-site testing).

Endpoints:

| Method | Path | Handler |
|--------|------|---------|
| `POST` | `/api/v1/preauth` | `HandlePreAuthCreate` |
| `DELETE` | `/api/v1/preauth/{correlationId}` | `HandlePreAuthCancel` |
| `GET` | `/api/v1/pump-status` | `HandlePumpStatus` |
| `GET` | `/api/v1/heartbeat` | `HandleHeartbeat` |
| `GET` | `/api/v1/transactions` | `HandleFetchTransactions` |

**2. `VirtualLab/src/VirtualLab.Infrastructure/DomsRest/DomsRestContracts.cs`**

DTOs matching what the adapter sends/expects:

```csharp
// Request — matches DomsPreAuthRequest in desktop agent
public sealed record DomsSimPreAuthRequest(
    string PreAuthId,
    int PumpNumber,
    int NozzleNumber,
    string ProductCode,
    long AmountMinorUnits,
    long UnitPriceMinorPerLitre,
    string CurrencyCode,
    string? VehicleNumber,
    string CorrelationId);

// Response — matches DomsPreAuthResponse in desktop agent
public sealed record DomsSimPreAuthResponse(
    bool Accepted,
    string CorrelationId,
    string? AuthorizationCode,
    string? ErrorCode,
    string Message,
    DateTimeOffset? ExpiresAtUtc);

// Heartbeat response
public sealed record DomsSimHeartbeatResponse(string Status);

// Pump status item
public sealed record DomsSimPumpStatusItem(
    int PumpNumber,
    string State,
    string? ProductCode,
    decimal? CurrentVolume,
    decimal? CurrentAmount);

// Transaction fetch response
public sealed record DomsSimTransactionResponse(
    IReadOnlyList<DomsSimTransactionItem> Transactions,
    string? Cursor,
    bool HasMore);

public sealed record DomsSimTransactionItem(
    string TransactionId,
    int PumpNumber,
    int NozzleNumber,
    string ProductCode,
    long AmountMinorUnits,
    long VolumeCentilitres,
    long UnitPriceMinorPerLitre,
    string CurrencyCode,
    DateTimeOffset CompletedAt);
```

#### Translation Logic (in endpoint handler)

```
DomsSimPreAuthRequest → PreAuthSimulationService.HandleAsync():
  - amount = AmountMinorUnits / 100.0m  (minor → major)
  - pump = PumpNumber
  - nozzle = NozzleNumber
  - preauthId = PreAuthId
  - correlationId = CorrelationId

PreAuthSimulationResponse → DomsSimPreAuthResponse:
  - Accepted = (statusCode == 200)
  - CorrelationId = from request
  - AuthorizationCode = extracted from response body JSON
  - ExpiresAtUtc = extracted from response body JSON
  - ErrorCode = extracted on non-200
  - Message = extracted from response body
```

#### Files to Modify

**3. `VirtualLab/src/VirtualLab.Api/Program.cs`**

After existing `app.MapDomsJplManagementEndpoints()`:
```csharp
app.MapDomsRestSimulatorEndpoints();
```

**4. `VirtualLab/src/VirtualLab.Infrastructure/DependencyInjection.cs`**

No new services needed — endpoints call `IPreAuthSimulationService` directly (already scoped).

#### Acceptance Criteria

- [x] `POST /doms/{siteCode}/api/v1/preauth` accepts `DomsSimPreAuthRequest` JSON, returns `DomsSimPreAuthResponse`
- [x] `DELETE /doms/{siteCode}/api/v1/preauth/{correlationId}` cancels a pre-auth
- [x] `GET /doms/{siteCode}/api/v1/heartbeat` returns `{"Status":"UP"}`
- [x] `GET /doms/{siteCode}/api/v1/pump-status` returns pump state from forecourt service
- [x] `GET /doms/{siteCode}/api/v1/transactions?limit=N&since=T&cursor=C` returns transactions from forecourt service
- [x] `AmountMinorUnits` correctly converted to/from major units used by `PreAuthSimulationService`
- [x] Optional `X-API-Key` header is accepted but not enforced (lab environment)
- [x] Pre-auth session appears in VirtualLab UI and scenario assertions
- [x] Error injection via existing profile failure simulation works through this endpoint

---

### VLE-02: Petronite OAuth2 Protocol Simulator

**Priority:** P0
**Estimated Complexity:** Medium
**Depends on:** None

#### Problem

The Petronite adapter uses a **two-step OAuth2-authenticated flow**:

**Step 1 — Create Order:**
```
POST {baseUrl}/direct-authorize-requests/create
Authorization: Bearer {access_token}
Content-Type: application/json

{
  "NozzleId": "NOZ-1-2",
  "MaxVolumeLitres": 9999,
  "MaxAmountMajor": 500.00,
  "Currency": "ZAR",
  "ExternalReference": "PA-123"
}
```
Response:
```json
{ "OrderId": "ORD-456", "Status": "PENDING" }
```

**Step 2 — Authorize:**
```
POST {baseUrl}/direct-authorize-requests/authorize
Authorization: Bearer {access_token}
Content-Type: application/json

{ "OrderId": "ORD-456" }
```
Response:
```json
{
  "OrderId": "ORD-456",
  "Status": "AUTHORIZED",
  "AuthorizationCode": "AUTH-789",
  "Message": "Pump authorized"
}
```

Plus additional endpoints:
- `GET /nozzles/assigned` — nozzle assignments (used for heartbeat)
- `GET /direct-authorize-requests/pending` — pending orders (used for startup reconciliation)
- `POST /direct-authorize-requests/{orderId}/cancel` — cancel an order
- `POST /oauth/token` — OAuth2 client credentials token endpoint

#### Files to Create

**1. `VirtualLab/src/VirtualLab.Infrastructure/Petronite/PetroniteSimulatorEndpoints.cs`**

Route group: `/petronite/{siteCode}`

Endpoints:

| Method | Path | Handler |
|--------|------|---------|
| `POST` | `/oauth/token` | `HandleTokenRequest` |
| `POST` | `/direct-authorize-requests/create` | `HandleCreateOrder` |
| `POST` | `/direct-authorize-requests/authorize` | `HandleAuthorizeOrder` |
| `POST` | `/direct-authorize-requests/{orderId}/cancel` | `HandleCancelOrder` |
| `GET` | `/direct-authorize-requests/pending` | `HandleListPending` |
| `GET` | `/nozzles/assigned` | `HandleNozzleAssignments` |

**2. `VirtualLab/src/VirtualLab.Infrastructure/Petronite/PetroniteSimulatorContracts.cs`**

```csharp
// OAuth token request (form-encoded)
public sealed record PetroniteSimTokenRequest(
    string GrantType,       // "client_credentials"
    string ClientId,
    string ClientSecret);

// OAuth token response
public sealed record PetroniteSimTokenResponse(
    string AccessToken,
    string TokenType,       // "Bearer"
    int ExpiresIn);         // seconds

// Create order request — matches PetroniteCreateOrderRequest
public sealed record PetroniteSimCreateOrderRequest(
    string NozzleId,
    decimal MaxVolumeLitres,
    decimal MaxAmountMajor,
    string Currency,
    string ExternalReference);

// Create order response — matches PetroniteCreateOrderResponse
public sealed record PetroniteSimCreateOrderResponse(
    string OrderId,
    string Status);

// Authorize request — matches PetroniteAuthorizeRequest
public sealed record PetroniteSimAuthorizeRequest(
    string OrderId);

// Authorize response — matches PetroniteAuthorizeResponse
public sealed record PetroniteSimAuthorizeResponse(
    string OrderId,
    string Status,
    string? AuthorizationCode,
    string? Message);

// Cancel response
public sealed record PetroniteSimCancelResponse(
    string OrderId,
    string Status,
    string? Message);

// Pending order
public sealed record PetroniteSimPendingOrder(
    string OrderId,
    string NozzleId,
    string Status,
    string ExternalReference,
    DateTimeOffset CreatedAt);

// Nozzle assignment
public sealed record PetroniteSimNozzleAssignment(
    string NozzleId,
    int PumpNumber,
    int NozzleNumber,
    string ProductCode,
    string Status);

// Error response — matches PetroniteErrorResponse
public sealed record PetroniteSimErrorResponse(string Message);
```

**3. `VirtualLab/src/VirtualLab.Infrastructure/Petronite/PetroniteSimulatorState.cs`**

Singleton state to track:
- Active orders: `ConcurrentDictionary<string, PetroniteOrder>` (OrderId → order)
- Token validity (simple counter-based tokens, no real OAuth validation)
- Nozzle assignments derived from site's configured pumps/nozzles
- Order ID generator: `ORD-{Guid.NewGuid().ToString()[..8].ToUpper()}`

```csharp
public sealed class PetroniteSimulatorState
{
    public ConcurrentDictionary<string, PetroniteOrder> Orders { get; } = new();
    public int TokenCounter { get; set; }

    public sealed class PetroniteOrder
    {
        public string OrderId { get; init; }
        public string SiteCode { get; init; }
        public string NozzleId { get; init; }
        public int PumpNumber { get; init; }
        public int NozzleNumber { get; init; }
        public decimal MaxAmountMajor { get; init; }
        public string Currency { get; init; }
        public string ExternalReference { get; init; }
        public string Status { get; set; }  // PENDING, AUTHORIZED, CANCELLED
        public string? AuthorizationCode { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
```

#### Translation Logic

**Create Order → PreAuthSimulationService:**
```
NozzleId "NOZ-{pump}-{nozzle}" → parse pump & nozzle numbers
MaxAmountMajor → amount (already major units, no conversion needed)
ExternalReference → preauthId
```

**Authorize Order → PreAuthSimulationService:**
```
Look up order → get preauthId from ExternalReference
Call HandleAsync with operation "preauth-authorize"
```

**OAuth Token:**
- Accept any `client_credentials` grant
- Return a simple bearer token (e.g., `"sim-token-{counter}"`)
- Validate `Authorization: Bearer` header on subsequent requests (presence only)

#### Files to Modify

**4. `VirtualLab/src/VirtualLab.Api/Program.cs`**

```csharp
app.MapPetroniteSimulatorEndpoints();
```

**5. `VirtualLab/src/VirtualLab.Infrastructure/DependencyInjection.cs`**

```csharp
services.AddSingleton<PetroniteSimulatorState>();
```

#### Acceptance Criteria

- [x] `POST /petronite/{siteCode}/oauth/token` returns a bearer token for `client_credentials` grant
- [x] `POST /petronite/{siteCode}/direct-authorize-requests/create` creates an order and underlying pre-auth session (status: PENDING)
- [x] `POST /petronite/{siteCode}/direct-authorize-requests/authorize` transitions order to AUTHORIZED
- [x] `POST /petronite/{siteCode}/direct-authorize-requests/{orderId}/cancel` cancels order
- [x] `GET /petronite/{siteCode}/direct-authorize-requests/pending` returns active pending orders
- [x] `GET /petronite/{siteCode}/nozzles/assigned` returns nozzle assignments from site config
- [x] Requests without `Authorization: Bearer` header return 401
- [x] Two-step create-then-authorize flow maps correctly to PreAuthSimulationService's `CreateThenAuthorize` mode
- [x] Single-step create (when profile is `CreateOnly`) auto-authorizes the order
- [x] Pre-auth sessions visible in VirtualLab UI
- [x] Error injection works (returns PetroniteSimErrorResponse with Message)

---

### VLE-03: Advatec EFD Protocol Simulator

**Priority:** P1
**Estimated Complexity:** Medium
**Depends on:** None

#### Problem

The Advatec adapter sends customer data JSON to a local device (`localhost:5560`), then receives transaction receipts via a webhook callback. Two separate services needed:

**Pre-Auth (Customer Data Submission):**
```
POST http://localhost:5560/
Content-Type: application/json

{
  "DataType": "Customer",
  "Data": {
    "Pump": 1,
    "Dose": 20.00,
    "CustIdType": 6,
    "CustomerId": "TAX-123",
    "CustomerName": "John Doe",
    "Payments": []
  }
}
```
Response: HTTP 200 (success) or non-200 (failure)

**Receipt Webhook (VirtualLab → Edge Agent callback):**
```
POST http://{agent}:8091/api/webhook/advatec/
X-Webhook-Token: {secret}
Content-Type: application/json

{
  "DataType": "Receipt",
  "Data": {
    "TransactionId": "TXN-001",
    "ReceiptCode": "FR-001",
    "Date": "2026-03-15",
    "Time": "10:30:00",
    "AmountInclusive": 500.00,
    "CustomerId": "TAX-123",
    "Items": [
      {
        "Product": "PMS",
        "Quantity": 20.00,
        "Price": 25.00,
        "Amount": 500.00,
        "DiscountAmount": null
      }
    ]
  }
}
```

#### Files to Create

**1. `VirtualLab/src/VirtualLab.Infrastructure/Advatec/AdvatecSimulatorEndpoints.cs`**

Route group: `/advatec/{siteCode}`

Endpoints:

| Method | Path | Handler |
|--------|------|---------|
| `POST` | `/` | `HandleCustomerData` (pre-auth submission) |
| `POST` | `/push-receipt` | `HandlePushReceipt` (lab UI trigger to send webhook) |
| `GET` | `/state` | `HandleGetState` (management: view active pre-auths) |
| `POST` | `/reset` | `HandleReset` (management: clear state) |

**2. `VirtualLab/src/VirtualLab.Infrastructure/Advatec/AdvatecSimulatorContracts.cs`**

```csharp
// Inbound from adapter — matches AdvatecCustomerRequest
public sealed record AdvatecSimCustomerRequest(
    string DataType,        // "Customer"
    AdvatecSimCustomerData Data);

public sealed record AdvatecSimCustomerData(
    int Pump,
    decimal Dose,
    int CustIdType,
    string CustomerId,
    string CustomerName,
    List<object> Payments);     // Always empty during pre-auth

// Outbound receipt webhook — matches AdvatecWebhookEnvelope
public sealed record AdvatecSimReceiptWebhook(
    string DataType,        // "Receipt"
    AdvatecSimReceiptData Data);

public sealed record AdvatecSimReceiptData(
    string TransactionId,
    decimal AmountInclusive,
    string CustomerId,
    string? Date,
    string? Time,
    string ReceiptCode,
    List<AdvatecSimReceiptItem> Items);

public sealed record AdvatecSimReceiptItem(
    string Product,
    decimal Quantity,
    decimal Price,
    decimal Amount,
    decimal? DiscountAmount);

// Lab-triggered receipt push request
public sealed record AdvatecSimPushReceiptRequest(
    int PumpNumber,
    string? CallbackUrl,            // Override agent webhook URL
    string? WebhookToken,           // Override webhook token
    string? Product,
    decimal? Volume,
    decimal? UnitPrice,
    decimal? Amount,
    string? CustomerId,             // Echo back for correlation
    string? ReceiptCode);
```

**3. `VirtualLab/src/VirtualLab.Infrastructure/Advatec/AdvatecSimulatorState.cs`**

```csharp
public sealed class AdvatecSimulatorState
{
    public ConcurrentDictionary<int, AdvatecActivePreAuth> ActivePreAuths { get; } = new(); // pump → preauth

    public sealed class AdvatecActivePreAuth
    {
        public int PumpNumber { get; init; }
        public decimal DoseLitres { get; init; }
        public string CustomerId { get; init; }
        public string CustomerName { get; init; }
        public int CustIdType { get; init; }
        public string SiteCode { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
```

#### Translation Logic

**Customer Data → PreAuthSimulationService:**
```
Pump → pump number
Dose → amount (convert litres to currency: Dose * site's default unit price, or use as-is with nozzle=1)
CustomerId → customerTaxId
CustomerName → customerName
Generate preauthId = "ADV-{pump}-{unixMs}"
```

**Push Receipt → Edge Agent Webhook:**
When a transaction completes in the forecourt sim (or triggered via lab UI `/push-receipt`):
1. Build `AdvatecSimReceiptWebhook` with transaction details
2. HTTP POST to the agent's webhook callback URL with `X-Webhook-Token` header
3. Echo back `CustomerId` for pre-auth correlation

#### Files to Modify

**4. `VirtualLab/src/VirtualLab.Api/Program.cs`**

```csharp
app.MapAdvatecSimulatorEndpoints();
```

**5. `VirtualLab/src/VirtualLab.Infrastructure/DependencyInjection.cs`**

```csharp
services.AddSingleton<AdvatecSimulatorState>();
```

#### Acceptance Criteria

- [x] `POST /advatec/{siteCode}/` accepts `{"DataType":"Customer","Data":{...}}` and creates pre-auth session
- [x] Returns HTTP 200 on success, 400/500 on failure (matching real Advatec device behaviour)
- [x] Active pre-auths tracked per pump (new submission replaces old on same pump)
- [x] `POST /advatec/{siteCode}/push-receipt` sends receipt webhook to configured callback URL
- [x] Receipt webhook includes `X-Webhook-Token` header
- [x] Receipt webhook echoes back `CustomerId` for adapter correlation
- [x] `GET /advatec/{siteCode}/state` shows active pre-auths and recent receipts
- [x] Pre-auth sessions visible in VirtualLab UI
- [x] Error injection works (returns non-200 status)

---

### VLE-04: Radix XML Protocol Simulator

**Priority:** P1
**Estimated Complexity:** Large
**Depends on:** None

#### Problem

The Radix adapter uses **XML over HTTP with SHA-1 signing** on dual ports:
- **Port P (auth):** Pre-auth XML requests
- **Port P+1 (transactions):** Transaction fetch/ack, product read, mode change

**Pre-Auth Request (to auth port P):**
```
POST http://{host}:{authPort}/
Content-Type: Application/xml
USN-Code: 12345
Operation: Authorize

<?xml version="1.0" encoding="utf-8"?>
<FDCMS>
  <AUTH_DATA>
    <PUMP>1</PUMP>
    <FP>1</FP>
    <AUTH>TRUE</AUTH>
    <PROD>0</PROD>
    <PRESET_VOLUME>0.00</PRESET_VOLUME>
    <PRESET_AMOUNT>500.00</PRESET_AMOUNT>
    <CUSTNAME>John Doe</CUSTNAME>
    <CUSTIDTYPE>1</CUSTIDTYPE>
    <CUSTID>TAX-123</CUSTID>
    <MOBILENUM></MOBILENUM>
    <TOKEN>42</TOKEN>
  </AUTH_DATA>
  <FDCSIGNATURE>{sha1_hash}</FDCSIGNATURE>
</FDCMS>
```

**Pre-Auth Response:**
```xml
<?xml version="1.0" encoding="utf-8"?>
<FDCMS>
  <FDCACK>
    <DATE>2026-03-15</DATE>
    <TIME>10:30:00</TIME>
    <ACKCODE>0</ACKCODE>
    <ACKMSG>SUCCESS</ACKMSG>
  </FDCACK>
  <FDCSIGNATURE>{sha1_hash}</FDCSIGNATURE>
</FDCMS>
```

**Transaction Fetch (port P+1):**
```
POST http://{host}:{txnPort}/
Content-Type: Application/xml
USN-Code: 12345
Operation: 1

<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
  <REQ CMD_CODE="10" CMD_NAME="TRN_REQ" TOKEN="{token}"/>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

**Transaction Response:**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <ANS RESP_CODE="201" RESP_MSG="Transaction available" TOKEN="{token}"/>
  <TRN amo="50000.0" efdId="EFD-001" fdcDate="2026-03-15" fdcTime="10:30:00"
       fdcName="Station1" fdcNum="FDC001" fdcProd="1" fdcProdName="PMS"
       fdcSaveNum="1234" fdcTank="" fp="1" noz="1" price="2500.0"
       pumpAddr="1" rdgDate="2026-03-15" rdgTime="10:30:00" rdgId="1"
       rdgIndex="1" rdgProd="1" rdgSaveNum="1234" regId="REG001"
       roundType="0" vol="20.0"/>
  <RFID_CARD/>
  <DISCOUNT/>
  <CUST_DATA/>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</FDC_RESP>
```

**ACK Codes:** 0=Success, 251=Signature error, 255=Bad XML, 256=Bad header, 258=Pump not ready, 260=DSB offline

#### Files to Create

**1. `VirtualLab/src/VirtualLab.Infrastructure/Radix/RadixSimulatorService.cs`**

A `BackgroundService` that starts two Kestrel listeners (or two route groups on different ports):

**Option A (simpler — route groups on main Kestrel):**
Route groups: `/radix/{siteCode}/auth` and `/radix/{siteCode}/txn`

**Option B (more realistic — separate ports):**
Use `WebApplication.CreateBuilder()` for separate Kestrel endpoints on configurable ports. Follow the `DomsJplSimulatorService` pattern but with HTTP instead of raw TCP.

**Recommendation:** Start with Option A (route groups) for simplicity. The edge agent can be configured to point at `http://virtuallab:5000/radix/SITE001/auth` and `http://virtuallab:5000/radix/SITE001/txn` instead of separate ports. Document that the adapter's `AuthPort` config should be replaced with `BaseUrl` pointing at the VL routes.

Endpoints (Option A):

| Method | Path | Handler |
|--------|------|---------|
| `POST` | `/radix/{siteCode}/auth` | `HandleAuthRequest` (pre-auth XML) |
| `POST` | `/radix/{siteCode}/txn` | `HandleTransactionRequest` (fetch/ack/mode/product XML) |
| `GET` | `/radix/{siteCode}/state` | `HandleGetState` (management JSON) |
| `POST` | `/radix/{siteCode}/inject-transaction` | `HandleInjectTransaction` (management JSON) |
| `POST` | `/radix/{siteCode}/reset` | `HandleReset` (management JSON) |

**2. `VirtualLab/src/VirtualLab.Infrastructure/Radix/RadixSimulatorContracts.cs`**

Internal models (not serialized as-is — XML is built manually):

```csharp
public sealed class RadixSimulatorOptions
{
    public const string SectionName = "Simulators:Radix";
    public bool Enabled { get; set; } = true;
    public string SharedSecret { get; set; } = "virtuallab-test-secret";
    public int DefaultUsnCode { get; set; } = 12345;
}

// Parsed from AUTH_DATA XML
public sealed record RadixSimAuthRequest(
    int Pump,
    int Fp,
    bool Authorize,         // TRUE = auth, FALSE = cancel
    int Product,
    string PresetVolume,
    string PresetAmount,
    string? CustomerName,
    int? CustomerIdType,
    string? CustomerId,
    string? MobileNumber,
    int? DiscountValue,
    string? DiscountType,
    string Token);

// Parsed from HOST_REQ XML
public sealed record RadixSimHostRequest(
    int CmdCode,
    string CmdName,
    string Token,
    string? Mode,           // For CMD_CODE=20
    string Signature);
```

**3. `VirtualLab/src/VirtualLab.Infrastructure/Radix/RadixXmlHelper.cs`**

Utility class for:
- Parsing `<AUTH_DATA>` elements from inbound XML
- Parsing `<HOST_REQ>` elements from inbound XML
- Building `<FDCMS><FDCACK>` response XML
- Building `<FDC_RESP><ANS><TRN>` response XML
- SHA-1 signature generation: `SHA1(element_content + sharedSecret)`
- SHA-1 signature validation of inbound requests
- USN-Code header validation

**4. `VirtualLab/src/VirtualLab.Infrastructure/Radix/RadixSimulatorState.cs`**

```csharp
public sealed class RadixSimulatorState
{
    // Transaction FIFO buffer (agent drains with CMD_CODE=10, acks with 201)
    public ConcurrentQueue<RadixBufferedTransaction> TransactionBuffer { get; } = new();

    // Active pre-auths by token
    public ConcurrentDictionary<int, RadixActivePreAuth> ActivePreAuths { get; } = new();

    // Current mode (0=OFF, 1=ON_DEMAND, 2=UNSOLICITED)
    public int CurrentMode { get; set; } = 1;

    // Products
    public List<RadixProduct> Products { get; set; } = [];

    // Error injection
    public RadixErrorInjection ErrorInjection { get; set; } = new();

    public sealed class RadixBufferedTransaction { /* TRN fields */ }
    public sealed class RadixActivePreAuth { /* pump, token, amount, customer */ }
    public sealed class RadixProduct { public int Id; public string Name; public string Price; }
    public sealed class RadixErrorInjection
    {
        public int? ForceAckCode { get; set; }
        public int? ForceRespCode { get; set; }
        public int ShotsRemaining { get; set; }
    }
}
```

#### Translation Logic

**Auth Request → PreAuthSimulationService:**
```
PUMP → pump
FP → nozzle (or use FP as pump if pump address map configured)
PRESET_AMOUNT (major units string) → amount
AUTH=TRUE → operation "preauth-create", AUTH=FALSE → operation "preauth-cancel"
TOKEN → preauthId = "RDX-{token}"
CUSTNAME → customerName
CUSTID → customerTaxId
```

**Transaction Fetch → ForecourtSimulationService:**
```
CMD_CODE=10 → PullTransactionsAsync (drain one from FIFO buffer)
CMD_CODE=201 → Acknowledge (remove from buffer)
CMD_CODE=20 → Set mode (store in state)
CMD_CODE=55 → Return products list
```

**SHA-1 Validation:**
- Parse inbound XML, extract `<SIGNATURE>` or `<FDCSIGNATURE>`
- Compute expected: `SHA1(inner_element_xml + sharedSecret)`
- Compare (case-insensitive hex)
- On mismatch: return ACKCODE=251 or RESP_CODE=251
- **Note:** Signature validation should be optional (configurable) to simplify initial testing

#### Files to Modify

**5. `VirtualLab/src/VirtualLab.Api/Program.cs`**

```csharp
app.MapRadixSimulatorEndpoints();
```

**6. `VirtualLab/src/VirtualLab.Infrastructure/DependencyInjection.cs`**

```csharp
services.Configure<RadixSimulatorOptions>(configuration.GetSection(RadixSimulatorOptions.SectionName));
services.AddSingleton<RadixSimulatorState>();
```

#### Acceptance Criteria

- [x] `POST /radix/{siteCode}/auth` accepts `<FDCMS><AUTH_DATA>` XML, returns `<FDCMS><FDCACK>` XML
- [x] Content-Type `Application/xml` accepted and returned
- [x] `USN-Code` header validated (logged, not enforced in lab)
- [x] SHA-1 signature validated when `SharedSecret` configured (optional bypass for quick testing)
- [x] ACKCODE=0 on success, 258 on pump not ready, 251 on signature error
- [x] `POST /radix/{siteCode}/txn` with CMD_CODE=10 returns buffered transaction as `<FDC_RESP><TRN>` XML
- [x] RESP_CODE=205 when buffer empty
- [x] CMD_CODE=201 acknowledges and removes transaction from buffer
- [x] CMD_CODE=55 returns product list XML
- [x] CMD_CODE=20 sets mode (ON_DEMAND/UNSOLICITED/OFF)
- [x] Management endpoints for injecting transactions and errors (JSON)
- [x] Pre-auth sessions visible in VirtualLab UI
- [x] Error injection via `ForceAckCode` and `ForceRespCode`

---

### VLE-05: DOMS JPL TCP Simulator Enhancements (Pre-Auth)

**Priority:** P0
**Estimated Complexity:** Small
**Depends on:** None

#### Problem

The existing `DomsJplSimulatorService` already handles TCP JPL messages including `authorize_Fp_req`. However, it needs verification and potential enhancement to ensure:
1. The `authorize_Fp_req` handler creates a proper pre-auth session in `PreAuthSimulationService`
2. The response format matches what `DomsJplAdapter` expects
3. The `deauthorize_Fp_req` message cancels the session

#### Files to Review/Modify

**1. `VirtualLab/src/VirtualLab.Infrastructure/DomsJpl/DomsJplSimulatorService.cs`**

Review `HandleAuthorizeFp()` method:
- Currently: verify it calls `PreAuthSimulationService` (or stores in local state only)
- If local state only: add bridge to `PreAuthSimulationService.HandleAsync()` so pre-auth sessions appear in the shared store
- Verify response JSON matches what `DomsJplAdapter.Protocol.DomsPreAuthHandler` expects:
  ```json
  {
    "type": "authorize_Fp_resp",
    "data": {
      "ResultCode": 0,
      "AuthCode": "AUTH-123",
      "ExpiresAt": "2026-03-15T10:35:00Z",
      "CorrelationId": "corr-001"
    }
  }
  ```

**2. Add `deauthorize_Fp_req` handling** if not present

- Should cancel the active pre-auth for the given FpId
- Bridge to `PreAuthSimulationService.HandleAsync()` with operation `"preauth-cancel"`

#### Acceptance Criteria

- [x] `authorize_Fp_req` JPL message creates a pre-auth session visible in the shared PreAuthSimulationService store
- [x] Response matches `DomsJplAdapter` expected format
- [x] `deauthorize_Fp_req` cancels the session
- [x] Error injection from DOMS JPL management endpoints affects pre-auth responses
- [x] Pre-auth sessions from JPL path visible in VirtualLab UI alongside REST-created sessions

---

### VLE-06: Unified Vendor Configuration in Site Profile

**Priority:** P1
**Estimated Complexity:** Small
**Depends on:** VLE-01 through VLE-04

#### Problem

Each site in the VirtualLab needs to know which vendor protocol to simulate, so the lab UI can present the right options and scenarios can target the right adapter.

#### Files to Modify

**1. `VirtualLab/src/VirtualLab.Domain/Models/Site.cs`**

Add or verify field:
```csharp
public string FccVendor { get; set; } = "Generic";
// Values: "Generic", "DOMS_REST", "DOMS_JPL", "PETRONITE", "ADVATEC", "RADIX"
```

**2. `VirtualLab/src/VirtualLab.Application/Scenarios/ScenarioContracts.cs`**

Add to `ScenarioSetupDefinition`:
```csharp
public string? FccVendor { get; init; }
```

This allows scenarios to specify which vendor protocol the test expects, enabling the scenario engine to validate that the right simulator endpoints were hit.

**3. Lab UI**

In the site settings component, add a dropdown for FCC Vendor selection. This controls:
- Which simulator base URL to display to the user for edge agent configuration
- Which protocol-specific options to show (e.g., Petronite OAuth settings, Radix shared secret)

#### Acceptance Criteria

- [x] Site model includes `FccVendor` field
- [x] Scenario setup can specify target vendor
- [x] Lab UI shows vendor-appropriate configuration guidance
- [x] All vendor simulators work regardless of `FccVendor` setting (field is advisory, not routing)

---

### VLE-07: Edge Agent Configuration Guide for VirtualLab Testing

**Priority:** P2
**Estimated Complexity:** Small
**Depends on:** VLE-01 through VLE-04

#### Problem

Developers need to know how to configure each edge agent adapter to point at the VirtualLab instead of a real FCC.

#### Configuration Mapping

| Adapter | Agent Config Field | Real Value | VirtualLab Value |
|---------|-------------------|------------|------------------|
| **DOMS REST** | `fcc.baseUrl` | `https://doms-fcc:8080` | `http://virtuallab:5000/doms/{siteCode}` |
| **DOMS REST** | `fcc.apiKey` | `{real-key}` | `any-value` (not enforced) |
| **DOMS JPL** | `fcc.baseUrl` (host) | `doms-fcc` | `virtuallab-host` |
| **DOMS JPL** | `fcc.jplPort` | `4711` | `{configured port in VL}` |
| **DOMS JPL** | `fcc.fcAccessCode` | `{real-code}` | `{configured in VL}` |
| **Petronite** | `fcc.baseUrl` | `https://petronite-api` | `http://virtuallab:5000/petronite/{siteCode}` |
| **Petronite** | `fcc.oAuthTokenEndpoint` | `https://petronite/oauth/token` | `http://virtuallab:5000/petronite/{siteCode}/oauth/token` |
| **Petronite** | `fcc.clientId` / `clientSecret` | `{real}` | `test-client` / `test-secret` |
| **Petronite** | `fcc.webhookListenerPort` | `8090` | `8090` (unchanged — VL pushes to it) |
| **Advatec** | `fcc.advatecDeviceAddress` | `127.0.0.1` | `virtuallab-host` |
| **Advatec** | `fcc.advatecDevicePort` | `5560` | `5000` (VL main port) |
| **Advatec** | Adapter must use path | `/` | `/advatec/{siteCode}/` |
| **Radix** | `fcc.baseUrl` | `http://radix-fcc` | `http://virtuallab:5000/radix/{siteCode}` |
| **Radix** | `fcc.authPort` | `7098` | N/A — use `/auth` and `/txn` suffixes |
| **Radix** | `fcc.sharedSecret` | `{real-secret}` | `virtuallab-test-secret` |
| **Radix** | `fcc.usnCode` | `{real-usn}` | `12345` |

**Note on Advatec:** The real adapter posts to `http://{host}:{port}/` (root path). The VirtualLab uses `/advatec/{siteCode}/`. This means the Advatec adapter needs a minor change to support a configurable base path, OR the VirtualLab needs to also listen on the root path with site code in a header/query param.

**Recommended approach for Advatec:** Add optional `fcc.advatecBasePath` config to the adapter (default `/`). For VirtualLab, set it to `/advatec/{siteCode}/`.

**Note on Radix:** The real adapter uses separate ports for auth (P) and transactions (P+1). The VirtualLab uses path-based routing (`/radix/{siteCode}/auth` and `/radix/{siteCode}/txn`). The Radix adapter needs a minor change to support URL-based routing instead of port-based, OR the VirtualLab can start separate listeners.

**Recommended approach for Radix:** Add optional `fcc.radixAuthUrl` and `fcc.radixTxnUrl` overrides. When set, these override the port-based URL construction.

---

## Implementation Order

```
Phase 1 (Unblock basic testing):
  VLE-01  DOMS REST Shim          ─── smallest delta, covers DOMS REST adapter
  VLE-05  DOMS JPL Pre-Auth Fix   ─── verify existing TCP sim bridges to PreAuthService

Phase 2 (Full adapter coverage):
  VLE-02  Petronite Simulator     ─── two-step OAuth flow
  VLE-03  Advatec Simulator       ─── customer data + receipt webhook
  VLE-04  Radix XML Simulator     ─── largest effort (XML + SHA-1 + dual endpoint)

Phase 3 (Polish):
  VLE-06  Vendor Config in Profile
  VLE-07  Configuration Guide
```

## Estimated New Files

| Task | New Files | Modified Files |
|------|-----------|----------------|
| VLE-01 | 2 (endpoints + contracts) | 1 (Program.cs) |
| VLE-02 | 3 (endpoints + contracts + state) | 2 (Program.cs + DI) |
| VLE-03 | 3 (endpoints + contracts + state) | 2 (Program.cs + DI) |
| VLE-04 | 4 (endpoints + contracts + state + XML helper) | 2 (Program.cs + DI) |
| VLE-05 | 0 | 1 (DomsJplSimulatorService.cs) |
| VLE-06 | 0 | 3 (Site.cs + ScenarioContracts.cs + UI) |
| VLE-07 | 0 | 0 (documentation only) |
| **Total** | **12 new files** | **~8 modified files** |

## Key Design Decisions

1. **All vendor shims delegate to `PreAuthSimulationService`** — no parallel state machines. This ensures one session store, one expiry system, one set of scenario assertions.

2. **Route-group based routing** (`/doms/{siteCode}/...`, `/petronite/{siteCode}/...`) rather than separate ports — simpler deployment, single Kestrel instance, easier CORS/proxy.

3. **Auth is relaxed** — API keys, OAuth tokens, and shared secrets are accepted but not strictly validated. The goal is protocol fidelity, not security testing.

4. **Contracts mirror adapter DTOs exactly** — field names, casing, and types match what the adapter serializes/deserializes, ensuring JSON round-trip compatibility.

5. **Management endpoints follow DOMS JPL pattern** — each simulator has `/state`, `/reset`, and injection endpoints for lab UI and scenario automation.
