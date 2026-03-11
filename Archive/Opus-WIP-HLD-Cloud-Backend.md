# Forecourt Middleware — Cloud Backend High Level Design

**Status:** WIP (Work in Progress)
**Version:** 0.1
**Date:** 2026-03-10
**Author:** Architecture Review — Opus

---

# 1. Overview

## 1.1 Purpose

The Cloud Backend is the central middleware platform that ingests fuel dispensing transactions from Forecourt Controllers (FCCs) across 2,000 retail fuel stations in 12 African countries, deduplicates and normalizes them into a canonical model, reconciles pre-authorized transactions, and exposes them for Odoo ERP to poll and create orders. It is the single source of truth for transaction state across the platform.

## 1.2 Business Context

Fuel retail operations in sub-Saharan Africa face unreliable internet connectivity. FCCs sit on station LANs and must continue dispensing fuel regardless of cloud availability. The cloud backend receives transactions from two paths — direct FCC push and Edge Agent catch-up upload — and must deduplicate transparently. It never pushes orders into Odoo; Odoo polls and creates orders on its own terms. This pull-based model keeps Odoo as the order master and avoids coupling the middleware into Odoo's transactional lifecycle.

## 1.3 Major Responsibilities

| Responsibility | Description |
|----------------|-------------|
| Transaction Ingestion | Receive transactions via FCC push and Edge Agent upload; acknowledge before processing |
| Deduplication | Primary key dedup (`fccTransactionId` + `siteCode`) with secondary fuzzy matching |
| Payload Normalization | Adapter-based translation from vendor-specific formats to canonical model |
| Pre-Auth Record Storage | Store pre-auth records forwarded by Edge Agents for reconciliation matching |
| Reconciliation | Match final dispense to pre-auth; calculate amount variance; flag exceptions |
| Odoo Transaction Polling API | Expose PENDING transactions for Odoo to poll; accept acknowledgments; mark SYNCED_TO_ODOO |
| Edge Agent Sync | Publish SYNCED_TO_ODOO status; push configuration; accept telemetry; version checks |
| Master Data Sync | Receive legal entity, site, pump/nozzle, product, and operator data from Databricks |
| Audit Trail | Publish immutable events to event bus; persist event store for 7-year retention |
| Multi-Tenancy | Row-level data isolation by legal entity; all APIs scoped to tenant context |
| Configuration Management | Store FCC-specific settings, ingestion modes, fiscalization overrides, tolerance thresholds |

## 1.4 Boundaries and Exclusions

- The cloud backend does **not** create orders in Odoo — Odoo polls and creates orders itself.
- The cloud backend does **not** communicate directly with FCCs for pre-auth commands — that is the Edge Agent's responsibility over LAN.
- The cloud backend does **not** manage Odoo master data — Databricks pipelines own that synchronization.
- The cloud backend does **not** manage the Android HHT fleet — Sure MDM handles device management.
- Direct pump control is never performed by the cloud backend.

---

# 2. Design Goals

| Goal | Rationale |
|------|-----------|
| **Configuration-Driven** | 2,000 sites across 12 countries with different FCCs, fiscalization rules, operating modes, and ingestion patterns. Behaviour must be driven by configuration — not code per site. |
| **Resilience to Dual-Path Ingestion** | In CLOUD_DIRECT mode, the same transaction arrives via FCC push AND Edge Agent catch-up. The system must handle this transparently via idempotent dedup. |
| **Pull-Based Odoo Integration** | Odoo controls when orders are created. The middleware stores and waits. This decouples the two systems and avoids pushing into Odoo's transactional model. |
| **Adapter Extensibility** | DOMS is the MVP vendor. Radix, Advatec, and Petronite follow in Phase 3. Each adapter is a separate code module behind a common interface. |
| **Multi-Country Readiness** | 12 legal entities from day one. All data, APIs, and business rules scoped by legal entity. Row-level isolation in a shared database. |
| **Operational Transparency** | 2,000 remote stations in Africa. Operations managers need clear visibility into transaction flow, reconciliation exceptions, agent health, and sync status — without visiting sites. |
| **Security** | This platform authorizes fuel dispensing, which is a financial operation. Authentication, authorization, encryption, and audit logging are non-negotiable. |
| **Practical MVP Delivery** | Avoid over-engineering. A modular monolith is faster to build, deploy, and debug than distributed microservices for a team starting fresh. |

---

# 3. Functional Scope

## 3.1 Key Features

1. **Transaction Ingestion Pipeline** — Push endpoint for FCC-direct transactions; upload endpoint for Edge Agent catch-up/relay; pull worker for cloud-side FCC polling (where applicable).
2. **Deduplication Engine** — Primary key match (silent skip) + secondary fuzzy match (flag for review).
3. **Adapter Framework** — Pluggable vendor adapters for payload normalization. DOMS adapter for MVP.
4. **Pre-Auth Record Management** — Receive and store pre-auth records from Edge Agents. Expiry tracking. Matching engine for final dispense correlation.
5. **Reconciliation Engine** — Match dispense to pre-auth. Calculate amount variance. Auto-approve within tolerance. Flag exceptions.
6. **Odoo Polling API** — Expose `PENDING` transactions. Accept acknowledgments. Mark `SYNCED_TO_ODOO`.
7. **Edge Agent Sync** — SYNCED_TO_ODOO status endpoint. Configuration push. Version compatibility check. Telemetry ingestion.
8. **Master Data Sync API** — Idempotent upsert endpoints for Databricks pipeline. Validation. Freshness tracking.
9. **Audit Event Publishing** — Immutable event stream for all transaction lifecycle events.
10. **Error Handling and DLQ** — Exponential backoff retry. Dead-letter queue for exhausted retries. Alerting.
11. **Multi-Tenancy** — Legal entity scoping on all data and APIs.

## 3.2 Major Use Cases

| Use Case | Actors | Flow |
|----------|--------|------|
| Normal Order Ingestion (Online) | FCC, Edge Agent | FCC pushes to cloud; Edge Agent uploads catch-up; cloud deduplicates; stores as PENDING |
| Normal Order Ingestion (Recovery) | Edge Agent | Internet restored; Edge Agent uploads buffered transactions; cloud deduplicates against FCC push |
| Pre-Auth Record Receipt | Edge Agent | Edge Agent forwards pre-auth record; cloud stores for reconciliation matching |
| Pre-Auth Reconciliation | Cloud Backend | Dispense arrives from FCC; matched to pre-auth; variance calculated; stored as PENDING |
| Odoo Order Creation | Odoo | Odoo polls PENDING transactions; creates orders; acknowledges back; cloud marks SYNCED_TO_ODOO |
| Edge Agent SYNCED_TO_ODOO Sync | Edge Agent | Agent polls cloud for SYNCED_TO_ODOO status; updates local buffer |
| Master Data Sync | Databricks | Databricks pushes legal entities, sites, pumps, products, operators to cloud |
| Reconciliation Review | Ops Manager | Reviews flagged variances and unmatched transactions via portal |
| Transaction Investigation | Ops Manager | Queries transactions by site, time, status; views audit trail |

