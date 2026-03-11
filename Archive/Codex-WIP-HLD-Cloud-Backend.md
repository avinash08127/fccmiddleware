# Forecourt Middleware Platform
## WIP High Level Design - Cloud Backend

Status: Working Draft  
Authoring Context: Derived from `Requirements.md`, `HighLevelRequirements.md`, `FlowDiagrams.md`, and the additional sizing/authentication constraints provided on 2026-03-10.  
Platform Preference: AWS  
Employee Identity Provider: Azure Entra ID  

## 1. Overview

### Purpose of This Subsystem

The Cloud Backend is the system of coordination for the forecourt middleware platform. It provides the central transaction ledger, pre-auth reconciliation, site and FCC configuration runtime, integration endpoints, monitoring surfaces, and the secure control plane for 2,000 retail stations across 12 African countries.

### Business Context

The business problem is not just transaction capture. It is controlled, auditable movement of fuel-authorization events between Odoo, field devices, and heterogeneous forecourt controllers under unreliable connectivity and country-specific fiscal/tax rules. The cloud platform must therefore act as:

- the authoritative transaction record outside the FCC itself
- the reconciliation engine between authorized and actual dispensed amounts
- the configuration and operational visibility layer across countries, legal entities, and sites
- the decoupling layer between volatile field connectivity and enterprise systems such as Odoo and Databricks

### Major Responsibilities

- Receive normal-order transactions from FCCs and/or Edge Agents
- Store canonical and raw transaction payloads with deduplication
- Persist pre-auth records from Edge Agents and reconcile them with final dispenses
- Expose Odoo polling and acknowledgement APIs
- Expose configuration, health, diagnostics, and admin APIs
- Maintain master data projections from Databricks/Odoo
- Publish audit and operational events for monitoring and downstream use
- Enforce legal-entity and site isolation
- Provide a secure machine-to-machine platform for Edge Agents and supported FCCs

### Boundaries and Exclusions

Included:

- middleware APIs, orchestration, async processing, reconciliation, adapter execution where cloud-facing, observability, admin APIs, and Odoo-facing transaction APIs

Excluded:

- Odoo internal order-creation logic
- low-level FCC protocol implementation details for the Android HHT runtime
- tax-authority implementations that sit outside this platform in `EXTERNAL_INTEGRATION` mode
- detailed UI design for the portal
- detailed database schema and class-level design

### Primary Requirement Alignment

- REQ-1 to REQ-5: legal entity, site, FCC, connectivity, fiscalization configuration
- REQ-6 to REQ-10: pre-auth, normal orders, reconciliation, Odoo pull model, normalization
- REQ-11 to REQ-17: master data sync, ingestion modes, duplicate detection, audit, retries, multi-tenancy

## 2. Design Goals

### Scalability

- Sustain the practical estate size of 2,000 sites, about 24,000 nozzles, and a design envelope up to 2 million transactions/day if every high-volume site approaches the stated peak.
- Scale write-heavy ingestion independently from reconciliation and portal read workloads.

### Configurability

- Drive country, site, FCC, fiscalization, tolerance, and routing behavior from configuration and reference data, not site-specific code forks.
- Keep vendor-specific parsing in adapters, but keep routing, validation, and business policy configurable.

### Resilience

- Preserve transactions even when cloud push, Edge replay, or Odoo polling occurs more than once.
- Tolerate partial outages in FCC, Edge, queue, or Odoo dependencies without data loss.

### Security

- Separate employee identity from machine identity.
- Treat pre-auth and dispense events as financially sensitive records requiring integrity, traceability, and least privilege.

### Maintainability

- Prefer a modular monolith with explicit bounded modules over premature microservices.
- Allow vendor adapter addition and country rollout without re-platforming.

### Multi-Country Readiness

- Make legal entity the primary partition key for policy and reporting.
- Support per-country timezone, currency, fiscalization behavior, receipt expectations, and operator-tax rules.

### Low Operational Friction

- Prefer AWS managed services over self-operated infrastructure where it materially lowers support burden.
- Build clear replay, retry, dead-letter, and diagnostics paths so support teams can resolve issues without engineering intervention.

## 3. Functional Scope

