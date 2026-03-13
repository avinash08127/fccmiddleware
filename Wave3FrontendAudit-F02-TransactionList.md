# F-02 Transaction List Page — Audit Report

**Page:** `/transactions/list` — `TransactionListComponent`
**Audited:** 2026-03-13
**Status:** Issues Found

---

## 1. File Inventory

| File | Purpose |
|------|---------|
| `src/portal/src/app/features/transactions/transaction-list.component.ts` | Main page — legal entity selector, filters, paginated table |
| `src/portal/src/app/features/transactions/transaction-filters.component.ts` | Filter panel — fccTransactionId, odooOrderId, site, status, vendor, source, pump, date range, stale toggle |
| `src/portal/src/app/core/services/transaction.service.ts` | HTTP service — `getTransactions()`, `getTransactionById()`, `acknowledgeTransactions()` |
| `src/portal/src/app/core/models/transaction.model.ts` | TypeScript enums + interfaces for Transaction, TransactionQueryParams, etc. |
| `src/portal/src/app/features/transactions/transactions.routes.ts` | Lazy route definitions — `list` and `:id` |
| `src/portal/src/app/shared/pipes/currency-minor-units.pipe.ts` | Formats minor-unit amounts with ISO 4217 decimal resolution |
| `src/portal/src/app/shared/pipes/status-label.pipe.ts` | Maps SCREAMING_SNAKE_CASE enums to readable labels |
| `src/portal/src/app/shared/pipes/utc-date.pipe.ts` | UTC date formatting pipe |

---

## 2. Routing & Auth

- Route: `transactions.routes.ts` — `path: 'list'`, lazy-loaded via `loadComponent`.
- Parent route in `app.routes.ts` loads `TRANSACTION_ROUTES` as children of `transactions` path.
- Guard: `MsalGuard` on the parent shell route. No additional role guard on this route.
- Backend auth: `[Authorize(Policy = "PortalUser")]` on `OpsTransactionsController`.
- Legal entity scoping: `PortalAccessResolver.Resolve(User)` + `access.CanAccess(legalEntityId)` enforced per request.

**No auth issues found.** Route is protected by MsalGuard, backend enforces PortalUser policy + legal entity scoping.

---

## 3. UI Logic Review

### 3a. TransactionListComponent

- Uses Angular signals for state management (`transactions`, `totalRecords`, `loading`, `tableFirst`, `selectedLegalEntityId`, `siteOptions`, `legalEntityOptions`).
- On construction: loads legal entities via `MasterDataService.getLegalEntities()`, sets up `load$` Subject with `switchMap` (properly cancels in-flight requests on re-emission).
- Legal entity selector is required before table appears — good UX guard.
- `onLegalEntityChange()` loads sites for the selected entity (pageSize: 500), resets pagination state, and lets PrimeNG's `onLazyLoad` fire automatically.
- `onFiltersChange()` resets pagination and triggers a new load.
- `onLazyLoad()` handles pagination and sorting via PrimeNG's `TableLazyLoadEvent`.
- Cursor-based pagination with a cursor stack: `cursors[page]` stores the cursor for each page. This correctly handles forward navigation but has implications for backward navigation (see issues).
- `formatVolume()` divides microlitres by 1,000,000 and shows 3 decimal places with "L" suffix.
- `onRowClick()` navigates to `/transactions/{tx.id}` for detail view.

### 3b. TransactionFiltersComponent

- Uses mutable `filters` object with two-way binding (`[(ngModel)]`).
- Emits a shallow copy (`{ ...this.filters }`) on every change via `filtersChange` EventEmitter.
- Filter fields: fccTransactionId (text), odooOrderId (text), siteCode (select), status (select), fccVendor (select), ingestionSource (select), pumpNumber (number), dateRange (date-range-picker), isStale (toggle).
- `clear()` resets to `EMPTY_FILTERS` and emits.
- `onDateRangeSelected()` sets date range and emits.
- All select options use hardcoded enum values matching the backend enums.

### 3c. TransactionService

- `getTransactions()` builds `HttpParams` from all non-null, non-empty entries in `TransactionQueryParams`, calls `GET /api/v1/ops/transactions`.
- `getTransactionById()` calls `GET /api/v1/ops/transactions/{id}`.
- `acknowledgeTransactions()` calls `POST /api/v1/ops/transactions/acknowledge`.

