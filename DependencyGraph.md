# FCC Middleware — Dependency Graph

## High-Level Architecture

```
┌─────────────┐   ┌─────────────┐   ┌─────────────┐   ┌─────────────┐
│  Angular     │   │  Odoo ERP   │   │ Databricks  │   │ FCC Devices │
│  Portal      │   │             │   │             │   │ (4 vendors) │
└──────┬───────┘   └──────┬──────┘   └──────┬──────┘   └──────┬──────┘
       │PortalBearer       │OdooApiKey       │DatabricksKey    │HMAC/Webhook
       ▼                   ▼                 ▼                 ▼
┌──────────────────────────────────────────────────────────────────────┐
│                    FccMiddleware.Api (ASP.NET Core)                  │
│  Controllers → MediatR → Handlers → Services → DbContext → PG/Redis│
└──────────────────────────────────┬───────────────────────────────────┘
                                   │EdgeAgentDevice JWT
       ┌───────────────────────────┼───────────────────────────┐
       ▼                           ▼                           ▼
┌──────────────┐          ┌──────────────┐          ┌──────────────┐
│ Android Edge │          │ Desktop Edge │          │  Background  │
│ Agent (Kotlin)│         │ Agent (.NET) │          │  Workers     │
└──────┬───────┘          └──────┬───────┘          └──────────────┘
       │ TCP/HTTP/XML              │ TCP/HTTP/XML
       ▼                           ▼
┌──────────────────────────────────────────────────────────────────────┐
│              Forecourt Controllers (DOMS, Radix, Petronite, Advatec)│
└──────────────────────────────────────────────────────────────────────┘
```

---

## Page → API → Handler → Service → Repository → Database

### Dashboard

```
DashboardComponent
├── active-alerts.component
├── agent-status-summary.component
├── ingestion-health.component
├── reconciliation-summary.component
├── stale-transactions.component
└── transaction-volume-chart.component
    │
    ▼
DashboardService
    │
    ├── GET /api/v1/admin/dashboard/summary
    │   └── AdminDashboardController.GetSummary()
    │       └── [Direct DB query via DbContext]
    │           └── PostgreSQL (transactions, agents, reconciliation, dead_letter_items)
    │
    └── GET /api/v1/admin/dashboard/alerts
        └── AdminDashboardController.GetAlerts()
            └── [Direct DB query via DbContext]
                └── PostgreSQL (agent_telemetry_snapshots, portal_settings)
```

### Transactions

```
TransactionListComponent
    │
    ▼
TransactionService
    │
    ├── GET /api/v1/ops/transactions
    │   └── OpsTransactionsController.List()
    │       └── [Direct DB query via DbContext]
    │           └── PostgreSQL (transactions) [partitioned, tenant-filtered]
    │
    ├── GET /api/v1/ops/transactions/{id}
    │   └── OpsTransactionsController.GetById()
    │       └── [Direct DB query via DbContext]
    │           └── PostgreSQL (transactions)
    │
    └── POST /api/v1/ops/transactions/acknowledge
        └── OpsTransactionsController.Acknowledge()
            └── AcknowledgeTransactionsBatchHandler
                ├── IAcknowledgeTransactionsDbContext.FindTransactionsByIdsAsync()
                │   └── PostgreSQL (transactions)
                └── IEventPublisher.Publish(TransactionSyncedToOdoo)
                    └── OutboxMessage → PostgreSQL (outbox_messages)
```

### Transaction Ingestion (FCC Push)

```
FCC Device (DOMS/Radix/Petronite/Advatec)
    │
    ▼
TransactionsController.Ingest[Vendor]()
    │
    ▼
IngestTransactionHandler
    ├── ISiteFccConfigProvider.GetBySiteCode/ByUsnCode/ByWebhookSecret()
    │   └── SiteFccConfigProvider → PostgreSQL (fcc_configs + sites + legal_entities)
    │
    ├── IFccAdapterFactory.Resolve(vendor)
    │   └── FccAdapterFactory → DomsCloudAdapter / RadixCloudAdapter / PetroniteCloudAdapter / AdvatecCloudAdapter
    │       ├── adapter.ValidatePayload() → ValidationResult
    │       └── adapter.NormalizeTransaction() → CanonicalTransaction
    │
    ├── IDeduplicationService.FindExistingAsync()
    │   └── RedisDeduplicationService
    │       ├── Redis (key: dedup:{siteCode}:{fccTxId})
    │       └── PostgreSQL fallback (transactions.fcc_transaction_id)
    │
    ├── IRawPayloadArchiver.ArchiveAsync()
    │   └── S3RawPayloadArchiver → AWS S3 (raw-payloads/{lei}/{site}/{year}/{month}/{txId}.json)
    │
    ├── IIngestDbContext.AddTransaction()
    │   └── PostgreSQL (transactions) [partitioned]
    │
    ├── ReconciliationMatchingService.MatchAsync()
    │   └── IReconciliationDbContext → PostgreSQL (pre_auth_records, reconciliation_records)
    │
    ├── IDeadLetterService.CreateAsync() [on failure]
    │   └── PostgreSQL (dead_letter_items)
    │
    └── IEventPublisher.Publish(TransactionIngested)
        └── OutboxMessage → PostgreSQL (outbox_messages)
```

