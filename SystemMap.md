# FCC Middleware — System Map

## System Overview

FCC Middleware is a multi-tenant fuel retail transaction processing platform that connects Forecourt Controllers (FCCs) to Odoo ERP via a cloud backend, with optional edge agents for offline/buffered operation. It supports 4 FCC vendors (DOMS, Radix, Petronite, Advatec), pre-authorization workflows, reconciliation, and fiscal compliance.

---

## 1. Frontend — Angular Portal

**Stack**: Angular 20.3 · PrimeNG 20.4 · MSAL (Azure Entra ID) · RxJS · Chart.js

### Routes

| Route | Component | Guard | Roles |
|-------|-----------|-------|-------|
| `/` | → redirect `/dashboard` | MsalGuard | All |
| `/dashboard` | DashboardComponent | MsalGuard | All |
| `/transactions/list` | TransactionListComponent | MsalGuard | All |
| `/transactions/:id` | TransactionDetailComponent | MsalGuard | All |
| `/reconciliation/exceptions` | ReconciliationListComponent | roleGuard | SystemAdmin, OperationsManager, SiteSupervisor, Auditor |
| `/reconciliation/exceptions/:id` | ReconciliationDetailComponent | roleGuard | SystemAdmin, OperationsManager, SiteSupervisor, Auditor |
| `/agents` | AgentListComponent | MsalGuard | All |
| `/agents/bootstrap-token` | BootstrapTokenComponent | roleGuard | SystemAdmin |
| `/agents/:id` | AgentDetailComponent | MsalGuard | All |
| `/sites/list` | SiteConfigComponent | roleGuard | SystemAdmin, OperationsManager, SiteSupervisor |
| `/sites/:id` | SiteDetailComponent | roleGuard | SystemAdmin, OperationsManager, SiteSupervisor |
| `/master-data/status` | MasterDataComponent | MsalGuard | All |
| `/audit/list` | AuditLogComponent | MsalGuard | All |
| `/audit/events/:id` | AuditDetailComponent | MsalGuard | All |
| `/dlq/list` | DlqListComponent | roleGuard | SystemAdmin, OperationsManager |
| `/dlq/items/:id` | DlqDetailComponent | roleGuard | SystemAdmin, OperationsManager |
| `/settings` | SettingsComponent | roleGuard | SystemAdmin |
| `/auth` | MsalRedirectComponent | — | — |
| `/access-denied` | AccessDeniedComponent | — | — |

### Guards & Interceptors

| Name | Type | Purpose |
|------|------|---------|
| MsalGuard | Route Guard | Azure Entra ID authentication |
| roleGuard | Route Guard | Role-based route protection (reads JWT `roles` claim) |
| apiInterceptor | HTTP Interceptor | Prepends apiBaseUrl, handles 401/403/500+ |
| MsalInterceptor | HTTP Interceptor | Attaches Bearer tokens to API requests |

### Services (API Clients)

| Service | Base Path | Key Operations |
|---------|-----------|----------------|
| AgentService | `/api/v1/agents` | List, detail, telemetry, events, diagnostic logs |
| TransactionService | `/api/v1/ops/transactions` | List, detail, acknowledge batch |
| ReconciliationService | `/api/v1/ops/reconciliation` | List exceptions, detail, approve, reject |
| SiteService | `/api/v1/sites` | List, detail, update, FCC config, pumps, nozzles |
| DlqService | `/api/v1/dlq` | List, detail, retry, discard (single + batch) |
| AuditService | `/api/v1/audit/events` | List, detail |
| BootstrapTokenService | `/api/v1/admin/bootstrap-tokens` | Generate, revoke |
| MasterDataService | `/api/v1/master-data` | Sync status, legal entities |
| SettingsService | `/api/v1/admin/settings` | Global defaults, overrides, alerts |
| DashboardService | `/api/v1/admin/dashboard` | Summary, alerts |
| LoggingService | `/api/v1/portal/client-logs` | Client-side error logging |

### Shared UI Components

| Component | Purpose |
|-----------|---------|
| DataTableComponent | Reusable data grid |
| DateRangePickerComponent | Date range selection |
| EmptyStateComponent | Empty list placeholder |
| LoadingSpinnerComponent | Loading indicator |
| StatusBadgeComponent | Status chip/badge |
| ShellComponent | Main layout (nav, header, outlet) |

### Pipes & Directives

