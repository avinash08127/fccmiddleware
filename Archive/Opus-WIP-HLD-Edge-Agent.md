# Forecourt Middleware — Edge Agent High Level Design

**Status:** WIP (Work in Progress)
**Version:** 0.1
**Date:** 2026-03-10
**Author:** Architecture Review — Opus

---

# 1. Overview

## 1.1 Purpose

The Edge Agent is a native Android application that runs on the same Urovo i9100 HHT (Handheld Terminal) as Odoo POS. It bridges the gap between the cloud-based middleware and the station-local Forecourt Controller (FCC) by maintaining direct LAN communication with the FCC, buffering transactions when internet is unavailable, relaying pre-authorization commands from Odoo POS to the FCC, and exposing a local REST API for Odoo POS to consume during offline periods.

The Edge Agent ensures that **no transaction is lost** during internet outages — fuel continues to be dispensed, transactions are captured, and everything synchronizes when connectivity returns.

## 1.2 Business Context

In African fuel retail, station-level internet connectivity is unreliable. SIM data drops, WiFi hotspots fail, power outages reset routers. However, the FCC sits on a station LAN that **remains available independently** of internet connectivity. The Edge Agent exploits this by maintaining continuous LAN communication with the FCC regardless of internet state.

The agent runs on the HHT the attendant already carries — no additional hardware is required. It coexists with Odoo POS and provides a localhost API that Odoo POS uses for pre-auth commands (always) and transaction queries (when offline).

## 1.3 Major Responsibilities

| Responsibility | Description |
|----------------|-------------|
| FCC LAN Communication | Maintain connection to FCC over station WiFi LAN. Heartbeat monitoring. |
| LAN Catch-Up Poll | Poll FCC at configured interval as safety-net ingestion. Normalize via embedded adapter. |
| Pre-Auth Relay | Receive pre-auth from Odoo POS → send to FCC over LAN → return result. Always available. |
| Pre-Auth Cloud Queue | Forward pre-auth records to cloud for reconciliation. Retry when offline. |
| Offline Transaction Buffering | Store transactions in local SQLite when cloud is unreachable. |
| Automatic Replay | Upload buffered transactions to cloud on reconnection. Batched. Idempotent. |
| SYNCED_TO_ODOO Status Sync | Poll cloud for SYNCED_TO_ODOO status. Filter local API results accordingly. |
| Local REST API | Expose localhost API for Odoo POS: transactions, pump status, pre-auth, health. |
| Multi-HHT LAN API | Expose same API on LAN IP for non-primary HHTs at multi-HHT sites. API key protected. |
| Connectivity Detection | Continuous monitoring of internet and FCC LAN. Automatic mode switching. |
| Telemetry Reporting | Report health metrics to cloud: FCC status, buffer depth, battery, storage, version. |
| Configuration Sync | Pull configuration updates from cloud on each sync cycle. |
| Local Diagnostics | On-device screen for Site Supervisor: FCC status, buffer depth, logs. |

## 1.4 Boundaries and Exclusions

- The Edge Agent does **not** create orders in Odoo. Odoo POS polls the agent's local API and creates orders itself.
- The Edge Agent does **not** manage Odoo POS state or configuration. They are separate apps on the same device.
- The Edge Agent does **not** perform reconciliation — that is the cloud backend's responsibility.
- The Edge Agent does **not** support automatic primary agent failover (post-MVP).
- OTA update management is handled by Sure MDM, not the agent itself.

---

# 2. Design Goals

| Goal | Rationale |
|------|-----------|
| **Offline-First** | The agent assumes internet will fail. All LAN operations (pre-auth, FCC polling) never depend on internet. Cloud sync is opportunistic. |
| **Zero Transaction Loss** | Every transaction captured from the FCC must eventually reach the cloud. The local buffer is the guarantee. |
| **LAN Independence** | Station LAN is always on. The agent's core value proposition depends entirely on LAN availability, not internet. |
| **Minimal Resource Footprint** | Runs alongside Odoo POS on an Urovo i9100 (Android 12). Must not degrade Odoo POS performance. Efficient memory, CPU, battery, and storage usage. |
| **Configuration-Driven** | FCC vendor, connection details, ingestion mode, poll interval, buffer settings — all configurable from the cloud without APK updates. |
| **Self-Contained FCC Adapter** | The adapter logic (DOMS, Radix, etc.) runs on-device. No cloud dependency for FCC protocol handling. |
| **Secure on Device** | FCC credentials encrypted in Android Keystore. Device tokens for cloud auth. API key for LAN access from other HHTs. |
| **Simple Provisioning** | QR code scan for field deployment. Minimizes manual configuration in remote African fuel stations. |

---

# 3. Functional Scope

## 3.1 Key Features

1. **FCC Adapter Engine** — Embedded protocol handlers for FCC vendors (DOMS for MVP). Translate between vendor wire format and canonical model on-device.
2. **LAN Catch-Up Poller** — Background worker that polls FCC over LAN at configurable intervals. Produces canonical transactions.
3. **Pre-Auth Handler** — Receives pre-auth requests from Odoo POS via localhost API. Sends authorization command to FCC over LAN. Returns result. Queues record to cloud.
4. **Transaction Buffer** — SQLite-based store-and-forward. WAL mode. Crash-resilient. 30,000+ transaction capacity.
5. **Cloud Sync Engine** — Uploads buffered transactions. Polls SYNCED_TO_ODOO status. Sends telemetry. Pulls config updates.
6. **Local REST API** — Ktor-based HTTP server on localhost:8585. Transaction queries, pump status, pre-auth, health.
7. **LAN API (Multi-HHT)** — Same API exposed on LAN IP for non-primary HHTs. API key authenticated.
8. **Connectivity Manager** — Monitors internet (cloud health ping) and FCC LAN (heartbeat). Drives mode transitions.
9. **Provisioning** — QR code scan, manual entry, or cloud push for initial and ongoing configuration.
10. **Diagnostics UI** — On-device screen for Site Supervisor with connection status, buffer depth, and manual pull.

