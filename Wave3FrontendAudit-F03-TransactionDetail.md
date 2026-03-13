# F-03 Transaction Detail Page — Audit Report

**Page:** `/transactions/:id` — `TransactionDetailComponent`
**Audited:** 2026-03-13
**Status:** Issues Found

---

## 1. File Inventory

| File | Purpose |
|------|---------|
| `src/portal/src/app/features/transactions/transaction-detail.component.ts` | Main detail page — inline template & styles, shows tx fields + audit trail |
| `src/portal/src/app/core/services/transaction.service.ts` | HTTP service — `getTransactionById(id)` calls `GET /api/v1/ops/transactions/{id}` |
| `src/portal/src/app/core/services/audit.service.ts` | HTTP service — `getAuditEvents(params)` calls `GET /api/v1/audit/events` |
| `src/portal/src/app/core/models/transaction.model.ts` | TypeScript interfaces — `Transaction`, `TransactionDetail` (type alias), enums |
| `src/portal/src/app/core/models/audit.model.ts` | TypeScript interfaces — `AuditEvent`, `AuditEventQueryParams`, `EventType` enum |
| `src/portal/src/app/shared/pipes/currency-minor-units.pipe.ts` | Pipe — converts minor-unit long to display string using ISO 4217 rules |
| `src/portal/src/app/shared/pipes/utc-date.pipe.ts` | Pipe — formats UTC date strings |
| `src/portal/src/app/shared/pipes/status-label.pipe.ts` | Pipe — maps enum values to human-readable labels |
| `src/portal/src/app/shared/components/status-badge/status-badge.component.ts` | Shared badge component for status display |
| `src/cloud/FccMiddleware.Api/Controllers/OpsTransactionsController.cs` | Backend controller — `GetTransactionById(Guid id)` at line 210 |
| `src/cloud/FccMiddleware.Contracts/Portal/PortalTransactionContracts.cs` | Backend DTO — `PortalTransactionDto` |

---

## 2. Routing & Auth

- Route: Lazy-loaded child under `/transactions` feature module, path `:id`.
- Guard: `MsalGuard` on the parent route. No additional role guard on this specific route.
- Backend auth: `[Authorize(Policy = "PortalUser")]` on `OpsTransactionsController` (line 19).
- Legal entity scoping: `PortalAccessResolver` checks user claims. If user cannot access the transaction's `LegalEntityId`, returns `403 Forbid` (line 228-231).

**Finding:** Auth is correct. Route-level guard + backend policy + legal entity scoping all in place.

---

## 3. UI Logic Review

### 3a. Component State Management

- Uses Angular signals: `tx`, `loading`, `error`, `auditEvents`, `eventsLoading`.
- `ngOnInit()` calls `load()`.
- `load()` reads `id` from `route.snapshot.paramMap` — this is a **snapshot**, not a subscription to params observable.

### 3b. Template Rendering

- Three states: loading skeleton, error state with retry, data display.
- Detail header shows `fccTransactionId` with status badge.
- Conditional notice banners for duplicates (with link to original) and reconciliation status.
- Four field sections: Identifiers, Fuel Dispensing, Status & Ingestion, Timestamps.
- Audit event timeline in a side card using PrimeNG Timeline.
- Collapsible raw FCC payload panel with JSON pretty-printing.

### 3c. Navigation

- "Back to Transactions" button calls `goBack()` which navigates to `/transactions/list`.
- Duplicate notice links to `/transactions/{duplicateOfId}`.
- Reconciliation notice links to `/reconciliation` with `preAuthId` query param.

### 3d. Data Formatting

- `formatVolume(microlitres)`: divides by 1,000,000, formats to 3 decimal places. Correct.
- `formatJson(json)`: parse + stringify with indentation, catches errors. Correct.
- `CurrencyMinorUnitsPipe`: uses ISO 4217 lookup table for decimal places. Correct.

---

## 4. Validations Review

### 4a. Frontend Validations

- **ID validation:** Only checks `if (!id)` — validates presence but does not validate GUID format. This is acceptable since the backend uses `Guid` type binding which returns 400 on invalid format.
- **Error handling:** Distinguishes 404 from other errors. Shows user-friendly messages.
- **Null safety:** Extensive use of `@if` guards and optional chaining. Template uses `tx()!.` (non-null assertion) but only inside `@else if (tx())` block, so this is safe.

### 4b. Backend Validations

- `GetTransactionById(Guid id)` — framework validates GUID format from route constraint `{id:guid}`.
- Access check: fetches transaction first, then checks `CanAccess(transaction.LegalEntityId)`. This is correct (fetch-then-authorize pattern avoids leaking existence info via 403 vs 404... see issue below).

---

## 5. API Calls & Backend Endpoint Trace

### 5a. GET /api/v1/ops/transactions/{id}

- **Frontend call:** `TransactionService.getTransactionById(id)` — line 25-27 of `transaction.service.ts`.
- **Backend handler:** `OpsTransactionsController.GetTransactionById()` — line 210-266 of controller.
- **Auth:** `[Authorize(Policy = "PortalUser")]` on controller + `PortalAccessResolver` per-request.
- **Query:** `_db.Transactions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.Id == id)`.
- **Response:** Maps to `PortalTransactionDto` with all fields. `RawPayloadJson` is always set to `null`.

