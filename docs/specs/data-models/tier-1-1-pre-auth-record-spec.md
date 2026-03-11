# PreAuthRecord Contract

## 1. Output Location
- Target file path: `docs/specs/data-models/tier-1-1-pre-auth-record-spec.md`
- Optional companion files: `schemas/canonical/pre-auth-record.schema.json`
- Why this location matches `docs/STRUCTURE.md`: `PreAuthRecord` is a shared data model, so the contract belongs in `/docs/specs/data-models` and its machine-readable companion belongs in `/schemas/canonical`.

## 2. Scope
- TODO item addressed: `Define PreAuthRecord — full lifecycle fields including FCC correlation, amounts, timestamps, status`
- In scope: canonical pre-auth fields, ownership, nullability, and core lifecycle data captured on the record
- Out of scope: pre-auth state-machine transitions, API behavior, database DDL, FCC vendor command payloads

## 3. Source Traceability
- Requirements referenced: `REQ-5`, `REQ-6`, `REQ-8`, `REQ-13`, `REQ-14`, `REQ-15`
- HLD sections referenced: `WIP-HLD-Cloud-Backend.md` sections `4.3`, `6.6`; `WIP-HLD-Edge-Agent.md` sections `3`, `5.3`
- Assumptions from TODO ordering/dependencies: shared enums are defined separately; reconciliation rules are defined later; transaction linking uses the canonical transaction spec

## 4. Key Decisions

| Decision | Why | Impact |
|---|---|---|
| Monetary values use integer minor units. | Keeps financial math consistent with the rest of the platform. | `requestedAmountMinorUnits`, `authorizedAmountMinorUnits`, and `actualAmountMinorUnits` are all integer fields. |
| `fccCorrelationId` is the preferred link key to the final dispense. | Requirements prioritize FCC correlation for reconciliation. | Reconciliation logic can match pre-auths before falling back to time and pump heuristics. |
| `expiresAt` is stored, not computed on read. | Expiry checks need a queryable value on cloud and edge. | Workers can query directly for expired pre-auth records. |
| Edge cloud-forwarding metadata stays on the same record. | Needed for relay/retry without introducing a second object. | Edge can track unsent pre-auths with simple local queries. |

## 5. Detailed Specification

| Field | Type | Required | Produced By | Description |
|---|---|---|---|---|
| `id` | `uuid` | Yes | Middleware | Middleware primary key for the pre-auth record. |
| `siteCode` | `string` | Yes | Edge / cloud entrypoint | Site where the pre-auth was created. |
| `legalEntityId` | `uuid` | Yes | Cloud / config context | Legal entity that owns the site. |
| `odooOrderId` | `string` | Yes | Odoo request | Odoo order reference used for idempotency and downstream linkage. |
| `pumpNumber` | `int` | Yes | Odoo request | Target pump number for the authorization. |
| `nozzleNumber` | `int` | Yes | Odoo request | Target nozzle number for the authorization. |
| `productCode` | `string` | Yes | Odoo request / mapping | Canonical product code requested for dispensing. |
| `currencyCode` | `string` | Yes | Site config | ISO 4217 currency for all amount fields. |
| `requestedAmountMinorUnits` | `long` | Yes | Odoo request | Requested pre-auth amount in minor units. |
| `authorizedAmountMinorUnits` | `long` | No | FCC response | Amount approved by FCC when different from requested amount. |
| `actualAmountMinorUnits` | `long` | No | Reconciliation | Final dispensed amount when matched to a transaction. |
| `actualVolumeMillilitres` | `long` | No | Reconciliation | Final dispensed volume in millilitres when matched. |
| `status` | `PreAuthStatus` | Yes | Edge / cloud workflow | Current pre-auth lifecycle status. |
| `fccCorrelationId` | `string` | No | FCC response | FCC-issued reference used to match the later dispense. |
| `fccAuthorizationCode` | `string` | No | FCC response | Vendor authorization code when supplied separately from correlation ID. |
| `failureReason` | `string` | No | FCC response / workflow | Failure or cancellation reason when status is terminal-negative. |
| `customerName` | `string` | No | Odoo request | Customer display name for fiscalized flows when supplied. |
| `customerTaxId` | `string` | No | Odoo request | Customer tax identifier for fiscalized flows. |
| `requestedAt` | `datetime` | Yes | Edge / cloud workflow | UTC timestamp when the pre-auth was created. |
| `authorizedAt` | `datetime` | No | FCC response | UTC timestamp when authorization succeeded. |
| `completedAt` | `datetime` | No | Reconciliation | UTC timestamp when matched dispense completed the pre-auth. |
| `cancelledAt` | `datetime` | No | Workflow | UTC timestamp when pre-auth was cancelled. |
| `failedAt` | `datetime` | No | Workflow | UTC timestamp when pre-auth failed. |
| `expiresAt` | `datetime` | Yes | Workflow | UTC timestamp after which the pre-auth is considered expired. |
| `matchedTransactionId` | `uuid` | No | Reconciliation | Linked canonical transaction when a dispense is matched. |
| `rawFccResponse` | `string` | No | Edge adapter | Raw FCC authorization response kept locally for diagnostics. |
| `isCloudSynced` | `boolean` | Yes | Edge forwarder | Indicates whether the edge copy has been forwarded to cloud. |
| `cloudSyncAttempts` | `int` | Yes | Edge forwarder | Number of cloud-forward attempts made from edge. |
| `lastCloudSyncAttemptAt` | `datetime` | No | Edge forwarder | UTC timestamp of the latest forward attempt. |
| `schemaVersion` | `int` | Yes | Edge / cloud workflow | Canonical model version for the record. |

## 6. Validation and Edge Cases
- `odooOrderId + siteCode` is the idempotency key for pre-auth creation.
- `requestedAmountMinorUnits > 0`; populated actual and authorized amounts must also be positive.
- `expiresAt >= requestedAt`.
- `matchedTransactionId` is only valid when the record has been reconciled to a dispense.
- `customerTaxId` may be stored but must not be logged in plain text.
- `rawFccResponse` is edge-local diagnostic data and is not required in cloud persistence.

## 7. Cross-Component Impact
- Cloud Backend: stores the shared pre-auth record and uses it in reconciliation.
- Edge Agent: creates the record, forwards it, and retries unsynced items.
- Angular Portal: uses status, expiry, and match fields for operational review.
- FCC / Odoo: Odoo originates the request; FCC supplies authorization and correlation data.

## 8. Dependencies
- Prerequisites: shared enums, canonical transaction contract, site config timeout settings
- Downstream TODOs affected: pre-auth lifecycle, pre-auth API spec, reconciliation rules, database schema design
- Recommended next implementation step: define `PreAuthStatus` in the shared enums artefact and then lock the pre-auth API payloads

## 9. Open Questions
None.

## 10. Acceptance Checklist
- [ ] All pre-auth fields have type, required flag, owner, and one-line meaning.
- [ ] Idempotency key and reconciliation link fields are explicit.
- [ ] Expiry, failure, cancellation, and completion timestamps are separated.
- [ ] Edge-only sync metadata is clearly distinguished from shared business fields.
- [ ] Companion schema aligns with this contract.

## 11. Output Files to Create
- `docs/specs/data-models/tier-1-1-pre-auth-record-spec.md`
- `schemas/canonical/pre-auth-record.schema.json`

## 12. Recommended Next TODO
Define all shared enums.
