# FCC Middleware — Audit Coverage Tracker

> Generated: 2026-03-13 | Discovery phase — no bug analysis performed yet

Legend: ⬜ Not started | 🔲 Discovered (not audited) | ✅ Audited | ❌ Issues found

---

## 1. Frontend (Angular Portal)

### Routes & Pages

| # | Route | Component | Status |
|---|-------|-----------|--------|
| F-01 | /dashboard | DashboardComponent | ❌ 7 issues |
| F-02 | /transactions/list | TransactionListComponent | ❌ 7 issues |
| F-03 | /transactions/:id | TransactionDetailComponent | ❌ 6 issues |
| F-04 | /reconciliation/exceptions | ReconciliationListComponent | ❌ 8 issues |
| F-05 | /reconciliation/exceptions/:id | ReconciliationDetailComponent | ❌ 7 issues |
| F-06 | /agents | AgentListComponent | ❌ 8 issues |
| F-07 | /agents/bootstrap-token | BootstrapTokenComponent | ❌ 6 issues |
| F-08 | /agents/:id | AgentDetailComponent | ❌ 9 issues |
| F-09 | /sites/list | SiteConfigComponent | ❌ 9 issues |
| F-10 | /sites/:id | SiteDetailComponent | ❌ 11 issues |
| F-11 | /master-data/status | MasterDataComponent | ❌ 7 issues |
| F-12 | /audit/list | AuditLogComponent | ❌ 10 issues |
| F-13 | /audit/events/:id | AuditDetailComponent | ❌ 7 issues |
| F-14 | /dlq/list | DlqListComponent | ❌ 9 issues |
| F-15 | /dlq/items/:id | DlqDetailComponent | ❌ 9 issues |
| F-16 | /settings | SettingsComponent | ❌ 10 issues |

### Services

| # | Service | Status |
|---|---------|--------|
| F-S01 | TransactionService | 🔲 |
| F-S02 | ReconciliationService | 🔲 |
| F-S03 | AgentService | 🔲 |
| F-S04 | SiteService | 🔲 |
| F-S05 | AuditService | 🔲 |
| F-S06 | DlqService | 🔲 |
| F-S07 | MasterDataService | 🔲 |
| F-S08 | SettingsService | 🔲 |
| F-S09 | BootstrapTokenService | 🔲 |
| F-S10 | LoggingService | 🔲 |
| F-S11 | DashboardService | 🔲 |

### Auth & Security

| # | Component | Status |
|---|-----------|--------|
| F-A01 | authGuard | 🔲 |
| F-A02 | roleGuard | 🔲 |
| F-A03 | apiInterceptor | 🔲 |
| F-A04 | MsalInterceptor config | 🔲 |
| F-A05 | [appRoleVisible] directive | 🔲 |
| F-A06 | MSAL auth config | 🔲 |

### Shared Components

| # | Component | Status |
|---|-----------|--------|
| F-C01 | DataTableComponent | 🔲 |
| F-C02 | StatusBadgeComponent | 🔲 |
| F-C03 | EmptyStateComponent | 🔲 |
| F-C04 | LoadingSpinnerComponent | 🔲 |
| F-C05 | DateRangePickerComponent | 🔲 |
| F-C06 | currencyMinorUnits pipe | 🔲 |
| F-C07 | statusLabel pipe | 🔲 |
| F-C08 | utcDate pipe | 🔲 |

---

## 2. Backend API Controllers

### Transaction Ingestion

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-01 | POST /api/v1/transactions/ingest | FccHmac | ❌ 2 issues (M-13, M-14) |
| B-02 | POST /api/v1/ingest/radix | Anonymous | ❌ 1 issue (H-5) |
| B-03 | POST /api/v1/ingest/petronite/webhook | Anonymous | ❌ 1 issue (L-7) |
| B-04 | POST /api/v1/ingest/advatec/webhook | Anonymous | ❌ 1 issue (L-7) |
| B-05 | POST /api/v1/transactions/upload | EdgeAgentDevice | ❌ 1 issue (M-15) |

