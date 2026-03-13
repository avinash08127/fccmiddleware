# Production Deployment Checklist

---

## 1. Infrastructure Prerequisites

### 1.1 PostgreSQL 16
- [ ] PostgreSQL 16 instance provisioned (RDS or self-managed)
- [ ] `pgcrypto` extension enabled: `CREATE EXTENSION IF NOT EXISTS "pgcrypto";`
- [ ] Database created: `fccmiddleware`
- [ ] Application user created with appropriate privileges (NOT superuser)
- [ ] Connection string configured in `ConnectionStrings:FccMiddleware`
- [ ] SSL/TLS enforced on database connections (`sslmode=require`)

### 1.2 Redis 7
- [ ] Redis 7 instance provisioned (ElastiCache or self-managed)
- [ ] Connection string configured in `ConnectionStrings:Redis`
- [ ] TLS enabled for Redis connections in production

### 1.3 Docker / Container Runtime
- [ ] Container runtime available for API + Worker deployments
- [ ] `Dockerfile.api` and `Dockerfile.worker` images built and pushed to registry

---

## 2. Database Schema & Migrations

### 2.1 Apply Schema
- [ ] Run `db/migrations/001-initial-schema.sql` against the target database
- [ ] Verify all 16 tables created: `legal_entities`, `sites`, `pumps`, `products`, `nozzles`, `operators`, `transactions`, `pre_auth_records`, `fcc_configs`, `agent_registrations`, `agent_telemetry_snapshots`, `portal_settings`, `legal_entity_settings_overrides`, `dead_letter_items`, `audit_events`, `outbox_messages`
- [ ] Verify partitioned tables have monthly partitions for the deployment year
- [ ] Verify indexes created (dedup, odoo_poll, portal_search, reconciliation, stale, webhook hashes)

### 2.2 Partition Management (Production)
- [ ] Install `pg_partman` extension for automatic monthly partition creation
- [ ] Configure `pg_partman` for `transactions` and `audit_events` tables
- [ ] Set retention policy (e.g., 24 months for transactions, 36 months for audit_events)
- [ ] Schedule `pg_partman` maintenance via `pg_cron` or external cron

### 2.3 Seed Master Data
- [ ] Seed legal entities via Databricks sync or manual SQL (one per country)
- [ ] Seed products per legal entity via Databricks sync
- [ ] Seed sites, pumps, nozzles per site via Databricks sync or portal
- [ ] For dev/staging: run `db/migrations/002-seed-radix-dev.sql`

---

## 3. Application Configuration

### 3.1 Field Encryption Key (Required)
- [ ] Set `FieldEncryption:Key` in application configuration
  - **Format:** 64-character hex string (32 bytes for AES-256)
  - **Generate:** `openssl rand -hex 32`
  - **Env var:** `FieldEncryption__Key`
  - **Scope:** Must be consistent across all API + Worker instances sharing the same database
  - **Encrypted fields:** `shared_secret`, `fc_access_code`, `client_secret`, `webhook_secret`, `advatec_webhook_token`
  - Existing plaintext values remain readable; encrypted on next write (incremental migration)
  - **Key rotation:** Decrypt all with old key, re-encrypt with new key. Prepare bulk migration script before rotation.

### 3.2 Connection Strings
- [ ] `ConnectionStrings:FccMiddleware` — PostgreSQL connection string
- [ ] `ConnectionStrings:Redis` — Redis connection string

### 3.3 Authentication
- [ ] `PortalJwt:Authority` — Azure Entra ID / MSAL authority URL
- [ ] `PortalJwt:Audience` — Expected audience claim
- [ ] `PortalJwt:ClientId` — Azure AD app client ID
- [ ] Edge Agent JWT signing key configured (for device Bearer tokens)
- [ ] Odoo API key(s) provisioned and stored in `odoo_api_keys` table (if applicable)
- [ ] Databricks API key provisioned and stored (if applicable)

### 3.4 Edge Agent Defaults
- [ ] `EdgeAgentDefaults:Rollout:MinAgentVersion` set to minimum supported version
- [ ] `EdgeAgentDefaults:Rollout:LatestAgentVersion` set to current release
- [ ] `EdgeAgentDefaults:Rollout:UpdateUrl` set to APK/MSIX download URL (if applicable)
- [ ] `EdgeAgentDefaults:Rollout:ConfigTtlHours` set (default: 24)

### 3.5 Logging
- [ ] Serilog minimum level set to `Information` for production
- [ ] Structured logging sink configured (CloudWatch, Datadog, etc.)
- [ ] Sensitive field filter active (customer_name, customer_tax_id never logged)

---

## 4. FCC Vendor Configuration

