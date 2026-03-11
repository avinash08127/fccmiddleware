# Forecourt Middleware — Edge Agent High Level Design

**Status:** WIP (Work in Progress)
**Version:** 0.3 (Reconciled)
**Date:** 2026-03-11
**Author:** Architecture Review

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
| Pre-Auth Relay | Receive pre-auth requests from Odoo POS (with Odoo pump/nozzle numbers), translate to FCC pump/nozzle numbers via local nozzle mapping table, authorize via FCC over LAN, return result. Always available (online/offline). |
| Transaction Ingestion | Poll or receive FCC transactions depending on site ingestion mode. Produce canonical transactions. |
| Local Buffer (Store-and-Forward) | Persist all transactions and pre-auth records in SQLite. Crash-resilient. 30,000+ capacity. |
| Cloud Sync | Upload buffered transactions, forward pre-auth records, send telemetry, pull config updates. |
| SYNCED_TO_ODOO Lifecycle | Poll cloud for confirmed status. Stop serving confirmed transactions locally. |
| Local REST API | Expose transaction queries, pump status, and pre-auth endpoints for Odoo POS on localhost. |
| Multi-HHT Support | Expose LAN API for non-primary HHTs at the same station (API key authenticated). |
| Provisioning | QR code or manual setup. Cloud registration. Config download. |
| Diagnostics | On-device screen for Site Supervisor. Connection status, buffer depth, manual pull. |
| Connectivity Monitoring | Track internet and FCC LAN health independently. Drive mode transitions. |
| Telemetry | Report device health, sync status, and performance metrics to cloud. |

## 1.4 Boundaries and Exclusions

**Included:**
- Device runtime, local buffering, LAN communication, local API, provisioning, telemetry, replay logic, and FCC adapter execution

**Excluded:**
- Central reconciliation logic (cloud responsibility)
- Enterprise user authentication (Entra ID handled by portal/cloud)
- Odoo internal offline behavior beyond the documented integration points
- Automatic multi-HHT failover in MVP
- Detailed Android UI screen designs

## 1.5 Primary Requirement Alignment

- REQ-6 to REQ-9: Pre-auth, normal-order capture support, Odoo offline poll model
- REQ-12 to REQ-16: Ingestion modes, dedup support, audit/retry behavior, edge buffering and diagnostics

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
| **Multi-Country Readiness** | Support per-country timezone, currency, fiscalization requirements, and site policies via downloaded configuration. |
| **Maintainability** | Keep runtime responsibilities focused and explicit. Isolate vendor adapters from buffer, sync, and API layers so new FCCs can be added without destabilizing core flows. |

## 2.1 Performance Guardrails

These guardrails are architectural constraints for the Edge Agent on the Urovo i9100 and should be validated continuously during implementation.

| Area | Guardrail |
|------|-----------|
| Pre-auth local API overhead | `POST /api/preauth` p95 <= 150 ms before FCC call time |
| Pre-auth end-to-end | `POST /api/preauth` p95 <= 1.5 s, p99 <= 3 s on healthy FCC LAN |
| Offline transaction reads | `GET /api/transactions` p95 <= 150 ms for first page (`limit <= 50`) with 30,000 buffered records |
| Status endpoint | `GET /api/status` p95 <= 100 ms |
| Pump status | Live response target <= 1 s on healthy FCC LAN; stale fallback <= 150 ms |
| Memory | Steady-state RSS target <= 180 MB |
| Replay throughput | >= 600 transactions/minute on stable internet while preserving chronological ordering |
| Battery drain | <= 8% over 8 hours in `CLOUD_DIRECT`; <= 12% over 8 hours in `RELAY` / `BUFFER_ALWAYS` |
| Runtime scheduling | One active cadence controller for recurring work inside the always-on runtime |

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
9. **Runtime Cadence Controller** — Coalesces recurring heartbeat, health, replay, and status-sync work under one orchestrated loop.
10. **Provisioning** — QR code scan, manual entry, or cloud push for initial and ongoing configuration.
11. **Diagnostics UI** — On-device screen for Site Supervisor with connection status, buffer depth, and manual pull.
12. **Manual Pull Path** — On-demand FCC fetch triggered by Odoo POS or supervisor workflow so a just-completed dispense can be surfaced immediately.

## 3.2 Supported Operating Modes

