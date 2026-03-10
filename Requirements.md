# Forecourt Middleware — Requirements Specification

Version: 1.0
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
10. [REQ-7: Unsolicited Transactions (Normal Orders)](#req-7-unsolicited-transactions-normal-orders)
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
| HHT | Handheld Terminal — Android device used by fuel attendants, running Odoo POS. |
| Pre-Auth | Pre-Authorization — an order authorized in Odoo before fuel is dispensed. The FCC authorizes the pump for a specific amount/volume. |
| Unsolicited Transaction | A dispense transaction that occurs without a prior pre-auth order. The attendant lifts the nozzle and dispenses directly. |
| Fiscalization | The process of reporting a transaction to a government tax authority (e.g., TRA in Tanzania, MRA in Malawi) for tax compliance. |
| Pull Mode | The middleware periodically polls the FCC for new transactions. |
| Push Mode | The FCC actively sends transactions to the middleware as they occur. |
| Edge Agent | A lightweight service running on the HHT (Android) or station-level device, communicating with FCC over local LAN. |
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
| REQ-7 | Unsolicited Transactions (Normal Orders) | P0 | Yes |
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
| `isActive` | Boolean | Whether this FCC is currently operational |
| `lastHeartbeatAt` | DateTime (nullable) | Last successful communication timestamp |
| `registeredAt` | DateTime | When this FCC was first registered |

## Pump and Nozzle Mapping

| Field | Type | Description |
|-------|------|-------------|
| `pumpNozzleId` | UUID | Internal unique identifier |
| `fccId` | UUID (FK) | Associated FCC |
| `pumpNumber` | Integer | Physical pump number as known to FCC |
| `nozzleNumber` | Integer | Physical nozzle number |
| `productCode` | String | Fuel product (e.g., PMS, AGO, IK) |
| `odooPumpId` | String (nullable) | Mapping to Odoo pump reference |
| `isActive` | Boolean | Whether this nozzle is active |

## Business Rules

- BR-3.1: A site can have at most one active FCC at a time.
- BR-3.2: If no FCC is assigned or active, the site operates in disconnected mode.
- BR-3.3: The `fccVendor` determines which adapter is used for communication.
- BR-3.4: Pump/nozzle mappings must match the physical FCC configuration. Mismatches must raise alerts.
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

Pre-authorization is the process where an Odoo POS order is created BEFORE fuel is dispensed. The middleware sends a pre-auth request to the FCC, which authorizes a specific pump for a requested amount or volume. The attendant then dispenses fuel, and the actual dispense transaction is returned later (see REQ-8).

## Flow

1. **Attendant** creates an order in Odoo POS (captures customer details, vehicle, product, amount).
2. **Odoo** sends a pre-auth request to the **Middleware API**.
3. **Middleware** resolves the site, FCC, and adapter.
4. **Adapter** translates and sends the pre-auth command to the **FCC**.
5. **FCC** authorizes the pump and responds with confirmation.
6. **Middleware** stores the pre-auth record and responds to Odoo.
7. Fuel is dispensed physically.
8. The final dispense transaction is received later via Pull/Push (see REQ-8, REQ-12).

## Pre-Auth Request Payload (Canonical)

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `odooOrderId` | String | Yes | Odoo POS order reference |
| `siteCode` | String | Yes | Site identifier |
| `pumpNumber` | Integer | Yes | Target pump |
| `nozzleNumber` | Integer | Yes | Target nozzle |
| `productCode` | String | Yes | Fuel product (PMS, AGO, IK) |
| `requestedAmount` | Decimal | Yes | Authorized amount in local currency |
| `requestedVolume` | Decimal | No | Authorized volume in litres (if applicable) |
| `unitPrice` | Decimal | Yes | Price per litre at time of auth |
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

- BR-6.1: Pre-auth is only applicable for sites in **connected** mode with `fiscalizationMode` = `FCC_DIRECT` or where the business process requires pump authorization.
- BR-6.2: Each pre-auth must be uniquely identifiable by `odooOrderId` + `siteCode`. Duplicate pre-auth requests for the same order must be rejected (idempotency).
- BR-6.3: Customer tax details (`customerTaxId`, `customerName`, `customerBusinessName`) are passed through to the FCC adapter when `requireCustomerTaxId` = true on the site's fiscalization config.
- BR-6.4: A pre-auth that is not completed or cancelled within a configurable timeout (e.g., 15 minutes) must transition to `EXPIRED` and release the pump authorization.
- BR-6.5: Cancellation is allowed only in `PENDING` or `AUTHORIZED` states.
- BR-6.6: The middleware must support cancellation via API: `POST /preauth/{id}/cancel`.

## Acceptance Criteria

- AC-6.1: A pre-auth request from Odoo is successfully sent to the correct FCC via the appropriate adapter.
- AC-6.2: The FCC response is correctly mapped and stored; the pre-auth state is updated.
- AC-6.3: Customer tax details are included in the FCC payload for fiscalized sites.
- AC-6.4: Duplicate pre-auth requests are rejected with an appropriate error.
- AC-6.5: Expired pre-auths are automatically transitioned and the pump authorization is released.

------------------------------------------------------------------------

# REQ-7: Unsolicited Transactions (Normal Orders)

**Priority:** P0
**MVP:** Yes

## Description

Unsolicited transactions occur when an attendant dispenses fuel WITHOUT creating a pre-auth order in Odoo. The attendant lifts the nozzle and dispenses directly. The FCC records the transaction, and the middleware ingests it via Pull or Push (see REQ-12).

These transactions have no corresponding Odoo order at the time of dispense. The middleware must ingest them and create orders in Odoo (see REQ-9).

## Transaction Payload (Canonical — Received from FCC)

| Field | Type | Description |
|-------|------|-------------|
| `fccTransactionId` | String | Unique transaction ID from the FCC |
| `siteCode` | String | Site where dispense occurred |
| `pumpNumber` | Integer | Pump used |
| `nozzleNumber` | Integer | Nozzle used |
| `productCode` | String | Fuel product dispensed |
| `volume` | Decimal | Litres dispensed |
| `amount` | Decimal | Total amount in local currency |
| `unitPrice` | Decimal | Price per litre |
| `startDateTime` | DateTime | Dispense start time |
| `endDateTime` | DateTime | Dispense end time |
| `fiscalReceiptNumber` | String (nullable) | If FCC fiscalizes directly |
| `rawPayload` | JSON | Full original FCC payload (preserved for audit) |

## Business Rules

- BR-7.1: Unsolicited transactions arrive via Pull or Push (REQ-12). The middleware must handle both modes per site/FCC configuration.
- BR-7.2: The middleware must NOT assume a fixed batch size. Payloads may contain a single transaction or multiple transactions. The system must process whatever the FCC sends — whether one-by-one or bulk — without requiring a configuration flag. The adapter must handle payload inspection and iteration.
- BR-7.3: Each unsolicited transaction must be checked for duplicates before processing (REQ-13).
- BR-7.4: After deduplication, the transaction is normalized to the canonical model and forwarded for Odoo order creation (REQ-9).
- BR-7.5: The raw FCC payload must be preserved alongside the canonical model for audit purposes.

## Acceptance Criteria

- AC-7.1: Unsolicited transactions from DOMS are correctly ingested and normalized.
- AC-7.2: Single-transaction and bulk payloads are both processed correctly without configuration changes.
- AC-7.3: Duplicate transactions are detected and skipped without error.
- AC-7.4: Raw payloads are stored for audit trail.

------------------------------------------------------------------------

# REQ-8: Pre-Auth Reconciliation and Volume Adjustment

**Priority:** P0
**MVP:** Yes

## Description

When a pre-auth order is completed, the actual dispensed volume/amount may differ from the pre-authorized amount. The middleware must match the final dispense transaction to the original pre-auth and update the Odoo order with the actual quantities.

## Flow

1. A final dispense transaction is received from the FCC (via Pull/Push).
2. The middleware matches it to an existing pre-auth record using FCC transaction correlation IDs, pump/nozzle, and time window.
3. If the actual dispensed amount differs from the pre-authorized amount:
   a. The middleware calculates the adjustment (over-dispense or under-dispense).
   b. The middleware sends an order update to Odoo with the actual volume, amount, and unit price.
4. The pre-auth record transitions to `COMPLETED`.
5. A reconciliation record is created capturing the variance.

## Reconciliation Record

| Field | Type | Description |
|-------|------|-------------|
| `reconciliationId` | UUID | Internal identifier |
| `preAuthId` | UUID (FK) | Original pre-auth |
| `fccTransactionId` | String | Final dispense transaction from FCC |
| `authorizedAmount` | Decimal | Original pre-auth amount |
| `actualAmount` | Decimal | Actual dispensed amount |
| `authorizedVolume` | Decimal | Original pre-auth volume (if set) |
| `actualVolume` | Decimal | Actual dispensed volume |
| `variance` | Decimal | Difference (actual - authorized) |
| `variancePercentage` | Decimal | Percentage variance |
| `odooOrderUpdated` | Boolean | Whether Odoo was successfully updated |
| `reconciledAt` | DateTime | Timestamp of reconciliation |
| `status` | Enum | MATCHED, VARIANCE_WITHIN_TOLERANCE, VARIANCE_FLAGGED, UNMATCHED |

## Business Rules

- BR-8.1: The middleware must attempt to correlate every dispense transaction at a pre-auth site to an existing pre-auth record.
- BR-8.2: Correlation uses: FCC-provided correlation ID (if available), pump number + nozzle number + time window, and `odooOrderId` echoed by the FCC (if supported by the adapter).
- BR-8.3: If the variance is within a configurable tolerance (e.g., ±2%), the reconciliation is auto-approved.
- BR-8.4: If the variance exceeds tolerance, the reconciliation is flagged for Operations Manager review.
- BR-8.5: Odoo must be updated with actual volume and amount regardless of variance.
- BR-8.6: If no matching pre-auth is found for a dispense transaction at a pre-auth site, the transaction is flagged as `UNMATCHED` for investigation.
- BR-8.7: Unmatched transactions at pre-auth sites should still create Odoo orders (as unsolicited) but with a reconciliation flag.

## Acceptance Criteria

- AC-8.1: A dispense transaction that matches a pre-auth correctly updates the Odoo order with actual amounts.
- AC-8.2: Variance within tolerance is auto-approved.
- AC-8.3: Variance exceeding tolerance is flagged for review.
- AC-8.4: Unmatched transactions at pre-auth sites are flagged and still create Odoo orders.

------------------------------------------------------------------------

# REQ-9: Automatic Order Creation in Odoo

**Priority:** P0
**MVP:** Yes

## Description

For unsolicited transactions (REQ-7) and unmatched transactions at pre-auth sites (REQ-8), the middleware must automatically create orders in Odoo POS.

## Flow

1. A normalized transaction passes duplicate detection (REQ-13).
2. The middleware constructs an Odoo order payload with: site, pump, nozzle, product, volume, amount, unit price, timestamps, fiscal receipt (if any).
3. The middleware calls the Odoo API to create the order.
4. Odoo returns the created order ID.
5. The middleware stores the mapping: `fccTransactionId` <-> `odooOrderId`.

## Business Rules

- BR-9.1: Every unsolicited transaction that passes deduplication MUST result in an Odoo order.
- BR-9.2: The Odoo order creation must be idempotent — if the middleware retries (e.g., after a failure), it must not create duplicate orders. This is enforced via `fccTransactionId` as an idempotency key.
- BR-9.3: If Odoo order creation fails, the transaction must be queued for retry with exponential backoff.
- BR-9.4: After a configurable number of retries (e.g., 5), the transaction is marked as `FAILED_TO_SYNC` and an alert is raised for the Operations Manager.
- BR-9.5: Operator tax details (from site config) must be included in the Odoo order where applicable (CODO/DODO sites).

## Acceptance Criteria

- AC-9.1: Unsolicited transactions automatically create orders in Odoo.
- AC-9.2: Retry logic correctly handles transient Odoo API failures.
- AC-9.3: No duplicate orders are created in Odoo for the same FCC transaction.
- AC-9.4: Failed syncs are visible to the Operations Manager with retry capability.

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
| `pumpNumberOffset` | If FCC numbering differs from physical | 0 |

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

The middleware supports two modes for receiving transactions from FCCs, configured per FCC.

## Modes

### Pull Mode

- The middleware (or Edge Agent) periodically polls the FCC for new transactions.
- Poll interval is configurable per FCC (`pullIntervalSeconds`).
- The adapter sends a fetch request and processes the response.
- The middleware must track the last successfully fetched transaction (cursor/offset/timestamp) to avoid re-fetching.

### Push Mode

- The FCC actively sends transactions to a webhook/endpoint exposed by the middleware or Edge Agent.
- The middleware must expose a vendor-specific or generic ingest endpoint.
- Push payloads may contain single or multiple transactions.

### Hybrid Mode

- Some FCCs support both Pull and Push. In hybrid mode, Push is the primary channel, and Pull acts as a fallback/catch-up mechanism to ensure no transactions are missed.

## Business Rules

- BR-12.1: The transaction mode is configured per FCC (REQ-3).
- BR-12.2: In Pull mode, the polling schedule must be managed by a background worker. Missed polls (e.g., due to downtime) must be caught up on restart.
- BR-12.3: In Push mode, the ingest endpoint must be highly available and must acknowledge receipt before processing (at-least-once delivery).
- BR-12.4: In Hybrid mode, Pull runs on a less frequent schedule (e.g., every 5 minutes) as a catch-up, while Push handles real-time flow.
- BR-12.5: Regardless of mode, all transactions pass through the same deduplication and normalization pipeline.

## Acceptance Criteria

- AC-12.1: Pull mode correctly polls DOMS at the configured interval and ingests new transactions.
- AC-12.2: Push mode correctly receives and acknowledges incoming transaction payloads.
- AC-12.3: Hybrid mode does not produce duplicates when the same transaction arrives via both Push and Pull.

------------------------------------------------------------------------

# REQ-13: Duplicate Detection

**Priority:** P0
**MVP:** Yes

## Description

Transactions may arrive multiple times due to retries, Pull/Push overlap, or Edge Agent replay. The middleware must detect and suppress duplicates.

## Deduplication Strategy

### Primary Key

`fccTransactionId` + `siteCode` — this combination must be globally unique.

### Secondary Checks

If `fccTransactionId` is not available or not trustworthy:

- `siteCode` + `pumpNumber` + `nozzleNumber` + `endDateTime` + `amount` (within a configurable time tolerance, e.g., ±5 seconds)

## Business Rules

- BR-13.1: A transaction that matches an existing record on the primary key is silently skipped (not treated as an error).
- BR-13.2: A transaction that matches on secondary checks is flagged as a potential duplicate for review but not auto-skipped.
- BR-13.3: Deduplication applies to both unsolicited transactions AND pre-auth dispense completions.
- BR-13.4: The deduplication window is configurable (e.g., check against last 30 days of transactions).

## Acceptance Criteria

- AC-13.1: Identical transactions received via Push and Pull are deduplicated.
- AC-13.2: Edge Agent replay after reconnection does not create duplicates.
- AC-13.3: Secondary-check matches are flagged for review.

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
**MVP:** Partial (detailed requirements to follow)

## Description

The Edge Agent runs on the same Android HHT device that runs Odoo POS. It provides local LAN connectivity to the FCC when internet connectivity is unavailable or unreliable.

## Architecture

- **Odoo POS** is installed on the HHT and connects to Odoo cloud via internet (SIM or WiFi).
- **Edge Agent** is a lightweight service on the same HHT that communicates with the FCC over **local WiFi LAN**.
- The Edge Agent and Odoo POS coexist on the same device but serve different purposes.

## Connectivity Topology

```
[Odoo Cloud] <-- Internet (SIM/WiFi) --> [HHT: Odoo POS]
                                          [HHT: Edge Agent] <-- Local WiFi LAN --> [FCC]
```

## Operating Modes

| Mode | Internet | Edge Agent Role |
|------|----------|----------------|
| **Online** | Available | Edge Agent relays FCC transactions to cloud middleware via internet. Odoo POS operates normally. |
| **Offline** | Unavailable | Edge Agent continues to communicate with FCC over LAN. Buffers transactions locally. Odoo POS can fetch transactions from the local Edge Agent instead of the cloud middleware. |
| **Recovery** | Restored | Edge Agent replays buffered transactions to the cloud middleware. Odoo POS resumes normal cloud operation. |

## Key Capabilities (Preliminary)

- KC-15.1: Communicate with FCC over local WiFi LAN using the appropriate adapter protocol.
- KC-15.2: Support both Pull and Push modes from the FCC over LAN.
- KC-15.3: Buffer transactions locally (SQLite) when internet is unavailable.
- KC-15.4: Expose a local API that Odoo POS on the same HHT can call to fetch transactions in offline mode.
- KC-15.5: Automatically replay buffered transactions to the cloud middleware when internet is restored.
- KC-15.6: Deduplicate transactions during replay (coordinate with cloud middleware deduplication).
- KC-15.7: Allow attendants to trigger a manual Pull from the FCC via the Edge Agent.

## Open Questions (To Be Detailed)

- OQ-15.1: How does Odoo POS discover the local Edge Agent API? (localhost on same device?)
- OQ-15.2: What is the offline Odoo POS workflow — does it create orders locally and sync later, or does it just display transaction data from the Edge Agent?
- OQ-15.3: How are FCC adapter libraries packaged for Android? (Same .NET adapters via MAUI, or separate lightweight implementations?)
- OQ-15.4: How is the Edge Agent updated/managed on HHT devices at scale?
- OQ-15.5: What happens if multiple HHTs are at the same site — which one acts as the Edge Agent? Or do all of them?
- OQ-15.6: How does the Edge Agent authenticate with the FCC (credentials stored locally on HHT)?

## Business Rules

- BR-15.1: The Edge Agent must continue FCC communication even when internet is down.
- BR-15.2: Buffered transactions must survive Edge Agent restarts (persisted to SQLite).
- BR-15.3: Replay must be idempotent — the cloud middleware handles deduplication.
- BR-15.4: The Edge Agent must not require manual intervention to switch between online/offline/recovery modes.

## Acceptance Criteria

- AC-15.1: The Edge Agent successfully communicates with a DOMS FCC over local LAN.
- AC-15.2: Transactions are buffered when internet is unavailable and replayed when restored.
- AC-15.3: Odoo POS on the same HHT can fetch transactions from the Edge Agent in offline mode.
- AC-15.4: No duplicate transactions are created after replay and reconciliation.

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
- Pre-authorization order flow
- Unsolicited transaction ingestion (Pull and Push)
- Pre-auth reconciliation with volume adjustment
- Automatic Odoo order creation
- Payload normalization (DOMS)
- Duplicate detection
- Master data sync API
- Audit trail and event logging
- Error handling with retry and dead-letter queue
- Multi-tenancy with row-level isolation
- Edge Android Agent — basic LAN communication and offline buffering (partial)

## Out of Scope (Post-MVP)

- Radix, Advatec, Petronite adapters (Phase 3)
- Reconciliation dashboards (Phase 4)
- Admin portal (Angular) for configuration management
- Physical database isolation per legal entity
- Horizontal auto-scaling
- Edge Agent fleet management and OTA updates
- Advanced analytics and reporting
