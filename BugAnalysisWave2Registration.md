# Bug Analysis Wave 2 - Device Registration Flow

**Date:** 2026-03-13
**Scope:** End-to-end device registration flow across Android Edge Agent, Desktop Edge Agent, and Cloud Backend
**Total Defects Found:** 39

---

## Summary by Severity

| Severity | Count | Components |
|----------|-------|------------|
| Critical | 3     | Android (1), Cloud (2) |
| High     | 14    | Android (5), Desktop (5), Cloud (4) |
| Medium   | 15    | Android (3), Desktop (6), Cloud (4) |
| Low      | 7     | Android (1), Desktop (4), Cloud (2) |

---

## CRITICAL DEFECTS

### C-01: Token Storage Failure Silently Proceeds to Service Start (Android)

**Files:**
- `src/edge-agent/.../ui/ProvisioningActivity.kt` lines 353-354, 167-180
- `src/edge-agent/.../sync/KeystoreDeviceTokenProvider.kt` lines 167-181

**Description:** In `handleRegistrationSuccess`, `tokenProvider.storeTokens()` is called which internally calls `keystoreManager.storeSecret()` for both the device token and refresh token. If `storeSecret()` returns `null` (Keystore failure), `storeTokens()` merely logs an error but does **not** throw or return a failure indicator. The calling code has no way to detect this failure. The flow then marks `isRegistered=true`, starts the foreground service, and navigates to DiagnosticsActivity.

**Production Impact:** The device appears fully registered, the service starts, but it has no usable tokens in the Keystore. Every cloud API call returns `null` token. The device is stuck in a zombie state -- registered but non-functional with no user-visible error. The only recovery is manual app data clearing.

---

### C-02: TransactionScope Likely No-Op Without Npgsql Enlistment (Cloud)

**File:** `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs` lines 172-221

**Description:** The `Register` endpoint wraps registration + config assembly in a `System.Transactions.TransactionScope`. However, Npgsql 7+ does not participate in `TransactionScope` ambient transactions by default -- it requires `Enlist=true` in the connection string. Without it, the `TransactionScope` is a no-op: if config assembly fails after `TrySaveChangesAsync` succeeds, the bootstrap token is consumed but the device gets HTTP 500 and cannot retry.

**Production Impact:** Registration succeeds (token consumed, device row written) but the client receives 500 because config assembly failed. The bootstrap token is burned. The device is half-registered -- has a JWT but no config returned. Admin must manually generate a new bootstrap token.

---

### C-03: DeviceTokenService Generates Token With Empty Signing Key (Cloud)

**File:** `src/cloud/FccMiddleware.Api/Auth/DeviceTokenService.cs` line 31

**Description:** If `DeviceJwt:SigningKey` is not configured (or empty), `GenerateDeviceToken` still creates a `SymmetricSecurityKey` from an empty string and signs the JWT. This produces a token signed with a trivially guessable zero-entropy key. Meanwhile, JWT validation in `Program.cs` (lines 90-95) returns early with no `TokenValidationParameters`, meaning validation is disabled or will reject all tokens inconsistently.

**Production Impact:** If the signing key config is missing (misconfiguration, secret rotation failure), every newly registered device gets a JWT that either cannot authenticate on subsequent calls or is signed with a zero-entropy key.

---

## HIGH SEVERITY DEFECTS

### H-01: Initial Config Stored Unencrypted While Later Configs Are Encrypted (Android)

**File:** `src/edge-agent/.../ui/ProvisioningActivity.kt` lines 369-383

**Description:** During registration, the initial config is persisted to Room as plaintext JSON. However, `ConfigManager.applyConfig()` encrypts config JSON using AES-256-GCM before persisting all subsequent configs. The initial config bypasses this encryption entirely.

**Production Impact:** The initial site configuration -- which may contain FCC credentials in `secretEnvelope.payload`, host addresses, and API keys -- is stored in plaintext in the SQLite database. Accessible to anyone with physical access to the device or on rooted devices.

---

### H-02: Re-provisioning Does Not Clear Old Keystore Keys or Encrypted Prefs (Android)

