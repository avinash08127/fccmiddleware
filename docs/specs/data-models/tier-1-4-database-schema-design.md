# Tier 1.4 — Database Schema Design

## 1. Output Location

- **Target file:** `docs/specs/data-models/tier-1-4-database-schema-design.md`
- **Companion files:**
  - `db/ddl/001-cloud-schema.sql` — Cloud PostgreSQL DDL
  - `db/ddl/002-edge-room-entities.sql` — Edge Agent Room/SQLite DDL reference
  - `db/reference/seed-data-strategy.md` — Seed data definitions
- **Why:** `docs/STRUCTURE.md` maps "Database schema design" to `/docs/specs/data-models` with supporting SQL in `/db/ddl`.

## 2. Scope

- **TODO item:** 1.4 Database Schema Design (Cloud + Edge)
- **In scope:** All cloud table definitions, column types, constraints, indexes, multi-tenancy enforcement, partitioning, soft-delete policy, migration tooling, seed data; all edge Room entities, DAOs, indexes, WAL config, retention, migration, integrity recovery
- **Out of scope:** EF Core entity configuration code, Room Kotlin annotations, actual migration files, reconciliation rule engine logic, API implementation

## 3. Source Traceability

- **Requirements:** REQ-1 (legal entities), REQ-2 (sites), REQ-3 (FCC), REQ-6–REQ-8 (pre-auth, transactions, reconciliation), REQ-9 (Odoo sync), REQ-11 (master data), REQ-13 (dedup), REQ-14 (audit), REQ-15 (edge agent), REQ-17 (multi-tenancy), NFR-4 (7-year retention)
- **HLD sections:** Cloud Backend §4.2 (component model), §7.7 (multi-tenancy), §9.1 decision #8 (row-level isolation), §9.3 (database growth risk), §10 (Aurora PostgreSQL + EF Core); Edge Agent §4.4 (buffer), §5.3 (sync flow), §8.6 (WAL resilience), §9.1 (Room + WAL)
- **Prerequisite artefacts:** Tier 1.1 canonical transaction spec, pre-auth record spec, device registration spec, telemetry payload spec, site config schema, pump status model; Tier 1.2 state machines

## 4. Key Decisions

| # | Decision | Why | Impact |
|---|----------|-----|--------|
| D1 | **Multi-tenancy: EF Core global query filters** (application-level), no PostgreSQL RLS for MVP | 12 tenants is small. EF global filters are simpler to develop, test, and debug. RLS adds operational complexity with marginal security gain when all access is through the application layer. | Every table with `legal_entity_id` gets a mandatory global filter. Integration tests must verify filter enforcement. RLS can be layered later as defense-in-depth. |
| D2 | **Partitioning: `transactions` and `audit_events` by `created_at` (monthly range)** | Time-based partitioning aligns with retention policy, archival to S3, and query patterns (Odoo polls recent data, portal searches by date range). `legal_entity_id` partitioning adds complexity for 12 tenants with uneven volume. | Partition management via `pg_partman`. Archive worker detaches old partitions. Other tables remain unpartitioned. |
| D3 | **Soft delete for master data tables; hard delete for transactional data via archival** | Master data (sites, pumps, products, legal entities, operators) must never disappear — historical transactions reference them. Transactional data uses status-based lifecycle (PENDING → ARCHIVED) with eventual partition detach. | Master data tables get `is_active` + `deactivated_at`. Transaction/pre-auth tables use `status` column. `audit_events` are append-only, never deleted from active store. |
| D4 | **Migration tooling: EF Core Migrations** | Matches the .NET tech stack decision (Cloud HLD §10). Strongly typed, version-controlled, supports global query filters and complex type configurations. Team uses one tool for schema and application model. | All schema changes go through EF Core migration files. Reviewed DDL companion files exist for human review and architecture reference only. |
| D5 | **Edge: Room auto-migration with fallback to manual** | Room auto-migration handles simple additive changes (new columns, new tables). Manual migration for destructive changes (column type changes, renames). | APK updates with schema changes must declare migration strategy in release notes. Destructive migrations require explicit `Migration` class. |
| D6 | **Edge: WAL mode enabled at database creation** | Required for crash resilience on Android. Prevents corruption from force-kills and power loss. | `PRAGMA journal_mode=WAL` set in Room database builder callback. |

---

## 5. Detailed Specification

### 5.1 Cloud PostgreSQL Schema

All tables live in the `public` schema. `legal_entity_id` is present on every tenant-scoped table and enforced by EF Core global query filters.

#### 5.1.1 Master Data Tables

**`legal_entities`**

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `uuid` | NO | `gen_random_uuid()` | PK | |
| `country_code` | `varchar(3)` | NO | | UNIQUE | ISO 3166-1 alpha-2/3 |
| `name` | `varchar(200)` | NO | | | |
| `currency_code` | `varchar(3)` | NO | | | ISO 4217 |
| `tax_authority_code` | `varchar(50)` | NO | | | |
| `fiscalization_required` | `boolean` | NO | `false` | | |
| `fiscalization_provider` | `varchar(50)` | YES | | | FCC_DIRECT, EXTERNAL_INTEGRATION, null |
| `default_timezone` | `varchar(50)` | NO | | | IANA timezone |
| `is_active` | `boolean` | NO | `true` | | Soft-delete flag |
| `deactivated_at` | `timestamptz` | YES | | | |
| `synced_at` | `timestamptz` | NO | | | Last Databricks sync |
| `created_at` | `timestamptz` | NO | `now()` | | |
| `updated_at` | `timestamptz` | NO | `now()` | | |

