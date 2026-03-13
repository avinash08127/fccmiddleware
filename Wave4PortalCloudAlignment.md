# Wave 4 - Portal Frontend / Cloud Backend Alignment Analysis

**Date:** 2026-03-14
**Scope:** Portal Angular frontend (`src/portal/`) vs Cloud ASP.NET Core backend (`src/cloud/`)
**Method:** Full cross-reference of every frontend service call, model, and enum against backend controllers, DTOs, and domain entities.

---

## Executive Summary

The portal frontend and cloud backend are **largely structurally aligned** on core CRUD operations (transactions, agents, sites, settings). However, there are **6 critical issues** that will cause runtime failures or silent data loss, **5 high-severity enum divergences** that will cause incorrect UI rendering, and several medium/low issues. The most impactful problems are: a field-name mismatch on reconciliation exceptions, a missing portal-scoped acknowledge endpoint, missing backend endpoints, and dashboard property casing drift.

---

## CRITICAL - Will Cause Runtime Failures

### C-01: Reconciliation Exception `reconciliationStatus` vs `status` Field Name

**STATUS: RESOLVED**

| | Frontend | Backend |
|---|---|---|
| **Model** | `ReconciliationException.reconciliationStatus` | `ReconciliationExceptionDto.Status` |
| **JSON key** | expects `reconciliationStatus` | sends `status` |

**Impact:** When the portal fetches `GET /api/v1/ops/reconciliation/exceptions`, the deserialized `reconciliationStatus` property is **always `undefined`**. The reconciliation list page cannot display or filter by status correctly.

**Note:** The query parameter mapping is correct (the service maps `reconciliationStatus` -> `status` for the request param), but the _response_ field name mismatch is not handled.

**Fix:** Rename frontend field to `status: ReconciliationStatus` in `ReconciliationException`, or add a backend mapping to serialize as `reconciliationStatus`.

**Resolution:** Renamed `ReconciliationException.reconciliationStatus` to `status` in `reconciliation.model.ts`. Updated all template references in `reconciliation-list.component.ts` (`ex.reconciliationStatus` -> `ex.status`). Also added missing backend fields (`reconciliationId`, `authorizedAmountMinorUnits`, `actualAmountMinorUnits`, `varianceMinorUnits`, `lastMatchAttemptAt`) as part of M-03.

---

### C-02: Transaction Acknowledge Endpoint Path + Auth Mismatch

**STATUS: RESOLVED (pre-existing)**

| | Frontend | Backend |
|---|---|---|
| **Path** | `POST /api/v1/ops/transactions/acknowledge` | `POST /api/v1/transactions/acknowledge` |
| **Auth** | Portal Bearer (MSAL JWT) | OdooApiKey (`X-Api-Key` header) |

**Impact:** The portal's acknowledge call will receive **404 Not Found** because the `OpsTransactionsController` (`/api/v1/ops/transactions`) does not expose an `/acknowledge` action. The backend acknowledge endpoint exists only on the Odoo-facing `TransactionsController` (`/api/v1/transactions`) and requires Odoo API key auth, not portal bearer auth.

**Fix:** Add a portal-scoped acknowledge endpoint to `OpsTransactionsController` with `PortalAdminWrite` policy, or move the frontend call to the correct path and switch auth.

**Resolution:** `OpsTransactionsController` already has `POST /acknowledge` with `PortalAdminWrite` policy. The endpoint was added in a prior change. Frontend path `/api/v1/ops/transactions/acknowledge` is now correctly served.

---

### C-03: Dashboard `bySource` Property Casing Mismatch

**STATUS: RESOLVED**

| | Frontend | Backend |
|---|---|---|
| **Model** | `TransactionVolumeHourlyBucket.bySource` | `TransactionVolumeBySourceDto` |
| **Keys** | `FCC_PUSH`, `EDGE_UPLOAD`, `CLOUD_PULL` | `FccPush`, `EdgeUpload`, `CloudPull` |
| **JSON** | expects `FCC_PUSH` etc. | sends `fccPush` etc. (camelCase) |

**Impact:** The dashboard transaction volume chart will show **zero for all sources** because the frontend reads `bucket.bySource.FCC_PUSH` but the JSON contains `bucket.bySource.fccPush`.

**Fix:** Align property keys. Either rename frontend to `fccPush`/`edgeUpload`/`cloudPull`, or rename backend DTO properties.

