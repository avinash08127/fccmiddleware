-- =============================================================================
-- Forecourt Middleware — Full Cloud PostgreSQL Schema (Local Dev)
-- Combines: 001-cloud-schema.sql + vendor-specific FCC columns + partitions
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- =============================================================================
-- MASTER DATA TABLES
-- =============================================================================

CREATE TABLE IF NOT EXISTS legal_entities (
    id                      uuid            NOT NULL DEFAULT gen_random_uuid(),
    business_code           varchar(50)     NOT NULL,
    country_code            varchar(3)      NOT NULL,
    country_name            varchar(100)    NOT NULL,
    name                    varchar(200)    NOT NULL,
    currency_code           varchar(3)      NOT NULL,
    tax_authority_code      varchar(50)     NOT NULL,
    fiscalization_required  boolean         NOT NULL DEFAULT false,
    fiscalization_provider  varchar(50),
    default_timezone        varchar(50)     NOT NULL,
    odoo_company_id         varchar(100)    NOT NULL,
    is_active               boolean         NOT NULL DEFAULT true,
    deactivated_at          timestamptz,
    synced_at               timestamptz     NOT NULL DEFAULT now(),
    created_at              timestamptz     NOT NULL DEFAULT now(),
    updated_at              timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_legal_entities PRIMARY KEY (id),
    CONSTRAINT uq_legal_entities_business_code UNIQUE (business_code),
    CONSTRAINT uq_legal_entities_country_code UNIQUE (country_code)
);

CREATE TABLE IF NOT EXISTS sites (
    id                      uuid            NOT NULL DEFAULT gen_random_uuid(),
    legal_entity_id         uuid            NOT NULL,
    site_code               varchar(50)     NOT NULL,
    site_name               varchar(200)    NOT NULL,
    operating_model         varchar(20)     NOT NULL,
    site_uses_pre_auth      boolean         NOT NULL DEFAULT false,
    connectivity_mode       varchar(20)     NOT NULL DEFAULT 'CONNECTED',
    operator_name           varchar(200),
    operator_tax_payer_id   varchar(100),
    company_tax_payer_id    varchar(100)    NOT NULL,
    fiscalization_mode      varchar(30)     NOT NULL DEFAULT 'NONE',
    tax_authority_endpoint  varchar(500),
    require_customer_tax_id boolean         NOT NULL DEFAULT false,
    fiscal_receipt_required boolean         NOT NULL DEFAULT false,
    odoo_site_id            varchar(100),
    is_active               boolean         NOT NULL DEFAULT true,
    deactivated_at          timestamptz,
    synced_at               timestamptz     NOT NULL DEFAULT now(),
    created_at              timestamptz     NOT NULL DEFAULT now(),
    updated_at              timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_sites PRIMARY KEY (id),
    CONSTRAINT uq_sites_site_code UNIQUE (site_code),
    CONSTRAINT fk_sites_legal_entity FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id),
    CONSTRAINT chk_sites_operating_model CHECK (operating_model IN ('COCO','CODO','DODO','DOCO')),
    CONSTRAINT chk_sites_fiscalization_mode CHECK (fiscalization_mode IN ('FCC_DIRECT','EXTERNAL_INTEGRATION','NONE'))
);

CREATE INDEX IF NOT EXISTS ix_sites_legal_entity ON sites (legal_entity_id);

CREATE TABLE IF NOT EXISTS pumps (
    id                  uuid            NOT NULL DEFAULT gen_random_uuid(),
    site_id             uuid            NOT NULL,
    legal_entity_id     uuid            NOT NULL,
    pump_number         int             NOT NULL,
    fcc_pump_number     int             NOT NULL,
    is_active           boolean         NOT NULL DEFAULT true,
    synced_at           timestamptz     NOT NULL DEFAULT now(),
    created_at          timestamptz     NOT NULL DEFAULT now(),
    updated_at          timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_pumps PRIMARY KEY (id),
    CONSTRAINT uq_pumps_site_odoo_pump UNIQUE (site_id, pump_number),
    CONSTRAINT uq_pumps_site_fcc_pump UNIQUE (site_id, fcc_pump_number),
    CONSTRAINT fk_pumps_site FOREIGN KEY (site_id) REFERENCES sites (id),
    CONSTRAINT fk_pumps_legal_entity FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id)
);

