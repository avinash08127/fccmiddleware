-- =============================================================================
-- Portal Users & Role-Based Access Control
-- Entra tokens do not carry role claims, so roles are maintained locally.
-- Users are looked up by email (always present in Entra tokens).
-- =============================================================================

-- Role lookup (seeded, not user-editable)
CREATE TABLE IF NOT EXISTS portal_roles (
    id      SMALLINT        NOT NULL,
    name    VARCHAR(64)     NOT NULL,
    CONSTRAINT pk_portal_roles PRIMARY KEY (id),
    CONSTRAINT uq_portal_roles_name UNIQUE (name)
);

INSERT INTO portal_roles (id, name) VALUES
    (1, 'FccAdmin'),
    (2, 'FccUser'),
    (3, 'FccViewer')
ON CONFLICT (id) DO NOTHING;

-- Portal user record (looked up by email from Entra token)
CREATE TABLE IF NOT EXISTS portal_users (
    id                  UUID            NOT NULL DEFAULT gen_random_uuid(),
    email               VARCHAR(320)    NOT NULL,
    display_name        VARCHAR(256)    NOT NULL,
    entra_object_id     VARCHAR(128),
    role_id             SMALLINT        NOT NULL,
    all_legal_entities  BOOLEAN         NOT NULL DEFAULT FALSE,
    is_active           BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at          TIMESTAMPTZ     NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ     NOT NULL DEFAULT now(),
    created_by          VARCHAR(320),
    updated_by          VARCHAR(320),
    CONSTRAINT pk_portal_users PRIMARY KEY (id),
    CONSTRAINT uq_portal_users_email UNIQUE (email),
    CONSTRAINT fk_portal_users_role FOREIGN KEY (role_id) REFERENCES portal_roles (id)
);

CREATE INDEX IF NOT EXISTS ix_portal_users_entra_oid ON portal_users (entra_object_id) WHERE entra_object_id IS NOT NULL;

-- User <-> Legal Entity scoping
CREATE TABLE IF NOT EXISTS portal_user_legal_entities (
    user_id         UUID    NOT NULL,
    legal_entity_id UUID    NOT NULL,
    CONSTRAINT pk_portal_user_legal_entities PRIMARY KEY (user_id, legal_entity_id),
    CONSTRAINT fk_pule_user FOREIGN KEY (user_id) REFERENCES portal_users (id) ON DELETE CASCADE,
    CONSTRAINT fk_pule_legal_entity FOREIGN KEY (legal_entity_id) REFERENCES legal_entities (id)
);

-- =============================================================================
-- SEED: Initial FccAdmin user
-- Replace the email and display name with the actual first admin's values.
-- This is the only way to bootstrap access — no auto-provisioning.
-- =============================================================================
INSERT INTO portal_users (email, display_name, role_id, all_legal_entities, created_by)
VALUES (
    'avinash.mishra@noobsplayground.com',
    'Avinash Mishra',
    1,                                 -- 1 = FccAdmin
    TRUE,                              -- access all legal entities
    'seed'
)
ON CONFLICT (email) DO NOTHING;