### 3d. Shared Pipes

- `CurrencyMinorUnitsPipe`: Correctly resolves ISO 4217 decimal places. Handles null/undefined input gracefully.
- `StatusLabelPipe`: Maps enum values to display labels. Falls back to title-cased snake_case conversion for unmapped values.

---

## 4. Validations Review

### Frontend Validations

- **Legal entity selector:** Must select from server-provided list. No free-text input.
- **Filter inputs:** No explicit validation on text fields (fccTransactionId, odooOrderId). These are passed as-is to the backend.
- **Pump number:** Uses `p-inputnumber` which ensures numeric input only.
- **Page size:** Hardcoded default of 20, with `rowsPerPageOptions: [20, 50, 100]` — all within backend's 1-100 range.
- **No sanitization on text filter inputs** — however, these are sent as query parameters via Angular's HttpParams (which URL-encodes them), and the backend uses parameterized EF Core LINQ queries (`EF.Functions.ILike`) which are SQL-injection safe.

### Backend Validations (`OpsTransactionsController.GetTransactions`)

- `pageSize` validated: 1-100 range, returns 400 if invalid.
- `legalEntityId` is `Guid` — ASP.NET model binding rejects invalid GUIDs automatically.
- `status`, `fccVendor`, `ingestionSource` are parsed via `TryParseEnum<T>()` — returns 400 with descriptive error on invalid values.
- `PortalAccessResolver` enforces legal entity access scoping.
- `cursor` is base64-decoded with error handling — falls back to offset 0 on invalid input.
- Text search fields (`fccTransactionId`, `odooOrderId`) use `EF.Functions.ILike` with parameterized queries — **SQL injection safe** but see issue BUG-F02-3 about wildcard injection.

**Overall:** Validation coverage is good. One concern noted below.

---

## 5. API Calls

| # | Frontend Call | Backend Endpoint | Method | Auth |
|---|-------------|------------------|--------|------|
| 1 | `TransactionService.getTransactions()` | `GET /api/v1/ops/transactions` | GET | PortalUser |
| 2 | `MasterDataService.getLegalEntities()` | `GET /api/v1/master-data/legal-entities` | GET | PortalUser |
| 3 | `SiteService.getSites()` | `GET /api/v1/sites?legalEntityId={id}&pageSize=500` | GET | PortalUser |
| 4 | (on row click) `Router.navigate` | Client-side navigation to `/transactions/:id` | — | — |

---

## 6. Backend Endpoint Trace

### GET /api/v1/ops/transactions

**Controller:** `OpsTransactionsController.GetTransactions()` (`OpsTransactionsController.cs:36-205`)

**Flow:**
1. Validates `pageSize` (1-100).
2. Resolves portal access from JWT claims via `PortalAccessResolver`.
3. Checks `access.CanAccess(legalEntityId)` — returns 403 if denied.
4. Parses optional enum filters (`status`, `fccVendor`, `ingestionSource`) — returns 400 on invalid.
5. Decodes cursor (base64-encoded offset integer).
6. Builds EF Core query with `IgnoreQueryFilters().AsNoTracking()` against `_db.Transactions`.
7. Applies WHERE clauses for each filter (siteCode, status, date range on `CompletedAt`, productCode, fccTransactionId via ILike, odooOrderId via ILike, vendor, ingestionSource, pumpNumber, isStale).
8. Counts total records via `CountAsync()`.
9. Applies sort ordering via `ApplyOrdering()` — supports fccTransactionId, siteCode, volumeMicrolitres, amountMinorUnits, status, startedAt. Default: CompletedAt descending.
10. Fetches `pageSize + 1` rows (Take(pageSize+1)) to detect `hasMore`.
11. Projects to `PortalTransactionDto` — sets `RawPayloadJson = null` (good: no raw payload in list view).
12. Returns `PortalPagedResult<PortalTransactionDto>` with cursor pagination metadata.

**Notable:** Uses offset-based pagination encoded as base64 "cursor" — this is technically offset pagination disguised as cursor pagination. This has implications for data consistency (see issues).

---

## 7. Issues Found

### BUG-F02-1: Offset-based pagination masquerading as cursor pagination causes data drift (Severity: MEDIUM)

**File:** `OpsTransactionsController.cs:84,147-152,394-421`

