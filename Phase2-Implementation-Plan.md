# Phase 2 — Implementation Plan (Agent-Assignable Tasks)

**Version:** 1.0
**Created:** 2026-03-13
**Source:** Phase2.md (analysis document)
**Status:** READY FOR DEVELOPMENT

---

## How to Use This Plan

Each task below is a **self-contained work unit** designed to be assigned to a single development agent. Tasks include:
- **Scope** — exactly which files to create/modify
- **Depends on** — which tasks must be completed first
- **Acceptance criteria** — what "done" looks like
- **Key references** — existing code the agent must read before starting

Tasks are grouped into 5 phases, ordered by dependency. Within each phase, tasks that have no interdependencies can run **in parallel**.

---

## Dependency Graph

```
Phase 2.5 (Site Data)          Phase 2.4 (Env Switch)       Phase 2.3 (Settings)
  T5.1 ──► T5.2 ──► T5.3       T4.1 ──► T4.2 ──► T4.3      T3.1 ──► T3.2 ──► T3.3
             │                           │                             │
             ▼                           ▼                             ▼
           T5.4                        T4.4                          T3.4
                                                                       │
Phase 2.2 (Network)                                                    ▼
  T2.1 ──► T2.2 ──► T2.3                                            T3.5

Phase 2.1 (WebSocket) — highest complexity, start after P2.5/P2.3 basics land
  T1.1 ──► T1.2 ──► T1.3 ──► T1.4
                       │
                       ▼
                     T1.5 ──► T1.6
```

---

# PHASE 2.5 — Post-Registration Site Data Fetch & Persistent Storage

> **Why first:** No dependencies on other phases. Establishes the local data layer that P2.1 (WebSocket) and P2.3 (Settings) will query.

---

### T5.1 — Android: Room Entities & DAO for Site Master Data

**Platform:** Android Edge-Agent
**Depends on:** Nothing
**Parallel with:** T5.4, T4.1, T3.1

**Goal:** Create Room entities and DAO for locally persisted site equipment data (products, pumps, nozzles, site info).

**New files to create:**

| File | Purpose |
|------|---------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/entity/SiteInfo.kt` | `@Entity(tableName = "site_info")` — site identity, FCC vendor/model, operating mode. Single row (siteCode as PK). |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/entity/LocalProduct.kt` | `@Entity(tableName = "local_products")` — `fccProductCode` (PK), `canonicalProductCode`, `displayName`, `active` |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/entity/LocalPump.kt` | `@Entity(tableName = "local_pumps")` — `odooPumpNumber` (PK), `fccPumpNumber`, `displayName` |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/entity/LocalNozzle.kt` | `@Entity(tableName = "local_nozzles")` — `odooNozzleNumber`+`odooPumpNumber` (composite PK), `fccNozzleNumber`, `fccPumpNumber`, `productCode` |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/dao/SiteDataDao.kt` | Room DAO: `insertSiteInfo()`, `insertProducts()`, `insertPumps()`, `insertNozzles()`, `getSiteInfo()`, `getAllProducts()`, `getAllPumps()`, `getNozzlesForPump()`, `deleteAllSiteData()`, `@Transaction replaceAll*(list)` |

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/BufferDatabase.kt` | Add `SiteInfo`, `LocalProduct`, `LocalPump`, `LocalNozzle` to `@Database(entities = [...])`. Add `abstract fun siteDataDao(): SiteDataDao`. Increment DB version and add migration. |

**Key references to read first:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/BufferDatabase.kt` — existing Room DB structure, migration pattern
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt` — `MappingsDto`, `ProductMappingDto`, `NozzleMappingDto` (source schema for entity fields)
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/dao/` — existing DAO patterns (TransactionBufferDao, SyncStateDao)

**Acceptance criteria:**
- [ ] All 4 Room entities compile with proper annotations
- [ ] `SiteDataDao` provides full CRUD for all entities
- [ ] `BufferDatabase` version incremented with migration that creates the 4 new tables
- [ ] Migration uses `fallbackToDestructiveMigration` ONLY for site data tables (they're repopulated from cloud)
- [ ] Existing tables/DAOs unaffected

---

### T5.2 — Android: SiteDataManager + Integration with Registration & Config Poll

**Platform:** Android Edge-Agent
**Depends on:** T5.1

**Goal:** Create `SiteDataManager` that extracts site equipment from `EdgeAgentConfigDto` and persists it to Room. Wire into registration and config poll flows.

**New files to create:**

| File | Purpose |
|------|---------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/SiteDataManager.kt` | Stateless class: `suspend fun syncFromConfig(config: EdgeAgentConfigDto)` — extracts `mappings.products[]` → `LocalProduct`, `mappings.nozzles[]` → `LocalPump` + `LocalNozzle`, `fcc.*` → `SiteInfo`. Uses `@Transaction` to atomically replace all site data. Logs summary: "Site data synced: N products, M pumps, K nozzles". |

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/ProvisioningActivity.kt` | After successful registration (where `siteConfig` is returned in `DeviceRegistrationResponse`), call `siteDataManager.syncFromConfig(parsedConfig)`. Inject `SiteDataManager` via Koin. |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/ConfigPollWorker.kt` | After a successful config apply (HTTP 200 with new config), call `siteDataManager.syncFromConfig(newConfig)`. |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/di/AppModule.kt` | Register `SiteDataManager` as singleton in Koin; inject `SiteDataDao`. |

**Key references to read first:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt` — `MappingsDto`, `ProductMappingDto`, `NozzleMappingDto` structure
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/ProvisioningActivity.kt` — registration success flow, where config is first received
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/ConfigPollWorker.kt` — config apply flow
- `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs` — cloud-side config contract (to understand field mapping)

