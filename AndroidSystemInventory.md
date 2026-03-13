# Android System Inventory — FCC Edge Agent

**Project**: `fcc-edge-agent`
**Package**: `com.fccmiddleware.edge`
**Source**: `src/edge-agent/`
**Last scanned**: 2026-03-13

---

## 1. Application Architecture

| Aspect | Detail |
|--------|--------|
| **Architecture pattern** | Service-oriented with layered separation (Adapter → Ingestion → Buffer → Sync). No MVVM/MVI — headless agent with minimal UI. |
| **Module structure** | Single Gradle module (`:app`). No feature modules. |
| **Dependency injection** | **Koin** 4.0.1 (`io.insert-koin:koin-android`) |
| **Navigation framework** | None (no Jetpack Navigation). Manual `Intent`-based Activity routing in `LauncherActivity`. |
| **UI framework** | Android Views (no Jetpack Compose, no Fragments). All screens are standalone Activities. |
| **Networking (cloud)** | **Ktor Client** 3.0.3 with OkHttp engine + kotlinx.serialization JSON |
| **Networking (local API)** | **Ktor Server CIO** 3.0.3 — embedded HTTP server on port 8585 |
| **Persistence** | **Room** 2.6.1 (KSP) with WAL mode. Schema version 5, 10 entities. |
| **Serialization** | `kotlinx-serialization-json` 1.7.3 |
| **Security** | `androidx.security:security-crypto` 1.1.0-alpha06, Android Keystore AES-256-GCM |
| **QR scanning** | ZXing Android Embedded 4.3.0 |
| **Static analysis** | Detekt 1.23.7 |
| **Language** | 100% Kotlin 2.1.0 (zero Java files) |
| **Build tooling** | AGP 8.13.2, Gradle 9.2.1, KSP 2.1.0-1.0.29 |
| **SDK targets** | `compileSdk` 35 · `minSdk` 31 · `targetSdk` 34 |
| **JVM target** | Java 17 |

---

## 2. Package Structure

