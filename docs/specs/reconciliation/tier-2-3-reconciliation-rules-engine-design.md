# Reconciliation Rules Engine Design

## 1. Output Location
- Target file path: `docs/specs/reconciliation/tier-2-3-reconciliation-rules-engine-design.md`
- Optional companion files: None
- Why this location matches `docs/STRUCTURE.md`: reconciliation matching rules, tolerance rules, and review flows belong in `/docs/specs/reconciliation`.

## 2. Scope
- TODO item addressed: `2.3 Reconciliation Rules Engine Design`
- In scope: match priority, candidate selection, tolerance config structure and scope, auto-approve versus review logic, Ops Manager review handling, unmatched retry and expiry rules
- Out of scope: portal UI layout, database DDL, event payload schemas, unrelated transaction dedup rules

## 3. Source Traceability
- Requirements referenced: `REQ-8`, `REQ-9`, `REQ-14`, `REQ-15`
- HLD sections referenced only: `WIP-HLD-Cloud-Backend.md` sections `4.3`, `5.2`, `6.6`; `WIP-HLD-Angular-Portal.md` sections `3.1.3`, `3.1.9`, `5`; `WIP-HLD-Edge-Agent.md` section `4`
- Assumptions from TODO ordering/dependencies: pre-auth and canonical transaction contracts are already defined; reconciliation state transitions from Tier `1.2` are authoritative and this artefact supplies the rule engine behavior behind them

## 4. Key Decisions

| Decision | Why | Impact |
|---|---|---|
| Match stages run in strict priority order: `fccCorrelationId` -> `pump+nozzle+time window` -> `odooOrderId` echoed by FCC | TODO item explicitly orders priority; implementation must be deterministic | Lower-priority keys are not evaluated if a single higher-priority match is found |
| Tolerance scope is `global` fallback -> `legalEntity` default -> optional `site` override; no product-level override in MVP | Portal HLD already supports global/per-legal-entity settings; product scope adds configuration and precedence complexity without changing amount-only variance logic | Config resolver is simple and implementable now; product-specific tolerance is deferred |
| Variance review does not block Odoo polling | Requirement BR-8.4 states flagged transactions still remain available to Odoo | `VARIANCE_FLAGGED`, `APPROVED`, and `REJECTED` are audit and operations states only |
| Unmatched reconciliation is retried for 24 hours, then left in `UNMATCHED` and escalated in portal | Tier 1.2 already establishes deferred matching behavior | Cloud worker schedule and portal alert behavior are fixed and testable |

## 5. Detailed Specification

### 5.1 Matching Rule Set

**Eligible dispense records**

| Field | Rule |
|---|---|
| Site applicability | Run reconciliation only when `siteUsesPreAuth = true` |
| Transaction eligibility | Run on non-duplicate final dispense transactions persisted as cloud `PENDING` |
| Pre-auth candidate status | Candidate pre-auths must be in `AUTHORIZED` or `DISPENSING` |
| Candidate site | Candidate pre-auth must share the same `siteCode` as the dispense |

**Execution algorithm**

| Step | Rule | Candidate filter | Success condition | Tie-breaker | On failure |
|---|---|---|---|---|---|
| 1 | `fccCorrelationId` exact match | Dispense `fccCorrelationId` not null; pre-auth `fccCorrelationId` equals it | Exactly one candidate | If multiple, choose most recent `authorizedAt`; mark `matchMethod = CORRELATION_ID`; add `ambiguityFlag = true` | Continue to Step 2 only when zero candidates |
| 2 | `pump+nozzle+time window` | Same `pumpNumber`, same `nozzleNumber`, `abs(dispense.completedAt - preAuth.authorizedAt) <= timeWindowMinutes` | Exactly one candidate | Choose smallest absolute time delta; if tied, choose latest `authorizedAt`; set `matchMethod = PUMP_NOZZLE_TIME` | Continue to Step 3 only when zero candidates |
| 3 | `odooOrderId` echoed by FCC | Dispense `odooOrderId` not null; same `siteCode`; pre-auth `odooOrderId` equals it | Exactly one candidate | If multiple, choose latest `authorizedAt`; set `matchMethod = ODOO_ORDER_ID` and `ambiguityFlag = true` | Create or retain `UNMATCHED` reconciliation record |

