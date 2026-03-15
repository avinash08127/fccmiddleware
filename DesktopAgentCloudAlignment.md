# Desktop Agent / Cloud Alignment Audit

Audit date: 2026-03-15

## Scope

This audit covers the desktop edge agent under `src/desktop-edge-agent` against the cloud backend under `src/cloud`.

Scanned areas:

- All active desktop cloud calls in registration, sync, telemetry, command, connectivity, and startup flows
- Desktop request/response DTOs under `FccDesktopAgent.Core`
- Cloud controllers, contracts, and request/response handling for every agent-facing endpoint the desktop agent calls
- Cloud-delivered `SiteConfig` because it is the largest shared contract surface and directly controls runtime behavior
- HA-related registration/config paths because they are contract-sensitive and affect authoritative cloud writes

This is a static code audit. I did not execute end-to-end integration tests for this report.

## Executive Summary

- I found 10 active desktop-to-cloud endpoints/probes and 5 additional cloud-facing DTO groups/endpoints that exist on the desktop side but are not wired.
- The two highest-risk breaking mismatches are:
  - the token refresh contract, where the desktop agent omits the required `deviceToken`
  - the cloud config contract, where the desktop `SiteConfig` fork has materially drifted from `FccMiddleware.Contracts.Config.SiteConfigResponse`
- Multi-agent HA is not cloud-aligned on desktop today. The cloud sends HA config and peer-directory data, but the desktop agent only applies a small subset of it and does not register peer API metadata back to the cloud.
- Transaction upload is only partially aligned. Required business data mostly reaches the cloud, but `fccCorrelationId` is dropped because the desktop agent serializes `correlationId` instead, and batch-level idempotency is effectively disabled on retry.
- I also found several functional bugs that are not pure DTO drift:
  - config `404` is treated the same as `304`
  - bootstrap config apply failures are ignored during registration
  - status polling can miss synced transactions because the cursor is advanced unsafely
  - peer-directory version hints are ignored on some authenticated responses

## Endpoint Coverage

| Endpoint | Desktop caller | Verdict | Notes |
| --- | --- | --- | --- |
| `GET /health` | `ConnectivityManager`, `SetupOrchestrator` | Aligned | Simple probe against cloud health middleware. |
| `POST /api/v1/agent/register` | `DeviceRegistrationService` | Partial | Route exists and base request shape aligns, but returned bootstrap config does not align with the desktop config model and desktop does not send peer API metadata. |
| `POST /api/v1/agent/token/refresh` | `DeviceTokenProvider` | Mismatch | Desktop request body is incompatible with the cloud contract. |
| `GET /api/v1/agent/version-check` | `VersionCheckService` | Partial | Request/response shape aligns, but auth/error handling is too loose and decommission/reprovision states are swallowed by fail-open logic. |
| `GET /api/v1/agent/config` | `ConfigPollWorker` | Mismatch | Desktop `SiteConfig` fork is materially out of sync with `SiteConfigResponse`, and `404` handling is wrong. |
| `POST /api/v1/transactions/upload` | `CloudUploadWorker` | Mismatch | Wrapper route exists, but the serialized transaction model is not the cloud contract and retry idempotency is not honored. |
| `GET /api/v1/transactions/synced-status` | `StatusPollWorker` | Partial | DTO shape aligns, but cursor/watermark handling is unsafe and peer-directory version hints are ignored. |
| `POST /api/v1/agent/telemetry` | `TelemetryReporter` | Partial | Payload is wire-compatible, but peer-directory version hints are ignored and identity fields depend on the drifted local config model. |
| `GET /api/v1/agent/commands` | `CommandPollWorker` | Partial | Poll DTO aligns, but `404 FEATURE_DISABLED` is treated as a generic transport failure. |
| `POST /api/v1/agent/commands/{id}/ack` | `CommandPollWorker` | Partial | Ack DTO aligns, but `404/400` are collapsed into generic transport failure semantics. |
| `POST /api/v1/agent/diagnostic-logs` | None | Not wired | Cloud endpoint exists; desktop UI shows the route, but there is no desktop caller. |
| `POST /api/v1/sites/{siteCode}/bna-reports` | None | Not wired | Desktop has DTOs only. |
| `POST /api/v1/sites/{siteCode}/pump-totals` | None | Not wired | Desktop has DTOs only. |
| `POST /api/v1/sites/{siteCode}/pump-control-history` | None | Not wired | Desktop has DTOs only. |
| `POST /api/v1/sites/{siteCode}/price-snapshots` | None | Not wired | Desktop has DTOs only. |