### Transaction Upload (Edge Agent Batch)

```
Edge Agent
    │
    ▼
TransactionsController.Upload()
    │
    ▼
UploadTransactionBatchHandler
    ├── IDeduplicationService (Redis → PostgreSQL)
    ├── IIngestDbContext → PostgreSQL (transactions)
    ├── ReconciliationMatchingService → PostgreSQL (pre_auth_records, reconciliation_records)
    ├── IDeadLetterService → PostgreSQL (dead_letter_items)
    └── IEventPublisher → PostgreSQL (outbox_messages)
```

### Odoo Polling & Acknowledge

```
Odoo ERP
    │
    ├── GET /api/v1/transactions (poll)
    │   └── PollTransactionsHandler
    │       └── IPollTransactionsDbContext.FetchPendingPageAsync()
    │           └── PostgreSQL (transactions WHERE status='PENDING') [cursor pagination]
    │
    └── POST /api/v1/transactions/acknowledge
        └── AcknowledgeTransactionsBatchHandler
            ├── IAcknowledgeTransactionsDbContext → PostgreSQL (transactions)
            └── IEventPublisher → PostgreSQL (outbox_messages)
```

### Reconciliation

```
ReconciliationListComponent
    │
    ▼
ReconciliationService
    │
    ├── GET /api/v1/ops/reconciliation/exceptions
    │   └── OpsReconciliationController.ListExceptions()
    │       └── GetReconciliationExceptionsHandler
    │           └── IReconciliationDbContext.FetchExceptionsPageAsync()
    │               └── PostgreSQL (reconciliation_records)
    │
    ├── GET /api/v1/ops/reconciliation/{id}
    │   └── OpsReconciliationController.GetById()
    │       └── IReconciliationDbContext.FindByIdAsync()
    │           └── PostgreSQL (reconciliation_records + transactions + pre_auth_records)
    │
    ├── POST /api/v1/ops/reconciliation/{id}/approve
    │   └── ReviewReconciliationHandler
    │       ├── IReconciliationDbContext → PostgreSQL
    │       └── IEventPublisher.Publish(ReconciliationApproved) → outbox_messages
    │
    └── POST /api/v1/ops/reconciliation/{id}/reject
        └── ReviewReconciliationHandler
            ├── IReconciliationDbContext → PostgreSQL
            └── IEventPublisher.Publish(ReconciliationRejected) → outbox_messages
```

### Reconciliation Matching (Internal)

```
ReconciliationMatchingService
    │
    ├── Strategy 1: FccCorrelationId match
    │   └── IReconciliationDbContext.FindCorrelationCandidatesAsync() → PostgreSQL
    │
    ├── Strategy 2: Pump + Nozzle + Time window match
    │   └── IReconciliationDbContext.FindPumpNozzleTimeCandidatesAsync() → PostgreSQL
    │
    └── Strategy 3: OdooOrderId match
        └── IReconciliationDbContext.FindOdooOrderCandidatesAsync() → PostgreSQL
```

### Pre-Authorization

```
Odoo POS → Edge Agent (LAN)
    │
    ▼
PreAuthHandler (Edge Agent)
    ├── Nozzle mapping (local DB)
    ├── FCC Adapter.SendPreAuth() → FCC Device (LAN, ≤1.5s)
    ├── Buffer locally
    └── Async forward to cloud
        │
        ▼
PreAuthCloudForwardWorker → POST /api/v1/preauth
    │
    ▼
ForwardPreAuthHandler
    ├── IPreAuthDbContext.FindByDedupKeyAsync() → PostgreSQL (pre_auth_records)
    ├── IPreAuthDbContext.AddPreAuthRecord() → PostgreSQL
    └── IEventPublisher.Publish(PreAuthCreated) → outbox_messages

PATCH /api/v1/preauth/{id}
    └── UpdatePreAuthStatusHandler
        ├── IPreAuthDbContext → PostgreSQL
        └── IEventPublisher → outbox_messages
```

### Edge Agent Management