**Acceptance criteria:**
- [ ] `SiteDataManager.syncFromConfig()` correctly maps `ProductMappingDto[]` → `LocalProduct[]`, `NozzleMappingDto[]` → `LocalPump[]` + `LocalNozzle[]`
- [ ] Registration flow calls `syncFromConfig` after success
- [ ] Config poll calls `syncFromConfig` after applying new config
- [ ] Site data is replaced atomically (old data cleared, new data inserted in single transaction)
- [ ] Handles empty mappings gracefully (no crash on null/empty lists)

---

### T5.3 — Android: Display Site Data in Diagnostics

**Platform:** Android Edge-Agent
**Depends on:** T5.2

**Goal:** Show site equipment summary on the diagnostics screen.

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/DiagnosticsActivity.kt` | Add a "Site Data" section showing: FCC Type (vendor/model), Product count, Pump count, Nozzle count, Last synced timestamp. Query from `SiteDataDao`. |

**Key references to read first:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/DiagnosticsActivity.kt` — existing layout and refresh pattern

**Acceptance criteria:**
- [ ] Site data section visible on diagnostics screen
- [ ] Shows "No site data" when not yet provisioned
- [ ] Auto-refreshes with existing 5-second timer

---

### T5.4 — Desktop: Site Data Persistence (JSON File)

**Platform:** Desktop Edge-Agent
**Depends on:** Nothing
**Parallel with:** T5.1

**Goal:** Persist site equipment data as a JSON file on the desktop agent after registration and config pull.

**New files to create:**

| File | Purpose |
|------|---------|
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/MasterData/SiteDataManager.cs` | `SiteDataManager` class: `SyncFromConfig(SiteConfigResponse config)` — extracts products/pumps/nozzles, serializes to `site-data.json` in agent data directory. `LoadSiteData()` — reads from JSON at startup. |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/MasterData/Models/SiteInfo.cs` | Site identity + FCC type model |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/MasterData/Models/LocalProduct.cs` | Product mapping model |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/MasterData/Models/LocalPump.cs` | Pump mapping model |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/MasterData/Models/LocalNozzle.cs` | Nozzle mapping model |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/MasterData/Models/SiteDataSnapshot.cs` | Root model wrapping all site data + `lastSyncedUtc` timestamp |

**Files to modify:**

| File | Change |
|------|--------|
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Registration/RegistrationManager.cs` | After successful registration, call `SiteDataManager.SyncFromConfig()` with the returned site config. |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Runtime/ServiceCollectionExtensions.cs` | Register `SiteDataManager` as singleton in DI. |

**Key references to read first:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Registration/RegistrationManager.cs` — registration flow, data directory path
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/AgentConfiguration.cs` — config structure
- `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs` — cloud contract

**Acceptance criteria:**
- [ ] `site-data.json` written to agent data directory after registration
- [ ] File contains products, pumps, nozzles, FCC type
- [ ] `LoadSiteData()` returns data on next startup
- [ ] Handles missing file gracefully (first boot before registration)

---

# PHASE 2.3 — Local FCC Connection Configuration & Settings Page

> **Why second:** Depends on nothing from other phases. Establishes the override mechanism that P2.1 (WebSocket) and P2.4 (Environment) will use.

---

### T3.1 — Android: LocalOverrideManager

**Platform:** Android Edge-Agent
**Depends on:** Nothing
**Parallel with:** T5.1, T4.1

**Goal:** Create a manager that reads/writes local FCC config overrides in EncryptedSharedPreferences, with a merge function that overlays overrides onto cloud config.

**New files to create:**

| File | Purpose |
|------|---------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/LocalOverrideManager.kt` | Reads/writes override keys from `EncryptedSharedPreferences`. Methods: `getOverriddenFccConfig(cloudConfig: FccDto): AgentFccConfig` (merges overrides), `saveOverride(key, value)`, `clearOverride(key)`, `clearAllOverrides()`, `hasAnyOverrides(): Boolean`. Override keys: `override_fcc_host`, `override_fcc_port`, `override_fcc_jpl_port`, `override_fcc_credential`, `override_ws_port`. |

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt` | Modify `toAgentFccConfig()` to accept optional `LocalOverrideManager` and apply overrides before returning `AgentFccConfig`. |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/di/AppModule.kt` | Register `LocalOverrideManager` as singleton. |

**Key references to read first:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/security/EncryptedPrefsManager.kt` — existing encrypted prefs pattern
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt` — `FccDto`, `toAgentFccConfig()` conversion
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/AdapterTypes.kt` — `AgentFccConfig` target type

**Acceptance criteria:**
- [ ] Override values stored encrypted in SharedPreferences
- [ ] `getOverriddenFccConfig()` returns cloud values when no overrides exist
- [ ] `getOverriddenFccConfig()` returns override values when set, cloud values for un-overridden fields
- [ ] `clearAllOverrides()` restores to cloud defaults
- [ ] IP validation (valid IPv4/hostname), port validation (1-65535)

