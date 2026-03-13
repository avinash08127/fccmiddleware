# Adapter Portal Screens Plan

## Goal

Enable portal users to:

1. View all backend-supported adapters in a dedicated `Adapters` screen.
2. View and edit adapter default settings.
3. View and edit site-level adapter overrides when a site must differ from the default.
4. Track every change with a complete audit trail.
5. Add new adapters in the backend without requiring new hard-coded portal UI work.

## Current State Review

### What exists today

- Cloud backend stores site FCC config in a wide `fcc_configs` table with one row per site and vendor-specific columns.
- Portal already has a site-level FCC editor under `Sites`, plus pump/nozzle mapping and generic audit screens.
- Cloud adapters already expose basic capability metadata through `AdapterInfo`.

### Gaps that block the requested functionality

1. The data model is site-specific, not adapter-default-plus-site-override.
   - `src/cloud/FccMiddleware.Domain/Entities/FccConfig.cs`
   - `db/migrations/001-initial-schema.sql`

2. The model is not future-proof for new adapters.
   - New vendor fields require DB columns, DTO changes, controller changes, and portal form changes.
   - Portal vendor options are hard-coded to 4 vendors.
   - `src/cloud/FccMiddleware.Infrastructure/Adapters/CloudFccAdapterFactoryRegistration.cs`
   - `src/portal/src/app/features/site-config/fcc-config-form.component.ts`

3. The runtime model and persisted model already drift.
   - `SiteFccConfig` contains fields not persisted or surfaced today, including `PumpNumberOffset` and `ProductCodeMapping`.
   - `SiteFccConfigProvider` currently hard-codes those to `0` and `{}`.
   - `src/cloud/FccMiddleware.Domain/Models/Adapter/SiteFccConfig.cs`
   - `src/cloud/FccMiddleware.Infrastructure/Repositories/SiteFccConfigProvider.cs`

4. Backend, portal, and edge contracts are out of sync.
   - Portal model contains `advatecWebhookListenerPort`, but cloud contracts/entities do not.
   - Cloud entity contains `DppPorts` and `ReconnectBackoffMaxSeconds`, but portal contracts/UI do not.
   - Edge `SiteConfigResponse.FccDto` omits vendor-specific fields entirely, even though `AgentFccConfig` supports them.
   - `src/portal/src/app/core/models/site.model.ts`
   - `src/cloud/FccMiddleware.Contracts/Portal/PortalSiteContracts.cs`
   - `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs`
   - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt`
   - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/AdapterTypes.kt`

5. Secrets are not handled safely for portal management.
   - Current read models return raw secret fields.
   - `SitesController.MapFccConfig` exposes values like `SharedSecret`, `ClientSecret`, `WebhookSecret`, `FcAccessCode`, and `AdvatecWebhookToken`.
   - `src/cloud/FccMiddleware.Api/Controllers/SitesController.cs`

6. Current audit events are not sufficient.
   - FCC/site events only record shallow metadata, not before/after diffs.
   - No required change reason.
   - Portal audit event enum does not include current FCC/site config events.
   - `src/cloud/FccMiddleware.Api/Controllers/SitesController.cs`
   - `src/portal/src/app/core/models/audit.model.ts`

7. Current update semantics cannot support clear/reset/inherit cleanly.
   - `UpdateFccConfigRequestDto` uses nullable patch fields, so `null` means "not provided", not "clear value".
   - This will not support "reset site override to adapter default".
   - `src/cloud/FccMiddleware.Contracts/Portal/PortalSiteContracts.cs`

## Recommended Target Design

Do not continue extending the current wide `FccConfig` model and hard-coded portal FCC form.

Use a metadata-driven adapter configuration model:

1. Adapter catalog
   - Backend registry of all supported adapters.
   - Each adapter publishes metadata, capabilities, config schema, UI schema, validation rules, and secret field definitions.

2. Adapter defaults
   - One default configuration profile per adapter.
   - Editable from the new `Adapters` screen.

3. Site overrides
   - Site-specific override payload storing only fields that differ from the adapter default.
   - Effective config = adapter default + site override + site/master-data-derived mappings.

4. Schema-driven portal UI
   - Portal renders adapter forms from backend field metadata instead of hard-coded vendor switch logic.
   - New adapters appear automatically once backend registration exists.