## 3.3 Supported Operational Scenarios

- **CLOUD_DIRECT**: FCC pushes to cloud (primary); Edge Agent catch-up as safety net.
- **RELAY**: Edge Agent is primary receiver; relays to cloud in real-time; buffers if offline.
- **BUFFER_ALWAYS**: Edge Agent always buffers locally; syncs on schedule.
- **Mixed Mode Sites**: Both Normal Orders and Pre-Auth at the same site.
- **Multi-HHT Sites**: Multiple Edge Agents; only primary communicates with FCC; all poll cloud.
- **Connected/Disconnected**: Connected sites have FCC; disconnected sites operate manually in Odoo.

---

# 4. Architecture Overview

## 4.1 Recommended Architecture Style

**Modular Monolith** with clean vertical module boundaries, deployed as containerized services on AWS ECS Fargate.

**Rationale:**
- Faster to build and deploy for MVP than distributed microservices.
- Single deployment unit simplifies debugging across modules (ingestion → dedup → reconciliation → Odoo sync).
- Module boundaries enforce separation of concerns and enable future decomposition if needed.
- The team can focus on domain logic rather than distributed systems infrastructure.
- Internal module communication via in-process method calls and MediatR — no inter-service latency for the critical transaction processing pipeline.

**Future Path:** If specific modules need independent scaling (e.g., ingestion under high load), they can be extracted as separate services behind the same API contracts. The modular structure makes this tractable.

## 4.2 Main Components

```
                           ┌─────────────────────────────────────┐
                           │          API Gateway / ALB          │
                           │    (TLS termination, WAF, routing)  │
                           └──────────┬──────────────────────────┘
                                      │
                    ┌─────────────────┼──────────────────┐
                    ▼                 ▼                   ▼
          ┌─────────────────┐ ┌──────────────┐  ┌───────────────────┐
          │   FCC Ingest    │ │  Odoo Sync   │  │   Admin / Portal  │
          │   API           │ │  API         │  │   API             │
          │                 │ │              │  │                   │
          │ POST /ingest    │ │ GET /pending │  │ Query, config,    │
          │ (FCC push)      │ │ POST /ack    │  │ reconciliation,   │
          │                 │ │              │  │ monitoring        │
          └────────┬────────┘ └──────┬───────┘  └────────┬──────────┘
                   │                 │                    │
          ┌────────┴─────────────────┴────────────────────┴──────────┐
          │                                                          │
          │              APPLICATION LAYER (MediatR)                 │
          │                                                          │
          │  ┌──────────┐ ┌───────────┐ ┌────────────┐ ┌─────────┐  │
          │  │Ingestion │ │ PreAuth   │ │Reconcilia- │ │ OdooSync│  │
          │  │Module    │ │ Module    │ │tion Module │ │ Module  │  │
          │  └────┬─────┘ └─────┬─────┘ └─────┬──────┘ └────┬────┘  │
          │       │             │              │             │       │
          │  ┌────┴─────┐ ┌────┴──────┐ ┌─────┴──────┐ ┌────┴────┐  │
          │  │Dedup     │ │EdgeSync   │ │ Config     │ │ Audit   │  │
          │  │Engine    │ │Module     │ │ Module     │ │ Module  │  │
          │  └──────────┘ └───────────┘ └────────────┘ └─────────┘  │
          │                                                          │
          └──────────────┬───────────────────────┬───────────────────┘
                         │                       │
                ┌────────┴────────┐     ┌────────┴────────┐
                │  PostgreSQL     │     │   RabbitMQ      │
                │  (Aurora)       │     │   (Amazon MQ)   │
                │                 │     │                 │
                │ Transactions    │     │ Domain Events   │
                │ Pre-Auth        │     │ Retry Queues    │
                │ Reconciliation  │     │ DLQ             │
                │ Config          │     │                 │
                │ Audit Events    │     │                 │
                └─────────────────┘     └─────────────────┘
                         │
                ┌────────┴────────┐     ┌─────────────────┐
                │  Redis          │     │  S3              │
                │  (ElastiCache)  │     │                  │
                │                 │     │ Raw payloads     │
                │ Dedup cache     │     │ Audit archive    │
                │ Config cache    │     │ Portal static    │
                │ Rate limiting   │     │                  │
                └─────────────────┘     └─────────────────┘
```

Additionally, a **Background Worker Host** runs alongside the API:

```
          ┌────────────────────────────────────────────┐
          │           WORKER HOST (ECS Fargate)        │
          │                                            │
          │  ┌──────────────┐  ┌────────────────────┐  │
          │  │ FCC Poll     │  │ Pre-Auth Expiry    │  │
          │  │ Workers      │  │ Worker             │  │
          │  │ (cloud-side  │  │                    │  │
          │  │  pull mode)  │  │                    │  │
          │  └──────────────┘  └────────────────────┘  │
          │                                            │
          │  ┌──────────────┐  ┌────────────────────┐  │
          │  │ Retry        │  │ Stale Transaction  │  │
          │  │ Consumer     │  │ Alert Worker       │  │
          │  │ (RabbitMQ)   │  │                    │  │
          │  └──────────────┘  └────────────────────┘  │
          │                                            │
          │  ┌──────────────┐  ┌────────────────────┐  │
          │  │ Event        │  │ Audit Archive      │  │
          │  │ Publisher    │  │ Worker (S3)        │  │
          │  └──────────────┘  └────────────────────┘  │
          └────────────────────────────────────────────┘
```

## 4.3 Module Decomposition

### Ingestion Module
- **Push Endpoint**: Receives FCC-direct transactions (`POST /api/v1/transactions/ingest`). API-key authenticated per FCC.
- **Upload Endpoint**: Receives Edge Agent catch-up/relay/buffer-sync batches (`POST /api/v1/transactions/upload`). Device-token authenticated.
- **Pull Worker**: For cloud-side pull mode — polls FCC at configured interval (where FCC is cloud-reachable and configured for PULL). Tracks cursor per FCC.
- **Dedup Check**: Looks up `fccTransactionId + siteCode` in Redis cache → PostgreSQL. Primary match = silent skip. Secondary fuzzy match = flag.
- **Normalization**: Delegates to the appropriate FCC adapter based on `fccVendor`. Adapter produces canonical model. Raw payload preserved to S3.
- **Persist**: Stores canonical transaction with status `PENDING`.

