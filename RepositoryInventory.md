# Repository & Data Layer Inventory — FCC Edge Agent (Android)

**Project**: `fcc-edge-agent`
**Last scanned**: 2026-03-13

---

## Architecture Note

This project does **not** follow the traditional Android Repository pattern. There are no `Repository` classes. Instead, the data layer is organised as:

- **DAOs** — Room database access (7 DAOs)
- **Managers** — Business logic over DAOs (TransactionBufferManager, ConfigManager, SiteDataManager)
- **Workers** — Cloud sync orchestration (CloudUploadWorker, ConfigPollWorker, etc.)
- **Providers** — Abstracted credential/token access (DeviceTokenProvider)
- **Secure storage** — EncryptedPrefsManager, KeystoreManager

---

## 1. Data Access Objects (Room DAOs)

| DAO | Entity | Table | Key Operations |
|-----|--------|-------|----------------|
| `TransactionBufferDao` | `BufferedTransaction` | `buffered_transactions` | Insert, getPendingForUpload (batch by limit), getForLocalApi (exclude SYNCED_TO_ODOO), markBatchUploaded, updateSyncStatus, archiveOldSynced, countByStatus, dead-letter, revertStaleUploaded, fiscalization retry, WebSocket queries, cross-adapter duplicate detection |
| `PreAuthDao` | `PreAuthRecord` | `pre_auth_records` | Insert (IGNORE dedup), getByOdooOrderId, getUnsynced (for cloud forward), updateStatus, markCloudSynced, getExpiring, deleteTerminal, markCancelledAndUnsync, markExpiredAndUnsync |
| `NozzleDao` | `Nozzle` | `nozzles` | replaceAll (transactional delete+insert), resolveForPreAuth (by odoo pump+nozzle), resolveByFcc (by fcc pump+nozzle), getAll |
| `SyncStateDao` | `SyncState` | `sync_state` | get, upsert (single-row), incrementAndGetTelemetrySequence (transactional) |
| `AgentConfigDao` | `AgentConfig` | `agent_config` | get, upsert (single-row cached config JSON) |
| `AuditLogDao` | `AuditLog` | `audit_log` | insert, getRecent (limit), deleteOlderThan |
| `SiteDataDao` | `SiteInfo`, `LocalProduct`, `LocalPump`, `LocalNozzle` | `site_info`, `local_products`, `local_pumps`, `local_nozzles` | Full CRUD for site master data, replaceAllSiteData (transactional bulk replace) |

---

## 2. Data Managers (Business Logic Layer)

| Manager | Location | Responsibility | Data Sources |
|---------|----------|---------------|--------------|
| `TransactionBufferManager` | `buffer/` | Transaction lifecycle: buffer with dedup, upload batching, status transitions (PENDING → UPLOADED → SYNCED_TO_ODOO → ARCHIVED), emergency cleanup, dead-letter | `TransactionBufferDao` |
| `ConfigManager` | `config/` | Site configuration lifecycle: load from Room, apply from cloud, validate, emit `StateFlow<EdgeAgentConfigDto?>` | `AgentConfigDao`, `EncryptedPrefsManager` |
| `SiteDataManager` | `config/` | Site master data (pumps, nozzles, products): apply from config poll response, persist to Room | `SiteDataDao` |
| `LocalOverrideManager` | `config/` | FCC host/port local overrides (technician-configured) | `EncryptedPrefsManager` |
| `IngestionOrchestrator` | `ingestion/` | FCC polling coordination: fetch → normalize → dedup → buffer → fiscalization retry | `TransactionBufferManager`, `IFccAdapter`, `NozzleDao` |
| `PreAuthHandler` | `preauth/` | Pre-auth lifecycle: request dedup, nozzle resolution, FCC call, expiry, cancel, cloud forward trigger | `PreAuthDao`, `NozzleDao`, `IFccAdapter` |

---

## 3. Cloud Sync Workers

