# Module Clusters — FCC Edge Agent (Android)

**Project**: `fcc-edge-agent`
**Source**: `src/edge-agent/`
**Last updated**: 2026-03-13

---

## Architecture Note

This is a **headless service-oriented agent**, not a consumer app. Key differences from a typical Android app:

- **No ViewModels** — state is managed through singleton managers and `StateFlow`
- **No UseCases** — business logic lives in Workers, Handlers, and Orchestrators
- **No Repositories** — data access uses Room DAOs + Manager singletons
- **6 Activities total** — minimal UI for provisioning and diagnostics only

The columns below are adapted accordingly:

| Column | Maps to |
|--------|---------|
| Screens | Activities |
| State Managers | Singletons that own domain state (equivalent to ViewModels + Repositories) |
| Workers | Background coroutine workers and listeners (equivalent to UseCases) |
| APIs | Cloud endpoints, local REST routes, FCC protocol adapters, WebSocket |
| DB Tables | Room entities |

---

## Summary

| # | Module | Screens | State Managers | Workers | APIs | DB Tables | Key Files |
|---|--------|---------|---------------|---------|------|-----------|-----------|
| 1 | Provisioning & Lifecycle | 4 | 3 | 0 | 3 | 1 | 14 |
| 2 | FCC Adapters | 0 | 2 | 3 | 4 | 0 | 27 |
| 3 | Transaction Management | 0 | 2 | 1 | 5 | 1 | 8 |
| 4 | Pre-Authorization | 0 | 1 | 1 | 3 | 1 | 5 |
| 5 | Site Configuration | 1 | 3 | 1 | 1 | 6 | 10 |
| 6 | Cloud Sync & Telemetry | 0 | 2 | 2 | 1 | 1 | 7 |
| 7 | Diagnostics & Monitoring | 1 | 2 | 1 | 2 | 1 | 7 |
| 8 | POS Integration (Odoo) | 0 | 1 | 0 | 1 | 1 | 3 |
| 9 | Connectivity | 0 | 3 | 0 | 0 | 0 | 3 |
| 10 | Security | 0 | 3 | 0 | 0 | 0 | 4 |
| **Total** | | **6** | **22** | **9** | **20** | **10** | **88** |

> Note: Some components are shared across modules (e.g., `EncryptedPrefsManager` appears in both Provisioning and Security). Counts reflect primary ownership.

---

## Detailed Module Breakdown

### 1. Provisioning & Lifecycle

Device onboarding, registration, token management, and decommission flow.

| Layer | Components |
|-------|-----------|
| **Screens** | `SplashActivity`, `LauncherActivity`, `ProvisioningActivity`, `DecommissionedActivity` |
| **State Managers** | `EncryptedPrefsManager`, `KeystoreManager`, `KeystoreDeviceTokenProvider` |
| **Workers** | — |
| **Cloud APIs** | `POST /api/v1/agent/register`, `POST /api/v1/agent/token/refresh`, `GET /api/v1/agent/version-check` |
| **Local APIs** | — |
| **DB Tables** | `agent_config` |

**Packages**: `ui/` (Splash, Launcher, Provisioning, Decommissioned), `security/`, `sync/DeviceTokenProvider`

**Navigation flow**:
```
SplashActivity → LauncherActivity → ProvisioningActivity → DiagnosticsActivity
                                  → DecommissionedActivity (terminal)
```

---

### 2. FCC Adapters

Communication with fuel controller consoles across four vendor protocols. Core domain logic for polling transactions, pre-auth commands, and pump status from physical hardware.

| Layer | Components |
|-------|-----------|
| **Screens** | — |
| **State Managers** | `FccAdapterFactory`, `FccRuntimeState` |
| **Workers** | `RadixPushListener`, `AdvatecWebhookListener`, `JplHeartbeatManager` |
| **FCC Protocols** | DOMS (JPL/TCP), Radix (XML/TCP), Petronite (REST+OAuth2), Advatec (REST+Webhook) |
| **DB Tables** | — |

**Packages**: `adapter/common/`, `adapter/doms/`, `adapter/radix/`, `adapter/petronite/`, `adapter/advatec/`

| Adapter | Vendor | Protocol | Transport | Files |
|---------|--------|----------|-----------|-------|
| `DomsJplAdapter` | DOMS | JPL binary frames | TCP (persistent) | 11 |
| `RadixAdapter` | Radix | XML messages | TCP (stateless) | 6 |
| `PetroniteAdapter` | Petronite | REST + OAuth2 | HTTP/HTTPS | 4 |
| `AdvatecAdapter` | Advatec | REST + webhooks | HTTP (localhost) | 4 |

