# Angular Portal — Phased Development Plan

**Agent System Prompt:** Always prepend `docs/plans/agent-prompt-angular-portal.md` when assigning any task below.

**Sprint Cadence:** 2-week sprints

---

## Phase 0 — Foundations (Sprints 1–2)

### AP-0.1: Angular Project Scaffold

**Sprint:** 1
**Prereqs:** None
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-1-project-scaffolding.md` — §5.3 (Angular Portal section, the definitive scaffold spec)
- `WIP-HLD-Angular-Portal.md` — §5.2 (Project Structure)
- `docs/specs/foundation/coding-conventions.md` — shared conventions

**Task:**
Create the Angular project with full structure, auth, interceptors, and design system baseline.

**Detailed instructions:**
1. Create project: `ng new fcc-admin-portal --style=scss --routing --standalone`
2. Install dependencies:
   - `ng add primeng` + `@primeng/themes` (PrimeNG)
   - `npm install @azure/msal-browser @azure/msal-angular` (Azure Entra auth)
   - `ng add @angular-eslint/schematics` + Prettier config
3. Configure routing in `app.routes.ts`:
   - Top-level routes with `canActivate: [MsalGuard]` on all feature routes
   - Lazy-load each feature via `loadChildren`
   - Routes: `/dashboard`, `/transactions`, `/reconciliation`, `/agents`, `/sites`, `/master-data`, `/audit`, `/dlq`, `/settings`
4. Create auth infrastructure:
   - `core/auth/auth.config.ts` — MSAL configuration from environment files
   - `core/auth/auth.guard.ts` — wraps `MsalGuard`, redirects to Entra login
   - `core/auth/role.guard.ts` — reads `roles` claim from JWT, blocks unauthorized navigation
5. Create HTTP interceptors:
   - `core/interceptors/auth.interceptor.ts` — attaches Azure Entra JWT via `MsalInterceptor`
   - `core/interceptors/api.interceptor.ts` — prepends `environment.apiBaseUrl`, handles 401 (silent refresh), 403 (access denied), 5xx (toast notification)
6. Create shell layout:
   - `core/layout/shell.component.ts` — sidebar navigation + header (with user info + legal entity selector) + `<router-outlet>`
   - Sidebar links to all 9 feature modules
   - Header shows logged-in user name, role, and active legal entity
7. Create shared stub components:
   - `shared/components/data-table/` — wrapper around PrimeNG Table with default pagination config
   - `shared/components/status-badge/` — colored badge for transaction/reconciliation/connectivity statuses
   - `shared/components/date-range-picker/` — date range selector for filtering
   - `shared/components/loading-spinner/` — standard loading indicator
   - `shared/components/empty-state/` — "no data" placeholder
8. Create shared pipes:
   - `shared/pipes/currency-minor-units.pipe.ts` — converts `12345` to `"123.45"` with currency symbol
   - `shared/pipes/utc-date.pipe.ts` — converts UTC ISO 8601 to user's local timezone display
   - `shared/pipes/status-label.pipe.ts` — maps enum values to display labels
9. Create shared directives:
   - `shared/directives/role-visible.directive.ts` — shows/hides elements based on user role
10. Create environment files:
    - `environment.ts` (dev) — `apiBaseUrl`, `msalClientId`, `msalAuthority`, `msalRedirectUri`
    - `environment.staging.ts`
    - `environment.prod.ts`
11. Apply PrimeNG Lara Light theme with custom SCSS variables in `styles/_variables.scss`

**Acceptance criteria:**
- `ng build` succeeds with zero errors
- `ng serve` loads the app shell with sidebar, header, router outlet
- MSAL configuration present (auth flow works if Entra app registration exists)
- Auth guard redirects unauthenticated users
- API interceptor attaches base URL
- PrimeNG theme renders correctly
- All shared components render (even if minimal content)
- Lazy-loaded routes configured for all 9 features

---

### AP-0.2: TypeScript API Models

**Sprint:** 2
**Prereqs:** AP-0.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — all endpoint request/response types
- `schemas/canonical/canonical-transaction.schema.json` — transaction model
- `schemas/canonical/pre-auth-record.schema.json` — pre-auth model
- `schemas/canonical/telemetry-payload.schema.json` — telemetry model
- `schemas/events/event-envelope.schema.json` — audit event model
- `schemas/config/site-config.schema.json` — site configuration model

**Task:**
Create TypeScript interfaces matching all API DTOs.

**Detailed instructions:**
1. Create interfaces in `core/models/`:
   - `transaction.model.ts` — `Transaction`, `TransactionQueryParams`, `TransactionDetail`
   - `pre-auth.model.ts` — `PreAuthRecord`, `PreAuthQueryParams`
   - `reconciliation.model.ts` — `ReconciliationRecord`, `ReconciliationException`, `ApproveRejectRequest`
   - `agent.model.ts` — `AgentRegistration`, `AgentTelemetry`, `AgentHealthSummary`
   - `site.model.ts` — `Site`, `FccConfig`, `Pump`, `Product`, `Operator`
   - `master-data.model.ts` — `LegalEntity`, `MasterDataSyncStatus`
   - `audit.model.ts` — `AuditEvent`, `AuditEventQueryParams`
   - `common.model.ts` — `PagedResult<T>`, `ErrorResponse`, `StatusBadge` mapping
2. All money fields: `number` (minor units — display pipe handles formatting)
3. All date fields: `string` (ISO 8601 UTC — display pipe handles timezone)
4. All IDs: `string` (UUID)
5. Enum types: TypeScript string enums matching backend values

**Acceptance criteria:**
- All interfaces match the OpenAPI spec response types
- Shared `PagedResult<T>` for paginated responses
- Enums match backend enum values exactly
- Models importable from feature modules

---

### AP-0.3: Core API Services

**Sprint:** 2
**Prereqs:** AP-0.1, AP-0.2
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — all endpoints

**Task:**
Create base API service classes for each backend resource.

**Detailed instructions:**
1. Create services in `core/services/`:
   - `transaction.service.ts` — `getTransactions(params)`, `getTransactionById(id)`, `acknowledgeTransactions(batch)`
   - `reconciliation.service.ts` — `getExceptions(params)`, `approve(id, reason)`, `reject(id, reason)`
   - `agent.service.ts` — `getAgents(params)`, `getAgentById(id)`, `getAgentTelemetry(id)`
   - `site.service.ts` — `getSites(params)`, `getSiteById(id)`, `updateFccConfig(siteId, config)`
   - `master-data.service.ts` — `getSyncStatus()`, `getLegalEntities()`
   - `audit.service.ts` — `getAuditEvents(params)`
   - `dlq.service.ts` — `getDeadLetters(params)`, `retry(id)`, `discard(id, reason)`
   - `settings.service.ts` — `getSettings()`, `updateSettings(settings)`
2. All services use `inject(HttpClient)` pattern
3. All methods return `Observable<T>` with correct TypeScript types
4. Error handling delegated to API interceptor (services don't catch errors)

**Acceptance criteria:**
- All services created with method signatures matching API spec
- Services use inject() pattern (Angular 18+)
- Return types match TypeScript model interfaces
- Services injectable from any feature module

---

### AP-0.4: CI Pipeline Setup

**Sprint:** 2
**Prereqs:** AP-0.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-2-repo-branching-and-cicd.md` — CI/CD spec for Angular Portal

