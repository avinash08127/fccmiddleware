# Wave 3 — Frontend Audit: F-10 SiteDetailComponent

**Route:** `/sites/:id`
**Component:** `SiteDetailComponent` (`src/portal/src/app/features/site-config/site-detail.component.ts`)
**Sub-components:** `FccConfigFormComponent`, `PumpMappingComponent`
**Backend:** `SitesController` (`src/cloud/FccMiddleware.Api/Controllers/SitesController.cs`)
**Date:** 2026-03-13

---

## Issues Found

### F10-01 · HIGH — Backend drops all vendor-specific FCC fields on read AND write

**Location:** `SitesController.cs:717-743` (MapFccConfig), `SitesController.cs:332-417` (UpdateFccConfig)

The `MapFccConfig` mapper only returns generic fields (vendor, protocol, host, port, heartbeat, etc.). It never maps vendor-specific fields:
- DOMS: `JplPort`, `FcAccessCode`, `DomsCountryCode`, `PosVersionId`, `ConfiguredPumps`
- Radix: `SharedSecret`, `UsnCode`, `AuthPort`, `FccPumpAddressMap`
- Petronite: `ClientId`, `ClientSecret`, `WebhookSecret`, `OAuthTokenEndpoint`
- Advatec: `AdvatecDevicePort`, `AdvatecWebhookToken`, `AdvatecEfdSerialNumber`, `AdvatecPumpMap`

Similarly, `UpdateFccConfig` only processes generic fields — all vendor-specific fields in the request body are silently ignored.

**Impact:** The FCC config form shows empty/null for all vendor fields after load. Saving a config discards vendor-specific settings already stored in the database, effectively breaking site FCC connectivity.

---

### F10-02 · HIGH — canSave() does not call FccConfigForm.isValid()

**Location:** `site-detail.component.ts:597-599`

```typescript
canSave(): boolean {
  return !this.saving() && !!this.draftOperatingModel;
}
```

The `FccConfigFormComponent` has a thorough `isValid()` method (lines 728-774) that validates vendor, protocol, host, port, and vendor-specific required fields. But the parent `SiteDetailComponent` never calls it. The form can be submitted with:
- Missing host address
- Invalid port (0, null, >65535)
- Missing DOMS access code
- Missing Radix shared secret / USN code
- Invalid JSON pump maps (despite client-side validation display)

**Impact:** Invalid FCC configurations can be submitted to the backend. Backend may reject some but not all — e.g. null host falls back to `"127.0.0.1"` (SitesController.cs:344).

---

### F10-03 · MEDIUM — Fiscalization toggles show draft defaults instead of actual values in read mode

**Location:** `site-detail.component.ts:328-344`

The `requireCustomerTaxId` and `fiscalReceiptRequired` toggle switches always bind to `draftFiscalization` properties, even when `editMode()` is false:

```html
<p-toggleswitch
  [(ngModel)]="draftFiscalization.requireCustomerTaxId"
  [disabled]="!editMode()"
/>
```

The draft is initialized to `{ requireCustomerTaxId: false, fiscalReceiptRequired: false }` at component creation (line 502-507) and only populated with actual site values when `enterEditMode()` is called (line 584-589). In read mode, the toggles display `false` regardless of the actual site values.

Compare with the tolerance section (lines 244-262) which correctly uses `site()!.tolerance?.amountTolerancePct` for read mode.

**Impact:** Users see incorrect fiscalization state on page load — toggles appear off even when actually enabled.

---

### F10-04 · MEDIUM — forkJoin creates partial save scenario

**Location:** `site-detail.component.ts:628-633`

```typescript
forkJoin([siteUpdate$, this.siteService.updateFccConfig(s.id, fccPayload)])
  .subscribe({
    next: ([updatedSite]) => this.onSaveSuccess(updatedSite),
    error: () => this.onSaveError(),
  });
```

If `updateSite` succeeds but `updateFccConfig` fails (or vice versa), `forkJoin` reports error and `onSaveError()` shows a generic failure toast. The user doesn't know that one of the two operations succeeded. Re-clicking "Save" will re-send both requests, potentially overwriting the already-saved data with stale values.

**Impact:** Partial saves are hidden from the user; retry sends duplicate writes.

---

### F10-05 · MEDIUM — GetSites loads all sites into memory before filtering

**Location:** `SitesController.cs:57-63`

```csharp
var sites = await _db.Sites
    .IgnoreQueryFilters()
    .AsNoTracking()
    .Include(site => site.LegalEntity)
    .Include(site => site.FccConfigs.Where(config => config.IsActive))
    .Where(site => site.LegalEntityId == legalEntityId)
    .ToListAsync(cancellationToken);

IEnumerable<Site> filtered = sites;
// ... then applies in-memory filters for isActive, operatingModel, connectivityMode, etc.
```

All sites for a legal entity (plus their FccConfigs and LegalEntity navigation) are materialized into memory, then filtered and paginated in-memory. For legal entities with hundreds or thousands of sites, this causes excessive memory allocation and DB load. Filters like `isActive`, `operatingModel`, and cursor-based pagination should be pushed into the SQL query.

**Impact:** Performance degradation at scale; potential OOM for large legal entities.

---

