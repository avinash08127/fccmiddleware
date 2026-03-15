# Edge Agent / Cloud Alignment Audit

Audit date: 2026-03-15

## Scope

This audit covers the Android edge agent under `src/edge-agent/app` against the cloud backend under `src/cloud`.

Scanned areas:

- All active Android cloud calls in `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt`
- Android request/response DTOs in `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt`
- Android call sites and workers that build or consume those DTOs
- Cloud controllers and contracts for every authenticated agent endpoint
- HA-related config and peer-discovery paths because they directly affect cloud contract correctness

This audit is Android-only. It does not cover the desktop edge agent.

## Executive Summary

- I scanned 13 active Android cloud endpoints/probes and 5 additional cloud endpoints that are exposed to agents but are not wired on Android.
- The highest-risk mismatch is the transaction upload contract: the Android request model is not aligned with the cloud upload contract, and important reconciliation fields are dropped before upload.
- HA registration metadata is also wrong on Android: the device registers a peer API port of `8585`, while the actual peer API server listens on `8586`.
- Several Android client result types under-model real cloud responses. In practice that means explicit backend errors are treated as generic transport failures, which causes bad retry behavior and misleading operator-facing errors.
- Android does not reliably honor the cloud `configPollIntervalSeconds` contract because of both default drift and a cadence-loop bug.

## Endpoint Coverage

| Endpoint | Android caller | Verdict | Notes |
| --- | --- | --- | --- |
| `GET /health` | `CloudApiClient.healthCheck()` | Aligned | Simple unauthenticated liveness probe; no DTO contract. |
| `POST /api/v1/agent/register` | `ProvisioningViewModel` -> `CloudApiClient.registerDevice()` | Partial | Request/response schema is mostly aligned, but Android sends bad HA peer API metadata and misclassifies `401`/`429`. |
| `POST /api/v1/agent/token/refresh` | `CloudApiClient.refreshToken()` | Aligned | Request/response contract is aligned. |
| `GET /api/v1/agent/version-check` | `CadenceController` -> `CloudApiClient.checkVersion()` | Partial | Request/response schema aligns, but `400`/`500` are treated as generic transport errors. |
| `GET /api/v1/agent/config` | `ConfigPollWorker` -> `CloudApiClient.getConfig()` | Partial | Config schema drifts, `404` is not modeled, and `304` loses peer-directory version info. |
| `GET /api/v1/agent/commands` | `CommandPollWorker` -> `CloudApiClient.pollCommands()` | Partial | Endpoint is wired, but `404 FEATURE_DISABLED` is not modeled and command payload typing is too narrow. |
| `POST /api/v1/agent/commands/{id}/ack` | `CommandPollWorker` -> `CloudApiClient.ackCommand()` | Partial | Endpoint is wired, but `404 COMMAND_NOT_FOUND` is not modeled and `result` typing is too narrow. |
| `POST /api/v1/agent/installations/android` | `AndroidInstallationSyncManager` -> `CloudApiClient.upsertAndroidInstallation()` | Mismatch | Body shape aligns, but `404 FEATURE_DISABLED` and `409 INSTALLATION_OWNERSHIP_CONFLICT` are collapsed into retryable transport errors. |
| `POST /api/v1/agent/telemetry` | `CloudUploadWorker.reportTelemetry()` | Partial | Payload shape is mostly aligned, with extra tolerated fields; `400`/`404` are not modeled. |
| `POST /api/v1/agent/diagnostic-logs` | `CloudUploadWorker.reportDiagnosticLogs()` | Partial | Request body aligns; `400`/`404` are not modeled. |
| `POST /api/v1/preauth` | `PreAuthCloudForwardWorker` -> `CloudApiClient.forwardPreAuth()` | Partial | Request/response contract mostly aligns, but Android never populates `attendantId` and treats `400` as transport failure. |
| `POST /api/v1/transactions/upload` | `CloudUploadWorker` -> `CloudApiClient.uploadBatch()` | Mismatch | Request model is materially out of sync with the cloud contract; `409` fencing conflicts are not modeled. |
| `GET /api/v1/transactions/synced-status` | `CloudUploadWorker.pollSyncedToOdooStatus()` | Aligned | Request/response contract is aligned. |
| `PATCH /api/v1/preauth/{id}` | None | Not wired | Cloud endpoint exists for agents; Android has no client or DTO for it. |
| `POST /api/v1/sites/{siteCode}/bna-reports` | None | Not wired | Android has a request DTO but no client method or worker uses it. |
| `POST /api/v1/sites/{siteCode}/pump-totals` | None | Not wired | Android has a request DTO but no client method or worker uses it. |
| `POST /api/v1/sites/{siteCode}/pump-control-history` | None | Not wired | Android has a request DTO but no client method or worker uses it. |
| `POST /api/v1/sites/{siteCode}/price-snapshots` | None | Not wired | Android has a request DTO but no client method or worker uses it. |