### Key Features

- FCC transaction ingestion via push, pull, and hybrid patterns
- Edge Agent transaction intake and replay intake
- Pre-auth registration intake and reconciliation
- Canonical transaction store with raw payload retention
- Duplicate detection and secondary-match review flags
- Odoo polling and acknowledgement endpoints
- Databricks master-data sync ingestion
- Site/FCC/configuration APIs for portal and operational tooling
- Event publishing for audit and operational notifications
- Health, metrics, tracing, and diagnostics APIs

### Major Use Cases

- Process normal orders pushed directly from FCCs in `CLOUD_DIRECT` mode
- Accept catch-up transactions replayed by Edge Agents after internet outages
- Match final dispenses against pre-auth records by correlation ID or fallback heuristics
- Serve pending transactions to Odoo and mark them `SYNCED_TO_ODOO` on acknowledgement
- Apply site/country-specific fiscalization and validation rules
- Support manual operational retry or review of flagged mismatches and duplicate candidates

### Supported Operational Scenarios

- Fully online connected site
- Internet down, FCC LAN up, Edge buffering and later replay
- Mixed-mode sites where normal orders are default but fiscalized pre-auth also occurs
- Country-specific fiscalization modes: `FCC_DIRECT`, `EXTERNAL_INTEGRATION`, `NONE`
- Sites with no FCC, where middleware excludes site from controller traffic

## 4. Architecture Overview

### Recommended Architecture Style

Recommended style: modular monolith with asynchronous workers and adapter/plugin boundaries.

Rationale:

- The business domains are tightly coupled around one transaction lifecycle and one authoritative store.
- The estate size is significant but not beyond what a well-structured .NET modular monolith on AWS can handle.
- The delivery team will move faster with one deployable API application plus separately scalable background workers than with many independently deployed microservices.
- The design still supports later service extraction at module boundaries if transaction volume, org size, or regulatory segregation grows.

### Logical Component Model

1. Ingestion API
   Accepts FCC pushes, Edge uploads, Databricks sync payloads, and Odoo acknowledgements.
2. Transaction Processing Module
   Normalizes, validates, deduplicates, persists, and emits domain events.
3. Pre-Auth and Reconciliation Module
   Stores pre-auth records, matches final dispenses, calculates variance, and flags exceptions.
4. Configuration Runtime Module
   Resolves legal-entity, site, FCC, fiscalization, tolerance, and routing settings.
5. Odoo Integration Module
   Serves pending transactions and accepts acknowledgements without pushing orders into Odoo.
6. Edge Control Module
   Handles agent registration, configuration sync, version compatibility, and status sync such as `SYNCED_TO_ODOO`.
7. Adapter Host Module
   Executes cloud-facing FCC adapter logic where required for direct FCC integrations or validation transforms.
8. Eventing and Audit Module
   Implements outbox publishing, immutable event history, and operational alerts.
9. Portal/Admin API Module
   Serves configuration, dashboards, reconciliation views, diagnostics, and audit search.
10. Background Workers
    Process reconciliation backlogs, outbox publishing, retry queues, data retention, and operational alerting.

### External Systems

- Odoo ERP/POS
- Databricks sync pipelines
- Edge Agent on Android HHTs
- FCC vendors: DOMS first, then Radix, Advatec, Petronite
- Optional external fiscalization integrations outside the middleware for some countries
- Azure Entra ID for employee authentication

### Key Runtime Flows

#### Normal Order in Default `CLOUD_DIRECT`

1. FCC pushes transaction to cloud ingestion API.
2. Ingestion API authenticates caller, resolves site/FCC, and persists raw message.
3. Transaction module normalizes to canonical form, deduplicates, and stores `PENDING`.
4. Odoo polls `GET /transactions?status=PENDING`.
5. Odoo creates order and calls acknowledge API.
6. Backend marks transaction `SYNCED_TO_ODOO` and publishes status event.
7. Edge Agent later syncs this status to hide already-consumed transactions locally.

#### Internet Down at Site

1. FCC push may fail or queue locally.
2. Edge Agent polls FCC over LAN, buffers locally, and later uploads backlog.
3. Cloud receives replayed transactions, deduplicates against FCC direct push and prior replays.
4. Odoo resumes cloud polling when internet returns.