**`sites`**

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `uuid` | NO | `gen_random_uuid()` | PK | |
| `legal_entity_id` | `uuid` | NO | | FK → `legal_entities.id` | Tenant scope |
| `site_code` | `varchar(50)` | NO | | UNIQUE | Global uniqueness per BR-2.3 |
| `site_name` | `varchar(200)` | NO | | | |
| `operating_model` | `varchar(20)` | NO | | CHECK IN ('COCO','CODO','DODO','DOCO') | |
| `connectivity_mode` | `varchar(20)` | NO | `'CONNECTED'` | | |
| `operator_name` | `varchar(200)` | YES | | | Required when CODO/DODO |
| `operator_tax_payer_id` | `varchar(100)` | YES | | | Required when CODO/DODO |
| `company_tax_payer_id` | `varchar(100)` | NO | | | |
| `odoo_site_id` | `varchar(100)` | YES | | | Odoo foreign reference |
| `is_active` | `boolean` | NO | `true` | | |
| `deactivated_at` | `timestamptz` | YES | | | |
| `synced_at` | `timestamptz` | NO | | | |
| `created_at` | `timestamptz` | NO | `now()` | | |
| `updated_at` | `timestamptz` | NO | `now()` | | |

**`pumps`**

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `uuid` | NO | `gen_random_uuid()` | PK | |
| `site_id` | `uuid` | NO | | FK → `sites.id` | |
| `legal_entity_id` | `uuid` | NO | | FK → `legal_entities.id` | Denormalized for query filters |
| `pump_number` | `int` | NO | | UNIQUE with `site_id` | Odoo pump number — what Odoo POS sends on pre-auth |
| `fcc_pump_number` | `int` | NO | | UNIQUE with `site_id` | FCC pump number — forwarded to the Forecourt Controller |
| `is_active` | `boolean` | NO | `true` | | |
| `synced_at` | `timestamptz` | NO | | | |
| `created_at` | `timestamptz` | NO | `now()` | | |
| `updated_at` | `timestamptz` | NO | `now()` | | |

Unique constraints: `(site_id, pump_number)` and `(site_id, fcc_pump_number)`.

> **Why two pump numbers?** Odoo numbers pumps independently of the FCC vendor. At most sites they match (1:1), but a site may have a replaced or re-numbered FCC where the numbering diverged. The mapping is set during site provisioning and synced from Odoo via Databricks.

**`nozzles`**

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `uuid` | NO | `gen_random_uuid()` | PK | |
| `pump_id` | `uuid` | NO | | FK → `pumps.id` | |
| `site_id` | `uuid` | NO | | FK → `sites.id` | Denormalized for query filters |
| `legal_entity_id` | `uuid` | NO | | FK → `legal_entities.id` | Denormalized for query filters |
| `odoo_nozzle_number` | `int` | NO | | UNIQUE with `pump_id` | Nozzle number as Odoo POS knows it |
| `fcc_nozzle_number` | `int` | NO | | UNIQUE with `pump_id` | Nozzle number sent to the FCC |
| `product_id` | `uuid` | NO | | FK → `products.id` | Product (fuel grade) dispensed by this nozzle |
| `is_active` | `boolean` | NO | `true` | | |
| `synced_at` | `timestamptz` | NO | | | |
| `created_at` | `timestamptz` | NO | `now()` | | |
| `updated_at` | `timestamptz` | NO | `now()` | | |

Unique constraints: `(pump_id, odoo_nozzle_number)` and `(pump_id, fcc_nozzle_number)`.

> **Pre-auth mapping flow**: Odoo POS sends `odoo_pump_number` + `odoo_nozzle_number` to the Edge Agent. The Edge Agent resolves `pumps WHERE pump_number = odoo_pump_number` → gets `fcc_pump_number`. Then resolves `nozzles WHERE pump_id = ? AND odoo_nozzle_number = ?` → gets `fcc_nozzle_number` and `product_code`. These FCC values are forwarded in the pre-auth command to the FCC.

**`products`**

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `uuid` | NO | `gen_random_uuid()` | PK | |
| `legal_entity_id` | `uuid` | NO | | FK → `legal_entities.id` | |
| `product_code` | `varchar(50)` | NO | | UNIQUE with `legal_entity_id` | |
| `product_name` | `varchar(200)` | NO | | | |
| `unit_of_measure` | `varchar(20)` | NO | `'LITRE'` | | |
| `is_active` | `boolean` | NO | `true` | | |
| `synced_at` | `timestamptz` | NO | | | |
| `created_at` | `timestamptz` | NO | `now()` | | |
| `updated_at` | `timestamptz` | NO | `now()` | | |

Unique constraint: `(legal_entity_id, product_code)`.

**`operators`**

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `uuid` | NO | `gen_random_uuid()` | PK | |
| `legal_entity_id` | `uuid` | NO | | FK → `legal_entities.id` | |
| `operator_code` | `varchar(50)` | NO | | UNIQUE with `legal_entity_id` | |
| `operator_name` | `varchar(200)` | NO | | | |
| `tax_payer_id` | `varchar(100)` | YES | | | |
| `is_active` | `boolean` | NO | `true` | | |
| `synced_at` | `timestamptz` | NO | | | |
| `created_at` | `timestamptz` | NO | `now()` | | |
| `updated_at` | `timestamptz` | NO | `now()` | | |

