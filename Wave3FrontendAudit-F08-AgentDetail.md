# Wave 3 — Frontend Audit: F-08 Agent Detail Page

**Component:** `AgentDetailComponent`
**Route:** `/agents/:id`
**File:** `src/portal/src/app/features/edge-agents/agent-detail.component.ts`

---

## 1. Summary

The Agent Detail page displays comprehensive telemetry, connectivity timeline, audit events, and diagnostic logs for a single edge agent. It uses a 30-second auto-refresh cycle via `interval` + `Subject` pattern. Four parallel API calls are made on each refresh: registration, telemetry, events, and diagnostic logs.

---

## 2. API Calls & Backend Endpoint Trace

| # | Frontend Call | Backend Endpoint | Controller | Auth |
|---|-------------|-----------------|------------|------|
| 1 | `agentService.getAgentById(id)` | `GET /api/v1/agents/{id:guid}` | `AgentsController.GetAgentById` | PortalUser |
| 2 | `agentService.getAgentTelemetry(id)` | `GET /api/v1/agents/{id:guid}/telemetry` | `AgentsController.GetTelemetry` | PortalUser |
| 3 | `agentService.getAgentEvents(id, 20)` | `GET /api/v1/agents/{id:guid}/events` | `AgentsController.GetEvents` | PortalUser |
| 4 | `agentService.getAgentDiagnosticLogs(id)` | `GET /api/v1/agents/{deviceId}/diagnostic-logs` | `AgentController.GetDiagnosticLogs` | PortalUser |

---

## 3. Issues Found

### F08-01 · [M] No route guard — any authenticated user can access agent details

**Location:** `edge-agents.routes.ts:19-22`
**Description:** The `:id` route has no `canActivate` guard. The sibling `bootstrap-token` route has `roleGuard(['SystemAdmin'])`, but the detail route is open to all authenticated users. While the backend enforces tenant scoping via `PortalAccessResolver`, the frontend should apply a role guard to avoid unnecessary API calls and to provide immediate UX feedback for unauthorized users.

**Impact:** Users without agent management roles see a "Failed to load" error after the 401/403 round-trip instead of being blocked at the router level.

---

### F08-02 · [M] No validation of route param `id` before API calls

**Location:** `agent-detail.component.ts:688`
**Description:** `this.agentId = this.route.snapshot.paramMap.get('id') ?? '';` — if the route param is missing or empty, the component fires API calls with an empty string. The backend expects `{id:guid}` so this will 404, but it wastes three HTTP calls. If a non-GUID string is passed (e.g. `/agents/abc`), the backend constraint will 404 or 400, but no client-side validation prevents this.

**Recommendation:** Validate that `id` is a non-empty GUID before triggering the refresh cycle. Navigate back to the agent list if invalid.

---

### F08-03 · [M] Diagnostic logs call fires without tenant-scoping check for `legalEntityId`

**Location:** `agent-detail.component.ts:732-738`, `AgentController.cs:606-654`
**Description:** The frontend passes only `id` to `getAgentDiagnosticLogs`. The backend endpoint (`AgentController.GetDiagnosticLogs`) independently looks up the device's `LegalEntityId` and checks `access.CanAccess()`. However, the diagnostic-logs endpoint lives on a **different controller** (`AgentController`) than the other three calls (`AgentsController`). The `AgentController` uses its own `_dbContext` reference that does NOT use the `ForPortal()` extension — it manually queries with `IgnoreQueryFilters()` and checks access. This works but is fragile: any tenant filter changes in `ForPortal` won't propagate here.

---

### F08-04 · [L] Auto-refresh continues when browser tab is hidden

**Location:** `agent-detail.component.ts:719-721`
**Description:** `interval(30_000)` fires regardless of page visibility. When the tab is in the background, this creates unnecessary API traffic (4 calls every 30s indefinitely). Should use `document.visibilityState` or a `switchMap` with a visibility-aware observable to pause polling when hidden.

---

### F08-05 · [L] `forkJoin` silently swallows partial failures

**Location:** `agent-detail.component.ts:695-705`
**Description:** The three main API calls are wrapped in a single `forkJoin` with one `catchError`. If any single call fails (e.g., telemetry 404 because the agent just registered), the entire `forkJoin` is cancelled and the generic error state is shown. This means a newly registered agent with no telemetry yet will show "Failed to load agent data" even though registration data loaded fine.

**Recommendation:** Handle each observable's error independently so that partial data (e.g., registration without telemetry) can be displayed.

---

### F08-06 · [L] Diagnostic logs XSS risk — raw log entries rendered in `<pre>` via `join`

**Location:** `agent-detail.component.ts:432`
**Description:** `{{ batch.logEntries.join('\n') }}` is rendered inside a `<pre>` tag. Angular's default interpolation binding (`{{ }}`) auto-escapes HTML, so this is safe from XSS. However, log entries could contain very long lines or ANSI escape sequences that could cause rendering issues. The `word-break: break-all` CSS mitigates horizontal overflow.

**Status:** Low risk, but worth noting that log content is untrusted edge-agent data. The auto-escaping makes this safe.

---

### F08-07 · [L] `loadDiagnosticLogs()` subscription is not managed by `takeUntilDestroyed`

**Location:** `agent-detail.component.ts:732-738`
**Description:** Unlike the main refresh pipeline which uses `takeUntilDestroyed(this.destroyRef)`, the `loadDiagnosticLogs()` method creates a bare subscription each time it's called. If the user clicks "Refresh Logs" and then navigates away quickly, the subscription completes after the component is destroyed, calling `this.diagnosticLogs.set()` on a destroyed component. This is unlikely to crash (signals work after destroy), but it's inconsistent with the rest of the component's lifecycle management.

---

### F08-08 · [L] No decommission/action buttons — detail page is read-only

**Location:** `agent-detail.component.ts` (entire template)
**Description:** The backend has `POST /api/v1/admin/agent/{deviceId}/decommission` (B-18) but the detail page has no way to trigger decommission or any other write action. This is a feature gap rather than a bug — users must use other means to decommission agents. Worth noting for completeness.

---

### F08-09 · [I] Backend telemetry endpoint redacts sensitive data for non-privileged users, but frontend doesn't indicate redaction

**Location:** `AgentsController.cs:227-244`, `agent-detail.component.ts:207-241`
**Description:** When the user lacks `HasSensitiveDataAccess` roles, the backend replaces `FccHost` with `"***"` and zeros out heartbeat details. The frontend displays `***:0` in the FCC Connection card without any indication that the data is redacted. Users may think the FCC host is literally `***`.

**Recommendation:** Detect the redacted placeholder and show a "Restricted" badge or hide the FCC Connection card for non-privileged users.

---

## 4. Positive Observations

- **Proper tenant scoping:** Backend uses `ForPortal(access)` for registration, telemetry, and events queries; diagnostic-logs endpoint has manual `CanAccess()` check.
- **Keyset pagination on events:** Uses cursor-based pagination for events query (M-18 fix already applied).
- **Skeleton loading states:** Clean loading skeleton while data is being fetched.
- **Connectivity timeline:** Well-implemented computed signal that filters `CONNECTIVITY_STATE_CHANGED` events.
- **Responsive grid layout:** `auto-fill` grid adapts well to different screen widths.
- **Signal-based state management:** Modern Angular signals pattern throughout.

---

## 5. Issue Severity Summary

| Severity | Count |
|----------|-------|
| Medium | 3 |
| Low | 5 |
| Info | 1 |
| **Total** | **9** |