#### Pre-Auth Reconciliation

1. Edge Agent posts pre-auth record asynchronously to cloud after LAN authorization.
2. Final dispense arrives from FCC or Edge replay.
3. Reconciliation module matches by correlation ID, then fallback heuristics.
4. Variance is auto-approved within tolerance or flagged for review beyond tolerance.
5. Canonical transaction remains available for Odoo polling using actual volume and actual amount.

## 5. Project Structure Recommendation

### Repository Strategy

Recommended approach: one backend repository with clear module boundaries, one edge repository, one portal repository, and one shared-contracts repository or folder set.

For the backend specifically, keep one deployable API solution and one worker solution in the same repository.

### Backend Solution Structure

```text
/src
  /Forecourt.Backend.Api
  /Forecourt.Backend.Workers
  /Forecourt.Backend.Contracts
  /Forecourt.Backend.SharedKernel
  /Modules
    /Configuration
    /Transactions
    /PreAuth
    /Reconciliation
    /OdooIntegration
    /EdgeControl
    /Observability
    /Audit
    /Adapters
      /Doms
      /Radix
      /Advatec
      /Petronite
/tests
  /ArchitectureTests
  /ModuleTests
  /IntegrationTests
  /ContractTests
/deploy
  /terraform or /cdk
  /pipelines
/docs
  /adr
  /api
```

### Internal Layering

- `Domain`: policies, aggregates, value objects, status transitions, reconciliation decisions
- `Application`: commands, queries, handlers, orchestration, idempotency rules
- `Infrastructure`: persistence, messaging, AWS integrations, adapter I/O, encryption, cache
- `Contracts`: API DTOs, event contracts, agent config contracts, Odoo/Databricks schemas

### Why Not Microservices Now

- Strong transactional coupling exists between ingest, dedup, reconciliation, and Odoo exposure.
- Operational complexity would rise materially with limited near-term value.
- Country growth from 5 to 12 is better handled by data partitioning and worker scale-out first.

## 6. Integration View

### Upstream and Downstream Systems

| System | Direction | Pattern | Notes |
|---|---|---|---|
| FCC | Upstream | HTTPS push, optional polling, vendor protocol | Default path is direct cloud push where supported |
| Edge Agent | Upstream and downstream | HTTPS APIs | Uploads buffered txns, posts pre-auth, fetches config and sync status |
| Odoo | Downstream consumer and acknowledgement source | Poll + acknowledge REST APIs | Middleware never pushes orders into Odoo |
| Databricks | Upstream | Batch/API sync | Master data only |
| External fiscalization | Conditional | Async integration or reference-only | Used only in `EXTERNAL_INTEGRATION` contexts if later brought in-scope |

### API Domains

- `/ingestion/fcc/*`
- `/ingestion/edge/*`
- `/transactions/*`
- `/preauth/*`
- `/config/*`
- `/agents/*`
- `/admin/*`
- `/health/*`

### Messaging and Eventing

Recommended pattern:

- transactional outbox in PostgreSQL
- background publisher to SNS/EventBridge for integration events
- SQS queues for worker-driven retries, alert processing, and review tasks

Rationale:

- keeps business writes atomic with event publication intent
- avoids RabbitMQ operational overhead unless protocol-specific routing becomes a hard requirement

### Retry and Idempotency

- FCC/Edge uploads use idempotency based on `fccTransactionId + siteCode`
- Odoo acknowledgement is idempotent by transaction ID
- Pre-auth creation uses site-scoped correlation IDs and deduplicates retries
- Replay processing always tolerates duplicate arrivals from FCC queue flush plus Edge backlog
- Non-retryable validation failures are parked for review, not endlessly retried

### Online/Offline Handling

- Cloud does not assume a single source of truth during outages; it assumes converging sources
- Reconciliation and duplicate policies must tolerate delayed, duplicated, and out-of-order arrivals
- Edge status-sync API supports hiding cloud-consumed transactions from offline Odoo polling

## 7. Security Architecture

### Identity Separation

Employee access and machine access must remain separate.