| Mode | Internet | FCC LAN | Agent Behaviour |
|------|----------|---------|-----------------|
| **Fully Online** | Up | Up | Polls FCC over LAN. Forwards catch-up to cloud immediately. Pre-auth via LAN. Syncs SYNCED_TO_ODOO. Reports telemetry opportunistically on successful cloud cycles. |
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

Rationale:
- Android 12 HHT constraints, local LAN requirements, and requirement resolution make native runtime the lowest-risk option.
- The edge runtime is an operational appliance, not a generic application platform.
- Reliability and controlled persistence matter more here than cross-platform code reuse.
- The always-on runtime must stay thin so Odoo POS responsiveness, device battery, and memory headroom are preserved.

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
[Connectivity Manager + Cadence Controller] → internet = UP, FCC LAN = UP

Every cadence tick where FCC poll is due:
  [FCC Poll Worker] → adapter.fetchTransactions(cursor)
    → [FCC] responds with transactions since cursor
    → adapter.normalize() → canonical transactions
    → try cloud upload:
      → SUCCESS: update cursor, done
      → FAIL (internet down): write to SQLite buffer (status=PENDING)
```

### Pre-Auth Flow

```
[Odoo POS] → POST localhost:8585/api/preauth  (Odoo pump_number + odoo_nozzle_number)
  → [Pre-Auth Handler] validates request
  → NozzleDao.resolveForPreAuth(siteCode, odoo_pump_number, odoo_nozzle_number)
      → resolves fcc_pump_number, fcc_nozzle_number, product_code
  → adapter.sendPreAuth(fcc_pump_number, fcc_nozzle_number, amount, TIN, ...)
    → [FCC] authorizes pump
    → [FCC] returns authorization response
  → store pre-auth locally (with both Odoo and FCC numbers for traceability)
  → return response to Odoo POS (AUTHORIZED / FAILED)
  → async only: queue pre-auth record to cloud (retry if offline)

Cloud communication is never on the request-response path for pre-auth.
```

### Offline Buffer and Replay

```
Phase 1: Internet DOWN
  [FCC Poll Worker] → gets transactions
    → cloud upload FAILS → write to SQLite (status=PENDING)

  [Odoo POS] → polls GET /api/transactions
    → returns PENDING transactions from buffer
    → excludes SYNCED_TO_ODOO entries
    → no live FCC dependency for transaction-list reads

Phase 2: Internet RESTORED
  [Connectivity Manager] detects cloud reachable
  [Replay Worker] activates:
    → SELECT * FROM buffer WHERE status='PENDING' ORDER BY timestamp ASC
    → batch upload (50 per request) to cloud
    → cloud responds per-transaction (created/skipped)
    → update status to SYNCED
    → continue until all PENDING uploaded

  [SYNCED_TO_ODOO Sync] activates on the shared cadence controller:
    → GET /api/v1/transactions/synced-status from cloud
    → update local entries to SYNCED_TO_ODOO
    → these entries no longer returned by local API
```

### SYNCED_TO_ODOO Flow

```
On cadence ticks when internet is available and status sync is due:
  [Cloud Sync Engine] → GET cloud/transactions/synced-status?since={lastCheck}
    → receives list of SYNCED_TO_ODOO transaction IDs
    → UPDATE buffer SET status='SYNCED_TO_ODOO' WHERE fccTransactionId IN (...)
    → GET /api/transactions now EXCLUDES these
    → after retention period (7 days), DELETE old SYNCED_TO_ODOO entries
```

### Manual Pull Flow

```
[Odoo POS or Diagnostics UI] → trigger manual FCC pull
  → [Manual Pull Path] acquires same poll lock used by scheduled polling
  → adapter.fetchTransactions(cursor)
  → normalize + dedup + buffer any new transactions
  → release poll lock
  → return summary of newly buffered transactions and cursor movement
