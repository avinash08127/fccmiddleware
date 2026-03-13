# Wave 3 Frontend Audit — F-12: Audit Log (`/audit/list`)

**Component:** `AuditLogComponent`
**File:** `src/portal/src/app/features/audit-log/audit-log.component.ts`
**Route:** `/audit/list`
**Related:** `AuditDetailComponent`, `AuditService`, `AuditController`
**Date:** 2026-03-13

---

## Scope

- UI logic and state management
- Filter validations
- API calls and data mapping
- Backend endpoint tracing (GET /api/v1/audit/events)
- Pagination (cursor-based keyset)
- Authorization and tenant scoping

---

## Architecture Summary

The audit log is a lazy-loaded, cursor-paginated list of immutable `AuditEvent` records. The frontend uses Angular signals for state management and a `Subject<LoadRequest>` → `switchMap` pattern for data fetching. The backend stores events in a PostgreSQL partitioned table (`audit_events`) with a composite PK `(Id, CreatedAt)` and serves them via keyset pagination with base64-encoded cursors.

Key features: correlation ID trace mode, event type severity badges, expandable JSON payloads, date range validation (30-day max), legal entity scoping.

---

## Findings

### F12-01 [MEDIUM] — "View Full Correlation Trace" navigation is broken

**Location:** `audit-detail.component.ts:263-268` → `audit-log.component.ts` (missing)

`AuditDetailComponent.viewTrace()` navigates to `/audit/list?correlationId=<uuid>`, but `AuditLogComponent` does not inject `ActivatedRoute` and never reads query params. The correlation ID filter is not populated and no search is executed. The user lands on an empty, unsearched audit log page.

**Impact:** The "View Full Correlation Trace" button in the detail view is non-functional. Users must manually copy the correlation ID and paste it into the filter.

**Fix:** Inject `ActivatedRoute` in `AuditLogComponent`, read `queryParams.correlationId` in the constructor or `ngOnInit`, pre-populate the filter, and auto-trigger `search()`.

---

### F12-02 [MEDIUM] — No UUID format validation on Correlation ID input

**Location:** `audit-log.component.ts:106-112` (template), `:542` (search logic)

The correlation ID input accepts any string. The backend parameter is `[FromQuery] Guid? correlationId` — if a non-UUID string is sent, ASP.NET model binding silently leaves it `null`, and the filter is ignored. The user sees all events instead of a filtered set, with no error feedback.

**Impact:** Misleading results when users type partial or malformed correlation IDs. Could cause confusion about the data being displayed.

**Fix:** Add a UUID regex validation (`/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i`) before submitting the search. Show a validation error if the format doesn't match.

---

### F12-03 [MEDIUM] — Silent error swallowing on API failures

**Location:** `audit-log.component.ts:469-471`

```typescript
catchError(() => {
  this.listState.update((s) => ({ ...s, loading: false, data: [] }));
  return EMPTY;
})
```

API errors (500, 403, network failures) silently result in an empty table. No toast, banner, or error signal is set. The result is visually identical to "no matching events found" — the user has no way to distinguish between no results and a backend failure.

**Impact:** Users may think there are no audit events when in reality the API is failing. Operational issues go unreported.

**Fix:** Set an error signal and display an error banner/toast in the template. Distinguish between "no results" and "error loading."

---

### F12-04 [MEDIUM] — No backend date range enforcement

**Location:** `AuditController.cs:73-81`

The frontend enforces a 30-day maximum date range (`MAX_DATE_RANGE_DAYS = 30`), but the backend has no such limit. Direct API callers (or a bypassed frontend) can query unbounded date ranges, potentially causing expensive full-partition scans on the `audit_events` table.

**Impact:** Performance risk. A malicious or misconfigured API client could cause database slowdowns affecting all users.

**Fix:** Add server-side validation: reject requests where `to - from > 31 days` (or appropriate limit). Return 400 with a clear error message.

---

### F12-05 [LOW] — Stale cursors when page size changes mid-session

**Location:** `audit-log.component.ts:567-586` (onLazyLoad), `:478-480` (cursor storage)

When the user changes `rowsPerPageOptions` (20 -> 50 -> 100) after navigating to page 2+, the `cursors[]` array retains entries computed for the previous page size. Changing page size triggers `onLazyLoad` with `first=0`, which correctly fetches page 0 from scratch and overwrites `cursors[1]`. However, stale entries at higher indices remain. If PrimeNG allows skipping to a later page (e.g., clicking page 3 directly), the stale cursor could produce incorrect results.

**Impact:** Low probability but could show misaligned data in edge cases.