## 3.2 Supported Operating Modes

| Mode | Internet | FCC LAN | Agent Behaviour |
|------|----------|---------|-----------------|
| **Fully Online** | Up | Up | Polls FCC over LAN. Forwards catch-up to cloud immediately. Pre-auth via LAN. Syncs SYNCED_TO_ODOO. Reports telemetry. |
| **Internet Down** | Down | Up | Polls FCC over LAN. Buffers locally. Pre-auth via LAN (unaffected). Exposes local API for Odoo POS. Queues pre-auth records for later cloud upload. |
| **FCC Unreachable** | Up/Down | Down | Cannot poll FCC. Alerts Site Supervisor. Existing buffer accessible via local API. Cloud sync continues for previously buffered items. |
| **Fully Offline** | Down | Down | No ingestion possible. Existing buffer accessible. Alerts on recovery. |

## 3.3 Ingestion Mode Behaviour

The agent's role varies based on the `ingestionMode` configured per FCC:

| Ingestion Mode | Agent Role in Transaction Ingestion |
|----------------|--------------------------------------|
| **CLOUD_DIRECT** (default) | Safety-net LAN poller. FCC pushes directly to cloud. Agent catches missed transactions and uploads. |
| **RELAY** | Primary receiver. FCC delivers to agent. Agent relays to cloud in real-time if online; buffers if not. |
| **BUFFER_ALWAYS** | Primary receiver. Agent always buffers locally first. Syncs to cloud on schedule. |

Pre-auth always routes via the agent regardless of ingestion mode.

---

# 4. Architecture Overview

## 4.1 Recommended Architecture Style

**Layered Android Application** with clean separation between:
- **Presentation** — Diagnostics UI, provisioning screens
- **API** — Ktor embedded HTTP server (local REST API)
- **Application** — Use cases, orchestration, state management
- **Domain** — Canonical models, adapter interfaces, business rules
- **Infrastructure** — SQLite (Room), network clients, Android services, security

## 4.2 Component Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    EDGE AGENT (Android App)                  │
│                                                             │
│  ┌─────────────────────────────────────────────────────┐    │
│  │                PRESENTATION LAYER                    │    │
│  │  ┌──────────────┐  ┌───────────────────────────┐    │    │
│  │  │ Diagnostics  │  │ Provisioning Screen       │    │    │
│  │  │ Screen       │  │ (QR Scan / Manual Entry)  │    │    │
│  │  └──────────────┘  └───────────────────────────┘    │    │
│  └─────────────────────────────────────────────────────┘    │
│                                                             │
│  ┌─────────────────────────────────────────────────────┐    │
│  │                   API LAYER (Ktor)                   │    │
│  │  localhost:8585 (+ LAN IP for multi-HHT)            │    │
│  │                                                      │    │
│  │  GET /api/transactions                               │    │
│  │  GET /api/transactions/{id}                          │    │
│  │  GET /api/pump-status                                │    │
│  │  POST /api/preauth                                   │    │
│  │  POST /api/preauth/{id}/cancel                       │    │
│  │  POST /api/transactions/acknowledge                  │    │
│  │  GET /api/status                                     │    │
│  └──────────────────────┬──────────────────────────────┘    │
│                         │                                    │
│  ┌──────────────────────┴──────────────────────────────┐    │
│  │                APPLICATION LAYER                     │    │
│  │                                                      │    │
│  │  ┌──────────────┐ ┌─────────────┐ ┌──────────────┐  │    │
│  │  │ Ingestion    │ │ Pre-Auth    │ │ Cloud Sync   │  │    │
│  │  │ Orchestrator │ │ Handler     │ │ Engine       │  │    │
│  │  └──────┬───────┘ └──────┬──────┘ └──────┬───────┘  │    │
│  │         │                │               │           │    │
│  │  ┌──────┴──────┐ ┌──────┴──────┐ ┌──────┴───────┐   │    │
│  │  │ Connectivity│ │ Config      │ │ Telemetry    │   │    │
│  │  │ Manager     │ │ Manager     │ │ Reporter     │   │    │
│  │  └─────────────┘ └─────────────┘ └──────────────┘   │    │
│  └─────────────────────────────────────────────────────┘    │
│                                                             │
│  ┌─────────────────────────────────────────────────────┐    │
│  │                  DOMAIN LAYER                        │    │
│  │                                                      │    │
│  │  ┌───────────────┐  ┌────────────────────────────┐   │    │
│  │  │ Canonical     │  │ IFccAdapter interface       │   │    │
│  │  │ Transaction   │  │                             │   │    │
│  │  │ Model         │  │ normalize(raw) → canonical  │   │    │
│  │  │               │  │ sendPreAuth(cmd) → result   │   │    │
│  │  │ PreAuth Model │  │ getPumpStatus() → status[]  │   │    │
│  │  │               │  │ heartbeat() → bool          │   │    │
│  │  │ Buffer States │  │ fetchTransactions(cursor)   │   │    │
│  │  └───────────────┘  └────────────────────────────┘   │    │
│  └─────────────────────────────────────────────────────┘    │
│                                                             │
│  ┌─────────────────────────────────────────────────────┐    │
│  │               INFRASTRUCTURE LAYER                   │    │
│  │                                                      │    │
│  │  ┌──────────────┐  ┌─────────────┐  ┌────────────┐  │    │
│  │  │ SQLite /     │  │ FCC Network │  │ Cloud HTTP  │  │    │
│  │  │ Room DB      │  │ Client      │  │ Client      │  │    │
│  │  │ (WAL mode)   │  │ (LAN)       │  │ (Internet)  │  │    │
│  │  └──────────────┘  └─────────────┘  └────────────┘  │    │
│  │                                                      │    │
│  │  ┌──────────────┐  ┌─────────────┐  ┌────────────┐  │    │
│  │  │ Android      │  │ QR Code     │  │ Android    │  │    │
│  │  │ Keystore     │  │ Scanner     │  │ Services   │  │    │
│  │  │ (Crypto)     │  │ (Camera)    │  │ (Foreground│  │    │
│  │  │              │  │             │  │  Service)  │  │    │
│  │  └──────────────┘  └─────────────┘  └────────────┘  │    │
│  └─────────────────────────────────────────────────────┘    │
│                                                             │
│  ┌─────────────────────────────────────────────────────┐    │
│  │                FCC ADAPTERS                          │    │
│  │                                                      │    │
│  │  ┌──────────────┐  ┌─────────────┐  ┌────────────┐  │    │
│  │  │ DOMS Adapter │  │ Radix       │  │ Advatec    │  │    │
│  │  │ (MVP)        │  │ (Phase 3)   │  │ (Phase 3)  │  │    │
│  │  └──────────────┘  └─────────────┘  └────────────┘  │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

