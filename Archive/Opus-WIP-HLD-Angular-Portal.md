# Forecourt Middleware — Angular Portal High Level Design

**Status:** WIP (Work in Progress)
**Version:** 0.1
**Date:** 2026-03-10
**Author:** Architecture Review — Opus

---

# 1. Overview

## 1.1 Purpose

The Angular Portal is the administrative and operational web application for the Forecourt Middleware platform. It provides Operations Managers, System Administrators, and Site Supervisors with visibility into transaction flows, reconciliation exceptions, Edge Agent health, site configuration, and audit trails across 2,000 fuel stations in 12 African countries.

This is an **employees-only** internal tool — there is no customer-facing component.

## 1.2 Business Context

Operations teams managing 2,000 remote fuel stations across Africa need centralized visibility without visiting sites. The portal enables them to:
- Monitor transaction ingestion in near real-time across all sites.
- Investigate reconciliation exceptions (pre-auth variance, unmatched dispenses).
- Track Edge Agent health and connectivity status per site.
- Review audit trails for regulatory compliance.
- Manage FCC configurations and site setup.
- Handle dead-letter queue items and manual retries.

The portal is **Phase 4** in the implementation roadmap but the HLD is prepared now to ensure the cloud backend API design supports portal requirements from the start.

## 1.3 Major Responsibilities

| Responsibility | Description |
|----------------|-------------|
| Operational Dashboard | Real-time overview of transaction throughput, site health, and alerts across all countries |
| Transaction Browser | Search, filter, and inspect transactions by site, status, time range, pump, product |
| Reconciliation Workbench | Review flagged variance exceptions. Approve/reject. Investigate unmatched dispenses. |
| Edge Agent Monitoring | Per-site agent health: connectivity, buffer depth, last sync, battery, version |
| Site & FCC Management | View site configurations. Manage FCC settings, pump mappings, ingestion modes. |
| Master Data Status | View sync status. Identify stale records. Monitor Databricks pipeline health. |
| Audit Log Viewer | Query immutable event trail by correlation ID, site, time range, event type |
| Dead-Letter Queue | Inspect failed transactions. Understand failure reason. Trigger manual retry. |
| User & Role Management | Assign portal access roles. Manage legal entity scoping. (Delegated to Azure Entra.) |
| System Settings | Manage tolerance thresholds, alert channels, retention policies, polling intervals |

## 1.4 Boundaries and Exclusions

- The portal does **not** directly communicate with FCCs or Edge Agents — it reads data from the cloud backend APIs.
- The portal does **not** create or modify master data (legal entities, sites, pumps) — that is Odoo's domain via Databricks sync. The portal is read-only for master data.
- The portal does **not** create orders in Odoo — Odoo polls the cloud backend directly.
- The portal does **not** manage the HHT fleet — Sure MDM handles device management.
- User identity management (creating accounts, password resets, MFA) is delegated to Azure Entra.

---

# 2. Design Goals

| Goal | Rationale |
|------|-----------|
| **Operational Efficiency** | Ops managers monitoring 2,000 sites need fast, filterable views — not page-by-page navigation. Design for bulk operations and quick triage. |
| **Role-Based Access** | System Administrators see everything. Ops Managers see their assigned legal entities. Site Supervisors see their assigned sites. |
| **Multi-Country Navigation** | 12 legal entities (countries). The portal must make it easy to switch context, compare across countries, and drill down to specific sites. |
| **Responsive and Performant** | Large datasets (millions of transactions). Must use server-side pagination, filtering, and aggregation. Client-side rendering must handle dashboard refresh without full page reloads. |
| **Low Maintenance** | Static SPA hosted on S3/CloudFront. No server-side rendering. API-driven. Angular 18+ standalone components for modular development. |
| **Accessible from Field** | Ops managers in Africa may access from varying bandwidth conditions. Progressive loading. Compact payloads. Consider offline dashboard caching (post-MVP). |

---

# 3. Functional Scope

## 3.1 Feature Modules

### 3.1.1 Dashboard

The primary landing page after login. Provides at-a-glance operational health.

| Widget | Description |
|--------|-------------|
| Transaction Volume (24h) | Bar chart by country. Drill down to site-level. |
| Ingestion Pipeline Health | Transactions/minute. Error rate. DLQ depth. |
| Edge Agent Status Map | Sites with agents online/offline/degraded. Count per country. |
| Reconciliation Summary | Pending exceptions. Auto-approved today. Flagged today. |
| Stale Transactions | Transactions PENDING beyond threshold — count and trend. |
| Recent Alerts | Last N alerts (connectivity issues, DLQ items, stale data). |
| Quick Actions | Links to: DLQ review, reconciliation workbench, transaction search. |

### 3.1.2 Transaction Browser