- Employee/portal/API user authentication: Azure Entra ID
- Edge Agent and FCC authentication: platform-managed machine identity

### Authentication

#### Employee Authentication

- Angular portal authenticates users with Azure Entra ID using OIDC Authorization Code with PKCE.
- Backend admin APIs validate Entra JWT access tokens.
- Use Entra app roles or security groups mapped to application roles.

#### Edge Agent Authentication

- Each primary Edge Agent device receives a site-bound device identity during provisioning.
- Preferred model: per-device client certificate stored in Android Keystore plus short-lived signed access token.
- Bootstrap model: QR code contains one-time registration token only; it must not be the long-lived runtime secret.

#### FCC Authentication

- Preferred for modern controllers: mTLS with site/FCC certificate.
- Fallback for limited devices: per-site API credential with HMAC signature and strict replay window.
- Capability variance by vendor should be treated as a design-risk item and validated early.

### Authorization

Recommended RBAC model with constrained ABAC filters.

- RBAC for employee roles: System Administrator, Operations Manager, Site Supervisor, Auditor
- ABAC filters for legal entity, country, region, and optionally site group
- No cross-legal-entity data access by default

### Secrets Handling

- AWS Secrets Manager for backend-managed secrets
- AWS KMS for envelope encryption of sensitive credentials at rest
- No plain FCC credentials in configuration tables; store references to secrets where possible

### Encryption

- TLS 1.2+ in transit for all cloud APIs
- Field-level protection or encrypted columns for FCC credentials and sensitive identifiers where justified
- PostgreSQL, S3, queueing, and logs encrypted at rest with KMS-managed keys

### Audit Logging

- Immutable audit events for config changes, manual retries, reconciliation overrides, role changes, and device registration
- Separate business audit trail from high-volume technical logs

### Tenant and Site Isolation

- Legal entity is the primary isolation boundary in application logic and reporting
- Row-level security can be added in PostgreSQL for defense in depth, but application-enforced tenant filtering remains mandatory
- Site-scoped machine identities cannot access data outside the assigned site

## 8. Deployment Architecture

### Recommended Deployment Model

AWS regional deployment with active-active application instances in one primary region and multi-AZ managed data services.

Recommended core services:

- Amazon ECS Fargate for API and worker containers
- Amazon Aurora PostgreSQL for transactional data
- Amazon ElastiCache Redis for hot configuration cache, rate limiting, and transient coordination
- Amazon SQS and SNS or EventBridge for asynchronous processing
- Amazon S3 for raw payload archive, large audit exports, and diagnostic bundles
- AWS WAF plus CloudFront for portal delivery
- API Gateway or ALB fronting backend APIs, selected based on mTLS and protocol needs
- Amazon CloudWatch, AWS X-Ray/OpenTelemetry collector, and centralized log analytics

### Environment Strategy

- `dev`
- `test`
- `uat`
- `prod`

Country onboarding should be data/config-driven inside shared environments, not separate per-country stacks, unless regulation later forces country-resident hosting.

### High Availability and Disaster Recovery

- Multi-AZ Aurora
- stateless API/workers across at least two AZs
- S3 versioning and lifecycle policies for archive
- defined RPO/RTO targets, with point-in-time database restore
- warm DR strategy in a secondary AWS region for production only after MVP stabilization

### Scaling Approach

- Scale ingestion API horizontally based on request count and queue depth
- Scale workers independently based on backlog and reconciliation latency
- Partition large transaction tables by time and possibly legal entity for sustained performance
- Use read replicas or reporting projections only if portal/reporting load begins to impact transactional workloads

### Observability

- Structured logs with site, legal entity, FCC vendor, ingestion mode, transaction ID, and correlation ID
- Metrics for ingest rate, dedup rate, reconciliation lag, Odoo poll latency, Edge backlog age, and auth failures
- Distributed tracing across API, queue, worker, and database boundaries where useful
- Operational dashboards by country and by site cohort

## 9. Key Design Decisions

### Decision 1: Modular Monolith Over Microservices

Reason:

- strongest balance of delivery speed, consistency, and operability for MVP and early scale

Trade-off:

- reduced independent deployability of modules

### Decision 2: Odoo Pull Model Preserved

Reason:

