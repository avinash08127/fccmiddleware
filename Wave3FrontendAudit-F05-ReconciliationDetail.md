# F-05 Reconciliation Detail Page — Audit Report

**Page:** `/reconciliation/exceptions/:id` — `ReconciliationDetailComponent`
**Audited:** 2026-03-13
**Status:** Issues Found

---

## 1. File Inventory

| File | Purpose |
|------|---------|
| `src/portal/src/app/features/reconciliation/reconciliation-detail.component.ts` | Detail view for a single reconciliation record — shows pre-auth, transaction, variance, and approve/reject actions |
| `src/portal/src/app/features/reconciliation/reconciliation.routes.ts` | Route definition: `exceptions/:id` lazy-loads `ReconciliationDetailComponent` |
| `src/portal/src/app/core/services/reconciliation.service.ts` | HTTP service — `getById()`, `approve()`, `reject()` |
| `src/portal/src/app/core/models/reconciliation.model.ts` | TypeScript interfaces — `ReconciliationRecord`, `ReconciliationPreAuthSummary`, `ReconciliationTransactionSummary` |
| `src/portal/src/app/core/models/transaction.model.ts` | Defines `ReconciliationStatus` enum |
| `src/portal/src/app/shared/directives/role-visible.directive.ts` | `*appRoleVisible` structural directive — controls visibility of approve/reject actions |
| `src/portal/src/app/shared/pipes/currency-minor-units.pipe.ts` | Formats minor-unit amounts using ISO 4217 decimals |
| `src/portal/src/app/shared/pipes/status-label.pipe.ts` | Maps SCREAMING_SNAKE_CASE status strings to human-readable labels |
| `src/portal/src/app/shared/pipes/utc-date.pipe.ts` | UTC date formatting pipe |
| `src/cloud/FccMiddleware.Api/Controllers/OpsReconciliationController.cs` | Backend controller — `GetById`, `Approve`, `Reject` endpoints |
| `src/cloud/FccMiddleware.Contracts/Reconciliation/ReviewReconciliationRequest.cs` | Backend request contract: `{ Reason: string? }` |

---

## 2. Routing & Auth

- Route defined in `reconciliation.routes.ts` line 13: `path: 'exceptions/:id'`, lazy-loads `ReconciliationDetailComponent`.
- Guard: `roleGuard(['SystemAdmin', 'SystemAdministrator', 'OperationsManager', 'SiteSupervisor', 'Auditor'])`.
- Backend `GetById` endpoint: `[Authorize(Policy = "PortalUser")]` at class level + legal entity access check via `PortalAccessResolver`.
- Backend `Approve`/`Reject` endpoints: `[Authorize(Policy = "PortalReconciliationReview")]` — requires roles `OperationsManager`, `SystemAdmin`, or `SystemAdministrator`.
- Frontend approve/reject buttons: `*appRoleVisible="['SystemAdmin', 'OperationsManager']"` — hides buttons for `SiteSupervisor`, `Auditor`, and `SystemAdministrator`.

**Finding (see F05-01):** Frontend `appRoleVisible` omits `SystemAdministrator` — users with this role can see the page but not the approve/reject buttons, even though the backend allows them.

---

## 3. UI Logic Review

### 3a. Component Structure

- Standalone component using Angular signals for state management.
- On init: reads `:id` from route snapshot, calls `loadRecord(id)`. If no id, redirects to list.
- Renders three cards: Pre-Auth Details, Transaction Details, Variance Breakdown.
- Conditional "Review Action" card shown only when `reconciliationStatus === VARIANCE_FLAGGED` and user has `SystemAdmin` or `OperationsManager` role.
- Approve/reject opens a confirmation dialog with mandatory reason text (min 10 characters).

### 3b. Data Loading