**Files:**
- `src/edge-agent/.../sync/KeystoreDeviceTokenProvider.kt` lines 152-159
- `src/edge-agent/.../ui/ProvisioningActivity.kt` (`handleRegistrationSuccess`)

**Description:** When `markReprovisioningRequired()` is called, it sets `isRegistered=false` but neither the old Keystore keys nor the EncryptedSharedPreferences data are cleared. `KeystoreManager.clearAll()` and `EncryptedPrefsManager.clearAll()` exist but are never called before re-provisioning.

**Production Impact:** When a device is re-provisioned to a different site, stale identity data (old deviceId, siteCode, legalEntityId) remains. If `saveRegistration()` partially fails, the device could authenticate with mixed old/new identity data, cross-contaminating site data.

---

### H-03: Decommissioned State Not Monitored by Running Service (Android)

**File:** `src/edge-agent/.../service/EdgeAgentForegroundService.kt` lines 241-263

**Description:** The `monitorReprovisioningState()` loop handles re-provisioning but there is **no equivalent monitor** for decommissioned state. The decommission flag is only checked on next full app launch via `LauncherActivity`. The decommissioned device continues running the foreground service with connectivity probes, local API server, and cadence loop.

**Production Impact:** A decommissioned device continues consuming battery and network resources indefinitely. The user sees the "running" notification and assumes it is working. Only reachable through force-kill and relaunch -- could be hours or days.

---

### H-04: Race Condition Between Registration Writes and Service Start (Android)

**File:** `src/edge-agent/.../ui/ProvisioningActivity.kt` lines 339-405

**Description:** If the Room write of initial config fails (caught by try/catch), `configManager.loadFromLocal()` in the freshly started service finds no stored config and falls into `configureBootstrapRuntime()` -- a degraded state showing "UNPROVISIONED" with no FCC runtime.

**Production Impact:** Device appears registered but is non-functional until the first config poll succeeds (requires internet and valid token). User sees DiagnosticsActivity and may not realize the device is in degraded state.

---

### H-05: failRuntimeReadiness Causes Permanent Crash Loop (Android)

**File:** `src/edge-agent/.../service/EdgeAgentForegroundService.kt` lines 266-272

**Description:** If the FCC adapter cannot be resolved from config (unsupported vendor/protocol), `failRuntimeReadiness()` calls `stopSelf()`. Since the service returns `START_STICKY`, Android restarts it, the same config loads, same failure occurs -- infinite crash loop.

**Production Impact:** A single invalid FCC vendor value in a config push kills the entire edge agent permanently. The cloud cannot push a corrected config because the service dies before config poll runs. Only recovery is manual intervention.

---

### H-06: Non-Atomic File Write for registration.json (Desktop)

**File:** `src/desktop-edge-agent/.../Registration/RegistrationManager.cs` lines 88-98

**Description:** `SaveStateAsync` calls `File.WriteAllTextAsync(path, json, ct)` directly. If the process crashes or power is lost mid-write, `registration.json` is corrupted. On next startup, `LoadState()` hits the `JsonException` catch and treats the device as unregistered.

**Production Impact:** Power failure during save (plausible at fuel stations) corrupts registration state, forcing manual re-provisioning with a new bootstrap token.

---

### H-07: IsSyncedToOdooPollTick Always Returns True (Desktop)

**File:** `src/desktop-edge-agent/.../Runtime/CadenceController.cs` lines 448-454

**Description:** `IsSyncedToOdooPollTick` computes `cadence` and `tickIntervalSeconds` from the same value (`config.CloudSyncIntervalSeconds`), so `every = Max(1, cadence / tickIntervalSeconds)` is always 1. The SYNCED_TO_ODOO poll runs every tick instead of at its intended sub-interval.

**Production Impact:** Excessive cloud API traffic on every cadence tick (every 30-60 seconds), wasting bandwidth at remote fuel stations and potentially causing rate limiting.

---

### H-08: Cached Unregistered State in PostConfigure After Manual Provisioning (Desktop)

**File:** `src/desktop-edge-agent/.../Views/ProvisioningWindow.axaml.cs` lines 558-617

**Description:** For manual config path, registration state is saved then host started. But `PostConfigure` calls `LoadState()` which may return the cached unregistered state if `IOptions<AgentConfiguration>` was already resolved during DI build.