**Ambiguity handling**

| Condition | Handling |
|---|---|
| A step returns more than one candidate after tie-breaker | Proceed with selected candidate, set `ambiguityFlag = true`, append `ambiguityReason`, and force final status to at least `VARIANCE_FLAGGED` even if amount variance is within tolerance |
| Selected candidate already linked to another dispense | Reject candidate and continue searching remaining candidates in the same step; if none remain, proceed to next step |
| Match found in any step | Stop processing lower-priority steps |

### 5.2 Tolerance Configuration

**Canonical structure**

| Field | Type | Required | Validation | Notes |
|---|---|---|---|---|
| `amountTolerancePercent` | `decimal(5,2)` | Yes | `>= 0` and `<= 100` | Applied to absolute amount variance percent |
| `amountToleranceAbsolute` | `long` minor units | Yes | `>= 0` | Absolute amount variance cap in currency minor units |
| `timeWindowMinutes` | `int` | Yes | `>= 1` and `<= 60` | Used only by Step 2 |

**Scope resolution**

| Scope level | Supported | Key | Resolution order | Notes |
|---|---|---|---|---|
| `global` | Yes | `default` | 4 | Platform fallback when no narrower scope exists |
| `legalEntity` | Yes | `legalEntityId` | 3 | Required operational scope for production configuration |
| `site` | Yes | `siteCode` | 2 | Optional override for stations with known FCC timing drift |
| `product` | No | N/A | N/A | Explicitly out of MVP scope |

**Resolved tolerance**

| Precedence | Rule |
|---|---|
| 1 | Use site-level tolerance if present for `siteCode` |
| 2 | Else use legal-entity tolerance for `legalEntityId` |
| 3 | Else use global default |
| 4 | If none exist, system startup/config validation fails |

### 5.3 Reconciliation Outcome Rules

| Condition | Derived fields | Reconciliation status | Pre-auth update | Transaction availability to Odoo |
|---|---|---|---|---|
| No candidate matched | `matchMethod = NONE` | `UNMATCHED` | No pre-auth change | Remains `PENDING` |
| Match found and `absoluteVarianceMinorUnits = 0` | `varianceMinorUnits = 0`, `variancePercent = 0` | `MATCHED` | Set pre-auth `matchedTransactionId`, `actualAmountMinorUnits`, `actualVolumeMillilitres`, `completedAt`, `status = COMPLETED` | Remains `PENDING` |
| Match found and variance within tolerance | Compute variance fields | `VARIANCE_WITHIN_TOLERANCE` | Same as above | Remains `PENDING` |
| Match found and variance exceeds tolerance | Compute variance fields | `VARIANCE_FLAGGED` | Same as above | Remains `PENDING` |
| Match found but `ambiguityFlag = true` | Compute variance fields | `VARIANCE_FLAGGED` | Same as above | Remains `PENDING` |

**Variance formula**

| Field | Formula |
|---|---|
| `varianceMinorUnits` | `actualAmountMinorUnits - authorizedAmountMinorUnits` |
| `absoluteVarianceMinorUnits` | `abs(varianceMinorUnits)` |
| `variancePercent` | `0` when `authorizedAmountMinorUnits = 0`, else `(absoluteVarianceMinorUnits / authorizedAmountMinorUnits) * 100` |
| `withinTolerance` | `absoluteVarianceMinorUnits <= amountToleranceAbsolute OR variancePercent <= amountTolerancePercent` |

### 5.4 Ops Manager Review Handling

**Technical meaning of "flag for Ops Manager review"**