| Capability | Description |
|------------|-------------|
| Search | By fccTransactionId, siteCode, odooOrderId, pump, time range |
| Filter | By status (PENDING, SYNCED_TO_ODOO, STALE_PENDING), legal entity, site, product, date range |
| Sort | By timestamp, amount, status |
| Detail View | Full canonical payload + raw FCC payload + audit trail (events) + reconciliation record (if any) |
| Bulk Actions | Bulk acknowledge (for testing). Export to CSV. |
| Pagination | Server-side. Configurable page size. |

### 3.1.3 Reconciliation Workbench

| Capability | Description |
|------------|-------------|
| Pending Exceptions | List of reconciliation records with status VARIANCE_FLAGGED or UNMATCHED |
| Detail View | Pre-auth record, final dispense record, variance breakdown, timeline |
| Actions | Approve variance (with reason). Reject (with reason). Escalate. |
| Filters | By legal entity, site, variance %, date range, status |
| Statistics | Exception rate trend. Average variance. Top sites by exception count. |

### 3.1.4 Edge Agent Monitoring

| Capability | Description |
|------------|-------------|
| Agent List | All registered agents with health indicators |
| Filters | By legal entity, site, connectivity status, buffer depth threshold, agent version |
| Detail View | FCC connectivity status, internet status, buffer depth, last sync, battery, storage, app version, telemetry history |
| Alerts | Agents offline > threshold. Buffer depth > threshold. Version below minimum. |
| Timeline | Historical connectivity status per agent (online/offline transitions) |

### 3.1.5 Site & FCC Configuration

| Capability | Description |
|------------|-------------|
| Site Browser | List all sites with operating mode, connectivity mode, FCC vendor, last transaction time |
| Site Detail | Site config (read-only), FCC config (editable), pump/nozzle mappings (editable), fiscalization overrides, Edge Agent assigned |
| FCC Management | Edit connection details, transaction mode, ingestion mode, pull interval. Activate/deactivate. |
| Pump Mapping | Edit pump/nozzle to product mappings. View physical vs. logical pump numbers. |
| Product Code Mapping | Configure per-FCC product code translation (e.g., DOMS "01" → "PMS") |
| Configuration Audit | History of config changes with who/when/what |

### 3.1.6 Master Data Status

| Capability | Description |
|------------|-------------|
| Sync Overview | Last sync time per entity type (legal entities, sites, pumps, products, operators) |
| Stale Data Alerts | Entities not synced within configurable threshold |
| Sync Log | Recent sync operations with record counts, errors, timestamps |
| Data Validation | Flag records with validation issues (e.g., missing operator TIN for dealer sites) |

### 3.1.7 Audit Log Viewer

| Capability | Description |
|------------|-------------|
| Event Search | By correlation ID, event type, site, actor, time range |
| Timeline View | Chronological event sequence for a specific transaction (full lifecycle) |
| Export | Export event trail for a time range or correlation ID (CSV, JSON) |
| Regulatory | Support for audit queries required by tax authorities per country |

### 3.1.8 Dead-Letter Queue

| Capability | Description |
|------------|-------------|
| DLQ Browser | List of failed transactions with failure reason, retry count, original payload |
| Detail View | Full error context, raw payload, processing history |
| Actions | Manual retry (re-enqueue). Discard with reason. Route to investigation. |
| Alerting | DLQ depth visible on dashboard. Alert when DLQ is non-empty. |

### 3.1.9 System Settings

| Capability | Description |
|------------|-------------|
| Tolerance Thresholds | Configure reconciliation variance tolerance (per legal entity or global) |
| Alert Channels | Configure email recipients, webhook URLs for alert notifications |
| Retention Policies | Configure transaction retention, audit event retention, buffer cleanup periods |
| Feature Flags | Enable/disable features per legal entity (e.g., new adapter, new reconciliation rule) |

---

# 4. Architecture Overview

## 4.1 Recommended Architecture Style

**Single-Page Application (SPA)** built with Angular 18+ using standalone components. Deployed as static files to S3 with CloudFront CDN. All data is fetched from the Cloud Backend REST API.

```
┌─────────────┐       ┌───────────────┐       ┌─────────────────────┐
│   Browser   │──────►│  CloudFront   │──────►│ S3 (Static Assets)  │
│ (Angular)   │       │  (CDN + TLS)  │       │ Angular SPA bundle  │
└──────┬──────┘       └───────────────┘       └─────────────────────┘
       │
       │ API calls (HTTPS)
       │
       ▼
┌──────────────┐       ┌────────────────────────────────┐
│     ALB      │──────►│ Cloud Backend API               │
│  (API route) │       │ /api/v1/admin/*                 │
└──────────────┘       │ /api/v1/transactions (read)     │
                       │ /api/v1/preauth (read)          │
                       │ /api/v1/reconciliation/*        │
                       │ /api/v1/agents/*                │
                       │ /api/v1/config/*                │
                       │ /api/v1/audit/*                 │
                       │ /api/v1/dlq/*                   │
                       └────────────────────────────────┘
       │
       │ Authentication
       ▼
┌──────────────┐
│ Azure Entra  │
│ (OIDC / JWT) │
└──────────────┘
```

