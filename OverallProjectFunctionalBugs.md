# Overall Project Functional Bugs

## Scope and method

- Reviewed `Requirements.md` and `HighLevelRequirements.md` as the functional baseline.
- Statically reviewed the four implementation areas under `src`: `cloud`, `portal`, `edge-agent`, and `desktop-edge-agent`.
- Focused on end-to-end completeness, functional bugs, technical bugs, required data setup, role ownership, and recovery paths.
- Validation limit: this report is based on static analysis. `dotnet` and `java` tooling are not available in this environment, so cloud, desktop, and Android runtime behavior could not be executed end to end here.

## Feature inventory / scan checklist

| Req | Feature | Main implementation areas scanned | Scan status | Headline result |
|---|---|---|---|---|
| REQ-1 | Legal Entity and Country Configuration | `src/cloud/FccMiddleware.Contracts/MasterData`, `src/cloud/FccMiddleware.Application/MasterData` | Scanned | Incomplete legal entity model and sync mapping |
| REQ-2 | Site Configuration and Operating Modes | `src/cloud/FccMiddleware.Contracts/MasterData`, `src/cloud/FccMiddleware.Api/Controllers/SitesController.cs` | Scanned | Site pre-auth enablement is not carried through sync/config surfaces |
| REQ-3 | FCC Registration and Assignment | `src/cloud/FccMiddleware.Api/Program.cs`, `src/desktop-edge-agent/.../Adapter`, `src/edge-agent/.../adapter` | Scanned | Vendor support is incomplete and inconsistent by project |
| REQ-4 | Connected vs Disconnected Mode | `src/cloud`, `src/edge-agent/.../di/AppModule.kt`, `src/desktop-edge-agent` | Scanned | Android connectivity wiring is stubbed, so real mode transitions do not happen |
| REQ-5 | Fiscalization | `src/cloud` site/legal entity config, edge config contracts | Scanned | Cloud supports the model, but Android config contract mismatch blocks delivery |
| REQ-6 | Pre-Authorization Orders | `src/cloud/FccMiddleware.Api/Controllers/PreAuthController.cs`, Android/Desktop pre-auth handlers and APIs | Scanned | Cloud tracking exists, but both edge implementations are functionally incomplete |
| REQ-7 | Normal Orders | Cloud ingestion, edge ingestion orchestrators, local transaction APIs | Scanned | Cloud path is present; Android and desktop fallback paths are incomplete |
| REQ-8 | Pre-Auth Reconciliation | `src/cloud/FccMiddleware.Application/Reconciliation` | Scanned | Reconciliation is effectively disabled for synced sites unless manually seeded |
| REQ-9 | Odoo Order Creation / Poll-Ack | Cloud transaction polling/ack, desktop local API, Android status sync | Scanned | Offline and sync-state flows are broken or contract-mismatched |
| REQ-10 | Payload Normalization and Field Mapping | Adapter factories, cloud config mappings, nozzle/pump flows | Scanned | Mapping model exists, but vendor/runtime coverage is incomplete |
| REQ-11 | Master Data Synchronization | Cloud master-data sync handlers and contracts | Scanned | Idempotent sync exists, but required requirement fields are missing |
| REQ-12 | Transaction Ingestion Modes | Cloud ingest/upload, desktop ingestion, Android ingestion | Scanned | `CLOUD_DIRECT` cloud path exists; edge poll/relay path is only partially operational |
| REQ-13 | Duplicate Detection | Cloud upload/ingest pipeline, edge buffer managers | Scanned | No major static design defect found; runtime verification still needed |
| REQ-14 | Audit Trail and Transaction Logging | Cloud outbox/raw payload archiving, edge audit logs, portal audit UI | Scanned | Baseline logging exists; DLQ/retry operationalization is incomplete |
| REQ-15 | Edge Agent Responsibilities | Android HHT app, local API, sync/config/telemetry workers | Scanned | Android agent is not wired for real FCC/config/status operation |
| REQ-16 | Error Handling, Retry, Alerting, DLQ | DLQ controller, portal surfaces, worker retry code | Scanned | Portal DLQ management exists, but retry is not a real replay path |
| REQ-17 | Multi-Tenancy and Data Isolation | Cloud tenant filters, auth claims, portal scoping | Scanned | No major static defect found; design is materially present |

