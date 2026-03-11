# Error Handling Strategy

## 1. Output Location

- **Target file:** `docs/specs/error-handling/tier-2-1-error-handling-strategy.md`
- **Companion file:** `schemas/errors/error-response.schema.json` (machine-readable error envelope; create when TODO 1.3 API contracts are built)
- **Why:** `docs/STRUCTURE.md` maps error taxonomy, retries, quarantine, and alerting to `/docs/specs/error-handling`.

## 2. Scope

- **TODO item:** 2.1 Error Handling Strategy
- **In scope:** Standard error envelope, error code taxonomy, retry semantics, error propagation paths, quarantine behavior, alerting triggers.
- **Out of scope:** Per-endpoint HTTP status code mapping (covered by TODO 1.3 API contracts), observability infrastructure setup (TODO 3.5), on-call runbooks (TODO 3.5).

## 3. Source Traceability

- **Requirements:** REQ-16 (Error Handling, Retry, and Alerting), REQ-14 (Audit Trail), NFR-7 (Observability)
- **HLD sections:** Cloud Backend 6.6 (Retry and Idempotency), 6.4 (Messaging — SQS queues, DLQ), 8.7 (Observability/Alerting); Edge Agent 6.5 (Retry and Resilience)
- **Assumptions:** TODO 1.3 (API contracts) will reference this error envelope. TODO 2.5 (Security) will define auth error details. TODO 2.6 (Event Schema) will define error-related events.

## 4. Key Decisions

| # | Decision | Why | Impact |
|---|----------|-----|--------|
| 1 | Error codes use hierarchical string format `CATEGORY.SUBCATEGORY` (not numeric) | Human-readable, extensible, grepable in logs | All API consumers parse string error codes |
| 2 | Retry policy is per error code, not per HTTP status | Same HTTP 502 may be retryable (FCC timeout) or terminal (protocol error); fine-grained control needed | Retry table must be consulted by all retry logic (cloud workers, Edge Agent sync) |
| 3 | Quarantine uses `QUARANTINED` status on the transaction record, not a separate table | Quarantined records stay queryable alongside normal records; Ops Manager reviews in portal | Transaction status enum gains `QUARANTINED` value |
| 4 | Cloud returns errors synchronously in HTTP responses; async failures route to SQS retry → DLQ | Per HLD: cloud acknowledges 202 for FCC push, then processes internally; internal failures go to `ingestion.retry` queue | FCC/Edge callers see only synchronous validation errors; async failures surface in portal |

## 5. Detailed Specification

### 5.1 Standard Error Response Envelope

All Cloud Backend HTTP error responses use this envelope. Edge Agent local API uses the same structure.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `errorCode` | string | Yes | Hierarchical code from taxonomy below (e.g., `VALIDATION.MISSING_FIELD`) |
| `message` | string | Yes | Human-readable summary. Never includes secrets, tokens, or customer TIN. |
| `details` | object \| null | No | Structured context. For validation: `{ "field": "pumpNumber", "constraint": "required" }`. For FCC: `{ "fccHost": "10.0.1.50", "timeoutMs": 5000 }`. |
| `traceId` | string | Yes | OpenTelemetry trace ID. Propagated from inbound request or generated if absent. |
| `timestamp` | string (ISO 8601 UTC) | Yes | Time the error was produced. |
| `retryable` | boolean | Yes | Whether the caller should retry this request. |

HTTP response body on error is always `{ "error": { ... } }` wrapping the envelope above.

### 5.2 Error Code Taxonomy