CREATE TABLE IF NOT EXISTS products (
    id                  uuid            NOT NULL DEFAULT gen_random_uuid(),
    legal_entity_id     uuid            NOT NULL,
    product_code        varchar(50)     NOT NULL,
    product_name        varchar(200)    NOT NULL,
    unit_of_measure     varchar(20)     NOT NULL DEFAULT 'LITRE',
    is_active           boolean         NOT NULL DEFAULT true,
    synced_at           timestamptz     NOT NULL DEFAULT now(),
    created_at          timestamptz     NOT NULL DEFAULT now(),
    updated_at          timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_products PRIMARY KEY (id),
    CONSTRAINT uq_products_entity_code UNIQUE (legal_entity_id, product_code),
    CONSTRAINT fk_products_legal_entity FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id)
);

CREATE TABLE IF NOT EXISTS nozzles (
    id                  uuid            NOT NULL DEFAULT gen_random_uuid(),
    pump_id             uuid            NOT NULL,
    site_id             uuid            NOT NULL,
    legal_entity_id     uuid            NOT NULL,
    odoo_nozzle_number  int             NOT NULL,
    fcc_nozzle_number   int             NOT NULL,
    product_id          uuid            NOT NULL,
    is_active           boolean         NOT NULL DEFAULT true,
    synced_at           timestamptz     NOT NULL DEFAULT now(),
    created_at          timestamptz     NOT NULL DEFAULT now(),
    updated_at          timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_nozzles PRIMARY KEY (id),
    CONSTRAINT uq_nozzles_pump_odoo UNIQUE (pump_id, odoo_nozzle_number),
    CONSTRAINT uq_nozzles_pump_fcc UNIQUE (pump_id, fcc_nozzle_number),
    CONSTRAINT fk_nozzles_pump FOREIGN KEY (pump_id) REFERENCES pumps (id),
    CONSTRAINT fk_nozzles_site FOREIGN KEY (site_id) REFERENCES sites (id),
    CONSTRAINT fk_nozzles_legal_entity FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id),
    CONSTRAINT fk_nozzles_product FOREIGN KEY (product_id) REFERENCES products (id)
);

CREATE INDEX IF NOT EXISTS ix_nozzles_pump ON nozzles (pump_id, is_active);
CREATE INDEX IF NOT EXISTS ix_nozzles_site_lookup ON nozzles (site_id, is_active);

CREATE TABLE IF NOT EXISTS operators (
    id                  uuid            NOT NULL DEFAULT gen_random_uuid(),
    legal_entity_id     uuid            NOT NULL,
    operator_code       varchar(50)     NOT NULL,
    operator_name       varchar(200)    NOT NULL,
    tax_payer_id        varchar(100),
    is_active           boolean         NOT NULL DEFAULT true,
    synced_at           timestamptz     NOT NULL DEFAULT now(),
    created_at          timestamptz     NOT NULL DEFAULT now(),
    updated_at          timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_operators PRIMARY KEY (id),
    CONSTRAINT uq_operators_entity_code UNIQUE (legal_entity_id, operator_code),
    CONSTRAINT fk_operators_legal_entity FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id)
);

-- =============================================================================
-- TRANSACTIONAL TABLES (PARTITIONED)
-- =============================================================================