| Name | Type | Purpose |
|------|------|---------|
| CurrencyMinorUnitsPipe | Pipe | Minor units → display currency (ISO 4217) |
| StatusLabelPipe | Pipe | Enum → human label |
| UtcDatePipe | Pipe | UTC ISO 8601 → local display |
| RoleVisibleDirective | Directive | `*appRoleVisible="['SystemAdmin']"` conditional rendering |

---

## 2. Backend — .NET Cloud API

**Stack**: ASP.NET Core · MediatR · EF Core 10 · PostgreSQL (partitioned) · Redis · AWS S3 · Serilog · CloudWatch EMF

### Controllers & Endpoints

#### Ingestion (Transaction Intake)

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| POST | `/api/v1/transactions/ingest` | FCC HMAC | Push ingest from FCC (generic) |
| POST | `/api/v1/transactions/ingest/radix` | Anonymous (SHA-1 sig) | Radix FDC XML push |
| POST | `/api/v1/transactions/ingest/petronite/webhook` | Anonymous (X-Webhook-Secret) | Petronite webhook |
| POST | `/api/v1/transactions/ingest/advatec/webhook` | Anonymous (X-Webhook-Token) | Advatec webhook |
| POST | `/api/v1/transactions/upload` | EdgeAgentDevice JWT | Edge Agent batch upload (max 500) |

#### Odoo Integration

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| GET | `/api/v1/transactions` | Odoo API Key | Poll PENDING transactions |
| POST | `/api/v1/transactions/acknowledge` | Odoo API Key | Batch acknowledge → SYNCED_TO_ODOO |
| GET | `/api/v1/transactions/synced-status` | EdgeAgentDevice JWT | Get acknowledged FCC tx IDs |

#### Portal Operations

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| GET | `/api/v1/ops/transactions` | PortalUser | List transactions (filtered) |
| GET | `/api/v1/ops/transactions/{id}` | PortalUser | Transaction detail |
| POST | `/api/v1/ops/transactions/acknowledge` | PortalAdminWrite | Manual acknowledge batch |
| GET | `/api/v1/ops/reconciliation/exceptions` | PortalUser | List reconciliation exceptions |
| GET | `/api/v1/ops/reconciliation/{id}` | PortalUser | Reconciliation detail |
| POST | `/api/v1/ops/reconciliation/{id}/approve` | PortalReconciliationReview | Approve exception |
| POST | `/api/v1/ops/reconciliation/{id}/reject` | PortalReconciliationReview | Reject exception |

#### Edge Agent Management

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| POST | `/api/v1/agent/register` | Anonymous (bootstrap token) | Device registration |
| POST | `/api/v1/agent/token/refresh` | Anonymous (refresh token) | JWT rotation |
| GET | `/api/v1/agent/config` | EdgeAgentDevice JWT | Site config (ETag caching) |
| GET | `/api/v1/agent/version-check` | EdgeAgentDevice JWT | Version compatibility |
| POST | `/api/v1/agent/telemetry` | EdgeAgentDevice JWT | Health snapshot |
| POST | `/api/v1/agent/diagnostic-logs` | EdgeAgentDevice JWT | Log upload |
| GET | `/api/v1/agents` | PortalUser | List agents |
| GET | `/api/v1/agents/{id}` | PortalUser | Agent detail |
| GET | `/api/v1/agents/{id}/telemetry` | PortalUser | Agent telemetry |
| GET | `/api/v1/agents/{id}/events` | PortalUser | Agent audit events |
| GET | `/api/v1/agents/{id}/diagnostic-logs` | PortalUser | Diagnostic logs |

#### Admin

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| POST | `/api/v1/admin/bootstrap-tokens` | PortalAdminWrite | Generate bootstrap token |
| DELETE | `/api/v1/admin/bootstrap-tokens/{id}` | PortalAdminWrite | Revoke bootstrap token |
| POST | `/api/v1/admin/agent/{id}/decommission` | PortalAdminWrite | Decommission device |
| GET | `/api/v1/admin/settings` | PortalAdminWrite | System settings |
| PUT | `/api/v1/admin/settings/global-defaults` | PortalAdminWrite | Update defaults |
| PUT | `/api/v1/admin/settings/overrides/{id}` | PortalAdminWrite | Upsert LE override |
| DELETE | `/api/v1/admin/settings/overrides/{id}` | PortalAdminWrite | Delete LE override |
| PUT | `/api/v1/admin/settings/alerts` | PortalAdminWrite | Update alert config |
| GET | `/api/v1/admin/dashboard/summary` | PortalUser | Dashboard metrics |
| GET | `/api/v1/admin/dashboard/alerts` | PortalUser | Active alerts |