```

---

# 5. Project Structure Recommendation

## 5.1 Repository Strategy

**Separate repository** from the cloud backend. The Edge Agent is a Kotlin/Java Android project with its own build, test, and release lifecycle. It shares the canonical model definition (documented, not as a code dependency) with the cloud backend.

## 5.2 Shared Contracts

Do not force shared runtime code with the .NET backend. Share only:

- Canonical payload schemas
- API contracts
- Event names and status enums
- Validation rules that can be expressed declaratively

This keeps cloud and device runtimes independently evolvable while preserving protocol consistency.

## 5.3 Recommended Project Structure

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
│   │   │   │   │   ├── PreAuthDao.kt                   # Room DAO
│   │   │   │   │   ├── NozzleDao.kt                    # Room DAO — Odoo↔FCC pump/nozzle lookup
│   │   │   │   │   ├── BufferedTransaction.kt          # Room entity
│   │   │   │   │   ├── PreAuthRecord.kt                # Room entity
│   │   │   │   │   ├── Nozzle.kt                       # Room entity — pump/nozzle number mapping + product
│   │   │   │   │   ├── BufferDatabase.kt               # Room database (WAL mode, 6 entities)
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
│   │   │   │   ├── runtime/                            # Thin always-on runtime control
│   │   │   │   │   └── CadenceController.kt            # Shared cadence loop for recurring work
│   │   │   │   │
│   │   │   │   ├── ingestion/                          # FCC Polling / Ingestion
│   │   │   │   │   ├── FccPollWorker.kt                # Periodic LAN poll worker
│   │   │   │   │   ├── IngestionOrchestrator.kt        # Routes based on ingestionMode
│   │   │   │   │   ├── ManualPullCoordinator.kt        # Serializes manual and scheduled pulls
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
│   ├── adapter-guide.md                                # How to implement a new FCC adapter
│   └── adr/                                            # Architecture Decision Records
│
└── tools/
    └── fcc-simulator/                                  # DOMS FCC simulator for development/testing
```

## 5.4 Internal Layering

| Layer | Responsibility |
|-------|---------------|
| `runtime` / `service` | Android services, app lifecycle, scheduling |
| `domain` / `adapter/common` | Canonical models and state machines for transaction, sync, and health status |
| `buffer` / `storage` | SQLite/Room, retention, integrity checks |
| `api` | Ktor local HTTP server handlers and request validation |
| `adapter/*` | FCC vendor implementations behind `IFccAdapter` interface |
| `cloud` / `sync` | Cloud client, replay orchestration, backoff, status sync |

## 5.5 Design Rationale

| Decision | Rationale |
|----------|-----------|
| Single Android module (not multi-module) | The Edge Agent is a single app with cohesive responsibilities. Multi-module adds complexity without clear benefit at this scale. Package structure provides sufficient separation. |
| Ktor for embedded HTTP server | Lightweight, Kotlin-native, coroutine-based. Ideal for embedding in an Android app. Lower resource footprint than alternatives. |
| Room for SQLite | Android's recommended persistence library. Compile-time SQL verification. Built-in support for LiveData/Flow. WAL mode configurable. |
| FCC adapter as an internal package | Adapters are not separate deployable units on Android. They're code modules within the same APK. The FccAdapterFactory pattern allows runtime selection by vendor. |
| Foreground Service | Android requires a foreground service for long-running background work (FCC polling, cloud sync). Use it as a thin always-on core only; recurring work is coalesced behind one cadence controller to reduce battery and CPU impact. |
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
| Sure MDM → HHT | Management | MDM deployment channel | Internet | MDM credentials |

## 6.2 Cloud API Endpoints Consumed

| Endpoint | Purpose | When Called |
|----------|---------|------------|
| `POST /api/v1/transactions/upload` | Upload catch-up/buffered transactions | On each sync cycle (when online) |
| `POST /api/v1/preauth` | Forward pre-auth record for reconciliation | After pre-auth authorized (async, retried) |
| `GET /api/v1/transactions/synced-status` | Poll SYNCED_TO_ODOO status | On shared cadence when online |
| `GET /api/v1/agent/config` | Fetch current configuration | On each sync cycle |
| `GET /api/v1/agent/version-check` | Check compatibility on startup | On app launch |
| `POST /api/v1/agent/telemetry` | Report health metrics | Opportunistically on successful cloud cycles, target every ~300 seconds |
| `POST /api/v1/agent/register` | Register new device | During provisioning |
| `GET /health` | Cloud health ping for connectivity detection | On shared cadence, nominally every ~30 seconds |

## 6.3 Local API Endpoints Exposed

