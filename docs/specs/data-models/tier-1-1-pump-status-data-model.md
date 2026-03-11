# PumpStatus Contract

## 1. Output Location
- Target file path: `docs/specs/data-models/tier-1-1-pump-status-data-model.md`
- Optional companion files: `schemas/canonical/pump-status.schema.json`
- Why this location matches `docs/STRUCTURE.md`: `PumpStatus` is a shared data model, so the contract belongs in `/docs/specs/data-models` and the companion schema belongs in `/schemas/canonical`.

## 2. Scope
- TODO item addressed: `Define PumpStatus — live pump state model (pump number, nozzle, state, current volume/amount, product)`
- In scope: pump status fields, local `PumpState` enum, and core freshness rules
- Out of scope: API behavior details, vendor-specific raw status codes, historical storage design

## 3. Source Traceability
- Requirements referenced: `REQ-3`, `REQ-6`, `REQ-15.6`, `REQ-15.8`, `REQ-15.9`
- HLD sections referenced: `WIP-HLD-Edge-Agent.md` section `6.3`; `WIP-HLD-Angular-Portal.md` section `3.1.5`
- Assumptions from TODO ordering/dependencies: `PumpState` is model-local because it is not listed in the shared-enum TODO; API details are defined later

## 4. Key Decisions

| Decision | Why | Impact |
|---|---|---|
| One record represents one site pump-nozzle pair. | Product and authorization are nozzle-level concepts. | Identity is `siteCode + pumpNumber + nozzleNumber`. |
| Product code is canonical, not vendor-native. | Clients should not depend on FCC-specific product values. | Adapters map raw product values before exposing status. |
| Live counters are nullable outside active or just-completed sales. | `0` would blur “not applicable” and “real zero.” | Consumers must treat null as no current live reading. |
| Freshness is explicit. | Pump status is volatile and can be synthesized during outages. | `observedAtUtc`, `lastChangedAtUtc`, and `source` stay on the model. |

## 5. Detailed Specification

### PumpState

| Enum | Value | Meaning | Usage Notes |
|---|---|---|---|
| `PumpState` | `IDLE` | No active authorization or sale. | Default resting state. |
| `PumpState` | `AUTHORIZED` | Pump is authorized but fuel is not flowing yet. | Typical pre-auth ready state. |
| `PumpState` | `CALLING` | Nozzle is selected or lifted and awaiting action. | Distinct from active fueling when vendor supports it. |
| `PumpState` | `DISPENSING` | Fuel is actively flowing. | Live counters should be populated when supported. |
| `PumpState` | `PAUSED` | Sale is active but temporarily stopped. | Resume returns to `DISPENSING`. |
| `PumpState` | `COMPLETED` | Sale has just completed. | Short-lived state before returning to `IDLE`. |
| `PumpState` | `ERROR` | Pump or nozzle is faulted. | Not safely usable until fault clears. |
| `PumpState` | `OFFLINE` | FCC cannot be reached for this nozzle. | Usually synthesized by edge after heartbeat failure. |
| `PumpState` | `UNKNOWN` | Raw status exists but cannot be confidently mapped. | Preserve raw FCC code for diagnostics if available. |

### PumpStatus fields

| Field | Type | Required | Produced By | Description |
|---|---|---|---|---|
| `schemaVersion` | `string` | Yes | Edge Agent | Contract version for the status object. |
| `siteCode` | `string` | Yes | Edge Agent | Site this pump status belongs to. |
| `pumpNumber` | `int` | Yes | FCC adapter | Physical pump number. |
| `nozzleNumber` | `int` | Yes | FCC adapter | Physical nozzle number. |
| `state` | `PumpState` | Yes | FCC adapter / edge synthesis | Canonical live state for the nozzle. |
| `productCode` | `string` | No | FCC adapter | Canonical product code for the active nozzle. |
| `productName` | `string` | No | Site config / master data | Friendly display name for the product. |
| `currentVolumeLitres` | `decimal-string` | No | FCC adapter | Current dispensed volume for the active sale. |
| `currentAmount` | `decimal-string` | No | FCC adapter | Current dispensed amount for the active sale. |
| `unitPrice` | `decimal-string` | No | FCC adapter / config fallback | Live display price for the active nozzle. |
| `currencyCode` | `string` | Yes | Site config | ISO 4217 currency for live amount display. |
| `fccStatusCode` | `string` | No | FCC adapter | Raw vendor status code kept for diagnostics. |
| `statusSequence` | `int` | Yes | Edge Agent | Monotonic sequence for latest-wins handling per nozzle. |
| `observedAtUtc` | `datetime` | Yes | Edge Agent | UTC timestamp when this reading was observed. |
| `lastChangedAtUtc` | `datetime` | No | Edge Agent | UTC timestamp when the canonical state last changed. |
| `source` | `string` | Yes | Edge Agent | `FCC_LIVE` or `EDGE_SYNTHESIZED`. |

## 6. Validation and Edge Cases
- The result set must contain at most one record per `siteCode + pumpNumber + nozzleNumber`.
- `currentVolumeLitres` and `currentAmount` should be null when no active or just-completed sale is present.
- `OFFLINE` may be synthesized when FCC heartbeat fails but nozzle mapping still exists.
- `COMPLETED` is transient and should age back to `IDLE` after the configured short retention period.
- Unknown vendor states map to `UNKNOWN` rather than a guessed canonical state.

## 7. Cross-Component Impact
- Edge Agent: primary owner of the live model and local API source.
- Angular Portal: may display mapped pump/nozzle/product information against this shape.
- Odoo / secondary HHTs: consume the local edge representation through the local API.

## 8. Dependencies
- Prerequisites: site config mappings, FCC adapter normalization rules
- Downstream TODOs affected: edge local API contract, FCC adapter interfaces
- Recommended next implementation step: define the edge local API contract using this exact object shape

## 9. Open Questions
None.

## 10. Acceptance Checklist
- [ ] `PumpState` values are fixed and one-line defined.
- [ ] Every `PumpStatus` field has type, required flag, owner, and one-line meaning.
- [ ] Nozzle-level identity is explicit.
- [ ] Freshness and synthesized-offline behavior are documented.
- [ ] Companion schema aligns with this contract.

## 11. Output Files to Create
- `docs/specs/data-models/tier-1-1-pump-status-data-model.md`
- `schemas/canonical/pump-status.schema.json`

## 12. Recommended Next TODO
Edge Agent Local API.