#### Site Configuration

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| GET | `/api/v1/sites` | PortalUser | List sites |
| GET | `/api/v1/sites/{id}` | PortalUser | Site detail |
| PATCH | `/api/v1/sites/{id}` | PortalAdminWrite | Update site |
| PUT | `/api/v1/sites/{id}/fcc-config` | PortalAdminWrite | Update FCC config |
| GET | `/api/v1/sites/{id}/pumps` | PortalUser | List pumps |
| POST | `/api/v1/sites/{id}/pumps` | PortalAdminWrite | Add pump |
| DELETE | `/api/v1/sites/{id}/pumps/{pumpId}` | PortalAdminWrite | Remove pump |
| PATCH | `/api/v1/sites/{id}/pumps/{pumpId}/nozzles/{n}` | PortalAdminWrite | Update nozzle |

#### Master Data Sync (Databricks)

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| PUT | `/api/v1/master-data/legal-entities` | Databricks API Key | Upsert legal entities |
| PUT | `/api/v1/master-data/sites` | Databricks API Key | Upsert sites |
| PUT | `/api/v1/master-data/pumps` | Databricks API Key | Upsert pumps |
| PUT | `/api/v1/master-data/products` | Databricks API Key | Upsert products |
| PUT | `/api/v1/master-data/operators` | Databricks API Key | Upsert operators |
| GET | `/api/v1/master-data/legal-entities` | PortalUser | Browse legal entities |
| GET | `/api/v1/master-data/products` | PortalUser | Browse products |
| GET | `/api/v1/master-data/sync-status` | PortalUser | Sync status |

#### Pre-Auth

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| POST | `/api/v1/preauth` | EdgeAgentDevice JWT | Forward pre-auth event |
| PATCH | `/api/v1/preauth/{id}` | EdgeAgentDevice JWT | Update pre-auth status |

#### Health

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| GET | `/health` | Anonymous | Liveness probe |
| GET | `/health/ready` | PortalUser | Readiness (PostgreSQL + Redis) |

### Middleware Pipeline (Order)

1. CorrelationIdMiddleware — Extract/generate correlation ID
2. GlobalExceptionHandlerMiddleware — Standardized error responses
3. SerilogRequestLoggingMiddleware — Structured request logging
4. RateLimiterMiddleware — Per-IP rate limits (anonymous-ingress: 100/min, registration: 10/min, token-refresh: 20/min)
5. AuthenticationMiddleware — JWT Bearer, PortalBearer, HMAC, API Keys
6. AuthorizationMiddleware — Policy enforcement
7. DeviceActiveCheckMiddleware — Decommission check for device JWTs
8. TenantScopeMiddleware — Populate TenantContext from claims

### Authentication Schemes

| Scheme | Method | Use Case |
|--------|--------|----------|
| JwtBearer (default) | SymmetricKey JWT | Edge Agent device tokens |
| PortalBearer | Azure Entra ID JWT | Portal users (MSAL) |
| FccHmac | HMAC signature header | FCC service-to-service |
| OdooApiKey | X-Api-Key header | Odoo polling/acknowledge |
| DatabricksApiKey | X-Api-Key header | Master data sync |

### Authorization Policies

| Policy | Required Roles |
|--------|----------------|
| PortalUser | OperationsManager, SystemAdmin, SystemAdministrator, Auditor, SiteSupervisor, SupportReadOnly |
| PortalAdminWrite | OperationsManager, SystemAdmin, SystemAdministrator |
| PortalReconciliationReview | OperationsManager, SystemAdmin, SystemAdministrator |
| EdgeAgentDevice | site + lei claims present |

---

## 3. Application Layer — Handlers & Services

### MediatR Handlers