## Model Coverage

| Model group | Verdict | Notes |
| --- | --- | --- |
| Registration request/response | Partial | Core registration fields align, but peer API metadata is never populated and the embedded `siteConfig` payload drifts badly. |
| Registration error modeling | Partial | Desktop only models a subset of cloud error codes; `CONFIG_NOT_FOUND` and `REGISTRATION_BLOCKED` are not represented. |
| Refresh token request/response | Mismatch | Response aligns; request does not. Desktop sends only `refreshToken`, while cloud requires both `refreshToken` and `deviceToken`. |
| Version-check request/response | Aligned | Request and response shape align at the wire level. |
| `SiteConfig` / `SiteConfigResponse` | Mismatch | This is the largest contract drift in the repo. |
| Upload request wrapper | Partial | Wrapper fields align, but the nested transaction payload does not. |
| Upload transaction record | Mismatch | Desktop serializes `CanonicalTransaction`, not `UploadTransactionRecord`. |
| Upload response/result | Partial | Desktop expects the contract shape, but the cloud implementation drops `errorMessage`. |
| Synced-status response | Aligned | Shape aligns, but the endpoint contract is too weak for the client’s cursor logic. |
| Telemetry payload | Partial | Wire-compatible, but local types are looser and some identity values depend on drifted config semantics. |
| Agent command poll/ack DTOs | Aligned | JSON shape and enum values align. The problem is endpoint status handling, not the DTOs. |
| Site-data upload DTOs | Aligned but unused | Local models match cloud contracts closely, but the desktop agent never calls those endpoints. |

## Findings

### 1. [High] Desktop token refresh is not compatible with the cloud contract

The cloud refresh endpoint requires both `refreshToken` and the current or expired `deviceToken` in the JSON body. The desktop agent only sends `refreshToken`.

That means the desktop path will hit `401 DEVICE_TOKEN_REQUIRED` or `401 DEVICE_TOKEN_INVALID` whenever the cloud strictly enforces the documented contract. The desktop client then misclassifies that as "refresh token expired or revoked" and raises `RefreshTokenExpiredException`, which is the wrong recovery path.

The desktop client also treats every `403` from refresh as decommissioning, but the cloud refresh endpoint also returns `403` for `DEVICE_PENDING_APPROVAL` and `DEVICE_QUARANTINED`.

Impact:

- token refresh can fail permanently even when the refresh token is valid
- pending-approval and quarantined devices are misclassified as decommissioned
- every higher-level cloud worker that relies on token refresh inherits the wrong behavior

References:

- Desktop sends only `refreshToken`: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/DeviceTokenProvider.cs:139-162`
- Cloud contract requires `DeviceToken`: `src/cloud/FccMiddleware.Contracts/Registration/RefreshTokenRequest.cs:5-15`
- Cloud controller validates `DeviceToken` and returns `403` for multiple non-active statuses: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:504-547`

### 2. [High] Desktop `SiteConfig` is materially out of sync with cloud `SiteConfigResponse`

The desktop agent does not consume `FccMiddleware.Contracts.Config.SiteConfigResponse`. It keeps an independent `SiteConfig` copy, and that copy has drifted in multiple breaking ways.

Breaking shape differences:

- desktop root config is missing `sourceRevision`
- cloud `identity` contains `legalEntityCode`, `siteId`, `siteName`, `timezone`, and `currencyCode`; desktop `identity` does not
- desktop incorrectly places `timezone` and `currency` under `site`, while cloud sends them under `identity`
- cloud `site` contains `isActive`, `siteUsesPreAuth`, `odooSiteId`, `companyTaxPayerId`, `operatorName`, and `operatorTaxPayerId`; desktop `site` does not
- cloud `sync` contains `syncedStatusPollIntervalSeconds`, `maxReplayBackoffSeconds`, `initialReplayBackoffSeconds`, `maxRecordsPerUploadWindow`, and `environment`; desktop `sync` does not
- cloud `buffer` contains `stalePendingDays`; desktop `buffer` does not
- cloud `localApi` contains `lanAllowCidrs`; desktop `localApi` does not
- desktop expects `fiscalization.vendor`, but cloud `FiscalizationDto` does not provide it
- desktop expects FCC fields that cloud does not send at all: `preAuthTimeoutSeconds`, `fiscalReceiptTimeoutSeconds`, `apiRequestTimeoutSeconds`, `webhookListenerPort`, and `advatecWebhookListenerPort`

Impact:

- valid cloud config can deserialize into a structurally incomplete desktop config
- desktop validation/runtime code reads fields from the wrong section
- several cloud-managed settings can never be delivered to desktop because the cloud contract does not carry the fields the desktop expects

References:

- Desktop config fork: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/SiteConfig.cs:8-247`
- Cloud contract: `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs:7-248`
- Cloud config builder sets timezone/currency under `Identity`, not `Site`: `src/cloud/FccMiddleware.Application/AgentConfig/GetAgentConfigHandler.cs:87-126`

### 3. [High] Cloud config drift already causes concrete desktop runtime failures

This is not just theoretical DTO drift. There are code paths that break today because desktop reads fields the cloud contract does not provide.

Examples:

- `DesktopFccRuntimeConfiguration.TryValidateSiteConfig()` requires DOMS TCP config to have `config.Site.Currency` and `config.Site.Timezone`, but cloud sends those values as `identity.currencyCode` and `identity.timezone`.
- `IngestionOrchestrator.ResolveFiscalizationService()` requires `siteConfig.Fiscalization.Vendor == "ADVATEC"`, but cloud `FiscalizationDto` has no `Vendor` property.
- `DeviceRegistrationService` ignores the `ApplyConfigAsync()` result during bootstrap registration and logs "Bootstrap site config applied" even if the config was rejected.

Impact:

- valid cloud DOMS config can be rejected by the desktop agent
- fiscalization service selection can never activate from cloud config alone
- provisioning can report success while leaving the device with an unapplied or rejected bootstrap config

References:

- DOMS TCP validation reads the wrong fields: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/DesktopFccRuntimeConfiguration.cs:57-66`
- Cloud sends timezone/currency under `Identity`: `src/cloud/FccMiddleware.Application/AgentConfig/GetAgentConfigHandler.cs:94-117`
- Desktop fiscalization service requires missing `Vendor`: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Ingestion/IngestionOrchestrator.cs:426-435`
- Cloud `FiscalizationDto` has no `Vendor`: `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs:208-214`
- Registration ignores config apply result: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Registration/DeviceRegistrationService.cs:138-146`

### 4. [High] Desktop only applies a small subset of the cloud config, so HA and other cloud-managed settings are not actually honored

Even when the config deserializes, `ConfigManager.ApplyHotReloadFields()` only copies a narrow subset of the cloud payload into runtime `AgentConfiguration`.

Notably, it does not apply:

- `sync.syncedStatusPollIntervalSeconds`
- `sync.environment`
- `buffer.stalePendingDays`
- most `localApi` settings other than `localhostPort`
- almost all `siteHa` settings other than `leaderEpoch`

