# Forecourt Middleware — High Level Requirements

Version: 0.3 (Working Draft — Iterating)
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
- **Pump and nozzle mapping per site**: Odoo numbers pumps and nozzles independently of the FCC vendor. Each pump record stores both an `odoo_pump_number` (what Odoo POS sends) and an `fcc_pump_number` (what is forwarded to the FCC). Each nozzle record stores `odoo_nozzle_number` → `fcc_nozzle_number` and the product (fuel grade) dispensed by that nozzle.
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

- Pre-auth is used when a customer requests a **fiscalized invoice with their Tax ID**. It is not the default flow — most transactions are Normal Orders (see section 7).
- Attendant creates order in Odoo POS (captures product, **amount**, pump, nozzle, customer TIN) → Odoo sends pre-auth to the **Edge Agent** (via localhost API, over the station LAN) → Edge Agent **translates the Odoo pump/nozzle numbers to FCC pump/nozzle numbers** using its local nozzle mapping table → Edge Agent sends pre-auth to FCC over LAN → pump authorized.
- **Pre-auth is always authorized by amount** (local currency). The FCC authorizes the pump to dispense up to the requested monetary value. Volume is not sent to the FCC for authorization.
- This flow works in **both online and offline modes** because it operates entirely over LAN — no internet is required for the authorization itself.
- The Edge Agent **queues the pre-auth record to the Cloud Middleware** for reconciliation tracking. This ensures the cloud can match the final dispense transaction when it arrives via the FCC's direct push. The queue-and-forward happens asynchronously and retries when internet is available.
- Customer tax details (TIN, business name) captured in Odoo and passed through to FCC for fiscalization.
- Pre-auth states: PENDING → AUTHORIZED → DISPENSING → COMPLETED / CANCELLED / EXPIRED / FAILED.
- Final dispense comes back via FCC→Cloud push (primary). The FCC returns the **actual dispensed volume** — this becomes the Odoo Order quantity (see section 8).

------------------------------------------------------------------------

# 7. Normal Orders (FCC-Initiated Transactions)

> **Terminology note**: "Unsolicited" and "Solicited" are FCC-level terms referring to how the FCC surfaces transactions (Push vs Pull). At the application level we use **Normal Orders** to describe transactions not initiated through Odoo — the attendant lifts the nozzle and dispenses directly without creating an Odoo order first.

- Attendant does NOT create an order in Odoo. Lifts nozzle, dispenses directly.
- FCC records the transaction and **pushes it directly to the Cloud Middleware** (primary ingestion path). The FCC is configured with the Cloud Middleware endpoint as its push target.
- As a safety net, the **Edge Agent also polls the FCC over LAN** to catch any transactions the cloud might have missed (e.g., if the cloud push failed or the FCC queued transactions during an outage). These are uploaded to the cloud, which deduplicates them transparently.
- Payloads may be one-by-one or bulk — no flag needed. Adapters handle whatever the FCC sends.
- After dedup and normalization → transaction is stored with status PENDING, available for Odoo to poll (see section 9).
- Normal orders are the predominant transaction type at most sites. Pre-auth is reserved for customers requesting a fiscalized invoice with their Tax ID (see section 6).

------------------------------------------------------------------------

# 8. Pre-Auth Reconciliation

- Final dispense transaction from FCC contains **actual dispensed volume** (litres) and **actual amount** (local currency).
- Middleware matches dispense to pre-auth, calculates **amount variance** (`actualAmount - authorizedAmount`).
- **Odoo Order quantity = actual dispensed volume** from the FCC. The authorized amount was only used to authorize the pump — volume authorization does not exist.
- Amount variance within tolerance → auto-approved. Exceeds tolerance → flagged for Ops Manager review.
- Regardless of variance, the transaction is stored as PENDING for Odoo to poll and create the order with actual figures.
- Unmatched dispenses at pre-auth sites → flagged for investigation + still stored as PENDING for Odoo to poll.

------------------------------------------------------------------------

# 9. Odoo Order Creation (Odoo-Polled Model)

