# F-06 Agent List Page — Audit Report

**Page:** `/agents` — `AgentListComponent`
**Audited:** 2026-03-13
**Status:** Issues Found

---

## 1. File Inventory

| File | Purpose |
|------|---------|
| `src/portal/src/app/features/edge-agents/agent-list.component.ts` | Agent monitoring list — legal entity selector, offline/online split tables, auto-refresh |
| `src/portal/src/app/features/edge-agents/edge-agents.routes.ts` | Route definitions for `/agents`, `/agents/bootstrap-token`, `/agents/:id` |
| `src/portal/src/app/core/services/agent.service.ts` | HTTP service — `getAgents()`, `getAgentById()`, `getAgentTelemetry()`, `getAgentEvents()` |
| `src/portal/src/app/core/services/http-params.util.ts` | Utility: `buildHttpParams()` — converts object to HttpParams, skipping null/undefined |
| `src/portal/src/app/core/models/agent.model.ts` | TypeScript interfaces — `AgentHealthSummary`, `ConnectivityState`, `AgentRegistrationStatus` |
| `src/portal/src/app/core/services/master-data.service.ts` | `getLegalEntities()` — called to populate legal entity selector |
| `src/cloud/FccMiddleware.Api/Controllers/AgentsController.cs` | Backend controller — `GetAgents` endpoint |

---

## 2. Routing & Auth

- Route defined in `edge-agents.routes.ts` line 5–9: `path: ''` (maps to `/agents`), lazy-loads `AgentListComponent`.
- **No `canActivate` guard on the agent list route.** The route has no `roleGuard`. Compare with `bootstrap-token` route (line 16) which has `roleGuard(['SystemAdmin'])`, and other pages (reconciliation, transactions, etc.) which all have `roleGuard`.
- Backend: `AgentsController` has `[Authorize(Policy = "PortalUser")]` at class level — requires `OperationsManager`, `SystemAdmin`, `SystemAdministrator`, `Auditor`, `SiteSupervisor`, or `SupportReadOnly`.
- The backend policy includes `SupportReadOnly` role. Even without a frontend guard, the MSAL auth guard at the app level should prevent unauthenticated access. However, any authenticated portal user can reach this route regardless of role — the backend will still enforce access.

**Finding (see F06-01):** Missing `roleGuard` on agent list route — inconsistent with all other feature routes.

---

## 3. UI Logic Review

### 3a. Component Structure

- Standalone component using Angular signals and computed signals.
- **Legal entity selector** at top-right, populated from `masterDataService.getLegalEntities()`.
- Two tables: "Offline / Unreachable Agents" (highlighted section) and "All Agents" (the remaining online agents).
- Agent split: `isAgentOffline()` checks `connectivityState === FULLY_OFFLINE`, `lastSeenAt` is null, or `Date.now() - lastSeenAt > 5 minutes`.
- "Generate Token" button visible only to `SystemAdmin` role (via `isAdmin()` computed).
- Clicking a row navigates to `/agents/:deviceId` for detail view.

### 3b. Data Loading

- Constructor sets up `refresh$` Subject → `switchMap` → `agentService.getAgents({ legalEntityId, pageSize: 500 })`.
- Uses `switchMap` so new requests cancel in-flight ones — correct.
- On legal entity change: clears agent list, triggers refresh.
- **Auto-refresh:** `interval(30_000)` fires `refresh$.next()` every 30 seconds — even if no legal entity is selected (the inner `switchMap` returns `EMPTY` in that case).

### 3c. Filtering

- Client-side filtering via `activeFilters` signal:
  - `siteCode` — case-insensitive substring match.
  - `connectivityState` — exact enum match, `null` means "all".
- Filters are applied via `computed` signals (`allFiltered`, `filteredOffline`, `filteredOnline`).
- `onFiltersChange()` copies `this.filters` (mutable object) into `activeFilters` signal — this triggers recomputation.

### 3d. Offline/Online Split