## Model Coverage

| Model group | Verdict | Notes |
| --- | --- | --- |
| `DeviceRegistrationRequest` | Partial | Contract shape aligns, but Android populates `peerApi` incorrectly. |
| `DeviceRegistrationResponse` | Aligned | Android is slightly more permissive because `siteConfig` is nullable locally. |
| `TokenRefreshRequest` / `TokenRefreshResponse` | Aligned | No mismatch found. |
| `VersionCheckRequest` / `VersionCheckResponse` | Aligned | No payload mismatch found. |
| `CloudUploadRequest` | Partial | Wrapper fields align; nested transaction model does not. |
| `CloudTransactionDto` vs `UploadTransactionRecord` | Mismatch | Missing cloud-expected fields and includes stale edge-only fields. |
| `CloudUploadResponse` / `CloudUploadRecordResult` | Partial | DTO matches the cloud contract, but the cloud controller does not populate `errorMessage`. |
| `SyncedStatusResponse` | Aligned | No mismatch found. |
| `EdgeAgentConfigDto` vs `SiteConfigResponse` | Partial | Several drifts: missing cloud fields, extra Android-local fields, and default mismatches. |
| `TelemetryPayload` vs `SubmitTelemetryRequest` | Partial | Mostly aligned; Android sends extra buffer counters that cloud ignores. |
| `EdgeCommandPollResponse` / `CommandAckRequest` | Partial | JSON payload/result types are narrower on Android (`JsonObject` vs cloud `JsonElement`). |
| `AndroidInstallationUpsertRequest` | Aligned | Request shape matches. Endpoint status handling is the actual issue. |
| `PreAuthForwardRequest` / `PreAuthForwardResponse` | Partial | Schema aligns, but Android never fills `attendantId`. |
| `DiagnosticLogUploadRequest` | Aligned | No mismatch found. |
| Site-data request DTOs | Aligned but unused | Local DTOs match cloud contracts, but Android never calls those endpoints. |

## Findings

### 1. [High] Transaction upload request and response contracts are not aligned

**ReviewStatus: Valid** -- All 5 sub-claims confirmed against source. `CloudTransactionDto` is missing `fccCorrelationId` and `odooOrderId` while including 10 edge-only fields absent from `UploadTransactionRecord`. Buffer mapping at `TransactionBufferManager:389-418` drops `odooOrderId` despite `BufferedTransaction` storing it. Cloud controller maps `ErrorMessage` in the response contract but never populates it. The impact assessment on lost reconciliation metadata is accurate.

The Android upload path claims to mirror the cloud canonical transaction contract, but it does not.

Problems:

- Android upload DTO is missing `fccCorrelationId` and `odooOrderId`, both of which the cloud upload contract expects.
- Android upload DTO includes several edge-only fields that the cloud contract does not define: `id`, `legalEntityId`, `status`, `ingestionSource`, `ingestedAt`, `updatedAt`, `schemaVersion`, `isDuplicate`, `correlationId`, and `rawPayloadJson`.
- The Android canonical/buffering path drops `odooOrderId` before upload.
- The Android canonical transaction model has no `fccCorrelationId` field at all, so that value cannot reach the cloud upload contract.
- The cloud upload response contract includes `errorMessage`, the Android worker expects it, but the cloud controller does not populate it.