## 4.2 Authentication Flow

```
1. User navigates to portal URL (CloudFront)
2. Angular app loads
3. MSAL.js checks for existing session
4. If not authenticated → redirect to Azure Entra login page
5. User authenticates (username/password + MFA if configured)
6. Azure Entra issues ID token + access token (JWT)
7. MSAL.js stores tokens in browser session storage
8. Angular HTTP interceptor attaches access token to all API calls
9. Cloud Backend validates JWT (signature, issuer, audience, expiry)
10. Cloud Backend extracts role claims and legal entity scope
11. API returns data scoped to user's authorized legal entities
```

## 4.3 Key Architectural Patterns

| Pattern | Usage |
|---------|-------|
| **Standalone Components** | Angular 18+ standalone components (no NgModules). Lazy-loaded feature routes. |
| **Smart/Dumb Component Pattern** | Smart (container) components handle data fetching and state. Dumb (presentational) components receive data via inputs and emit events. |
| **API Service Layer** | Angular services encapsulate HTTP calls to the cloud backend. Return Observables. Handle error mapping. |
| **State Management** | NgRx Signals (Angular 18+) for complex state (dashboard, transaction browser). Simple component state for forms and modals. |
| **Route Guards** | Role-based route guards using Azure Entra role claims. Prevent unauthorized navigation. |
| **HTTP Interceptor** | Attaches Azure Entra JWT. Handles 401 (token refresh). Handles 403 (insufficient permissions). Global error handling. |
| **Pagination** | Server-side pagination for all list views. Angular paginator component. API supports `?page=N&pageSize=M` plus cursor-based for large result sets. |
| **Environment Configuration** | `environment.ts` for dev/staging/prod API URLs, Azure Entra tenant ID, client ID. Build-time substitution. |

---

# 5. Project Structure Recommendation

## 5.1 Repository Strategy

**Separate repository** from the cloud backend and edge agent. The portal has its own build, test, and deployment pipeline.

Optionally, the `FccMiddleware.Contracts` NuGet package from the cloud backend can be used to generate TypeScript API client types (via NSwag or similar) for type-safe API consumption.

## 5.2 Recommended Project Structure