### 4.1 Radix FCC Setup
For each Radix site, create an `fcc_configs` row with:
- [ ] `fcc_vendor` = `RADIX`
- [ ] `connection_protocol` = `REST`
- [ ] `host_address` = Radix FCC IP on station LAN (e.g., `192.168.1.100`)
- [ ] `port` = Transaction port (auth_port + 1)
- [ ] `auth_port` = Pre-auth/authorization port (e.g., `10000`)
- [ ] `shared_secret` = SHA-1 signing password (from Radix commissioning)
- [ ] `usn_code` = Unique Station Number (1–999999, from Radix)
- [ ] `fcc_pump_address_map` = JSON mapping FCC pump number to `{PumpAddr, Fp}` pairs
  - Example: `{"1": {"PumpAddr": 1, "Fp": 1}, "2": {"PumpAddr": 2, "Fp": 1}}`
- [ ] `transaction_mode` = `PULL` (Radix uses FIFO drain, CMD_CODE=10 request -> CMD_CODE=201 ACK)
- [ ] `ingestion_mode` = `RELAY` (edge agent relays to cloud) or `CLOUD_DIRECT`
- [ ] `pull_interval_seconds` = Polling interval (recommended: 15s)
- [ ] `credential_ref` = AWS Secrets Manager path for this site

### 4.2 DOMS TCP/JPL Setup
For each DOMS site:
- [ ] `fcc_vendor` = `DOMS`
- [ ] `connection_protocol` = `TCP`
- [ ] `jpl_port` = JPL binary-framed port
- [ ] `fc_access_code` = FcLogon credential
- [ ] `doms_country_code` = Locale code
- [ ] `pos_version_id` = Handshake version
- [ ] `configured_pumps` = Comma-separated pump list
- [ ] `dpp_ports` = DPP port list
- [ ] `reconnect_backoff_max_seconds` = Max reconnect backoff

### 4.3 Petronite OAuth2 Setup
For each Petronite site:
- [ ] `fcc_vendor` = `PETRONITE`
- [ ] `connection_protocol` = `REST`
- [ ] `client_id` = OAuth2 client ID
- [ ] `client_secret` = OAuth2 client secret
- [ ] `webhook_secret` = HMAC validation secret
- [ ] `webhook_secret_hash` = SHA-256 hash of webhook_secret (computed by app on save)
- [ ] `oauth_token_endpoint` = Token URL

### 4.4 Advatec EFD Setup
For each Advatec site:
- [ ] `fcc_vendor` = `ADVATEC`
- [ ] `connection_protocol` = `REST`
- [ ] `advatec_device_port` = Device port (default: 5560)
- [ ] `advatec_webhook_token` = Webhook authentication token
- [ ] `advatec_webhook_token_hash` = SHA-256 hash (computed by app on save)
- [ ] `advatec_efd_serial_number` = TRA-registered EFD serial
- [ ] `advatec_cust_id_type` = Customer ID type (1=TIN, 2=DL, 3=Voters, 4=Passport, 5=NID, 6=NIL)
- [ ] `advatec_pump_map` = JSON map of EFD serial -> pump number

---

## 5. Edge Agent Provisioning

### 5.1 Per-Site Agent Setup
- [ ] Bootstrap token generated for the site
- [ ] Edge agent (Android APK or Desktop MSIX) installed on device
- [ ] Device connected to station LAN (same network as FCC)
- [ ] Agent provisioned: registered with cloud backend, JWT issued
- [ ] Config pulled successfully from cloud (verify via agent diagnostics screen)
- [ ] FCC connectivity verified (heartbeat green on diagnostics)
- [ ] Odoo WebSocket bridge connected (if site uses pre-auth)

### 5.2 Network Requirements
- [ ] Edge device can reach FCC on station LAN (ping host_address:port)
- [ ] Edge device can reach cloud API over WAN (HTTPS 443)
- [ ] Firewall rules allow outbound HTTPS from edge device
- [ ] For RELAY mode: edge device acts as intermediary between FCC and cloud

---

## 6. Portal Frontend

### 6.1 Azure Entra ID (Production / Staging)
- [ ] Azure AD App Registration created for the portal SPA
- [ ] `msalClientId` — Application (client) ID from Azure portal
- [ ] `msalAuthority` — `https://login.microsoftonline.com/{TENANT_ID}`
- [ ] `msalRedirectUri` — Portal URL (e.g., `https://portal.fccmiddleware.internal`)
- [ ] App roles defined in Azure AD manifest: `SystemAdmin`, `OperationsManager`, `SiteSupervisor`, `Auditor`, `SupportReadOnly`
- [ ] Users assigned to app roles in Azure AD
- [ ] API permissions: portal app granted access to backend API scope (`api://{clientId}/.default`)

### 6.2 Environment Configuration
- [ ] `src/environments/environment.prod.ts` updated with production values:
  - `apiBaseUrl` — Cloud backend URL (e.g., `https://api.fccmiddleware.internal`)
  - `msalClientId` — Azure AD client ID
  - `msalAuthority` — Azure AD authority URL
  - `msalRedirectUri` — Portal production URL
  - `backendLoggingEnabled` — `true`
  - `bypassAuth` — `false` (or omitted) in production/staging