```
com.fccmiddleware.edge/
├── FccEdgeApplication.kt              # Application subclass — Koin init
├── adapter/
│   ├── advatec/                        # Advatec EFD fiscal adapter
│   │   ├── AdvatecAdapter.kt
│   │   ├── AdvatecFiscalizationService.kt
│   │   ├── AdvatecProtocolDtos.kt
│   │   └── AdvatecWebhookListener.kt
│   ├── common/                         # Adapter contracts & shared types
│   │   ├── AdapterTimeouts.kt
│   │   ├── AdapterTypes.kt
│   │   ├── CanonicalTransaction.kt
│   │   ├── Enums.kt
│   │   ├── FccAdapterFactory.kt
│   │   ├── FccVendorSupportMatrix.kt
│   │   ├── IFccAdapter.kt
│   │   ├── IFccAdapterFactory.kt
│   │   ├── IFccConnectionLifecycle.kt
│   │   ├── IFccEventListener.kt
│   │   ├── IFiscalizationService.kt
│   │   ├── PreAuthRecord.kt
│   │   ├── PumpStatus.kt
│   │   └── PumpStatusSynthesizer.kt
│   ├── doms/                           # DOMS FCC adapter (TCP/JPL)
│   │   ├── DomsJplAdapter.kt
│   │   ├── jpl/
│   │   │   ├── JplFrameCodec.kt
│   │   │   ├── JplHeartbeatManager.kt
│   │   │   ├── JplMessage.kt
│   │   │   └── JplTcpClient.kt
│   │   ├── mapping/
│   │   │   └── DomsCanonicalMapper.kt
│   │   ├── model/
│   │   │   ├── DomsFpMainState.kt
│   │   │   ├── DomsSupParam.kt
│   │   │   └── DomsTransactionDto.kt
│   │   └── protocol/
│   │       ├── DomsLogonHandler.kt
│   │       ├── DomsPreAuthHandler.kt
│   │       ├── DomsPumpStatusParser.kt
│   │       ├── DomsSupParamParser.kt
│   │       └── DomsTransactionParser.kt
│   ├── petronite/                      # Petronite REST + OAuth2 adapter
│   │   ├── PetroniteAdapter.kt
│   │   ├── PetroniteNozzleResolver.kt
│   │   ├── PetroniteOAuthClient.kt
│   │   └── PetroniteProtocolDtos.kt
│   └── radix/                          # Radix TCP adapter
│       ├── RadixAdapter.kt
│       ├── RadixProtocolDtos.kt
│       ├── RadixPushListener.kt
│       ├── RadixSignatureHelper.kt
│       ├── RadixXmlBuilder.kt
│       └── RadixXmlParser.kt
├── api/                                # Embedded Ktor local REST API
│   ├── ApiModels.kt
│   ├── LocalApiServer.kt
│   ├── PreAuthRoutes.kt
│   ├── PumpStatusRoutes.kt
│   ├── StatusRoutes.kt
│   └── TransactionRoutes.kt
├── buffer/                             # Room persistence layer
│   ├── BufferDatabase.kt
│   ├── CleanupWorker.kt
│   ├── IntegrityChecker.kt
│   ├── TransactionBufferManager.kt
│   ├── dao/
│   │   ├── AgentConfigDao.kt
│   │   ├── AuditLogDao.kt
│   │   ├── NozzleDao.kt
│   │   ├── PreAuthDao.kt
│   │   ├── SiteDataDao.kt
│   │   ├── SyncStateDao.kt
│   │   └── TransactionBufferDao.kt
│   └── entity/
│       ├── AgentConfig.kt
│       ├── AuditLog.kt
│       ├── BufferedTransaction.kt
│       ├── LocalNozzle.kt
│       ├── LocalProduct.kt
│       ├── LocalPump.kt
│       ├── Nozzle.kt
│       ├── PreAuthRecord.kt
│       ├── SiteInfo.kt
│       └── SyncState.kt
├── config/                             # Configuration management
│   ├── CloudEnvironments.kt
│   ├── ConfigManager.kt
│   ├── EdgeAgentConfigDto.kt
│   ├── LocalOverrideManager.kt
│   └── SiteDataManager.kt
├── connectivity/                       # Network binding & connectivity probes
│   ├── BoundSocketFactory.kt
│   ├── ConnectivityManager.kt
│   └── NetworkBinder.kt
├── di/                                 # Koin DI module
│   └── AppModule.kt
├── ingestion/                          # FCC transaction ingestion orchestration
│   └── IngestionOrchestrator.kt
├── logging/                            # Structured JSONL logging
│   ├── AppLogger.kt
│   ├── LogLevel.kt
│   └── StructuredFileLogger.kt
├── preauth/                            # Pre-authorization handler
│   └── PreAuthHandler.kt
├── runtime/                            # Runtime state & cadence loop
│   ├── CadenceController.kt
│   └── FccRuntimeState.kt
├── security/                           # Keystore, EncryptedPrefs, PII filtering
│   ├── EncryptedPrefsManager.kt
│   ├── KeystoreManager.kt
│   ├── Sensitive.kt
│   └── SensitiveFieldFilter.kt
├── service/                            # Foreground service & boot receiver
│   ├── BootReceiver.kt
│   └── EdgeAgentForegroundService.kt
├── sync/                               # Cloud sync workers
│   ├── CircuitBreaker.kt
│   ├── CloudApiClient.kt
│   ├── CloudApiModels.kt
│   ├── CloudUploadWorker.kt
│   ├── ConfigPollWorker.kt
│   ├── DeviceTokenProvider.kt
│   ├── KeystoreDeviceTokenProvider.kt
│   ├── PreAuthCloudForwardWorker.kt
│   └── TelemetryReporter.kt
├── ui/                                 # Activities (minimal UI)
│   ├── DecommissionedActivity.kt
│   ├── DiagnosticsActivity.kt
│   ├── LauncherActivity.kt
│   ├── ProvisioningActivity.kt
│   ├── SettingsActivity.kt
│   └── SplashActivity.kt
└── websocket/                          # Odoo POS WebSocket server
    ├── OdooWebSocketServer.kt
    ├── OdooWsMessageHandler.kt
    └── OdooWsModels.kt
```

---

## 3. Dependency Injection (Koin)

All dependencies are wired in `di/AppModule.kt`. Key singletons:

| Component | Scope | Type |
|-----------|-------|------|
| `BufferDatabase` | Singleton | Room DB |
| `TransactionBufferDao` | Singleton | DAO |
| `PreAuthDao` | Singleton | DAO |
| `NozzleDao` | Singleton | DAO |
| `SyncStateDao` | Singleton | DAO |
| `AgentConfigDao` | Singleton | DAO |
| `AuditLogDao` | Singleton | DAO |
| `SiteDataDao` | Singleton | DAO |
| `TransactionBufferManager` | Singleton | Buffer logic |
| `EncryptedPrefsManager` | Singleton | Secure storage |
| `KeystoreManager` | Singleton | Key management |
| `KeystoreDeviceTokenProvider` | Singleton | Token management |
| `ConfigManager` | Singleton | Config state |
| `SiteDataManager` | Singleton | Site data state |
| `LocalOverrideManager` | Singleton | FCC override config |
| `ConnectivityManager` | Singleton | Network state machine |
| `NetworkBinder` | Singleton | Network binding |
| `FccAdapterFactory` | Singleton | Adapter resolution |
| `FccRuntimeState` | Singleton | Runtime adapter state |
| `IngestionOrchestrator` | Singleton | FCC polling coordinator |
| `PreAuthHandler` | Singleton | Pre-auth lifecycle |
| `CloudApiClient` | Singleton | Cloud HTTP client |
| `CloudUploadWorker` | Singleton | Upload sync |
| `ConfigPollWorker` | Singleton | Config polling |
| `PreAuthCloudForwardWorker` | Singleton | Pre-auth sync |
| `TelemetryReporter` | Singleton | Health metrics |
| `CleanupWorker` | Singleton | Data retention |
| `IntegrityChecker` | Singleton | DB integrity |
| `CadenceController` | Singleton | Main event loop |
| `LocalApiServer` | Singleton | Ktor embedded server |
| `OdooWebSocketServer` | Singleton | WebSocket server |
| `StructuredFileLogger` | Singleton | JSONL file logger |

---

## 4. State Management

This is a **headless agent** — there are no ViewModels, LiveData, or UI state objects. State is managed through:

| Mechanism | Location | Purpose |
|-----------|----------|---------|
| `StateFlow<EdgeAgentConfigDto?>` | `ConfigManager.config` | Current site configuration |
| `FccRuntimeState` | `runtime/` | Current FCC adapter + config binding |
| `ConnectivityState` enum | `ConnectivityManager` | 4-state network state machine |
| `CircuitBreaker` | `sync/` | Per-worker failure tracking with exponential backoff |
| `SyncState` Room entity | `buffer/entity/` | Cloud sync cursor persisted to SQLite |
| `EncryptedPrefsManager` | `security/` | Registration state, tokens, decommission flags |
| `PumpStatusCache` | `api/PumpStatusRoutes.kt` | In-memory pump status with single-flight protection |

---

## 5. Data Layer

### 5.1 Room Database (`BufferDatabase`)

| Entity | Table | Purpose |
|--------|-------|---------|
| `BufferedTransaction` | `buffered_transactions` | FCC dispense transactions (offline buffer) |
| `PreAuthRecord` | `pre_auth_records` | Pre-authorization lifecycle records |
| `Nozzle` | `nozzles` | Odoo ↔ FCC pump/nozzle mapping |
| `SyncState` | `sync_state` | Single-row cloud sync cursor |
| `AgentConfig` | `agent_config` | Single-row cached site config JSON |
| `AuditLog` | `audit_log` | Local diagnostic audit trail |
| `SiteInfo` | `site_info` | Site identity & FCC metadata |
| `LocalProduct` | `local_products` | FCC ↔ canonical product mapping |
| `LocalPump` | `local_pumps` | Odoo ↔ FCC pump mapping |
| `LocalNozzle` | `local_nozzles` | Odoo ↔ FCC nozzle mapping |

Schema version: **5** (migrations 1→2, 2→3, 3→4, 4→5 defined)
WAL mode enabled.

### 5.2 DAOs