Impact:

- Cloud-side reconciliation metadata is silently lost for uploaded transactions.
- Duplicate detection and order-correlation workflows have less information than the cloud contract assumes.
- Rejected upload records lose human-readable error detail even though the response contract says it is available.

References:

- Edge DTO: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:13-20`
- Edge DTO: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:30-97`
- Android request construction: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudUploadWorker.kt:889-924`
- Android canonical transaction model: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/CanonicalTransaction.kt:13-103`
- Buffered transaction entity stores `odooOrderId`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/entity/BufferedTransaction.kt:118-123`
- Buffer mapping drops `odooOrderId`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/TransactionBufferManager.kt:389-418`
- Cloud upload contract: `src/cloud/FccMiddleware.Contracts/Ingestion/UploadTransactionRecord.cs:7-55`
- Cloud controller reads `FccCorrelationId` and `OdooOrderId`: `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:821-840`
- Cloud response contract includes `ErrorMessage`: `src/cloud/FccMiddleware.Contracts/Ingestion/UploadRecordResult.cs:21-31`
- Cloud controller does not map `ErrorMessage`: `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:852-861`
- Android worker uses `errorMessage` if present: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudUploadWorker.kt:812-816`

### 2. [High] Android registers unusable HA peer API metadata with the cloud

**ReviewStatus: Valid** -- Confirmed. `ProvisioningViewModel:112-115` hardcodes `port = 8585` and `tlsEnabled = false` with no `baseUrl` or `advertisedHost`. `PeerApiServer` defaults to port `8586` (`DEFAULT_PEER_PORT = 8586`). Cloud contract supports all four fields (`BaseUrl`, `AdvertisedHost`, `Port`, `TlsEnabled`) and persists them to the peer directory. `PeerCoordinator.buildBaseUrl()` returns `null` when `advertisedHost` is missing, making peers unreachable via the authoritative directory.

Android registration sends:

- `peerApi.port = 8585`
- `peerApi.tlsEnabled = false`
- no `peerApi.baseUrl`
- no `peerApi.advertisedHost`

That does not match Android runtime behavior:

- The Android peer API server listens on `8586` by default.
- The peer coordinator can only synthesize peer URLs when the cloud peer directory contains either `peerApiBaseUrl` or `peerApiAdvertisedHost` plus `peerApiPort`.

Impact:

- The cloud peer directory can contain endpoints that other agents cannot use.
- HA peer discovery may fall back to LAN broadcast because the authoritative cloud directory is incomplete or wrong.

References:

- Android registration builder: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/ProvisioningViewModel.kt:96-115`
- Cloud registration contract supports `BaseUrl`, `AdvertisedHost`, and `Port`: `src/cloud/FccMiddleware.Contracts/Registration/DeviceRegistrationApiRequest.cs:48-59`
- Cloud registration controller persists those fields: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:396-413`
- Cloud config builder returns those fields in peer directory: `src/cloud/FccMiddleware.Application/AgentConfig/GetAgentConfigHandler.cs:310-322`
- Android peer API server default port: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/peer/PeerApiServer.kt:39-42`
- Android peer API server port resolution: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/peer/PeerApiServer.kt:134-141`
- Peer coordinator URL synthesis: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/peer/PeerCoordinator.kt:88-97`
- Peer coordinator URL synthesis helper: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/peer/PeerCoordinator.kt:371-375`

### 3. [High] Android under-models real cloud error responses on multiple endpoints

**ReviewStatus: Valid** -- All 10 endpoint mismatches confirmed. Registration result type has no `401`/`429` variants; upload has no `409 Conflict`; config/commands have no `404`; installation has no `404`/`409`. `DeviceActiveCheckMiddleware` confirmed to return `401` for `DEVICE_NOT_FOUND` and `403` for non-active states, meaning Android's blanket token-refresh on `401` is semantically wrong. The claim about endless retries of permanent errors (e.g. installation `409 INSTALLATION_OWNERSHIP_CONFLICT` classified as `TransportError`) is accurate.

Several Android result types only model a subset of the statuses that the cloud actually returns. The result is that explicit backend errors are treated as generic transport failures.

This is not cosmetic. In a few places it creates objectively wrong behavior, especially endless retries of permanent errors.

| Endpoint | Cloud statuses not modeled by Android | Current Android effect |
| --- | --- | --- |
| `POST /api/v1/agent/register` | `401`, `429` | Invalid bootstrap token and throttling surface as generic "Network error". |
| `POST /api/v1/transactions/upload` | `409` | HA fencing conflicts become retryable transport failures. |
| `GET /api/v1/agent/config` | `404` | `CONFIG_NOT_FOUND`/`DEVICE_NOT_FOUND` become transport failures. |
| `GET /api/v1/agent/commands` | `404` | `FEATURE_DISABLED` becomes transport failure. |
| `POST /api/v1/agent/commands/{id}/ack` | `404` | `COMMAND_NOT_FOUND` becomes transport failure. |
| `POST /api/v1/agent/installations/android` | `404`, `409` | Feature-disabled and ownership-conflict responses are retried indefinitely. |
| `GET /api/v1/agent/version-check` | `400`, `500` | Validation and server config errors become transport failures. |
| `POST /api/v1/agent/telemetry` | `400`, `404` | Malformed payload/device-not-found errors are not distinguished from transient failures. |
| `POST /api/v1/agent/diagnostic-logs` | `400`, `404` | Same issue as telemetry. |
| `POST /api/v1/preauth` | `400` | Validation failures are treated as transient transport problems. |

Additional nuance:

- Cloud `401` is not always "expired token". The device-active middleware also returns `401` for `DEVICE_NOT_FOUND` and for some non-active states, so Android's blanket "refresh token and retry" logic is not always semantically correct.

References:

- Registration result type models only `Success`, `Rejected`, `TransportError`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:338-346`
- Registration client handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:643-660`
- Registration controller returns `401` and `429`: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:367-371`
- Registration controller returns `401`/`400`/`409`: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:390-452`
- Provisioning UI converts `TransportError` into network copy: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/ProvisioningViewModel.kt:65-74`
- Upload result type has no `Conflict`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:61-91`
- Upload client handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:426-453`
- Upload controller can return fencing conflicts: `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:795-816`
- Upload worker treats transport failures as transient: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudUploadWorker.kt:603-656`
- Config client handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:500-542`
- Config controller returns `404`: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:896-905`
- Config poll worker handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/ConfigPollWorker.kt:124-167`
- Command poll client handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:703-728`
- Command poll controller `FEATURE_DISABLED`: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:925-928`
- Command ack client handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:737-769`
- Command ack controller `COMMAND_NOT_FOUND`: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:1016-1019`
- Installation result type models only `Success`, `Unauthorized`, `Forbidden`, `TransportError`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:484-488`
- Installation client handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:778-801`
- Installation controller returns `404` and `409`: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:1126-1147`
- Installation sync manager retries transport errors: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/AndroidInstallationSyncManager.kt:84-117`
- Version-check client handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:366-385`
- Version-check controller returns `400` and `500`: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:1228-1255`
- Telemetry client handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:545-573`
- Telemetry controller returns `404`: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:1314-1320`
- Diagnostic-log client handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:827-848`
- Diagnostic-log controller returns `404`: `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs:1371-1377`
- Pre-auth client handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:583-623`
- Pre-auth controller returns `400`: `src/cloud/FccMiddleware.Api/Controllers/PreAuthController.cs:51-57`
- Device-active middleware uses `401`/`403` for non-token problems too: `src/cloud/FccMiddleware.Api/Infrastructure/DeviceActiveCheckMiddleware.cs:34-57`

