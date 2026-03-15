# SiteConfig Contract

## 1. Output Location
- Target file path: `docs/specs/config/tier-1-1-site-config-schema.md`
- Optional companion files: `schemas/config/site-config.schema.json`
- Why this location matches `docs/STRUCTURE.md`: `SiteConfig` is a cloud-to-edge configuration contract, so the human-readable spec belongs in `/docs/specs/config` and the machine-readable schema belongs in `/schemas/config`.

## 2. Scope
- TODO item addressed: `Define SiteConfig — full configuration object pushed from cloud to Edge Agent`
- In scope: effective config sections, field-level ownership, mutability, and reload behavior
- Out of scope: config API status codes, persistence schema, portal UX, vendor protocol internals

## 3. Source Traceability
- Requirements referenced: `REQ-2`, `REQ-3`, `REQ-4`, `REQ-5`, `REQ-9.6`, `REQ-10`, `REQ-12`, `REQ-15.1`, `REQ-15.2`, `REQ-15.3a`, `REQ-15.4`, `REQ-15.5`, `REQ-15.8`, `REQ-15.9`, `REQ-15.10`, `REQ-15.11`, `REQ-15.12`, `REQ-15.13`
- HLD sections referenced: `WIP-HLD-Cloud-Backend.md` sections covering `Configuration Module` and `GET /api/v1/agent/config`; `WIP-HLD-Edge-Agent.md` sections covering cloud sync, security architecture, provisioning flow, and deployment architecture; `WIP-HLD-Angular-Portal.md` sections covering site and FCC management
- Assumptions from TODO ordering/dependencies: shared enums are defined separately; API details are defined in Tier `1.3`; Tier `2.4` should build on this contract rather than redefine it

## 4. Key Decisions

| Decision | Why | Impact |
|---|---|---|
| `SiteConfig` is a full snapshot, not a patch. | Edge should apply one authoritative effective config per version. | Every config pull returns the complete resolved object. |
| `schemaVersion` and `configVersion` are separate. | Schema evolution and site edits change at different cadences. | Edge can reject unsupported schema versions and stale config versions independently. |
| Effective config is resolved by cloud before delivery. | Inheritance and override logic should not be duplicated on-device. | Edge applies only final values, not merge rules. |
| Each field is classified by change behavior. | The agent needs deterministic apply behavior. | Fields are marked provisioning-only, hot-reload, or restart-required. |

## 5. Detailed Specification

### Top-level sections

| Section | Required | Owned By | Reload Behavior | Description |
|---|---|---|---|---|
| `schemaVersion` | Yes | Cloud | Hot-reload | Contract version for schema compatibility checks. |
| `configVersion` | Yes | Cloud | Hot-reload | Monotonic config revision for stale-update protection. |
| `configId` | Yes | Cloud | Hot-reload | Unique issuance identifier for audit and troubleshooting. |
| `issuedAtUtc` | Yes | Cloud | Hot-reload | UTC timestamp when the config snapshot was generated. |
| `effectiveAtUtc` | Yes | Cloud | Hot-reload | UTC timestamp when the config becomes active. |
| `sourceRevision` | Yes | Cloud | Hot-reload | Source-system revision metadata used for traceability. |
| `identity` | Yes | Cloud | Provisioning-only | Site and device identity block. |
| `site` | Yes | Cloud | Hot-reload | Operating model and site-level business settings. |
| `fcc` | Yes | Cloud | Restart-required | FCC connection, polling, and ingestion settings. |
| `sync` | Yes | Cloud | Hot-reload | Cloud upload, polling, and replay behavior. |
| `buffer` | Yes | Cloud | Hot-reload | Local retention and cleanup settings. |
| `localApi` | Yes | Cloud | Restart-required | Local/LAN API exposure and security settings. |
| `siteHa` | Yes | Cloud | Hot-reload | Site high-availability policy, peer directory bootstrap, and leader-fencing metadata. |
| `telemetry` | Yes | Cloud | Hot-reload | Health-reporting interval and verbosity settings. |
| `fiscalization` | Yes | Cloud | Hot-reload | Fiscalization mode and customer-tax requirements. |
| `mappings` | Yes | Cloud | Hot-reload | Product and pump/nozzle canonical mappings. |
| `rollout` | Yes | Cloud | Hot-reload | Agent-version gating and config TTL settings. |

### Section contents