```
fcc-admin-portal/
│
├── src/
│   ├── app/
│   │   │
│   │   ├── core/                                  # Singleton services, guards, interceptors
│   │   │   ├── auth/
│   │   │   │   ├── auth.config.ts                 # MSAL configuration
│   │   │   │   ├── auth.guard.ts                  # Route guard (authenticated)
│   │   │   │   ├── role.guard.ts                  # Route guard (role-based)
│   │   │   │   ├── auth.interceptor.ts            # JWT token attachment
│   │   │   │   └── auth.service.ts                # MSAL wrapper service
│   │   │   ├── api/
│   │   │   │   ├── api.interceptor.ts             # Base URL, error handling
│   │   │   │   ├── transaction.api.ts             # Transaction API service
│   │   │   │   ├── reconciliation.api.ts
│   │   │   │   ├── agent.api.ts                   # Edge Agent API service
│   │   │   │   ├── config.api.ts                  # Site/FCC config API service
│   │   │   │   ├── audit.api.ts
│   │   │   │   ├── dlq.api.ts
│   │   │   │   ├── master-data.api.ts
│   │   │   │   └── dashboard.api.ts
│   │   │   ├── models/                            # TypeScript interfaces (API contracts)
│   │   │   │   ├── transaction.model.ts
│   │   │   │   ├── preauth.model.ts
│   │   │   │   ├── reconciliation.model.ts
│   │   │   │   ├── site.model.ts
│   │   │   │   ├── fcc.model.ts
│   │   │   │   ├── agent.model.ts
│   │   │   │   ├── audit-event.model.ts
│   │   │   │   └── user.model.ts
│   │   │   ├── tenant/
│   │   │   │   ├── tenant.service.ts              # Current legal entity context
│   │   │   │   └── tenant-selector.component.ts   # Legal entity switcher
│   │   │   └── layout/
│   │   │       ├── shell.component.ts             # App shell (sidebar, header, content area)
│   │   │       ├── sidebar.component.ts
│   │   │       ├── header.component.ts
│   │   │       └── breadcrumb.component.ts
│   │   │
│   │   ├── shared/                                # Reusable components, directives, pipes
│   │   │   ├── components/
│   │   │   │   ├── data-table/                    # Generic sortable, paginated table
│   │   │   │   ├── status-badge/                  # PENDING, SYNCED, FAILED badges
│   │   │   │   ├── date-range-picker/
│   │   │   │   ├── site-selector/                 # Autocomplete site search
│   │   │   │   ├── country-selector/
│   │   │   │   ├── confirmation-dialog/
│   │   │   │   ├── loading-spinner/
│   │   │   │   └── empty-state/
│   │   │   ├── pipes/
│   │   │   │   ├── currency.pipe.ts               # Format by legal entity currency
│   │   │   │   ├── volume.pipe.ts                 # Format litres
│   │   │   │   ├── relative-time.pipe.ts          # "5 minutes ago"
│   │   │   │   └── timezone.pipe.ts               # Display in legal entity timezone
│   │   │   └── directives/
│   │   │       ├── role-visible.directive.ts       # Show/hide by role
│   │   │       └── copy-to-clipboard.directive.ts
│   │   │
│   │   ├── features/                              # Feature modules (lazy-loaded routes)
│   │   │   │
│   │   │   ├── dashboard/
│   │   │   │   ├── dashboard.routes.ts
│   │   │   │   ├── dashboard.component.ts         # Container: fetches data, composes widgets
│   │   │   │   ├── widgets/
│   │   │   │   │   ├── transaction-volume.component.ts
│   │   │   │   │   ├── pipeline-health.component.ts
│   │   │   │   │   ├── agent-status-map.component.ts
│   │   │   │   │   ├── reconciliation-summary.component.ts
│   │   │   │   │   ├── stale-transactions.component.ts
│   │   │   │   │   └── recent-alerts.component.ts
│   │   │   │   └── dashboard.store.ts             # Signal-based state
│   │   │   │
│   │   │   ├── transactions/
│   │   │   │   ├── transactions.routes.ts
│   │   │   │   ├── transaction-list.component.ts  # Search, filter, paginated list
│   │   │   │   ├── transaction-detail.component.ts # Full payload + audit trail
│   │   │   │   ├── transaction-filters.component.ts
│   │   │   │   └── transactions.store.ts
│   │   │   │
│   │   │   ├── reconciliation/
│   │   │   │   ├── reconciliation.routes.ts
│   │   │   │   ├── exception-list.component.ts    # Flagged variances, unmatched
│   │   │   │   ├── exception-detail.component.ts  # Pre-auth + dispense + variance
│   │   │   │   ├── exception-actions.component.ts # Approve / reject / escalate
│   │   │   │   └── reconciliation.store.ts
│   │   │   │
│   │   │   ├── edge-agents/
│   │   │   │   ├── edge-agents.routes.ts
│   │   │   │   ├── agent-list.component.ts        # All agents with health status
│   │   │   │   ├── agent-detail.component.ts      # Telemetry, connectivity timeline
│   │   │   │   └── edge-agents.store.ts
│   │   │   │
│   │   │   ├── sites/
│   │   │   │   ├── sites.routes.ts
│   │   │   │   ├── site-list.component.ts
│   │   │   │   ├── site-detail.component.ts       # Config, FCC, pumps, agent
│   │   │   │   ├── fcc-config.component.ts        # Editable FCC settings
│   │   │   │   └── pump-mapping.component.ts      # Editable pump/nozzle/product
│   │   │   │
│   │   │   ├── master-data/
│   │   │   │   ├── master-data.routes.ts
│   │   │   │   ├── sync-status.component.ts
│   │   │   │   └── sync-log.component.ts
│   │   │   │
│   │   │   ├── audit/
│   │   │   │   ├── audit.routes.ts
│   │   │   │   ├── audit-search.component.ts
│   │   │   │   ├── audit-timeline.component.ts    # Event timeline for a transaction
│   │   │   │   └── audit-export.component.ts
│   │   │   │
│   │   │   ├── dlq/
│   │   │   │   ├── dlq.routes.ts
│   │   │   │   ├── dlq-list.component.ts
│   │   │   │   └── dlq-detail.component.ts        # Inspect + retry + discard
│   │   │   │
│   │   │   └── settings/
│   │   │       ├── settings.routes.ts
│   │   │       ├── tolerance-config.component.ts
│   │   │       ├── alert-channels.component.ts
│   │   │       └── retention-config.component.ts
│   │   │
│   │   ├── app.component.ts                       # Root component
│   │   ├── app.routes.ts                          # Top-level routes with lazy loading
│   │   └── app.config.ts                          # Application providers
│   │
│   ├── environments/
│   │   ├── environment.ts                         # Dev
│   │   ├── environment.staging.ts
│   │   └── environment.prod.ts
│   │
│   ├── assets/
│   │   ├── i18n/                                  # Translation files (future)
│   │   └── images/
│   │
│   ├── styles/
│   │   ├── _variables.scss
│   │   ├── _mixins.scss
│   │   └── global.scss
│   │
│   ├── index.html
│   └── main.ts
│
├── angular.json
├── tsconfig.json
├── package.json
├── tailwind.config.js                              # If using Tailwind CSS
│
├── e2e/                                            # End-to-end tests (Playwright or Cypress)
│   ├── dashboard.spec.ts
│   ├── transactions.spec.ts
│   └── reconciliation.spec.ts
│
└── docs/
    └── api-contract-generation.md                  # How to generate TS types from Cloud Backend OpenAPI
```

## 5.3 Design Rationale