## 4.3 Interaction with External Systems

```
                          STATION LAN (always on)
    ┌─────────────────────────────────────────────────┐
    │                                                 │
    │  [FCC]  ◄──── LAN poll / pre-auth ────► [Edge Agent]
    │                                              │
    │                                    localhost:8585
    │                                              │
    │                                        [Odoo POS]
    │                                              │
    └──────────────────────────────────────────────┘
                           │
                    INTERNET (SIM / WiFi)
                    (may be unavailable)
                           │
               ┌───────────┴───────────┐
               │                       │
          [Cloud Middleware]       [Odoo Cloud]
               │
    Upload, pre-auth fwd,
    status sync, telemetry,
    config pull
```

## 4.4 Key Runtime Flows

### LAN Catch-Up Poll (CLOUD_DIRECT mode)

```
[Connectivity Manager] → internet = UP, FCC LAN = UP

Every {pullIntervalSeconds}:
  [FCC Poll Worker] → adapter.fetchTransactions(cursor)
    → [FCC] responds with transactions since cursor
    → adapter.normalize() → canonical transactions
    → try cloud upload:
      → SUCCESS: update cursor, done
      → FAIL (internet down): write to SQLite buffer (status=PENDING)
```

### Pre-Auth Flow

```
[Odoo POS] → POST localhost:8585/api/preauth
  → [Pre-Auth Handler] validates request
  → adapter.sendPreAuth(pump, amount, TIN, ...)
    → [FCC] authorizes pump
    → [FCC] returns authorization response
  → store pre-auth locally
  → return response to Odoo POS (AUTHORIZED / FAILED)
  → async: queue pre-auth record to cloud (retry if offline)
```

### Offline Buffer and Replay

```
Phase 1: Internet DOWN
  [FCC Poll Worker] → gets transactions
    → cloud upload FAILS → write to SQLite (status=PENDING)

  [Odoo POS] → polls GET /api/transactions
    → returns PENDING transactions from buffer
    → excludes SYNCED_TO_ODOO entries

Phase 2: Internet RESTORED
  [Connectivity Manager] detects cloud reachable
  [Replay Worker] activates:
    → SELECT * FROM buffer WHERE status='PENDING' ORDER BY timestamp ASC
    → batch upload (50 per request) to cloud
    → cloud responds per-transaction (created/skipped)
    → update status to SYNCED
    → continue until all PENDING uploaded

  [SYNCED_TO_ODOO Sync] activates:
    → GET /api/v1/transactions/synced-status from cloud
    → update local entries to SYNCED_TO_ODOO
    → these entries no longer returned by local API
```

### SYNCED_TO_ODOO Flow

```
Every ~30 seconds (when internet is available):
  [Cloud Sync Engine] → GET cloud/transactions/synced-status?since={lastCheck}
    → receives list of SYNCED_TO_ODOO transaction IDs
    → UPDATE buffer SET status='SYNCED_TO_ODOO' WHERE fccTransactionId IN (...)
    → GET /api/transactions now EXCLUDES these
    → after retention period (7 days), DELETE old SYNCED_TO_ODOO entries
```

---

# 5. Project Structure Recommendation

## 5.1 Repository Strategy

**Separate repository** from the cloud backend. The Edge Agent is a Kotlin/Java Android project with its own build, test, and release lifecycle. It shares the canonical model definition (documented, not as a code dependency) with the cloud backend.

## 5.2 Recommended Project Structure