---

### T3.2 — Android: Settings Activity UI

**Platform:** Android Edge-Agent
**Depends on:** T3.1

**Goal:** Create a settings screen accessible from the diagnostics page where technicians can view/edit FCC connection overrides.

**New files to create:**

| File | Purpose |
|------|---------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/SettingsActivity.kt` | Activity with form fields for: FCC IP, FCC Port, FCC JPL Port, FCC Access Code (masked), WebSocket Port, WebSocket Enabled toggle. Read-only section: Cloud Base URL, Environment, Device ID, Site Code. Buttons: "Save & Reconnect" (writes to `LocalOverrideManager`, signals `CadenceController` to reconnect FCC adapter), "Reset to Cloud Defaults" (calls `clearAllOverrides()`). |
| `src/edge-agent/app/src/main/res/layout/activity_settings.xml` | Layout XML for settings form (or programmatic layout matching existing UI pattern) |

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/DiagnosticsActivity.kt` | Add "Settings" button/menu item that launches `SettingsActivity` |
| `src/edge-agent/app/src/main/AndroidManifest.xml` | Register `SettingsActivity` |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt` | Add public method `requestFccReconnect()` that triggers the adapter to disconnect and reconnect with new config. Called from settings "Save & Reconnect". |

**Key references to read first:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/DiagnosticsActivity.kt` — existing activity UI pattern
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/ProvisioningActivity.kt` — form input pattern
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt` — adapter lifecycle, reconnect mechanism

**Acceptance criteria:**
- [ ] Settings screen accessible from diagnostics
- [ ] Form pre-populated with current effective values (override if set, cloud if not)
- [ ] Override indicator ("(overridden)") shown next to fields with local overrides
- [ ] "Save & Reconnect" persists values and triggers adapter reconnect
- [ ] "Reset to Cloud Defaults" clears all overrides
- [ ] Validation prevents saving invalid IP or port values

---

### T3.3 — Android: Wire Overrides into Adapter Lifecycle

**Platform:** Android Edge-Agent
**Depends on:** T3.1

**Goal:** Ensure the FCC adapter uses overridden config when starting/reconnecting.

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/service/EdgeAgentForegroundService.kt` | When creating the FCC adapter, use `LocalOverrideManager.getOverriddenFccConfig()` instead of raw `config.toAgentFccConfig()`. |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt` | On FCC reconnect, re-read config with overrides applied. Log when overrides are active: "FCC config override active: host=X, port=Y (cloud default: host=A, port=B)". |

**Key references to read first:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/service/EdgeAgentForegroundService.kt` — adapter creation flow
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/FccAdapterFactory.kt` — how adapter receives config

**Acceptance criteria:**
- [ ] Adapter uses overridden host/port when overrides are set
- [ ] Adapter uses cloud values when no overrides exist
- [ ] Override values logged at adapter connect time
- [ ] Reconnect after settings change actually uses new values

---

### T3.4 — Desktop: Local Override Manager + Settings UI

**Platform:** Desktop Edge-Agent
**Depends on:** Nothing (can run parallel with T3.1-T3.3)

**Goal:** Desktop equivalent of local FCC overrides with a settings panel in the main window.

**New files to create:**

| File | Purpose |
|------|---------|
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/LocalOverrideManager.cs` | Reads/writes `overrides.json` from agent data directory. Methods: `GetEffectiveFccHost()`, `GetEffectiveFccPort()`, `SaveOverride(key, value)`, `ClearAllOverrides()`, `HasOverrides()`. Merges with `AgentConfiguration` values. |
| `src/desktop-edge-agent/src/FccDesktopAgent.App/ViewModels/SettingsViewModel.cs` | MVVM ViewModel binding for settings form: FCC IP, Port, JPL Port, WebSocket Port. Commands: SaveCommand, ResetCommand. |
| `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/SettingsPanel.axaml` | Avalonia XAML panel for settings form |
| `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/SettingsPanel.axaml.cs` | Code-behind |

**Files to modify:**

| File | Change |
|------|--------|
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/AgentConfiguration.cs` | Add `FccHostOverride`, `FccPortOverride` properties. Add method `GetEffectiveFccConfig(LocalOverrideManager overrides)`. |
| `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/MainWindow.axaml` | Add Settings tab/panel to the main window |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Runtime/ServiceCollectionExtensions.cs` | Register `LocalOverrideManager` in DI |

**Key references to read first:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/AgentConfiguration.cs` — current config structure
- `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/MainWindow.axaml` — existing UI layout
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Registration/RegistrationManager.cs` — data directory path

**Acceptance criteria:**
- [ ] `overrides.json` created in agent data directory when overrides are saved
- [ ] Settings panel accessible from main window
- [ ] Form pre-populated with current effective values
- [ ] "Save & Reconnect" triggers adapter reconnect
- [ ] "Reset" deletes `overrides.json` and reverts to cloud config

---

### T3.5 — Display Cloud API Routes (Read-Only)

**Platform:** Android + Desktop
**Depends on:** T3.2, T3.4

**Goal:** Add a read-only section to the settings page showing all cloud API routes the device uses.

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/SettingsActivity.kt` | Add read-only "Cloud API Routes" section listing: Registration, Config Poll, Token Refresh, Transaction Upload, Synced Status, Pre-Auth Forward, Telemetry, Diagnostic Logs, Version Check — each showing the full URL (baseUrl + route path). |
| Desktop: `SettingsPanel.axaml` | Same read-only route display |