| Handler | Command/Query | Purpose |
|---------|---------------|---------|
| IngestTransactionHandler | IngestTransactionCommand | FCC push ingestion pipeline |
| UploadTransactionBatchHandler | UploadTransactionBatchCommand | Edge Agent batch upload |
| PollTransactionsHandler | PollTransactionsQuery | Odoo cursor-paginated fetch |
| GetSyncedTransactionIdsHandler | GetSyncedTransactionIdsQuery | Edge Agent synced-status |
| AcknowledgeTransactionsBatchHandler | AcknowledgeTransactionsBatchCommand | Odoo batch acknowledge |
| GenerateBootstrapTokenHandler | GenerateBootstrapTokenCommand | Create provisioning token |
| RevokeBootstrapTokenHandler | RevokeBootstrapTokenCommand | Revoke provisioning token |
| RegisterDeviceHandler | RegisterDeviceCommand | Device registration |
| RefreshDeviceTokenHandler | RefreshDeviceTokenCommand | JWT rotation (refresh token reuse detection) |
| DecommissionDeviceHandler | DecommissionDeviceCommand | Decommission device |
| ForwardPreAuthHandler | ForwardPreAuthCommand | Pre-auth lifecycle forwarding |
| UpdatePreAuthStatusHandler | UpdatePreAuthStatusCommand | Pre-auth status transition |
| ReviewReconciliationHandler | ReviewReconciliationCommand | Approve/reject reconciliation |
| GetReconciliationExceptionsHandler | GetReconciliationExceptionsQuery | List reconciliation exceptions |
| SyncProductsHandler | SyncProductsCommand | Master data product sync |
| SyncOperatorsHandler | SyncOperatorsCommand | Master data operator sync |
| SyncPumpsHandler | SyncPumpsCommand | Master data pump sync |
| SyncLegalEntitiesHandler | SyncLegalEntitiesCommand | Master data LE sync |
| SyncSitesHandler | SyncSitesCommand | Master data site sync |
| SubmitTelemetryHandler | SubmitTelemetryCommand | Edge Agent health report |
| CheckAgentVersionHandler | CheckAgentVersionQuery | Version compatibility |
| GetAgentConfigHandler | GetAgentConfigQuery | Build full SiteConfig |
| SubmitDiagnosticLogsHandler | SubmitDiagnosticLogsCommand | Diagnostic log persistence |
| GetDiagnosticLogsHandler | GetDiagnosticLogsQuery | Diagnostic log retrieval |

### Application Services

| Service | Interface | Purpose |
|---------|-----------|---------|
| RedisDeduplicationService | IDeduplicationService | Two-tier dedup (Redis → PostgreSQL) |
| DeadLetterService | IDeadLetterService | Capture unrecoverable failures |
| DlqReplayService | IDlqReplayService | Replay dead-letter items |
| DeviceTokenService | IDeviceTokenService | Generate device JWTs |
| SiteFccConfigProvider | ISiteFccConfigProvider | Resolve site FCC config |
| ReconciliationMatchingService | — | Pre-auth ↔ dispense matching (3 strategies) |
| OutboxEventPublisher | IEventPublisher | Stage domain events in outbox |

---

## 4. Domain Layer

### Entities (22 DbSets)

| Entity | Partition | Tenant-Scoped | Purpose |
|--------|-----------|---------------|---------|
| Transaction | Monthly (CreatedAt) | Yes | Core fuel transaction |
| PreAuthRecord | — | Yes | Pre-authorization lifecycle |
| ReconciliationRecord | — | Yes | Pre-auth ↔ dispense reconciliation |
| AgentRegistration | — | Yes | Edge Agent device |
| DeviceRefreshToken | — | — | JWT refresh tokens |
| BootstrapToken | — | Yes | Provisioning tokens |
| FccConfig | — | Yes | Per-site FCC configuration |
| Site | — | Yes | Fuel retail location |
| LegalEntity | — | — | Tenant/business entity |
| Pump | — | — | Physical pump |
| Nozzle | — | — | Nozzle on pump |
| Product | — | Yes | Fuel product |
| Operator | — | Yes | Fuel operator |
| DeadLetterItem | — | Yes | Failed ingestion records |
| AuditEvent | Monthly (CreatedAt) | Yes | Audit trail |
| OutboxMessage | — | — | Transactional outbox |
| AgentTelemetrySnapshot | — | Yes | Latest agent health |
| AgentDiagnosticLog | — | Yes | Agent diagnostic logs |
| PortalSettings | — | — | Global settings |
| LegalEntitySettingsOverride | — | — | Per-LE settings |
| OdooApiKey | — | — | Odoo API keys |
| DatabricksApiKey | — | — | Databricks API keys |

### State Machines

**Transaction**: PENDING → SYNCED_TO_ODOO | DUPLICATE | ARCHIVED

**PreAuth**: PENDING → AUTHORIZED → DISPENSING → COMPLETED | CANCELLED | EXPIRED | FAILED

**Reconciliation**: UNMATCHED → MATCHED | VARIANCE_WITHIN_TOLERANCE | VARIANCE_FLAGGED → APPROVED | REJECTED | REVIEW_FUZZY_MATCH

**DeadLetter**: PENDING → RETRIED | DISCARDED