- `loadRecord()` calls `reconService.getById(id)`, sets `record` signal on success, shows toast on error (line 643–651).
- Uses `takeUntilDestroyed(this.destroyRef)` for cleanup — correct.
- Route param is read from snapshot only (`this.route.snapshot.paramMap.get('id')`). This means if the route parameter changes without destroying/recreating the component (e.g., via `routerLink` to another detail), the component would NOT reload. However, since Angular lazy-loaded routes with different `:id` params typically destroy and recreate the component, this is acceptable in most scenarios.

### 3c. Approve/Reject Flow

1. User clicks "Approve Variance" or "Reject" → `openDialog(action)` sets `pendingAction` and clears `reason`.
2. Dialog shows reason textarea with `[disabled]="reason().length < 10 || submitting()"`.
3. `submitAction()` double-checks `reason().length < 10` guard, calls `reconService.approve()` or `.reject()`.
4. Service calls `POST /api/v1/ops/reconciliation/{id}/approve` (or `/reject`) with `{ reason }`, then chains `getById(id)` to refresh the record.
5. On success: updates `record` signal, closes dialog, shows success toast.
6. On error: keeps dialog open, shows error toast "Could not complete the action."

### 3d. Variance Formatting

- `formatVariance()` creates `new CurrencyMinorUnitsPipe()` on every call (same issue as F04-03).
- `formatVariancePct()` converts basis points to percentage: `(bps / 100).toFixed(2)%`.
- `varianceClass()` returns `variance-positive` (red) for positive variance, `variance-negative` (orange) for negative, `variance-zero` (green) for zero, `variance-null` (muted) for null.
- `formatVolume()` converts microlitres to litres: `(microlitres / 1_000_000).toFixed(3)`.

### 3e. Match Method Labels

- `matchMethodLabel()` maps known match methods to readable labels: `EXACT_CORRELATION_ID`, `PUMP_NOZZLE_TIME_WINDOW`, `ODOO_ORDER_ID`.
- Unknown methods fall back to `method.replace(/_/g, ' ')` — reasonable.

---

## 4. Validation Review

### 4a. Route Parameter

- `id` is read from `this.route.snapshot.paramMap.get('id')`. No validation of format (e.g., GUID pattern check).
- Backend endpoint uses `{id:guid}` route constraint (controller line 217), so non-GUID ids would return 404 — safe.

### 4b. Reason Text

- Frontend: disabled button when `reason().length < 10` (dialog line 350). `submitAction()` also guards with `this.reason().length < 10` (line 556).
- Backend: validates `string.IsNullOrWhiteSpace(request.Reason)` (controller line 294) — only checks for empty/null, NOT for minimum length.
- **Mismatch**: Frontend requires 10+ characters, backend only requires non-empty. This is safe (frontend is stricter), but if reason validation is a business requirement, backend should enforce it too.

### 4c. Reason Text — No Maximum Length

- Frontend textarea has no `maxlength` attribute. A user could submit extremely long reason text.
- Backend `ReviewReconciliationRequest.Reason` is `string?` with no `[MaxLength]` annotation.
- Database column length limit (if any) would be the only safety net.

### 4d. Status Guard for Review Actions

- Frontend: the review card only appears when `reconciliationStatus === ReconciliationStatus.VARIANCE_FLAGGED` (line 275).
- Records with `UNMATCHED` or `REVIEW_FUZZY_MATCH` status cannot be approved/rejected from the UI.
- Backend handler (`ReviewReconciliationCommand`) likely validates valid status transitions. The `CONFLICT.INVALID_TRANSITION` error code (controller line 331) confirms this.
- **Finding (see F05-02):** `REVIEW_FUZZY_MATCH` records cannot be reviewed from this detail page because the action card is gated on `VARIANCE_FLAGGED` only.

---

## 5. API Call Review

### 5a. ReconciliationService.getById()

- Calls `GET /api/v1/ops/reconciliation/{id}` — simple GET, no query params.
- Returns `ReconciliationRecord` — the full detail model with embedded `preAuthSummary` and `transactionSummary`.

### 5b. ReconciliationService.approve() / reject()

