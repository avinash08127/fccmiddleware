# Bug Analysis - Cloud Backend and Portal

Date: 2026-03-12

## Scope

- Reviewed `src/cloud` and `src/portal`.
- Cross-checked behavior against `Requirements.md` and `HighLevelRequirements.md`.
- Focused on functional defects and technical gaps likely to cause failures during system testing, SIT, UAT, and integration testing.

## Validation limits

- Cloud build/tests were not executed in this shell because `dotnet` is not installed in the current environment.
- Portal build was attempted, but `npm run build` is currently blocked by a cross-platform dependency issue in `node_modules` (`@esbuild/win32-x64` is installed while this shell is Linux/WSL and expects `@esbuild/linux-x64`).

## Executive summary

The largest risk is not isolated bugs inside single modules; it is that the current portal and cloud backend are not integration-compatible. The portal is built as an Entra/MSAL-admin console with a broad admin API surface, while the backend currently exposes a much smaller API focused on FCC ingestion, Edge Agent, Odoo polling, Databricks sync, and reconciliation review. In the current state, many portal test flows will fail immediately with `401`, `403`, or `404`, even before business logic is exercised.

The next highest-risk area is master-data integrity. The sync handlers soft-deactivate any active records missing from a given request, while the controller simultaneously enforces batch limits. For any estate that cannot fit into one request per entity type, partial or chunked syncs will deactivate valid data and create cascading failures in ingestion, reconciliation, and portal views.

## P0 Findings

### P0-1: Portal authentication is incompatible with the cloud API

**Why this matters**

This is a release-blocking integration defect. The portal is wired to acquire Azure Entra/MSAL bearer tokens and protect the entire app with `MsalGuard`, but the backend does not validate portal Entra tokens. Portal screens will fail before functional testing can begin.

**Evidence**

- The API only registers:
  - default `Bearer` as the device JWT scheme
  - `FccHmac`
  - `OdooApiKey`
  - `DatabricksApiKey`
  - See `src/cloud/FccMiddleware.Api/Program.cs:49-67`.
- The default bearer validation is driven by `DeviceJwtOptions` with a symmetric signing key, not by Entra issuer metadata or portal client config. See `src/cloud/FccMiddleware.Api/Program.cs:72-101`.
- Portal authorization policies (`PortalUser`, `PortalReconciliationReview`) exist, but they still rely on whatever authenticated principal the API can validate. There is no separate Entra validation pipeline behind them. See `src/cloud/FccMiddleware.Api/Program.cs:104-155`.
- The portal creates an MSAL `PublicClientApplication`, requests `${environment.msalClientId}/.default` scopes, and protects the app with `MsalGuard`. See `src/portal/src/app/core/auth/auth.config.ts:14-20`, `src/portal/src/app/core/auth/auth.config.ts:33-50`, and `src/portal/src/app/app.routes.ts:18-86`.

**Requirement impact**

- Blocks portal-based operations and admin acceptance testing.
- Prevents execution of reconciliation-review and operational portal scenarios expected by the requirements.

**Fix direction**

Add a real Entra JWT bearer configuration for portal users, separate it clearly from device JWT auth, and bind portal policies to that scheme.

### P0-2: The portal calls a backend API surface that does not exist

**Why this matters**

Most portal screens are wired to endpoints that are not implemented in the cloud backend. This will cause immediate `404` failures across dashboard, sites, agent monitoring, audit, DLQ, settings, and master-data pages.

**Evidence**

- Current controller surface under `src/cloud/FccMiddleware.Api/Controllers` is limited to:
  - transactions
  - pre-auth
  - health
  - master data sync
  - ops reconciliation
  - agent/device registration/config
- There are no controllers exposing portal endpoints such as `/api/v1/agents`, `/api/v1/sites`, `/api/v1/audit/events`, `/api/v1/admin/dashboard/*`, `/api/v1/admin/settings`, or `/api/v1/dlq`.
- Portal services expect those routes:
  - agents: `src/portal/src/app/core/services/agent.service.ts:25-42`
  - sites: `src/portal/src/app/core/services/site.service.ts:35-84`
  - audit: `src/portal/src/app/core/services/audit.service.ts:11-26`
  - dashboard: `src/portal/src/app/features/dashboard/dashboard.service.ts:14-29`
  - settings: `src/portal/src/app/core/services/settings.service.ts:15-37`
  - DLQ: `src/portal/src/app/core/services/dlq.service.ts:20-42`
  - master data browser endpoints: `src/portal/src/app/core/services/master-data.service.ts:10-15`

