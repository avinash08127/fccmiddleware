# Event Schema Design

## 1. Output Location

- **Target file:** `docs/specs/events/event-schema-design.md`
- **Companion file:** `schemas/events/event-envelope.schema.json` — machine-readable envelope schema for runtime validation
- **Why:** `docs/STRUCTURE.md` maps event envelope/type definitions to `/docs/specs/events` and machine-readable event schemas to `/schemas/events`

## 2. Scope

- **TODO item:** 2.6 Event Schema Design
- **In scope:** Event envelope definition, all event types with payload fields, versioning strategy, storage design (PostgreSQL + S3), consumption classification (action-triggering vs audit-only)
- **Out of scope:** Outbox publisher implementation, SNS topic configuration, alert rule thresholds, portal event viewer UI

## 3. Source Traceability

- **Requirements:** REQ-14 (Audit Trail and Transaction Logging — BR-14.1 through BR-14.4)
- **HLD sections:**
  - Cloud Backend HLD: Event Publishing Architecture, Messaging Topics, Design Decision #5 (managed messaging), Design Decision #7 (selective event streaming via outbox)
  - Cloud Backend HLD: `audit_events` and `outbox_messages` table design
  - Edge Agent HLD: `audit_log` Room entity
- **Assumptions:** Database schema from TODO 1.4 is already defined (DDL in `db/ddl/001-cloud-schema.sql`). State machines from TODO 1.2 define the transitions that produce events.

## 4. Key Decisions

| Decision | Why | Impact |
|----------|-----|--------|
| Envelope version field `schemaVersion` uses integer (starting at 1), not semver | Events are immutable; consumers only need to know which generation of payload shape to expect | Consumers switch on integer, not string parsing |
| Event type names use PascalCase dot-free strings (not dotted namespaces) | Matches HLD and requirements; simpler SNS filter policies | Filter policies use exact string match on `eventType` |
| Payload is typed per event but stored as JSONB | Allows schema validation at publish time while keeping storage flexible | Consumers deserialize payload based on `eventType` + `schemaVersion` |
| Additive-only schema evolution (no field removal or type changes) | Prevents breaking downstream consumers; aligns with immutable event store | New fields added with defaults; breaking changes require new event type |

## 5. Detailed Specification

### 5.1 Event Envelope

| Field | Type | Required | Validation | Description |
|-------|------|----------|------------|-------------|
| `eventId` | UUID v4 | Yes | Format: UUID | Unique identifier for this event instance |
| `eventType` | string | Yes | Must match a defined event type | PascalCase event name (see 5.2) |
| `schemaVersion` | integer | Yes | >= 1 | Payload schema version for this event type |
| `timestamp` | string (ISO 8601) | Yes | UTC, format: `yyyy-MM-ddTHH:mm:ss.fffZ` | When the event occurred |
| `source` | string | Yes | Max 100 chars | Producing system: `cloud-ingestion`, `cloud-reconciliation`, `cloud-preauth`, `cloud-master-data`, `cloud-config`, `edge-agent:{siteCode}` |
| `correlationId` | UUID | Yes | Format: UUID | Links related events across the processing chain |
| `legalEntityId` | UUID | Yes | Must reference existing legal entity | Tenant scope |
| `siteCode` | string | No | Max 50 chars | Null for legal-entity-level events (e.g., MasterDataSynced) |
| `payload` | object | Yes | Schema depends on `eventType` + `schemaVersion` | Event-specific data |

### 5.2 Event Types and Payloads

#### Transaction Events — Topic: `transaction.events`

| Event Type | Produced By | Payload Fields | Description |
|------------|------------|----------------|-------------|
| `TransactionIngested` | cloud-ingestion | `transactionId`, `fccTransactionId`, `fccVendor`, `pumpNumber`, `totalAmount`, `currency` | Raw transaction received and normalized |
| `TransactionDeduplicated` | cloud-ingestion | `transactionId`, `fccTransactionId`, `existingTransactionId`, `dedupKey` | Duplicate detected; original ID referenced |
| `TransactionSyncedToOdoo` | cloud-ingestion | `transactionId`, `odooOrderId`, `acknowledgedAt` | Odoo confirmed receipt |

#### Pre-Auth Events — Topic: `preauth.events`

| Event Type | Produced By | Payload Fields | Description |
|------------|------------|----------------|-------------|
| `PreAuthCreated` | cloud-preauth | `preAuthId`, `pumpNumber`, `nozzleNumber`, `requestedAmount`, `currency` | Pre-auth request received |
| `PreAuthAuthorized` | cloud-preauth | `preAuthId`, `authorizedAmount`, `fccAuthCode` | FCC confirmed authorization |
| `PreAuthDispensing` | cloud-preauth | `preAuthId`, `pumpNumber`, `nozzleNumber`, `fccCorrelationId` | FCC reported dispensing has started |
| `PreAuthCompleted` | cloud-preauth | `preAuthId`, `dispensedAmount`, `matchedTransactionId` | Dispensing finished; matched to transaction |
| `PreAuthCancelled` | cloud-preauth | `preAuthId`, `cancelledBy`, `reason` | Manually cancelled. `cancelledBy`: `operator` or `system` |
| `PreAuthExpired` | cloud-preauth | `preAuthId`, `expiredAfterSeconds` | Timed out without completion |
| `PreAuthFailed` | cloud-preauth | `preAuthId`, `reason` | FCC or workflow reported the pre-auth failed |

