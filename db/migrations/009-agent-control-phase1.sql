-- 009-agent-control-phase1.sql
-- Adds bootstrap token actor/revoke metadata plus the Phase 1 agent command/installations tables.

BEGIN;

ALTER TABLE bootstrap_tokens
    ADD COLUMN IF NOT EXISTS created_by_actor_id varchar(200),
    ADD COLUMN IF NOT EXISTS created_by_actor_display varchar(200),
    ADD COLUMN IF NOT EXISTS revoked_at timestamptz,
    ADD COLUMN IF NOT EXISTS revoked_by_actor_id varchar(200),
    ADD COLUMN IF NOT EXISTS revoked_by_actor_display varchar(200);

UPDATE bootstrap_tokens
SET created_by_actor_display = created_by
WHERE created_by_actor_display IS NULL
  AND created_by IS NOT NULL;

CREATE TABLE IF NOT EXISTS agent_commands (
    id                       uuid            NOT NULL DEFAULT gen_random_uuid(),
    device_id                uuid            NOT NULL,
    legal_entity_id          uuid            NOT NULL,
    site_code                varchar(50)     NOT NULL,
    command_type             varchar(50)     NOT NULL,
    reason                   varchar(500)    NOT NULL,
    payload_json             jsonb,
    status                   varchar(30)     NOT NULL DEFAULT 'PENDING',
    created_by_actor_id      varchar(200),
    created_by_actor_display varchar(200),
    created_at               timestamptz     NOT NULL DEFAULT now(),
    expires_at               timestamptz     NOT NULL,
    acked_at                 timestamptz,
    handled_at_utc           timestamptz,
    updated_at               timestamptz     NOT NULL DEFAULT now(),
    attempt_count            int             NOT NULL DEFAULT 0,
    last_error               varchar(1000),
    result_json              jsonb,
    failure_code             varchar(100),
    failure_message          varchar(1000),
    CONSTRAINT pk_agent_commands PRIMARY KEY (id),
    CONSTRAINT fk_agent_commands_device
        FOREIGN KEY (device_id) REFERENCES agent_registrations (id),
    CONSTRAINT fk_agent_commands_legal_entity
        FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id),
    CONSTRAINT chk_agent_commands_type
        CHECK (command_type IN ('FORCE_CONFIG_PULL','RESET_LOCAL_STATE','DECOMMISSION')),
    CONSTRAINT chk_agent_commands_status
        CHECK (status IN ('PENDING','DELIVERY_HINT_SENT','ACKED','FAILED','EXPIRED','CANCELLED'))
);

CREATE INDEX IF NOT EXISTS ix_agent_commands_device_status_created
    ON agent_commands (device_id, status, created_at);

CREATE INDEX IF NOT EXISTS ix_agent_commands_tenant_site_created
    ON agent_commands (legal_entity_id, site_code, created_at);

CREATE INDEX IF NOT EXISTS ix_agent_commands_device_expiry
    ON agent_commands (device_id, expires_at);

CREATE TABLE IF NOT EXISTS agent_installations (
    id                             uuid            NOT NULL DEFAULT gen_random_uuid(),
    device_id                      uuid            NOT NULL,
    legal_entity_id                uuid            NOT NULL,
    site_code                      varchar(50)     NOT NULL,
    platform                       varchar(30)     NOT NULL,
    push_provider                  varchar(30)     NOT NULL,
    registration_token_ciphertext  varchar(8192)   NOT NULL,
    token_hash                     varchar(128)    NOT NULL,
    app_version                    varchar(50)     NOT NULL,
    os_version                     varchar(50)     NOT NULL,
    device_model                   varchar(100)    NOT NULL,
    last_seen_at                   timestamptz     NOT NULL,
    last_hint_sent_at              timestamptz,
    created_at                     timestamptz     NOT NULL DEFAULT now(),
    updated_at                     timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_agent_installations PRIMARY KEY (id),
    CONSTRAINT fk_agent_installations_device
        FOREIGN KEY (device_id) REFERENCES agent_registrations (id),
    CONSTRAINT fk_agent_installations_legal_entity
        FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id),
    CONSTRAINT chk_agent_installations_platform
        CHECK (platform IN ('ANDROID')),
    CONSTRAINT chk_agent_installations_push_provider
        CHECK (push_provider IN ('FCM'))
);

CREATE INDEX IF NOT EXISTS ix_agent_installations_device_platform
    ON agent_installations (device_id, platform, push_provider);

CREATE INDEX IF NOT EXISTS ix_agent_installations_token_hash
    ON agent_installations (token_hash);

COMMIT;
