-- =============================================================================
-- Forecourt Middleware — Edge Agent Room/SQLite Schema (Reference DDL)
-- Source: docs/specs/data-models/tier-1-4-database-schema-design.md
-- Purpose: Human-reviewed design reference. Actual schema via Room annotations.
-- Note: Room uses TEXT for dates (ISO 8601), INTEGER for booleans (0/1),
--       and INTEGER is 64-bit in SQLite (sufficient for bigint equivalents).
-- =============================================================================

-- PRAGMA journal_mode=WAL; -- Set in Room database builder callback

-- =============================================================================
-- BUFFERED TRANSACTIONS
-- =============================================================================

CREATE TABLE buffered_transactions (
    id                          TEXT        NOT NULL PRIMARY KEY,
    fcc_transaction_id          TEXT        NOT NULL,
    site_code                   TEXT        NOT NULL,
    pump_number                 INTEGER     NOT NULL,
    nozzle_number               INTEGER     NOT NULL,
    product_code                TEXT        NOT NULL,
    volume_microlitres          INTEGER     NOT NULL,
    amount_minor_units          INTEGER     NOT NULL,
    unit_price_minor_per_litre  INTEGER     NOT NULL,
    currency_code               TEXT        NOT NULL,
    started_at                  TEXT        NOT NULL,
    completed_at                TEXT        NOT NULL,
    fiscal_receipt_number       TEXT,
    fcc_vendor                  TEXT        NOT NULL,
    attendant_id                TEXT,
    status                      TEXT        NOT NULL DEFAULT 'PENDING',
    sync_status                 TEXT        NOT NULL DEFAULT 'PENDING',
    ingestion_source            TEXT        NOT NULL,
    raw_payload_json            TEXT,
    correlation_id              TEXT        NOT NULL,
    upload_attempts             INTEGER     NOT NULL DEFAULT 0,
    last_upload_attempt_at      TEXT,
    last_upload_error           TEXT,
    schema_version              INTEGER     NOT NULL DEFAULT 1,
    created_at                  TEXT        NOT NULL,
    updated_at                  TEXT        NOT NULL
);

CREATE UNIQUE INDEX ix_bt_dedup ON buffered_transactions (fcc_transaction_id, site_code);
CREATE INDEX ix_bt_sync_status ON buffered_transactions (sync_status, created_at);
CREATE INDEX ix_bt_local_api ON buffered_transactions (sync_status, pump_number, completed_at DESC);
CREATE INDEX ix_bt_cleanup ON buffered_transactions (sync_status, updated_at);

-- =============================================================================
-- NOZZLE MAPPING (EDGE)
-- Cached from cloud config push. Used by the pre-auth handler to translate
-- the Odoo pump/nozzle numbers received from Odoo POS into FCC pump/nozzle
-- numbers before sending the pre-auth command to the FCC over LAN.
-- =============================================================================

CREATE TABLE nozzles (
    id                  TEXT        NOT NULL PRIMARY KEY,
    site_code           TEXT        NOT NULL,
    odoo_pump_number    INTEGER     NOT NULL,  -- Pump number as Odoo knows it
    fcc_pump_number     INTEGER     NOT NULL,  -- Pump number to send to FCC
    odoo_nozzle_number  INTEGER     NOT NULL,  -- Nozzle number as Odoo knows it
    fcc_nozzle_number   INTEGER     NOT NULL,  -- Nozzle number to send to FCC
    product_code        TEXT        NOT NULL,  -- Product dispensed by this nozzle
    is_active           INTEGER     NOT NULL DEFAULT 1,
    synced_at           TEXT        NOT NULL,
    created_at          TEXT        NOT NULL,
    updated_at          TEXT        NOT NULL
);

-- Pre-auth translation lookup: given Odoo pump + nozzle → FCC pump + nozzle + product
CREATE UNIQUE INDEX ix_nozzles_odoo_lookup ON nozzles (site_code, odoo_pump_number, odoo_nozzle_number);
-- Reverse lookup: given FCC pump + nozzle → normalise incoming transaction product code
CREATE UNIQUE INDEX ix_nozzles_fcc_lookup ON nozzles (site_code, fcc_pump_number, fcc_nozzle_number);

-- =============================================================================
-- PRE-AUTH RECORDS (EDGE)
-- =============================================================================

CREATE TABLE pre_auth_records (
    id                              TEXT        NOT NULL PRIMARY KEY,
    site_code                       TEXT        NOT NULL,
    odoo_order_id                   TEXT        NOT NULL,
    pump_number                     INTEGER     NOT NULL,
    nozzle_number                   INTEGER     NOT NULL,
    product_code                    TEXT        NOT NULL,
    currency_code                   TEXT        NOT NULL,
    requested_amount_minor_units    INTEGER     NOT NULL,
    authorized_amount_minor_units   INTEGER,
    status                          TEXT        NOT NULL DEFAULT 'PENDING',
    fcc_correlation_id              TEXT,
    fcc_authorization_code          TEXT,
    failure_reason                  TEXT,
    customer_name                   TEXT,
    customer_tax_id                 TEXT,
    raw_fcc_response                TEXT,
    requested_at                    TEXT        NOT NULL,
    authorized_at                   TEXT,
    completed_at                    TEXT,
    expires_at                      TEXT        NOT NULL,
    is_cloud_synced                 INTEGER     NOT NULL DEFAULT 0,
    cloud_sync_attempts             INTEGER     NOT NULL DEFAULT 0,
    last_cloud_sync_attempt_at      TEXT,
    schema_version                  INTEGER     NOT NULL DEFAULT 1,
    created_at                      TEXT        NOT NULL
);

CREATE UNIQUE INDEX ix_par_idemp ON pre_auth_records (odoo_order_id, site_code);
CREATE INDEX ix_par_unsent ON pre_auth_records (is_cloud_synced, created_at);
CREATE INDEX ix_par_expiry ON pre_auth_records (status, expires_at);

-- =============================================================================
-- SYNC STATE (SINGLE ROW)
-- =============================================================================

CREATE TABLE sync_state (
    id                      INTEGER     NOT NULL PRIMARY KEY,
    last_fcc_cursor         TEXT,
    last_upload_at          TEXT,
    last_status_poll_at     TEXT,
    last_config_pull_at     TEXT,
    last_config_version     INTEGER,
    telemetry_sequence      INTEGER     NOT NULL DEFAULT 0,
    updated_at              TEXT        NOT NULL
);

-- =============================================================================
-- AGENT CONFIG (SINGLE ROW)
-- =============================================================================

CREATE TABLE agent_config (
    id                  INTEGER     NOT NULL PRIMARY KEY,
    config_json         TEXT        NOT NULL,
    config_version      INTEGER     NOT NULL,
    schema_version      INTEGER     NOT NULL,
    received_at         TEXT        NOT NULL
);

-- =============================================================================
-- AUDIT LOG (EDGE-LOCAL)
-- =============================================================================

CREATE TABLE audit_log (
    id                  INTEGER     NOT NULL PRIMARY KEY AUTOINCREMENT,
    event_type          TEXT        NOT NULL,
    message             TEXT        NOT NULL,
    correlation_id      TEXT,
    created_at          TEXT        NOT NULL
);

CREATE INDEX ix_al_time ON audit_log (created_at);
