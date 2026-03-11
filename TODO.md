# Forecourt Middleware Platform — Pre-Development TODO

**Purpose:** Complete these items in order before starting development. Each tier must be substantially complete before the next begins. Items within a tier can be parallelized.

**Status Key:** `[ ]` Not Started · `[~]` In Progress · `[x]` Done

---

## Tier 1 — Foundational Contracts (Blocks Everything)

These are the load-bearing definitions. If any of these shift during development, every component refactors.

### 1.1 Canonical Data Model Specification
- [x] Define `CanonicalTransaction` — all fields, types, nullability, validation rules, which system produces each field
- [x] Define `PreAuthRecord` — full lifecycle fields including FCC correlation, amounts, timestamps, status
- [x] Define `PumpStatus` — live pump state model (pump number, nozzle, state, current volume/amount, product)
- [x] Define `SiteConfig` — full configuration object pushed from cloud to Edge Agent
- [x] Define `DeviceRegistration` — registration request/response model
- [x] Define `TelemetryPayload` — health metrics reported by Edge Agent
- [x] Define all shared enums:
  - [ ] `TransactionStatus` (PENDING, SYNCED, SYNCED_TO_ODOO, STALE_PENDING, DUPLICATE, ARCHIVED)
  - [ ] `PreAuthStatus` (PENDING, AUTHORIZED, DISPENSING, COMPLETED, CANCELLED, EXPIRED, FAILED)
  - [ ] `IngestionMode` (CLOUD_DIRECT, RELAY, BUFFER_ALWAYS)
  - [ ] `IngestionMethod` (PUSH, PULL, HYBRID)
  - [ ] `FccVendor` (DOMS, RADIX, ADVATEC, PETRONITE)
  - [ ] `ConnectivityState` (FULLY_ONLINE, INTERNET_DOWN, FCC_UNREACHABLE, FULLY_OFFLINE)
  - [ ] `SiteOperatingModel` (COCO, CODO, DODO, DOCO)
  - [ ] `FiscalizationMode` (FCC_DIRECT, EXTERNAL_INTEGRATION, NONE)
- [ ] Document field-level mapping: raw FCC payload → canonical model (for DOMS adapter as reference)
- [ ] Define model versioning strategy (how to evolve without breaking Edge Agents in the field)

### 1.2 State Machine Formal Definitions
- [ ] **Transaction Lifecycle** — every state, every valid transition, trigger, guard conditions, side effects, invalid transition handling
- [ ] **Pre-Auth Lifecycle** — states, transitions, timeout rules, expiry behavior, cancellation semantics
- [ ] **Edge Sync Record State** — per-record states on Edge Agent (PENDING → UPLOADED → SYNCED → SYNCED_TO_ODOO → ARCHIVED), transition triggers
- [ ] **Connectivity State Machine** — transitions between FULLY_ONLINE / INTERNET_DOWN / FCC_UNREACHABLE / FULLY_OFFLINE, what each module does in each state
- [ ] **Reconciliation State** — matched/unmatched/variance-flagged/approved/rejected states and transitions
- [ ] Document state machine diagrams (Mermaid or PlantUML) for inclusion in design docs