| Endpoint | Method | Description | Auth |
|----------|--------|-------------|------|
| `/api/transactions` | GET | Paginated transactions from buffer. Excludes SYNCED_TO_ODOO. Filterable by time, pump, product. | None (localhost) / API key (LAN) |
| `/api/transactions/{id}` | GET | Specific transaction by ID | Same |
| `/api/pump-status` | GET | Live pump statuses from FCC (proxied over LAN) with short timeout, stale fallback, and freshness metadata | Same |
| `/api/preauth` | POST | Submit pre-auth request. Always available (online/offline). | Same |
| `/api/preauth/{id}/cancel` | POST | Cancel a pending/authorized pre-auth | Same |
| `/api/transactions/acknowledge` | POST | Odoo POS acknowledges transactions consumed locally | Same |
| `/api/status` | GET | Agent health: FCC connectivity, internet status, buffer depth, last sync, version | Same |

Manual pull is also a required capability. If it is not yet represented in the OpenAPI contract, the contract should be extended before implementation so Odoo POS and diagnostics workflows can invoke it consistently.

## 6.4 Sync Patterns

| Ingestion Mode | Sync Behaviour |
|---------------|----------------|
| `CLOUD_DIRECT` | Edge polls FCC as catch-up and uploads missed transactions |
| `RELAY` | FCC talks to Edge first; Edge relays to cloud in near real time when online |
| `BUFFER_ALWAYS` | Edge stores first and uploads on schedule |

## 6.5 Retry and Resilience

| Operation | Retry Strategy |
|-----------|---------------|
| Cloud upload (buffered transactions) | Exponential backoff: 5s, 10s, 20s, 40s, ... up to 5 min. Maintains chronological order — does not skip ahead. |
| Pre-auth cloud queue | Same exponential backoff. Queued locally with retry count. Never discarded. |
| FCC LAN poll | Shared cadence interval (configurable, e.g., 30s in relay mode, longer in catch-up mode). If FCC unreachable, logs warning and retries on next scheduled cadence. |
| Cloud health ping | Shared cadence interval (nominally 30s). Drives connectivity state. Not retried — just repeated on schedule. |
| Telemetry reporting | Best-effort. If cloud unreachable, skip until next interval. No buffering of telemetry. |
| Config sync | Best-effort on each cloud sync cycle. Uses last-known config if cloud unreachable. |

## 6.6 Idempotency Considerations

- Cloud dedup is authoritative, but the Edge Agent should still avoid duplicate uploads of already-confirmed records.
- Local records track `cloud-accepted`, `cloud-rejected`, and `SYNCED_TO_ODOO` states separately.
- Manual pull results and scheduled pull results must converge into one local record model.

## 6.7 Online/Offline Handling

- Internet state and FCC LAN state are evaluated independently.
- Internet loss does not disable pre-auth or local transaction serving.
- FCC LAN loss triggers alerting and possibly manual operating fallback, but the agent continues cloud sync for buffered items if internet remains up.

---

# 7. Security Architecture

## 7.1 Authentication and Device Identity

- Each primary Edge Agent receives a unique device identity tied to site and device serial metadata.
- Preferred runtime credential model: device keypair in Android Keystore with certificate- or signed-token-based authentication to cloud.
- Non-primary HHT access to the primary agent uses a site-scoped API key plus short-lived session token if later needed.
- Registration produces a unique `deviceId` and `deviceToken` binding the device to a specific `siteCode` and `legalEntityId`.
- The `deviceId` is included in all cloud API calls and telemetry, enabling per-device tracking and access control.
- If a device is decommissioned, the cloud backend can revoke its `deviceToken`, preventing further API access.

## 7.2 Credential Storage

| Credential | Storage | Protection |
|-----------|---------|------------|
| FCC connection credentials (IP, port, username/password, API key) | Android Keystore (encrypted) | Hardware-backed if device supports it. Encrypted at rest. Not exportable. |
| Cloud device token | Android Keystore | Signed JWT issued during registration. Refreshed on each cloud sync. |
| LAN API key (for multi-HHT access) | Android Keystore (encrypted SharedPreferences) | Provisioned during setup. Required for non-localhost API requests. |

## 7.3 Authorization

- Device permissions are site-scoped only.
- Non-primary HHT calls are restricted to allowed local operations such as transaction fetch, pump status, and pre-auth submission.
- Administrative functions such as reprovisioning or viewing advanced diagnostics require supervisor-only access in the local UI.

## 7.4 Local API Security

