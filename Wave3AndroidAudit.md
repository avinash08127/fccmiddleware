# Wave 3 — Android Edge Agent End-to-End Audit

**Application:** Puma Energy FCC Edge Agent (Android)  
**Package:** `com.fccmiddleware.edge`  
**Auditor Role:** Senior Android Architect / QA Lead / Security Reviewer / Backend Integration Auditor  
**Date:** 2026-03-13  
**Codebase Path:** `src/edge-agent/`

---

## Table of Contents

1. [App Inventory](#1-app-inventory)
2. [Screen Traceability Report](#2-screen-traceability-report)
3. [Functional Findings Report](#3-functional-findings-report)
4. [Technical Findings Report](#4-technical-findings-report)
5. [Security Findings Report](#5-security-findings-report)
6. [Performance & Reliability Report](#6-performance--reliability-report)
7. [Test Gap Report](#7-test-gap-report)
8. [Remediation Plan](#8-remediation-plan)

---

## 1. App Inventory

### 1.1 Project Overview

| Property | Value |
|----------|-------|
| Module | `:app` (single module) |
| Namespace | `com.fccmiddleware.edge` |
| compileSdk | 35 |
| minSdk | 31 (Android 12) |
| targetSdk | 34 (Android 14) |
| versionCode | 1 |
| versionName | 1.0.0 |
| Language | Kotlin 2.1.0 |
| Build System | Gradle 9.2.1 / AGP 8.13.2 |
| DI Framework | Koin 4.0.1 |
| Networking | Ktor 3.0.3 (OkHttp engine) |
| Local DB | Room 2.6.1 |
| QR Scanning | ZXing 4.3.0 / 3.5.3 |
| Security | AndroidX Security-Crypto 1.1.0-alpha06 |
| UI Approach | Programmatic layout (no XML layouts, no Compose) |

### 1.2 Android Components

| Component | Class | Type |
|-----------|-------|------|
| SplashActivity | `ui.SplashActivity` | Activity (LAUNCHER) |
| LauncherActivity | `ui.LauncherActivity` | Activity |
| ProvisioningActivity | `ui.ProvisioningActivity` | Activity |
| DiagnosticsActivity | `ui.DiagnosticsActivity` | Activity |
| SettingsActivity | `ui.SettingsActivity` | Activity |
| DecommissionedActivity | `ui.DecommissionedActivity` | Activity |
| EdgeAgentForegroundService | `service.EdgeAgentForegroundService` | Foreground Service |
| BootReceiver | `service.BootReceiver` | BroadcastReceiver |
| FileProvider | `androidx.core.content.FileProvider` | ContentProvider |

### 1.3 Permissions

| Permission | Purpose |
|------------|---------|
| `INTERNET` | Cloud API communication |
| `ACCESS_NETWORK_STATE` | Connectivity detection |
| `ACCESS_WIFI_STATE` | WiFi network binding |
| `CHANGE_NETWORK_STATE` | Network selection |
| `FOREGROUND_SERVICE` | Persistent service |
| `FOREGROUND_SERVICE_DATA_SYNC` | Data sync foreground type |
| `RECEIVE_BOOT_COMPLETED` | Auto-start on boot |
| `CAMERA` | QR code provisioning |

### 1.4 Architectural Layers

```
┌─────────────────────────────────────────────────┐
│  UI Layer (6 Activities, programmatic layout)    │
├─────────────────────────────────────────────────┤
│  Service Layer (Foreground Service, Workers)     │
├─────────────────────────────────────────────────┤
│  Adapter Layer (Advatec, DOMS, Petronite, Radix)│
├─────────────────────────────────────────────────┤
│  Domain/Ingestion (Orchestrator, Pre-Auth)       │
├─────────────────────────────────────────────────┤
│  Network Layer (CloudApiClient, LocalApiServer)  │
├─────────────────────────────────────────────────┤
│  Storage Layer (Room DB, EncryptedPrefs, Keystore│
├─────────────────────────────────────────────────┤
│  Connectivity (NetworkBinder, ConnectivityMgr)   │
└─────────────────────────────────────────────────┘
```

### 1.5 Local Servers

| Server | Port | Protocol | Purpose |
|--------|------|----------|---------|
| LocalApiServer | 8585 | HTTP REST | Odoo POS integration |
| OdooWebSocketServer | 8443 | WebSocket | Legacy Odoo POS |
| AdvatecWebhookListener | 8091 | HTTP | Advatec receipt push |
| RadixPushListener | P+2 | HTTP | Radix unsolicited push |

### 1.6 Cloud API Endpoints Called

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `/api/v1/agent/register` | POST | Bootstrap token | Device registration |
| `/api/v1/agent/token/refresh` | POST | Refresh token | Token rotation |
| `/api/v1/agent/config` | GET | Bearer JWT | Config polling |
| `/api/v1/agent/telemetry` | POST | Bearer JWT | Telemetry |
| `/api/v1/agent/diagnostic-logs` | POST | Bearer JWT | Log upload |
| `/api/v1/agent/version-check` | GET | Bearer JWT | Version compat |
| `/api/v1/transactions/upload` | POST | Bearer JWT | Transaction sync |
| `/api/v1/transactions/synced-status` | GET | Bearer JWT | Odoo sync poll |
| `/api/v1/preauth` | POST | Bearer JWT | Pre-auth forward |

### 1.7 Room Database Schema (v5)

| Table | Purpose |
|-------|---------|
| `buffered_transactions` | Offline transaction buffer |
| `pre_auth_records` | Pre-auth lifecycle |
| `nozzles` | Odoo-FCC nozzle mapping |
| `sync_state` | Sync cursor (single-row) |
| `agent_config` | Encrypted config (single-row) |
| `audit_log` | Local audit trail |
| `site_info` | Site identity |
| `local_products` | Product mapping |
| `local_pumps` | Pump mapping |
| `local_nozzles` | Nozzle mapping |

---

## 2. Screen Traceability Report

### 2.1 Navigation Flow

```
App Launch
  └── SplashActivity (LAUNCHER, 2s delay)
        └── LauncherActivity (instant router)
              ├── [isDecommissioned] → DecommissionedActivity (dead end)
              ├── [isRegistered]    → EdgeAgentForegroundService + DiagnosticsActivity
              └── [else]            → ProvisioningActivity
                                          └── [success] → Service + DiagnosticsActivity

DiagnosticsActivity
  └── [Settings button] → SettingsActivity
  └── [Share Logs]       → Intent.createChooser (external)

SettingsActivity
  └── [Back / system back] → DiagnosticsActivity

EdgeAgentForegroundService (background monitors)
  ├── [reprovisioning required] → ProvisioningActivity (CLEAR_TASK)
  └── [decommissioned]          → DecommissionedActivity (CLEAR_TASK)
```

### 2.2 Screen-by-Screen Trace

---

#### Screen 1: SplashActivity

| Attribute | Detail |
|-----------|--------|
| **Purpose** | Brand splash for Puma Energy; 2-second delay before routing |
| **File** | `ui/SplashActivity.kt` (84 lines) |
| **UI Elements** | FrameLayout, ImageView (logo), 2× TextView (brand name, subtitle) |
| **User Actions** | None — non-interactive |
| **Navigation In** | App launcher |
| **Navigation Out** | LauncherActivity (after 2s, with fade transition) |
| **API Calls** | None |
| **Loading State** | Static splash — no spinner or animation |
| **Error State** | None |
| **Lifecycle Handling** | Only `onCreate`; no `onSaveInstanceState`, `onPause`, `onDestroy` |
| **Permission Requests** | None |
| **State Restoration** | None |
| **Back Button** | Default — exits app |

**Identified Issues:**
- No `Handler` cancellation on `onDestroy` → rotation during 2s delay starts two LauncherActivities
- No `android:configChanges` to prevent rotation recreation

---

#### Screen 2: LauncherActivity

| Attribute | Detail |
|-----------|--------|
| **Purpose** | Thin routing activity — checks registration state and navigates |
| **File** | `ui/LauncherActivity.kt` (52 lines) |
| **UI Elements** | None (no `setContentView`) |
| **User Actions** | None — instant routing |
| **Navigation In** | SplashActivity |
| **Navigation Out** | DecommissionedActivity / DiagnosticsActivity / ProvisioningActivity |
| **API Calls** | None (reads EncryptedPrefsManager) |
| **Loading State** | None — routing is synchronous |
| **Error State** | None — assumes EncryptedPrefs always works |
| **Lifecycle Handling** | Only `onCreate` |
| **Permission Requests** | None |
| **State Restoration** | N/A — finishes immediately |
| **Back Button** | N/A — finishes before user interaction |

**Identified Issues:**
- Brief empty window flash before navigation (no theme windowBackground)
- `startForegroundService` may throw on backgrounded launch (Android 12+ restrictions)
- No error handling if EncryptedSharedPreferences initialization fails (corruption, Keystore issues)

---

#### Screen 3: ProvisioningActivity

| Attribute | Detail |
|-----------|--------|
| **Purpose** | Device registration via QR scan or manual entry |
| **File** | `ui/ProvisioningActivity.kt` (722 lines) |
| **UI Elements** | Method selection: 2 buttons (Scan QR / Enter Manually). Manual entry: Spinner (env), 3 EditTexts (URL, site code, token), 2 buttons (Back, Register). Shared: ProgressBar, status text, error text. All in ScrollView. |
| **User Actions** | Scan QR, enter manual details, select environment, submit registration |
| **Navigation In** | LauncherActivity (unregistered), EdgeAgentForegroundService (reprovisioning) |
| **Navigation Out** | DiagnosticsActivity (on success, CLEAR_TASK) |
| **API Calls** | `POST /api/v1/agent/register` via `CloudApiClient.registerDevice()` |
| **Loading State** | ProgressBar + status text ("Registering device...", "Storing credentials...") |
| **Error State** | Red error text with specific messages for each failure mode |
| **Lifecycle Handling** | `onCreate` (layout + listeners), `onDestroy` (cancel scope) |
| **Permission Requests** | CAMERA (request code 100) for QR scanning |
| **State Restoration** | None — form state lost on rotation/process death |
| **Back Button** | Default — exits to launcher. "Back" button in manual entry → method selection |

**Form Validations:**
- Cloud URL: non-blank, must start with `https://`
- Site Code: non-blank
- Token: non-blank
- QR: version 1–2, required fields `sc` and `pt`, HTTPS enforcement

**API Call Trace → Backend:**
- POST `/api/v1/agent/register`
- Backend: `AgentsController.Register()` → `GenerateBootstrapTokenHandler`
- Auth: none (bootstrap token in body)
- Rate limit: 10 req/min per IP
- Request body: `DeviceRegistrationRequest`
- Success: 201 with deviceId, tokens, siteConfig
- Errors: 400 (SITE_NOT_FOUND), 401 (TOKEN_INVALID), 409 (TOKEN_ALREADY_USED, ACTIVE_AGENT_EXISTS)

**Identified Issues:**
- Double-tap: buttons disabled during registration, but rapid taps before `isEnabled = false` can fire multiple coroutines
- Rotation destroys activity; in-flight coroutine may reference destroyed views
- No `onSaveInstanceState`: manual entry form contents lost on rotation/process death
- `activityScope` is not lifecycle-aware — potential updates to destroyed activity
- System back from method selection exits entirely instead of user expectation

---

#### Screen 4: DiagnosticsActivity

| Attribute | Detail |
|-----------|--------|
| **Purpose** | Supervisor/technician diagnostics dashboard |
| **File** | `ui/DiagnosticsActivity.kt` (462 lines) |
| **UI Elements** | 6 sections with label/value rows: Connectivity (state, FCC heartbeat), Buffer (depth), Sync (lag, last sync), Config (version), Site Data (FCC type, products/pumps/nozzles, last synced), Recent Activity (audit log), File Logs (WARN/ERROR). 2 buttons (Settings, Share Logs). Last refresh timestamp. All in ScrollView. |
| **User Actions** | Navigate to Settings, Share Logs |
| **Navigation In** | LauncherActivity (registered), ProvisioningActivity (after registration) |
| **Navigation Out** | SettingsActivity (via button) |
| **API Calls** | None (local data only: Room DAOs, ConnectivityManager, ConfigManager, StructuredFileLogger) |
| **Loading State** | Initial "Loading..." / "..." values; auto-refresh every 5s |
| **Empty State** | "No recent audit entries", "No recent WARN/ERROR file log entries", "-" for missing site data |
| **Error State** | All DAO calls wrapped in try/catch with fallback values |
| **Lifecycle Handling** | `onCreate` (layout + initial refresh), `onResume` (schedule refresh), `onPause` (remove callbacks), `onDestroy` (cancel scope) |
| **Permission Requests** | None |
| **State Restoration** | None — scroll position lost on rotation |
| **Back Button** | Default — exits app |

**Identified Issues:**
- Double-tap Share Logs: no debounce — multiple zip/share operations
- `shareLogs()` returns silently if `logFiles.isEmpty()` — no user feedback
- `shareLogs()` catches exceptions but shows no error message to user
- Coroutine in `refreshData()` references `this@DiagnosticsActivity` — potential update to destroyed activity on rotation
- `handler.postDelayed` + `scope.launch` — dual scheduling mechanism creates complexity
- 5s auto-refresh creates ~20 sequential DAO queries per cycle; could be batched

---

#### Screen 5: SettingsActivity

| Attribute | Detail |
|-----------|--------|
| **Purpose** | Technician FCC connection overrides |
| **File** | `ui/SettingsActivity.kt` (491 lines) |
| **UI Elements** | 5 editable fields (FCC IP, FCC Port, JPL Port, Access Code, WS Port) with override indicators. 4 read-only fields (Cloud URL, Environment, Device ID, Site Code). 9 Cloud API route display rows. Status text. 3 buttons (Save & Reconnect, Reset to Cloud Defaults, Back). All in ScrollView. |
| **User Actions** | Edit FCC connection fields, Save & Reconnect, Reset to Cloud Defaults, Back |
| **Navigation In** | DiagnosticsActivity |
| **Navigation Out** | Back to DiagnosticsActivity via `finish()` |
| **API Calls** | None (all local: LocalOverrideManager, ConfigManager, CadenceController.requestFccReconnect()) |
| **Loading State** | None |
| **Error State** | Validation errors shown in red status text |
| **Lifecycle Handling** | Only `onCreate` |
| **Permission Requests** | None |
| **State Restoration** | None — form state lost on rotation |
| **Back Button** | `finish()` — back to Diagnostics |

**Form Validations:**
- FCC IP: `isValidHostOrIp()` when non-empty
- All ports: 1–65535 when non-empty
- Empty field = "use cloud default" (clears override)

**Identified Issues:**
- Double-tap Save: no debounce — multiple saves and `requestFccReconnect()` calls
- Rotation: no `onSaveInstanceState` — form edits lost
- `resolvedFccCredential()` may expose credential in plaintext as EditText hint
- No `onDestroy` to clean up anything
- `AlertDialog.Builder(this)` in `resetToCloudDefaults()` — standard but could leak on rotation

---

#### Screen 6: DecommissionedActivity

| Attribute | Detail |
|-----------|--------|
| **Purpose** | Dead-end screen shown when device is decommissioned (403) |
| **File** | `ui/DecommissionedActivity.kt` (76 lines) |
| **UI Elements** | "X" icon text (64sp red), title "Device Decommissioned", explanation message. All in ScrollView. |
| **User Actions** | None — informational dead-end |
| **Navigation In** | LauncherActivity (decommissioned), EdgeAgentForegroundService (detected decommission) |
| **Navigation Out** | None — back button consumed |
| **API Calls** | None |
| **Loading State** | None |
| **Error State** | N/A (the screen itself is the error state) |
| **Lifecycle Handling** | Only `onCreate` with `OnBackPressedCallback` |
| **Permission Requests** | None |
| **State Restoration** | None (static content) |
| **Back Button** | Consumed — no-op (security intent: user cannot navigate away) |

**Identified Issues:**
- No way to re-provision from this screen; user must clear app data or reinstall
- No contact information beyond "contact your supervisor"

---

## 3. Functional Findings Report

### F-001: Double-Tap Registration Submission

| Field | Value |
|-------|-------|
| **ID** | F-001 |
| **Title** | Rapid taps can trigger multiple concurrent registrations |
| **Screen/Flow** | ProvisioningActivity |
| **Severity** | Medium |
| **Category** | Functional Bug |
| **Description** | The `scanButton` and `manualRegisterButton` are disabled inside `performRegistration()`, but there is a race window between the click listener firing and `isEnabled = false` executing. Rapid double-taps can launch two coroutines that both call `POST /api/v1/agent/register`. |
| **Evidence** | `ProvisioningActivity.kt:313-317` — buttons disabled inside `performRegistration()` but after coroutine launch, not before the click handler returns. The `setOnClickListener` at line 123 calls `submitManualEntry()` which calls `performRegistration()` — no guard variable. |
| **Root Cause** | Click listeners do not use a `@Volatile` guard flag or immediate `isEnabled = false` before async work. |
| **User Impact** | First registration succeeds; second gets 409 ACTIVE_AGENT_EXISTS. User sees confusing error after successful provisioning. Backend may create two agent records. |
| **Recommended Fix** | Add `isEnabled = false` on all buttons immediately at the top of `performRegistration()` (before coroutine launch), or use a `@Volatile var isRegistering` guard. |

### F-002: Rotation Destroys In-Flight Registration

| Field | Value |
|-------|-------|
| **ID** | F-002 |
| **Title** | Device rotation during registration causes crash or silent failure |
| **Screen/Flow** | ProvisioningActivity |
| **Severity** | Medium |
| **Category** | Functional Bug |
| **Description** | When the device is rotated during `performRegistration()`, the activity is destroyed and recreated. The `activityScope` is cancelled in `onDestroy()`, which cancels the in-flight registration coroutine. The registration may succeed on the backend but the device never stores the tokens, leaving the device in an inconsistent state. |
| **Evidence** | `ProvisioningActivity.kt:85` — `activityScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)` is not lifecycle-scoped to `lifecycleScope`. Line 126-128: `onDestroy` cancels the scope. |
| **Root Cause** | Using a manually-managed `CoroutineScope` instead of `lifecycleScope` or a ViewModel. No `android:configChanges` declared. |
| **User Impact** | User rotates phone during registration → registration cancelled mid-flight. Token may be consumed server-side but never stored locally. Device cannot re-register with same token (409). |
| **Recommended Fix** | Either (a) add `android:configChanges="orientation|screenSize"` to the manifest for ProvisioningActivity, or (b) move registration logic to a ViewModel with `viewModelScope`, or (c) at minimum, lock orientation during registration. |

### F-003: Share Logs Silent Failure

| Field | Value |
|-------|-------|
| **ID** | F-003 |
| **Title** | Share Logs fails silently when no log files exist or on error |
| **Screen/Flow** | DiagnosticsActivity |
| **Severity** | Low |
| **Category** | Functional Bug |
| **Description** | When `fileLogger.getLogFiles()` returns empty, the `shareLogs()` function returns silently with only an `AppLogger.i` — no toast or UI feedback. When an exception occurs, it is caught and logged but no feedback is shown to the user. |
| **Evidence** | `DiagnosticsActivity.kt:411-413` — `if (logFiles.isEmpty()) { return@launch }` with no UI feedback. Line 442-444: catch block only logs. |
| **Root Cause** | Missing user-facing error/information messages in share flow. |
| **User Impact** | User taps "Share Logs", nothing visible happens. User retries repeatedly. |
| **Recommended Fix** | Show a `Toast` or `Snackbar` for empty logs ("No log files available") and for errors ("Failed to share logs"). |

### F-004: System Back from ProvisioningActivity Exits App

| Field | Value |
|-------|-------|
| **ID** | F-004 |
| **Title** | System back button behavior inconsistent with UI "Back" button |
| **Screen/Flow** | ProvisioningActivity |
| **Severity** | Low |
| **Category** | Functional Bug |
| **Description** | The manual entry panel has a "Back" button that returns to method selection. However, the system back button from manual entry exits the activity entirely (and since LauncherActivity used CLEAR_TASK, exits the app). |
| **Evidence** | `ProvisioningActivity.kt:122` — `manualBackButton.setOnClickListener { showMethodSelectionScreen() }`. No `onBackPressedDispatcher` handling. |
| **Root Cause** | No `OnBackPressedCallback` registered to handle system back when on manual entry panel. |
| **User Impact** | User presses system back expecting to return to method selection, but exits the app. |
| **Recommended Fix** | Register an `OnBackPressedCallback` that checks if `manualPanel.isVisible` and calls `showMethodSelectionScreen()` instead of finishing. |

### F-005: SplashActivity Double Navigation on Rotation

| Field | Value |
|-------|-------|
| **ID** | F-005 |
| **Title** | Rotating during splash delay causes double LauncherActivity start |
| **Screen/Flow** | SplashActivity |
| **Severity** | Low |
| **Category** | Functional Bug |
| **Description** | If the user rotates the device during the 2-second splash delay, `onCreate` runs again and posts a second `Handler.postDelayed` callback. Two LauncherActivity instances are started. |
| **Evidence** | `SplashActivity.kt:23-33` — `Handler.postDelayed` in `onCreate` with no cancellation in `onDestroy`. |
| **Root Cause** | No `onDestroy` override to remove the pending Handler callback. |
| **User Impact** | Brief UI glitch; two activities on the stack. Low severity due to CLEAR_TASK flags on LauncherActivity. |
| **Recommended Fix** | Store the Handler as a field and call `handler.removeCallbacksAndMessages(null)` in `onDestroy()`. Alternatively add `android:configChanges="orientation|screenSize"` or `android:screenOrientation="portrait"`. |

### F-006: No State Persistence Across Process Death

| Field | Value |
|-------|-------|
| **ID** | F-006 |
| **Title** | Form state not saved/restored on process death |
| **Screen/Flow** | ProvisioningActivity, SettingsActivity |
| **Severity** | Medium |
| **Category** | Functional Bug |
| **Description** | Neither ProvisioningActivity nor SettingsActivity implement `onSaveInstanceState`. If the system kills the process while the user is entering data (e.g., during a phone call), all form input is lost when the user returns. |
| **Evidence** | Neither activity overrides `onSaveInstanceState`. ProvisioningActivity has `siteCodeInput`, `tokenInput`, `cloudUrlInput`, `environmentSpinner` with no state save. SettingsActivity has `fccIpInput`, `fccPortInput`, etc. |
| **Root Cause** | Missing `onSaveInstanceState`/`onRestoreInstanceState` implementation. |
| **User Impact** | User enters long provisioning token, gets a phone call, returns to find form empty. Must re-enter everything. |
| **Recommended Fix** | Implement `onSaveInstanceState` to persist form state in `Bundle`, or use `rememberSaveable`-style approach. For ProvisioningActivity, also save the current panel visibility. |

### F-007: No Re-provisioning Path from DecommissionedActivity

| Field | Value |
|-------|-------|
| **ID** | F-007 |
| **Title** | Decommissioned device has no self-service recovery path |
| **Screen/Flow** | DecommissionedActivity |
| **Severity** | Low |
| **Category** | Functional Gap |
| **Description** | Once decommissioned, the user sees a dead-end screen with no action items. The only recovery is to clear app data or reinstall. There is no "Re-provision" or "Clear & Restart" button. |
| **Evidence** | `DecommissionedActivity.kt:30-32` — back button consumed, no other actions. |
| **Root Cause** | By design (security), but limits operational flexibility. |
| **User Impact** | Field technician must know to clear app data; no guidance on screen. |
| **Recommended Fix** | Add a "Clear Data & Re-provision" button with admin confirmation (e.g., supervisor PIN), or at minimum display instructions for clearing app data. |

### F-008: EncryptedSharedPreferences Corruption Not Handled

| Field | Value |
|-------|-------|
| **ID** | F-008 |
| **Title** | No error handling if EncryptedSharedPreferences fails to initialize |
| **Screen/Flow** | LauncherActivity → All flows |
| **Severity** | High |
| **Category** | Functional Bug |
| **Description** | `LauncherActivity` reads `encryptedPrefs.isDecommissioned` and `encryptedPrefs.isRegistered` without any try/catch. If the EncryptedSharedPreferences backing file is corrupted (known Android issue with `security-crypto` alpha), the app crashes on launch with an unrecoverable error. |
| **Evidence** | `LauncherActivity.kt:32-46` — direct reads with no error handling. EncryptedSharedPreferences uses `security-crypto:1.1.0-alpha06` (alpha quality). |
| **Root Cause** | Alpha-quality `security-crypto` library has known corruption bugs (Google Issue Tracker #164901843). No defensive coding around initialization. |
| **User Impact** | App crashes on every launch. User must clear app data to recover. Device loses registration and must re-provision. |
| **Recommended Fix** | Wrap EncryptedSharedPreferences access in try/catch. On failure, log the error, delete the corrupted file, and redirect to ProvisioningActivity. Consider using stable `security-crypto:1.0.0` or implementing custom AES-GCM encryption. |

---

## 4. Technical Findings Report

### T-001: Activity Coroutine Scope Not Lifecycle-Aware

| Field | Value |
|-------|-------|
| **ID** | T-001 |
| **Title** | Activities use manually-managed CoroutineScope instead of lifecycleScope |
| **Screen/Flow** | ProvisioningActivity, DiagnosticsActivity |
| **Severity** | Medium |
| **Category** | Lifecycle Misuse |
| **Description** | Both ProvisioningActivity and DiagnosticsActivity create `CoroutineScope(SupervisorJob() + Dispatchers.Main)` and manually cancel it in `onDestroy`. This is fragile: if `onDestroy` is not called (process death), the scope leaks. The standard `lifecycleScope` from `lifecycle-runtime-ktx` is lifecycle-aware and automatically cancels. |
| **Evidence** | `ProvisioningActivity.kt:85` — `private val activityScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)`. `DiagnosticsActivity.kt:55` — same pattern. |
| **Root Cause** | Missing `lifecycle-runtime-ktx` dependency or unfamiliarity with `lifecycleScope`. |
| **User Impact** | Potential memory leak if `onDestroy` is skipped. Coroutines may update views after activity is destroyed → `IllegalStateException`. |
| **Recommended Fix** | Replace `activityScope` with `lifecycleScope` from `androidx.lifecycle:lifecycle-runtime-ktx`. Add the dependency if missing. |

### T-002: DiagnosticsActivity Excessive Sequential IO

| Field | Value |
|-------|-------|
| **ID** | T-002 |
| **Title** | DiagnosticsActivity refresh makes ~10 sequential withContext(IO) calls |
| **Screen/Flow** | DiagnosticsActivity |
| **Severity** | Low |
| **Category** | Performance |
| **Description** | The `refreshData()` function makes sequential `withContext(Dispatchers.IO)` calls for each DAO query (buffer depth, sync state, audit log, site info, products, pumps, nozzles, file logs). These are independent queries that could be parallelized with `async/await` or batched into a single IO block. |
| **Evidence** | `DiagnosticsActivity.kt:99-246` — ~10 separate `withContext(Dispatchers.IO)` blocks, each performing one DAO call. Each context switch has overhead. |
| **Root Cause** | Sequential coding pattern instead of parallel coroutines. |
| **User Impact** | Slower refresh cycle (5s interval, but each refresh takes longer than needed). |
| **Recommended Fix** | Batch all DAO calls into a single `withContext(Dispatchers.IO)` block, or use `coroutineScope { async {} }` to parallelize independent queries. |

### T-003: No ViewModel Architecture

| Field | Value |
|-------|-------|
| **ID** | T-003 |
| **Title** | No ViewModel used anywhere — all state lives in Activities |
| **Screen/Flow** | All Activities |
| **Severity** | Medium |
| **Category** | Architecture |
| **Description** | None of the 6 activities use Android ViewModel. All state (form inputs, loading state, data) is held in activity-scoped variables that are destroyed on configuration changes. This is the root cause of several functional bugs (F-002, F-006). |
| **Evidence** | No ViewModel classes exist anywhere in the codebase. All activities use direct Koin injection and local variables. |
| **Root Cause** | Architectural decision to use simple activity-only architecture, possibly for a kiosk-style deployment. |
| **User Impact** | State loss on rotation, in-flight operation cancellation, inability to properly scope long-running operations. |
| **Recommended Fix** | Introduce ViewModel for ProvisioningActivity (registration state), DiagnosticsActivity (data state), and SettingsActivity (form state). This resolves F-002 and F-006 as side effects. |

### T-004: Service onStartCommand May Race with Existing Service

| Field | Value |
|-------|-------|
| **ID** | T-004 |
| **Title** | Multiple `onStartCommand` calls may run configuration setup concurrently |
| **Screen/Flow** | EdgeAgentForegroundService |
| **Severity** | Medium |
| **Category** | Concurrency |
| **Description** | `EdgeAgentForegroundService` returns `START_STICKY`, so the system may restart and call `onStartCommand` again. If the service is already running (e.g., from LauncherActivity) and `ProvisioningActivity` also calls `startForegroundService`, `onStartCommand` runs again. This launches new coroutines in `serviceScope` without cancelling existing ones, potentially running `configManager.loadFromLocal()`, `applyRuntimeConfig()`, and `cadenceController.start()` twice concurrently. |
| **Evidence** | `EdgeAgentForegroundService.kt:91-141` — `onStartCommand` launches multiple coroutines without checking if they're already running. `localApiServer.start()` and `cadenceController.start()` may be called multiple times. |
| **Root Cause** | No guard against re-entrant `onStartCommand`. `START_STICKY` guarantees restart but not idempotent startup. |
| **User Impact** | Duplicate connectivity probes, duplicate cadence ticks, potential port binding conflicts for local API server. |
| **Recommended Fix** | Add an `AtomicBoolean` or `@Volatile var isInitialized` guard in `onStartCommand` to skip setup if already running. Or move initialization to `onCreate` (called only once). |

### T-005: Handler Callback Leak in SplashActivity

| Field | Value |
|-------|-------|
| **ID** | T-005 |
| **Title** | Handler postDelayed callback not cancelled on destroy |
| **Screen/Flow** | SplashActivity |
| **Severity** | Low |
| **Category** | Memory Leak |
| **Description** | `SplashActivity` posts a delayed callback via `Handler(Looper.getMainLooper()).postDelayed(...)` but stores neither the Handler nor the Runnable. If the activity is destroyed before the 2s delay (e.g., back button), the callback still fires and calls `startActivity` on a destroyed context. |
| **Evidence** | `SplashActivity.kt:23-33` — anonymous lambda, no `onDestroy` override. |
| **Root Cause** | Handler reference not stored for cleanup. |
| **User Impact** | Minor — LauncherActivity handles edge case gracefully. Potential `WindowManager$BadTokenException` on older devices. |
| **Recommended Fix** | Store Handler as field, store Runnable, cancel in `onDestroy`. |

### T-006: targetSdk 34 with compileSdk 35 Discrepancy

| Field | Value |
|-------|-------|
| **ID** | T-006 |
| **Title** | targetSdk (34) lower than compileSdk (35) — missing Android 15 behavior changes |
| **Screen/Flow** | App-wide |
| **Severity** | Low |
| **Category** | Configuration |
| **Description** | `targetSdk` is 34 (Android 14) but `compileSdk` is 35 (Android 15). When targeting SDK 35, Android 15 introduces stricter foreground service restrictions, edge-to-edge enforcement, and predictive back gesture requirements. The app should either target 35 or document the rationale for staying at 34. |
| **Evidence** | `app/build.gradle.kts` — `targetSdk = 34`, `compileSdk = 35` |
| **Root Cause** | Incomplete SDK target upgrade. |
| **User Impact** | App may behave differently on Android 15 devices than expected. Google Play will require targetSdk 35 by Nov 2025 for updates. |
| **Recommended Fix** | Update targetSdk to 35 and verify foreground service, edge-to-edge, and predictive back gesture compatibility. |

### T-007: Room Database Missing Destructive Migration Fallback

| Field | Value |
|-------|-------|
| **ID** | T-007 |
| **Title** | No fallbackToDestructiveMigration configured for Room |
| **Screen/Flow** | BufferDatabase |
| **Severity** | Medium |
| **Category** | Data Integrity |
| **Description** | The Room database defines migrations 1→2, 2→3, 3→4, 4→5, but if a migration is missed (e.g., jumping from v3 to v5 without v4), Room will throw an `IllegalStateException` and crash the app. No `fallbackToDestructiveMigration` is configured as a safety net. |
| **Evidence** | Room schema files exist for versions 1-5 with manual migrations. |
| **Root Cause** | Deliberate choice to enforce data integrity, but creates crash risk. |
| **User Impact** | If a user somehow has a database version that doesn't match the migration chain, the app crashes on every launch. |
| **Recommended Fix** | Add `.fallbackToDestructiveMigration()` as a last resort, or implement a custom `MigrationStrategy` that handles any-version-to-latest. |

### T-008: Coroutine Exception Handling in Service Scope

| Field | Value |
|-------|-------|
| **ID** | T-008 |
| **Title** | Unhandled exceptions in service coroutines may crash the foreground service |
| **Screen/Flow** | EdgeAgentForegroundService |
| **Severity** | Medium |
| **Category** | Error Handling |
| **Description** | While `serviceScope` uses `SupervisorJob()` (so one child failure doesn't cancel siblings), unhandled exceptions in child coroutines still propagate to the thread's `UncaughtExceptionHandler` and may crash the service. No `CoroutineExceptionHandler` is installed. |
| **Evidence** | `EdgeAgentForegroundService.kt:58` — `CoroutineScope(SupervisorJob() + Dispatchers.IO)` with no `CoroutineExceptionHandler`. Lines 102-127: launch blocks without explicit exception handling. |
| **Root Cause** | Missing `CoroutineExceptionHandler` on the scope. |
| **User Impact** | Service crash → Android restarts (START_STICKY) → potential crash loop. |
| **Recommended Fix** | Add a `CoroutineExceptionHandler` to the service scope that logs errors and prevents service crash: `SupervisorJob() + Dispatchers.IO + exceptionHandler`. |

---

## 5. Security Findings Report

### S-001: Alpha-Quality Security-Crypto Library

| Field | Value |
|-------|-------|
| **ID** | S-001 |
| **Title** | Using alpha-quality `security-crypto:1.1.0-alpha06` for production secrets |
| **Screen/Flow** | App-wide (EncryptedPrefsManager, all token storage) |
| **Severity** | High |
| **Category** | Security — Dependency Risk |
| **Description** | The app uses `androidx.security:security-crypto:1.1.0-alpha06` for storing device tokens, refresh tokens, and registration data. This is an alpha release with known issues (Google Issue Tracker #164901843, #176215143) including SharedPreferences file corruption, Keystore key invalidation after biometric changes, and initialization crashes. |
| **Evidence** | `app/build.gradle.kts` — `implementation("androidx.security:security-crypto:1.1.0-alpha06")` |
| **Root Cause** | Using pre-release library in production. |
| **User Impact** | EncryptedSharedPreferences corruption → app crash loop → must clear data → device re-provisioning required. |
| **Recommended Fix** | Downgrade to stable `security-crypto:1.0.0` (uses Tink 1.5.0, stable), or implement custom AES-256-GCM encryption using the Android Keystore directly (which the app already does via `KeystoreManager`). |

### S-002: FCC Access Code Visible in Settings

| Field | Value |
|-------|-------|
| **ID** | S-002 |
| **Title** | FCC Access Code exposed in Settings EditText |
| **Screen/Flow** | SettingsActivity |
| **Severity** | Medium |
| **Category** | Security — Credential Exposure |
| **Description** | The `fccAccessCodeInput` is populated with the resolved FCC credential from either the cloud config or local override. Although the input type is `TYPE_TEXT_VARIATION_PASSWORD`, the value is set programmatically in `populateFields()` which means it is in memory and can be revealed by toggling password visibility. More critically, `resolvedFccCredential()` extracts the credential from `secretEnvelope.payload` or `credentialRef` and sets it as EditText text. |
| **Evidence** | `SettingsActivity.kt:94` — `fccAccessCodeInput.setText(localOverrideManager.fccCredential ?: cloudCredential)`. Line 135-140: `resolvedFccCredential()` returns plaintext credential. |
| **Root Cause** | No access control on settings screen. Any person with physical access can reveal the FCC access code. |
| **User Impact** | Unauthorized personnel can extract FCC credentials from the device. |
| **Recommended Fix** | (a) Do not pre-populate the access code field; show "••••••" placeholder and only save when the user explicitly enters a new value. (b) Consider requiring supervisor authentication (PIN/password) to access Settings. |

### S-003: ANDROID_ID Used as Device Serial Number

| Field | Value |
|-------|-------|
| **ID** | S-003 |
| **Title** | ANDROID_ID used as device identifier for registration |
| **Screen/Flow** | ProvisioningActivity |
| **Severity** | Low |
| **Category** | Security — Privacy |
| **Description** | `Settings.Secure.ANDROID_ID` is used as `deviceSerialNumber` in the registration request. Since Android 8, ANDROID_ID is unique per app signing key per user per device, and changes on factory reset. It is not a hardware serial number. The fallback `"unknown-${UUID.randomUUID()}"` generates a random identifier. |
| **Evidence** | `ProvisioningActivity.kt:354` — `Settings.Secure.getString(contentResolver, Settings.Secure.ANDROID_ID)` |
| **Root Cause** | `Build.SERIAL` was deprecated in API 29; ANDROID_ID is the recommended alternative but has different semantics. |
| **User Impact** | Device serial number changes on factory reset, potentially creating duplicate registrations. The UUID fallback creates a completely untraceable identifier. |
| **Recommended Fix** | This is acceptable for the use case (app-scoped unique identifier). Document that `deviceSerialNumber` is actually an app-scoped ID, not a hardware serial. The backend should use it for dedup, not hardware identification. |

### S-004: Local API Server Exposed on LAN Without mTLS

| Field | Value |
|-------|-------|
| **ID** | S-004 |
| **Title** | LAN API server uses only API key authentication, no TLS |
| **Screen/Flow** | LocalApiServer (port 8585) |
| **Severity** | Medium |
| **Category** | Security — Network |
| **Description** | When `enableLanApi = true`, the local API server binds to `0.0.0.0:8585` and accepts requests with `X-Api-Key` header. Traffic is unencrypted HTTP on the LAN, meaning the API key and all transaction data can be intercepted via ARP spoofing, WiFi sniffing, or any device on the same network. |
| **Evidence** | LocalApiServer configuration — binds `0.0.0.0` for LAN mode with API key only. |
| **Root Cause** | Ktor CIO embedded server does not natively support TLS easily, and adding TLS to a local service requires certificate management. |
| **User Impact** | Attacker on the station LAN can intercept/modify transactions, pre-auth requests, and pump status. |
| **Recommended Fix** | (a) Document that LAN API should only be used on trusted/isolated networks. (b) Consider adding TLS with a self-signed cert and pinning on the Odoo client. (c) Consider IP allowlist in addition to API key. |

### S-005: WebSocket Server No Authentication

| Field | Value |
|-------|-------|
| **ID** | S-005 |
| **Title** | Odoo WebSocket server has no authentication mechanism |
| **Screen/Flow** | OdooWebSocketServer (port 8443) |
| **Severity** | Medium |
| **Category** | Security — Authentication |
| **Description** | The WebSocket server accepts connections on port 8443 from any client on the network without authentication. Any device on the LAN can connect and send `manager_update`, `fp_unblock`, or other commands that modify transaction state. |
| **Evidence** | OdooWebSocketServer binds to `0.0.0.0` with `maxConnections` limit but no auth. |
| **Root Cause** | Legacy compatibility with Odoo POS which doesn't support authenticated WebSockets. |
| **User Impact** | Rogue client on the network can modify transactions, trigger pump unblocks, or discard transactions. |
| **Recommended Fix** | (a) Add origin validation or a shared secret in the initial WebSocket handshake. (b) Rate-limit command messages. (c) Log all inbound commands with client IP for audit trail. |

### S-006: Certificate Pinning Only Optional

| Field | Value |
|-------|-------|
| **ID** | S-006 |
| **Title** | Certificate pinning is optional and not enforced by default |
| **Screen/Flow** | CloudApiClient |
| **Severity** | Low |
| **Category** | Security — TLS |
| **Description** | Certificate pinning is only applied when `certificatePins` is non-empty (delivered via SiteConfig). A newly provisioned device has no pins until the first config poll succeeds. During this window, the device communicates with the cloud without pinning. |
| **Evidence** | `CloudApiClient.kt:648` — `if (certificatePins.isNotEmpty())` guard. Initial registration call uses `registerDevice(cloudBaseUrl, request)` which goes through the same client. |
| **Root Cause** | Chicken-and-egg: pins are delivered via config, but config requires a connection. |
| **User Impact** | MITM attack possible during initial registration and first config poll window. |
| **Recommended Fix** | (a) Bundle default pins in the APK as fallback. (b) Add `network_security_config.xml` with pin set for production domains. This provides pinning from first connection. |

### S-007: Provisioning Token Logged

| Field | Value |
|-------|-------|
| **ID** | S-007 |
| **Title** | Provisioning metadata logged including potential token context |
| **Screen/Flow** | ProvisioningActivity |
| **Severity** | Low |
| **Category** | Security — Logging |
| **Description** | `AppLogger.i(TAG, "Manual entry submitted, env=$envKey, starting registration")` at line 239 logs the environment key. While the token itself is not logged here, the QR parsing logs `"QR code scanned, parsing payload"`. The concern is whether AppLogger elsewhere logs request bodies. |
| **Evidence** | `ProvisioningActivity.kt:200,239` — log statements near sensitive data. Token is not directly logged based on code review. |
| **Root Cause** | Logging near sensitive operations. |
| **User Impact** | Low — token is not logged in the reviewed code. But the pattern creates risk for future modifications. |
| **Recommended Fix** | Add `@Sensitive` annotation or redaction to token fields. Review all AppLogger usages to ensure tokens are never logged. |

### S-008: SHA-1 Used for Radix Signing

| Field | Value |
|-------|-------|
| **ID** | S-008 |
| **Title** | Radix adapter uses SHA-1 for request/response signing |
| **Screen/Flow** | RadixAdapter, RadixPushListener |
| **Severity** | Medium |
| **Category** | Security — Cryptography |
| **Description** | The Radix FCC communication protocol uses SHA-1 hash of XML body + shared secret for request integrity. SHA-1 is cryptographically broken for collision resistance (SHAttered attack, 2017). An attacker who can intercept LAN traffic could potentially forge signed requests. |
| **Evidence** | `RadixSignatureHelper.kt` — SHA-1 signing. Test file `RadixSignatureHelperTests.kt` verifies SHA-1 signatures. |
| **Root Cause** | Radix FCC protocol specification mandates SHA-1. This is a protocol-level constraint, not an app design choice. |
| **User Impact** | On a compromised LAN, an attacker could forge FCC commands (pump authorization, transaction acknowledgment). |
| **Recommended Fix** | This is a protocol limitation. Document the risk. (a) Ensure the FCC LAN is physically isolated. (b) If Radix supports SHA-256, upgrade. (c) Add additional integrity checks at the application layer. |

---

## 6. Performance & Reliability Report

### P-001: No Explicit HTTP Timeouts Configured

| Field | Value |
|-------|-------|
| **ID** | P-001 |
| **Title** | Cloud API client uses OkHttp default timeouts (10s each) |
| **Screen/Flow** | All cloud API calls |
| **Severity** | Medium |
| **Category** | Performance — Timeout |
| **Description** | `HttpCloudApiClient` does not explicitly configure connect, read, or write timeouts on the Ktor OkHttp engine. OkHttp defaults are 10s connect, 10s read, 10s write. For a field device with potentially poor connectivity, these may be too short (causing premature failures) or too long (blocking the sync cadence). |
| **Evidence** | `CloudApiClient.kt:636-687` — `buildKtorClient()` configures interceptors and pinning but no timeout settings. |
| **Root Cause** | Omitted timeout configuration. |
| **User Impact** | On slow networks, requests may time out prematurely. On completely unresponsive servers, the 30s total timeout (10+10+10) blocks the cadence tick. |
| **Recommended Fix** | Explicitly configure timeouts: `connectTimeoutMillis = 15_000`, `readTimeoutMillis = 30_000`, `writeTimeoutMillis = 30_000` for the cloud client. Consider separate timeout profiles for registration (longer) vs. telemetry (shorter). |

### P-002: Transaction Upload Batch Size Not Adaptive

| Field | Value |
|-------|-------|
| **ID** | P-002 |
| **Title** | Upload batch size is fixed from config, adaptive reduction only on 413 |
| **Screen/Flow** | CloudUploadWorker |
| **Severity** | Low |
| **Category** | Performance |
| **Description** | The upload worker uses `config.sync.uploadBatchSize` as the batch size. On HTTP 413 (Payload Too Large), it halves the batch and retries. However, there's no proactive adaptation based on network conditions, response times, or error rates. A site with 10,000 buffered transactions uses the same batch size as a site with 10. |
| **Evidence** | CloudUploadWorker implementation handles 413 → halve. No other adaptive behavior. |
| **Root Cause** | Simple implementation; adaptation adds complexity. |
| **User Impact** | Suboptimal sync speed on high-backlog sites. |
| **Recommended Fix** | Consider starting with a larger batch on backlog >1000 and reducing on failures. Not critical given the existing 413 handling. |

### P-003: 5-Second Auto-Refresh Creates UI Jank on Low-End Devices

| Field | Value |
|-------|-------|
| **ID** | P-003 |
| **Title** | DiagnosticsActivity 5s refresh creates view churn |
| **Screen/Flow** | DiagnosticsActivity |
| **Severity** | Low |
| **Category** | Performance — UI |
| **Description** | Every 5 seconds, `refreshData()` removes all views from `recentErrorsContainer` and `structuredLogsContainer` and recreates them. This causes GC pressure from short-lived TextView allocations and potential frame drops on low-end Android 12 devices. |
| **Evidence** | `DiagnosticsActivity.kt:202,224` — `removeAllViews()` followed by `addView()` loops every 5 seconds. |
| **Root Cause** | View inflation/destruction cycle instead of data binding or DiffUtil-style updates. |
| **User Impact** | Possible micro-stutters on low-end devices. Minor GC pressure. |
| **Recommended Fix** | Reuse existing views by updating their text/color instead of removing and recreating. Keep a list of TextViews and update content in-place. |

### P-004: Foreground Service Uses Generic System Icon

| Field | Value |
|-------|-------|
| **ID** | P-004 |
| **Title** | Foreground notification uses generic system icon |
| **Screen/Flow** | EdgeAgentForegroundService |
| **Severity** | Low |
| **Category** | UX / Reliability |
| **Description** | The foreground service notification uses `android.R.drawable.ic_menu_manage` (a generic system wrench icon) instead of the app's own icon. On Android 13+, generic icons may be hidden or appear unprofessional. |
| **Evidence** | `EdgeAgentForegroundService.kt:342` — `.setSmallIcon(android.R.drawable.ic_menu_manage)` |
| **Root Cause** | No custom notification icon created. |
| **User Impact** | Unprofessional appearance; on some OEMs, the notification may not render correctly. |
| **Recommended Fix** | Create a proper notification icon (`res/drawable/ic_notification.xml`) and reference it. |

### P-005: Potential Main Thread Blocking in SettingsActivity

| Field | Value |
|-------|-------|
| **ID** | P-005 |
| **Title** | Settings save operations run on main thread |
| **Screen/Flow** | SettingsActivity |
| **Severity** | Low |
| **Category** | Performance — Main Thread |
| **Description** | `saveAndReconnect()` calls `localOverrideManager.saveOverride()` (EncryptedSharedPreferences write) and `cadenceController.requestFccReconnect()` on the main thread. EncryptedSharedPreferences `commit()` is synchronous and may block for 10-50ms due to encryption. |
| **Evidence** | `SettingsActivity.kt:144-234` — no `withContext(Dispatchers.IO)` wrapping. `saveOverride` likely calls `edit().apply()` (async) or `edit().commit()` (blocking). |
| **Root Cause** | Settings save not moved to IO dispatcher. |
| **User Impact** | Brief UI freeze on save. Acceptable for infrequent operation. |
| **Recommended Fix** | Wrap save operations in `lifecycleScope.launch(Dispatchers.IO)`. |

### P-006: No ProGuard/R8 Rules for Serialization

| Field | Value |
|-------|-------|
| **ID** | P-006 |
| **Title** | Missing ProGuard keep rules for kotlinx-serialization |
| **Screen/Flow** | App-wide |
| **Severity** | Medium |
| **Category** | Reliability — Build |
| **Description** | The app uses `kotlinx-serialization` for JSON parsing across all DTO classes. If R8/ProGuard is enabled for release builds, serialization classes may be stripped or renamed, causing runtime crashes. No custom ProGuard rules file content was verified. |
| **Evidence** | `app/build.gradle.kts` references `proguard-rules.pro`. The serialization plugin should auto-generate keep rules, but this depends on the plugin version and R8 compatibility. |
| **Root Cause** | Potential misconfiguration of obfuscation rules. |
| **User Impact** | Release builds may crash on JSON deserialization if classes are obfuscated. |
| **Recommended Fix** | Verify `proguard-rules.pro` contains rules for kotlinx-serialization, Ktor, and Room entities. Test with minified release build. |

### P-007: Large Transaction Buffer Without Pagination in Local API

| Field | Value |
|-------|-------|
| **ID** | P-007 |
| **Title** | Local API transaction list may return large unpaginated responses |
| **Screen/Flow** | LocalApiServer `/api/v1/transactions` |
| **Severity** | Low |
| **Category** | Performance |
| **Description** | The local API `GET /api/v1/transactions` accepts `limit` and `offset` parameters, but there is no enforced maximum limit. A client requesting `?limit=100000` could cause a large Room query and serialization payload, potentially causing OOM on constrained devices. |
| **Evidence** | TransactionRoutes implementation accepts user-provided limit. |
| **Root Cause** | No server-side maximum limit enforcement. |
| **User Impact** | Malicious or misconfigured Odoo client could exhaust device memory. |
| **Recommended Fix** | Enforce a maximum limit (e.g., 500) regardless of client request. |

---

## 7. Test Gap Report

### 7.1 Test Coverage Summary

| Area | Test Classes | Approx. Tests | Coverage Quality |
|------|-------------|---------------|------------------|
| Buffer/Room | 4 | 71 | Good |
| Local API | 5 | 71 | Good |
| Ingestion | 1 | 74 | Excellent |
| Cloud Sync | 7 | 162 | Excellent |
| Pre-Auth | 2 | 63 | Good |
| Provisioning | 3 | 43 | Good |
| Config | 2 | 21 | Adequate |
| Connectivity | 1 | 16 | Adequate |
| Security | 2 | 101 | Excellent |
| Adapters (Radix) | 4 | 121 | Good |
| Adapters (DOMS) | 1 | 28 | Adequate |
| Offline/Stress | 4 | 43 | Good |
| WebSocket | 1 | 15 | Adequate |
| Benchmarks | 3 | 7 | Basic |
| **Total** | **~44** | **~900+** | **Good overall** |

### 7.2 Identified Gaps

### TG-001: No UI/Activity Tests

| Field | Value |
|-------|-------|
| **ID** | TG-001 |
| **Title** | Zero test coverage for all 6 Activities |
| **Severity** | High |
| **Category** | Test Gap — UI |
| **Description** | No unit tests, Robolectric tests, or instrumented tests exist for any Activity. The navigation flow (SplashActivity → LauncherActivity → conditional routing), form validation in ProvisioningActivity, and data display in DiagnosticsActivity are all untested. |
| **Evidence** | No test files in `test/` or `androidTest/` directories reference any Activity class. |
| **Recommended Fix** | Add Robolectric tests for: (a) LauncherActivity routing logic, (b) ProvisioningActivity QR parsing (partially covered by `QrPayloadParsingTest`) and form validation, (c) SettingsActivity validation logic. |

### TG-002: No Instrumented (androidTest) Tests

| Field | Value |
|-------|-------|
| **ID** | TG-002 |
| **Title** | No androidTest directory — zero on-device tests |
| **Severity** | Medium |
| **Category** | Test Gap — Integration |
| **Description** | The project has no `androidTest` directory. All tests are JVM-side with Robolectric and MockK. There are no on-device integration tests for Room migrations, EncryptedSharedPreferences, Keystore operations, or foreground service lifecycle. |
| **Evidence** | No `src/androidTest/` directory exists. |
| **Recommended Fix** | Add instrumented tests for: (a) Room migration verification (1→2→3→4→5), (b) EncryptedSharedPreferences round-trip, (c) KeystoreManager encrypt/decrypt, (d) Foreground service start/stop. |

### TG-003: No Advatec or Petronite Adapter Tests

| Field | Value |
|-------|-------|
| **ID** | TG-003 |
| **Title** | Advatec and Petronite adapters have no dedicated unit tests |
| **Severity** | Medium |
| **Category** | Test Gap — Adapter |
| **Description** | The Radix adapter has 121 tests and DOMS/JPL has 28 tests, but Advatec and Petronite adapters have zero dedicated tests. These adapters contain complex logic: OAuth client for Petronite, webhook listener for Advatec, pre-auth flows, receipt correlation. |
| **Evidence** | No `AdvatecAdapterTest`, `PetroniteAdapterTest`, `PetroniteOAuthClientTest`, or `AdvatecWebhookListenerTest` files exist. |
| **Recommended Fix** | Add tests for: (a) Advatec receipt normalization, webhook token validation, pre-auth flow. (b) Petronite OAuth token refresh, nozzle resolution, 2-step pre-auth, reconciliation. |

### TG-004: No EdgeAgentForegroundService Tests

| Field | Value |
|-------|-------|
| **ID** | TG-004 |
| **Title** | Foreground service has no tests for startup, config application, or monitoring |
| **Severity** | Medium |
| **Category** | Test Gap — Service |
| **Description** | `EdgeAgentForegroundService` is the central orchestrator of the application with complex startup logic, config hot-reload, re-provisioning monitoring, and decommission detection. None of this is tested. |
| **Evidence** | No test file references `EdgeAgentForegroundService`. |
| **Recommended Fix** | Add Robolectric service tests for: (a) startup sequence, (b) config apply/reload, (c) re-provisioning detection, (d) decommission detection, (e) `START_STICKY` restart behavior. |

### TG-005: No OdooWebSocketServer Integration Tests

| Field | Value |
|-------|-------|
| **ID** | TG-005 |
| **Title** | WebSocket server message handling has minimal test coverage |
| **Severity** | Low |
| **Category** | Test Gap — WebSocket |
| **Description** | `OdooWsModelsTest` covers model serialization (15 tests) but doesn't test actual WebSocket message routing, broadcast behavior, pump status polling, or concurrent client handling. |
| **Evidence** | Only `OdooWsModelsTest.kt` exists; no integration test with actual WebSocket connections. |
| **Recommended Fix** | Add WebSocket integration tests using Ktor test client for message routing, broadcast, and edge cases (max connections, malformed messages). |

### TG-006: No Negative/Boundary Tests for Local API Rate Limiting

| Field | Value |
|-------|-------|
| **ID** | TG-006 |
| **Title** | Local API rate limiting behavior is untested |
| **Severity** | Low |
| **Category** | Test Gap — API |
| **Description** | The local API server has rate limits (10 req/s mutating, 30 req/s read) but no tests verify rate limiting kicks in or returns correct HTTP 429 responses. |
| **Evidence** | `SecurityInputValidationTest` tests input validation but not rate limiting. |
| **Recommended Fix** | Add rate limiting tests that send rapid requests and verify 429 responses. |

---

## 8. Remediation Plan

### Priority 1 — Critical (Fix Immediately)

| ID | Finding | Effort | Action |
|----|---------|--------|--------|
| F-008 | EncryptedSharedPreferences corruption crash | 2h | Wrap all ESP access in try/catch; on corruption, delete pref file and redirect to provisioning. Consider downgrading to `security-crypto:1.0.0`. |
| S-001 | Alpha security-crypto library | 1h | Downgrade to stable `security-crypto:1.0.0` or replace with custom Keystore-based encryption (already implemented in `KeystoreManager`). |
| T-008 | Unhandled coroutine exceptions in service | 1h | Add `CoroutineExceptionHandler` to `serviceScope` in `EdgeAgentForegroundService`. |

### Priority 2 — High (Fix in Next Sprint)

| ID | Finding | Effort | Action |
|----|---------|--------|--------|
| F-001 | Double-tap registration | 30m | Add `@Volatile var isRegistering` guard at top of `performRegistration()`. |
| F-002 | Rotation destroys registration | 2h | Move registration logic to a ViewModel. Or add `android:configChanges="orientation|screenSize"` for ProvisioningActivity. |
| T-001 | Manual CoroutineScope | 1h | Replace `activityScope` / `scope` with `lifecycleScope` in all Activities. Add `lifecycle-runtime-ktx` dependency. |
| T-004 | Service re-entrant onStartCommand | 1h | Add `AtomicBoolean` guard to prevent double initialization. |
| TG-001 | No UI tests | 4h | Add Robolectric tests for LauncherActivity routing and ProvisioningActivity form validation. |
| P-001 | No HTTP timeouts | 30m | Configure explicit timeouts in `buildKtorClient()`. |

### Priority 3 — Medium (Fix in Current Release)

| ID | Finding | Effort | Action |
|----|---------|--------|--------|
| F-006 | No state persistence | 2h | Implement `onSaveInstanceState`/`onRestoreInstanceState` in ProvisioningActivity and SettingsActivity. |
| S-002 | FCC credential visible | 1h | Don't pre-populate access code field; use masked placeholder. |
| S-004 | LAN API no TLS | 4h | Document security requirements for LAN network isolation. Consider adding self-signed TLS. |
| S-005 | WebSocket no auth | 2h | Add origin validation and shared secret for WebSocket handshake. |
| T-003 | No ViewModel | 4h | Introduce ViewModel for Provisioning and Diagnostics activities. |
| T-007 | No destructive migration fallback | 30m | Add `.fallbackToDestructiveMigration()` to Room builder. |
| P-006 | ProGuard rules verification | 1h | Verify and test minified release build. |
| TG-003 | No Advatec/Petronite tests | 4h | Add adapter unit tests. |
| TG-004 | No service tests | 3h | Add foreground service lifecycle tests. |

### Priority 4 — Low (Schedule for Future Sprint)

| ID | Finding | Effort | Action |
|----|---------|--------|--------|
| F-003 | Share logs silent failure | 30m | Add Toast feedback. |
| F-004 | System back inconsistency | 30m | Add `OnBackPressedCallback` in ProvisioningActivity. |
| F-005 | Splash double navigation | 30m | Store Handler, cancel in `onDestroy`. |
| F-007 | No re-provision from decommissioned | 2h | Add "Clear & Re-provision" button with confirmation. |
| T-005 | Handler callback leak | 15m | Store and cancel Handler. |
| T-006 | targetSdk 34 vs 35 | 2h | Update targetSdk and verify compatibility. |
| T-002 | Sequential IO in diagnostics | 1h | Batch DAO calls. |
| S-006 | Optional certificate pinning | 2h | Bundle default pins in APK. |
| S-008 | SHA-1 in Radix | N/A | Protocol constraint — document risk. |
| P-003 | UI jank on refresh | 1h | Reuse views instead of recreating. |
| P-004 | Generic notification icon | 30m | Create custom icon. |
| P-005 | Main thread blocking in Settings | 30m | Move to IO dispatcher. |
| P-007 | Unbounded local API limit | 30m | Enforce max limit. |
| TG-002 | No instrumented tests | 8h | Add androidTest suite. |
| TG-005 | WebSocket integration tests | 3h | Add WS integration tests. |
| TG-006 | Rate limiting tests | 1h | Add rate limit tests. |

---

### Summary Statistics

| Category | Critical | High | Medium | Low | Total |
|----------|----------|------|--------|-----|-------|
| Functional Bugs | 1 | 2 | 2 | 3 | 8 |
| Technical Issues | 1 | 2 | 3 | 2 | 8 |
| Security Issues | 1 | 0 | 4 | 3 | 8 |
| Performance Issues | 0 | 1 | 1 | 5 | 7 |
| Test Gaps | 0 | 1 | 3 | 2 | 6 |
| **Total** | **3** | **6** | **13** | **15** | **37** |

### Estimated Total Remediation Effort

| Priority | Findings | Effort |
|----------|----------|--------|
| P1 — Critical | 3 | ~4 hours |
| P2 — High | 7 | ~10 hours |
| P3 — Medium | 10 | ~22 hours |
| P4 — Low | 17 | ~22 hours |
| **Total** | **37** | **~58 hours** |

---

*End of Wave 3 Android Edge Agent Audit Report*
