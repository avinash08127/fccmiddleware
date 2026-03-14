-- 006-reconciliation-schema.sql
-- Adds missing columns and tables required by the reconciliation feature
-- and admin dashboard.

BEGIN;

-- 1. Add fcc_correlation_id to transactions table.
--    Partitioned tables require ALTER on the parent; the column propagates
--    to all existing partitions automatically.
ALTER TABLE transactions
    ADD COLUMN IF NOT EXISTS fcc_correlation_id varchar(200);

-- 2. Create reconciliation_records table.
CREATE TABLE IF NOT EXISTS reconciliation_records (
    id                              uuid            NOT NULL DEFAULT gen_random_uuid(),
    legal_entity_id                 uuid            NOT NULL,
    site_code                       varchar(50)     NOT NULL,
    transaction_id                  uuid            NOT NULL,
    pre_auth_id                     uuid,
    odoo_order_id                   varchar(200),
    pump_number                     int             NOT NULL,
    nozzle_number                   int             NOT NULL,
    authorized_amount_minor_units   bigint,
    actual_amount_minor_units       bigint          NOT NULL,
    variance_minor_units            bigint,
    absolute_variance_minor_units   bigint,
    variance_percent                numeric(9,4),
    within_tolerance                boolean,
    match_method                    varchar(40)     NOT NULL,
    status                          varchar(30)     NOT NULL DEFAULT 'UNMATCHED',
    ambiguity_flag                  boolean         NOT NULL DEFAULT false,
    ambiguity_reason                varchar(200),
    last_match_attempt_at           timestamptz     NOT NULL,
    matched_at                      timestamptz,
    reviewed_by_user_id             varchar(200),
    reviewed_at_utc                 timestamptz,
    review_reason                   varchar(1000),
    escalated_at_utc                timestamptz,
    schema_version                  int             NOT NULL DEFAULT 1,
    created_at                      timestamptz     NOT NULL DEFAULT now(),
    updated_at                      timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_reconciliation_records PRIMARY KEY (id),
    CONSTRAINT chk_reconciliation_status CHECK (
        status IN ('UNMATCHED','MATCHED','VARIANCE_WITHIN_TOLERANCE','VARIANCE_FLAGGED','APPROVED','REJECTED','REVIEW_FUZZY_MATCH')
    )
);

-- Indexes matching EF Core configuration
CREATE UNIQUE INDEX IF NOT EXISTS ix_reconciliation_transaction
    ON reconciliation_records (transaction_id);

CREATE INDEX IF NOT EXISTS ix_reconciliation_tenant_status
    ON reconciliation_records (legal_entity_id, status, created_at);

CREATE INDEX IF NOT EXISTS ix_reconciliation_retry
    ON reconciliation_records (site_code, status, last_match_attempt_at)
    WHERE status = 'UNMATCHED';

COMMIT;