**Production Impact:** After manual provisioning, `AgentConfiguration` may not have `DeviceId`, `SiteId`, or `CloudBaseUrl` populated. Cloud upload silently skips, connectivity probes fail, and the agent appears functional but never syncs data.

---

### H-09: No HTTPS Enforcement on Registration Endpoint (Desktop)

**File:** `src/desktop-edge-agent/.../Registration/DeviceRegistrationService.cs` lines 43-80

**Description:** `RegisterAsync` accepts any `cloudBaseUrl` string and sends the provisioning token without HTTPS validation. Unlike `CloudUploadWorker` which checks `CloudUrlGuard.IsSecure()`, registration has no such check. The `ProvisioningWindow` accepts both HTTP and HTTPS URLs.

**Production Impact:** If a field technician enters an HTTP URL, the provisioning token and returned device/refresh tokens are transmitted in cleartext, vulnerable to interception.

---

### H-10: ConnectivityManager Captures Empty CloudBaseUrl at Construction (Desktop)

**File:** `src/desktop-edge-agent/.../Connectivity/ConnectivityManager.cs` lines 68-78

**Description:** The DI constructor captures `config.Value.CloudBaseUrl` in a closure at construction time. If CloudBaseUrl changes after registration (via PostConfigure), the connectivity probe still uses the original (possibly empty) URL. Since `IOptions<AgentConfiguration>` is used (not `IOptionsMonitor`), the captured value never updates.

**Production Impact:** After provisioning, the agent permanently reports "Internet Down" even when internet is available. Cloud upload is gated on `IsInternetUp`, so no transactions are ever uploaded until process restart.

---

### H-11: No Input Validation on Registration DTO (Cloud)

**File:** `src/cloud/FccMiddleware.Contracts/Registration/DeviceRegistrationApiRequest.cs` lines 1-12

**Description:** `DeviceRegistrationApiRequest` has `SiteCode`, `DeviceSerialNumber`, `DeviceModel`, etc. as strings with no `[Required]`, `[MaxLength]`, or `[StringLength]` annotations. The database columns have max-length constraints, so oversized values throw unhandled `DbUpdateException`.

**Production Impact:** A malicious or buggy edge agent can send oversized fields, causing database exceptions that surface as generic 500 errors. The bootstrap token may already be consumed.

---

### H-12: Null Device Passes Through DeviceActiveCheckMiddleware (Cloud)

**File:** `src/cloud/FccMiddleware.Api/Infrastructure/DeviceActiveCheckMiddleware.cs` lines 28-29

**Description:** The middleware checks `if (device is not null && !device.IsActive)` -- if the device is NOT found in the database (deleted, or forged JWT), the request passes through. A valid JWT with a non-existent device ID bypasses this middleware entirely.

**Production Impact:** A forged or stale JWT whose device ID was deleted from the database passes through the active check and reaches downstream endpoints.

---

### H-13: EdgeAgentDevice Policy Missing Scheme Pin (Cloud)

**File:** `src/cloud/FccMiddleware.Api/Program.cs` lines 218-222

**Description:** The `EdgeAgentDevice` authorization policy requires `site` and `lei` claims but does not call `.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)`. The `FccHmac` handler also adds `site` and `lei` claims when configured on a client, potentially satisfying this policy.

**Production Impact:** An FCC HMAC API key with `SiteCode` and `LegalEntityId` configured could access edge-agent-only endpoints (upload, config, telemetry), bypassing device JWT authentication.

---

### H-14: Advatec Fields Not Projected in GetBySiteCodeAsync (Cloud)

**File:** `src/cloud/FccMiddleware.Infrastructure/Repositories/SiteFccConfigProvider.cs` lines 167-217

**Description:** `BuildSiteFccConfig` does not set `AdvatecWebhookToken`, `AdvatecDevicePort`, `AdvatecEfdSerialNumber`, or `AdvatecCustIdType`. The `FccConfigProjection` class includes `AdvatecWebhookToken` but `ProjectFccConfigRow` does not populate Advatec fields. Advatec sites retrieved via `GetBySiteCodeAsync` always have null vendor-specific config.

