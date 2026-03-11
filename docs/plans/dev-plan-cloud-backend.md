# Cloud Backend — Phased Development Plan

**Agent System Prompt:** Always prepend `docs/plans/agent-prompt-cloud-backend.md` when assigning any task below.

**Sprint Cadence:** 2-week sprints

---

## Phase 0 — Foundations (Sprints 1–2)

### CB-0.1: Solution Scaffold & Project Structure

**Sprint:** 1
**Prereqs:** None
**Estimated effort:** 1–2 days

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-1-project-scaffolding.md` — §5.1 (Cloud Backend section, the definitive scaffold spec)
- `WIP-HLD-Cloud-Backend.md` — §5.2 (Project Structure tree)
- `docs/specs/foundation/coding-conventions.md` — .NET naming/patterns

**Task:**
Create the complete .NET 10 solution structure from scratch. The existing `src/cloud/` directory has a partial scaffold — verify it matches the spec and fill gaps.

**Detailed instructions:**
1. Create the solution file `FccMiddleware.sln` with all 12 projects per the scaffolding spec §5.1
2. Set up project references following the dependency flow: Api/Worker → Application → Domain; Api/Worker → Infrastructure; Infrastructure → Domain; Adapters → Domain
3. `FccMiddleware.Domain` must have ZERO NuGet package references — enforce this
4. Add NuGet packages per the scaffolding spec table (Serilog, MediatR, FluentValidation, Npgsql.EF, StackExchange.Redis, xUnit, NSubstitute, FluentAssertions, Testcontainers, NetArchTest.Rules)
5. Create `Program.cs` for both Api and Worker hosts with DI registration stubs, Serilog configuration (JSON console + CloudWatch), and configuration binding (`appsettings.json` + `appsettings.{Environment}.json`)
6. Create `appsettings.Development.json` with placeholder connection strings for PostgreSQL and Redis
7. Set up the `Result<T>` type in `Application/Common/` (already exists — verify it matches the Result pattern)

**Acceptance criteria:**
- `dotnet build` succeeds with zero warnings
- `dotnet test` discovers all test projects (0 tests OK at this stage)
- Solution structure matches `tier-3-1-project-scaffolding.md` §5.1 exactly
- Serilog writes structured JSON to console on startup

---

### CB-0.2: Database Setup — EF Core + Initial Migration

**Sprint:** 1
**Prereqs:** CB-0.1
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — the COMPLETE database spec (all tables, columns, indexes, constraints, partitioning, multi-tenancy)
- `db/ddl/001-cloud-schema.sql` — reference DDL (use as cross-check, NOT as the EF Core source of truth)
- `db/reference/seed-data-strategy.md` — seed data definitions
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — status enum values used in CHECK constraints

**Task:**
Implement the full cloud PostgreSQL schema in EF Core within the Infrastructure project.

**Detailed instructions:**
1. Create `FccMiddlewareDbContext` in `Infrastructure/Persistence/`
2. Create EF Core entity configurations (Fluent API, NOT data annotations) for ALL 10 cloud tables:
   - Master data: `legal_entities`, `sites`, `pumps`, `products`, `operators`
   - Transactional: `transactions` (partitioned), `pre_auth_records`
   - Config/registration: `fcc_configs`, `agent_registrations`
   - Audit/outbox: `audit_events` (partitioned), `outbox_messages`
3. Implement EF Core global query filters on `LegalEntityId` for all tenant-scoped entities (§5.4 of DB schema spec, decision D1)
4. Create the initial EF Core migration `001_InitialSchema`
5. Create ALL indexes defined in §5.2 of the DB schema spec — especially partial indexes with WHERE clauses
6. Implement composite PKs for partitioned tables: `(Id, CreatedAt)` for transactions and audit_events
7. Implement soft-delete configuration for master data tables (`is_active`, `deactivated_at`)
8. Implement the seed data from `db/reference/seed-data-strategy.md` using EF Core `HasData()`
9. Configure value conversions for enum columns (store as strings, e.g., `TransactionStatus.PENDING` → `"PENDING"`)

**Acceptance criteria:**
- EF Core migration generates DDL that matches `db/ddl/001-cloud-schema.sql` structurally
- All unique constraints from the spec are present (dedup key, pre-auth idempotency key, etc.)
- Global query filter on `LegalEntityId` verified with a unit test
- Migration can be applied to a local/Testcontainers PostgreSQL instance
- Seed data populates correctly in non-production environments

---

### CB-0.3: Domain Models & Enums

**Sprint:** 1
**Prereqs:** CB-0.1
**Estimated effort:** 1–2 days

**Read these artifacts before starting:**
- `schemas/canonical/canonical-transaction.schema.json` — every field, type, validation rule
- `schemas/canonical/pre-auth-record.schema.json` — pre-auth model
- `schemas/canonical/pump-status.schema.json` — pump state model
- `schemas/canonical/device-registration.schema.json` — registration models
- `schemas/canonical/telemetry-payload.schema.json` — telemetry model
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — all enum values

**Task:**
Create all domain models and enums in the `FccMiddleware.Domain` project.

**Detailed instructions:**
1. Create all shared enums:
   - `TransactionStatus`: PENDING, SYNCED_TO_ODOO, DUPLICATE, ARCHIVED
   - `PreAuthStatus`: PENDING, AUTHORIZED, DISPENSING, COMPLETED, CANCELLED, EXPIRED, FAILED
   - `ReconciliationStatus`: UNMATCHED, MATCHED, VARIANCE_WITHIN_TOLERANCE, VARIANCE_FLAGGED, APPROVED, REJECTED
   - `IngestionMode`: CLOUD_DIRECT, RELAY, BUFFER_ALWAYS
   - `IngestionMethod`: PUSH, PULL, HYBRID
   - `IngestionSource`: FCC_PUSH, EDGE_UPLOAD, CLOUD_PULL
   - `FccVendor`: DOMS, RADIX, ADVATEC, PETRONITE
   - `ConnectivityState`: FULLY_ONLINE, INTERNET_DOWN, FCC_UNREACHABLE, FULLY_OFFLINE
   - `SiteOperatingModel`: COCO, CODO, DODO, DOCO
   - `FiscalizationMode`: FCC_DIRECT, EXTERNAL_INTEGRATION, NONE
2. Create domain entity classes matching the JSON schemas:
   - `CanonicalTransaction` — all fields from `canonical-transaction.schema.json`
   - `PreAuthRecord` — all fields from `pre-auth-record.schema.json`
   - `PumpStatus` — from `pump-status.schema.json`
   - `DeviceRegistration` — from `device-registration.schema.json`
   - `TelemetryPayload` — from `telemetry-payload.schema.json`
3. Create domain entity classes for master data: `LegalEntity`, `Site`, `Pump` (with `PumpNumber` = Odoo, `FccPumpNumber`), `Nozzle` (with `OdooNozzleNumber`, `FccNozzleNumber`, FK to `Pump` and `Product`), `Product`, `Operator`, `FccConfig`, `AgentRegistration`
4. Implement the `Transaction` state machine enforcement in the domain entity — `Transition(newStatus)` method with guard checks per §5.1 of state machines spec
5. Implement the `PreAuthRecord` state machine enforcement per §5.2
6. All money fields must be `long` (minor units). All timestamps `DateTimeOffset`.

**Acceptance criteria:**
- All enums defined with correct values
- All domain entities match their JSON schema counterparts
- Transaction state machine rejects invalid transitions (unit test)
- Pre-auth state machine rejects invalid transitions (unit test)
- Domain project has ZERO external NuGet dependencies
- Currency fields are `long`, never `decimal`/`double`

---

### CB-0.4: Health Endpoint

**Sprint:** 1
**Prereqs:** CB-0.1, CB-0.2
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-1-project-scaffolding.md` — §5.1 health checks section
- `schemas/openapi/cloud-api.yaml` — GET /health endpoint definition