### Pre-Auth Module
- **Receive Endpoint**: Accepts pre-auth records forwarded by Edge Agents (`POST /api/v1/preauth`).
- **State Machine**: Tracks pre-auth states (PENDING → AUTHORIZED → DISPENSING → COMPLETED / CANCELLED / EXPIRED / FAILED).
- **Expiry Worker**: Transitions pre-auths past the configurable timeout to EXPIRED.
- **Matching Index**: Maintains lookup structures for efficient dispense-to-preauth matching (correlation ID, pump+nozzle+time window).

### Reconciliation Module
- **Matching Engine**: When a dispense transaction arrives at a pre-auth site, attempts to match to an existing pre-auth record.
- **Variance Calculator**: `actualAmount - authorizedAmount`. Checks configurable tolerance (e.g., +/-2%).
- **Auto-Approve / Flag**: Within tolerance = auto-approve; exceeds = flag for Ops Manager review.
- **Reconciliation Record**: Creates immutable reconciliation record regardless of outcome.
- **Unmatched Handler**: Dispenses at pre-auth sites without a matching pre-auth are flagged as UNMATCHED but still stored as PENDING for Odoo.

### Odoo Sync Module
- **Poll Endpoint**: `GET /api/v1/transactions?status=PENDING` — returns normalized transactions paginated, filterable by legal entity, site, time range.
- **Acknowledge Endpoint**: `POST /api/v1/transactions/acknowledge` — accepts list of `fccTransactionId` values with corresponding `odooOrderId`; marks `SYNCED_TO_ODOO`.
- **Stale Alert Worker**: Flags transactions PENDING beyond configurable threshold (e.g., 7 days) as `STALE_PENDING`.

### Edge Sync Module
- **SYNCED_TO_ODOO Status Endpoint**: `GET /api/v1/transactions/synced-status?since={timestamp}` — returns transaction IDs confirmed as SYNCED_TO_ODOO. Edge Agents poll this.
- **Config Endpoint**: `GET /api/v1/agent/config?siteCode={code}` — returns current FCC config, ingestion mode, poll intervals, etc.
- **Version Check**: `GET /api/v1/agent/version-check?version={v}` — returns compatibility status.
- **Telemetry Endpoint**: `POST /api/v1/agent/telemetry` — accepts agent health data (FCC status, buffer depth, battery, storage, app version).
- **Agent Registration**: `POST /api/v1/agent/register` — registers a new Edge Agent device for a site.

### Configuration Module
- **Legal Entity Config**: Country, currency, timezone, fiscalization defaults. Read-only (synced from Databricks).
- **Site Config**: Operating mode, connectivity mode, operator details. Read-only (synced from Databricks).
- **FCC Config**: Vendor, connection details, transaction mode, ingestion mode, pull interval. Partially synced, partially admin-configured.
- **Pump/Nozzle Mapping**: Physical-to-logical pump and product mappings per FCC.
- **Fiscalization Overrides**: Site-level overrides of legal entity defaults.
- **Tolerance Settings**: Reconciliation variance tolerance per legal entity or site.
- **Product Code Mappings**: FCC vendor product codes to canonical codes per FCC.

### Audit Module
- **Event Publisher**: Publishes domain events to RabbitMQ (e.g., TransactionReceived, TransactionDeduplicated, PreAuthAuthorized, ReconciliationFlagged).
- **Event Store**: Persists all events to an append-only table in PostgreSQL.
- **Archive Worker**: Periodically archives older events to S3 for long-term retention (7-year regulatory requirement).
- **Query API**: Events queryable by correlation ID, site, type, and time range.

### Master Data Sync Module
- **Sync API**: `POST /api/v1/sync/legal-entities`, `/sync/sites`, `/sync/pumps`, `/sync/products`, `/sync/operators`.
- **Idempotent Upsert**: Re-syncing the same data produces no side effects.
- **Validation**: Required field checks (e.g., operator TIN for CODO/DODO sites). Rejects invalid records with descriptive errors.
- **Freshness Tracking**: Updates `syncedAt` timestamp. Alerts if data goes stale beyond threshold.

## 4.4 Key Runtime Flows

### Normal Order — CLOUD_DIRECT (Primary Path)

```
FCC → POST /api/v1/transactions/ingest (API key)
  → Adapter: resolve FCC vendor from siteCode → normalize payload
  → Dedup: check fccTransactionId + siteCode → skip if exists
  → Persist: store canonical transaction, status=PENDING
  → S3: archive raw payload
  → Event: publish TransactionReceived, TransactionNormalized
  → Response: 202 Accepted (acknowledge before heavy processing)
```

### Edge Agent Catch-Up Upload

```
Edge Agent → POST /api/v1/transactions/upload (device token, batch)
  → For each transaction in batch:
    → Adapter: normalize (same pipeline as direct push)
    → Dedup: likely already exists from FCC push → silent skip
    → Persist if new
  → Response: 200 OK with per-transaction status (created/skipped)
```

### Odoo Poll and Acknowledge

```
Odoo → GET /api/v1/transactions?status=PENDING&legalEntityId={id} (API key)
  → Returns paginated list of PENDING transactions
  → Odoo creates orders in its own system
Odoo → POST /api/v1/transactions/acknowledge (API key)
  → Body: [{fccTransactionId, odooOrderId}, ...]
  → Cloud marks each as SYNCED_TO_ODOO
  → Event: publish OdooOrderCreated for each
```

---

# 5. Project Structure Recommendation

## 5.1 Repository Strategy

**Single repository** (monorepo) for the cloud backend. The modular monolith deploys as 2 container images (API host + worker host) from the same codebase.

## 5.2 Recommended Solution Structure