## System ownership and end-to-end functional flow

### Who does what

- Odoo / attendants:
  Create pre-auth orders, poll pending transactions, create Odoo orders, acknowledge processed transaction IDs.
- Databricks integration:
  Sync legal entities, sites, pumps, nozzles, products, and operators into cloud master data.
- Cloud middleware:
  Owns tenant-scoped master data, FCC push ingestion, deduplication, pre-auth tracking, reconciliation, Odoo polling APIs, agent registration/config, telemetry ingestion, and portal APIs.
- Edge agent:
  Should own LAN-side FCC communication, local buffering, offline Odoo local API, catch-up polling, pre-auth relay, and store-and-forward to cloud.
- Portal users:
  Monitor transactions, reconciliation exceptions, agents, site/FCC config, audit, and DLQ actions.

### Required data setup before the system can function

- Legal entity with country code, currency, timezone, tax authority, fiscalization defaults.
- Site linked to a legal entity with operating mode, connectivity mode, company tax ID, operator tax ID for dealer-operated sites, and active status.
- FCC configuration per site with vendor, host, port, credentials, transaction mode, ingestion mode, and heartbeat/poll settings.
- Pump and nozzle mappings with Odoo pump/nozzle numbers mapped to FCC pump/nozzle numbers and product assignments.
- Product master data for nozzle and transaction normalization.
- Odoo API key / legal entity scoping for cloud polling and acknowledgement.
- Edge device registration with site assignment, device ID, cloud base URL, and issued device tokens.

### End-to-end flow summary

#### 1. Master data and configuration setup

- Databricks calls cloud master-data sync APIs for legal entities, sites, pumps, nozzles, products, and operators.
- Portal admins then enrich runtime settings not sourced from Odoo, mainly FCC connection details and tolerances.
- Cloud builds per-agent configuration snapshots and serves them from the agent config endpoint.
- Edge agents are expected to apply config locally and use it to talk to the FCC and cloud.

#### 2. Pre-auth flow

- Odoo POS should call the edge local API.
- Edge resolves Odoo pump/nozzle to FCC pump/nozzle using local mappings.
- Edge sends pre-auth to the FCC over LAN.
- Edge asynchronously forwards the pre-auth record to cloud for reconciliation tracking.
- Cloud stores the pre-auth lifecycle and later matches the final dispense transaction.

#### 3. Normal order flow

- Primary path: FCC pushes transactions directly to cloud.
- Safety-net path: edge agent polls the FCC over LAN and uploads missed transactions to cloud.
- Cloud normalizes, deduplicates, stores as `PENDING`, and exposes them to Odoo polling.

#### 4. Odoo poll and acknowledge flow

- Odoo polls cloud for pending transactions in online mode.
- When internet is unavailable, Odoo should poll the edge local API instead.
- After Odoo creates orders, it acknowledges the transaction IDs back to cloud.
- Edge should poll cloud for `SYNCED_TO_ODOO` state and suppress already-synced records from local offline APIs.

#### 5. Monitoring and recovery flow

- Portal users monitor dashboard, transactions, reconciliation, agents, sites, audit, and DLQ.
- DLQ should capture permanently failed items, allow retry/discard, and expose retry history.
- Telemetry should give site/device/FCC/buffer health for operations.

## Detailed findings and recommended actions

### REQ-1: Legal Entity and Country Configuration

**Expected flow**

- Legal entities should carry country context, fiscalization defaults, and Odoo linkage for all downstream routing.

**Current implementation**