**Key references:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt` — all route paths used

**Acceptance criteria:**
- [ ] All 9 cloud API routes displayed with full URLs
- [ ] Routes update if cloud base URL changes

---

# PHASE 2.4 — Cloud URL & Route Configuration / Environment Switching

> **Why third:** Independent of other phases. Small scope, low risk. The environment map is compiled into the app.

---

### T4.1 — Android + Desktop: Environment Map & QR Code v2

**Platform:** Android Edge-Agent, Desktop Edge-Agent
**Depends on:** Nothing
**Parallel with:** T5.1, T3.1

**Goal:** Add a built-in environment map and support v2 QR codes with an `env` field.

**New files to create:**

| File | Purpose |
|------|---------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/CloudEnvironments.kt` | `object CloudEnvironments { val ENVIRONMENTS: Map<String, CloudEnv> }` with entries for PRODUCTION, STAGING, DEVELOPMENT, LOCAL. `data class CloudEnv(val baseUrl: String, val displayName: String)`. `fun resolve(env: String?): String?` returns base URL or null if unknown. |

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/ProvisioningActivity.kt` | Parse `env` field from v2 QR code (`{ "v": 2, "sc": "...", "cu": "...", "pt": "...", "env": "STAGING" }`). When `env` is present, resolve URL from `CloudEnvironments`. When absent (v1 QR), use `cu` directly (backward compatible). Add environment dropdown to manual entry form. |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/security/EncryptedPrefsManager.kt` | Store `environment` string alongside `cloudBaseUrl`. |

**Key references to read first:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/ui/ProvisioningActivity.kt` — QR code parsing, registration flow
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/security/EncryptedPrefsManager.kt` — existing prefs storage

**Acceptance criteria:**
- [ ] v1 QR codes (`{ "v": 1, ... }`) continue to work unchanged
- [ ] v2 QR codes with `env` field resolve URL from built-in map
- [ ] Manual entry form has environment dropdown (Production/Staging/Development/Local)
- [ ] Selected environment stored in encrypted prefs
- [ ] Unknown `env` value falls back to explicit `cu` URL

---

### T4.2 — Android: CloudApiClient Environment Integration

**Platform:** Android Edge-Agent
**Depends on:** T4.1

**Goal:** `CloudApiClient` resolves base URL from the stored environment when set.

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt` | On initialization and `updateBaseUrl()`, check `EncryptedPrefsManager.environment`. If set, resolve from `CloudEnvironments`. Otherwise use explicit URL. |

**Key references:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt` — `updateBaseUrl()`, base URL resolution

**Acceptance criteria:**
- [ ] Base URL resolves from environment map when `environment` is set
- [ ] Falls back to explicit URL when `environment` is null
- [ ] URL change triggers OkHttp client rebuild (existing mechanism)

---

### T4.3 — Desktop: Environment Support

**Platform:** Desktop Edge-Agent
**Depends on:** Nothing (can run parallel with T4.1-T4.2)

**Goal:** Desktop equivalent of environment selection during provisioning.

**New files to create:**

| File | Purpose |
|------|---------|
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/CloudEnvironments.cs` | Static environment map matching Android's `CloudEnvironments.kt` |

**Files to modify:**

| File | Change |
|------|--------|
| `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/ProvisioningWindow.axaml` | Add environment combo box to provisioning form |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Registration/RegistrationManager.cs` | Store `Environment` in `RegistrationState`. Resolve base URL from map when set. |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/AgentConfiguration.cs` | Add `Environment` property |

**Acceptance criteria:**
- [ ] Environment combo box on provisioning screen
- [ ] Environment stored in `registration.json`
- [ ] Cloud base URL resolved from environment map

---

### T4.4 — Cloud + Portal: Environment Field in Bootstrap Token

**Platform:** Cloud Backend, Portal
**Depends on:** T4.1 (to understand the field name/values)

**Goal:** Include optional `environment` field in bootstrap token generation and agent detail views.

**Files to modify:**

| File | Change |
|------|--------|
| Cloud: `GenerateBootstrapTokenRequest` (find in Contracts) | Add optional `string? Environment` field |
| Cloud: `SiteConfigResponse` contracts | Add `environment` field to `SyncDto` |
| `src/portal/src/app/features/edge-agents/agent-detail.component.ts` | Display agent's environment |
| `src/portal/src/app/features/site-config/site-detail.component.ts` | Show environment in bootstrap token generation form |
| `src/portal/src/app/core/services/agent.service.ts` | Include environment in bootstrap token API call |

**Key references to read first:**
- `src/cloud/FccMiddleware.Contracts/Config/SiteConfigResponse.cs` — current contract
- `src/portal/src/app/features/edge-agents/agent-detail.component.ts` — agent detail UI
- `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs` — registration endpoint

**Acceptance criteria:**
- [ ] Bootstrap token can include environment
- [ ] Agent detail page shows environment
- [ ] Config response includes environment in sync section
- [ ] All changes are additive (existing agents without environment continue to work)

---

# PHASE 2.2 — Network Strategy (WiFi for FCC, Mobile Data for Cloud)

> **Why fourth:** Android-only platform work. Requires physical device testing. Independent of other phases.
> **Note:** Desktop agent does NOT need network binding (single NIC).

---

### T2.1 — Android: NetworkBinder Class

**Platform:** Android Edge-Agent
**Depends on:** Nothing

**Goal:** Create a class that uses Android `ConnectivityManager.requestNetwork()` to track WiFi and mobile data networks as reactive state flows.

**New files to create:**

| File | Purpose |
|------|---------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/connectivity/NetworkBinder.kt` | Manages `NetworkRequest` for WiFi (`TRANSPORT_WIFI`) and mobile (`TRANSPORT_CELLULAR` + `NET_CAPABILITY_INTERNET`). Exposes `wifiNetwork: StateFlow<Network?>`, `mobileNetwork: StateFlow<Network?>`, `cloudNetwork: StateFlow<Network?>` (mobile preferred, WiFi fallback). Methods: `start()` registers callbacks, `stop()` unregisters. |

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/di/AppModule.kt` | Register `NetworkBinder` singleton |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/service/EdgeAgentForegroundService.kt` | Call `networkBinder.start()` on service start, `networkBinder.stop()` on destroy |

