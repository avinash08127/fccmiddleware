# Forecourt Middleware — High Level Requirements

Version: 0.1 (Working Draft — Iterating)
Last Updated: 2026-03-10

------------------------------------------------------------------------

# 1. Legal Entity (Country) Configuration

- Each country = one legal entity.
- Synced from Odoo via Databricks. No CRUD screens.
- Stores: country code, currency, tax authority, fiscalization default, timezone.
- All downstream data (sites, transactions) is scoped to a legal entity.

------------------------------------------------------------------------

# 2. Site Configuration

- Each fuel station is a site, belonging to one legal entity.
- Operating modes: COCO, CODO, DODO, DOCO.
- For dealer-operated sites (CODO/DODO): store operator name + Tax Payer ID.
- Only minimum data stored — Odoo is the master. Middleware stores what it needs to process and route.
- Synced from Odoo via Databricks.

------------------------------------------------------------------------

# 3. FCC Configuration

- Each site may have zero or one active FCC.
- Store: vendor (DOMS/Radix/Advatec/Petronite), connection details, transaction mode (Pull/Push/Hybrid).
- Pump and nozzle mapping per FCC.
- If no FCC → site is disconnected.

------------------------------------------------------------------------

# 4. Connected vs Disconnected Mode

- **Connected**: Middleware communicates with FCC via adapters. Pre-auth and transaction ingestion are automated.
- **Disconnected**: No FCC. Odoo orders are manual. Middleware is not involved in pump operations.
- A connected site can temporarily go **offline** (FCC unreachable) — this is NOT the same as disconnected. Edge Agent handles buffering.

------------------------------------------------------------------------

# 5. Fiscalization

- Three modes: `FCC_DIRECT` (FCC handles it, e.g., Tanzania), `EXTERNAL_INTEGRATION` (separate system, e.g., Malawi/MRA), `NONE`.
- Default set at legal entity level, overridable per site.
- When FCC_DIRECT: customer tax details must be sent with pre-auth. FCC returns fiscal receipt.
- Flag `requireCustomerTaxId` drives whether attendant must capture customer TIN.
- Flag `fiscalReceiptRequired` drives whether middleware expects a receipt back from FCC.

------------------------------------------------------------------------

# 6. Pre-Authorization Orders

- Attendant creates order in Odoo POS → Odoo sends pre-auth to middleware → adapter sends to FCC → pump authorized.
- Customer tax details (TIN, business name) captured in Odoo and passed through to FCC for fiscalization.
- Pre-auth states: PENDING → AUTHORIZED → DISPENSING → COMPLETED / CANCELLED / EXPIRED / FAILED.
- Final dispense comes back via Pull/Push and must be reconciled (see section 8).

------------------------------------------------------------------------

# 7. Unsolicited Transactions (Normal Orders)

- Attendant does NOT create an order in Odoo. Lifts nozzle, dispenses directly.
- FCC records the transaction and sends to middleware via Pull or Push.
- Payloads may be one-by-one or bulk — no flag needed. Adapters handle whatever the FCC sends.
- After dedup and normalization → auto-create order in Odoo (see section 9).

------------------------------------------------------------------------

# 8. Pre-Auth Reconciliation

- Final dispense volume/amount may differ from pre-authorized amount.
- Middleware matches dispense to pre-auth, calculates variance.
- Odoo order is updated with actual quantities.
- Variance within tolerance → auto-approved. Beyond tolerance → flagged for review.
- Unmatched dispenses at pre-auth sites → flagged + still create Odoo order.

------------------------------------------------------------------------

# 9. Automatic Odoo Order Creation

- For unsolicited and unmatched transactions → middleware creates orders in Odoo automatically.
- Idempotent: FCC transaction ID is the idempotency key.
- Retry with backoff on failure. Dead-letter after max retries.
- Full duplicate check before creation.

------------------------------------------------------------------------

# 10. Payload Normalization

- Field mapping is **hardcoded in each adapter** (vendor protocols are structurally different).
- Configurable overrides per deployment: timezone, currency, volume unit, product code mapping, decimal precision.
- Raw FCC payload always preserved alongside canonical model.
- New FCC vendor = new adapter (code change). Config alone is not enough.

------------------------------------------------------------------------

# 11. Master Data Sync

- All master data synced from Odoo via Databricks pipelines.
- No CRUD screens in middleware for master data.
- Entities: legal entities, sites, pumps/nozzles, products, operators.
- Sync API is idempotent. Validates required fields. Tracks freshness.

------------------------------------------------------------------------

# 12. Transaction Ingestion (Pull / Push / Hybrid)