```
fcc-middleware-cloud/
│
├── src/
│   ├── FccMiddleware.Api/                        # ASP.NET Core Web API host
│   │   ├── Controllers/                          # API controllers (thin — delegates to Application layer)
│   │   │   ├── TransactionIngestController.cs
│   │   │   ├── TransactionPollController.cs
│   │   │   ├── PreAuthController.cs
│   │   │   ├── AgentSyncController.cs
│   │   │   ├── MasterDataSyncController.cs
│   │   │   └── AdminController.cs
│   │   ├── Middleware/                            # Auth, tenant resolution, exception handling, correlation ID
│   │   ├── Filters/
│   │   └── Program.cs
│   │
│   ├── FccMiddleware.Domain/                      # Core domain — no external dependencies
│   │   ├── Transactions/                          # Transaction aggregate, canonical model, states
│   │   ├── PreAuth/                               # Pre-auth aggregate, state machine
│   │   ├── Reconciliation/                        # Reconciliation records, variance logic
│   │   ├── Configuration/                         # Legal entity, site, FCC, pump/nozzle entities
│   │   ├── Adapters/                              # IFccAdapter interface, adapter registry
│   │   ├── Events/                                # Domain event definitions
│   │   └── Common/                                # Value objects (SiteCode, FccTransactionId, Money, Volume)
│   │
│   ├── FccMiddleware.Application/                 # Use cases — orchestrates domain logic
│   │   ├── Ingestion/                             # IngestTransactionCommand, NormalizeTransactionHandler
│   │   ├── Deduplication/                         # DeduplicateTransactionHandler
│   │   ├── PreAuth/                               # ReceivePreAuthCommand, ExpirePreAuthCommand
│   │   ├── Reconciliation/                        # ReconcileDispenseCommand
│   │   ├── OdooSync/                              # GetPendingTransactionsQuery, AcknowledgeTransactionsCommand
│   │   ├── EdgeSync/                              # GetSyncedStatusQuery, GetAgentConfigQuery
│   │   ├── MasterData/                            # UpsertSitesCommand, UpsertLegalEntitiesCommand
│   │   └── Common/                                # Interfaces (ITransactionRepository, IEventPublisher, etc.)
│   │
│   ├── FccMiddleware.Infrastructure/              # External concerns
│   │   ├── Persistence/                           # EF Core DbContext, migrations, repositories
│   │   │   ├── FccMiddlewareDbContext.cs
│   │   │   ├── Configurations/                    # Entity type configurations (Fluent API)
│   │   │   ├── Repositories/
│   │   │   └── Migrations/
│   │   ├── Messaging/                             # RabbitMQ publisher/consumer via MassTransit
│   │   ├── Caching/                               # Redis cache implementations
│   │   ├── Storage/                               # S3 raw payload archival
│   │   └── Telemetry/                             # OpenTelemetry configuration
│   │
│   ├── FccMiddleware.Workers/                     # Background worker host
│   │   ├── FccPollWorker.cs                       # Cloud-side FCC polling (for PULL mode)
│   │   ├── PreAuthExpiryWorker.cs
│   │   ├── StaleTransactionAlertWorker.cs
│   │   ├── RetryConsumer.cs                       # RabbitMQ retry consumer
│   │   ├── AuditArchiveWorker.cs
│   │   └── Program.cs
│   │
│   ├── Adapters/
│   │   ├── FccMiddleware.Adapter.Doms/            # DOMS FCC adapter (MVP)
│   │   │   ├── DomsAdapter.cs                     # Implements IFccAdapter
│   │   │   ├── DomsPayloadParser.cs
│   │   │   ├── DomsProtocolClient.cs              # For cloud-side PULL mode
│   │   │   └── DomsFieldMapping.cs
│   │   ├── FccMiddleware.Adapter.Radix/           # Phase 3
│   │   ├── FccMiddleware.Adapter.Advatec/         # Phase 3
│   │   └── FccMiddleware.Adapter.Petronite/       # Phase 3
│   │
│   ├── FccMiddleware.Contracts/                   # Shared API contracts / DTOs
│   │   ├── Api/                                   # Request/response DTOs
│   │   ├── Events/                                # Integration event contracts
│   │   └── Canonical/                             # Canonical transaction model (shared with Edge Agent team)
│   │
│   └── FccMiddleware.ServiceDefaults/             # Cross-cutting configuration
│       ├── OpenTelemetryExtensions.cs
│       ├── HealthCheckExtensions.cs
│       └── AuthenticationExtensions.cs
│
├── tests/
│   ├── FccMiddleware.UnitTests/
│   │   ├── Ingestion/
│   │   ├── Deduplication/
│   │   ├── Reconciliation/
│   │   └── PreAuth/
│   ├── FccMiddleware.IntegrationTests/            # Tests against real PostgreSQL + Redis + RabbitMQ (Testcontainers)
│   └── FccMiddleware.Adapter.Doms.Tests/          # DOMS adapter-specific tests with sample payloads
│
├── infra/                                         # Infrastructure as Code
│   ├── terraform/                                 # or AWS CDK
│   └── docker/
│       ├── Dockerfile.api
│       └── Dockerfile.worker
│
└── docs/
```

## 5.3 Design Rationale

| Decision | Rationale |
|----------|-----------|
| Domain / Application / Infrastructure split | Clean Architecture ensures domain logic is testable without infrastructure. Adapters and persistence are swappable. |
| Separate Adapter projects per FCC vendor | Each adapter is independently testable and deployable. Adding a new vendor is a new project implementing `IFccAdapter`. |
| Separate API and Worker hosts | API serves HTTP requests. Workers run background jobs. Same codebase, different entry points. Can scale independently. |
| Contracts project | Shared between Cloud Backend and Portal (API clients). Can be published as a NuGet package for Edge Agent team reference. |
| MediatR for command/query dispatch | Decouples controllers from use case handlers. Enables pipeline behaviours (logging, validation, transaction management). |

---

# 6. Integration View

## 6.1 External Systems

```
┌──────────────────────────────────────────────────────────────────┐
│                        CLOUD BACKEND                              │
│                                                                    │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────┐         │
│  │  Ingest API  │    │  Odoo API    │    │  Admin API   │         │
│  └──────┬───────┘    └──────┬───────┘    └──────┬───────┘         │
│         │                   │                   │                  │
└─────────┼───────────────────┼───────────────────┼──────────────────┘
          │                   │                   │
    ┌─────┴──────┐     ┌─────┴──────┐     ┌──────┴──────┐
    │            │     │            │     │             │
┌───┴────┐ ┌────┴───┐ │   Odoo     │ ┌───┴──────┐ ┌───┴──────┐
│  FCC   │ │ Edge   │ │   ERP      │ │ Angular  │ │ Azure    │
│(Push)  │ │ Agent  │ │(Poll+Ack)  │ │ Portal   │ │ Entra    │
└────────┘ │(Upload,│ └────────────┘ └──────────┘ └──────────┘
           │PreAuth,│
           │Telemetry)│       ┌────────────┐
           └─────────┘       │ Databricks │
                              │ (Master    │
                              │  Data Sync)│
                              └────────────┘
```

## 6.2 API Surface