**Requirement impact**

- Dashboard smoke tests will fail.
- Site configuration, edge agent monitoring, audit log, DLQ, settings, and master-data test cases cannot proceed.

**Fix direction**

Either implement the API surface consumed by the portal or reduce the portal to the backend capabilities that actually exist.

### P0-3: Even the overlapping transaction and reconciliation APIs do not match portal paths, auth, or contracts

**Why this matters**

The few areas where portal and cloud appear to overlap still do not integrate cleanly. The portal is treating backend integration as a portal-admin API, while the backend endpoints are actually Odoo or operational APIs with different routes and payload shapes.

**Evidence**

- Reconciliation route mismatch:
  - portal calls `/api/v1/reconciliation/...`: `src/portal/src/app/core/services/reconciliation.service.ts:15-40`
  - backend exposes `/api/v1/ops/reconciliation/...`: `src/cloud/FccMiddleware.Api/Controllers/OpsReconciliationController.cs:13-15`, `25`, `112`, `126`
- Portal expects `GET /api/v1/reconciliation/{id}`, but the backend does not implement a get-by-id route. See `src/portal/src/app/core/services/reconciliation.service.ts:26-28` versus `src/cloud/FccMiddleware.Api/Controllers/OpsReconciliationController.cs:25-138`.
- Transactions list endpoint mismatch:
  - portal treats `/api/v1/transactions` as a portal data API using bearer auth: `src/portal/src/app/core/services/transaction.service.ts:17-20`
  - backend `GET /api/v1/transactions` is an Odoo poll endpoint protected by `OdooApiKey`, not portal auth: `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:281-368`
- Portal expects `GET /api/v1/transactions/{id}` for detail view, but the backend does not implement it. See `src/portal/src/app/core/services/transaction.service.ts:23-25`.
- Portal expects `POST /api/v1/transactions/acknowledge` to return `AcknowledgeResult[]`, but backend returns an `AcknowledgeResponse` wrapper with `results`, `succeededCount`, and `failedCount`. Compare `src/portal/src/app/core/services/transaction.service.ts:27-32` with `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:381-436`.
- Portal transaction model expects many fields that the backend poll DTO does not return, including `legalEntityId`, `updatedAt`, `schemaVersion`, `isDuplicate`, `odooOrderId`, `preAuthId`, `reconciliationStatus`, `duplicateOfId`, `rawPayloadRef`, and `rawPayloadJson`. Compare `src/portal/src/app/core/models/transaction.model.ts:37-69` with `src/cloud/FccMiddleware.Contracts/Transactions/TransactionPollDto.cs:7-70`.
- Portal query model includes filters such as `legalEntityId`, `status`, `to`, `productCode`, `fccVendor`, `ingestionSource`, `isStale`, and sorting fields, but backend `GET /api/v1/transactions` only accepts `siteCode`, `pumpNumber`, `from`, `cursor`, and `pageSize`. Compare `src/portal/src/app/core/models/transaction.model.ts:77-96` with `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:290-327`.

**Requirement impact**

- Transaction list/detail and reconciliation flows will fail or behave incorrectly even if auth were fixed.
- SIT will produce false negatives because UI failures come from contract drift, not from business logic alone.

**Fix direction**

Align the portal to the existing backend contract instead of reusing Odoo-oriented endpoints.

### P0-4: Master-data sync will deactivate valid records whenever data is sent in multiple batches

**Why this matters**

This is a high-risk data integrity defect. The controller caps request sizes, but the handlers treat every request as a complete snapshot and soft-deactivate any active records not present in that request. Real master data volumes will inevitably be chunked, causing valid sites, pumps, products, or operators to be deactivated by later batches.

**Evidence**

- Controller batch limits:
  - legal entities max 500: `src/cloud/FccMiddleware.Api/Controllers/MasterDataController.cs:15-19`, `52-53`
  - sites max 500: `src/cloud/FccMiddleware.Api/Controllers/MasterDataController.cs:15-19`, `87-88`
  - pumps max 1000: `src/cloud/FccMiddleware.Api/Controllers/MasterDataController.cs:17`, `122-123`
  - products max 200: `src/cloud/FccMiddleware.Api/Controllers/MasterDataController.cs:18`, `160-161`
  - operators max 500: `src/cloud/FccMiddleware.Api/Controllers/MasterDataController.cs:19`, `194-195`