**Task:**
Implement the health check endpoints.

**Detailed instructions:**
1. Configure health checks in `Program.cs`:
   - PostgreSQL connectivity check (`AspNetCore.HealthChecks.NpgSql`)
   - Redis connectivity check (`AspNetCore.HealthChecks.Redis`)
   - Self liveness check
2. Expose two endpoints:
   - `GET /health` — liveness (always returns 200 if app is running)
   - `GET /health/ready` — readiness (checks DB + Redis connectivity)
3. Return structured JSON response with individual check statuses

**Acceptance criteria:**
- `/health` returns 200 when app is running
- `/health/ready` returns 200 when DB and Redis are reachable
- `/health/ready` returns 503 when DB or Redis is down
- Integration test validates both endpoints

---

### CB-0.5: CI Pipeline Setup

**Sprint:** 2
**Prereqs:** CB-0.1, CB-0.2
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-2-repo-branching-and-cicd.md` — CI/CD pipeline spec
- `docs/specs/foundation/tier-3-1-project-scaffolding.md` — project structure (for build steps)

**Task:**
Create the CI pipeline configuration (GitHub Actions or equivalent).

**Detailed instructions:**
1. Create `.github/workflows/ci.yml` with:
   - Trigger: push to `main`, PRs targeting `main`
   - Steps: checkout → setup .NET 10 → restore → build → unit tests → integration tests (with Testcontainers)
   - Publish test results as artifacts
2. Integration tests should use Testcontainers for PostgreSQL and Redis (no external infra needed)
3. Add a `Dockerfile` for the API and Worker projects (multi-stage build)
4. Add a step to build Docker images (don't push yet — that's Phase 6)

**Acceptance criteria:**
- CI passes on clean checkout
- Unit tests and integration tests run in CI
- Docker image builds successfully
- Build time is under 10 minutes

---

### CB-0.6: Swagger/OpenAPI Generation

**Sprint:** 2
**Prereqs:** CB-0.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-1-project-scaffolding.md` — §5.1 Swagger section
- `schemas/openapi/cloud-api.yaml` — target API spec to eventually match

**Task:**
Configure Swashbuckle to generate OpenAPI docs from Endpoints.

**Detailed instructions:**
1. Add Swashbuckle.AspNetCore to the Api project
2. Enable XML comment generation in the Api .csproj
3. Configure Swagger to group by controller, include JWT bearer auth definition
4. Expose at `/swagger` in dev/staging only (disabled in production via config flag)
5. Add a middleware to redirect `/` to `/swagger` in development

**Acceptance criteria:**
- `/swagger` renders the Swagger UI in development mode
- JWT bearer auth button is available in Swagger UI
- XML comments appear in API documentation

---

## Phase 1 — Cloud Core Ingestion (Sprints 3–5)

### CB-1.1: FCC Adapter Interface & DOMS Adapter