| Worker | Location | Responsibility | Data Sources |
|--------|----------|---------------|--------------|
| `CloudUploadWorker` | `sync/` | Batch upload of PENDING transactions to cloud, handle duplicates/rejections, retry with circuit breaker, decommission detection | `TransactionBufferManager`, `CloudApiClient`, `DeviceTokenProvider`, `CircuitBreaker` |
| `ConfigPollWorker` | `sync/` | Periodic config fetch from cloud, apply config changes, trigger site data updates | `ConfigManager`, `SiteDataManager`, `CloudApiClient` |
| `PreAuthCloudForwardWorker` | `sync/` | Forward unsynced pre-auth records to cloud backend | `PreAuthDao`, `CloudApiClient` |
| `TelemetryReporter` | `sync/` | Assemble and report device health metrics (battery, disk, connectivity, buffer depth, uptime) | `TransactionBufferDao`, `SyncStateDao`, `ConnectivityManager`, `CloudApiClient` |
| `CleanupWorker` | `buffer/` | Retention-based + quota-based cleanup of old transactions, pre-auths, audit logs, and log files | `TransactionBufferDao`, `PreAuthDao`, `AuditLogDao` |

---

## 4. Secure Storage Providers

| Provider | Location | Technology | Stored Data |
|----------|----------|-----------|-------------|
| `EncryptedPrefsManager` | `security/` | `EncryptedSharedPreferences` (AES-256-SIV master key, AES-256-GCM values) | `deviceId`, `siteCode`, `isRegistered`, `isDecommissioned`, `isReprovisioningRequired`, `cloudBaseUrl`, `fccHost`, `fccPort`, `certificatePins`, FCC override values |
| `KeystoreManager` | `security/` | Android Keystore + AES-256-GCM | Encrypted access token, encrypted refresh token |
| `KeystoreDeviceTokenProvider` | `sync/` | Wraps KeystoreManager + EncryptedPrefsManager | Token decrypt/refresh, legal entity ID extraction from JWT claims, decommission/reprovisioning state |

---

## 5. Network Data Sources

| Source | Location | Protocol | Purpose |
|--------|----------|----------|---------|
| `CloudApiClient` | `sync/` | HTTPS (Ktor Client + OkHttp) | All cloud backend communication |
| `DomsJplAdapter` → `JplTcpClient` | `adapter/doms/` | TCP (binary JPL frames) | DOMS FCC persistent connection |
| `RadixAdapter` | `adapter/radix/` | TCP (XML) | Radix FCC stateless requests |
| `RadixPushListener` | `adapter/radix/` | TCP listener | Radix unsolicited transaction push |
| `PetroniteAdapter` → `PetroniteOAuthClient` | `adapter/petronite/` | HTTPS (REST + OAuth2) | Petronite FCC REST API |
| `AdvatecAdapter` | `adapter/advatec/` | HTTP (localhost) | Advatec EFD REST API |
| `AdvatecWebhookListener` | `adapter/advatec/` | HTTP listener | Advatec receipt webhook callbacks |

---

## 6. Caching Layers

| Cache | Location | Strategy | TTL |
|-------|----------|----------|-----|
| `AgentConfig` (Room) | `buffer/dao/AgentConfigDao` | Write-through (cloud poll → Room → in-memory StateFlow) | Persisted until replaced |
| `PumpStatusCache` | `api/PumpStatusRoutes.kt` | In-memory with single-flight protection | Configurable `liveTimeoutMs` (default 1s) |
| `SyncState` (Room) | `buffer/dao/SyncStateDao` | Single-row sync cursor | Persisted until replaced |
| `PetroniteOAuthClient` token cache | `adapter/petronite/` | In-memory OAuth2 access token | Token expiry time |
| `IPreAuthMatcher` correlation map | Per-adapter in-memory | In-memory active pre-auth tracking | 30 min TTL (`PRE_AUTH_TTL_MILLIS`) |

---

## 7. Data Flow Summary

```
FCC Hardware (LAN)
    │
    ▼
IFccAdapter.fetchTransactions() / push listener
    │
    ▼
IngestionOrchestrator (normalize → dedup → buffer)
    │
    ▼
TransactionBufferManager → TransactionBufferDao → Room DB (SQLite/WAL)
    │                                                    │
    │                                                    ▼
    │                                              LocalApiServer (Ktor)
    │                                              OdooWebSocketServer
    │                                                    │
    │                                                    ▼
    │                                              Odoo POS (LAN client)
    ▼
CloudUploadWorker → CloudApiClient → Cloud Backend (HTTPS)
```
