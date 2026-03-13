# Wave 3 Frontend Audit — F-13: Audit Detail (`/audit/events/:id`)

**Component:** `AuditDetailComponent`
**File:** `src/portal/src/app/features/audit-log/audit-detail.component.ts`
**Route:** `/audit/events/:id`
**Related:** `AuditLogComponent`, `AuditService`, `AuditController.GetAuditEventById`
**Date:** 2026-03-13

---

## Scope

- UI logic and state management
- Input validations
- API calls and data mapping
- Backend endpoint tracing (GET /api/v1/audit/events/{eventId})
- Authorization and tenant scoping
- Payload display and clipboard interaction

---

## Architecture Summary

The audit detail page is a standalone Angular component that loads a single `AuditEvent` by ID from the route parameter. It displays an event "envelope" (metadata fields) and a formatted JSON payload. State is managed via Angular signals (`loading`, `event`). Navigation flows from the audit log list via `viewDetail()` → route `/audit/events/:eventId`. The backend endpoint (`AuditController.GetAuditEventById`) queries by the DB `Id` column using `IgnoreQueryFilters()` + post-fetch tenant access check.

Key features: formatted JSON payload display, clipboard copy, "View Full Correlation Trace" link back to the list with a correlationId filter, PrimeSeverity badge by event type.

---

## Findings

### F13-01 [HIGH] — Backend detail query scans all partitions (no partition key in WHERE clause)

**Location:** `AuditController.cs:138-142`

```csharp
var auditEvent = await _db.AuditEvents
    .IgnoreQueryFilters()
    .AsNoTracking()
    .OrderByDescending(item => item.CreatedAt)
    .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
```

The `audit_events` table uses monthly range partitioning on `created_at` with a composite PK `(id, created_at)`. There is no unique index on `id` alone. This query filters only by `Id` without any `CreatedAt` constraint, so PostgreSQL must scan **every partition** to find the row (partition pruning cannot activate). The `OrderByDescending(CreatedAt)` makes it scan newest-first but doesn't eliminate partitions.

As the table grows (one partition per month), every detail-page load becomes progressively slower.

**Impact:** O(N) partition scans where N = number of monthly partitions. For a 2-year deployment: 24 partition scans per detail view.

**Fix:** Accept an optional `timestamp` or `createdAt` query parameter from the frontend (the list response already includes `timestamp`). Use it in the WHERE clause to enable partition pruning. Alternatively, maintain a global unique index on `id` (though this adds write overhead).

---

### F13-02 [MEDIUM] — EventId in DTO may differ from DB Id, causing 404 on detail navigation

**Location:** `PortalJson.cs:19-27` (ReadEventId), `AuditController.cs:141` (lookup by Id), `audit-log.component.ts:600` (navigation)

`PortalJson.ReadEventId` extracts `eventId` from the **JSON payload** first, falling back to the DB `Id`:

```csharp
public static Guid ReadEventId(AuditEvent auditEvent, JsonElement payload)
{
    if (TryGetGuid(payload, out var eventId, "eventId"))
        return eventId;
    return auditEvent.Id;
}
```

The list endpoint returns this payload-derived `eventId` in the DTO. The list component navigates to `/audit/events/${event.eventId}`. The detail endpoint then queries `item.Id == eventId` — using the **DB column**, not the payload field.

If any event's payload contains an `eventId` property that differs from the DB `Id` (e.g., an upstream system's event ID embedded in the payload), the navigation produces a 404 because the DB has no row with `Id == payloadEventId`.

**Impact:** Detail view returns "Event not found" for events whose payload `eventId` differs from the database primary key. Silent data inaccessibility.

**Fix:** Either (a) always use the DB `Id` in the DTO (ignore payload eventId), or (b) store the original payload eventId in a separate DTO field and always use DB `Id` for navigation/lookup.

---

### F13-03 [MEDIUM] — SupportReadOnly users see raw "null" instead of redaction notice

**Location:** `AuditController.cs:172` (backend), `audit-detail.component.ts:240-248` (frontend)

When the user lacks sensitive data access (e.g., SupportReadOnly role), the backend sets:

```csharp
Payload = includePayload ? payload : default
```