**Sprint:** 3
**Prereqs:** CB-0.3
**Estimated effort:** 3–4 days

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md` — THE definitive adapter contract (interfaces, shared types, DOMS MVP spec, factory rules)
- `schemas/canonical/canonical-transaction.schema.json` — the normalization target
- `WIP-HLD-Cloud-Backend.md` — §4.3, §4.4 (adapter module design)

**Task:**
Implement the cloud-side FCC adapter interface and the DOMS MVP adapter.

**Detailed instructions:**
1. In `FccMiddleware.Domain`, create the `IFccAdapter` interface with these 4 methods:
   - `NormalizeTransaction(RawPayloadEnvelope) → CanonicalTransaction`
   - `ValidatePayload(RawPayloadEnvelope) → ValidationResult`
   - `FetchTransactions(FetchCursor) → TransactionBatch`
   - `GetAdapterMetadata() → AdapterInfo`
2. Create all shared supporting types per §5.2 of the adapter contracts spec:
   - `RawPayloadEnvelope` (vendor, siteCode, receivedAtUtc, contentType, payload)
   - `FetchCursor` (cursorToken, sinceUtc, limit)
   - `TransactionBatch` (transactions, nextCursorToken, highWatermarkUtc, hasMore, sourceBatchId)
   - `ValidationResult` (isValid, errorCode, message, recoverable)
   - `AdapterInfo` (vendor, adapterVersion, supportedTransactionModes, supportsPreAuth, supportsPumpStatus, protocol)
3. Create `IFccAdapterFactory` in Domain: `Resolve(FccVendor vendor, SiteFccConfig config) → IFccAdapter`
4. In `FccMiddleware.Adapter.Doms`, implement `DomsCloudAdapter : IFccAdapter`:
   - `NormalizeTransaction`: Parse DOMS JSON payload → map to `CanonicalTransaction` fields. Use the DOMS MVP protocol spec from §5.5 of adapter contracts.
   - `ValidatePayload`: Structural validation (required fields, JSON parse, message type)
   - `FetchTransactions`: HTTP GET to `http://{host}:{port}/api/v1/transactions?since={ISO8601}&cursor={token}&limit={n}`, parse response, normalize each transaction
   - `GetAdapterMetadata`: Return static DOMS adapter info
5. Implement `FccAdapterFactory` in Infrastructure that resolves by `FccVendor`
6. Create sample DOMS JSON fixtures in test project for unit tests

**Acceptance criteria:**
- `IFccAdapter` interface matches §5.1 of adapter contracts spec exactly
- DOMS adapter normalizes a sample DOMS payload into a valid `CanonicalTransaction`
- DOMS adapter validates payloads (rejects missing required fields)
- Factory resolves DOMS adapter and fails for unknown vendors with `ADAPTER_NOT_REGISTERED`
- Unit tests cover normalization of all mapped fields
- Unit tests cover validation edge cases (null payload, missing transaction ID, invalid JSON)

---

### CB-1.2: Ingestion API — Receive, Validate, Deduplicate, Store

**Sprint:** 3–4
**Prereqs:** CB-0.2, CB-0.3, CB-1.1
**Estimated effort:** 4–5 days

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/transactions/ingest` endpoint definition
- `docs/specs/reconciliation/tier-2-2-deduplication-strategy.md` — primary dedup key, time window, duplicate storage rules, secondary fuzzy match, Redis cache
- `docs/specs/error-handling/tier-2-1-error-handling-strategy.md` — error response envelope, error code taxonomy
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.1 Transaction Lifecycle (PENDING vs DUPLICATE on ingest)
- `docs/specs/events/event-schema-design.md` — `TransactionIngested`, `TransactionDeduplicated` events

**Task:**
Implement the transaction ingestion pipeline for FCC direct push.

**Detailed instructions:**
1. Create `IngestTransactionCommand` and `IngestTransactionHandler` in Application layer (MediatR)
2. Pipeline steps (in order):
   a. Receive raw payload via `POST /api/v1/transactions/ingest`
   b. Resolve FCC adapter from vendor header/payload
   c. Validate payload via adapter `ValidatePayload()` — reject with 400 if invalid
   d. Normalize via adapter `NormalizeTransaction()`
   e. **Primary dedup check**: Check Redis cache for `(fccTransactionId, siteCode)`. On cache miss, check PostgreSQL with `WHERE fcc_transaction_id = @id AND site_code = @site AND created_at >= NOW() - INTERVAL '@window days'`
   f. If duplicate: Store with `status = DUPLICATE`, `isDuplicate = true`, `duplicateOfId = original.id`. Return 200 with `{ accepted: false, reason: "DUPLICATE", originalTransactionId }`. Publish `TransactionDeduplicated` event.
   g. If new: Store with `status = PENDING`. Set Redis cache key with TTL = `dedupWindowDays`. Archive raw payload to S3. Publish `TransactionIngested` event to outbox.
   h. **Secondary fuzzy match** (AFTER primary passes): Check `(siteCode, pumpNumber, nozzleNumber, completedAt ±5s, amountMinorUnits)`. If match found, set `reconciliationStatus = REVIEW_FUZZY_MATCH`. Do NOT mark as duplicate.
3. Implement the standard error response envelope: `{ errorCode, message, details, traceId, timestamp }`
4. Create the ingestion controller: `POST /api/v1/transactions/ingest`
5. Handle race conditions: Use the unique index `(fcc_transaction_id, site_code)` as final dedup safety net. Catch unique constraint violations and treat as duplicate.

**Acceptance criteria:**
- New transactions stored as PENDING with all fields populated
- Duplicate transactions stored as DUPLICATE with link to original
- Redis cache populated on new transaction, checked before DB
- 400 returned for invalid payloads with structured error response
- Secondary fuzzy match flags records without marking as duplicate
- Raw payload archived to S3 (or mock in tests)
- `TransactionIngested` event written to outbox table
- Integration test: ingest same transaction twice → second is DUPLICATE
- Integration test: ingest valid transaction → stored as PENDING
- Load test consideration: endpoint handles concurrent duplicate submissions correctly

---

### CB-1.3: Edge Agent Batch Upload API

**Sprint:** 4
**Prereqs:** CB-1.2
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/transactions/upload` endpoint definition
- `docs/specs/reconciliation/tier-2-2-deduplication-strategy.md` — §5.4 Edge Agent pre-filtering, per-record response format
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.2 Edge Agent auth (device JWT bearer)