| DAO | Entity(ies) | Key operations |
|-----|-------------|----------------|
| `TransactionBufferDao` | `BufferedTransaction` | Insert, getPendingForUpload, getForLocalApi, markBatchUploaded, countByStatus, dead-letter, revert, fiscalization retry |
| `PreAuthDao` | `PreAuthRecord` | Insert, getByOdooOrderId, getUnsynced, updateStatus, markCloudSynced, getExpiring, deleteTerminal |
| `NozzleDao` | `Nozzle` | replaceAll, resolveForPreAuth, resolveByFcc, getAll |
| `SyncStateDao` | `SyncState` | get, upsert, incrementAndGetTelemetrySequence |
| `AgentConfigDao` | `AgentConfig` | get, upsert |
| `AuditLogDao` | `AuditLog` | insert, getRecent, deleteOlderThan |
| `SiteDataDao` | `SiteInfo`, `LocalProduct`, `LocalPump`, `LocalNozzle` | CRUD for all site master data, replaceAllSiteData |

### 5.3 Secure Storage

| Store | Technology | Content |
|-------|-----------|---------|
| `EncryptedPrefsManager` | `EncryptedSharedPreferences` (AES-256-SIV keys, AES-256-GCM values) | Registration state, device ID, site code, cloud base URL, FCC host/port, decommission flag, reprovisioning flag, certificate pins |
| `KeystoreManager` | Android Keystore (AES-256-GCM) | Device JWT access token, refresh token encryption |

---

## 6. Networking

### 6.1 Cloud API Client (`CloudApiClient`)

| Method | HTTP | Cloud Endpoint | Purpose |
|--------|------|---------------|---------|
| `register` | POST | `/api/v1/agent/register` | Device registration with bootstrap token |
| `uploadTransactions` | POST | `/api/v1/transactions/upload` | Batch transaction upload (max 500) |
| `getConfig` | GET | `/api/v1/agent/config` | Poll for site configuration updates |
| `refreshToken` | POST | `/api/v1/agent/token/refresh` | JWT access token refresh |
| `reportTelemetry` | POST | `/api/v1/agent/telemetry` | Device health metrics |
| `forwardPreAuth` | POST | `/api/v1/preauth/forward` | Pre-auth cloud forwarding |
| `checkVersion` | GET | `/api/v1/agent/version-check` | Agent version compatibility |

Client: Ktor Client + OkHttp engine with certificate pinning (`CertificatePinner`).
Auth: Bearer JWT (`DeviceTokenProvider`).

### 6.2 Local API Server (Ktor CIO — port 8585)

| Route | Method | Purpose |
|-------|--------|---------|
| `/api/v1/status` | GET | Agent health & connectivity |
| `/api/v1/transactions` | GET | List buffered transactions (filter: pumpNumber, since, limit, offset) |
| `/api/v1/transactions/{id}` | GET | Single buffered transaction |
| `/api/v1/transactions/acknowledge` | POST | Odoo POS batch acknowledge |
| `/api/v1/transactions/pull` | POST | On-demand FCC pull |
| `/api/v1/preauth` | POST | Submit pre-auth to FCC |
| `/api/v1/preauth/cancel` | POST | Cancel active pre-auth |
| `/api/v1/pump-status` | GET | Live/stale pump status |

Auth: Localhost bypass; LAN requires `X-Api-Key` header. Rate limiting plugin included.

### 6.3 FCC Adapters (LAN Protocols)

| Adapter | Vendor | Protocol | Transport |
|---------|--------|----------|-----------|
| `DomsJplAdapter` | DOMS | JPL binary frames | TCP socket (persistent) |
| `RadixAdapter` | Radix | XML over TCP | TCP socket (stateless per request) |
| `PetroniteAdapter` | Petronite | REST + OAuth2 | HTTP/HTTPS |
| `AdvatecAdapter` | Advatec | REST + webhook callbacks | HTTP (localhost) |

---

## 7. Background Processing