**Resolution:** Renamed frontend keys in `dashboard.model.ts` from `FCC_PUSH`/`EDGE_UPLOAD`/`CLOUD_PULL` to `fccPush`/`edgeUpload`/`cloudPull`. Updated `transaction-volume-chart.component.ts` and `dashboard.component.spec.ts` to match.

---

### C-04: Dashboard Alerts Endpoint Likely Missing

**STATUS: RESOLVED (pre-existing)**

| | Frontend | Backend |
|---|---|---|
| **Call** | `GET /api/v1/admin/dashboard/alerts` | Not found in `AdminDashboardController` |

**Impact:** The dashboard alerts panel will receive **404** or connection error. The `AdminDashboardController` only exposes `/summary`.

**Fix:** Add `GET /api/v1/admin/dashboard/alerts` endpoint returning `DashboardAlertsResponseDto`.

**Resolution:** `AdminDashboardController` already has `GET /alerts` endpoint returning `DashboardAlertsResponseDto` with threshold-based alerts. The endpoint was added in a prior change.

---

### C-05: Master Data Sync Status Endpoint Likely Missing

**STATUS: RESOLVED (pre-existing)**

| | Frontend | Backend |
|---|---|---|
| **Call** | `GET /api/v1/master-data/sync-status` | Not found in `MasterDataBrowserController` |

**Impact:** The master data monitoring page will receive **404**. The `MasterDataBrowserController` exposes `/legal-entities`, `/products`, `/sites`, `/pumps`, `/operators` but no `/sync-status`.

**Fix:** Add `GET /api/v1/master-data/sync-status` endpoint returning `List<MasterDataSyncStatusDto>`.

**Resolution:** `MasterDataBrowserController` already has `GET /sync-status` endpoint returning `IReadOnlyList<MasterDataSyncStatusDto>` with staleness detection (24-hour threshold). The endpoint was added in a prior change.

---

### C-06: Portal Client Logging Endpoint Likely Missing

**STATUS: RESOLVED**

| | Frontend | Backend |
|---|---|---|
| **Call** | `POST /api/v1/portal/client-logs` | No matching controller found |

**Impact:** When `environment.backendLoggingEnabled` is true and an ERROR-level event occurs, the POST will fail silently (the `LoggingService` may catch the error). No functional breakage for the user, but structured client error logs are lost.

**Fix:** Add a lightweight `PortalLogsController` or minimal API endpoint to ingest `StructuredLogEntry` payloads.

**Resolution:** Added `PortalLogsController` at `Controllers/PortalLogsController.cs` with `POST /api/v1/portal/client-logs` endpoint. Accepts `ClientLogEntry` payloads, maps log levels, and writes to ASP.NET Core `ILogger`. Protected by `PortalUser` auth policy. Returns 204 No Content.

---

## HIGH - Enum Value Divergences (Incorrect UI Display / Broken Filters)

### H-01: `IngestionSource` Enum Values Diverge

**STATUS: RESOLVED (pre-existing)**

| Frontend | Backend (Domain Entity) |
|---|---|
| `FCC_PUSH` | - |
| `EDGE_UPLOAD` | `EDGE_AGENT` |
| `CLOUD_PULL` | - |
| `CLOUD_DIRECT` | `CLOUD_DIRECT` |
| `WEBHOOK` | - |
| - | `UNKNOWN` |

**Impact:** Only `CLOUD_DIRECT` matches between both sides. Transactions ingested via edge agents will show as `EDGE_AGENT` from the backend, but the frontend has no badge/label for that value. Filter dropdowns for ingestion source will not work correctly. The `PortalTransactionDto` uses `string` for this field, so the raw value passes through.

**Fix:** Unify enum values. Decide on a single set and update both sides.

**Resolution:** Backend `IngestionSource` enum was updated in a prior change to match: `FCC_PUSH`, `EDGE_UPLOAD`, `CLOUD_PULL`, `CLOUD_DIRECT`, `WEBHOOK`. Both sides are now aligned.

---

### H-02: `PreAuthStatus` Enum Values Diverge

**STATUS: RESOLVED (pre-existing)**

| Frontend | Backend |
|---|---|
| `PENDING` | `PENDING_APPROVAL` |
| `AUTHORIZED` | `APPROVED` |
| `DISPENSING` | _(no equivalent)_ |
| `COMPLETED` | `CAPTURED` |
| `CANCELLED` | `DECLINED` |
| `EXPIRED` | `EXPIRED` |
| `FAILED` | `FAILED` |