**Task:**
Implement the batch upload endpoint for Edge Agent transaction submissions.

**Detailed instructions:**
1. Create `POST /api/v1/transactions/upload` endpoint
2. Accept an array of transactions (batch upload from Edge Agent)
3. Process each record individually through the same ingestion pipeline as CB-1.2
4. Return per-record response: `{ transactionId, fccTransactionId, status: "created" | "skipped", reason?: "DUPLICATE" }`
5. HTTP 200 for the batch even if individual records are duplicates (207 Multi-Status is acceptable alternative)
6. Authenticate via device JWT bearer token (implement JWT validation middleware for `Authorization: Bearer <deviceToken>`)
7. Validate JWT claims: `sub` (deviceId), `site` (siteCode), `lei` (legalEntityId) — ensure uploaded transactions match the device's registered site

**Acceptance criteria:**
- Batch of 100 transactions processed with per-record status
- Duplicate records in batch return `skipped` without error
- JWT validation rejects expired/invalid tokens
- Device can only upload transactions for its registered site
- Integration test with mixed new + duplicate batch

---

### CB-1.4: Odoo Poll API

**Sprint:** 4
**Prereqs:** CB-0.2, CB-0.3
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `GET /api/v1/transactions` endpoint definition
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — §5.2 index `ix_transactions_odoo_poll`
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.1 (only PENDING transactions served to Odoo)

**Task:**
Implement the Odoo poll endpoint — Odoo calls this to fetch PENDING transactions.

**Detailed instructions:**
1. Create `GET /api/v1/transactions` endpoint
2. Filter: `status = PENDING` (DUPLICATE and ARCHIVED are NEVER returned to Odoo)
3. Scope by `legalEntityId` from API key context (Odoo API key is scoped to a legal entity)
4. Support query parameters:
   - `siteCode` (optional filter)
   - `since` (ISO 8601 UTC, inclusive lower bound on `createdAt`)
   - `cursor` (cursor-based pagination token)
   - `limit` (page size, max 100, default 50)
   - `pumpNumber` (optional filter)
5. Return paginated response with `nextCursor`, `hasMore`, and `totalCount`
6. Authenticate via API Key (`X-Api-Key` header) — validate against `odoo_api_keys` table
7. Use the `ix_transactions_odoo_poll` partial index for efficient queries

**Acceptance criteria:**
- Returns only PENDING transactions for the authenticated legal entity
- Pagination works correctly with cursor
- Filter by siteCode, since, pumpNumber all work
- DUPLICATE/ARCHIVED transactions never returned
- API key authentication works
- Performance: <100ms for typical page at scale (verify via explain plan)

---

### CB-1.5: Odoo Acknowledge API

**Sprint:** 4–5
**Prereqs:** CB-1.4
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/transactions/acknowledge` endpoint definition
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.1 PENDING → SYNCED_TO_ODOO transition
- `docs/specs/events/event-schema-design.md` — `TransactionSyncedToOdoo` event

**Task:**
Implement the batch acknowledge endpoint — Odoo calls this after creating orders.

**Detailed instructions:**
1. Create `POST /api/v1/transactions/acknowledge` endpoint
2. Accept batch: `{ acknowledgements: [{ transactionId, odooOrderId }] }`
3. For each item:
   - Find transaction by ID, verify status is PENDING
   - Transition to `SYNCED_TO_ODOO` using domain state machine
   - Set `odooOrderId` and `syncedToOdooAt`
   - Publish `TransactionSyncedToOdoo` event to outbox
4. Return per-record status: `{ transactionId, status: "acknowledged" | "not_found" | "already_acknowledged" | "invalid_status" }`
5. Idempotent: acknowledging an already-acknowledged transaction returns `already_acknowledged` (success, not error)
6. Reject acknowledging DUPLICATE transactions with `409 Conflict`
7. Same API key auth as poll endpoint

**Acceptance criteria:**
- PENDING → SYNCED_TO_ODOO transition works
- `odooOrderId` and `syncedToOdooAt` populated
- Idempotent for already-acknowledged
- Rejects DUPLICATE transaction acknowledgements
- `TransactionSyncedToOdoo` event published
- Integration test: poll → acknowledge → verify status change

---

### CB-1.6: Master Data Sync APIs

**Sprint:** 5
**Prereqs:** CB-0.2
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/master-data/*` endpoints
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — master data table definitions
- `db/reference/seed-data-strategy.md` — reference data
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.2 Databricks auth (API key, role = master-data-sync)

**Task:**
Implement master data sync endpoints for Databricks to push reference data.