### 4. [High] Android does not reliably honor the cloud `configPollIntervalSeconds` contract

**ReviewStatus: Valid** -- Both sub-problems confirmed. Problem A: cloud default is `300` (GetAgentConfigHandler:256), Android DTO default is `60` (EdgeAgentConfigDto:130), cadence fallback is `6 * 30s = 180s` (CadenceController:108,119) -- three different defaults for the same semantic. Problem B: `computeTickModulus()` uses static `CadenceConfig` frequencies for the LCM, but `effectiveConfigPollTickFrequency` is computed dynamically from runtime config. When the dynamic frequency doesn't evenly divide the static modulus, tick-count wrap creates irregular polling intervals.

There are two separate problems.

Problem A: default drift

- Cloud config builder default is `300` seconds.
- Android config DTO default is `60` seconds.
- Cadence fallback default is `6` ticks at a `30s` base interval, which is `180` seconds.

Problem B: runtime cadence bug

- Android computes `effectiveConfigPollTickFrequency` dynamically from the runtime config.
- But `tickCount` wraps using a modulus computed from the static frequencies only.
- When the runtime config frequency differs from the static frequency, the modulo wrap distorts the schedule.

Impact:

- Android does not faithfully implement the cloud cadence contract for HA and non-HA sites.
- The current code can poll too frequently or at uneven intervals after tick-count wrap.