- Calls `POST /api/v1/ops/reconciliation/{id}/approve` (or `/reject`) with body `{ reason }`.
- Backend `ReviewReconciliationRequest` expects `{ Reason: string? }`.
- Frontend sends lowercase `reason`, ASP.NET camelCase deserialization maps it to `Reason` — correct.
- **After the POST**, the service chains `.pipe(switchMap(() => this.getById(id)))` to fetch the updated record.
- **Finding (see F05-03):** This means two HTTP requests per review action (POST + GET). The POST endpoint returns a `ReviewReconciliationResponse` with fields `ReconciliationId`, `Status`, `ReviewedByUserId`, `ReviewedAtUtc`, `ReviewReason` — this response is discarded and replaced by a full `getById()` call. This works but is wasteful.

### 5c. Error Handling for Review Actions

- The `approve()`/`reject()` service methods return `Observable<ReconciliationRecord>` — the chained `getById()`.
- If the POST succeeds but the subsequent GET fails, the error handler fires and shows "Could not complete the action" — misleading because the action actually succeeded.
- If the POST fails (e.g., 409 Conflict for already-reviewed records), the error toast shows a generic message without details from the backend error response.

### 5d. Backend Response DTO Field Mapping

| Backend DTO (ReconciliationRecordDto) | Frontend model (ReconciliationRecord) | Match? |
|---|---|---|
| `Id` (Guid) | `id` (string) | Match (JSON serializes Guid as string) |
| `PreAuthId` (Guid?) | `preAuthId` (string \| null) | Match |
| `TransactionId` (Guid) | `transactionId` (string \| null) | Backend is non-nullable, frontend allows null — safe |
| `SiteCode` (string) | `siteCode` (string) | Match |
| `LegalEntityId` (Guid) | `legalEntityId` (string) | Match |
| `OdooOrderId` (string?) | `odooOrderId` (string \| null) | Match |
| `PumpNumber` (int) | `pumpNumber` (number) | Match |
| `NozzleNumber` (int) | `nozzleNumber` (number) | Match |
| `ProductCode` (string?) | `productCode` (string \| null) | Match |
| `CurrencyCode` (string?) | `currencyCode` (string \| null) | Match |
| `RequestedAmount` (long?) | `requestedAmount` (number \| null) | Match |
| `ActualAmount` (long?) | `actualAmount` (number \| null) | Match |
| `AmountVariance` (long?) | `amountVariance` (number \| null) | Match |
| `VarianceBps` (decimal?) | `varianceBps` (number \| null) | Match |
| `MatchMethod` (string?) | `matchMethod` (string \| null) | Match |
| `AmbiguityFlag` (bool) | `ambiguityFlag` (boolean) | Match |
| `PreAuthStatus` (string?) | `preAuthStatus` (PreAuthStatus \| null) | Backend returns status `.ToString()`, frontend enum values match — correct |
| `ReconciliationStatus` (string) | `reconciliationStatus` (ReconciliationStatus \| null) | Backend returns `.ToString()`, frontend allows null but backend always returns — safe |
| `Decision` (string?) | `decision` (ReconciliationDecision \| null) | Match |
| `DecisionReason` (string?) | `decisionReason` (string \| null) | Match |
| `DecidedBy` (string?) | `decidedBy` (string \| null) | Match |
| `DecidedAt` (DateTimeOffset?) | `decidedAt` (string \| null) | Match (JSON serialization) |
| `PreAuthSummary` (dto?) | `preAuthSummary` (interface \| null) | Match |
| `TransactionSummary` (dto?) | `transactionSummary` (interface \| null) | Match |

All fields match correctly.

---

## 6. Backend Endpoint Trace

### 6a. GET /api/v1/ops/reconciliation/{id:guid}

**Controller:** `OpsReconciliationController.GetById()` (line 220)
**Auth:** `[Authorize(Policy = "PortalUser")]` + `PortalAccessResolver` legal entity scoping.