### 1.3 API Contract Specifications (OpenAPI / Swagger)
- [ ] **FCC → Cloud Ingestion API** — `POST /api/v1/transactions/ingest` (vendor-agnostic envelope with raw payload)
- [ ] **Edge Agent → Cloud Upload API** — `POST /api/v1/transactions/upload` (batch upload with per-record response)
- [ ] **Odoo → Cloud Poll API** — `GET /api/v1/transactions` (pagination, filtering, field selection)
- [ ] **Odoo → Cloud Acknowledge API** — `POST /api/v1/transactions/acknowledge` (batch acknowledge with odooOrderId)
- [ ] **Edge Agent → Cloud SYNCED_TO_ODOO Status API** — `GET /api/v1/transactions/synced-status`
- [ ] **Edge Agent → Cloud Pre-Auth Forward API** — `POST /api/v1/preauth`
- [ ] **Edge Agent → Cloud Registration API** — `POST /api/v1/agent/register`
- [ ] **Edge Agent → Cloud Config API** — `GET /api/v1/agent/config`
- [ ] **Edge Agent → Cloud Version Check API** — `GET /api/v1/agent/version-check`
- [ ] **Edge Agent → Cloud Telemetry API** — `POST /api/v1/agent/telemetry`
- [ ] **Cloud Health Endpoint** — `GET /health`
- [ ] **Databricks → Cloud Master Data Sync APIs** — legal-entities, sites, pumps, products, operators
- [ ] **Edge Agent Local API** — all 7 endpoints (transactions, transaction by id, pump-status, preauth, preauth cancel, acknowledge, status)
- [ ] Define standard error response envelope (consistent across all APIs)
- [ ] Define pagination convention (cursor-based vs offset, page size limits)
- [ ] Define filtering/sorting convention (query parameter syntax)
- [ ] Define date/time format convention (UTC ISO 8601 everywhere)
- [ ] Define API versioning strategy (URL path versioning, header versioning, or both)

### 1.4 Database Schema Design

#### Cloud (PostgreSQL / Aurora)
- [ ] Table definitions: transactions, pre_auth_records, sites, pumps, products, legal_entities, operators, fcc_configs, audit_events, agent_registrations
- [ ] Column types, constraints, NOT NULL rules, defaults, check constraints
- [ ] Primary keys, foreign keys, unique constraints
- [ ] Index strategy (covering queries from Odoo poll, portal search, reconciliation matching)
- [ ] Multi-tenancy enforcement mechanism (row-level security policies vs application-level filtering) — decide and document
- [ ] Partitioning strategy (by legal_entity_id? by created_at time range? both?)
- [ ] Soft delete vs hard delete policy per table
- [ ] Migration tooling decision (EF Core Migrations vs FluentMigrator vs raw SQL) — decide and document
- [ ] Seed data strategy (countries, default configs, test legal entities)

#### Edge Agent (SQLite / Room)
- [ ] Room entity definitions: BufferedTransaction, PreAuthRecord, SyncState, AgentConfig, AuditLog
- [ ] DAO interface definitions with exact queries
- [ ] Index strategy for local API query performance (by timestamp, pump, status)
- [ ] WAL mode configuration specifics
- [ ] Retention/cleanup SQL (delete SYNCED_TO_ODOO records older than X days)
- [ ] Schema migration strategy for APK updates (Room auto-migration vs manual migration)
- [ ] Integrity check and corruption recovery procedure

### 1.5 FCC Adapter Interface Contracts
- [ ] Define `IFccAdapter` interface for Cloud side (.NET):
  - [ ] `NormalizeTransaction(rawPayload) → CanonicalTransaction`
  - [ ] `ValidatePayload(rawPayload) → ValidationResult`
  - [ ] `FetchTransactions(cursor) → TransactionBatch` (for pull mode)
  - [ ] `GetAdapterMetadata() → AdapterInfo`
- [ ] Define `IFccAdapter` interface for Edge Agent side (Kotlin):
  - [ ] `normalize(rawPayload) → CanonicalTransaction`
  - [ ] `sendPreAuth(command) → PreAuthResult`
  - [ ] `getPumpStatus() → List<PumpStatus>`
  - [ ] `heartbeat() → Boolean`
  - [ ] `fetchTransactions(cursor) → TransactionBatch`
- [ ] Document semantic equivalence between .NET and Kotlin interfaces
- [ ] Define adapter registration/discovery mechanism (factory pattern, config-driven selection)
- [ ] Define DOMS adapter specifics: protocol type (REST/TCP/SOAP), authentication, endpoint paths, payload format

---

## Tier 2 — Detailed Design Decisions (Before Sprint 1)

These flesh out the design enough that developers can write code without ambiguity.