The backend uses `Skip(offset).Take(pageSize+1)` with a base64-encoded offset as the "cursor." This is offset pagination, not true cursor pagination. When new transactions are ingested while a user is browsing:
- Rows shift positions, causing duplicates on the next page or skipped rows.
- The frontend's cursor stack (`cursors[]` array) stores these offset-based cursors, compounding the drift across pages.

The `totalCount` is computed fresh each request, so the paginator's total may jump as new data arrives, causing jarring UX.

**Impact:** Users browsing transactions during active ingestion may see duplicate rows across pages or miss rows entirely. For a transaction browser this is confusing but not data-loss.

**Recommendation:** Use keyset (seek) pagination on `(CompletedAt, Id)` for stable page boundaries regardless of inserts. Alternatively, document the limitation and accept the trade-off for simplicity.

---

### BUG-F02-2: COUNT(*) on every page request is expensive at scale (Severity: MEDIUM)

**File:** `OpsTransactionsController.cs:147`

```csharp
var totalCount = await query.CountAsync(cancellationToken);
```

Every pagination request executes a full `COUNT(*)` against the filtered transaction set. For large datasets (millions of transactions), this is a sequential scan even with indexes, because the WHERE clause varies per request.

The frontend uses `totalRecords` mainly for the paginator's page count display. The PrimeNG paginator already works with `hasMore` alone.

**Impact:** Slow page loads on large datasets. The count query may take seconds on millions of rows with complex filters.

**Recommendation:** Remove `totalCount` from the paginated query and use only `hasMore`. If an approximate count is needed, use PostgreSQL's `reltuples` estimate or cache the count with a TTL.

---

### BUG-F02-3: ILike wildcard characters in fccTransactionId/odooOrderId filters are not escaped (Severity: LOW)

**File:** `OpsTransactionsController.cs:119,124`

```csharp
query = query.Where(item => EF.Functions.ILike(item.FccTransactionId, $"%{fccTransactionId.Trim()}%"));
query = query.Where(item => item.OdooOrderId != null && EF.Functions.ILike(item.OdooOrderId, $"%{odooOrderId.Trim()}%"));
```

User-provided filter text is interpolated directly into the LIKE pattern without escaping PostgreSQL wildcard characters (`%`, `_`). A user typing `%` or `_` in the search field would match unintended rows:
- Searching for `test_123` would match `testX123`, `testY123`, etc. because `_` is a single-character wildcard.
- Searching for `%` would match everything.

This is **not a SQL injection vulnerability** (parameterized queries prevent that), but it produces unexpected search results.

**Recommendation:** Escape `%`, `_`, and `\` in the filter values before wrapping with `%...%`. PostgreSQL's `ILIKE` supports `ESCAPE` clauses, or manually replace: `value.Replace("%", "\\%").Replace("_", "\\_")`.

---

### BUG-F02-4: Date range filter uses CompletedAt but column header says "Started At" (Severity: LOW)

**File:**
- Frontend: `transaction-filters.component.ts:141` — label says "Started At (range)"
- Backend: `OpsTransactionsController.cs:104-109` — filters on `item.CompletedAt`

```csharp
if (from.HasValue) query = query.Where(item => item.CompletedAt >= from.Value);
if (to.HasValue)   query = query.Where(item => item.CompletedAt <= to.Value);
```

The filter label says "Started At (range)" but the backend filters on `CompletedAt`. The table column also says "Started At" and shows `tx.startedAt`. Users filtering by date range expect to filter on the same field they see in the table, but they're actually filtering on a different timestamp.

**Impact:** Users may get unexpected results — transactions whose `startedAt` is within the selected range but `completedAt` is outside it would be excluded (and vice versa).

**Recommendation:** Either change the backend to filter on `StartedAt` (matching the UI label), or change the frontend label to "Completed At (range)". Filtering on `StartedAt` is probably the better choice since it matches the visible sort column.

---

### BUG-F02-5: Frontend sends `sortField` values not recognized by backend — silently falls through to default (Severity: LOW)

**File:**
- Frontend: `transaction-list.component.ts:127-144` — sortable columns include `fccTransactionId`, `siteCode`, `volumeMicrolitres`, `amountMinorUnits`, `status`, `startedAt`
- Backend: `OpsTransactionsController.cs:367-381` — `ApplyOrdering` switch

The frontend table allows sorting by `startedAt`, but the backend's `ApplyOrdering` switch does not have a case for `"startedAt"` — it maps to the `_` default which sorts by `CompletedAt`. This means clicking "Started At" to sort doesn't actually sort by `StartedAt`.

```csharp
return field switch
{
    "fccTransactionId" => ...,
    "siteCode" => ...,
    "volumeMicrolitres" => ...,
    "amountMinorUnits" => ...,
    "status" => ...,
    "startedAt" => ThenById(OrderBy(query, item => item.StartedAt, descending), descending),
    _ => ThenById(OrderBy(query, item => item.CompletedAt, descending), descending)
};
```

Wait — actually re-reading the backend, `"startedAt"` IS listed as a case. Let me re-verify...

Yes, `"startedAt"` is handled at line 378. This is NOT a bug. Withdrawn.

---

### BUG-F02-5 (revised): loadSitesForEntity leaks subscriptions on rapid entity switches (Severity: LOW)

**File:** `transaction-list.component.ts:406-418`

```typescript
private loadSitesForEntity(entityId: string): void {
    this.siteService
      .getSites({ legalEntityId: entityId, pageSize: 500 })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => { this.siteOptions.set(...); },
        error: () => this.siteOptions.set([]),
      });
  }