**Impact:** Pre-auth status badges, filters, and lifecycle displays in the reconciliation detail view will show raw backend values (`PENDING_APPROVAL`, `APPROVED`, `CAPTURED`, `DECLINED`) that don't match any frontend enum member, causing badge rendering failures or "unknown" fallback states.

**Fix:** Align enum values. The backend values (`PENDING_APPROVAL`, `APPROVED`, `CAPTURED`, `DECLINED`) are more precise; update frontend to match.

**Resolution:** Backend `PreAuthStatus` enum was updated in a prior change to match: `PENDING`, `AUTHORIZED`, `DISPENSING`, `COMPLETED`, `CANCELLED`, `EXPIRED`, `FAILED`. Both sides are now aligned.

---

### H-03: `ConnectivityState` Enum Values Diverge

**STATUS: RESOLVED (pre-existing)**

| Frontend | Backend |
|---|---|
| `FULLY_ONLINE` | `FULLY_ONLINE` |
| `INTERNET_DOWN` | _(no equivalent)_ |
| `FCC_UNREACHABLE` | _(no equivalent)_ |
| - | `PARTIALLY_ONLINE` |
| `FULLY_OFFLINE` | `FULLY_OFFLINE` |

**Impact:** Backend returns `PARTIALLY_ONLINE` for degraded states, but the frontend has no matching enum value. The agent list's connectivity badge will show a raw string or fallback. The frontend's finer-grained `INTERNET_DOWN`/`FCC_UNREACHABLE` states will never be returned by the backend.

**Fix:** Decide on granularity. If backend collapses to `PARTIALLY_ONLINE`, frontend should match. If finer granularity is needed, update backend to return the specific sub-state.

**Resolution:** Backend `ConnectivityState` enum was updated in a prior change to match the frontend's finer-grained states: `FULLY_ONLINE`, `INTERNET_DOWN`, `FCC_UNREACHABLE`, `FULLY_OFFLINE`. Both sides are now aligned.

---

### H-04: `EventType` (Audit) Casing + Naming Divergence

**STATUS: RESOLVED (pre-existing)**

| Frontend (PascalCase) | Backend (SCREAMING_SNAKE_CASE) |
|---|---|
| `TransactionIngested` | `TRANSACTION_CREATED` |
| `TransactionDeduplicated` | (unknown if exists) |
| `TransactionSyncedToOdoo` | (unknown if exists) |
| `PreAuthCreated` | `PRE_AUTH_UPDATED` |
| `AgentRegistered` | `DEVICE_REGISTERED` |
| etc. | etc. |

**Impact:** The audit event type filter dropdown and event type badges will not match any backend values. Users filtering by `TransactionIngested` will get zero results because the backend stores `TRANSACTION_CREATED`. The audit log page is functionally broken for type filtering.

**Fix:** Align naming convention. Adopt one casing standard and reconcile event type names.

**Resolution:** Frontend `EventType` was changed from an enum to a flexible `string` type with a `KNOWN_AUDIT_EVENT_TYPES` const array in `audit.model.ts`. The known types list includes both PascalCase and SCREAMING_SNAKE_CASE values. The backend does not have a dedicated EventType enum — it stores event types as strings. The frontend now handles any string value the backend sends.

---

### H-05: `DeadLetterStatus` Enum Values Diverge

**STATUS: RESOLVED (pre-existing)**

| Frontend | Backend |
|---|---|
| `PENDING` | `PENDING` |
| `REPLAY_QUEUED` | _(no equivalent)_ |
| `RETRYING` | _(no equivalent)_ |
| `RESOLVED` | `RESOLVED` |
| `REPLAY_FAILED` | _(no equivalent)_ |
| `DISCARDED` | `DISCARDED` |
| - | `UNDER_REVIEW` |

**Impact:** DLQ status filter dropdown includes values (`REPLAY_QUEUED`, `RETRYING`, `REPLAY_FAILED`) that the backend never returns. Backend returns `UNDER_REVIEW` which the frontend doesn't display with a proper badge.

**Fix:** Align enum values. Determine the actual DLQ state machine and update both sides.

**Resolution:** Backend `DeadLetterStatus` enum was updated in a prior change to match: `PENDING`, `REPLAY_QUEUED`, `RETRYING`, `RESOLVED`, `REPLAY_FAILED`, `DISCARDED`. Both sides are now aligned.