- Sync handlers soft-deactivate active records absent from the payload:
  - sites: `src/cloud/FccMiddleware.Application/MasterData/SyncSitesHandler.cs:62-78`
  - pumps: `src/cloud/FccMiddleware.Application/MasterData/SyncPumpsHandler.cs:104-120`
  - products: `src/cloud/FccMiddleware.Application/MasterData/SyncProductsHandler.cs:54-74`
  - operators: `src/cloud/FccMiddleware.Application/MasterData/SyncOperatorsHandler.cs:54-74`

**Requirement impact**

- Violates the high-level requirement that master-data sync be idempotent and track freshness (`HighLevelRequirements.md:111-116`).
- Causes downstream failures in ingestion, mapping, reconciliation, and portal browsing once master data starts cycling on and off.

**Fix direction**

Introduce an explicit full-snapshot marker/version for destructive deactivation, or change sync semantics to true upsert-only for partial batches.

### P0-5: DOMS cloud ingestion rejects bulk push payloads despite requirements allowing bulk or single transaction push

**Why this matters**

This is a primary-ingestion defect. If the FCC pushes a transaction array with more than one item, the DOMS adapter rejects it. Any vendor/site configured to push bulk payloads will fail normal-order ingestion.

**Evidence**

- Requirement allows one-by-one or bulk FCC push payloads with no special flag: `HighLevelRequirements.md:71-75`.
- Cloud ingest endpoint accepts a single raw payload and forwards it to adapter normalization: `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:45-128`.
- DOMS adapter comment says it accepts both a bare transaction object and a wrapper with `transactions`, but then explicitly rejects any wrapper array whose length is not exactly `1`. See `src/cloud/FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs:201-218`.
- Validation maps that failure to `UNSUPPORTED_MESSAGE_TYPE`: `src/cloud/FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs:73-85`.

**Requirement impact**

- Breaks REQ-7 normal-order ingestion for bulk-emitting FCCs.
- Will surface as failed push-ingestion tests even when payloads are otherwise valid.

**Fix direction**

Allow multi-item arrays at the controller or adapter boundary and iterate/store each item individually with per-record outcomes.

## P1 Findings

### P1-1: Pre-auth forwarding API omits customer tax fields required for fiscalized flows

**Why this matters**

Fiscalized pre-auth flows depend on customer TIN and business-name propagation. The requirements make these fields mandatory when `requireCustomerTaxId` is enabled, but the cloud API and command model do not expose them.

**Evidence**

- Requirements require customer tax details in fiscalized pre-auth flows:
  - `Requirements.md:347-354`
  - `Requirements.md:403-405`
  - `Requirements.md:427-438`
  - `HighLevelRequirements.md:56-63`
- Cloud request contract does not include `customerTaxId` or `customerBusinessName`: `src/cloud/FccMiddleware.Contracts/PreAuth/PreAuthForwardRequest.cs:10-29`
- Application command also omits those fields: `src/cloud/FccMiddleware.Application/PreAuth/ForwardPreAuthCommand.cs:11-30`
- Handler only applies correlation/auth/vehicle/customerName/attendant fields: `src/cloud/FccMiddleware.Application/PreAuth/ForwardPreAuthHandler.cs:250-269`
- Database model already has storage columns for `CustomerTaxId` and `CustomerBusinessName`, which means the gap is in the API/application wiring, not in persistence: `src/cloud/FccMiddleware.Infrastructure/Persistence/Configurations/PreAuthRecordConfiguration.cs:43-46`

**Requirement impact**

- Fails Tanzanian or similar `FCC_DIRECT` fiscalized pre-auth scenarios.
- Blocks AC-5.1 and AC-6.3 from the requirements.

**Fix direction**

Add the missing fields end-to-end in the request contract, command, validation, handler, and adapter payload mapping.

### P1-2: Master-data sync contracts are too thin for the required site, fiscalization, and mapping model, and handlers seed incorrect defaults

**Why this matters**

Even when sync succeeds technically, the resulting data model is not sufficient to satisfy the requirements around legal-entity defaults, site connectivity, operator tax data, and Odoo-to-FCC pump/nozzle mapping. Some values are also hardcoded incorrectly.

**Evidence**