```

Each call to `onLegalEntityChange()` calls `loadSitesForEntity()` which creates a new subscription. The `takeUntilDestroyed` only cancels on component destruction, not on subsequent entity changes. If a user rapidly switches legal entities, multiple site-loading requests fly concurrently and their responses can arrive out-of-order, setting `siteOptions` to the wrong entity's sites.

The transaction loading itself is safe (uses `switchMap` on `load$`), but the site-options loading is not.

**Impact:** Site filter dropdown may show sites from a previously-selected legal entity if responses arrive out-of-order.

**Recommendation:** Use a `switchMap` pattern (similar to `load$`) for site loading, or cancel the previous subscription before starting a new one.

---

### BUG-F02-6: Filter debouncing is absent — every keystroke triggers an API call (Severity: LOW)

**File:** `transaction-filters.component.ts:60-67`

```html
<input pInputText [(ngModel)]="filters.fccTransactionId" (ngModelChange)="emit()" />
```

All text input filters (fccTransactionId, odooOrderId) emit on every `ngModelChange`, which fires on every keystroke. Each emission triggers `onFiltersChange()` in the parent, which resets pagination and fires an API request via `load$`.

The `switchMap` in `load$` does cancel in-flight requests, so only the last one completes — but every keystroke still initiates an HTTP request that must be cancelled, creating unnecessary network traffic and backend load.

**Impact:** Typing "ABC123" in the transaction ID filter fires 6 API requests (A, AB, ABC, ABC1, ABC12, ABC123), of which 5 are immediately cancelled. Under heavy load this wastes server resources.

**Recommendation:** Add a `debounceTime(300)` to the text input emissions, or debounce within the `load$` pipeline.

---

### BUG-F02-7: IgnoreQueryFilters bypasses soft-delete and tenant filters (Severity: MEDIUM)

**File:** `OpsTransactionsController.cs:88`

```csharp
var query = _db.Transactions
    .IgnoreQueryFilters()
    .AsNoTracking()
    .Where(item => item.LegalEntityId == legalEntityId);
```

The query uses `IgnoreQueryFilters()` which bypasses all global query filters configured on the `Transaction` entity in EF Core. If there are soft-delete filters (e.g., `IsDeleted == false`) or other tenant isolation filters configured globally, this call bypasses them.

While the explicit `WHERE LegalEntityId = @id` provides tenant scoping, any other global filters (soft-delete, archival status, etc.) are completely bypassed. The `GetTransactionById` endpoint (line 219) also uses `IgnoreQueryFilters()`.

**Impact:** Depends on what global query filters exist. Could expose soft-deleted or otherwise filtered-out transactions in the portal list view.

**Recommendation:** Audit what global query filters are configured on the `Transaction` entity. If soft-delete or similar filters exist, either remove `IgnoreQueryFilters()` or manually re-apply critical filters.

---

## 8. Summary

| Severity | Count | IDs |
|----------|-------|-----|
| HIGH | 0 | — |
| MEDIUM | 3 | BUG-F02-1, BUG-F02-2, BUG-F02-7 |
| LOW | 4 | BUG-F02-3, BUG-F02-4, BUG-F02-5, BUG-F02-6 |
| **Total** | **7** | |