```
fcc-edge-agent/
│
├── app/
│   ├── src/
│   │   ├── main/
│   │   │   ├── java/com/fccmiddleware/edge/
│   │   │   │   │
│   │   │   │   ├── FccEdgeApplication.kt              # Application class
│   │   │   │   │
│   │   │   │   ├── adapter/                            # FCC Adapter Layer
│   │   │   │   │   ├── FccAdapter.kt                   # Interface: IFccAdapter
│   │   │   │   │   ├── FccAdapterFactory.kt            # Resolves adapter by vendor
│   │   │   │   │   ├── doms/                           # DOMS adapter (MVP)
│   │   │   │   │   │   ├── DomsAdapter.kt
│   │   │   │   │   │   ├── DomsProtocolClient.kt       # DOMS wire protocol (REST/TCP)
│   │   │   │   │   │   ├── DomsPayloadParser.kt
│   │   │   │   │   │   └── DomsFieldMapping.kt
│   │   │   │   │   ├── radix/                          # Phase 3
│   │   │   │   │   └── common/
│   │   │   │   │       └── CanonicalTransaction.kt     # Canonical model
│   │   │   │   │
│   │   │   │   ├── api/                                # Local REST API (Ktor)
│   │   │   │   │   ├── LocalApiServer.kt               # Ktor server setup
│   │   │   │   │   ├── routes/
│   │   │   │   │   │   ├── TransactionRoutes.kt
│   │   │   │   │   │   ├── PreAuthRoutes.kt
│   │   │   │   │   │   ├── PumpStatusRoutes.kt
│   │   │   │   │   │   └── StatusRoutes.kt
│   │   │   │   │   ├── auth/
│   │   │   │   │   │   └── ApiKeyAuth.kt               # API key validation for LAN access
│   │   │   │   │   └── dto/                            # Request/response models
│   │   │   │   │
│   │   │   │   ├── buffer/                             # Transaction Buffer
│   │   │   │   │   ├── TransactionBufferDao.kt         # Room DAO
│   │   │   │   │   ├── BufferedTransaction.kt          # Room entity
│   │   │   │   │   ├── PreAuthRecord.kt                # Room entity
│   │   │   │   │   ├── BufferDatabase.kt               # Room database (WAL mode)
│   │   │   │   │   └── BufferIntegrityChecker.kt       # PRAGMA integrity_check on startup
│   │   │   │   │
│   │   │   │   ├── cloud/                              # Cloud Communication
│   │   │   │   │   ├── CloudApiClient.kt               # HTTP client to cloud middleware
│   │   │   │   │   ├── TransactionUploader.kt          # Batch upload logic
│   │   │   │   │   ├── PreAuthForwarder.kt             # Queue and forward pre-auth records
│   │   │   │   │   ├── SyncedStatusPoller.kt           # SYNCED_TO_ODOO status sync
│   │   │   │   │   ├── ConfigPoller.kt                 # Remote config updates
│   │   │   │   │   └── TelemetryReporter.kt            # Health metric reporting
│   │   │   │   │
│   │   │   │   ├── connectivity/                       # Connectivity Management
│   │   │   │   │   ├── ConnectivityManager.kt          # Monitors internet + FCC LAN
│   │   │   │   │   ├── ConnectivityState.kt            # State: FULLY_ONLINE, INTERNET_DOWN, etc.
│   │   │   │   │   └── ModeTransitionLogger.kt         # Audit logging for mode changes
│   │   │   │   │
│   │   │   │   ├── ingestion/                          # FCC Polling / Ingestion
│   │   │   │   │   ├── FccPollWorker.kt                # Periodic LAN poll worker
│   │   │   │   │   ├── IngestionOrchestrator.kt        # Routes based on ingestionMode
│   │   │   │   │   └── CursorTracker.kt                # Tracks last fetched transaction
│   │   │   │   │
│   │   │   │   ├── preauth/                            # Pre-Auth Handling
│   │   │   │   │   ├── PreAuthHandler.kt               # Orchestrates pre-auth flow
│   │   │   │   │   ├── PreAuthStateMachine.kt          # State transitions
│   │   │   │   │   └── PreAuthExpiryWorker.kt          # Timeout handling
│   │   │   │   │
│   │   │   │   ├── config/                             # Configuration
│   │   │   │   │   ├── AgentConfig.kt                  # Configuration data class
│   │   │   │   │   ├── ConfigStore.kt                  # SharedPreferences / encrypted storage
│   │   │   │   │   └── QrCodeProvisioner.kt            # QR code scan and parse
│   │   │   │   │
│   │   │   │   ├── security/                           # Security
│   │   │   │   │   ├── KeystoreManager.kt              # Android Keystore operations
│   │   │   │   │   ├── DeviceTokenManager.kt           # Cloud auth token management
│   │   │   │   │   └── CredentialEncryptor.kt          # FCC credential encryption
│   │   │   │   │
│   │   │   │   ├── service/                            # Android Services
│   │   │   │   │   ├── EdgeAgentForegroundService.kt   # Long-running foreground service
│   │   │   │   │   └── BootReceiver.kt                 # Auto-start on device boot
│   │   │   │   │
│   │   │   │   └── ui/                                 # UI Screens
│   │   │   │       ├── diagnostics/                    # Diagnostics screen
│   │   │   │       ├── provisioning/                   # Setup / QR scan screen
│   │   │   │       └── main/                           # Main status screen
│   │   │   │
│   │   │   └── res/                                    # Android resources
│   │   │
│   │   ├── test/                                       # Unit tests
│   │   │   └── java/com/fccmiddleware/edge/
│   │   │       ├── adapter/doms/                       # DOMS adapter tests
│   │   │       ├── buffer/                             # Buffer tests
│   │   │       ├── ingestion/                          # Ingestion tests
│   │   │       └── preauth/                            # Pre-auth tests
│   │   │
│   │   └── androidTest/                                # Instrumented tests
│   │       └── java/com/fccmiddleware/edge/
│   │           ├── api/                                # Local API integration tests
│   │           └── buffer/                             # SQLite integration tests
│   │
│   └── build.gradle.kts
│
├── gradle/
├── build.gradle.kts                                    # Root build
├── settings.gradle.kts
│
├── docs/
│   ├── canonical-model.md                              # Shared canonical model reference
│   └── adapter-guide.md                                # How to implement a new FCC adapter
│
└── tools/
    └── fcc-simulator/                                  # DOMS FCC simulator for development/testing
```

## 5.3 Design Rationale

| Decision | Rationale |
|----------|-----------|
| Single Android module (not multi-module) | The Edge Agent is a single app with cohesive responsibilities. Multi-module adds complexity without clear benefit at this scale. Package structure provides sufficient separation. |
| Ktor for embedded HTTP server | Lightweight, Kotlin-native, coroutine-based. Ideal for embedding in an Android app. Lower resource footprint than alternatives. |
| Room for SQLite | Android's recommended persistence library. Compile-time SQL verification. Built-in support for LiveData/Flow. WAL mode configurable. |
| FCC adapter as an internal package | Adapters are not separate deployable units on Android. They're code modules within the same APK. The FccAdapterFactory pattern allows runtime selection by vendor. |
| Foreground Service | Android requires a foreground service for long-running background work (FCC polling, cloud sync). Ensures the OS does not kill the agent. |
| FCC Simulator tool | Essential for development and testing. Developers cannot test against real FCCs during development. Simulator mimics DOMS protocol. |

