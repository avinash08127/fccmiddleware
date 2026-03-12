# Edge Agent Device Registration — End-to-End Flow

This document describes the complete flow for scanning a QR code and registering an Android Edge Agent device with the FCC Middleware cloud backend. It covers every layer: the Android UI, security primitives, API contracts, cloud-side logic, and post-registration runtime startup.

---

## Table of Contents

1. [Overview](#1-overview)
2. [What Needs to Exist — Component Checklist](#2-what-needs-to-exist--component-checklist)
3. [QR Code Format](#3-qr-code-format)
4. [Android Entry Point: LauncherActivity](#4-android-entry-point-launcheractivity)
5. [ProvisioningActivity — QR Scan & Registration](#5-provisioningactivity--qr-scan--registration)
6. [Security Storage (Keystore + EncryptedPrefs)](#6-security-storage-keystore--encryptedprefs)
7. [Cloud API: Registration Endpoint](#7-cloud-api-registration-endpoint)
8. [Cloud API: Bootstrap Token Generation](#8-cloud-api-bootstrap-token-generation)
9. [Post-Registration: Config Download](#9-post-registration-config-download)
10. [Post-Registration: Service Startup](#10-post-registration-service-startup)
11. [Token Lifecycle](#11-token-lifecycle)
12. [Decommissioning Flow](#12-decommissioning-flow)
13. [Full State Machine](#13-full-state-machine)
14. [Key File Locations](#14-key-file-locations)
15. [What Still Needs to Be Created](#15-what-still-needs-to-be-created)

---

## 1. Overview

Device registration is a one-time bootstrapping process that binds an Android Edge Agent device to a specific FCC site in the cloud. The mechanism is:

1. An **administrator generates a single-use provisioning QR code** from the cloud management UI for a target site.
2. An **operator scans the QR code** on the Android device using the built-in camera.
3. The device sends its **hardware fingerprint** plus the **provisioning token** extracted from the QR code to the cloud.
4. The cloud validates the token, creates a device identity, and returns **long-lived credentials**.
5. The agent **encrypts and stores these credentials** using Android Keystore (hardware-backed TEE).
6. A foreground service starts, and the device enters normal **sync + ingestion operation**.

---

## 2. What Needs to Exist — Component Checklist

### Android Edge Agent (Kotlin)

| Component | File | Status |
|---|---|---|
| Entry-point routing | `ui/LauncherActivity.kt` | Must exist |
| QR scanner screen | `ui/ProvisioningActivity.kt` | Must exist |
| QR payload model | `sync/CloudApiModels.kt` → `QrBootstrapData` | Must exist |
| Registration request/response models | `sync/CloudApiModels.kt` | Must exist |
| HTTP client | `sync/CloudApiClient.kt` + `HttpCloudApiClient` | Must exist |
| Token storage interface | `sync/DeviceTokenProvider.kt` | Must exist |
| Token storage (Keystore) | `sync/KeystoreDeviceTokenProvider.kt` | Must exist |
| AES-GCM crypto helper | `security/KeystoreManager.kt` | Must exist |
| Encrypted preferences | `security/EncryptedPrefsManager.kt` | Must exist |
| Room database | `buffer/BufferDatabase.kt` | Must exist |
| Site config entity | `buffer/entity/AgentConfig.kt` | Must exist |
| Sync state entity | `buffer/entity/SyncState.kt` | Must exist |
| Config DTO | `config/EdgeAgentConfigDto.kt` | Must exist |
| Config poll worker | `sync/ConfigPollWorker.kt` | Must exist |
| DI module (Koin) | `di/AppModule.kt` | Must exist |
| Android Manifest | `AndroidManifest.xml` | Must declare CAMERA, INTERNET, FOREGROUND_SERVICE |
| ZXing dependency | `build.gradle.kts` | `com.journeyapps:zxing-android-embedded:4.3.0` |

### Cloud Backend (C#)

| Component | File | Status |
|---|---|---|
| Registration endpoint | `AgentController.cs` → `POST /api/v1/agent/register` | Must exist |
| Bootstrap token generation | `AgentController.cs` → `POST /api/v1/admin/bootstrap-tokens` | Must exist |
| Config endpoint | `AgentController.cs` → `GET /api/v1/agent/config` | Must exist |
| Token refresh endpoint | `AgentController.cs` → `POST /api/v1/agent/token/refresh` | Must exist |
| Decommission endpoint | `AgentController.cs` → `POST /api/v1/admin/agent/{deviceId}/decommission` | Must exist |
| DeviceRegistration domain entity | `Domain/Models/DeviceRegistration.cs` | Must exist |
| BootstrapToken domain entity | `Domain/Models/BootstrapToken.cs` | Must exist |

---

## 3. QR Code Format

The QR code encodes a compact JSON payload. The device parses this with `kotlinx.serialization` using lenient/ignoreUnknownKeys mode.

```json
{
  "v": 1,
  "sc": "SITE-001",
  "cu": "https://api.fccmiddleware.io",
  "pt": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
}
```

| Field | Key | Description |
|---|---|---|
| Version | `v` | Always `1`. Validation fails if different. |
| Site Code | `sc` | The site identifier the device is being registered to |
| Cloud URL | `cu` | Base URL of the cloud API (no trailing slash). Stored on device; used for all future requests. Prevents redirect attacks. |
| Provisioning Token | `pt` | Single-use opaque bootstrap token. This is the credential that authorises registration. It expires after use and has a short TTL. |

**Parsing logic** (`ProvisioningActivity.parseQrPayload()`):

```kotlin
@Serializable
data class QrBootstrapData(
    @SerialName("v") val version: Int,
    @SerialName("sc") val siteCode: String,
    @SerialName("cu") val cloudBaseUrl: String,
    @SerialName("pt") val provisioningToken: String,
)

fun parseQrPayload(raw: String): QrBootstrapData? {
    val data = Json { ignoreUnknownKeys = true; isLenient = true }
        .decodeFromString<QrBootstrapData>(raw)
    if (data.version != 1) return null
    if (data.siteCode.isBlank() || data.cloudBaseUrl.isBlank() || data.provisioningToken.isBlank()) return null
    return data
}
```

---

## 4. Android Entry Point: LauncherActivity

`LauncherActivity` is the main launcher activity declared in `AndroidManifest.xml`. On every cold start it reads two boolean flags from `EncryptedPrefsManager` and routes accordingly.

```
Cold Start
    │
    ├─ isDecommissioned == true  ──► DecommissionedActivity  (dead end)
    │
    ├─ isRegistered == true      ──► start EdgeAgentForegroundService
    │                                 └──► DiagnosticsActivity
    │
    └─ (neither)                 ──► ProvisioningActivity
```

**Required EncryptedPrefs keys read at startup:**

| Key | Type | Meaning |
|---|---|---|
| `is_registered` | Boolean | True once registration completes successfully |
| `is_decommissioned` | Boolean | True once cloud returns `DEVICE_DECOMMISSIONED` |

---

## 5. ProvisioningActivity — QR Scan & Registration

### 5.1 Camera Permission

The activity checks `Manifest.permission.CAMERA` at runtime. If not granted it calls `ActivityCompat.requestPermissions()`. The manifest must declare:

```xml
<uses-permission android:name="android.permission.CAMERA" />
<uses-feature android:name="android.hardware.camera" android:required="true" />
```

### 5.2 Launching the Scanner

Uses the JourneyApps ZXing embedded library:

```kotlin
val scanLauncher = registerForActivityResult(ScanContract()) { result ->
    if (result.contents != null) onQrScanned(result.contents)
}

fun launchScanner() {
    val options = ScanOptions().apply {
        setDesiredBarcodeFormats(ScanOptions.QR_CODE)
        setOrientationLocked(true)
        setBeepEnabled(true)
    }
    scanLauncher.launch(options)
}
```

### 5.3 Registration Request

After parsing the QR payload the activity collects the device hardware fingerprint and calls the cloud:

```kotlin
@Serializable
data class DeviceRegistrationRequest(
    @Sensitive val provisioningToken: String,
    val siteCode: String,
    val deviceSerialNumber: String,   // Build.getSerial() or Build.SERIAL
    val deviceModel: String,          // Build.MODEL
    val osVersion: String,            // Build.VERSION.RELEASE
    val agentVersion: String,         // PackageManager versionName
    val replacePreviousAgent: Boolean = false,
)
```

The call is made via `CloudApiClient.registerDevice(cloudBaseUrl, request)` using a **Ktor/OkHttp** client (not Retrofit). The `cloudBaseUrl` comes from the QR code, not from any previously-stored value.

### 5.4 Registration Response

On HTTP **201 Created**:

```kotlin
@Serializable
data class DeviceRegistrationResponse(
    val deviceId: String,
    @Sensitive val deviceToken: String,      // Short-lived JWT (access token)
    @Sensitive val refreshToken: String,     // Long-lived opaque refresh token (90 days)
    val tokenExpiresAt: String,             // ISO-8601
    val siteCode: String,
    val legalEntityId: String,
    val siteConfig: JsonObject? = null,     // Optional bootstrapped config
    val registeredAt: String,
)
```

**Error handling** — `CloudRegistrationResult` sealed class:

| Result | Cause | UI Action |
|---|---|---|
| `Success(response)` | HTTP 201 | Proceed to credential storage |
| `Rejected(code, message)` | HTTP 4xx | Show error message, allow retry |
| `TransportError(exception)` | Network failure | Show network error, allow retry |

**Known rejection error codes from the cloud:**

| Code | Meaning |
|---|---|
| `BOOTSTRAP_TOKEN_MISSING` | `pt` field absent from request |
| `BOOTSTRAP_TOKEN_INVALID` | Token not found in database |
| `BOOTSTRAP_TOKEN_EXPIRED` | Token TTL elapsed |
| `BOOTSTRAP_TOKEN_REVOKED` | Manually revoked by admin |
| `BOOTSTRAP_TOKEN_ALREADY_USED` | Single-use token already consumed |
| `ACTIVE_AGENT_EXISTS` | A registered agent already exists for this site (use `replacePreviousAgent: true`) |
| `SITE_NOT_FOUND` | `sc` site code unknown |
| `SITE_MISMATCH` | Token's bound site != `sc` in request |

### 5.5 Credential Storage After Success

Once a successful response arrives, the activity:

1. Encrypts `deviceToken` and `refreshToken` using `KeystoreManager` (AES-256-GCM, hardware-backed TEE).
2. Stores the encrypted blobs as Base64 strings in `EncryptedSharedPreferences`.
3. Stores identity fields (`deviceId`, `siteCode`, `legalEntityId`, `cloudBaseUrl`) in `EncryptedSharedPreferences`.
4. If `siteConfig` is present in the response, writes it into the `AgentConfig` Room table (id=1 singleton row).
5. Sets `is_registered = true`.

After this, **no plaintext token ever touches disk**.

---

## 6. Security Storage (Keystore + EncryptedPrefs)

### 6.1 Android Keystore (`KeystoreManager`)

- **Provider:** `AndroidKeyStore`
- **Algorithm:** AES/GCM/NoPadding, 256-bit key, hardware-backed on supported devices
- **IV handling:** IV is prepended to the ciphertext; both stored together as a single Base64 blob — self-contained decryption without separate IV storage

**Key aliases:**

| Alias | Purpose |
|---|---|
| `fcc-middleware-device-jwt` | Encrypts the short-lived access (JWT) token |
| `fcc-middleware-refresh-token` | Encrypts the 90-day refresh token |
| `fcc-middleware-fcc-cred` | Encrypts FCC controller credentials |
| `fcc-middleware-lan-key` | Encrypts the local LAN API key |

Keys are non-exportable and do not require user authentication.

### 6.2 EncryptedSharedPreferences (`EncryptedPrefsManager`)

- **File name:** `fcc_edge_secure_prefs`
- **Key encryption:** AES256_SIV
- **Value encryption:** AES256_GCM

**Stored keys:**

| Key | Type | Description |
|---|---|---|
| `device_id` | String | UUID assigned by cloud on registration |
| `site_code` | String | Site binding |
| `legal_entity_id` | String | Tenant/legal entity ID |
| `cloud_base_url` | String | Cloud API base URL from QR code |
| `fcc_host` | String | FCC controller LAN address |
| `fcc_port` | String | FCC controller port |
| `is_registered` | Boolean | True after successful registration |
| `is_decommissioned` | Boolean | True after DEVICE_DECOMMISSIONED from cloud |
| `device_token_enc` | String | Base64(IV + AES-GCM ciphertext) of access token |
| `refresh_token_enc` | String | Base64(IV + AES-GCM ciphertext) of refresh token |

---

## 7. Cloud API: Registration Endpoint

### `POST /api/v1/agent/register`

**Authentication:** None required (provisioning token is the credential)

**Request body:**

```json
{
  "provisioningToken": "eyJ...",
  "siteCode": "SITE-001",
  "deviceSerialNumber": "RF8N30ABCDE",
  "deviceModel": "Urovo i9100",
  "osVersion": "9",
  "agentVersion": "1.0.0",
  "replacePreviousAgent": false
}
```

**Success response (HTTP 201):**

```json
{
  "deviceId": "550e8400-e29b-41d4-a716-446655440000",
  "deviceToken": "eyJhbGciOiJSUzI1NiJ9...",
  "refreshToken": "rt_abc123...",
  "tokenExpiresAt": "2026-03-12T16:00:00Z",
  "siteCode": "SITE-001",
  "legalEntityId": "LE-042",
  "siteConfig": { ... },
  "registeredAt": "2026-03-12T08:00:00Z"
}
```

**Cloud-side responsibilities on receipt:**
1. Validate provisioning token (not expired, not revoked, not already used).
2. Validate `siteCode` matches token's bound site.
3. Check for an existing active agent — reject with `ACTIVE_AGENT_EXISTS` unless `replacePreviousAgent: true`.
4. Create `DeviceRegistration` domain entity.
5. Mark the bootstrap token as consumed (single-use).
6. Issue a signed JWT access token and an opaque refresh token.
7. Bundle current `SiteConfig` JSON into the response.
8. Return 201 with tokens + identity.

---

## 8. Cloud API: Bootstrap Token Generation

Before a QR code can be printed, an admin must generate a bootstrap token.

### `POST /api/v1/admin/bootstrap-tokens`

**Authentication:** Admin JWT

**Request:**

```json
{
  "siteCode": "SITE-001",
  "legalEntityId": "LE-042",
  "expiresInMinutes": 60
}
```

**Response (HTTP 201):**

```json
{
  "token": "bt_xyzabc...",
  "siteCode": "SITE-001",
  "expiresAt": "2026-03-12T09:00:00Z"
}
```

The caller then encodes the token into the QR JSON payload and displays the QR code for scanning.

---

## 9. Post-Registration: Config Download

The registration response includes an optional `siteConfig` JSON object as a bootstrap payload. If present it is immediately written to the `AgentConfig` Room table (id=1).

Subsequent config updates are fetched by `ConfigPollWorker`, called periodically by the `CadenceController`:

### `GET /api/v1/agent/config`

**Authentication:** Bearer token (device JWT)

**Request headers:**

```
Authorization: Bearer eyJ...
If-None-Match: "config-version-v3"   (ETag from last known config)
```

**Responses:**

| Status | Meaning | Action |
|---|---|---|
| 200 OK | New config available | Parse `EdgeAgentConfigDto`, apply via `ConfigManager`, update `AgentConfig` row |
| 304 Not Modified | Config unchanged | Update `lastConfigPullAt` timestamp only |
| 401 Unauthorized | Token expired | Attempt token refresh, then retry |
| 403 DEVICE_DECOMMISSIONED | Device revoked | Mark decommissioned, stop all sync |

### Configuration Structure (`EdgeAgentConfigDto`)

```
schemaVersion, configVersion, configId
├── compatibility       min/max agent versions
├── agent               deviceId, isPrimaryAgent
├── site                siteCode, legalEntityId, timezone, currency, operatingModel, connectivityMode
├── fccConnection       vendor, host, port, credentialsRef, protocolType, transactionMode, ingestionMode, heartbeatInterval
├── polling             pullInterval, batchSize, cursorStrategy
├── sync                cloudBaseUrl, uploadBatchSize, syncIntervals
├── buffer              retention, maxRecords, cleanupInterval
├── api                 localApiPort, lanApiEnabled, lanApiKeyRef
├── telemetry           telemetryInterval, logLevel
└── fiscalization       mode, requireTaxId, requireFiscalReceipt
```

---

## 10. Post-Registration: Service Startup

After credential storage completes, `ProvisioningActivity` starts `EdgeAgentForegroundService` and navigates to `DiagnosticsActivity`.

`EdgeAgentForegroundService` bootstraps the following in order:

1. **ConnectivityManager** — starts internet and FCC health probes
2. **LocalApiServer** — starts the local REST API (for inbound POS/FCC data) on the configured port
3. **CadenceController** — starts the tick-based scheduler that drives all periodic workers

Workers driven by `CadenceController`:

| Worker | Purpose |
|---|---|
| `CloudUploadWorker` | Uploads buffered FCC transactions to cloud in batches |
| `ConfigPollWorker` | Polls cloud for config updates (ETag-based) |
| `StatusPollWorker` | Polls cloud for synced-status (which transactions landed in Odoo) |
| `PreAuthCloudForwardWorker` | Forwards pre-authorization records to cloud |

---

## 11. Token Lifecycle

### Access Token (JWT)

- Short-lived (e.g., 8 hours)
- Used as `Authorization: Bearer <token>` on all cloud API calls
- Stored encrypted in Keystore under alias `fcc-middleware-device-jwt`
- On **401** response: `KeystoreDeviceTokenProvider.refreshAccessToken()` is called automatically

### Token Refresh

### `POST /api/v1/agent/token/refresh`

**Request:**
```json
{ "refreshToken": "rt_abc123..." }
```

**Response (HTTP 200):**
```json
{
  "deviceToken": "eyJ...",
  "refreshToken": "rt_newtoken...",
  "tokenExpiresAt": "2026-03-13T08:00:00Z"
}
```

- Both the access token **and** the refresh token are rotated on every refresh call.
- The new pair is re-encrypted and stored in Keystore + EncryptedPrefs.
- If refresh fails (e.g., refresh token expired after 90 days), the device returns to an unregistered/manual re-provisioning state.

---

## 12. Decommissioning Flow

When the cloud needs to permanently revoke a device:

### `POST /api/v1/admin/agent/{deviceId}/decommission`

**Cloud side:** Sets `deactivatedAt` on the `DeviceRegistration` entity. Subsequent token refreshes for that `deviceId` return `403 DEVICE_DECOMMISSIONED`.

**Agent side:** On receiving `403 DEVICE_DECOMMISSIONED` during any cloud call:

1. `KeystoreDeviceTokenProvider.markDecommissioned()` sets `is_decommissioned = true` in `EncryptedPrefsManager`.
2. `CadenceController` stops all workers.
3. Next cold start of `LauncherActivity` routes to `DecommissionedActivity`.
4. `DecommissionedActivity` is a dead-end screen. Re-provisioning requires a factory reset or manual clearance of app data.

---

## 13. Full State Machine

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  UNREGISTERED                                                                │
│  • is_registered = false                                                     │
│  • LauncherActivity → ProvisioningActivity                                   │
└──────────────────┬───────────────────────────────────────────────────────────┘
                   │ User scans valid QR code
                   ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│  PROVISIONING                                                                │
│  • POST /api/v1/agent/register in flight                                     │
│  • UI shows "Registering device..."                                          │
│  • On Rejected → back to UNREGISTERED (show error)                          │
│  • On TransportError → back to UNREGISTERED (show network error)            │
└──────────────────┬───────────────────────────────────────────────────────────┘
                   │ HTTP 201, tokens stored in Keystore
                   ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│  REGISTERED / OPERATIONAL                                                    │
│  • is_registered = true                                                      │
│  • EdgeAgentForegroundService running                                        │
│  • CadenceController ticking                                                 │
│  • Workers: upload, config poll, status poll, pre-auth forward              │
│  • Token auto-refresh on 401                                                 │
└──────────────────┬───────────────────────────────────────────────────────────┘
                   │ 403 DEVICE_DECOMMISSIONED received
                   ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│  DECOMMISSIONED                                                              │
│  • is_decommissioned = true                                                  │
│  • All sync permanently stopped                                              │
│  • LauncherActivity → DecommissionedActivity (dead end)                     │
│  • Recovery: factory reset / clear app data                                 │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## 14. Key File Locations

### Android Edge Agent

| File | Purpose |
|---|---|
| `src/edge-agent/app/src/main/AndroidManifest.xml` | Declares permissions (CAMERA, INTERNET, FOREGROUND_SERVICE), activities, and service |
| `src/edge-agent/app/src/main/kotlin/.../ui/LauncherActivity.kt` | Entry-point router |
| `src/edge-agent/app/src/main/kotlin/.../ui/ProvisioningActivity.kt` | QR scan UI + registration orchestration |
| `src/edge-agent/app/src/main/kotlin/.../ui/DiagnosticsActivity.kt` | Post-registration dashboard |
| `src/edge-agent/app/src/main/kotlin/.../ui/DecommissionedActivity.kt` | Dead-end decommission screen |
| `src/edge-agent/app/src/main/kotlin/.../sync/CloudApiClient.kt` | HTTP client interface + Ktor implementation |
| `src/edge-agent/app/src/main/kotlin/.../sync/CloudApiModels.kt` | All request/response/result data classes |
| `src/edge-agent/app/src/main/kotlin/.../sync/DeviceTokenProvider.kt` | Token management interface |
| `src/edge-agent/app/src/main/kotlin/.../sync/KeystoreDeviceTokenProvider.kt` | Keystore-backed token manager + refresh logic |
| `src/edge-agent/app/src/main/kotlin/.../sync/ConfigPollWorker.kt` | Periodic config polling |
| `src/edge-agent/app/src/main/kotlin/.../security/KeystoreManager.kt` | AES-GCM encrypt/decrypt via Android Keystore |
| `src/edge-agent/app/src/main/kotlin/.../security/EncryptedPrefsManager.kt` | Typed wrappers for EncryptedSharedPreferences |
| `src/edge-agent/app/src/main/kotlin/.../buffer/BufferDatabase.kt` | Room DB definition (6 tables) |
| `src/edge-agent/app/src/main/kotlin/.../buffer/entity/AgentConfig.kt` | Singleton Row for cached site config JSON |
| `src/edge-agent/app/src/main/kotlin/.../buffer/entity/SyncState.kt` | Singleton row for sync cursors and timestamps |
| `src/edge-agent/app/src/main/kotlin/.../config/EdgeAgentConfigDto.kt` | Deserialization model for cloud config payload |
| `src/edge-agent/app/src/main/kotlin/.../di/AppModule.kt` | Koin DI bindings |
| `src/edge-agent/app/build.gradle.kts` | Gradle deps: ZXing, Ktor, kotlinx-serialization, Room, Koin |

### Cloud Backend

| File | Purpose |
|---|---|
| `src/cloud/FccMiddleware.Api/Controllers/AgentController.cs` | All agent endpoints (register, config, token refresh, telemetry, decommission) |
| `src/cloud/FccMiddleware.Domain/Models/DeviceRegistration.cs` | Device registration domain entity |
| `src/cloud/FccMiddleware.Domain/Models/BootstrapToken.cs` | Single-use provisioning token entity |

---

## 15. What Still Needs to Be Created

The following components are referenced by the architecture but may not yet be fully implemented. This section tracks what is outstanding.

### Android Edge Agent

| Item | Notes |
|---|---|
| `DecommissionedActivity` | Simple screen telling the user the device has been decommissioned; no action except contacting support |
| Certificate pinning configuration | `HttpCloudApiClient` has a `certificatePins: List<String>` parameter; pins should be delivered via `EdgeAgentConfigDto` and applied when the client is rebuilt after config update |
| Re-provisioning recovery flow | Currently `DecommissionedActivity` is a dead end; a deliberate "factory reset" action (clear app data via Settings or a hidden button) may be needed for field recovery |
| `replacePreviousAgent` UI | When `ACTIVE_AGENT_EXISTS` is returned, a dialog should offer the user an option to replace the previous agent by re-sending with `replacePreviousAgent: true` |

### Cloud Backend

| Item | Notes |
|---|---|
| Bootstrap token generation UI | The admin portal needs a screen to generate a QR code for a site and display it for printing/scanning |
| QR code rendering | The portal must encode the JSON payload into an actual QR image (using a JS library such as `qrcode`) |
| Bootstrap token TTL enforcement | Server must reject expired tokens during registration; TTL is set at generation time |
| Token rotation on refresh | Both `deviceToken` and `refreshToken` must be rotated atomically on every refresh call |
| `DEVICE_DECOMMISSIONED` error propagation | All authenticated endpoints must check device deactivation status and return `403 DEVICE_DECOMMISSIONED` when applicable |

---

*Last updated: 2026-03-12*