- Cloud legal entity sync exists in `src/cloud/FccMiddleware.Application/MasterData/SyncLegalEntitiesHandler.cs`.
- Contract exists in `src/cloud/FccMiddleware.Contracts/MasterData/LegalEntitySyncRequest.cs`.
- Entity exists in `src/cloud/FccMiddleware.Domain/Entities/LegalEntity.cs`.

**Findings**

- High: the requirement field `odooCompanyId` is not implemented anywhere in the codebase. It only appears in `Requirements.md:143`.
- High: `LegalEntitySyncRequest` exposes optional `Country`, but `SyncLegalEntitiesHandler` ignores it and instead maps `CountryCode = i.Code` in `SyncLegalEntitiesHandler.cs:106-145`.
- Medium: `LegalEntity` does not store `odooCompanyId`, `countryName`, or a distinct business code vs country code split, so the requirement model is collapsed into a smaller shape than specified.

**Impact**

- Traceability back to Odoo company records is missing.
- Country identity can be conflated with internal business code, which becomes risky once codes diverge from ISO country codes.
- Tenant routing and fiscalization defaults can become ambiguous in multi-country rollout.

**Recommended action**

- Add explicit `OdooCompanyId` and, if needed, `BusinessCode` and `CountryCode` separation to the domain model and sync contract.
- Update `SyncLegalEntitiesHandler` to map `Country` to a distinct country code field instead of overloading `Code`.
- Add integration tests covering non-ISO entity codes and Odoo company traceability.

### REQ-2: Site Configuration and Operating Modes

**Expected flow**

- Site sync should fully populate the fields that later control fiscalization, reconciliation, connectivity, and agent behavior.

**Current implementation**

- `Site` includes `SiteUsesPreAuth` and reconciliation tolerance fields in `src/cloud/FccMiddleware.Domain/Entities/Site.cs:11-33`.
- Site sync contract and handler live in `src/cloud/FccMiddleware.Contracts/MasterData/SiteSyncRequest.cs` and `src/cloud/FccMiddleware.Application/MasterData/SyncSitesHandler.cs`.
- Portal site patch contract lives in `src/cloud/FccMiddleware.Contracts/Portal/PortalSiteContracts.cs`.

**Findings**

- Critical: `SiteUsesPreAuth` exists on the entity (`Site.cs:16`) but is absent from the Databricks sync contract (`SiteSyncRequest.cs:20-58`) and absent from the sync handler change/create paths (`SyncSitesHandler.cs:134-200`).
- High: portal site update contracts also expose tolerance and fiscalization overrides only; there is no API surface to maintain `SiteUsesPreAuth` (`PortalSiteContracts.cs:97-118`).

**Impact**

- Cloud has no reliable way to know which sites should participate in pre-auth reconciliation.
- Production behavior depends on manual database seeding or test data rather than supported configuration flow.

**Recommended action**

- Add `siteUsesPreAuth` to the master-data contract or explicitly introduce a portal-managed site capability flag.
- Surface the field in portal read/write DTOs and agent config payloads where relevant.
- Add a migration/backfill plan for already-synced sites.

### REQ-3: FCC Registration and Assignment

**Expected flow**

- Each site should be able to run with the configured vendor adapter across cloud ingestion and edge LAN operations.

**Current implementation**

- Cloud API registers DOMS, RADIX, and PETRONITE adapters in `src/cloud/FccMiddleware.Api/Program.cs:348-374`.
- Cloud worker registers DOMS and a stub RADIX only in `src/cloud/FccMiddleware.Worker/Program.cs:57-77`.
- Desktop adapter factory lives in `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs`.
- Android adapter factory lives in `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/FccAdapterFactory.kt`.

**Findings**