**Production Impact:** Advatec sites looked up by site code (standard ingest path) have missing vendor-specific config, causing adapter normalization failures.

---

## MEDIUM SEVERITY DEFECTS

### M-01: Camera Hardware Required Flag Prevents Installation on Camera-less Devices (Android)

**File:** `src/edge-agent/app/src/main/AndroidManifest.xml` line 12

**Description:** `android:required="true"` for camera prevents installation on camera-less devices, but `ProvisioningActivity` supports manual entry fallback. Should be `android:required="false"`.

**Production Impact:** App cannot be installed on industrial tablets and Android kiosks without cameras, even though manual provisioning is fully supported.

---

### M-02: Certificate Pins Immutable After OkHttp Construction (Android)

**File:** `src/edge-agent/.../sync/CloudApiClient.kt` lines 272-275

**Description:** The `HttpCloudApiClient` is a Koin singleton. `updateBaseUrl()` changes the URL but does not rebuild OkHttp with new pins. If the cloud URL changes to a different hostname via config, certificate pinner remains pinned to the old hostname.

**Production Impact:** Cloud URL change to a different hostname causes all API calls to fail with SSL handshake errors. Device stuck until restarted.

---

### M-03: BootReceiver Does Not Handle Partial Reprovisioning Flag Write (Android)

**Files:**
- `src/edge-agent/.../service/BootReceiver.kt` lines 16-33
- `src/edge-agent/.../sync/KeystoreDeviceTokenProvider.kt` lines 152-159

**Description:** `markReprovisioningRequired()` uses `commit()` for the reprovisioning flag but `isRegistered=false` uses `apply()` (async). If the process is killed between these two operations, the device reboots with `isReprovisioningRequired=true` AND `isRegistered=true`.

**Production Impact:** After a crash during re-provisioning detection, the BootReceiver starts the service with expired tokens. For up to 10 seconds the service attempts cloud operations with invalid tokens, generating unnecessary 401 errors.

---

### M-04: Mutable RegistrationState Shared Cache (Desktop)

**File:** `src/desktop-edge-agent/.../Registration/RegistrationManager.cs` lines 47-86

**Description:** `LoadState()` caches a mutable `RegistrationState` object and returns the same reference to all callers. Concurrent calls to `MarkDecommissionedAsync()` and `MarkReprovisioningRequiredAsync()` both mutate the same cached object, causing race conditions.

**Production Impact:** Concurrent 401 and 403 responses from different cloud workers could race to set conflicting state.

---

### M-05: Event Handler Leak in "Continue Anyway" Flow (Desktop)

**File:** `src/desktop-edge-agent/.../Views/ProvisioningWindow.axaml.cs` lines 474-481

**Description:** The "Continue Anyway" flow detaches `OnNextClicked` and attaches an anonymous lambda. The cleanup inside the lambda removes `OnNextClicked` (already removed), not the anonymous handler. Multiple retries accumulate handlers.

**Production Impact:** Multiple "Continue Anyway" attempts cause duplicate step 4 transitions, potentially launching multiple MainWindows.

---

### M-06: Connection Test Bypasses Certificate Pinning (Desktop)

**File:** `src/desktop-edge-agent/.../Views/ProvisioningWindow.axaml.cs` lines 398, 423

**Description:** `RunConnectionTestsAsync()` creates raw `HttpClient` instances instead of using the DI-registered "cloud" named client. These bypass TLS certificate pinning configured on the named client.

**Production Impact:** Cloud connectivity test during provisioning succeeds even if the cloud URL has been MITM'd, giving a false positive.

---

### M-07: Splash Close Can Trigger Premature Shutdown (Desktop)

**File:** `src/desktop-edge-agent/.../App.axaml.cs` lines 34-63

**Description:** `InitializeDecommissionedMode` sets `ShutdownMode = OnLastWindowClose`. Then `splash.Close()` runs. If the decommissioned window isn't visible yet, closing splash triggers app shutdown.

**Production Impact:** Race condition: decommissioned device app silently exits without showing any UI.

---

### M-08: No Cancel Mechanism for Registration HTTP Calls (Desktop)

**File:** `src/desktop-edge-agent/.../Views/ProvisioningWindow.axaml.cs` lines 157-243

