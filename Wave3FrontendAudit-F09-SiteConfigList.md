# Wave 3 — Frontend Audit: F-09 SiteConfigComponent (`/sites/list`)

**Audited:** 2026-03-13
**Files reviewed:**
- `src/portal/src/app/features/site-config/site-config.component.ts`
- `src/portal/src/app/features/site-config/site-config.routes.ts`
- `src/portal/src/app/core/services/site.service.ts`
- `src/portal/src/app/core/services/master-data.service.ts`
- `src/portal/src/app/core/services/http-params.util.ts`
- `src/portal/src/app/core/models/site.model.ts`
- `src/portal/src/app/core/models/common.model.ts`
- `src/cloud/FccMiddleware.Api/Controllers/SitesController.cs`
- `src/cloud/FccMiddleware.Api/Portal/PortalAccessResolver.cs`
- `src/cloud/FccMiddleware.Contracts/Portal/PortalCommonContracts.cs`

---

## Summary

The SiteConfigComponent is a legal-entity-scoped site listing page with cursor-based pagination, filtering by operating model / connectivity mode / active status, and row-click navigation to detail. It uses a `Subject`+`switchMap` reactive pattern for data loading.

---

## Issues Found

### F09-01 · Backend loads ALL sites into memory, then filters/paginates in-process (MEDIUM — Performance)

**Location:** `SitesController.cs:57-63`

The `GetSites` endpoint calls `ToListAsync()` on the entire result set for a legal entity (line 63), pulling **all sites** into memory. Filtering (`isActive`, `operatingModel`, etc.), cursor-based pagination, and ordering are then done with LINQ-to-Objects. For legal entities with hundreds or thousands of sites, this defeats the purpose of pagination and creates unnecessary memory pressure and latency.

```csharp
var sites = await _db.Sites
    .IgnoreQueryFilters()
    .AsNoTracking()
    .Include(site => site.LegalEntity)
    .Include(site => site.FccConfigs.Where(config => config.IsActive))
    .Where(site => site.LegalEntityId == legalEntityId)
    .ToListAsync(cancellationToken);  // ← loads ALL sites for entity into memory

IEnumerable<Site> filtered = sites;
// ... in-memory filtering follows
```

Filtering and cursor pagination should be pushed down to the database query via `IQueryable`.

---

### F09-02 · `TotalCount` re-enumerates disposed/completed `IEnumerable` (MEDIUM — Correctness)

**Location:** `SitesController.cs:132`

```csharp
TotalCount = filtered.Count()   // ← `filtered` was already partially consumed
```

`filtered` is an `IEnumerable<Site>` built from a `List<Site>`, so technically it can be re-enumerated. However the variable `ordered` (line 98-101) materialises via `.ToList()`, and `filtered` may have been trimmed again at line 105-107. The `filtered.Count()` at line 132 re-enumerates the *original* lazy filter chain (before cursor pruning) which is correct because `filtered` is never reassigned — but this is fragile and confusing. If `ordered` were ever assigned back to `filtered`, the count would silently change.

More practically: the `TotalCount` reflects the total pre-cursor-filtered count, but the variable is a lazy `IEnumerable` evaluated twice — once for the data path and once for the count. This is wasteful on large datasets (O(2N)).

---

### F09-03 · Cursor pagination breaks when `UpdatedAt` is `null` (LOW — Correctness)

**Location:** `SitesController.cs:99, 103-107`

Ordering is by `site.UpdatedAt` (which can be null). The cursor encodes `UpdatedAt`, but `PortalCursor.TryDecode` will decode a timestamp, and the comparison `site.UpdatedAt > cursorTimestamp` will never be true when `UpdatedAt` is null (null comparisons return false). Sites with `UpdatedAt = null` will always be excluded after the first page, making them invisible to pagination beyond page 1.

---

### F09-04 · `legalEntityId` query parameter not validated for `Guid.Empty` (LOW — Validation)

**Location:** `SitesController.cs:31`

```csharp
[FromQuery] Guid legalEntityId
```

If the frontend sends an empty or zero GUID, the backend will proceed with a query for `LegalEntityId == Guid.Empty` and return an empty result silently. This should be explicitly validated to return 400.

The frontend uses `string | null` for entity IDs but the backend binds to `Guid`, so a non-GUID string would fail model binding — but `Guid.Empty` is still valid.

---

### F09-05 · No error feedback to user on failed data load (LOW — UX)

**Location:** `site-config.component.ts:351-356`

```typescript
catchError(() => {
  this.sites.set([]);
  this.totalRecords.set(0);
  this.loading.set(false);
  return EMPTY;
}),
```

