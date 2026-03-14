-- 007-adapter-default-configs.sql
-- Creates the adapter_default_configs table required by the portal adapters page.

BEGIN;

CREATE TABLE IF NOT EXISTS adapter_default_configs (
    id                  uuid            NOT NULL DEFAULT gen_random_uuid(),
    legal_entity_id     uuid            NOT NULL,
    adapter_key         varchar(50)     NOT NULL,
    fcc_vendor          varchar(30)     NOT NULL,
    config_json         jsonb           NOT NULL DEFAULT '{}',
    config_version      int             NOT NULL DEFAULT 1,
    updated_by          varchar(200),
    created_at          timestamptz     NOT NULL DEFAULT now(),
    updated_at          timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_adapter_default_configs PRIMARY KEY (id),
    CONSTRAINT fk_adapter_default_configs_legal_entity
        FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_adapter_default_configs_legal_entity_adapter
    ON adapter_default_configs (legal_entity_id, adapter_key);

COMMIT;