**Key references to read first:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/connectivity/ConnectivityManager.kt` — existing probe logic (to understand what needs to change)
- Android docs: `ConnectivityManager.requestNetwork()`, `NetworkCallback`, `Network.bindSocket()`

**Pseudocode included in Phase2.md Section 4.3.**

**Acceptance criteria:**
- [ ] `wifiNetwork` emits non-null when WiFi is connected, null when lost
- [ ] `mobileNetwork` emits non-null when cellular with internet is available, null when lost
- [ ] `cloudNetwork` prefers mobile, falls back to WiFi
- [ ] Callbacks properly unregistered on `stop()`
- [ ] No crashes when network is unavailable

---

### T2.2 — Android: Bind FCC Traffic to WiFi, Cloud Traffic to Mobile

**Platform:** Android Edge-Agent
**Depends on:** T2.1

**Goal:** Force FCC TCP connections over WiFi and cloud HTTP calls over mobile data (with WiFi fallback).

**New files to create:**

| File | Purpose |
|------|---------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/connectivity/BoundSocketFactory.kt` | OkHttp `SocketFactory` implementation that calls `network.bindSocket(socket)` before returning. Used to bind cloud HTTP traffic to the mobile data network. |

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/jpl/JplTcpClient.kt` | Accept optional `Network?` parameter. Before `socket.connect()`, call `network?.bindSocket(socket)` to force FCC traffic over WiFi. Read `NetworkBinder.wifiNetwork.value` at connect time. |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt` | Use `BoundSocketFactory` with `NetworkBinder.cloudNetwork` as the bound network. When `cloudNetwork` changes, rebuild OkHttp client with new socket factory. |

**Key references to read first:**
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/jpl/JplTcpClient.kt` — TCP socket creation
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt` — OkHttp engine setup

**Acceptance criteria:**
- [ ] FCC TCP connections go over WiFi network when WiFi is available
- [ ] Cloud HTTP requests go over mobile data when available
- [ ] Cloud falls back to WiFi when mobile data is unavailable
- [ ] Both fall back to default routing when `NetworkBinder` has no network
- [ ] No regression in existing connectivity behavior

---

### T2.3 — Android: Enhanced Connectivity Probes

**Platform:** Android Edge-Agent
**Depends on:** T2.1

**Goal:** Update `ConnectivityManager.kt` to use network-bound probes — FCC probe over WiFi, internet probe over mobile/cloud network.

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/connectivity/ConnectivityManager.kt` | Inject `NetworkBinder`. FCC probe: bind to `wifiNetwork` before connecting. Internet probe: bind to `cloudNetwork`. Log which physical network each probe uses. |

**Acceptance criteria:**
- [ ] FCC probe runs over WiFi network
- [ ] Internet probe runs over cloud network (mobile preferred)
- [ ] Probe results correctly reflect per-network reachability
- [ ] No changes to `ConnectivityState` enum or existing consumers
- [ ] Graceful fallback when network binding is unavailable

---

# PHASE 2.1 — WebSocket Server for Odoo Backward Compatibility

> **Why last:** Highest complexity. Benefits from P2.5 (site data for pump/nozzle info), P2.3 (settings for WS port override). Requires Odoo integration testing.

---

### T1.1 — Android: WebSocket Message Models & Protocol Layer

**Platform:** Android Edge-Agent
**Depends on:** Nothing (can start early, but WebSocket server integration depends on T1.2)

**Goal:** Define all WebSocket message types as Kotlin data classes matching the legacy DOMSRealImplementation protocol exactly.

**New files to create:**

| File | Purpose |
|------|---------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/websocket/OdooWsModels.kt` | All message models: `WsInboundMessage` (parsed from `mode` field), `PumpTransactionWsDto` (matches legacy `PumpTransactions` snake_case JSON exactly: `id`, `transaction_id`, `pump_id`, `nozzle_id`, `attendant`, `product_id`, `qty`, `unit_price`, `total`, `state`, `start_time`, `end_time`, `order_uuid`, `sync_status`, `odoo_order_id`, `add_to_cart`, `payment_id`), `FuelPumpStatusWsDto` (matches legacy `FuelPumpStatusDto`: `pump_number`, `nozzle_number`, `status`, `reading`, `volume`, `litre`, `amount`, `attendant`, `count`, `FpGradeOptionNo`, `unit_price`, `isOnline`), `WsErrorResponse`, `WsAttendantPumpCountAck`. |

