# FCC Vendor Configuration Reference

Per-vendor configuration fields, examples, and validation rules for all supported FCC adapters.

---

## Common Fields (All Vendors)

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `fccVendor` | Enum | Yes | `Doms`, `Radix`, or `Petronite` |
| `hostAddress` | String | Yes | FCC device IP/hostname on LAN |
| `port` | Int | Yes | Primary protocol port |
| `connectionProtocol` | String | DOMS only | `REST` (default) or `TCP` |
| `ingestionMode` | String | Yes | `Relay`, `BufferAlways`, or `CloudDirect` |
| `heartbeatIntervalSeconds` | Int | No | Liveness probe interval (default: 30) |

---

## DOMS (Tokheim / Dover)

### REST Mode (Legacy / VirtualLab)

Stateless HTTP adapter used for VirtualLab simulation and backward compatibility.

```json
{
  "fcc": {
    "enabled": true,
    "vendor": "Doms",
    "connectionProtocol": "REST",
    "hostAddress": "192.168.1.100",
    "port": 8080,
    "ingestionMode": "Relay",
    "heartbeatIntervalSeconds": 30
  }
}
```

### TCP/JPL Mode (Production)

Persistent TCP connection with JPL binary framing. Requires additional fields.

```json
{
  "fcc": {
    "enabled": true,
    "vendor": "Doms",
    "connectionProtocol": "TCP",
    "hostAddress": "192.168.1.100",
    "port": 4001,
    "ingestionMode": "Relay",
    "heartbeatIntervalSeconds": 30
  },
  "identity": {
    "siteCode": "SITE-001",
    "legalEntityId": "LE-001"
  },
  "site": {
    "currency": "TRY",
    "timezone": "Europe/Istanbul"
  }
}
```

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `connectionProtocol` | String | Yes | `REST` | Must be `TCP` for production DOMS |
| `port` | Int | Yes | 4001 | JPL TCP port (typically 4001 or 8888) |
| `fcAccessCode` | String | Yes (TCP) | — | FcLogon authentication credential. **Sensitive** |
| `domsCountryCode` | String | No | — | ISO country code for locale |
| `posVersionId` | String | No | — | POS version sent during FcLogon handshake |
| `heartbeatIntervalSeconds` | Int | No | 30 | STX/ETX heartbeat interval (15-60s) |
| `reconnectBackoffMaxSeconds` | Int | No | 120 | Max exponential backoff on TCP reconnect |
| `configuredPumps` | String | No | — | Comma-separated pump numbers: `"1,2,3,4"` |

**Validation Rules:**
- TCP mode requires `siteCode`, `legalEntityId`, `currency`, and `timezone` in site config
- `fcAccessCode` must not be empty for TCP mode
- `port` must be > 0 and < 65536
- `heartbeatIntervalSeconds` range: 15–60

**Volume/Amount Conversion:**
- Volume: centilitres (integer) -> microlitres = `value * 10_000`
- Amount: x10 factor (integer) -> minor units = `value * 10`

---

## Radix FDC

Dual-port HTTP/XML adapter with SHA-1 message signing.

```json
{
  "fcc": {
    "enabled": true,
    "vendor": "Radix",
    "hostAddress": "192.168.1.200",
    "port": 5000,
    "ingestionMode": "Relay",
    "heartbeatIntervalSeconds": 30
  },
  "identity": {
    "siteCode": "SITE-002",
    "legalEntityId": "LE-002"
  },
  "site": {
    "currency": "TZS",
    "timezone": "Africa/Dar_es_Salaam"
  }
}
```

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `port` | Int | Yes | 5000 | Authorization port (P). Transaction port = P+1 |
| `sharedSecret` | String | Yes | — | SHA-1 signing password. **Sensitive** |
| `usnCode` | Int | Yes | — | Unique Station Number (1–999999) |
| `authPort` | Int | No | = port | Explicit auth port (overrides convention) |
| `fccPumpAddressMap` | JSON | No | — | Canonical pump -> (PUMP_ADDR, FP) mapping |

**Pump Address Map Example:**
```json
{
  "1": { "pumpAddr": 1, "fp": 1 },
  "2": { "pumpAddr": 2, "fp": 1 },
  "3": { "pumpAddr": 3, "fp": 1 }
}
```

**Validation Rules:**
- `sharedSecret` must not be empty
- `usnCode` must be in range 1–999999
- `port` must be > 0 and < 65535 (port+1 is used for transactions)

