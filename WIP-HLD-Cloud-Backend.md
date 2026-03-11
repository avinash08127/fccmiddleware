# Forecourt Middleware — Cloud Backend High-Level Design

**Status:** WIP (Work in Progress)
**Version:** 0.2
**Date:** 2026-03-10
**Reconciled from:** Opus WIP v0.1 + Codex Working Draft

---

# 1. Overview

## 1.1 Purpose

The Cloud Backend is the central middleware platform and system of coordination for the forecourt middleware platform. It ingests fuel dispensing transactions from Forecourt Controllers (FCCs) across 2,000 retail fuel stations in 12 African countries, deduplicates and normalizes them into a canonical model, reconciles pre-authorized transactions, and exposes them for Odoo ERP to poll and create orders. It provides the central transaction ledger, pre-auth reconciliation, site and FCC configuration runtime, integration endpoints, monitoring surfaces, and the secure control plane. It is the single source of truth for transaction state across the platform.

## 1.2 Business Context

Fuel retail operations in sub-Saharan Africa face unreliable internet connectivity. FCCs sit on station LANs and must continue dispensing fuel regardless of cloud availability. The cloud backend receives transactions from two paths — direct FCC push and Edge Agent catch-up upload — and must deduplicate transparently. It never pushes orders into Odoo; Odoo polls and creates orders on its own terms. This pull-based model keeps Odoo as the order master and avoids coupling the middleware into Odoo's transactional lifecycle.

The business problem is not just transaction capture. It is controlled, auditable movement of fuel-authorization events between Odoo, field devices, and heterogeneous forecourt controllers under unreliable connectivity and country-specific fiscal/tax rules. The cloud platform must therefore act as:

- The authoritative transaction record outside the FCC itself
- The reconciliation engine between authorized and actual dispensed amounts
- The configuration and operational visibility layer across countries, legal entities, and sites
- The decoupling layer between volatile field connectivity and enterprise systems such as Odoo and Databricks

## 1.3 Major Responsibilities

| Responsibility | Description |
|----------------|-------------|
| Transaction Ingestion | Receive transactions via FCC push and Edge Agent upload; acknowledge before processing |
| Deduplication | Primary key dedup (`fccTransactionId` + `siteCode`) with secondary fuzzy matching |
| Payload Normalization | Adapter-based translation from vendor-specific formats to canonical model |
| Pre-Auth Record Storage | Store pre-auth records forwarded by Edge Agents for reconciliation matching |
| Reconciliation | Match final dispense to pre-auth; calculate amount variance; flag exceptions |
| Odoo Integration | Expose poll + acknowledge endpoints for Odoo to consume PENDING transactions |
| Edge Agent Coordination | Config sync, status sync (SYNCED_TO_ODOO), version checks, telemetry, agent registration |
| Master Data Sync | Ingest legal entity, site, pump (with FCC pump number), nozzle (with Odoo↔FCC number mapping and product assignment), product, and operator reference data from Databricks/Odoo |
| Configuration Runtime | Resolve legal-entity, site, FCC, fiscalization, tolerance, and routing settings |
| Event Publishing & Audit | Publish audit and operational events for monitoring and downstream use; immutable event history |
| Operational Transparency | Health, metrics, tracing, diagnostics APIs, and operational dashboards |

## 1.4 Boundaries and Exclusions

**Included:**

- Middleware APIs, orchestration, async processing, reconciliation, adapter execution where cloud-facing, observability, admin APIs, and Odoo-facing transaction APIs

**Excluded:**

- Odoo internal order-creation logic
- Low-level FCC protocol implementation details for the Android HHT runtime
- Tax-authority implementations that sit outside this platform in `EXTERNAL_INTEGRATION` mode
- Detailed UI design for the Angular portal
- Detailed database schema and class-level design

## 1.5 Primary Requirement Alignment

- REQ-1 to REQ-5: Legal entity, site, FCC, connectivity, fiscalization configuration
- REQ-6 to REQ-10: Pre-auth, normal orders, reconciliation, Odoo pull model, normalization
- REQ-11 to REQ-17: Master data sync, ingestion modes, duplicate detection, audit, retries, multi-tenancy

---

# 2. Design Goals

| Goal | Rationale |
|------|-----------|
| **Scalability** | Sustain 2,000 sites, ~24,000 nozzles, and a design envelope up to 2M txns/day. Scale write-heavy ingestion independently from reconciliation and portal read workloads. |
| **Configurability** | 2,000 sites across 12 countries with different FCCs, fiscalization rules, operating modes, and ingestion patterns. Behaviour must be driven by configuration — not code per site. Vendor-specific parsing stays in adapters; routing, validation, and business policy are configurable. |
| **Resilience** | In CLOUD_DIRECT mode, the same transaction arrives via FCC push AND Edge Agent catch-up. The system must handle this transparently via idempotent dedup. Tolerate partial outages in FCC, Edge, queue, or Odoo dependencies without data loss. |
| **Security** | This platform authorizes fuel dispensing — a financial operation. Separate employee identity from machine identity. Treat pre-auth and dispense events as financially sensitive records requiring integrity, traceability, and least privilege. |
| **Maintainability** | Prefer a modular monolith with explicit bounded modules over premature microservices. Allow vendor adapter addition and country rollout without re-platforming. |
| **Multi-Country Readiness** | 12 legal entities from day one. All data, APIs, and business rules scoped by legal entity. Support per-country timezone, currency, fiscalization behaviour, receipt expectations, and operator-tax rules. Row-level isolation in a shared database. |
| **Operational Transparency** | 2,000 remote stations in Africa. Operations managers need clear visibility into transaction flow, reconciliation exceptions, agent health, and sync status — without visiting sites. |
| **Low Operational Friction** | Prefer AWS managed services over self-operated infrastructure where it materially lowers support burden. Build clear replay, retry, dead-letter, and diagnostics paths so support teams can resolve issues without engineering intervention. |
| **Practical MVP Delivery** | Avoid over-engineering. A modular monolith is faster to build, deploy, and debug than distributed microservices for a team starting fresh. |

---

# 3. Functional Scope

## 3.1 Key Features