| Section | Key fields | Description |
|---|---|---|
| `sourceRevision` | `databricksSyncAtUtc`, `siteMasterRevision`, `fccConfigRevision`, `portalChangeId` | Traceability fields showing which upstream revisions produced this snapshot. |
| `identity` | `legalEntityId`, `legalEntityCode`, `siteId`, `siteCode`, `siteName`, `timezone`, `currencyCode`, `deviceId`, `deviceClass`, `isPrimaryAgent` | Stable identity and tenancy values for the registered agent. `isPrimaryAgent` remains a compatibility field; new HA behavior is driven by `siteHa`. |
| `site` | `isActive`, `operatingModel`, `connectivityMode`, `odooSiteId`, `companyTaxPayerId`, `operatorName`, `operatorTaxPayerId` | Site-level business settings and tax identity values. |
| `fcc` | `enabled`, `fccId`, `vendor`, `model`, `version`, `connectionProtocol`, `hostAddress`, `port`, `credentialRef`, `credentialRevision`, `secretEnvelope`, `transactionMode`, `ingestionMode`, `pullIntervalSeconds`, `catchUpPullIntervalSeconds`, `hybridCatchUpIntervalSeconds`, `heartbeatIntervalSeconds`, `heartbeatTimeoutSeconds`, `pushSourceIpAllowList` | FCC connectivity, credentials reference, and ingestion behavior. |
| `sync` | `cloudBaseUrl`, `uploadBatchSize`, `uploadIntervalSeconds`, `syncedStatusPollIntervalSeconds`, `configPollIntervalSeconds`, `cursorStrategy`, `maxReplayBackoffSeconds`, `initialReplayBackoffSeconds`, `maxRecordsPerUploadWindow` | Cloud communication cadence and replay controls. |
| `buffer` | `retentionDays`, `stalePendingDays`, `maxRecords`, `cleanupIntervalHours`, `persistRawPayloads` | Local SQLite retention, stale detection, and cleanup settings. |
| `localApi` | `localhostPort`, `enableLanApi`, `lanBindAddress`, `lanAllowCidrs`, `lanApiKeyRef`, `rateLimitPerMinute` | Local and LAN API listener configuration. |
| `siteHa` | `enabled`, `autoFailoverEnabled`, `priority`, `roleCapability`, `currentRole`, `heartbeatIntervalSeconds`, `failoverTimeoutSeconds`, `maxReplicationLagSeconds`, `peerDiscoveryMode`, `allowFailback`, `leaderAgentId`, `leaderEpoch`, `leaderSinceUtc`, `peerDirectory[]` | Site-level active-standby policy, per-device priority, current leader metadata, and bootstrap peer directory used before LAN discovery converges. |
| `telemetry` | `telemetryIntervalSeconds`, `logLevel`, `includeDiagnosticsLogs`, `metricsWindowSeconds` | Edge telemetry cadence and diagnostics verbosity. |
| `fiscalization` | `mode`, `taxAuthorityEndpoint`, `requireCustomerTaxId`, `fiscalReceiptRequired` | Fiscalization and tax data collection settings. |
| `mappings` | `priceDecimalPlaces`, `volumeUnit`, `products`, `nozzles` | Canonical mapping data needed for normalization and local operations. `nozzles` is an array of `{ odooPumpNumber, fccPumpNumber, odooNozzleNumber, fccNozzleNumber, productCode }` — one entry per active nozzle at the site. The Edge Agent caches this array in its local `nozzles` Room table and uses it to translate Odoo POS pump/nozzle numbers into FCC pump/nozzle numbers before sending every pre-auth command to the FCC. |
| `rollout` | `minAgentVersion`, `maxAgentVersion`, `requiresRestartSections`, `configTtlHours` | Version gating and operational rollout controls. |

## 6. Validation and Edge Cases
- `configVersion` must increase monotonically for a given site and device.
- Edge must reject configs with unsupported `schemaVersion`.
- `fcc` settings may remain populated when the site is disconnected, but the agent must not use them while `connectivityMode = DISCONNECTED`.
- `credentialRef` and `lanApiKeyRef` are opaque references; secrets should not be logged.
- `siteHa.peerDirectory` is authoritative bootstrap data from the cloud control plane, not a proof of current LAN reachability. Runtime peer health must still be established through heartbeat/peer probes.
- `siteHa.leaderEpoch` must increase monotonically whenever leadership changes. Authoritative cloud writes from agents must carry the current epoch once fencing is enabled.
- Restart-required sections must not be partially applied.
- `mappings.nozzles` must not be empty for connected sites. If a nozzle entry is missing, pre-auth for that pump/nozzle combination will fail with `NOZZLE_MAPPING_NOT_FOUND`. This is a config error — raise an alert and investigate master data sync.
- `mappings.nozzles` entries must be unique by `(odooPumpNumber, odooNozzleNumber)` within the site. Duplicate entries must be rejected during config validation on the Edge Agent.

## 7. Cross-Component Impact
- Cloud Backend: resolves and serves the effective snapshot.
- Edge Agent: validates, stores, and applies the snapshot atomically.
- Angular Portal: edits the source values that roll into the resolved config.

## 8. Dependencies
- Prerequisites: shared enums, device registration, **pumps and nozzles master data sync** (each pump must have `fcc_pump_number`; each nozzle must have `odoo_nozzle_number`, `fcc_nozzle_number`, and `product_id` before a valid config snapshot can be built)
- Downstream TODOs affected: config API spec, security implementation, FCC adapter registration, rollout behavior
- Recommended next implementation step: define the config API contract and version-compatibility rules against this schema

## 9. Open Questions
None.

## 10. Acceptance Checklist
- [ ] Every top-level `SiteConfig` section is fixed with owner and reload behavior.
- [ ] Key fields per section are listed without duplicating full schema prose.
- [ ] Cloud-resolved snapshot behavior is explicit.
- [ ] Versioning and secret-handling rules are clear.
- [ ] Companion schema aligns with this contract.

## 11. Output Files to Create
- `docs/specs/config/tier-1-1-site-config-schema.md`
- `schemas/config/site-config.schema.json`

## 12. Recommended Next TODO
Configuration Schema (Full) implementation and rollout behavior.
