# Forecourt Middleware Platform
## WIP High Level Design - Angular Portal

Status: Working Draft  
Authoring Context: Derived from `Requirements.md`, `HighLevelRequirements.md`, `FlowDiagrams.md`, and the additional sizing/authentication constraints provided on 2026-03-10.  
Identity Direction: Employee-only access via Azure Entra ID

## 1. Overview

### Purpose of This Subsystem

The Angular Portal is the operational and administrative interface for the middleware estate. It is not the transaction system of record, but it is the primary control and visibility surface for support teams, system administrators, and operations managers.

### Business Context

The platform spans 2,000 stations across 12 African countries, multiple FCC vendors, mixed online/offline conditions, and country-specific fiscalization rules. Support teams need a single surface to:

- understand station health
- review reconciliation exceptions
- inspect site/FCC mappings and ingest status
- audit who changed configuration or retried flows

Without that portal, the cloud backend becomes operationally opaque and support costs rise sharply.

### Major Responsibilities

- Expose operational dashboards for sites, agents, FCC health, and transaction throughput
- Provide admin/configuration screens for mappings and site/controller setup where middleware-owned configuration exists
- Provide reconciliation workbench and transaction/audit search
- Enforce employee role-based access using Azure Entra identity
- Surface diagnostics for troubleshooting without direct database access

### Boundaries and Exclusions

Included:

- admin and support UI for middleware-owned capabilities
- operational monitoring and audit access
- exception review and manual retry initiation

Excluded:

- Odoo business-order management screens
- field-attendant workflows
- detailed BI/reporting platform functionality
- custom analytics beyond operational dashboards

### Primary Requirement Alignment

- REQ-1 to REQ-5: configuration visibility and admin operations
- REQ-8, REQ-13, REQ-16: reconciliation, duplicates, failures, retries
- REQ-15: edge diagnostics visibility
- REQ-17: role-scoped country/legal-entity access

## 2. Design Goals

### Scalability

- Keep portal UX responsive while querying a high-volume operational estate.
- Avoid direct UI dependence on hot transactional tables for broad dashboards where summarization is more appropriate.

### Configurability

- Make portal screens driven by backend metadata such as country, vendor, and ingestion mode definitions.
- Support phased rollout of new FCC vendors and countries without major UI rewrites.

### Resilience

- The portal must degrade gracefully if some monitoring or reporting views are stale.
- It must not become a critical path for core transaction ingest.

### Security

- Employee-only access through Azure Entra with strong RBAC and auditability.
- Prevent cross-country or cross-legal-entity overexposure by default.

### Maintainability

- Use a clean Angular feature-module architecture with shared UI libraries and API client generation.
- Keep business logic in the backend; the portal should orchestrate views, not own core rules.

### Multi-Country Readiness

- Present time, currency, and site views in country-aware ways.
- Support legal-entity scoped administration and reporting.

### Low Operational Friction

- Make critical exception and diagnostics views fast to navigate.
- Prefer clear drill-down from country to region to site to device/controller.

## 3. Functional Scope

### Key Features

- Dashboard landing page with site health, ingest throughput, and alert summary
- Legal-entity and site administration views
- FCC assignment and pump/nozzle mapping views
- Agent health and version-compatibility screens
- Reconciliation workbench for pre-auth variance and unmatched dispenses
- Duplicate candidate and failed-ingestion review
- Audit trail search
- User-role aware operational actions such as retry, resync trigger, or config publish

### Major Use Cases

- Operations Manager reviews stations with rising Edge backlog or stale FCC heartbeat
- System Administrator updates middleware-owned FCC settings or site runtime settings
- Auditor reviews who changed fiscalization overrides or retried failed transactions
- Support agent investigates why Odoo did not consume transactions for a site

### Supported Operational Scenarios

- Country-level operations view
- Site drill-down to FCC, Edge, and recent transaction timeline
- Investigation of internet-down periods versus disconnected configuration
- Review of transactions already marked `SYNCED_TO_ODOO` versus still pending

## 4. Architecture Overview

### Recommended Architecture Style

Recommended style: Angular SPA with backend-for-frontend-friendly APIs, feature-based composition, and generated typed API clients.

Rationale:

- Employee-only portal with Entra authentication aligns well to SPA + API architecture
- Operational workflows benefit from fast drill-down, route-level lazy loading, and selective real-time refresh
- Typed contracts reduce drift between portal and backend over time