### Transaction Management

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-06 | GET /api/v1/transactions/synced-status | EdgeAgentDevice | ✅ |
| B-07 | GET /api/v1/transactions | OdooApiKey | ❌ 1 issue (L-8) |
| B-08 | POST /api/v1/transactions/acknowledge | OdooApiKey | ✅ |
| B-09 | GET /api/v1/ops/transactions | PortalUser | ❌ 3 issues (M-16, L-9, L-10) |
| B-10 | GET /api/v1/ops/transactions/{id} | PortalUser | ✅ |
| B-11 | POST /api/v1/ops/transactions/acknowledge | PortalAdminWrite | ❌ 1 issue (L-11) |

### Agent Management

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-12 | GET /api/v1/agents | PortalUser | ❌ 2 issues (M-17, L-14) |
| B-13 | GET /api/v1/agents/{id} | PortalUser | ❌ 1 issue (L-12) |
| B-14 | GET /api/v1/agents/{id}/telemetry | PortalUser | ❌ 2 issues (L-12, L-13) |
| B-15 | GET /api/v1/agents/{id}/events | PortalUser | ❌ 2 issues (M-18, L-12) |
| B-16 | POST /api/v1/agent/register | BootstrapToken | 🔲 |
| B-17 | POST /api/v1/agent/token/refresh | DeviceRefreshToken | 🔲 |
| B-18 | POST /api/v1/admin/agent/{deviceId}/decommission | PortalAdminWrite | 🔲 |
| B-19 | GET /api/v1/agent/config | EdgeAgentDevice | 🔲 |
| B-20 | GET /api/v1/agent/version-check | EdgeAgentDevice | 🔲 |
| B-21 | POST /api/v1/agent/telemetry | EdgeAgentDevice | 🔲 |
| B-22 | POST /api/v1/agent/diagnostic-logs | EdgeAgentDevice | 🔲 |
| B-23 | GET /api/v1/agents/{deviceId}/diagnostic-logs | PortalUser | 🔲 |

### Bootstrap Tokens

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-24 | POST /api/v1/admin/bootstrap-tokens | PortalAdminWrite | 🔲 |
| B-25 | DELETE /api/v1/admin/bootstrap-tokens/{tokenId} | PortalAdminWrite | 🔲 |

### Audit

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-26 | GET /api/v1/audit/events | PortalUser | 🔲 |
| B-27 | GET /api/v1/audit/events/{eventId} | PortalUser | 🔲 |

### Dead Letter Queue

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-28 | GET /api/v1/dlq | PortalUser | 🔲 |
| B-29 | GET /api/v1/dlq/{id} | PortalUser | 🔲 |
| B-30 | POST /api/v1/dlq/{id}/retry | PortalAdminWrite | 🔲 |
| B-31 | POST /api/v1/dlq/{id}/discard | PortalAdminWrite | 🔲 |
| B-32 | POST /api/v1/dlq/retry-batch | PortalAdminWrite | 🔲 |
| B-33 | POST /api/v1/dlq/discard-batch | PortalAdminWrite | 🔲 |

### Reconciliation

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-34 | GET /api/v1/ops/reconciliation/exceptions | PortalUser | 🔲 |
| B-35 | GET /api/v1/ops/reconciliation/{id} | PortalUser | 🔲 |
| B-36 | POST /api/v1/ops/reconciliation/{id}/approve | PortalReconciliationReview | 🔲 |
| B-37 | POST /api/v1/ops/reconciliation/{id}/reject | PortalReconciliationReview | 🔲 |

### Sites & Configuration

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-38 | GET /api/v1/sites | PortalUser | 🔲 |
| B-39 | GET /api/v1/sites/{id} | PortalUser | 🔲 |
| B-40 | PATCH /api/v1/sites/{id} | PortalAdminWrite | 🔲 |
| B-41 | PUT /api/v1/sites/{siteId}/fcc-config | PortalAdminWrite | 🔲 |
| B-42 | GET /api/v1/sites/{siteId}/pumps | PortalUser | 🔲 |
| B-43 | POST /api/v1/sites/{siteId}/pumps | PortalAdminWrite | 🔲 |
| B-44 | DELETE /api/v1/sites/{siteId}/pumps/{pumpId} | PortalAdminWrite | 🔲 |
| B-45 | PATCH /api/v1/sites/{siteId}/pumps/{pumpId}/nozzles/{n} | PortalAdminWrite | 🔲 |