1. **Transaction Ingestion Pipeline** — Push endpoint for FCC-direct transactions; upload endpoint for Edge Agent catch-up/relay; pull worker for cloud-side FCC polling (where applicable).
2. **Deduplication Engine** — Primary key match (silent skip) + secondary fuzzy match (flag for review).
3. **Payload Normalization** — Adapter per FCC vendor translates to canonical model. Raw payload archived.
4. **Pre-Auth Intake & State Machine** — Receive pre-auth records from Edge Agents; track lifecycle (PENDING → AUTHORIZED → DISPENSING → COMPLETED / CANCELLED / EXPIRED / FAILED).
5. **Reconciliation Engine** — Match final dispenses to pre-auth by correlation ID or fallback heuristics; variance calculation; auto-approve within tolerance or flag for review.
6. **Canonical Transaction Store** — Normalized transactions with status tracking and raw payload retention.
7. **Odoo Polling & Acknowledgement** — Expose PENDING transactions; accept acknowledgement with `odooOrderId`; mark `SYNCED_TO_ODOO`.
8. **Edge Agent Coordination** — Config sync, status sync, version checks, telemetry intake, agent registration.
9. **Master Data Sync** — Idempotent upsert of legal entities, sites, pumps, products, operators from Databricks.
10. **Event Publishing & Audit** — Domain events for audit trail and operational notifications; immutable append-only event store; S3 archive for long-term retention.
11. **Health, Metrics, Tracing & Diagnostics** — Structured logging, distributed tracing, operational dashboards, health check endpoints.

## 3.2 Major Use Cases

- Process normal orders pushed directly from FCCs in `CLOUD_DIRECT` mode
- Accept catch-up transactions replayed by Edge Agents after internet outages
- Match final dispenses against pre-auth records by correlation ID or fallback heuristics
- Serve pending transactions to Odoo and mark them `SYNCED_TO_ODOO` on acknowledgement
- Apply site/country-specific fiscalization and validation rules
- Support manual operational retry or review of flagged mismatches and duplicate candidates

## 3.3 Supported Operational Scenarios

- Fully online connected site
- Internet down, FCC LAN up, Edge buffering and later replay
- Mixed-mode sites where normal orders are default but fiscalized pre-auth also occurs
- Country-specific fiscalization modes: `FCC_DIRECT`, `EXTERNAL_INTEGRATION`, `NONE`
- Sites with no FCC, where middleware excludes site from controller traffic

---

# 4. Architecture Overview

## 4.1 Recommended Architecture Style

**Modular Monolith** with clean vertical module boundaries, asynchronous workers, and adapter/plugin boundaries, deployed as containerized services on AWS ECS Fargate.

**Rationale:**

- The business domains are tightly coupled around one transaction lifecycle and one authoritative store.
- Faster to build and deploy for MVP than distributed microservices.
- Single deployment unit simplifies debugging across modules (ingestion → dedup → reconciliation → Odoo sync).
- Module boundaries enforce separation of concerns and enable future decomposition if needed.
- Internal module communication via in-process method calls and MediatR — no inter-service latency for the critical transaction processing pipeline.
- The estate size is significant but not beyond what a well-structured .NET modular monolith on AWS can handle.
- The delivery team will move faster with one deployable API application plus separately scalable background workers than with many independently deployed microservices.

**Future Path:** If specific modules need independent scaling (e.g., ingestion under high load), they can be extracted as separate services behind the same API contracts. The modular structure makes this tractable.

**Why Not Microservices Now:**

- Strong transactional coupling exists between ingest, dedup, reconciliation, and Odoo exposure.
- Operational complexity would rise materially with limited near-term value.
- Country growth from 5 to 12 is better handled by data partitioning and worker scale-out first.

## 4.2 Logical Component Model

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
                │  PostgreSQL     │     │   SQS / SNS /   │
                │  (Aurora)       │     │   EventBridge   │
                │                 │     │                 │
                │ Transactions    │     │ Domain Events   │
                │ Pre-Auth        │     │ Retry Queues    │
                │ Reconciliation  │     │ DLQ             │
                │ Config          │     │                 │
                │ Audit Events    │     │                 │
                │ Outbox          │     │                 │
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
          │  │ (SQS)        │  │                    │  │
          │  └──────────────┘  └────────────────────┘  │
          │                                            │
          │  ┌──────────────┐  ┌────────────────────┐  │
          │  │ Outbox       │  │ Audit Archive      │  │
          │  │ Publisher    │  │ Worker (S3)        │  │
          │  └──────────────┘  └────────────────────┘  │
          └────────────────────────────────────────────┘
