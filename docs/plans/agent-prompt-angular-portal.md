# Angular Portal — Agent System Prompt

**Use this prompt as context when assigning ANY Angular Portal task to an AI coding agent.**

---

## You Are Working On

The **Forecourt Middleware Angular Portal** — an internal operations and administration web application. It provides visibility into transaction flows, reconciliation exceptions, Edge Agent health, site configuration, and audit trails across 2,000+ fuel stations in 12 African countries.

This is an **employees-only** internal tool — no customer-facing component. It is NOT the transaction system of record; it is the primary control and visibility surface for support teams and operations.

## What This System Does

1. **Dashboard** — Transaction volume charts, ingestion health, agent status map, reconciliation summary, stale transaction alerts
2. **Transaction Browser** — Search/filter/sort transactions by fccTransactionId, siteCode, odooOrderId, pump, time range; detail view with raw FCC payload and event trail
3. **Reconciliation Workbench** — Review variance exceptions, duplicate candidates; approve/reject with mandatory reason
4. **Edge Agent Monitoring** — Per-site agent health (connectivity, buffer depth, battery, version); timeline of state changes
5. **Site & FCC Configuration** — Browse sites, edit FCC settings, manage pump/nozzle/product mappings, manage ingestion modes
6. **Master Data Status** — Sync status from Databricks, stale data alerts, validation issues
7. **Audit Log Viewer** — Query immutable event trail by correlation ID, event type, site, time range
8. **Dead-Letter Queue** — Failed transactions, retry logic, discard with reason
9. **System Settings** — Configure tolerance thresholds, alert channels, retention policies

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Framework | Angular 18+ (standalone components) |
| UI Library | PrimeNG (Lara Light theme) |
| Auth | Azure Entra ID via MSAL.js v3 (`@azure/msal-angular`) |
| State | NgRx Signals (lightweight) or RxJS-based services |
| HTTP | Angular HttpClient with interceptors |
| Charts | PrimeNG Charts (Chart.js wrapper) |
| Styling | SCSS with PrimeNG theme variables |
| Testing | Jasmine/Karma (unit), Cypress (e2e) |
| Hosting | S3 + CloudFront |

## Project Structure

```
src/app/
├── core/
│   ├── auth/               # MSAL config, auth guard, role guard
│   ├── interceptors/       # auth.interceptor.ts, api.interceptor.ts
│   ├── services/           # Shared API services, notification service
│   ├── models/             # TypeScript interfaces matching API DTOs
│   └── layout/             # Shell component (sidebar + header + router-outlet)
├── features/
│   ├── dashboard/          # Dashboard module (lazy-loaded)
│   ├── transactions/       # Transaction browser (lazy-loaded)
│   ├── reconciliation/     # Reconciliation workbench (lazy-loaded)
│   ├── agents/             # Edge Agent monitoring (lazy-loaded)
│   ├── sites/              # Site & FCC configuration (lazy-loaded)
│   ├── master-data/        # Master data status (lazy-loaded)
│   ├── audit/              # Audit log viewer (lazy-loaded)
│   ├── dlq/                # Dead-letter queue (lazy-loaded)
│   └── settings/           # System settings (lazy-loaded)
├── shared/
│   ├── components/         # Reusable: data-table, status-badge, date-range-picker, etc.
│   ├── pipes/              # currency-minor-units, utc-date, status-label
│   └── directives/         # role-visible, loading-overlay
└── environments/           # environment.ts, environment.staging.ts, environment.prod.ts
```

## Key Architecture Rules

1. **Standalone components**: Use Angular 18+ standalone components. No NgModules.
2. **Lazy loading**: Every feature is lazy-loaded via `loadChildren` in routes.
3. **Role-based access**: 4 roles — `SystemAdmin`, `OperationsManager`, `SiteSupervisor`, `Auditor`. Route guards AND element-level visibility using `RoleVisibleDirective`.
4. **API communication**: All data comes from the Cloud Backend REST API. The portal has NO direct database access.
5. **TypeScript interfaces**: Must match the Cloud API OpenAPI spec (`schemas/openapi/cloud-api.yaml`). Generate via NSwag or manually maintain.
6. **Currency display**: Receive amounts as `long` minor units from API. Use `CurrencyMinorUnitsPipe` to format (e.g., `12345` → `123.45 MWK`).
7. **Dates**: Receive as UTC ISO 8601 strings. Display in user's local timezone using `UtcDatePipe`.
8. **Multi-tenancy**: All API calls include `legalEntityId` context. Users see only data for their assigned legal entities.
9. **Error handling**: API interceptor handles 401 (silent refresh), 403 (access denied page), 5xx (toast notification). Feature components handle 404 (not found state).
10. **Pagination**: Use cursor-based pagination from API. PrimeNG DataTable with server-side paging.

## Must-Read Artifacts (Before ANY Task)

| Artifact | Path | What It Tells You |
|----------|------|-------------------|
| Portal HLD | `WIP-HLD-Angular-Portal.md` | Architecture, all feature modules, wireframe guidance |
| Cloud OpenAPI Spec | `schemas/openapi/cloud-api.yaml` | API endpoints the portal consumes |
| Canonical Transaction Schema | `schemas/canonical/canonical-transaction.schema.json` | Transaction field definitions |
| Pre-Auth Record Schema | `schemas/canonical/pre-auth-record.schema.json` | Pre-auth model |
| Telemetry Schema | `schemas/canonical/telemetry-payload.schema.json` | Agent health data structure |
| Site Config Schema | `schemas/config/site-config.schema.json` | Configuration model for site management |
| Event Envelope Schema | `schemas/events/event-envelope.schema.json` | Audit event structure |
| Reconciliation Rules | `docs/specs/reconciliation/tier-2-3-reconciliation-rules-engine-design.md` | Match rules, tolerance, approve/reject flow |
| Security Plan | `docs/specs/security/tier-2-5-security-implementation-plan.md` | Azure Entra auth, role definitions |
| State Machines | `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` | Transaction, reconciliation, connectivity states |
| Scaffolding Spec | `docs/specs/foundation/tier-3-1-project-scaffolding.md` | Angular project setup details |

## Role Permissions Matrix

| Feature | SystemAdmin | OpsManager | SiteSupervisor | Auditor |
|---------|------------|------------|----------------|---------|
| Dashboard | Full | Full | Own sites only | Read-only |
| Transaction Browser | Full | Full | Own sites only | Read-only |
| Reconciliation Approve/Reject | Yes | Yes | No | No |
| Agent Monitoring | Full | Full | Own sites only | Read-only |
| Site Configuration | Full (edit) | Full (edit) | View only | No |
| Master Data Status | Full | View only | No | No |
| Audit Log | Full | Full | Own sites only | Full |
| Dead-Letter Queue | Full | Retry/discard | No | View only |
| System Settings | Full | No | No | No |

## API Communication Pattern

```typescript
// Standard pattern for all feature services
@Injectable({ providedIn: 'root' })
export class TransactionService {
  private readonly http = inject(HttpClient);

  getTransactions(params: TransactionQueryParams): Observable<PagedResult<Transaction>> {
    return this.http.get<PagedResult<Transaction>>('/api/v1/transactions', { params: toHttpParams(params) });
  }
}
```

The `api.interceptor.ts` prepends `environment.apiBaseUrl` and attaches the Entra JWT via `MsalInterceptor`.

## Testing Standards

- Components: Jasmine/Karma with shallow rendering, mock services
- Services: Jasmine with `HttpClientTestingModule`
- Guards/Interceptors: Unit tests with mock router and HTTP testing
- E2E: Cypress for critical flows (login, search, approve/reject)
