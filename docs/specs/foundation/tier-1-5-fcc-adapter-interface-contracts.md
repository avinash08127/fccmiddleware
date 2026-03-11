# FCC Adapter Interface Contracts

## 1. Output Location
- Target file path: `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md`
- Optional companion files: None
- Why this location matches `docs/STRUCTURE.md`: FCC adapter interfaces are mapped to `/docs/specs/foundation`, and this artefact defines the cross-runtime contract, selection rules, and vendor adapter boundary.

## 2. Scope
- TODO item addressed: `1.5 FCC Adapter Interface Contracts`
- In scope: cloud `.NET` `IFccAdapter`, edge Kotlin `IFccAdapter`, shared semantics, adapter factory/discovery rules, DOMS MVP protocol contract
- Out of scope: field-level DOMS-to-canonical mapping, cloud ingest API shape, edge local API shape, concrete class implementations

## 3. Source Traceability
- Requirements referenced: `REQ-3`, `REQ-6`, `REQ-7`, `REQ-10`, `REQ-12`, `REQ-15`
- HLD sections referenced only: `WIP-HLD-Cloud-Backend.md` sections `4.3`, `4.4`; `WIP-HLD-Edge-Agent.md` sections `4.4`, `6.3`, `7`
- Assumptions from TODO ordering/dependencies: `CanonicalTransaction` and `PumpStatus` contracts already exist; DOMS field mapping is detailed separately after this interface contract

## 4. Key Decisions

| Decision | Why | Impact |
|---|---|---|
| Cloud and edge use separate language-native interfaces with one semantic contract. | `REQ-15` and edge HLD require Kotlin-native adapter code; cloud HLD requires `.NET` adapter projects. | Vendors are implemented twice, but behaviour and return contracts remain aligned. |
| `FetchTransactions` / `fetchTransactions` returns normalized `CanonicalTransaction` objects, not raw vendor records. | Pull workers need a single downstream shape and `REQ-10` makes adapters responsible for normalization. | Pull-mode workers do not perform vendor parsing outside the adapter boundary. |
| DOMS MVP protocol is fixed to REST + JSON + API key auth until the Phase `4.1` PoC proves otherwise. | `REQ-12` and both HLDs assume pollable endpoints; a concrete MVP contract is needed now. | Implementation can proceed with stable endpoints; PoC may revise this document if DOMS documentation disagrees. |
| Adapter selection is config-driven by `fccVendor` and runtime context, not reflection-only discovery. | `REQ-3` requires deterministic vendor resolution per FCC/site configuration. | Factories remain testable and startup validation can fail fast on missing vendor bindings. |

## 5. Detailed Specification

### 5.1 Interface and Shared Type Contract

| Runtime | Member | Input | Output | Required | Semantics |
|---|---|---|---|---|---|
| Cloud `.NET` | `NormalizeTransaction(rawPayload)` | `RawPayloadEnvelope rawPayload` | `CanonicalTransaction` | Yes | Parse one vendor payload object or one vendor transaction item and produce a valid canonical transaction; must preserve source payload reference and apply configured mappings. |
| Cloud `.NET` | `ValidatePayload(rawPayload)` | `RawPayloadEnvelope rawPayload` | `ValidationResult` | Yes | Structural and vendor-rule validation only; no persistence or dedup checks; used before normalization for push ingress and diagnostics. |
| Cloud `.NET` | `FetchTransactions(cursor)` | `FetchCursor cursor` | `TransactionBatch` | Yes | Pull-mode fetch against cloud-reachable FCC endpoint; returns zero or more normalized transactions plus next cursor and batch completeness metadata. |
| Cloud `.NET` | `GetAdapterMetadata()` | None | `AdapterInfo` | Yes | Static capability metadata used for registration, diagnostics, and config validation. |
| Edge Kotlin | `normalize(rawPayload)` | `RawPayloadEnvelope rawPayload` | `CanonicalTransaction` | Yes | Same semantics as cloud `NormalizeTransaction`; used for LAN pull and LAN push inputs. |
| Edge Kotlin | `sendPreAuth(command)` | `PreAuthCommand command` | `PreAuthResult` | Yes | Issue a pre-auth command to the FCC over LAN and return canonical authorization outcome. |
| Edge Kotlin | `getPumpStatus()` | None | `List<PumpStatus>` | Yes | Return one latest status record per configured pump-nozzle pair reachable through this FCC. |
| Edge Kotlin | `heartbeat()` | None | `Boolean` | Yes | Connectivity liveness probe only; `true` means authenticated protocol reachability succeeded. |
| Edge Kotlin | `fetchTransactions(cursor)` | `FetchCursor cursor` | `TransactionBatch` | Yes | Same semantics as cloud `FetchTransactions`, but executed over LAN against the local FCC. |

### 5.2 Shared Supporting Types