**Key references to read first:**
- `DOMSRealImplementation/DppMiddleWareService/Models/PumpTransactions.cs` — exact field names and JSON annotations
- `DOMSRealImplementation/DppMiddleWareService/Models/FpStatusResponse.cs` — `FuelPumpStatusDto` fields
- `DOMSRealImplementation/DppMiddleWareService/WebSocketServerHostedService.cs` — FleckWebSocketAdapter message format
- Phase2.md Section 3.2 — complete protocol contract

**Acceptance criteria:**
- [x] All DTOs match legacy JSON field names exactly (including mixed-case like `FpGradeOptionNo`, `isOnline`)
- [x] `@SerialName` annotations used where Kotlin property names differ from JSON keys
- [x] Serialization produces JSON identical to legacy output
- [x] Unit tests verify serialization round-trip for each DTO

---

### T1.2 — Android: WebSocket Server Core (Ktor)

**Platform:** Android Edge-Agent
**Depends on:** T1.1

**Goal:** Create the Ktor WebSocket server that listens on port 8443 (configurable), tracks connected clients, and handles the message routing switch statement.

**New files to create:**

| File | Purpose |
|------|---------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/websocket/OdooWebSocketServer.kt` | Ktor CIO server on configurable port (default 8443). WebSocket route at `/`. On connect: add to `ConcurrentHashMap<WebSocketSession, Job>`, start per-connection pump status timer. On message: parse JSON, switch on `mode` field, dispatch to handler. On disconnect: cancel timer, remove from map. `broadcastToAll(type, data)` sends to all connected sessions with snake_case key conversion. `start()`, `stop()` lifecycle methods. |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/websocket/OdooWsMessageHandler.kt` | Handler functions for each `mode`: `handleLatest()`, `handleAll()`, `handleManagerUpdate()`, `handleAttendantUpdate()`, `handleFuelPumpStatus()`, `handleFpUnblock()`, `handleAttendantPumpCountUpdate()`, `handleManagerManualUpdate()`, `handleAddTransaction()`. Each reads from/writes to the transaction buffer DAO and calls FCC adapter methods as needed. |

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigDto.kt` | Add `WebSocketDto` section: `enabled`, `port`, `useTls`, `bindAddress`, `maxConnections`, `pumpStatusBroadcastIntervalSeconds`, `requireApiKeyForLan`. Add `toWebSocketConfig()` converter. |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/di/AppModule.kt` | Register `OdooWebSocketServer` and `OdooWsMessageHandler` as singletons |

**Key references to read first:**
- `DOMSRealImplementation/DppMiddleWareService/WebSocketServerHostedService.cs` — FleckWebSocketAdapter switch statement (the source of truth for message handling)
- `DOMSRealImplementation/DppMiddleWareService/Services/TransactionService.cs` — `HandleWebSocketRequest()`, `HandleAttendantSocket()`, `HandleSiteManagerSocket()`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/api/LocalApiServer.kt` — Ktor server lifecycle pattern to follow
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/dao/TransactionBufferDao.kt` — how to query transactions

**Acceptance criteria:**
- [x] Server starts on configurable port (default 8443)
- [x] Multiple WebSocket clients can connect simultaneously
- [x] Each `mode` command dispatched to correct handler
- [x] Unknown mode returns `{ status: "error", message: "Unknown mode '...'" }`
- [x] Connection never forcibly closed on error
- [x] Per-connection pump status timer fires every 3 seconds
- [x] Timer cancelled on disconnect
- [x] `broadcastToAll()` sends to all connected clients

---

### T1.3 — Android: Transaction Query & Mutation Handlers

**Platform:** Android Edge-Agent
**Depends on:** T1.2

**Goal:** Implement the transaction-related WebSocket handlers that read/write the local buffer.

**What to implement in `OdooWsMessageHandler.kt`:**

| Handler | Logic |
|---------|-------|
| `handleLatest(session, data)` | Query `TransactionBufferDao` for unsynced transactions matching optional `pump_id`, `nozzle_id`, `emp`, `CreatedDate` filters. Map to `PumpTransactionWsDto[]`. Send `{ type: "latest", data: [...] }`. |
| `handleAll(session)` | Query all transactions from buffer. Send `{ type: "all_transactions", data: [...] }`. |
| `handleManagerUpdate(session, data)` | Extract `transaction_id` and `update` fields. Update transaction in buffer (state, order_uuid, order_id, payment_id, sync_status, add_to_cart). Broadcast `{ type: "transaction_update", data: {updated tx} }` to ALL clients. |
| `handleAttendantUpdate(session, data)` | Extract `transaction_id` and `update` with `order_uuid`, `order_id`, `state`, `add_to_cart`, `payment_id`. Update in buffer. If `add_to_cart` changed, broadcast update. If `order_uuid` set, broadcast update. |
| `handleAddTransaction(session, data)` | Insert new transaction into buffer. |
| `handleManagerManualUpdate(session, data)` | Mark transaction as discarded in buffer. |

