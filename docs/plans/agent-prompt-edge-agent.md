# Edge Agent — Agent System Prompt

**Use this prompt as context when assigning ANY Edge Agent task to an AI coding agent.**

---

## You Are Working On

The **Forecourt Middleware Edge Agent** — a native Android application (Kotlin) that runs on Urovo i9100 HHT (Handheld Terminal) devices alongside Odoo POS. It bridges the cloud middleware and the station-local Forecourt Controller (FCC) over WiFi LAN.

## What This System Does

1. **Polls the FCC** over station WiFi LAN to fetch fuel dispensing transactions
2. **Buffers transactions locally** in SQLite (Room) when internet is unavailable
3. **Uploads buffered transactions** to the cloud backend in chronological batches when connectivity returns
4. **Relays pre-authorization commands** from Odoo POS to the FCC over LAN (always via LAN, never cloud)
5. **Exposes a local REST API** (Ktor on localhost:8585) for Odoo POS to query transactions and submit pre-auths
6. **Monitors connectivity** — dual-probe system checking both internet (cloud) and FCC (LAN) independently
7. **Reports telemetry** to cloud — battery, storage, buffer depth, FCC heartbeat, sync status
8. **Self-registers** with cloud via QR code bootstrap token scanned during provisioning
9. **Receives configuration** from cloud and applies it at runtime (hot-reload where possible)

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Language | Kotlin (Android) |
| Target | Android 12+ (API 31+), Urovo i9100 HHT |
| Database | SQLite via Room ORM (WAL mode) |
| HTTP Server | Ktor (CIO engine) on localhost:8585 |
| HTTP Client | Ktor Client with OkHttp engine |
| DI | Koin |
| Coroutines | kotlinx.coroutines (Dispatchers.IO for network/DB) |
| Security | Android Keystore, EncryptedSharedPreferences, OkHttp CertificatePinner |
| Testing | JUnit 5, MockK, Room in-memory DB, Robolectric |

## Project Structure (Single :app module)

```
com.fccmiddleware.edge/
├── api/                    # Ktor local REST API routes
│   ├── TransactionRoutes.kt
│   ├── PreAuthRoutes.kt
│   ├── PumpStatusRoutes.kt
│   └── StatusRoutes.kt
├── adapter/                # FCC vendor adapters
│   ├── common/             # IFccAdapter interface, shared types
│   └── doms/               # DOMS-specific implementation
├── buffer/                 # Room database, entities, DAOs
│   ├── BufferDatabase.kt
│   ├── entity/             # BufferedTransaction, PreAuthRecord, SyncState, AgentConfig, AuditLog
│   └── dao/                # TransactionBufferDao, PreAuthDao, SyncStateDao
├── sync/                   # Cloud synchronization
│   ├── CloudUploadWorker.kt
│   ├── StatusPollWorker.kt
│   ├── ConfigPollWorker.kt
│   └── TelemetryReporter.kt
├── connectivity/           # Connectivity state machine
│   └── ConnectivityManager.kt
├── preauth/                # Pre-auth handler
│   └── PreAuthHandler.kt
├── ingestion/              # Ingestion orchestrator
│   └── IngestionOrchestrator.kt
├── config/                 # Configuration management
│   └── ConfigManager.kt
├── security/               # Keystore, encrypted prefs, cert pinning
├── service/                # Foreground service, boot receiver
├── ui/                     # Diagnostics screen, provisioning UI
└── di/                     # Koin modules
```

## Key Architecture Rules