That becomes critical because major desktop subsystems are gated on runtime HA flags:

- LAN peer listener waits for `SiteHaEnabled`
- heartbeat broadcaster waits for `SiteHaEnabled`
- replication worker requires `SiteHaEnabled` and `ReplicationEnabled`
- election logic requires `SiteHaEnabled`, `AutoFailoverEnabled`, and `RoleCapability`

But `ConfigManager` never copies those values from the cloud config. On top of that, the Kestrel peer API listener is configured at startup from static app configuration, not from the cloud config.

Impact:

- cloud-driven HA enablement is not actually applied on desktop
- role, priority, failover, and replication settings from cloud do not control the running agent
- the cloud and desktop can disagree about the device’s HA state and allowed write behavior

References:

- Desktop only applies a subset of fields: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/ConfigManager.cs:290-375`
- Cloud sends full `SiteHaDto`: `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs:163-198`
- Cloud builds per-device HA data into config responses: `src/cloud/FccMiddleware.Application/AgentConfig/GetAgentConfigHandler.cs:147-165`, `src/cloud/FccMiddleware.Application/AgentConfig/GetAgentConfigHandler.cs:300-329`
- LAN peer listener is gated on `SiteHaEnabled`: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Peer/LanPeerListener.cs:60-67`
- Heartbeat worker is gated on `SiteHaEnabled`: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Peer/HeartbeatWorker.cs:46-54`
- Replication is gated on `SiteHaEnabled` and `ReplicationEnabled`: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Replication/ReplicationSyncWorker.cs:48-55`
- Election is gated on `SiteHaEnabled`, `AutoFailoverEnabled`, and `RoleCapability`: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Replication/ElectionCoordinator.cs:62-73`
- Peer API Kestrel listener is configured from static `Agent` config at startup: `src/desktop-edge-agent/src/FccDesktopAgent.App/Program.cs:71-93`

### 5. [High] Desktop does not publish peer API registration metadata back to the cloud

The registration request model supports `peerApi` metadata, and the cloud config builder returns peer-directory entries using those persisted values. The desktop registration builder never fills `PeerApi`.

That means the cloud peer directory can contain desktop agents without a usable `peerApiBaseUrl` or `peerApiAdvertisedHost`. The desktop peer coordinator then receives empty peer URLs from the authoritative directory.

Impact:

- cloud-delivered peer directories are incomplete for desktop agents
- cross-agent HA behavior must fall back to LAN discovery instead of using the authoritative cloud directory
- peer-to-peer recovery, replication, and switchover paths are harder to trust

References:

- Desktop registration builder omits `PeerApi`: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Registration/DeviceInfoProvider.cs:29-45`
- Cloud registration contract supports `PeerApi`: `src/cloud/FccMiddleware.Contracts/Registration/DeviceRegistrationApiRequest.cs:41-60`
- Cloud peer directory returns persisted peer API metadata: `src/cloud/FccMiddleware.Application/AgentConfig/GetAgentConfigHandler.cs:310-327`
- Desktop peer directory bootstrap relies on `PeerApiBaseUrl`: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Peer/PeerCoordinator.cs:320-339`

### 6. [High] Transaction upload is not fully aligned with the cloud contract

There are three separate problems in the desktop upload path.

Problem A: wrong transaction model

The cloud expects `UploadTransactionRecord`, including optional `fccCorrelationId`. The desktop serializes `CanonicalTransaction` instead. That model uses `correlationId`, not `fccCorrelationId`, so the cloud never receives the FCC correlation identifier.

Problem B: batch-level idempotency is not actually used

The cloud upload handler supports cached batch results keyed by `uploadBatchId`, but the desktop generates a fresh GUID every upload attempt. Retries for the same pending batch therefore miss the cloud cache completely.

Problem C: cloud response contract is richer than the cloud implementation

The contract exposes `errorMessage`, the desktop client expects it, but the cloud application result and controller never populate it.

Impact:

- FCC correlation IDs are silently dropped during upload
- upload retries do more work than necessary and do not benefit from the intended idempotency cache
- rejected-record logs on desktop lose human-readable server error detail

References:

- Desktop upload builds a fresh `UploadBatchId` every attempt: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/CloudUploadWorker.cs:344-354`
- Desktop serializes `CanonicalTransaction` with `correlationId`: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/CloudUploadWorker.cs:357-382`, `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/CanonicalTransaction.cs:81-91`
- Cloud contract expects `FccCorrelationId`: `src/cloud/FccMiddleware.Contracts/Ingestion/UploadTransactionRecord.cs:45-55`
- Cloud controller reads `FccCorrelationId` and `OdooOrderId`: `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:821-840`
- Cloud batch-idempotency cache exists: `src/cloud/FccMiddleware.Application/Ingestion/UploadTransactionBatchHandler.cs:47-58`
- Cloud response contract exposes `ErrorMessage`: `src/cloud/FccMiddleware.Contracts/Ingestion/UploadRecordResult.cs:27-31`
- Cloud application result has no `ErrorMessage`: `src/cloud/FccMiddleware.Application/Ingestion/UploadTransactionBatchResult.cs:6-21`
- Cloud controller omits `ErrorMessage` from the response mapping: `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:852-861`

### 7. [Medium] Config polling conflates `404` and `304`, and loses peer-directory version hints on `304`

`ConfigPollWorker.SendPollRequestAsync()` returns `null` for both:

- `304 Not Modified`
- `404 Not Found`

The caller interprets `null` as "unchanged", so a missing config is treated like a healthy cache hit.

There is a second issue on the `304` path. The cloud emits `X-Peer-Directory-Version` on every authenticated response, but the desktop config poller returns early on `304` before calling `PeerDirectoryVersionHelper.CheckAndTrigger()`.

Impact:

- a missing config can be silently misreported as "no change"
- peer-directory staleness hints can be lost on valid `304` config responses

References:

- Cloud emits peer-directory version on every authenticated response: `src/cloud/FccMiddleware.Api/Infrastructure/PeerDirectoryVersionMiddleware.cs:7-36`
- Desktop returns `null` for both `304` and `404`: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/ConfigPollWorker.cs:125-165`