| Code | HTTP Status | Retryable | Retry Strategy | Alerting |
|------|-------------|-----------|----------------|----------|
| **VALIDATION.MISSING_FIELD** | 400 | No | Terminal | Silent (logged) |
| **VALIDATION.INVALID_VALUE** | 400 | No | Terminal | Silent (logged) |
| **VALIDATION.SCHEMA_MISMATCH** | 400 | No | Terminal | Count threshold (>50/hour) |
| **VALIDATION.UNKNOWN_SITE** | 400 | No | Terminal — until master data syncs | Count threshold (>10/hour) |
| **VALIDATION.UNKNOWN_PUMP** | 400 | No | Terminal — until master data syncs | Count threshold (>10/hour) |
| **FCC.TIMEOUT** | 504 | Yes | Exponential backoff: 5s/10s/20s/40s/.../300s max, jitter ±20% | After 3 consecutive failures per site |
| **FCC.CONNECTION_REFUSED** | 502 | Yes | Same as FCC.TIMEOUT | After 3 consecutive failures per site |
| **FCC.PROTOCOL_ERROR** | 502 | No | Terminal — adapter cannot parse FCC response | Immediate |
| **FCC.HEARTBEAT_STALE** | — (internal) | — | N/A (detected by heartbeat monitor) | After configurable threshold (default 5 min) |
| **AUTH.TOKEN_EXPIRED** | 401 | Yes | Caller refreshes token, then retries once | Silent (expected flow) |
| **AUTH.TOKEN_INVALID** | 401 | No | Terminal | Count threshold (>10/hour per source) |
| **AUTH.API_KEY_INVALID** | 401 | No | Terminal | Immediate |
| **AUTH.SCOPE_MISMATCH** | 403 | No | Terminal | Count threshold (>10/hour per source) |
| **AUTH.DEVICE_REVOKED** | 403 | No | Terminal — device must re-register | Immediate |
| **CONFLICT.DUPLICATE_TRANSACTION** | 409 | No | Silent skip — dedup handled per TODO 2.2 | Silent (audit logged) |
| **CONFLICT.STALE_CONFIG_VERSION** | 409 | Yes | Caller fetches latest config, then retries | Silent (logged) |
| **CONFLICT.OPTIMISTIC_LOCK** | 409 | Yes | Caller re-reads, re-applies, retries (max 3) | Silent (logged) |
| **INFRA.DATABASE_UNAVAILABLE** | 503 | Yes | Exponential backoff: 1s/2s/4s/.../60s max | Immediate |
| **INFRA.QUEUE_FULL** | 503 | Yes | Exponential backoff: 5s/10s/20s/.../300s max | Immediate |
| **INFRA.CACHE_UNAVAILABLE** | 503 | Yes | Bypass cache, hit DB directly; retry cache reconnect in background | After 1 min continuous failure |
| **INTERNAL.UNEXPECTED** | 500 | Yes | Exponential backoff: 5s up to 300s max, 5 retries then DLQ | Immediate |

### 5.3 Retry Semantics Summary

| Parameter | Default | Configurable |
|-----------|---------|-------------|
| Initial delay | 5 seconds | Yes (per environment) |
| Max delay | 5 minutes (300s) | Yes |
| Backoff multiplier | 2x | No |
| Jitter | ±20% | No |
| Max retries (cloud SQS worker) | 5 | Yes |
| Max retries (Edge Agent upload) | Unlimited until success | N/A — bounded by backoff ceiling |
| Max retries (Edge Agent pre-auth to cloud) | Unlimited until success | N/A |
| Max retries (Edge Agent FCC poll) | Unlimited (polling loop) | N/A |

After max retries exhausted on cloud: record moves to `ingestion.dlq` (SQS dead-letter queue).

### 5.4 Error Propagation Paths

**Path 1: FCC → Cloud (direct push/pull)**

```
FCC sends payload
  → Cloud Ingestion API validates synchronously
    → Validation failure? Return 4xx + error envelope immediately
    → Valid? Return 202 Accepted
      → Async processing (normalize, dedup, store)
        → Failure? → ingestion.retry queue (exponential backoff, max 5)
          → Still failing? → ingestion.dlq + QUARANTINED status
```

**Path 2: FCC → Edge Agent → Cloud (relay/buffer)**

```
FCC payload arrives at Edge Agent (poll or push)
  → Edge adapter normalizes locally
    → Normalize failure? Log raw payload locally, status=PARSE_FAILED, skip
  → Write to SQLite buffer (status=PENDING)
  → Upload to Cloud when online
    → Cloud returns 4xx validation error? Mark record ERROR locally, do not retry
    → Cloud returns 5xx / timeout? Retry with backoff (unlimited, ceiling 5 min)
    → Cloud returns 202? Mark record UPLOADED
  → Edge polls SYNCED_TO_ODOO status → update local status
```

**Path 3: Edge Local API → Odoo POS**

```
Odoo POS calls Edge local API
  → Edge processes (e.g., pre-auth to FCC)
    → FCC timeout/refused? Return error envelope with retryable=true
    → FCC protocol error? Return error envelope with retryable=false
    → Success? Return result
  → Error envelope identical to cloud format (same schema)
```

### 5.5 Quarantine Behavior

| Trigger | Action | Resolution |
|---------|--------|------------|
| Cloud DLQ arrival (5 retries exhausted) | Set transaction status to `QUARANTINED`. Write audit event `TransactionQuarantined`. | Ops Manager inspects in portal, fixes data or config, triggers manual retry. |
| Non-retryable validation error on async processing | Set status to `QUARANTINED` immediately (skip retry queue). | Ops Manager reviews. May require master data sync or adapter fix. |
| Edge Agent `PARSE_FAILED` record | Stored locally with raw payload. Not uploaded to cloud. | Site Supervisor or Ops Manager extracts via diagnostics API. Requires adapter fix. |

Quarantined records:
- Remain queryable in portal with filter `status=QUARANTINED`
- Retain full raw payload and error context in `quarantineReason` field
- Can be manually retried (sets status back to `PENDING`, re-enters processing pipeline)
- Are included in quarantine-depth metrics and alerting

### 5.6 Alerting Triggers