- High: Advatec is in requirements but is not implemented. Desktop throws `NotImplementedException` for `FccVendor.Advatec` in `FccAdapterFactory.cs:38-40`.
- High: Android only treats `DOMS` as implemented. `RADIX` and `PETRONITE` classes exist in the `when`, but the factory blocks all non-DOMS vendors via `IMPLEMENTED_VENDORS` in `FccAdapterFactory.kt:23-37`.
- High: cloud worker still throws for RADIX in background services (`src/cloud/FccMiddleware.Worker/Program.cs:75-76`), so worker-side vendor parity is not achieved.
- High: desktop pre-auth handling ignores configured vendor and always creates a DOMS adapter in `src/desktop-edge-agent/src/FccDesktopAgent.Core/PreAuth/PreAuthHandler.cs:190-196` and `:361`.
- Medium: desktop `DomsJplAdapter` is created with fallback values that are not tenant-safe defaults, including `legalEntityId = config.SiteCode` and hardcoded `ZAR` / `Africa/Johannesburg` in `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs:67-75`.

**Impact**

- Multi-vendor rollout is not operationally complete.
- Desktop pre-auth can silently target the wrong protocol even when the site is configured for another vendor.
- Worker vs API behavior can diverge by vendor.

**Recommended action**

- Make vendor support matrix explicit and block unsupported vendors at configuration time.
- Remove the hardcoded DOMS pre-auth path from desktop and always use `config.FccVendor`.
- Bring cloud worker and edge factories onto a shared supported-vendor registry.
- Add adapter contract tests per vendor for cloud, desktop, and Android.

### REQ-4: Connected vs Disconnected Mode

**Expected flow**

- Edge agents should detect internet and FCC reachability and move between connected, offline, and disconnected behavior accordingly.

**Current implementation**

- Android connectivity is composed in `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/di/AppModule.kt`.
- Desktop connectivity is more substantially wired.

**Findings**

- Critical: Android DI registers both connectivity probes as hardcoded `false` in `AppModule.kt:103-118`.
- Critical: Android ingestion, pre-auth, and local API are instantiated with `adapter = null`, `config = null`, and `fccAdapter = null` in `AppModule.kt:146-163` and `:209-223`.

**Impact**

- Android agent boots effectively in a permanent degraded state.
- Real FCC reachability, cloud reachability, and connected/offline transitions cannot happen.

**Recommended action**

- Wire the actual adapter factory and config manager into connectivity probes.
- Add startup health assertions that fail fast when adapter/config are still null after registration/config load.
- Add integration tests covering `FULLY_ONLINE`, `INTERNET_DOWN`, and `FCC_UNREACHABLE`.

### REQ-5: Fiscalization

**Expected flow**

- Fiscalization defaults should flow from legal entity and site config into edge/cloud transaction handling.

**Current implementation**

- Cloud site and legal entity models carry fiscalization flags.
- Cloud site config response also includes fiscalization settings in `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs:128-134`.

**Findings**

- High: Android cannot reliably consume cloud config because its DTO expects fields such as `compatibility`, `agent`, `fccConnection`, `polling`, and `api`, while cloud returns `sourceRevision`, `identity`, `fcc`, `localApi`, `mappings`, and `rollout` in `SiteConfigResponse.cs:7-168` vs `EdgeAgentConfigDto.kt:15-108`.
- High: `ConfigPollWorker` decodes cloud JSON directly into `EdgeAgentConfigDto` in `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/ConfigPollWorker.kt:175-184`, so this mismatch is a runtime blocker, not just a naming inconsistency.

**Impact**

- Android agents cannot reliably receive fiscalization, FCC, buffer, or local API settings from cloud.
- All downstream fiscalization behavior on Android is unstable or dead on arrival.

**Recommended action**

- Make Android consume the same schema that cloud emits, ideally from a generated/shared contract.
- Add config contract compatibility tests that serialize on cloud and deserialize on Android/Desktop.

### REQ-6: Pre-Authorization Orders

**Expected flow**

- Odoo calls the edge local API, edge authorizes on FCC over LAN, and cloud tracking happens asynchronously.

**Current implementation**

- Cloud pre-auth tracking endpoint exists in `src/cloud/FccMiddleware.Api/Controllers/PreAuthController.cs`.
- Android pre-auth domain logic exists in `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/preauth/PreAuthHandler.kt`.
- Desktop pre-auth domain logic exists in `src/desktop-edge-agent/src/FccDesktopAgent.Core/PreAuth/PreAuthHandler.cs`.