#### 5.1.2 Transactional Tables

**`transactions`** — Partitioned by `created_at` (monthly range)

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `uuid` | NO | `gen_random_uuid()` | PK (composite with `created_at` for partitioning) | |
| `legal_entity_id` | `uuid` | NO | | FK → `legal_entities.id` | Tenant scope |
| `fcc_transaction_id` | `varchar(200)` | NO | | | Opaque FCC identifier |
| `site_code` | `varchar(50)` | NO | | | |
| `pump_number` | `int` | NO | | | |
| `nozzle_number` | `int` | NO | | | |
| `product_code` | `varchar(50)` | NO | | | |
| `volume_microlitres` | `bigint` | NO | | CHECK > 0 | |
| `amount_minor_units` | `bigint` | NO | | CHECK > 0 | |
| `unit_price_minor_per_litre` | `bigint` | NO | | CHECK > 0 | |
| `currency_code` | `varchar(3)` | NO | | | |
| `started_at` | `timestamptz` | NO | | | |
| `completed_at` | `timestamptz` | NO | | CHECK >= `started_at` | |
| `fiscal_receipt_number` | `varchar(200)` | YES | | | |
| `fcc_vendor` | `varchar(30)` | NO | | | FccVendor enum |
| `attendant_id` | `varchar(100)` | YES | | | |
| `status` | `varchar(30)` | NO | `'PENDING'` | CHECK IN ('PENDING','SYNCED_TO_ODOO','DUPLICATE','ARCHIVED') | |
| `ingestion_source` | `varchar(30)` | NO | | | FCC_PUSH, EDGE_UPLOAD, CLOUD_PULL |
| `raw_payload_ref` | `varchar(500)` | YES | | | S3 key |
| `odoo_order_id` | `varchar(200)` | YES | | | Stamped on acknowledge |
| `synced_to_odoo_at` | `timestamptz` | YES | | | |
| `pre_auth_id` | `uuid` | YES | | | Linked pre-auth |
| `reconciliation_status` | `varchar(30)` | YES | | | |
| `is_duplicate` | `boolean` | NO | `false` | | |
| `duplicate_of_id` | `uuid` | YES | | | |
| `is_stale` | `boolean` | NO | `false` | | Stale detection flag |
| `correlation_id` | `uuid` | NO | | | Trace propagation |
| `schema_version` | `int` | NO | `1` | | |
| `created_at` | `timestamptz` | NO | `now()` | | Partition key |
| `updated_at` | `timestamptz` | NO | `now()` | | |

Unique constraint: `(fcc_transaction_id, site_code)` — the dedup key.

**`pre_auth_records`**

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `uuid` | NO | `gen_random_uuid()` | PK | |
| `legal_entity_id` | `uuid` | NO | | FK → `legal_entities.id` | |
| `site_code` | `varchar(50)` | NO | | | |
| `odoo_order_id` | `varchar(200)` | NO | | | Idempotency key with `site_code` |
| `pump_number` | `int` | NO | | | |
| `nozzle_number` | `int` | NO | | | |
| `product_code` | `varchar(50)` | NO | | | |
| `currency_code` | `varchar(3)` | NO | | | |
| `requested_amount_minor_units` | `bigint` | NO | | CHECK > 0 | |
| `authorized_amount_minor_units` | `bigint` | YES | | | |
| `actual_amount_minor_units` | `bigint` | YES | | | |
| `actual_volume_millilitres` | `bigint` | YES | | | |
| `status` | `varchar(30)` | NO | `'PENDING'` | CHECK IN ('PENDING','AUTHORIZED','DISPENSING','COMPLETED','CANCELLED','EXPIRED','FAILED') | |
| `fcc_correlation_id` | `varchar(200)` | YES | | | FCC-issued match key |
| `fcc_authorization_code` | `varchar(200)` | YES | | | |
| `failure_reason` | `varchar(500)` | YES | | | |
| `customer_name` | `varchar(200)` | YES | | | |
| `customer_tax_id` | `varchar(100)` | YES | | | Sensitive — never log |
| `requested_at` | `timestamptz` | NO | | | |
| `authorized_at` | `timestamptz` | YES | | | |
| `completed_at` | `timestamptz` | YES | | | |
| `cancelled_at` | `timestamptz` | YES | | | |
| `failed_at` | `timestamptz` | YES | | | |
| `expires_at` | `timestamptz` | NO | | | |
| `matched_transaction_id` | `uuid` | YES | | | Linked dispense |
| `schema_version` | `int` | NO | `1` | | |
| `created_at` | `timestamptz` | NO | `now()` | | |
| `updated_at` | `timestamptz` | NO | `now()` | | |

Unique constraint: `(odoo_order_id, site_code)` — the idempotency key.

#### 5.1.3 Configuration & Registration Tables