---

# 6. Integration View

## 6.1 Integration Points

| Integration | Direction | Protocol | Network | Auth |
|-------------|-----------|----------|---------|------|
| FCC → Edge Agent (poll) | Agent → FCC | Vendor-specific (REST/TCP) | Station LAN (WiFi) | FCC credentials (encrypted in Keystore) |
| FCC ← Edge Agent (pre-auth) | Agent → FCC | Vendor-specific | Station LAN | FCC credentials |
| Edge Agent → Cloud (upload) | Agent → Cloud | HTTPS REST | Internet (SIM/WiFi) | Device token (signed JWT) |
| Edge Agent → Cloud (pre-auth fwd) | Agent → Cloud | HTTPS REST | Internet | Device token |
| Edge Agent → Cloud (telemetry) | Agent → Cloud | HTTPS REST | Internet | Device token |
| Cloud → Edge Agent (config) | Agent polls Cloud | HTTPS REST | Internet | Device token |
| Cloud → Edge Agent (SYNCED_TO_ODOO) | Agent polls Cloud | HTTPS REST | Internet | Device token |
| Odoo POS → Edge Agent (local API) | Odoo → Agent | HTTP REST | Localhost (same device) | None (localhost) or API key (LAN) |
| Non-primary HHT → Edge Agent (LAN API) | HHT → Agent | HTTP REST | Station LAN (WiFi) | API key |

## 6.2 Cloud API Endpoints Consumed

| Endpoint | Purpose | When Called |
|----------|---------|------------|
| `POST /api/v1/transactions/upload` | Upload catch-up/buffered transactions | On each sync cycle (when online) |
| `POST /api/v1/preauth` | Forward pre-auth record for reconciliation | After pre-auth authorized (async, retried) |
| `GET /api/v1/transactions/synced-status` | Poll SYNCED_TO_ODOO status | Every ~30 seconds when online |
| `GET /api/v1/agent/config` | Fetch current configuration | On each sync cycle |
| `GET /api/v1/agent/version-check` | Check compatibility on startup | On app launch |
| `POST /api/v1/agent/telemetry` | Report health metrics | Every ~60 seconds when online |
| `POST /api/v1/agent/register` | Register new device | During provisioning |
| `GET /health` | Cloud health ping for connectivity detection | Every ~30 seconds |

## 6.3 Local API Endpoints Exposed

| Endpoint | Method | Description | Auth |
|----------|--------|-------------|------|
| `/api/transactions` | GET | Paginated transactions from buffer. Excludes SYNCED_TO_ODOO. Filterable by time, pump, product. | None (localhost) / API key (LAN) |
| `/api/transactions/{id}` | GET | Specific transaction by ID | Same |
| `/api/pump-status` | GET | Live pump statuses from FCC (proxied over LAN) | Same |
| `/api/preauth` | POST | Submit pre-auth request. Always available (online/offline). | Same |
| `/api/preauth/{id}/cancel` | POST | Cancel a pending/authorized pre-auth | Same |
| `/api/transactions/acknowledge` | POST | Odoo POS acknowledges transactions consumed locally | Same |
| `/api/status` | GET | Agent health: FCC connectivity, internet status, buffer depth, last sync, version | Same |

## 6.4 Retry and Resilience

| Operation | Retry Strategy |
|-----------|---------------|
| Cloud upload (buffered transactions) | Exponential backoff: 5s, 10s, 20s, 40s, ... up to 5 min. Maintains chronological order — does not skip ahead. |
| Pre-auth cloud queue | Same exponential backoff. Queued locally with retry count. Never discarded. |
| FCC LAN poll | Fixed interval (configurable, e.g., 30s). If FCC unreachable, logs warning and retries next interval. |
| Cloud health ping | Fixed interval (30s). Drives connectivity state. Not retried — just repeated on schedule. |
| Telemetry reporting | Best-effort. If cloud unreachable, skip until next interval. No buffering of telemetry. |
| Config sync | Best-effort on each cloud sync cycle. Uses last-known config if cloud unreachable. |

---

# 7. Security Architecture

## 7.1 Credential Storage

| Credential | Storage | Protection |
|-----------|---------|------------|
| FCC connection credentials (IP, port, username/password, API key) | Android Keystore (encrypted) | Hardware-backed if device supports it. Encrypted at rest. Not exportable. |
| Cloud device token | Android Keystore | Signed JWT issued during registration. Refreshed on each cloud sync. |
| LAN API key (for multi-HHT access) | Android Keystore (encrypted SharedPreferences) | Provisioned during setup. Required for non-localhost API requests. |

## 7.2 Local API Security

| Scenario | Protection |
|----------|------------|
| **Same-device (Odoo POS → localhost:8585)** | Binds to localhost only by default. Only processes on the same device can connect. No authentication required — OS-level process isolation is sufficient. |
| **Multi-HHT (LAN access from other HHTs)** | API binds to LAN IP (e.g., 192.168.1.10:8585). Requires `X-Api-Key` header. API key provisioned during site setup. Rate limiting on LAN API (prevent abuse). |
| **Request validation** | All incoming requests validated for required fields, sane values, and injection prevention. |

## 7.3 Cloud Communication Security

| Concern | Approach |
|---------|----------|
| Transport | TLS 1.2+ for all cloud communication. Certificate pinning for cloud middleware domain (prevents MITM on untrusted networks). |
| Authentication | Device token (signed JWT) included in `Authorization: Bearer` header. Token includes `siteCode` and `legalEntityId` claims. Cloud validates signature, expiry, and scope. |
| Token refresh | Token refreshed on each successful cloud sync cycle. Short-lived tokens (e.g., 24-hour expiry) with refresh capability. |
| Mutual TLS (optional) | For high-security deployments, client certificates can be provisioned during setup. Post-MVP consideration. |