**Task:**
Create the CI pipeline.

**Detailed instructions:**
1. Create `.github/workflows/ci.yml`:
   - Trigger: push to `main`, PRs targeting `main`
   - Steps: checkout → setup Node 20 → npm ci → lint → `ng build --configuration=production` → unit tests (`ng test --watch=false`)
2. Add lint configuration (ESLint + Prettier)

**Acceptance criteria:**
- CI passes on clean checkout
- Production build succeeds
- Unit tests run
- Lint passes

---

## Phase 5 — Angular Portal Features (Sprints 8–11)

### AP-5.1: Authentication — Azure Entra Integration

**Sprint:** 8
**Prereqs:** AP-0.1 (Entra app registration must be ready)
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.2 Portal auth (Azure Entra JWT, roles)
- `WIP-HLD-Angular-Portal.md` — §7 (Authentication & Authorization)

**Task:**
Complete the Azure Entra authentication integration.

**Detailed instructions:**
1. Configure MSAL in `auth.config.ts`:
   - `clientId` from Entra app registration
   - `authority`: `https://login.microsoftonline.com/{tenantId}`
   - `redirectUri`: environment-specific
   - `scopes`: `["api://{clientId}/.default"]`
2. Implement `MsalGuard` on all feature routes
3. Implement `MsalInterceptor` to attach bearer token to API calls
4. Implement role extraction from JWT `roles` claim
5. Create `AuthService`:
   - `getCurrentUser()` — returns user info from token
   - `getUserRoles()` — returns `string[]` of roles
   - `hasRole(role: string)` — boolean check
   - `getAccessToken()` — for manual API calls