### Logical Frontend Component Model

1. Shell and Navigation
   Layout, role-aware menu, country/site context switching.
2. Identity and Session Module
   Entra login, token handling, route guards, silent refresh, session timeout behavior.
3. Dashboard Module
   Estate summaries, health cards, alert counts, and site cohorts.
4. Configuration Module
   Legal entity, site, FCC, mapping, and policy views.
5. Reconciliation Module
   Pre-auth variance queues, unmatched dispense review, duplicate candidates.
6. Operations Module
   Agent health, replay backlog, Odoo poll lag, manual actions.
7. Audit and Troubleshooting Module
   Search, correlation-ID lookup, event timeline, config-change history.
8. Shared UI and API Client Libraries
   Common tables, filters, status chips, auth helpers, typed HTTP services.

### Runtime Interaction Model

- Portal authenticates with Entra
- Portal calls backend admin/ops APIs with Entra access token
- Backend enforces data scope and action authorization
- Portal polls or subscribes to near-real-time status endpoints for selected operational views

## 5. Project Structure Recommendation

### Repository / Module Structure

```text
/src
  /app
    /core
      /auth
      /layout
      /http
      /state
      /guards
    /features
      /dashboard
      /legal-entities
      /sites
      /fcc
      /mappings
      /agents
      /transactions
      /reconciliation
      /audit
      /support
    /shared
      /components
      /pipes
      /directives
      /models
      /utils
    /generated-api
  /assets
  /styles
/tests
  /unit
  /e2e
/docs
  /ux-notes
  /adr
```

### Frontend Module Boundaries

- `core`: singleton services only
- `features/*`: route-level lazy-loaded domains
- `shared`: reusable presentation elements only, not app-wide state
- `generated-api`: OpenAPI-generated clients or equivalent, versioned with backend contracts

### State Management Direction

- Keep local component state simple where possible
- Use a lightweight global state layer only for auth, current scope, filters that must survive navigation, and shared dashboard refresh state
- Avoid pushing business rules into client state stores

## 6. Integration View

### Upstream and Downstream Systems

| System | Direction | Pattern | Notes |
|---|---|---|---|
| Azure Entra ID | Upstream | OIDC/OAuth2 | Employee authentication |
| Cloud Backend Admin APIs | Downstream | REST/JSON | Primary data source |
| Optional notifications feed | Downstream | SSE/WebSocket/polling | Use only for targeted live views |

### API Domains Consumed by Portal

- `/admin/legal-entities`
- `/admin/sites`
- `/admin/fcc`
- `/admin/mappings`
- `/ops/agents`
- `/ops/transactions`
- `/ops/reconciliation`
- `/ops/audit`
- `/ops/alerts`

### Sync and Refresh Patterns

- Most pages use paginated API queries with explicit filters
- Dashboards use summarized read models from backend, not raw transaction scans
- Detailed diagnostic views can auto-refresh selectively
- Manual action endpoints must be explicitly confirmed and fully audited

### Retry and Idempotency

- UI retries are safe for GETs
- Mutating actions such as retry/requeue/rebind config should use backend idempotency keys where action duplication matters
- Portal should display action outcome from backend audit/event response instead of assuming success

## 7. Security Architecture

### Authentication

- Azure Entra ID using Authorization Code with PKCE
- MSAL Angular or equivalent enterprise-supported library
- Short-lived access tokens with silent renewal where allowed by policy

### Authorization

Recommended model: RBAC in Entra, enforced by backend, reflected by frontend.

Roles:

- `SystemAdministrator`
- `OperationsManager`
- `SiteSupervisor`
- `Auditor`
- optional `SupportReadOnly`

Constraints:

- Backend remains authoritative for action permissions
- Frontend hides unauthorized routes/actions for clarity only
- Legal-entity and country scoping applied server-side even if client requests broader filters

### Audit Logging

- All portal-initiated mutations require audit logging with user ID, role, action, target, and correlation ID
- Sensitive views such as TIN-bearing fiscalization details should log access for traceability if policy requires it

### Secrets and Browser Security

- No secrets stored in source or browser local storage beyond approved token/session mechanisms
- Strong Content Security Policy
- Trusted Types and sanitization for any log/raw payload display
- SameSite cookies if backend session adjuncts are ever used

### Tenant/Site Isolation

- UI scope selectors should be limited to user-authorized legal entities and sites
- Saved filters and exported data must respect those same boundaries