| Decision | Rationale |
|----------|-----------|
| Feature-based folder structure | Each feature is self-contained (routes, components, store, API calls). Easy to navigate. Easy to lazy-load. |
| Standalone components (no NgModules) | Angular 18+ recommended approach. Simpler, less boilerplate, better tree-shaking. |
| Lazy-loaded feature routes | Each feature loads only when navigated to. Reduces initial bundle size. Critical for access from lower-bandwidth African networks. |
| Separate API service per domain | Encapsulates HTTP logic. Easy to mock in tests. Easy to regenerate from OpenAPI spec. |
| Signal-based stores | Angular 18+ signals provide reactive state without the full NgRx boilerplate. Sufficient for the portal's state complexity. Upgrade to full NgRx if needed. |
| Core/Shared/Features split | Core: singletons (auth, layout). Shared: reusable UI components. Features: business domains. Standard Angular architecture. |

---

# 6. Integration View

## 6.1 APIs Consumed

The Angular Portal consumes the Cloud Backend Admin API exclusively. All data access is via REST.

| API Category | Endpoints | Description |
|-------------|-----------|-------------|
| **Transactions** | `GET /api/v1/admin/transactions` | Search/filter/paginate transactions |
| | `GET /api/v1/admin/transactions/{id}` | Transaction detail with full payload |
| | `GET /api/v1/admin/transactions/{id}/events` | Audit trail for a transaction |
| **Reconciliation** | `GET /api/v1/admin/reconciliation/exceptions` | Flagged and unmatched reconciliations |
| | `GET /api/v1/admin/reconciliation/{id}` | Reconciliation detail |
| | `POST /api/v1/admin/reconciliation/{id}/approve` | Approve variance |
| | `POST /api/v1/admin/reconciliation/{id}/reject` | Reject with reason |
| **Edge Agents** | `GET /api/v1/admin/agents` | All agents with health status |
| | `GET /api/v1/admin/agents/{id}` | Agent detail with telemetry |
| | `GET /api/v1/admin/agents/{id}/timeline` | Connectivity history |
| **Sites & FCC** | `GET /api/v1/admin/sites` | All sites with config |
| | `GET /api/v1/admin/sites/{id}` | Site detail |
| | `PUT /api/v1/admin/fcc/{id}` | Update FCC configuration |
| | `PUT /api/v1/admin/fcc/{id}/pumps` | Update pump mappings |
| **Dashboard** | `GET /api/v1/admin/dashboard/summary` | Aggregated dashboard metrics |
| | `GET /api/v1/admin/dashboard/alerts` | Recent alerts |
| **Master Data** | `GET /api/v1/admin/sync/status` | Sync status per entity type |
| | `GET /api/v1/admin/sync/logs` | Sync operation logs |
| **Audit** | `GET /api/v1/admin/audit/events` | Search audit events |
| **DLQ** | `GET /api/v1/admin/dlq` | Dead-letter queue items |
| | `POST /api/v1/admin/dlq/{id}/retry` | Retry a DLQ item |
| | `POST /api/v1/admin/dlq/{id}/discard` | Discard with reason |
| **Settings** | `GET/PUT /api/v1/admin/settings/{category}` | Read/update system settings |

## 6.2 Authentication Integration

| Concern | Detail |
|---------|--------|
| **Identity Provider** | Azure Entra (Azure Active Directory) |
| **Protocol** | OpenID Connect (OIDC) + OAuth 2.0 Authorization Code Flow with PKCE |
| **Client Library** | @azure/msal-angular (MSAL for Angular) |
| **Token Storage** | Session storage (default MSAL behaviour) |
| **Token Refresh** | MSAL handles silent token refresh via hidden iframe or refresh token |
| **API Scoping** | Access token includes `api://fcc-middleware/.default` scope |
| **Role Claims** | `roles` claim in JWT contains: SystemAdministrator, OperationsManager, SiteSupervisor, ReadOnlyAuditor |
| **Legal Entity Claims** | Custom claim `legal_entities` contains list of authorized legalEntityIds (or `*` for SystemAdmin) |

## 6.3 Data Refresh Strategy

| View | Refresh Approach |
|------|-----------------|
| Dashboard | Poll every 30-60 seconds for updated metrics. Manual refresh button. |
| Transaction List | On-demand (search/filter). No auto-refresh. |
| Reconciliation | On-demand. Badge count on sidebar auto-refreshes. |
| Edge Agent List | Poll every 60 seconds. Health indicators update in-place. |
| Audit | On-demand search. |
| DLQ | Badge count auto-refreshes. List on-demand. |

> **Post-MVP Enhancement:** WebSocket or Server-Sent Events (SSE) for real-time dashboard updates. For MVP, polling is simpler and sufficient.

---

# 7. Security Architecture

## 7.1 Authentication

- **Azure Entra OIDC** for all portal users.
- **MSAL.js** handles the authentication flow, token caching, and silent refresh.
- Users must authenticate before accessing any portal route (auth guard on all routes).
- MFA can be enforced via Azure Entra Conditional Access policies (configured in Azure, not in the portal).