| Scenario | Protection |
|----------|------------|
| **Same-device (Odoo POS → localhost:8585)** | Binds to localhost only by default. Only processes on the same device can connect. No authentication required — OS-level process isolation is sufficient. |
| **Multi-HHT (LAN access from other HHTs)** | API binds to LAN IP (e.g., 192.168.1.10:8585). Requires `X-Api-Key` header. API key provisioned during site setup. Rate limiting on LAN API (prevent abuse). |
| **Request validation** | All incoming requests validated for required fields, sane values, and injection prevention. |
| **LAN binding control** | Enable LAN binding only when site configuration explicitly marks the device as primary multi-HHT agent. Prefer station-WiFi-only exposure with explicit IP allow rules if feasible. |

## 7.5 Cloud Communication Security

| Concern | Approach |
|---------|----------|
| Transport | TLS 1.2+ for all cloud communication. Certificate pinning for cloud middleware domain (prevents MITM on untrusted networks). |
| Authentication | Device token (signed JWT) included in `Authorization: Bearer` header. Token includes `siteCode` and `legalEntityId` claims. Cloud validates signature, expiry, and scope. |
| Token refresh | Token refreshed on each successful cloud sync cycle. Short-lived tokens (e.g., 24-hour expiry) with refresh capability. |
| Mutual TLS (optional) | For high-security deployments, client certificates can be provisioned during setup. Post-MVP consideration. |
| Bootstrap token | Single-use and rotated away after registration. |

## 7.6 Data Security on Device

| Concern | Approach |
|---------|----------|
| SQLite database | Stored in app-private storage (Android sandbox). Not accessible to other apps. Optional: SQLCipher encryption for database-level encryption at rest (consider performance impact on Urovo i9100). |
| Raw FCC payloads | Stored alongside canonical transactions in SQLite. Same protection as above. |
| Log files | Stored in app-private storage. Rotated. No sensitive data in logs (FCC credentials, tokens masked). |
| App-to-app isolation | Android OS enforces process isolation between Edge Agent and Odoo POS. Communication only via localhost HTTP. |
| No storage of employee Entra tokens | Beyond transient Odoo/portal contexts. |

## 7.7 Audit and Tamper Awareness

- Record provisioning changes, manual retries, mode transitions, config updates, and supervisor actions.
- Surface clock skew, repeated auth failures, or SQLite integrity failures as tamper or reliability signals.

---

# 8. Deployment Architecture

## 8.1 Deployment Model

The Edge Agent is an Android APK installed on the Urovo i9100 HHT. It runs as a foreground service alongside Odoo POS. The foreground service hosts a thin always-on runtime: local API server, pre-auth path, connectivity/cadence control, FCC polling orchestration, and replay triggers. One primary agent per site for MVP. Optional LAN API exposure for other HHTs on the same station network.

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
│  │  - WorkManager (backup / deferred jobs)│  │
│  │  - Foreground Service Manager          │  │
│  └────────────────────────────────────────┘  │
└──────────────────────────────────────────────┘
```

## 8.2 Provisioning Flow

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
8. Agent performs FCC connectivity test and stores baseline diagnostics
9. Ready for operation
```

## 8.3 Distribution and Updates

| Mechanism | Description |
|-----------|-------------|
| **Initial Install** | APK distributed via Sure MDM to HHT fleet. Alternatively, enterprise sideload for initial deployment. |
| **Updates** | APK updates pushed via Sure MDM. Agent checks version compatibility with cloud on startup (`/agent/version-check`). |
| **Backward Compatibility** | Agent must be backward-compatible with cloud middleware. Older agent works with newer cloud within a supported version range. |
| **Minimum Version Enforcement** | If agent version is below cloud's minimum supported, agent disables FCC communication and alerts Site Supervisor. Prevents data format mismatches. |
| **Configuration Updates** | Config changes (FCC IP, poll interval, ingestion mode) pushed from cloud — no APK update needed. |

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

## 8.5 Environment Strategy

- Device points to environment-specific cloud base URL.
- Separate bootstrap credentials per environment.
- UAT should include realistic multi-HHT and outage simulation, not just happy-path connectivity.

## 8.6 Resilience and Recovery