### 8. [Medium] Status polling can miss `SYNCED_TO_ODOO` records because the cursor is advanced unsafely

The cloud status endpoint uses an inclusive `since` filter and returns only FCC transaction IDs. It does not return a server watermark or per-record sync timestamps.

The desktop client advances `LastStatusSyncAt` to `DateTimeOffset.UtcNow` after every call, including empty responses. That creates a race window:

- desktop queries with `since = T0`
- cloud evaluates the query
- a transaction syncs at `T1`
- desktop stores `LastStatusSyncAt = T2` where `T2 > T1`
- next poll uses `since = T2`, so the transaction synced at `T1` is never returned

Impact:

- some cloud-synced transactions can be permanently missed by the desktop agent
- local buffer state can lag behind actual cloud/Odoo sync state

References:

- Cloud endpoint is inclusive on `since` and returns only IDs: `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:889-924`
- Desktop updates the cursor to current wall clock after every poll: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/StatusPollWorker.cs:86-100`

### 9. [Medium] Peer-directory version hints are only consumed on some desktop endpoints

The cloud backend emits `X-Peer-Directory-Version` on every authenticated agent response. The desktop agent only checks that header on:

- config poll `200`
- command poll `200`
- upload `200`

It does not check the header on:

- config poll `304`
- status poll
- telemetry
- version-check

Impact:

- cloud-driven HA staleness propagation is weaker than intended
- desktop agents can take longer than necessary to notice peer-directory changes

References:

- Shared helper exists: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/PeerDirectoryVersionHelper.cs:6-43`
- Config poll checks it only on `200`: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/ConfigPollWorker.cs:127-165`
- Command poll checks it: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/CommandPollWorker.cs:252-257`
- Upload checks it: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/CloudUploadWorker.cs:236-239`
- Status poll does not check it: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/StatusPollWorker.cs:125-141`
- Telemetry does not check it: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/TelemetryReporter.cs:116-125`
- Version check does not check it: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/VersionCheckService.cs:77-103`