---

## MEDIUM - Functional Gaps / Missing Fields

### M-01: `AgentHealthSummary` Missing `hasTelemetry` Field

**STATUS: RESOLVED**

| | Frontend | Backend |
|---|---|---|
| **Field** | _(not present)_ | `HasTelemetry: bool` |

**Impact:** The agent list cannot distinguish agents that have never sent telemetry from those with stale telemetry. May lead to misleading "N/A" displays.

**Fix:** Add `hasTelemetry: boolean` to `AgentHealthSummary` interface.

**Resolution:** Added `hasTelemetry: boolean` to `AgentHealthSummary` interface in `agent.model.ts`.

---

### M-02: `AgentRegistration` Has Unused `environment` Field

**STATUS: RESOLVED**

| | Frontend | Backend |
|---|---|---|
| **Field** | `environment?: string` | _(not present in DTO)_ |

**Impact:** `environment` will always be `undefined` after deserialization. If the UI displays it, it shows blank. Low functional risk since it's optional.

**Fix:** Remove from frontend model or add to backend `AgentRegistrationDto`.

**Resolution:** Removed `environment` from `AgentRegistration` interface in `agent.model.ts`. Removed template references in `agent-detail.component.ts` that displayed the environment badge.

---

### M-03: `ReconciliationException` Missing Backend Fields

**STATUS: RESOLVED**

Backend `ReconciliationExceptionDto` returns fields that the frontend does not model:

| Backend Field | Type | Purpose |
|---|---|---|
| `ReconciliationId` | `Guid` | Separate reconciliation record ID |
| `AuthorizedAmountMinorUnits` | `long?` | Pre-auth authorized amount |
| `ActualAmountMinorUnits` | `long?` | Actual dispensed amount |
| `VarianceMinorUnits` | `long?` | Absolute variance in minor units |
| `LastMatchAttemptAt` | `DateTimeOffset` | When matching was last tried |

**Impact:** These fields are silently dropped during deserialization. The portal cannot display authorized vs actual amount in minor units or show when matching was last attempted. The existing `requestedAmount`/`actualAmount` fields overlap but may have different semantics.

**Fix:** Add missing fields to `ReconciliationException` interface.

**Resolution:** Added all five fields to `ReconciliationException` interface in `reconciliation.model.ts`: `reconciliationId`, `authorizedAmountMinorUnits`, `actualAmountMinorUnits`, `varianceMinorUnits`, `lastMatchAttemptAt`.

---

### M-04: `FiscalizationMode` Enum Values Diverge

**STATUS: RESOLVED (pre-existing)**

| Frontend | Backend |
|---|---|
| `FCC_DIRECT` | _(no equivalent)_ |
| `EXTERNAL_INTEGRATION` | _(no equivalent)_ |
| `NONE` | `NONE` |
| - | `NFC_E` |
| - | `CF_E` |
| - | `SATCOD` |

**Impact:** Site configuration display will show raw backend values (Brazilian fiscal modes) that the frontend doesn't recognize. The frontend's abstract modes (`FCC_DIRECT`, `EXTERNAL_INTEGRATION`) don't exist in the backend.

**Fix:** Align to backend's concrete fiscal modes.

**Resolution:** Backend `FiscalizationMode` enum was updated in a prior change to match: `FCC_DIRECT`, `EXTERNAL_INTEGRATION`, `NONE`. Both sides are now aligned.

---

### M-05: `SiteOperatingModel` Enum May Diverge

**STATUS: RESOLVED (pre-existing)**

| Frontend | Backend (may vary) |
|---|---|
| `COCO` | Possibly `CLOUD_CONNECTED` |
| `CODO` | Possibly `CLOUD_EDGE_HYBRID` |
| `DODO` | _(unknown)_ |
| `DOCO` | _(unknown)_ |

**Impact:** If backend uses connectivity-based model names and frontend uses fuel-industry operating model codes, the site list operating model column and filter will be broken.

**Fix:** Verify actual backend enum values and align.

**Resolution:** Backend `SiteOperatingModel` enum was verified to match: `COCO`, `CODO`, `DODO`, `DOCO` (with XML doc comments). Both sides are now aligned.

---

### M-06: `Reconciliation` Query Param `decision` Not Supported by Backend

**STATUS: RESOLVED**