**Flow:**
1. Resolves portal access. Returns 401 if invalid.
2. Loads `ReconciliationRecord` by ID. Returns 404 if not found.
3. Checks `access.CanAccess(record.LegalEntityId)` — returns 403 if user's legal entity scope doesn't include this record.
4. Loads related `PreAuthRecord` (if `PreAuthId` is set) and `Transaction` (always, by `TransactionId`).
5. Maps to `ReconciliationRecordDto` via `MapRecord()` — includes embedded summaries.

**Note:** Transaction lookup uses `record.TransactionId` directly without null check (line 252). If `TransactionId` is `Guid.Empty` (for unmatched records), the query runs but returns null — harmless but wasteful DB round-trip.

### 6b. POST /api/v1/ops/reconciliation/{id:guid}/approve

**Controller:** `OpsReconciliationController.Approve()` (line 268)
**Auth:** `[Authorize(Policy = "PortalReconciliationReview")]` — requires `OperationsManager`, `SystemAdmin`, or `SystemAdministrator`.

**Flow:**
1. Validates `request.Reason` is not empty/whitespace.
2. Resolves portal access and user ID.
3. Sends `ReviewReconciliationCommand` via MediatR with `TargetStatus = APPROVED`.
4. Returns error responses based on result error codes: validation errors → 400, not found → 404, forbidden → 403, conflict → 409.
5. On success, returns `ReviewReconciliationResponse` (not the full record DTO).

### 6c. POST /api/v1/ops/reconciliation/{id:guid}/reject

Same as approve, with `TargetStatus = REJECTED`.

---

## 7. Issues Found

### F05-01 — appRoleVisible omits SystemAdministrator role for approve/reject buttons (Medium)

**Location:** `reconciliation-detail.component.ts` line 276
**Problem:** The `*appRoleVisible` directive is set to `['SystemAdmin', 'OperationsManager']`, but the backend `PortalReconciliationReview` policy also accepts `SystemAdministrator` (Program.cs line 212–214). Users with the `SystemAdministrator` role can access the detail page (route guard includes it) and would be authorized by the backend, but the approve/reject buttons are hidden.
**Impact:** `SystemAdministrator` users cannot perform reconciliation reviews from the UI, even though the backend authorizes them.
**Fix:** Add `'SystemAdministrator'` to the `appRoleVisible` array: `*appRoleVisible="['SystemAdmin', 'SystemAdministrator', 'OperationsManager']"`.

### F05-02 — REVIEW_FUZZY_MATCH records cannot be reviewed from detail page (Medium)