6. Implement `RoleGuard`:
   - Each route specifies required roles in route data
   - Guard checks user has at least one required role
   - Unauthorized → redirect to "Access Denied" page
7. Implement `RoleVisibleDirective`:
   - `*roleVisible="['OperationsManager', 'SystemAdmin']"` — shows element only for listed roles
8. Create "Access Denied" page and "Login" page

**Acceptance criteria:**
- Unauthenticated users redirected to Entra login
- Successful login returns JWT with roles
- Role-based route access enforced
- Token attached to all API calls
- Silent token refresh on expiry
- Role-based element visibility works
- Logout clears session

---

### AP-5.2: Dashboard

**Sprint:** 8–9
**Prereqs:** AP-0.3, AP-5.1
**Estimated effort:** 3–4 days

**Read these artifacts before starting:**
- `WIP-HLD-Angular-Portal.md` — §3.1.1 (Dashboard feature spec, wireframe guidance)
- `schemas/openapi/cloud-api.yaml` — dashboard-related endpoints

**Task:**
Build the operational dashboard.

**Detailed instructions:**
1. Create `features/dashboard/` with standalone components:
   - `dashboard.component.ts` — layout grid
   - `transaction-volume-chart.component.ts` — PrimeNG Chart (line chart): transactions/hour for last 24h, by ingestion source
   - `ingestion-health.component.ts` — success rate, error rate, latency p95
   - `agent-status-summary.component.ts` — count of agents by connectivity state (green/yellow/red), list of offline agents
   - `reconciliation-summary.component.ts` — matched/unmatched/flagged counts, pending review count
   - `stale-transactions.component.ts` — count of stale PENDING transactions, link to transaction browser filtered by stale
   - `active-alerts.component.ts` — recent system alerts
2. Auto-refresh every 60 seconds (configurable)
3. Legal entity selector in header filters dashboard data
4. Role-based: SiteSupervisor sees only their assigned sites
5. Use PrimeNG Cards for widget containers

**Acceptance criteria:**
- Dashboard renders all 6 widgets
- Charts display data from API (mock data in tests)
- Auto-refresh works
- Legal entity filter changes all widget data
- SiteSupervisor sees filtered view
- Loading spinners while data loads
- Error states shown gracefully (not blank)

---

### AP-5.3: Transaction Browser

**Sprint:** 9
**Prereqs:** AP-0.3, AP-5.1
**Estimated effort:** 3–4 days

**Read these artifacts before starting:**
- `WIP-HLD-Angular-Portal.md` — §3.1.2 (Transaction Browser feature spec)
- `schemas/openapi/cloud-api.yaml` — `GET /api/v1/transactions` endpoint (portal-facing query params)
- `schemas/canonical/canonical-transaction.schema.json` — all transaction fields
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — transaction statuses

**Task:**
Build the transaction browser with search, filter, and detail view.

**Detailed instructions:**
1. Create `features/transactions/` with standalone components:
   - `transaction-list.component.ts` — main page with filters and data table
   - `transaction-detail.component.ts` — full detail view (route: `/transactions/:id`)
   - `transaction-filters.component.ts` — filter panel
2. Filter panel (collapsible):
   - Text search: `fccTransactionId`, `odooOrderId`
   - Dropdowns: `siteCode`, `status`, `fccVendor`, `ingestionSource`
   - Date range: `startedAt` range
   - Pump number filter
   - Stale toggle (show only stale transactions)
3. Data table (PrimeNG Table with server-side pagination):
   - Columns: fccTransactionId, siteCode, pumpNumber, productCode, volume (formatted), amount (formatted with currency), status (badge), startedAt, ingestionSource
   - Sort by any column (server-side)
   - Click row → navigate to detail view