```
AgentListComponent / AgentDetailComponent
    │
    ▼
AgentService
    │
    ├── GET /api/v1/agents
    │   └── AgentsController.List() → PostgreSQL (agent_registrations + agent_telemetry_snapshots)
    │
    ├── GET /api/v1/agents/{id}
    │   └── AgentsController.GetById() → PostgreSQL (agent_registrations)
    │
    ├── GET /api/v1/agents/{id}/telemetry
    │   └── AgentsController.GetTelemetry() → PostgreSQL (agent_telemetry_snapshots)
    │
    └── GET /api/v1/agents/{id}/events
        └── AgentsController.GetEvents() → PostgreSQL (audit_events)
```

### Device Registration & Provisioning

```
BootstrapTokenComponent
    │
    ▼
BootstrapTokenService
    │
    ├── POST /api/v1/admin/bootstrap-tokens
    │   └── GenerateBootstrapTokenHandler
    │       └── IRegistrationDbContext → PostgreSQL (bootstrap_tokens) [max 5 active/site]
    │
    └── DELETE /api/v1/admin/bootstrap-tokens/{id}
        └── RevokeBootstrapTokenHandler
            └── IRegistrationDbContext → PostgreSQL (bootstrap_tokens)

Edge Agent (QR scan)
    │
    ├── POST /api/v1/agent/register
    │   └── RegisterDeviceHandler
    │       ├── IRegistrationDbContext → PostgreSQL (bootstrap_tokens, agent_registrations, sites)
    │       ├── IAgentConfigDbContext → PostgreSQL (fcc_configs)
    │       └── IDeviceTokenService → JWT generation
    │
    ├── POST /api/v1/agent/token/refresh
    │   └── RefreshDeviceTokenHandler
    │       ├── IRegistrationDbContext → PostgreSQL (device_refresh_tokens, agent_registrations)
    │       └── IDeviceTokenService → JWT generation
    │
    ├── GET /api/v1/agent/config
    │   └── GetAgentConfigHandler
    │       └── IAgentConfigDbContext → PostgreSQL (sites, fcc_configs, pumps, nozzles, products)
    │
    ├── POST /api/v1/agent/telemetry
    │   └── SubmitTelemetryHandler
    │       └── ITelemetryDbContext → PostgreSQL (agent_telemetry_snapshots, audit_events)
    │
    └── POST /api/v1/agent/diagnostic-logs
        └── SubmitDiagnosticLogsHandler
            └── IDiagnosticLogsDbContext → PostgreSQL (agent_diagnostic_logs)
```

### Site Configuration

```
SiteConfigComponent / SiteDetailComponent
    │
    ▼
SiteService
    │
    ├── GET /api/v1/sites → PostgreSQL (sites)
    ├── GET /api/v1/sites/{id} → PostgreSQL (sites + fcc_configs + pumps + nozzles)
    ├── PATCH /api/v1/sites/{id} → PostgreSQL (sites)
    ├── PUT /api/v1/sites/{id}/fcc-config → PostgreSQL (fcc_configs) [AES-256-GCM encrypted fields]
    ├── GET /api/v1/sites/{id}/pumps → PostgreSQL (pumps + nozzles)
    ├── POST /api/v1/sites/{id}/pumps → PostgreSQL (pumps + nozzles)
    ├── DELETE /api/v1/sites/{id}/pumps/{pumpId} → PostgreSQL (pumps) [soft delete]
    └── PATCH /api/v1/sites/{id}/pumps/{pumpId}/nozzles/{n} → PostgreSQL (nozzles)
```

### Master Data Sync

```
Databricks
    │
    ▼
MasterDataController (Databricks API Key auth)
    │
    ├── PUT /api/v1/master-data/legal-entities → SyncLegalEntitiesHandler → PostgreSQL (legal_entities)
    ├── PUT /api/v1/master-data/sites → SyncSitesHandler → PostgreSQL (sites)
    ├── PUT /api/v1/master-data/pumps → SyncPumpsHandler → PostgreSQL (pumps + nozzles)
    ├── PUT /api/v1/master-data/products → SyncProductsHandler → PostgreSQL (products) + outbox_messages
    └── PUT /api/v1/master-data/operators → SyncOperatorsHandler → PostgreSQL (operators)

MasterDataBrowserController (PortalUser auth)
    │
    ├── GET /api/v1/master-data/legal-entities → PostgreSQL (legal_entities)
    ├── GET /api/v1/master-data/products → PostgreSQL (products)
    └── GET /api/v1/master-data/sync-status → PostgreSQL (aggregate query)
```

### Dead Letter Queue

