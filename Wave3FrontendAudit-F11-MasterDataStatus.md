# Wave 3 Frontend Audit — F-11 Master Data Status

**Route:** `/master-data/status`
**Component:** `MasterDataComponent` — `src/portal/src/app/features/master-data/master-data.component.ts`
**Routes:** `master-data.routes.ts`
**Service:** `MasterDataService` — `src/portal/src/app/core/services/master-data.service.ts`
**Model:** `master-data.model.ts`
**Backend:** `MasterDataBrowserController` — `src/cloud/FccMiddleware.Api/Controllers/MasterDataBrowserController.cs`

---

## Findings

### F11-01 — Stub component: no UI logic, no API calls (HIGH)

**File:** `master-data.component.ts:1-8`

The `MasterDataComponent` is a placeholder that renders only `<p>Master Data Status — coming soon</p>`. It does not inject `MasterDataService`, does not call `getSyncStatus()`, and displays no data. The backend endpoint `GET /api/v1/master-data/sync-status` is fully implemented (183 lines of logic in the controller) and returns staleness info for all 5 entity types. The frontend service method `getSyncStatus()` exists and is correctly typed but is never invoked.

**Impact:** The master-data status page is non-functional. Operators have no visibility into whether master data syncs from Databricks are running, stale, or failing.

---

### F11-02 — Frontend model `LegalEntity` misses fields from backend DTO (MEDIUM)

**File:** `master-data.model.ts:3-11` vs `PortalMasterDataContracts.cs:3-15`

The backend `PortalLegalEntityDto` returns 10 fields: `id`, `code`, `name`, `countryCode`, `countryName`, `currencyCode`, `country`, `odooCompanyId`, `isActive`, `updatedAt`. The frontend `LegalEntity` interface only declares 6 fields (`id`, `code`, `name`, `currencyCode`, `country`, `isActive`, `updatedAt`), silently dropping:

- `countryCode` — ISO country code
- `countryName` — full country name (the frontend `country` field maps to backend `Country` which is nullable and redundant)
- `odooCompanyId` — Odoo integration identifier

When `getLegalEntities()` is eventually used, these fields will be unavailable to the template without a model fix.

---

### F11-03 — Backend DTO has duplicate `Country`/`CountryName` fields (LOW)

**File:** `PortalMasterDataContracts.cs:8-11`, `MasterDataBrowserController.cs:53-54`

`PortalLegalEntityDto` has both `CountryName` (required) and `Country` (nullable). In the controller mapping, both are set to `item.CountryName`:

```csharp
CountryName = item.CountryName,  // line 53
Country = item.CountryName,       // line 54
```

This is redundant and sends the same value under two JSON keys (`countryName` and `country`), wasting bandwidth and creating ambiguity about which field consumers should use.

---

### F11-04 — Backend `GetSyncStatus` loads all entities into memory (MEDIUM — Performance)

**File:** `MasterDataBrowserController.cs:119`

The `BuildAsync<T>` helper calls `query.AsNoTracking().ToListAsync()` then computes `Max(syncedAt)` and `Count(isActive)` in-memory via LINQ-to-Objects. This materializes every row from `LegalEntities`, `Sites`, `Pumps`, `Products`, and `Operators` tables. For large deployments with thousands of sites/pumps/products, this causes:

- Excessive memory allocation (5 full table loads per request)
- Unnecessary network transfer from PostgreSQL

These aggregates (`MAX`, `COUNT`) should be computed server-side in SQL.

---

### F11-05 — Backend `GetSyncStatus` reports misleading `UpsertedCount` (LOW)

**File:** `MasterDataBrowserController.cs:128-129`

```csharp
UpsertedCount = activeCount,          // line 128
DeactivatedCount = deactivatedCount,  // line 129
```

`UpsertedCount` is set to the total count of currently-active records, not the number of records upserted in the last sync. The frontend model `MasterDataSyncStatus.upsertedCount` will display this misleading value. The field name implies "records changed in last sync" but actually means "total active records."

---

### F11-06 — Backend `GetSyncStatus` runs 5 queries sequentially (LOW — Performance)

**File:** `MasterDataBrowserController.cs:146-179`

The 5 `BuildAsync(...)` calls are awaited sequentially because they appear as list initializer items (each `await` must complete before the next list item). These are independent queries on different tables and could run in parallel via `Task.WhenAll(...)`, roughly halving the endpoint latency.

---

### F11-07 — No role-based route guard on `/master-data` (LOW)

**File:** `app.routes.ts:60-66`

The `/master-data` route relies only on the parent shell's `MsalGuard` (authentication). There is no role-based `canActivate` guard. Any authenticated user can navigate here. While the backend controller enforces `[Authorize(Policy = "PortalUser")]`, the frontend should prevent navigation for unauthorized roles to avoid confusing UI states (especially once the page is implemented).

---

## Summary

| # | Issue | Severity | Category |
|---|-------|----------|----------|
| F11-01 | Stub component — no UI, no API calls | HIGH | Functional |
| F11-02 | Frontend model missing backend DTO fields | MEDIUM | Data |
| F11-03 | Backend DTO has duplicate Country fields | LOW | Data |
| F11-04 | Backend loads all entities into memory for aggregates | MEDIUM | Performance |
| F11-05 | `UpsertedCount` field is semantically misleading | LOW | Data |
| F11-06 | 5 sequential queries could run in parallel | LOW | Performance |
| F11-07 | No role-based route guard | LOW | Security |

**Total: 7 issues (1 HIGH, 2 MEDIUM, 4 LOW)**