| | Frontend | Backend |
|---|---|---|
| **Query Param** | sends `decision` filter | Does not accept `decision` param |

Backend exception listing accepts: `legalEntityId`, `siteCode`, `status`, `from`, `to`, `since`, `cursor`, `pageSize`.

**Impact:** Portal users cannot filter reconciliation exceptions by decision (APPROVED/REJECTED/PENDING_DECISION). The `decision` param is silently ignored by the backend.

**Fix:** Add `decision` query param support to `OpsReconciliationController`.

**Resolution:** Added `decision` query parameter to `OpsReconciliationController.GetExceptions()`. Accepts `APPROVED`, `REJECTED`, or `PENDING_DECISION`. Validates input and derives the effective status filter when both `status` and `decision` are provided.

---

### M-07: Transaction `fields` Query Param Not Supported by Backend

**STATUS: WON'T FIX (deferred)**

| | Frontend | Backend |
|---|---|---|
| **Query Param** | sends `fields` for field projection | Does not accept `fields` param |

**Impact:** Silently ignored. Frontend always receives full transaction DTOs regardless of the `fields` parameter. No functional breakage, but prevents bandwidth optimization.

**Resolution:** No action taken. The `fields` parameter is silently ignored by the backend with no functional impact. Field projection is a bandwidth optimization that can be implemented later if needed. The full DTO is always returned.

---

### M-08: `ErrorResponse` Missing `retryable` Field

**STATUS: RESOLVED**

| | Frontend | Backend |
|---|---|---|
| **Field** | _(not present)_ | `Retryable: bool` |

**Impact:** The portal error handler cannot distinguish retryable from non-retryable errors for automatic retry logic or user messaging.

**Fix:** Add `retryable?: boolean` to `ErrorResponse` interface.

**Resolution:** Added `retryable?: boolean` to `ErrorResponse` interface in `common.model.ts`.

---

### M-09: `BufferStatusDto.bufferSizeMb` Type Precision Mismatch

**STATUS: WON'T FIX (acceptable)**

| | Frontend | Backend |
|---|---|---|
| **Type** | `number` (float-capable) | `int` |

**Impact:** Minor - telemetry display will show whole numbers only. Sub-megabyte buffer sizes will display as 0.

**Resolution:** No action taken. TypeScript `number` is a superset of `int` — no deserialization issue occurs. The integer precision from the backend is acceptable for the telemetry display use case.

---

## LOW - Cosmetic / Non-Breaking

### L-01: `VersionCheckResponse` Extra Frontend Fields

**STATUS: RESOLVED**

Frontend defines `minSupportedVersion` and `downloadUrl` that the backend does not return. These will always be `undefined`. No functional impact if UI handles `null`/`undefined` gracefully.

**Resolution:** Removed `minSupportedVersion` and `downloadUrl` from `VersionCheckResponse` in `agent.model.ts`. These duplicated existing fields (`minimumVersion` and `updateUrl`).

---

### L-02: `ApproveRejectRequest` Model Orphaned

**STATUS: RESOLVED**

Frontend defines `ApproveRejectRequest` with a `decision` field, but the reconciliation service sends `{ reason }` only. The model is unused dead code.

**Resolution:** Removed `ApproveRejectRequest` interface from `reconciliation.model.ts`. The reconciliation service correctly sends `{ reason }` directly — the approve/reject decision is implicit in the endpoint path (`/approve` vs `/reject`).

---

### L-03: Decommission Reason Minimum Length Not Enforced Client-Side

**STATUS: RESOLVED (pre-existing)**

Backend `DecommissionRequest.Reason` requires `[MinLength(10)]`. Frontend sends `{ reason: string }` without length validation. Users will see a 400 error from the backend for short reasons.

**Fix:** Add `minLength: 10` validation in the decommission dialog component.

**Resolution:** Already implemented. The `confirmDecommission()` method in `agent-detail.component.ts` validates `reason.trim().length < 10` before submitting and shows an alert if too short.

---

### L-04: `ReconciliationRecordDto` vs `ReconciliationRecord` Minor Field Differences

**STATUS: NO ACTION NEEDED**

Backend `ReconciliationRecordDto` has `ReconciliationStatus: string` (required), frontend has `reconciliationStatus: ReconciliationStatus | null`. The backend field is non-nullable but the frontend allows null. Not a runtime issue since backend always sends a value.