- **Pull**: Middleware polls FCC at configurable intervals. Tracks cursor to avoid re-fetch.
- **Push**: FCC sends to middleware webhook. Acknowledge before processing.
- **Hybrid**: Push primary, Pull as catch-up fallback.
- Configured per FCC.

------------------------------------------------------------------------

# 13. Duplicate Detection

- Primary key: `fccTransactionId` + `siteCode` → silently skip.
- Secondary: pump + nozzle + time + amount → flag for review (not auto-skip).
- Applies to all transaction types.

------------------------------------------------------------------------

# 14. Audit Trail

- All transaction events published to event bus (selective event streaming).
- Immutable event log. Retained per regulatory requirements (default 7 years).

------------------------------------------------------------------------

# 15. Edge Android Agent (HHT)

## Context

The Edge Agent runs on the same **Android HHT** (Handheld Terminal) that already runs **Odoo POS**. These are rugged Android devices carried by fuel attendants at the station.

- Odoo POS connects to Odoo cloud via **internet** (SIM card or WiFi hotspot).
- The Edge Agent connects to the FCC via **local WiFi LAN** at the station.
- Both apps coexist on the same physical device.

## Why an Edge Agent on the HHT

In African fuel retail, station-level internet connectivity is unreliable. SIM data drops, WiFi hotspots fail, power outages reset routers. But the FCC sits on a local network that remains available. The Edge Agent bridges this gap — it keeps talking to the FCC even when the cloud is unreachable, and it buffers everything until connectivity returns.

Running the Edge Agent on the HHT (rather than a separate device) reduces hardware cost, simplifies deployment, and leverages the device the attendant already carries.

## Network Topology

```
                    INTERNET (SIM / WiFi)
                         |
               +---------+---------+
               |                   |
         [Odoo Cloud]        [Cloud Middleware]
               |                   |
               +------- WAN -------+
                         |
                    [ HHT Device ]
                    |            |
              [Odoo POS]   [Edge Agent]
                                |
                         LOCAL WiFi LAN
                                |
                       [Forecourt Controller]
```

## Core Responsibilities

### 15.1 FCC Communication over LAN

- The Edge Agent communicates with the FCC over the station's **local WiFi LAN**.
- It uses the same adapter protocol logic as the cloud middleware (DOMS, Radix, etc.).
- Connection details (FCC IP, port, credentials) are provisioned to the agent during setup.
- The agent maintains a persistent or periodic connection to the FCC depending on the vendor protocol.
- **Heartbeat monitoring**: The agent periodically pings the FCC and reports health status.

### 15.2 Transaction Ingestion (Pull and Push over LAN)

- **Pull mode (LAN)**: The Edge Agent polls the FCC on the local network at a configured interval. This works identically to cloud-based polling but over LAN — lower latency, no internet dependency.
- **Push mode (LAN)**: The Edge Agent exposes a local HTTP listener (or TCP socket, depending on vendor) on the LAN. The FCC pushes transactions directly to the agent.
- The agent normalizes incoming transactions using the same adapter logic and prepares them for upstream relay.

### 15.3 Pre-Auth Relay

- When Odoo POS creates a pre-auth order, it sends the request to the cloud middleware (via internet).
- The cloud middleware forwards the pre-auth command to the Edge Agent (if the site uses an Edge Agent).
- The Edge Agent sends the pre-auth to the FCC over LAN and relays the response back upstream.
- **Offline pre-auth**: If internet is down, Odoo POS can optionally send the pre-auth directly to the Edge Agent's local API on the same device (localhost). The Edge Agent sends it to the FCC over LAN and stores the response locally. When internet returns, the pre-auth record is synced to the cloud.

### 15.4 Offline Transaction Buffering (Store-and-Forward)

- When internet is unavailable, the Edge Agent **continues to ingest transactions from the FCC** over LAN.
- All transactions are buffered in a **local SQLite database** on the HHT.
- Each buffered transaction is stored with: full canonical payload, raw FCC payload, timestamp, sync status (`PENDING`, `SYNCED`, `FAILED`).
- The buffer survives app restarts and device reboots.
- **Buffer capacity**: The agent must handle at least 30 days of transactions for a typical station (estimated 500-1000 transactions/day) without storage issues.

### 15.5 Automatic Replay on Reconnection

- The Edge Agent continuously monitors internet connectivity (ping cloud middleware health endpoint).
- When connectivity is restored, the agent **replays all buffered PENDING transactions** to the cloud middleware in chronological order.
- Replay is idempotent — the cloud middleware handles deduplication (REQ-13).
- Replay uses batched uploads (configurable batch size, e.g., 50 transactions per request) to avoid overwhelming the cloud endpoint.
- Failed replays are retried with exponential backoff. The agent does not skip ahead — it maintains order.
- Once a transaction is confirmed synced, its status is updated to `SYNCED` in the local buffer.