References:

- Android sync DTO default `60`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt:125-143`
- Android cadence defaults: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt:108-123`
- Dynamic frequency calculation: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt:233-240`
- Static modulus calculation: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt:246-255`
- Tick-count wrap: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt:511-513`
- Config poll uses dynamic frequency: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt:564-565`
- Config poll uses dynamic frequency: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt:597-598`
- Cloud sync default builder sets `300`: `src/cloud/FccMiddleware.Application/AgentConfig/GetAgentConfigHandler.cs:284`

### 5. [Medium] Peer-directory version header is emitted by cloud on every response, but Android only captures it on success paths

**ReviewStatus: Valid** -- Confirmed. `PeerDirectoryVersionMiddleware` adds the header via `OnStarting()` on every authenticated response. Android `CloudApiClient` only calls `extractPeerDirectoryVersion(response)` in the `HttpStatusCode.OK` branch for config polling; `NotModified` is a bare `data object` with no fields. `ConfigPollWorker` only persists the version inside the `NewConfig` branch. Notably, the desktop agent handles this correctly via `PeerDirectoryVersionHelper` which extracts the version from all responses including 304.

The cloud middleware adds `X-Peer-Directory-Version` to every authenticated agent response. Android only retains that header on a subset of success responses.

Concrete example:

- `GET /api/v1/agent/config` on `304 Not Modified` carries no peer-directory version in the Android result type.
- `ConfigPollWorker` only persists the peer-directory version on `NewConfig`.

Impact:

- HA peer-directory staleness can be detected later than the cloud intended.
- Android loses a Layer-2 HA hint on perfectly valid `304` and non-success responses.

References:

- Cloud emits header on every authenticated response: `src/cloud/FccMiddleware.Api/Infrastructure/PeerDirectoryVersionMiddleware.cs:18-36`
- Config poll result type has no version on `NotModified`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:170-195`
- Config client handling: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:511-531`
- Config poll worker loses version on `NotModified`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/ConfigPollWorker.kt:124-129`
- Config poll worker only persists version on new config: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/ConfigPollWorker.kt:194-198`
- Upload/command/preauth result types also only carry peer-directory version on success: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:61-91`
- Upload/command/preauth result types also only carry peer-directory version on success: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:449-458`
- Upload/command/preauth result types also only carry peer-directory version on success: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:565-586`

### 6. [Medium] Android config model is not a strict 1:1 match for cloud `SiteConfigResponse`