### 6.3 Build & Deploy
- [ ] `npm run build` — Production build (validates env placeholders are replaced)
- [ ] Deploy `dist/portal/` to static hosting (S3 + CloudFront, Nginx, Azure Static Web Apps)
- [ ] Configure hosting to serve `index.html` for all routes (SPA fallback)
- [ ] HTTPS enforced with valid TLS certificate
- [ ] CORS on backend allows the portal's origin

### 6.4 Local Dev (Auth Bypass)
When `bypassAuth: true` in `environment.ts`:
- MSAL route guard is replaced by a passthrough guard
- MSAL token interceptor is skipped (API requests have no Bearer token)
- ShellComponent shows "Dev User / SystemAdmin" instead of Azure AD identity
- Backend endpoints that require `PortalUser` policy will return 401 (expected in dev without JWT)

---

## 7. Verification & Smoke Testing

### 6.1 Cloud Backend
- [ ] API starts without errors (`dotnet run` or container)
- [ ] Worker starts without errors (background jobs running)
- [ ] Health check endpoint returns 200: `GET /health`
- [ ] Swagger UI accessible in dev: `GET /swagger` (disabled in prod)
- [ ] Portal can authenticate via Azure Entra and load dashboard

### 6.2 Transaction Flow
- [ ] Simulate a transaction ingest: `POST /api/v1/transactions/ingest`
- [ ] Verify transaction appears in `transactions` table with status `PENDING`
- [ ] Verify deduplication works (re-send same fcc_transaction_id -> no duplicate)
- [ ] Verify Odoo sync marks transaction as `SYNCED_TO_ODOO`

### 6.3 Pre-Auth Flow (Radix)
- [ ] Odoo POS sends pre-auth request via WebSocket
- [ ] Edge agent forwards AUTH_DATA XML to Radix FCC on auth_port
- [ ] FCC responds with ACKCODE
- [ ] Pre-auth record transitions: PENDING -> AUTHORIZED -> DISPENSING -> COMPLETED
- [ ] Matched transaction linked to pre-auth record

### 6.4 Monitoring
- [ ] Agent telemetry snapshots arriving in `agent_telemetry_snapshots`
- [ ] Heartbeat monitoring active (alerts on consecutive failures)
- [ ] Dead letter queue monitored (alerts on threshold breach)
- [ ] Audit events being recorded for key operations

---

## 7. Local Development Quick Start

```bash
# 1. Start PostgreSQL + Redis
docker compose up -d

# 2. Verify containers are healthy
docker compose ps

# 3. Schema is auto-applied from db/migrations/ on first start
#    (files in db/migrations/ are mounted to /docker-entrypoint-initdb.d/)

# 4. Connect to verify
docker exec -it fcc-postgres psql -U postgres -d fccmiddleware -c "\dt"

# 5. Run the cloud API (terminal 1)
cd src/cloud/FccMiddleware.Api
dotnet run
# API on http://localhost:5070 — Swagger at http://localhost:5070/swagger

# 6. Run the worker (terminal 2)
cd src/cloud/FccMiddleware.Worker
dotnet run

# 7. Run the portal frontend (terminal 3)
cd src/portal
npm install   # first time only
npm start
# Portal on http://localhost:4200 (auth bypassed by default)

# 8. To reset database completely
docker compose down -v
docker compose up -d
```

### Connection Strings (Local Dev)
```
PostgreSQL: Host=localhost;Port=5432;Database=fccmiddleware;Username=postgres;Password=postgres
Redis:      localhost:6379
```

### Seed Data Reference IDs (Local Dev)
| Entity | ID | Code |
|---|---|---|
| Legal Entity (MW) | `a1000001-0000-4000-8000-000000000001` | `FCC-MW` |
| Site (Lilongwe 01) | `b2000001-0000-4000-8000-000000000001` | `MW-LL-001` |
| Product (ULP) | `c3000001-0000-4000-8000-000000000001` | `PETROL_ULP` |
| Product (Diesel 50) | `c3000002-0000-4000-8000-000000000002` | `DIESEL_50` |
| Product (Diesel 500) | `c3000003-0000-4000-8000-000000000003` | `DIESEL_500` |
| Pump 1 | `d4000001-0000-4000-8000-000000000001` | Odoo=1, FCC=1 |
| Pump 2 | `d4000002-0000-4000-8000-000000000002` | Odoo=2, FCC=2 |
| FCC Config (Radix) | `f7000001-0000-4000-8000-000000000001` | Radix P4000 |
| Operator | `f6000001-0000-4000-8000-000000000001` | `ATT-001` |

### Radix FCC Config (Local Dev)
| Field | Value |
|---|---|
| Host | `192.168.1.100` |
| Auth Port | `10000` |
| Transaction Port | `10001` |
| USN Code | `100001` |
| Shared Secret | `dev-test-shared-secret-do-not-use-in-prod` |
| Transaction Mode | `PULL` (FIFO drain) |
| Ingestion Mode | `RELAY` |
| Poll Interval | `15s` |
| Pump Map | `{"1": {"PumpAddr": 1, "Fp": 1}, "2": {"PumpAddr": 2, "Fp": 1}}` |