### 15.6 Local API for Odoo POS (Offline Mode)

- The Edge Agent exposes a **local REST API on localhost** (e.g., `http://localhost:8585`) that Odoo POS on the same HHT can call.
- This API provides:

| Endpoint | Description |
|----------|-------------|
| `GET /api/transactions` | Fetch recent transactions from the local buffer (paginated, filterable by time range, pump, product) |
| `GET /api/transactions/{id}` | Fetch a specific transaction |
| `GET /api/pump-status` | Get current pump statuses from FCC (live, over LAN) |
| `POST /api/preauth` | Submit a pre-auth request locally (offline pre-auth) |
| `POST /api/preauth/{id}/cancel` | Cancel a local pre-auth |
| `GET /api/status` | Agent health: FCC connectivity, internet status, buffer depth, last sync time |

- **When internet is available**: Odoo POS talks to the cloud as normal. The local API is available but not the primary path.
- **When internet is down**: Odoo POS detects the outage and switches to the local Edge Agent API for transaction visibility and pre-auth (if needed).
- The switch should be **automatic** based on internet availability detection in Odoo POS, with a manual override option for the attendant.

### 15.7 Attendant-Triggered Manual Pull

- The attendant can trigger an on-demand Pull from the FCC via the Edge Agent.
- This is useful when the attendant wants to immediately see a transaction that was just dispensed, rather than waiting for the next poll cycle.
- Triggered via a button in Odoo POS that calls the Edge Agent's local API.
- The manual pull result is returned to Odoo POS and also stored in the local buffer.

### 15.8 Multi-HHT Site Handling

At a site with multiple HHTs (multiple attendants), only **one HHT should act as the Edge Agent** for FCC communication to avoid conflicts:

- **Primary Agent Election**: On setup, one HHT at each site is designated as the primary Edge Agent. This is a configuration choice (not automatic election, to keep it simple for MVP).
- **Other HHTs**: Run Odoo POS only. They interact with the cloud middleware (when online) or can query the primary Edge Agent's local API over the station LAN (when offline).
- **Failover (Post-MVP)**: If the primary HHT goes offline, a secondary HHT can be manually promoted. Automatic failover is complex and deferred.

### 15.9 Connectivity Detection and Mode Switching

The Edge Agent maintains awareness of three connectivity states:

| State | Internet | FCC LAN | Behaviour |
|-------|----------|---------|-----------|
| **Fully Online** | Up | Up | Relay transactions to cloud in real-time. Odoo POS uses cloud. |
| **Internet Down** | Down | Up | Buffer transactions locally. Odoo POS uses local API. |
| **FCC Unreachable** | Up or Down | Down | Alert site supervisor. No transactions to ingest. Log connectivity gap. |
| **Fully Offline** | Down | Down | No ingestion possible. Odoo POS operates in manual mode. Alert on recovery. |

- Internet detection: Periodic ping to cloud middleware health endpoint (e.g., every 30 seconds).
- FCC detection: Periodic heartbeat to FCC (vendor-specific, e.g., every 15 seconds).
- Mode transitions are logged as events for audit.

### 15.10 Security

- FCC credentials are stored **encrypted** on the HHT device (Android Keystore).
- The local REST API binds to **localhost only** by default (only Odoo POS on the same device can access it).
- If other HHTs need to access the primary agent's API over LAN, the API must require an **API key** (provisioned during setup).
- Cloud-to-agent communication uses mutual TLS or API key authentication.
- The agent does not store Odoo user credentials. It authenticates to the cloud middleware with a device-level service token.

### 15.11 Provisioning and Configuration

- The Edge Agent is installed as a separate Android app (APK) on the HHT.
- Initial configuration is provided via:
  - **QR code scan** (containing site code, FCC connection details, cloud middleware URL, device token) — preferred for field deployment.
  - **Manual entry** in a setup screen (fallback).
  - **Cloud push** — the cloud middleware pushes config to the agent after registration (for updates).
- Configuration includes: site code, FCC vendor, FCC IP/port, FCC credentials, cloud middleware URL, device token, poll interval, buffer settings.
- **Remote config updates**: The cloud middleware can push updated configuration (e.g., new poll interval, new FCC IP) to the agent. The agent checks for config updates on each cloud sync.

### 15.12 Monitoring and Diagnostics

- The Edge Agent reports telemetry to the cloud middleware when online:
  - FCC connectivity status and last heartbeat
  - Buffer depth (number of pending transactions)
  - Last successful cloud sync timestamp
  - Device battery level and storage availability
  - App version