| API | Auth Method | Consumer | Description |
|-----|------------|----------|-------------|
| `POST /api/v1/transactions/ingest` | API Key (per FCC) | FCC | Direct FCC push endpoint |
| `POST /api/v1/transactions/upload` | Device Token | Edge Agent | Batch upload (catch-up, relay, buffer sync) |
| `GET /api/v1/transactions` | API Key (Odoo) | Odoo | Poll PENDING transactions |
| `POST /api/v1/transactions/acknowledge` | API Key (Odoo) | Odoo | Acknowledge processed transactions |
| `POST /api/v1/preauth` | Device Token | Edge Agent | Forward pre-auth record for reconciliation |
| `GET /api/v1/transactions/synced-status` | Device Token | Edge Agent | Poll SYNCED_TO_ODOO transaction IDs |
| `GET /api/v1/agent/config` | Device Token | Edge Agent | Fetch current configuration |
| `GET /api/v1/agent/version-check` | Device Token | Edge Agent | Check agent version compatibility |
| `POST /api/v1/agent/telemetry` | Device Token | Edge Agent | Report health metrics |
| `POST /api/v1/agent/register` | Provisioning Token | Edge Agent | Register new device |
| `POST /api/v1/sync/{entity}` | Service Principal | Databricks | Master data upsert |
| `GET/POST /api/v1/admin/*` | Azure Entra JWT | Angular Portal | Configuration, monitoring, reconciliation queries |

## 6.3 Messaging / Events

| Exchange / Queue | Publisher | Consumer | Pattern |
|-----------------|-----------|----------|---------|
| `transaction.events` | Ingestion Module | Audit Module, future consumers | Fanout — all transaction lifecycle events |
| `ingestion.retry` | Ingestion Module | Retry Worker | Delayed retry with exponential backoff |
| `ingestion.dlq` | Retry Worker | Ops Manager (via portal) | Dead-letter queue for exhausted retries |
| `preauth.events` | PreAuth Module | Audit Module | Pre-auth lifecycle events |
| `reconciliation.events` | Reconciliation Module | Audit Module, Alert Worker | Reconciliation outcomes including flagged variances |
| `master-data.events` | Sync Module | Config cache invalidation | Cache invalidation on master data changes |

## 6.4 Sync Patterns

| Pattern | Where Used | Detail |
|---------|-----------|--------|
| **Push (Webhook)** | FCC → Cloud | FCC pushes transactions. Cloud acknowledges before processing (at-least-once). |
| **Pull (Polling)** | Cloud → FCC | Cloud-side pull worker polls FCC at interval (for PULL-mode FCCs reachable from cloud). Tracks cursor. |
| **Upload (Batch POST)** | Edge Agent → Cloud | Edge Agent uploads catch-up/buffered transactions in configurable batch size (e.g., 50). |
| **Poll (Consumer-Driven)** | Odoo → Cloud | Odoo polls PENDING transactions on schedule or manual trigger. |
| **Status Poll** | Edge Agent → Cloud | Edge Agent polls SYNCED_TO_ODOO status every ~30 seconds when online. |
| **Config Pull** | Edge Agent → Cloud | Agent checks for config updates on each cloud sync cycle. |
| **Master Data Push** | Databricks → Cloud | Databricks pushes master data on schedule or change. |

## 6.5 Retry and Idempotency

| Concern | Strategy |
|---------|----------|
| FCC push retry | Cloud acknowledges with 202 immediately. If internal processing fails, transaction goes to retry queue. |
| Edge Agent upload retry | Agent retries with exponential backoff. Cloud deduplicates on redelivery. |
| Odoo acknowledge retry | Acknowledge endpoint is idempotent — re-acknowledging an already-acknowledged transaction is a no-op. |
| Databricks sync retry | Upsert is idempotent by design — re-syncing same data has no effect. |
| Dead-letter | Transactions exhausting retries go to DLQ. Ops Manager can inspect and manually retry via portal. |

---

# 7. Security Architecture

## 7.1 Authentication

| Actor | Method | Details |
|-------|--------|---------|
| **Portal Users (Employees)** | Azure Entra (OIDC / OAuth 2.0) | Employees authenticate via Azure Entra. Portal uses MSAL.js. API validates JWT tokens issued by Azure Entra. |
| **Odoo** | API Key | Odoo authenticates with a static API key scoped to its legal entity. API key stored in AWS Secrets Manager. Rotated periodically. |
| **FCC (Push)** | API Key | Each FCC has a unique API key provisioned during setup. Included in `X-Api-Key` header or Basic Auth. |
| **Edge Agent** | Device Token | Each device receives a token during provisioning (QR code or registration). Token is a signed JWT or opaque token validated by the cloud. |
| **Databricks** | Service Principal / API Key | Databricks service account authenticates to the sync API. Scoped to master data write operations only. |

## 7.2 Authorization (RBAC)

Since this is employees-only, a role-based model aligned to the documented system roles:

| Role | Permissions |
|------|-------------|
| **System Administrator** | Full configuration access. Cross-legal-entity access. FCC management. Agent management. |
| **Operations Manager** | Read all transactions within assigned legal entities. Reconciliation actions. Manual retry. Alert management. |
| **Site Supervisor** | Read transactions for assigned sites. View Edge Agent health. Trigger manual pulls. |
| **Read-Only Auditor** | Read-only access to transactions, audit trail, and reconciliation records across assigned legal entities. |

Roles are managed as Azure Entra app roles or groups. Role claims are included in the JWT and enforced by the API via policy-based authorization.

**Legal Entity Scoping:** All API queries automatically filter by the user's assigned legal entity unless the user has the System Administrator role. This is enforced at the query layer via a tenant filter applied as a global query filter in EF Core.

## 7.3 Secrets Management

| Secret | Storage | Rotation |
|--------|---------|----------|
| Database connection string | AWS Secrets Manager | Rotated via Aurora IAM auth or manual rotation |
| Redis connection string | AWS Secrets Manager | Managed by ElastiCache |
| RabbitMQ credentials | AWS Secrets Manager | Manual rotation |
| FCC API keys | AWS Secrets Manager → loaded to DB encrypted | Per-FCC, rotatable via admin portal |
| Edge Agent device tokens | Signed JWTs (cloud issues, cloud validates) | Token refresh on each cloud sync |
| Odoo API key | AWS Secrets Manager | Manual rotation, coordinated with Odoo team |
| Azure Entra client secret | AWS Secrets Manager | Rotated per Azure Entra policy |

## 7.4 Encryption