**Description:** `RegisterAsync` is called without a `CancellationToken`. Buttons are disabled. If the server is unresponsive, the UI freezes at "Registering..." indefinitely.

**Production Impact:** Field technicians at remote fuel stations with poor connectivity are stuck with disabled buttons, forced to kill the application.

---

### M-09: Equipment Data Not Synced After Initial Registration (Desktop)

**File:** `src/desktop-edge-agent/.../Views/ProvisioningWindow.axaml.cs`

**Description:** `RegistrationManager.SyncSiteData()` syncs equipment data from cloud config. But neither `DeviceRegistrationService.HandleSuccessAsync` nor `ProvisioningWindow` calls it after initial registration.

**Production Impact:** After fresh provisioning, no local equipment data exists until the first config poll (potentially 60+ seconds). Local API calls for pump status return empty results.

---

### M-10: Silent Upload Halt on Refresh Token Expiry (Desktop)

**File:** `src/desktop-edge-agent/.../Sync/CloudUploadWorker.cs` lines 152-153

**Description:** When `RefreshTokenExpiredException` is caught, the code sets `_decommissioned = true` and calls `MarkReprovisioningRequiredAsync()`. The agent silently stops uploading without any UI notification.

**Production Impact:** When a refresh token expires, the agent silently stops uploading transactions. No mechanism to notify the UI or trigger restart. Transactions buffer locally indefinitely until someone manually restarts.

---

### M-11: Constant-Time Comparison Defeated by Length Short-Circuit (Cloud)

**File:** `src/cloud/FccMiddleware.Infrastructure/Repositories/SiteFccConfigProvider.cs` lines 102-105

**Description:** `CryptographicOperations.FixedTimeEquals` returns false immediately when spans have different lengths. An attacker can determine the length of a valid webhook secret by timing requests with varying-length secrets.

**Production Impact:** Timing analysis could narrow the brute-force space for webhook secrets.

---

### M-12: Radix XML Endpoint Unauthenticated When SharedSecret is Null (Cloud)

**File:** `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs` lines 122-199

**Description:** The `IngestRadixXml` endpoint is `[AllowAnonymous]` and relies on Radix SHA-1 signature validation. But if a Radix site has no `SharedSecret` configured, signature validation is skipped -- anyone who knows a USN code can inject transactions.

**Production Impact:** Radix sites without a SharedSecret configured accept unauthenticated transaction injections. USN codes are small integer range (1-999999), easily enumerable.

---

### M-13: Internal Error Details Leaked in HTTP 500 Responses (Cloud)

**File:** `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs` lines 190-191

**Description:** The default case returns raw `result.Error.Message` to the client. This may contain database column names, constraint names, or stack traces depending on how the handler constructs errors.

**Production Impact:** Internal implementation details leaked to edge agents in error responses.

---

### M-14: DOMS/Radix Vendor Fields Not Projected in BuildSiteFccConfig (Cloud)

**File:** `src/cloud/FccMiddleware.Infrastructure/Repositories/SiteFccConfigProvider.cs` lines 195-217

**Description:** `BuildSiteFccConfig` does not set `JplPort`, `DppPorts`, `FcAccessCode`, `DomsCountryCode`, `PosVersionId`, `HeartbeatIntervalSeconds`, `ReconnectBackoffMaxSeconds`, `ConfiguredPumps`, or `FccPumpAddressMap`. DOMS and Radix sites via `GetBySiteCodeAsync` always get null vendor-specific fields.

**Production Impact:** DOMS TCP adapter gets incomplete config; Radix `FccPumpAddressMap` is always null, forcing fallback to offset-based pump resolution.

---

### M-15: Event Handler Leak in "Continue Anyway" Flow (Desktop)

*Duplicate of M-05, consolidated above.*

---

## LOW SEVERITY DEFECTS

### L-01: SplashActivity Transition Animation Called After finish() (Android)

**File:** `src/edge-agent/.../ui/SplashActivity.kt` lines 23-32

**Description:** `overrideActivityTransition()` is called after `finish()`. Should be called before `finish()` to affect the outgoing transition.

**Production Impact:** Splash-to-launcher transition may show a jarring default animation on certain devices.

---