- Requirements expect legal-entity configuration to store country, currency, tax authority, fiscalization default, and timezone: `HighLevelRequirements.md:8-13`
- Requirements expect site configuration to carry operating model and, for dealer-operated sites, operator name and taxpayer ID: `HighLevelRequirements.md:17-23`
- Requirements expect Odoo↔FCC pump/nozzle mapping per site: `HighLevelRequirements.md:27-32`
- Current sync contracts are much thinner:
  - legal entity item only has `Id`, `Code`, `Name`, `CurrencyCode`, `Country`, `IsActive`: `src/cloud/FccMiddleware.Application/MasterData/SyncLegalEntitiesCommand.cs:10-17`
  - site item only has `Id`, `SiteCode`, `LegalEntityId`, `SiteName`, `OperatingModel`, `IsActive`: `src/cloud/FccMiddleware.Application/MasterData/SyncSitesCommand.cs:10-18`
  - pump/nozzle item only has `PumpNumber`, `NozzleNumber`, `CanonicalProductCode`: `src/cloud/FccMiddleware.Application/MasterData/SyncPumpsCommand.cs:10-23`
- Site handler seeds incorrect defaults for new sites:
  - `ConnectivityMode = "CONNECTED"`
  - `CompanyTaxPayerId = string.Empty`
  - See `src/cloud/FccMiddleware.Application/MasterData/SyncSitesHandler.cs:125-139`
- Pump/nozzle handler forces 1:1 Odoo-to-FCC mapping:
  - pump `FccPumpNumber = item.PumpNumber`: `src/cloud/FccMiddleware.Application/MasterData/SyncPumpsHandler.cs:66-67`, `88-89`
  - nozzle `FccNozzleNumber = nozzleItem.NozzleNumber`: `src/cloud/FccMiddleware.Application/MasterData/SyncPumpsHandler.cs:191-193`

**Requirement impact**

- Misconfigures disconnected/connected state.
- Prevents correct fiscalization inheritance and routing decisions.
- Breaks Odoo-to-FCC number translation assumptions needed by pre-auth and transaction matching.

**Fix direction**

Expand sync payloads to include the requirement-critical fields and stop hardcoding mapping/connectivity defaults in handlers.

### P1-3: FCC HMAC ingest credentials can be site-scoped, but the ingest endpoint does not enforce the scope

**Why this matters**

This is a tenant-isolation defect. If an FCC HMAC client is configured with a `site` or `lei`, the controller should reject requests whose body claims another site. Right now, those claims are issued but never enforced.

**Evidence**

- HMAC auth handler adds `site` and `lei` claims when configured: `src/cloud/FccMiddleware.Api/Auth/FccHmacAuthHandler.cs:75-89`
- `TransactionsController.Ingest` uses `request.SiteCode` directly and never compares it with the authenticated claim set before sending the command: `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:61-84`

**Requirement impact**

- Risks cross-site or cross-legal-entity data pollution.
- Could produce hard-to-debug duplicate or reconciliation failures when one credential ingests under the wrong site code.

**Fix direction**

Enforce `site` and `lei` claim matching in the controller before accepting the payload.

### P1-4: Raw payload archiving is not implemented; the system returns a fake S3 URI instead

**Why this matters**

The requirements say raw FCC payloads must be preserved alongside the canonical model. The current implementation only fabricates the S3 path and logs it; nothing is uploaded.

**Evidence**

- Requirement: raw payload must always be preserved with the canonical model: `HighLevelRequirements.md:102-107`
- Current archiver behavior:
  - computes an S3 key
  - returns `s3://...`
  - logs "Would archive payload"
  - contains `TODO: integrate AWS SDK for S3`
  - See `src/cloud/FccMiddleware.Infrastructure/Storage/S3RawPayloadArchiver.cs:39-47`

**Requirement impact**

- Any test that verifies retrieval or existence of archived raw payloads will fail.
- Production/staging records can end up pointing to nonexistent archive objects.

**Fix direction**

Either implement the actual object upload now or keep `RawPayloadRef` null until storage is truly written.

### P1-5: Admin provisioning endpoints are anonymous, and device registration returns an empty site config payload

**Why this matters**

The admin bootstrap and decommission APIs are effectively open if the service is reachable. In addition, successful device registration returns an empty `siteConfig` object even though the contract defines a rich `SiteConfigResponse`.

**Evidence**