| Scope | Approach |
|-------|----------|
| **In Transit** | TLS 1.2+ on all endpoints. ALB terminates TLS with ACM-managed certificates. Internal VPC traffic between services also encrypted (ECS service mesh or VPC-internal TLS). |
| **At Rest** | Aurora PostgreSQL: AWS-managed encryption (AES-256). ElastiCache: encryption at rest enabled. S3: SSE-S3 or SSE-KMS. RabbitMQ: Amazon MQ encryption at rest. |
| **FCC Credentials** | Encrypted column in the database using application-level encryption (AES-256-GCM) with a KMS-managed key. Never logged or returned in API responses. |

## 7.5 Audit Logging

- All API requests logged with: timestamp, actor identity, action, resource, legal entity context, IP, result.
- All transaction state changes logged as immutable domain events.
- Authentication events (login, token refresh, failed attempts) logged via Azure Entra audit logs + cloud middleware access logs.
- Audit logs retained for 7 years (regulatory requirement).
- Audit logs are append-only and cannot be modified or deleted by any role.

## 7.6 Tenant / Site Isolation

- **Row-level isolation**: All tables include `legalEntityId`. A global EF Core query filter ensures all queries are scoped.
- **API-level enforcement**: Tenant context extracted from authentication token. Requests cannot cross tenant boundaries unless System Administrator.
- **Edge Agent isolation**: Each device token is bound to a specific `siteCode` and `legalEntityId`. Agent cannot access data from other sites.
- **FCC API key isolation**: Each FCC API key is bound to a `siteCode`. Transactions from an FCC are validated against the registered site.

## 7.7 Trust Boundaries

```
┌─────────────────────── PUBLIC INTERNET ───────────────────────────┐
│                                                                    │
│  [FCC]  ─── TLS + API Key ──►  [ALB/WAF]  ──► [Cloud Backend]    │
│  [Edge Agent] ─ TLS + Device Token ─►  [ALB/WAF]  ──►  [Cloud]   │
│  [Odoo] ─── TLS + API Key ──►  [ALB/WAF]  ──►  [Cloud Backend]   │
│  [Portal] ─── TLS + Entra JWT ──►  [ALB/WAF]  ──►  [Cloud]       │
│  [Databricks] ─── TLS + Service Principal ──►  [ALB/WAF]  ──►    │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘

┌─────────────── AWS VPC (PRIVATE) ─────────────────────────────────┐
│                                                                    │
│  [Cloud Backend API]  ──►  [Aurora PostgreSQL] (private subnet)   │
│  [Cloud Backend API]  ──►  [ElastiCache Redis] (private subnet)   │
│  [Cloud Backend API]  ──►  [Amazon MQ RabbitMQ] (private subnet)  │
│  [Cloud Backend API]  ──►  [S3] (VPC endpoint)                    │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

---

# 8. Deployment Architecture

## 8.1 Recommended Deployment Model

**AWS — Single Region (af-south-1, Cape Town)** — closest AWS region to the 12 African countries of operation. Provides the lowest latency for FCC push, Edge Agent sync, and Odoo polling.

> **Note:** If af-south-1 does not have all required services (Amazon MQ, etc.), fall back to eu-west-1 (Ireland) which has full service availability and reasonable latency to Africa.

## 8.2 Cloud Deployment Topology

```
                    ┌──────────────────────────┐
                    │       CloudFront          │
                    │  (Angular Portal CDN)     │
                    └─────────┬────────────────┘
                              │
                    ┌─────────┴────────────────┐
                    │     Application Load      │
                    │     Balancer (ALB)        │
                    │     + AWS WAF             │
                    │     + ACM Certificate     │
                    └──┬──────────────────┬────┘
                       │                  │
              ┌────────┴──────┐  ┌────────┴──────┐
              │  ECS Fargate  │  │  ECS Fargate  │
              │  API Service  │  │  Worker Svc   │
              │  (2+ tasks)   │  │  (2+ tasks)   │
              │               │  │               │
              │  .NET 10      │  │  .NET 10      │
              │  ASP.NET Core │  │  Background   │
              └───┬───┬───┬───┘  └───┬───┬───┬───┘
                  │   │   │          │   │   │
       ┌──────────┘   │   └──────┐   │   │   │
       ▼              ▼          ▼   │   │   │
  ┌─────────┐  ┌───────────┐  ┌─────┴───┴───┴──┐
  │ Aurora   │  │ElastiCache│  │   Amazon MQ    │
  │PostgreSQL│  │  Redis    │  │  (RabbitMQ)    │
  │ (Multi-  │  │ (cluster) │  │               │
  │  AZ)     │  │           │  │               │
  └──────────┘  └───────────┘  └───────────────┘
       │
  ┌────┴───────┐
  │     S3     │
  │ Raw payload│
  │ Audit arch.│
  │ Portal     │
  └────────────┘