- `filteredOffline` = agents where `isAgentOffline()` returns true.
- `filteredOnline` = agents where `isAgentOffline()` returns false.
- **Important:** The "All Agents" table (lines 246–316) only shows `filteredOnline()` — not truly "all agents". The header says "All Agents" but the data source is `filteredOnline()`. An agent that is offline appears ONLY in the offline section, not in "All Agents".

### 3e. Admin Check

- `isAdmin` computed checks `hasAnyRequiredRole(account, ['SystemAdmin'])` — only `SystemAdmin`, not `SystemAdministrator`.
- The "Generate Token" button is hidden from `SystemAdministrator` users.
- Backend `bootstrap-token` route guard also only allows `SystemAdmin` (routes line 16), so this is consistent. However, if `SystemAdministrator` was intended to be equivalent to `SystemAdmin`, this could be a gap.

---

## 4. Validation Review

### 4a. Page Size

- Frontend sends `pageSize: 500` to `getAgents()` (line 520).
- Backend validates `pageSize` is 1–500 (controller line 42). Frontend value is at the maximum allowed.
- This fetches ALL agents for a legal entity in a single request (assuming ≤500 agents). For entities with 500+ agents, only the first page is fetched and no pagination is implemented.

### 4b. Filter Input

- Site code filter is free-text input (`pInputText`). No sanitization, but it's only used for client-side `includes()` filtering — never sent to the backend. Safe.
- Connectivity state filter is a dropdown with fixed options — safe.

### 4c. Auto-Refresh Behavior

- `interval(30_000)` runs continuously even when the tab is in the background. Modern browsers may throttle `setInterval` in background tabs, but the requests will still fire (just delayed).
- No mechanism to pause auto-refresh when the page is not visible (e.g., `document.hidden` check or `visibilitychange` event).

---

## 5. API Call Review

### 5a. AgentService.getAgents()

- Calls `GET /api/v1/agents` with params built by `buildHttpParams()`.
- Frontend sends: `{ legalEntityId: string, pageSize: 500 }`.
- The `connectivityState` filter from the UI is applied client-side only — NOT sent to the backend. The backend does support `connectivityState` as a query param (controller line 39).
- The `siteCode` filter is also client-side only — backend supports `siteCode` param (controller line 37).

### 5b. Response Mapping

| Backend DTO (AgentHealthSummaryDto) | Frontend model (AgentHealthSummary) | Match? |
|---|---|---|
| `DeviceId` (Guid) | `deviceId` (string) | Match |
| `SiteCode` (string) | `siteCode` (string) | Match |
| `SiteName` (string) | `siteName` (string \| null) | Backend reads `agent.Site.SiteName` — could be null if `Include(Site)` doesn't resolve; frontend allows null — safe |
| `LegalEntityId` (Guid) | `legalEntityId` (string) | Match |
| `AgentVersion` (string) | `agentVersion` (string) | Match |
| `Status` (string) | `status` (AgentRegistrationStatus) | Backend returns "ACTIVE"/"DEACTIVATED"; frontend enum matches — correct |
| `ConnectivityState` (string?) | `connectivityState` (ConnectivityState \| null) | Backend returns `.ToString()` of enum or null if no snapshot — correct |
| `BatteryPercent` (int?) | `batteryPercent` (number \| null) | Match |
| `IsCharging` (bool?) | `isCharging` (boolean \| null) | Match |
| `BufferDepth` (int?) | `bufferDepth` (number \| null) | Backend maps from `PendingUploadCount` — correct |
| `SyncLagSeconds` (int?) | `syncLagSeconds` (number \| null) | Match |
| `LastTelemetryAt` (DateTimeOffset?) | `lastTelemetryAt` (string \| null) | Match |
| `LastSeenAt` (DateTimeOffset?) | `lastSeenAt` (string \| null) | Match |

All fields match correctly.

### 5c. Legal Entities Loading

- `masterDataService.getLegalEntities()` calls `GET /api/v1/master-data/legal-entities` — returns a simple array (not paged).
- No error handling on this call (line 508–511). If it fails, `legalEntities` remains `[]` and the selector is empty with no indication.

---

## 6. Backend Endpoint Trace

### 6a. GET /api/v1/agents