## 8. Deployment Architecture

### Recommended Deployment Model

- Static Angular build hosted on S3 and fronted by CloudFront
- WAF attached at CloudFront
- Backend APIs hosted separately on AWS and protected by token validation

### Environment Strategy

- Separate portal builds or environment-injected config for `dev`, `test`, `uat`, `prod`
- Entra app registrations per environment
- API base URLs and telemetry endpoints environment-specific

### Availability and DR

- S3 + CloudFront provide high availability for static hosting
- Portal should tolerate temporary staleness in operational widgets if backend summary endpoints are delayed
- DR primarily depends on backend/API recovery rather than static-hosting recovery

### Observability

- Frontend error telemetry
- page-load and API-latency monitoring
- route-level usage analytics for support-heavy screens
- correlation ID propagation from backend to UI for troubleshooting

## 9. Key Design Decisions

### Decision 1: Employee-Only Portal with Entra

Reason:

- requirement explicitly states employee login only, making enterprise SSO the right default

Trade-off:

- any future partner/dealer access may need a different identity pattern or B2B setup

### Decision 2: Backend-Centric Authorization

Reason:

- prevents sensitive operational rules from drifting into frontend code

Trade-off:

- more backend endpoints may be needed for tailored read models

### Decision 3: Summary-First Dashboards

Reason:

- 2,000-site operations views should not query raw transactional history directly

Trade-off:

- backend must maintain summary/read models suitable for the portal

### Decision 4: Feature-Based Angular Architecture

Reason:

- keeps large operational UI maintainable and aligned to business domains

Trade-off:

- requires discipline around shared component ownership and route-level contracts

### Assumptions

- Portal is for internal staff only in current phases
- Bulk operational actions are relatively low frequency compared to read activity
- Backend will provide purpose-built ops/read endpoints rather than exposing raw tables through generic APIs

### Known Risks

- Operational screens can become too dense if every exception type is surfaced without prioritization
- Large raw-payload views may tempt the UI into becoming a developer console instead of an operator tool
- Role and scope design may become inconsistent across countries if not standardized early

### Areas Needing Validation / PoC

- Exact Entra group/app-role mapping model
- Reconciliation workbench UX for high exception volume days
- Whether near-real-time status requires SSE/WebSockets or simple polling is sufficient
- Export/reporting expectations for operations and audit users

## 10. Non-Functional Requirements Mapping

| NFR Area | HLD Response |
|---|---|
| Performance | Lazy-loaded features, summarized backend read models, pagination, targeted refresh |
| Availability | Static hosting on S3/CloudFront, graceful degradation when live widgets are stale |
| Recoverability | Versioned deployments, environment config separation, backend correlation IDs for issue tracing |
| Supportability | Strong drill-down paths, typed clients, audit-linked action results, frontend telemetry |
| Operability | Clear role-based navigation, site/country filters, explicit operational actions |
| Extensibility | Feature modules, generated contracts, backend-owned business rules, reusable operational UI components |

## 11. Recommended Technology Direction

### Frontend Stack

- Angular 18+
- TypeScript strict mode
- Angular Router with lazy-loaded feature areas
- Angular signals or established team-standard reactive state pattern
- OpenAPI-generated clients or equivalent typed service layer

### Authentication and Security

- MSAL Angular for Entra integration
- Route guards based on token claims
- Backend-enforced authorization on every data/mutation endpoint

### UI/UX Direction

- Operations-first information architecture
- high-density tables where justified, but always with filter presets and saved views
- clear status semantics for `PENDING`, `SYNCED_TO_ODOO`, reconciliation exceptions, agent offline, FCC unreachable

### Design Patterns

- feature-based frontend architecture
- backend-for-frontend-friendly API design
- typed contracts
- server-driven filtering and paging

## 12. Open Questions / Pending Decisions

1. Which configuration entities are truly middleware-owned and editable in the portal versus read-only projections from Databricks/Odoo?
2. What operational actions are allowed directly from the portal in MVP: retry, resync, requeue, override, or view-only for some domains?
3. Do auditors require exportable evidence packs, or is in-portal history sufficient for the first phase?
4. Should support staff view raw FCC payloads directly, or only normalized/canonical representations with on-demand escalation?
5. What regional hierarchy beyond legal entity is needed in the UI: country only, or country -> region -> territory -> site?
6. Is multilingual UI support needed soon, or can phase 1 remain English-only with localized time/currency formatting?