**BootstrapToken**: ACTIVE → USED | REVOKED

---

## 5. Infrastructure Layer

### Background Workers

| Worker | Interval | Purpose |
|--------|----------|---------|
| OutboxPublisherWorker | 5s | Publish outbox events → audit + (future: SNS) |
| ArchiveWorker | 1h | Partition export to S3 Parquet + cleanup |
| PreAuthExpiryWorker | 60s | Expire stale pre-auths, deauth pumps |
| StaleTransactionWorker | 15min | Flag PENDING transactions > 3 days |
| UnmatchedReconciliationWorker | 60s | Retry/escalate unmatched reconciliation |
| MonitoringSnapshotWorker | 5min | Emit observability metrics |

### External Integrations

| System | Protocol | Purpose |
|--------|----------|---------|
| PostgreSQL | EF Core / Npgsql | Primary database (partitioned) |
| Redis | StackExchange.Redis | Dedup cache, batch result cache |
| AWS S3 | AWSSDK.S3 | Raw payload archive, partition export |
| AWS KMS | AWSSDK.S3 (SSE) | Encryption at rest |
| CloudWatch | EMF JSON logs | Metrics & observability |
| Azure Entra ID | OIDC/JWT | Portal authentication |
| Odoo ERP | API Key polling | Transaction delivery |
| Databricks | API Key | Master data sync |

### Security Features

| Feature | Implementation |
|---------|---------------|
| Field encryption | AES-256-GCM for FCC credentials at rest |
| Webhook auth | Constant-time HMAC (Petronite), SHA-256 hash index (Advatec) |
| Token security | SHA-256 hashed storage, refresh token rotation, reuse detection |
| Tenant isolation | Global query filters on LegalEntityId |
| Rate limiting | Per-IP sliding window (100/min ingress, 10/min registration) |

---

## 6. Cloud Adapters

| Vendor | Class | Ingestion | Pull | Protocol |
|--------|-------|-----------|------|----------|
| DOMS | DomsCloudAdapter | Push + Pull | HTTP REST | JSON |
| Radix | RadixCloudAdapter | Push only | — | XML + SHA-1 signature |
| Petronite | PetroniteCloudAdapter | Push only | — | JSON webhook |
| Advatec | AdvatecCloudAdapter | Push only | — | JSON webhook (TRA fiscal) |

All adapters: Validate → Normalize → produce CanonicalTransaction
Currency: CurrencyHelper (ISO 4217 factor mapping)
Volume: Always microlitres (×1,000,000)
Amount: Always minor units (cents/fils/etc.)

---

## 7. Edge Agents

### Android Edge Agent (Kotlin)

| Component | Purpose |
|-----------|---------|
| EdgeAgentForegroundService | Always-on service: adapters, local API (Ktor:8585), sync |
| IngestionOrchestrator | FCC polling, normalization, local buffering |
| TransactionBufferManager | Room DB buffer: PENDING → UPLOADED → SYNCED → ARCHIVED |
| CloudUploadWorker | Batch upload to `/api/v1/transactions/upload` (circuit breaker) |
| PreAuthCloudForwardWorker | Forward pre-auth events to cloud |
| PreAuthHandler | LAN-only pre-auth (p95 ≤150ms local, ≤1.5s e2e) |
| ConfigManager | StateFlow config, offline bootstrap, hot-reload |
| BootReceiver | Auto-start on device boot |
| 4 Adapters | DOMS (TCP/JPL), Radix (XML/HTTP), Petronite (REST/OAuth2), Advatec (HTTP/webhook) |

### Desktop Edge Agent (.NET)

| Component | Purpose |
|-----------|---------|
| FccDesktopAgent.Core | Core logic: adapters, buffer, config, sync |
| AgentDbContext | EF Core + SQLite (WAL mode) |
| ConfigPollWorker | Cloud config polling with ETag |
| PreAuthHandler | LAN-only pre-auth |
| 4 Adapters | Same vendor coverage, C# implementations |

### Adapter Protocols by Vendor

| Vendor | Connection | Pre-Auth | Pump Status |
|--------|------------|----------|-------------|
| DOMS | Persistent TCP (STX/ETX framed JPL) | authorize_Fp_req | Live (from FCC) |
| Radix | HTTP POST (XML, dual-port) | AUTH_DATA XML | Not supported |
| Petronite | REST + OAuth2 (LAN) | Two-step create+authorize | Synthesized |
| Advatec | HTTP REST + webhook listener | Customer data submission | Synthesized from pre-auths |