CREATE TABLE IF NOT EXISTS transactions (
    id                          uuid            NOT NULL DEFAULT gen_random_uuid(),
    legal_entity_id             uuid            NOT NULL,
    fcc_transaction_id          varchar(200)    NOT NULL,
    site_code                   varchar(50)     NOT NULL,
    pump_number                 int             NOT NULL,
    nozzle_number               int             NOT NULL,
    product_code                varchar(50)     NOT NULL,
    volume_microlitres          bigint          NOT NULL,
    amount_minor_units          bigint          NOT NULL,
    unit_price_minor_per_litre  bigint          NOT NULL,
    currency_code               varchar(3)      NOT NULL,
    started_at                  timestamptz     NOT NULL,
    completed_at                timestamptz     NOT NULL,
    fiscal_receipt_number       varchar(200),
    fcc_vendor                  varchar(30)     NOT NULL,
    attendant_id                varchar(100),
    status                      varchar(30)     NOT NULL DEFAULT 'PENDING',
    ingestion_source            varchar(30)     NOT NULL,
    raw_payload_ref             varchar(500),
    odoo_order_id               varchar(200),
    synced_to_odoo_at           timestamptz,
    pre_auth_id                 uuid,
    reconciliation_status       varchar(30),
    is_duplicate                boolean         NOT NULL DEFAULT false,
    duplicate_of_id             uuid,
    is_stale                    boolean         NOT NULL DEFAULT false,
    correlation_id              uuid            NOT NULL,
    schema_version              int             NOT NULL DEFAULT 1,
    created_at                  timestamptz     NOT NULL DEFAULT now(),
    updated_at                  timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_transactions PRIMARY KEY (id, created_at),
    CONSTRAINT chk_transactions_volume CHECK (volume_microlitres > 0),
    CONSTRAINT chk_transactions_amount CHECK (amount_minor_units > 0),
    CONSTRAINT chk_transactions_price CHECK (unit_price_minor_per_litre > 0),
    CONSTRAINT chk_transactions_times CHECK (completed_at >= started_at),
    CONSTRAINT chk_transactions_status CHECK (status IN ('PENDING','SYNCED_TO_ODOO','DUPLICATE','ARCHIVED'))
) PARTITION BY RANGE (created_at);

-- Create partitions for 2026 (local dev)
CREATE TABLE IF NOT EXISTS transactions_2026_01 PARTITION OF transactions FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');
CREATE TABLE IF NOT EXISTS transactions_2026_02 PARTITION OF transactions FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');
CREATE TABLE IF NOT EXISTS transactions_2026_03 PARTITION OF transactions FOR VALUES FROM ('2026-03-01') TO ('2026-04-01');
CREATE TABLE IF NOT EXISTS transactions_2026_04 PARTITION OF transactions FOR VALUES FROM ('2026-04-01') TO ('2026-05-01');
CREATE TABLE IF NOT EXISTS transactions_2026_05 PARTITION OF transactions FOR VALUES FROM ('2026-05-01') TO ('2026-06-01');
CREATE TABLE IF NOT EXISTS transactions_2026_06 PARTITION OF transactions FOR VALUES FROM ('2026-06-01') TO ('2026-07-01');
CREATE TABLE IF NOT EXISTS transactions_2026_07 PARTITION OF transactions FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');
CREATE TABLE IF NOT EXISTS transactions_2026_08 PARTITION OF transactions FOR VALUES FROM ('2026-08-01') TO ('2026-09-01');
CREATE TABLE IF NOT EXISTS transactions_2026_09 PARTITION OF transactions FOR VALUES FROM ('2026-09-01') TO ('2026-10-01');
CREATE TABLE IF NOT EXISTS transactions_2026_10 PARTITION OF transactions FOR VALUES FROM ('2026-10-01') TO ('2026-11-01');
CREATE TABLE IF NOT EXISTS transactions_2026_11 PARTITION OF transactions FOR VALUES FROM ('2026-11-01') TO ('2026-12-01');
CREATE TABLE IF NOT EXISTS transactions_2026_12 PARTITION OF transactions FOR VALUES FROM ('2026-12-01') TO ('2027-01-01');