- The cloud middleware dashboard shows per-site agent health.
- Local diagnostics screen on the HHT (accessible to Site Supervisor) showing:
  - FCC connection status
  - Internet status
  - Buffer depth
  - Last sync time
  - Manual pull button
  - Logs (last 100 entries)

### 15.13 Update and Lifecycle Management

- Agent app updates are distributed via **MDM (Mobile Device Management)** or a managed app store (e.g., Google Play Managed, or enterprise sideload).
- The agent should be **backward-compatible** with the cloud middleware — i.e., an older agent version should still work with a newer cloud deployment (within a supported version range).
- The cloud middleware exposes a `/agent/version-check` endpoint that the agent calls on startup to check compatibility.
- If the agent version is below the minimum supported, it alerts the site supervisor and disables FCC communication until updated (to prevent data format mismatches).

### 15.14 Data Integrity and Recovery

- The SQLite buffer uses **WAL mode** for crash resilience.
- On app startup, the agent runs a buffer integrity check (SQLite `PRAGMA integrity_check`).
- If corruption is detected, the agent creates a backup of the corrupted database, starts a fresh buffer, and alerts the cloud middleware. The corrupted backup can be retrieved for forensic analysis.
- Transactions in the buffer are **never deleted** until confirmed synced to the cloud. Even after sync, they are retained locally for a configurable period (e.g., 7 days) before cleanup.

------------------------------------------------------------------------

# 16. Error Handling and Retry

- All retryable failures: exponential backoff with jitter.
- Non-retryable failures (parse errors, unknown sites): flag immediately.
- Dead-letter queue for exhausted retries.
- Alerts via configurable channels.

------------------------------------------------------------------------

# 17. Multi-Tenancy

- All data partitioned by legal entity.
- Row-level isolation for MVP.
- API calls scoped to legal entity context.

------------------------------------------------------------------------

# Open Questions

| ID | Question | Status |
|----|----------|--------|
| OQ-1 | Edge Agent tech stack — .NET MAUI on Android, or native Kotlin/Java with shared adapter logic? | Open |
| OQ-2 | Odoo POS offline workflow — does Odoo POS have native offline order creation, or does it depend entirely on the Edge Agent for transaction data when offline? | Open |
| OQ-3 | If Odoo POS can create orders offline and sync later, how do we reconcile Odoo-created offline orders with Edge Agent-buffered FCC transactions? | Open |
| OQ-4 | FCC adapter packaging — can the same .NET adapter libraries run on Android (via MAUI), or do we need separate lightweight adapter implementations? | Open |
| OQ-5 | How many HHTs per site on average? Does every attendant have one, or are they shared? | Open |
| OQ-6 | Is there a site-level router/access point that is always on (for LAN to FCC), even during internet outages? Or does the LAN also depend on the same router that provides internet? | Open |
| OQ-7 | For multi-HHT sites in offline mode, should non-primary HHTs be able to query the primary agent over station LAN, or only the HHT running the agent can see transactions? | Open |
| OQ-8 | What is the expected transaction volume per site per day? This affects buffer sizing and replay strategy. | Open |
| OQ-9 | Are there sites where both pre-auth and unsolicited transactions happen (mixed mode), or is it one or the other per site? | Open |
| OQ-10 | What MDM solution is in use for HHT fleet management? | Open |

------------------------------------------------------------------------

# MVP vs Post-MVP Summary

| Area | MVP | Post-MVP |
|------|-----|----------|
| Legal entity + site config | Yes | — |
| DOMS adapter | Yes | Radix, Advatec, Petronite |
| Connected / Disconnected | Yes | — |
| Fiscalization config | Yes | — |
| Pre-auth flow | Yes | — |
| Unsolicited transactions | Yes | — |
| Reconciliation | Yes | Dashboard (Phase 4) |
| Odoo order creation | Yes | — |
| Payload normalization (DOMS) | Yes | Other vendors |
| Master data sync (Databricks) | Yes | — |
| Pull / Push ingestion | Yes | — |
| Duplicate detection | Yes | — |
| Audit trail | Yes | — |
| Edge Agent: LAN comms | Yes | — |
| Edge Agent: Offline buffer + replay | Yes | — |
| Edge Agent: Local API for Odoo POS | Yes | — |
| Edge Agent: Multi-HHT failover | No | Yes |
| Edge Agent: OTA updates | No | Yes |
| Edge Agent: Advanced diagnostics | No | Yes |
| Admin portal (Angular) | No | Yes |
| Horizontal auto-scaling | No | Yes |
