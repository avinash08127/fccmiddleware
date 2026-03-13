# F-04 Reconciliation List Page — Audit Report

**Page:** `/reconciliation/exceptions` — `ReconciliationListComponent`
**Audited:** 2026-03-13
**Status:** Issues Found

---

## 1. File Inventory

| File | Purpose |
|------|---------|
| `src/portal/src/app/features/reconciliation/reconciliation-list.component.ts` | Main reconciliation exceptions list — tabbed view (Variance / Unmatched / Reviewed) with cursor pagination |
| `src/portal/src/app/features/reconciliation/reconciliation-filters.component.ts` | Filter bar — site dropdown + date range picker + clear button |
| `src/portal/src/app/features/reconciliation/reconciliation.routes.ts` | Lazy route definitions for `/exceptions` and `/exceptions/:id` |
| `src/portal/src/app/features/reconciliation/reconciliation.component.ts` | Placeholder stub — unused "coming soon" component |
| `src/portal/src/app/core/services/reconciliation.service.ts` | HTTP service — `getExceptions()`, `getById()`, `approve()`, `reject()` |
| `src/portal/src/app/core/models/reconciliation.model.ts` | TypeScript interfaces — `ReconciliationException`, `ReconciliationRecord`, `ReconciliationQueryParams` |
| `src/portal/src/app/core/models/transaction.model.ts` | Defines `ReconciliationStatus` enum used across reconciliation |
| `src/portal/src/app/shared/pipes/currency-minor-units.pipe.ts` | Formats minor-unit amounts using ISO 4217 decimals |
| `src/portal/src/app/shared/pipes/status-label.pipe.ts` | Maps SCREAMING_SNAKE_CASE status strings to human-readable labels |
| `src/portal/src/app/shared/pipes/utc-date.pipe.ts` | UTC date formatting pipe |

---

## 2. Routing & Auth

- Route defined in `reconciliation.routes.ts` line 8: `path: 'exceptions'`, lazy-loads `ReconciliationListComponent`.
- Guard: `roleGuard(['SystemAdmin', 'SystemAdministrator', 'OperationsManager', 'SiteSupervisor', 'Auditor'])`.
- Backend: `OpsReconciliationController` has `[Authorize(Policy = "PortalUser")]` at class level.
- Backend access scoping: `PortalAccessResolver.Resolve(User)` checks legal entity access. If `legalEntityId` is provided and user cannot access it, returns 403.

**Finding:** Route guard allows `SiteSupervisor` and `Auditor`, which is appropriate for a read-only list view. The approve/reject endpoints have the stricter `PortalReconciliationReview` policy. No auth issues.

---

## 3. UI Logic Review

### 3a. Component Structure

- Standalone component using Angular signals for state management.
- Three tabs: **Variance Flagged**, **Unmatched**, **Reviewed** — each with independent `TabState` (data, loading, totalRecords, cursor array, currentPage).
- Legal entity selector at top, site + date range filters below.
- Uses RxJS `Subject` + `switchMap` per tab to cancel in-flight requests on new loads.
- Cursor-based pagination backed by PrimeNG `p-table` lazy loading.

### 3b. Tab Loading Logic

- On legal entity change: resets all 3 tabs, triggers load for the active tab only.
- On tab change: loads only if the tab has no data and isn't loading (line 585).
- On filter change: resets all tabs, triggers active tab load.
- Each tab's `onLazyLoad` handler calculates the page index from `event.first / event.rows`, looks up the cursor for that page, and emits a `LoadRequest`.

### 3c. Variance / Percentage Formatting

- `formatVariance()` instantiates `new CurrencyMinorUnitsPipe()` on every call (line 651). This works but creates a new pipe instance per cell render.
- `formatVariancePct()` converts `varianceBps` (basis points) to percentage: `(bps / 100).toFixed(2)%`.
- `variancePctClass()` returns `variance-positive` only when status is `VARIANCE_FLAGGED`, otherwise `variance-zero` — this means a reviewed-but-approved record with high variance would show green styling, which may be intentional (approved = resolved).

### 3d. Row Click Navigation

- `onRowClick(ex)` navigates to `/reconciliation/exceptions/:id` — correct.
- Rows also have `tabindex="0"` and `(keydown.enter)` for keyboard accessibility — good.

---

## 4. Validation Review

### 4a. Filter Validation

- Site code filter is a dropdown from pre-loaded site list — no free-text injection risk.
- Date range comes from `DateRangePickerComponent` which provides `Date | null` objects — safe.
- No minimum/maximum date range enforcement on the frontend. Large date ranges could produce expensive backend queries.

### 4b. Pagination Validation

- `pageSize` is hard-coded to 20 (line 438) with `rowsPerPageOptions: [20, 50, 100]`.
- Backend validates `pageSize` is 1–100 (controller line 50). Frontend values are within range.
- Cursor is opaque string from backend — passed through as-is. Backend validates with `PortalCursor.TryDecode`.

---

## 5. API Call Review

### 5a. ReconciliationService.getExceptions()