-- Note: unique index on partitioned table must include the partition key (created_at)
CREATE UNIQUE INDEX IF NOT EXISTS ix_transactions_dedup ON transactions (fcc_transaction_id, site_code, created_at);
CREATE INDEX IF NOT EXISTS ix_transactions_odoo_poll ON transactions (legal_entity_id, status, created_at) WHERE status = 'PENDING';
CREATE INDEX IF NOT EXISTS ix_transactions_portal_search ON transactions (legal_entity_id, site_code, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_transactions_reconciliation ON transactions (site_code, pump_number, completed_at) WHERE pre_auth_id IS NULL AND status = 'PENDING';
CREATE INDEX IF NOT EXISTS ix_transactions_stale ON transactions (status, is_stale, created_at) WHERE status = 'PENDING' AND is_stale = false;

-- =============================================================================
-- PRE-AUTH RECORDS
-- =============================================================================

CREATE TABLE IF NOT EXISTS pre_auth_records (
    id                              uuid            NOT NULL DEFAULT gen_random_uuid(),
    legal_entity_id                 uuid            NOT NULL,
    site_code                       varchar(50)     NOT NULL,
    odoo_order_id                   varchar(200)    NOT NULL,
    pump_number                     int             NOT NULL,
    nozzle_number                   int             NOT NULL,
    product_code                    varchar(50)     NOT NULL,
    currency_code                   varchar(3)      NOT NULL,
    requested_amount_minor_units    bigint          NOT NULL,
    authorized_amount_minor_units   bigint,
    actual_amount_minor_units       bigint,
    actual_volume_millilitres       bigint,
    status                          varchar(30)     NOT NULL DEFAULT 'PENDING',
    fcc_correlation_id              varchar(200),
    fcc_authorization_code          varchar(200),
    failure_reason                  varchar(500),
    customer_name                   varchar(200),
    customer_tax_id                 varchar(100),
    requested_at                    timestamptz     NOT NULL,
    authorized_at                   timestamptz,
    completed_at                    timestamptz,
    cancelled_at                    timestamptz,
    failed_at                       timestamptz,
    expires_at                      timestamptz     NOT NULL,
    matched_transaction_id          uuid,
    schema_version                  int             NOT NULL DEFAULT 1,
    created_at                      timestamptz     NOT NULL DEFAULT now(),
    updated_at                      timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_pre_auth_records PRIMARY KEY (id),
    CONSTRAINT fk_preauth_legal_entity FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id),
    CONSTRAINT chk_preauth_amount CHECK (requested_amount_minor_units > 0),
    CONSTRAINT chk_preauth_status CHECK (status IN ('PENDING','AUTHORIZED','DISPENSING','COMPLETED','CANCELLED','EXPIRED','FAILED'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_preauth_idemp ON pre_auth_records (odoo_order_id, site_code);
CREATE INDEX IF NOT EXISTS ix_preauth_correlation ON pre_auth_records (fcc_correlation_id) WHERE fcc_correlation_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_preauth_expiry ON pre_auth_records (status, expires_at) WHERE status IN ('PENDING','AUTHORIZED','DISPENSING');
CREATE INDEX IF NOT EXISTS ix_preauth_tenant_status ON pre_auth_records (legal_entity_id, status, requested_at DESC);

-- =============================================================================
-- CONFIGURATION & REGISTRATION
-- =============================================================================

CREATE TABLE IF NOT EXISTS fcc_configs (
    id                              uuid            NOT NULL DEFAULT gen_random_uuid(),
    site_id                         uuid            NOT NULL,
    legal_entity_id                 uuid            NOT NULL,
    fcc_vendor                      varchar(30)     NOT NULL,
    fcc_model                       varchar(100),
    connection_protocol             varchar(20)     NOT NULL,
    host_address                    varchar(200)    NOT NULL,
    port                            int             NOT NULL,
    credential_ref                  varchar(200)    NOT NULL,
    transaction_mode                varchar(20)     NOT NULL DEFAULT 'PUSH',
    ingestion_mode                  varchar(20)     NOT NULL DEFAULT 'CLOUD_DIRECT',
    pull_interval_seconds           int,
    catchup_pull_interval_seconds   int,
    hybrid_catchup_interval_seconds int,
    heartbeat_interval_seconds      int             NOT NULL DEFAULT 60,
    heartbeat_timeout_seconds       int             NOT NULL DEFAULT 180,
    is_active                       boolean         NOT NULL DEFAULT true,
    config_version                  int             NOT NULL DEFAULT 1,
    created_at                      timestamptz     NOT NULL DEFAULT now(),
    updated_at                      timestamptz     NOT NULL DEFAULT now(),

    -- Radix-specific fields
    shared_secret                   varchar(500),
    usn_code                        int,
    auth_port                       int,
    fcc_pump_address_map            text,

    -- DOMS TCP/JPL fields
    jpl_port                        int,
    fc_access_code                  varchar(500),
    doms_country_code               varchar(10),
    pos_version_id                  varchar(50),
    reconnect_backoff_max_seconds   int,
    configured_pumps                varchar(200),
    dpp_ports                       varchar(200),

    -- Petronite OAuth2 fields
    client_id                       varchar(500),
    client_secret                   varchar(500),
    webhook_secret                  varchar(500),
    webhook_secret_hash             varchar(64),
    oauth_token_endpoint            varchar(500),

    -- Advatec EFD fields
    advatec_device_port             int,
    advatec_webhook_token           varchar(500),
    advatec_webhook_token_hash      varchar(64),
    advatec_efd_serial_number       varchar(100),
    advatec_cust_id_type            int,
    advatec_pump_map                text,

    CONSTRAINT pk_fcc_configs PRIMARY KEY (id),
    CONSTRAINT fk_fcc_configs_site FOREIGN KEY (site_id) REFERENCES sites (id),
    CONSTRAINT fk_fcc_configs_legal_entity FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id),
    CONSTRAINT chk_fcc_transaction_mode CHECK (transaction_mode IN ('PUSH','PULL','HYBRID')),
    CONSTRAINT chk_fcc_ingestion_mode CHECK (ingestion_mode IN ('CLOUD_DIRECT','RELAY','BUFFER_ALWAYS'))
);

CREATE INDEX IF NOT EXISTS ix_fcc_configs_advatec_webhook_token_hash
    ON fcc_configs (advatec_webhook_token_hash) WHERE advatec_webhook_token_hash IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_fcc_configs_webhook_secret_hash
    ON fcc_configs (webhook_secret_hash) WHERE webhook_secret_hash IS NOT NULL;

CREATE TABLE IF NOT EXISTS agent_registrations (
    id                      uuid            NOT NULL DEFAULT gen_random_uuid(),
    site_id                 uuid            NOT NULL,
    legal_entity_id         uuid            NOT NULL,
    site_code               varchar(50)     NOT NULL,
    device_serial_number    varchar(200)    NOT NULL,
    device_model            varchar(100)    NOT NULL,
    os_version              varchar(50)     NOT NULL,
    agent_version           varchar(50)     NOT NULL,
    is_active               boolean         NOT NULL DEFAULT true,
    token_hash              varchar(500)    NOT NULL,
    token_expires_at        timestamptz     NOT NULL,
    last_seen_at            timestamptz,
    registered_at           timestamptz     NOT NULL,
    deactivated_at          timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    updated_at              timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_agent_registrations PRIMARY KEY (id),
    CONSTRAINT fk_agent_reg_site FOREIGN KEY (site_id) REFERENCES sites (id),
    CONSTRAINT fk_agent_reg_legal_entity FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id)
);

CREATE INDEX IF NOT EXISTS ix_agent_site ON agent_registrations (site_id, is_active);

CREATE TABLE IF NOT EXISTS agent_telemetry_snapshots (
    device_id                        uuid            NOT NULL,
    legal_entity_id                  uuid            NOT NULL,
    site_code                        varchar(50)     NOT NULL,
    reported_at_utc                  timestamptz     NOT NULL,
    connectivity_state               varchar(30)     NOT NULL,
    payload_json                     jsonb           NOT NULL,
    battery_percent                  int             NOT NULL,
    is_charging                      boolean         NOT NULL,
    pending_upload_count             int             NOT NULL,
    sync_lag_seconds                 int,
    last_heartbeat_at_utc            timestamptz,
    heartbeat_age_seconds            int,
    fcc_vendor                       varchar(30)     NOT NULL,
    fcc_host                         varchar(200)    NOT NULL,
    fcc_port                         int             NOT NULL,
    consecutive_heartbeat_failures   int             NOT NULL,
    created_at                       timestamptz     NOT NULL DEFAULT now(),
    updated_at                       timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_agent_telemetry_snapshots PRIMARY KEY (device_id)
);

CREATE INDEX IF NOT EXISTS ix_agent_telemetry_legal_entity_site
    ON agent_telemetry_snapshots (legal_entity_id, site_code);

CREATE TABLE IF NOT EXISTS portal_settings (
    id                          uuid            NOT NULL,
    global_defaults_json        jsonb           NOT NULL,
    alert_configuration_json    jsonb           NOT NULL,
    created_at                  timestamptz     NOT NULL DEFAULT now(),
    updated_at                  timestamptz     NOT NULL DEFAULT now(),
    updated_by                  varchar(200),
    CONSTRAINT pk_portal_settings PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS legal_entity_settings_overrides (
    id                                  uuid            NOT NULL DEFAULT gen_random_uuid(),
    legal_entity_id                     uuid            NOT NULL,
    amount_tolerance_percent            numeric(5,2),
    amount_tolerance_absolute_minor_units bigint,
    time_window_minutes                 int,
    stale_pending_threshold_days        int,
    created_at                          timestamptz     NOT NULL DEFAULT now(),
    updated_at                          timestamptz     NOT NULL DEFAULT now(),
    updated_by                          varchar(200),
    CONSTRAINT pk_legal_entity_settings_overrides PRIMARY KEY (id),
    CONSTRAINT fk_legal_entity_settings_overrides_legal_entity
        FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id),
    CONSTRAINT uq_legal_entity_settings_overrides_legal_entity_id UNIQUE (legal_entity_id)
);

CREATE TABLE IF NOT EXISTS dead_letter_items (
    id                  uuid            NOT NULL DEFAULT gen_random_uuid(),
    legal_entity_id     uuid            NOT NULL,
    site_code           varchar(50)     NOT NULL,
    type                varchar(20)     NOT NULL,
    fcc_transaction_id  varchar(200),
    raw_payload_ref     varchar(500),
    raw_payload_json    jsonb,
    failure_reason      varchar(40)     NOT NULL,
    error_code          varchar(100)    NOT NULL,
    error_message       varchar(4000)   NOT NULL,
    status              varchar(20)     NOT NULL,
    retry_count         int             NOT NULL DEFAULT 0,
    last_retry_at       timestamptz,
    retry_history_json  jsonb,
    discard_reason      varchar(2000),
    discarded_by        varchar(200),
    discarded_at        timestamptz,
    created_at          timestamptz     NOT NULL DEFAULT now(),
    updated_at          timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_dead_letter_items PRIMARY KEY (id),
    CONSTRAINT chk_dead_letter_type CHECK (type IN ('TRANSACTION','PRE_AUTH','TELEMETRY','UNKNOWN')),
    CONSTRAINT chk_dead_letter_reason CHECK (failure_reason IN ('VALIDATION_FAILURE','DEDUPLICATION_ERROR','ADAPTER_ERROR','PERSISTENCE_ERROR','UNKNOWN')),
    CONSTRAINT chk_dead_letter_status CHECK (status IN ('PENDING','RETRYING','RESOLVED','DISCARDED'))
);

CREATE INDEX IF NOT EXISTS ix_dead_letter_items_legal_entity_status_created_at
    ON dead_letter_items (legal_entity_id, status, created_at);

-- =============================================================================
-- AUDIT EVENTS (PARTITIONED)
-- =============================================================================

CREATE TABLE IF NOT EXISTS audit_events (
    id                  uuid            NOT NULL DEFAULT gen_random_uuid(),
    legal_entity_id     uuid            NOT NULL,
    event_type          varchar(100)    NOT NULL,
    correlation_id      uuid            NOT NULL,
    site_code           varchar(50),
    source              varchar(100)    NOT NULL,
    payload             jsonb           NOT NULL,
    created_at          timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_audit_events PRIMARY KEY (id, created_at)
) PARTITION BY RANGE (created_at);

-- Create partitions for 2026 (local dev)
CREATE TABLE IF NOT EXISTS audit_events_2026_01 PARTITION OF audit_events FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');
CREATE TABLE IF NOT EXISTS audit_events_2026_02 PARTITION OF audit_events FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');
CREATE TABLE IF NOT EXISTS audit_events_2026_03 PARTITION OF audit_events FOR VALUES FROM ('2026-03-01') TO ('2026-04-01');
CREATE TABLE IF NOT EXISTS audit_events_2026_04 PARTITION OF audit_events FOR VALUES FROM ('2026-04-01') TO ('2026-05-01');
CREATE TABLE IF NOT EXISTS audit_events_2026_05 PARTITION OF audit_events FOR VALUES FROM ('2026-05-01') TO ('2026-06-01');
CREATE TABLE IF NOT EXISTS audit_events_2026_06 PARTITION OF audit_events FOR VALUES FROM ('2026-06-01') TO ('2026-07-01');
CREATE TABLE IF NOT EXISTS audit_events_2026_07 PARTITION OF audit_events FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');
CREATE TABLE IF NOT EXISTS audit_events_2026_08 PARTITION OF audit_events FOR VALUES FROM ('2026-08-01') TO ('2026-09-01');
CREATE TABLE IF NOT EXISTS audit_events_2026_09 PARTITION OF audit_events FOR VALUES FROM ('2026-09-01') TO ('2026-10-01');
CREATE TABLE IF NOT EXISTS audit_events_2026_10 PARTITION OF audit_events FOR VALUES FROM ('2026-10-01') TO ('2026-11-01');
CREATE TABLE IF NOT EXISTS audit_events_2026_11 PARTITION OF audit_events FOR VALUES FROM ('2026-11-01') TO ('2026-12-01');
CREATE TABLE IF NOT EXISTS audit_events_2026_12 PARTITION OF audit_events FOR VALUES FROM ('2026-12-01') TO ('2027-01-01');

CREATE INDEX IF NOT EXISTS ix_audit_correlation ON audit_events (correlation_id);
CREATE INDEX IF NOT EXISTS ix_audit_type_time ON audit_events (legal_entity_id, event_type, created_at DESC);

-- =============================================================================
-- TRANSACTIONAL OUTBOX
-- =============================================================================

CREATE TABLE IF NOT EXISTS outbox_messages (
    id              bigint          NOT NULL GENERATED ALWAYS AS IDENTITY,
    event_type      varchar(100)    NOT NULL,
    payload         jsonb           NOT NULL,
    correlation_id  uuid            NOT NULL,
    created_at      timestamptz     NOT NULL DEFAULT now(),
    processed_at    timestamptz,
    CONSTRAINT pk_outbox_messages PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS ix_outbox_unprocessed ON outbox_messages (id) WHERE processed_at IS NULL;