### L-02: Manual Config Path Missing LegalEntityId (Desktop)

**File:** `src/desktop-edge-agent/.../Views/ProvisioningWindow.axaml.cs` lines 569-579

**Description:** Manual config path does not include `LegalEntityId` in the registration state. `CloudUploadWorker` falls back to `SiteId`, diverging from the code-path registration flow.

**Production Impact:** Transactions uploaded from manually-configured agents have `LegalEntityId` set to site code instead of actual legal entity ID until a cloud config poll fills it in. Could cause reconciliation mismatches.

---

### L-03: Timer Thread-Safety Issue in MainWindow StatusBar (Desktop)

**File:** `src/desktop-edge-agent/.../Views/MainWindow.axaml.cs` lines 46, 174-210

**Description:** `_statusTimer` fires on a ThreadPool thread. Timer callback may still be in flight when page dispose runs (line 257-258), accessing disposed resources.

**Production Impact:** Rare `ObjectDisposedException`. Non-fatal since catch-all swallows it.

---

### L-04: Silent No-Op if SiteDataManager is Null (Desktop)

**File:** `src/desktop-edge-agent/.../Registration/RegistrationManager.cs` lines 37-41

**Description:** Test constructor does not set `_siteDataManager`. Production constructor does not verify non-null. If DI registration of `SiteDataManager` fails, `SyncSiteData()` silently no-ops.

**Production Impact:** If SiteDataManager DI fails, equipment metadata sync fails silently.

---

### L-05: Partial Host Start Cleanup on Provisioning Failure (Desktop)

**File:** `src/desktop-edge-agent/.../Program.cs` lines 114-126

**Description:** If the web host partially starts during provisioning and then fails, the cleanup path may throw, potentially leaving orphaned ports or file handles.

**Production Impact:** Rare edge case requiring process restart.

---

### L-06: TOCTOU Race on Bootstrap Token Count (Cloud)

**File:** `src/cloud/FccMiddleware.Application/Registration/GenerateBootstrapTokenHandler.cs` lines 39-44

**Description:** Two concurrent requests can both read count=4 (below limit of 5), pass the check, and both insert, resulting in 6 active tokens. No database-level enforcement.

**Production Impact:** Slightly more tokens than the 5-token limit under concurrent admin usage. Minor policy violation.

---

### L-07: Missing Concurrency Conflict Handling on Token Revocation (Cloud)

**File:** `src/cloud/FccMiddleware.Application/Registration/RevokeBootstrapTokenHandler.cs` lines 44, 64

**Description:** Does not catch `DbUpdateConcurrencyException`. Concurrent revocation and registration of the same token produces HTTP 500 instead of a clear error.

**Production Impact:** Poor user experience on rare concurrent operation. Data integrity is protected by xmin concurrency token.

---

## Cross-Component Issues

### X-01: No End-to-End Registration Rollback

The registration flow spans edge agent -> cloud API -> database. If any step after token consumption fails, there is no rollback mechanism across components:
- **Cloud:** TransactionScope likely ineffective (C-02)
- **Android:** Token stored but Keystore fails silently (C-01)
- **Desktop:** Non-atomic file write (H-06)

The combined effect is that registration is a one-shot operation with no recovery path except manual admin intervention to generate new bootstrap tokens.

### X-02: Inconsistent Security Posture Across Platforms

- **Android:** Encrypts subsequent configs but not the initial one (H-01)
- **Desktop:** No HTTPS enforcement on registration (H-09)
- **Cloud:** Input validation absent (H-11), error details leaked (M-13)

### X-03: Post-Registration Startup Race Conditions

Both Android (H-04) and Desktop (H-08) have race conditions where the service starts before registration data is fully persisted or available via DI. This can result in a "registered but non-functional" state requiring restart.

---

## Recommended Fix Priority

1. **Immediate (Critical):** C-01, C-02, C-03 -- These can cause data loss, security vulnerabilities, or permanent device failures
2. **Next Sprint (High):** H-01 through H-14 -- These affect production reliability and security
3. **Backlog (Medium):** M-01 through M-14 -- These are edge cases or secondary issues
4. **Low Priority:** L-01 through L-07 -- Minor UX or theoretical issues
