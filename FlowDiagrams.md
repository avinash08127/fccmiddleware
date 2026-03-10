# Forecourt Middleware — Flow Diagrams

Version: 1.0
Last Updated: 2026-03-10

------------------------------------------------------------------------

# Table of Contents

1. [Scenario 1 — Normal Order, Fully Online](#scenario-1--normal-order-fully-online)
2. [Scenario 2 — Normal Order, Internet Down](#scenario-2--normal-order-internet-down)
3. [Scenario 3 — Pre-Auth, Online](#scenario-3--pre-auth-online)
4. [Scenario 4 — Pre-Auth, Internet Down](#scenario-4--pre-auth-internet-down)
5. [Scenario 5 — Pre-Auth Reconciliation Detail](#scenario-5--pre-auth-reconciliation-detail)
6. [Scenario 6 — Multi-HHT Site, Internet Down](#scenario-6--multi-hht-site-internet-down)
7. [Scenario 7 — SYNCED_TO_ODOO Cleanup Flow](#scenario-7--synced_to_odoo-cleanup-flow)

------------------------------------------------------------------------

# Scenario 1 — Normal Order, Fully Online

> Default flow. FCC pushes directly to Cloud. Edge Agent runs a parallel LAN catch-up poll. Cloud stores the transaction. **Odoo polls the Cloud Middleware and creates the order itself** — either automatically on a schedule or via a manual bulk-create operation. Cloud marks the transaction `SYNCED_TO_ODOO` once Odoo acknowledges. Edge Agent respects this status.

```
Attendant lifts nozzle, dispenses fuel
         │
         ▼
    [FCC records transaction]
         │
         ├─── Push (primary) ──────────────────────────────────► [Cloud Middleware]
         │                                                              │
         │                                                    deduplicate + normalize
         │                                                              │
         │                                                    store transaction
         │                                                    status: PENDING
         │                                                              │
         │                                                              │◄── [Odoo] polls GET /transactions
         │                                                              │    (scheduled or manual bulk)
         │                                                              │
         │                                                    [Odoo] creates order in Odoo
         │                                                              │
         │                                                    [Odoo] acknowledges to Cloud
         │                                                              │
         │                                                    mark SYNCED_TO_ODOO
         │
         └─── (parallel) Edge Agent polls FCC over LAN
                         │
                         │  gets same transaction
                         │
                         ▼
                  [Edge Agent] ──upload──► [Cloud Middleware]
                         ▲                       │
                         │                 DUPLICATE → silently skip
                         │                       │
                         └── polls SYNCED_TO_ODOO status ◄─────────────┘
                                         │
                               marks local buffer entry as SYNCED_TO_ODOO
                               (will not offer to Odoo POS via local API)
```

------------------------------------------------------------------------

# Scenario 2 — Normal Order, Internet Down

> Internet outage. FCC push to cloud fails. Edge Agent polls FCC over LAN and buffers locally. **Odoo polls the Edge Agent local API** for transactions and creates orders itself. On recovery, both FCC and Edge Agent upload; cloud deduplicates; Odoo switches back to polling the Cloud Middleware.

```
Phase 1: Internet is DOWN, FCC LAN is UP
─────────────────────────────────────────

Attendant lifts nozzle, dispenses fuel
         │
         ▼
    [FCC records transaction]
         │
         ├─── Push to Cloud ──► FAILS (FCC queues internally, will retry)
         │
         └─── Edge Agent polls FCC over LAN ──► gets transaction
                         │
                         │  tries to upload to cloud ──► FAILS (internet down)
                         │
                         ▼
                 [Edge Agent local buffer]
                 status: PENDING
                         │
                         │◄── [Odoo POS] polls GET /api/transactions
                         │    (Odoo POS has auto-switched to Edge Agent local API)
                         │
                 [Odoo POS] creates order from transaction data


Phase 2: Internet RESTORED
──────────────────────────

         [FCC] ──── flushes queued push ──────────────────────► [Cloud Middleware]
                                                                        │
         [Edge Agent] ── uploads PENDING buffer ──────────────► [Cloud Middleware]
                                                                        │
                                                          deduplicate both arrivals
                                                          (one is silently skipped)
                                                                        │
                                                          store transactions
                                                          status: PENDING
                                                                        │
                                                          [Odoo] polls Cloud Middleware
                                                          (switches back from Edge Agent)
                                                                        │
                                                          [Odoo] creates/acknowledges orders
                                                          (idempotency key prevents duplicates
                                                           for orders already created offline)
                                                                        │
                                                          mark SYNCED_TO_ODOO
                                                                        │
         [Edge Agent] ◄──── polls SYNCED_TO_ODOO status ──────────────┘
                    │
          updates local buffer entries → SYNCED_TO_ODOO
          (no longer offered to Odoo POS via local API)
```

------------------------------------------------------------------------

# Scenario 3 — Pre-Auth, Online

> Customer requests fiscalized invoice with Tax ID. Pre-auth always goes via Edge Agent over LAN — internet availability is irrelevant for authorization. Edge Agent queues pre-auth to cloud for reconciliation. **Odoo polls the Cloud Middleware to retrieve the reconciled transaction and creates/updates the order itself.**

```
1. Attendant creates order in Odoo POS
   (captures: product, amount, pump, nozzle, customer TIN if required)
   Note: pre-auth is ALWAYS by amount — volume is not authorized
         │
         ▼
2. [Odoo POS] ──► POST /api/preauth ──► [Edge Agent local API] (localhost)
                                                │
                                                │  (always over LAN, no internet needed)
                                                ▼
3.                                        [FCC] ◄── pre-auth command (pump, requestedAmount, TIN)
                                                │    authorization by amount only — no volume sent
                                                │
                                                │  FCC authorizes pump up to requestedAmount
                                                ▼
4.                                    authorization response
                                                │
                                                ▼
5.                                    [Edge Agent] stores pre-auth locally
                                                │
                                    returns confirmation to Odoo POS ──► attendant sees "AUTHORIZED"
                                                │
                                                │ (async, internet available)
                                                ▼
6.                                    [Edge Agent] ──► POST pre-auth record ──► [Cloud Middleware]
                                                                                        │
                                                                              stores for reconciliation

7. Attendant dispenses fuel (actual volume dispensed may differ from estimated)
         │
         ▼
8. [FCC records final dispense]
   FCC returns: actualVolume (litres) + actualAmount (currency)
         │
         └── Push ──► [Cloud Middleware]
                              │
                      match dispense to pre-auth record (step 6)
                              │
                      calculate AMOUNT variance (actualAmount - requestedAmount)
                              │
                      variance ≤ tolerance? ── Yes ──► auto-approve
                              │                              │
                              No                             │
                              ▼                             ▼
                     flag for Ops Manager      store reconciled transaction
                     (still stores PENDING)    actualVolume = Odoo Order quantity
                                               actualAmount = Odoo Order value
                                               status: PENDING
                                                        │
                                               [Odoo] polls GET /transactions
                                               (scheduled or manual bulk)
                                                        │
                                               [Odoo] creates order:
                                               quantity = actualVolume (litres)
                                               value = actualAmount
                                                        │
                                               [Odoo] acknowledges to Cloud
                                                        │
                                               mark SYNCED_TO_ODOO
```

------------------------------------------------------------------------

# Scenario 4 — Pre-Auth, Internet Down

> Same as Scenario 3 but internet is down throughout. Pre-auth authorization is unaffected (LAN only). The pre-auth record and the final dispense transaction are both held locally until internet returns, then uploaded and reconciled at the cloud. **Odoo polls the Edge Agent local API during the outage, then switches back to polling the Cloud Middleware on recovery.**

```
Steps 1–5: Same as Scenario 3 — unaffected by internet outage (all over LAN)

         [Odoo POS] ──► [Edge Agent] ──► [FCC] ──► authorized ──► Odoo POS sees "AUTHORIZED"

6. [Edge Agent] tries to queue pre-auth to Cloud ──► FAILS (internet down)
         │
         └── pre-auth record queued locally for retry

7. Attendant dispenses fuel
         │
         ▼
8. [FCC records final dispense]
         │
         ├── Push to Cloud ──► FAILS (internet down, FCC queues)
         │
         └── Edge Agent polls FCC over LAN ──► gets dispense transaction
                         │
                         │  tries to upload ──► FAILS (internet down)
                         │
                         ▼
                 [Edge Agent local buffer]
                 (both pre-auth record + dispense transaction held locally)
                         │
                 [Odoo POS] polls Edge Agent local API (offline mode)
                 [Odoo POS] can see dispense transaction in local buffer


Phase: Internet RESTORED
────────────────────────

         [Edge Agent] ──► forwards queued pre-auth record ──────────────► [Cloud Middleware]
                                                                                   │
         [Edge Agent] ──► uploads buffered dispense transaction ─────────► [Cloud Middleware]
                                                                                   │
         [FCC] ──────────► flushes queued dispense push ──────────────────► [Cloud Middleware]
                                                                                   │
                                                                    deduplicate dispense arrivals
                                                                                   │
                                                                    match dispense to pre-auth record
                                                                                   │
                                                                    reconcile variance
                                                                                   │
                                                                    store reconciled transaction
                                                                    actualVolume = Odoo Order quantity
                                                                    actualAmount = Odoo Order value
                                                                    status: PENDING
                                                                                   │
                                                                    [Odoo] polls Cloud Middleware
                                                                    (switches back from Edge Agent)
                                                                                   │
                                                                    [Odoo] creates order:
                                                                    quantity = actualVolume (litres)
                                                                    value = actualAmount
                                                                    (idempotency prevents duplicates
                                                                     for orders created offline)
                                                                                   │
                                                                    [Odoo] acknowledges to Cloud
                                                                    mark SYNCED_TO_ODOO
```

------------------------------------------------------------------------

# Scenario 5 — Pre-Auth Reconciliation Detail

> How the Cloud Middleware matches a final dispense transaction to its pre-auth record and handles variance. Pre-auth is always authorized by amount; the actual dispensed volume from the FCC becomes the Odoo Order quantity.

```
Cloud Middleware receives a dispense transaction
FCC payload includes: actualVolume (litres) + actualAmount (currency)
         │
         ▼
Is this site a pre-auth site?
         │
    No ──┘                    Yes
    │                          │
    ▼                          ▼
Normal Order           Look for matching pre-auth record
processing             using (in priority order):
(Scenario 1/2)         1. FCC correlation ID
                       2. pump + nozzle + time window (±configurable)
                       3. odooOrderId echoed by FCC (if supported)
                              │
              ┌───────────────┴───────────────┐
              │                               │
           MATCHED                       NOT MATCHED
              │                               │
   calculate AMOUNT variance:       flag as UNMATCHED
   actualAmount - requestedAmount   store as PENDING (treat as normal order)
              │                     alert Ops Manager
   ┌──────────┴──────────┐
   │                     │
within tolerance      exceeds tolerance
(e.g. ±2%)
   │                     │
auto-approve         flag for Ops Manager review
   │                     │
   └──────────┬──────────┘
              │
   store reconciled transaction as PENDING:
   Odoo Order quantity = actualVolume (litres)   ← from FCC dispense
   Odoo Order value    = actualAmount (currency) ← from FCC dispense
   (authorized amount was for pump control only)
              │
   pre-auth state → COMPLETED
              │
   create reconciliation record
   (authorizedAmount vs actualAmount)
              │
   [Odoo polls and creates order with actualVolume as quantity]
              │
   mark transaction SYNCED_TO_ODOO
```

------------------------------------------------------------------------

# Scenario 6 — Multi-HHT Site, Internet Down

> Station with multiple HHTs. Only HHT-1 runs the Edge Agent (primary). Non-primary HHTs query HHT-1's LAN API when offline. Pre-auth from any HHT is routed to the primary Edge Agent over LAN.

```
Station has 3 HHTs: HHT-1 (primary Edge Agent), HHT-2, HHT-3 (Odoo POS only)

INTERNET DOWN — station LAN still up

         [FCC]
           │
           └── LAN poll ──► [HHT-1 Edge Agent] ── buffers transactions locally
                                    │
                                    │  exposes LAN API (not just localhost)
                                    │  on station WiFi IP e.g. 192.168.1.10:8585
                                    │
                    ┌───────────────┼───────────────┐
                    │               │               │
             [HHT-2 Odoo POS]  [HHT-3 Odoo POS]   (primary Odoo POS)
                    │               │
      GET 192.168.1.10:8585    GET 192.168.1.10:8585
      /api/transactions        /api/transactions
                    │               │
             sees same data   sees same data
             (excl. SYNCED_TO_ODOO)

Pre-auth from HHT-2 (non-primary):
         │
         ▼
[HHT-2 Odoo POS] ──► POST 192.168.1.10:8585/api/preauth ──► [HHT-1 Edge Agent] ──► [FCC]
                                                                      │
                                                              authorized response
                                                                      │
                                                         [HHT-2 Odoo POS] sees "AUTHORIZED"


INTERNET RESTORED

[HHT-1 Edge Agent] ──► uploads buffer ──► [Cloud Middleware]
                   ──► forwards pre-auth records ──► [Cloud Middleware]
                   ──► polls SYNCED_TO_ODOO status
                              │
                   marks consumed transactions
                   (HHT-2 and HHT-3 local API calls will no longer see them)
```

------------------------------------------------------------------------

# Scenario 7 — SYNCED_TO_ODOO Cleanup Flow

> How the cloud marks a transaction as synced after Odoo has polled it and created the order, and how the Edge Agent uses this status to avoid serving already-processed transactions.

```
                 [Cloud Middleware]
                        │
         [Odoo] polls GET /transactions?status=PENDING
                        │
         [Odoo] creates order(s) in Odoo
                        │
         [Odoo] calls POST /transactions/{id}/acknowledge
         (or bulk acknowledge endpoint)
                        │
                        ▼
         mark transaction: SYNCED_TO_ODOO = true
         (queryable by Edge Agent via status API)


         [Edge Agent] ── every ~30s (when online) ──► GET /cloud/transactions/status
                                                              │
                                              returns list of SYNCED_TO_ODOO IDs
                                                              │
                        ┌─────────────────────────────────────┘
                        │
                        ▼
         update local SQLite buffer entries:
         status → SYNCED_TO_ODOO
                        │
         ┌──────────────┴──────────────┐
         │                             │
GET /api/transactions          Edge Agent catch-up
(Odoo POS polls this           will NOT re-upload
 when offline)                 these to cloud
         │                     (BR-9.7)
EXCLUDES SYNCED_TO_ODOO
entries from results
(prevents Odoo seeing
 already-processed txns)
                        │
         after retention period (e.g. 7 days)
                        │
                        ▼
         local cleanup — remove old SYNCED_TO_ODOO entries
         (never before confirmed synced)
```