```
DlqListComponent / DlqDetailComponent
    │
    ▼
DlqService
    │
    ├── GET /api/v1/dlq → PostgreSQL (dead_letter_items)
    ├── GET /api/v1/dlq/{id} → PostgreSQL (dead_letter_items)
    ├── POST /api/v1/dlq/{id}/retry
    │   └── DlqReplayService
    │       ├── Reconstruct IngestTransactionCommand
    │       ├── Re-run through IngestTransactionHandler
    │       │   └── Full ingestion pipeline (adapter → dedup → persist → reconcile)
    │       └── Update dead_letter_items (RESOLVED or REPLAY_FAILED)
    ├── POST /api/v1/dlq/{id}/discard → PostgreSQL (dead_letter_items → DISCARDED)
    ├── POST /api/v1/dlq/retry-batch → DlqReplayService (per item)
    └── POST /api/v1/dlq/discard-batch → PostgreSQL (dead_letter_items)
```

### Audit Log

```
AuditLogComponent / AuditDetailComponent
    │
    ▼
AuditService
    │
    ├── GET /api/v1/audit/events → PostgreSQL (audit_events) [partitioned, cursor pagination]
    └── GET /api/v1/audit/events/{id} → PostgreSQL (audit_events)
```

### Settings

```
SettingsComponent
    │
    ▼
SettingsService
    │
    ├── GET /api/v1/admin/settings → PostgreSQL (portal_settings + legal_entity_settings_overrides)
    ├── PUT /api/v1/admin/settings/global-defaults → PostgreSQL (portal_settings)
    ├── PUT /api/v1/admin/settings/overrides/{id} → PostgreSQL (legal_entity_settings_overrides)
    ├── DELETE /api/v1/admin/settings/overrides/{id} → PostgreSQL (legal_entity_settings_overrides)
    └── PUT /api/v1/admin/settings/alerts → PostgreSQL (portal_settings)
```

---

## Background Worker Dependencies

```
OutboxPublisherWorker (5s)
    └── PostgreSQL (outbox_messages → audit_events) [future: → AWS SNS]

ArchiveWorker (1h)
    ├── PostgresPartitionManager → PostgreSQL (pg_inherits, partition DDL)
    ├── Raw SQL partition reads → PostgreSQL
    ├── Parquet.Net → Parquet serialization
    └── ArchiveObjectStore → AWS S3 (archives/)

PreAuthExpiryWorker (60s)
    ├── PostgreSQL (pre_auth_records WHERE status IN ('PENDING','AUTHORIZED','DISPENSING') AND expires_at < now)
    ├── IFccPumpDeauthorizationAdapter → FCC Device (for DISPENSING records)
    └── IEventPublisher → outbox_messages

StaleTransactionWorker (15min)
    ├── PostgreSQL (transactions WHERE status='PENDING' AND is_stale=false AND created_at < threshold)
    ├── IEventPublisher → outbox_messages
    └── IObservabilityMetrics → CloudWatch EMF

UnmatchedReconciliationWorker (60s)
    ├── PostgreSQL (reconciliation_records WHERE status='UNMATCHED')
    ├── ReconciliationMatchingService → PostgreSQL (pre_auth_records)
    └── IEventPublisher → outbox_messages

MonitoringSnapshotWorker (5min)
    ├── PostgreSQL (agent_telemetry_snapshots, transactions)
    └── IObservabilityMetrics → CloudWatch EMF
```

---

## Edge Agent Internal Dependencies

### Android Edge Agent

```
EdgeAgentForegroundService
    │
    ├── IngestionOrchestrator
    │   ├── FccAdapterFactory → DOMS/Radix/Petronite/Advatec adapter
    │   │   └── FCC Device (TCP/HTTP/XML over LAN)
    │   └── TransactionBufferManager
    │       └── Room Database (SQLite)
    │
    ├── CloudUploadWorker
    │   ├── TransactionBufferManager → Room Database
    │   └── Cloud API → POST /api/v1/transactions/upload
    │
    ├── PreAuthCloudForwardWorker
    │   ├── Room Database (buffered_pre_auths)
    │   └── Cloud API → POST /api/v1/preauth
    │
    ├── PreAuthHandler
    │   ├── FCC Adapter → FCC Device (LAN)
    │   └── Room Database (pre_auth_records, nozzle_mappings)
    │
    ├── ConfigManager
    │   ├── Cloud API → GET /api/v1/agent/config
    │   └── Room Database (agent_config_record)
    │
    ├── ConnectivityManager
    │   └── Android NetworkCallback
    │
    └── Local Ktor API Server (:8585)
        └── Odoo POS (LAN client)
```

### Desktop Edge Agent