## 7.4 Data Security on Device

| Concern | Approach |
|---------|----------|
| SQLite database | Stored in app-private storage (Android sandbox). Not accessible to other apps. Optional: SQLCipher encryption for database-level encryption at rest (consider performance impact on Urovo i9100). |
| Raw FCC payloads | Stored alongside canonical transactions in SQLite. Same protection as above. |
| Log files | Stored in app-private storage. Rotated. No sensitive data in logs (FCC credentials, tokens masked). |
| App-to-app isolation | Android OS enforces process isolation between Edge Agent and Odoo POS. Communication only via localhost HTTP. |

## 7.5 Device Identity

- Each Edge Agent instance is registered with the cloud backend during provisioning.
- Registration produces a unique `deviceId` and `deviceToken` binding the device to a specific `siteCode` and `legalEntityId`.
- The `deviceId` is included in all cloud API calls and telemetry, enabling per-device tracking and access control.
- If a device is decommissioned, the cloud backend can revoke its `deviceToken`, preventing further API access.

---

# 8. Deployment Architecture

## 8.1 Deployment Model

The Edge Agent is an Android APK installed on the Urovo i9100 HHT. It runs as a foreground service alongside Odoo POS.

```
┌─────────────────────────────────────────────┐
│             Urovo i9100 HHT                  │
│             Android 12                       │
│                                              │
│  ┌────────────────┐  ┌────────────────────┐  │
│  │   Odoo POS     │  │   Edge Agent       │  │
│  │   (Odoo app)   │  │   (FCC Middleware)  │  │
│  │                │  │                    │  │
│  │  Connects to   │  │  Connects to FCC   │  │
│  │  Odoo Cloud    │  │  over LAN          │  │
│  │  via internet  │  │                    │  │
│  │                │  │  Ktor HTTP server   │  │
│  │  Polls Edge    │  │  localhost:8585     │  │
│  │  Agent locally │  │  + LAN:8585        │  │
│  └────────────────┘  └────────────────────┘  │
│                                              │
│  ┌────────────────────────────────────────┐  │
│  │  Android OS Services                   │  │
│  │  - WiFi (LAN to FCC + internet)        │  │
│  │  - Keystore (credential storage)       │  │
│  │  - WorkManager (background scheduling) │  │
│  │  - Foreground Service Manager          │  │
│  └────────────────────────────────────────┘  │
└──────────────────────────────────────────────┘
```

## 8.2 Distribution and Updates

| Mechanism | Description |
|-----------|-------------|
| **Initial Install** | APK distributed via Sure MDM to HHT fleet. Alternatively, enterprise sideload for initial deployment. |
| **Updates** | APK updates pushed via Sure MDM. Agent checks version compatibility with cloud on startup (`/agent/version-check`). |
| **Backward Compatibility** | Agent must be backward-compatible with cloud middleware. Older agent works with newer cloud within a supported version range. |
| **Minimum Version Enforcement** | If agent version is below cloud's minimum supported, agent disables FCC communication and alerts Site Supervisor. Prevents data format mismatches. |
| **Configuration Updates** | Config changes (FCC IP, poll interval, ingestion mode) pushed from cloud — no APK update needed. |

## 8.3 Provisioning Flow

```
1. Install APK via Sure MDM (or sideload)
2. Launch Edge Agent → Provisioning Screen
3. Option A: QR Code Scan
   - Admin generates QR code in portal containing:
     {siteCode, fccVendor, fccHost, fccPort, fccCredentials,
      cloudUrl, provisioningToken}
   - Agent scans QR code
   - Agent calls POST /api/v1/agent/register with provisioning token
   - Cloud validates and returns deviceToken + full config
   - Agent stores config in encrypted storage
4. Option B: Manual Entry
   - Supervisor enters: site code, FCC IP, cloud URL, provisioning token
   - Same registration flow as above
5. Agent starts foreground service
6. Agent connects to FCC over LAN (heartbeat)
7. Agent connects to cloud (health check, config sync)
8. Ready for operation
```

## 8.4 Multi-HHT Deployment

```
Station with 3 HHTs:

HHT-1 (Primary Edge Agent):
  - Full Edge Agent installed and provisioned
  - FCC communication active (LAN poll + pre-auth)
  - Local API on localhost:8585 AND LAN IP:8585
  - Buffer stores all transactions for the site

HHT-2, HHT-3 (Odoo POS Only):
  - Odoo POS installed
  - No Edge Agent APK (or Edge Agent in "secondary" mode — future)
  - When online: Odoo POS talks to Odoo Cloud
  - When offline: Odoo POS polls HHT-1 at 192.168.1.10:8585
  - Pre-auth from HHT-2/3: POST to 192.168.1.10:8585/api/preauth
  - API key required for all LAN API calls
```

## 8.5 Resource Constraints (Urovo i9100)

| Resource | Consideration |
|----------|--------------|
| **Storage** | 30,000 transactions × ~2KB each ≈ 60MB for SQLite buffer. Well within device storage capacity. Raw payloads add ~2x → ~120MB total. |
| **Memory** | Ktor embedded server + Room + coroutines. Target: <100MB RAM. Must not starve Odoo POS. |
| **CPU** | Polling every 30s, HTTP server idle most of the time. Bursts during batch upload. Negligible CPU load. |
| **Battery** | Foreground service with periodic work. WiFi LAN stays connected regardless. Cloud sync only when online. Conservative polling intervals reduce battery drain. |
| **Network** | WiFi LAN for FCC. SIM or WiFi for internet. Concurrent connections to both. Android handles this natively. |