#### Reconciliation Events — Topic: `reconciliation.events`

| Event Type | Produced By | Payload Fields | Description |
|------------|------------|----------------|-------------|
| `ReconciliationMatched` | cloud-reconciliation | `reconciliationId`, `transactionId`, `preAuthId`, `matchMethod` | Transaction matched to pre-auth. `matchMethod`: `correlation_id`, `pump_time_window`, `odoo_order_id` |
| `ReconciliationVarianceFlagged` | cloud-reconciliation | `reconciliationId`, `transactionId`, `preAuthId`, `varianceAmount`, `variancePercent`, `toleranceExceeded` | Variance outside tolerance; flagged for review |
| `ReconciliationApproved` | cloud-reconciliation | `reconciliationId`, `approvedBy`, `approvalNote` | Ops Manager approved a flagged variance |

#### Agent Events — Topic: `transaction.events` (low volume; shared topic)

| Event Type | Produced By | Payload Fields | Description |
|------------|------------|----------------|-------------|
| `AgentRegistered` | cloud-ingestion | `deviceId`, `agentVersion`, `hardwareModel` | New Edge Agent registered |
| `AgentConfigUpdated` | cloud-config | `deviceId`, `configVersion`, `changedFields` | Config pushed to agent. `changedFields`: array of field names |
| `AgentHealthReported` | cloud-ingestion | `deviceId`, `bufferDepth`, `lastFccHeartbeat`, `syncLagSeconds`, `batteryPercent` | Telemetry snapshot received |

#### Infrastructure Events — Topic: `transaction.events` (low volume; shared topic)

| Event Type | Produced By | Payload Fields | Description |
|------------|------------|----------------|-------------|
| `ConnectivityChanged` | edge-agent | `deviceId`, `previousState`, `newState`, `detectedAt` | Connectivity state transition. States: `FULLY_ONLINE`, `INTERNET_DOWN`, `FCC_UNREACHABLE`, `FULLY_OFFLINE` |
| `BufferThresholdExceeded` | edge-agent | `deviceId`, `bufferDepth`, `thresholdValue` | Buffer depth exceeds configured warning threshold |
| `MasterDataSynced` | cloud-master-data | `entityType`, `recordCount`, `syncDurationMs` | Master data sync completed. `entityType`: `legal_entities`, `sites`, `pumps`, `products`, `operators` |
| `ConfigChanged` | cloud-config | `configScope`, `scopeId`, `changedFields`, `changedBy` | Config changed via portal. `configScope`: `site` or `legal_entity` |

### 5.3 Versioning Strategy

| Rule | Detail |
|------|--------|
| Version scope | Per event type — each type has its own `schemaVersion` counter |
| Initial version | All types start at `schemaVersion: 1` |
| Additive changes | Add new optional fields → same `schemaVersion` (no bump needed) |
| Structural changes | New required field, field type change, or field removal → increment `schemaVersion` and publish both versions during migration window |
| Consumer contract | Consumers must ignore unknown fields (forward-compatible). Consumers declare the max `schemaVersion` they support |
| Migration window | Old schema version published for 90 days after new version introduced, then discontinued |
| Breaking change | If semantics change fundamentally, create a new event type (e.g., `ReconciliationMatchedV2`) rather than incrementing version |

### 5.4 Event Storage

Storage is already defined in DDL (`db/ddl/001-cloud-schema.sql`). This section documents the design intent.

| Aspect | Design |
|--------|--------|
| **Hot store** | `audit_events` table in PostgreSQL, partitioned monthly by `created_at` |
| **Columns** | `id` (UUID), `legal_entity_id`, `event_type`, `correlation_id`, `site_code`, `source`, `payload` (JSONB), `created_at` |
| **Immutability** | No UPDATE or DELETE operations; enforced by application-level policy and optional DB trigger |
| **Outbox** | `outbox_messages` table; events written in same transaction as business state change, then published by OutboxPublisherWorker |
| **Hot retention** | 24 months in PostgreSQL (configurable per legal entity) |
| **Cold archive** | Monthly partitions older than retention window detached and exported to S3 as Parquet via archive worker |
| **S3 path** | `s3://{bucket}/events/{legalEntityId}/{year}/{month}/events-{partition}.parquet` |
| **S3 retention** | 7 years (BR-14.4), managed via S3 Lifecycle policy |
| **S3 encryption** | SSE-KMS (per security plan TODO 2.5) |
| **Query** | Hot: PostgreSQL indexes (`ix_audit_correlation`, `ix_audit_type_time`). Cold: Athena over S3 Parquet for compliance queries |