**`fcc_configs`**

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `uuid` | NO | `gen_random_uuid()` | PK | |
| `site_id` | `uuid` | NO | | FK → `sites.id` | |
| `legal_entity_id` | `uuid` | NO | | FK → `legal_entities.id` | |
| `fcc_vendor` | `varchar(30)` | NO | | | |
| `fcc_model` | `varchar(100)` | YES | | | |
| `connection_protocol` | `varchar(20)` | NO | | | REST, TCP, SOAP |
| `host_address` | `varchar(200)` | NO | | | |
| `port` | `int` | NO | | | |
| `credential_ref` | `varchar(200)` | NO | | | Reference to Secrets Manager |
| `transaction_mode` | `varchar(20)` | NO | `'PUSH'` | CHECK IN ('PUSH','PULL','HYBRID') | |
| `ingestion_mode` | `varchar(20)` | NO | `'CLOUD_DIRECT'` | CHECK IN ('CLOUD_DIRECT','RELAY','BUFFER_ALWAYS') | |
| `pull_interval_seconds` | `int` | YES | | | |
| `heartbeat_interval_seconds` | `int` | NO | `60` | | |
| `is_active` | `boolean` | NO | `true` | | |
| `config_version` | `int` | NO | `1` | | Monotonic |
| `created_at` | `timestamptz` | NO | `now()` | | |
| `updated_at` | `timestamptz` | NO | `now()` | | |

**`agent_registrations`**

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `uuid` | NO | `gen_random_uuid()` | PK | Stable `deviceId` |
| `site_id` | `uuid` | NO | | FK → `sites.id` | |
| `legal_entity_id` | `uuid` | NO | | FK → `legal_entities.id` | |
| `site_code` | `varchar(50)` | NO | | | Denormalized for API |
| `device_serial_number` | `varchar(200)` | NO | | | |
| `device_model` | `varchar(100)` | NO | | | |
| `os_version` | `varchar(50)` | NO | | | |
| `agent_version` | `varchar(50)` | NO | | | |
| `is_active` | `boolean` | NO | `true` | | |
| `token_hash` | `varchar(500)` | NO | | | Hashed device token |
| `token_expires_at` | `timestamptz` | NO | | | |
| `last_seen_at` | `timestamptz` | YES | | | Updated on telemetry |
| `registered_at` | `timestamptz` | NO | | | |
| `deactivated_at` | `timestamptz` | YES | | | |
| `created_at` | `timestamptz` | NO | `now()` | | |
| `updated_at` | `timestamptz` | NO | `now()` | | |

#### 5.1.4 Event & Audit Table

**`audit_events`** — Partitioned by `created_at` (monthly range). Append-only.

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `uuid` | NO | `gen_random_uuid()` | PK (composite with `created_at`) | |
| `legal_entity_id` | `uuid` | NO | | | Tenant scope |
| `event_type` | `varchar(100)` | NO | | | e.g., TransactionIngested |
| `correlation_id` | `uuid` | NO | | | |
| `site_code` | `varchar(50)` | YES | | | |
| `source` | `varchar(100)` | NO | | | System or actor |
| `payload` | `jsonb` | NO | | | Event-specific data |
| `created_at` | `timestamptz` | NO | `now()` | | Partition key |

No `updated_at` — events are immutable.

#### 5.1.5 Outbox Table

**`outbox_messages`**

| Column | Type | Nullable | Default | Constraints | Notes |
|--------|------|----------|---------|-------------|-------|
| `id` | `bigint` | NO | `GENERATED ALWAYS AS IDENTITY` | PK | Sequential for ordered processing |
| `event_type` | `varchar(100)` | NO | | | |
| `payload` | `jsonb` | NO | | | |
| `correlation_id` | `uuid` | NO | | | |
| `created_at` | `timestamptz` | NO | `now()` | | |
| `processed_at` | `timestamptz` | YES | | | Null until published |

---

### 5.2 Cloud Index Strategy

| Table | Index | Columns | Purpose |
|-------|-------|---------|---------|
| `transactions` | `ix_transactions_dedup` | `(fcc_transaction_id, site_code)` UNIQUE | Dedup lookup (primary path) |
| `transactions` | `ix_transactions_odoo_poll` | `(legal_entity_id, status, created_at)` WHERE `status = 'PENDING'` | Odoo poll: fetch PENDING by legal entity, ordered by time |
| `transactions` | `ix_transactions_portal_search` | `(legal_entity_id, site_code, created_at DESC)` | Portal transaction browser |
| `transactions` | `ix_transactions_reconciliation` | `(site_code, pump_number, completed_at)` WHERE `pre_auth_id IS NULL AND status = 'PENDING'` | Reconciliation: find unmatched dispenses by pump + time |
| `transactions` | `ix_transactions_stale` | `(status, is_stale, created_at)` WHERE `status = 'PENDING' AND is_stale = false` | Stale detection worker |
| `pre_auth_records` | `ix_preauth_idemp` | `(odoo_order_id, site_code)` UNIQUE | Idempotency check |
| `pre_auth_records` | `ix_preauth_correlation` | `(fcc_correlation_id)` WHERE `fcc_correlation_id IS NOT NULL` | Reconciliation primary match |
| `pre_auth_records` | `ix_preauth_expiry` | `(status, expires_at)` WHERE `status IN ('PENDING','AUTHORIZED','DISPENSING')` | Expiry worker scan |
| `pre_auth_records` | `ix_preauth_tenant_status` | `(legal_entity_id, status, requested_at DESC)` | Portal pre-auth browser |
| `audit_events` | `ix_audit_correlation` | `(correlation_id)` | Trace lookup |
| `audit_events` | `ix_audit_type_time` | `(legal_entity_id, event_type, created_at DESC)` | Portal audit viewer |
| `sites` | `ix_sites_legal_entity` | `(legal_entity_id)` | Tenant-scoped site listing |
| `nozzles` | `ix_nozzles_pump` | `(pump_id, is_active)` | Active nozzles for a pump |
| `nozzles` | `ix_nozzles_site_lookup` | `(site_id, is_active)` | Pre-auth mapping lookup by site |
| `agent_registrations` | `ix_agent_site` | `(site_id, is_active)` | Active agent lookup per site |
| `outbox_messages` | `ix_outbox_unprocessed` | `(id)` WHERE `processed_at IS NULL` | Outbox publisher scan |