**Detailed instructions:**
1. Create endpoints for each master data type:
   - `POST /api/v1/master-data/legal-entities` — upsert legal entities
   - `POST /api/v1/master-data/sites` — upsert sites
   - `POST /api/v1/master-data/pumps` — upsert pumps; payload must include both `pumpNumber` (Odoo) and `fccPumpNumber`
   - `POST /api/v1/master-data/nozzles` — upsert nozzles; payload: `{ siteId, pumpId, odooNozzleNumber, fccNozzleNumber, productId }` — maps Odoo nozzle numbers to FCC nozzle numbers with product assignment
   - `POST /api/v1/master-data/products` — upsert products
   - `POST /api/v1/master-data/operators` — upsert operators
2. Each endpoint accepts a batch of records and performs upsert (insert or update)
3. Soft-delete handling: if a record is missing from the sync batch but exists in DB, set `is_active = false`
4. Authenticate via API key with role `master-data-sync`
5. Publish `MasterDataSynced` event on successful sync
6. Update `synced_at` timestamp on each record

**Acceptance criteria:**
- New records inserted correctly
- Existing records updated on re-sync
- Missing records soft-deleted
- `synced_at` timestamps updated
- API key auth with correct role
- Integration test for full sync cycle

---

### CB-1.7: Event Publishing — Outbox Worker

**Sprint:** 5
**Prereqs:** CB-0.2
**Estimated effort:** 1–2 days

**Read these artifacts before starting:**
- `docs/specs/events/event-schema-design.md` — event envelope, all event types
- `schemas/events/event-envelope.schema.json` — event envelope schema
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — §5.1.5 outbox table

**Task:**
Implement the transactional outbox pattern for domain event publishing.

**Detailed instructions:**
1. Create `OutboxMessage` entity and EF Core configuration (already in schema)
2. Create `IEventPublisher` interface in Domain: `Publish(DomainEvent)` writes to outbox table in the same DB transaction
3. Create `OutboxPublisherWorker` in Worker project:
   - Poll `outbox_messages` where `processed_at IS NULL` ordered by `id`
   - Process each message (for now: write to `audit_events` table and log — actual message broker comes later)
   - Set `processed_at` on successful processing
   - Retry logic for failed messages
   - Cleanup: delete messages older than 7 days with `processed_at IS NOT NULL`
4. Define all event types as classes inheriting from `DomainEvent`:
   - `TransactionIngested`, `TransactionDeduplicated`, `TransactionSyncedToOdoo`
   - `PreAuthCreated`, `PreAuthAuthorized`, `PreAuthCompleted`, `PreAuthCancelled`, `PreAuthExpired`
   - `ReconciliationMatched`, `ReconciliationVarianceFlagged`, `ReconciliationApproved`, `ReconciliationRejected`
   - `AgentRegistered`, `AgentConfigUpdated`, `AgentHealthReported`
   - `MasterDataSynced`, `ConfigChanged`

**Acceptance criteria:**
- Domain events written to outbox in same transaction as entity changes
- Outbox worker processes pending messages
- Processed messages marked with timestamp
- Old processed messages cleaned up
- Events written to audit_events table
- Integration test: ingest transaction → outbox message created → worker processes it → audit event stored

---

### CB-1.8: Stale Transaction Detection Worker

**Sprint:** 5
**Prereqs:** CB-0.2, CB-0.3
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.1 stale detection (flag, not state transition)
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — `is_stale` column, `ix_transactions_stale` index

**Task:**
Implement the background worker that flags stale PENDING transactions.

**Detailed instructions:**
1. Create `StaleTransactionWorker` in Worker project
2. Run periodically (every 15 minutes)
3. Query: `WHERE status = 'PENDING' AND is_stale = false AND created_at < NOW() - INTERVAL :stalePendingThresholdDays`
4. Set `is_stale = true` — this is a FLAG, NOT a state transition. Status remains PENDING.
5. `stalePendingThresholdDays` is configurable (default: 3 days)
6. Publish `TransactionStaleFlagged` event for alerting

**Acceptance criteria:**
- PENDING transactions older than threshold get `is_stale = true`
- Status remains PENDING (not changed)
- Odoo can still acknowledge stale transactions normally
- Unit test verifies stale detection logic

---

## Phase 3 — Cloud ↔ Edge Integration (Sprints 6–8)

### CB-3.1: Device Registration & Provisioning

**Sprint:** 6
**Prereqs:** CB-0.2, CB-0.3
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.1 Device Registration Flow (THE definitive spec)
- `docs/specs/data-models/tier-1-1-device-registration-spec.md` — registration request/response models
- `schemas/canonical/device-registration.schema.json` — JSON schema for all registration types

**Task:**
Implement the full device registration flow.

**Detailed instructions:**
1. Create `bootstrap_tokens` table (if not already in schema) and EF Core entity
2. Create `POST /api/v1/admin/bootstrap-tokens` — generate bootstrap token (32-byte random, Base64URL), store SHA-256 hash, link to siteCode/legalEntityId, 72h expiry
3. Create `POST /api/v1/agent/register` endpoint:
   - Validate bootstrap token (exists, not used, not expired, siteCode matches)
   - Generate `deviceId` (UUID v4)
   - Create `agent_registrations` record
   - Issue `deviceToken` (JWT, 24h) with claims: `sub=deviceId`, `site=siteCode`, `lei=legalEntityId`, `roles=["edge-agent"]`
   - Issue `refreshToken` (opaque, 90d)
   - Mark bootstrap token as used
   - Return `{ deviceId, deviceToken, refreshToken, config }`