### 10. [Medium] Several endpoint status codes are handled too generically on desktop

The DTOs for command, config, upload, and version flows are mostly fine. The problem is transport/status handling.

Examples:

- command poll `404 FEATURE_DISABLED` becomes a generic transport failure
- command ack `404 COMMAND_NOT_FOUND` becomes a generic transport failure
- upload `409 CONFLICT.NON_LEADER_WRITE` and `409 CONFLICT.STALE_LEADER_EPOCH` become generic batch failure/retry loops
- version-check auth-refresh failures are swallowed by fail-open behavior instead of marking the device for re-provisioning or decommissioning

Impact:

- operator-facing behavior does not match backend intent
- some permanent or policy-driven failures are retried as if they were transient

References:

- Cloud command poll and ack return `404` on disabled/missing commands: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:925-928`, `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:1016-1018`
- Desktop command poll/ack uses `EnsureSuccessStatusCode()` and generic transport failures: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/CommandPollWorker.cs:229-257`, `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/CommandPollWorker.cs:298-338`
- Cloud write-fence returns `409` conflicts for stale/non-leader writers: `src/cloud/FccMiddleware.Api/Infrastructure/AuthoritativeWriteFenceService.cs:127-164`
- Version-check fail-open path: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/VersionCheckService.cs:83-103`

### 11. [Medium] The desktop project is forking cloud contracts instead of referencing the shared contracts assembly

This is the root technical reason the drift exists.

`FccDesktopAgent.Core.csproj` does not reference `src/cloud/FccMiddleware.Contracts`. The desktop agent keeps independent copies of cloud DTOs under `FccDesktopAgent.Core`, and those copies are already drifting in breaking ways.

Impact:

- contract drift is easy to introduce and hard to detect
- fixes have to be duplicated manually across cloud, Android, and desktop
- future endpoint changes will likely keep regressing alignment unless the contracts are shared or generated

References:

- Desktop core project has no contracts project reference: `src/desktop-edge-agent/src/FccDesktopAgent.Core/FccDesktopAgent.Core.csproj:1-27`
- Shared cloud contracts project exists separately: `src/cloud/FccMiddleware.Contracts/FccMiddleware.Contracts.csproj:1-11`

## Additional Observations

- The desktop repository already contains DTOs for site-data uploads (`bna-reports`, `pump-totals`, `pump-control-history`, `price-snapshots`), but there are no callers. Those models are close to the cloud contracts and look intentionally staged for future work.
- The desktop UI shows `/api/v1/agent/diagnostic-logs` in the settings screen, but there is no desktop worker that actually posts diagnostic logs to the cloud.
- Registration error modeling is incomplete on desktop. The cloud can return `CONFIG_NOT_FOUND` and `REGISTRATION_BLOCKED`, but the desktop registration enum/parser does not model either code, so the UI falls back to generic messaging.

## Recommended Remediation Order

1. Stop hand-maintaining desktop copies of cloud contracts. Either reference `FccMiddleware.Contracts` directly or generate desktop DTOs from the same source.
2. Fix token refresh immediately. This is the most direct wire-level break.
3. Replace the desktop `SiteConfig` fork with the real cloud contract, then fix runtime mapping so HA, fiscalization, and sync settings are actually applied.
4. Fix upload serialization to send `UploadTransactionRecord` instead of `CanonicalTransaction`, and persist/reuse `uploadBatchId` for retries.
5. Fix config/status poll semantics: distinguish `404` from `304`, consume peer-directory headers on all authenticated responses, and use a safe watermark strategy for `synced-status`.
