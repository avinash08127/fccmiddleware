# F-01 Dashboard Page — Audit Report

**Page:** `/dashboard` — `DashboardComponent`
**Audited:** 2026-03-13
**Status:** Issues Found

---

## 1. File Inventory

| File | Purpose |
|------|---------|
| `src/portal/src/app/features/dashboard/dashboard.component.ts` | Main dashboard page — toolbar + widget grid |
| `src/portal/src/app/features/dashboard/dashboard.service.ts` | HTTP service — calls summary + alerts endpoints |
| `src/portal/src/app/features/dashboard/dashboard.model.ts` | TypeScript interfaces for all dashboard DTOs |
| `src/portal/src/app/features/dashboard/dashboard.routes.ts` | Lazy route definition |
| `src/portal/src/app/features/dashboard/dashboard.component.spec.ts` | Unit tests (7 specs) |
| `src/portal/src/app/features/dashboard/components/transaction-volume-chart/transaction-volume-chart.component.ts` | Line chart widget — 24h transaction volume by source |
| `src/portal/src/app/features/dashboard/components/ingestion-health/ingestion-health.component.ts` | Health metrics widget — TPM, success/error rate, p95, DLQ depth |
| `src/portal/src/app/features/dashboard/components/agent-status-summary/agent-status-summary.component.ts` | Agent online/degraded/offline counts + offline agent list |
| `src/portal/src/app/features/dashboard/components/reconciliation-summary/reconciliation-summary.component.ts` | Reconciliation flagged/pending/auto-approved counts |
| `src/portal/src/app/features/dashboard/components/stale-transactions/stale-transactions.component.ts` | Stale transaction count with trend indicator |
| `src/portal/src/app/features/dashboard/components/active-alerts/active-alerts.component.ts` | Alert list with severity coloring |

---

## 2. Routing & Auth

- Route: `app.routes.ts` line 26 — `path: 'dashboard'`, lazy-loaded child of `ShellComponent`.
- Guard: `MsalGuard` on the parent route (line 22). No additional role guard on this route.
- Backend auth: `[Authorize(Policy = "PortalUser")]` on `AdminDashboardController`.
- Legal entity scoping: `PortalAccessResolver` enforces scoping per user claims. If `legalEntityId` is provided and user cannot access it, returns `403 Forbid`.

**Finding:** No issues with auth. MsalGuard protects the route, backend enforces PortalUser policy + legal entity scoping.

---

## 3. UI Logic Review

### 3a. DashboardComponent (main page)

- Uses Angular signals for reactive state management.
- On construction: loads legal entities for the filter dropdown, triggers `loadAll()`, starts 60-second auto-refresh interval.
- `onLegalEntityChange()` sets the selected entity and triggers `loadAll()`.
- `refreshing` computed signal = `summaryLoading || alertsLoading`.
- `lastRefreshedAt` is only updated on summary success, not on alerts success.

### 3b. Sub-widgets

All 6 sub-widgets follow a consistent pattern:
- Accept `data`, `loading`, `error` as `@Input()` properties.
- Show loading spinner, error state, empty state, or data.
- Pure display components — no direct API calls.

### 3c. TransactionVolumeChartComponent

- Builds a Chart.js line chart with 3 datasets (FCC_PUSH, EDGE_UPLOAD, CLOUD_PULL).
- Uses `toLocaleTimeString()` for labels — **timezone-dependent**, will show user's local time while data is UTC-bucketed. This is acceptable UX but could confuse users comparing across timezones.

### 3d. AgentStatusSummaryComponent

- Shows online/degraded/offline counts with color pills.
- Offline agent list capped to 5 items with a "view all" link to `/agents`.
- `stateLabel()` maps `ConnectivityState` enum to human-readable strings.

### 3e. ReconciliationSummaryComponent

- "Review exceptions" link navigates to `/reconciliation` (should be `/reconciliation/exceptions` based on tracker route F-04).

