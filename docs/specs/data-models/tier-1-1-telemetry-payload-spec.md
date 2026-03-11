# TelemetryPayload Contract

## 1. Output Location
- Target file path: `docs/specs/data-models/tier-1-1-telemetry-payload-spec.md`
- Optional companion files: `schemas/canonical/telemetry-payload.schema.json`
- Why this location matches `docs/STRUCTURE.md`: telemetry is a shared object model reported by edge, so the contract belongs in `/docs/specs/data-models` and the schema belongs in `/schemas/canonical`.

## 2. Scope
- TODO item addressed: `Define TelemetryPayload — health metrics reported by Edge Agent`
- In scope: payload fields, nested metric groups, and lightweight reporting rules
- Out of scope: telemetry API status codes, storage schema, alert routing, dashboard UX

## 3. Source Traceability
- Requirements referenced: `REQ-3`, `REQ-15.1`, `REQ-15.9`, `REQ-15.12`, `REQ-16`
- HLD sections referenced: `WIP-HLD-Edge-Agent.md` sections `1.3`, `6.2`, `6.5`, `8.8`; `WIP-HLD-Cloud-Backend.md` section `6.2`; `WIP-HLD-Angular-Portal.md` section `3.1.4`
- Assumptions from TODO ordering/dependencies: `ConnectivityState` is defined in the shared-enum TODO; `SiteConfig` provides the telemetry interval

## 4. Key Decisions

| Decision | Why | Impact |
|---|---|---|
| Telemetry is best-effort and not buffered. | It is diagnostic data, not transactional data. | Missed reports are acceptable during outages. |
| Each report is a full snapshot. | Cloud consumers should not reconstruct state from deltas. | Every payload contains the full current health view. |
| `sequenceNumber` is monotonic per device. | Helps detect missing or out-of-order reports. | Edge must persist a simple counter. |
| Connectivity uses the shared enum. | Keeps monitoring aligned with platform state semantics. | Portal and cloud can interpret telemetry with the same state labels used elsewhere. |

## 5. Detailed Specification

### Root payload

| Field | Type | Required | Produced By | Description |
|---|---|---|---|---|
| `schemaVersion` | `string` | Yes | Edge Agent | Telemetry schema version. |
| `deviceId` | `uuid` | Yes | Edge Agent | Registered device identifier. |
| `siteCode` | `string` | Yes | Edge Agent | Site that emitted the telemetry. |
| `legalEntityId` | `uuid` | Yes | Edge Agent | Legal entity for the site. |
| `reportedAtUtc` | `datetime` | Yes | Edge Agent | UTC timestamp when the payload was assembled. |
| `sequenceNumber` | `int` | Yes | Edge Agent | Monotonic report counter per device. |
| `connectivityState` | `ConnectivityState` | Yes | Edge Agent | Current overall connectivity state. |
| `device` | `DeviceStatus` | Yes | Edge Agent | Snapshot of device health metrics. |
| `fccHealth` | `FccHealthStatus` | Yes | Edge Agent | Snapshot of FCC connectivity metrics. |
| `buffer` | `BufferStatus` | Yes | Edge Agent | Snapshot of local buffer metrics. |
| `sync` | `SyncStatus` | Yes | Edge Agent | Snapshot of cloud sync metrics. |
| `errorCounts` | `ErrorCounts` | Yes | Edge Agent | Rolling operational error counters. |

### Nested objects

| Object | Key fields | Description |
|---|---|---|
| `DeviceStatus` | `batteryPercent`, `isCharging`, `storageFreeMb`, `storageTotalMb`, `memoryFreeMb`, `memoryTotalMb`, `appVersion`, `appUptimeSeconds`, `osVersion`, `deviceModel` | Android device health and runtime metrics. |
| `FccHealthStatus` | `isReachable`, `lastHeartbeatAtUtc`, `heartbeatAgeSeconds`, `fccVendor`, `fccHost`, `fccPort`, `consecutiveHeartbeatFailures` | FCC reachability and heartbeat details. |
| `BufferStatus` | `totalRecords`, `pendingUploadCount`, `syncedCount`, `syncedToOdooCount`, `failedCount`, `oldestPendingAtUtc`, `bufferSizeMb` | Local SQLite transaction-buffer health. |
| `SyncStatus` | `lastSyncAttemptUtc`, `lastSuccessfulSyncUtc`, `syncLagSeconds`, `lastStatusPollUtc`, `lastConfigPullUtc`, `configVersion`, `uploadBatchSize` | Cloud communication and backlog timing metrics. |
| `ErrorCounts` | `fccConnectionErrors`, `cloudUploadErrors`, `cloudAuthErrors`, `localApiErrors`, `bufferWriteErrors`, `adapterNormalizationErrors`, `preAuthErrors` | Rolling counters for the main operational failure categories. |

## 6. Validation and Edge Cases
- `sequenceNumber >= 1` and should increase by one per emitted report.
- `batteryPercent` must be in `0..100`.
- Storage, memory, and count fields must not be negative.
- `reportedAtUtc` is the device clock timestamp; cloud may store its own receive time separately.
- When cloud is unreachable, the agent skips the report and emits a fresh snapshot at the next interval.

## 7. Cross-Component Impact
- Edge Agent: assembles and posts the payload.
- Cloud Backend: accepts and stores the current health snapshot.
- Angular Portal: renders the same grouped metrics in monitoring views.

## 8. Dependencies
- Prerequisites: device registration, site config telemetry interval, shared connectivity enum
- Downstream TODOs affected: telemetry API spec, alerting strategy, monitoring UI
- Recommended next implementation step: define the telemetry API contract using this payload shape

## 9. Open Questions
None.

## 10. Acceptance Checklist
- [ ] Root payload fields are fixed with types and meanings.
- [ ] Nested metric groups are named and scoped clearly.
- [ ] Reporting semantics are concise and unambiguous.
- [ ] No API or storage design detail is mixed into the model contract.
- [ ] Companion schema aligns with this contract.

## 11. Output Files to Create
- `docs/specs/data-models/tier-1-1-telemetry-payload-spec.md`
- `schemas/canonical/telemetry-payload.schema.json`

## 12. Recommended Next TODO
Edge Agent Telemetry API.