### 5b. GET /api/v1/audit/events

- **Frontend call:** `AuditService.getAuditEvents({legalEntityId, correlationId, pageSize: 50})` — line 561-576 of component.
- **Triggered after:** transaction loads successfully, uses `transaction.legalEntityId` and `transaction.correlationId`.
- **Backend handler:** `AuditController` (separate controller, endpoint B-26 in tracker).

---

## 6. Issues Found

### F03-01: RawPayloadJson is always null — Raw Payload panel never renders (MEDIUM)

**Location:** `OpsTransactionsController.cs` line 263
**Problem:** The backend always sets `RawPayloadJson = null` in the detail DTO mapping. The frontend has a conditional panel `@if (tx()!.rawPayloadJson)` (line 306) that displays the raw FCC payload, but this will **never** render because the backend never returns the data.
**Impact:** The "Raw FCC Payload" panel is dead UI. Users cannot inspect the original payload.
**Root cause:** The list endpoint intentionally omits `RawPayloadJson` for performance, but the detail endpoint copies the same projection and forgets to include it. The `RawPayloadRef` field (S3 key) IS returned, but no fetch from archive storage is performed.
**Fix:** In the detail endpoint, either populate `RawPayloadJson` from the transaction entity (if stored in DB) or fetch it from the `S3RawPayloadArchiver` using `RawPayloadRef`.

### F03-02: Route param read uses snapshot — won't react to in-app navigation between transactions (LOW)

**Location:** `transaction-detail.component.ts` line 511
**Problem:** `this.route.snapshot.paramMap.get('id')` reads the ID only once. If the user navigates from one transaction detail to another (e.g., clicking the duplicate link at line 105-107), Angular may reuse the component and the snapshot won't update.
**Impact:** Clicking the "See original" duplicate link (`[routerLink]="['/transactions', tx()!.duplicateOfId]"`) may not reload the component. The page would show stale data for the previous transaction.
**Fix:** Subscribe to `this.route.paramMap` observable instead of using `snapshot`, and reload data when the `id` param changes.

### F03-03: Audit events error is silently swallowed (LOW)

**Location:** `transaction-detail.component.ts` line 575
**Problem:** The `error` callback for audit events only calls `this.eventsLoading.set(false)`. No error state is shown to the user. The timeline section shows "No audit events found" which is misleading when the actual problem is a failed API call.
**Impact:** Users see "No audit events found" instead of an error message when the audit API fails. Confusing UX.
**Fix:** Add an `eventsError` signal and display it in the template when the audit events API call fails.

### F03-04: Information leakage via fetch-then-authorize pattern (LOW)

**Location:** `OpsTransactionsController.cs` lines 218-231
**Problem:** The endpoint first fetches the transaction, returns 404 if not found, then checks access and returns 403 if unauthorized. This means an attacker who knows a valid transaction ID but doesn't have access will get `403 Forbid`, while an invalid ID returns `404`. This difference reveals whether a transaction exists.
**Impact:** Low severity — requires authentication, and GUIDs are not guessable. But it's a minor information disclosure.
**Fix:** Return 404 for both cases (transaction not found OR not accessible) to avoid leaking existence.

### F03-05: No route param subscription for audit events reload (LOW)

**Location:** `transaction-detail.component.ts` line 561
**Problem:** Related to F03-02. If the route does get reloaded (e.g., via `onSameUrlNavigation: 'reload'`), the audit events are loaded via `loadAuditEvents(transaction)` inside the `next` callback. However, the `Retry` button on the error state (line 82) only calls `load()`, which will correctly re-trigger audit events. No issue here on retry — this is a consequence of F03-02.

### F03-06: IsStale field returned by backend but not displayed in UI (INFO)

**Location:** `PortalTransactionContracts.cs` line 34, `transaction.model.ts` — field missing
**Problem:** The backend DTO includes `IsStale` boolean, but the frontend `Transaction` interface does not include this field, and the detail template does not display it. The transaction list page uses `STALE_PENDING` status enum value, but the detail page doesn't show if a transaction has been marked stale.
**Impact:** Informational — users have no visibility into whether a transaction is stale from the detail page. The status badge may show `STALE_PENDING` if the status enum covers it, but the explicit `IsStale` flag is lost.

---

## 7. Summary

| ID | Severity | Title |
|----|----------|-------|
| F03-01 | MEDIUM | RawPayloadJson always null — raw payload panel is dead UI |
| F03-02 | LOW | Route param snapshot won't react to in-app navigation |
| F03-03 | LOW | Audit events API error silently swallowed |
| F03-04 | LOW | Information leakage via 403/404 distinction |
| F03-05 | LOW | Route param issue cascades to audit events (related to F03-02) |
| F03-06 | INFO | IsStale field not displayed in detail UI |

**Total issues: 6** (1 medium, 4 low, 1 info)