**Controller:** `AgentsController.GetAgents()` (line 33)
**Auth:** `[Authorize(Policy = "PortalUser")]` + `PortalAccessResolver` legal entity scoping.

**Flow:**
1. Validates `pageSize` is 1–500.
2. Resolves portal access. Returns 401 if invalid.
3. Checks `access.CanAccess(legalEntityId)` — returns 403 if user cannot access the legal entity.
4. Parses optional `status` and `connectivityState` filters.
5. Builds query: `AgentRegistrations.ForPortal(access, legalEntityId).Include(Site)`.
6. Applies optional `siteCode` exact match filter (not substring — unlike frontend client-side which does substring).
7. Applies cursor-based pagination: `RegisteredAt > cursorTimestamp || (RegisteredAt == cursorTimestamp && Id > cursorId)`.
8. If `connectivityState` provided: subquery join with `AgentTelemetrySnapshots` to filter agents by connectivity.
9. Orders by `RegisteredAt ASC, Id ASC`, takes `pageSize + 1` to detect `hasMore`.
10. Runs `CountAsync()` for `totalCount` — full count on every request.
11. Fetches telemetry snapshots for the page by `DeviceId` for battery, connectivity, buffer, sync data.
12. Maps to `AgentHealthSummaryDto` and returns paged result.

**Performance note:** `CountAsync()` runs on every request (line 104). With pagination and filters, this could be slow for large datasets.

**Note on `agent.Site.SiteName` (line 130):** The query uses `.Include(agent => agent.Site)`. If the `Site` navigation property is null (e.g., site was deleted), `agent.Site.SiteName` would throw a `NullReferenceException`. However, since agents must be registered to a site, this is unlikely.

---

## 7. Issues Found

### F06-01 — Missing roleGuard on agent list route (Medium)

**Location:** `edge-agents.routes.ts` lines 5–9
**Problem:** The agent list route has no `canActivate: [roleGuard(...)]`, unlike every other feature route in the portal. The `bootstrap-token` sub-route (line 16) and the `:id` detail route also lack guards. While the backend enforces the `PortalUser` policy, the missing frontend guard means:
1. Any authenticated user (including roles not in the `PortalUser` policy) can navigate to `/agents` and attempt API calls — they'll get 401/403 from the backend, but will see the page skeleton with an error state.
2. Inconsistent with the rest of the codebase.
**Impact:** Users without portal roles see a broken page instead of being redirected. Minor security exposure since backend enforces auth.
**Fix:** Add `canActivate: [roleGuard(['SystemAdmin', 'SystemAdministrator', 'OperationsManager', 'SiteSupervisor', 'Auditor', 'SupportReadOnly'])]` to the agent list and detail routes.

### F06-02 — Hard-coded pageSize 500 with no pagination for large agent fleets (Medium)

**Location:** `agent-list.component.ts` line 520
**Problem:** `agentService.getAgents({ legalEntityId: entityId, pageSize: 500 })` fetches at most 500 agents. The component does not check `result.meta.hasMore` and does not implement cursor-based pagination. If a legal entity has more than 500 registered agents, the remaining ones are silently invisible.
**Impact:** Large deployments (500+ agents per legal entity) would have missing agents in the monitoring view — critical for an operations monitoring page.
**Fix:** Either implement pagination/virtual scrolling, or increase the limit and add a warning when `hasMore` is true.

### F06-03 — "All Agents" table only shows online agents — misleading header (Low)

**Location:** `agent-list.component.ts` line 256 (data source is `filteredOnline()`) and line 250 (header says "All Agents")
**Problem:** The "All Agents" card header is misleading — the table data source is `filteredOnline()`, which excludes offline agents. Offline agents appear only in the separate "Offline / Unreachable" section above. A user searching for a specific agent in the "All Agents" table won't find it if it's offline.
**Impact:** UX confusion — operators may think an agent doesn't exist when it's actually in the offline section.
**Fix:** Either rename the header to "Online Agents" or include all agents in this table (with connectivity badge indicating status).

### F06-04 — Filters applied client-side only, not sent to backend (Low)