All partial indexes use `WHERE` clauses to keep index size small on high-volume tables.

---

### 5.3 Partitioning Strategy

| Table | Method | Key | Granularity | Management |
|-------|--------|-----|-------------|------------|
| `transactions` | Range | `created_at` | Monthly | `pg_partman` auto-creates partitions 3 months ahead. Archive worker detaches partitions older than the active retention window (e.g., 24 months) and exports to S3 Parquet. |
| `audit_events` | Range | `created_at` | Monthly | Same as above. 7-year regulatory retention met via S3 archive. |
| All other tables | None | — | — | Too small to benefit from partitioning at 12 tenants / 2,000 sites. |

PK for partitioned tables must include the partition key: `(id, created_at)`.

---

### 5.4 Soft Delete / Lifecycle Policy

| Table | Policy | Mechanism |
|-------|--------|-----------|
| `legal_entities` | Soft delete | `is_active = false`, `deactivated_at` set |
| `sites` | Soft delete | `is_active = false`, `deactivated_at` set |
| `pumps` | Soft delete | `is_active = false` |
| `nozzles` | Soft delete | `is_active = false` |
| `products` | Soft delete | `is_active = false` |
| `operators` | Soft delete | `is_active = false` |
| `fcc_configs` | Soft delete | `is_active = false` |
| `agent_registrations` | Soft delete | `is_active = false`, `deactivated_at` set |
| `transactions` | Status lifecycle | `status` column: PENDING → SYNCED_TO_ODOO → ARCHIVED. Partition detach for old months. |
| `pre_auth_records` | Status lifecycle | Terminal states (COMPLETED, CANCELLED, EXPIRED, FAILED). Archival by retention policy. |
| `audit_events` | Append-only | Never deleted from active partitions. Old partitions detached to S3. |
| `outbox_messages` | Hard delete | Processed messages purged by cleanup worker (retain 7 days for debugging). |

---

### 5.5 Edge Agent — Room/SQLite Schema

#### 5.5.1 Room Entities

**`buffered_transactions`**

| Column | Type | Nullable | Default | Notes |
|--------|------|----------|---------|-------|
| `id` | `TEXT` (UUID) | NO | | PK. Middleware-generated UUID. |
| `fcc_transaction_id` | `TEXT` | NO | | |
| `site_code` | `TEXT` | NO | | |
| `pump_number` | `INTEGER` | NO | | |
| `nozzle_number` | `INTEGER` | NO | | |
| `product_code` | `TEXT` | NO | | |
| `volume_microlitres` | `INTEGER` | NO | | SQLite INTEGER = 64-bit |
| `amount_minor_units` | `INTEGER` | NO | | |
| `unit_price_minor_per_litre` | `INTEGER` | NO | | |
| `currency_code` | `TEXT` | NO | | |
| `started_at` | `TEXT` | NO | | ISO 8601 UTC |
| `completed_at` | `TEXT` | NO | | |
| `fiscal_receipt_number` | `TEXT` | YES | | |
| `fcc_vendor` | `TEXT` | NO | | |
| `attendant_id` | `TEXT` | YES | | |
| `status` | `TEXT` | NO | `'PENDING'` | TransactionStatus enum |
| `sync_status` | `TEXT` | NO | `'PENDING'` | SyncStatus: PENDING, UPLOADED, SYNCED, SYNCED_TO_ODOO, FAILED |
| `ingestion_source` | `TEXT` | NO | | |
| `raw_payload_json` | `TEXT` | YES | | Inline raw FCC payload |
| `correlation_id` | `TEXT` | NO | | |
| `upload_attempts` | `INTEGER` | NO | `0` | |
| `last_upload_attempt_at` | `TEXT` | YES | | |
| `last_upload_error` | `TEXT` | YES | | |
| `schema_version` | `INTEGER` | NO | `1` | |
| `created_at` | `TEXT` | NO | | |
| `updated_at` | `TEXT` | NO | | |

**`nozzles`** (edge)

| Column | Type | Nullable | Default | Notes |
|--------|------|----------|---------|-------|
| `id` | `TEXT` (UUID) | NO | | PK |
| `site_code` | `TEXT` | NO | | Single site per agent |
| `odoo_pump_number` | `INTEGER` | NO | | Pump number Odoo POS sends |
| `fcc_pump_number` | `INTEGER` | NO | | Pump number forwarded to FCC |
| `odoo_nozzle_number` | `INTEGER` | NO | | Nozzle number Odoo POS sends |
| `fcc_nozzle_number` | `INTEGER` | NO | | Nozzle number forwarded to FCC |
| `product_code` | `TEXT` | NO | | Product dispensed (for pre-auth payload) |
| `is_active` | `INTEGER` | NO | `1` | Boolean: 0/1 |
| `synced_at` | `TEXT` | NO | | |
| `created_at` | `TEXT` | NO | | |
| `updated_at` | `TEXT` | NO | | |