**Key references to read first:**
- `DOMSRealImplementation/DppMiddleWareService/WebSocketServerHostedService.cs` lines 311-555 — FleckWebSocketAdapter.OnMessage switch body (exact logic to replicate)
- `DOMSRealImplementation/DppMiddleWareService/Services/TransactionService.cs` — `HandleWebSocketRequest()`, `UpdateOrderUuidAsync()`, `UpdateAddToCartAsync()`, `UpdateIsDiscard()`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/dao/TransactionBufferDao.kt` — available query methods

**Acceptance criteria:**
- [x] `latest` mode returns filtered unsynced transactions in correct DTO format
- [x] `all` mode returns all transactions
- [x] `manager_update` updates DB and broadcasts to all clients
- [x] `attendant_update` with `add_to_cart` broadcasts update
- [x] `attendant_update` with `order_uuid` broadcasts update
- [x] Broadcast uses ALL connected sessions (not just sender)
- [x] Empty result returns `{ type: "latest", data: null }` (matching legacy behavior)

---

### T1.4 — Android: Pump Status & FCC Command Handlers

**Platform:** Android Edge-Agent
**Depends on:** T1.2

**Goal:** Implement the FCC-interacting WebSocket handlers (pump status broadcast, pump unblock, attendant pump counts).

**What to implement in `OdooWsMessageHandler.kt`:**

| Handler | Logic |
|---------|-------|
| `handleFuelPumpStatus(session)` | Call `IFccAdapter.getPumpStatus()`, map result to `FuelPumpStatusWsDto[]`, send each item individually to the requesting session. |
| Per-connection pump status timer | In `OdooWebSocketServer`, on each timer tick (every 3s): call `getPumpStatus()`, send each `FuelPumpStatusWsDto` to that specific session (NOT broadcast to all). |
| `handleFpUnblock(session, data)` | Extract `fp_id`. Forward to FCC adapter as a release command. |
| `handleAttendantPumpCountUpdate(session, data)` | Parse `data` array of `{ PumpNumber, EmpTagNo, NewMaxTransaction }`. For each item, update count and send per-item ack: `{ type: "attendant_pump_count_update_ack", data: { pump_number, emp_tag_no, max_limit, status: "updated" } }`. |

**Key references to read first:**
- `DOMSRealImplementation/DppMiddleWareService/WebSocketServerHostedService.cs` — `BroadcastFuelPumpStatusUpdateAsync()` (lines 582-613), `fp_unblock` handler, `attendant_pump_count_update` handler
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/IFccAdapter.kt` — `getPumpStatus()` interface

**Acceptance criteria:**
- [x] Pump status broadcast fires every 3 seconds per connection
- [x] Each `FuelPumpStatusDto` sent individually (not wrapped in array)
- [x] Timer cancelled on client disconnect
- [x] `fp_unblock` sends release command to FCC adapter
- [x] `attendant_pump_count_update` sends per-item ack back to sender

---

### T1.5 — Android: WebSocket Server Lifecycle Integration

**Platform:** Android Edge-Agent
**Depends on:** T1.2

**Goal:** Wire the WebSocket server into the foreground service lifecycle and cadence controller.

**Files to modify:**

| File | Change |
|------|--------|
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/service/EdgeAgentForegroundService.kt` | Create and start `OdooWebSocketServer` alongside `LocalApiServer`. Wire FCC adapter via `server.wireFccAdapter()`. Stop on service destroy. Start only when `config.websocket.enabled == true`. |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt` | When FCC adapter connects/disconnects, notify `OdooWebSocketServer` so it can update pump status timers. |
| `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/ConfigManager.kt` | Validate WebSocket config fields (port range, etc.). Detect WebSocket config changes as restart-required. |

**Acceptance criteria:**
- [x] WebSocket server starts with foreground service when `websocket.enabled` is true
- [x] WebSocket server does NOT start when `websocket.enabled` is false
- [x] Server stops cleanly on service destroy
- [x] FCC adapter wired into WebSocket server for pump status queries
- [x] Config change (e.g., port change) triggers server restart

---

### T1.6 — Desktop: WebSocket Server (Kestrel)

**Platform:** Desktop Edge-Agent
**Depends on:** T1.1 conceptually (shared protocol understanding, not code dependency)

**Goal:** Implement the same WebSocket protocol on the desktop agent using ASP.NET Core Kestrel WebSocket middleware.

**New files to create:**

| File | Purpose |
|------|---------|
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/WebSocket/OdooWebSocketServer.cs` | Kestrel WebSocket middleware: accepts upgrades at `/`, tracks clients in `ConcurrentDictionary<WebSocket, CancellationTokenSource>`, per-connection pump status timer, `mode`-based message routing switch. |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/WebSocket/OdooWsBridge.cs` | Message handler methods matching Android's `OdooWsMessageHandler` |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/WebSocket/OdooWsModels.cs` | C# DTOs: `PumpTransactionWsDto`, `FuelPumpStatusWsDto`, `WsErrorResponse` — JSON serialization matching legacy format exactly |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/WebSocket/OdooWsMessageHandler.cs` | Handler dispatch for all `mode` values |

**Files to modify:**