### 3f. StaleTransactionsComponent

- "View stale transactions" link passes `queryParams: { status: 'PENDING', stale: 'true' }` to `/transactions`.
- Trend arrow: up = red, down = green, stable = gray.

### 3g. ActiveAlertsComponent

- `typeSeverity()` maps alert type to PrimeNG tag severity.
- `severityIcon()` maps alert severity to PrimeNG icon class.
- No pagination — displays all alerts from the response.

---

## 4. Validations Review

- **Frontend:** No user input validation needed — the only input is the legal entity dropdown which passes a value from the server-provided list. `showClear` allows deselecting.
- **Backend:** `legalEntityId` is a `Guid?` query param — ASP.NET model binding handles type validation. Invalid GUIDs will return a 400 automatically.

**No issues found.**

---

## 5. API Calls

| # | Frontend Call | Backend Endpoint | Method | Auth |
|---|-------------|------------------|--------|------|
| 1 | `DashboardService.getSummary()` | `GET /api/v1/admin/dashboard/summary?legalEntityId={id}` | GET | PortalUser |
| 2 | `DashboardService.getAlerts()` | `GET /api/v1/admin/dashboard/alerts?legalEntityId={id}` | GET | PortalUser |
| 3 | `MasterDataService.getLegalEntities()` | `GET /api/v1/master-data/legal-entities` | GET | PortalUser |

---

## 6. Backend Endpoint Trace

### GET /api/v1/admin/dashboard/summary

**Controller:** `AdminDashboardController.GetSummary()` (`AdminDashboardController.cs:30-185`)

**Logic:**
1. Resolves portal access via `PortalAccessResolver`.
2. Loads system settings for stale threshold.
3. Loads ALL transactions from the last 24 hours into memory (`.ToListAsync()`).
4. Loads ALL active agents into memory.
5. Loads ALL telemetry snapshots into memory.
6. Loads ALL non-resolved dead-letter items into memory.
7. Loads ALL reconciliation records into memory.
8. Computes transaction volume buckets, health metrics, agent status, reconciliation summary, and stale counts in-memory.

### GET /api/v1/admin/dashboard/alerts

**Controller:** `AdminDashboardController.GetAlerts()` (`AdminDashboardController.cs:187-296`)

**Logic:**
1. Resolves portal access and loads settings/thresholds.
2. Loads active agents, checks offline threshold.
3. Counts DLQ items, stale transactions, reconciliation exceptions.
4. Builds alert list based on threshold checks.

---

## 7. Issues Found

### BUG-F01-1: Massive in-memory data loading in GetSummary (Severity: HIGH)

**File:** `AdminDashboardController.cs:59-78`

The summary endpoint loads entire result sets into memory:
- All 24h transactions (`ToListAsync`)
- All active agents (`ToListAsync`)
- All telemetry snapshots (`ToListAsync`) — no time filter!
- All non-resolved DLQ items (`ToListAsync`)
- All reconciliation records (`ToListAsync`) — no time filter!

At scale, this will cause severe memory pressure and slow responses. Telemetry snapshots and reconciliation records are unbounded — they grow indefinitely.

**Impact:** OOM risk, slow dashboard loads, 60-second auto-refresh amplifies the problem.

**Recommendation:** Push aggregation to SQL (GROUP BY hour, COUNT, etc.) instead of loading rows into memory. Add time filters to telemetry and reconciliation queries.

---

### BUG-F01-2: LatencyP95Ms is always hardcoded to 0 (Severity: MEDIUM)

**File:** `AdminDashboardController.cs:156`

```csharp
LatencyP95Ms = 0,
```

The ingestion health widget displays p95 latency, but the backend always returns 0. The frontend renders "0ms" which is misleading — it implies zero latency rather than "not measured."

**Recommendation:** Either compute from stored processing times, or remove the metric from the UI until it's implemented. At minimum, return `null` and show "N/A" in the frontend.

---