| Concern | Required behavior |
|---|---|
| Status field | Set reconciliation record `status = VARIANCE_FLAGGED` |
| Notification | Emit `ReconciliationVarianceFlagged` domain event; notification worker creates a portal notification scoped to the transaction `legalEntityId` |
| Portal queue | Record appears in `GET /api/v1/ops/reconciliation/exceptions` when status in `VARIANCE_FLAGGED, UNMATCHED` |
| Queue fields | API projection must include `reconciliationId`, `status`, `siteCode`, `legalEntityId`, `pumpNumber`, `nozzleNumber`, `authorizedAmountMinorUnits`, `actualAmountMinorUnits`, `varianceMinorUnits`, `variancePercent`, `matchMethod`, `ambiguityFlag`, `createdAt`, `lastMatchAttemptAt` |
| Approve action | `POST /api/v1/ops/reconciliation/{id}/approve` sets `status = APPROVED`, requires `reason`, records `reviewedByUserId`, `reviewedAtUtc`, `reviewReason` |
| Reject action | `POST /api/v1/ops/reconciliation/{id}/reject` sets `status = REJECTED`, requires `reason`, records same review fields |
| Authorization | Only `OperationsManager` and `SystemAdmin` may approve or reject |

### 5.5 Unmatched Handling

| Phase | Rule | Worker cadence |
|---|---|---|
| Initial attempt | Run synchronously when dispense is ingested | Immediate |
| Retry phase 1 | Re-attempt matching for new `UNMATCHED` records while `age <= 60 minutes` | Every 5 minutes |
| Retry phase 2 | Re-attempt matching while `60 minutes < age <= 24 hours` | Every 60 minutes |
| Give-up point | After `age > 24 hours`, stop automatic matching and leave status `UNMATCHED` | No more retries |
| Escalation | When record crosses 24 hours unmatched, create a portal notification `ReconciliationUnmatchedAged` | Once |

## 6. Validation and Edge Cases
- If the pre-auth arrives in cloud after the dispense, the worker must re-run matching using the original dispense timestamp and current tolerance config.
- `authorizedAmountMinorUnits = 0` is invalid for pre-auth reconciliation; such a record must be left `UNMATCHED` with `ambiguityReason = INVALID_AUTHORIZED_AMOUNT`.
- Time-window matching uses UTC timestamps only; site timezone is never applied inside the algorithm.
- If a flagged record is later approved or rejected, the linked transaction remains visible to Odoo only according to transaction `status`, not review outcome.
- The matching engine must be idempotent: reprocessing the same dispense cannot create a second reconciliation record or relink a completed pre-auth.

## 7. Cross-Component Impact

| Component | Impact |
|---|---|
| Cloud Backend | Implement ordered match stages, tolerance resolver, unmatched retry worker, review audit fields, and exceptions query projection |
| Angular Portal | Reconciliation workbench must show `VARIANCE_FLAGGED` and `UNMATCHED` queues, notifications, and approve/reject actions |

## 8. Dependencies
- Prerequisites: Tier `1.1` canonical transaction and pre-auth record contracts; Tier `1.2` reconciliation state machine; Tier `2.4` configuration schema must carry the tolerance object and scope keys
- Downstream TODOs affected: cloud reconciliation query API, notification/event specs, index strategy for reconciliation lookups, reconciliation scenario integration tests
- Recommended next implementation step: detail Tier `2.4 Configuration Schema (Full)` so the tolerance resolver can be implemented against a fixed config contract

## 9. Open Questions
None.

## 10. Acceptance Checklist
- [ ] Match priority is defined with deterministic candidate filters and tie-breakers.
- [ ] Tolerance object fields, validation, and scope precedence are explicit.
- [ ] Auto-approve versus review rules are defined using exact status values.
- [ ] "Flag for Ops Manager review" maps to status, notification, and portal queue behavior.
- [ ] Unmatched retry cadence and give-up point are fixed.
- [ ] Odoo availability behavior is explicit for matched, flagged, and unmatched outcomes.

## 11. Output Files to Create
- `docs/specs/reconciliation/tier-2-3-reconciliation-rules-engine-design.md`

## 12. Recommended Next TODO
`2.4 Configuration Schema (Full)`