**ReviewStatus: Valid** -- All four drift points confirmed. Cloud `SyncDto` has `Environment` (SiteConfigResponse.cs:141); Android `SyncDto` does not. Android `SiteHaDto` adds `peerApiPort`, `peerSharedSecret`, `replicationEnabled`, `proxyingEnabled` not in cloud contract. Android adds a full `WebSocketDto` block (12 fields) and `fiscalization.vendor` absent from cloud. Android parser uses `ignoreUnknownKeys = true` which masks the divergence at runtime.

Some drift is benign because Android uses permissive JSON parsing, but it is still real drift.

Observed differences:

- Cloud `sync.environment` exists; Android `SyncDto` does not expose it.
- Android `SiteHaDto` adds local-only fields not present in the cloud contract: `peerApiPort`, `peerSharedSecret`, `replicationEnabled`, `proxyingEnabled`.
- Android adds a `websocket` block and `fiscalization.vendor`, neither of which exist in the cloud `SiteConfigResponse`.
- Cloud `localApi` includes `lanBindAddress`, `lanAllowCidrs`, and `lanApiKeyRef`, but Android runtime mapping does not actually use those fields.

Impact:

- This is not a strict shared contract today.
- Some config fields are validated on Android but never applied at runtime.

References:

- Cloud `SyncDto` includes `Environment`: `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs:120-141`
- Android `SyncDto` does not: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt:125-143`
- Cloud `SiteHaDto`: `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs:163-198`
- Android `SiteHaDto`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt:165-203`
- Android `websocket` and `fiscalization.vendor`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt:213-253`
- Android config parser ignores unknown keys: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt:289-299`

### 7. [Medium] Cloud local-API contract is only partially implemented on Android

**ReviewStatus: Valid** -- Confirmed. Cloud defaults `LocalhostPort` to `8080` (GetAgentConfigHandler:284); Android defaults to `8585` (EdgeAgentConfigDto:156). Android `toLocalApiServerConfig()` only maps `localhostPort`, `enableLanApi`, and `lanApiKey`, ignoring `lanBindAddress`, `lanAllowCidrs`, and `lanApiKeyRef`. The server intentionally stays localhost-only. The audit correctly notes this is partly intentional but still represents a contract mismatch if cloud config omits the port field.

This is partly intentional, but it is still a cloud/edge behavior mismatch.

Observed issues:

- Cloud default `localApi.localhostPort` is `8080`; Android defaults and tests assume `8585`.
- Android `toLocalApiServerConfig()` ignores `lanBindAddress`, `lanAllowCidrs`, and `lanApiKeyRef`.
- Android explicitly refuses LAN exposure until secure LAN transport is implemented, even if cloud config enables it.

Impact:

- Cloud config can express local-API settings that Android currently does not honor.
- Default port drift can create unexpected behavior if cloud config omits the field.

References:

- Cloud local-API contract: `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs:153-160`
- Cloud default port `8080`: `src/cloud/FccMiddleware.Application/AgentConfig/GetAgentConfigHandler.cs:279-289`
- Android local-API DTO default `8585`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt:155-161`
- Android runtime mapping only uses `port`, `enableLanApi`, and `lanApiKey`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt:370-380`
- Android local API server intentionally stays localhost-only: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/api/LocalApiServer.kt:41-55`
- Android local API server bind behavior: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/api/LocalApiServer.kt:94-119`
- Android contract test expects `8585`: `src/edge-agent/app/src/test/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigContractTest.kt:20-26`
- Android contract test expects `8585`: `src/edge-agent/app/src/test/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigContractTest.kt:40-45`

### 8. [Medium] Command payload/result typing is narrower on Android than on cloud

**ReviewStatus: Valid** -- Confirmed. Android `EdgeCommandDto.payload` and `CommandAckRequest.result` are typed as `JsonObject?` (CloudApiModels.kt:421-439). Cloud `EdgeCommandPollResponse.Payload` and `CommandAckRequest.Result` use `JsonElement?` (EdgeCommandPollResponse.cs:32, CommandAckRequest.cs:33). `JsonObject` rejects root-level arrays, strings, numbers, and booleans that `JsonElement` would accept.