## 7.2 Authorization

| Layer | Enforcement |
|-------|------------|
| **Route Guards** | Angular route guards check role claims in the JWT. Unauthorized users are redirected to an "Access Denied" page. |
| **UI Element Visibility** | `*roleVisible` directive conditionally shows/hides UI elements based on the user's role. Example: "Approve Variance" button only visible to Operations Manager and System Administrator. |
| **API-Level** | Cloud Backend validates role claims and legal entity scope on every request. The portal cannot bypass this — even if the UI is manipulated, the API rejects unauthorized requests. |
| **Legal Entity Scoping** | The `TenantService` in Angular reads the user's authorized legal entities from token claims and includes the active legal entity in all API requests. The API validates this server-side. |

### Role-to-Feature Matrix

| Feature | System Admin | Ops Manager | Site Supervisor | Read-Only Auditor |
|---------|-------------|-------------|-----------------|-------------------|
| Dashboard | Full | Scoped to legal entities | Scoped to sites | Scoped to legal entities |
| Transaction Browser | Full | Scoped, read-only | Scoped to sites, read-only | Scoped, read-only |
| Reconciliation Workbench | Full + actions | Scoped + approve/reject | View only | View only |
| Edge Agent Monitoring | Full | Scoped | Scoped to sites | View only |
| Site & FCC Config | Full + edit | View only | View only | — |
| Master Data Status | Full | View only | — | View only |
| Audit Log | Full (all events) | Scoped | Scoped to sites | Scoped |
| DLQ | Full + retry/discard | Scoped + retry | — | View only |
| System Settings | Full + edit | View only | — | — |

## 7.3 Content Security

| Concern | Approach |
|---------|----------|
| **XSS Prevention** | Angular's built-in template sanitization. Avoid `bypassSecurityTrust*`. Content Security Policy (CSP) headers via CloudFront. |
| **CSRF** | Not applicable — SPA with JWT tokens in Authorization header (not cookies). |
| **Clickjacking** | `X-Frame-Options: DENY` and `Content-Security-Policy: frame-ancestors 'none'` via CloudFront response headers. |
| **Sensitive Data in Client** | No sensitive data cached in browser beyond JWT tokens (session storage, not localStorage). No FCC credentials or raw payloads rendered in browser unless explicitly requested (detail view). |
| **API Security** | All API calls over HTTPS (TLS 1.2+). JWT token validated server-side for every request. |

---

# 8. Deployment Architecture

## 8.1 Deployment Model

**Static SPA hosted on S3 + CloudFront**. No server-side component. All dynamic data comes from the Cloud Backend API.

```
┌─────────────────────────────────────────────────┐
│                   CloudFront                     │
│                                                  │
│  Distribution:                                   │
│    Origin 1: S3 bucket (Angular static files)    │
│    Origin 2: ALB (Cloud Backend API, /api/*)     │
│                                                  │
│  Behaviours:                                     │
│    /api/*    → ALB origin                        │
│    /*        → S3 origin (SPA fallback to index) │
│                                                  │
│  HTTPS: ACM certificate                          │
│  WAF: AWS WAF for rate limiting, geo-blocking    │
│  Headers: CSP, X-Frame-Options, HSTS             │
└─────────┬──────────────────────────┬─────────────┘
          │                          │
     ┌────┴──────┐            ┌──────┴──────┐
     │ S3 Bucket │            │     ALB     │
     │           │            │             │
     │ index.html│            │ /api/v1/*   │
     │ main.js   │            │             │
     │ styles.css│            └─────────────┘
     │ assets/   │
     └───────────┘
```

## 8.2 Build and Deploy Pipeline

```
1. Developer pushes to feature branch
2. CI (GitHub Actions):
   - npm ci
   - ng lint
   - ng test --watch=false --browsers=ChromeHeadless
   - ng build --configuration=staging (or production)
   - Upload dist/ to S3 bucket
   - Invalidate CloudFront cache
3. Staging: auto-deploy from main branch
4. Production: manual approval gate, then deploy
```

## 8.3 Environment Strategy

| Environment | API Base URL | Azure Entra | Deployment |
|-------------|-------------|-------------|------------|
| Local Dev | `http://localhost:5000/api` | Dev tenant or mock | `ng serve` |
| Staging | `https://staging-api.fccmiddleware.com/api` | Staging app registration | S3 + CloudFront (staging distribution) |
| Production | `https://api.fccmiddleware.com/api` | Production app registration | S3 + CloudFront (production distribution) |

## 8.4 Performance Considerations