---

# 9. Key Design Decisions

## 9.1 Architectural Choices

| Decision | Choice | Rationale | Trade-off |
|----------|--------|-----------|-----------|
| **Technology stack** | Native Kotlin/Java (Android) | Resolved in requirements (OQ-1). Native provides full access to Android Keystore, foreground services, WiFi management, and camera (QR scan). Ktor for embedded HTTP. Room for SQLite. | FCC adapter logic cannot be shared as a binary with the .NET cloud backend. Adapters are re-implemented in Kotlin. |
| **Embedded HTTP server** | Ktor | Lightweight, Kotlin-native, coroutine-based. Runs in-process. No separate container. Lower footprint than Netty or Spring Boot on Android. | Less ecosystem than Spring. Acceptable for the simple REST API surface. |
| **Local database** | Room (SQLite, WAL mode) | Android-native. Compile-time query verification. WAL mode for crash resilience. Sufficient for 30K+ transactions. | No encryption by default (SQLCipher available if needed). No full-text search. |
| **Background execution** | Android Foreground Service + Coroutines | Foreground service ensures OS does not kill the agent. Coroutines for concurrent polling, sync, and API serving. | Foreground service notification is always visible to the attendant. Necessary on Android 12+. |
| **Single primary agent per site** | Configuration-based designation (not automatic election) | Simple. Avoids distributed consensus on Android devices. One HHT is provisioned as primary during setup. | No automatic failover. If primary HHT dies, manual re-provisioning needed. Automatic failover deferred to post-MVP. |
| **Ingestion mode as config** | Cloud-pushed configuration | Changing from CLOUD_DIRECT to RELAY or BUFFER_ALWAYS does not require APK update. Agent reads config on each sync cycle. | Agent must handle runtime config changes gracefully (e.g., mid-sync mode switch). |

## 9.2 Technology Choice: Kotlin/Java vs .NET MAUI

The requirements (OQ-1) resolved the Edge Agent technology as **native Kotlin/Java**. The user's prompt mentions ".NET for backend and edge." This tension is addressed here:

| Factor | Native Kotlin/Java | .NET MAUI on Android |
|--------|--------------------|--------------------|
| Android Keystore access | Native, full API | Available via platform-specific code |
| Foreground Service | Native, straightforward | Requires platform channel |
| Ktor embedded server | First-class Kotlin library | Would use Kestrel or similar — less proven on Android |
| Room / SQLite | Native Room library | EF Core with SQLite — possible but less battle-tested on Android |
| APK size | Smaller (no .NET runtime bundled) | Larger (includes .NET runtime, ~30-50MB overhead) |
| Device performance (Urovo i9100) | Optimized for Android | Additional runtime overhead |
| Team skills alignment | Requires Kotlin/Java skills | Aligns with .NET cloud team skills |
| Adapter code sharing | Re-implement in Kotlin | Could share adapter libraries with cloud |

**Recommendation:** Follow the resolved requirements — **Kotlin/Java** for the Edge Agent. The Urovo i9100 is a constrained device, and native Android provides the best performance, smallest footprint, and most reliable access to platform APIs. The adapter logic re-implementation cost is acceptable given the small number of adapters (DOMS for MVP, 3 more in Phase 3).

**If the team strongly prefers .NET** and is willing to accept the larger APK, runtime overhead, and platform-specific workarounds, .NET MAUI remains a viable alternative. This should be validated with a PoC on the actual Urovo i9100 hardware before committing.

## 9.3 Assumptions

1. The Urovo i9100 has sufficient free storage (500MB+) after Odoo POS and OS for the Edge Agent and its buffer.
2. The station WiFi LAN provides reliable, low-latency connectivity to the FCC (no firewall restrictions between HHT and FCC).
3. DOMS FCC exposes a poll-able API over LAN (REST or TCP) with transaction fetch and pre-auth endpoints.
4. Android 12 on the Urovo i9100 supports foreground services with the required permissions.
5. Sure MDM can push APK updates to the HHT fleet reliably.
6. Only one HHT per site needs to run the Edge Agent for MVP (multi-HHT failover is post-MVP).
7. Odoo POS can detect internet outage and switch to the local Edge Agent API — this capability exists or will be built by the Odoo team.

## 9.4 Known Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| **Urovo i9100 hardware limitations** | Memory, storage, or CPU constraints may affect agent + Odoo POS coexistence | Early hardware testing. Memory profiling. Conservative buffer sizing. |
| **Android Doze / battery optimization** | OS may throttle background work, affecting FCC polling and cloud sync | Foreground service with persistent notification. Whitelist app from battery optimization. |
| **WiFi LAN disconnection** | If station WiFi drops, FCC communication is lost | Heartbeat monitoring. Auto-reconnect. Alert Site Supervisor. Consider Ethernet via USB-C if WiFi is unreliable. |
| **DOMS protocol complexity** | Unknown protocol details may require significant adapter development effort | Early PoC. FCC simulator for development. Obtain DOMS documentation ASAP. |
| **Primary HHT failure at multi-HHT site** | All non-primary HHTs lose access to buffered transactions and pre-auth | Manual failover procedure (re-provision another HHT as primary). Automatic failover in post-MVP. |
| **SQLite corruption** | Power loss or force-kill may corrupt buffer | WAL mode mitigates this. Integrity check on startup. Backup corrupted DB. Start fresh buffer. Alert cloud for forensic retrieval. |
| **QR code provisioning security** | QR code contains FCC credentials and cloud token | Use one-time provisioning tokens. Encrypt sensitive QR payload fields. Token expires after first use. |

---

# 10. Non-Functional Requirements Mapping