4. Detail view:
   - All transaction fields displayed
   - Status badge with color
   - Timeline: event trail from audit events (TransactionIngested → TransactionSyncedToOdoo → etc.)
   - Raw FCC payload (collapsible JSON viewer)
   - If DUPLICATE: link to original transaction
   - If reconciled: link to reconciliation record and pre-auth
5. Use shared `data-table`, `status-badge`, `date-range-picker`, `currency-minor-units` pipe

**Acceptance criteria:**
- All filters work and combine correctly
- Server-side pagination with cursor
- Sort works on all sortable columns
- Detail view shows all fields including event timeline
- Duplicate records link to original
- Amount/volume formatted correctly (minor units → display)
- Status badges color-coded
- Empty state when no results
- Loading state during API calls

---

### AP-5.4: Reconciliation Workbench

**Sprint:** 9–10
**Prereqs:** AP-0.3, AP-5.1
**Estimated effort:** 4–5 days

**Read these artifacts before starting:**
- `WIP-HLD-Angular-Portal.md` — §3.1.3 (Reconciliation Workbench feature spec)
- `docs/specs/reconciliation/tier-2-3-reconciliation-rules-engine-design.md` — match rules, tolerance, review flow (§5.4 is critical — defines approve/reject UX requirements)
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.5 Reconciliation states
- `schemas/openapi/cloud-api.yaml` — reconciliation endpoints

**Task:**
Build the reconciliation workbench for Ops Managers.

**Detailed instructions:**
1. Create `features/reconciliation/` with standalone components:
   - `reconciliation-list.component.ts` — exception queue with tabs
   - `reconciliation-detail.component.ts` — detail view with approve/reject actions
   - `reconciliation-filters.component.ts` — filter panel
2. Tab layout:
   - **Variance Flagged** tab: records with `status = VARIANCE_FLAGGED`, sorted by oldest first
   - **Unmatched** tab: records with `status = UNMATCHED`, sorted by oldest first
   - **Reviewed** tab: records with `status = APPROVED | REJECTED`, sorted by reviewedAt DESC
3. Data table columns:
   - siteCode, pumpNumber, nozzleNumber, authorizedAmount (formatted), actualAmount (formatted), variance (formatted with +/- and color), variancePercent, matchMethod, status (badge), createdAt
4. Detail view:
   - Full reconciliation record details
   - Linked pre-auth details (requested amount, authorized amount, customer info)
   - Linked transaction details (actual amount, volume)
   - Variance calculation breakdown
   - Match method explanation
   - If `ambiguityFlag`: warning banner explaining ambiguity
5. Approve/Reject actions:
   - Only visible to `OperationsManager` and `SystemAdmin` roles
   - Only enabled for `VARIANCE_FLAGGED` records
   - Mandatory reason text field (min 10 characters)
   - Confirmation dialog before action
   - On success: update status in list, show success toast
6. Filters: legalEntityId, siteCode, status, since/until dates

**Acceptance criteria:**
- Three tabs show correct records
- Variance displayed with color coding (green for within tolerance, red for over)
- Approve/reject only available to authorized roles
- Reason is mandatory
- Confirmation dialog prevents accidental actions
- Detail view shows linked pre-auth and transaction
- Ambiguity warning displayed when applicable
- Empty states for each tab

---

### AP-5.5: Edge Agent Monitoring Dashboard

**Sprint:** 10
**Prereqs:** AP-0.3, AP-5.1
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `WIP-HLD-Angular-Portal.md` — §3.1.4 (Agent Monitoring feature spec)
- `schemas/canonical/telemetry-payload.schema.json` — telemetry fields
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.4 Connectivity states

**Task:**
Build the Edge Agent monitoring dashboard.

**Detailed instructions:**
1. Create `features/agents/` with standalone components:
   - `agent-list.component.ts` — overview of all agents with health indicators
   - `agent-detail.component.ts` — detailed view for a single agent (route: `/agents/:id`)
2. Agent list:
   - PrimeNG Table: siteCode, siteName, connectivity state (color-coded badge), buffer depth, last seen, battery %, agent version, sync lag
   - Sort by any column
   - Filter by connectivity state, siteCode, legalEntityId
   - Color coding: FULLY_ONLINE = green, INTERNET_DOWN = yellow, FCC_UNREACHABLE = orange, FULLY_OFFLINE = red