**Findings**

- Critical: Android pre-auth handler explicitly returns an error when no FCC adapter is configured, and DI currently injects `null` (`PreAuthHandler.kt:127-132`, `AppModule.kt:154-163`).
- Critical: desktop pre-auth API endpoints are still `501 Not Implemented` in `src/desktop-edge-agent/src/FccDesktopAgent.Api/Endpoints/PreAuthEndpoints.cs:19-46`, even though there is a core handler.
- Medium: Android pre-auth persistence does not currently store unit price, forcing later cloud forwarding to fabricate it.

**Impact**

- Android cannot perform real LAN pre-auth in the current wiring.
- Desktop cannot expose the intended local API to Odoo POS despite having some underlying core logic.

**Recommended action**

- Wire Android local API to a live adapter and config source.
- Replace desktop API stubs with calls into `IPreAuthHandler`.
- Add end-to-end tests: Odoo request -> edge local API -> FCC adapter stub -> cloud forward record.

### REQ-7: Normal Orders

**Expected flow**

- FCC pushes to cloud, edge polls as a safety net, and all transactions land in cloud as deduplicated `PENDING` records.

**Current implementation**

- Cloud ingest/upload APIs are present in `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs`.
- Desktop ingestion orchestrator is implemented in `src/desktop-edge-agent/src/FccDesktopAgent.Core/Ingestion/IngestionOrchestrator.cs`.
- Android ingestion orchestrator exists in `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ingestion/IngestionOrchestrator.kt`.

**Findings**

- Critical: Android ingestion orchestrator is registered with `adapter = null` and `config = null` in `AppModule.kt:146-153`, and the orchestrator no-ops when either is missing (`IngestionOrchestrator.kt:119-127`, `:170-178`).
- High: desktop local transaction APIs for offline Odoo polling are not implemented: list/detail/ack endpoints return `501` in `src/desktop-edge-agent/src/FccDesktopAgent.Api/Endpoints/TransactionEndpoints.cs:21-43`.

**Impact**

- Android cannot perform the required safety-net LAN polling.
- Desktop cannot serve buffered transactions to Odoo in offline mode.

**Recommended action**

- Wire Android poller to live adapter/config.
- Complete desktop transaction endpoints on top of the existing buffer/query services.
- Add online/offline sequence tests covering push miss -> edge catch-up upload -> Odoo poll.

### REQ-8: Pre-Auth Reconciliation and Volume Adjustment

**Expected flow**

- Cloud matches dispense transactions to pre-auth records for sites that use pre-auth, applies tolerance rules, and flags exceptions.

**Current implementation**

- Matching service is in `src/cloud/FccMiddleware.Application/Reconciliation/ReconciliationMatchingService.cs`.
- Site pre-auth enablement is stored on the `Site` entity.

**Findings**

- Critical: `ReconciliationMatchingService` skips reconciliation entirely when `SiteUsesPreAuth` is false (`ReconciliationMatchingService.cs:53-67`), but sync/config flows never populate that field for real sites.
- Medium: Android pre-auth cloud forwarding hardcodes `unitPrice = 1` in `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/PreAuthCloudForwardWorker.kt:247-259`.

**Impact**

- Pre-auth reconciliation can be disabled for all Databricks-synced sites by default.
- Amount variance and audit detail for Android-originated pre-auth records can be materially wrong.

**Recommended action**

- Fix site-level pre-auth configuration propagation first; without that, reconciliation is functionally incomplete.
- Persist actual unit price in Android pre-auth records and forward the real value.
- Add reconciliation integration tests with Databricks-synced sites, matched dispenses, unmatched dispenses, and variance thresholds.

### REQ-9: Odoo Order Creation / Poll-Ack

**Expected flow**