- SQLite WAL mode for crash resilience.
- Startup integrity check (`PRAGMA integrity_check`).
- Backup of corrupted DB before reset.
- Replay-safe resynchronization after app restart or device reboot.
- Foreground service with persistent notification. Whitelist app from battery optimization.
- Auto-restart via BootReceiver on device boot.

## 8.7 Resource Constraints (Urovo i9100)

| Resource | Consideration |
|----------|--------------|
| **Storage** | 30,000 transactions x ~2KB each = ~60MB for SQLite buffer. Raw payloads add ~2x = ~120MB total. Well within device storage capacity. |
| **Memory** | Ktor embedded server + Room + coroutines. Steady-state RSS target: <= 180MB, with lower real-world usage preferred. Must not starve Odoo POS. |
| **CPU** | Use one cadence controller rather than multiple hot loops. CPU bursts are expected during batch upload and manual pull, but idle cost should remain low. |
| **Battery** | Foreground service with coalesced periodic work. Target <= 8% drain over 8 hours in `CLOUD_DIRECT`; <= 12% in `RELAY` / `BUFFER_ALWAYS`. |
| **Network** | WiFi LAN for FCC. SIM or WiFi for internet. Concurrent connections to both. Android handles this natively. |

## 8.8 Observability

- **Device metrics:** battery, storage, app version, sync lag, buffer depth
- **Integration metrics:** FCC heartbeat age, cloud reachability, failed upload counts
- **Local diagnostics screen** with recent logs and manual pull
- **Telemetry upload** to cloud dashboard for remote monitoring across 2,000+ sites

---

# 9. Key Design Decisions

## 9.1 Architectural Choices

| Decision | Choice | Rationale | Trade-off |
|----------|--------|-----------|-----------|
| **Technology stack** | Native Kotlin/Java (Android) | Resolved in requirements (OQ-1). Native provides full access to Android Keystore, foreground services, WiFi management, and camera (QR scan). Ktor for embedded HTTP. Room for SQLite. | FCC adapter logic cannot be shared as a binary with the .NET cloud backend. Adapters are re-implemented in Kotlin. |
| **Embedded HTTP server** | Ktor | Lightweight, Kotlin-native, coroutine-based. Runs in-process. No separate container. Lower footprint than Netty or Spring Boot on Android. | Less ecosystem than Spring. Acceptable for the simple REST API surface. |
| **Local database** | Room (SQLite, WAL mode) | Android-native. Compile-time query verification. WAL mode for crash resilience. Sufficient for 30K+ transactions. | No encryption by default (SQLCipher available if needed). No full-text search. |
| **Background execution** | Android Foreground Service + Coroutines + shared cadence controller | Foreground service ensures OS does not kill the agent. Coroutines handle concurrent work. One cadence controller coalesces heartbeat, health, status sync, and replay triggers. | Foreground service notification is always visible to the attendant. Requires discipline to avoid turning the service into a collection of independent hot loops. |
| **Single primary agent per site** | Configuration-based designation (not automatic election) | Simple. Avoids distributed consensus on Android devices. One HHT is provisioned as primary during setup. | No automatic failover. If primary HHT dies, manual re-provisioning needed. Automatic failover deferred to post-MVP. |
| **Ingestion mode as config** | Cloud-pushed configuration | Changing from CLOUD_DIRECT to RELAY or BUFFER_ALWAYS does not require APK update. Agent reads config on each sync cycle. | Agent must handle runtime config changes gracefully (e.g., mid-sync mode switch). |
| **SQLite durable store-and-forward** | Room over SQLite, WAL mode | Simple, mature, and adequate for 30K+ retained records per device. | Careful schema evolution and corruption recovery are required. |
| **Local API as Odoo offline integration contract** | Ktor localhost REST API | Keeps Odoo POS reading FCC-originated transaction facts during outages. | Local API compatibility becomes a release-critical contract. |

## 9.2 Technology Choice: Kotlin/Java vs .NET MAUI

The requirements (OQ-1) resolved the Edge Agent technology as **native Kotlin/Java**. This tension with ".NET for backend and edge" is addressed here:

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
8. Station LAN remains available independently of internet outages.
9. Primary agent device remains powered and connected during most station operation.

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
| **Multi-HHT LAN discovery** | Support workflow may become operationally messy if not tightly specified | Explicit IP configuration during provisioning. No auto-discovery in MVP. |
| **Long-running FCC sessions on Android** | Some FCC protocols may behave poorly on long-running Android network sessions | PoC validation on actual hardware. Session reconnect logic. |