### Master Data

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-46 | PUT /api/v1/master-data/legal-entities | DatabricksApiKey | 🔲 |
| B-47 | PUT /api/v1/master-data/sites | DatabricksApiKey | 🔲 |
| B-48 | PUT /api/v1/master-data/pumps | DatabricksApiKey | 🔲 |
| B-49 | PUT /api/v1/master-data/products | DatabricksApiKey | 🔲 |
| B-50 | PUT /api/v1/master-data/operators | DatabricksApiKey | 🔲 |
| B-51 | GET /api/v1/master-data/legal-entities | PortalUser | 🔲 |
| B-52 | GET /api/v1/master-data/products | PortalUser | 🔲 |
| B-53 | GET /api/v1/master-data/sync-status | PortalUser | 🔲 |

### Pre-Auth

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-54 | POST /api/v1/preauth | EdgeAgentDevice | 🔲 |
| B-55 | PATCH /api/v1/preauth/{id} | EdgeAgentDevice | 🔲 |

### Dashboard & Settings

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-56 | GET /api/v1/admin/dashboard/summary | PortalUser | 🔲 |
| B-57 | GET /api/v1/admin/dashboard/alerts | PortalUser | 🔲 |
| B-58 | GET /api/v1/admin/settings | PortalAdminWrite | 🔲 |
| B-59 | PUT /api/v1/admin/settings/global-defaults | PortalAdminWrite | 🔲 |
| B-60 | PUT /api/v1/admin/settings/overrides/{lei} | PortalAdminWrite | 🔲 |
| B-61 | DELETE /api/v1/admin/settings/overrides/{lei} | PortalAdminWrite | 🔲 |
| B-62 | PUT /api/v1/admin/settings/alerts | PortalAdminWrite | 🔲 |

### Health

| # | Endpoint | Auth | Status |
|---|----------|------|--------|
| B-63 | GET /api/v1/health-info | Anonymous | 🔲 |
| B-64 | GET /health | Anonymous | 🔲 |
| B-65 | GET /health/ready | PortalUser | 🔲 |

---

## 3. Application Handlers

| # | Handler | Status |
|---|---------|--------|
| H-01 | IngestTransactionHandler | 🔲 |
| H-02 | UploadTransactionBatchHandler | 🔲 |
| H-03 | PollTransactionsHandler | 🔲 |
| H-04 | GetSyncedTransactionIdsHandler | 🔲 |
| H-05 | AcknowledgeTransactionsBatchHandler | 🔲 |
| H-06 | SyncLegalEntitiesHandler | 🔲 |
| H-07 | SyncSitesHandler | 🔲 |
| H-08 | SyncPumpsHandler | 🔲 |
| H-09 | SyncProductsHandler | 🔲 |
| H-10 | SyncOperatorsHandler | 🔲 |
| H-11 | ForwardPreAuthHandler | 🔲 |
| H-12 | UpdatePreAuthStatusHandler | 🔲 |
| H-13 | GetReconciliationExceptionsHandler | 🔲 |
| H-14 | ReviewReconciliationHandler | 🔲 |
| H-15 | GenerateBootstrapTokenHandler | 🔲 |
| H-16 | RegisterDeviceHandler | 🔲 |
| H-17 | RefreshDeviceTokenHandler | 🔲 |
| H-18 | RevokeBootstrapTokenHandler | 🔲 |
| H-19 | DecommissionDeviceHandler | 🔲 |
| H-20 | GetAgentConfigHandler | 🔲 |
| H-21 | CheckAgentVersionHandler | 🔲 |
| H-22 | SubmitTelemetryHandler | 🔲 |
| H-23 | SubmitDiagnosticLogsHandler | 🔲 |
| H-24 | GetDiagnosticLogsHandler | 🔲 |
| H-25 | ReconciliationMatchingService | 🔲 |