- Odoo polls cloud in online mode, polls edge in offline mode, acknowledges processed transactions, and edge learns `SYNCED_TO_ODOO` state.

**Current implementation**

- Cloud online poll/ack APIs are present in `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs`.
- Desktop has a status poll worker for `SYNCED_TO_ODOO`.
- Android has its own cloud client and status models.

**Findings**

- Critical: Android status polling calls `GET /api/v1/transactions/synced-status?ids=...` and expects `statuses[]` entries (`CloudApiClient.kt:102-113`, `:240-248`; `CloudApiModels.kt:145-160`), but cloud actually requires `since=` and returns `FccTransactionIds` only (`TransactionsController.cs:669-709`, `SyncedStatusResponse.cs:1-10`).
- Critical: desktop offline Odoo API is incomplete because transaction list/detail/ack endpoints are still stubbed (`TransactionEndpoints.cs:21-43`).

**Impact**

- Android cannot correctly synchronize local buffer suppression with cloud acknowledgements.
- Desktop offline Odoo mode, which is explicitly required, is not end-to-end usable.

**Recommended action**

- Align Android status polling to the cloud API or change the cloud API and all clients together.
- Implement desktop offline transaction read and acknowledge endpoints.
- Add a shared test matrix for online cloud poll, offline edge poll, acknowledge, and resync suppression.

### REQ-10: Payload Normalization and Field Mapping

**Expected flow**

- Vendor adapters own hardcoded protocol mappings, while deployment-specific overrides come from configuration and nozzle/product mappings.

**Current implementation**

- Cloud config includes `Mappings` in `SiteConfigResponse.cs:136-160`.
- Edge nozzle mapping usage exists in Android and desktop pre-auth handlers.

**Findings**

- High: Android config mismatch means nozzle/product/pump mapping overrides from cloud are not reliably applied.
- High: vendor implementation coverage is incomplete, so normalization behavior is not available for the full required vendor set.

**Impact**

- Mapping correctness depends on vendor and project.
- New deployments can appear configured in cloud while edge runtimes remain unable to consume the mapping payload.

**Recommended action**

- Unify adapter configuration contracts across cloud, Android, and desktop.
- Add canonical normalization golden-file tests per vendor and per deployment override.

### REQ-11: Master Data Synchronization

**Expected flow**

- Databricks master-data sync should be idempotent and supply every field needed later by processing flows.

**Current implementation**

- Cloud sync handlers exist for legal entities and sites.

**Findings**

- High: site sync does not carry pre-auth participation.
- High: legal entity sync does not carry `odooCompanyId` and ignores distinct `Country`.
- Medium: required downstream runtime fields are split across Databricks sync and portal-only patches without a clear authoritative ownership model.

**Impact**

- Master data appears to sync successfully while downstream operational features remain underconfigured.

**Recommended action**

- Define a single ownership matrix per field: Odoo/Databricks vs portal admin.
- Reject incomplete master data earlier when downstream-required fields are missing for enabled features.

### REQ-12: Transaction Ingestion Modes (Pull / Push / Hybrid)

**Expected flow**

- `CLOUD_DIRECT` should use cloud push with edge catch-up polling; `RELAY` and `BUFFER_ALWAYS` should use edge as the primary path.

**Current implementation**

- Cloud ingest and upload endpoints exist.
- Desktop polling logic exists.
- Android ingestion orchestrator models all three modes but is not wired.

**Findings**

- Critical: Android edge cannot act as a real poller/relay because adapter/config are null in DI.
- Medium: desktop config hot reload only maps a subset of cloud config fields (`ConfigManager.cs:207-245`), while runtime still depends on fields like `FccBaseUrl`, `FccVendor`, and `FccApiKey` in `AgentConfiguration.cs:18-81`.
- Medium: desktop registration post-config only overlays `DeviceId`, `SiteId`, and `CloudBaseUrl` (`RegistrationManager.cs:105-118`), so FCC connection details may never be updated from cloud config.

**Impact**