3. Agent detail view:
   - Current status card: connectivity state, uptime, last heartbeat
   - Telemetry card: battery %, storage free, buffer depth breakdown (PENDING/UPLOADED/SYNCED_TO_ODOO)
   - Sync status card: last upload time, sync lag, last config pull, config version
   - FCC connection card: FCC vendor, host, heartbeat status, last heartbeat time
   - Connectivity timeline: chart showing state changes over last 24h (PrimeNG Timeline)
   - Recent events: last 20 audit events for this agent
4. Auto-refresh every 30 seconds
5. Offline agents section at top (highlighted): agents with `lastSeenAt` older than threshold

**Acceptance criteria:**
- All agents listed with correct health indicators
- Color-coded connectivity states
- Detail view shows all telemetry fields
- Connectivity timeline renders state changes
- Offline agents highlighted prominently
- Auto-refresh works
- SiteSupervisor sees only their sites

---

### AP-5.6: Site & FCC Configuration Management

**Sprint:** 10–11
**Prereqs:** AP-0.3, AP-5.1
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `WIP-HLD-Angular-Portal.md` — §3.1.5 (Site Configuration feature spec)
- `schemas/config/site-config.schema.json` — full site configuration model
- `docs/specs/config/tier-1-1-site-config-schema.md` — config field details
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — sites, fcc_configs, pumps tables

**Task:**
Build the site and FCC configuration management interface.

**Detailed instructions:**
1. Create `features/sites/` with standalone components:
   - `site-list.component.ts` — all sites with key info
   - `site-detail.component.ts` — full site config view/edit (route: `/sites/:id`)
   - `fcc-config-form.component.ts` — FCC configuration edit form
   - `pump-mapping.component.ts` — pump/nozzle/product mapping management
2. Site list:
   - Table: siteCode, siteName, legalEntity, operatingModel, connectivityMode, ingestionMode, fccVendor, isActive
   - Filter by legalEntity, operatingModel, isActive
3. Site detail (edit form for SystemAdmin/OpsManager):
   - Site info section (read-only for most fields, editable: connectivity mode, operating model)
   - FCC config section:
     - Vendor, protocol, host, port (edit)
     - Transaction mode, ingestion mode (dropdown)
     - Pull interval, heartbeat interval (number inputs)
   - Pump mapping section:
     - Table of pumps with nozzle count
     - Add/remove pump
     - Product code mapping per nozzle
   - Tolerance config section:
     - Amount tolerance % and absolute (minor units)
     - Time window minutes
   - Fiscalization section:
     - Fiscalization mode dropdown
     - Country-specific fields
4. Save triggers config version increment → Edge Agent picks up on next poll
5. Form validation: required fields, numeric ranges, valid enum values
6. View-only for SiteSupervisor, no access for Auditor

**Acceptance criteria:**
- Site list with all columns and filters
- Edit form saves changes correctly
- Config version increments on save
- Form validation prevents invalid values
- Pump mapping add/remove works
- Role-based: edit for Admin/OpsManager, view-only for Supervisor

---

### AP-5.7: Audit Log Viewer

**Sprint:** 11
**Prereqs:** AP-0.3, AP-5.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `WIP-HLD-Angular-Portal.md` — §3.1.7 (Audit Log feature spec)
- `schemas/events/event-envelope.schema.json` — audit event structure
- `docs/specs/events/event-schema-design.md` — all event types

**Task:**
Build the audit log viewer.

**Detailed instructions:**
1. Create `features/audit/` with standalone components:
   - `audit-list.component.ts` — event query and results
   - `audit-detail.component.ts` — full event payload view
2. Query interface:
   - Correlation ID search (exact match — primary use case for tracing)
   - Event type dropdown (multi-select from all event types)
   - Site code filter
   - Legal entity filter
   - Date range (required, max 30 days)
3. Results table:
   - Columns: timestamp, eventType, siteCode, source, correlationId
   - Click → expand to show full payload JSON (collapsible)
   - Sort by timestamp (default: newest first)
4. Correlation trace view:
   - When searching by correlationId: show ALL events with that ID in chronological order
   - This shows the full lifecycle of a transaction or pre-auth

**Acceptance criteria:**
- All query filters work
- Correlation ID search shows complete trace
- JSON payload displayed in collapsible viewer
- Date range enforced (max 30 days)
- Server-side pagination

---

### AP-5.8: Dead-Letter Queue Management

