# Deduplication Strategy

## 1. Output Location
- Target file: `docs/specs/reconciliation/tier-2-2-deduplication-strategy.md`
- Why: `docs/STRUCTURE.md` maps deduplication to `/docs/specs/reconciliation`

## 2. Scope
- TODO item: **2.2 Deduplication Strategy (Detailed)**
- In scope: dedup keys, time window, duplicate handling behavior, Edge Agent pre-filtering, FCC ID reuse, pre-auth dedup
- Out of scope: reconciliation matching algorithm (TODO 2.3), secondary fuzzy matching implementation details, Redis cache sizing

## 3. Source Traceability
- Requirements: REQ-13 (BR-13.1 through BR-13.4), REQ-7 (BR-7.3, BR-7.4), REQ-9 (BR-9.2), REQ-12 (BR-12.3, BR-12.5, BR-12.10)
- HLD: Cloud Backend — Deduplication Engine, Ingestion Module Pipeline; Edge Agent — §6.6 Idempotency
- Depends on: tier-1-1-canonical-transaction-spec (defines `isDuplicate`, `duplicateOfId` fields), tier-1-4-database-schema-design (defines `ix_transactions_dedup` unique index)

## 4. Key Decisions

| Decision | Why | Impact |
|---|---|---|
| Dedup window is 90 days, configurable per legal entity. | 30 days is too short for month-end reconciliation edge cases; 90 days covers quarterly cycles while bounding DB lookups. | Dedup query scopes to `createdAt >= NOW() - window`. Records older than the window with a reused `fccTransactionId` are treated as new. |
| Primary-match duplicates are stored with `status = DUPLICATE`, not silently dropped. | Auditability is critical for financial transactions. Silent drops make troubleshooting impossible. | Every ingested record gets a row. Duplicates are excluded from Odoo poll and reconciliation by status filter. |
| Edge Agent pre-filters using local sync state, not by re-checking cloud. | Avoids unnecessary upload bandwidth and cloud load. Edge already tracks per-record status. | Edge skips records in `UPLOADED`, `SYNCED`, or `SYNCED_TO_ODOO` states when building upload batches. |
| Pre-auth dedup key is `(odooOrderId, siteCode)`, not `fccCorrelationId`. | `odooOrderId` is known at request time and is the idempotency key Odoo controls. `fccCorrelationId` arrives later in the FCC response. | Matches the existing unique constraint in the DB schema. |

## 5. Detailed Specification

### 5.1 Transaction Dedup Rules

| Rule | Key | Scope | Behaviour on Match | Event Published |
|---|---|---|---|---|
| **Primary dedup** | `(fccTransactionId, siteCode)` | Cloud ingestion + Edge upload endpoints | Store new row with `status = DUPLICATE`, `isDuplicate = true`, `duplicateOfId = <original.id>`. Return HTTP 200 with `{ accepted: false, reason: "DUPLICATE", originalTransactionId: <id> }`. | `TransactionDeduplicated` |
| **Secondary fuzzy match** | `(siteCode, pumpNumber, nozzleNumber, endDateTime ±5 s, amountMinorUnits)` | Cloud ingestion only, applied after primary check passes | Store normally with `status = PENDING` but set `reconciliationStatus = REVIEW_FUZZY_MATCH`. Do **not** mark as duplicate. | `TransactionFuzzyMatchFlagged` |
| **Dual-path dedup** | Same primary key | FCC push arrives, then Edge catch-up uploads same record (or vice versa) | Handled identically to primary dedup — second arrival is stored as `DUPLICATE`. | `TransactionDeduplicated` |

### 5.2 Dedup Time Window

| Parameter | Default | Configurable At | Storage |
|---|---|---|---|
| `dedupWindowDays` | 90 | Legal entity (via `SiteConfig` override) | `site_configs.dedup_window_days` |

**Lookup rule:** When checking primary dedup, query `WHERE fcc_transaction_id = @id AND site_code = @site AND created_at >= NOW() - INTERVAL '@window days'`. A match within the window is a duplicate. A same-key record outside the window is accepted as new.

**Cache layer:** Redis stores recent dedup keys with TTL = `dedupWindowDays`. Cache miss falls through to PostgreSQL. Cache is populated on every successful ingestion.

### 5.3 FCC Transaction ID Reuse Behaviour

| Scenario | Detection | Handling |
|---|---|---|
| FCC vendor never reuses IDs (most vendors) | N/A — standard dedup applies | No special handling. |
| FCC vendor reuses IDs after a long gap | `created_at` of original is outside `dedupWindowDays` | Treated as a new transaction. The dedup window prevents false matches. |
| FCC vendor reuses IDs within the dedup window | Primary dedup match fires | Stored as `DUPLICATE`. If this is a known vendor behaviour, Ops must configure a shorter `dedupWindowDays` or the adapter must synthesize a unique suffix (configured per `FccVendor` in adapter metadata). |

Adapter-level configuration field:

| Field | Type | Default | Description |
|---|---|---|---|
| `fccIdReuseBehaviour` | enum: `TRUST`, `APPEND_TIMESTAMP` | `TRUST` | `TRUST`: use raw `fccTransactionId` as-is. `APPEND_TIMESTAMP`: adapter appends `_<endDateTime epoch seconds>` before passing to dedup pipeline. |

### 5.4 Edge Agent Pre-Filtering

The Edge Agent must NOT upload records that are already confirmed by cloud. Upload batch query:

```
SELECT * FROM buffered_transactions
WHERE cloud_sync_status IN ('PENDING', 'FAILED_RETRYABLE')
ORDER BY fcc_end_date_time ASC
LIMIT :batchSize
```

State transitions that exclude records from future uploads:

| Local Status | Meaning | Upload Eligible |
|---|---|---|
| `PENDING` | Not yet sent to cloud | Yes |
| `FAILED_RETRYABLE` | Previous upload attempt failed with retryable error | Yes |
| `UPLOADED` | Cloud accepted (not duplicate) | No |
| `DUPLICATE_CONFIRMED` | Cloud responded `accepted: false, reason: DUPLICATE` | No |
| `SYNCED_TO_ODOO` | Cloud confirmed Odoo acknowledged | No |
| `ARCHIVED` | Past retention, pending local cleanup | No |

On cloud upload response, Edge sets `DUPLICATE_CONFIRMED` for items where `accepted = false` and `reason = "DUPLICATE"`. These are never retried.

### 5.5 Pre-Auth Dedup Rules

| Rule | Key | Scope | Behaviour on Match |
|---|---|---|---|
| **Primary pre-auth dedup** | `(odooOrderId, siteCode)` | Cloud pre-auth endpoint + Edge local pre-auth handler | Return the existing pre-auth record and its current status. Do not create a second record. HTTP 200 with `{ created: false, existingPreAuthId: <id>, status: <current status> }`. |
| **Edge local dedup** | `(odooOrderId, siteCode)` in local Room table | Edge pre-auth handler before forwarding to FCC | If a local record exists with terminal status (`COMPLETED`, `CANCELLED`, `EXPIRED`, `FAILED`), allow a new request (Odoo is retrying a new authorization). If non-terminal (`PENDING`, `AUTHORIZED`, `DISPENSING`), return existing record. |

`fccCorrelationId` is NOT used as a dedup key — it is used downstream for reconciliation matching (TODO 2.3).

## 6. Validation and Edge Cases

- **Null `fccTransactionId`:** Reject at adapter validation. Every transaction must have an `fccTransactionId` after normalization.
- **Empty `siteCode`:** Reject at API validation. Both dedup key components are required.
- **Race condition — two identical transactions arrive simultaneously:** The DB unique index `ix_transactions_dedup` ensures exactly one INSERT succeeds. The second attempt catches the unique-constraint violation, loads the original, and stores the duplicate row with `duplicateOfId`.
- **Redis cache eviction before window expiry:** Falls through to PostgreSQL. Correctness is not affected, only latency.
- **Edge Agent replays after extended offline period (>90 days):** Records whose `fccEndDateTime` is older than the dedup window may be accepted as new if the original was ingested from FCC push and has since aged out of the window. This is acceptable — the original will have status `SYNCED_TO_ODOO` or `ARCHIVED`, and the new record enters as `PENDING` and will be caught by secondary fuzzy matching or reconciliation.

## 7. Cross-Component Impact

| Component | Impact |
|---|---|
| **Cloud Backend** | Dedup module in ingestion pipeline; Redis cache TTL configuration; `TransactionDeduplicated` event publishing; upload API response contract includes `accepted` and `reason` fields. |
| **Edge Agent** | Upload batch query filters by `cloud_sync_status`; handles `DUPLICATE_CONFIRMED` response from cloud; local pre-auth dedup before FCC forwarding. |
| **Angular Portal** | Transaction browser must display `DUPLICATE` status records with a link to the original (`duplicateOfId`). No portal-side dedup logic. |
| **Database** | `dedup_window_days` column on `site_configs` table. No DDL change for transactions table — unique index already exists. |

## 8. Dependencies
- Prerequisites: tier-1-1-canonical-transaction-spec (done), tier-1-4-database-schema-design (done), tier-1-1-pre-auth-record-spec (done)
- Downstream: TODO 2.3 Reconciliation Rules Engine (secondary fuzzy match flagging feeds into reconciliation review), TODO 2.1 Error Handling (dedup-related error codes), TODO 2.4 Configuration Schema (`dedupWindowDays` and `fccIdReuseBehaviour` fields)
- Recommended next step: implement cloud-side `DeduplicateTransactionHandler` with primary key check, DB upsert, and Redis cache write

## 9. Open Questions

None. All sub-items from TODO 2.2 are resolved above. The `fccIdReuseBehaviour = APPEND_TIMESTAMP` option covers vendor ID reuse. Actual vendor-specific settings will be confirmed during TODO 4.1 (DOMS FCC Protocol PoC).

## 10. Acceptance Checklist

- [ ] Primary dedup key confirmed as `(fccTransactionId, siteCode)` with 90-day configurable window
- [ ] Duplicate records stored with `status = DUPLICATE`, `isDuplicate = true`, `duplicateOfId` populated
- [ ] `TransactionDeduplicated` domain event defined and published on duplicate detection
- [ ] Cloud upload API response includes `accepted` and `reason` fields for Edge Agent consumption
- [ ] Edge Agent upload batch query excludes `UPLOADED`, `DUPLICATE_CONFIRMED`, `SYNCED_TO_ODOO`, `ARCHIVED` records
- [ ] Edge Agent sets `DUPLICATE_CONFIRMED` on cloud duplicate response
- [ ] Pre-auth dedup uses `(odooOrderId, siteCode)` with terminal-status re-request allowance
- [ ] `dedupWindowDays` config field added to site config spec
- [ ] `fccIdReuseBehaviour` adapter config field documented
- [ ] Secondary fuzzy match flags records for review without marking as duplicate
- [ ] Race condition handled via DB unique constraint with fallback to duplicate-row insert

## 11. Output Files to Create

| File | Location |
|---|---|
| This spec | `docs/specs/reconciliation/tier-2-2-deduplication-strategy.md` |

No machine-readable companion needed — dedup logic is procedural, not schema-driven.

## 12. Recommended Next TODO

**2.3 Reconciliation Rules Engine Design** — it consumes the fuzzy-match flags produced by dedup and defines the full matching algorithm.