| Type | Field | Type | Required | Notes |
|---|---|---|---|---|
| `RawPayloadEnvelope` | `vendor` | `FccVendor` | Yes | Must match resolved site/FCC config. |
| `RawPayloadEnvelope` | `siteCode` | `string` | Yes | Adapter context key for mappings and validation. |
| `RawPayloadEnvelope` | `receivedAtUtc` | `datetime` | Yes | Time payload reached cloud or edge boundary. |
| `RawPayloadEnvelope` | `contentType` | `string` | Yes | MVP values: `application/json`, `text/xml`, `application/octet-stream`; DOMS uses `application/json`. |
| `RawPayloadEnvelope` | `payload` | `string` or `bytes` | Yes | Exact raw payload, unchanged. |
| `FetchCursor` | `cursorToken` | `string` | No | Vendor opaque continuation token. |
| `FetchCursor` | `sinceUtc` | `datetime` | No | Inclusive lower bound when vendor token is unavailable. |
| `FetchCursor` | `limit` | `int` | No | Caller hint; adapter may reduce but must not exceed configured max page size. |
| `TransactionBatch` | `transactions` | `List<CanonicalTransaction>` | Yes | May be empty. |
| `TransactionBatch` | `nextCursorToken` | `string` | No | Returned when more fetches can continue from a vendor token. |
| `TransactionBatch` | `highWatermarkUtc` | `datetime` | No | Returned when cursor progression is time-based. |
| `TransactionBatch` | `hasMore` | `boolean` | Yes | `true` when immediate follow-up fetch should continue. |
| `TransactionBatch` | `sourceBatchId` | `string` | No | Vendor batch/message identifier for diagnostics. |
| `ValidationResult` | `isValid` | `boolean` | Yes | `false` means normalization must not be attempted. |
| `ValidationResult` | `errorCode` | `string` | No | Stable vendor-neutral code such as `INVALID_JSON`, `MISSING_REQUIRED_FIELD`, `UNSUPPORTED_MESSAGE_TYPE`. |
| `ValidationResult` | `message` | `string` | No | Short diagnostic string. |
| `ValidationResult` | `recoverable` | `boolean` | Yes | `true` when retry with corrected payload or transient dependency may succeed. |
| `AdapterInfo` | `vendor` | `FccVendor` | Yes | Registration key. |
| `AdapterInfo` | `adapterVersion` | `string` | Yes | Semantic version of the adapter package/module. |
| `AdapterInfo` | `supportedTransactionModes` | `List<TransactionMode>` | Yes | Subset of `PULL`, `PUSH`, `HYBRID`. |
| `AdapterInfo` | `supportsPreAuth` | `boolean` | Yes | Cloud DOMS = `false`; edge DOMS = `true`. |
| `AdapterInfo` | `supportsPumpStatus` | `boolean` | Yes | Edge-only meaningful capability. |
| `AdapterInfo` | `protocol` | `string` | Yes | `REST`, `TCP`, or `SOAP`; DOMS MVP = `REST`. |
| `PreAuthCommand` | `siteCode` | `string` | Yes | Used to resolve FCC config and mappings. |
| `PreAuthCommand` | `pumpNumber` | `int` | Yes | Physical pump number. |
| `PreAuthCommand` | `nozzleNumber` | `int` | No | Required when FCC needs explicit nozzle selection. |
| `PreAuthCommand` | `amountMinorUnits` | `long` | Yes | Authorized amount. |
| `PreAuthCommand` | `currencyCode` | `string` | Yes | Must match site config. |
| `PreAuthCommand` | `odooOrderId` | `string` | No | Echo field for later correlation when vendor supports it. |
| `PreAuthCommand` | `customerTaxId` | `string` | No | Required when site fiscalization config requires it. |
| `PreAuthResult` | `status` | `string` | Yes | `AUTHORIZED`, `DECLINED`, `TIMEOUT`, `ERROR`. |
| `PreAuthResult` | `authorizationCode` | `string` | No | Vendor reference when successful. |
| `PreAuthResult` | `expiresAtUtc` | `datetime` | No | Returned when FCC provides authorization expiry. |
| `PreAuthResult` | `message` | `string` | No | Operator-safe outcome detail. |

### 5.3 Semantic Equivalence Rules

| Cloud member | Edge member | Equivalent | Notes |
|---|---|---|---|
| `NormalizeTransaction` | `normalize` | Yes | Same input envelope and same canonical output rules. |
| `FetchTransactions` | `fetchTransactions` | Yes | Same cursor progression contract, batch semantics, and normalization responsibility. |
| `GetAdapterMetadata` | Kotlin adapter static metadata property or method | Yes | Edge implementation must expose the same metadata values for registration and health endpoints, even if language packaging differs. |
| `ValidatePayload` | None | Partial | Edge may call `normalize` directly; invalid raw input must fail with a typed adapter exception equivalent to `ValidationResult.isValid = false`. |
| None | `sendPreAuth` | No cloud equivalent | Pre-auth is edge-only per `REQ-6` and `REQ-15`. |
| None | `getPumpStatus` | No cloud equivalent | Pump state is local FCC runtime state, not a cloud adapter concern. |
| None | `heartbeat` | No cloud equivalent | Heartbeat is an edge operational capability over LAN. |