Errors are silently swallowed. The user sees an empty table with no indication that the load failed (no toast, no error message). This applies to both the sites load and the legal entities load (line 335-336 has no error handler at all — an error from `getLegalEntities()` will propagate unhandled).

---

### F09-06 · Legal entities load has no error handling (LOW — Reliability)

**Location:** `site-config.component.ts:333-336`

```typescript
this.masterDataService
  .getLegalEntities()
  .pipe(takeUntilDestroyed())
  .subscribe({ next: (entities) => this.legalEntities.set(entities) });
```

No `error` callback. If `/api/v1/master-data/legal-entities` fails, the error propagates as an unhandled observable error. The legal entity dropdown stays empty with no feedback. This is especially problematic because the entire page is unusable without legal entities.

---

### F09-07 · `filterActiveOnly` toggle semantics are ambiguous (LOW — UX/Logic)

**Location:** `site-config.component.ts:315, 438`

```typescript
filterActiveOnly = true;  // default ON

// in triggerLoad:
isActive: this.filterActiveOnly ? true : null,
```

When the toggle is OFF, `isActive` is sent as `null` (no filter), which shows both active and inactive sites. This is correct. But the default is `true` — meaning inactive sites are hidden by default with no visual indication. A user who is looking for a decommissioned/inactive site may not realize the toggle needs to be switched off.

---

### F09-08 · `pageSize` mismatch between frontend default and backend default (LOW — Consistency)

**Location:** `site-config.component.ts:293` vs `SitesController.cs:33`

```typescript
// Frontend
readonly pageSize = 20;
```
```csharp
// Backend
[FromQuery] int pageSize = 50
```

The frontend defaults to 20, the backend to 50. While the frontend always sends `pageSize` as a query parameter (via `buildHttpParams`), if for any reason it's omitted, the backend would return 50 rows — more than the frontend paginator expects with `[rows]="pageSize"` set to 20. Not a current bug but a lurking inconsistency.

---

### F09-09 · `IgnoreQueryFilters()` bypasses tenant-scoping global query filters (MEDIUM — Security)

**Location:** `SitesController.cs:58`

```csharp
var sites = await _db.Sites
    .IgnoreQueryFilters()   // ← bypasses all global query filters
```

Every query in this controller uses `IgnoreQueryFilters()`. While the controller manually checks `access.CanAccess(legalEntityId)`, this disables any global tenant-scoping or soft-delete filters configured on the `DbContext`. If tenant isolation is enforced via global query filters elsewhere, this bypass means the controller is responsible for its own isolation — and a missed check on any future endpoint added to this controller would be a tenant data leak.

This is the same pattern across all portal controllers, but it's worth flagging as the authorization model is fragile.

---

## What Works Well

1. **Reactive loading pattern**: `Subject` → `switchMap` cleanly cancels in-flight requests on filter/page changes
2. **Proper role guard**: Route is protected by `roleGuard(['SystemAdmin', 'OperationsManager', 'SiteSupervisor'])`
3. **Backend legal-entity access control**: The controller properly resolves portal access and checks `CanAccess(legalEntityId)` before returning data
4. **Cursor-based pagination**: Frontend maintains a cursor stack enabling forward/backward page navigation
5. **Keyboard accessibility**: Table rows have `tabindex="0"` and `keydown.enter` handlers
6. **buildHttpParams utility**: Cleanly skips null/undefined values, preventing "null" string params

---

## Backend Endpoint Trace

| Frontend Call | Backend Endpoint | Auth | Controller Method |
|---|---|---|---|
| `masterDataService.getLegalEntities()` | `GET /api/v1/master-data/legal-entities` | PortalUser | MasterDataController |
| `siteService.getSites(params)` | `GET /api/v1/sites` | PortalUser | SitesController.GetSites |
| Row click → navigate `/sites/:id` | `GET /api/v1/sites/{id}` | PortalUser | SitesController.GetSite |

---

## Issue Count: 9

| ID | Severity | Category | Summary |
|---|---|---|---|
| F09-01 | MEDIUM | Performance | All sites loaded into memory, filtered/paginated in-process |
| F09-02 | MEDIUM | Correctness | TotalCount uses lazy IEnumerable, double-enumeration |
| F09-03 | LOW | Correctness | Cursor pagination breaks for null UpdatedAt |
| F09-04 | LOW | Validation | Guid.Empty not rejected for legalEntityId |
| F09-05 | LOW | UX | Silent error swallowing on site load failure |
| F09-06 | LOW | Reliability | No error handler on legal entities load |
| F09-07 | LOW | UX | Active-only filter default hides inactive sites silently |
| F09-08 | LOW | Consistency | Frontend/backend pageSize defaults differ (20 vs 50) |
| F09-09 | MEDIUM | Security | IgnoreQueryFilters bypasses tenant-scoping globally |