### 5.5 Event Consumption Classification

| Event Type | Triggers Downstream Action | Action | Audit-Only |
|------------|---------------------------|--------|------------|
| `TransactionIngested` | Yes | Trigger reconciliation matching attempt | Also audited |
| `TransactionDeduplicated` | No | — | Yes |
| `TransactionSyncedToOdoo` | Yes | Notify Edge Agent to update local status | Also audited |
| `PreAuthCreated` | No | — | Yes |
| `PreAuthAuthorized` | No | — | Yes |
| `PreAuthCompleted` | Yes | Trigger reconciliation matching | Also audited |
| `PreAuthCancelled` | No | — | Yes |
| `PreAuthExpired` | No | — | Yes |
| `ReconciliationMatched` | No | — | Yes |
| `ReconciliationVarianceFlagged` | Yes | Create review item in portal; trigger alert | Also audited |
| `ReconciliationApproved` | No | — | Yes |
| `AgentRegistered` | No | — | Yes |
| `AgentConfigUpdated` | No | — | Yes |
| `AgentHealthReported` | Yes | Evaluate alert rules (buffer threshold, sync lag, offline duration) | Also audited |
| `ConnectivityChanged` | Yes | Evaluate alert rules (offline > N hours) | Also audited |
| `BufferThresholdExceeded` | Yes | Trigger ops alert | Also audited |
| `MasterDataSynced` | Yes | Invalidate config cache | Also audited |
| `ConfigChanged` | Yes | Push updated config to affected Edge Agents | Also audited |

**SNS subscription routing:** Action-triggering events have SQS subscribers with filter policies on `eventType`. Audit-only events are consumed by the audit writer (all events) and optionally by a future analytics pipeline.

## 6. Validation and Edge Cases

- `eventId` must be generated at publish time, never reused — UUID v4 collision is acceptable risk
- `correlationId` must be propagated from the originating request; if no upstream correlation exists, generate a new one
- `timestamp` must reflect when the domain event occurred, not when the outbox row was published
- `siteCode` is null only for legal-entity-scoped events (`MasterDataSynced`, `ConfigChanged` with `configScope: legal_entity`); all other events must populate it
- Outbox publisher must process events in `id` order to preserve causal ordering within a transaction
- If SNS publish fails, outbox row remains unprocessed; publisher retries on next poll cycle (no event loss)
- Consumers must be idempotent — outbox publisher guarantees at-least-once delivery

## 7. Cross-Component Impact

| Component | Impact |
|-----------|--------|
| **Cloud Backend** | Implement event envelope as shared domain type in `FccMiddleware.Domain/Events/`. All domain services publish via outbox. OutboxPublisherWorker reads outbox and publishes to SNS |
| **Edge Agent** | Produces `ConnectivityChanged` and `BufferThresholdExceeded` events; serialized as JSON and uploaded via telemetry endpoint. Local `audit_log` table stores simplified event records for diagnostics |
| **Angular Portal** | Consumes events indirectly: audit viewer queries `audit_events` table via Cloud API. Reconciliation flagged events create review items visible in portal |

## 8. Dependencies

- **Prerequisites:** TODO 1.4 (Database Schema — `audit_events` and `outbox_messages` DDL already defined), TODO 1.2 (State Machines — define which transitions produce events)
- **Downstream:** TODO 3.5 (Observability — alert rules consume action-triggering events), TODO 5.3 Phase 1 (Cloud Core — implements event publishing)
- **Recommended next:** TODO 2.1 (Error Handling Strategy) or TODO 2.3 (Reconciliation Rules Engine) — both reference events

## 9. Open Questions

None. All decisions can be made from existing requirements and HLD context.

## 10. Acceptance Checklist

- [ ] Event envelope structure defined with all fields, types, and validation rules
- [ ] All 18 event types defined with payload fields and producing system
- [ ] Each event type assigned to an SNS topic
- [ ] Versioning strategy documented: additive-only, per-type version counter, 90-day migration window
- [ ] Storage design confirmed: PostgreSQL hot store (24 months) + S3 Parquet cold archive (7 years)
- [ ] Every event classified as action-triggering (with specific action) or audit-only
- [ ] Companion JSON Schema for event envelope created at `schemas/events/event-envelope.schema.json`

## 11. Output Files to Create

| File | Purpose |
|------|---------|
| `docs/specs/events/event-schema-design.md` | This artefact |
| `schemas/events/event-envelope.schema.json` | Machine-readable envelope schema for runtime validation |

## 12. Recommended Next TODO

**TODO 2.1 — Error Handling Strategy** — defines error taxonomy and retry semantics, which are needed before implementing the ingestion and event publishing pipeline.