**Sprint:** 11
**Prereqs:** AP-0.3, AP-5.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `WIP-HLD-Angular-Portal.md` — §3.1.8 (DLQ feature spec)
- `docs/specs/error-handling/tier-2-1-error-handling-strategy.md` — quarantine behavior, error categories

**Task:**
Build the dead-letter queue management interface.

**Detailed instructions:**
1. Create `features/dlq/` with standalone components:
   - `dlq-list.component.ts` — list of failed/quarantined items
   - `dlq-detail.component.ts` — detail view with retry/discard actions
2. List columns:
   - Type (transaction, pre-auth, etc.), error code, error message, siteCode, createdAt, retryCount, lastRetryAt
   - Filter by error category, siteCode, date range
3. Detail view:
   - Full error details
   - Original payload (JSON viewer)
   - Retry history
4. Actions:
   - **Retry**: re-enqueue for processing (available to OpsManager+)
   - **Discard**: mark as permanently failed with mandatory reason (available to OpsManager+)
   - Auditor: view only
5. Batch actions: retry all selected, discard all selected

**Acceptance criteria:**
- Failed items listed with error details
- Retry re-processes the item
- Discard requires mandatory reason
- Batch actions work
- Role-based action visibility

---

### AP-5.9: System Settings

**Sprint:** 11
**Prereqs:** AP-0.3, AP-5.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `WIP-HLD-Angular-Portal.md` — §3.1.9 (System Settings feature spec)
- `docs/specs/reconciliation/tier-2-3-reconciliation-rules-engine-design.md` — §5.2 tolerance configuration
- `docs/specs/error-handling/tier-3-5-observability-monitoring-design.md` — alerting rules

**Task:**
Build the system settings interface (SystemAdmin only).

**Detailed instructions:**
1. Create `features/settings/` with standalone components:
   - `settings.component.ts` — tabbed settings page
2. Tabs:
   - **Global Defaults**: default tolerance thresholds (amount %, absolute, time window), stale pending threshold days, retention days
   - **Per-Legal-Entity Overrides**: table of overrides, add/edit/remove
   - **Alert Configuration**: thresholds for each alert type (offline hours, buffer depth, sync lag, error rate)
   - **Retention Policies**: archive retention months, outbox cleanup days
3. Save validates and persists settings
4. Only accessible to SystemAdmin role

**Acceptance criteria:**
- All settings sections render and save
- Per-legal-entity overrides managed via table
- Validation on numeric inputs
- SystemAdmin only (guard + directive)

---

## Phase 6 — Hardening & Production Readiness (Sprints 10–12)

### AP-6.1: End-to-End Testing

**Sprint:** 12
**Prereqs:** All AP-5.x tasks
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/testing/testing-strategy.md` — e2e testing section

**Task:**
Create Cypress e2e tests for critical flows.

**Detailed instructions:**
1. Install Cypress: `npm install cypress --save-dev`
2. Critical flows to test:
   - Login → dashboard loads
   - Transaction browser: search → filter → view detail
   - Reconciliation: navigate to flagged → approve with reason
   - Agent monitoring: view list → click agent → see detail
   - Site config: edit FCC config → save
3. Mock API responses using Cypress intercepts (no real backend needed)
4. Test role-based access: OpsManager vs Auditor vs SiteSupervisor

**Acceptance criteria:**
- All critical flows pass
- Role-based access verified
- Tests run in CI
- Test execution time under 5 minutes

---

### AP-6.2: Performance & Bundle Optimization

**Sprint:** 12
**Prereqs:** All AP-5.x tasks
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `WIP-HLD-Angular-Portal.md` — §9 (performance considerations)

**Task:**
Optimize the Angular bundle and runtime performance.

**Detailed instructions:**
1. Run `ng build --stats-json` and analyze with `webpack-bundle-analyzer`
2. Verify all feature modules are lazy-loaded (no eager imports)
3. Verify PrimeNG tree-shaking (import only used components)
4. Enable Angular production optimizations (AOT, minification, tree-shaking)
5. Add `trackBy` functions to all `*ngFor` loops
6. Target: initial bundle < 500KB gzipped

**Acceptance criteria:**
- Initial bundle under 500KB gzipped
- All features lazy-loaded
- No unused PrimeNG modules in main bundle
- Lighthouse performance score > 80