- Calls `GET /api/v1/ops/reconciliation/exceptions` with query params.
- **Key mapping**: `reconciliationStatus` in the frontend `ReconciliationQueryParams` is remapped to `status` in the HTTP params (service line 20: `key === 'reconciliationStatus' ? 'status' : key`). This matches the backend's `[FromQuery] string? status` parameter.
- All params are serialized via `Object.entries()` with null/empty filtering.

### 5b. Response Mapping

- Backend returns `GetReconciliationExceptionsResponse` with `Data` and `Meta` (PascalCase in C#).
- Frontend expects `PagedResult<ReconciliationException>` with `data` and `meta` (camelCase).
- This works if the ASP.NET JSON serializer is configured for camelCase (standard). Verified by the `PagedResult<T>` interface matching `data: T[]` and `meta: PageMeta` with `pageSize`, `hasMore`, `nextCursor`, `totalCount`.

### 5c. Sites Loading

- `loadSitesForEntity()` calls `siteService.getSites({ legalEntityId, pageSize: 500 })`.
- Uses `takeUntilDestroyed(this.destroyRef)` for cleanup — correct.
- Hard-coded 500 page size. If a legal entity has 500+ sites, the dropdown would be incomplete with no indication to the user.

---

## 6. Backend Endpoint Trace

### 6a. GET /api/v1/ops/reconciliation/exceptions

**Controller:** `OpsReconciliationController.GetExceptions()` (line 39)
**Auth:** `[Authorize(Policy = "PortalUser")]` + `PortalAccessResolver` legal entity scoping.

**Query building:**
1. If `legalEntityId` provided: filters to that entity (after access check).
2. If no entity and user is scoped: filters to user's allowed entities.
3. If `status` provided: filters to that status. If not: defaults to `VARIANCE_FLAGGED || UNMATCHED`.
4. `from` or `since` parameter: `CreatedAt >= value`. `to` parameter: `CreatedAt <= value`.
5. Cursor-based pagination: `(CreatedAt > cursorTimestamp || (CreatedAt == cursorTimestamp && Id > cursorId))`.
6. Orders by `CreatedAt ASC, Id ASC`, takes `pageSize + 1` to detect `hasMore`.
7. Fetches related `PreAuthRecord` and `Transaction` by collecting IDs and batch-loading.
8. Returns `TotalCount` from a full `CountAsync()` — this runs a count query on every page load.

**Performance concern:** `totalCount` is computed via `CountAsync()` on every request (line 125). For large datasets with filters, this could be slow. The frontend uses it if available (`result.meta.totalCount`), falling back to an estimate.

### 6b. DTO field mapping (backend → frontend)

| Backend DTO field | Frontend model field | Match? |
|---|---|---|
| `Status` (string) | `reconciliationStatus` (enum) | Requires enum parse on frontend — works because TS enum values match backend strings |
| `MatchMethod` (required string) | `matchMethod` (string \| null) | Backend marks as `required` but frontend allows null — safe direction |
| `VarianceBps` (decimal?) | `varianceBps` (number \| null) | Backend computes as `VariancePercent * 100` rounded to 2dp |
| `RequestedAmount` / `ActualAmount` (long?) | same names (number \| null) | Match |
| `AmbiguityFlag` (bool) | `ambiguityFlag` (boolean) | Match |
| `LastMatchAttemptAt` (DateTimeOffset) | Not in frontend `ReconciliationException` model | Extra backend field — harmless |

---

## 7. Issues Found

### F04-01 — REVIEW_FUZZY_MATCH status not surfaced in any tab (Medium)

**Location:** `reconciliation-list.component.ts` lines 484, 512, 540
**Problem:** The backend `ReconciliationStatus` enum includes `REVIEW_FUZZY_MATCH`. The frontend enum also defines it (`transaction.model.ts` line 32). However, the list component only loads three categories:
- Variance tab → `VARIANCE_FLAGGED`
- Unmatched tab → `UNMATCHED`
- Reviewed tab → `APPROVED` or `REJECTED`

Records with status `REVIEW_FUZZY_MATCH` are invisible in the UI. The backend default filter (no status param) only returns `VARIANCE_FLAGGED || UNMATCHED` (controller line 102-104), so these records are also excluded from unfiltered API calls.
**Impact:** Fuzzy-match review items could accumulate without operator awareness.
**Fix:** Add a fourth tab or include `REVIEW_FUZZY_MATCH` alongside `VARIANCE_FLAGGED` in the variance tab.

### F04-02 — statusLabel pipe missing REVIEW_FUZZY_MATCH mapping (Low)

**Location:** `src/portal/src/app/shared/pipes/status-label.pipe.ts`
**Problem:** The `STATUS_LABELS` map has no entry for `REVIEW_FUZZY_MATCH`. The fallback (line 66) would produce "Review Fuzzy Match" via regex title-casing, which is acceptable but inconsistent with other hand-crafted labels.
**Impact:** Minor display inconsistency if `REVIEW_FUZZY_MATCH` records appear anywhere.
**Fix:** Add `REVIEW_FUZZY_MATCH: 'Fuzzy Match Review'` to the map.

### F04-03 — CurrencyMinorUnitsPipe instantiated per cell render (Low)

**Location:** `reconciliation-list.component.ts` line 651
**Problem:** `formatVariance()` creates `new CurrencyMinorUnitsPipe()` on each invocation. This is called for every visible row on each change detection cycle. The pipe is stateless so it works, but it's wasteful.
**Impact:** Unnecessary GC pressure. Negligible for 20–100 rows, but poor practice.
**Fix:** Store a single instance as a class field: `private readonly currencyPipe = new CurrencyMinorUnitsPipe();`

### F04-04 — Pagination: changing rowsPerPage does not reset cursor state (Medium)

**Location:** `reconciliation-list.component.ts` lines 590–638 (all `onXxxLazyLoad` handlers)
**Problem:** When the user changes `rowsPerPageOptions` (e.g., from 20 to 50), PrimeNG fires `onLazyLoad` with a new `rows` value. The component calculates the page from `event.first / event.rows` and looks up the cursor at that page index. However, the cursor array was populated with cursors based on the *old* page size. For example, cursor[1] was the cursor after 20 items, but after changing to 50 rows, page 0 (first=0) uses cursor[0]=null (correct), but page 1 (first=50) would try cursor[1] which was set for offset=20.
**Impact:** After changing page size mid-navigation, pagination may skip or repeat records.
**Fix:** When `event.rows` differs from the stored page size, reset the tab state (clear cursors) and reload from the beginning.

### F04-05 — Tab badge passes number to p-badge [value] which expects string (Low)

**Location:** `reconciliation-list.component.ts` lines 131, 137
**Problem:** `<p-badge [value]="varianceTab().totalRecords" ...>` — PrimeNG `p-badge` `[value]` input is typed as `string`. Passing a `number` works at runtime due to JavaScript coercion, but is technically a type mismatch.
**Impact:** No runtime bug. Could cause issues with strict template type checking or future PrimeNG versions.
**Fix:** Use `[value]="varianceTab().totalRecords.toString()"` or use string interpolation.

### F04-06 — No error feedback to user on API failure (Medium)

**Location:** `reconciliation-list.component.ts` lines 486–489, 514–516, 543–545
**Problem:** All three tab subscriptions use `catchError(() => { ... loading: false, data: [] ... return EMPTY; })`. On API error, the table silently shows empty state with "No variance flagged exceptions" / "No unmatched records" / "No reviewed records" — indistinguishable from genuinely empty data.
**Impact:** User has no way to know the data failed to load vs. there being no data. They may incorrectly believe there are no exceptions to review.
**Fix:** Add an `error` field to `TabState` and display an error message in the empty template when the error flag is set.

### F04-07 — totalRecords estimate can shrink, causing PrimeNG paginator confusion (Low)

**Location:** `reconciliation-list.component.ts` lines 498–503 (and same pattern at 526–531, 554–559)
**Problem:** When the backend reports `totalCount`, it's used directly. When it doesn't, the estimate is `(currentPage + 2) * pageSize` if `hasMore`, else `currentPage * pageSize + data.length`. This estimate can decrease when navigating to later pages where `hasMore=false`. PrimeNG's paginator may show/hide page buttons inconsistently as `totalRecords` fluctuates.
**Impact:** Minor UX confusion — paginator may flicker or show incorrect page count.
**Note:** Backend *does* always return `totalCount` (controller line 125 runs `CountAsync`), so this estimate code may never execute. However, if the backend ever omits `totalCount` (e.g., for performance), this would trigger.

### F04-08 — Hard-coded 500 site limit in loadSitesForEntity (Low)

**Location:** `reconciliation-list.component.ts` line 723
**Problem:** `getSites({ legalEntityId: entityId, pageSize: 500 })` — if a legal entity has more than 500 sites, the dropdown will be silently incomplete.
**Impact:** Users with 500+ sites under one legal entity won't see all sites in the filter dropdown.
**Fix:** Either paginate/scroll the site dropdown, or set a much higher limit with a safeguard.

---

## 8. Summary

| ID | Severity | Category | Description |
|----|----------|----------|-------------|
| F04-01 | Medium | Data Gap | `REVIEW_FUZZY_MATCH` records invisible in all tabs |
| F04-02 | Low | Display | statusLabel pipe missing `REVIEW_FUZZY_MATCH` entry |
| F04-03 | Low | Performance | CurrencyMinorUnitsPipe instantiated per cell render |
| F04-04 | Medium | Pagination | Changing rowsPerPage corrupts cursor-based pagination |
| F04-05 | Low | Type Safety | p-badge [value] receives number instead of string |
| F04-06 | Medium | UX | API errors silently show empty state — no error feedback |
| F04-07 | Low | UX | totalRecords estimate can fluctuate causing paginator confusion |
| F04-08 | Low | Data Gap | Hard-coded 500 site limit may truncate large site lists |

**Total: 8 issues (3 Medium, 5 Low)**