### 2.1 Error Handling Strategy
- [ ] Define standard error response envelope: `{ errorCode, message, details, traceId, timestamp }`
- [ ] Define error code taxonomy:
  - [ ] Validation errors (missing fields, invalid values, schema mismatch)
  - [ ] FCC communication errors (timeout, connection refused, protocol error)
  - [ ] Authentication/authorization errors (expired token, invalid API key, scope mismatch)
  - [ ] Conflict errors (duplicate transaction, stale config version)
  - [ ] Infrastructure errors (database unavailable, queue full)
- [ ] Define retry semantics per error category (retryable vs terminal)
- [ ] Define error propagation paths:
  - [ ] FCC → Cloud Adapter → Ingestion API → response to FCC
  - [ ] FCC → Edge Adapter → Local Buffer → Cloud Upload → response handling
  - [ ] Edge Local API → Odoo POS error responses
- [ ] Define quarantine behavior for permanently failed records
- [ ] Define alerting triggers (which errors trigger ops alerts, which are silently logged)

### 2.2 Deduplication Strategy (Detailed)
- [ ] Define exact dedup key: `fccTransactionId + siteCode` (confirm or revise)
- [ ] Define dedup time window (is a transaction with same fccTransactionId after 30 days a new transaction or duplicate?)
- [ ] Define what happens to duplicates: silently dropped? logged? stored with DUPLICATE status?
- [ ] Define Edge Agent pre-filtering responsibility (avoid uploading already-confirmed records)
- [ ] Define behavior when FCC reuses transaction IDs (if applicable per vendor)
- [ ] Define dedup for pre-auth records (separate key? correlationId-based?)

### 2.3 Reconciliation Rules Engine Design
- [ ] Define matching algorithm with priority:
  1. FCC correlation ID (exact match)
  2. Pump + nozzle + time window (configurable window, e.g., ±5 minutes)
  3. odooOrderId echoed by FCC (if available)
- [ ] Define tolerance configuration structure: `{ amountTolerancePercent, amountToleranceAbsolute, timeWindowMinutes }`
- [ ] Define tolerance scope: per legal entity? per site? per product?
- [ ] Define auto-approve vs flag-for-review logic
- [ ] Define what "flag for Ops Manager review" means technically (status field, notification, queue in portal)
- [ ] Define unmatched transaction handling (how long to wait for a match, when to give up)

### 2.4 Configuration Schema (Full)
- [ ] Define complete Edge Agent config object with all fields, types, defaults, constraints:
  - [ ] FCC connection: vendor, host, port, credentials reference, protocol type
  - [ ] Polling: pullIntervalSeconds, batchSize, cursorStrategy
  - [ ] Sync: cloudBaseUrl, uploadBatchSize, syncIntervalSeconds, statusPollIntervalSeconds
  - [ ] Buffer: retentionDays, maxRecords, cleanupIntervalHours
  - [ ] API: localApiPort, enableLanApi, lanApiKey
  - [ ] Telemetry: telemetryIntervalSeconds, logLevel
  - [ ] Fiscalization: mode, requireCustomerTaxId, fiscalReceiptRequired
  - [ ] Site: siteCode, legalEntityId, timezone, currency, operatingModel