## Portal Screens To Build

### 1. Adapters List Screen

Route: `/adapters`

Display:

- Adapter name
- Adapter key
- Vendor
- Version
- Supported protocols
- Supported ingestion methods
- Capability badges
- Active site count
- Last default update info

Actions:

- View adapter details
- Edit defaults
- View sites using adapter
- Open audit history filtered to this adapter

### 2. Adapter Detail Screen

Route: `/adapters/:adapterKey`

Tabs/sections:

- Overview
  - Capabilities, version, backend registration info, docs/help text
- Default Settings
  - Schema-driven form
  - Diff preview before save
  - Save reason required
- Sites Using This Adapter
  - List sites, current effective config version, override count, link to site override view
- Audit History
  - Filtered adapter-level change history

### 3. Site Adapter Override View

Can live under the existing site detail page or as `/adapters/:adapterKey/sites/:siteId`.

Display:

- Effective config
- Which values come from adapter default
- Which values are site overrides
- Reset-to-default action per field or section
- Secret fields as masked metadata only (`set` / `not set`, last rotated info)

Actions:

- Save override changes
- Clear override / inherit default
- Open audit history filtered to site + adapter

## Required Cloud Backend Changes

### A. Domain and persistence

1. Add an adapter catalog model.
   - Example entities: `AdapterDefinition`, `AdapterDefaultConfig`, `SiteAdapterOverride`
   - Prefer `jsonb` payloads plus schema metadata over more vendor-specific columns.

2. Decouple portal/backend contracts from hard-coded enum-only rendering.
   - Keep runtime enums where needed internally.
   - Expose adapter identity to portal as stable string keys plus metadata from the backend catalog.

3. Preserve or migrate the current site binding concept.
   - A site still needs one active adapter association.
   - That association should point to an adapter definition plus override payload, not a vendor-specific wide row.

4. Persist currently missing runtime config fields.
   - `PumpNumberOffset`
   - `ProductCodeMapping`
   - `DppPorts`
   - `ReconnectBackoffMaxSeconds`
   - Any edge/runtime ports that must be centrally managed

5. Add versioning and concurrency.
   - Default config version
   - Site override version
   - ETag or row-version style optimistic concurrency

### B. Backend adapter registration

Extend adapter registration so each adapter provides:

- Adapter key
- Display name
- Vendor
- Version
- Supported protocols
- Supported ingestion methods
- Capability flags
- Config schema
- UI schema/grouping
- Field-level validation rules
- Secret field definitions
- Default values

This should be added alongside or as an expansion of `AdapterInfo`.

### C. Runtime config composition

Refactor config assembly so effective adapter config is built from:

1. adapter defaults
2. site overrides
3. site/legal-entity/master-data mappings
4. secret resolution from secure storage

This affects:

- `SiteFccConfigProvider`
- cloud adapter resolution
- edge `GetAgentConfigHandler`

### D. API surface

Add new endpoints:

- `GET /api/v1/adapters`
- `GET /api/v1/adapters/{adapterKey}`
- `GET /api/v1/adapters/{adapterKey}/defaults`
- `PUT /api/v1/adapters/{adapterKey}/defaults`
- `GET /api/v1/sites/{siteId}/adapter-config`
- `PUT /api/v1/sites/{siteId}/adapter-config/overrides`
- `POST /api/v1/sites/{siteId}/adapter-config/reset`

Contract requirements:

- Return backend-provided field metadata for rendering
- Support explicit `set`, `clear`, and `inherit` operations
- Return field-level validation errors
- Never return raw secret values after initial save

Keep `/api/v1/sites/{siteId}/fcc-config` only as a compatibility wrapper during migration.

### E. Secrets and security

1. Stop returning raw secret values in portal read models.
2. Store only secret refs or encrypted secret envelopes in durable storage.
3. Keep indexed hashes only where lookup is needed.
4. Mark secret fields as write-only in APIs.
5. Audit only masked secret changes, never raw values.

## Required Portal Frontend Changes

### New feature area

Add a new `Adapters` feature module and nav item:

- `src/portal/src/app/app.routes.ts`
- `src/portal/src/app/core/layout/shell.component.ts`
- new `src/portal/src/app/features/adapters/*`