```
FccDesktopAgent.Core
    │
    ├── FCC Adapters (same 4 vendors, .NET implementations)
    │   └── FCC Device (TCP/HTTP/XML over LAN)
    │
    ├── TransactionBufferManager
    │   └── EF Core + SQLite (WAL mode)
    │
    ├── ConfigPollWorker
    │   └── Cloud API → GET /api/v1/agent/config
    │
    └── PreAuthHandler
        ├── FCC Adapter → FCC Device (LAN)
        └── SQLite (nozzle_mappings)
```

---

## Data Flow Summary

```
                    Master Data
                    (Databricks)
                        │
                        ▼ PUT /master-data/*
┌─────────┐    ┌──────────────────┐    ┌─────────┐
│ FCC     │───▶│  Cloud Backend   │◀───│ Odoo    │
│ Devices │push│                  │poll│ ERP     │
│         │    │  PostgreSQL (PG) │ack │         │
└─────────┘    │  Redis (cache)   │    └─────────┘
     ▲         │  S3 (archive)    │         ▲
     │ LAN     │  CloudWatch      │         │
     │         └────────┬─────────┘         │
     │                  │                   │
     │           ┌──────┴──────┐            │
     │           │             │            │
     ▼           ▼             ▼            │
┌─────────┐ ┌─────────┐  ┌─────────┐      │
│ Android │ │ Desktop │  │ Angular │      │
│ Edge    │ │ Edge    │  │ Portal  │      │
│ Agent   │ │ Agent   │  │ (SPA)   │      │
└────┬────┘ └────┬────┘  └─────────┘      │
     │           │                         │
     └─────┬─────┘                         │
           │ Local Ktor/Kestrel API (:8585)│
           ▼                               │
     ┌───────────┐                         │
     │ Odoo POS  │─────────────────────────┘
     │ (on-site) │
     └───────────┘
```

---

## Database Tables (PostgreSQL)

| Table | Partitioned | Key Indexes |
|-------|------------|-------------|
| transactions | Monthly (created_at) | ix_dedup (fcc_tx_id, site_code) UNIQUE, ix_odoo_poll (lei, status, created_at) partial, ix_portal_search (lei, site, created_at), ix_reconciliation (site, pump, completed_at) partial, ix_stale (status, is_stale, created_at) partial |
| pre_auth_records | No | (odoo_order_id, site_code) UNIQUE |
| reconciliation_records | No | (transaction_id), (status, created_at) |
| audit_events | Monthly (created_at) | (lei, created_at), (correlation_id) |
| outbox_messages | No | (processed_at) partial WHERE NULL |
| agent_registrations | No | (site_id), (lei) |
| device_refresh_tokens | No | (device_id), (token_hash) |
| bootstrap_tokens | No | (token_hash), (site_code, lei, status) |
| fcc_configs | No | (site_id), (usn_code), (advatec_webhook_token_hash) |
| sites | No | (site_code) UNIQUE, (lei) |
| legal_entities | No | — |
| pumps | No | (site_id, pump_number) |
| nozzles | No | (pump_id) |
| products | No | (lei, product_code) |
| operators | No | (lei) |
| dead_letter_items | No | (lei, status, created_at) |
| agent_telemetry_snapshots | No | (device_id) |
| agent_diagnostic_logs | No | (device_id) |
| portal_settings | No | — |
| legal_entity_settings_overrides | No | (lei) |
| odoo_api_keys | No | (key_hash) |
| databricks_api_keys | No | (key_hash) |

---

## External System Integrations

| System | Direction | Protocol | Auth | Purpose |
|--------|-----------|----------|------|---------|
| Azure Entra ID | Inbound | OIDC/JWT | MSAL | Portal user authentication |
| Odoo ERP | Outbound (poll) | REST | API Key | Transaction delivery |
| Databricks | Inbound | REST | API Key | Master data sync |
| AWS S3 | Outbound | HTTPS | IAM | Raw payload archive, partition export |
| AWS KMS | Outbound | HTTPS | IAM | Server-side encryption keys |
| AWS CloudWatch | Outbound | EMF logs | IAM | Metrics & observability |
| DOMS FCC | Bidirectional | TCP (JPL) / REST | Access code / API key | Transaction fetch, pre-auth, pump status |
| Radix FCC | Bidirectional | HTTP (XML) | SHA-1 signature | Transaction push, pre-auth |
| Petronite FCC | Bidirectional | REST (JSON) | OAuth2 CC | Webhook push, pre-auth |
| Advatec FCC | Bidirectional | REST (JSON) | Webhook token | Webhook push, pre-auth |
| Seq (dev) | Outbound | HTTP | — | Development log aggregation |