4. Create `POST /api/v1/agent/token/refresh`:
   - Validate refresh token
   - Issue new device JWT + new refresh token (rotation)
   - Invalidate old refresh token
5. Create device decommission flow (`POST /api/v1/admin/agent/{deviceId}/decommission`):
   - Set `agent_registrations.status = DECOMMISSIONED`
   - Revoke all refresh tokens
   - Next API call returns `403 DEVICE_DECOMMISSIONED`
6. Implement JWT validation middleware for all `/agent/*`, `/upload`, `/preauth`, `/synced-status` endpoints

**Acceptance criteria:**
- Full registration flow works: generate bootstrap → scan → register → get JWT
- JWT contains correct claims
- Token refresh works with rotation
- Expired bootstrap token returns 401
- Used bootstrap token returns 409
- Decommissioned device gets 403
- Integration tests for happy path + all error cases

---

### CB-3.2: Edge Agent Config API

**Sprint:** 6
**Prereqs:** CB-3.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `GET /api/v1/agent/config` endpoint
- `schemas/config/edge-agent-config.schema.json` — config model
- `schemas/config/site-config.schema.json` — full site config
- `docs/specs/config/tier-2-4-edge-agent-configuration-schema.md` — config versioning, compatibility

**Task:**
Implement the config pull endpoint for Edge Agents.

**Detailed instructions:**
1. Create `GET /api/v1/agent/config` endpoint
2. Accept `If-None-Match` header with config version — return 304 if unchanged
3. Build SiteConfig from: `fcc_configs` + `sites` + `legal_entities` + `pumps` + `nozzles` + `products` tables — the `mappings.nozzles` array in SiteConfig must include `{ odooPumpNumber, fccPumpNumber, odooNozzleNumber, fccNozzleNumber, productCode }` for every active nozzle at the site, so the Edge Agent can resolve Odoo pump/nozzle numbers to FCC numbers at pre-auth time
4. Include config version (monotonic integer from `fcc_configs.config_version`)
5. Return full SiteConfig JSON matching `site-config.schema.json`
6. Auth: device JWT

**Acceptance criteria:**
- Returns complete SiteConfig for the device's registered site
- 304 when config hasn't changed (ETag/version comparison)
- Config version increments when config is updated in portal
- JWT scoping: device only gets config for its own site

---

### CB-3.3: Edge Agent Telemetry API

**Sprint:** 6
**Prereqs:** CB-3.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/agent/telemetry` endpoint
- `schemas/canonical/telemetry-payload.schema.json` — telemetry model
- `docs/specs/data-models/tier-1-1-telemetry-payload-spec.md` — all telemetry fields

**Task:**
Implement telemetry ingestion from Edge Agents.

**Detailed instructions:**
1. Create `POST /api/v1/agent/telemetry` endpoint
2. Accept `TelemetryPayload` JSON (battery, storage, buffer depth, FCC heartbeat, sync status, error counts)
3. Update `agent_registrations.last_seen_at`
4. Store telemetry in `audit_events` table as `AgentHealthReported` event
5. Evaluate alerting conditions (buffer depth > threshold, sync lag > threshold) — for now, just log warnings
6. Auth: device JWT

**Acceptance criteria:**
- Telemetry stored successfully
- `last_seen_at` updated
- Invalid payload returns 400 with structured error
- Auth via device JWT

---

### CB-3.4: SYNCED_TO_ODOO Status API for Edge

**Sprint:** 7
**Prereqs:** CB-1.5, CB-3.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `GET /api/v1/transactions/synced-status` endpoint
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.3 Edge Sync: UPLOADED → SYNCED_TO_ODOO transition

**Task:**
Implement the endpoint that Edge Agents poll to learn which transactions have been acknowledged by Odoo.

**Detailed instructions:**
1. Create `GET /api/v1/transactions/synced-status` endpoint
2. Accept query param: `since` (ISO 8601 UTC timestamp)
3. Return list of `fccTransactionId` values where `status = SYNCED_TO_ODOO` and `syncedToOdooAt >= since`
4. Scope by device's registered site (from JWT `site` claim)
5. Auth: device JWT

**Acceptance criteria:**
- Returns only SYNCED_TO_ODOO transactions for the device's site
- `since` filter works correctly
- Edge Agent can use this to advance local sync state

---

### CB-3.5: Pre-Auth Forward API

**Sprint:** 7
**Prereqs:** CB-0.3, CB-3.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/preauth` endpoint
- `schemas/canonical/pre-auth-record.schema.json` — pre-auth model
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.2 Pre-Auth Lifecycle
- `docs/specs/reconciliation/tier-2-2-deduplication-strategy.md` — §5.5 Pre-auth dedup on `(odooOrderId, siteCode)`

**Task:**
Implement the pre-auth forward endpoint — Edge Agent sends pre-auth results to cloud for storage and reconciliation.

**Detailed instructions:**
1. Create `POST /api/v1/preauth` endpoint
2. Accept pre-auth record from Edge Agent (after Edge has sent it to FCC)
3. Dedup on `(odooOrderId, siteCode)` — return existing record if non-terminal status
4. Store `PreAuthRecord` with all fields from the schema
5. Transition status based on what Edge reports (AUTHORIZED, FAILED, etc.)
6. Publish appropriate domain event (`PreAuthCreated`, `PreAuthAuthorized`, etc.)
7. Auth: device JWT