```

## 8.3 Environment Strategy

| Environment | Purpose | Infrastructure |
|-------------|---------|----------------|
| **Development** | Developer testing | Single Fargate task. Shared dev Aurora. Localstack or real AWS services. |
| **Staging** | Pre-production validation | Production-like. Smaller instance sizes. Synthetic test data. |
| **Production** | Live operations | Full HA. Multi-AZ Aurora. Multi-AZ Amazon MQ. ECS auto-scaling. |

## 8.4 HA / DR Considerations

| Component | HA Strategy |
|-----------|-------------|
| **API Service** | 2+ ECS tasks across AZs. ALB health checks. Auto-scaling on CPU/memory/request count. |
| **Worker Service** | 2+ ECS tasks. RabbitMQ ensures only one consumer processes each message. |
| **Aurora PostgreSQL** | Multi-AZ with automatic failover. Read replicas for portal read queries (if needed). |
| **ElastiCache Redis** | Multi-AZ cluster mode. Automatic failover. |
| **Amazon MQ** | Multi-AZ active/standby broker. |
| **S3** | 99.999999999% durability by default. Cross-region replication if required. |
| **DR Strategy** | Aurora: automated backups + point-in-time recovery (35-day retention). S3: versioning enabled. RabbitMQ: messages are durable. Full region failover is post-MVP. |

## 8.5 Scaling Approach

**Scale targets (derived from requirements):**
- 2,000 sites × up to 1,000 txns/day = up to 2,000,000 txns/day = ~23 txns/second average, ~100 txns/second peak burst.
- NFR-3: Support 100 concurrent sites × 10 txns/min = ~17 txns/second sustained.
- These are modest numbers for a .NET API backed by Aurora. Horizontal auto-scaling is post-MVP.

| Component | Scaling Strategy |
|-----------|-----------------|
| **API Service** | Start with 2 Fargate tasks. Auto-scale on CPU (target 60%) or request count for peak periods. |
| **Worker Service** | 2 tasks. Scale based on RabbitMQ queue depth. |
| **Aurora** | Start with `db.r6g.large`. Scale vertically. Add read replicas for portal queries if needed. |
| **Redis** | Start with `cache.r6g.large`. Scale vertically. |

## 8.6 Observability

| Pillar | Implementation |
|--------|---------------|
| **Logging** | Structured JSON logging via Serilog. Shipped to CloudWatch Logs. Queryable via CloudWatch Insights or forwarded to a log aggregator (e.g., Grafana Loki). |
| **Metrics** | OpenTelemetry metrics: transaction ingestion rate, dedup hit rate, reconciliation outcomes, Odoo poll frequency, Edge Agent sync lag, error rates. Exported to CloudWatch Metrics or Prometheus. |
| **Tracing** | OpenTelemetry distributed tracing. Correlation IDs flow from FCC → ingestion → dedup → reconciliation → Odoo sync. Exported to AWS X-Ray or Grafana Tempo. |
| **Health Checks** | `/health/ready` (all dependencies reachable), `/health/live` (process alive). Used by ALB and ECS for routing and restart. |
| **Dashboards** | CloudWatch dashboards or Grafana for: transaction throughput, error rates, queue depths, Edge Agent health, reconciliation exception rates, Odoo sync lag. |
| **Alerting** | CloudWatch Alarms or Grafana alerting. Critical alerts: ingestion failures > threshold, DLQ depth > 0, Aurora storage > 80%, Edge Agent offline > configurable duration. |

---

# 9. Key Design Decisions

## 9.1 Architectural Choices

| Decision | Choice | Rationale | Trade-off |
|----------|--------|-----------|-----------|
| **Architecture style** | Modular monolith | Faster MVP delivery. Simpler deployment and debugging. Modules can be extracted later. | Less independent scalability than microservices. Shared database and process. |
| **Odoo integration model** | Odoo polls middleware (pull-based) | Keeps Odoo as order master. Avoids tight coupling. Middleware is a transaction store, not an Odoo client. | Latency between dispense and Odoo order creation depends on Odoo poll interval. |
| **Deduplication strategy** | Primary key (silent skip) + secondary fuzzy (flag) | Primary key dedup handles the expected dual-path arrival cleanly. Secondary fuzzy catches edge cases without false-positive auto-skipping. | Secondary fuzzy matches require human review — operational overhead. |
| **Event publishing** | Selective event streaming (not full event sourcing) | Publish domain events for audit and downstream consumption. Not full CQRS/ES — simpler to implement and reason about. | Cannot replay full state from events alone. State is in PostgreSQL. |
| **Multi-tenancy** | Row-level isolation in shared database | Simpler for 12 tenants. Lower infrastructure cost. EF Core global query filters enforce isolation. | Noisy-neighbour risk (mitigated by scale — 12 tenants is manageable). Database-per-tenant migration path exists if needed. |
| **FCC adapter pattern** | Code-level adapters (one project per vendor) | Vendor protocols are structurally different. Configuration alone cannot handle new vendors. Code-level adapters provide full control. | Adding a new FCC vendor requires a code change and deployment — not just configuration. This is an intentional design choice per requirements. |
| **Authentication** | Azure Entra for employees; API keys for system integrations; device tokens for Edge Agents | Different trust levels and capabilities for different actors. Azure Entra provides enterprise SSO. API keys are practical for FCC hardware and Odoo integration. | Multiple auth schemes add complexity. Mitigated by using ASP.NET Core's policy-based auth to compose them. |
| **Cloud region** | AWS af-south-1 (Cape Town) | Closest region to operating countries. Lowest latency for FCC push and Edge Agent sync. | af-south-1 has fewer services than eu-west-1. May need fallback. Must verify Amazon MQ availability. |

## 9.2 Assumptions

1. Odoo will implement the poll + acknowledge pattern against the middleware API. The Odoo team owns this integration.
2. Databricks pipelines for master data sync already exist or will be built by the data team. The middleware exposes the sync API.
3. FCC vendors (starting with DOMS) have documented protocols for push and/or pull. Protocol documentation is available.
4. Azure Entra tenant is already provisioned. Employee accounts and groups/roles exist.
5. Each FCC has a unique `fccTransactionId` that is stable across retries and dual-path delivery.
6. Station LAN is reliably available independent of internet connectivity (confirmed in requirements).
7. The Urovo i9100 running the Edge Agent has sufficient storage for 30,000+ transactions in SQLite.

## 9.3 Known Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| **FCC push endpoint availability** | If the cloud ingestion endpoint is slow or down, FCCs may queue internally or drop transactions. | Edge Agent catch-up poll provides a safety net. Acknowledge before processing (202 pattern). ALB health checks and auto-scaling. |
| **Odoo polling lag** | If Odoo fails to poll regularly, PENDING transactions accumulate and may go stale. | Stale transaction alerting (configurable threshold). Manual bulk-create available. |
| **Cross-cloud auth (Azure Entra + AWS)** | Added complexity. Token validation, key rotation, potential latency for JWKS fetch. | Cache JWKS keys. Use standard libraries (Microsoft.Identity.Web). Well-established pattern. |
| **Database growth** | 2M transactions/day × 7-year retention = billions of rows. | Table partitioning by `legalEntityId` and time. Archive older transactions to S3. Implement retention-aware queries. |
| **DOMS protocol unknowns** | Adapter development depends on DOMS protocol documentation quality. | Plan for a PoC/spike on DOMS integration early in Phase 1. |

## 9.4 Areas Needing Validation / PoC

1. **DOMS Protocol Integration** — Obtain DOMS documentation. Build a PoC adapter against a test FCC or simulator.
2. **af-south-1 Service Availability** — Verify Amazon MQ, ElastiCache, and Aurora are available in af-south-1.
3. **Odoo Poll + Acknowledge Implementation** — Validate that the Odoo team can implement the polling and acknowledge pattern.
4. **Azure Entra Token Validation on AWS** — Validate JWT validation performance and JWKS caching.
5. **Transaction Volume Stress Test** — Simulate 100 txns/second burst to validate ingestion pipeline throughput.

---

# 10. Non-Functional Requirements Mapping

| NFR | Target | HLD Approach |
|-----|--------|-------------|
| **NFR-1: Availability (99.5%)** | ~43.8 hours downtime/year | Multi-AZ deployment. ALB health checks. ECS auto-restart. Aurora automatic failover. No single points of failure. |
| **NFR-2: Latency (Pre-auth <5s)** | Pre-auth round-trip < 5s cloud processing | Pre-auth record receipt is a simple write — well under 5s. The actual pre-auth command goes Edge Agent → FCC over LAN (not through cloud). |
| **NFR-3: Throughput (100 sites × 10 txns/min)** | ~17 txns/sec sustained | .NET on Fargate with Aurora easily handles this. Start with 2 tasks. Auto-scale if needed. |
| **NFR-4: Data Retention (7 years)** | Regulatory compliance | PostgreSQL for active data (configurable window, e.g., 1-2 years). S3 archive for long-term. Partitioned tables for efficient queries. |
| **NFR-5: Security** | OAuth 2.0 / API Keys / TLS / Encryption at rest | Azure Entra (employees), API keys (integrations), device tokens (agents). TLS 1.2+ everywhere. Aurora + S3 + Redis encryption at rest. |
| **NFR-6: Scalability** | Horizontal scaling (post-MVP) | Modular monolith can be decomposed. ECS auto-scaling available. Aurora read replicas available. |
| **NFR-7: Observability** | Structured logging, distributed tracing, health checks | OpenTelemetry + Serilog + CloudWatch. Correlation IDs across all flows. Health check endpoints. |
| **NFR-8: Recovery** | No data loss on restart | Durable RabbitMQ messaging. PostgreSQL WAL. ECS restarts preserve no in-memory state (all state in DB/queue). |

---

# 11. Recommended Technology Direction

| Concern | Technology | Rationale |
|---------|-----------|-----------|
| **Runtime** | .NET 10, ASP.NET Core | Per requirements. Mature, high-performance, excellent for API workloads. |
| **ORM** | Entity Framework Core 10 | Productive. Supports global query filters (multi-tenancy). Strongly-typed migrations. |
| **CQRS / Mediator** | MediatR | Lightweight command/query dispatch. Pipeline behaviours for cross-cutting concerns. |
| **Validation** | FluentValidation | Expressive validation rules for incoming payloads. Integrates with MediatR pipeline. |
| **Messaging** | MassTransit + RabbitMQ (Amazon MQ) | MassTransit provides retry, DLQ, saga support over RabbitMQ. Well-proven in .NET ecosystem. |
| **Caching** | StackExchange.Redis | Standard .NET Redis client. Used for dedup cache, config cache, rate limiting. |
| **Object Storage** | AWSSDK.S3 | Raw payload archival and long-term audit storage. |
| **Authentication** | Microsoft.Identity.Web + custom API key middleware | Azure Entra JWT validation + API key validation + device token validation as composable auth schemes. |
| **Observability** | OpenTelemetry .NET SDK + Serilog + AWS X-Ray / Grafana | Structured logging, distributed tracing, metrics export. |
| **Testing** | xUnit + Testcontainers + NSubstitute | Unit tests with mocks. Integration tests with real PostgreSQL, Redis, RabbitMQ in Docker. |
| **IaC** | Terraform or AWS CDK (.NET) | Infrastructure as code for reproducible deployments. |
| **CI/CD** | GitHub Actions | Build, test, push Docker images to ECR, deploy to ECS. |
| **API Documentation** | Swashbuckle (Swagger / OpenAPI) | Auto-generated API docs from controllers. Shared with Odoo team and Edge Agent team. |
| **Health Checks** | ASP.NET Core Health Checks | Built-in. Checks Aurora, Redis, RabbitMQ, S3 connectivity. |
| **Background Jobs** | .NET BackgroundService + MassTransit Consumers | BackgroundService for scheduled workers. MassTransit consumers for message-driven workers. |
| **Serialization** | System.Text.Json | High-performance JSON. Used for API payloads and event serialization. |

---

# 12. Open Questions / Pending Decisions

| ID | Question | Impact | Assumption Made |
|----|----------|--------|-----------------|
| OQ-BE-1 | Is AWS af-south-1 (Cape Town) the confirmed target region? Are all required services (Amazon MQ, Aurora, ElastiCache) available there? | Deployment architecture | Assumed af-south-1 with eu-west-1 fallback. Needs validation. |
| OQ-BE-2 | What is the DOMS push protocol? REST? TCP? SOAP? What does the payload look like? | Adapter design, ingestion endpoint | Assumed REST/JSON based on the JSON rawPayload field in requirements. Needs DOMS documentation. |
| OQ-BE-3 | Does Odoo have an existing polling/webhook mechanism, or does the Odoo team need to build the poll + acknowledge integration from scratch? | Odoo integration timeline | Assumed Odoo team builds this. Cloud backend just exposes the API. |
| OQ-BE-4 | What is the Odoo polling interval? Should the cloud backend support webhooks/notifications to Odoo as an optimization (post-MVP)? | Latency between dispense and order creation | Assumed Odoo polls on a schedule (e.g., every 1-5 minutes) or manual bulk trigger. |
| OQ-BE-5 | How are FCC API keys provisioned? Are they generated by the cloud backend during FCC registration, or provided externally? | Security, onboarding flow | Assumed cloud backend generates and manages FCC API keys during registration. |
| OQ-BE-6 | What is the exact variance tolerance per country? Is it a percentage, absolute amount, or both? | Reconciliation logic | Assumed configurable per legal entity (default +/-2%). |
| OQ-BE-7 | Is the 7-year audit retention a regulatory requirement for all 12 countries, or does it vary? | Storage sizing and archival strategy | Assumed 7 years as default, configurable per legal entity. |
| OQ-BE-8 | For cloud-side FCC polling (PULL mode with cloud-reachable FCCs), does the FCC need to be on a public IP or VPN? How many sites will use this mode vs. CLOUD_DIRECT push? | Network architecture, pull worker design | Assumed majority of sites use push in CLOUD_DIRECT mode. Cloud-side pull is for specific FCCs that support it. |
| OQ-BE-9 | Are there rate limits or throttling requirements for the Odoo polling API? (e.g., if Odoo polls too frequently) | API design | Assumed no rate limiting for MVP. Redis-based rate limiting available if needed. |
| OQ-BE-10 | Is there a need for real-time push notifications to the Angular portal (WebSocket/SSE) for live transaction monitoring? | Portal architecture | Assumed polling-based dashboard for MVP. WebSocket for live updates post-MVP. |
| OQ-BE-11 | Will the initial deployment cover all 12 countries simultaneously, or will there be a phased country rollout? | Configuration, testing, and deployment planning | Assumed phased rollout starting with 2-3 countries. |
| OQ-BE-12 | What are the exact 12 countries beyond the 5 currently listed (MW, TZ, BW, ZM, NA)? Different countries may have different fiscalization and regulatory requirements. | Legal entity configuration, fiscalization adapters | Assumed the system must support 12 legal entities. Specific countries beyond the initial 5 are TBD. |

---

*End of Cloud Backend HLD — WIP v0.1*
