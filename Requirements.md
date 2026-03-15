# Forecourt Middleware — Requirements Specification

Version: 1.2
Status: Draft
Last Updated: 2026-03-10

------------------------------------------------------------------------

# Table of Contents

1. [Glossary](#glossary)
2. [Roles and Responsibilities](#roles-and-responsibilities)
3. [Requirements Overview](#requirements-overview)
4. [REQ-1: Legal Entity and Country Configuration](#req-1-legal-entity-and-country-configuration)
5. [REQ-2: Site Configuration and Operating Modes](#req-2-site-configuration-and-operating-modes)
6. [REQ-3: FCC Registration and Assignment](#req-3-fcc-registration-and-assignment)
7. [REQ-4: Site Connectivity Mode (Connected / Disconnected)](#req-4-site-connectivity-mode-connected--disconnected)
8. [REQ-5: Fiscalization Configuration](#req-5-fiscalization-configuration)
9. [REQ-6: Pre-Authorization Orders](#req-6-pre-authorization-orders)
10. [REQ-7: Normal Orders (FCC-Initiated Transactions)](#req-7-normal-orders-fcc-initiated-transactions)
11. [REQ-8: Pre-Auth Reconciliation and Volume Adjustment](#req-8-pre-auth-reconciliation-and-volume-adjustment)
12. [REQ-9: Automatic Order Creation in Odoo](#req-9-automatic-order-creation-in-odoo)
13. [REQ-10: Payload Normalization and Field Mapping](#req-10-payload-normalization-and-field-mapping)
14. [REQ-11: Master Data Synchronization](#req-11-master-data-synchronization)
15. [REQ-12: Transaction Ingestion Modes (Pull / Push)](#req-12-transaction-ingestion-modes-pull--push)
16. [REQ-13: Duplicate Detection](#req-13-duplicate-detection)
17. [REQ-14: Audit Trail and Transaction Logging](#req-14-audit-trail-and-transaction-logging)
18. [REQ-15: Edge Android Agent (HHT)](#req-15-edge-android-agent-hht)
19. [REQ-16: Error Handling, Retry, and Alerting](#req-16-error-handling-retry-and-alerting)
20. [REQ-17: Multi-Tenancy and Data Isolation](#req-17-multi-tenancy-and-data-isolation)
21. [Non-Functional Requirements](#non-functional-requirements)
22. [MVP Scope Summary](#mvp-scope-summary)

------------------------------------------------------------------------

# Glossary

| Term | Definition |
|------|-----------|
| Legal Entity | A country-level business entity operating under local tax and regulatory law. Each supported country is one legal entity. |
| COCO | Company Owned, Company Operated — the fuel company directly operates the site. |
| CODO | Company Owned, Dealer Operated — the fuel company owns the site but a third-party dealer operates it. |
| DODO | Dealer Owned, Dealer Operated — a third-party dealer owns and operates the site. |
| DOCO | Dealer Owned, Company Operated — a third-party dealer owns the site but the fuel company operates it. |
| FCC | Forecourt Controller — hardware/software system controlling fuel pumps at a site. |
| HHT | Handheld Terminal — Android device used by fuel attendants, running Odoo POS. **Device in use: Urovo i9100 running Android 12.** |
| Pre-Auth | Pre-Authorization — an order authorized in Odoo before fuel is dispensed. The FCC authorizes the pump for a specific amount/volume. Used specifically for customers requesting a fiscalized invoice with their Tax ID. |
| Normal Order | A dispense transaction that occurs without a prior pre-auth order. The attendant lifts the nozzle and dispenses directly. This is the predominant transaction type at all sites. Previously referred to as "unsolicited transaction" (an FCC-level term). |
| Unsolicited Transaction | FCC-level term for a transaction surfaced by the FCC without a prior pre-auth request (i.e., via Pull or Push without a correlated authorization). At the application level, these are referred to as **Normal Orders**. |
| Fiscalization | The process of reporting a transaction to a government tax authority (e.g., TRA in Tanzania, MRA in Malawi) for tax compliance. |
| Pull Mode | The middleware periodically polls the FCC for new transactions. |
| Push Mode | The FCC actively sends transactions to the middleware as they occur. |
| Edge Agent | A native **Kotlin/Java** Android application running on the HHT alongside Odoo POS. Communicates with the FCC over local LAN and includes the FCC adapter logic on-device. |
| Sure MDM | The Mobile Device Management platform used to manage the HHT fleet. Used for distributing Edge Agent APK updates. |
| Adapter | A vendor-specific integration module that translates between the middleware canonical model and a specific FCC protocol. |
| Canonical Model | The standardized internal data format used by the middleware regardless of FCC vendor. |
| MRA | Malawi Revenue Authority. |
| TRA | Tanzania Revenue Authority. |

------------------------------------------------------------------------

# Roles and Responsibilities

## System Roles

| Role | Description | Access Level |
|------|-------------|-------------|
| **System Administrator** | Manages legal entities, site configurations, FCC assignments, connectivity modes, and fiscalization settings. Manages adapter configurations and field mappings. Monitors system health and transaction flows. | Full configuration and monitoring access. |
| **Operations Manager** | Monitors transaction flows, reconciliation status, and site connectivity per legal entity or region. Reviews failed transactions and triggers manual retries. | Read access to all transactions. Write access to reconciliation actions and manual retries. |
| **Site Supervisor** | On-site personnel overseeing pump operations, attendant activity, and Edge Agent health. May trigger manual transaction pulls if needed. | Read access to site transactions. Limited actions on Edge Agent. |
| **Fuel Attendant** | Uses Odoo POS on HHT to create pre-auth orders (where applicable). Does not interact with middleware directly. In disconnected mode, operates manually. | Odoo POS access only. No direct middleware access. |
| **Integration Service (Databricks)** | Automated service that syncs master data (legal entities, sites, pumps, nozzles, products, operators) from Odoo to the middleware. | Write access to master data tables via sync API. |
| **Odoo ERP** | The upstream ERP system. Source of truth for master data, orders, and customer information. Consumes transaction data from the middleware. | API consumer and provider. |
| **FCC (External System)** | The forecourt controller hardware/software. Receives pre-auth commands and sends back dispense transactions. | Communicates via adapter protocols (vendor-specific). |

## Responsibility Matrix (RACI)

| Activity | Sys Admin | Ops Manager | Site Supervisor | Attendant | Databricks | Odoo |
|----------|-----------|-------------|-----------------|-----------|------------|------|
| Legal Entity setup | R/A | I | - | - | C | C |
| Site configuration | R/A | C | I | - | R | C |
| FCC assignment | R/A | C | I | - | - | - |
| Connectivity mode | R/A | C | I | - | - | - |
| Fiscalization config | R/A | I | - | - | - | C |
| Pre-Auth order creation | - | - | I | R | - | A |
| Transaction monitoring | I | R/A | C | - | - | I |
| Reconciliation review | - | R/A | C | - | - | I |
| Master data sync | I | I | - | - | R/A | C |
| Adapter field mapping | R/A | I | - | - | - | - |
| Edge Agent health | I | R | A | C | - | - |

------------------------------------------------------------------------

# Requirements Overview

| Req ID | Title | Priority | MVP |
|--------|-------|----------|-----|
| REQ-1 | Legal Entity and Country Configuration | P0 | Yes |
| REQ-2 | Site Configuration and Operating Modes | P0 | Yes |
| REQ-3 | FCC Registration and Assignment | P0 | Yes |
| REQ-4 | Site Connectivity Mode | P0 | Yes |
| REQ-5 | Fiscalization Configuration | P0 | Yes |
| REQ-6 | Pre-Authorization Orders | P0 | Yes |
| REQ-7 | Normal Orders (FCC-Initiated Transactions) | P0 | Yes |
| REQ-8 | Pre-Auth Reconciliation and Volume Adjustment | P0 | Yes |
| REQ-9 | Automatic Order Creation in Odoo | P0 | Yes |
| REQ-10 | Payload Normalization and Field Mapping | P0 | Yes |
| REQ-11 | Master Data Synchronization | P0 | Yes |
| REQ-12 | Transaction Ingestion Modes (Pull / Push) | P0 | Yes |
| REQ-13 | Duplicate Detection | P0 | Yes |
| REQ-14 | Audit Trail and Transaction Logging | P1 | Yes |
| REQ-15 | Edge Android Agent (HHT) | P1 | Partial |
| REQ-16 | Error Handling, Retry, and Alerting | P1 | Yes |
| REQ-17 | Multi-Tenancy and Data Isolation | P1 | Yes |

Priority: P0 = Must Have, P1 = Should Have, P2 = Nice to Have

------------------------------------------------------------------------

# REQ-1: Legal Entity and Country Configuration

**Priority:** P0
**MVP:** Yes
**Data Source:** Synced from Odoo via Databricks (no CRUD screens)

## Description

Each country of operation is treated as a **Legal Entity**. The middleware must be aware of legal entities to correctly route transactions, apply fiscalization rules, and enforce country-specific regulatory requirements.

## Data Model (Minimum Required Fields)

| Field | Type | Description |
|-------|------|-------------|
| `legalEntityId` | UUID | Internal unique identifier |
| `countryCode` | String (ISO 3166-1) | e.g., MW, TZ, BW, ZM, NA |
| `countryName` | String | e.g., Malawi, Tanzania |
| `currencyCode` | String (ISO 4217) | e.g., MWK, TZS, BWP, ZMW, NAD |
| `taxAuthorityCode` | String | e.g., MRA, TRA, BURS, ZRA, NamRA |
| `fiscalizationRequired` | Boolean | Whether this legal entity requires fiscalization |
| `fiscalizationProvider` | String (nullable) | e.g., FCC_DIRECT, MRA_INTEGRATION, TRA_INTEGRATION |
| `defaultTimezone` | String | e.g., Africa/Blantyre, Africa/Dar_es_Salaam |
| `isActive` | Boolean | Soft-enable/disable for rollout control |
| `odooCompanyId` | String | Reference back to Odoo company record |
| `syncedAt` | DateTime | Last sync timestamp from Databricks |

## Business Rules

- BR-1.1: All sites, configurations, and transactions must be associated with exactly one legal entity.
- BR-1.2: Fiscalization rules are determined at the legal entity level as the default, but may be overridden at the site level (see REQ-5).
- BR-1.3: Legal entity data is read-only in the middleware. All changes originate from Odoo and sync via Databricks.
- BR-1.4: A legal entity cannot be deleted — only deactivated.

## Acceptance Criteria

- AC-1.1: The middleware stores and resolves legal entity configuration for all five initial countries (MW, TZ, BW, ZM, NA).
- AC-1.2: All API responses include the legal entity context (country code) in transaction payloads.
- AC-1.3: The system rejects any transaction that references an unknown or inactive legal entity.

------------------------------------------------------------------------

# REQ-2: Site Configuration and Operating Modes

**Priority:** P0
**MVP:** Yes
**Data Source:** Synced from Odoo via Databricks (no CRUD screens)

## Description

Each fuel station (site) must be registered in the middleware with its operating mode. The operating mode determines who runs the site and affects tax handling, operator identification, and data requirements.

## Operating Modes

| Mode | Description | Operator Tax ID Required |
|------|-------------|-------------------------|
| COCO | Company Owned, Company Operated | No (uses company tax ID) |
| CODO | Company Owned, Dealer Operated | Yes |
| DODO | Dealer Owned, Dealer Operated | Yes |
| DOCO | Dealer Owned, Company Operated | No (uses company tax ID) |

## Data Model (Minimum Required Fields)

| Field | Type | Description |
|-------|------|-------------|
| `siteId` | UUID | Internal unique identifier |
| `siteCode` | String | Unique site code (e.g., MW-BT001, TZ-DAR01) |
| `siteName` | String | Human-readable site name |
| `legalEntityId` | UUID (FK) | Associated legal entity |
| `operatingMode` | Enum | COCO, CODO, DODO, DOCO |
| `operatorName` | String (nullable) | Dealer/operator name (required for CODO/DODO) |
| `operatorTaxPayerId` | String (nullable) | Dealer TIN/TPIN (required for CODO/DODO) |
| `companyTaxPayerId` | String | Company TIN for this legal entity |
| `connectivityMode` | Enum | See REQ-4 |
| `isActive` | Boolean | Soft-enable/disable |
| `odooSiteId` | String | Reference back to Odoo |
| `syncedAt` | DateTime | Last sync timestamp |

## Business Rules

- BR-2.1: `operatorTaxPayerId` is **required** when `operatingMode` is CODO or DODO. The sync process must validate this.
- BR-2.2: A site must belong to exactly one legal entity.
- BR-2.3: The `siteCode` must be unique across the entire system.
- BR-2.4: Only minimum data needed for middleware processing is stored. Full site details remain in Odoo.
- BR-2.5: Site data is read-only in the middleware. All changes originate from Odoo via Databricks.

## Acceptance Criteria

- AC-2.1: Sites synced from Odoo are correctly stored with operating mode and operator details.
- AC-2.2: Transactions from CODO/DODO sites include operator tax ID in fiscal payloads.
- AC-2.3: The system rejects transactions for unknown or inactive sites.

------------------------------------------------------------------------

# REQ-3: FCC Registration and Assignment

**Priority:** P0
**MVP:** Yes
**Data Source:** Synced from Odoo via Databricks + System Admin configuration for adapter-specific settings

## Description

Each site may have zero or one FCC assigned. The middleware must know which FCC vendor is deployed at a site and how to communicate with it. Sites without an FCC operate in disconnected mode (see REQ-4).

## Data Model

| Field | Type | Description |
|-------|------|-------------|
| `fccId` | UUID | Internal unique identifier |
| `siteId` | UUID (FK) | Associated site |
| `fccVendor` | Enum | DOMS, RADIX, ADVATEC, PETRONITE |
| `fccModel` | String (nullable) | Hardware model identifier |
| `fccVersion` | String (nullable) | Firmware/software version |
| `connectionProtocol` | String | e.g., REST, TCP, SOAP |
| `hostAddress` | String | IP or hostname of the FCC (LAN or public) |
| `port` | Integer | Connection port |
| `authCredentials` | Encrypted String | API key, username/password, or certificate reference |
| `transactionMode` | Enum | PULL, PUSH, HYBRID |
| `pullIntervalSeconds` | Integer (nullable) | Polling interval for PULL mode (e.g., 30, 60) |
| `ingestionMode` | Enum | `CLOUD_DIRECT` (default), `RELAY`, `BUFFER_ALWAYS` — controls where the FCC is configured to send data |
| `isActive` | Boolean | Whether this FCC is currently operational |
| `lastHeartbeatAt` | DateTime (nullable) | Last successful communication timestamp |
| `registeredAt` | DateTime | When this FCC was first registered |

## Pump and Nozzle Mapping

Odoo numbers pumps and nozzles independently of the FCC vendor. The middleware stores explicit mapping tables so that Odoo POS pump/nozzle numbers can be translated to FCC pump/nozzle numbers before a pre-auth command is sent to the FCC.

**`pumps`**

| Field | Type | Description |
|-------|------|-------------|
| `pumpId` | UUID | Internal unique identifier |
| `siteId` | UUID (FK) | Owning site |
| `pump_number` | Integer | Pump number as Odoo POS knows it (synced from Odoo via Databricks) |
| `fcc_pump_number` | Integer | Pump number sent to the FCC — may differ from `pump_number` |
| `isActive` | Boolean | Soft-delete flag |

**`nozzles`**

| Field | Type | Description |
|-------|------|-------------|
| `nozzleId` | UUID | Internal unique identifier |
| `pumpId` | UUID (FK) | Parent pump |
| `siteId` | UUID (FK) | Owning site (denormalized) |
| `odoo_nozzle_number` | Integer | Nozzle number as Odoo POS knows it |
| `fcc_nozzle_number` | Integer | Nozzle number sent to the FCC |
| `productId` | UUID (FK) | Product (fuel grade) dispensed by this nozzle |
| `isActive` | Boolean | Soft-delete flag |

Both tables are synced from Odoo via Databricks. Numbers match 1:1 at most sites, but may diverge after an FCC replacement or renumbering.

## Business Rules

- BR-3.1: A site can have at most one active FCC at a time.
- BR-3.2: If no FCC is assigned or active, the site operates in disconnected mode.
- BR-3.3: The `fccVendor` determines which adapter is used for communication.
- BR-3.4: Pump/nozzle mappings must match the physical FCC configuration. Mismatches must raise alerts.
- BR-3.5a: The `pumps` table must store both `pump_number` (Odoo) and `fcc_pump_number` (FCC). The `nozzles` table must store `odoo_nozzle_number` and `fcc_nozzle_number` along with the `productId`. These are synced from Odoo via Databricks.
- BR-3.5b: On every pre-auth, the Edge Agent MUST translate the Odoo pump/nozzle numbers received from Odoo POS into FCC pump/nozzle numbers by looking up the `nozzles` table before sending the pre-auth command to the FCC.
- BR-3.5: `authCredentials` must be stored encrypted at rest.
- BR-3.6: `lastHeartbeatAt` is updated on every successful communication. If stale beyond a configurable threshold, the system raises a connectivity alert.

## Acceptance Criteria

- AC-3.1: FCC configuration is correctly resolved for each site during transaction processing.
- AC-3.2: The correct adapter is invoked based on `fccVendor`.
- AC-3.3: Pump and nozzle numbers in incoming FCC transactions are correctly mapped to Odoo references.
- AC-3.4: Sites without an active FCC are automatically treated as disconnected.

------------------------------------------------------------------------

# REQ-4: Site Connectivity Mode (Connected / Disconnected)

**Priority:** P0
**MVP:** Yes

## Description

Each site operates in one of two logical modes that determine how orders flow between Odoo and the FCC.

## Modes

| Mode | Description | FCC Required | Order Flow |
|------|-------------|-------------|------------|
| **Connected** | Middleware communicates with the FCC via adapters (directly or via Edge Agent). Pre-auth and transaction ingestion are automated. | Yes | Odoo <-> Middleware <-> FCC |
| **Disconnected** | No FCC communication. Attendants create and complete orders manually in Odoo POS. No middleware involvement in pump control. | No | Odoo POS only (manual) |

## Business Rules

- BR-4.1: Connectivity mode is set per site and synced from Odoo via Databricks.
- BR-4.2: A site in **connected** mode MUST have an active FCC assignment (REQ-3). If the FCC becomes inactive, the system must alert the Operations Manager but continue to accept manual orders.
- BR-4.3: A site in **disconnected** mode does not generate any middleware-to-FCC traffic. The middleware ignores this site for polling and push listeners.
- BR-4.4: Transitioning from disconnected to connected requires an active FCC assignment and is an admin-level action.
- BR-4.5: A connected site may temporarily fall into an **offline** state if the FCC or network is unreachable. This is different from disconnected mode — the site is configured as connected but currently unable to communicate. The Edge Agent handles buffering in this scenario.

## Acceptance Criteria

- AC-4.1: The middleware only initiates FCC communication for sites in connected mode.
- AC-4.2: Disconnected sites are excluded from transaction polling schedules.
- AC-4.3: The system correctly distinguishes between "disconnected" (by configuration) and "offline" (by connectivity failure).

------------------------------------------------------------------------

# REQ-5: Fiscalization Configuration

**Priority:** P0
**MVP:** Yes

## Description

Fiscalization requirements vary by country and by site. The middleware must know whether and how a transaction should be fiscalized to correctly drive the adapter and any external tax authority integrations.

## Fiscalization Modes

| Mode | Description | Example |
|------|-------------|---------|
| `FCC_DIRECT` | The FCC itself handles fiscalization with the tax authority. The middleware sends customer tax details to the FCC, and the FCC returns a fiscal receipt. | Tanzania — DOMS/Radix handle TRA fiscalization directly. |
| `EXTERNAL_INTEGRATION` | Fiscalization is handled by a separate integration outside the middleware (e.g., a direct Odoo-to-MRA integration). The middleware does NOT fiscalize. | Malawi — DOMS does not fiscalize. A separate MRA integration handles it. |
| `NONE` | No fiscalization required for this site/country. | Countries without fuel fiscalization mandates. |

## Data Model (Site-level override)

| Field | Type | Description |
|-------|------|-------------|
| `siteId` | UUID (FK) | Site reference |
| `fiscalizationMode` | Enum | FCC_DIRECT, EXTERNAL_INTEGRATION, NONE |
| `taxAuthorityEndpoint` | String (nullable) | Only for FCC_DIRECT — endpoint the FCC uses (informational) |
| `requireCustomerTaxId` | Boolean | Whether customer TIN must be captured and sent with pre-auth |
| `fiscalReceiptRequired` | Boolean | Whether the middleware must expect a fiscal receipt in the dispense response |

## Business Rules

- BR-5.1: The default fiscalization mode is inherited from the legal entity (REQ-1). It can be overridden at the site level.
- BR-5.2: When `fiscalizationMode` = `FCC_DIRECT`, the pre-auth payload MUST include customer tax details (TIN, business name) if `requireCustomerTaxId` is true.
- BR-5.3: When `fiscalizationMode` = `FCC_DIRECT`, the dispense transaction response from the FCC MUST contain a fiscal receipt reference. If missing, the transaction is flagged for review.
- BR-5.4: When `fiscalizationMode` = `EXTERNAL_INTEGRATION`, the middleware passes through transactions without fiscal data. Fiscalization is handled externally.
- BR-5.5: When `fiscalizationMode` = `NONE`, no fiscal fields are required or expected.

## Acceptance Criteria

- AC-5.1: Tanzania sites configured with `FCC_DIRECT` correctly include customer tax details in pre-auth payloads sent to the FCC.
- AC-5.2: Malawi sites configured with `EXTERNAL_INTEGRATION` process transactions without fiscal receipt expectations.
- AC-5.3: Transactions at `FCC_DIRECT` sites that return without a fiscal receipt are flagged for Operations Manager review.
- AC-5.4: The fiscalization mode is correctly resolved (legal entity default → site override).

------------------------------------------------------------------------

# REQ-6: Pre-Authorization Orders

**Priority:** P0
**MVP:** Yes

## Description

Pre-authorization is **not the default transaction flow**. It is used specifically when a customer requests a **fiscalized invoice with their Tax ID** (TIN). At most sites the majority of transactions are Normal Orders (see REQ-7). Pre-auth and Normal Order transactions can coexist at the same site (mixed mode).

When pre-auth is required: an Odoo POS order is created BEFORE fuel is dispensed. The Edge Agent sends a pre-auth request to the FCC over LAN, which authorizes a specific pump for a requested **amount** (in local currency). The attendant then dispenses fuel. The actual dispensed **volume** is recorded in the final dispense transaction returned via the FCC's cloud push, and this actual volume becomes the quantity on the Odoo order (see REQ-8).

Pre-auth always routes via the **Edge Agent** — never directly from Odoo to the cloud middleware for FCC command forwarding. This is because the Edge Agent has the FCC adapter logic and direct LAN access to the FCC.

## Flow

1. **Attendant** creates an order in Odoo POS (captures customer details, vehicle, product, amount).
2. **Odoo POS** sends a pre-auth request to the **Edge Agent's local API** (localhost on the same HHT).
3. **Edge Agent** translates and sends the pre-auth command to the **FCC** over the station LAN.
4. **FCC** authorizes the pump and responds with confirmation.
5. **Edge Agent** stores the pre-auth response locally and returns confirmation to Odoo POS.
6. **Edge Agent** queues the pre-auth record to the **Cloud Middleware** for reconciliation tracking (async, with retry when internet is available).
7. Fuel is dispensed physically.
8. The final dispense transaction is received by the Cloud Middleware via the FCC's direct push and reconciled (see REQ-8, REQ-12).

## Pre-Auth Request Payload (Canonical)

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `odooOrderId` | String | Yes | Odoo POS order reference |
| `siteCode` | String | Yes | Site identifier |
| `pumpNumber` | Integer | Yes | Target pump — **Odoo pump number** (as displayed in Odoo POS) |
| `nozzleNumber` | Integer | Yes | Target nozzle — **Odoo nozzle number** (as displayed in Odoo POS) |
| `productCode` | String | Yes | Fuel product (PMS, AGO, IK) |
| `requestedAmount` | Decimal | Yes | Authorized amount in local currency. **Pre-auth is always by amount — volume authorization is not used.** |
| `unitPrice` | Decimal | Yes | Price per litre at time of auth. Used by Odoo to display the estimated volume (`requestedAmount / unitPrice`) before dispense, but the FCC authorizes by amount only. |
| `vehicleNumber` | String | No | Vehicle registration (if captured) |
| `customerName` | String | Conditional | Required if `requireCustomerTaxId` = true |
| `customerTaxId` | String | Conditional | Customer TIN — required if `requireCustomerTaxId` = true (REQ-5) |
| `customerBusinessName` | String | Conditional | Required for B2B fiscalized transactions |
| `attendantId` | String | No | Odoo user reference for the attendant |

## Pre-Auth States

| State | Description |
|-------|-------------|
| `PENDING` | Sent to FCC, awaiting confirmation |
| `AUTHORIZED` | FCC confirmed pump authorization |
| `DISPENSING` | Fuel dispense in progress (if FCC reports this) |
| `COMPLETED` | Final dispense transaction received and matched |
| `CANCELLED` | Pre-auth cancelled before dispense |
| `EXPIRED` | Pre-auth timed out without dispense (configurable timeout) |
| `FAILED` | FCC rejected the authorization |

## Business Rules

- BR-6.1: Pre-auth is used when a customer requests a fiscalized invoice with their Tax ID. It is applicable for sites in **connected** mode with `fiscalizationMode` = `FCC_DIRECT`.
- BR-6.1b: Pre-auth is **always authorized by amount** (local currency). The FCC receives `requestedAmount` and authorizes the pump to dispense up to that monetary value. Volume authorization is not sent to the FCC.
- BR-6.1c: The estimated dispensable volume (`requestedAmount / unitPrice`) may be displayed to the attendant in Odoo POS for guidance, but this is informational only. The Odoo Order quantity is set from the actual dispensed volume returned by the FCC in the final dispense transaction (see BR-8.5).
- BR-6.1a: Odoo POS **always** sends pre-auth requests to the Edge Agent's local API (localhost). The cloud middleware is not in the pre-auth command path. This applies in both online and offline modes because pre-auth operates over LAN.
- BR-6.2: Each pre-auth must be uniquely identifiable by `odooOrderId` + `siteCode`. Duplicate pre-auth requests for the same order must be rejected (idempotency).
- BR-6.3: Customer tax details (`customerTaxId`, `customerName`, `customerBusinessName`) are passed through to the FCC adapter when `requireCustomerTaxId` = true on the site's fiscalization config.
- BR-6.4: A pre-auth that is not completed or cancelled within a configurable timeout (e.g., 15 minutes) must transition to `EXPIRED` and release the pump authorization.
- BR-6.5: Cancellation is allowed only in `PENDING` or `AUTHORIZED` states.
- BR-6.6: The Edge Agent must support cancellation via its local API: `POST /api/preauth/{id}/cancel`.
- BR-6.7: The Edge Agent must forward the pre-auth record to the Cloud Middleware for reconciliation. If the cloud is unreachable, the record must be queued locally and retried with exponential backoff when internet returns.
- BR-6.8: The Cloud Middleware must be able to receive and store pre-auth records from the Edge Agent so it can match final dispense transactions arriving via FCC push.

## Acceptance Criteria

- AC-6.1: A pre-auth request from Odoo POS is sent to the Edge Agent local API and forwarded to the FCC over LAN.
- AC-6.2: The FCC response is correctly mapped and stored in the Edge Agent; Odoo POS receives the confirmation.
- AC-6.3: Customer tax details are included in the FCC payload for fiscalized sites.
- AC-6.4: Duplicate pre-auth requests are rejected with an appropriate error.
- AC-6.5: Expired pre-auths are automatically transitioned and the pump authorization is released.
- AC-6.6: The pre-auth record is successfully forwarded to the Cloud Middleware; if offline, it queues and retries automatically.
- AC-6.7: The Cloud Middleware can use the pre-auth record to match final dispense transactions arriving via FCC push (REQ-8).

------------------------------------------------------------------------

# REQ-7: Normal Orders (FCC-Initiated Transactions)

**Priority:** P0
**MVP:** Yes

> **Terminology note**: "Unsolicited" and "Solicited" are FCC-level terms describing how the FCC surfaces transactions (Push vs Pull). At the application level these are called **Normal Orders** — transactions not initiated by an Odoo pre-auth. This is the **default transaction type** at all sites.

## Description

Normal Orders occur when an attendant dispenses fuel WITHOUT creating a pre-auth order in Odoo. The attendant lifts the nozzle and dispenses directly. The FCC records the transaction, and the middleware ingests it via Pull or Push (see REQ-12).

These transactions have no corresponding Odoo order at the time of dispense. The middleware must ingest and store them so Odoo can poll and create orders (see REQ-9).

Normal Orders and Pre-Auth orders can coexist at the same site (mixed mode). Normal Orders are predominant at all sites; Pre-Auth is reserved for customers requesting a fiscalized invoice with their Tax ID (see REQ-6).

## Transaction Payload (Canonical — Received from FCC)

| Field | Type | Description |
|-------|------|-------------|
| `fccTransactionId` | String | Unique transaction ID from the FCC |
| `siteCode` | String | Site where dispense occurred |
| `pumpNumber` | Integer | FCC pump number (as used by the FCC; stored in `pumps.fcc_pump_number`) |
| `nozzleNumber` | Integer | FCC nozzle number (as used by the FCC; stored in `nozzles.fcc_nozzle_number`) |
| `productCode` | String | Fuel product dispensed |
| `volume` | Decimal | Litres dispensed |
| `amount` | Decimal | Total amount in local currency |
| `unitPrice` | Decimal | Price per litre |
| `startDateTime` | DateTime | Dispense start time |
| `endDateTime` | DateTime | Dispense end time |
| `fiscalReceiptNumber` | String (nullable) | If FCC fiscalizes directly |
| `rawPayload` | JSON | Full original FCC payload (preserved for audit) |

## Business Rules

- BR-7.1: Normal Orders arrive via Pull or Push (REQ-12). The middleware must handle both modes per site/FCC configuration.
- BR-7.2: The middleware must NOT assume a fixed batch size. Payloads may contain a single transaction or multiple transactions. The system must process whatever the FCC sends — whether one-by-one or bulk — without requiring a configuration flag. The adapter must handle payload inspection and iteration.
- BR-7.3: Each Normal Order must be checked for duplicates before processing (REQ-13).
- BR-7.4: After deduplication, the transaction is normalized to the canonical model and stored with status `PENDING`, making it available for Odoo to poll and create an order (REQ-9).
- BR-7.5: The raw FCC payload must be preserved alongside the canonical model for audit purposes.

## Acceptance Criteria

- AC-7.1: Normal Orders from DOMS are correctly ingested and normalized.
- AC-7.2: Single-transaction and bulk payloads are both processed correctly without configuration changes.
- AC-7.3: Duplicate transactions are detected and skipped without error.
- AC-7.4: Raw payloads are stored for audit trail.

------------------------------------------------------------------------

# REQ-8: Pre-Auth Reconciliation and Volume Adjustment

**Priority:** P0
**MVP:** Yes

## Description

When a pre-auth order is completed, the actual dispensed amount may differ from the pre-authorized amount. The middleware matches the final dispense transaction to the original pre-auth, calculates the variance on amount, and stores the reconciled transaction with the **actual dispensed volume from the FCC**. Odoo polls this reconciled transaction and creates the order using the actual volume as the order quantity.

## Flow

1. A final dispense transaction is received from the FCC (via Pull/Push). It contains the **actual dispensed volume** (litres) and **actual amount** (local currency).
2. The middleware matches it to an existing pre-auth record using FCC transaction correlation IDs, pump/nozzle, and time window.
3. The middleware calculates the **amount variance** (`actualAmount - requestedAmount`).
   - Within tolerance → auto-approved.
   - Exceeds tolerance → flagged for Ops Manager review.
4. Regardless of variance, the reconciled transaction is stored with the **actual volume and actual amount** from the FCC. This is what Odoo polls when creating the order — the Odoo Order quantity = `actualVolume`.
5. The pre-auth record transitions to `COMPLETED`.
6. A reconciliation record is created capturing the variance.

## Reconciliation Record

| Field | Type | Description |
|-------|------|-------------|
| `reconciliationId` | UUID | Internal identifier |
| `preAuthId` | UUID (FK) | Original pre-auth |
| `fccTransactionId` | String | Final dispense transaction from FCC |
| `authorizedAmount` | Decimal | Amount sent in the pre-auth request (always in local currency) |
| `actualAmount` | Decimal | Actual dispensed amount returned by FCC |
| `actualVolume` | Decimal | Actual dispensed volume (litres) returned by FCC — this becomes the Odoo Order quantity |
| `variance` | Decimal | Amount difference: `actualAmount - authorizedAmount` |
| `variancePercentage` | Decimal | Amount variance as a percentage of authorized amount |
| `odooOrderAvailable` | Boolean | Whether the reconciled transaction has been stored as PENDING for Odoo to poll |
| `reconciledAt` | DateTime | Timestamp of reconciliation |
| `status` | Enum | MATCHED, VARIANCE_WITHIN_TOLERANCE, VARIANCE_FLAGGED, UNMATCHED |

## Business Rules

- BR-8.1: The middleware must attempt to correlate every dispense transaction at a pre-auth site to an existing pre-auth record.
- BR-8.2: Correlation uses: FCC-provided correlation ID (if available), pump number + nozzle number + time window, and `odooOrderId` echoed by the FCC (if supported by the adapter).
- BR-8.3: **Variance is calculated on amount only** (`actualAmount - authorizedAmount`). If within a configurable tolerance (e.g., ±2%), the reconciliation is auto-approved.
- BR-8.4: If the amount variance exceeds tolerance, the reconciliation is flagged for Operations Manager review. The transaction is still stored as PENDING for Odoo to poll regardless of variance outcome.
- BR-8.5: The reconciled transaction stored as PENDING must include the **actual dispensed volume from the FCC** as the order quantity and the **actual amount** as the order value. When Odoo polls and creates the order, the Odoo Order quantity = `actualVolume` (litres). There is no authorized volume — volume is never part of the pre-auth authorization.
- BR-8.6: If no matching pre-auth is found for a dispense transaction at a pre-auth site, the transaction is flagged as `UNMATCHED` for investigation.
- BR-8.7: Unmatched transactions at pre-auth sites are still stored as PENDING (with a reconciliation flag) so Odoo can poll and create an order as if it were a normal transaction.

## Acceptance Criteria

- AC-8.1: A dispense transaction that matches a pre-auth is stored with actual volume and actual amount from the FCC. When Odoo polls this transaction, the Odoo Order quantity equals the actual dispensed volume.
- AC-8.2: Variance within tolerance is auto-approved.
- AC-8.3: Variance exceeding tolerance is flagged for review.
- AC-8.4: Unmatched transactions at pre-auth sites are flagged and still available for Odoo to poll and create orders.

------------------------------------------------------------------------

# REQ-9: Odoo Order Creation (Odoo-Polled Model)

**Priority:** P0
**MVP:** Yes

## Description

For Normal Orders (REQ-7) and reconciled transactions at pre-auth sites (REQ-8), the middleware exposes transactions for Odoo to consume. **The middleware does not call Odoo to create orders.** Odoo polls the middleware and creates orders itself.

Two polling sources are supported — Odoo polls whichever is reachable:

| Source | When Used | Network |
|--------|-----------|---------|
| **Cloud Middleware** | Primary path. Internet is available. Odoo polls `GET /transactions?status=PENDING` on a schedule or via manual bulk trigger. | Internet |
| **Edge Agent local API** | Offline path. Internet is down. Odoo POS switches to polling `GET /api/transactions` on the Edge Agent (localhost). | LAN (localhost or station WiFi) |

After Odoo creates an order, it **acknowledges the transaction** back to the middleware. The middleware marks acknowledged transactions as `SYNCED_TO_ODOO`. The Edge Agent polls this status and will not serve already-acknowledged transactions to Odoo via the local API.

The `fccTransactionId` idempotency key prevents Odoo from creating duplicate orders for a transaction it may have already processed during offline mode.

## Flow (Primary — Online, Odoo polls Cloud Middleware)

1. FCC pushes transaction to Cloud Middleware.
2. Normalized transaction passes duplicate detection (REQ-13).
3. Cloud Middleware stores transaction with status `PENDING`. Transaction is now available for Odoo to poll.
4. Odoo polls `GET /transactions?status=PENDING` (scheduled or manual bulk).
5. Odoo creates order(s) in its own system using the returned transaction data (site, pump, nozzle, product, volume, amount, unit price, timestamps, fiscal receipt if any).
6. Odoo calls `POST /transactions/acknowledge` with the list of processed `fccTransactionId` values.
7. Cloud Middleware stores the mapping `fccTransactionId` <-> `odooOrderId` and marks transactions `SYNCED_TO_ODOO`.

## Flow (Offline — Odoo polls Edge Agent)

1. Internet is down. Odoo POS detects outage and switches to Edge Agent local API.
2. Edge Agent has transactions buffered locally (collected via LAN catch-up poll from FCC).
3. Odoo polls `GET /api/transactions` on Edge Agent (localhost or LAN IP).
4. Odoo creates orders from the returned transactions.
5. Odoo calls `POST /api/transactions/acknowledge` on Edge Agent to mark them as locally consumed.
6. Edge Agent marks those buffer entries as locally acknowledged.
7. On internet recovery:
   - Edge Agent uploads buffered transactions to Cloud Middleware.
   - Odoo switches back to polling Cloud Middleware.
   - For transactions Odoo already created during offline, Odoo acknowledges them to the cloud using the same `fccTransactionId` idempotency key — no duplicate orders are created.
   - Cloud Middleware marks transactions `SYNCED_TO_ODOO`.

## Manual Bulk-Create

- The Ops team may trigger a manual bulk-create at any time (e.g., end-of-shift) by initiating a poll + order creation run covering a specified time range or all PENDING transactions.
- This is equivalent to the scheduled polling path but operator-initiated.

## Business Rules

- BR-9.1: Every Normal Order that passes deduplication MUST be made available for Odoo to poll and create an order.
- BR-9.2: Odoo order creation must be idempotent — `fccTransactionId` is the idempotency key. If Odoo polls the same transaction twice (e.g., during reconnection), it must not create a duplicate order.
- BR-9.3: The Cloud Middleware must expose a `GET /transactions?status=PENDING` endpoint that returns normalized, deduplicated transactions available for Odoo to consume.
- BR-9.4: The Cloud Middleware must expose a `POST /transactions/acknowledge` endpoint (or bulk equivalent) for Odoo to signal that it has created orders. This triggers the `SYNCED_TO_ODOO` marking.
- BR-9.5: The Edge Agent must expose equivalent `GET /api/transactions` and `POST /api/transactions/acknowledge` endpoints for Odoo's offline polling path.
- BR-9.6: Operator tax details (from site config) must be included in transaction data returned to Odoo where applicable (CODO/DODO sites).
- BR-9.7: The Edge Agent must not serve transactions already marked `SYNCED_TO_ODOO` via its local API. Transactions consumed via the offline path that are later confirmed synced to cloud must be excluded from future local API results.
- BR-9.8: If a transaction is not acknowledged (Odoo polling failure), it must remain in `PENDING` status and be returned on the next poll. Transactions do not expire from the PENDING queue automatically — they stay until acknowledged or manually resolved.
- BR-9.9: After a configurable retention period (e.g., 7 days) with no acknowledgement, a transaction is flagged as `STALE_PENDING` and an alert is raised for the Operations Manager.

## Acceptance Criteria

- AC-9.1: Transactions stored by the Cloud Middleware after FCC push are visible via `GET /transactions?status=PENDING`.
- AC-9.2: Odoo can poll, create orders, and acknowledge transactions in a single cycle with no duplicates.
- AC-9.3: When internet is down, Odoo successfully polls the Edge Agent local API and creates orders from buffered transactions.
- AC-9.4: On internet recovery, transactions created during offline are correctly acknowledged to the cloud and marked `SYNCED_TO_ODOO` without creating duplicates in Odoo.
- AC-9.5: Transactions marked `SYNCED_TO_ODOO` are excluded from both the Cloud Middleware poll endpoint and the Edge Agent local API results.
- AC-9.6: Stale unacknowledged transactions are flagged to the Operations Manager after the configured retention period.

------------------------------------------------------------------------

# REQ-10: Payload Normalization and Field Mapping

**Priority:** P0
**MVP:** Yes

## Description

Different FCC vendors return transaction data in different formats, with different field names, structures, and conventions. The middleware must normalize all vendor payloads into the canonical data model.

## Approach: Adapter-Encapsulated Mapping with Configurable Overrides

Field mapping is **primarily hardcoded within each adapter**, as each vendor's protocol is structurally different and requires code-level integration. However, certain fields that vary by deployment (not by vendor) should be **configurable**.

### Hardcoded in Adapter (Vendor-Specific)

- Payload structure parsing (JSON, XML, CSV, binary)
- Field name mapping (e.g., DOMS `fuel_qty` → canonical `volume`)
- Data type conversions (e.g., string amounts to decimals)
- Unit conversions (e.g., millilitres to litres)
- Status code mapping (vendor codes to canonical states)
- Protocol-level handling (REST response parsing, TCP frame decoding)

### Configurable per Deployment (Site/FCC Level)

| Setting | Description | Example |
|---------|-------------|---------|
| `timezone` | Timezone for timestamp normalization | Africa/Dar_es_Salaam |
| `currencyCode` | Expected currency for amount fields | TZS |
| `volumeUnit` | Volume unit used by this FCC | LITRES, MILLILITRES |
| `priceDecimalPlaces` | Decimal precision for prices | 2 |
| `productCodeMapping` | Map FCC product codes to canonical codes | {"01": "PMS", "02": "AGO"} |
| `pumpNumberOffset` | **Deprecated** — replaced by explicit `pumps.fcc_pump_number` and `nozzles.fcc_nozzle_number` per-record mappings. Adapters must not use a simple offset. See REQ-3 (Pump and Nozzle Mapping). | — |

## Business Rules

- BR-10.1: Each adapter is responsible for producing a valid canonical model from vendor-specific input. If the adapter cannot parse a payload, it must log the raw payload and raise an error.
- BR-10.2: Configurable mappings (product codes, timezone, etc.) are stored per FCC and loaded at runtime.
- BR-10.3: The raw vendor payload must always be preserved alongside the normalized canonical model.
- BR-10.4: Adding a new FCC vendor requires developing a new adapter (code change). Configurable mappings alone are not sufficient for a new vendor.

## Acceptance Criteria

- AC-10.1: DOMS transactions are correctly normalized to the canonical model.
- AC-10.2: Product code mappings configured per FCC correctly translate vendor codes.
- AC-10.3: Timezone and currency normalization produce consistent canonical records across countries.
- AC-10.4: Raw payloads are preserved for all transactions.

------------------------------------------------------------------------

# REQ-11: Master Data Synchronization

**Priority:** P0
**MVP:** Yes

## Description

Master data (legal entities, sites, pumps, nozzles, products, operators) is sourced from Odoo and synchronized to the middleware via **Databricks pipelines**. The middleware does not provide CRUD screens for master data.

## Synced Entities

| Entity | Source | Sync Direction | Frequency |
|--------|--------|---------------|-----------|
| Legal Entities | Odoo | Odoo → Middleware | Daily or on change |
| Sites | Odoo | Odoo → Middleware | Daily or on change |
| Pump/Nozzle Mappings | Odoo + FCC Config | Odoo → Middleware | Daily or on change |
| Products | Odoo | Odoo → Middleware | Daily or on change |
| Operators (Dealers) | Odoo | Odoo → Middleware | Daily or on change |
| FCC Configuration | Admin Config | Manual | On change |

## Business Rules

- BR-11.1: The middleware exposes a sync API that Databricks calls to upsert master data.
- BR-11.2: Sync is idempotent — re-syncing the same data produces no side effects.
- BR-11.3: Sync must validate required fields (e.g., operator TIN for dealer-operated sites) and reject invalid records with clear error messages.
- BR-11.4: The middleware must track `syncedAt` timestamps for all master data to detect stale records.
- BR-11.5: If critical master data is missing (e.g., a site referenced in a transaction has not been synced), the transaction must be queued and retried after the next sync cycle.

## Acceptance Criteria

- AC-11.1: Databricks can successfully sync all master data entities to the middleware.
- AC-11.2: Invalid records are rejected with descriptive errors.
- AC-11.3: Stale master data (not synced for > configurable threshold) triggers alerts.

------------------------------------------------------------------------

# REQ-12: Transaction Ingestion Modes (Pull / Push)

**Priority:** P0
**MVP:** Yes

## Description

The middleware supports two modes for receiving transactions from FCCs (`transactionMode`), configured per FCC. Separately, `ingestionMode` controls where the FCC is configured to deliver data (cloud or Edge Agent).

> **Key constraint**: Most FCC vendors can only be configured to push or send to **one endpoint**. Some FCC vendors also do not expose a Pull API. The ingestion architecture is designed to work within these constraints.

## Transaction Modes (How FCC Surfaces Data)

### Pull Mode

- The middleware (or Edge Agent) periodically polls the FCC for new transactions.
- Poll interval is configurable per FCC (`pullIntervalSeconds`).
- The adapter sends a fetch request and processes the response.
- The middleware must track the last successfully fetched transaction (cursor/offset/timestamp) to avoid re-fetching.
- In `CLOUD_DIRECT` mode: the cloud middleware performs the pull directly. In `RELAY`/`BUFFER_ALWAYS` modes: the Edge Agent performs the pull over LAN.

### Push Mode

- The FCC actively sends transactions to a configured endpoint.
- In `CLOUD_DIRECT` mode (default): the FCC pushes to the **Cloud Middleware** endpoint.
- In `RELAY`/`BUFFER_ALWAYS` modes: the FCC pushes to the **Edge Agent** over LAN.
- The receiving endpoint must acknowledge receipt before processing (at-least-once delivery).
- Push payloads may contain single or multiple transactions.

### Hybrid Mode

- Some FCCs support both Pull and Push. In hybrid mode, Push is the primary channel, and Pull acts as a fallback/catch-up mechanism to ensure no transactions are missed.

## Ingestion Routing Modes (Where FCC Data Flows)

`transactionMode` (Pull/Push/Hybrid) describes **how** the FCC surfaces data. `ingestionMode` describes **where** it is delivered. These are independent settings configured per FCC.

| Mode | Default | Description | When to Use |
|------|---------|-------------|-------------|
| `CLOUD_DIRECT` | **Yes** | **FCC is configured to push/send directly to the Cloud Middleware.** Edge Agent polls FCC over LAN as a catch-up safety net and uploads missed transactions to cloud. Cloud deduplicates dual-path arrivals. | Default for all sites. Requires FCC to be able to reach the cloud endpoint (public IP, VPN, or SIM-connected FCC). |
| `RELAY` | No | FCC is configured to push/pull via the Edge Agent over LAN. Edge Agent immediately relays to cloud when internet is available; buffers locally if not. | Sites where the FCC cannot reach the cloud directly (isolated LAN, no VPN). |
| `BUFFER_ALWAYS` | No | FCC delivers to Edge Agent. Edge Agent always buffers locally first and syncs on schedule — regardless of internet status. | High-volume sites, bursty connectivity, or where scheduled batch sync is preferred. |

## Business Rules

- BR-12.1: The `transactionMode` (Pull/Push/Hybrid) and `ingestionMode` (CLOUD_DIRECT/RELAY/BUFFER_ALWAYS) are configured independently per FCC (REQ-3). `CLOUD_DIRECT` is the default `ingestionMode`.
- BR-12.2: In Pull mode, the polling schedule must be managed by a background worker. Missed polls (e.g., due to downtime) must be caught up on restart.
- BR-12.3: In Push mode, the ingest endpoint must be highly available and must acknowledge receipt before processing (at-least-once delivery).
- BR-12.4: In Hybrid mode, Pull runs on a less frequent schedule (e.g., every 5 minutes) as a catch-up, while Push handles real-time flow.
- BR-12.5: In `CLOUD_DIRECT` mode, the Edge Agent must still poll the FCC over LAN at a configured interval as a catch-up safety net. Transactions collected via LAN catch-up are uploaded to the cloud, which handles deduplication against FCC push arrivals.
- BR-12.6: In `CLOUD_DIRECT` mode, if the Edge Agent cannot reach the cloud when it collects a catch-up transaction, it must buffer locally and upload on reconnection.
- BR-12.7: In `RELAY` mode, the Edge Agent must detect cloud unavailability and switch to local buffering automatically, then resume relay when cloud is reachable again — without manual intervention.
- BR-12.8: In `BUFFER_ALWAYS` mode, the Edge Agent syncs buffered transactions on a configurable schedule (e.g., every 5 minutes).
- BR-12.9: `CLOUD_DIRECT` mode requires the FCC to have a cloud-reachable endpoint. Sites where this is not possible must use `RELAY` or `BUFFER_ALWAYS`.
- BR-12.10: Regardless of ingestion mode, all transactions pass through the same deduplication and normalization pipeline at the cloud middleware.
- BR-12.11: Pre-auth commands always route via the Edge Agent regardless of `ingestionMode` (see REQ-6). `ingestionMode` only affects Normal Order (transaction) ingestion.

## Acceptance Criteria

- AC-12.1: Pull mode correctly polls DOMS at the configured interval and ingests new transactions.
- AC-12.2: Push mode correctly receives and acknowledges incoming transaction payloads at the configured endpoint (cloud or Edge Agent, per `ingestionMode`).
- AC-12.3: Hybrid mode does not produce duplicates when the same transaction arrives via both Push and Pull.
- AC-12.4: In `CLOUD_DIRECT` mode, transactions reach the cloud via FCC push; Edge Agent catch-up poll uploads do not result in duplicate records available for Odoo to poll.
- AC-12.5: In `RELAY` mode, transactions reach the cloud in real-time when internet is available; local buffering activates automatically when cloud is unreachable.
- AC-12.6: In `BUFFER_ALWAYS` mode, transactions are always buffered locally and synced on schedule.
- AC-12.7: Switching a site's `ingestionMode` takes effect without redeploying the Edge Agent (config-driven).

------------------------------------------------------------------------

# REQ-13: Duplicate Detection

**Priority:** P0
**MVP:** Yes

## Description

Transactions may arrive multiple times due to retries, Pull/Push overlap, Edge Agent catch-up poll overlap with FCC direct push, or Edge Agent replay after reconnection. The middleware must detect and suppress duplicates transparently. In the default `CLOUD_DIRECT` ingestion mode, the same transaction may legitimately arrive via two paths simultaneously: the FCC's direct cloud push AND the Edge Agent's LAN catch-up poll. This is expected and handled by primary key deduplication.

## Deduplication Strategy

### Primary Key

`fccTransactionId` + `siteCode` — this combination must be globally unique.

### Secondary Checks

If `fccTransactionId` is not available or not trustworthy:

- `siteCode` + `pumpNumber` + `nozzleNumber` + `endDateTime` + `amount` (within a configurable time tolerance, e.g., ±5 seconds) — these are **FCC pump/nozzle numbers** (`fcc_pump_number`, `fcc_nozzle_number`), i.e., as received from the FCC in the transaction payload

## Business Rules

- BR-13.1: A transaction that matches an existing record on the primary key is silently skipped (not treated as an error).
- BR-13.2: A transaction that matches on secondary checks is flagged as a potential duplicate for review but not auto-skipped.
- BR-13.3: Deduplication applies to both unsolicited transactions AND pre-auth dispense completions.
- BR-13.4: The deduplication window is configurable (e.g., check against last 30 days of transactions).

## Acceptance Criteria

- AC-13.1: Identical transactions received via Push and Pull are deduplicated.
- AC-13.2: Edge Agent replay after reconnection does not create duplicates.
- AC-13.3: Secondary-check matches are flagged for review.
- AC-13.4: A transaction arriving via both the FCC's direct cloud push and the Edge Agent's LAN catch-up poll is stored exactly once — the second arrival is silently skipped.

------------------------------------------------------------------------

# REQ-14: Audit Trail and Transaction Logging

**Priority:** P1
**MVP:** Yes

## Description

All transaction processing must produce an immutable audit trail published to the event bus (as per the selective event streaming architecture — Option C).

## Events Published

| Event | Trigger |
|-------|---------|
| `PreAuthRequested` | Pre-auth sent to FCC |
| `PreAuthAuthorized` | FCC confirms authorization |
| `PreAuthCancelled` | Pre-auth cancelled |
| `PreAuthExpired` | Pre-auth timed out |
| `TransactionReceived` | Raw transaction received from FCC (Push or Pull) |
| `TransactionNormalized` | Transaction normalized to canonical model |
| `TransactionDeduplicated` | Duplicate detected and skipped |
| `TransactionMatchedToPreAuth` | Dispense matched to pre-auth |
| `OdooOrderCreated` | Order successfully created in Odoo |
| `OdooOrderUpdated` | Order updated (reconciliation) |
| `ReconciliationFlagged` | Variance exceeds tolerance |
| `SyncCompleted` | Master data sync completed |

## Business Rules

- BR-14.1: All events are published to the event bus (RabbitMQ / Azure Service Bus) and persisted to an event store.
- BR-14.2: Events are immutable — no updates or deletes.
- BR-14.3: Each event includes: timestamp, event type, correlation ID, actor (system/user), and full payload.
- BR-14.4: Events must be retained for a minimum period per legal entity's regulatory requirements (configurable, default 7 years).

## Acceptance Criteria

- AC-14.1: All listed events are published during normal transaction flows.
- AC-14.2: Events are queryable by correlation ID, site, and time range.
- AC-14.3: No events are lost during system restarts (durable messaging).

------------------------------------------------------------------------

# REQ-15: Edge Android Agent (HHT)

**Priority:** P1
**MVP:** Partial

## Description

The Edge Agent is a native **Kotlin/Java** Android application running on the same **Urovo i9100 (Android 12) HHT** that runs Odoo POS. It provides local LAN connectivity to the FCC and offline transaction buffering when internet connectivity is unavailable or unreliable.

The FCC adapter logic (protocol handling for DOMS, Radix, etc.) is implemented **within the Edge Agent** — not a separate cloud-only component. This means the Edge Agent is self-contained for FCC communication.

## Architecture

- **Odoo POS** is installed on the HHT and connects to Odoo cloud via internet (SIM or WiFi).
- **Edge Agent** is a separate Android app (APK) on the same HHT that communicates with the FCC over **local WiFi LAN**.
- Both apps coexist on the same physical device.
- The station **LAN is always on** — independent of internet availability. Internet outages do not affect LAN connectivity to the FCC.
- In the default `CLOUD_DIRECT` ingestion mode, the **FCC pushes transactions directly to the Cloud Middleware**. The Edge Agent is not the primary transaction receiver — it acts as a LAN catch-up safety net and is always the pre-auth handler.

## Connectivity Topology

```
[Forecourt Controller]
    │
    ├── Push (primary) ──────────────────────────► [Cloud Middleware] ──► [Odoo Cloud]
    │                                                       ▲  │
    │                                              pre-auth │  │ SYNCED_TO_ODOO status
    │                                                       │  │ catch-up upload
    └── Poll LAN (catch-up) ◄──────────────── [Edge Agent] ◄──┘
                                                    │
                              ┌─────────────────────┤
                              │                     │
                   (online) [Odoo Cloud]    (offline) [Edge Agent API]
                              │                     │
                              └────────[Odoo POS]───┘

Pre-Auth (always over LAN):
  [Odoo POS] ──► [Edge Agent local API] ──► [FCC]
  [Edge Agent] ──► [Cloud Middleware] (queues pre-auth for reconciliation)
```

## Operating Modes

Behaviour in the table below reflects the default `ingestionMode = CLOUD_DIRECT`. See REQ-12 for `RELAY` and `BUFFER_ALWAYS` variants.

| Mode | Internet | FCC LAN | Behaviour (CLOUD_DIRECT — default) |
|------|----------|---------|--------------------------------------|
| **Fully Online** | Up | Up | FCC pushes transactions to Cloud Middleware. Edge Agent polls FCC over LAN as catch-up; uploads any missed transactions to cloud. Cloud stores transactions (status: PENDING). **Odoo polls Cloud Middleware** and creates orders; acknowledges back to cloud; cloud marks `SYNCED_TO_ODOO`. Edge Agent syncs this status. |
| **Internet Down** | Down | Up | FCC push to cloud fails/queues at FCC. Edge Agent **polls FCC over LAN and buffers transactions locally**. **Odoo POS polls Edge Agent local API** and creates orders from buffered transactions. Pre-auth still works (LAN only). On recovery, Edge Agent uploads buffer; cloud deduplicates; Odoo switches back to polling Cloud Middleware. |
| **FCC Unreachable** | Up or Down | Down | Alert site supervisor. LAN catch-up not possible. FCC-to-cloud push may still work independently. Log LAN connectivity gap. |
| **Fully Offline** | Down | Down | No ingestion possible. Odoo POS operates in manual mode. Alert on recovery. |

Mode transitions are automatic (no manual intervention) and logged as audit events.

## REQ-15.1: FCC Communication over LAN

- The Edge Agent communicates with the FCC over the station's local WiFi LAN.
- It uses the same adapter protocol logic as the cloud middleware (DOMS, Radix, etc.) — implemented natively in Kotlin/Java.
- Connection details (FCC IP, port, credentials) are provisioned to the agent during setup.
- The agent maintains a persistent or periodic connection depending on the vendor protocol.
- Heartbeat monitoring: the agent periodically pings the FCC and reports health status.

## REQ-15.2: LAN Catch-Up Poll (Safety Net Ingestion)

In the default `CLOUD_DIRECT` ingestion mode, the FCC pushes transactions directly to the Cloud Middleware. The Edge Agent is **not** the primary transaction receiver. Its role in ingestion is a safety-net catch-up:

- The Edge Agent polls the FCC over LAN at a configured interval (`pullIntervalSeconds`). This catches any transactions the cloud may have missed (e.g., FCC's cloud push failed or queued during an outage).
- The agent normalizes collected transactions using the embedded adapter logic.
- Catch-up transactions are forwarded to the Cloud Middleware. The cloud deduplicates against any transactions already received via the FCC's direct push.
- If the cloud is unreachable when the agent polls, transactions are written to the local buffer and uploaded on reconnection.

In `RELAY` or `BUFFER_ALWAYS` ingestion modes, the Edge Agent is the **primary receiver** (FCC is configured to deliver to the Edge Agent, not the cloud):
- `RELAY`: immediately forwards to cloud if internet is available; buffers locally if not.
- `BUFFER_ALWAYS`: always writes to local buffer first; syncs on schedule.

The `ingestionMode` is a configuration value pushed to the agent by the cloud middleware. Changing it does not require an APK update.

## REQ-15.3: Pre-Auth Relay and Cloud Queue

Pre-auth **always** routes via the Edge Agent, regardless of `ingestionMode`:

- Odoo POS sends the pre-auth request to the **Edge Agent's local API** (localhost on the same HHT). This applies in both online and offline modes — no internet is required for authorization.
- The Edge Agent sends the pre-auth command to the FCC over LAN and receives the authorization response.
- The Edge Agent returns the authorization result to Odoo POS.
- The Edge Agent **queues the pre-auth record to the Cloud Middleware** asynchronously for reconciliation. If the cloud is unreachable, the record is queued locally and forwarded with retry when internet returns.
- The Cloud Middleware stores the pre-auth record so it can match the final dispense transaction when it arrives via the FCC's cloud push (REQ-8).

## REQ-15.3a: Transaction Status Sync (SYNCED_TO_ODOO)

- The Cloud Middleware marks each transaction `SYNCED_TO_ODOO` after **Odoo acknowledges** that it has created the order (via `POST /transactions/acknowledge`).
- The Edge Agent **periodically polls the Cloud Middleware** to fetch `SYNCED_TO_ODOO` status updates for transactions it holds in its local buffer.
- Transactions confirmed as `SYNCED_TO_ODOO` are flagged in the local buffer accordingly.
- The Edge Agent's local API (`GET /api/transactions`) must **exclude** `SYNCED_TO_ODOO` transactions from results returned to Odoo POS. This prevents Odoo from seeing transactions it has already created orders for when polling the cloud.
- The status sync runs on the same connectivity check cycle as the cloud health ping (e.g., every 30 seconds when online).

## REQ-15.4: Offline Transaction Buffering (Store-and-Forward)

- In `CLOUD_DIRECT` mode (default): buffering activates **only when the cloud is unreachable** during catch-up poll upload. Under normal conditions, catch-up transactions are forwarded directly to the cloud without being stored in the buffer first.
- In `RELAY` mode: buffering activates when the cloud becomes unreachable. Under normal conditions, transactions are relayed in real-time.
- In `BUFFER_ALWAYS` mode: all transactions are written to the local buffer regardless of internet status, then synced on schedule.
- In all modes, the Edge Agent **continues to poll the FCC over LAN** uninterrupted when internet is down.
- All buffered transactions are stored in a **local SQLite database** (WAL mode for crash resilience) on the HHT.
- Each buffered transaction stores: full canonical payload, raw FCC payload, timestamp, sync status (`PENDING`, `SYNCED`, `SYNCED_TO_ODOO`, `FAILED`).
- Buffer survives app restarts and device reboots.
- **Buffer capacity**: Must handle at least **30 days × 1,000 transactions/day** (30,000+ transactions) on the Urovo i9100 without storage issues.

## REQ-15.5: Automatic Replay on Reconnection

- Agent continuously monitors internet connectivity (ping cloud middleware health endpoint, e.g., every 30 seconds).
- On connectivity restored: replay all `PENDING` buffered transactions to cloud middleware in chronological order.
- Replay is idempotent — cloud middleware handles deduplication (REQ-13).
- Batched uploads: configurable batch size (e.g., 50 transactions per request).
- Failed replays: exponential backoff. Agent does not skip ahead — order is maintained.
- On confirmed sync: transaction status updated to `SYNCED`. `SYNCED` transactions retained locally for a configurable period (e.g., 7 days) before cleanup.

## REQ-15.6: Local API for Odoo POS (Offline Mode)

The Edge Agent exposes a **REST API on localhost** (e.g., `http://localhost:8585`) accessible to Odoo POS on the same device:

| Endpoint | Description |
|----------|-------------|
| `GET /api/transactions` | Fetch recent transactions from local buffer (paginated, filterable by time, pump, product). **Excludes** transactions with status `SYNCED_TO_ODOO`. |
| `GET /api/transactions/{id}` | Fetch a specific transaction |
| `GET /api/pump-status` | Get current pump statuses from FCC (live, over LAN) |
| `POST /api/preauth` | Submit a pre-auth request — **this endpoint is always available** (online or offline). Odoo POS always sends pre-auth here, not to the cloud. |
| `POST /api/preauth/{id}/cancel` | Cancel a local pre-auth |
| `GET /api/status` | Agent health: FCC connectivity, internet status, buffer depth, last sync time, `SYNCED_TO_ODOO` count |

- When internet is available: Odoo POS fetches transaction data from the cloud. Pre-auth requests are always sent to the Edge Agent local API.
- When internet is down: Odoo POS automatically switches to the local Edge Agent API for transaction visibility. Pre-auth continues to work uninterrupted. Switch should be automatic with a manual override for the attendant.

## REQ-15.7: Attendant-Triggered Manual Pull

- Attendant can trigger an on-demand Pull from the FCC via a button in Odoo POS.
- Useful for immediately surfacing a just-completed dispense without waiting for the next poll cycle.
- Manual pull result is returned to Odoo POS and stored in the local buffer.

## REQ-15.8: Multi-HHT Site Handling

- Busy sites may have **multiple HHTs** (one per attendant).
- One Desktop Edge Agent and one or more Android Edge Agents may run in parallel at the same site.
- Exactly **one eligible online agent** must hold the `PRIMARY` role at a time. All other participating agents must be `STANDBY_HOT`, `RECOVERING`, or `READ_ONLY`.
- **All HHTs must see the same transaction data**. Attendants always select pump and nozzle when creating orders (pre-auth or normal orders). Every Android HHT continues to talk to `localhost`; standby devices proxy or serve replicated data as appropriate.
- Default leader preference is **Desktop first, then Android agents by configured priority**, but the policy must remain configuration-driven per site.
- Automatic failover must promote a warm, healthy standby within **30 seconds** of confirmed primary failure.
- A recovered former primary rejoins as standby. It must **not auto-preempt** the current leader.

## REQ-15.9: Connectivity Detection and Mode Switching

- FCC detection: periodic heartbeat to FCC (vendor-specific, e.g., every 15 seconds).
- Internet detection: periodic ping to cloud middleware health endpoint (e.g., every 30 seconds).
- On internet recovery: upload buffered transactions to cloud; fetch `SYNCED_TO_ODOO` status updates; flush queued pre-auth records to cloud.
- Mode transitions are automatic — no manual intervention required.
- All transitions are logged as audit events.

## REQ-15.10: Security

- FCC credentials stored **encrypted** on the HHT (Android Keystore).
- Local API binds to localhost by default on every Android device; peer/LAN exposure is an explicitly enabled HA capability.
- When LAN access is enabled for peer devices: API requires an **API key** or signed peer credentials provisioned during setup.
- Cloud-to-agent communication uses mutual TLS or API key authentication.
- Agent authenticates to cloud middleware with a device-level service token. No Odoo user credentials stored.
- Authoritative agent-to-cloud writes must carry the current **leader epoch** once HA fencing is enabled. Stale epochs must be rejected.

## REQ-15.11: Provisioning and Configuration

- Edge Agent installed as a separate Android APK on the HHT.
- Initial configuration via:
  - **QR code scan** (preferred for field deployment) — encodes site code, FCC connection details, cloud middleware URL, device token.
  - **Manual entry** — fallback.
  - **Cloud push** — cloud middleware pushes config after registration (for updates).
- Configuration includes: site code, FCC vendor, FCC IP/port, credentials, cloud middleware URL, device token, poll interval, buffer settings.
- Remote config updates: cloud middleware can push updated config (e.g., new FCC IP) on each cloud sync.

## REQ-15.12: Monitoring and Diagnostics

- Agent reports telemetry to cloud middleware when online: FCC connectivity, buffer depth, last sync timestamp, battery level, storage availability, app version.
- Cloud dashboard shows per-site agent health.
- Local diagnostics screen on HHT (Site Supervisor access): FCC connection status, internet status, buffer depth, last sync time, manual pull button, last 100 log entries.

## REQ-15.13: Update and Lifecycle Management

- Agent updates distributed via **Sure MDM** or enterprise sideload as fallback.
- Agent must be backward-compatible with cloud middleware (older agent works with newer cloud, within a supported version range).
- Cloud middleware exposes `/agent/version-check` — agent calls on startup. If below minimum supported version, agent alerts Site Supervisor and disables FCC communication until updated.

## REQ-15.14: Data Integrity and Recovery

- SQLite buffer uses **WAL mode** for crash resilience.
- On startup: buffer integrity check (`PRAGMA integrity_check`).
- If corruption detected: backup corrupted DB, start fresh buffer, alert cloud middleware for forensic retrieval.
- Transactions never deleted until confirmed synced to cloud.

## Business Rules

- BR-15.1: The Edge Agent must continue FCC LAN communication (catch-up polling and pre-auth) even when internet is down.
- BR-15.2: Buffered transactions must survive Edge Agent restarts (persisted SQLite, WAL mode).
- BR-15.3: Catch-up and replay must be idempotent — cloud middleware handles deduplication (REQ-13).
- BR-15.4: Mode transitions (online/offline/recovery) must be automatic — no manual intervention required.
- BR-15.5: All HHTs at a site must have access to the same transaction data while preserving the Android localhost contract on every device.
- BR-15.6: FCC adapter logic runs on-device (Kotlin/Java). The Edge Agent is self-contained for FCC communication.
- BR-15.7: Buffer must be sized for a minimum of 30 days × 1,000 transactions/day on the Urovo i9100.
- BR-15.8: The `ingestionMode` is config-driven and pushed from the cloud middleware. Changing it must not require an APK update or manual device intervention.
- BR-15.9: In `RELAY` mode, the transition from relay to buffer (on cloud loss) and back (on cloud recovery) must be fully automatic and transparent to Odoo POS and the attendant.
- BR-15.10: Pre-auth requests from Odoo POS must always be routed to the Edge Agent local API, regardless of `ingestionMode` or internet availability.
- BR-15.11: The Edge Agent must queue pre-auth records to the Cloud Middleware for reconciliation. If offline, it must retry with exponential backoff when internet returns.
- BR-15.12: The Edge Agent must poll the Cloud Middleware for `SYNCED_TO_ODOO` status and must not offer transactions already confirmed as `SYNCED_TO_ODOO` to Odoo POS via the local API.
- BR-15.13: Site high-availability must use epoch-based leader fencing to prevent stale primaries from performing authoritative writes after failover.
- BR-15.14: Automatic promotion is allowed only when standby replication lag is within the configured threshold and the warm replica is complete.

## Acceptance Criteria

- AC-15.1: The Edge Agent (Kotlin/Java) successfully communicates with a DOMS FCC over local LAN.
- AC-15.2: Transactions buffered during an internet outage are uploaded to the cloud on reconnection without data loss.
- AC-15.3: Odoo POS on the same HHT can fetch transactions from the Edge Agent local API in offline mode. Transactions already `SYNCED_TO_ODOO` are excluded from results.
- AC-15.4: No duplicate Odoo orders are created after cloud-path processing and Edge Agent catch-up, thanks to deduplication and `SYNCED_TO_ODOO` status checking.
- AC-15.5: Non-primary HHTs at the same site can use their local Android agent facade and still see the same transaction data without manual primary IP entry.
- AC-15.6: QR code provisioning correctly configures the agent for a new site deployment.
- AC-15.7: Agent updates can be pushed and applied via Sure MDM without data loss.
- AC-15.8: In `RELAY` mode, the agent immediately forwards transactions to cloud when online and seamlessly falls back to buffering when cloud becomes unreachable — with no data loss or manual action.
- AC-15.9: Changing `ingestionMode` via cloud config push takes effect on the agent without requiring an APK update.
- AC-15.10: Pre-auth requests submitted offline (via Edge Agent) are correctly queued and forwarded to the cloud when internet returns, enabling proper reconciliation.
- AC-15.11: In `CLOUD_DIRECT` mode, the Edge Agent's catch-up LAN poll detects and uploads a transaction that the FCC failed to push to the cloud directly.
- AC-15.12: Automatic failover completes within 30 seconds of confirmed primary failure with no buffered transaction loss and no duplicate pre-auth execution.
- AC-15.13: A stale former primary attempting an authoritative cloud write after failover is rejected by leader epoch fencing.

------------------------------------------------------------------------

# REQ-16: Error Handling, Retry, and Alerting

**Priority:** P1
**MVP:** Yes

## Description

The middleware must handle failures gracefully across all integration points.

## Error Categories

| Category | Examples | Handling |
|----------|----------|----------|
| **FCC Communication Failure** | Timeout, connection refused, protocol error | Retry with exponential backoff. Alert after threshold. |
| **Odoo API Failure** | Timeout, validation error, server error | Queue for retry. Alert after threshold. |
| **Payload Parse Error** | Invalid JSON, missing required fields, unknown format | Log raw payload. Flag for investigation. Do not retry (manual fix needed). |
| **Duplicate Detected** | Transaction already exists | Silent skip. Log for audit. |
| **Master Data Missing** | Site or pump not found in middleware | Queue transaction. Retry after next sync cycle. |
| **Fiscalization Failure** | FCC fails to return fiscal receipt | Flag transaction. Alert Operations Manager. |

## Business Rules

- BR-16.1: All retryable failures use exponential backoff with jitter (initial delay: 5s, max delay: 5 minutes, max retries: configurable).
- BR-16.2: Non-retryable failures (parse errors, unknown sites) are flagged immediately.
- BR-16.3: Alerts are sent via configurable channels (email, webhook, dashboard notification).
- BR-16.4: A dead-letter queue captures transactions that exhaust all retries.

## Acceptance Criteria

- AC-16.1: Transient FCC failures are retried and succeed when the FCC recovers.
- AC-16.2: Persistent failures are escalated via alerts.
- AC-16.3: Dead-letter transactions are visible and can be manually retried.

------------------------------------------------------------------------

# REQ-17: Multi-Tenancy and Data Isolation

**Priority:** P1
**MVP:** Yes

## Description

The middleware serves multiple legal entities (countries) from a single deployment. Data must be logically isolated.

## Business Rules

- BR-17.1: All data is partitioned by `legalEntityId`. Queries and APIs are scoped to a legal entity context.
- BR-17.2: API authentication must include legal entity context (e.g., via API key scoping or token claims).
- BR-17.3: Cross-legal-entity data access is only available to System Administrators.
- BR-17.4: Database-level isolation is achieved via row-level filtering (not separate databases per tenant) for MVP. Physical isolation may be considered post-MVP.

## Acceptance Criteria

- AC-17.1: API calls scoped to one legal entity cannot access data from another.
- AC-17.2: System Administrators can query across legal entities for operational views.

------------------------------------------------------------------------

# Non-Functional Requirements

| ID | Requirement | Target | MVP |
|----|------------|--------|-----|
| NFR-1 | **Availability** | 99.5% uptime for cloud middleware | Yes |
| NFR-2 | **Latency** | Pre-auth round-trip < 5 seconds (middleware processing, excluding FCC response time) | Yes |
| NFR-3 | **Throughput** | Support 100 concurrent sites with up to 10 transactions/minute per site | Yes |
| NFR-4 | **Data Retention** | Transaction data retained for minimum 7 years (configurable per legal entity) | Yes |
| NFR-5 | **Security** | All API endpoints secured via OAuth 2.0 / API keys. All data encrypted in transit (TLS 1.2+) and at rest. | Yes |
| NFR-6 | **Scalability** | Horizontal scaling of middleware workers and adapters | Post-MVP |
| NFR-7 | **Observability** | Structured logging, distributed tracing (OpenTelemetry), health check endpoints | Yes |
| NFR-8 | **Recovery** | Edge Agent must not lose buffered transactions on restart. Cloud middleware must recover processing state on restart. | Yes |

------------------------------------------------------------------------

# MVP Scope Summary

## In Scope (MVP)

- Legal entity and site configuration (synced via Databricks)
- DOMS adapter (first FCC vendor)
- Connected / Disconnected mode
- Fiscalization configuration (FCC_DIRECT for Tanzania, EXTERNAL_INTEGRATION for Malawi)
- Pre-authorization order flow (Tax ID fiscalization use case)
- Normal Order ingestion (Pull and Push) — default transaction type
- Pre-auth reconciliation with volume adjustment
- Automatic Odoo order creation
- Payload normalization (DOMS)
- Duplicate detection
- Master data sync API
- Audit trail and event logging
- Error handling with retry and dead-letter queue
- Multi-tenancy with row-level isolation
- Edge Android Agent (Urovo i9100, Android 12, Kotlin/Java) — LAN communication, offline buffering, local API, multi-HHT LAN visibility (partial — failover and OTA deferred)

## Out of Scope (Post-MVP)

- Radix, Advatec, Petronite adapters (Phase 3)
- Reconciliation dashboards (Phase 4)
- Admin portal (Angular) for configuration management
- Physical database isolation per legal entity
- Horizontal auto-scaling
- Edge Agent fleet management and OTA updates (Sure MDM push is MVP; advanced OTA lifecycle is post-MVP)
- Advanced analytics and reporting