- matches requirements and avoids tight coupling to Odoo transaction timing

Trade-off:

- requires clean acknowledgement semantics and portal visibility into poll lag

### Decision 3: Default `CLOUD_DIRECT`, Edge as Safety Net

Reason:

- minimizes field dependency on the HHT in steady state while preserving offline resilience

Trade-off:

- duplicate arrival is normal and must be engineered as a first-class condition

### Decision 4: Azure Entra for Employees, Separate Device Identity for Machines

Reason:

- employee SSO should use enterprise identity, but station devices must not depend on human tokens

Trade-off:

- two identity models must be managed and documented

### Decision 5: AWS Managed Messaging Instead of Self-Managed Broker

Reason:

- lowers ops burden and integrates cleanly with ECS/Fargate

Trade-off:

- less broker-specific routing flexibility than RabbitMQ

### Assumptions

- One active FCC per connected site
- One designated primary Edge Agent per site for MVP
- Odoo can poll and acknowledge at a cadence compatible with business operations
- Country residency requirements do not currently force per-country AWS accounts or regions

### Known Risks

- FCC security capabilities may vary significantly by vendor and country deployment
- Direct FCC internet reachability may be inconsistent, increasing `RELAY` mode usage
- Portal and reconciliation read workload could affect OLTP performance if not isolated carefully
- Cross-country timezone and fiscal-receipt nuances can create subtle reconciliation defects if canonical rules are weak

### Areas Needing Validation / PoC

- FCC-to-cloud authentication capability by vendor
- Throughput and latency of bulk replay after multi-day outage at many sites
- Odoo polling cadence and acknowledgement contract under end-of-shift bulk operations
- Partitioning strategy for transaction and audit tables at projected growth

## 10. Non-Functional Requirements Mapping

| NFR Area | HLD Response |
|---|---|
| Performance | Stateless API scale-out, queue-backed workers, caching for hot config, partitioned transaction storage |
| Availability | Multi-AZ deployment, duplicate-tolerant ingestion, decoupled workers, no synchronous dependency on Odoo for ingest |
| Recoverability | Outbox, retries, DLQ patterns, raw payload retention, PITR backups, replay-safe APIs |
| Supportability | Structured diagnostics, portal admin APIs, correlation IDs, operational dashboards, explicit error classifications |
| Operability | Managed AWS services, separate scaling of APIs/workers, health endpoints, version checks for agents |
| Extensibility | Adapter module boundary, configuration-driven routing, event contracts, domain-based module separation |

## 11. Recommended Technology Direction

### Core Backend

- .NET 10 / ASP.NET Core for APIs and background workers
- MediatR-style application orchestration only if it matches team standards; avoid over-abstraction
- FluentValidation or equivalent for command/input policy enforcement
- Entity Framework Core for primary persistence, with selective Dapper/Npgsql usage for high-volume queries if required

### Data and Messaging

- Aurora PostgreSQL
- Redis
- SQS + SNS/EventBridge
- S3 for immutable payload and audit archive

### Security and Identity

- Azure Entra ID for employee authentication and RBAC claims
- AWS KMS, Secrets Manager, Private CA where client certificates are adopted

### Observability

- OpenTelemetry
- CloudWatch dashboards and alarms
- Central log search with retained structured logs

### Design Patterns

- modular monolith
- outbox/inbox
- idempotent command processing
- adapter/plugin model for FCC vendors
- configuration-driven policy resolution by legal entity and site

## 12. Open Questions / Pending Decisions

1. Which FCC vendors support modern TLS and client certificates for direct cloud push, and which require compensating controls?
2. Is the 12-country rollout expected in one shared AWS region initially, or are there country-specific data-hosting constraints not yet captured?
3. What Odoo polling cadence and batch size are acceptable from an operational perspective at peak trading hours?
4. Should reconciliation review remain entirely in the Angular portal, or do some exception actions need API-only integration for back-office tooling?
5. What retention split is required between OLTP transaction tables and long-term audit/archive stores beyond the stated 7-year default?
6. Is `EXTERNAL_INTEGRATION` purely informational in phase 1, or does the cloud backend need to orchestrate callbacks to any tax systems later?