| Concern | Approach |
|---------|----------|
| **Initial Load** | Lazy-loaded routes keep the initial bundle small (~200-400KB gzipped). CloudFront CDN for fast delivery. |
| **API Latency** | CloudFront routes API calls to ALB. Caching on read-heavy dashboard endpoints (e.g., 30-second TTL on summary metrics). |
| **Large Data Sets** | All list views use server-side pagination. No client-side loading of full datasets. Virtual scrolling for long lists. |
| **Bandwidth** | Gzip/Brotli compression on CloudFront. JSON payloads only include requested fields (sparse responses where supported). |
| **Caching** | Static assets cached with content-hash filenames (long cache TTL). API responses with appropriate Cache-Control headers. |

---

# 9. Key Design Decisions

## 9.1 Architectural Choices

| Decision | Choice | Rationale | Trade-off |
|----------|--------|-----------|-----------|
| **Framework** | Angular 18+ (standalone components) | Per requirements. Mature enterprise framework. Strong typing. Built-in HTTP, forms, routing. Standalone components reduce boilerplate. | Larger bundle than React/Svelte alternatives. Acceptable for internal enterprise app. |
| **Hosting** | S3 + CloudFront | Fully managed. No server to maintain. Global CDN. Low cost. | No server-side rendering. Acceptable for internal app (SEO not relevant). |
| **State Management** | Angular Signals (with NgRx escalation path) | Signals are Angular-native in v18+. Simpler than full NgRx for the portal's complexity. Can upgrade to NgRx store if state becomes complex. | Less tooling (no NgRx DevTools) unless upgraded to full NgRx. |
| **UI Library** | Angular Material or Tailwind CSS | Angular Material: consistent, accessible, enterprise-appropriate. Tailwind: flexible, utility-first. Either works. | Angular Material: opinionated look. Tailwind: requires more design decisions. |
| **API Contract** | TypeScript types generated from Cloud Backend OpenAPI spec | Single source of truth. No manual type maintenance. NSwag or openapi-generator can auto-generate types. | Adds build step. Requires Cloud Backend to maintain accurate OpenAPI spec. |
| **Authentication** | Azure Entra via MSAL.js | Per requirements (employee login via Azure Entra). MSAL.js is Microsoft's official library. Well-documented. | Cross-cloud dependency (Azure auth + AWS hosting). Requires proper CORS and token audience configuration. |
| **Multi-tenancy in UI** | Tenant selector in header/sidebar | Ops managers switch legal entity context. All API calls include the selected legal entity. System admins can select "All" for cross-entity views. | Users must remember to switch context. Clear visual indicator of current context is essential. |

## 9.2 Assumptions

1. Azure Entra app registration is provisioned with appropriate redirect URIs for each environment (dev, staging, prod).
2. Azure Entra custom claims (legal_entities, roles) are configured via app roles and group claims in the Azure portal.
3. The Cloud Backend exposes a comprehensive Admin API (`/api/v1/admin/*`) with all endpoints the portal needs. This API must be designed in parallel with the portal feature specifications.
4. The Cloud Backend produces an OpenAPI specification that can be used to generate TypeScript client types.
5. Employee browser environments support modern JavaScript (no IE11 support needed).
6. The portal is English-only for MVP. Multi-language (i18n) support is post-MVP.

## 9.3 Known Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| **Admin API not ready when portal development starts** | Portal development blocked if API endpoints are not defined | Define OpenAPI contract early. Portal can develop against mock data. API and portal develop in parallel against the contract. |
| **Azure Entra + CloudFront CORS** | Cross-origin issues between CloudFront (portal), ALB (API), and Azure Entra (auth) | CloudFront path-based routing means portal and API share the same domain — avoids CORS for API calls. Azure Entra redirect URIs configured per environment. |
| **Dashboard performance at scale** | Aggregating metrics across 2,000 sites and millions of transactions may be slow | Cloud Backend pre-computes dashboard metrics (materialized views or background aggregation). Dashboard API returns pre-aggregated data. |
| **Low bandwidth access from Africa** | Portal must be usable on slower connections | Lazy loading. Compression. Minimal initial bundle. Server-side pagination. No heavy client-side data processing. |
| **Role claim complexity** | Multiple roles + legal entity scoping + site-level scoping may create complex authorization logic | Define a clear authorization model early. Implement as middleware on the backend. Portal relies on backend enforcement — UI hides are cosmetic only. |

---

# 10. Non-Functional Requirements Mapping

| NFR | Target | HLD Approach |
|-----|--------|-------------|
| **Performance** | Dashboard loads in < 3 seconds on 10Mbps connection | Pre-aggregated dashboard metrics. Lazy loading. CDN. Gzip/Brotli. |
| **Availability** | Same as Cloud Backend (99.5%) | S3 + CloudFront: >99.99% for static hosting. API availability depends on Cloud Backend. |
| **Accessibility** | WCAG 2.1 AA compliance | Angular Material provides accessible components. Keyboard navigation. Screen reader support. |
| **Browser Support** | Chrome (latest 2), Firefox (latest 2), Edge (latest 2), Safari (latest 2) | Standard Angular browser support matrix. |
| **Scalability** | Support 50+ concurrent portal users | Static hosting scales automatically. API scalability handled by Cloud Backend. |
| **Security** | Azure Entra authentication. Role-based access. CSP headers. | MSAL.js + JWT validation + server-side authorization. |
| **Maintainability** | Modular, testable, team-scalable codebase | Feature-based structure. Standalone components. Type-safe API clients. Unit and e2e tests. |
| **Operability** | Deployable without downtime | S3 upload + CloudFront invalidation = zero-downtime deployment. |

