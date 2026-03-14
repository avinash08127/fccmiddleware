-- 008-sites-tolerance-and-adapter-overrides.sql
-- Adds missing tolerance columns to sites/legal_entities and creates the
-- site_adapter_overrides table.

BEGIN;

-- 1a. Add reconciliation tolerance columns and default_fiscalization_mode to legal_entities.
ALTER TABLE legal_entities
    ADD COLUMN IF NOT EXISTS amount_tolerance_percent numeric(5,2),
    ADD COLUMN IF NOT EXISTS amount_tolerance_absolute bigint,
    ADD COLUMN IF NOT EXISTS time_window_minutes int,
    ADD COLUMN IF NOT EXISTS default_fiscalization_mode varchar(30) NOT NULL DEFAULT 'NONE';

-- Migrate from old boolean column to new enum column.
UPDATE legal_entities
   SET default_fiscalization_mode = 'FCC_DIRECT'
 WHERE fiscalization_required = true
   AND default_fiscalization_mode = 'NONE';

ALTER TABLE legal_entities
    DROP CONSTRAINT IF EXISTS chk_legal_entities_default_fiscalization_mode;
ALTER TABLE legal_entities
    ADD CONSTRAINT chk_legal_entities_default_fiscalization_mode
    CHECK (default_fiscalization_mode IN ('FCC_DIRECT','EXTERNAL_INTEGRATION','NONE'));

-- 1b. Add reconciliation tolerance columns to sites.
ALTER TABLE sites
    ADD COLUMN IF NOT EXISTS amount_tolerance_percent numeric(5,2),
    ADD COLUMN IF NOT EXISTS amount_tolerance_absolute bigint,
    ADD COLUMN IF NOT EXISTS time_window_minutes int;

-- 2. Create site_adapter_overrides table for per-site adapter config overrides.
CREATE TABLE IF NOT EXISTS site_adapter_overrides (
    id                  uuid            NOT NULL DEFAULT gen_random_uuid(),
    site_id             uuid            NOT NULL,
    legal_entity_id     uuid            NOT NULL,
    adapter_key         varchar(50)     NOT NULL,
    fcc_vendor          varchar(30)     NOT NULL,
    override_json       jsonb           NOT NULL DEFAULT '{}',
    config_version      int             NOT NULL DEFAULT 1,
    updated_by          varchar(200),
    created_at          timestamptz     NOT NULL DEFAULT now(),
    updated_at          timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_site_adapter_overrides PRIMARY KEY (id),
    CONSTRAINT fk_site_adapter_overrides_site
        FOREIGN KEY (site_id) REFERENCES sites (id),
    CONSTRAINT fk_site_adapter_overrides_legal_entity
        FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_site_adapter_overrides_site_adapter
    ON site_adapter_overrides (site_id, adapter_key);

COMMIT;