**Acceptance criteria:**
- Pre-auth record stored with correct status
- Dedup prevents duplicate creation for same `(odooOrderId, siteCode)`
- Terminal-status records allow re-request (new authorization)
- Events published for state transitions

---

### CB-3.6: Version Compatibility Check API

**Sprint:** 7
**Prereqs:** CB-3.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `GET /api/v1/agent/version-check` endpoint
- `WIP-HLD-Edge-Agent.md` — version compatibility logic

**Task:**
Implement the version check endpoint.

**Detailed instructions:**
1. Create `GET /api/v1/agent/version-check` endpoint
2. Accept `appVersion` query parameter
3. Compare against minimum supported version stored in config/DB
4. Return `{ compatible: bool, minimumVersion: string, latestVersion: string, updateRequired: bool, updateUrl?: string }`
5. Auth: device JWT

**Acceptance criteria:**
- Compatible version returns `compatible: true`
- Old version returns `updateRequired: true` with latest version info
- Used by Edge Agent to decide if update is needed

---

## Phase 4 — Pre-Auth & Reconciliation (Sprints 7–9)

### CB-4.1: Pre-Auth Lifecycle Management

**Sprint:** 7
**Prereqs:** CB-3.5
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/data-models/tier-1-1-pre-auth-record-spec.md` — authoritative pre-auth spec
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.2 Pre-Auth Lifecycle
- `schemas/canonical/pre-auth-record.schema.json` — all fields

**Task:**
Implement full pre-auth lifecycle management in the cloud.

**Detailed instructions:**
1. Implement `PreAuthExpiryWorker` — scans for PENDING/AUTHORIZED/DISPENSING pre-auths past `expires_at`, transitions to EXPIRED
2. Implement pre-auth status update endpoint: `PATCH /api/v1/preauth/{id}` — Edge Agent sends status updates (DISPENSING started, COMPLETED, CANCELLED)
3. Enforce state machine transitions: only valid transitions allowed (per §5.2 of state machines spec)
4. Side effect on DISPENSING → EXPIRED: attempt FCC pump deauthorization (best-effort via cloud adapter if available, otherwise log)
5. Publish domain events for all transitions

**Acceptance criteria:**
- Expiry worker correctly expires old pre-auths
- State machine rejects invalid transitions
- All transitions publish events
- DISPENSING → EXPIRED side effect documented/logged

---

### CB-4.2: Reconciliation Matching Engine

**Sprint:** 8
**Prereqs:** CB-4.1, CB-1.2
**Estimated effort:** 4–5 days

**Read these artifacts before starting:**
- `docs/specs/reconciliation/tier-2-3-reconciliation-rules-engine-design.md` — THE definitive reconciliation spec (matching algorithm, tolerance, review handling, unmatched retry)
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.5 Reconciliation State Machine
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — reconciliation-related indexes

**Task:**
Implement the reconciliation matching engine.

**Detailed instructions:**
1. Create `ReconciliationMatchingService` in Application layer
2. Implement the 3-step priority matching algorithm per §5.1 of reconciliation spec:
   - Step 1: `fccCorrelationId` exact match
   - Step 2: `pump + nozzle + time window` match (configurable `timeWindowMinutes`)
   - Step 3: `odooOrderId` echoed by FCC
3. Implement tie-breaker rules (most recent `authorizedAt`, smallest time delta)
4. Implement ambiguity handling (`ambiguityFlag = true` forces VARIANCE_FLAGGED)
5. Only run on transactions at sites where `siteUsesPreAuth = true`
6. Call reconciliation synchronously during ingestion (immediate matching)
7. Create `ReconciliationRecord` entity with all fields from spec
8. Implement tolerance resolution per §5.2:
   - Site override → legal entity → global default
   - `amountTolerancePercent`, `amountToleranceAbsolute`, `timeWindowMinutes`
9. Compute variance fields: `varianceMinorUnits`, `absoluteVarianceMinorUnits`, `variancePercent`, `withinTolerance`
10. Set reconciliation status: MATCHED, VARIANCE_WITHIN_TOLERANCE, VARIANCE_FLAGGED, or UNMATCHED
11. On match: link pre-auth to transaction, update pre-auth with actuals, transition to COMPLETED
12. Publish events: `ReconciliationMatched`, `ReconciliationVarianceFlagged`

**Acceptance criteria:**
- Step 1 (correlation ID) matches correctly
- Step 2 (pump+nozzle+time) matches within configurable window
- Step 3 (odooOrderId) matches as fallback
- Tolerance thresholds applied correctly
- Ambiguity forces VARIANCE_FLAGGED
- Already-linked pre-auths rejected as candidates
- UNMATCHED set when no match found
- Unit tests for each matching step independently
- Integration test: pre-auth → dispense → reconciliation → MATCHED

---

### CB-4.3: Unmatched Reconciliation Retry Worker

**Sprint:** 8
**Prereqs:** CB-4.2
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/specs/reconciliation/tier-2-3-reconciliation-rules-engine-design.md` — §5.5 Unmatched Handling (retry cadence, give-up)

**Task:**
Implement the deferred matching worker for unmatched reconciliation records.

**Detailed instructions:**
1. Create `UnmatchedReconciliationWorker` in Worker project
2. Retry schedule per spec §5.5:
   - Age ≤ 60 min: retry every 5 minutes
   - 60 min < age ≤ 24h: retry every 60 minutes
   - Age > 24h: stop retrying, create portal notification