- Edge-based ingestion modes are not production-ready, especially on Android.
- Desktop may register successfully yet remain unable to poll the FCC with the actual site config.

**Recommended action**

- Complete cloud-to-edge config mapping for all required FCC connection fields.
- Add mode-specific startup validation for `CLOUD_DIRECT`, `RELAY`, and `BUFFER_ALWAYS`.

### REQ-13: Duplicate Detection

**Current assessment**

- No major static defect was found in the high-level duplicate-detection design.
- Cloud upload and ingest paths both model accepted/duplicate/rejected outcomes, and desktop/Android buffering layers also assume deduplication.

**Residual risk**

- Because runtime tests could not be executed here, cross-path duplicate behavior for `FCC push + edge catch-up upload` still needs automated verification.

**Recommended action**

- Add end-to-end duplicate tests across both ingress paths using the same `fccTransactionId + siteCode`.

### REQ-14: Audit Trail and Transaction Logging

**Current assessment**

- Cloud outbox/event publishing and raw payload preservation are materially present.
- Edge agents also write local audit records.

**Findings**

- Medium: operational recovery artifacts are inconsistent because the DLQ control surface exists, but a real dead-letter production and replay lifecycle is not evident in the main runtime paths.

**Impact**

- Auditability is stronger than recoverability; operators may see failure records without a real recovery path behind them.

**Recommended action**

- Define where failed ingests/forwards/retries become `DeadLetterItem` records and add explicit producer logic plus traceability back to the originating transaction/pre-auth.

### REQ-15: Edge Agent Responsibilities

**Expected flow**

- Android HHT agent should own LAN FCC communication, local API, buffering, config polling, telemetry, and recovery.

**Current implementation**

- Android local API server exists in `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/api/LocalApiServer.kt`.
- DI wiring is in `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/di/AppModule.kt`.

**Findings**

- Critical: Android local API is started with `enableLanApi = false` and `fccAdapter = null` in `AppModule.kt:209-223`.
- Critical: Android config polling cannot deserialize the cloud contract reliably.
- High: Android upload/status contracts diverge from cloud contracts.

**Impact**

- The Android HHT project is architecturally outlined but not operationally complete.

**Recommended action**

- Treat Android edge completion as a dedicated milestone, not as a finished module.
- Close DI wiring, config schema, upload schema, and status schema gaps before field rollout.

### REQ-16: Error Handling, Retry, Alerting, and DLQ

**Expected flow**

- Failures should enter DLQ, retries should re-submit through the real processing pipeline, and operators should have accurate status.

**Current implementation**

- Portal DLQ controller and routes are present.

**Findings**

- High: `Retry` and `RetryBatch` only mutate history/status through `ApplyRetry`; they do not actually requeue or replay payloads (`src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:175-208`, `:249-352`).
- High: repository scan found DLQ reads and portal test seeding, but no obvious runtime producer path adding failed records into `DeadLetterItems` in the main code paths (`rg` over `src/cloud` only found controller/dashboard/config references and one integration-test seeding location).

**Impact**

- Operators can believe a retry happened when only metadata changed.
- DLQ screens may exist without reliably receiving real failures.

**Recommended action**

- Introduce an explicit replay service and have DLQ retry call that service.
- Add producer logic from ingestion/upload/pre-auth failures into `DeadLetterItems`.
- Separate `RETRY_QUEUED`, `RETRY_SUCCEEDED`, and `RETRY_FAILED` states instead of directly marking records as retrying without dispatch.

### REQ-17: Multi-Tenancy and Data Isolation

**Current assessment**

- No major static defect was found in the tenant isolation design.
- Cloud has tenant context, query filters, auth claims, and portal access scoping across controllers and persistence layers.

**Evidence**

- Tenant context registration is in `src/cloud/FccMiddleware.Api/Program.cs:302-305`.
- Tenant middleware is added in `src/cloud/FccMiddleware.Api/Program.cs:426`.
- Global tenant query filters are described and applied in `src/cloud/FccMiddleware.Infrastructure/Persistence/FccMiddlewareDbContext.cs`.