---

## 4. Cloud Adapters

| # | Adapter | Vendor | Status |
|---|---------|--------|--------|
| CA-01 | DomsCloudAdapter | DOMS | 🔲 |
| CA-02 | RadixCloudAdapter | RADIX | 🔲 |
| CA-03 | PetroniteCloudAdapter | PETRONITE | 🔲 |
| CA-04 | AdvatecCloudAdapter | ADVATEC | 🔲 |
| CA-05 | CloudFccAdapterFactoryRegistration | — | 🔲 |
| CA-06 | RadixSignatureHelper | RADIX | 🔲 |

---

## 5. Infrastructure Services

| # | Service | Status |
|---|---------|--------|
| I-01 | FccMiddlewareDbContext | 🔲 |
| I-02 | SiteFccConfigProvider | 🔲 |
| I-03 | RedisDeduplicationService | 🔲 |
| I-04 | S3RawPayloadArchiver | 🔲 |
| I-05 | ArchiveObjectStore | 🔲 |
| I-06 | OutboxEventPublisher | 🔲 |
| I-07 | DeadLetterService | 🔲 |
| I-08 | DlqReplayService | 🔲 |
| I-09 | AesGcmFieldEncryptor | 🔲 |
| I-10 | FccHmacAuthHandler | 🔲 |
| I-11 | OdooApiKeyAuthHandler | 🔲 |
| I-12 | DatabricksApiKeyAuthHandler | 🔲 |
| I-13 | DeviceTokenService | 🔲 |
| I-14 | CloudWatchEmfMetricSink | 🔲 |
| I-15 | HttpClientResilienceExtensions | 🔲 |
| I-16 | PortalAccessResolver | 🔲 |
| I-17 | SecurityConfigurationValidator | 🔲 |
| I-18 | HealthResponseWriter | 🔲 |
| I-19 | PostgresPartitionManager | 🔲 |

---

## 6. Background Workers

| # | Worker | Status |
|---|--------|--------|
| W-01 | OutboxPublisherWorker | 🔲 |
| W-02 | ArchiveWorker | 🔲 |
| W-03 | PreAuthExpiryWorker | 🔲 |
| W-04 | StaleTransactionWorker | 🔲 |
| W-05 | UnmatchedReconciliationWorker | 🔲 |
| W-06 | MonitoringSnapshotWorker | 🔲 |

---

## 7. Middleware Pipeline

| # | Middleware | Status |
|---|-----------|--------|
| M-01 | CorrelationIdMiddleware | 🔲 |
| M-02 | GlobalExceptionHandlerMiddleware | 🔲 |
| M-03 | DeviceActiveCheckMiddleware | 🔲 |
| M-04 | TenantScopeMiddleware | 🔲 |
| M-05 | Rate Limiting (3 policies) | 🔲 |
| M-06 | Request Size Limits | 🔲 |

---

## 8. Desktop Edge Agent (C#)

### Adapters

| # | Component | Status |
|---|-----------|--------|
| DA-01 | AdvatecAdapter | 🔲 |
| DA-02 | AdvatecApiClient | 🔲 |
| DA-03 | AdvatecWebhookListener | 🔲 |
| DA-04 | AdvatecFiscalizationService | 🔲 |
| DA-05 | DomsAdapter (REST) | 🔲 |
| DA-06 | DomsJplAdapter (TCP) | 🔲 |
| DA-07 | JplTcpClient | 🔲 |
| DA-08 | JplFrameCodec | 🔲 |
| DA-09 | JplHeartbeatManager | 🔲 |
| DA-10 | DomsLogonHandler | 🔲 |
| DA-11 | DomsPreAuthHandler | 🔲 |
| DA-12 | DomsPumpStatusParser | 🔲 |
| DA-13 | DomsTransactionParser | 🔲 |
| DA-14 | PetroniteAdapter | 🔲 |
| DA-15 | PetroniteOAuthClient | 🔲 |
| DA-16 | PetroniteNozzleResolver | 🔲 |
| DA-17 | PetroniteWebhookListener | 🔲 |
| DA-18 | RadixAdapter | 🔲 |
| DA-19 | RadixPushListener | 🔲 |
| DA-20 | RadixSignatureHelper | 🔲 |
| DA-21 | RadixXmlBuilder | 🔲 |
| DA-22 | RadixXmlParser | 🔲 |
| DA-23 | FccAdapterFactory | 🔲 |
| DA-24 | PumpStatusService | 🔲 |
| DA-25 | CurrencyHelper | 🔲 |