**Location:** `reconciliation-detail.component.ts` line 275
**Problem:** The review action card is conditionally shown only when `reconciliationStatus === ReconciliationStatus.VARIANCE_FLAGGED`. Records with `REVIEW_FUZZY_MATCH` status also require operator review, but the action card is not displayed for them. Combined with F04-01 (these records don't appear in any list tab), fuzzy-match records are completely unreviewable from the portal.
**Impact:** Operators cannot approve or reject fuzzy-match reconciliation records, even if they navigate to the detail URL directly.
**Fix:** Expand the condition to also include `REVIEW_FUZZY_MATCH`: `record()!.reconciliationStatus === ReconciliationStatus.VARIANCE_FLAGGED || record()!.reconciliationStatus === ReconciliationStatus.REVIEW_FUZZY_MATCH`.

### F05-03 — Approve/reject makes redundant GET after POST (Low)

**Location:** `src/portal/src/app/core/services/reconciliation.service.ts` lines 33–37, 40–44
**Problem:** `approve()` and `reject()` call `POST .../approve` and then chain `switchMap(() => this.getById(id))`. The POST already returns a `ReviewReconciliationResponse` with the updated status, reviewer, and reason. This response is discarded and a second HTTP round-trip is made to get the full record.
**Impact:** Unnecessary network request on every review action. The extra GET also creates a race condition window: if the record is modified between the POST and GET (unlikely but possible), the UI could show stale data.
**Fix:** Either use the POST response to partially update the local record state, or accept the double-fetch as acceptable for correctness (getting the full record with embedded summaries).

### F05-04 — CurrencyMinorUnitsPipe instantiated per call in formatVariance() (Low)

**Location:** `reconciliation-detail.component.ts` line 597
**Problem:** `formatVariance()` creates `new CurrencyMinorUnitsPipe()` on each invocation. Same issue as F04-03 in the list component.
**Impact:** Unnecessary object creation per change detection cycle. For a single detail page, this is negligible.
**Fix:** Store a single instance as a class field: `private readonly currencyPipe = new CurrencyMinorUnitsPipe();`

### F05-05 — Generic error message hides actionable backend error details (Medium)

**Location:** `reconciliation-detail.component.ts` lines 577–585
**Problem:** When the approve/reject POST fails, the error handler shows a generic toast: "Could not complete the action. Please try again." The backend returns specific error codes and messages:
- `CONFLICT.INVALID_TRANSITION` — record already reviewed (409)
- `FORBIDDEN.LEGAL_ENTITY_SCOPE` — access denied (403)
- `NOT_FOUND.RECONCILIATION` — record deleted (404)
- `VALIDATION.REASON_REQUIRED` — empty reason (400)

None of these details reach the user. For example, if another operator already approved the record (409 Conflict), the user sees "Could not complete the action" with no indication that it's already been resolved.
**Impact:** Users cannot distinguish transient errors from permanent ones, leading to futile retries or confusion.
**Fix:** Extract the error response body and display the backend message, or at minimum differentiate 409 Conflict with "This record has already been reviewed."

### F05-06 — Approve/reject service sends chained request; if POST succeeds but GET fails, error handler misleads (Low)

**Location:** `reconciliation.service.ts` lines 33–37 and `reconciliation-detail.component.ts` lines 577–585
**Problem:** The `approve()`/`reject()` service method chains `POST` → `switchMap(getById)`. If the POST succeeds but the subsequent GET fails (e.g., transient network error), the component's error handler fires and shows "Could not complete the action" — the user believes the approval failed and may retry, but it already succeeded on the backend.
**Impact:** Potential for duplicate review actions if user retries after a false error (though backend 409 Conflict would prevent actual data corruption).
**Fix:** Separate the POST result handling from the GET refresh. Show success toast immediately after POST succeeds, then attempt the refresh silently.

### F05-07 — No reason max-length enforcement on frontend or backend (Low)

**Location:** `reconciliation-detail.component.ts` line 325 (textarea), `ReviewReconciliationRequest.cs` line 5
**Problem:** The reason textarea has no `maxlength` attribute. The backend `ReviewReconciliationRequest.Reason` has no `[MaxLength]` or `[StringLength]` annotation. A user could submit an extremely long reason string.
**Impact:** Potential for oversized payloads or database column overflow (depending on DB column definition). Unlikely to be exploited but is a missing boundary.
**Fix:** Add `maxlength="2000"` to the textarea and `[MaxLength(2000)]` to the backend request.

---

## 8. Summary

| ID | Severity | Category | Description |
|----|----------|----------|-------------|
| F05-01 | Medium | Auth | `appRoleVisible` omits `SystemAdministrator` — backend allows it but UI hides buttons |
| F05-02 | Medium | Data Gap | `REVIEW_FUZZY_MATCH` records cannot be reviewed from detail page |
| F05-03 | Low | Performance | Redundant GET after successful POST on approve/reject |
| F05-04 | Low | Performance | CurrencyMinorUnitsPipe instantiated per call |
| F05-05 | Medium | UX | Generic error message hides actionable backend error codes (409, 403, 404) |
| F05-06 | Low | UX | Chained POST→GET: if POST succeeds but GET fails, user sees false error |
| F05-07 | Low | Validation | No max-length on reason text — frontend or backend |

**Total: 7 issues (3 Medium, 4 Low)**