### Frontend architecture changes

1. Replace hard-coded adapter/vendor forms with schema-driven rendering.
2. Replace hard-coded vendor option arrays with backend catalog data.
3. Replace closed audit event enum assumptions with backend-compatible event type handling.
4. Add reusable field renderers for:
   - text
   - password/secret
   - number
   - boolean
   - select
   - JSON editor / key-value map
   - grouped advanced fields

### Existing screen changes

1. Remove or reduce the hard-coded FCC editor inside site detail.
2. Keep pump/nozzle mapping in the site area, but link to adapter overrides/effective config.
3. Add save-reason modal for adapter default changes and site override changes.
4. Add "reset to default" UI and override badges.

## Audit Trail Requirements

Every adapter config mutation must create an immutable audit event with:

- event type
- timestamp
- actor user ID
- actor display name if available
- legal entity ID
- site ID / site code when site-scoped
- adapter key
- correlation/change-set ID
- reason/comment
- previous version
- new version
- field-level diff
- secret-field redaction markers

Recommended event types:

- `AdapterDefaultConfigUpdated`
- `AdapterDefaultSecretRotated`
- `SiteAdapterOverrideSet`
- `SiteAdapterOverrideCleared`
- `SiteAdapterOverrideResetToDefault`

Also update portal audit filtering so these event types are visible and searchable.

## Migration and Rollout

1. Add new adapter catalog/default/override tables and APIs.
2. Seed current adapters: `DOMS`, `RADIX`, `PETRONITE`, `ADVATEC`.
3. Backfill current `fcc_configs` into the new structure.
4. Update `GetAgentConfigHandler` and `SiteFccConfigProvider` to read the new effective config.
5. Build and release the new portal `Adapters` screens.
6. Switch site detail to read from the new effective-config API.
7. Deprecate the legacy hard-coded `/sites/{siteId}/fcc-config` flow.

## Testing Required

### Backend

- adapter catalog discovery
- default config validation
- site override merge logic
- clear/inherit/reset semantics
- audit event creation and redaction
- role-based access control
- edge config serialization including vendor-specific fields

### Portal

- adapter list/detail rendering from backend metadata
- default config edit/save
- site override edit/reset
- reason-required flow
- audit history filters for adapter events
- rendering of a newly added backend adapter without portal code changes

### End-to-end

- seed a new adapter in backend test fixtures
- verify it appears in the portal
- change default config
- apply a site override
- verify audit events and effective runtime config

## Concrete Code Areas Likely To Change

### Cloud backend

- `src/cloud/FccMiddleware.Domain/Models/Adapter/AdapterInfo.cs`
- `src/cloud/FccMiddleware.Domain/Models/Adapter/SiteFccConfig.cs`
- `src/cloud/FccMiddleware.Domain/Entities/FccConfig.cs` or its replacement
- `src/cloud/FccMiddleware.Infrastructure/Repositories/SiteFccConfigProvider.cs`
- `src/cloud/FccMiddleware.Infrastructure/Adapters/CloudFccAdapterFactoryRegistration.cs`
- `src/cloud/FccMiddleware.Api/Controllers/SitesController.cs`
- `src/cloud/FccMiddleware.Api/Controllers/AuditController.cs`
- `src/cloud/FccMiddleware.Contracts/Portal/PortalSiteContracts.cs`
- `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs`
- new DB migrations under `db/migrations`

### Portal frontend

- `src/portal/src/app/app.routes.ts`
- `src/portal/src/app/core/layout/shell.component.ts`
- `src/portal/src/app/core/models/site.model.ts`
- `src/portal/src/app/core/models/audit.model.ts`
- `src/portal/src/app/core/services/site.service.ts`
- `src/portal/src/app/core/services/audit.service.ts`
- `src/portal/src/app/features/site-config/fcc-config-form.component.ts`
- `src/portal/src/app/features/site-config/site-detail.component.ts`
- new `src/portal/src/app/features/adapters/*`

### Edge agent

- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/ConfigManager.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/AdapterTypes.kt`

## Implementation Recommendation

Treat this as a backend-first change.

If the portal must support new adapters without frontend rework, the backend must become the source of truth for adapter metadata and field schema. The current hard-coded vendor/form approach cannot satisfy that requirement with only incremental UI changes.