**Resolution:** No action needed. The frontend's nullable typing is a defensive pattern. Since the backend always populates the field, null is never encountered at runtime.

---

### L-05: Reconciliation Review Response `ReviewedAtUtc` Field

**STATUS: NO ACTION NEEDED**

The response field `reviewedAtUtc` comes as an ISO 8601 string from JSON serialization. Frontend types it as `string` which is correct. No issue.

**Resolution:** No action needed. Types are correctly aligned.

---

### L-06: `MasterDataSyncResponse` Unused by Portal Read Path

**STATUS: RESOLVED**

The frontend defines `MasterDataSyncResponse` (with `upsertedCount`, `unchangedCount`, etc.) but the portal only reads master data, never writes it. This model mirrors the Databricks sync response and is not used by any portal service call. Dead code.

**Resolution:** Removed `MasterDataSyncResponse` interface from `master-data.model.ts`. No imports or references existed elsewhere.

---

## Endpoint Inventory Cross-Reference

### Portal Frontend Calls vs Backend Availability

| Frontend Endpoint | Backend Match | Status |
|---|---|---|
| `GET /api/v1/agents` | `AgentsController` | Aligned |
| `GET /api/v1/agents/{id}` | `AgentsController` | Aligned |
| `GET /api/v1/agents/{id}/telemetry` | `AgentsController` | Aligned |
| `GET /api/v1/agents/{id}/events` | `AgentsController` | Aligned |
| `GET /api/v1/agents/{id}/diagnostic-logs` | `AgentController` | Aligned |
| `POST /api/v1/admin/agent/{id}/decommission` | `AgentController` | Aligned |
| `POST /api/v1/admin/bootstrap-tokens` | `AgentController` | Aligned |
| `DELETE /api/v1/admin/bootstrap-tokens/{id}` | `AgentController` | Aligned |
| `GET /api/v1/audit/events` | `AuditController` | Aligned |
| `GET /api/v1/audit/events/{id}` | `AuditController` | Aligned |
| `GET /api/v1/dlq` | `DlqController` | Aligned |
| `GET /api/v1/dlq/{id}` | `DlqController` | Aligned |
| `POST /api/v1/dlq/{id}/retry` | `DlqController` | Aligned |
| `POST /api/v1/dlq/{id}/discard` | `DlqController` | Aligned |
| `POST /api/v1/dlq/retry-batch` | `DlqController` | Aligned |
| `POST /api/v1/dlq/discard-batch` | `DlqController` | Aligned |
| `GET /api/v1/master-data/legal-entities` | `MasterDataBrowserController` | Aligned |
| `GET /api/v1/master-data/products` | `MasterDataBrowserController` | Aligned |
| `GET /api/v1/master-data/sync-status` | `MasterDataBrowserController` | Aligned |
| `GET /api/v1/ops/transactions` | `OpsTransactionsController` | Aligned |
| `GET /api/v1/ops/transactions/{id}` | `OpsTransactionsController` | Aligned |
| `POST /api/v1/ops/transactions/acknowledge` | `OpsTransactionsController` | Aligned |
| `GET /api/v1/ops/reconciliation/exceptions` | `OpsReconciliationController` | Aligned |
| `GET /api/v1/ops/reconciliation/{id}` | `OpsReconciliationController` | Aligned |
| `POST /api/v1/ops/reconciliation/{id}/approve` | `OpsReconciliationController` | Aligned |
| `POST /api/v1/ops/reconciliation/{id}/reject` | `OpsReconciliationController` | Aligned |
| `GET /api/v1/sites` | `SitesController` | Aligned |
| `GET /api/v1/sites/{id}` | `SitesController` | Aligned |
| `PATCH /api/v1/sites/{id}` | `SitesController` | Aligned |
| `PUT /api/v1/sites/{id}/fcc-config` | `SitesController` | Aligned |
| `GET /api/v1/sites/{id}/pumps` | `SitesController` | Aligned |
| `POST /api/v1/sites/{id}/pumps` | `SitesController` | Aligned |
| `DELETE /api/v1/sites/{id}/pumps/{pumpId}` | `SitesController` | Aligned |
| `PATCH /api/v1/sites/{id}/pumps/{pumpId}/nozzles/{n}` | `SitesController` | Aligned |
| `GET /api/v1/admin/settings` | `AdminSettingsController` | Aligned |
| `PUT /api/v1/admin/settings/global-defaults` | `AdminSettingsController` | Aligned |
| `PUT /api/v1/admin/settings/overrides/{id}` | `AdminSettingsController` | Aligned |
| `DELETE /api/v1/admin/settings/overrides/{id}` | `AdminSettingsController` | Aligned |
| `PUT /api/v1/admin/settings/alerts` | `AdminSettingsController` | Aligned |
| `GET /api/v1/admin/dashboard/summary` | `AdminDashboardController` | Aligned |
| `GET /api/v1/admin/dashboard/alerts` | `AdminDashboardController` | Aligned |
| `POST /api/v1/portal/client-logs` | `PortalLogsController` | Aligned |