### BUG-F01-3: Reconciliation summary link goes to wrong route (Severity: LOW)

**File:** `reconciliation-summary.component.ts:45`

```html
<a routerLink="/reconciliation" class="action-link">
```

Per the app routing, the reconciliation list is at `/reconciliation/exceptions`, not `/reconciliation`. This link will likely 404 or redirect unexpectedly.

**Recommendation:** Change to `routerLink="/reconciliation/exceptions"`.

---

### BUG-F01-4: SuccessRate/ErrorRate calculation uses DLQ count not scoped to time window (Severity: MEDIUM)

**File:** `AdminDashboardController.cs:150-155`

`recentTransactions` is filtered to the last 15 minutes, but `deadLetters` is all non-resolved DLQ items regardless of when they were created. This mixes a 15-minute transaction count with the total DLQ backlog, producing inaccurate success/error rates.

For example: 10 recent transactions + 100 old DLQ items = 9.1% success rate, which is misleading when the actual recent error rate may be 0%.

**Recommendation:** Filter DLQ items to the same 15-minute health window.

---

### BUG-F01-5: No unsubscribe protection on rapid legal entity filter changes (Severity: LOW)

**File:** `dashboard.component.ts:267-275`

`onLegalEntityChange()` calls `loadAll()` which fires two HTTP requests. If the user rapidly switches the legal entity filter, previous in-flight requests are not cancelled. The responses could arrive out-of-order, causing the dashboard to display data for a previously-selected entity.

**Recommendation:** Use `switchMap` or cancel previous subscriptions when the filter changes.

---

### BUG-F01-6: Auto-refresh fires even when tab is backgrounded (Severity: LOW)

**File:** `dashboard.component.ts:262-264`

The 60-second `interval` runs regardless of tab visibility. On a backgrounded tab, this generates unnecessary API traffic and loads server resources.

**Recommendation:** Use `document.visibilityState` or a visibility-aware timer to pause polling when the tab is hidden.

---

### BUG-F01-7: StaleTransactions threshold uses days * 24 * 60 but previous window doubles it (Severity: LOW)

**File:** `AdminDashboardController.cs:52-57`

```csharp
var staleThresholdMinutes = settings.GlobalDefaults.Tolerance.StalePendingThresholdDays * 24 * 60;
var staleCutoff = now.AddMinutes(-staleThresholdMinutes);
var previousStaleWindowStart = staleCutoff.AddMinutes(-staleThresholdMinutes);
```

The "previous" window goes back 2x the stale threshold (e.g. if threshold is 7 days, previous window starts 14 days ago). This compares "currently stale" to "transactions that were stale 7-14 days ago" which is a misleading trend comparison — it compares different time window sizes.

**Recommendation:** Use a fixed comparison window (e.g., previous equal-length period) or compare same-time-yesterday snapshots.

---

## 8. Test Coverage Assessment

**File:** `dashboard.component.spec.ts` — 7 tests

| Test | What It Covers |
|------|----------------|
| should create the dashboard component | Basic instantiation |
| should show loading spinners while data is loading | Loading state |
| should render all 6 widgets after data loads | Widget presence |
| should pass summary data to child widgets | Data binding |
| should display alerts after load | Alerts binding |
| should reload data when legal entity filter changes | Filter interaction + URL params |
| should show error state when summary request fails | Error handling |
| should auto-refresh every 60 seconds | Auto-refresh interval |

**Gaps:**
- No tests for sub-widget components (chart rendering, metric thresholds, alert severity styling).
- No test for clearing the legal entity filter (back to "All").
- No test for concurrent alert + summary error states.

---

## 9. Summary

| Severity | Count | IDs |
|----------|-------|-----|
| HIGH | 1 | BUG-F01-1 |
| MEDIUM | 2 | BUG-F01-2, BUG-F01-4 |
| LOW | 4 | BUG-F01-3, BUG-F01-5, BUG-F01-6, BUG-F01-7 |
| **Total** | **7** | |