---

# 11. Recommended Technology Direction

| Concern | Technology | Rationale |
|---------|-----------|-----------|
| **Framework** | Angular 18+ | Per requirements. Enterprise-grade. Standalone components. Signals for reactivity. |
| **Language** | TypeScript 5.x (strict mode) | Type safety. Catch errors at compile time. Better IDE support. |
| **Authentication** | @azure/msal-angular (MSAL v3) | Official Microsoft library for Azure Entra authentication in Angular SPAs. |
| **UI Components** | Angular Material 18+ or PrimeNG | Angular Material: Google-maintained, consistent, accessible. PrimeNG: richer data components (table, charts). Evaluate during implementation. |
| **Charting** | ngx-charts or Chart.js (via ng2-charts) | Dashboard widgets need bar charts, line charts, gauges. ngx-charts is Angular-native. Chart.js is more flexible. |
| **State Management** | Angular Signals (built-in) | Angular 18+ signals for reactive state. Simpler than NgRx for this app's complexity. |
| **HTTP** | Angular HttpClient | Built-in. Interceptors. Observables. Type-safe. |
| **Forms** | Angular Reactive Forms | For configuration editing (FCC settings, tolerance thresholds). Type-safe. Validation. |
| **Styling** | SCSS + Angular Material theming (or Tailwind CSS) | SCSS for variables and mixins. Angular Material theming for consistent look. Tailwind as alternative for utility-first. |
| **Testing** | Jest (unit) + Playwright (e2e) | Jest for fast unit tests. Playwright for end-to-end browser tests. |
| **Linting** | ESLint + Angular ESLint + Prettier | Code quality and formatting consistency. |
| **API Contract** | NSwag or openapi-typescript-codegen | Generate TypeScript types from Cloud Backend OpenAPI spec. |
| **Build** | Angular CLI (esbuild-based builder) | Angular 18+ uses esbuild for fast builds. Standard tooling. |
| **CI/CD** | GitHub Actions | Build, test, lint, deploy to S3, invalidate CloudFront. |

---

# 12. Open Questions / Pending Decisions

| ID | Question | Impact | Assumption Made |
|----|----------|--------|-----------------|
| OQ-PO-1 | Is the portal Phase 4 (as stated in README) or should parts of it be delivered earlier (e.g., transaction browser for Phase 1 testing)? | Delivery timeline and API readiness | Assumed Phase 4 for full portal. Recommend a minimal transaction browser + agent monitor for Phase 1/2 internal testing. |
| OQ-PO-2 | Angular Material or PrimeNG (or Tailwind + headless UI)? | UI component library selection | No assumption. Recommend evaluating PrimeNG for its DataTable component which suits the transaction browser and reconciliation workbench. |
| OQ-PO-3 | Is multi-language (i18n) support needed for MVP? Countries span English, French, and Portuguese-speaking regions. | UI architecture (ngx-translate, Angular i18n) | Assumed English-only for MVP. i18n architecture should be laid in from the start (externalized strings) to ease future translation. |
| OQ-PO-4 | Should the portal support real-time updates (WebSocket/SSE) for the dashboard, or is polling sufficient? | Architecture complexity vs. operational value | Assumed polling for MVP. WebSocket for post-MVP. |
| OQ-PO-5 | What is the expected number of concurrent portal users? | Scaling and session management | Assumed <50 concurrent users. S3 + CloudFront handles this trivially for static assets. API concurrency handled by Cloud Backend. |
| OQ-PO-6 | Are Azure Entra app roles and custom claims already configured, or does this need to be set up? | Authentication/authorization timeline | Assumed this needs to be configured. Recommend setting up Azure Entra app registration early in the project. |
| OQ-PO-7 | Should the portal include a QR code generator for Edge Agent provisioning? | Provisioning workflow | Assumed yes — helpful for site setup. Simple feature (encode JSON as QR). |
| OQ-PO-8 | Does the Cloud Backend need to pre-aggregate dashboard metrics, or can the portal query raw data with server-side aggregation? | Backend API design, performance | Assumed pre-aggregated metrics for dashboard (background job on cloud). Raw data queries for drill-downs. |
| OQ-PO-9 | Is there a corporate branding / design system to follow? | UI/UX consistency | Assumed no existing design system. Angular Material provides a default professional look. Custom theming if brand guidelines are provided. |
| OQ-PO-10 | Should the portal support exporting reports (PDF, Excel) for operations teams? | Feature scope | Assumed CSV export for MVP. PDF/Excel export post-MVP. |

---

*End of Angular Portal HLD — WIP v0.1*