- [ ] Identify which fields are set at provisioning vs cloud-updatable
- [ ] Identify which fields require APK restart vs hot-reloadable
- [ ] Define config version field and backward compatibility handling
- [ ] Define cloud-side config management (how ops edits configs in portal, how they're pushed)

### 2.5 Security Implementation Plan
- [ ] **Device Registration Flow:**
  - [ ] Bootstrap token generation (portal or API), single-use, expiry
  - [ ] Registration request → cloud validates → issues deviceId + deviceToken (JWT)
  - [ ] JWT claims: deviceId, siteCode, legalEntityId, roles, expiry
  - [ ] Token refresh mechanism and expiry duration
  - [ ] Decommission flow (revoke token, block device)
- [ ] **Cloud API Authentication:**
  - [ ] FCC ingestion: API key per FCC (how generated, rotated, revoked)
  - [ ] Odoo integration: API key per legal entity (or service account)
  - [ ] Edge Agent: device JWT bearer token
  - [ ] Portal: Cognito user pools, token validation middleware, role mapping
  - [ ] Master Data Sync: service-to-service auth (API key or IAM role)
- [ ] **Edge Agent Security:**
  - [ ] Android Keystore usage: what keys stored, key aliases, encryption algorithms
  - [ ] EncryptedSharedPreferences: what config values encrypted
  - [ ] LAN API key: generation, storage, distribution to non-primary HHTs
  - [ ] Certificate pinning: which domains, pin rotation procedure
- [ ] **Data Protection:**
  - [ ] Fields that must never appear in logs (FCC credentials, tokens, customer TIN)
  - [ ] SQLCipher decision for Edge Agent (MVP: no, post-MVP: evaluate)
  - [ ] Cloud database encryption at rest (Aurora default encryption)
  - [ ] S3 archive encryption (SSE-S3 or SSE-KMS)

### 2.6 Event Schema Design
- [ ] Define event envelope: `{ eventId, eventType, timestamp, source, correlationId, legalEntityId, siteCode, payload }`
- [ ] Define event types:
  - [ ] TransactionIngested, TransactionDeduplicated, TransactionSyncedToOdoo
  - [ ] PreAuthCreated, PreAuthAuthorized, PreAuthCompleted, PreAuthCancelled, PreAuthExpired
  - [ ] ReconciliationMatched, ReconciliationVarianceFlagged, ReconciliationApproved
  - [ ] AgentRegistered, AgentConfigUpdated, AgentHealthReported
  - [ ] ConnectivityChanged, BufferThresholdExceeded
  - [ ] MasterDataSynced, ConfigChanged
- [ ] Define event versioning strategy (schema evolution without breaking consumers)
- [ ] Define event storage: PostgreSQL event table + S3 archive for long-term retention
- [ ] Define event consumption: which events trigger downstream actions vs. audit-only

---

## Tier 3 — Engineering Practices & Infrastructure (Parallel with Early Dev)

### 3.1 Project Scaffolding
- [ ] **Cloud Backend (.NET):**
  - [ ] Create solution structure: API host project, Worker host project, shared domain library, adapter projects, test projects
  - [ ] Configure dependency injection, logging (Serilog), configuration (appsettings per environment)
  - [ ] Set up EF Core with PostgreSQL, initial migration
  - [ ] Set up health check endpoints
  - [ ] Set up Swagger/OpenAPI generation from controllers
- [ ] **Edge Agent (Kotlin/Android):**
  - [ ] Create Android project with recommended package structure
  - [ ] Configure Gradle build (Kotlin DSL)
  - [ ] Set up Room database with initial entities and migrations
  - [ ] Set up Ktor embedded server scaffold
  - [ ] Set up foreground service and boot receiver
  - [ ] Set up dependency injection (Koin or Hilt)
- [ ] **Angular Portal:**
  - [ ] Create Angular project with module structure
  - [ ] Configure routing, auth guard (Cognito), HTTP interceptors
  - [ ] Set up component library / design system baseline

### 3.2 Repository & Branching Strategy
- [ ] Decide: monorepo (all 3 components) vs separate repos per component
- [ ] Define branching model: trunk-based with short-lived feature branches (recommended) or GitFlow
- [ ] Define branch naming convention: `feature/`, `bugfix/`, `release/`
- [ ] Define PR review requirements (approvals, CI checks must pass)
- [ ] Define commit message convention (Conventional Commits recommended)
- [ ] Define release tagging strategy and changelog generation

### 3.3 CI/CD Pipeline Design
- [ ] **Cloud Backend Pipeline:**
  - [ ] Build → unit tests → integration tests → Docker image → push to ECR
  - [ ] Deploy to dev → staging → UAT → production (with approval gates)
  - [ ] Database migration step in pipeline
  - [ ] Infrastructure provisioning (Terraform or CDK)
- [ ] **Edge Agent Pipeline:**
  - [ ] Build → unit tests → instrumented tests (emulator) → sign APK → publish
  - [ ] APK distribution: internal testing → UAT → Sure MDM production push
  - [ ] APK signing key management (secure, not in repo)
- [ ] **Angular Portal Pipeline:**
  - [ ] Build → unit tests → e2e tests → deploy to S3 + CloudFront invalidation
  - [ ] Environment-specific builds (API base URL, Cognito config)
- [ ] Define environment list: local-dev, dev (shared), staging, UAT, production
- [ ] Define infrastructure as code tool (Terraform recommended) and state management

### 3.4 Testing Strategy
- [ ] **Unit Testing:**
  - [ ] .NET: xUnit + Moq/NSubstitute, target coverage for domain logic and adapters
  - [ ] Kotlin: JUnit 5 + MockK, target coverage for adapter, buffer, sync, pre-auth logic
  - [ ] Angular: Jasmine/Karma for component tests
- [ ] **Integration Testing:**
  - [ ] Cloud: Testcontainers (PostgreSQL, Redis) for repository and API tests
  - [ ] Edge Agent: Robolectric for Android framework tests, Room in-memory DB tests
  - [ ] Cross-component: Edge Agent upload → Cloud ingest → verify stored (needs test harness)
- [ ] **FCC Simulator:**
  - [ ] Define scope: DOMS protocol simulation for dev and CI
  - [ ] Define features: configurable responses, error injection, transaction generation
  - [ ] Define interface: standalone process or in-process test fixture
- [ ] **Offline Scenario Testing:**
  - [ ] Define how to simulate network partitions reproducibly
  - [ ] Define test scenarios: internet drop during upload, FCC LAN drop during poll, recovery after 1hr/1day/7day outage
  - [ ] Define where these tests run (local, CI, dedicated test environment)
- [ ] **Load / Performance Testing:**
  - [ ] Tool selection: k6, Artillery, or JMeter
  - [ ] Target: sustain 2M transactions/day through ingestion pipeline
  - [ ] Target: Edge Agent replay 30,000 buffered transactions without OOM or battery drain
- [ ] **Contract Testing:**
  - [ ] Evaluate Pact or similar for Cloud ↔ Edge Agent API compatibility
  - [ ] Ensure Edge Agent APK and Cloud API stay compatible across version skew

### 3.5 Observability & Monitoring Design
- [ ] **Cloud Backend:**
  - [ ] Structured logging format (JSON, correlation ID propagation)
  - [ ] Logging destination: CloudWatch Logs (or ELK if preferred)
  - [ ] Metrics: request rate, latency percentiles, error rate, queue depth, DB connection pool
  - [ ] Dashboards: ingestion throughput, Odoo poll latency, reconciliation match rate, buffer depths
- [ ] **Edge Agent:**
  - [ ] Telemetry payload fields: battery %, storage free, buffer depth, FCC heartbeat age, last sync timestamp, sync lag, app version, error counts
  - [ ] Local log rotation: max size, max files, log level (configurable from cloud)
  - [ ] Diagnostics screen wireframe: what the supervisor sees
- [ ] **Alerting Rules:**
  - [ ] FCC heartbeat stale > X minutes → alert
  - [ ] Transaction buffer depth > Y → alert
  - [ ] Cloud sync lag > Z hours → alert
  - [ ] Ingestion error rate spike → alert
  - [ ] Edge Agent offline > N hours → alert
  - [ ] Master data stale > threshold → alert
  - [ ] Define notification channels: email, SMS, PagerDuty, portal notification
- [ ] Define on-call runbook structure (one runbook per alert type)

### 3.6 Coding Conventions
- [ ] **.NET Conventions:**
  - [ ] Naming: PascalCase classes/methods, camelCase locals, _prefixed private fields
  - [ ] Project structure: feature-folder or layer-based (decide)
  - [ ] Async/await patterns, CancellationToken propagation
  - [ ] Result pattern vs exceptions for domain errors (decide)
  - [ ] Logging conventions: structured logging, what to log at Info/Warning/Error
- [ ] **Kotlin Conventions:**
  - [ ] Package naming aligned with project structure
  - [ ] Coroutine patterns: CoroutineScope management, Dispatcher usage
  - [ ] Room conventions: entity naming, DAO method naming
  - [ ] Ktor route organization
- [ ] **Shared Conventions:**
  - [ ] Date/time: UTC ISO 8601 everywhere, timezone only in display layer
  - [ ] Currency: minor units (cents) as Long/BigDecimal, never floating point
  - [ ] IDs: UUID v4 for middleware-generated IDs, preserve original FCC IDs as-is
  - [ ] API field naming: camelCase in JSON payloads

---

## Tier 4 — PoC & Validation (Before Committing to Implementation Approach)

### 4.1 DOMS FCC Protocol PoC
- [ ] Obtain DOMS protocol documentation
- [ ] Validate: REST or TCP or SOAP?
- [ ] Validate: poll/fetch API exists for transaction retrieval
- [ ] Validate: pre-auth command API exists and format
- [ ] Validate: heartbeat/health mechanism
- [ ] Build minimal adapter prototype that can fetch one transaction from DOMS
- [ ] Document findings and update adapter interface contract if needed

### 4.2 Urovo i9100 Hardware Validation
- [ ] Verify available free storage after OS + Odoo POS
- [ ] Verify WiFi LAN reliability (sustained connection to FCC IP, no drops)
- [ ] Verify dual connectivity (WiFi LAN + SIM internet simultaneously)
- [ ] Verify foreground service behavior under Android 12 battery optimization
- [ ] Verify Ktor embedded server memory footprint (<100MB RAM target)
- [ ] Verify Room/SQLite performance with 30,000+ records
- [ ] Document any device-specific quirks or limitations

### 4.3 Edge Agent Background Execution PoC
- [ ] Prototype foreground service with persistent notification
- [ ] Test: does Android kill the service after X hours? After Doze?
- [ ] Test: polling every 30s — sustained over 8+ hours
- [ ] Test: WorkManager as backup scheduling mechanism
- [ ] Test: boot receiver — does agent auto-start reliably?
- [ ] Document power management configuration required

### 4.4 .NET MAUI Evaluation (If Team Prefers .NET)
- [ ] Build minimal .NET MAUI app on Urovo i9100
- [ ] Measure APK size overhead (~30-50MB expected)
- [ ] Measure startup time vs native Kotlin
- [ ] Test Android Keystore access from .NET MAUI
- [ ] Test background service reliability
- [ ] Make go/no-go decision and document rationale
- [ ] **If go:** revise Edge Agent HLD, project structure, and technology choices

---

## Tier 5 — Phased Development Plan

### 5.1 Delivery Phase Planning
- [ ] Define sprint cadence (2-week recommended)
- [ ] Map the phases below to sprints with estimated capacity
- [ ] Identify team allocation: who works on Cloud vs Edge vs Portal
- [ ] Identify external dependencies and their timelines (DOMS documentation, Urovo hardware, Sure MDM access, Cognito setup, AWS account provisioning)

### 5.2 Phase 0 — Foundations (Sprints 1–2)
- [ ] Cloud: solution scaffold, database setup, health endpoint, CI pipeline
- [ ] Edge: Android project scaffold, foreground service, Room DB, CI pipeline
- [ ] Shared: canonical model types generated/created in both codebases
- [ ] Infra: dev environment provisioned (AWS dev account, PostgreSQL, Redis)
- [ ] FCC Simulator: basic DOMS transaction generator for dev use

### 5.3 Phase 1 — Cloud Core Ingestion (Sprints 3–5)
- [ ] DOMS adapter (cloud-side) — normalize FCC payload to canonical model
- [ ] Ingestion API — receive, validate, deduplicate, store transactions
- [ ] Transaction storage — PostgreSQL with dedup logic
- [ ] Odoo Poll API — paginated query with filtering
- [ ] Odoo Acknowledge API — batch acknowledge, mark SYNCED_TO_ODOO
- [ ] Master Data Sync API — legal entities, sites, pumps, products, operators
- [ ] Event publishing — TransactionIngested, TransactionSyncedToOdoo
- [ ] Unit + integration tests for all above

### 5.4 Phase 2 — Edge Agent Core (Sprints 4–7, overlaps with Phase 1)
- [ ] FCC adapter (DOMS, LAN poll) — connect to FCC, fetch transactions, normalize
- [ ] SQLite buffer — Room entities, DAOs, write + query + cleanup
- [ ] Connectivity manager — internet health ping, FCC heartbeat, state transitions
- [ ] Local REST API (Ktor) — all 7 endpoints
- [ ] Pre-auth handler — receive from Odoo POS, send to FCC, store locally, return result
- [ ] Ingestion orchestrator — route by ingestion mode (CLOUD_DIRECT catch-up, RELAY, BUFFER_ALWAYS)
- [ ] Unit + instrumented tests

### 5.5 Phase 3 — Cloud ↔ Edge Integration (Sprints 6–8)
- [ ] Edge → Cloud upload (batch transactions, replay ordering, backoff)
- [ ] Edge → Cloud pre-auth forwarding
- [ ] Edge ← Cloud SYNCED_TO_ODOO status sync
- [ ] Edge ← Cloud config pull
- [ ] Edge → Cloud telemetry reporting
- [ ] Device registration + provisioning flow (QR code)
- [ ] Version compatibility check
- [ ] End-to-end scenario tests: online, offline, recovery, pre-auth

### 5.6 Phase 4 — Pre-Auth & Reconciliation (Sprints 7–9)
- [ ] Cloud: pre-auth record storage and lifecycle management
- [ ] Cloud: reconciliation matching engine (correlation ID, pump+time window)
- [ ] Cloud: variance detection and tolerance checking
- [ ] Cloud: auto-approve and flag-for-review logic
- [ ] Cloud: reconciliation query API for portal
- [ ] Integration tests for reconciliation scenarios

### 5.7 Phase 5 — Angular Portal (Sprints 8–11)
- [ ] Authentication: Cognito integration, role-based access
- [ ] Transaction browser: search, filter, detail view
- [ ] Reconciliation workbench: matched/unmatched/flagged queues, approve/reject
- [ ] Agent health dashboard: per-site agent status, buffer depths, connectivity
- [ ] Site configuration management: FCC config, ingestion mode, tolerances
- [ ] Audit log viewer

### 5.8 Phase 6 — Hardening & Production Readiness (Sprints 10–12)
- [ ] Load testing at target scale (2M txns/day)
- [ ] Offline scenario stress testing (7-day outage replay)
- [ ] Security review: penetration testing, credential rotation test, token expiry handling
- [ ] Monitoring and alerting setup (all rules active, runbooks written)
- [ ] Disaster recovery drill (database failover, Edge Agent corruption recovery)
- [ ] Documentation: operations guide, troubleshooting guide, API reference
- [ ] UAT with field devices at representative stations
- [ ] Production deployment runbook

---

## Cross-Cutting Concerns (Track Throughout)

- [ ] Ensure UTC ISO 8601 dates used consistently across all APIs and stores
- [ ] Ensure currency stored as minor units (cents) everywhere, never floating point
- [ ] Ensure fccTransactionId treated as opaque string, never parsed or assumed unique globally
- [ ] Ensure multi-tenancy (legalEntityId) enforced on every query and API call
- [ ] Ensure no secrets in logs, no customer TIN in logs unless strictly required
- [ ] Ensure correlation ID propagated from ingestion through to Odoo acknowledge
- [ ] Ensure all API changes are backward-compatible or version-bumped
- [ ] Ensure Edge Agent APK and Cloud API version compatibility is enforced

---

*Last updated: 2026-03-11*