---

# 10. Non-Functional Requirements Mapping

| NFR Area | Target | HLD Response |
|----------|--------|-------------|
| **Buffer Capacity** | 30 days x 1,000 txns/day = 30,000+ transactions | SQLite with Room. ~60-120MB storage. Well within Urovo i9100 capacity. |
| **Offline Resilience** | Zero transaction loss during internet outage | SQLite WAL mode. Buffer survives restarts. Replay on reconnection. |
| **Pre-Auth Latency** | p95 <= 1.5 seconds, p99 <= 3 seconds on healthy FCC LAN | LAN-only path. Cloud forwarding is explicitly asynchronous and not on the request path. |
| **Offline Read Latency** | `GET /api/transactions` p95 <= 150 ms for first page with 30,000 buffered records | Buffer-backed reads only, page-bounded queries, and indexed hot paths. |
| **Status Latency** | `GET /api/status` p95 <= 100 ms | Status is served from in-memory/runtime state plus cheap local queries. |
| **Pump Status Latency** | Live <= 1 second on healthy LAN; stale fallback <= 150 ms | Short timeouts, single-flight live fetch, and last-known snapshot fallback. |
| **Cloud Sync Latency** | Transactions uploaded within seconds of reconnection | Replay worker activates immediately on internet restoration. Batched upload. |
| **Battery Life** | Must not significantly degrade HHT battery life | Thin foreground service and shared cadence controller reduce wakeups and duplicated polling. |
| **Crash Recovery** | Buffer intact after crash or reboot | SQLite WAL mode. Foreground service auto-restart. Boot receiver for auto-start. |
| **Security** | FCC credentials encrypted. Cloud communication secured. | Android Keystore. TLS 1.2+. Certificate pinning. Device tokens. |
| **Availability** | LAN-first operation ensures continuity | Durable buffer, automatic reconnect, manual pull support. |
| **Observability** | Telemetry visible in cloud dashboard | Periodic health reporting. Local diagnostics screen. Structured logging. |
| **Supportability** | Remote troubleshooting for 2,000+ remote sites | Telemetry (buffer depth, FCC status, last sync). Local log viewer. Remote config push. |
| **Operability** | Field-friendly deployment | QR provisioning, MDM rollout, explicit primary-agent model, simple local APIs. |
| **Extensibility** | New FCC vendors without core destabilization | Adapter isolation, config-driven site behavior, shared contracts without forcing shared runtime code. |

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

### Design Patterns

- Store-and-forward
- Explicit state machine for transaction sync lifecycle
- Adapter pattern for FCC vendors
- Supervisor pattern for connectivity and background tasks

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
| OQ-EA-11 | What exact local-network discovery/configuration method will non-primary HHTs use to find the primary agent IP in offline mode? | Multi-HHT usability | Assumed explicit IP configuration during provisioning. |
| OQ-EA-12 | Should the primary agent expose LAN API only on private WiFi, or is there any scenario where hotspot networking changes that assumption? | Security exposure | Assumed private WiFi only. |
| OQ-EA-13 | Which FCC vendors require persistent sockets versus stateless polling, and how does that interact with Android background limits? | Adapter design and reliability | Needs vendor-by-vendor analysis. |
| OQ-EA-14 | Is supervisor authentication on the local diagnostics screen delegated to Odoo user context, or handled with a separate local PIN/policy? | Local security model | To be decided. |
| OQ-EA-15 | What minimum offline retention beyond 30 days is needed for rare prolonged outages or operational negligence cases? | Storage planning | Assumed 30 days sufficient for MVP. |

---

# 13. Areas Needing Validation / PoC

- Long-running FCC session stability on Urovo i9100 devices
- Background execution behavior under Android power-management policies
- Realistic replay speed after 2 to 7 days of outage
- LAN API behavior with multiple HHTs on typical station WiFi networks
- Manual pull behavior under contention with scheduled polling
- Local API latency and memory profile with a 30,000-record buffer on real hardware
- .NET MAUI viability on Urovo i9100 (if team prefers .NET — must validate APK size, performance, and platform API access)
- Ktor embedded server memory and battery footprint under sustained operation

---

*End of Edge Agent HLD — WIP v0.3 (Reconciled)*
