# Detailed Bugs Implementation Plan

**Date:** 2026-03-12  
**Source report:** `OverallProjectFunctionalBugs.md`  
**Re-validation basis:** static source inspection across `src/cloud`, `src/edge-agent`, `src/desktop-edge-agent`, and `src/portal`; `npm run lint` re-run in `src/portal` on 2026-03-12 and still failing with 79 errors.

## Purpose

This document turns the findings in `OverallProjectFunctionalBugs.md` into a delivery-oriented implementation plan. Every defect below was re-checked against the current repository state. For each item, this plan captures:

- the exact issue in the current code
- the functional impact
- the concrete implementation work needed
- sequencing and dependencies
- validation required before closure

## Recommended Delivery Order

1. Fix shared cloud/edge contracts and Android runtime wiring.
2. Fix site pre-auth propagation and reconciliation gating.
3. Complete desktop offline/local API paths.
4. Bring vendor support and config application to a consistent supported matrix.
5. Implement real DLQ production and replay.
6. Clear portal lint/accessibility backlog.

## Defect Plan

### DBIP-01: Legal Entity Sync Drops Required Requirement Fields

- **Requirement:** REQ-1
- **Severity:** High
- **Evidence re-validated:**
  - `Requirements.md:135-143` requires `countryCode`, `countryName`, and `odooCompanyId`.
  - `src/cloud/FccMiddleware.Contracts/MasterData/LegalEntitySyncRequest.cs:20-51` has `Code` and optional `Country`, but no `odooCompanyId` or `countryName`.
  - `src/cloud/FccMiddleware.Application/MasterData/SyncLegalEntitiesHandler.cs:106-145` maps `CountryCode = i.Code`, not `i.Country`.
  - `src/cloud/FccMiddleware.Domain/Entities/LegalEntity.cs:12-27` stores `CountryCode` only; there is no separate business code, country name, or Odoo company reference.
- **Exact issue:** the sync contract and domain model collapse multiple requirement fields into one overloaded `Code` field. The handler then persists the wrong value into `CountryCode`.
- **Impact:** legal entity identity is ambiguous, Odoo traceability is absent, and future multi-country rollouts can break when business codes diverge from ISO country codes.
- **Implementation plan:**
  1. Extend the legal entity domain model and persistence schema with `BusinessCode`, `CountryCode`, `CountryName`, and `OdooCompanyId`.
  2. Extend `LegalEntityRecord` and `LegalEntitySyncItem` to carry the same fields explicitly.
  3. Update `SyncLegalEntitiesHandler` change detection and mapping so `CountryCode` is sourced from `Country`, not `Code`.
  4. Add migration and backfill logic for existing records whose `CountryCode` currently contains business codes.
  5. Reject invalid sync payloads when required requirement fields are missing.
- **Dependencies:** schema migration before handler rollout.
- **Validation:**
  - unit tests for mapping and change detection
  - integration test with a legal entity whose business code is not an ISO country code
  - regression test proving `odooCompanyId` survives sync and read models

### DBIP-02: Site Pre-Auth Enablement Never Enters Supported Config Flows

- **Requirement:** REQ-2, REQ-8, REQ-11
- **Severity:** Critical
- **Evidence re-validated:**
  - `src/cloud/FccMiddleware.Domain/Entities/Site.cs:15-20` includes `SiteUsesPreAuth`.
  - `src/cloud/FccMiddleware.Contracts/MasterData/SiteSyncRequest.cs:20-57` does not expose a pre-auth participation field.
  - `src/cloud/FccMiddleware.Application/MasterData/SyncSitesHandler.cs:134-200` never reads or writes `SiteUsesPreAuth`.
  - `src/cloud/FccMiddleware.Contracts/Portal/PortalSiteContracts.cs:97-118` has no portal patch field for `SiteUsesPreAuth`.
  - `src/cloud/FccMiddleware.Application/Reconciliation/ReconciliationMatchingService.cs:53-67` and `:145-161` skip reconciliation when `SiteUsesPreAuth` is false.
- **Exact issue:** the entity supports the flag, but neither Databricks sync nor portal edit flows can populate it. Real sites therefore default to `false`.
- **Impact:** reconciliation is effectively disabled for synced production sites unless the database is manually seeded.
- **Implementation plan:**
  1. Decide ownership of `SiteUsesPreAuth`: Databricks-owned master data or portal-managed runtime configuration.
  2. Add the field to the chosen write path and to all read models that must expose it.
  3. Update sync/patch handlers and change detection to persist the field.
  4. Include the flag in the agent config payload if edge behavior depends on it.
  5. Add a data migration/backfill strategy for existing sites.
  6. Add startup or admin validation to flag sites with pre-auth enabled downstream but no `SiteUsesPreAuth` value configured.