Indexes: `ix_nozzles_odoo_lookup` `(site_code, odoo_pump_number, odoo_nozzle_number)` UNIQUE — the primary pre-auth lookup path. `ix_nozzles_fcc_lookup` `(site_code, fcc_pump_number, fcc_nozzle_number)` UNIQUE — reverse lookup for incoming FCC transactions.

This table is populated (and refreshed) from the cloud config push. The `CleanupWorker` replaces the full set on each config update.

**`pre_auth_records`** (edge)

| Column | Type | Nullable | Default | Notes |
|--------|------|----------|---------|-------|
| `id` | `TEXT` (UUID) | NO | | PK |
| `site_code` | `TEXT` | NO | | |
| `odoo_order_id` | `TEXT` | NO | | |
| `pump_number` | `INTEGER` | NO | | |
| `nozzle_number` | `INTEGER` | NO | | |
| `product_code` | `TEXT` | NO | | |
| `currency_code` | `TEXT` | NO | | |
| `requested_amount_minor_units` | `INTEGER` | NO | | |
| `authorized_amount_minor_units` | `INTEGER` | YES | | |
| `status` | `TEXT` | NO | `'PENDING'` | PreAuthStatus enum |
| `fcc_correlation_id` | `TEXT` | YES | | |
| `fcc_authorization_code` | `TEXT` | YES | | |
| `failure_reason` | `TEXT` | YES | | |
| `customer_name` | `TEXT` | YES | | |
| `customer_tax_id` | `TEXT` | YES | | Sensitive |
| `raw_fcc_response` | `TEXT` | YES | | |
| `requested_at` | `TEXT` | NO | | |
| `authorized_at` | `TEXT` | YES | | |
| `completed_at` | `TEXT` | YES | | |
| `expires_at` | `TEXT` | NO | | |
| `is_cloud_synced` | `INTEGER` | NO | `0` | Boolean: 0/1 |
| `cloud_sync_attempts` | `INTEGER` | NO | `0` | |
| `last_cloud_sync_attempt_at` | `TEXT` | YES | | |
| `schema_version` | `INTEGER` | NO | `1` | |
| `created_at` | `TEXT` | NO | | |

**`sync_state`**

| Column | Type | Nullable | Default | Notes |
|--------|------|----------|---------|-------|
| `id` | `INTEGER` | NO | | PK. Single row (id=1) for site-level sync state. |
| `last_fcc_cursor` | `TEXT` | YES | | Last successfully fetched FCC cursor/offset |
| `last_upload_at` | `TEXT` | YES | | |
| `last_status_poll_at` | `TEXT` | YES | | |
| `last_config_pull_at` | `TEXT` | YES | | |
| `last_config_version` | `INTEGER` | YES | | |
| `telemetry_sequence` | `INTEGER` | NO | `0` | Monotonic counter |
| `updated_at` | `TEXT` | NO | | |

**`agent_config`**

| Column | Type | Nullable | Default | Notes |
|--------|------|----------|---------|-------|
| `id` | `INTEGER` | NO | | PK. Single row (id=1). |
| `config_json` | `TEXT` | NO | | Full SiteConfig JSON snapshot |
| `config_version` | `INTEGER` | NO | | |
| `schema_version` | `INTEGER` | NO | | |
| `received_at` | `TEXT` | NO | | |

**`audit_log`** (edge)

| Column | Type | Nullable | Default | Notes |
|--------|------|----------|---------|-------|
| `id` | `INTEGER` | NO | | PK, autoincrement |
| `event_type` | `TEXT` | NO | | |
| `message` | `TEXT` | NO | | |
| `correlation_id` | `TEXT` | YES | | |
| `created_at` | `TEXT` | NO | | |

#### 5.5.2 Edge Index Strategy

| Table | Index | Columns | Purpose |
|-------|-------|---------|---------|
| `buffered_transactions` | `ix_bt_dedup` | `(fcc_transaction_id, site_code)` UNIQUE | Local dedup |
| `buffered_transactions` | `ix_bt_sync_status` | `(sync_status, created_at)` | Upload worker: find PENDING records ordered by time |
| `buffered_transactions` | `ix_bt_local_api` | `(sync_status, pump_number, completed_at DESC)` | Local API: transaction queries by pump, excluding SYNCED_TO_ODOO |
| `buffered_transactions` | `ix_bt_cleanup` | `(sync_status, updated_at)` | Retention cleanup |
| `nozzles` | `ix_nozzles_odoo_lookup` | `(site_code, odoo_pump_number, odoo_nozzle_number)` UNIQUE | Pre-auth translation: Odoo → FCC numbers |
| `nozzles` | `ix_nozzles_fcc_lookup` | `(site_code, fcc_pump_number, fcc_nozzle_number)` UNIQUE | Reverse lookup: FCC → product code for incoming transactions |
| `pre_auth_records` | `ix_par_idemp` | `(odoo_order_id, site_code)` UNIQUE | Idempotency |
| `pre_auth_records` | `ix_par_unsent` | `(is_cloud_synced, created_at)` | Cloud forward worker |
| `pre_auth_records` | `ix_par_expiry` | `(status, expires_at)` | Expiry check |
| `audit_log` | `ix_al_time` | `(created_at)` | Diagnostics screen time filter |

#### 5.5.3 DAO Interface Definitions