Cloud command payloads and ack results use `JsonElement?`, which permits objects, arrays, strings, numbers, booleans, and null. Android uses `JsonObject?`.

Impact:

- Android can deserialize and send only object-shaped payloads/results.
- If the cloud ever sends or expects a non-object JSON value, Android will not match the contract.

References:

- Android command payload and ack result types: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:421-439`
- Cloud poll contract uses `JsonElement? Payload`: `src/cloud/FccMiddleware.Contracts/AgentControl/EdgeCommandPollResponse.cs:17-34`
- Cloud ack contract uses `JsonElement? Result`: `src/cloud/FccMiddleware.Contracts/AgentControl/CommandAckRequest.cs:29-33`
- Cloud admin create-command contract also allows `JsonElement? Payload`: `src/cloud/FccMiddleware.Contracts/AgentControl/CreateAgentCommandRequest.cs:22-31`

### 9. [Low] Android pre-auth forward request never fills `attendantId`

**ReviewStatus: Valid** -- Confirmed. Android `PreAuthForwardRequest` DTO includes `attendantId: String? = null` (CloudApiModels.kt:547). The builder in `PreAuthCloudForwardWorker.toForwardRequest()` (lines 384-405) constructs the request with 18 fields but omits `attendantId`. Cloud contract defines `AttendantId` (PreAuthForwardRequest.cs:44). The field is silently dropped.

The field exists in both the Android DTO and the cloud contract, but the Android worker never sets it when building the request.

Impact:

- Operator metadata supported by the cloud contract is silently not forwarded from Android.

References:

- Android DTO includes `attendantId`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:521-548`
- Android forward-request builder omits it: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/PreAuthCloudForwardWorker.kt:384-405`
- Cloud contract includes it: `src/cloud/FccMiddleware.Contracts/PreAuth/PreAuthForwardRequest.cs:28-44`

### 10. [Low] Android telemetry sends extra fields that the cloud ignores

**ReviewStatus: Valid** -- Confirmed. Android `BufferStatusDto` (CloudApiModels.kt:247-261) includes `deadLetterCount`, `archivedCount`, `fiscalPendingCount`, and `fiscalDeadLetterCount`. Cloud `SubmitTelemetryBufferStatusRequest` (SubmitTelemetryRequest.cs:107-128) defines only `TotalRecords`, `PendingUploadCount`, `SyncedCount`, `SyncedToOdooCount`, `FailedCount`, `OldestPendingAtUtc`, `BufferSizeMb`. The extra fields are silently ignored. Benign today but evidence of contract drift.

Android telemetry buffer status includes:

- `deadLetterCount`
- `archivedCount`
- `fiscalPendingCount`
- `fiscalDeadLetterCount`

The cloud request contract does not define those fields.

Impact:

- This is benign today because the cloud deserializer ignores unknown fields.
- It is still evidence that the telemetry contract is not truly identical across the two sides.

References:

- Android telemetry buffer DTO: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:247-260`
- Cloud telemetry buffer contract: `src/cloud/FccMiddleware.Contracts/Telemetry/SubmitTelemetryRequest.cs:107-128`

### 11. [Medium] LAN peer announcement uses the wrong semantic field for peer-directory version

**ReviewStatus: Valid** -- Confirmed. `LanPeerAnnouncer` (line 74) sets `peerDirectoryVersion = siteHa.leaderEpoch`. `LanPeerListener` (lines 135-143) stores that value as `leaderEpoch` in `PeerState`. Cloud `PeerDirectoryVersionMiddleware` derives the header from `site.PeerDirectoryVersion`, which is a monotonically increasing counter for peer membership changes -- semantically distinct from `leaderEpoch` which tracks HA leader elections. The two concepts are conflated in LAN discovery.

This is not a direct cloud API mismatch, but it is an HA bug found while tracing cloud/edge alignment.