| Condition | Severity | Channel | Threshold |
|-----------|----------|---------|-----------|
| DLQ depth > 0 | Critical | Ops dashboard + email | Immediate |
| Ingestion error rate > 5% of volume in rolling 15 min | High | Ops dashboard + email | Sustained 15 min |
| FCC heartbeat stale | High | Ops dashboard | Default 5 min per site (configurable) |
| `VALIDATION.SCHEMA_MISMATCH` count > 50/hour | High | Ops dashboard + email | Rolling 1 hour |
| `AUTH.API_KEY_INVALID` or `AUTH.DEVICE_REVOKED` | Critical | Ops dashboard + email | Immediate |
| `INFRA.DATABASE_UNAVAILABLE` | Critical | Ops dashboard + email + PagerDuty | Immediate |
| `INFRA.QUEUE_FULL` | Critical | Ops dashboard + email + PagerDuty | Immediate |
| Edge Agent offline > N hours | High | Ops dashboard | Configurable (default 2 hours) |
| Transaction buffer depth > Y | Medium | Ops dashboard | Configurable (default 5,000 records) |
| Stale PENDING transactions > Z days | Medium | Ops dashboard | Configurable (default 7 days) |
| `INTERNAL.UNEXPECTED` spike > 10/min | Critical | Ops dashboard + email + PagerDuty | Rolling 5 min |

**Silently logged (no alert):** `CONFLICT.DUPLICATE_TRANSACTION`, `AUTH.TOKEN_EXPIRED`, `CONFLICT.STALE_CONFIG_VERSION`, `CONFLICT.OPTIMISTIC_LOCK`, individual validation errors below threshold.

## 6. Validation and Edge Cases

- **Clock skew:** `timestamp` in error envelope uses server UTC time, not client time. `traceId` is the correlation anchor.
- **Burst of duplicates:** Large Edge Agent buffer replay triggers many `CONFLICT.DUPLICATE_TRANSACTION` responses. These must not trigger alerts or inflate error rate metrics. Dedup responses use 409 but are excluded from error-rate calculations.
- **Partial batch failure:** Edge Agent batch upload returns per-record status. Some records may succeed while others fail. Each record gets its own error code. The batch response is an array of `{ sequenceId, status, error? }`.
- **Master data race:** A transaction referencing a site that hasn't synced yet gets `VALIDATION.UNKNOWN_SITE`. This is terminal in the moment but resolves after next Databricks sync. Edge Agent should buffer and retry on next upload cycle.

## 7. Cross-Component Impact

| Component | Impact |
|-----------|--------|
| **Cloud Backend** | Implements error envelope on all API responses. Retry worker consumes `ingestion.retry`. DLQ processing in portal. `QUARANTINED` status added to transaction lifecycle. |
| **Edge Agent** | Uses same error envelope on local API. Implements per-error-code retry decisions on cloud upload responses. Stores `PARSE_FAILED` records locally. |
| **Angular Portal** | Displays quarantined records with filter. Manual retry action. DLQ depth on ops dashboard. Alerting configuration UI (thresholds). |
| **Transaction Status Enum** | Add `QUARANTINED` value (coordinate with TODO 1.1 enums). |

## 8. Dependencies

- **Prerequisites:** TODO 1.1 (Canonical Data Model — `TransactionStatus` enum must include `QUARANTINED`), TODO 1.3 (API Contracts — error envelope referenced by all endpoints)
- **Downstream:** TODO 2.6 (Event Schema — `TransactionQuarantined` event type), TODO 3.5 (Observability — alerting rules implementation), TODO 2.5 (Security — auth error codes consumed here)
- **Next step:** Implement error envelope as a shared library type in both .NET and Kotlin during TODO 3.1 (Project Scaffolding).

## 9. Open Questions

None. All decisions can be made from existing requirements and HLD context. Thresholds are configurable and can be tuned post-deployment.

## 10. Acceptance Checklist

- [ ] Error response envelope schema defined and reviewed
- [ ] All error codes in taxonomy assigned to at least one API or propagation path
- [ ] Every error code marked retryable or terminal
- [ ] Retry parameters (backoff, max retries, jitter) specified with defaults
- [ ] Three propagation paths documented (cloud direct, edge relay, edge local API)
- [ ] Quarantine behavior defined: trigger, status, resolution, portal visibility
- [ ] Alerting triggers defined with severity, channel, and threshold for each
- [ ] `QUARANTINED` status added to `TransactionStatus` enum (coordinate with TODO 1.1)
- [ ] Partial batch failure handling specified for Edge Agent uploads
- [ ] Duplicate transaction responses excluded from error rate metrics

## 11. Output Files to Create

| File | Purpose |
|------|---------|
| `docs/specs/error-handling/tier-2-1-error-handling-strategy.md` | This artefact |
| `schemas/errors/error-response.schema.json` | Machine-readable error envelope (create alongside TODO 1.3 API contracts) |

## 12. Recommended Next TODO

**TODO 2.2 — Deduplication Strategy (Detailed):** It directly consumes the `CONFLICT.DUPLICATE_TRANSACTION` error code and must define how duplicate detection interacts with error propagation and the "silent skip" audit behavior specified here.