**Residual risk**

- This still needs integration validation for mixed-tenant access through every portal/API route, but the static structure is materially present.

## Cross-project defects outside a single requirement

### Cloud <-> Android contract mismatch

- Critical: Android `CloudUploadResponse` expects `siteCode`, `id`, and nested `error` (`CloudApiModels.kt:97-129`), while cloud returns `transactionId`, `originalTransactionId`, `errorCode`, and `errorMessage` (`UploadRecordResult.cs:6-31`).
- Critical: Android synced-status contract mismatch blocks Odoo sync-state suppression.
- Critical: Android config contract mismatch blocks runtime provisioning.

**Recommended action**

- Move these contracts to a single shared schema source and generate clients/models from it.

### Cloud <-> Desktop contract/data mismatch

- High: desktop upload model expects `siteCode`, `id`, and nested `error` in `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/Models/UploadModels.cs:35-67`, which does not match the cloud contract in `UploadRecordResult.cs:6-31`.
- High: desktop telemetry publishes `LegalEntityId = config.SiteId` in `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/TelemetryReporter.cs:179-185`.
- High: desktop cloud upload payload also sets `LegalEntityId = config.SiteId` in `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/CloudUploadWorker.cs:341-348`.

**Impact**

- Desktop sync and telemetry can send semantically wrong tenant identifiers and parse cloud replies incorrectly.

**Recommended action**

- Carry true legal entity ID in registration/config state and use it consistently.
- Align desktop response models with the actual cloud contract.

### Portal static validation findings

- Medium: `npm run lint` fails in `src/portal` with 79 errors.
- Main categories:
  - template accessibility violations (`label-has-associated-control`) across audit, DLQ, site config, transactions, settings, reconciliation, and edge-agent screens
  - template equality rule violations (`==` / `!=`) in reconciliation, settings, and FCC config templates
  - interaction accessibility issues in `src/portal/src/app/features/edge-agents/agent-list.component.ts`
  - unused variables in `src/portal/src/app/core/auth/role.guard.ts` and `src/portal/src/app/features/reconciliation/reconciliation-list.component.ts`

**Impact**

- Portal CI quality gates are failing.
- Accessibility and template consistency are below release quality for several operations screens.

**Recommended action**

- Clear the current lint backlog before adding more portal behavior.
- Add lint enforcement to PR checks if it is not already mandatory.

## Improvement suggestions

- Create a single source of truth for shared contracts between cloud, Android, desktop, and portal DTO consumers.
- Add requirement-traceable integration tests for the four most important flows:
  1. Databricks sync -> portal config -> agent config apply
  2. Odoo pre-auth -> edge -> FCC -> cloud pre-auth tracking -> cloud reconciliation
  3. FCC push + edge catch-up poll -> cloud dedup -> Odoo poll -> acknowledge
  4. Cloud unavailable -> edge local API offline polling -> reconnect -> sync-state suppression
- Introduce startup readiness checks in both edge agents so null adapters/configs cannot silently boot into fake-online states.
- Publish a field ownership matrix: which fields come from Databricks, portal admin, device registration, or runtime discovery.
- Replace placeholder `TODO` and `501` surfaces in shipping paths with explicit feature flags or unsupported capability responses so rollout state is visible.

## Overall priority order

1. Fix site pre-auth enablement propagation and reconciliation gating.
2. Align shared cloud/Android/desktop contracts for config, upload, and synced-status.
3. Finish Android runtime wiring for connectivity, ingestion, pre-auth, and local API.
4. Finish desktop local API endpoints and remove vendor hardcoding from pre-auth.
5. Implement real DLQ production and replay behavior.

## Validation executed

- Static requirements-to-code analysis across all four `src` projects.
- Portal validation: `npm run lint` in `src/portal` failed with 79 errors.
- Cloud, desktop, and Android runtime tests were not executed here because `dotnet` and `java` toolchains are unavailable in the current environment.