```

## 4.3 Module Decomposition

### Ingestion Module
- **Push Endpoint**: Receives FCC-direct transactions (`POST /api/v1/transactions/ingest`). API-key or mTLS authenticated per FCC.
- **Upload Endpoint**: Receives Edge Agent catch-up/relay/buffer-sync batches (`POST /api/v1/transactions/upload`). Device-token authenticated.
- **Pull Worker**: For cloud-side pull mode — polls FCC at configured interval (where FCC is cloud-reachable and configured for PULL). Tracks cursor per FCC.
- **Dedup Check**: Looks up `fccTransactionId + siteCode` in Redis cache → PostgreSQL. Primary match = silent skip. Secondary fuzzy match = flag for review.
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
- **Pump/Nozzle Mapping**: Two separate master data tables synced from Odoo via Databricks. The `pumps` table stores both `pump_number` (Odoo) and `fcc_pump_number` (FCC). The `nozzles` table stores `odoo_nozzle_number` → `fcc_nozzle_number` and the product (`productId`) dispensed by each nozzle. This mapping is included in the SiteConfig pushed to Edge Agents so they can translate Odoo pump/nozzle numbers to FCC numbers at pre-auth time.
- **Fiscalization Overrides**: Site-level overrides of legal entity defaults.
- **Tolerance Settings**: Reconciliation variance tolerance per legal entity or site.
- **Product Code Mappings**: FCC vendor product codes to canonical codes per FCC.

### Adapter Host Module
- Executes cloud-facing FCC adapter logic where required for direct FCC integrations or validation transforms.
- One adapter project per vendor behind a common `IFccAdapter` interface.
- DOMS is the MVP vendor. Radix, Advatec, and Petronite follow in Phase 3.

### Eventing and Audit Module
- **Event Publisher**: Publishes domain events via transactional outbox → SNS/EventBridge (e.g., TransactionReceived, TransactionDeduplicated, PreAuthAuthorized, ReconciliationFlagged).
- **Event Store**: Persists all events to an append-only table in PostgreSQL.
- **Archive Worker**: Periodically archives older events to S3 for long-term retention (7-year regulatory requirement).
- **Query API**: Events queryable by correlation ID, site, type, and time range.

### Master Data Sync Module
- **Sync API**: `POST /api/v1/sync/legal-entities`, `/sync/sites`, `/sync/pumps`, `/sync/products`, `/sync/operators`.
- **Idempotent Upsert**: Re-syncing the same data produces no side effects.
- **Validation**: Required field checks (e.g., operator TIN for CODO/DODO sites). Rejects invalid records with descriptive errors.
- **Freshness Tracking**: Updates `syncedAt` timestamp. Alerts if data goes stale beyond threshold.

### Portal/Admin API Module
- Serves configuration management, dashboards, reconciliation views, diagnostics, and audit search for the Angular portal.

### Background Workers
- Process reconciliation backlogs, outbox publishing, retry queues, data retention/archival, pre-auth expiry, stale transaction alerts, and operational alerting.

## 4.4 Key Runtime Flows

### Normal Order — CLOUD_DIRECT (Primary Path)

```
FCC → POST /api/v1/transactions/ingest (API key / mTLS)
  → Adapter: resolve FCC vendor from siteCode → normalize payload
  → Dedup: check fccTransactionId + siteCode → skip if exists
  → Persist: store canonical transaction, status=PENDING
  → S3: archive raw payload
  → Outbox: write TransactionReceived, TransactionNormalized events
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
  → Outbox: write OdooOrderCreated event for each
```

### Internet Down at Site

1. FCC push may fail or queue locally.
2. Edge Agent polls FCC over LAN, buffers locally, and later uploads backlog.
3. Cloud receives replayed transactions, deduplicates against FCC direct push and prior replays.
4. Odoo resumes cloud polling when internet returns.

### Pre-Auth Reconciliation

1. Edge Agent posts pre-auth record asynchronously to cloud after LAN authorization.
2. Final dispense arrives from FCC or Edge replay.
3. Reconciliation module matches by correlation ID, then fallback heuristics.
4. Variance is auto-approved within tolerance or flagged for review beyond tolerance.
5. Canonical transaction remains available for Odoo polling using actual volume and actual amount.

---

# 5. Project Structure Recommendation

## 5.1 Repository Strategy

**Single repository** (monorepo) for the cloud backend. The modular monolith deploys as 2 container images (API host + worker host) from the same codebase. Separate repositories for Edge Agent, Angular portal, and shared contracts.

## 5.2 Recommended Solution Structure

```
fcc-middleware-cloud/
│
├── src/
│   ├── FccMiddleware.Api/                        # ASP.NET Core Web API host
│   │   ├── Controllers/                          # Thin API controllers — delegate to Application layer
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
│   │   ├── Configuration/                         # Legal entity, site, FCC, pump/nozzle, nozzle mapping entities
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
│   │   ├── Messaging/                             # Outbox publisher, SQS/SNS integration
│   │   ├── Caching/                               # Redis cache implementations
│   │   ├── Storage/                               # S3 raw payload archival
│   │   └── Telemetry/                             # OpenTelemetry configuration
│   │
│   ├── FccMiddleware.Workers/                     # Background worker host
│   │   ├── FccPollWorker.cs                       # Cloud-side FCC polling (for PULL mode)
│   │   ├── PreAuthExpiryWorker.cs
│   │   ├── StaleTransactionAlertWorker.cs
│   │   ├── OutboxPublisherWorker.cs               # Publishes outbox events to SNS/EventBridge
│   │   ├── RetryConsumer.cs                       # SQS retry consumer
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
│   ├── FccMiddleware.IntegrationTests/            # Tests against real PostgreSQL + Redis (Testcontainers)
│   ├── FccMiddleware.ArchitectureTests/           # Enforce module boundaries
│   ├── FccMiddleware.ContractTests/               # API contract verification
│   └── FccMiddleware.Adapter.Doms.Tests/          # DOMS adapter-specific tests with sample payloads
│
├── infra/                                         # Infrastructure as Code
│   ├── terraform/                                 # or AWS CDK
│   ├── pipelines/                                 # CI/CD pipeline definitions
│   └── docker/
│       ├── Dockerfile.api
│       └── Dockerfile.worker
│
└── docs/
    ├── adr/                                       # Architecture Decision Records
    └── api/                                       # API documentation