- **Dependencies:** must be fixed before reconciliation can be considered operational.
- **Validation:**
  - sync handler tests
  - portal/site update tests
  - reconciliation integration tests proving a Databricks-synced site with pre-auth enabled creates records instead of skipping

### DBIP-03: Vendor Support Matrix Is Inconsistent Across Cloud, Desktop, and Android

- **Requirement:** REQ-3, REQ-10
- **Severity:** High
- **Evidence re-validated:**
  - Cloud API registers DOMS, RADIX, and PETRONITE in `src/cloud/FccMiddleware.Api/Program.cs:348-374`.
  - Cloud worker still throws for RADIX in `src/cloud/FccMiddleware.Worker/Program.cs:57-77`.
  - Desktop throws for Advatec in `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs:26-41`.
  - Android factory only marks `DOMS` as implemented in `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/FccAdapterFactory.kt:23-37`.
  - Desktop DOMS JPL factory injects placeholder tenant data in `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs:65-75`.
- **Exact issue:** supported vendors differ by runtime, and some runtimes silently carry stubs or placeholders instead of explicit unsupported-state handling.
- **Impact:** rollout behavior varies by platform, and the same site configuration can work in one runtime and fail in another.
- **Implementation plan:**
  1. Publish a single supported-vendor matrix for API, worker, desktop, and Android.
  2. Centralize supported-vendor enforcement so unsupported vendors are rejected at configuration time, not during runtime traffic.
  3. Remove placeholder runtime defaults in desktop DOMS adapter creation and require real tenant/config values.
  4. Either implement missing vendors to parity or explicitly block them in all configuration surfaces.
  5. Add platform-specific adapter contract tests for every supported vendor.
- **Dependencies:** shared configuration validation and contract cleanup.
- **Validation:**
  - per-platform supported-vendor tests
  - site config API validation tests
  - worker and API parity tests for the same vendor list

### DBIP-04: Desktop Pre-Auth Hardcodes DOMS Instead of Using Site Vendor

- **Requirement:** REQ-3, REQ-6
- **Severity:** High
- **Evidence re-validated:**
  - `src/desktop-edge-agent/src/FccDesktopAgent.Core/PreAuth/PreAuthHandler.cs:190-196` creates `FccVendor.Doms` unconditionally.
  - `src/desktop-edge-agent/src/FccDesktopAgent.Core/PreAuth/PreAuthHandler.cs:359-366` does the same on cancel.
- **Exact issue:** desktop pre-auth ignores configured vendor and always routes through DOMS.
- **Impact:** non-DOMS sites can register and appear configured but pre-auth will still use the wrong protocol.
- **Implementation plan:**
  1. Thread `FccVendor` and full FCC connection settings from config into pre-auth handler calls.
  2. Replace hardcoded DOMS instantiation with `config.FccVendor`.
  3. Make cancel/deauthorize use the same resolved adapter path.
  4. Add negative tests so unsupported vendors fail explicitly instead of silently falling back.
- **Dependencies:** DBIP-03 and desktop config completeness.
- **Validation:**
  - unit tests for pre-auth and cancel using multiple vendors
  - integration tests proving configured vendor selection is honored

### DBIP-05: Android Agent Is Still Wired in Stub Mode