### F10-06 · MEDIUM — ConnectivityMode stored as raw string without validation

**Location:** `SitesController.cs:206-209`

```csharp
if (!string.IsNullOrWhiteSpace(request.ConnectivityMode))
{
    site.ConnectivityMode = request.ConnectivityMode;
}
```

Unlike `OperatingModel` (which is parsed and validated via `Enum.TryParse` at lines 187-191) and `FiscalizationMode` (validated at lines 194-199), `ConnectivityMode` is assigned directly from the request string without any validation. A user could POST `"FOOBAR"` as the connectivity mode and it would be stored.

**Impact:** Invalid connectivity mode values can be persisted, causing downstream failures in edge agents that parse this field.

---

### F10-07 · MEDIUM — No loading/disabled state for pump and nozzle mutation operations

**Location:** `site-detail.component.ts:642-721`

`onPumpAdded()`, `onPumpRemoved()`, and `onNozzleUpdated()` make API calls without setting any loading/saving state. The UI remains interactive during the request:
- Add Pump button is not disabled — rapid clicks can create duplicate pumps (backend checks `PumpNumber` uniqueness but nozzle-level duplicates are possible)
- Remove Pump has no confirmation dialog or loading state — accidental clicks trigger immediate soft-delete
- Nozzle updates can be fired multiple times concurrently

**Impact:** Race conditions; accidental deletions; duplicate API calls.

---

### F10-08 · MEDIUM — MapFccConfig hardcodes CatchUpPullIntervalSeconds and HybridCatchUpIntervalSeconds to null

**Location:** `SitesController.cs:738-739`

```csharp
CatchUpPullIntervalSeconds = null,
HybridCatchUpIntervalSeconds = null,
```

These values exist on the `FccConfig` domain entity but are hardcoded to `null` in the response mapper. The frontend form (fcc-config-form.component.ts:199-229) has input fields for these values and conditionally shows them based on transaction mode. After save, these fields reset to null on the next load.

**Impact:** Catch-up interval configuration is silently discarded; edge agents may use wrong polling intervals.

---

### F10-09 · LOW — getSiteById and getSiteDetail are identical API calls

**Location:** `site.service.ts:42-47`

```typescript
getSiteById(id: string): Observable<Site> {
  return this.http.get<Site>(`/api/v1/sites/${id}`);
}
getSiteDetail(id: string): Observable<SiteDetail> {
  return this.http.get<SiteDetail>(`/api/v1/sites/${id}`);
}
```

Both methods hit the same endpoint `GET /api/v1/sites/{id}`. The backend always returns a `SiteDetailDto`. The only difference is the TypeScript type assertion. `getSiteById` claims to return `Site` but actually receives a `SiteDetail`, silently discarding extra fields at the type level.

**Impact:** Confusing API surface; no actual functional bug since SiteDetail extends Site.

---

### F10-10 · LOW — Backend FiscalReceiptRequired auto-forced for FCC_DIRECT mode

**Location:** `SitesController.cs:682-684`

```csharp
FiscalReceiptRequired = site.FiscalReceiptRequired
    || site.FiscalizationMode == FiscalizationMode.FCC_DIRECT
```

When fiscalization mode is `FCC_DIRECT`, the response always returns `FiscalReceiptRequired = true` regardless of the stored value. If a user sets `FCC_DIRECT` mode and explicitly unchecks "Fiscal Receipt Required", the toggle will snap back to `true` after the next load. This is undocumented behavior with no UI hint.

**Impact:** Confusing UX; users cannot disable fiscal receipts for FCC_DIRECT sites even if they intend to.

---

### F10-11 · LOW — No audit events for site/FCC configuration changes

**Location:** `SitesController.cs` (UpdateSite, UpdateFccConfig, AddPump, RemovePump, UpdateNozzle)

None of the write endpoints in `SitesController` create `AuditEvent` records. Changes to critical configuration (FCC credentials, tolerance settings, pump mappings, fiscalization) leave no audit trail. Compare with other controllers that likely create audit events for mutations.

**Impact:** Compliance gap; no way to trace who changed what configuration and when.

---

## Summary

| ID | Severity | Category | Description |
|----|----------|----------|-------------|
| F10-01 | HIGH | Backend/Data | Vendor-specific FCC fields dropped on read and write |
| F10-02 | HIGH | Validation | canSave() ignores FCC form validation |
| F10-03 | MEDIUM | UI Logic | Fiscalization toggles show false in read mode |
| F10-04 | MEDIUM | UI Logic | forkJoin partial save not surfaced to user |
| F10-05 | MEDIUM | Performance | All sites loaded into memory before filtering |
| F10-06 | MEDIUM | Validation | ConnectivityMode not validated before storage |
| F10-07 | MEDIUM | UI Logic | No loading state for pump/nozzle mutations |
| F10-08 | MEDIUM | Backend/Data | Catch-up intervals hardcoded to null in response |
| F10-09 | LOW | Code Quality | Duplicate service methods for same endpoint |
| F10-10 | LOW | UI Logic | FiscalReceiptRequired silently forced for FCC_DIRECT |
| F10-11 | LOW | Compliance | No audit events for site config mutations |

**Total: 11 issues (2 High, 5 Medium, 4 Low, 0 Info)**
