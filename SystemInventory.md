# FCC Middleware — System Inventory

> Generated: 2026-03-13 | Discovery only — no bug analysis

---

## Table of Contents

1. [Solution & Project Structure](#1-solution--project-structure)
2. [Frontend (Angular Portal)](#2-frontend-angular-portal)
3. [Backend Controllers & APIs](#3-backend-controllers--apis)
4. [Application Layer (CQRS Handlers)](#4-application-layer-cqrs-handlers)
5. [Domain Entities & Enums](#5-domain-entities--enums)
6. [Infrastructure Services](#6-infrastructure-services)
7. [Cloud Adapters](#7-cloud-adapters)
8. [Desktop Edge Agent (C#)](#8-desktop-edge-agent-c)
9. [Android Edge Agent (Kotlin)](#9-android-edge-agent-kotlin)
10. [External Integrations](#10-external-integrations)
11. [Background Workers](#11-background-workers)
12. [Authentication & Authorization](#12-authentication--authorization)
13. [Test Projects](#13-test-projects)
14. [Configuration & Deployment](#14-configuration--deployment)

---

## 1. Solution & Project Structure

| Solution | Platform | Location |
|----------|----------|----------|
| FccMiddleware.sln | .NET 10 | `src/cloud/` |
| FccDesktopAgent.sln | .NET 10 | `src/desktop-edge-agent/` |
| Edge Agent (Gradle) | Kotlin/Android | `src/edge-agent/` |
| Portal | Angular 20 | `src/portal/` |
| VirtualLab.sln | .NET 10 | `VirtualLab/src/` |

**Cloud Projects:**
- FccMiddleware.Domain
- FccMiddleware.Application
- FccMiddleware.Infrastructure
- FccMiddleware.Api
- FccMiddleware.Worker
- FccMiddleware.Contracts
- FccMiddleware.ServiceDefaults
- FccMiddleware.Adapter.Doms
- FccMiddleware.Adapter.Radix
- FccMiddleware.Adapter.Petronite
- FccMiddleware.Adapter.Advatec

**Desktop Agent Projects:**
- FccDesktopAgent.Core
- FccDesktopAgent.Api
- FccDesktopAgent.App
- FccDesktopAgent.Service

---

## 2. Frontend (Angular Portal)

### 2.1 Routes

| Path | Component | Guard |
|------|-----------|-------|
| `/dashboard` | DashboardComponent | MsalGuard |
| `/transactions/list` | TransactionListComponent | MsalGuard |
| `/transactions/:id` | TransactionDetailComponent | MsalGuard |
| `/reconciliation/exceptions` | ReconciliationListComponent | MsalGuard |
| `/reconciliation/exceptions/:id` | ReconciliationDetailComponent | MsalGuard |
| `/agents` | AgentListComponent | MsalGuard |
| `/agents/bootstrap-token` | BootstrapTokenComponent | roleGuard(SystemAdmin) |
| `/agents/:id` | AgentDetailComponent | MsalGuard |
| `/sites/list` | SiteConfigComponent | roleGuard(SystemAdmin, OperationsManager, SiteSupervisor) |
| `/sites/:id` | SiteDetailComponent | roleGuard(SystemAdmin, OperationsManager, SiteSupervisor) |
| `/master-data/status` | MasterDataComponent | MsalGuard |
| `/audit/list` | AuditLogComponent | MsalGuard |
| `/audit/events/:id` | AuditDetailComponent | MsalGuard |
| `/dlq/list` | DlqListComponent | MsalGuard |
| `/dlq/items/:id` | DlqDetailComponent | MsalGuard |
| `/settings` | SettingsComponent | roleGuard(SystemAdmin) |
| `/access-denied` | AccessDeniedComponent | — |
| `/auth` | MsalRedirectComponent | — |

### 2.2 Components

**Layout:** ShellComponent (app-shell) — main nav + user info

**Feature Components (20):**

| Component | Selector | Location |
|-----------|----------|----------|
| DashboardComponent | app-dashboard | features/dashboard/ |
| TransactionListComponent | app-transaction-list | features/transactions/ |
| TransactionDetailComponent | app-transaction-detail | features/transactions/ |
| ReconciliationListComponent | app-reconciliation-list | features/reconciliation/ |
| ReconciliationDetailComponent | app-reconciliation-detail | features/reconciliation/ |
| ReconciliationFiltersComponent | app-reconciliation-filters | features/reconciliation/ |
| AgentListComponent | app-agent-list | features/edge-agents/ |
| AgentDetailComponent | app-agent-detail | features/edge-agents/ |
| BootstrapTokenComponent | app-bootstrap-token | features/edge-agents/ |
| SiteConfigComponent | app-site-config | features/site-config/ |
| SiteDetailComponent | app-site-detail | features/site-config/ |
| FccConfigFormComponent | app-fcc-config-form | features/site-config/ |
| PumpMappingComponent | app-pump-mapping | features/site-config/ |
| MasterDataComponent | app-master-data | features/master-data/ |
| AuditLogComponent | app-audit-log | features/audit-log/ |
| AuditDetailComponent | app-audit-detail | features/audit-log/ |
| DlqListComponent | app-dlq-list | features/dlq/ |
| DlqDetailComponent | app-dlq-detail | features/dlq/ |
| SettingsComponent | app-settings | features/settings/ |
| AccessDeniedComponent | app-access-denied | shared/components/ |

**Dashboard Subcomponents (6):**
TransactionVolumeChartComponent, IngestionHealthComponent, AgentStatusSummaryComponent, ReconciliationSummaryComponent, StaleTransactionsComponent, ActiveAlertsComponent

**Shared Components (6):**
DataTableComponent, StatusBadgeComponent, EmptyStateComponent, LoadingSpinnerComponent, DateRangePickerComponent, AccessDeniedComponent

### 2.3 Services

| Service | Key Methods |
|---------|------------|
| TransactionService | getTransactions(), getTransactionById(), acknowledgeTransactions() |
| ReconciliationService | getExceptions(), getById(), approve(), reject() |
| AgentService | getAgents(), getAgentById(), getAgentTelemetry(), getAgentEvents() |
| SiteService | getSites(), getSiteById(), getSiteConfig(), updateSite(), addPump(), updateNozzle() |
| AuditService | getAuditEvents(), getAuditEventById() |
| DlqService | getDeadLetters(), getDeadLetterById(), retry(), discard(), retryBatch(), discardBatch() |
| MasterDataService | getSyncStatus(), getLegalEntities() |
| SettingsService | getSettings(), updateGlobalDefaults(), upsertLegalEntityOverride(), updateAlertConfiguration() |
| BootstrapTokenService | generate(), revoke() |
| LoggingService | debug(), info(), warn(), error() |
| DashboardService | dashboard-specific queries |

### 2.4 Models (core/models/)

common.model.ts, transaction.model.ts, agent.model.ts, site.model.ts, reconciliation.model.ts, audit.model.ts, dlq.model.ts, pre-auth.model.ts, master-data.model.ts, settings.model.ts, bootstrap-token.model.ts

### 2.5 Guards, Interceptors, Pipes, Directives

| Type | Name | Purpose |
|------|------|---------|
| Guard | authGuard | Wraps MsalGuard |
| Guard | roleGuard(roles) | JWT role check |
| Interceptor | apiInterceptor | Base URL prepend + error handling |
| Interceptor | MsalInterceptor | JWT attachment (class-based) |
| Pipe | currencyMinorUnits | Minor units → display string |
| Pipe | statusLabel | Enum → human label |
| Pipe | utcDate | UTC ISO → local timezone |
| Directive | [appRoleVisible] | Show/hide by role |

### 2.6 Auth Config

- Azure Entra ID / MSAL
- Roles: SystemAdmin, SystemAdministrator, OperationsManager, SiteSupervisor, Auditor, SupportReadOnly
- PrimeNG UI (Lara preset, Puma Energy Red)

---

## 3. Backend Controllers & APIs

### TransactionsController — `/api/v1/transactions`

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| POST | /api/v1/transactions/ingest | FccHmac | FCC vendor push (batch/single) |
| POST | /api/v1/ingest/radix | Anonymous | Radix CLOUD_DIRECT XML push |
| POST | /api/v1/ingest/petronite/webhook | Anonymous | Petronite webhook |
| POST | /api/v1/ingest/advatec/webhook | Anonymous | Advatec Receipt webhook |
| POST | /api/v1/transactions/upload | EdgeAgentDevice | Edge agent batch upload |
| GET | /api/v1/transactions/synced-status | EdgeAgentDevice | FCC txn IDs synced to Odoo |
| GET | /api/v1/transactions | OdooApiKey | Poll PENDING (cursor-paginated) |
| POST | /api/v1/transactions/acknowledge | OdooApiKey | Batch PENDING → SYNCED_TO_ODOO |

### AgentsController — `/api/v1/agents` [PortalUser]

| Verb | Route | Purpose |
|------|-------|---------|
| GET | /api/v1/agents | List agents (cursor-paginated) |
| GET | /api/v1/agents/{id} | Agent details |
| GET | /api/v1/agents/{id}/telemetry | Telemetry history |
| GET | /api/v1/agents/{id}/events | Audit events |

### AgentController — Mixed routes

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| POST | /api/v1/admin/bootstrap-tokens | PortalAdminWrite | Generate bootstrap token |
| DELETE | /api/v1/admin/bootstrap-tokens/{tokenId} | PortalAdminWrite | Revoke token |
| POST | /api/v1/agent/register | BootstrapToken | Device registration |
| POST | /api/v1/agent/token/refresh | DeviceRefreshToken | Refresh JWT |
| POST | /api/v1/admin/agent/{deviceId}/decommission | PortalAdminWrite | Decommission device |
| GET | /api/v1/agent/config | EdgeAgentDevice | Get site FCC config |
| GET | /api/v1/agent/version-check | EdgeAgentDevice | Version update check |
| POST | /api/v1/agent/telemetry | EdgeAgentDevice | Submit telemetry |
| POST | /api/v1/agent/diagnostic-logs | EdgeAgentDevice | Submit diagnostic logs |
| GET | /api/v1/agents/{deviceId}/diagnostic-logs | PortalUser | Get diagnostic logs |

### AuditController — `/api/v1/audit/events` [PortalUser]

| Verb | Route | Purpose |
|------|-------|---------|
| GET | /api/v1/audit/events | List audit events |
| GET | /api/v1/audit/events/{eventId} | Event details |

### DlqController — `/api/v1/dlq` [PortalUser]

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| GET | /api/v1/dlq | PortalUser | List dead-letter items |
| GET | /api/v1/dlq/{id} | PortalUser | Item details |
| POST | /api/v1/dlq/{id}/retry | PortalAdminWrite | Retry item |
| POST | /api/v1/dlq/{id}/discard | PortalAdminWrite | Discard item |
| POST | /api/v1/dlq/retry-batch | PortalAdminWrite | Batch retry |
| POST | /api/v1/dlq/discard-batch | PortalAdminWrite | Batch discard |

### OpsTransactionsController — `/api/v1/ops/transactions` [PortalUser]

| Verb | Route | Purpose |
|------|-------|---------|
| GET | /api/v1/ops/transactions | List transactions (advanced filters) |
| GET | /api/v1/ops/transactions/{id} | Transaction details |
| POST | /api/v1/ops/transactions/acknowledge | Acknowledge/flag |

### OpsReconciliationController — `/api/v1/ops/reconciliation` [PortalUser]

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| GET | /api/v1/ops/reconciliation/exceptions | PortalUser | List unmatched records |
| GET | /api/v1/ops/reconciliation/{id} | PortalUser | Record details |
| POST | /api/v1/ops/reconciliation/{id}/approve | PortalReconciliationReview | Approve exception |
| POST | /api/v1/ops/reconciliation/{id}/reject | PortalReconciliationReview | Reject exception |

### SitesController — `/api/v1/sites` [PortalUser]

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| GET | /api/v1/sites | PortalUser | List sites |
| GET | /api/v1/sites/{id} | PortalUser | Site details |
| PATCH | /api/v1/sites/{id} | PortalAdminWrite | Update site |
| PUT | /api/v1/sites/{siteId}/fcc-config | PortalAdminWrite | Update FCC config |
| GET | /api/v1/sites/{siteId}/pumps | PortalUser | List pumps |
| POST | /api/v1/sites/{siteId}/pumps | PortalAdminWrite | Create pump |
| DELETE | /api/v1/sites/{siteId}/pumps/{pumpId} | PortalAdminWrite | Delete pump |
| PATCH | /api/v1/sites/{siteId}/pumps/{pumpId}/nozzles/{nozzleNumber} | PortalAdminWrite | Update nozzle |

### MasterDataController — `/api/v1/master-data` [DatabricksApiKey]

| Verb | Route | Purpose |
|------|-------|---------|
| PUT | /api/v1/master-data/legal-entities | Sync legal entities |
| PUT | /api/v1/master-data/sites | Sync sites |
| PUT | /api/v1/master-data/pumps | Sync pumps |
| PUT | /api/v1/master-data/products | Sync products |
| PUT | /api/v1/master-data/operators | Sync operators |

### MasterDataBrowserController — `/api/v1/master-data` [PortalUser]

| Verb | Route | Purpose |
|------|-------|---------|
| GET | /api/v1/master-data/legal-entities | List legal entities |
| GET | /api/v1/master-data/products | List products |
| GET | /api/v1/master-data/sync-status | Sync status |

### PreAuthController — `/api/v1/preauth` [EdgeAgentDevice]

| Verb | Route | Purpose |
|------|-------|---------|
| POST | /api/v1/preauth | Request pre-authorization |
| PATCH | /api/v1/preauth/{id} | Update status (COMPLETED/CANCELLED) |

### AdminDashboardController — `/api/v1/admin/dashboard` [PortalUser]

| Verb | Route | Purpose |
|------|-------|---------|
| GET | /api/v1/admin/dashboard/summary | Dashboard stats |
| GET | /api/v1/admin/dashboard/alerts | Dashboard alerts |

### AdminSettingsController — `/api/v1/admin/settings` [PortalAdminWrite]

| Verb | Route | Purpose |
|------|-------|---------|
| GET | /api/v1/admin/settings | Get all settings |
| PUT | /api/v1/admin/settings/global-defaults | Update global defaults |
| PUT | /api/v1/admin/settings/overrides/{legalEntityId} | Upsert entity override |
| DELETE | /api/v1/admin/settings/overrides/{legalEntityId} | Delete entity override |
| PUT | /api/v1/admin/settings/alerts | Update alert config |

### HealthController

| Verb | Route | Auth | Purpose |
|------|-------|------|---------|
| GET | /api/v1/health-info | Anonymous | Health info |
| GET | /health | Anonymous | Liveness |
| GET | /health/ready | PortalUser | Readiness (PG + Redis) |

---

## 4. Application Layer (CQRS Handlers)

| Feature | Command/Query | Handler |
|---------|---------------|---------|
| Ingestion | IngestTransactionCommand | IngestTransactionHandler |
| Ingestion | UploadTransactionBatchCommand | UploadTransactionBatchHandler |
| Transactions | PollTransactionsQuery | PollTransactionsHandler |
| Transactions | GetSyncedTransactionIdsQuery | GetSyncedTransactionIdsHandler |
| Transactions | AcknowledgeTransactionsBatchCommand | AcknowledgeTransactionsBatchHandler |
| MasterData | SyncLegalEntitiesCommand | SyncLegalEntitiesHandler |
| MasterData | SyncSitesCommand | SyncSitesHandler |
| MasterData | SyncPumpsCommand | SyncPumpsHandler |
| MasterData | SyncProductsCommand | SyncProductsHandler |
| MasterData | SyncOperatorsCommand | SyncOperatorsHandler |
| PreAuth | ForwardPreAuthCommand | ForwardPreAuthHandler |
| PreAuth | UpdatePreAuthStatusCommand | UpdatePreAuthStatusHandler |
| Reconciliation | GetReconciliationExceptionsQuery | GetReconciliationExceptionsHandler |
| Reconciliation | ReviewReconciliationCommand | ReviewReconciliationHandler |
| Registration | GenerateBootstrapTokenCommand | GenerateBootstrapTokenHandler |
| Registration | RegisterDeviceCommand | RegisterDeviceHandler |
| Registration | RefreshDeviceTokenCommand | RefreshDeviceTokenHandler |
| Registration | RevokeBootstrapTokenCommand | RevokeBootstrapTokenHandler |
| Registration | DecommissionDeviceCommand | DecommissionDeviceHandler |
| AgentConfig | GetAgentConfigQuery | GetAgentConfigHandler |
| AgentConfig | CheckAgentVersionQuery | CheckAgentVersionHandler |
| Telemetry | SubmitTelemetryCommand | SubmitTelemetryHandler |
| DiagnosticLogs | SubmitDiagnosticLogsCommand | SubmitDiagnosticLogsHandler |
| DiagnosticLogs | GetDiagnosticLogsQuery | GetDiagnosticLogsHandler |

**Common Services:**
- Result<T> / Result — Railway-oriented error handling
- ReconciliationMatchingService — Fuzzy matching (FCC ↔ Odoo)
- SemanticVersion — Version comparison utility
- IFieldEncryptor — Field-level encryption interface

---

## 5. Domain Entities & Enums

### Entities

| Entity | Category | Key Fields |
|--------|----------|------------|
| LegalEntity | Master Data | Tenant/company scope |
| Site | Master Data | Fuel station |
| Pump | Master Data | Pump with status + operating model |
| Nozzle | Master Data | Pump nozzle config |
| Product | Master Data | Fuel product |
| Operator | Master Data | Pump attendant |
| Transaction | Transactional | FCC transaction (partitioned by CreatedAt) |
| PreAuthRecord | Transactional | Pre-authorization lifecycle |
| ReconciliationRecord | Transactional | FCC ↔ Odoo unmatched record |
| FccConfig | Configuration | Site FCC vendor config (encrypted secrets) |
| AgentRegistration | Registration | Edge agent device registration |
| AgentTelemetrySnapshot | Telemetry | Latest agent telemetry |
| AgentDiagnosticLog | Telemetry | Agent diagnostic logs |
| BootstrapToken | Registration | One-time provisioning token |
| DeviceRefreshToken | Registration | JWT refresh token |
| AuditEvent | Audit | User action audit (partitioned) |
| OutboxMessage | Events | Transactional outbox for event publishing |
| DeadLetterItem | DLQ | Failed transaction/command parking |
| OdooApiKey | API Keys | Odoo service auth |
| DatabricksApiKey | API Keys | Databricks master-data auth |
| PortalSettings | Settings | Global portal settings |
| LegalEntitySettingsOverride | Settings | Per-entity overrides |

### Enums

FccVendor (DOMS, RADIX, PETRONITE, ADVATEC), IngestionMode, IngestionMethod, IngestionSource, TransactionMode, TransactionStatus, FiscalizationMode, PreAuthStatus, ReconciliationStatus, DeadLetterType, DeadLetterReason, DeadLetterStatus, AgentRegistrationStatus, ConnectivityState, PumpState, PumpStatusSource, ProvisioningTokenStatus, OperatingModel, SiteOperatingModel, ConnectionProtocol

---

## 6. Infrastructure Services

### Persistence

| Component | Purpose |
|-----------|---------|
| FccMiddlewareDbContext | Main EF Core context — multi-tenant, 12 specialized interfaces |
| 23 EntityTypeConfigurations | FluentAPI mappings for all entities |
| PostgresPartitionManager | DDL for partition creation/detachment |
| SiteFccConfigProvider | Site config lookup by siteCode, usnCode, webhookSecret |

### Deduplication

| Component | Purpose |
|-----------|---------|
| RedisDeduplicationService | Primary dedup via Redis (configurable window) |
| IDeduplicationDbContext | Secondary PostgreSQL fallback |

### Storage

| Component | Purpose |
|-----------|---------|
| S3RawPayloadArchiver | Archive raw FCC payloads to S3 (KMS encrypted) |
| ArchiveObjectStore | Parquet archival for historical data |

### Events

| Component | Purpose |
|-----------|---------|
| OutboxEventPublisher | Transactional outbox publisher |
| OutboxPublisherWorker | Background service — polls outbox, publishes events |

### Dead Letter Queue

| Component | Purpose |
|-----------|---------|
| DeadLetterService | Create/retrieve DLQ items |
| DlqReplayService | Retry/discard DLQ items |

### Security

| Component | Purpose |
|-----------|---------|
| AesGcmFieldEncryptor | AES-256-GCM for sensitive DB fields |
| EncryptedFieldConverter | EF Core value converter |
| FccHmacAuthHandler | HMAC-SHA256 for FCC ingestion |
| OdooApiKeyAuthHandler | API key auth for Odoo |
| DatabricksApiKeyAuthHandler | API key auth for master-data |
| DeviceTokenService | JWT creation/validation |
| SecurityConfigurationValidator | Auth config validation at startup |

### Observability

| Component | Purpose |
|-----------|---------|
| CloudWatchEmfMetricSink | EMF metrics to CloudWatch |
| ActivityEnricher | Serilog + trace context |
| RedactingDestructuringPolicy | Sensitive field redaction |

### Resilience

| Component | Purpose |
|-----------|---------|
| HttpClientResilienceExtensions | Retry + circuit breaker for HTTP clients |

---

## 7. Cloud Adapters

All implement `IFccAdapter` (and optional `IFccPumpDeauthorizationAdapter`).

| Adapter | Vendor | Mode | Protocol | Key Behavior |
|---------|--------|------|----------|--------------|
| DomsCloudAdapter | DOMS | Push+Pull | JSON REST | Validates vendor, content-type, required fields |
| RadixCloudAdapter | RADIX | Push+Pull | XML/JSON | SHA-1 signature validation, reverse pump map |
| PetroniteCloudAdapter | PETRONITE | Push | JSON webhook | Event type check, orderId/productCode validation |
| AdvatecCloudAdapter | ADVATEC | Push | JSON webhook | Receipt envelope, TZS currency, Items validation |

**Factory:** CloudFccAdapterFactoryRegistration — resolves by (FccVendor, SiteFccConfig)

---

## 8. Desktop Edge Agent (C#)

### Adapters

| Adapter | Protocol | External System |
|---------|----------|-----------------|
| AdvatecAdapter | HTTP REST + TCP + Webhook listener (8091) | Advatec device (localhost:5560) |
| DomsAdapter | HTTP REST over LAN | DOMS FCC station |
| DomsJplAdapter | TCP/JPL binary frames (STX/ETX) | DOMS FCC station |
| PetroniteAdapter | HTTP REST + OAuth2 + Webhook (8090) | Petronite cloud API |
| RadixAdapter | HTTP POST (XML, dual-port) | Radix FCC station |

### Buffer (SQLite + EF Core)

AgentDbContext with entities: BufferedTransaction, BufferedPreAuth, NozzleMapping, SyncStateRecord, AgentConfigRecord, AuditLogEntry

### Sync Workers

CloudUploadWorker, ConfigPollWorker, TelemetryReporter, VersionCheckService, StatusPollWorker

### Key Components

IngestionOrchestrator, PreAuthHandler, TransactionBufferManager, ConfigManager, FccAdapterFactory, PumpStatusService

---

## 9. Android Edge Agent (Kotlin)

### Adapters

| Adapter | Protocol | Notes vs Desktop |
|---------|----------|-----------------|
| AdvatecAdapter | HTTP REST + TCP + Webhook | Same as desktop |
| DomsJplAdapter | TCP/JPL only | No REST variant on Android |
| PetroniteAdapter | HTTP REST + OAuth2 (Ktor) | Same capability |
| RadixAdapter | HTTP POST (XML) | Same capability |

### Buffer (Room Database v5)

BufferDatabase with 10 entities: BufferedTransaction, PreAuthRecord, Nozzle, SyncState, AgentConfig, AuditLog, SiteInfo, LocalProduct, LocalPump, LocalNozzle

DAOs: TransactionBufferDao, PreAuthDao, NozzleDao, SyncStateDao, AgentConfigDao, AuditLogDao, SiteDataDao

### Sync Workers

CloudUploadWorker, ConfigPollWorker, PreAuthCloudForwardWorker, TelemetryReporter

### Key Components

IngestionOrchestrator, PreAuthHandler, TransactionBufferManager, ConfigManager, FccAdapterFactory, ConnectivityManager, NetworkBinder, KeystoreManager, EncryptedPrefsManager

---

## 10. External Integrations

| System | Protocol | Direction | Consumer |
|--------|----------|-----------|----------|
| PostgreSQL | EF Core | Cloud backend | Persistence, multi-tenant, partitioned |
| Redis | StackExchange.Redis | Cloud backend | Deduplication (primary) |
| AWS S3 | AWS SDK | Cloud backend | Raw payload archival (KMS encrypted) |
| CloudWatch | EMF | Cloud backend | Metrics/observability |
| Azure Entra ID | MSAL/OAuth2 | Portal + API | Portal user authentication |
| Odoo ERP | API Key (X-Api-Key) | Cloud backend | Transaction polling + acknowledgment |
| Databricks | API Key | Cloud backend | Master data sync |
| DOMS FCC | HTTP REST or TCP/JPL | Edge agents | Transaction fetch, pre-auth, pump status |
| Radix FCC | HTTP XML (dual-port) | Edge agents | Transaction fetch, pre-auth |
| Petronite Cloud | HTTP REST + OAuth2 | Edge agents | Pre-auth, webhook transactions |
| Advatec Device | HTTP REST + TCP | Edge agents | Customer data, receipt webhooks |
| Odoo POS | HTTP REST (LAN) | Edge agents | Pre-auth requests inbound |
| Android KeyStore | Android API | Android agent | JWT + credential storage |

---

## 11. Background Workers

| Worker | Project | Purpose |
|--------|---------|---------|
| OutboxPublisherWorker | Cloud | Polls outbox, publishes domain events |
| ArchiveWorker | Cloud | Archive old partitions to S3/Parquet, clean outbox |
| PreAuthExpiryWorker | Cloud | Expire stale pre-auths, trigger deauthorization |
| StaleTransactionWorker | Cloud | Mark aged PENDING transactions as stale |
| UnmatchedReconciliationWorker | Cloud | Auto-resolve matched reconciliation records |
| MonitoringSnapshotWorker | Cloud | Snapshot agent connectivity/telemetry |

---

## 12. Authentication & Authorization

### API Authentication Schemes

| Scheme | Mechanism | Used By |
|--------|-----------|---------|
| Bearer (default) | JWT | Edge agent devices |
| PortalBearer | Azure Entra / MSAL | Portal users |
| FccHmac | HMAC-SHA256 | FCC service-to-service |
| OdooApiKey | X-Api-Key header | Odoo polling |
| DatabricksApiKey | API Key | Master data sync |
| BootstrapToken | One-time token | Device registration |
| DeviceRefreshToken | Refresh token | Token refresh |

### Authorization Policies

| Policy | Roles |
|--------|-------|
| PortalUser | OperationsManager, SystemAdmin, Auditor, SiteSupervisor, SupportReadOnly |
| PortalReconciliationReview | OperationsManager, SystemAdmin |
| PortalAdminWrite | OperationsManager, SystemAdmin |
| EdgeAgentDevice | JWT with site + lei claims |

### Rate Limiting

| Policy | Limit |
|--------|-------|
| anonymous-ingress | 100 req/min per IP |
| registration | 10 req/min per IP |
| token-refresh | 20 req/min per IP |

### Middleware Pipeline

CorrelationId → GlobalExceptionHandler → SerilogRequestLogging → RateLimiter → Authentication → DeviceActiveCheck → TenantScope → Authorization

---

## 13. Test Projects

| Project | Framework | Deps | Location |
|---------|-----------|------|----------|
| FccMiddleware.Domain.Tests | xUnit 2.9.3 | FluentAssertions, NSubstitute | src/cloud/ |
| FccMiddleware.Application.Tests | xUnit 2.9.3 | FluentAssertions, NSubstitute | src/cloud/ |
| FccMiddleware.Infrastructure.Tests | xUnit 2.9.3 | FluentAssertions, NSubstitute | src/cloud/ |
| FccMiddleware.Api.Tests | xUnit 2.9.3 | AspNetCore.Mvc.Testing | src/cloud/ |
| FccMiddleware.UnitTests | xUnit 2.9.3 | FluentAssertions, NSubstitute | src/cloud/tests/ |
| FccMiddleware.IntegrationTests | xUnit 2.9.3 | Testcontainers (PG, Redis) | src/cloud/tests/ |
| FccMiddleware.ArchitectureTests | xUnit 2.9.3 | Architecture rules | src/cloud/tests/ |
| FccDesktopAgent.Core.Tests | xUnit | NSubstitute, FluentAssertions | src/desktop-edge-agent/tests/ |
| FccDesktopAgent.Api.Tests | xUnit | Mvc.Testing | src/desktop-edge-agent/tests/ |
| FccDesktopAgent.Integration.Tests | xUnit | FluentAssertions | src/desktop-edge-agent/tests/ |
| FccDesktopAgent.Benchmarks | BenchmarkDotNet | — | src/desktop-edge-agent/tests/ |
| Android Tests | JUnit 5 | — | src/edge-agent/app/src/test/ |
| Portal Tests | Jasmine+Karma, Cypress | — | src/portal/ |

---

## 14. Configuration & Deployment

### Docker

| File | Base Image | Purpose |
|------|------------|---------|
| Dockerfile.api | dotnet/aspnet:10.0 | API container |
| Dockerfile.worker | dotnet/aspnet:10.0 | Background worker container |

### CI/CD (GitHub Actions)

| Workflow | Purpose |
|----------|---------|
| ci.yml | Cloud build + test + lint |
| ci-desktop-agent.yml | Desktop agent CI |
| desktop-agent-release.yml | Desktop release |

### Environment Configs

| Environment | API URL | Auth |
|-------------|---------|------|
| Development | http://localhost:5000 | Placeholder Entra IDs |
| Staging | https://api.staging.fccmiddleware.internal | Staging Entra |
| Production | https://api.fccmiddleware.internal | Prod Entra |

### Key Settings

- Kestrel request limit: 5 MB global, 1 MB webhooks
- Serilog structured JSON logging
- OpenTelemetry tracing
- PostgreSQL partitioning (pg_partman)
- Redis dedup window (configurable)
- S3 archival with KMS encryption
