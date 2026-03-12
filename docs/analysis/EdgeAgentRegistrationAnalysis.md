# Edge Agent Registration Features -- Deep Analysis Report

**Date:** 2026-03-12
**Scope:** Android Edge Agent, Desktop Edge Agent, Cloud Middleware, Portal
**Focus:** Registration, Provisioning, Configuration, Authentication

---

## Table of Contents

1. [End-to-End Functional Flow](#1-end-to-end-functional-flow)
2. [Data Setup Requirements](#2-data-setup-requirements)
3. [Bug Report: Critical Findings](#3-bug-report-critical-findings)
4. [Bug Report: High Severity](#4-bug-report-high-severity)
5. [Bug Report: Medium Severity](#5-bug-report-medium-severity)
6. [Bug Report: Low Severity](#6-bug-report-low-severity)
7. [Functional Incompleteness](#7-functional-incompleteness)
8. [Cross-Platform Inconsistencies](#8-cross-platform-inconsistencies)
9. [Improvement Suggestions](#9-improvement-suggestions)

---

## 1. End-to-End Functional Flow

### 1.1 Bootstrap Token Generation (Portal Admin -> Cloud)

| Step | Actor | Action | System |
|------|-------|--------|--------|
| 1 | Portal Admin | Navigates to site config, clicks "Generate Bootstrap Token" | Portal (Angular) |
| 2 | Portal | `POST /api/v1/admin/bootstrap-tokens` with `{siteCode, legalEntityId}` | Cloud API |
| 3 | Cloud | `GenerateBootstrapTokenHandler` generates 32-byte random token, stores SHA-256 hash, 72h expiry | Cloud DB |
| 4 | Cloud | Returns `{tokenId, rawToken, expiresAt}` to portal | Portal |
| 5 | Portal Admin | **MISSING:** No UI exists to generate QR code from the token for Android provisioning | -- |

**Key Files:**
- Cloud: `FccMiddleware.Api/Controllers/AgentController.cs` (lines 57-93)
- Cloud: `FccMiddleware.Application/Registration/GenerateBootstrapTokenHandler.cs`
- Portal: **No bootstrap token generation UI exists** (gap)

### 1.2 Android Agent Provisioning Flow

```
[LauncherActivity] ──check state──► [ProvisioningActivity] ──scan QR──► [Cloud Register]
       │                                     │                              │
  isRegistered?                        Parse QR JSON:                 POST /api/v1/agent/register
  isDecommissioned?                    { v, sc, cu, pt }              Body: {provisioningToken, siteCode, ...}
       │                                     │                              │
  Yes──► [DiagnosticsActivity]          Validate HTTPS               Receive: {deviceToken, refreshToken,
  Decom─► [DecommissionedActivity]      Validate fields                       siteConfig, deviceId}
                                             │                              │
                                        Register device              Store tokens in Keystore
                                             │                       Store identity in EncryptedPrefs
                                        Start service                Store config in Room DB
                                             │                              │
                                        [EdgeAgentForegroundService] ──► [CadenceController]
```

**QR Code Payload Format:**
```json
{
  "v": 1,
  "sc": "SITE-001",
  "cu": "https://api.fccmiddleware.com",
  "pt": "<provisioning-token>"
}
```

**Key Files:**
- `edge-agent/.../ui/LauncherActivity.kt` -- Entry routing (3 states)
- `edge-agent/.../ui/ProvisioningActivity.kt` -- QR scan + registration
- `edge-agent/.../sync/CloudApiClient.kt` -- HTTP POST registration
- `edge-agent/.../sync/CloudApiModels.kt` -- Request/response DTOs
- `edge-agent/.../security/KeystoreManager.kt` -- AES-256-GCM token encryption
- `edge-agent/.../security/EncryptedPrefsManager.kt` -- Identity persistence
- `edge-agent/.../config/ConfigManager.kt` -- Config storage
- `edge-agent/.../service/EdgeAgentForegroundService.kt` -- Always-on service

### 1.3 Desktop Agent Provisioning Flow

```
[Program.cs] ──check state──► [ProvisioningWindow] ──4-step wizard──► [Cloud Register]
       │                            │                                      │
  LoadState()                  Step 1: Choose method                  POST /api/v1/agent/register
  IsRegistered?                  Code-based / Manual                  Body: {provisioningToken, siteCode, ...}
  IsDecommissioned?            Step 2: Enter details                       │
       │                         Cloud URL, Site Code,               Receive: {deviceToken, refreshToken,
  Registered──► Start Host         Token or FCC details                      siteConfig, deviceId}
  Decom──► DecommissionedWindow Step 3: Connection tests                    │
                                Step 4: Success summary              Store tokens in PlatformCredentialStore
                                  Show API key                       Store state in registration.json
                                  Launch agent                       Store config in SQLite
```

**Key Files:**
- `desktop-edge-agent/FccDesktopAgent.App/Program.cs` -- Entry + registration gate
- `desktop-edge-agent/FccDesktopAgent.App/Views/ProvisioningWindow.axaml.cs` -- 4-step wizard
- `desktop-edge-agent/FccDesktopAgent.Core/Registration/DeviceRegistrationService.cs` -- Registration HTTP
- `desktop-edge-agent/FccDesktopAgent.Core/Registration/RegistrationManager.cs` -- State persistence
- `desktop-edge-agent/FccDesktopAgent.Core/Security/PlatformCredentialStore.cs` -- Multi-platform secrets

### 1.4 Cloud Registration Handler Flow

```
POST /api/v1/agent/register
       │
  Extract X-Provisioning-Token header ◄── BUG: Agents send token in body, not header
       │
  Hash token (SHA-256) → lookup in bootstrap_tokens table
       │
  Validate: status=ACTIVE, not expired, not revoked
       │
  Lookup site by siteCode → validate exists
       │
  Check for existing active agent on site
    ├── exists + replacePreviousAgent=false → 409 ACTIVE_AGENT_EXISTS
    └── exists + replacePreviousAgent=true → deactivate old agent
       │
  Create AgentRegistration record
  Consume bootstrap token (status → USED)
  Generate device JWT (HMAC-SHA256, 24h)
  Generate refresh token (32-byte random, 90-day, stored as SHA-256 hash)
       │
  Assemble SiteConfigResponse from site + FCC config + pumps + nozzles
       │
  Return 201: {deviceId, deviceToken, refreshToken, siteConfig}
```

**Key Files:**
- `cloud/FccMiddleware.Api/Controllers/AgentController.cs` -- HTTP endpoint
- `cloud/FccMiddleware.Application/Registration/RegisterDeviceHandler.cs` -- Business logic
- `cloud/FccMiddleware.Api/Auth/DeviceTokenService.cs` -- JWT generation
- `cloud/FccMiddleware.Domain/Entities/AgentRegistration.cs` -- DB entity
- `cloud/FccMiddleware.Domain/Entities/BootstrapToken.cs` -- Token entity

### 1.5 Post-Registration: Config Polling

```
[Edge Agent] ──every ~3 min──► GET /api/v1/agent/config
                                    │
                              If-None-Match: "<configVersion>"
                                    │
                              304 Not Modified → skip
                              200 OK → parse SiteConfigResponse
                                    │
                              Validate schema version (major=2)
                              Validate config version > current
                              Check immutable fields unchanged
                              Detect restart-required changes
                                    │
                              Persist to local DB (encrypted on Android)
                              Apply hot-reload fields
```

### 1.6 Post-Registration: Token Refresh

```
[Edge Agent] ──on 401──► POST /api/v1/agent/token/refresh
                              │
                         Body: {refreshToken}
                              │
                         Cloud: lookup SHA-256(token) → validate not revoked, not expired
                              │
                         Revoke old refresh token
                         Generate new device JWT + new refresh token
                              │
                         Return: {deviceToken, refreshToken, tokenExpiresAt}
                              │
                         Agent: store both new tokens, retry original request
```

### 1.7 Decommissioning Flow

```
[Portal Admin] ──► POST /api/v1/admin/agent/{deviceId}/decommission
                         │
                    Deactivate AgentRegistration (is_active=false, deactivated_at=now)
                    Revoke all DeviceRefreshTokens for device
                         │
                    Next agent API call returns 403 DEVICE_DECOMMISSIONED
                         │
                    Agent: marks self decommissioned
                      - Android: volatile flag + EncryptedPrefs commit
                      - Desktop: RegistrationState.IsDecommissioned + registration.json
                         │
                    Next launch: routes to DecommissionedActivity/DecommissionedWindow
                    BootReceiver skips service start
```

---

## 2. Data Setup Requirements

### 2.1 Cloud Prerequisites

| Entity | Source | Required Before Registration |
|--------|--------|------------------------------|
| **Legal Entity** | Synced from Odoo via Databricks | Yes -- bootstrap token references it |
| **Site** | Synced from Odoo via Databricks | Yes -- registration validates site exists |
| **FCC Config** | Portal Admin via `PUT /api/v1/sites/{id}/fcc-config` | Yes -- included in SiteConfig response |
| **Pump/Nozzle Mappings** | Portal Admin via `POST /api/v1/sites/{id}/pumps` | Yes -- included in SiteConfig response |
| **Products** | Synced from Odoo via Databricks | Yes -- nozzle mappings reference products |
| **DeviceJwt:SigningKey** | App configuration (appsettings / env var) | Yes -- used to sign device JWTs |
| **Bootstrap Token** | Portal Admin via `POST /api/v1/admin/bootstrap-tokens` | Yes -- consumed during registration |

### 2.2 Device Prerequisites

| Item | Android | Desktop |
|------|---------|---------|
| **Network** | WiFi LAN to FCC + Internet (SIM/WiFi) | LAN to FCC + Internet |
| **Camera** | Required for QR scanning | N/A (text entry) |
| **Storage** | Internal storage for SQLite | Disk for SQLite + credential files |
| **OS** | Android 12+ (API 31) | Windows / macOS / Linux |
| **Permissions** | INTERNET, CAMERA, FOREGROUND_SERVICE, BOOT_COMPLETED | Filesystem access, network access |

### 2.3 Configuration Delivered at Registration

The `SiteConfigResponse` returned by the cloud includes:

```
Identity:     deviceId, siteCode, legalEntityId
Site:         operatingModel, connectivityMode, timezone, currency
FCC:          enabled, vendor, host, port, protocol, transactionMode, ingestionMode,
              pullIntervalSeconds, heartbeatIntervalSeconds, heartbeatTimeoutSeconds,
              [vendor-specific: sharedSecret, usnCode, authPort, jplPort, fcAccessCode, ...]
Sync:         cloudBaseUrl, uploadBatchSize, uploadIntervalSeconds, configPollIntervalSeconds
Buffer:       retentionDays, maxRecords, cleanupIntervalHours
LocalApi:     port, enableLanApi, rateLimitPerMinute
Telemetry:    intervalSeconds, logLevel
Fiscalization: mode, taxAuthorityEndpoint, requireCustomerTaxId, fiscalReceiptRequired
Mappings:     pumps[{pumpNumber, fccPumpNumber, nozzles[{nozzleNumber, fccNozzleNumber, productCode}]}]
Rollout:      minAgentVersion, maxAgentVersion, configTtlHours, requiresRestartSections
```

---

## 3. Bug Report: Critical Findings

### BUG-001: Registration is Broken -- Token Location Mismatch

| Field | Value |
|-------|-------|
| **Severity** | CRITICAL -- Registration cannot succeed |
| **Components** | Android Agent, Desktop Agent, Cloud |
| **Impact** | No edge agent can register. Complete feature blocker. |

**Finding:** The cloud `AgentController.cs` (line 104) extracts the bootstrap token from the `X-Provisioning-Token` HTTP header. Both the Android agent (`CloudApiModels.kt` line 286) and the Desktop agent (`RegistrationModels.cs` line 12-13) send the `provisioningToken` inside the JSON request body. The cloud's `DeviceRegistrationApiRequest.cs` does NOT define a `ProvisioningToken` property, so the JSON field is silently ignored by ASP.NET deserialization.

**Result:** Every registration attempt returns 401 with error code `BOOTSTRAP_TOKEN_MISSING`.

**Recommended Fix:** Either:
- (A) Change the cloud controller to read the token from the JSON body (add `ProvisioningToken` to `DeviceRegistrationApiRequest`), OR
- (B) Change both agents to send the token as an `X-Provisioning-Token` header instead of in the body.

Option (A) is simpler and aligns with the agent implementations.

---

### BUG-002: Android Agent -- CloudApiClient Uses Stale URL After Registration

| Field | Value |
|-------|-------|
| **Severity** | CRITICAL -- All post-registration cloud calls fail |
| **Components** | Android Agent |
| **Impact** | Config polling, transaction upload, telemetry, status sync all fail until app restart |

**Finding:** The `CloudApiClient` singleton is created at DI initialization time (AppModule.kt) with `cloudBaseUrl = "https://not-yet-provisioned"`. After registration, the correct URL is saved in EncryptedPrefs but the singleton is never recreated. All subsequent API calls (upload, config poll, telemetry, token refresh) use `"https://not-yet-provisioned"` as the base URL.

**Files:** `CloudApiClient.kt` line 199 (constructor URL), `ProvisioningActivity.kt` line 299 (starts service with stale DI)

**Recommended Fix:** After registration, either:
- Recreate the Koin module with the new URL, or
- Make `cloudBaseUrl` a mutable property on `HttpCloudApiClient` that is updated after registration, or
- Restart the process after registration completes.

---

### BUG-003: Android Agent -- Connectivity Probes Are Hardcoded Stubs

| Field | Value |
|-------|-------|
| **Severity** | CRITICAL -- Agent is permanently non-functional |
| **Components** | Android Agent |
| **Impact** | Agent permanently believes it is FULLY_OFFLINE. No FCC polling, no cloud upload, no config poll, no telemetry. |

**Finding:** In `AppModule.kt`, both `internetProbe` and `fccProbe` lambda functions return `false`. The `ConnectivityManager` therefore permanently reports `FULLY_OFFLINE`. The `CadenceController` does nothing in `FULLY_OFFLINE` state.

**Result:** The entire agent is non-functional after registration. No data flows.

**Recommended Fix:** Implement the connectivity probes:
- Internet probe: HTTP GET to cloud `/health` endpoint
- FCC probe: Call `fccAdapter.heartbeatAsync()` over LAN

---

### BUG-004: Android Agent -- FCC Adapter is Never Wired in Production DI

| Field | Value |
|-------|-------|
| **Severity** | CRITICAL -- FCC communication impossible |
| **Components** | Android Agent |
| **Impact** | No FCC polling, no pre-auth, no heartbeat, no transaction ingestion |

**Finding:** In `AppModule.kt`, all components that accept an `fccAdapter` parameter receive `null`. The `FccAdapterFactory` exists and can create adapters, but nothing calls `factory.create(vendor, config)` during the startup flow. The `CadenceController` has no adapter to poll.

**Recommended Fix:** After config is loaded, resolve the FCC vendor and connection details from `SiteConfig.Fcc`, call `FccAdapterFactory.create()`, and inject the resulting adapter into the cadence controller and connectivity manager.

---

### BUG-005: Android Agent -- Config Never Loaded from Local DB on Startup

| Field | Value |
|-------|-------|
| **Severity** | CRITICAL -- Config is null until first cloud poll succeeds |
| **Components** | Android Agent |
| **Impact** | Combined with BUG-003, creates a startup deadlock: needs internet to get config, but probes say offline so cloud never polled. |

**Finding:** `ConfigManager.loadFromLocal()` exists but is never called from any production code path. After app restart, the config `StateFlow` remains `null` until the first successful config poll. Since the connectivity probes are stubs (BUG-003), config poll never runs, and config stays null forever.

**Files:** `ConfigManager.kt` (loadFromLocal never called), `EdgeAgentForegroundService.kt` (no load call in onStartCommand)

**Recommended Fix:** Call `configManager.loadFromLocal()` in `EdgeAgentForegroundService.onStartCommand()` before starting the cadence controller.

---

### BUG-006: Android Agent -- SiteConfig Field Name Mismatch with Cloud Response

| Field | Value |
|-------|-------|
| **Severity** | HIGH -- FCC connection info not extracted at registration |
| **Components** | Android Agent |
| **Impact** | FCC host/port not stored during provisioning; must wait for first config poll |

**Finding:** `ProvisioningActivity.kt` (lines 267-275) attempts to access `config["fccConnection"]` from the registration response's `siteConfig`. However, the cloud's `SiteConfigResponse` serializes the FCC section as `"fcc"` (property name `Fcc`), not `"fccConnection"`. The field access silently returns null, and FCC connection info is not stored in EncryptedPrefs.

**Recommended Fix:** Change the JSON key access from `config["fccConnection"]` to `config["fcc"]`, and update sub-field names to match the cloud's `SiteConfigFccDto` property names.

---

## 4. Bug Report: High Severity

### BUG-007: Cloud -- Race Condition on Bootstrap Token Consumption

| Field | Value |
|-------|-------|
| **Severity** | HIGH -- Security, single-use token can be double-used |
| **Components** | Cloud |
| **Impact** | Two concurrent registration requests with the same token can both succeed, creating duplicate agents |

**Finding:** `RegisterDeviceHandler.cs` checks `bootstrapToken.Status == USED` (line 44) without any database-level locking. Two concurrent requests can both read the token as `ACTIVE`, both proceed to register, and both consume the token. No `SELECT FOR UPDATE`, no optimistic concurrency token, and no unique constraint prevents this.

**Recommended Fix:** Add `SET STATUS = 'USED' WHERE STATUS = 'ACTIVE' RETURNING *` atomic update, or use optimistic concurrency with a `RowVersion` column.

---

### BUG-008: Cloud -- Race Condition on Concurrent Token Refresh

| Field | Value |
|-------|-------|
| **Severity** | HIGH -- Security, defeats token rotation |
| **Components** | Cloud |
| **Impact** | Two valid refresh tokens can exist simultaneously; leaked token may not be detected |

**Finding:** `RefreshDeviceTokenHandler.cs` has no concurrency control. Two concurrent refresh requests with the same token can both succeed because neither has committed the revocation when the other reads the token. This creates two valid device JWTs and two valid refresh tokens, defeating the security purpose of token rotation.

**Recommended Fix:** Use `UPDATE ... WHERE revoked_at IS NULL RETURNING *` for atomic revocation, or use optimistic concurrency.

---

### BUG-009: Desktop Agent -- Token Refresh Has No Concurrency Guard

| Field | Value |
|-------|-------|
| **Severity** | HIGH -- Token rotation can be defeated |
| **Components** | Desktop Agent |
| **Impact** | If `ConfigPollWorker` and `CloudUploadWorker` both get 401 simultaneously, both attempt refresh with the same token; one fails, leaving the agent unable to authenticate |

**Finding:** `DeviceTokenProvider.cs` (lines 54-123) has no mutex or semaphore. The Android agent uses a `Mutex` around its refresh logic, but the Desktop agent does not. Concurrent 401 responses from two workers trigger two refresh attempts; the second will fail because the token was already rotated.

**Recommended Fix:** Add a `SemaphoreSlim(1,1)` around the refresh logic, matching the Android agent's pattern.

---

### BUG-010: Cloud -- No Refresh Token Reuse Detection

| Field | Value |
|-------|-------|
| **Severity** | HIGH -- Security gap |
| **Components** | Cloud |
| **Impact** | If a refresh token is stolen and the legitimate device rotates it, the attacker's subsequent attempt with the old token is silently rejected without alerting or revoking all tokens for the device |

**Finding:** Per RFC 6819 Section 5.2.2.3, when a revoked refresh token is presented, the authorization server should revoke ALL active tokens for that client (indicating potential compromise). `RefreshDeviceTokenHandler.cs` simply returns "expired or revoked" without triggering any security response.

**Recommended Fix:** When a revoked (but not expired) refresh token is presented, revoke all tokens for that device and create a security alert audit event.

---

### BUG-011: Cloud -- Decommissioned Devices Retain Valid JWTs for 24 Hours

| Field | Value |
|-------|-------|
| **Severity** | HIGH -- Security gap |
| **Components** | Cloud |
| **Impact** | A decommissioned device can continue uploading transactions and telemetry for up to 24 hours |

**Finding:** When a device is decommissioned, only the refresh tokens are revoked and `is_active` is set to false. The existing device JWT remains valid until expiry (24 hours). There is no JWT blacklist mechanism. The device can continue making authenticated API calls until the JWT expires.

**Recommended Fix:** Either:
- Add a decommission check in the JWT validation pipeline (check `is_active` on each request), or
- Reduce JWT lifetime to 1 hour to minimize the window, or
- Implement a JWT blacklist for decommissioned devices.

---

### BUG-012: Android Agent -- Build.getSerial() Returns "unknown" on Modern Android

| Field | Value |
|-------|-------|
| **Severity** | HIGH -- Device identity is not unique |
| **Components** | Android Agent |
| **Impact** | All devices register with `deviceSerialNumber = "unknown"`, making device identification impossible |

**Finding:** `ProvisioningActivity.kt` (lines 228-232) uses `Build.getSerial()` which requires `READ_PRIVILEGED_PHONE_STATE` on API 29+. This permission is only available to system/platform apps. On user-installed apps, it returns `"unknown"`. The `READ_PHONE_STATE` permission is not declared in AndroidManifest.xml either.

**Recommended Fix:** Use `Settings.Secure.getString(contentResolver, Settings.Secure.ANDROID_ID)` which returns a unique 64-bit hex string per app/device combination without special permissions.

---

### BUG-013: Desktop Agent -- Manual Config Path Never Persists Registration State

| Field | Value |
|-------|-------|
| **Severity** | HIGH -- Functional failure on restart |
| **Components** | Desktop Agent |
| **Impact** | Agent provisioned via manual config loses registration on restart; shows provisioning wizard again |

**Finding:** `ProvisioningWindow.axaml.cs` (lines 244-287): The manual configuration path sets local fields but never calls `DeviceRegistrationService.RegisterAsync()` or `RegistrationManager.SaveStateAsync()`. After connection tests pass and the agent launches, no `registration.json` is written. On next startup, `LoadState().IsRegistered` is `false` and the provisioning wizard reappears.

**Recommended Fix:** The manual config path must persist registration state after successful connection tests.

---

### BUG-014: Desktop Agent -- FCC Adapter Resolution Returns Null

| Field | Value |
|-------|-------|
| **Severity** | HIGH -- FCC probe always fails |
| **Components** | Desktop Agent |
| **Impact** | FCC connectivity permanently reports "unreachable" |

**Finding:** `ConnectivityManager.cs` (line 77) resolves `IFccAdapter` from `IServiceProvider`, but `IFccAdapter` is never registered in DI (`ServiceCollectionExtensions.cs`). Only `IFccAdapterFactory` is registered. The FCC probe always returns `false`, so FCC connectivity is permanently `DOWN`.

**Recommended Fix:** After config is loaded, create the adapter via `IFccAdapterFactory.Create()` and register the instance in the DI container, or inject the factory into the connectivity manager.

---

### BUG-015: Android Agent -- LocalApiServer Ktor Plugins Don't Stop Pipeline After Reject

| Field | Value |
|-------|-------|
| **Severity** | HIGH -- Security bypass + HTTP errors |
| **Components** | Android Agent |
| **Impact** | Unauthorized requests may still execute route handlers; double-response exceptions |

**Finding:** `LocalApiServer.kt` -- `LanApiKeyAuthPlugin` (lines 276-291), `LanApiBlockPlugin` (lines 298-318), and `RateLimitPlugin` (lines 370-394) call `call.respond(...)` to reject requests but do NOT return from the interceptor or cancel the pipeline. The request continues through to the route handler, which may execute unauthorized logic and attempt to send a second response, causing "Response already committed" exceptions.

**Recommended Fix:** After `call.respond(...)`, add `return@on<Phase>` or `finish()` to prevent pipeline continuation.

---

### BUG-016: Cloud -- LegalEntityId Not Cross-Validated Between Bootstrap Token and Site

| Field | Value |
|-------|-------|
| **Severity** | HIGH -- Data integrity |
| **Components** | Cloud |
| **Impact** | Registration could create an agent with a mismatched LegalEntityId, scoping data to the wrong entity |

**Finding:** `RegisterDeviceHandler.cs` (line 107) sets `registration.LegalEntityId = bootstrapToken.LegalEntityId`. There is no check that `site.LegalEntityId == bootstrapToken.LegalEntityId`. A bootstrap token created for Legal Entity A could register a device on a site belonging to Legal Entity B.

**Recommended Fix:** Add validation: `if (site.LegalEntityId != bootstrapToken.LegalEntityId) return SiteMismatch`.

---

## 5. Bug Report: Medium Severity

### BUG-017: Android Agent -- Duplicate TokenProvider Instance in ProvisioningActivity

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **Components** | Android Agent |
| **Impact** | Token caching state (decommission flag, mutex) diverges between the ephemeral and singleton instances |

**Finding:** `ProvisioningActivity.kt` (line 255) creates a `new KeystoreDeviceTokenProvider(...)` instead of using the Koin DI singleton. While tokens are stored in the shared Keystore/EncryptedPrefs, the in-memory volatile `decommissionedCached` flag and `refreshMutex` on the DI singleton are not updated.

**Recommended Fix:** Inject the DI-provided singleton via `val tokenProvider: DeviceTokenProvider by inject()`.

---

### BUG-018: Android Agent -- Config Encryption Is Dead Code

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **Components** | Android Agent |
| **Impact** | All config JSON stored unencrypted in Room DB |

**Finding:** `ConfigManager.kt` (line 30) has `keystoreManager: KeystoreManager? = null`. In `AppModule.kt`, the `ConfigManager` is constructed without passing the `keystoreManager`. Since the parameter is null, `encryptConfigJson()` returns raw JSON and all configs are stored unencrypted.

**Recommended Fix:** Pass the `KeystoreManager` instance from Koin DI into the `ConfigManager` constructor.

---

### BUG-019: Android Agent -- EncryptedPrefs saveRegistration Uses apply() Not commit()

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **Components** | Android Agent |
| **Impact** | Race: service can start before async write completes, reads stale `isRegistered = false` |

**Finding:** `EncryptedPrefsManager.kt` (line 134): `saveRegistration()` uses `.apply()` (async). The foreground service starts immediately after (ProvisioningActivity line 298). If the service reads `isRegistered` before the async write completes, it could misroute.

**Note:** The `isDecommissioned` setter correctly uses `.commit()` (line 95), showing awareness of this issue.

**Recommended Fix:** Change `saveRegistration()` to use `.commit()` for durability before starting the service.

---

### BUG-020: Desktop Agent -- ApiKeyMiddleware Stack Overflow via stackalloc

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM -- DoS vector |
| **Components** | Desktop Agent |
| **Impact** | A LAN attacker sending a multi-megabyte X-Api-Key header can crash the agent |

**Finding:** `ApiKeyMiddleware.cs` (line 79): `Span<byte> padA = stackalloc byte[maxLen]` where `maxLen` is derived from the request header length. No bounds check. A malicious request with a very large `X-Api-Key` header causes a stack overflow.

**Recommended Fix:** Cap `maxLen` to a reasonable maximum (e.g., 1024) before `stackalloc`. Reject keys longer than the cap immediately.

---

### BUG-021: Cloud -- Connectivity Filter Applied After Pagination

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **Components** | Cloud (Portal API) |
| **Impact** | Agent list returns fewer items than pageSize; incorrect totalCount |

**Finding:** `AgentsController.cs` (lines 140-141): The connectivity state filter is applied client-side after pagination. The `totalCount` query at line 95 also ignores the connectivity filter.

**Recommended Fix:** Push the connectivity filter into the SQL query by joining with the telemetry snapshot table.

---

### BUG-022: Desktop Agent -- macOS Keychain Leaks Secrets via Command Line

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM -- Security |
| **Components** | Desktop Agent (macOS) |
| **Impact** | Device tokens visible in `ps aux` output during storage |

**Finding:** `PlatformCredentialStore.cs` (line 146) passes the secret as a `-w` argument to `/usr/bin/security add-generic-password`. Command-line arguments are visible to all users via `ps aux`. The Linux `secret-tool` path correctly uses stdin.

**Recommended Fix:** Pass the secret via stdin using `Process.StandardInput.Write()`, not as a command-line argument.

---

### BUG-023: Cloud -- Half-Committed Registration on Config Assembly Failure

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **Components** | Cloud |
| **Impact** | Device is registered and bootstrap token consumed, but client receives HTTP 500. Token cannot be reused. Unrecoverable without admin intervention. |

**Finding:** `AgentController.cs` (lines 151-164): If `RegisterDeviceAsync` succeeds but `GetAgentConfigHandler` fails, the registration is committed but the response is 500. The bootstrap token is consumed (USED), so the device cannot retry.

**Recommended Fix:** Wrap registration + config assembly in a DB transaction. Roll back if config assembly fails.

---

### BUG-024: Desktop Agent -- Hardcoded Tick Interval Assumption in CadenceController

| Field | Value |
|-------|-------|
| **Severity** | MEDIUM |
| **Components** | Desktop Agent |
| **Impact** | Telemetry and SYNCED_TO_ODOO polling fire at wrong intervals |

**Finding:** `CadenceController.cs` -- `IsTelemetryTick` (line 383) and `IsSyncedToOdooPollTick` (line 365) both divide by hardcoded `30` instead of the actual `CloudSyncIntervalSeconds` tick base. If the tick interval is 60s, telemetry fires at double the expected rate.

**Recommended Fix:** Use `cadence / actualTickIntervalSeconds` instead of `cadence / 30`.

---

## 6. Bug Report: Low Severity

### BUG-025: Cloud -- Missing Audit Trail for Registration Events

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **Components** | Cloud |
| **Impact** | No audit record of when devices were registered, replaced, or had tokens generated |

**Finding:** No audit events are created for: device registration, agent replacement, bootstrap token generation, or decommissioning. Only telemetry submissions create audit events. Per Requirements.md (REQ-14), all transaction events should be audited.

---

### BUG-026: Cloud -- Duplicate Fields in VersionCheckResponse

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **Components** | Cloud |
| **Impact** | API confusion for consumers |

**Finding:** `AgentController.cs` (lines 372-384): `MinimumVersion` and `MinSupportedVersion` contain the same value. `UpdateUrl` and `DownloadUrl` contain the same value. Redundant fields that may diverge in future maintenance.

---

### BUG-027: Desktop Agent -- CredentialKeys Constants Are Dead Code

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **Components** | Desktop Agent |
| **Impact** | Duplicated constants that could diverge |

**Finding:** `CredentialKeys.cs` defines `DeviceToken`, `RefreshToken`, `FccApiKey`, `LanApiKey` constants. None are referenced anywhere. `DeviceTokenProvider.cs` uses inline string constants `"device:token"` and `"device:refresh_token"` instead.

**Recommended Fix:** Use `CredentialKeys` constants everywhere and delete inline duplicates.

---

### BUG-028: Cloud -- TokenHash Column Max Length 500 for SHA-256 (Always 64 chars)

| Field | Value |
|-------|-------|
| **Severity** | LOW |
| **Components** | Cloud |
| **Impact** | Wasted database storage space |

**Finding:** `AgentRegistrationConfiguration.cs` (line 26): `HasMaxLength(500)` for `TokenHash`. SHA-256 hex encoding is always exactly 64 characters.

---

## 7. Functional Incompleteness

### MISSING-001: Portal -- No Bootstrap Token Generation UI

| Status | Impact |
|--------|--------|
| **Not Implemented** | Admin must use raw API calls to generate provisioning tokens |

The portal Angular app has no component for generating bootstrap tokens. The cloud endpoint `POST /api/v1/admin/bootstrap-tokens` exists but has no portal UI. Admins cannot provision devices through the portal workflow.

---

### MISSING-002: Portal -- No QR Code Generation

| Status | Impact |
|--------|--------|
| **Not Implemented** | No way to produce QR codes for Android agent provisioning |

Requirements.md specifies QR code provisioning as the preferred method. The portal has no QR code generation capability. Even if bootstrap token generation is added (MISSING-001), there is no way to encode the token + site code + cloud URL into a scannable QR code.

---

### MISSING-003: Android Agent -- No Manual Entry Fallback

| Status | Impact |
|--------|--------|
| **Not Implemented** | If QR scanning fails (broken camera, damaged code), no alternative provisioning path |

Requirements.md specifies "manual entry in a setup screen (fallback)." The Android agent only supports QR code scanning. The Desktop agent has a manual entry wizard, but the Android agent does not.

---

### MISSING-004: Both Agents -- No Version Check on Startup

| Status | Impact |
|--------|--------|
| **Not Implemented** | Outdated agents run with potentially incompatible behavior |

Requirements.md specifies the agent should call `/agent/version-check` on startup and disable FCC communication if below minimum version. The cloud endpoint exists, the `CompatibilityDto` is parsed, but neither agent evaluates `minAgentVersion` or `maxAgentVersion` anywhere.

---

### MISSING-005: Android Agent -- No Certificate Pin Rotation via Config

| Status | Impact |
|--------|--------|
| **Not Implemented** | Certificate pin changes require APK update |

The Android agent hardcodes certificate pins in `AppModule.kt`. The `EdgeAgentConfigDto` has no field for certificate pins. The cloud cannot push new pins without an APK update. Requirements imply runtime pin delivery via SiteConfig.

---

### MISSING-006: Cloud -- No Bootstrap Token Revocation Endpoint

| Status | Impact |
|--------|--------|
| **Not Implemented** | Generated tokens cannot be cancelled before expiry |

The `BootstrapToken` entity supports `REVOKED` status, and the registration handler checks for it, but no API endpoint exists to revoke a token. Once generated, a token is valid for 72 hours with no cancellation option.

---

### MISSING-007: Desktop Agent -- No Certificate Pinning

| Status | Impact |
|--------|--------|
| **Not Implemented** | Cloud MITM attacks are possible |

Neither the cloud HTTP client nor any other client in the Desktop agent has certificate pinning. The Android agent has (hardcoded) pinning. The Desktop agent only enforces TLS 1.2+.

---

### MISSING-008: Both Agents -- Re-Provisioning Flow Not Handled

| Status | Impact |
|--------|--------|
| **Not Implemented** | After factory reset or token expiry (90 days without refresh), device is stuck |

If the refresh token expires (90-day lifetime), the agent cannot authenticate. `KeystoreDeviceTokenProvider` (Android) logs "re-provisioning required" but does not navigate to ProvisioningActivity. The Desktop agent similarly logs a warning but takes no corrective action. The user must manually clear data and re-provision.

---

### MISSING-009: Desktop Agent -- API Key Not Persisted During Provisioning

| Status | Impact |
|--------|--------|
| **Not Implemented** | LAN API key authentication is effectively disabled after restart |

`ProvisioningWindow.axaml.cs` (lines 432-438) generates an API key and displays it in the UI but never stores it in the credential store. After restart, the API key is empty and `ApiKeyMiddleware` warns "authentication is DISABLED."

---

### MISSING-010: Cloud -- No Active Bootstrap Token Limit Per Site

| Status | Impact |
|--------|--------|
| **Not Implemented** | Unlimited tokens can be generated, creating attack surface |

No check limits how many active bootstrap tokens exist for a site. An admin could generate thousands.

---

## 8. Cross-Platform Inconsistencies

| Feature | Android Agent | Desktop Agent | Risk |
|---------|--------------|---------------|------|
| **Provisioning Method** | QR code only | Text-entry wizard (code-based + manual) | Different provisioning flows; no manual fallback on Android |
| **HTTPS Enforcement** | Enforced on QR payload (rejects http://) | Not enforced | Desktop can register over insecure connections |
| **Token Refresh Mutex** | `Mutex` protects concurrent refreshes | No concurrency guard | Desktop can corrupt token state |
| **Credential Storage** | Android Keystore (hardware TEE) | DPAPI / Keychain / libsecret | Varying security levels; macOS leaks via CLI args |
| **Config Encryption** | Intended (dead code due to missing KeystoreManager injection) | No config encryption | Both store config unencrypted |
| **Localhost API Bypass** | Localhost requests bypass API key check | All requests require API key (no localhost bypass) | Inconsistent LAN API security model |
| **Decommission Persistence** | `commit()` (synchronous, durable) | `File.WriteAllTextAsync()` (async) | Desktop has a small window where decommission flag could be lost on crash |
| **Certificate Pinning** | Hardcoded (partial) | None | Desktop is more vulnerable to MITM |
| **Connectivity Probes** | Stubs (always return false) | Implemented but `IFccAdapter` never registered | Both non-functional for different reasons |
| **Boot Recovery** | BootReceiver auto-starts on boot | No auto-start mechanism | Desktop requires manual start or OS-level service registration |
| **Foreground Service** | Always-on foreground notification | Background service or GUI | Android ensures system doesn't kill the agent |

---

## 9. Improvement Suggestions

### IMP-001: Implement Atomic Registration Transaction

**Current:** Registration (device creation, token consumption, config assembly) is not wrapped in a DB transaction. Partial failures leave the system in an inconsistent state.

**Suggested:** Wrap the entire registration flow in a single DB transaction. If any step fails, roll back everything including the bootstrap token consumption.

**Impact:** Eliminates BUG-023 and prevents unrecoverable registration states.

---

### IMP-002: Add JWT Blacklist for Decommissioned Devices

**Current:** Decommissioned devices retain valid JWTs for up to 24 hours.

**Suggested:** Add a small in-memory cache or DB lookup in the JWT validation pipeline that checks `agent_registrations.is_active` for the device ID in the JWT's `sub` claim. Cache with 1-minute TTL to avoid per-request DB hits.

**Impact:** Eliminates BUG-011 security window.

---

### IMP-003: Use Database-Level Constraints for Single-Use Tokens

**Current:** Application-level checks for bootstrap token status and single-active-agent-per-site are susceptible to race conditions.

**Suggested:**
- Add a partial unique index: `CREATE UNIQUE INDEX ix_one_active_agent_per_site ON agent_registrations (site_id) WHERE is_active = true`
- Use atomic update for token consumption: `UPDATE bootstrap_tokens SET status = 'USED' WHERE id = @id AND status = 'ACTIVE' RETURNING *`

**Impact:** Eliminates BUG-007 and BUG-008 at the database level.

---

### IMP-004: Implement Process Restart After Android Registration

**Current:** The DI graph is stale after registration (wrong cloud URL, null adapter, stale singletons).

**Suggested:** After successful registration, call `System.exit(0)` or `ProcessPhoenix.triggerRebirth(context)` to restart the app cleanly. The `BootReceiver` and `LauncherActivity` already handle the post-registration startup path correctly.

**Impact:** Eliminates BUG-002, BUG-004, BUG-005 in one fix.

---

### IMP-005: Add Bootstrap Token Management to Portal

**Current:** No portal UI for generating, viewing, or revoking bootstrap tokens.

**Suggested:** Add a "Device Provisioning" section in the portal with:
- Generate token (with site code selector)
- View active tokens (with expiry countdown)
- Revoke token
- Generate QR code (with site code + cloud URL + token encoded as JSON)

**Impact:** Eliminates MISSING-001, MISSING-002, MISSING-006.

---

### IMP-006: Reduce JWT Lifetime and Add Configurable Expiry

**Current:** JWT lifetime is hardcoded at 24 hours.

**Suggested:** Make JWT lifetime configurable via `DeviceJwtOptions.TokenLifetimeMinutes` (default 60 minutes). Shorter tokens reduce the window of BUG-011 and limit exposure from stolen tokens.

**Impact:** Reduces security window of BUG-011; adds operational flexibility.

---

### IMP-007: Add Agent Version Check Gate on Startup

**Current:** `CompatibilityDto.minAgentVersion` is parsed but never evaluated.

**Suggested:** On startup (both agents), after loading local config, compare running agent version against `minAgentVersion`. If below, display a blocking message requiring the user to update. This is already a requirement per REQ-15.13.

**Impact:** Eliminates MISSING-004; prevents incompatible agents from operating.

---

### IMP-008: Standardize Provisioning Token Delivery

**Current:** Cloud expects header, agents send in body.

**Suggested:** Standardize on body (add `ProvisioningToken` to `DeviceRegistrationApiRequest`). This is simpler, aligns with both agent implementations, and is more REST-conventional for request-specific data.

**Impact:** Eliminates BUG-001 (the most critical bug).

---

### IMP-009: Add Re-Provisioning Flow

**Current:** If refresh token expires, the agent is stuck with no recovery path.

**Suggested:** When token refresh fails with 401 (not decommission), clear registration state and navigate to the provisioning screen. Alternatively, add a "Re-provision" button in the diagnostics screen that clears state and restarts the provisioning flow.

**Impact:** Eliminates MISSING-008; improves field serviceability.

---

### IMP-010: Implement Missing Database Indexes

**Current:** Several queries lack supporting indexes.

**Suggested additions:**
- `agent_registrations`: Index on `legal_entity_id`; partial unique index on `(site_id) WHERE is_active = true`
- `bootstrap_tokens`: Index on `(site_code, status)` for active token lookup; index on `expires_at` for cleanup
- `agent_telemetry_snapshots`: FK constraint to `agent_registrations`
- `device_refresh_tokens`: Index on `(device_id, revoked_at)` for decommission revocation

**Impact:** Improves query performance and data integrity.

---

**End of Report**