1. **Offline-first**: The agent MUST function when internet is down. LAN operations (FCC poll, pre-auth, local API) never depend on cloud.
2. **No transaction left behind**: Every transaction polled from FCC is buffered locally. Upload failures are retried. Replay is in chronological order (oldest first).
3. **SQLite WAL mode**: Always enabled. Required for crash resilience on Android.
4. **Foreground service**: The agent runs as a `START_STICKY` foreground service with persistent notification. It must survive Doze mode and battery optimization.
5. **Coroutine scoping**: Use structured concurrency. Workers use `SupervisorJob` scopes. Never use `GlobalScope`.
6. **Currency**: `Long` minor units (cents). NEVER floating point for money.
7. **Dates**: UTC ISO 8601 strings in SQLite (`TEXT` columns). `Instant` or `OffsetDateTime` in Kotlin.
8. **IDs**: UUID v4 strings for middleware-generated IDs. Preserve FCC IDs as opaque strings.
9. **Logging**: NEVER log sensitive fields (FCC credentials, tokens, customer TIN). Use `@Sensitive` annotation.
10. **Room entities**: All timestamps as `TEXT` (ISO 8601 UTC). Booleans as `INTEGER` (0/1). UUIDs as `TEXT`.

## Must-Read Artifacts (Before ANY Task)

| Artifact | Path | What It Tells You |
|----------|------|-------------------|
| Edge Agent HLD | `WIP-HLD-Edge-Agent.md` | Architecture, all flows, operating modes |
| Canonical Transaction Schema | `schemas/canonical/canonical-transaction.schema.json` | Transaction field contract |
| Pre-Auth Record Schema | `schemas/canonical/pre-auth-record.schema.json` | Pre-auth lifecycle model |
| Pump Status Schema | `schemas/canonical/pump-status.schema.json` | Pump state model |
| Telemetry Schema | `schemas/canonical/telemetry-payload.schema.json` | Health metrics structure |
| Edge Local API Spec | `schemas/openapi/edge-agent-local-api.yaml` | All local REST endpoints |
| Edge Room Schema | `db/ddl/002-edge-room-schema.sql` | SQLite table definitions |
| Edge Agent Config Schema | `schemas/config/edge-agent-config.schema.json` | Runtime configuration model |
| Site Config Schema | `schemas/config/site-config.schema.json` | Full site configuration |
| State Machines | `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` | Edge sync, connectivity, pre-auth state machines |
| FCC Adapter Contracts | `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md` | IFccAdapter interface for Kotlin |
| Database Schema Design | `docs/specs/data-models/tier-1-4-database-schema-design.md` | Edge Room entities, DAOs, indexes |
| Security Plan | `docs/specs/security/tier-2-5-security-implementation-plan.md` | Android Keystore, EncryptedSharedPreferences, LAN API key |
| Device Registration Spec | `docs/specs/data-models/tier-1-1-device-registration-spec.md` | QR bootstrap, registration flow |

## Connectivity States

| State | Internet | FCC LAN | Behaviour |
|-------|----------|---------|-----------|
| FULLY_ONLINE | UP | UP | Normal: poll FCC, upload to cloud, sync status, report telemetry |
| INTERNET_DOWN | DOWN | UP | Poll FCC, buffer locally, pre-auth via LAN, local API serves full buffer |
| FCC_UNREACHABLE | UP | DOWN | Can't poll FCC, but upload existing buffer, sync status from cloud |
| FULLY_OFFLINE | DOWN | DOWN | Local API serves stale buffer only, alert supervisor |

State transitions use **3 consecutive failures** for DOWN, **1 success** for UP recovery.

## Edge Sync Record States

```
PENDING → UPLOADED → SYNCED_TO_ODOO → ARCHIVED → (deleted)
```

Upload is in `created_at ASC` order. Never skip past a failed record.

## Ingestion Modes (from site config)

| Mode | Behaviour |
|------|-----------|
| CLOUD_DIRECT | FCC pushes directly to cloud. Agent is safety-net LAN poller (catch-up only). |
| RELAY | Agent is primary receiver. Polls FCC, buffers, uploads to cloud. |
| BUFFER_ALWAYS | Agent always buffers locally first, then uploads. |

## Testing Standards

- Domain logic: JUnit 5 with MockK
- Room DAOs: Room in-memory database tests
- Ktor routes: Ktor test application
- Connectivity manager: Unit tests with mocked probes
- Integration: Robolectric for Android framework tests