### Buffer & Storage

| # | Component | Status |
|---|-----------|--------|
| DA-30 | AgentDbContext | 🔲 |
| DA-31 | TransactionBufferManager | 🔲 |
| DA-32 | BufferedTransaction entity | 🔲 |
| DA-33 | BufferedPreAuth entity | 🔲 |
| DA-34 | SqliteWalModeInterceptor | 🔲 |
| DA-35 | IntegrityChecker | 🔲 |
| DA-36 | CleanupWorker | 🔲 |

### Sync & Config

| # | Component | Status |
|---|-----------|--------|
| DA-40 | CloudUploadWorker | 🔲 |
| DA-41 | ConfigPollWorker | 🔲 |
| DA-42 | ConfigManager | 🔲 |
| DA-43 | TelemetryReporter | 🔲 |
| DA-44 | VersionCheckService | 🔲 |
| DA-45 | StatusPollWorker | 🔲 |
| DA-46 | DeviceTokenProvider | 🔲 |

### Core Orchestration

| # | Component | Status |
|---|-----------|--------|
| DA-50 | IngestionOrchestrator | 🔲 |
| DA-51 | PreAuthHandler | 🔲 |
| DA-52 | DesktopFccRuntimeConfiguration | 🔲 |
| DA-53 | SiteConfig | 🔲 |

---

## 9. Android Edge Agent (Kotlin)

### Adapters

| # | Component | Status |
|---|-----------|--------|
| AA-01 | AdvatecAdapter | 🔲 |
| AA-02 | AdvatecFiscalizationService | 🔲 |
| AA-03 | AdvatecWebhookListener | 🔲 |
| AA-04 | DomsJplAdapter (TCP only) | 🔲 |
| AA-05 | JplTcpClient | 🔲 |
| AA-06 | JplFrameCodec | 🔲 |
| AA-07 | JplHeartbeatManager | 🔲 |
| AA-08 | DomsLogonHandler | 🔲 |
| AA-09 | DomsPreAuthHandler | 🔲 |
| AA-10 | PetroniteAdapter | 🔲 |
| AA-11 | PetroniteOAuthClient | 🔲 |
| AA-12 | PetroniteNozzleResolver | 🔲 |
| AA-13 | RadixAdapter | 🔲 |
| AA-14 | RadixPushListener | 🔲 |
| AA-15 | RadixSignatureHelper | 🔲 |
| AA-16 | RadixXmlBuilder | 🔲 |
| AA-17 | RadixXmlParser | 🔲 |
| AA-18 | FccAdapterFactory | 🔲 |
| AA-19 | PumpStatusSynthesizer | 🔲 |
| AA-20 | AdapterTimeouts | 🔲 |

### Buffer & Storage (Room)

| # | Component | Status |
|---|-----------|--------|
| AA-30 | BufferDatabase (v5) | 🔲 |
| AA-31 | TransactionBufferDao | 🔲 |
| AA-32 | PreAuthDao | 🔲 |
| AA-33 | NozzleDao | 🔲 |
| AA-34 | SyncStateDao | 🔲 |
| AA-35 | AgentConfigDao | 🔲 |
| AA-36 | SiteDataDao | 🔲 |
| AA-37 | TransactionBufferManager | 🔲 |
| AA-38 | IntegrityChecker | 🔲 |
| AA-39 | CleanupWorker | 🔲 |

### Sync & Config