- The middleware **stores** transactions. It does NOT call Odoo to create orders — Odoo pulls transactions from the middleware and creates orders itself.
- **Online path**: Odoo polls the **Cloud Middleware** (`GET /transactions?status=PENDING`) on a schedule or via a manual bulk-create operation triggered by the Ops team. Odoo creates the orders, then acknowledges the transaction IDs back to the Cloud Middleware. The Cloud Middleware marks acknowledged transactions `SYNCED_TO_ODOO`.
- **Offline path**: When internet is down, Odoo POS detects the outage and switches to polling the **Edge Agent's local API** (`GET /api/transactions`). Odoo creates orders from the buffered transactions available there. When internet is restored, Odoo switches back to polling the Cloud Middleware. The idempotency key (`fccTransactionId`) prevents duplicate order creation for any transaction Odoo already created during the outage.
- **Manual bulk-create**: The Ops team can trigger a bulk poll at any time (e.g., end-of-shift) to process all pending transactions in one operation.
- Idempotent: `fccTransactionId` is the idempotency key in Odoo.
- The Cloud Middleware marks transactions `SYNCED_TO_ODOO` after Odoo acknowledges them. The Edge Agent polls this status and will not serve already-acknowledged transactions to Odoo via the local API.

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
- Entities: legal entities, sites, pumps, nozzles (with Odoo↔FCC number mappings and product assignments), products, operators.
- Sync API is idempotent. Validates required fields. Tracks freshness.

------------------------------------------------------------------------

# 12. Transaction Ingestion (Pull / Push / Hybrid)

- **Pull**: Middleware (cloud or Edge Agent) polls FCC at configurable intervals. Tracks cursor to avoid re-fetch.
- **Push**: FCC sends transactions to a configured endpoint. In the default architecture, this is the **Cloud Middleware** endpoint. Acknowledge before processing.
- **Hybrid**: Push primary, Pull as catch-up fallback.
- Configured per FCC.

> **Important constraint**: Most FCC vendors can only be configured to push/send to **one endpoint**. Some FCC vendors also do not expose a Pull API. The ingestion architecture must account for this.

## Ingestion Routing Mode (configurable per FCC)

This controls **where** the FCC is configured to send data. Configured alongside Pull/Push mode.

| Mode | Default | Description |
|------|---------|-------------|
| `CLOUD_DIRECT` | **Yes** | **FCC is configured to push/send directly to the Cloud Middleware endpoint.** Edge Agent polls FCC over LAN as a catch-up safety net and uploads any missed transactions to the cloud. Deduplication at the cloud handles dual-path arrivals transparently. Works for any FCC that can reach the cloud endpoint. |
| `RELAY` | No | Edge Agent is configured as the FCC's push/pull target. Edge Agent relays to cloud in real-time when internet is available; buffers locally if not. Used only when the FCC cannot reach the cloud directly (e.g., isolated private network, no VPN). |
| `BUFFER_ALWAYS` | No | Like RELAY, but Edge Agent always buffers locally first, then syncs on schedule. Used for high-volume sites or constrained connectivity where real-time relay is not practical. |

**Default behaviour**: `CLOUD_DIRECT`. The FCC sends directly to the cloud. The Edge Agent's role in the ingestion path is a **safety-net LAN poller**, not a primary receiver. Pre-auth always goes via Edge Agent regardless of ingestion mode (see section 6).

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

- **Device**: Urovo i9100, running **Android 12**.
- **Tech stack**: Native **Kotlin/Java** Android application.
- Odoo POS connects to Odoo cloud via **internet** (SIM card or WiFi hotspot).
- The Edge Agent connects to the FCC via **local WiFi LAN** at the station.
- Both apps coexist on the same physical device.
- The FCC adapter logic (protocol handling for DOMS, Radix, etc.) is implemented **within the Edge Agent** — the adapter is not a separate cloud-only component.

## Why an Edge Agent on the HHT

In African fuel retail, station-level internet connectivity is unreliable. SIM data drops, WiFi hotspots fail, power outages reset routers. But the FCC sits on a local network that **remains available independently** — the station LAN is always on even during internet outages. The Edge Agent bridges this gap — it keeps talking to the FCC even when the cloud is unreachable, and it buffers everything until connectivity returns.

Running the Edge Agent on the HHT (rather than a separate device) reduces hardware cost, simplifies deployment, and leverages the device the attendant already carries.

## Network Topology

```
[Forecourt Controller]
    │
    ├── Push (primary) ──────────────────────────► [Cloud Middleware] ──► [Odoo Cloud]
    │                                                       ▲  │
    │                                              pre-auth │  │ SYNCED_TO_ODOO status
    │                                                       │  ▼
    └── Poll LAN (catch-up) ◄──────────────── [Edge Agent] ◄──┘
                                                    │
                                    INTERNET (SIM / WiFi)
                                               +---+---+
                                               │       │
                                         online│  offline│
                                               ▼       ▼
                                          [Cloud]  [Edge Agent]
                                               │       │
                                               └───┬───┘
                                            [Odoo POS]

Pre-Auth flow (always over LAN):
  [Odoo POS] ──► [Edge Agent] ──► [FCC]
  [Edge Agent] ──► [Cloud Middleware] (queues pre-auth for reconciliation)
```