**Shared contracts** (in `adapter/common/`):
`IFccAdapter`, `IFccAdapterFactory`, `IFccConnectionLifecycle`, `IFccEventListener`, `IFiscalizationService`, `CanonicalTransaction`, `PumpStatus`, `PumpStatusSynthesizer`, `PreAuthRecord`, `AdapterTimeouts`, `AdapterTypes`, `Enums`, `FccVendorSupportMatrix`

---

### 3. Transaction Management

Capture FCC dispense transactions, buffer them locally in SQLite, expose them to POS via local API, and upload to cloud in batches.

| Layer | Components |
|-------|-----------|
| **Screens** | — |
| **State Managers** | `TransactionBufferManager`, `IngestionOrchestrator` |
| **Workers** | `CloudUploadWorker` |
| **Cloud APIs** | `POST /api/v1/transactions/upload` |
| **Local APIs** | `GET /api/v1/transactions`, `GET /api/v1/transactions/{id}`, `POST /api/v1/transactions/acknowledge`, `POST /api/v1/transactions/pull` |
| **DB Tables** | `buffered_transactions` |
| **DAOs** | `TransactionBufferDao` |

**Packages**: `ingestion/`, `buffer/TransactionBufferManager`, `sync/CloudUploadWorker`, `api/TransactionRoutes`

**Data flow**:
```
FCC Adapter → IngestionOrchestrator → TransactionBufferManager → BufferedTransaction (Room)
                                                                        │
                                                          ┌─────────────┼──────────────┐
                                                          ▼             ▼              ▼
                                                   Local API      CloudUpload    Odoo WebSocket
                                                   (POS pull)     (batch sync)   (push notify)
```

---

### 4. Pre-Authorization

Manage fuel pre-authorization lifecycle: receive requests from POS, send to FCC, track status, and forward results to cloud.

| Layer | Components |
|-------|-----------|
| **Screens** | — |
| **State Managers** | `PreAuthHandler` |
| **Workers** | `PreAuthCloudForwardWorker` |
| **Cloud APIs** | `POST /api/v1/preauth/forward` |
| **Local APIs** | `POST /api/v1/preauth`, `POST /api/v1/preauth/cancel` |
| **DB Tables** | `pre_auth_records` |
| **DAOs** | `PreAuthDao` |

**Packages**: `preauth/`, `sync/PreAuthCloudForwardWorker`, `api/PreAuthRoutes`

**Lifecycle**:
```
POS (local API) → PreAuthHandler → FCC Adapter → PreAuthRecord (Room) → Cloud Forward
```

---

### 5. Site Configuration

Manage site identity, FCC connection settings, product/pump/nozzle mappings, and local overrides. Periodically poll cloud for config updates.

| Layer | Components |
|-------|-----------|
| **Screens** | `SettingsActivity` |
| **State Managers** | `ConfigManager`, `SiteDataManager`, `LocalOverrideManager` |
| **Workers** | `ConfigPollWorker` |
| **Cloud APIs** | `GET /api/v1/agent/config` |
| **Local APIs** | — |
| **DB Tables** | `agent_config`, `site_info`, `local_products`, `local_pumps`, `local_nozzles`, `nozzles` |
| **DAOs** | `AgentConfigDao`, `SiteDataDao`, `NozzleDao` |

**Packages**: `config/`, `ui/SettingsActivity`, `sync/ConfigPollWorker`, `buffer/dao/SiteDataDao`, `buffer/dao/NozzleDao`

**Config sources** (priority order):
```
1. LocalOverrideManager (user-set FCC host/port in SettingsActivity)
2. ConfigManager (cloud-polled EdgeAgentConfigDto)
3. Defaults (compiled fallbacks)
```

---

### 6. Cloud Sync & Telemetry

Cross-cutting cloud communication infrastructure: HTTP client, auth, circuit breaker, telemetry reporting, and data retention cleanup.

| Layer | Components |
|-------|-----------|
| **Screens** | — |
| **State Managers** | `CloudApiClient`, `CircuitBreaker` |
| **Workers** | `TelemetryReporter`, `CleanupWorker` |
| **Cloud APIs** | `POST /api/v1/agent/telemetry` |
| **Local APIs** | — |
| **DB Tables** | `sync_state` |
| **DAOs** | `SyncStateDao` |

**Packages**: `sync/`, `buffer/CleanupWorker`

> `CloudApiClient` is a shared dependency consumed by modules 1, 3, 4, and 5. Telemetry and sync-state tracking are owned here.

---

### 7. Diagnostics & Monitoring

Operational dashboard, structured logging, audit trail, database integrity checks, and live pump/agent status.

| Layer | Components |
|-------|-----------|
| **Screens** | `DiagnosticsActivity` |
| **State Managers** | `StructuredFileLogger`, `AppLogger` |
| **Workers** | `IntegrityChecker` |
| **Cloud APIs** | — |
| **Local APIs** | `GET /api/v1/status`, `GET /api/v1/pump-status` |
| **DB Tables** | `audit_log` |
| **DAOs** | `AuditLogDao` |