```

## 5.3 Internal Layering

- **Domain**: Policies, aggregates, value objects, status transitions, reconciliation decisions — no external dependencies.
- **Application**: Commands, queries, handlers, orchestration, idempotency rules.
- **Infrastructure**: Persistence, messaging, AWS integrations, adapter I/O, encryption, cache.
- **Contracts**: API DTOs, event contracts, agent config contracts, Odoo/Databricks schemas. Can be published as a NuGet package for Edge Agent team reference.

## 5.4 Design Rationale

| Decision | Rationale |
|----------|-----------|
| Domain / Application / Infrastructure split | Clean Architecture ensures domain logic is testable without infrastructure. Adapters and persistence are swappable. |
| Separate Adapter projects per FCC vendor | Each adapter is independently testable and deployable. Adding a new vendor is a new project implementing `IFccAdapter`. |
| Separate API and Worker hosts | API serves HTTP requests. Workers run background jobs. Same codebase, different entry points. Can scale independently. |
| Contracts project | Shared between Cloud Backend and Portal (API clients). Can be published as a NuGet package. |
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

| System | Direction | Pattern | Notes |
|--------|-----------|---------|-------|
| FCC | Upstream | HTTPS push, optional polling, vendor protocol | Default path is direct cloud push where supported |
| Edge Agent | Upstream and downstream | HTTPS APIs | Uploads buffered txns, posts pre-auth, fetches config and sync status |
| Odoo | Downstream consumer and acknowledgement source | Poll + acknowledge REST APIs | Middleware never pushes orders into Odoo |
| Databricks | Upstream | Batch/API sync | Master data only |
| External fiscalization | Conditional | Async integration or reference-only | Used only in `EXTERNAL_INTEGRATION` contexts if later brought in-scope |
| Azure Entra ID | External IdP | OIDC / OAuth 2.0 | Employee authentication |

## 6.2 API Surface

| API | Auth Method | Consumer | Description |
|-----|------------|----------|-------------|
| `POST /api/v1/transactions/ingest` | API Key / mTLS (per FCC) | FCC | Direct FCC push endpoint |
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
| `GET /api/v1/health/*` | None / Internal | ALB / ECS | Health and readiness checks |

## 6.3 API Domains

- `/ingestion/fcc/*`
- `/ingestion/edge/*`
- `/transactions/*`
- `/preauth/*`
- `/config/*`
- `/agents/*`
- `/admin/*`
- `/health/*`

## 6.4 Messaging and Eventing

**Recommended pattern:**

- Transactional outbox in PostgreSQL — keeps business writes atomic with event publication intent
- Background outbox publisher to SNS/EventBridge for integration events
- SQS queues for worker-driven retries, alert processing, and review tasks

**Rationale:** Avoids self-managed broker operational overhead. Integrates cleanly with ECS/Fargate and AWS managed services. If protocol-specific routing becomes a hard requirement, RabbitMQ (Amazon MQ) can be considered.

| Topic / Queue | Publisher | Consumer | Pattern |
|---------------|-----------|----------|---------|
| `transaction.events` (SNS) | Outbox Publisher | Audit consumers, future downstream | Fanout — all transaction lifecycle events |
| `ingestion.retry` (SQS) | Ingestion Module | Retry Worker | Delayed retry with exponential backoff |
| `ingestion.dlq` (SQS) | Retry Worker | Ops Manager (via portal) | Dead-letter queue for exhausted retries |
| `preauth.events` (SNS) | Outbox Publisher | Audit consumers | Pre-auth lifecycle events |
| `reconciliation.events` (SNS) | Outbox Publisher | Audit consumers, Alert Worker | Reconciliation outcomes including flagged variances |
| `master-data.events` (SNS) | Outbox Publisher | Config cache invalidation | Cache invalidation on master data changes |

## 6.5 Sync Patterns

| Pattern | Where Used | Detail |
|---------|-----------|--------|
| **Push (Webhook)** | FCC → Cloud | FCC pushes transactions. Cloud acknowledges before processing (at-least-once). |
| **Pull (Polling)** | Cloud → FCC | Cloud-side pull worker polls FCC at interval (for PULL-mode FCCs reachable from cloud). Tracks cursor. |
| **Upload (Batch POST)** | Edge Agent → Cloud | Edge Agent uploads catch-up/buffered transactions in configurable batch size (e.g., 50). |
| **Poll (Consumer-Driven)** | Odoo → Cloud | Odoo polls PENDING transactions on schedule or manual trigger. |
| **Status Poll** | Edge Agent → Cloud | Edge Agent polls SYNCED_TO_ODOO status every ~30 seconds when online. |
| **Config Pull** | Edge Agent → Cloud | Agent checks for config updates on each cloud sync cycle. |
| **Master Data Push** | Databricks → Cloud | Databricks pushes master data on schedule or change. |

## 6.6 Retry and Idempotency

| Concern | Strategy |
|---------|----------|
| FCC push retry | Cloud acknowledges with 202 immediately. If internal processing fails, transaction goes to retry queue via outbox. |
| Edge Agent upload retry | Agent retries with exponential backoff. Cloud deduplicates on redelivery. |
| Odoo acknowledge retry | Acknowledge endpoint is idempotent — re-acknowledging an already-acknowledged transaction is a no-op. |
| Databricks sync retry | Upsert is idempotent by design — re-syncing same data has no effect. |
| Pre-auth creation | Site-scoped correlation IDs and deduplicates retries. |
| Replay processing | Always tolerates duplicate arrivals from FCC queue flush plus Edge backlog. |
| Dead-letter | Transactions exhausting retries go to DLQ. Ops Manager can inspect and manually retry via portal. Non-retryable validation failures are parked for review, not endlessly retried. |

## 6.7 Online/Offline Handling

- Cloud does not assume a single source of truth during outages; it assumes converging sources.
- Reconciliation and duplicate policies must tolerate delayed, duplicated, and out-of-order arrivals.
- Edge status-sync API supports hiding cloud-consumed transactions from offline Odoo polling.

---

# 7. Security Architecture

## 7.1 Identity Separation

Employee access and machine access must remain separate.

- Employee/portal/API user authentication: **Azure Entra ID**
- Edge Agent and FCC authentication: **Platform-managed machine identity**

## 7.2 Authentication

### Employee Authentication

- Angular portal authenticates users with Azure Entra ID using OIDC Authorization Code with PKCE.
- Backend admin APIs validate Entra JWT access tokens.
- Use Entra app roles or security groups mapped to application roles.

### Edge Agent Authentication

- Each primary Edge Agent device receives a site-bound device identity during provisioning.
- **Preferred model**: Per-device client certificate stored in Android Keystore plus short-lived signed access token.
- **Bootstrap model**: QR code contains one-time registration token only; it must not be the long-lived runtime secret.

### FCC Authentication

| Actor | Method | Details |
|-------|--------|---------|
| FCC (modern controllers) | mTLS | Per-site/FCC client certificate. Preferred for controllers that support TLS. |
| FCC (limited devices) | API Key + HMAC | Per-site API credential with HMAC signature and strict replay window. Fallback for limited devices. |
| Odoo | API Key | Static API key scoped to legal entity. Stored in AWS Secrets Manager. Rotated periodically. |
| Databricks | Service Principal / API Key | Service account scoped to master data write operations only. |

> **Design-risk item:** FCC security capabilities may vary significantly by vendor and country deployment. Capability variance should be validated early.

## 7.3 Authorization

**Recommended: RBAC with constrained ABAC filters.**

| Role | Permissions |
|------|-------------|
| **System Administrator** | Full configuration access. Cross-legal-entity access. FCC management. Agent management. |
| **Operations Manager** | Read all transactions within assigned legal entities. Reconciliation actions. Manual retry. Alert management. |
| **Site Supervisor** | Read transactions for assigned sites. View Edge Agent health. Trigger manual pulls. |
| **Read-Only Auditor** | Read-only access to transactions, audit trail, and reconciliation records across assigned legal entities. |

- Roles are managed as Azure Entra app roles or groups. Role claims are included in the JWT and enforced by the API via policy-based authorization.
- **ABAC filters** for legal entity, country, region, and optionally site group.
- **Legal Entity Scoping**: All API queries automatically filter by the user's assigned legal entity unless the user has the System Administrator role. Enforced at the query layer via a global EF Core query filter.
- **No cross-legal-entity data access by default.**

## 7.4 Secrets Management

| Secret | Storage | Rotation |
|--------|---------|----------|
| Database connection string | AWS Secrets Manager | Rotated via Aurora IAM auth or manual rotation |
| Redis connection string | AWS Secrets Manager | Managed by ElastiCache |
| FCC API keys | AWS Secrets Manager → loaded to DB encrypted | Per-FCC, rotatable via admin portal |
| Edge Agent device tokens | Signed JWTs (cloud issues, cloud validates) | Token refresh on each cloud sync |
| Odoo API key | AWS Secrets Manager | Manual rotation, coordinated with Odoo team |
| Azure Entra client secret | AWS Secrets Manager | Rotated per Azure Entra policy |
| Encryption keys | AWS KMS | Managed by KMS — envelope encryption for sensitive credentials at rest |

## 7.5 Encryption

| Scope | Approach |
|-------|----------|
| **In Transit** | TLS 1.2+ on all endpoints. ALB terminates TLS with ACM-managed certificates. Internal VPC traffic encrypted. |
| **At Rest** | Aurora PostgreSQL: AWS-managed encryption (AES-256). ElastiCache: encryption at rest. S3: SSE-S3 or SSE-KMS. Queues: encryption at rest with KMS-managed keys. |
| **FCC Credentials** | Field-level protection: encrypted column using application-level encryption (AES-256-GCM) with a KMS-managed key. Never logged or returned in API responses. |

## 7.6 Audit Logging

- All API requests logged with: timestamp, actor identity, action, resource, legal entity context, IP, result.
- All transaction state changes logged as immutable domain events.
- Immutable audit events for config changes, manual retries, reconciliation overrides, role changes, and device registration.
- Separate business audit trail from high-volume technical logs.
- Authentication events logged via Azure Entra audit logs + cloud middleware access logs.
- Audit logs retained for 7 years (regulatory requirement).
- Audit logs are append-only and cannot be modified or deleted by any role.

## 7.7 Tenant / Site Isolation

- **Row-level isolation**: All tables include `legalEntityId`. A global EF Core query filter ensures all queries are scoped.
- **API-level enforcement**: Tenant context extracted from authentication token. Requests cannot cross tenant boundaries unless System Administrator.
- **Edge Agent isolation**: Each device token is bound to a specific `siteCode` and `legalEntityId`. Agent cannot access data from other sites.
- **FCC API key isolation**: Each FCC API key is bound to a `siteCode`. Transactions from an FCC are validated against the registered site.
- **Defense in depth**: Row-level security can be added in PostgreSQL, but application-enforced tenant filtering remains mandatory.

## 7.8 Trust Boundaries

```
┌─────────────────────── PUBLIC INTERNET ───────────────────────────┐
│                                                                    │
│  [FCC]  ─── TLS + API Key/mTLS ──►  [ALB/WAF]  ──► [Cloud]      │
│  [Edge Agent] ─ TLS + Device Token ─►  [ALB/WAF]  ──►  [Cloud]   │
│  [Odoo] ─── TLS + API Key ──►  [ALB/WAF]  ──►  [Cloud Backend]   │
│  [Portal] ─── TLS + Entra JWT ──►  [CloudFront/WAF]  ──►  [Cloud]│
│  [Databricks] ─── TLS + Service Principal ──►  [ALB/WAF]  ──►    │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘

┌─────────────── AWS VPC (PRIVATE) ─────────────────────────────────┐
│                                                                    │
│  [Cloud Backend API]  ──►  [Aurora PostgreSQL] (private subnet)   │
│  [Cloud Backend API]  ──►  [ElastiCache Redis] (private subnet)   │
│  [Cloud Backend API]  ──►  [SQS/SNS] (VPC endpoint)              │
│  [Cloud Backend API]  ──►  [S3] (VPC endpoint)                    │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

---

# 8. Deployment Architecture

## 8.1 Recommended Deployment Model

**AWS — Single Region** with active-active application instances in one primary region and multi-AZ managed data services.

**Primary region preference**: af-south-1 (Cape Town) — closest AWS region to the 12 African countries of operation. Provides the lowest latency for FCC push, Edge Agent sync, and Odoo polling.

> **Note:** If af-south-1 does not have all required services, fall back to eu-west-1 (Ireland) which has full service availability and reasonable latency to Africa. Service availability in af-south-1 must be validated.

## 8.2 Core AWS Services

| Service | Purpose |
|---------|---------|
| Amazon ECS Fargate | API and worker containers |
| Amazon Aurora PostgreSQL | Transactional data |
| Amazon ElastiCache Redis | Hot config cache, dedup cache, rate limiting, transient coordination |
| Amazon SQS | Retry queues, worker-driven async processing, DLQ |
| Amazon SNS / EventBridge | Domain event fanout, integration events |
| Amazon S3 | Raw payload archive, audit exports, diagnostic bundles, portal static assets |
| AWS WAF + CloudFront | Portal delivery and API protection |
| API Gateway or ALB | Frontend for backend APIs — selected based on mTLS and protocol needs |
| AWS KMS | Encryption key management |
| AWS Secrets Manager | Runtime secrets |
| AWS Private CA | Client certificates where adopted (Edge Agent, modern FCCs) |
| Amazon CloudWatch | Logs, metrics, alarms, dashboards |
| AWS X-Ray / OpenTelemetry | Distributed tracing |

## 8.3 Cloud Deployment Topology

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
  │ Aurora   │  │ElastiCache│  │  SQS / SNS /   │
  │PostgreSQL│  │  Redis    │  │  EventBridge   │
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

## 8.4 Environment Strategy

| Environment | Purpose | Infrastructure |
|-------------|---------|----------------|
| **dev** | Developer testing | Single Fargate task. Shared dev Aurora. Localstack or real AWS services. |
| **test** | Automated testing / QA | Moderate sizing. Synthetic test data. |
| **uat** | Pre-production user acceptance | Production-like. Smaller instance sizes. Representative data. |
| **prod** | Live operations | Full HA. Multi-AZ Aurora. Multi-AZ services. ECS auto-scaling. |

Country onboarding should be data/config-driven inside shared environments, not separate per-country stacks, unless regulation later forces country-resident hosting.

## 8.5 High Availability and Disaster Recovery

| Component | HA Strategy |
|-----------|-------------|
| **API Service** | 2+ ECS tasks across AZs. ALB health checks. Auto-scaling on CPU/memory/request count. |
| **Worker Service** | 2+ ECS tasks. SQS ensures at-least-once delivery; idempotent consumers handle concurrent processing. |
| **Aurora PostgreSQL** | Multi-AZ with automatic failover. Read replicas for portal read queries if needed. |
| **ElastiCache Redis** | Multi-AZ cluster mode. Automatic failover. |
| **S3** | 99.999999999% durability by default. Cross-region replication if required. |
| **DR Strategy** | Automated backups + point-in-time database recovery (35-day retention). S3 versioning and lifecycle policies. Defined RPO/RTO targets. Warm DR in a secondary region for production only after MVP stabilization. |

## 8.6 Scaling Approach

**Scale targets (derived from requirements):**
- 2,000 sites × up to 1,000 txns/day = up to 2,000,000 txns/day = ~23 txns/second average, ~100 txns/second peak burst.
- NFR-3: Support 100 concurrent sites × 10 txns/min = ~17 txns/second sustained.
- These are modest numbers for a .NET API backed by Aurora. Horizontal auto-scaling is available from day one.

| Component | Scaling Strategy |
|-----------|-----------------|
| **API Service** | Start with 2 Fargate tasks. Auto-scale on CPU (target 60%) or request count for peak periods. |
| **Worker Service** | 2 tasks. Scale based on SQS queue depth and reconciliation backlog latency. |
| **Aurora** | Start with `db.r6g.large`. Scale vertically. Add read replicas for portal queries if needed. |
| **Redis** | Start with `cache.r6g.large`. Scale vertically. |
| **Transaction Tables** | Partition large tables by time and possibly legal entity for sustained performance. |
| **Reporting** | Use read replicas or reporting projections only if portal/reporting load begins to impact transactional workloads. |

## 8.7 Observability

| Pillar | Implementation |
|--------|---------------|
| **Logging** | Structured JSON logging via Serilog. Fields include: site, legal entity, FCC vendor, ingestion mode, transaction ID, and correlation ID. Shipped to CloudWatch Logs. Queryable via CloudWatch Insights or forwarded to Grafana Loki. |
| **Metrics** | OpenTelemetry metrics: ingest rate, dedup hit rate, reconciliation lag, Odoo poll latency, Edge Agent backlog age, auth failures, error rates, queue depths. Exported to CloudWatch Metrics or Prometheus. |
| **Tracing** | OpenTelemetry distributed tracing. Correlation IDs flow from FCC → ingestion → dedup → reconciliation → Odoo sync. Exported to AWS X-Ray or Grafana Tempo. |
| **Health Checks** | `/health/ready` (all dependencies reachable), `/health/live` (process alive). Used by ALB and ECS for routing and restart. |
| **Dashboards** | Operational dashboards by country and by site cohort: transaction throughput, error rates, queue depths, Edge Agent health, reconciliation exception rates, Odoo sync lag. |
| **Alerting** | CloudWatch Alarms or Grafana alerting. Critical alerts: ingestion failures > threshold, DLQ depth > 0, Aurora storage > 80%, Edge Agent offline > configurable duration, stale PENDING transactions. |

---

# 9. Key Design Decisions

## 9.1 Architectural Choices

| # | Decision | Choice | Rationale | Trade-off |
|---|----------|--------|-----------|-----------|
| 1 | Architecture style | Modular monolith | Fastest MVP delivery. Simpler deployment and debugging. Modules can be extracted later. Strongest balance of delivery speed, consistency, and operability. | Reduced independent deployability of modules. Shared database and process. |
| 2 | Odoo integration model | Odoo polls middleware (pull-based) | Keeps Odoo as order master. Avoids tight coupling. Middleware is a transaction store, not an Odoo client. Matches requirements. | Latency between dispense and Odoo order creation depends on Odoo poll interval. Requires clean acknowledgement semantics and portal visibility into poll lag. |
| 3 | Default ingestion mode | `CLOUD_DIRECT` with Edge as safety net | Minimizes field dependency on the HHT in steady state while preserving offline resilience. | Duplicate arrival is normal and must be engineered as a first-class condition. |
| 4 | Identity model | Azure Entra for employees; separate device identity for machines | Employee SSO should use enterprise identity. Station devices must not depend on human tokens. | Two identity models must be managed and documented. |
| 5 | Messaging | AWS managed messaging (SQS/SNS/EventBridge) with transactional outbox | Lowers ops burden. Integrates cleanly with ECS/Fargate. Outbox ensures atomic writes with event intent. | Less broker-specific routing flexibility than RabbitMQ. |
| 6 | Deduplication strategy | Primary key (silent skip) + secondary fuzzy (flag) | Primary key dedup handles the expected dual-path arrival cleanly. Secondary fuzzy catches edge cases without false-positive auto-skipping. | Secondary fuzzy matches require human review — operational overhead. |
| 7 | Event publishing | Selective event streaming via outbox (not full event sourcing) | Publish domain events for audit and downstream consumption. Simpler to implement and reason about. | Cannot replay full state from events alone. State is in PostgreSQL. |
| 8 | Multi-tenancy | Row-level isolation in shared database | Simpler for 12 tenants. Lower infrastructure cost. EF Core global query filters enforce isolation. | Noisy-neighbour risk (mitigated by scale — 12 tenants is manageable). Database-per-tenant migration path exists if needed. |
| 9 | FCC adapter pattern | Code-level adapters (one project per vendor) | Vendor protocols are structurally different. Configuration alone cannot handle new vendors. Code-level adapters provide full control. | Adding a new FCC vendor requires a code change and deployment — not just configuration. Intentional per requirements. |

## 9.2 Assumptions

1. One active FCC per connected site.
2. One designated primary Edge Agent per site for MVP.
3. Odoo will implement the poll + acknowledge pattern against the middleware API. The Odoo team owns this integration.
4. Databricks pipelines for master data sync already exist or will be built by the data team. The middleware exposes the sync API.
5. FCC vendors (starting with DOMS) have documented protocols for push and/or pull. Protocol documentation is available.
6. Azure Entra tenant is already provisioned. Employee accounts and groups/roles exist.
7. Each FCC has a unique `fccTransactionId` that is stable across retries and dual-path delivery.
8. Station LAN is reliably available independent of internet connectivity (confirmed in requirements).
9. The Urovo i9100 running the Edge Agent has sufficient storage for 30,000+ transactions in SQLite.
10. Odoo can poll and acknowledge at a cadence compatible with business operations.
11. Country residency requirements do not currently force per-country AWS accounts or regions.

## 9.3 Known Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| FCC security capabilities vary by vendor | Some vendors may not support modern TLS or client certificates for direct cloud push. | Validate FCC-to-cloud auth capability early per vendor. Design fallback auth (API key + HMAC) from the start. |
| FCC push endpoint availability | If the cloud ingestion endpoint is slow or down, FCCs may queue internally or drop transactions. | Edge Agent catch-up provides safety net. 202 acknowledge-before-processing pattern. ALB health checks and auto-scaling. |
| Direct FCC internet reachability inconsistent | Increases `RELAY` mode usage vs. `CLOUD_DIRECT`. | Design dual-path dedup as first-class. Edge Agent is the resilience layer. |
| Odoo polling lag | If Odoo fails to poll regularly, PENDING transactions accumulate and may go stale. | Stale transaction alerting (configurable threshold). Manual bulk-create available. |
| Cross-cloud auth (Azure Entra + AWS) | Added complexity. Token validation, key rotation, potential latency for JWKS fetch. | Cache JWKS keys. Use standard libraries (Microsoft.Identity.Web). Well-established pattern. |
| Database growth | 2M txns/day × 7-year retention = billions of rows. | Table partitioning by `legalEntityId` and time. Archive older transactions to S3. Retention-aware queries. |
| Portal/reconciliation read load | Could affect OLTP performance if not isolated carefully. | Read replicas, reporting projections, query optimization. |
| Cross-country timezone/fiscal-receipt nuances | Subtle reconciliation defects if canonical rules are weak. | Strong canonical model. Per-country configuration. Thorough testing per legal entity. |
| DOMS protocol unknowns | Adapter development depends on documentation quality. | Plan PoC/spike on DOMS integration early in Phase 1. |

## 9.4 Areas Needing Validation / PoC

1. **FCC-to-cloud authentication** — Capability by vendor. Which support modern TLS and client certificates, and which require compensating controls?
2. **DOMS Protocol Integration** — Obtain DOMS documentation. Build a PoC adapter against a test FCC or simulator.
3. **af-south-1 Service Availability** — Verify Aurora, ElastiCache, SQS/SNS/EventBridge are available in af-south-1.
4. **Throughput and latency** of bulk replay after multi-day outage at many sites.
5. **Odoo Poll + Acknowledge Implementation** — Validate cadence, batch size, and acknowledgement contract under peak trading hours.
6. **Azure Entra Token Validation on AWS** — JWT validation performance and JWKS caching.
7. **Transaction Volume Stress Test** — Simulate 100 txns/second burst to validate ingestion pipeline throughput.
8. **Partitioning strategy** for transaction and audit tables at projected growth.

---

# 10. Non-Functional Requirements Mapping

| NFR Area | Target | HLD Response |
|----------|--------|-------------|
| **Availability** | 99.5% (~43.8h downtime/year) | Multi-AZ deployment. ALB health checks. ECS auto-restart. Aurora automatic failover. No single points of failure. Duplicate-tolerant ingestion. Decoupled workers. No synchronous dependency on Odoo for ingest. |
| **Latency (Pre-auth)** | < 5s cloud processing | Pre-auth record receipt is a simple write — well under 5s. Actual pre-auth command goes Edge Agent → FCC over LAN (not through cloud). |
| **Throughput** | 100 sites × 10 txns/min (~17 txns/sec sustained) | .NET on Fargate with Aurora easily handles this. Start with 2 tasks. Auto-scale if needed. |
| **Data Retention** | 7 years (regulatory) | PostgreSQL for active data (configurable window, e.g., 1-2 years). S3 archive for long-term. Partitioned tables for efficient queries. |
| **Security** | OAuth 2.0 / API Keys / mTLS / TLS / Encryption at rest | Azure Entra (employees), API keys (integrations), device tokens/certificates (agents). TLS 1.2+ everywhere. Aurora + S3 + Redis encryption at rest. |
| **Scalability** | Horizontal scale-out | Modular monolith can be decomposed. ECS auto-scaling. Aurora read replicas. Queue-backed workers. Caching for hot config. Partitioned transaction storage. |
| **Recoverability** | No data loss on restart | Outbox pattern. Durable SQS messaging. PostgreSQL WAL. ECS restarts preserve no in-memory state. PITR backups. Raw payload retention. Replay-safe APIs. |
| **Supportability** | Operational self-service | Structured diagnostics. Portal admin APIs. Correlation IDs. Operational dashboards. Explicit error classifications. DLQ inspection. |
| **Operability** | Low ops burden | Managed AWS services. Separate scaling of APIs/workers. Health endpoints. Version checks for agents. |
| **Extensibility** | New vendors, new countries | Adapter module boundary. Configuration-driven routing. Event contracts. Domain-based module separation. |

---

# 11. Recommended Technology Direction

| Concern | Technology | Rationale |
|---------|-----------|-----------|
| **Runtime** | .NET 10, ASP.NET Core | Per requirements. Mature, high-performance, excellent for API workloads. |
| **ORM** | Entity Framework Core 10 | Productive. Supports global query filters (multi-tenancy). Strongly-typed migrations. Selective Dapper/Npgsql for high-volume queries if required. |
| **CQRS / Mediator** | MediatR | Lightweight command/query dispatch. Pipeline behaviours for cross-cutting concerns. Only if it matches team standards; avoid over-abstraction. |
| **Validation** | FluentValidation | Expressive validation rules for incoming payloads. Integrates with MediatR pipeline. |
| **Messaging** | SQS + SNS/EventBridge + Transactional Outbox | AWS-managed. Outbox ensures atomic event publication. SQS provides retry/DLQ. SNS/EventBridge for fanout. |
| **Caching** | StackExchange.Redis (ElastiCache) | Dedup cache, config cache, rate limiting. |
| **Object Storage** | AWSSDK.S3 | Raw payload archival and long-term audit storage. |
| **Database** | Aurora PostgreSQL | Transactional store. Multi-AZ. Read replicas. Partitioning. |
| **Authentication** | Microsoft.Identity.Web + custom API key middleware | Azure Entra JWT validation + API key validation + device token validation as composable auth schemes. |
| **Security** | AWS KMS, Secrets Manager, Private CA | Key management, secrets, client certificates where adopted. |
| **Observability** | OpenTelemetry .NET SDK + Serilog + CloudWatch/X-Ray | Structured logging, distributed tracing, metrics export. |
| **Testing** | xUnit + Testcontainers + NSubstitute | Unit tests with mocks. Integration tests with real PostgreSQL, Redis in Docker. |
| **IaC** | Terraform or AWS CDK (.NET) | Infrastructure as code for reproducible deployments. |
| **CI/CD** | GitHub Actions | Build, test, push Docker images to ECR, deploy to ECS. |
| **API Documentation** | Swashbuckle (Swagger / OpenAPI) | Auto-generated API docs. Shared with Odoo team and Edge Agent team. |
| **Health Checks** | ASP.NET Core Health Checks | Built-in. Checks Aurora, Redis, SQS, S3 connectivity. |
| **Background Jobs** | .NET BackgroundService + SQS Consumers | BackgroundService for scheduled workers. SQS consumers for message-driven workers. |
| **Serialization** | System.Text.Json | High-performance JSON for API payloads and event serialization. |

### Design Patterns

- Modular monolith
- Transactional outbox / inbox
- Idempotent command processing
- Adapter/plugin model for FCC vendors
- Configuration-driven policy resolution by legal entity and site
- CQRS (light — command/query separation via MediatR, not separate read/write stores)

---

# 12. Open Questions / Pending Decisions

| ID | Question | Impact | Current Assumption |
|----|----------|--------|--------------------|
| OQ-1 | Which FCC vendors support modern TLS and client certificates for direct cloud push, and which require compensating controls? | Security architecture, adapter design | Fallback API key + HMAC designed in. Needs validation per vendor. |
| OQ-2 | Is AWS af-south-1 (Cape Town) the confirmed target region? Are all required services available there? | Deployment architecture | Assumed af-south-1 with eu-west-1 fallback. Needs validation. |
| OQ-3 | What is the DOMS push protocol? REST? TCP? SOAP? What does the payload look like? | Adapter design, ingestion endpoint | Assumed REST/JSON based on rawPayload field. Needs DOMS documentation. |
| OQ-4 | Does Odoo have an existing polling/webhook mechanism, or does the Odoo team need to build from scratch? | Odoo integration timeline | Assumed Odoo team builds this. Cloud backend exposes the API. |
| OQ-5 | What is the Odoo polling interval? What batch size is acceptable at peak trading hours? Should webhooks/notifications be considered post-MVP? | Latency, API design | Assumed Odoo polls on schedule (e.g., every 1-5 minutes) or manual bulk trigger. |
| OQ-6 | How are FCC API keys provisioned? Generated by cloud during registration or provided externally? | Security, onboarding flow | Assumed cloud backend generates and manages during registration. |
| OQ-7 | What is the exact variance tolerance per country? Percentage, absolute amount, or both? | Reconciliation logic | Assumed configurable per legal entity (default +/-2%). |
| OQ-8 | Is the 7-year audit retention a regulatory requirement for all 12 countries, or does it vary? What is the retention split between OLTP and archive? | Storage sizing and archival | Assumed 7 years default, configurable per legal entity. |
| OQ-9 | For cloud-side FCC polling (PULL mode), does the FCC need to be on a public IP or VPN? How many sites will use this mode? | Network architecture, pull worker design | Assumed majority use push in CLOUD_DIRECT. Cloud-side pull for specific FCCs. |
| OQ-10 | Is the 12-country rollout simultaneous or phased? What are the exact 12 countries beyond the initial 5 (MW, TZ, BW, ZM, NA)? | Configuration, testing, deployment planning | Assumed phased rollout starting with 2-3 countries. Specific countries beyond initial 5 are TBD. |
| OQ-11 | Should reconciliation review remain entirely in the Angular portal, or do some exception actions need API-only integration for back-office tooling? | Portal vs API scope | Assumed portal-based for MVP. |
| OQ-12 | Is `EXTERNAL_INTEGRATION` purely informational in phase 1, or does the cloud backend need to orchestrate callbacks to any tax systems later? | Fiscalization scope | Assumed informational only in phase 1. |
| OQ-13 | Is there a need for real-time push notifications to the Angular portal (WebSocket/SSE) for live transaction monitoring? | Portal architecture | Assumed polling-based dashboard for MVP. WebSocket for live updates post-MVP. |
| OQ-14 | Are there rate limits or throttling requirements for the Odoo polling API? | API design | Assumed no rate limiting for MVP. Redis-based rate limiting available if needed. |

---

*End of Cloud Backend HLD — WIP v0.2 (Reconciled)*