Observed issue:

- `LanPeerAnnouncer` populates `peerDirectoryVersion` with `siteHa.leaderEpoch`.
- `LanPeerListener` then stores that value into peer state as `leaderEpoch`.
- Cloud’s `X-Peer-Directory-Version` header is a site peer-directory version, not a leader epoch.

Impact:

- LAN-discovered peer state can mix two different HA concepts: peer-directory version and leader epoch.

References:

- Android announcement payload: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/peer/LanPeerAnnouncer.kt:39-75`
- Android listener stores announcement version into `leaderEpoch`: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/peer/LanPeerListener.kt:135-143`
- Cloud peer-directory header semantics: `src/cloud/FccMiddleware.Api/Infrastructure/PeerDirectoryVersionMiddleware.cs:24-30`

## Verified Aligned Areas

These areas looked aligned in the current codebase:

- `POST /api/v1/agent/token/refresh` request and response models
- `GET /api/v1/agent/version-check` request and response payload shapes
- `GET /api/v1/transactions/synced-status` request and response payloads
- `POST /api/v1/agent/diagnostic-logs` request body shape
- Command enums `AgentCommandType`, `AgentCommandStatus`, and `AgentCommandCompletionStatus`
- Site-data upload request DTO shapes in Android match the cloud `SiteDataContracts` definitions if those endpoints are wired later

References:

- Refresh-token request/response: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:353-383`
- Refresh-token contracts: `src/cloud/FccMiddleware.Contracts/Registration/RefreshTokenRequest.cs:5-15`
- Refresh-token contracts: `src/cloud/FccMiddleware.Contracts/Registration/RefreshTokenResponse.cs:5-11`
- Version-check request/response: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:597-618`
- Version-check contracts: `src/cloud/FccMiddleware.Contracts/Agent/VersionCheckRequest.cs:3-6`
- Version-check contracts: `src/cloud/FccMiddleware.Contracts/Agent/VersionCheckResponse.cs:3-12`
- Synced-status response: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:153-161`
- Synced-status contract: `src/cloud/FccMiddleware.Contracts/Transactions/SyncedStatusResponse.cs:7-10`
- Diagnostic-log request: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:643-654`
- Diagnostic-log contract: `src/cloud/FccMiddleware.Contracts/DiagnosticLogs/DiagnosticLogUploadRequest.cs:5-25`
- Android command enums: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:390-413`
- Cloud command enums: `src/cloud/FccMiddleware.Domain/Enums/AgentCommandType.cs:7-14`
- Cloud command enums: `src/cloud/FccMiddleware.Domain/Enums/AgentCommandStatus.cs`
- Cloud command enums: `src/cloud/FccMiddleware.Domain/Enums/AgentCommandCompletionStatus.cs`
- Android site-data request DTOs: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:660-716`
- Cloud site-data contracts: `src/cloud/FccMiddleware.Contracts/SiteData/SiteDataContracts.cs:13-98`

## Unwired Cloud Endpoints and Dormant Android Models

These are not active mismatches in runtime behavior today, but they are coverage gaps:

- Cloud exposes `PATCH /api/v1/preauth/{id}` for agents; Android has no corresponding client method or DTO.
- Android defines request DTOs for site-data uploads (`bna-reports`, `pump-totals`, `pump-control-history`, `price-snapshots`), but there are no corresponding `CloudApiClient` methods or workers using them.

References:

- Cloud pre-auth patch endpoint: `src/cloud/FccMiddleware.Api/Controllers/PreAuthController.cs:192-248`
- Android cloud client method surface: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt:117-269`
- Dormant Android site-data DTOs: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt:660-716`

## Recommended Fix Order

1. Fix the upload contract first.
2. Fix Android registration peer API metadata for HA.
3. Expand Android client result types so permanent cloud errors are not treated as transient transport failures.
4. Fix config-poll cadence and peer-directory-version handling.
5. Tighten config-model parity and either implement or remove unsupported local-API / site-data contract surface.