- Bootstrap token generation is anonymous: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:53-55`
- Device decommission is anonymous: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:196-198`
- Registration response returns `SiteConfig = new { }` placeholder: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:141-152`
- Expected `SiteConfigResponse` contract is much richer and required for agent behavior: `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs:7-25`, `48-160`

**Requirement impact**

- Security posture is weak for admin/provisioning paths.
- Edge-agent onboarding/provisioning tests may pass registration but fail immediately on missing config assumptions.

**Fix direction**

Protect admin operations with a real admin policy and either return the real initial site config or document/configure an immediate required follow-up fetch.

### P1-6: Portal role handling is brittle and likely to deny valid users even after backend auth is fixed

**Why this matters**

The portal's own role gating is fragile. It relies on `getActiveAccount()` being populated, but there is no observed code that sets the active account after redirect/login completion. It also omits roles that the backend explicitly allows.

**Evidence**

- Route guard relies on `msal.instance.getActiveAccount()`: `src/portal/src/app/core/auth/role.guard.ts:11-33`
- Role-based directive also relies on `getActiveAccount()`: `src/portal/src/app/shared/directives/role-visible.directive.ts:34-54`
- Shell user bootstrap relies on `getActiveAccount()`: `src/portal/src/app/core/layout/shell.component.ts:51-58`
- Repo-wide search found no usage of `setActiveAccount`, `handleRedirectObservable`, `LOGIN_SUCCESS`, or `addEventCallback` under `src/portal/src`, which means account activation after redirect is not currently evident in the codebase.
- Frontend role union only allows `SystemAdmin | OperationsManager | SiteSupervisor | Auditor`: `src/portal/src/app/core/auth/role.guard.ts:5`
- Backend portal policy also accepts `SystemAdministrator` and `SupportReadOnly`: `src/cloud/FccMiddleware.Api/Program.cs:115-124`

**Requirement impact**

- Some authenticated users may see blank/hidden UI or be redirected to access denied incorrectly.
- Role-based portal testing will produce inconsistent failures unrelated to backend business logic.

**Fix direction**

Set the active account after MSAL redirect/login completion and align frontend role definitions with backend policy roles.

## P2 Findings

### P2-1: Portal root template still contains the Angular starter placeholder content

**Why this matters**

This is not as severe as the backend integration blockers, but it will pollute the UI and make screenshot, Cypress, Playwright, and visual-regression tests unreliable.

**Evidence**

- `src/portal/src/app/app.html:1-8` still contains the Angular starter comments.
- The starter template and demo content occupy almost the whole file: `src/portal/src/app/app.html:10-331`
- The real app `<router-outlet />` is only appended at the end: `src/portal/src/app/app.html:342`

**Requirement impact**

- UI baselines and smoke tests will not reflect the intended portal experience.

**Fix direction**

Replace the starter template with the actual app shell root.

### P2-2: The secondary/fuzzy duplicate-review path is not wired even though the API contract and DB support it

**Why this matters**

The requirements define a secondary duplicate-check path for suspicious near-matches. The codebase contains the interface, DB implementation, and response field for that flag, but the ingest handler never calls the check and always returns `FuzzyMatchFlagged = false`.

**Evidence**

- Requirement REQ-13 requires secondary-check matches to be flagged for review, not silently skipped: `Requirements.md:783-809`
- Ingestion DB contract exposes `HasFuzzyMatchAsync`: `src/cloud/FccMiddleware.Application/Ingestion/IIngestDbContext.cs:34-46`
- EF implementation exists: `src/cloud/FccMiddleware.Infrastructure/Persistence/FccMiddlewareDbContext.cs:117-134`
- Public ingest response exposes `FuzzyMatchFlagged`: `src/cloud/FccMiddleware.Contracts/Ingestion/IngestResponse.cs:7-16`
- Ingest handler comments and constants reference secondary fuzzy matching, but the handler never invokes `HasFuzzyMatchAsync` and hardcodes `FuzzyMatchFlagged = false`: `src/cloud/FccMiddleware.Application/Ingestion/IngestTransactionHandler.cs:16-18`, `24-27`, `210-215`

**Requirement impact**

- AC-13.3 is not met.
- Potential duplicates with unreliable FCC transaction identifiers will not be surfaced for investigation.

**Fix direction**

Call the secondary-match check before returning success, persist the review flag/status, and expose it consistently to downstream consumers.

## Recommended triage order

1. Fix the portal/backend auth model and agree the intended portal API surface.
2. Resolve the route/contract mismatch between portal services and backend controllers.
3. Correct master-data sync semantics before any large test data load.
4. Fix DOMS bulk push handling for the primary FCC ingestion path.
5. Close the fiscalization and mapping gaps in pre-auth/master data.