- **Requirement:** REQ-4, REQ-7, REQ-12, REQ-15
- **Severity:** Critical
- **Evidence re-validated:**
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/di/AppModule.kt:103-118` registers `internetProbe` and `fccProbe` as `false`.
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/di/AppModule.kt:146-163` injects `adapter = null`, `config = null`, and `fccAdapter = null`.
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/di/AppModule.kt:209-223` starts `LocalApiServer` with `enableLanApi = false` and `fccAdapter = null`.
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ingestion/IngestionOrchestrator.kt:119-127` and `:170-178` no-op when adapter/config are missing.
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/preauth/PreAuthHandler.kt:129-132` returns an error when no FCC adapter is configured.
- **Exact issue:** the Android project has architecture placeholders but not operational DI wiring.
- **Impact:** the agent remains effectively offline, cannot poll FCC, cannot pre-auth, and cannot provide the intended local API behavior.
- **Implementation plan:**
  1. Add a real adapter factory binding to Koin.
  2. Resolve live FCC config from `ConfigManager` and inject it into connectivity, ingestion, pre-auth, and local API construction.
  3. Replace probe stubs with real cloud `/health` and FCC heartbeat checks.
  4. Add startup readiness validation that fails fast when registration exists but runtime wiring is incomplete.
  5. Split provisional bootstrap behavior from steady-state behavior so the app cannot silently keep running on null adapters after config is applied.
- **Dependencies:** DBIP-06 contract alignment, because live config is required to instantiate the adapter.
- **Validation:**
  - DI smoke test proving all runtime services resolve after registration
  - instrumentation/integration tests for `FULLY_ONLINE`, `INTERNET_DOWN`, and `FCC_UNREACHABLE`
  - end-to-end test for manual pull and pre-auth after config apply

### DBIP-06: Cloud-to-Android Configuration Contract Is Incompatible

- **Requirement:** REQ-5, REQ-10, REQ-15
- **Severity:** Critical
- **Evidence re-validated:**
  - `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs:7-168` emits `SourceRevision`, `Identity`, `Fcc`, `Sync`, `Buffer`, `LocalApi`, `Telemetry`, `Fiscalization`, `Mappings`, and `Rollout`.
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt:15-108` expects `compatibility`, `agent`, `site`, `fccConnection`, `polling`, `sync`, `buffer`, `api`, `telemetry`, and `fiscalization`.
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/ConfigPollWorker.kt:175-184` deserializes cloud JSON directly into `EdgeAgentConfigDto`.
- **Exact issue:** Android expects a different schema than cloud sends. This is not a naming variation; required object shapes differ.
- **Impact:** Android config polling cannot reliably succeed, which blocks FCC connection details, local API settings, mappings, and fiscalization behavior.
- **Implementation plan:**
  1. Choose one contract as canonical. The preferred fix is a shared/generated schema package consumed by cloud and edge clients.
  2. Replace handwritten Android DTOs with models generated from or structurally aligned to the cloud contract.
  3. Update `ConfigManager` and all config consumers to read the canonical structure.
  4. Add compatibility/versioning rules for additive vs breaking fields.
  5. Gate deployment on serialization/deserialization contract tests.
- **Dependencies:** must land before DBIP-05 can be completed.
- **Validation:**
  - contract round-trip tests: cloud serialization -> Android deserialization
  - config poll worker integration test with a real `SiteConfigResponse`
  - version-compatibility tests for `ignoreUnknownKeys` behavior

### DBIP-07: Android Upload and Synced-Status Contracts Diverge from Cloud API

- **Requirement:** REQ-9, REQ-15
- **Severity:** Critical
- **Evidence re-validated:**
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:102-113` documents `GET /api/v1/transactions/synced-status?ids=...`.
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:254-262` sends `ids`.
  - `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:679-709` requires `since` and returns IDs acknowledged since that timestamp.
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:145-160` expects `statuses[]`.
  - `src/cloud/FccMiddleware.Contracts/Transactions/SyncedStatusResponse.cs:7-10` returns `FccTransactionIds`.
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:97-129` expects upload result fields `siteCode`, `id`, and nested `error`.
  - `src/cloud/FccMiddleware.Contracts/Ingestion/UploadRecordResult.cs:6-31` returns `TransactionId`, `OriginalTransactionId`, `ErrorCode`, and `ErrorMessage`.
- **Exact issue:** Android is built against an older or invented cloud contract for both upload responses and Odoo-sync suppression polling.
- **Impact:** local buffer suppression cannot work correctly, and upload response parsing is semantically wrong even when HTTP succeeds.
- **Implementation plan:**
  1. Align Android request/response models to the actual cloud contract or redesign the cloud endpoints and migrate all clients together.
  2. Decide whether synced-status should be timestamp-based (`since`) or ID-based; keep only one shape.
  3. Update Android upload worker and status poll worker to use the chosen canonical models.
  4. Add server-client contract tests that run against generated JSON fixtures from the cloud DTOs.
- **Dependencies:** DBIP-06 shared contract strategy.
- **Validation:**
  - cloud fixture tests for upload response parsing
  - status poll integration tests proving `SYNCED_TO_ODOO` suppression works
  - regression tests for unauthorized/forbidden/rate-limit responses

### DBIP-08: Android Pre-Auth Cloud Forwarding Fabricates Unit Price

- **Requirement:** REQ-6, REQ-8
- **Severity:** Medium
- **Evidence re-validated:**
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/PreAuthCloudForwardWorker.kt:247-259` hardcodes `unitPrice = 1`.
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/preauth/PreAuthHandler.kt:142-168` persists requested amount, currency, and product data, but not unit price.
- **Exact issue:** the edge pre-auth record schema does not store unit price, so cloud forwarding fabricates a placeholder.
- **Impact:** reconciliation math and audit records can be wrong for Android-originated pre-auths.
- **Implementation plan:**
  1. Extend Android `PreAuthRecord` schema and DAO methods to store unit price.
  2. Persist the actual price during pre-auth creation.
  3. Forward the real value to cloud and remove the placeholder.
  4. Backfill existing local records conservatively or mark them as incomplete for reconciliation.
- **Dependencies:** Android pre-auth wiring.
- **Validation:**
  - DAO migration test
  - worker test proving real unit price is sent
  - reconciliation test covering price variance

### DBIP-09: Desktop Local Pre-Auth API Is Still a 501 Stub

- **Requirement:** REQ-6
- **Severity:** Critical
- **Evidence re-validated:**
  - `src/desktop-edge-agent/src/FccDesktopAgent.Api/Endpoints/PreAuthEndpoints.cs:19-31` returns `501 Not Implemented` for create and cancel.
  - core pre-auth logic exists in `src/desktop-edge-agent/src/FccDesktopAgent.Core/PreAuth/PreAuthHandler.cs`.
- **Exact issue:** the desktop runtime has core pre-auth logic but never exposes it through the local API that Odoo would call.
- **Impact:** desktop cannot satisfy the required offline pre-auth flow despite having partial business logic.
- **Implementation plan:**
  1. Inject `IPreAuthHandler` into endpoint mapping.
  2. Implement request/response DTOs and route the endpoints to core handler methods.
  3. Map domain errors to stable HTTP responses and error codes.
  4. Add input validation, idempotency handling, and PII-safe logging.
- **Dependencies:** DBIP-04 vendor correctness.
- **Validation:**
  - endpoint integration tests for submit and cancel
  - happy-path and duplicate-request tests
  - timeout and FCC-unreachable tests

### DBIP-10: Desktop Offline Transaction APIs Are Still a 501 Stub

- **Requirement:** REQ-7, REQ-9
- **Severity:** Critical
- **Evidence re-validated:**
  - `src/desktop-edge-agent/src/FccDesktopAgent.Api/Endpoints/TransactionEndpoints.cs:21-43` returns `501` for list, detail, and acknowledge.
  - manual pull is implemented at `src/desktop-edge-agent/src/FccDesktopAgent.Api/Endpoints/TransactionEndpoints.cs:45-65`.
- **Exact issue:** desktop supports ingestion and pull infrastructure but not the offline Odoo transaction APIs required for disconnected mode.
- **Impact:** offline order creation and acknowledgement cannot work end to end.
- **Implementation plan:**
  1. Introduce/query the buffer service behind GET list/detail endpoints.
  2. Implement acknowledge semantics with idempotency and conflict handling.
  3. Ensure the GET path is buffer-only and never depends on live FCC reachability.
  4. Add cursor pagination and filtering aligned with the cloud polling model where appropriate.
- **Dependencies:** none beyond existing buffer/query services; can proceed in parallel with Android work.
- **Validation:**
  - endpoint integration tests for list/detail/ack
  - offline-mode tests with no FCC connection
  - duplicate acknowledgement and conflicting Odoo order ID tests

### DBIP-11: Desktop Config Application Is Incomplete for FCC Runtime Fields

- **Requirement:** REQ-12
- **Severity:** Medium
- **Evidence re-validated:**
  - `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/AgentConfiguration.cs:18-81` still depends on `FccBaseUrl`, `FccVendor`, `FccApiKey`, and related fields.
  - `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/ConfigManager.cs:207-245` hot-reloads only intervals and a few buffer/telemetry fields.
  - `src/desktop-edge-agent/src/FccDesktopAgent.Core/Registration/RegistrationManager.cs:105-118` only overlays `DeviceId`, `SiteId`, and `CloudBaseUrl` after registration.
- **Exact issue:** desktop receives a broad cloud config model, but only a narrow subset is mapped into the runtime configuration that ingestion and pre-auth depend on.
- **Impact:** a desktop agent can register successfully but remain unable to talk to the FCC with the actual site configuration.
- **Implementation plan:**
  1. Map FCC host, port, vendor, credentials, local API settings, and identity fields from site config into runtime configuration.
  2. Decide which fields are hot-reloadable vs restart-required and enforce that consistently.
  3. Store true legal entity and site identity from registration/config.
  4. Add startup validation that rejects incomplete runtime config for the selected ingestion mode.
- **Dependencies:** contract cleanup for any missing fields.
- **Validation:**
  - config apply unit tests
  - registration-to-runtime integration tests
  - mode-specific startup validation tests for `CLOUD_DIRECT`, `RELAY`, and `BUFFER_ALWAYS`

### DBIP-12: Desktop Upload and Telemetry Use Wrong Legal Entity and Response Shapes

- **Requirement:** Cross-project contract/data mismatch
- **Severity:** High
- **Evidence re-validated:**
  - `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/Models/UploadModels.cs:39-67` expects `siteCode`, `id`, and nested `error`, not the actual cloud upload contract.
  - `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/TelemetryReporter.cs:182-185` sets `LegalEntityId = config.SiteId`.
  - `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/CloudUploadWorker.cs:341-348` also sets `LegalEntityId = config.SiteId`.
- **Exact issue:** desktop uses the wrong semantic identifier for legal entity and the wrong JSON shape for upload response parsing.
- **Impact:** uploads and telemetry can be tenant-scoped incorrectly, and response handling is brittle or wrong.
- **Implementation plan:**
  1. Carry real legal entity identity from registration/config into `AgentConfiguration`.
  2. Update upload and telemetry payload builders to use that value.
  3. Replace desktop upload response DTOs with models aligned to cloud `UploadResponse`.
  4. Add contract tests and telemetry payload validation.
- **Dependencies:** DBIP-11 for identity propagation.
- **Validation:**
  - serialization tests against cloud DTO fixtures
  - telemetry integration tests proving legal entity ID is correct
  - upload worker tests for accepted/duplicate/rejected responses

### DBIP-13: Reconciliation Logic Depends on DB-Seated Flags Instead of Supported Config Flows

- **Requirement:** REQ-8
- **Severity:** Critical
- **Evidence re-validated:**
  - `src/cloud/FccMiddleware.Application/Reconciliation/ReconciliationMatchingService.cs:53-67` skips when `SiteUsesPreAuth` is false.
  - only tests and DB mappings set `SiteUsesPreAuth`; operational sync/update paths do not.
- **Exact issue:** the reconciliation service is logically correct only if site configuration is correct, but the system currently provides no supported way to make that true in production.
- **Impact:** reconciliation appears implemented but remains functionally disabled for real synced sites.
- **Implementation plan:**
  1. Treat DBIP-02 as a hard prerequisite.
  2. After DBIP-02 lands, add site-level readiness validation for reconciliation-enabled sites.
  3. Add monitoring for skipped reconciliation counts by site to catch misconfiguration quickly.
  4. Review seeded defaults and migrations to avoid silently preserving false values.
- **Dependencies:** DBIP-02.
- **Validation:**
  - end-to-end reconciliation tests with synced master data
  - operational metric/alarm test for skipped matches

### DBIP-14: Master Data Ownership Model Is Underspecified

- **Requirement:** REQ-11
- **Severity:** Medium
- **Evidence re-validated:**
  - legal entity sync omits required fields from `Requirements.md`.
  - site sync omits `SiteUsesPreAuth`.
  - portal site patch DTOs only cover tolerance/fiscalization/operating/connectivity fields.
- **Exact issue:** downstream-required fields are split across sync and portal models without a clearly enforced ownership matrix.
- **Impact:** master data can appear healthy while operational features remain underconfigured.
- **Implementation plan:**
  1. Publish a field ownership matrix: Databricks vs portal vs registration vs runtime-only.
  2. Enforce ownership in DTOs and handlers.
  3. Add validation that blocks enabling dependent features when required fields are missing.
  4. Align read models and admin UX with the ownership model.
- **Dependencies:** DBIP-01 and DBIP-02.
- **Validation:**
  - contract tests for required fields
  - API validation tests preventing partial feature enablement

### DBIP-15: DLQ Retry Is Metadata-Only and Real Dead-Letter Production Is Missing

- **Requirement:** REQ-16, REQ-14
- **Severity:** High
- **Evidence re-validated:**
  - `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:178-208` and `:252-295` call `ApplyRetry` and save.
  - `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:335-352` only appends retry history and sets `Status = RETRYING`.
  - repository scan found no runtime producer path creating `DeadLetterItem` instances outside tests; only `src/cloud/tests/FccMiddleware.IntegrationTests/Portal/PortalApiSurfaceTests.cs:564` seeds one.
- **Exact issue:** DLQ screens exist, but retry does not replay anything and mainline failure paths do not clearly insert dead-letter records.
- **Impact:** operators are shown a recovery surface that does not actually recover or even reliably capture failures.
- **Implementation plan:**
  1. Define explicit dead-letter entry points for ingestion, upload, pre-auth forwarding, and replay failures.
  2. Implement a replay service that can resubmit the original payload through the actual pipeline.
  3. Change `Retry` and `RetryBatch` to enqueue replay work instead of mutating status only.
  4. Expand DLQ state machine to distinguish queued, running, succeeded, and failed retries.
  5. Persist enough payload/reference metadata to support replay safely.
- **Dependencies:** requires agreement on replay architecture and payload retention.
- **Validation:**
  - unit tests for DLQ state transitions
  - integration tests proving a retry causes real pipeline execution
  - failure-path tests proving `DeadLetterItem` rows are produced from live runtime code

### DBIP-16: Portal Lint and Accessibility Backlog Is Still Present

- **Requirement:** Portal static validation findings
- **Severity:** Medium
- **Evidence re-validated:**
  - `npm run lint` re-run in `src/portal` on 2026-03-12 still fails with 79 errors.
  - Main categories confirmed:
    - `label-has-associated-control` across audit, DLQ, site config, transactions, settings, and reconciliation templates
    - `eqeqeq` violations in reconciliation, settings, and FCC config templates
    - accessibility interaction issues in `src/portal/src/app/features/edge-agents/agent-list.component.ts`
    - unused variables in `src/portal/src/app/core/auth/role.guard.ts` and `src/portal/src/app/features/reconciliation/reconciliation-list.component.ts`
- **Exact issue:** the portal still fails its lint/a11y baseline.
- **Impact:** CI quality remains below release standard, and several operations screens have avoidable accessibility defects.
- **Implementation plan:**
  1. Clear unused-variable and `eqeqeq` issues first; they are low-risk cleanup.
  2. Fix label/control associations and keyboard-focus issues component by component.
  3. Add lint to required PR checks if not already enforced.
  4. Avoid landing more portal work until the current lint baseline is clean.
- **Dependencies:** none.
- **Validation:**
  - `npm run lint` passes cleanly
  - spot-check keyboard navigation on affected screens

## Verification-Only Workstreams

These were not re-validated as design defects, but they still need automated proof because the current report called them out as residual risks:

### Duplicate Detection

- **Requirement:** REQ-13
- **Current assessment:** no major static design bug was confirmed.
- **Work needed:** add end-to-end tests covering FCC push plus edge catch-up upload using the same `(fccTransactionId, siteCode)` dedup key.

### Multi-Tenancy and Isolation

- **Requirement:** REQ-17
- **Current assessment:** no major static defect confirmed.
- **Work needed:** add mixed-tenant integration tests across portal and API routes.

## Proposed Milestones

### Milestone 1: Contract and Android Runtime Recovery

- DBIP-05
- DBIP-06
- DBIP-07
- DBIP-08

### Milestone 2: Pre-Auth and Reconciliation Correctness

- DBIP-02
- DBIP-04
- DBIP-09
- DBIP-13
- DBIP-14

### Milestone 3: Offline Odoo and Desktop Runtime Completion

- DBIP-10
- DBIP-11
- DBIP-12

### Milestone 4: Vendor Parity and Operational Recovery

- DBIP-03
- DBIP-15
- DBIP-16

## Exit Criteria

The findings report can be considered materially addressed only when all of the following are true:

- cloud, Android, and desktop share contract-compatible config/upload/status schemas
- Android no longer boots with null adapters, stub probes, or disabled local API defaults after registration
- site pre-auth enablement is configurable through supported flows and reconciliation uses it correctly
- desktop local pre-auth and offline transaction APIs are fully implemented
- unsupported FCC vendors are blocked consistently across all runtimes, or implemented to parity
- DLQ retry causes real replay and runtime failures produce dead-letter rows
- portal lint passes cleanly