**Summary:** 42 frontend endpoints checked. **42 aligned, 0 missing.** All previously missing or mismatched endpoints have been resolved.

---

## Enum Cross-Reference Summary

| Enum | Aligned | Divergent Values | Severity | Status |
|---|---|---|---|---|
| `FccVendor` | Yes (`DOMS`, `RADIX`, `ADVATEC`, `PETRONITE`) | None | - | Aligned |
| `TransactionStatus` | Yes (`PENDING`, `SYNCED_TO_ODOO`, `DUPLICATE`, `ARCHIVED`) | None | - | Aligned |
| `ReconciliationStatus` | Yes (all 7 values match) | None | - | Aligned |
| `IngestionSource` | Yes (all 5 values match) | None | - | **RESOLVED** |
| `PreAuthStatus` | Yes (all 7 values match) | None | - | **RESOLVED** |
| `ConnectivityState` | Yes (all 4 values match) | None | - | **RESOLVED** |
| `EventType` | Yes (flexible `string` type) | None | - | **RESOLVED** |
| `DeadLetterStatus` | Yes (all 6 values match) | None | - | **RESOLVED** |
| `FiscalizationMode` | Yes (all 3 values match) | None | - | **RESOLVED** |
| `SiteOperatingModel` | Yes (all 4 values match) | None | - | **RESOLVED** |
| `ReconciliationDecision` | N/A (frontend only) | Used locally for UI | - | Aligned |

---

## Pagination Contract Alignment

| Aspect | Frontend (`PagedResult<T>`) | Backend (`PortalPagedResult<T>`) | Aligned? |
|---|---|---|---|
| Wrapper shape | `{ data: T[], meta: PageMeta }` | `{ Data: List<T>, Meta: PortalPageMeta }` | Yes (camelCase) |
| `meta.pageSize` | `number` | `int` | Yes |
| `meta.hasMore` | `boolean` | `bool` | Yes |
| `meta.nextCursor` | `string \| null` | `string?` | Yes |
| `meta.totalCount` | `number \| null` | `int?` | Yes |

Pagination contracts are fully aligned. The reconciliation exceptions endpoint uses its own `ReconciliationExceptionPageMeta` but with identical fields.

---

## Recommended Fix Priority

| Priority | Issue | Effort | Impact if Unfixed | Status |
|---|---|---|---|---|
| **P0** | C-01: Reconciliation `status` field name | Small (rename field) | Reconciliation list broken | **RESOLVED** |
| **P0** | C-02: Acknowledge endpoint path + auth | Medium (add endpoint) | Acknowledge feature broken | **RESOLVED** |
| **P0** | C-03: Dashboard `bySource` casing | Small (rename keys) | Volume chart shows zeros | **RESOLVED** |
| **P1** | C-04: Dashboard alerts endpoint | Medium (add endpoint) | Alert panel broken | **RESOLVED** |
| **P1** | C-05: Master data sync-status endpoint | Medium (add endpoint) | Sync monitoring broken | **RESOLVED** |
| **P1** | H-01-H-05: Enum value alignment | Medium (coordinate both sides) | Broken filters, wrong badges | **RESOLVED** |
| **P2** | M-01-M-03: Missing/extra fields | Small (add properties) | Incomplete data display | **RESOLVED** |
| **P2** | M-04-M-05: Enum divergences | Small (align enums) | Wrong labels | **RESOLVED** |
| **P2** | M-06: Reconciliation `decision` filter | Small (add query param) | Filter doesn't work | **RESOLVED** |
| **P3** | C-06: Client logging endpoint | Small (add endpoint) | Lose client error logs | **RESOLVED** |
| **P3** | L-01-L-06: Low issues | Trivial | Minimal | **RESOLVED** |