### 5.4 Registration and Discovery

| Rule | Specification |
|---|---|
| Registration key | One adapter binding per `FccVendor` per runtime. |
| Cloud resolution | `IFccAdapterFactory.Resolve(FccVendor vendor, SiteFccConfig config)` returns the registered vendor adapter after validating `transactionMode`, `ingestionMode`, and protocol compatibility. |
| Edge resolution | `FccAdapterFactory.resolve(vendor: FccVendor, config: AgentFccConfig)` returns the vendor adapter for the provisioned site config. |
| Startup validation | Application startup must fail if an active configured vendor lacks a registered adapter binding. |
| Config inputs | Minimum: `fccVendor`, `connectionProtocol`, `hostAddress`, `port`, `authCredentials`, `transactionMode`, `pullIntervalSeconds`, `productCodeMapping`, `timezone`, `currencyCode`, `pumpNumberOffset`. |
| Override rule | Runtime config may override endpoint/auth values, but never the adapter vendor key. |
| Unknown vendor | Resolve failure returns configuration error `ADAPTER_NOT_REGISTERED`; no fallback adapter is permitted. |

### 5.5 DOMS MVP Adapter Contract

| Item | DOMS MVP specification |
|---|---|
| Protocol type | `REST` |
| Payload format | `application/json`, UTF-8 |
| Authentication | Static API key in header `X-API-Key`; optional site-scoped basic auth may be layered by deployment but is not the adapter contract default |
| Base URL | `http://{hostAddress}:{port}/api/v1` on LAN for edge; `https://{hostAddress}:{port}/api/v1` when cloud-direct deployment exposes DOMS through a secured public endpoint |
| Fetch transactions endpoint | `GET /transactions?since={ISO8601UTC}&cursor={token?}&limit={n}` |
| Fetch success response | JSON object containing `transactions[]`, optional `nextCursor`, optional `hasMore`, optional `sourceBatchId` |
| Push validation target shape | JSON object containing one transaction object or `transactions[]`; adapter must accept both without a config flag |
| Pre-auth endpoint | `POST /preauth` with JSON body derived from `PreAuthCommand` |
| Pump status endpoint | `GET /pump-status` returning array of pump-nozzle status objects |
| Heartbeat endpoint | `GET /heartbeat` returning `200 OK` with JSON `{ "status": "UP" }` |
| Idempotency expectation | DOMS may return overlapping transactions across fetch calls; adapter must not suppress overlaps and must advance only from returned cursor/high watermark |

## 6. Validation and Edge Cases
- `RawPayloadEnvelope.vendor` must equal the configured `fccVendor`; mismatch is a non-recoverable validation error.
- `TransactionBatch.transactions` may be empty with `hasMore = false`; this is a valid no-data poll result.
- `normalize` / `NormalizeTransaction` must reject payloads containing multiple transactions; multi-item payloads must be iterated by caller or by `FetchTransactions`.
- `fetchTransactions` must be side-effect free on vendor state except for vendor-defined cursor acknowledgment implicit in the request parameters.
- `heartbeat()` returning `true` does not imply transaction fetch or pre-auth success; it proves only authenticated endpoint reachability.
- When DOMS returns HTTP `401` or `403`, adapter must surface a non-recoverable auth error; HTTP `408`, `429`, and `5xx` are recoverable.

## 7. Cross-Component Impact
- Cloud Backend: implements adapter registry, vendor packages, pull worker integration, and push-payload validation.
- Edge Agent: implements LAN adapter operations, pre-auth flow, heartbeat telemetry, and poll worker integration.
- Angular Portal: indirectly affected through FCC config fields that drive adapter selection and protocol settings.

## 8. Dependencies
- Prerequisites: `CanonicalTransaction` contract, `PumpStatus` contract, shared enums, site/FCC config schema
- Downstream TODOs affected: DOMS field-level mapping, error handling strategy, edge local API, cloud ingest contracts, DOMS PoC in `4.1`
- Recommended next implementation step: define the DOMS field mapping artefact and then pin cloud/edge configuration schemas to the registration inputs above

## 9. Open Questions
None.

## 10. Acceptance Checklist
- [ ] Cloud `.NET` `IFccAdapter` methods and return contracts are fixed.
- [ ] Edge Kotlin `IFccAdapter` methods and return contracts are fixed.
- [ ] Shared supporting types required by the interfaces are defined at contract level.
- [ ] Cloud/edge semantic equivalence is explicit, including intentional edge-only operations.
- [ ] Factory-based adapter registration and config-driven resolution rules are fixed.
- [ ] DOMS MVP protocol, auth mode, endpoints, and payload format are concrete enough to implement.

## 11. Output Files to Create
- `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md`

## 12. Recommended Next TODO
Document field-level mapping: raw FCC payload to canonical model for the DOMS adapter as reference.