3. Re-run the same matching algorithm from CB-4.2
4. On match found: transition from UNMATCHED to appropriate status
5. On 24h expiry: publish `ReconciliationUnmatchedAged` event, create alert

**Acceptance criteria:**
- Worker retries at correct intervals
- Successful deferred match transitions correctly
- 24h expired records stop being retried
- Escalation notification created

---

### CB-4.4: Reconciliation Review API

**Sprint:** 9
**Prereqs:** CB-4.2
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/specs/reconciliation/tier-2-3-reconciliation-rules-engine-design.md` — §5.4 Ops Manager Review Handling
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.5 VARIANCE_FLAGGED → APPROVED/REJECTED

**Task:**
Implement the review endpoints for the reconciliation workbench.

**Detailed instructions:**
1. Create `GET /api/v1/ops/reconciliation/exceptions` — query VARIANCE_FLAGGED and UNMATCHED records
   - Support filters: `legalEntityId`, `siteCode`, `status`, `since`, pagination
   - Return fields per spec §5.4
2. Create `POST /api/v1/ops/reconciliation/{id}/approve`:
   - Require `reason` text
   - Set `status = APPROVED`, `reviewedBy`, `reviewedAt`, `reviewReason`
   - Publish `ReconciliationApproved` event
3. Create `POST /api/v1/ops/reconciliation/{id}/reject`:
   - Same as approve but `status = REJECTED`
   - Publish `ReconciliationRejected` event
4. Only `OperationsManager` and `SystemAdmin` roles can approve/reject (Azure Entra role check)

**Acceptance criteria:**
- Exceptions query returns correct records with all required fields
- Approve/reject transitions work
- Only valid roles can approve/reject
- Reason text is mandatory
- Events published

---

## Phase 6 — Hardening & Production Readiness (Sprints 10–12)

### CB-6.1: Load Testing

**Sprint:** 10
**Prereqs:** All Phase 1 + Phase 3 tasks
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/specs/testing/testing-strategy.md` — load testing section
- `WIP-HLD-Cloud-Backend.md` — §9.3 database growth risk, performance targets

**Task:**
Create and run load tests targeting 2M transactions/day throughput.

**Detailed instructions:**
1. Use k6 or Artillery
2. Scenarios:
   - Sustained ingestion: 23 tx/sec for 1 hour (= 2M/day rate)
   - Burst ingestion: 100 tx/sec for 5 minutes
   - Odoo poll under load: 10 concurrent pollers at 2 req/sec
   - Odoo acknowledge: batch of 100 every 5 seconds
   - Edge Agent upload: 50 batch uploads of 100 txns each concurrently
3. Measure: p50/p95/p99 latency, error rate, DB connection pool usage
4. Identify bottlenecks and optimize

**Acceptance criteria:**
- Ingestion sustains 23 tx/sec with p99 < 500ms
- Odoo poll returns in < 200ms at p95
- Zero errors under sustained load
- DB connection pool stays below 80% utilization

---

### CB-6.2: Security Hardening

**Sprint:** 11
**Prereqs:** All Phase 3 tasks
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — full security spec

**Task:**
Verify and harden all security implementations.

**Detailed instructions:**
1. Verify all sensitive field log redaction works (`[Sensitive]` attribute)
2. Verify JWT validation on all protected endpoints
3. Verify API key HMAC validation for FCC endpoints
4. Verify multi-tenancy isolation (no cross-tenant data leakage)
5. Verify credential storage (Secrets Manager references, never plaintext in config)
6. Run OWASP dependency check on NuGet packages
7. Verify TLS configuration

**Acceptance criteria:**
- No sensitive fields in any log output
- Cross-tenant query returns zero results (integration test)
- Expired/invalid tokens rejected
- OWASP dependency check passes

---

### CB-6.3: Monitoring & Alerting Setup

**Sprint:** 11
**Prereqs:** All previous phases
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/error-handling/tier-3-5-observability-monitoring-design.md` — full observability spec

**Task:**
Implement monitoring dashboards and alerting rules.

**Detailed instructions:**
1. Configure structured logging with correlation ID propagation
2. Create CloudWatch dashboards:
   - Ingestion throughput (tx/sec, by source)
   - Odoo poll latency (p50/p95/p99)
   - Reconciliation match rate
   - Buffer depths (Edge Agent upload backlog)
   - Error rate by category
3. Create alerting rules:
   - Ingestion error rate > 5% → alert
   - Odoo poll latency p95 > 1s → alert
   - Edge Agent offline > 4h → alert
   - Buffer depth > 1000 → warning
   - Transaction stale count > 50 → alert

**Acceptance criteria:**
- All dashboards rendering with test data
- Alert rules trigger correctly on test conditions
- Correlation IDs flow through from ingestion to Odoo acknowledge

---

### CB-6.4: Archive & Retention Worker

**Sprint:** 12
**Prereqs:** CB-0.2
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — §5.3 Partitioning, §5.4 Lifecycle Policy
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — ARCHIVED transitions

**Task:**
Implement the partition management and archival worker.

**Detailed instructions:**
1. Create `ArchiveWorker` in Worker project
2. Detach partitions older than retention window (default: 24 months for transactions)
3. Export detached partition data to S3 as Parquet
4. For `audit_events`: 7-year regulatory retention via S3 archive
5. Clean up `outbox_messages` older than 7 days with `processed_at IS NOT NULL`

**Acceptance criteria:**
- Old partitions detached and exported to S3
- Active partitions unaffected
- Outbox cleanup works
- Archive data queryable via Athena (or documented how)