| Component | Type | Purpose |
|-----------|------|---------|
| `EdgeAgentForegroundService` | `Service` (foreground, `dataSync`) | Main agent lifecycle — starts all workers, adapters, servers |
| `BootReceiver` | `BroadcastReceiver` (`BOOT_COMPLETED`) | Auto-start service after device reboot |
| `CadenceController` | Coroutine loop (in service scope) | Main event loop — FCC polling, cloud sync, config poll, telemetry, cleanup, version check |
| `CloudUploadWorker` | Coroutine worker | Batched transaction upload to cloud |
| `ConfigPollWorker` | Coroutine worker | Periodic config poll from cloud |
| `PreAuthCloudForwardWorker` | Coroutine worker | Forward pre-auth records to cloud |
| `TelemetryReporter` | Coroutine worker | Device health metrics reporting |
| `CleanupWorker` | Coroutine worker | Retention-based + quota-based data cleanup |
| `IntegrityChecker` | Startup task | SQLite `PRAGMA integrity_check` on startup |
| `RadixPushListener` | TCP listener coroutine | Unsolicited Radix push messages |
| `AdvatecWebhookListener` | HTTP listener coroutine | Advatec EFD receipt webhook callbacks |
| `JplHeartbeatManager` | Timer coroutine | DOMS TCP heartbeat keep-alive |

Note: The project does **not** use `WorkManager`, `JobScheduler`, or `AlarmManager`. All background work is coroutine-based within the foreground service scope.

---

## 8. Integrations

| Integration | Technology | Details |
|-------------|-----------|---------|
| **QR Code Scanning** | ZXing Android Embedded 4.3.0 | Device provisioning via QR code in `ProvisioningActivity` |
| **Android Keystore** | `java.security.KeyStore` + AES-256-GCM | Token encryption, credential protection |
| **EncryptedSharedPreferences** | `androidx.security:security-crypto` | Secure key-value storage for registration state |
| **Camera** | `android.permission.CAMERA` | QR scanning (not required — `android.hardware.camera required=false`) |
| **Network Binding** | `android.net.ConnectivityManager` + `Network.bindSocket()` | WiFi-bound FCC traffic, mobile-bound cloud traffic |
| **FileProvider** | `androidx.core.content.FileProvider` | Sharing log zip files via Android share sheet |
| **Certificate Pinning** | OkHttp `CertificatePinner` | Cloud API HTTPS pinning |

**Not used**: Firebase, Analytics, Push Notifications, Maps, Payments, Google Play Services.

---

## 9. Testing Infrastructure

| Category | Framework | Location |
|----------|-----------|----------|
| Unit tests | JUnit 5 + JUnit 4 (vintage) | `app/src/test/` |
| Mocking | MockK 1.13.13 | All unit tests |
| Coroutine testing | `kotlinx-coroutines-test` 1.9.0 | Async tests |
| Room testing | `room-testing` 2.6.1 | Migration tests |
| Android testing | Robolectric 4.14.1, `androidx.test:core` | Android context tests |
| HTTP testing | Ktor `server-test-host` + `client-mock` | API route tests |
| Instrumented | `androidx.test.ext:junit`, `test:runner` | Android instrumentation |
| Benchmark | Custom JUnit tests | `test/benchmark/` |
| Fixtures | XML files | `test/resources/fixtures/` (auth, transaction, products) |

**Test packages**: adapter, api, benchmark, buffer, config, connectivity, ingestion, offline, preauth, provisioning, runtime, security, sync, websocket.

---

## 10. Permissions

| Permission | Purpose |
|------------|---------|
| `INTERNET` | Cloud API + FCC LAN communication |
| `ACCESS_NETWORK_STATE` | Connectivity probes |
| `ACCESS_WIFI_STATE` | WiFi network binding |
| `CHANGE_NETWORK_STATE` | Network binding control |
| `FOREGROUND_SERVICE` | Persistent foreground service |
| `FOREGROUND_SERVICE_DATA_SYNC` | Foreground service type |
| `RECEIVE_BOOT_COMPLETED` | Auto-start after reboot |
| `CAMERA` | QR code scanning (optional) |

---

## 11. Build Configuration

| Setting | Value |
|---------|-------|
| `applicationId` | `com.fccmiddleware.edge` |
| `versionCode` | 1 |
| `versionName` | `1.0.0` |
| `minifyEnabled` (release) | `true` |
| ProGuard | `proguard-android-optimize.txt` + `proguard-rules.pro` |
| Room schema export | `app/schemas/` |
| KSP room options | `generateKotlin=true`, `incremental=true` |