All devices on the station LAN. Internet outages do not affect LAN connectivity.

## Core Responsibilities

### 15.1 FCC Communication over LAN

- The Edge Agent communicates with the FCC over the station's **local WiFi LAN**.
- It uses the same adapter protocol logic as the cloud middleware (DOMS, Radix, etc.).
- Connection details (FCC IP, port, credentials) are provisioned to the agent during setup.
- The agent maintains a persistent or periodic connection to the FCC depending on the vendor protocol.
- **Heartbeat monitoring**: The agent periodically pings the FCC and reports health status.

### 15.2 LAN Catch-Up Poll (Safety Net)

- In the default `CLOUD_DIRECT` ingestion mode, the FCC pushes transactions directly to the cloud. The Edge Agent is **not** the primary receiver.
- The Edge Agent **polls the FCC over LAN at a configured interval** as a catch-up safety net. This catches any transactions the cloud may have missed (e.g., if the FCC's cloud push failed or queued during an outage).
- Catch-up transactions collected by the Edge Agent are forwarded to the cloud middleware. The cloud deduplicates any overlap with transactions already received via the FCC's direct push.
- If the cloud is unreachable when the Edge Agent polls, the transactions are **buffered locally** and uploaded when internet returns.
- In `RELAY` or `BUFFER_ALWAYS` ingestion modes, the Edge Agent is the primary receiver and this section describes the full ingestion path (see §12).

### 15.3 Pre-Auth Relay

- Odoo POS **always** sends pre-auth requests to the **Edge Agent's local API** (localhost on the same HHT). There is no cloud routing for pre-auth — the Edge Agent is always the handler.
- The Edge Agent sends the pre-auth command to the FCC over LAN and receives the authorization response.
- The Edge Agent **queues the pre-auth record to the Cloud Middleware** asynchronously (for reconciliation). This ensures the cloud can match the final dispense transaction when it arrives via the FCC's direct push.
- The queue-and-forward retries when internet is available. Pre-auth authorization itself never requires internet — it operates entirely over LAN.

### 15.3a Transaction Status Sync (SYNCED_TO_ODOO)

- The Cloud Middleware marks transactions as `SYNCED_TO_ODOO` after **Odoo acknowledges** that it has created an order for the transaction (via the acknowledge endpoint).
- The Edge Agent **periodically polls the Cloud Middleware** to fetch the `SYNCED_TO_ODOO` status for transactions it holds in its local buffer.
- Transactions marked `SYNCED_TO_ODOO` in the local buffer are **not offered to Odoo POS** via the local API. This prevents Odoo from processing a transaction it has already handled when it was polling the Cloud Middleware online.
- This sync runs on the same connectivity check cycle as the cloud health ping.

### 15.4 Offline Transaction Buffering (Store-and-Forward)

- When internet is unavailable, the Edge Agent **continues to ingest transactions from the FCC** over LAN.
- All transactions are buffered in a **local SQLite database** on the HHT.
- Each buffered transaction is stored with: full canonical payload, raw FCC payload, timestamp, sync status (`PENDING`, `SYNCED`, `FAILED`).
- The buffer survives app restarts and device reboots.
- **Buffer capacity**: The agent must handle at least 30 days of transactions for a typical station. Busy sites process up to **1,000 transactions/day** — buffer must be sized accordingly (minimum 30,000 transactions without storage issues on the Urovo i9100).

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

At a site with multiple HHTs (multiple attendants), one Desktop Edge Agent and one or more Android Edge Agents may run in parallel:

- **Single-primary invariant**: Exactly one eligible online agent is `PRIMARY` at a time. All others remain `STANDBY_HOT`, `RECOVERING`, or `READ_ONLY`.
- **Priority policy**: Prefer Desktop first where available, then Android agents by configured priority. The policy is configuration-driven per site.
- **Shared visibility**: All HHTs at a site must see the **same transaction data**. When an attendant creates an order (pre-auth or normal), they select the pump and nozzle. Every Android HHT continues to use `localhost`; the local agent proxies or serves replicated state as needed.
- **LAN access**: Peer traffic still uses the station LAN and must be authenticated. The Android localhost contract stays stable for Odoo POS.
- **Failover**: Automatic failover promotes a warm, healthy standby within 30 seconds of confirmed primary failure. A recovered former primary rejoins as standby and does not auto-preempt.

### 15.9 Connectivity Detection and Mode Switching

The Edge Agent maintains awareness of three connectivity states. Behaviour below reflects the default `CLOUD_DIRECT` ingestion mode.

| State | Internet | FCC LAN | Behaviour (CLOUD_DIRECT — default) |
|-------|----------|---------|-------------------------------------|
| **Fully Online** | Up | Up | FCC pushes transactions to Cloud. Edge Agent polls FCC over LAN as catch-up, forwards any missed transactions to cloud. Cloud stores transactions (status: PENDING). **Odoo polls Cloud Middleware** and creates orders; acknowledges back to cloud. Edge Agent syncs `SYNCED_TO_ODOO` status from cloud. |
| **Internet Down** | Down | Up | FCC push to cloud will fail or queue at FCC. Edge Agent continues polling FCC over LAN and **buffers transactions locally**. **Odoo POS switches to polling Edge Agent local API** and creates orders from buffered transactions. Pre-auth still works (LAN only). On recovery, Edge Agent uploads buffered transactions; cloud deduplicates; Odoo switches back to polling Cloud Middleware. |
| **FCC Unreachable** | Up or Down | Down | Alert site supervisor. No LAN catch-up possible. Normal FCC-to-cloud push may still work if FCC is reachable from cloud. Log LAN connectivity gap. |
| **Fully Offline** | Down | Down | No ingestion possible. Odoo POS operates in manual mode. Alert on recovery. |

> For `RELAY` or `BUFFER_ALWAYS` mode: the Edge Agent is the primary FCC receiver; see §12 for full behaviour differences.

- Internet detection: Periodic ping to cloud middleware health endpoint (e.g., every 30 seconds).
- FCC detection: Periodic heartbeat to FCC (vendor-specific, e.g., every 15 seconds).
- Mode transitions are automatic and logged as events for audit.

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

- Agent app updates are distributed via **Sure MDM** (the MDM solution in use for the HHT fleet) or enterprise sideload as a fallback.
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

| ID | Question | Status | Answer |
|----|----------|--------|--------|
| OQ-1 | Edge Agent tech stack — .NET MAUI on Android, or native Kotlin/Java with shared adapter logic? | **Resolved** | Native **Kotlin/Java** Android app. |
| OQ-2 | Odoo POS offline workflow — does Odoo POS have native offline order creation, or does it depend entirely on the Edge Agent for transaction data when offline? | **Resolved** | Odoo has its own offline capability. However, once the Edge Agent is operational and communicating with Odoo in offline mode, Odoo will always pull from the Edge Agent / FCC transaction data. Odoo will **not** create independent orders. |
| OQ-3 | If Odoo POS can create orders offline and sync later, how do we reconcile Odoo-created offline orders with Edge Agent-buffered FCC transactions? | **Resolved** | Once the Edge Agent is live, Odoo will always create orders from FCC transactions — never independently. Reconciliation of legacy Odoo-only offline orders is not in scope. |
| OQ-4 | FCC adapter packaging — can the same .NET adapter libraries run on Android (via MAUI), or do we need separate lightweight adapter implementations? | **Resolved** | The FCC adapter is part of the Edge Agent (Kotlin/Java). Adapter logic is implemented natively on the device — not shared from a .NET library. |
| OQ-5 | How many HHTs per site on average? Does every attendant have one, or are they shared? | **Resolved** | Busy sites have multiple HHTs (one per attendant). Plan for multi-HHT sites. |
| OQ-6 | Is there a site-level router/access point that is always on (for LAN to FCC), even during internet outages? Or does the LAN also depend on the same router that provides internet? | **Resolved** | **Station LAN is always on**, independent of internet. Internet outages do not affect LAN connectivity to the FCC. |
| OQ-7 | For multi-HHT sites in offline mode, should non-primary HHTs be able to query the primary agent over station LAN, or only the HHT running the agent can see transactions? | **Resolved** | All HHTs must see the **same transaction data**. Non-primary HHTs query the primary Edge Agent over the station LAN. Attendants always select pump and nozzle when creating orders — this applies to Normal Orders too. |
| OQ-8 | What is the expected transaction volume per site per day? This affects buffer sizing and replay strategy. | **Resolved** | Up to **1,000 transactions/day** at busy sites. Buffer must support at least 30 days (30K+ transactions). |
| OQ-9 | Are there sites where both pre-auth and unsolicited transactions happen (mixed mode), or is it one or the other per site? | **Resolved** | **Normal Orders are the default** at all sites. Pre-auth is used only when a customer requests a fiscalized invoice with their Tax ID. Mixed mode at any site is expected. |
| OQ-10 | What MDM solution is in use for HHT fleet management? | **Resolved** | **Sure MDM**. |

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