| File | Change |
|------|--------|
| `src/desktop-edge-agent/src/FccDesktopAgent.Api/Program.cs` | Configure Kestrel to listen on WebSocket port (default 8443). Register WebSocket middleware. |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/AgentConfiguration.cs` | Add `WebSocket` config section |
| `src/desktop-edge-agent/src/FccDesktopAgent.Core/Runtime/ServiceCollectionExtensions.cs` | Register WebSocket services |

**Key references to read first:**
- `DOMSRealImplementation/DppMiddleWareService/WebSocketServerHostedService.cs` — the source of truth (this IS a .NET project, so patterns can be more directly ported)
- `src/desktop-edge-agent/src/FccDesktopAgent.Api/Endpoints/PumpStatusEndpoints.cs` — existing pump status query pattern
- `src/desktop-edge-agent/src/FccDesktopAgent.Api/Endpoints/StatusEndpoints.cs` — existing endpoint pattern

**Acceptance criteria:**
- [x] WebSocket server listens on configurable port (default 8443)
- [x] All 9 `mode` commands handled identically to Android
- [x] Per-connection 3-second pump status broadcast
- [x] Transaction mutations broadcast to all clients
- [x] JSON format matches legacy DOMSRealImplementation exactly
- [x] TLS support via PFX certificate (same as legacy)

---

# CROSS-CUTTING TASKS

---

### TX.1 — Portal: Agent Detail Enhancements

**Platform:** Portal (Angular)
**Depends on:** T5.4, T4.4

**Goal:** Display site data counts and environment in the portal agent detail view.

**Files to modify:**

| File | Change |
|------|--------|
| `src/portal/src/app/features/edge-agents/agent-detail.component.ts` | Show: environment, product count, pump count, nozzle count (from telemetry or config). |
| `src/portal/src/app/features/site-config/site-detail.component.ts` | Show pump/nozzle/product counts; environment field in bootstrap token form. |

**Acceptance criteria:**
- [ ] Agent detail shows environment badge
- [ ] Site detail shows equipment counts

---

### TX.2 — Cloud: GlobalExceptionHandler + Resilience (if not already done)

**Platform:** Cloud Backend
**Depends on:** Nothing

**Goal:** Ensure cloud API returns consistent error responses for all Phase 2 endpoints.

**Note:** Check if `src/cloud/FccMiddleware.Api/Infrastructure/GlobalExceptionHandlerMiddleware.cs` already covers this. If already implemented, skip this task.

---

## Summary Table

| Task | Platform | Depends On | Effort | Can Parallel With |
|------|----------|-----------|--------|-------------------|
| **T5.1** | Android | — | S | T5.4, T3.1, T4.1, T2.1 |
| **T5.2** | Android | T5.1 | M | T3.2, T4.2 |
| **T5.3** | Android | T5.2 | S | T3.3 |
| **T5.4** | Desktop | — | S | T5.1, T3.4, T4.3 |
| **T3.1** | Android | — | S | T5.1, T4.1, T2.1 |
| **T3.2** | Android | T3.1 | M | T5.2, T4.2 |
| **T3.3** | Android | T3.1 | S | T3.2 |
| **T3.4** | Desktop | — | M | T3.1, T5.4, T4.3 |
| **T3.5** | Both | T3.2, T3.4 | S | — |
| **T4.1** | Android | — | S | T5.1, T3.1, T2.1 |
| **T4.2** | Android | T4.1 | S | T3.2, T5.2 |
| **T4.3** | Desktop | — | S | T4.1, T3.4, T5.4 |
| **T4.4** | Cloud+Portal | T4.1 | M | T1.1 |
| **T2.1** | Android | — | M | T5.1, T3.1, T4.1 |
| **T2.2** | Android | T2.1 | M | T2.3 |
| **T2.3** | Android | T2.1 | S | T2.2 |
| **T1.1** | Android | — | M | T2.1, T5.1 |
| **T1.2** | Android | T1.1 | L | — |
| **T1.3** | Android | T1.2 | L | T1.4 |
| **T1.4** | Android | T1.2 | M | T1.3 |
| **T1.5** | Android | T1.2 | M | T1.3, T1.4 |
| **T1.6** | Desktop | — | L | T1.2 |
| **TX.1** | Portal | T5.4, T4.4 | S | — |

**Effort key:** S = Small (< 1 day), M = Medium (1-2 days), L = Large (3+ days)

---

## Recommended Execution Waves

### Wave 1 (Week 1-2) — Foundation, max parallelism
Run simultaneously: **T5.1, T5.4, T3.1, T3.4, T4.1, T4.3, T2.1, T1.1**
- 8 independent tasks across Android, Desktop, all platforms
- Establishes: Room entities, override manager, environment map, network binder, WS models

### Wave 2 (Week 3-4) — Integration
Run simultaneously: **T5.2, T3.2, T3.3, T4.2, T2.2, T2.3**
- Wires foundation into registration, settings UI, network binding
- Depends on Wave 1 completions

### Wave 3 (Week 5-6) — WebSocket core + finishing touches
Run simultaneously: **T1.2, T1.6, T5.3, T3.5, T4.4**
- WebSocket server core (Android + Desktop in parallel)
- Portal/cloud environment field
- Diagnostics enhancements

### Wave 4 (Week 7-8) — WebSocket handlers + integration
Run simultaneously: **T1.3, T1.4, T1.5, TX.1**
- Transaction handlers, pump status handlers, lifecycle wiring
- Portal enhancements

### Wave 5 (Week 9-10) — Integration testing
- End-to-end WebSocket testing with Odoo POS
- Physical device testing for network binding (T2.x)
- Regression testing for existing REST API