| # | Component | Status |
|---|-----------|--------|
| AA-40 | CloudUploadWorker | 🔲 |
| AA-41 | ConfigPollWorker | 🔲 |
| AA-42 | PreAuthCloudForwardWorker | 🔲 |
| AA-43 | ConfigManager | 🔲 |
| AA-44 | TelemetryReporter | 🔲 |
| AA-45 | CloudApiClient | 🔲 |
| AA-46 | DeviceTokenProvider | 🔲 |
| AA-47 | CircuitBreaker | 🔲 |

### Core Orchestration

| # | Component | Status |
|---|-----------|--------|
| AA-50 | IngestionOrchestrator | 🔲 |
| AA-51 | PreAuthHandler | 🔲 |
| AA-52 | ConnectivityManager | 🔲 |
| AA-53 | NetworkBinder | 🔲 |
| AA-54 | KeystoreManager | 🔲 |
| AA-55 | EncryptedPrefsManager | 🔲 |

---

## 10. Domain & Entity Configurations

| # | Component | Status |
|---|-----------|--------|
| D-01 | Transaction entity | 🔲 |
| D-02 | PreAuthRecord entity | 🔲 |
| D-03 | ReconciliationRecord entity | 🔲 |
| D-04 | FccConfig entity | 🔲 |
| D-05 | AgentRegistration entity | 🔲 |
| D-06 | AgentTelemetrySnapshot entity | 🔲 |
| D-07 | BootstrapToken entity | 🔲 |
| D-08 | DeadLetterItem entity | 🔲 |
| D-09 | OutboxMessage entity | 🔲 |
| D-10 | AuditEvent entity | 🔲 |
| D-11 | LegalEntity entity | 🔲 |
| D-12 | Site entity | 🔲 |
| D-13 | Pump entity | 🔲 |
| D-14 | Nozzle entity | 🔲 |
| D-15 | Product entity | 🔲 |
| D-16 | Operator entity | 🔲 |
| D-17 | CurrencyHelper (Domain) | 🔲 |
| D-18 | All 23 EF Configurations | 🔲 |

---

## 11. Test Coverage

| # | Test Project | Status |
|---|-------------|--------|
| T-01 | FccMiddleware.Domain.Tests | 🔲 |
| T-02 | FccMiddleware.Application.Tests | 🔲 |
| T-03 | FccMiddleware.Infrastructure.Tests | 🔲 |
| T-04 | FccMiddleware.Api.Tests | 🔲 |
| T-05 | FccMiddleware.UnitTests | 🔲 |
| T-06 | FccMiddleware.IntegrationTests | 🔲 |
| T-07 | FccMiddleware.ArchitectureTests | 🔲 |
| T-08 | FccDesktopAgent.Core.Tests | 🔲 |
| T-09 | FccDesktopAgent.Api.Tests | 🔲 |
| T-10 | FccDesktopAgent.Integration.Tests | 🔲 |
| T-11 | Android JUnit tests | 🔲 |
| T-12 | Portal Jasmine/Karma tests | 🔲 |
| T-13 | Portal Cypress E2E tests | 🔲 |

---

## Summary Counts

| Category | Total Items | Audited | Issues |
|----------|-------------|---------|--------|
| Frontend Routes/Pages | 16 | 16 | 130 |
| Frontend Services | 11 | 0 | 0 |
| Frontend Auth/Security | 6 | 0 | 0 |
| Frontend Shared | 8 | 0 | 0 |
| Backend Endpoints | 65 | 15 | 15 |
| Application Handlers | 25 | 0 | 0 |
| Cloud Adapters | 6 | 0 | 0 |
| Infrastructure Services | 19 | 0 | 0 |
| Background Workers | 6 | 0 | 0 |
| Middleware | 6 | 0 | 0 |
| Desktop Agent Components | ~30 | 0 | 0 |
| Android Agent Components | ~30 | 0 | 0 |
| Domain Entities/Config | 18 | 0 | 0 |
| Test Projects | 13 | 0 | 0 |
| **TOTAL** | **~259** | **31** | **145** |
