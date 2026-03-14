-- 010-agent-control-phase5.sql
-- Adds suspicious-device workflow state to agent registrations.

BEGIN;

ALTER TABLE agent_registrations
    ADD COLUMN IF NOT EXISTS status varchar(40),
    ADD COLUMN IF NOT EXISTS suspension_reason_code varchar(100),
    ADD COLUMN IF NOT EXISTS suspension_reason varchar(500),
    ADD COLUMN IF NOT EXISTS replacement_for_device_id uuid,
    ADD COLUMN IF NOT EXISTS approval_granted_at timestamptz,
    ADD COLUMN IF NOT EXISTS approval_granted_by_actor_id varchar(200),
    ADD COLUMN IF NOT EXISTS approval_granted_by_actor_display varchar(200);

ALTER TABLE agent_registrations
    ALTER COLUMN token_hash DROP NOT NULL,
    ALTER COLUMN token_expires_at DROP NOT NULL;

UPDATE agent_registrations
SET status = CASE
    WHEN is_active THEN 'ACTIVE'
    ELSE 'DEACTIVATED'
END
WHERE status IS NULL;

ALTER TABLE agent_registrations
    ALTER COLUMN status SET DEFAULT 'ACTIVE',
    ALTER COLUMN status SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'chk_agent_registrations_status') THEN
        ALTER TABLE agent_registrations
            ADD CONSTRAINT chk_agent_registrations_status
            CHECK (status IN ('ACTIVE', 'PENDING_APPROVAL', 'QUARANTINED', 'DEACTIVATED'));
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_agent_reg_replacement_device') THEN
        ALTER TABLE agent_registrations
            ADD CONSTRAINT fk_agent_reg_replacement_device
            FOREIGN KEY (replacement_for_device_id) REFERENCES agent_registrations (id);
    END IF;
END $$;

DROP INDEX IF EXISTS ix_agent_site;
CREATE INDEX IF NOT EXISTS ix_agent_site
    ON agent_registrations (site_id, status);

CREATE INDEX IF NOT EXISTS ix_agent_legal_entity_status_registered
    ON agent_registrations (legal_entity_id, status, registered_at);

CREATE INDEX IF NOT EXISTS ix_agent_site_serial_status
    ON agent_registrations (site_id, device_serial_number, status);

COMMIT;
