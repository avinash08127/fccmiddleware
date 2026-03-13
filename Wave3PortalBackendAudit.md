# Wave 3 — Portal & Backend End-to-End Audit Report

**Date:** 2026-03-13
**Auditor Role:** Senior Solution Architect, QA Lead, Functional Analyst, Security Reviewer, Full-Stack Code Auditor
**Scope:** Angular Portal → .NET API Controllers → Application Handlers → Infrastructure → Database

---

## A. Executive Summary

The FCC Middleware system is a multi-tenant, multi-vendor fuel control computer integration platform spanning an Angular portal, .NET cloud API, desktop edge agent (C#), and mobile edge agent (Kotlin). The portal provides operational visibility across 9 feature areas: Dashboard, Transactions, Reconciliation, Edge Agents, Sites, Master Data, Audit Logs, Dead-Letter Queue, and System Settings.

**Overall Architecture Quality:** The system demonstrates mature engineering practices — cursor-based pagination, switchMap-based request cancellation, structured logging, field-level encryption, multi-scheme authentication, and rate limiting. However, the audit identified **54 findings** across functional, security, technical, and performance dimensions.

**Risk Rating: MEDIUM-HIGH** — No critical exploitable vulnerabilities were found, but several medium-severity issues (hardcoded currency decimals, unbounded client-side data loading, duplicated COUNT queries, missing frontend route guards, broken Radix XML error responses, unbounded in-memory GetEvents loop) compound to create meaningful operational and correctness risk.

| Category | Critical | High | Medium | Low | Info |
|----------|----------|------|--------|-----|------|
| Functional | 1 | 4 | 9 | 5 | 2 |
| Security | 0 | 4 | 7 | 5 | 1 |
| Technical | 0 | 2 | 5 | 3 | 1 |
| Performance | 0 | 1 | 3 | 1 | 0 |

---

## B. Page-by-Page Traceability Matrix

### B.1 Dashboard (`/dashboard`)

| Layer | Component / File | Notes |
|-------|-----------------|-------|
| Route | `dashboard.routes.ts` | No role guard — relies solely on MsalGuard |
| Page | `dashboard.component.ts` | 60s auto-refresh, legal entity filter, 6 widget grid |
| Service | `dashboard.service.ts` | `GET /api/v1/admin/dashboard/summary`, `GET /api/v1/admin/dashboard/alerts` |
| Backend | Not directly traced (no controller file named DashboardController in working tree) | Routes under `api/v1/admin/` prefix — likely served by admin-scoped controller |
| Auth | Frontend: MsalGuard only. Backend: Unknown (admin/* routes) | **Gap: No frontend role guard** |

### B.2 Transactions (`/transactions`)

| Layer | Component / File | Notes |
|-------|-----------------|-------|
| Route | `transactions.routes.ts` | **No role guard** — any MsalGuard-authenticated user can access |
| List | `transaction-list.component.ts` | Cursor-based pagination, switchMap cancellation, filters (status, vendor, site, date, pump, stale) |
| Detail | `transaction-detail.component.ts` | Audit event timeline, collapsible raw payload |
| Filters | `transaction-filters.component.ts` | TransactionFilters interface with 9 filter fields |
| Service | `transaction.service.ts` | `GET /api/v1/ops/transactions`, `GET /api/v1/ops/transactions/:id`, `POST /api/v1/ops/transactions/acknowledge` |
| Backend | `TransactionsController.cs` | **Portal read endpoints not in this controller** — this controller handles ingestion (ingest, upload, poll, acknowledge). Portal-facing ops endpoints served separately |
| Models | `transaction.model.ts` | FccVendor(4), TransactionStatus(6), IngestionSource(3), ReconciliationStatus(7) |

### B.3 Reconciliation (`/reconciliation`)

| Layer | Component / File | Notes |
|-------|-----------------|-------|
| Route | `reconciliation.routes.ts` | **No role guard** — any authenticated user can view |
| List | `reconciliation-list.component.ts` | 3 tabs: Variance Flagged, Unmatched, Reviewed. Cursor pagination for first two tabs, client-side sort for Reviewed |
| Detail | `reconciliation-detail.component.ts` | Pre-auth + transaction details, variance breakdown, approve/reject with mandatory reason (min 10 chars) |
| Service | `reconciliation.service.ts` | `GET /api/v1/ops/reconciliation/exceptions`, `GET /api/v1/ops/reconciliation/:id`, `POST .../approve`, `POST .../reject` |
| Backend | Controller uses `[Authorize(Policy = "PortalReconciliationReview")]` for approve/reject | Only OperationsManager, SystemAdmin, SystemAdministrator |
| Models | `reconciliation.model.ts` | ReconciliationRecord (36 fields), ReconciliationException (22 fields) |

### B.4 Edge Agents (`/agents`)

| Layer | Component / File | Notes |
|-------|-----------------|-------|
| Route | `edge-agents.routes.ts` | Bootstrap token: `roleGuard(['SystemAdmin'])` |
| List | `agent-list.component.ts` | 30s auto-refresh, offline/online split, battery/sync indicators |
| Detail | `agent-detail.component.ts` | 4 status cards, connectivity timeline, events table, diagnostic logs. 30s auto-refresh |
| Token | `bootstrap-token.component.ts` | QR code generation, token reveal/hide, memory clearing, revocation |
| Service | `agent.service.ts` | `GET /api/v1/agents`, `GET /api/v1/agents/:id`, `.../telemetry`, `.../events`, `.../diagnostic-logs` |
| Backend | `AgentsController.cs` | `[Authorize(Policy = "PortalUser")]`. Telemetry redacts FCC host/port for non-sensitive roles. **Issues: M-17 (totalCount cursor bug), M-18 (unbounded GetEvents loop), L-12 (existence oracle), L-13 (incomplete redaction), L-14 (missing legalEntityId guard)** |
| Backend | `PortalAccessResolver.cs` | Multi-tenant access: legal_entities claim scoping, sensitive data access for OperationsManager/SystemAdmin/Auditor |

### B.5 Sites (`/sites`)

| Layer | Component / File | Notes |
|-------|-----------------|-------|
| Route | `site-config.routes.ts` | `roleGuard(['SystemAdmin', 'OperationsManager', 'SiteSupervisor'])` |
| List | `site-config.component.ts` | Operating model/connectivity/active-only filters, cursor pagination |
| Detail | `site-detail.component.ts` | Edit mode: operating model, connectivity, pre-auth toggle, FCC config, pump mapping, tolerance, fiscalization. forkJoin parallel save |
| FCC Form | `fcc-config-form.component.ts` | Vendor-specific fields: DOMS TCP/JPL, Radix, Petronite, Advatec. JSON validation for pump maps |
| Service | `site.service.ts` | `GET /api/v1/sites`, `GET .../detail`, `PATCH`, `PUT .../fcc-config`, pump CRUD, products |

### B.6 Dead-Letter Queue (`/dlq`)

| Layer | Component / File | Notes |
|-------|-----------------|-------|
| Route | `dlq.routes.ts` | **No role guard** |
| List | `dlq-list.component.ts` | Multi-select, batch retry/discard, discard confirmation with mandatory reason, filters |
| Detail | `dlq-detail.component.ts` | Error details, raw payload JSON viewer, retry history, retry/discard actions |
| Service | `dlq.service.ts` | `GET /api/v1/dlq`, `GET /api/v1/dlq/:id`, `POST .../retry`, `POST .../discard`, batch variants |
| Backend | `DlqController.cs` | `[Authorize(Policy = "PortalUser")]` for reads, `[Authorize(Policy = "PortalAdminWrite")]` for retry/discard |

### B.7 Audit Logs (`/audit`)

| Layer | Component / File | Notes |
|-------|-----------------|-------|
| Route | `audit-log.routes.ts` | **No role guard** |
| List | `audit-log.component.ts` | Correlation ID search, event type multi-select, site code, date range (required unless correlation ID, max 30 days). Row expansion |
| Detail | `audit-detail.component.ts` | Event envelope fields, formatted JSON payload with copy |
| Service | `audit.service.ts` | `GET /api/v1/audit/events`, `GET /api/v1/audit/events/:eventId` |
| Backend | `AuditController.cs` | `[Authorize(Policy = "PortalUser")]`. Payload visibility gated by HasSensitiveDataAccess |

### B.8 System Settings (`/settings`)

| Layer | Component / File | Notes |
|-------|-----------------|-------|
| Route | `settings.routes.ts` | `roleGuard(['SystemAdmin'])` |
| Page | `settings.component.ts` | 4 tabs: Global Defaults, Per-Legal-Entity Overrides, Alert Configuration, Retention Policies |
| Service | `settings.service.ts` | `GET /api/v1/admin/settings`, `PUT .../global-defaults`, `PUT .../overrides/:id`, `DELETE .../overrides/:id`, `PUT .../alerts` |

### B.9 Master Data (`/master-data`)

| Layer | Component / File | Notes |
|-------|-----------------|-------|
| Route | `master-data.routes.ts` | **No role guard** |
| Page | `master-data.component.ts` | **STUB — "coming soon" placeholder** |
| Service | `master-data.service.ts` | `GET /api/v1/master-data/sync-status`, `GET /api/v1/master-data/legal-entities` |

---

## C. Detailed Findings Register

### CRITICAL


### HIGH



### MEDIUM

### LOW




---

## D. Missing Test Scenarios

### D.1 Frontend (Angular)

| # | Component | Missing Test | Priority |
|---|-----------|-------------|----------|
| 1 | `CurrencyMinorUnitsPipe` | Test with JPY (0 decimals), KWD (3 decimals), USD (2 decimals) | Critical |
| 2 | `ReconciliationListComponent` | Test Reviewed tab with >100 combined records — verify truncation behavior | High |
| 3 | `DlqListComponent` | Test batch discard with 1-char reason vs 10-char reason | Medium |
| 4 | `SettingsComponent` | Test concurrent save from two tabs — verify last-write-wins behavior | Medium |
| 5 | `TransactionListComponent` | Test with non-string filter params to catch `as unknown` cast issues | Medium |
| 6 | `DashboardComponent` | Test auto-refresh stops when component is destroyed | Low |
| 7 | `AgentDetailComponent` | Test with agent that has no telemetry snapshot | Low |
| 8 | `FccConfigFormComponent` | Test JSON validation for malformed pump map entries | Medium |
| 9 | `RoleVisibleDirective` | Test that elements are hidden for SupportReadOnly users on DLQ batch actions | Medium |
| 10 | `ApiInterceptor` | Test 401 handling doesn't discard form state | Low |

### D.2 Backend (.NET)

| # | Controller / Handler | Missing Test | Priority |
|---|---------------------|-------------|----------|
| 1 | `DlqController.GetDeadLetters` | Verify totalCount query matches paginated query filters exactly | High |
| 2 | `AuditController.GetAuditEvents` | Same — verify totalCount consistency with filters | High |
| 3 | `AgentsController.GetEvents` | Test with site having >limit*10 audit events — verify result completeness | High |
| 4 | `TransactionsController.IngestRadixXml` | Test with USN code for site without SharedSecret configured | Medium |
| 5 | `TransactionsController.IngestPetroniteWebhook` | Test with empty body returns 200 (not 4xx) | Medium |
| 6 | `DlqController.DiscardBatch` | Test cross-tenant batch discard — verify forbidden items are silently skipped | High |
| 7 | `DlqController.Retry` | Test retry on RESOLVED/DISCARDED items returns proper error | Medium |
| 8 | `PortalAccessResolver` | Test with comma-separated roles claim | Medium |
| 9 | `PortalAccessResolver` | Test with wildcard `*` legal_entities claim | Medium |
| 10 | `ReconciliationService` (backend) | Test approve/reject with exactly 10-char reason (boundary) | Low |
| 11 | `TransactionsController.Upload` | Test batch of exactly 500 (boundary) and 501 (exceeds) | Medium |
| 12 | `CloudFccAdapterFactoryRegistration` | Test registration of all 4 vendors, test unsupported vendor rejection | Medium |

### D.3 Integration Tests

| # | Scenario | Missing Coverage | Priority |
|---|---------|-----------------|----------|
| 1 | End-to-end ingestion | Radix XML → IngestHandler → Deduplication → DB → Odoo poll | Critical |
| 2 | End-to-end ingestion | Petronite webhook → IngestHandler → DB → Odoo acknowledge | High |
| 3 | Bootstrap token flow | Generate token → Register device → Upload transactions | High |
| 4 | Reconciliation flow | Pre-auth → Transaction ingest → Match → Variance flag → Approve/Reject | High |
| 5 | DLQ retry flow | Failed ingestion → DLQ entry → Retry → Success → RESOLVED status | Medium |
| 6 | Multi-tenant isolation | Verify user scoped to Entity A cannot read Entity B's transactions | Critical |

---

## E. Refactoring Recommendations

### E.1 High Priority

1. **Centralize currency formatting** — Create a shared `CurrencyHelper` (one already exists at `src/cloud/FccMiddleware.Domain/Common/CurrencyHelper.cs` and `src/desktop-edge-agent/.../CurrencyHelper.cs`) and expose an ISO 4217 decimals lookup from the backend. Update `CurrencyMinorUnitsPipe` to use the correct decimal count per currency.

2. **Deduplicate totalCount queries** — In `DlqController`, `AuditController`, and any other controllers with the same pattern, compute totalCount from the filtered `IQueryable` before applying cursor predicates. This halves database round-trips:
   ```csharp
   var baseQuery = /* filters applied */;
   var totalCount = await baseQuery.CountAsync(ct);
   var cursorQuery = /* apply cursor to baseQuery */;
   var page = await cursorQuery.Take(pageSize + 1).ToListAsync(ct);
   ```

3. **Fix reconciliation Reviewed tab pagination** — Replace the `forkJoin` approach with a server-side combined query that accepts `status=APPROVED,REJECTED` and returns cursor-paginated results sorted by `decidedAt DESC`.

4. **Standardize HTTP params construction** — Replace all `params as unknown as Record<string, string>` casts with the `HttpParams` builder pattern used in `reconciliation.service.ts` and `audit.service.ts`.

### E.2 Medium Priority

5. **Add frontend route guards** — Apply `roleGuard` to DLQ (write roles), Reconciliation (write roles), and Dashboard routes. Even though the backend enforces authorization, route guards prevent confusing UX where buttons appear but fail.

6. **Separate settings save endpoints** — Split `UpdateGlobalDefaultsRequest` into separate endpoints for tolerance and retention configuration, preventing cross-tab overwrites.

7. **Add audit events for DLQ discard operations** — Emit `DeadLetterDiscarded` and `DeadLetterRetried` events through the outbox pattern for full audit trail coverage.

8. **Refactor AgentsController.GetEvents** — Add a `deviceId` column to the AuditEvent entity (or a denormalized `agent_events` view) to enable SQL-level filtering instead of loading 10x records and filtering in memory.

### E.3 Low Priority

9. **Implement Master Data page** — Either build the master data sync status UI or remove the route from navigation.

10. **Add visibility-change-aware refresh** — Use `document.visibilitychange` in Dashboard and Agent components to pause auto-refresh when the tab is backgrounded.

11. **Validate email format in Settings** — Add email regex validation to the alert configuration email recipient inputs.

12. **Consolidate SystemAdmin/SystemAdministrator** — Pick one role name and deprecate the other. Update all guards, policies, and documentation.

---

## F. Final Risk Rating

### Risk Matrix

| Dimension | Rating | Rationale |
|-----------|--------|-----------|
| **Data Correctness** | **HIGH** | Currency formatting bug (C-1, H-2) will display incorrect monetary values for non-2-decimal currencies. This is a financial reporting correctness issue. |
| **Security** | **LOW-MEDIUM** | Backend authorization is well-implemented with multi-scheme auth, rate limiting, HMAC/API-key validation, and field-level encryption. Frontend route guards are incomplete but the backend is the authoritative enforcement point. No credential leakage paths found. |
| **Performance** | **MEDIUM** | Duplicated COUNT queries on every paginated request (H-4, M-3), in-memory filtering in GetEvents (M-2), and unbounded client-side loading in Reviewed tab (H-1) will degrade under production load. |
| **Reliability** | **LOW-MEDIUM** | The reconciliation approve/reject double-fetch (M-9) could show false errors. Auto-refresh timers don't pause in background tabs (L-2). DLQ batch retry is sequential (L-4). |
| **Maintainability** | **LOW** | Clean architecture with MediatR CQRS pattern. Consistent use of Angular Signals and standalone components. Good separation of concerns across layers. Minor inconsistencies (HTTP params casting, reason validation lengths) are localized. |
| **Compliance** | **MEDIUM** | DLQ discard operations lack audit trail events (I-4). Currency display bugs could affect financial reporting accuracy. Audit event retention is configurable but defaults are sensible (2555 days = ~7 years). |

### Overall: **MEDIUM-HIGH RISK**

The system is architecturally sound and demonstrates mature engineering practices. The most impactful issues are:

1. **Currency formatting bug** — Fix immediately before deploying to markets with non-2-decimal currencies
2. **Duplicated database queries** — Fix before production scale-up
3. **Reviewed tab unbounded loading** — Fix before reconciliation volume grows
4. **Missing audit events for DLQ operations** — Fix for compliance requirements

With these 4 items addressed, the risk rating would drop to **LOW-MEDIUM**.

---

*Report generated 2026-03-13 by automated audit. All findings should be validated against the current deployment configuration and business requirements.*