**Location:** `agent-list.component.ts` lines 485–491, 520
**Problem:** The `siteCode` and `connectivityState` filters are applied client-side on the already-fetched 500 agents. The backend supports both `siteCode` and `connectivityState` as query params. Since the frontend already fetches up to 500 agents in one call, client-side filtering works — but it's inefficient and becomes incorrect if there are 500+ agents (F06-02). Filtered agents that fell outside the 500 limit won't appear.
**Impact:** Combined with F06-02, filtering could miss agents beyond the 500 limit. For ≤500 agents, behavior is correct.
**Fix:** If implementing server-side pagination (fixing F06-02), also pass filters to the backend.

### F06-05 — Auto-refresh fires in background tabs with no visibility check (Low)

**Location:** `agent-list.component.ts` lines 536–538
**Problem:** `interval(30_000)` fires continuously regardless of tab visibility. This generates unnecessary network traffic when the user has switched to another tab. Modern browsers throttle background timers but don't stop them entirely.
**Impact:** Unnecessary API calls. For 30-second intervals this is minor, but could add up across many open portal sessions.
**Fix:** Check `document.hidden` before firing the refresh, or use `visibilitychange` event to pause/resume the interval.

### F06-06 — No error handling for legal entity load failure (Low)

**Location:** `agent-list.component.ts` lines 508–511
**Problem:** `masterDataService.getLegalEntities()` subscription has no error handler. If the API call fails, the `legalEntities` signal stays as `[]` and the legal entity dropdown is empty with no indication of failure.
**Impact:** User sees an empty dropdown with no way to know it's an error vs. genuinely having no legal entities. They cannot load any agents.
**Fix:** Add `error` handler that sets an error flag, or show a toast notification.

### F06-07 — isAdmin check omits SystemAdministrator role (Low)

**Location:** `agent-list.component.ts` lines 463–466
**Problem:** `isAdmin` computed checks only `['SystemAdmin']`, not `['SystemAdmin', 'SystemAdministrator']`. The "Generate Token" button is hidden from `SystemAdministrator` users. The bootstrap-token route guard (routes line 16) also only allows `SystemAdmin`, so this is internally consistent. However, if `SystemAdministrator` should be equivalent to `SystemAdmin` for token generation, this is a gap.
**Impact:** `SystemAdministrator` users cannot navigate to the bootstrap token page from the agent list. They could still navigate directly via URL, but would be blocked by the route guard.
**Fix:** If `SystemAdministrator` should have token generation access, add it to both the `isAdmin` check and the route guard.

### F06-08 — Backend siteCode filter uses exact match; frontend filter uses substring (Low)

**Location:** Backend: `AgentsController.cs` line 74 (`agent.SiteCode == siteCode`); Frontend: `agent-list.component.ts` line 488 (`a.siteCode.toLowerCase().includes(f.siteCode.toLowerCase())`)
**Problem:** If server-side filtering were enabled, the frontend sends a substring to a backend that does exact match. Currently not an issue because the filter is client-side only, but would become a mismatch if filters are moved to the backend (per F06-04 fix).
**Impact:** No current impact — the filter is client-side. Potential future inconsistency.
**Fix:** When implementing server-side filtering, either change backend to use `LIKE`/`Contains` or change frontend to exact match with a dropdown.

---

## 8. Summary

| ID | Severity | Category | Description |
|----|----------|----------|-------------|
| F06-01 | Medium | Auth | Missing `roleGuard` on agent list route — inconsistent with all other routes |
| F06-02 | Medium | Data Gap | Hard-coded pageSize 500 with no pagination — agents beyond 500 are invisible |
| F06-03 | Low | UX | "All Agents" table header misleading — only shows online agents |
| F06-04 | Low | Performance | Filters applied client-side only — incorrect if 500+ agents |
| F06-05 | Low | Performance | Auto-refresh fires in background tabs with no visibility check |
| F06-06 | Low | UX | No error handling for legal entity load failure |
| F06-07 | Low | Auth | `isAdmin` check omits `SystemAdministrator` role |
| F06-08 | Low | Consistency | Backend siteCode uses exact match; frontend uses substring |

**Total: 8 issues (2 Medium, 6 Low)**