**Packages**: `logging/`, `ui/DiagnosticsActivity`, `api/StatusRoutes`, `api/PumpStatusRoutes`, `buffer/IntegrityChecker`

---

### 8. POS Integration (Odoo)

WebSocket server for Odoo POS real-time communication. Pushes transaction notifications and handles nozzle resolution for POS displays.

| Layer | Components |
|-------|-----------|
| **Screens** | — |
| **State Managers** | `OdooWebSocketServer` |
| **Workers** | — |
| **Cloud APIs** | — |
| **Local APIs** | WebSocket server (embedded, port varies) |
| **DB Tables** | `nozzles` (shared with Site Configuration) |
| **DAOs** | `NozzleDao` (shared) |

**Packages**: `websocket/`

**Files**: `OdooWebSocketServer.kt`, `OdooWsMessageHandler.kt`, `OdooWsModels.kt`

---

### 9. Connectivity

Network state machine, dual-network binding (WiFi for FCC, mobile for cloud), and socket factory for adapter transports.

| Layer | Components |
|-------|-----------|
| **Screens** | — |
| **State Managers** | `ConnectivityManager`, `NetworkBinder`, `BoundSocketFactory` |
| **Workers** | — |
| **APIs** | — |
| **DB Tables** | — |

**Packages**: `connectivity/`

**State machine**: `Unknown → Checking → Connected → Disconnected → Checking → ...`

---

### 10. Security

Credential storage, key management, PII filtering, and certificate pinning. Foundation layer consumed by Provisioning, Cloud Sync, and Adapter modules.

| Layer | Components |
|-------|-----------|
| **Screens** | — |
| **State Managers** | `EncryptedPrefsManager`, `KeystoreManager`, `SensitiveFieldFilter` |
| **Workers** | — |
| **APIs** | — |
| **DB Tables** | — |

**Packages**: `security/`

**Files**: `EncryptedPrefsManager.kt`, `KeystoreManager.kt`, `Sensitive.kt`, `SensitiveFieldFilter.kt`

---

## Cross-Cutting Infrastructure

These components span all modules and are not clustered into a single business feature:

| Component | Package | Consumed by |
|-----------|---------|-------------|
| `EdgeAgentForegroundService` | `service/` | All modules (lifecycle host) |
| `BootReceiver` | `service/` | Provisioning & Lifecycle |
| `CadenceController` | `runtime/` | Transactions, Pre-Auth, Cloud Sync, Config, Diagnostics |
| `LocalApiServer` | `api/` | Transactions, Pre-Auth, Diagnostics |
| `AppModule` (Koin) | `di/` | All modules |
| `FccEdgeApplication` | root | All modules |

---

## Module Dependency Graph

```
                    ┌──────────────────┐
                    │    Security       │
                    │  (Keystore, Enc)  │
                    └────────┬─────────┘
                             │
              ┌──────────────┼──────────────┐
              ▼              ▼              ▼
   ┌──────────────┐ ┌───────────────┐ ┌──────────────┐
   │ Provisioning │ │  Cloud Sync   │ │ Connectivity │
   │ & Lifecycle  │ │ & Telemetry   │ │              │
   └──────┬───────┘ └───────┬───────┘ └──────┬───────┘
          │                 │                │
          │          ┌──────┴──────┐         │
          │          ▼             ▼         │
          │  ┌──────────────┐ ┌────────┐    │
          │  │  Site Config  │ │  Diag  │    │
          │  └──────┬───────┘ └────────┘    │
          │         │                       │
          │         ▼                       │
          │  ┌──────────────┐               │
          └─►│ FCC Adapters │◄──────────────┘
             └──────┬───────┘
                    │
          ┌─────────┼─────────┐
          ▼                   ▼
   ┌──────────────┐   ┌──────────────┐
   │ Transactions │   │  Pre-Auth    │
   └──────┬───────┘   └──────────────┘
          │
          ▼
   ┌──────────────┐
   │POS Integration│
   │   (Odoo WS)  │
   └──────────────┘
```

---

## Database Table Ownership

| Table | Owner Module | Shared with |
|-------|-------------|-------------|
| `buffered_transactions` | Transaction Management | — |
| `pre_auth_records` | Pre-Authorization | — |
| `nozzles` | Site Configuration | POS Integration |
| `sync_state` | Cloud Sync & Telemetry | — |
| `agent_config` | Site Configuration | Provisioning |
| `audit_log` | Diagnostics & Monitoring | — |
| `site_info` | Site Configuration | — |
| `local_products` | Site Configuration | — |
| `local_pumps` | Site Configuration | — |
| `local_nozzles` | Site Configuration | — |
