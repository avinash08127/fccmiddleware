# FCC Middleware — Module Inventory

## Project Structure

```
src/
├── portal/                                    # Angular 20 SPA
├── cloud/
│   ├── FccMiddleware.Api/                     # ASP.NET Core Web API
│   ├── FccMiddleware.Application/             # MediatR handlers & services
│   ├── FccMiddleware.Domain/                  # Entities, enums, interfaces
│   ├── FccMiddleware.Infrastructure/          # EF Core, Redis, S3, workers
│   ├── FccMiddleware.Infrastructure.Tests/    # Infrastructure tests
│   ├── FccMiddleware.Adapter.Advatec/         # Advatec cloud adapter
│   ├── FccMiddleware.Adapter.Doms/            # DOMS cloud adapter
│   ├── FccMiddleware.Adapter.Petronite/       # Petronite cloud adapter
│   └── FccMiddleware.Adapter.Radix/           # Radix cloud adapter
├── edge-agent/                                # Android Edge Agent (Kotlin)
└── desktop-edge-agent/                        # Desktop Edge Agent (.NET)
```

---

## Frontend Modules

### Core (`src/portal/src/app/core/`)

| Module | Files | Purpose |
|--------|-------|---------|
| **auth/** | auth.guard.ts, role.guard.ts, auth-state.ts | MSAL auth, role guard, auth state |
| **interceptors/** | api.interceptor.ts | HTTP error handling, base URL |
| **layout/** | shell.component.ts | Main shell (nav, header, router-outlet) |
| **models/** | 11 model files | TypeScript interfaces for all API entities |
| **services/** | 11 service files + http-params.util.ts | API clients, logging, utilities |

### Features (`src/portal/src/app/features/`)

| Feature | Components | Routes | Description |
|---------|------------|--------|-------------|
| **dashboard/** | DashboardComponent + 6 widgets (active-alerts, agent-status-summary, ingestion-health, reconciliation-summary, stale-transactions, transaction-volume-chart) | `/dashboard` | Operational overview with KPIs |
| **transactions/** | TransactionListComponent, TransactionDetailComponent, TransactionFiltersComponent, TransactionsComponent | `/transactions/list`, `/transactions/:id` | Transaction browser & detail |
| **reconciliation/** | ReconciliationListComponent, ReconciliationDetailComponent, ReconciliationFiltersComponent, ReconciliationComponent | `/reconciliation/exceptions`, `/reconciliation/exceptions/:id` | Exception review & approval |
| **edge-agents/** | AgentListComponent, AgentDetailComponent, BootstrapTokenComponent, EdgeAgentsComponent | `/agents`, `/agents/:id`, `/agents/bootstrap-token` | Device monitoring & provisioning |
| **site-config/** | SiteConfigComponent, SiteDetailComponent, FccConfigFormComponent, PumpMappingComponent | `/sites/list`, `/sites/:id` | Site & FCC configuration |
| **master-data/** | MasterDataComponent | `/master-data/status` | Sync status dashboard |
| **audit-log/** | AuditLogComponent, AuditDetailComponent | `/audit/list`, `/audit/events/:id` | Audit trail browser |
| **dlq/** | DlqComponent, DlqListComponent, DlqDetailComponent | `/dlq/list`, `/dlq/items/:id` | Dead-letter queue management |
| **settings/** | SettingsComponent | `/settings` | Global system settings (admin) |

### Shared (`src/portal/src/app/shared/`)

| Type | Items |
|------|-------|
| **Components** | DataTableComponent, DateRangePickerComponent, EmptyStateComponent, LoadingSpinnerComponent, StatusBadgeComponent, AccessDeniedComponent |
| **Pipes** | CurrencyMinorUnitsPipe, StatusLabelPipe, UtcDatePipe |
| **Directives** | RoleVisibleDirective |

### Models (`src/portal/src/app/core/models/`)

| File | Key Types |
|------|-----------|
| agent.model.ts | AgentRegistration, AgentTelemetry, AgentHealthSummary, ConnectivityState, AgentRegistrationStatus |
| transaction.model.ts | Transaction, TransactionDetail, TransactionQueryParams, FccVendor, TransactionStatus, IngestionSource |
| reconciliation.model.ts | ReconciliationRecord, ReconciliationException, ReconciliationDecision, ReconciliationQueryParams |
| site.model.ts | Site, SiteDetail, FccConfig, Pump, Nozzle, SiteConfig (full agent config), SiteOperatingModel, ConnectivityMode, IngestionMode |
| dlq.model.ts | DeadLetter, DeadLetterDetail, DeadLetterType, DeadLetterStatus, DeadLetterReason |
| audit.model.ts | AuditEvent, AuditEventQueryParams |
| common.model.ts | PagedResult\<T\>, ErrorResponse |
| settings.model.ts | SystemSettings, GlobalDefaults, LegalEntityOverride, AlertConfiguration |
| master-data.model.ts | LegalEntity, MasterDataSyncStatus |
| pre-auth.model.ts | PreAuthRecord, PreAuthStatus, PreAuthQueryParams |
| bootstrap-token.model.ts | GenerateBootstrapTokenRequest/Response |

---

## Backend Modules

### FccMiddleware.Api

| Category | Files | Purpose |
|----------|-------|---------|
| **Controllers/** | 14 controllers | REST API endpoints |
| **Auth/** | FccHmacAuthHandler, OdooApiKeyAuthHandler, DatabricksApiKeyAuthHandler | Custom auth scheme handlers |
| **Infrastructure/** | CorrelationIdMiddleware, GlobalExceptionHandlerMiddleware, DeviceActiveCheckMiddleware, TenantScopeMiddleware, HealthResponseWriter | Request pipeline middleware |
| **Portal/** | PortalAccessResolver | Portal policy resolution |
| **Program.cs** | — | App bootstrap, DI, middleware, health checks |

### FccMiddleware.Application

| Category | Handlers | Purpose |
|----------|----------|---------|
| **Ingestion/** | IngestTransactionHandler, UploadTransactionBatchHandler | Transaction intake pipeline |
| **Transactions/** | PollTransactionsHandler, GetSyncedTransactionIdsHandler, AcknowledgeTransactionsBatchHandler | Odoo integration |
| **Registration/** | GenerateBootstrapTokenHandler, RevokeBootstrapTokenHandler, RegisterDeviceHandler, RefreshDeviceTokenHandler, DecommissionDeviceHandler | Device lifecycle |
| **PreAuth/** | ForwardPreAuthHandler, UpdatePreAuthStatusHandler | Pre-authorization |
| **Reconciliation/** | ReviewReconciliationHandler, GetReconciliationExceptionsHandler, ReconciliationMatchingService | Variance management |
| **MasterData/** | SyncProductsHandler, SyncOperatorsHandler, SyncPumpsHandler, SyncLegalEntitiesHandler, SyncSitesHandler | Data synchronization |
| **Telemetry/** | SubmitTelemetryHandler, CheckAgentVersionHandler, GetAgentConfigHandler | Agent management |
| **DiagnosticLogs/** | SubmitDiagnosticLogsHandler, GetDiagnosticLogsHandler | Diagnostic log management |

### FccMiddleware.Domain

| Category | Items | Purpose |
|----------|-------|---------|
| **Entities/** | Transaction, PreAuthRecord, ReconciliationRecord, AgentRegistration, DeviceRefreshToken, BootstrapToken, FccConfig, Site, LegalEntity, Pump, Nozzle, Product, Operator, DeadLetterItem, AuditEvent, OutboxMessage, AgentTelemetrySnapshot, AgentDiagnosticLog | Domain entities |
| **Enums/** | TransactionStatus, PreAuthStatus, ReconciliationStatus, FccVendor, IngestionMethod, IngestionMode, IngestionSource, FiscalizationMode, DeadLetterType, DeadLetterReason, DeadLetterStatus, ProvisioningTokenStatus, AgentRegistrationStatus, ConnectionProtocol, SiteOperatingModel, ConnectivityState | Domain enumerations |
| **Interfaces/** | IFccAdapter, IFccPumpDeauthorizationAdapter, IFccAdapterFactory, IEventPublisher, ICurrentTenantProvider, ITenantScoped | Contracts |
| **Models/Adapter/** | CanonicalTransaction, RawPayloadEnvelope, ValidationResult, TransactionBatch, FetchCursor, AdapterInfo, SiteFccConfig | Adapter models |
| **Common/** | CurrencyHelper, Result\<T\>, Error | Shared utilities |
| **Events/** | 17 domain event types | Domain events |

### FccMiddleware.Infrastructure

| Category | Items | Purpose |
|----------|-------|---------|
| **Persistence/** | FccMiddlewareDbContext (22 DbSets, 11 interface implementations), TenantContext, PostgresPartitionManager | Database access |
| **Persistence/Configurations/** | 19 entity configuration files | EF Core fluent configuration |
| **Repositories/** | SiteFccConfigProvider | Site config resolution |
| **Adapters/** | FccAdapterFactory, CloudFccAdapterFactoryRegistration | Adapter factory + DI |
| **Deduplication/** | RedisDeduplicationService | Two-tier dedup (Redis → PostgreSQL) |
| **DeadLetter/** | DeadLetterService, DlqReplayService | DLQ capture + replay |
| **Events/** | OutboxEventPublisher, OutboxPublisherWorker | Transactional outbox |
| **Storage/** | S3RawPayloadArchiver, ArchiveObjectStore | S3/local storage |
| **Workers/** | ArchiveWorker, PreAuthExpiryWorker, StaleTransactionWorker, UnmatchedReconciliationWorker, MonitoringSnapshotWorker | Background processing |
| **Observability/** | CloudWatchEmfMetricSink | Metrics emission |
| **Security/** | AesGcmFieldEncryptor, EncryptedFieldConverter | Field-level encryption |
| **Resilience/** | HttpClientResilienceExtensions | Retry + circuit breaker |

### Cloud Adapters

| Project | Main Class | Internal DTOs | Protocol |
|---------|-----------|---------------|----------|
| FccMiddleware.Adapter.Advatec | AdvatecCloudAdapter | AdvatecReceiptData, AdvatecWebhookEnvelope | JSON webhook (TRA fiscal) |
| FccMiddleware.Adapter.Doms | DomsCloudAdapter | DomsTransactionDto, DomsListResponse | JSON REST |
| FccMiddleware.Adapter.Petronite | PetroniteCloudAdapter | PetroniteTransactionDto, PetroniteWebhookPayload | JSON webhook |
| FccMiddleware.Adapter.Radix | RadixCloudAdapter | RadixTransactionDto, RadixSignatureHelper | XML + SHA-1 |

---

## Edge Agent Modules

### Android Edge Agent (`src/edge-agent/`)

| Package | Key Classes | Purpose |
|---------|-------------|---------|
| **adapter/advatec/** | AdvatecAdapter | Advatec FCC via HTTP + webhook |
| **adapter/doms/jpl/** | DomsJplAdapter, JplTcpClient, JplHeartbeatManager | DOMS via persistent TCP/JPL |
| **adapter/petronite/** | PetroniteAdapter | Petronite via REST + OAuth2 |
| **adapter/radix/** | RadixAdapter, RadixPushListener, RadixXmlParser | Radix via HTTP + XML |
| **adapter/common/** | FccAdapterFactory, AdapterTypes, AdapterTimeouts, PumpStatusSynthesizer, CurrencyHelper | Shared adapter infrastructure |
| **buffer/** | TransactionBufferManager | Room DB transaction buffer |
| **buffer/dao/** | TransactionBufferDao | Room DAO for buffer operations |
| **config/** | ConfigManager, SiteConfig, DesktopFccRuntimeConfiguration | Config management with StateFlow |
| **sync/** | CloudUploadWorker, PreAuthCloudForwardWorker | Cloud synchronization |
| **ingestion/** | IngestionOrchestrator | FCC polling + normalization + buffering |
| **preauth/** | PreAuthHandler | LAN-only pre-auth processing |
| **ui/** | SplashActivity, LauncherActivity, ProvisioningActivity, DiagnosticsActivity, SettingsActivity, DecommissionedActivity | Android UI |

### Desktop Edge Agent (`src/desktop-edge-agent/`)

| Namespace | Key Classes | Purpose |
|-----------|-------------|---------|
| **Adapter/Advatec/** | AdvatecAdapter, AdvatecApiClient, AdvatecFiscalizationService | Advatec FCC integration |
| **Adapter/Doms/** | DomsAdapter, DomsJplAdapter | DOMS FCC integration |
| **Adapter/Petronite/** | PetroniteAdapter | Petronite FCC integration |
| **Adapter/Radix/** | RadixAdapter, RadixPushListener, RadixXmlParser | Radix FCC integration |
| **Adapter/Common/** | FccAdapterFactory, AdapterTypes, CurrencyHelper | Shared adapter infrastructure |
| **Buffer/** | TransactionBufferManager | EF Core + SQLite buffer |
| **Config/** | SiteConfig, DesktopFccRuntimeConfiguration | Configuration models |

---

## External Dependencies

### Frontend (package.json)

| Package | Version | Purpose |
|---------|---------|---------|
| @angular/* | 20.3.0 | Core framework |
| @azure/msal-angular | 5.1.1 | Azure Entra ID auth |
| @azure/msal-browser | 5.4.0 | MSAL core |
| primeng | 20.4.0 | UI component library |
| chart.js | 4.5.1 | Dashboard charts |
| qrcode | 1.5.4 | Bootstrap token QR |
| rxjs | 7.8.0 | Reactive streams |

### Backend (NuGet)

| Package | Version | Purpose |
|---------|---------|---------|
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.0 | PostgreSQL provider |
| StackExchange.Redis | 2.11.8 | Redis client |
| AWSSDK.S3 | 4.0.6.7 | S3 storage |
| Parquet.Net | 5.3.0 | Parquet serialization |
| Serilog | — | Structured logging |
| Polly | — | Resilience policies |
| Microsoft.EntityFrameworkCore | 10.0.0 | ORM |