**Volume/Amount Conversion:**
- Volume: litres as decimal string (e.g., "15.54") -> microlitres = `BigDecimal(VOL) * 1_000_000`
- Amount: currency decimal string (e.g., "30000.0") -> minor units = `BigDecimal(AMO) * 10^currencyDecimals`

**Operating Modes:**
- `ON_DEMAND` (mode 1): Agent polls for transactions via CMD_CODE=10
- `UNSOLICITED` (mode 2): FDC pushes transactions via CMD_CODE=30

---

## Petronite

REST/JSON adapter with OAuth2 Client Credentials and webhook-based transaction delivery.

```json
{
  "fcc": {
    "enabled": true,
    "vendor": "Petronite",
    "hostAddress": "petronite-bot.local",
    "port": 6000,
    "ingestionMode": "CloudDirect",
    "webhookListenerPort": 8090
  },
  "identity": {
    "siteCode": "SITE-003",
    "legalEntityId": "LE-003"
  },
  "site": {
    "currency": "USD",
    "timezone": "America/New_York"
  }
}
```

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `port` | Int | Yes | 6000 | Petronite bot REST API port |
| `clientId` | String | Yes | — | OAuth2 client ID. **Sensitive** |
| `clientSecret` | String | Yes | — | OAuth2 client secret. **Sensitive** |
| `webhookSecret` | String | Yes | — | X-Webhook-Secret header value. **Sensitive** |
| `oauthTokenEndpoint` | String | No | `{baseUrl}/oauth/token` | OAuth2 token endpoint URL |
| `webhookListenerPort` | Int | No | 8090 | Local HTTP port for receiving webhooks |

**Desktop Agent `appsettings.json` Override:**
```json
{
  "Agent": {
    "PetroniteWebhookListenerPort": 8090
  }
}
```

**Validation Rules:**
- `clientId` and `clientSecret` must not be empty
- `webhookSecret` should be >= 16 characters
- `webhookListenerPort` must be > 0 and < 65536; avoid conflicts with LocalApi port (8585)
- `ingestionMode` must not be `Relay` (Petronite is push-only; no pull API)

**Ingestion Mode Constraints:**
- `Relay` -> **Rejected** (Petronite has no pull API)
- `BufferAlways` -> Supported (webhooks buffered locally, uploaded to cloud)
- `CloudDirect` -> Recommended (webhooks forwarded directly to cloud)

**Volume/Amount Conversion:**
- Volume: litres as decimal (e.g., 25.50) -> microlitres = `(long)(value * 1_000_000m)`
- Amount: major currency units (e.g., 71400.00) -> minor units = `(long)(value * 10^currencyDecimals)`

**Pre-Auth Flow (Two-Step):**
1. `POST /direct-authorize-requests/create` -> creates order, returns orderId
2. `POST /direct-authorize-requests/authorize` -> authorizes pump for dispensing
3. Webhook `transaction.completed` arrives after fuelling completes
4. `POST /{id}/cancel` -> cancels an authorized order before dispensing starts

---

## Cloud Backend Configuration

Cloud-side FCC configuration is stored in the `fcc_configs` table and managed via the Portal.

| Column | DOMS | Radix | Petronite |
|--------|------|-------|-----------|
| `fcc_vendor` | DOMS | RADIX | PETRONITE |
| `host_address` | LAN IP | LAN IP | Bot hostname |
| `port` | JPL port | Auth port (P) | REST API port |
| `connection_protocol` | TCP/REST | — | — |
| `shared_secret` | — | SHA-1 key | — |
| `usn_code` | — | Station number | — |
| `fc_access_code` | FcLogon code | — | — |
| `client_id` | — | — | OAuth2 client ID |
| `client_secret` | — | — | OAuth2 client secret |
| `webhook_secret` | — | — | Webhook header secret |
| `oauth_token_endpoint` | — | — | Token URL |
| `configured_pumps` | JSON array | — | — |

---

## Currency Decimal Places

Special currency handling (used by all adapters for minor unit conversion):

| Decimals | Currencies |
|----------|------------|
| 0 | BIF, CLP, DJF, GNF, ISK, JPY, KMF, KRW, PYG, RWF, UGX, VND, VUV, XAF, XOF, XPF |
| 2 | **Default** — USD, EUR, TZS, TRY, GBP, etc. |
| 3 | BHD, IQD, JOD, KWD, LYD, OMR, TND |