**TransactionBufferDao**

| Method | Query | Notes |
|--------|-------|-------|
| `insert(tx)` | `INSERT INTO buffered_transactions ...` | `OnConflictStrategy.IGNORE` on dedup key |
| `getPendingForUpload(limit)` | `SELECT * FROM buffered_transactions WHERE sync_status = 'PENDING' ORDER BY created_at ASC LIMIT :limit` | Upload worker |
| `getForLocalApi(excludeSynced, pumpNumber?, limit, offset)` | `SELECT * FROM buffered_transactions WHERE sync_status NOT IN ('SYNCED_TO_ODOO') [AND pump_number = :pumpNumber] ORDER BY completed_at DESC LIMIT :limit OFFSET :offset` | Odoo POS query |
| `getById(id)` | `SELECT * FROM buffered_transactions WHERE id = :id` | |
| `updateSyncStatus(id, status, attempts, lastAttemptAt, error?)` | `UPDATE buffered_transactions SET sync_status = :status, upload_attempts = :attempts, last_upload_attempt_at = :lastAttemptAt, last_upload_error = :error, updated_at = :now WHERE id = :id` | |
| `markSyncedToOdoo(fccTransactionIds)` | `UPDATE buffered_transactions SET sync_status = 'SYNCED_TO_ODOO', updated_at = :now WHERE fcc_transaction_id IN (:ids)` | Status poll response |
| `deleteOldSynced(cutoffDate)` | `DELETE FROM buffered_transactions WHERE sync_status = 'SYNCED_TO_ODOO' AND updated_at < :cutoffDate` | Retention cleanup |
| `countByStatus()` | `SELECT sync_status, COUNT(*) FROM buffered_transactions GROUP BY sync_status` | Telemetry / diagnostics |

**NozzleDao**

| Method | Query | Notes |
|--------|-------|-------|
| `resolveForPreAuth(siteCode, odooPumpNumber, odooNozzleNumber)` | `SELECT * FROM nozzles WHERE site_code = :siteCode AND odoo_pump_number = :odooPumpNumber AND odoo_nozzle_number = :odooNozzleNumber AND is_active = 1` | Called on every pre-auth to translate Odoo → FCC numbers |
| `resolveByFcc(siteCode, fccPumpNumber, fccNozzleNumber)` | `SELECT * FROM nozzles WHERE site_code = :siteCode AND fcc_pump_number = :fccPumpNumber AND fcc_nozzle_number = :fccNozzleNumber AND is_active = 1` | Reverse lookup when normalising incoming FCC transactions |
| `replaceAll(nozzles)` | `DELETE FROM nozzles WHERE site_code = :siteCode` + bulk insert | Called on config push; replaces the full nozzle set for the site |
| `getAll(siteCode)` | `SELECT * FROM nozzles WHERE site_code = :siteCode AND is_active = 1` | Diagnostics / health screen |

**PreAuthDao**

| Method | Query | Notes |
|--------|-------|-------|
| `insert(record)` | `INSERT INTO pre_auth_records ...` | `OnConflictStrategy.IGNORE` on idemp key |
| `getByOdooOrderId(odooOrderId, siteCode)` | `SELECT * FROM pre_auth_records WHERE odoo_order_id = :odooOrderId AND site_code = :siteCode` | |
| `getUnsynced(limit)` | `SELECT * FROM pre_auth_records WHERE is_cloud_synced = 0 ORDER BY created_at ASC LIMIT :limit` | Cloud forward worker |
| `updateStatus(id, status, ...)` | `UPDATE pre_auth_records SET status = :status, ... WHERE id = :id` | |
| `markCloudSynced(id)` | `UPDATE pre_auth_records SET is_cloud_synced = 1, cloud_sync_attempts = cloud_sync_attempts + 1 WHERE id = :id` | |
| `getExpiring(now)` | `SELECT * FROM pre_auth_records WHERE status IN ('PENDING','AUTHORIZED','DISPENSING') AND expires_at <= :now` | Expiry worker |

**SyncStateDao**

| Method | Query |
|--------|-------|
| `get()` | `SELECT * FROM sync_state WHERE id = 1` |
| `upsert(state)` | `INSERT OR REPLACE INTO sync_state ...` |

#### 5.5.4 WAL Mode Configuration

Set in Room database builder callback at database creation:

```
database.openHelper.writableDatabase.execSQL("PRAGMA journal_mode=WAL")
```

Room's `SupportSQLiteOpenHelper` applies this before any DAO call. WAL mode persists across connections for the database file.

#### 5.5.5 Retention and Cleanup

Executed by a periodic `CleanupWorker` (interval from `buffer.cleanupIntervalHours` in SiteConfig):

```sql
-- Delete SYNCED_TO_ODOO transactions older than retention period
DELETE FROM buffered_transactions
WHERE sync_status = 'SYNCED_TO_ODOO'
  AND updated_at < datetime('now', '-' || :retentionDays || ' days');

-- Delete terminal pre-auth records older than retention period
DELETE FROM pre_auth_records
WHERE status IN ('COMPLETED', 'CANCELLED', 'EXPIRED', 'FAILED')
  AND created_at < datetime('now', '-' || :retentionDays || ' days');

-- Trim audit log older than retention period
DELETE FROM audit_log
WHERE created_at < datetime('now', '-' || :retentionDays || ' days');
```