**Fix:** Reset the `cursors` array to `[null]` when `event.rows` differs from the current `pageSize` in `onLazyLoad`.

---

### F12-06 [LOW] — EventType enum drift risk (frontend vs backend)

**Location:** `audit.model.ts:3-22` (frontend enum), `AuditEvent.cs:16` (backend string field)

The frontend hardcodes 18 `EventType` enum values. The backend stores `EventType` as a plain `VARCHAR(100)` with no enum constraint. New event types added to the backend:
- Won't appear in the frontend's multi-select filter dropdown
- Will fall through the `eventTypeSeverity()` function to the `'contrast'` default
- Won't cause runtime errors but create a silent feature gap

**Impact:** Maintenance burden. New event types are invisible in the filter until the frontend enum is updated.

**Fix:** Consider fetching distinct event types from the backend, or add a backend enum registry endpoint. At minimum, document the sync requirement.

---

### F12-07 [LOW] — Duplicated `eventTypeSeverity` function

**Location:** `audit-log.component.ts:32-39`, `audit-detail.component.ts:16-23`

The identical `eventTypeSeverity()` function is copy-pasted in both components. Any severity mapping change must be made in two places, and they will inevitably diverge.

**Impact:** Maintenance risk — inconsistent severity colors between list and detail views after a partial update.

**Fix:** Extract to a shared utility (e.g., `audit.utils.ts`) or into the `StatusBadgeComponent`.

---

### F12-08 [LOW] — `to` date filter boundary is inclusive

**Location:** `AuditController.cs:80`

```csharp
query = query.Where(item => item.CreatedAt <= to.Value);
```

If the frontend sends the end date as midnight (e.g., `2026-03-13T00:00:00.000Z`), events occurring after midnight on that day are excluded. Users selecting "March 13" as the end date likely expect the entire day to be included.

**Impact:** Users may miss events on the last day of their selected range unless they explicitly pick the next day.

**Fix:** Either use `< to.Value.AddDays(1)` for date-only inputs, or document the exclusive boundary behavior. Check consistency with other controllers.

---

### F12-09 [LOW] — Detail component doesn't distinguish 403 from 404

**Location:** `audit-detail.component.ts:290-293`

```typescript
error: () => {
  this.event.set(null);
  this.loading.set(false);
}
```

Any error — 403 (wrong legal entity), 404 (not found), 500 (server error) — shows the same "Event not found" message. Users denied access see a misleading "not found" instead of "access denied."

**Impact:** Confusing UX for users with limited legal entity scope. Could lead to unnecessary bug reports.

**Fix:** Inspect the HTTP error status and show appropriate messages: 403 -> "You don't have access to this event", 404 -> "Event not found", 5xx -> "Error loading event."

---

### F12-10 [LOW] — `SchemaVersion` derived from payload JSON, not entity column

**Location:** `PortalJson.cs:29-42`, `AuditEvent.cs` (no `SchemaVersion` property)

The `AuditEvent` entity has no `SchemaVersion` column. The DTO's `SchemaVersion` is extracted from the JSON payload via `PortalJson.ReadSchemaVersion()`, checking `payload.schemaVersion` then `payload.payload.schemaVersion`, defaulting to 1. This is fragile — if the payload format changes or the field is renamed, all events silently report version 1.

**Impact:** Incorrect schema version display in the UI. Not filterable or queryable via SQL without JSON path expressions.

**Fix:** Consider promoting `SchemaVersion` to a first-class column on the `audit_events` table for reliability and queryability.

---

## Summary

| ID | Severity | Category | Title |
|----|----------|----------|-------|
| F12-01 | MEDIUM | Navigation | "View Full Correlation Trace" broken — query params not read |
| F12-02 | MEDIUM | Validation | No UUID format validation on Correlation ID input |
| F12-03 | MEDIUM | Error Handling | Silent error swallowing on API failures |
| F12-04 | MEDIUM | Security/Perf | No backend date range enforcement |
| F12-05 | LOW | Pagination | Stale cursors when page size changes mid-session |
| F12-06 | LOW | Maintenance | EventType enum drift between frontend and backend |
| F12-07 | LOW | Maintenance | Duplicated `eventTypeSeverity` function across components |
| F12-08 | LOW | Data | `to` date filter boundary is inclusive (may miss last-day events) |
| F12-09 | LOW | UX | Detail component doesn't distinguish 403 from 404 |
| F12-10 | LOW | Design | SchemaVersion derived from payload JSON, not entity column |

**Total: 10 issues (4 Medium, 6 Low)**