`default(JsonElement)` serializes as JSON `null`. The frontend `formattedPayload()` computed signal runs `JSON.stringify(null, null, 2)` which returns the string `"null"`. The user sees `null` displayed in the payload `<pre>` block with no explanation that the payload has been redacted.

**Impact:** SupportReadOnly users are confused by a payload showing just "null" — they may report it as a bug or assume the event has no data.

**Fix:** Return a sentinel object like `{"redacted": true, "reason": "Insufficient permissions"}` instead of `default(JsonElement)`, or check for null payload on the frontend and display a "Payload redacted — insufficient permissions" message.

---

### F13-04 [MEDIUM] — Detail endpoint uses fetch-then-check instead of ForPortal() for tenant scoping

**Location:** `AuditController.cs:138-155` vs `AuditController.cs:55-56`

The list endpoint uses the `ForPortal()` extension which applies tenant scoping at the query level:
```csharp
var query = _db.AuditEvents.ForPortal(access, legalEntityId);
```

The detail endpoint does not — it fetches the record across all tenants first, then checks access:
```csharp
var auditEvent = await _db.AuditEvents.IgnoreQueryFilters()...
if (!access.CanAccess(auditEvent.LegalEntityId)) return Forbid();
```

While functionally secure (data is never returned to unauthorized users), this pattern is inconsistent with the rest of the codebase and doesn't benefit from tenant-scoped indexes (`ix_audit_type_time` includes `LegalEntityId`).

**Impact:** Inconsistent authorization pattern; misses index-based query optimization; if a future developer copies this pattern to a mutable endpoint, the fetch-then-check approach could enable TOCTOU issues.

**Fix:** Use `ForPortal()` with the user's access context, or at minimum add a `LegalEntityId` filter to the query.

---

### F13-05 [LOW] — All API errors display as "Event not found"

**Location:** `audit-detail.component.ts:290-293`

```typescript
error: () => {
  this.event.set(null);
  this.loading.set(false);
},
```

The error handler discards the error object. Network failures (0), forbidden (403), server errors (500), and not-found (404) all result in `event() === null`, which displays the same "Event not found" empty state. The global `apiInterceptor` handles 401/403/500 with toasts/redirects, but 404 and network errors pass through with no differentiation.

**Impact:** Users cannot distinguish between "this event doesn't exist" and "the server is down" or "you don't have access". Hampers troubleshooting.

**Fix:** Capture the error status and display contextual messages (e.g., "Network error — please retry" vs "Event not found").

---

### F13-06 [LOW] — No retry mechanism after load failure

**Location:** `audit-detail.component.ts:250-257`, template line 51-56

When loading fails, the component shows a static "Event not found" empty state with no retry button. The user must navigate away and return. The route param is read from `snapshot` (not an observable), so even if the user could trigger a retry, the component doesn't support re-loading.

**Impact:** Transient network failures require full page re-navigation to retry.

**Fix:** Add a "Retry" button in the error state that calls `loadEvent()` again with the stored ID.

---

### F13-07 [LOW] — `copyPayload()` silently swallows clipboard errors

**Location:** `audit-detail.component.ts:272-274`

```typescript
copyPayload(): void {
  const text = this.formattedPayload();
  navigator.clipboard.writeText(text).catch(() => {});
}
```

The `catch(() => {})` swallows all errors. If the Clipboard API is unavailable (non-HTTPS in some browsers, iframe restrictions, user denied permission), the copy silently fails with no feedback.

**Impact:** Users click "Copy" and believe the payload is on their clipboard when it isn't.

**Fix:** Show a toast or change the button label to indicate success/failure (`catch(() => messageService.add({severity: 'warn', ...}))`).

---

## Summary

| ID | Severity | Title |
|----|----------|-------|
| F13-01 | HIGH | Backend detail query scans all partitions (no partition key in WHERE) |
| F13-02 | MEDIUM | EventId from DTO may differ from DB Id, causing 404 |
| F13-03 | MEDIUM | SupportReadOnly users see raw "null" instead of redaction notice |
| F13-04 | MEDIUM | Detail endpoint uses fetch-then-check instead of ForPortal() |
| F13-05 | LOW | All API errors display as "Event not found" |
| F13-06 | LOW | No retry mechanism after load failure |
| F13-07 | LOW | copyPayload() silently swallows clipboard errors |

**Total: 7 issues (1 HIGH, 3 MEDIUM, 3 LOW)**