`retentionDays` is sourced from `SiteConfig.buffer.retentionDays` (default 7).

#### 5.5.6 Schema Migration Strategy

- **Additive changes** (new nullable columns, new tables): Use Room `@AutoMigration` annotation. Room generates migration code at compile time.
- **Destructive changes** (column type change, column rename, table restructure): Write explicit `Migration(fromVersion, toVersion)` class. Test with `MigrationTestHelper`.
- **Emergency fallback:** If migration fails, back up the database file to `<db_name>.corrupt.<timestamp>`, delete the original, and recreate. Upload backed-up file to cloud for forensic retrieval. This is a data-loss path — used only when migration code has a bug.

#### 5.5.7 Integrity Check and Corruption Recovery

On every app startup, before any DAO call:

1. Run `PRAGMA integrity_check`.
2. If result is `ok`, proceed normally.
3. If result indicates corruption:
   a. Copy the database file to `<db_name>.corrupt.<timestamp>`.
   b. Delete the corrupted database.
   c. Room recreates an empty database on next access.
   d. Log the corruption event locally and flag for cloud telemetry upload (once connectivity is available).
   e. The agent re-syncs by polling FCC from cursor `null` (full catch-up).

---

## 6. Validation and Edge Cases

- `fcc_transaction_id + site_code` uniqueness is enforced at both cloud (unique index) and edge (unique index). Cloud dedup also uses Redis cache as a fast-path check before hitting the database.
- `odoo_order_id + site_code` uniqueness on `pre_auth_records` prevents duplicate pre-auth creation from Odoo retries.
- Partitioned table PKs are composite `(id, created_at)` — application code must always include `created_at` in lookups or rely on index-only scans.
- `customer_tax_id` column exists in both cloud and edge `pre_auth_records`. Logging infrastructure must mask this field. EF Core value converter should not serialize it to logs.
- On edge, SQLite `INTEGER` is 64-bit, sufficient for `bigint` equivalents (`volume_microlitres`, `amount_minor_units`).
- Edge `sync_state` and `agent_config` tables are single-row. DAOs use `INSERT OR REPLACE` with `id = 1`.

## 7. Cross-Component Impact

| Component | Impact |
|-----------|--------|
| **Cloud Backend** | EF Core `DbContext` with entity configurations for all cloud tables. Global query filter on `legal_entity_id`. `pg_partman` setup in deployment scripts. |
| **Edge Agent** | Room `@Database` with 6 entities, 5 DAOs. WAL callback. Auto-migration annotations. `CleanupWorker` and `IntegrityChecker` classes. |
| **Angular Portal** | Reads from cloud tables via API — no direct DB access. Affected by index strategy (portal search index supports filtered queries). |
| **CI/CD** | Cloud: EF Core migration step in deployment pipeline. Edge: Room schema export verification in build. |

## 8. Dependencies

- **Prerequisites:** Tier 1.1 (all data models and enums), Tier 1.2 (state machines — status values used in CHECK constraints)
- **Downstream TODOs affected:**
  - 1.3 API Contracts — query parameters align with index strategy
  - 1.5 FCC Adapter Interfaces — adapter output maps to transaction columns
  - 2.1 Error Handling — quarantine behavior may add columns or tables
  - 2.2 Deduplication — dedup key and index confirmed here
  - 2.3 Reconciliation — reconciliation index supports matching queries
  - 2.6 Event Schema — `audit_events.payload` is `jsonb`, event type list defined elsewhere
  - 3.1 Project Scaffolding — EF Core and Room setup uses this schema
- **Recommended next step:** Create the companion DDL SQL files, then proceed to Tier 1.5 (FCC Adapter Interface Contracts)

## 9. Open Questions

None. All decisions are closable from existing context.

## 10. Acceptance Checklist

- [ ] All 11 cloud tables defined with columns, types, nullability, defaults, and constraints
- [ ] All 6 edge Room entities defined with columns and types
- [ ] Dedup key (`fcc_transaction_id + site_code`) enforced at both cloud and edge
- [ ] Pre-auth idempotency key (`odoo_order_id + site_code`) enforced at both cloud and edge
- [ ] Cloud index strategy covers Odoo poll, portal search, reconciliation matching, stale detection, and outbox publishing
- [ ] Edge index strategy covers upload worker, local API queries, cleanup, and expiry checks
- [ ] Multi-tenancy enforcement mechanism documented (EF Core global query filters)
- [ ] Partitioning strategy documented for `transactions` and `audit_events`
- [ ] Soft-delete vs hard-delete policy stated for every table
- [ ] Migration tooling decision documented (EF Core Migrations for cloud, Room auto-migration for edge)
- [ ] Edge DAO interfaces defined with exact queries
- [ ] WAL mode configuration documented
- [ ] Retention/cleanup SQL documented
- [ ] Integrity check and corruption recovery procedure documented
- [ ] Seed data strategy referenced

## 11. Output Files to Create

- `docs/specs/data-models/tier-1-4-database-schema-design.md` (this file)
- `db/ddl/001-cloud-schema.sql` — Reference DDL for cloud PostgreSQL
- `db/ddl/002-edge-room-schema.sql` — Reference DDL for edge SQLite/Room
- `db/reference/seed-data-strategy.md` — Seed data definitions

## 12. Recommended Next TODO

1.5 FCC Adapter Interface Contracts — depends on knowing the canonical transaction columns and dedup key confirmed here.
