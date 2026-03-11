# CanonicalTransaction Contract

## 1. Output Location
- Target file path: `docs/specs/data-models/tier-1-1-canonical-transaction-spec.md`
- Optional companion files: `schemas/canonical/canonical-transaction.schema.json`
- Why this location matches `docs/STRUCTURE.md`: `CanonicalTransaction` is a shared data model, so the human-readable contract belongs in `/docs/specs/data-models` and the machine-readable schema belongs in `/schemas/canonical`.

## 2. Scope
- TODO item addressed: `Define CanonicalTransaction — all fields, types, nullability, validation rules, which system produces each field`
- In scope: canonical transaction fields, nullability, ownership, and core validation rules
- Out of scope: lifecycle transitions, API envelopes, database DDL, raw FCC-to-canonical mapping details

## 3. Source Traceability
- Requirements referenced: `REQ-5`, `REQ-6`, `REQ-7`, `REQ-8`, `REQ-9`, `REQ-10`, `REQ-13`, `REQ-14`
- HLD sections referenced: `WIP-HLD-Cloud-Backend.md` sections `4.3`, `4.4`, `5.2`, `7.7`; `WIP-HLD-Edge-Agent.md` sections `4.4`, `5.3`
- Assumptions from TODO ordering/dependencies: shared enums are defined separately; state-machine behavior is defined in Tier `1.2`

## 4. Key Decisions

| Decision | Why | Impact |
|---|---|---|
| Money fields use integer minor units. | Matches cross-cutting rule to avoid floating point. | `amountMinorUnits` and `unitPriceMinorPerLitre` are integer fields across cloud and edge. |
| Volume uses integer microlitres. | Preserves precision without decimals. | `volumeMicrolitres` is the canonical quantity field. |
| `fccTransactionId` is opaque; middleware uses its own UUID primary key. | FCC IDs are site-scoped and vendor-specific. | Dedup key is `(fccTransactionId, siteCode)`; internal joins use `id`. |
| Cloud stores raw payload by reference; edge stores it inline. | Cloud HLD places archived payloads in S3, while edge has only local SQLite. | `rawPayloadRef` is cloud-only in practice; `rawPayloadJson` is edge-only in practice. |

## 5. Detailed Specification

### FCC-sourced fields

| Field | Type | Required | Produced By | Description |
|---|---|---|---|---|
| `fccTransactionId` | `string` | Yes | FCC adapter | Opaque FCC transaction identifier. |
| `siteCode` | `string` | Yes | FCC adapter / site context | Site where the dispense occurred. |
| `pumpNumber` | `int` | Yes | FCC adapter | Canonical pump number after any configured offset. |
| `nozzleNumber` | `int` | Yes | FCC adapter | Physical nozzle number used for the dispense. |
| `productCode` | `string` | Yes | FCC adapter | Canonical product code after mapping from vendor payload. |
| `volumeMicrolitres` | `long` | Yes | FCC adapter | Dispensed volume in microlitres. |
| `amountMinorUnits` | `long` | Yes | FCC adapter | Total transaction amount in currency minor units. |
| `unitPriceMinorPerLitre` | `long` | Yes | FCC adapter | Unit price per litre in currency minor units. |
| `startedAt` | `datetime` | Yes | FCC adapter | Dispense start timestamp in UTC ISO 8601 format. |
| `completedAt` | `datetime` | Yes | FCC adapter | Dispense completion timestamp in UTC ISO 8601 format. |
| `fiscalReceiptNumber` | `string` | No | FCC adapter | FCC fiscal receipt reference when available. |
| `fccVendor` | `FccVendor` | Yes | FCC adapter | Vendor that produced the source payload. |
| `attendantId` | `string` | No | FCC adapter | Operator or attendant identifier when present in FCC data. |

### Middleware-enriched fields

| Field | Type | Required | Produced By | Description |
|---|---|---|---|---|
| `id` | `uuid` | Yes | Ingestion pipeline | Middleware primary key for the transaction. |
| `legalEntityId` | `uuid` | Yes | Ingestion pipeline | Legal entity that owns the site. |
| `currencyCode` | `string` | Yes | Ingestion pipeline | ISO 4217 currency for all amount fields. |
| `status` | `TransactionStatus` | Yes | Ingestion pipeline / sync workers | Current transaction lifecycle status. |
| `ingestionSource` | `IngestionSource` | Yes | Ingestion entrypoint | Path that delivered the transaction to cloud. |
| `rawPayloadRef` | `string` | No | Ingestion pipeline | Cloud archive reference for the raw FCC payload. |
| `rawPayloadJson` | `string` | No | Edge ingestion path | Raw FCC payload stored inline on edge. |
| `ingestedAt` | `datetime` | Yes | Ingestion pipeline | Timestamp when middleware first persisted the record. |
| `updatedAt` | `datetime` | Yes | Any updater | Timestamp of the latest mutation to the canonical record. |
| `schemaVersion` | `int` | Yes | Adapter / ingestion pipeline | Canonical model version carried on the record. |
| `odooOrderId` | `string` | No | Odoo acknowledge flow | Odoo order identifier stamped after acknowledgement. |
| `preAuthId` | `uuid` | No | Reconciliation module | Linked pre-auth record when this dispense is matched. |
| `reconciliationStatus` | `ReconciliationStatus` | No | Reconciliation module | Match result for pre-auth reconciliation workflows. |
| `isDuplicate` | `boolean` | Yes | Dedup module | Indicates the record was retained as a duplicate. |
| `duplicateOfId` | `uuid` | No | Dedup module | Reference to the original transaction when duplicate. |
| `correlationId` | `uuid` | Yes | Ingestion pipeline | Trace identifier propagated through logs and events. |

## 6. Validation and Edge Cases
- `fccTransactionId` must be non-empty and is never parsed for structure.
- `siteCode`, `productCode`, and `currencyCode` must match registered master data.
- `startedAt <= completedAt`.
- `volumeMicrolitres > 0`, `amountMinorUnits > 0`, and `unitPriceMinorPerLitre > 0`.
- Exactly one raw payload field should be populated per runtime path: cloud uses `rawPayloadRef`; edge uses `rawPayloadJson`.
- `duplicateOfId` is only valid when `isDuplicate = true`.

## 7. Cross-Component Impact
- Cloud Backend: persists and serves the canonical transaction record.
- Edge Agent: buffers the same model before upload, with inline raw payload storage.
- Angular Portal: reads status, reconciliation, and duplicate flags for operations views.
- Odoo / FCC: FCC supplies source fields; Odoo later stamps `odooOrderId`.

## 8. Dependencies
- Prerequisites: shared enums, site master data, product mapping rules
- Downstream TODOs affected: transaction lifecycle, ingestion APIs, cloud and edge schema design, DOMS mapping spec
- Recommended next implementation step: define shared enums and then finalize API payload contracts against this field set

## 9. Open Questions
None.

## 10. Acceptance Checklist
- [ ] Every canonical transaction field has type, required flag, owner, and one-line meaning.
- [ ] Monetary and volume units are fixed and unambiguous.
- [ ] Dedup identity and middleware identity are both explicit.
- [ ] Cloud-versus-edge raw payload handling is documented.
- [ ] Companion schema aligns with this contract.

## 11. Output Files to Create
- `docs/specs/data-models/tier-1-1-canonical-transaction-spec.md`
- `schemas/canonical/canonical-transaction.schema.json`

## 12. Recommended Next TODO
Define all shared enums.