| NFR | Target | HLD Approach |
|-----|--------|-------------|
| **Buffer Capacity** | 30 days × 1,000 txns/day = 30,000+ transactions | SQLite with Room. ~60-120MB storage. Well within Urovo i9100 capacity. |
| **Offline Resilience** | Zero transaction loss during internet outage | SQLite WAL mode. Buffer survives restarts. Replay on reconnection. |
| **Pre-Auth Latency** | < 5 seconds (Edge Agent + FCC, no cloud dependency) | LAN-only path. Ktor → adapter → FCC round-trip. Target < 2 seconds on local WiFi. |
| **Cloud Sync Latency** | Transactions uploaded within seconds of reconnection | Replay worker activates immediately on internet restoration. Batched upload. |
| **Battery Life** | Must not significantly degrade HHT battery life | Foreground service with efficient polling intervals. WiFi stays connected for both Odoo and agent. |
| **Crash Recovery** | Buffer intact after crash or reboot | SQLite WAL mode. Foreground service auto-restart. Boot receiver for auto-start. |
| **Security** | FCC credentials encrypted. Cloud communication secured. | Android Keystore. TLS 1.2+. Certificate pinning. Device tokens. |
| **Observability** | Telemetry visible in cloud dashboard | Periodic health reporting. Local diagnostics screen. Structured logging. |
| **Supportability** | Remote troubleshooting for 2,000 remote sites | Telemetry (buffer depth, FCC status, last sync). Local log viewer. Remote config push. |

---

# 11. Recommended Technology Direction

| Concern | Technology | Rationale |
|---------|-----------|-----------|
| **Language** | Kotlin (primary), Java (interop as needed) | Modern Android development language. Coroutines for async. Null safety. |
| **Build System** | Gradle (Kotlin DSL) | Standard Android build system. |
| **HTTP Server** | Ktor (embedded, CIO engine) | Lightweight, coroutine-native. Ideal for Android embedded server. CIO engine has minimal dependencies. |
| **Database** | Room (over SQLite, WAL mode) | Android's recommended persistence library. Compile-time query checks. Migration support. |
| **HTTP Client** | Ktor Client or OkHttp | For cloud API communication. OkHttp is proven on Android; Ktor Client integrates with coroutines. |
| **Serialization** | kotlinx.serialization | Kotlin-native, compile-time. Lighter than Gson/Moshi for canonical model serialization. |
| **Dependency Injection** | Koin or Hilt | Koin is lightweight and Kotlin-native. Hilt is Google's recommended DI for Android. Either is suitable. |
| **Background Execution** | Foreground Service + Kotlin Coroutines + WorkManager | Foreground service for continuous operation. Coroutines for concurrent tasks. WorkManager for guaranteed periodic work (backup mechanism). |
| **Security** | Android Keystore API + EncryptedSharedPreferences | Hardware-backed key storage. Encrypted app preferences for config. |
| **QR Code** | ZXing or ML Kit | Proven QR code scanning libraries for Android. |
| **Testing** | JUnit 5 + MockK + Robolectric | Unit tests with MockK mocks. Robolectric for Android framework tests without emulator. |
| **UI** | Jetpack Compose (minimal UI) | Modern Android UI toolkit. Only used for diagnostics and provisioning screens. |
| **Logging** | Timber | Standard Android logging library. Structured. Configurable. |
| **Network Monitoring** | Android ConnectivityManager + custom cloud health ping | System-level connectivity changes + application-level cloud reachability check. |

---

# 12. Open Questions / Pending Decisions

| ID | Question | Impact | Assumption Made |
|----|----------|--------|-----------------|
| OQ-EA-1 | What is the DOMS FCC LAN protocol? REST API? TCP socket? What ports? What authentication? | Adapter development is blocked until this is known | Assumed REST API over LAN. PoC needed. |
| OQ-EA-2 | Does DOMS FCC support a poll/fetch API for transaction retrieval, or only push? | Determines if LAN catch-up polling is feasible with DOMS | Assumed poll API exists. If not, agent must receive FCC push on LAN (RELAY mode). |
| OQ-EA-3 | Does DOMS FCC support pre-auth commands? What is the command format? | Pre-auth flow depends on FCC adapter support | Assumed supported based on requirements. Needs DOMS documentation. |
| OQ-EA-4 | What is the available free storage on Urovo i9100 after OS and Odoo POS? | Buffer sizing validation | Assumed 500MB+ free. Needs verification on actual device. |
| OQ-EA-5 | Can the Urovo i9100 reliably maintain WiFi connections to both the station LAN and internet simultaneously? | Dual-network requirement | Assumed yes (single WiFi connection to station LAN; internet via same WiFi or SIM). Needs hardware testing. |
| OQ-EA-6 | How does Odoo POS detect internet outage and switch to the Edge Agent local API? Is this Odoo POS built-in, or must the Odoo team build this? | End-to-end offline flow depends on Odoo POS capability | Assumed Odoo team builds this. Edge Agent just exposes the API. |
| OQ-EA-7 | For multi-HHT sites, how is the LAN API key distributed to non-primary HHTs? | Provisioning flow for secondary HHTs | Assumed API key is part of site provisioning. Distributed via QR code or manual entry on non-primary HHTs. |
| OQ-EA-8 | Should SQLCipher be used for SQLite encryption at rest? What is the performance impact on Urovo i9100? | Security vs. performance trade-off | Assumed not required for MVP (Android sandbox provides file-level isolation). Evaluate post-MVP. |
| OQ-EA-9 | Is the Urovo i9100 WiFi reliable enough for continuous FCC polling every 15-30 seconds? Any known WiFi stability issues? | Core agent reliability | Assumed WiFi is stable on the station LAN. Field testing required. |
| OQ-EA-10 | Final confirmation: Kotlin/Java (per resolved OQ-1 in requirements) or .NET MAUI (per user preference for ".NET on edge")? | Entire technology stack and team skills | Followed resolved requirements: Kotlin/Java. See section 9.2 for comparison. |

---

*End of Edge Agent HLD — WIP v0.1*
