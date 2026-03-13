-- =============================================================================
-- Forecourt Middleware — Dev Seed Data (Radix FCC, Malawi)
-- One legal entity, one site, 3 products, 2 pumps, 4 nozzles, Radix FCC config
-- =============================================================================

-- Deterministic UUIDs for stable references in testing
-- Namespace: a1b2c3d4-e5f6-7890-abcd-ef1234567890

BEGIN;

-- =============================================================================
-- LEGAL ENTITY: Malawi
-- =============================================================================
INSERT INTO legal_entities (
    id, business_code, country_code, country_name, name, currency_code,
    tax_authority_code, fiscalization_required, fiscalization_provider,
    default_timezone, odoo_company_id
) VALUES (
    'a1000001-0000-4000-8000-000000000001',
    'FCC-MW', 'MW', 'Malawi', 'FCC Malawi Ltd', 'MWK',
    'MRA', true, 'MRA',
    'Africa/Blantyre', 'odoo-mw-001'
) ON CONFLICT (country_code) DO NOTHING;

-- =============================================================================
-- SITE: Lilongwe Station 01 (COCO, Radix FCC)
-- =============================================================================
INSERT INTO sites (
    id, legal_entity_id, site_code, site_name, operating_model,
    site_uses_pre_auth, connectivity_mode, company_tax_payer_id,
    fiscalization_mode, odoo_site_id
) VALUES (
    'b2000001-0000-4000-8000-000000000001',
    'a1000001-0000-4000-8000-000000000001',
    'MW-LL-001', 'Lilongwe Station 01', 'COCO',
    true, 'CONNECTED', 'MW-TIN-12345',
    'FCC_DIRECT', 'odoo-site-mw-001'
) ON CONFLICT (site_code) DO NOTHING;

-- =============================================================================
-- PRODUCTS: 3 fuel grades for Malawi
-- =============================================================================
INSERT INTO products (id, legal_entity_id, product_code, product_name, unit_of_measure) VALUES
    ('c3000001-0000-4000-8000-000000000001', 'a1000001-0000-4000-8000-000000000001', 'PETROL_ULP', 'Unleaded Petrol', 'LITRE'),
    ('c3000002-0000-4000-8000-000000000002', 'a1000001-0000-4000-8000-000000000001', 'DIESEL_50',  'Diesel 50ppm',    'LITRE'),
    ('c3000003-0000-4000-8000-000000000003', 'a1000001-0000-4000-8000-000000000001', 'DIESEL_500', 'Diesel 500ppm',   'LITRE')
ON CONFLICT (legal_entity_id, product_code) DO NOTHING;

-- =============================================================================
-- PUMPS: 2 pumps at Lilongwe Station 01
-- Pump 1: Odoo pump=1, FCC pump=1 (Radix PumpAddr=1, Fp=1)
-- Pump 2: Odoo pump=2, FCC pump=2 (Radix PumpAddr=2, Fp=1)
-- =============================================================================
INSERT INTO pumps (id, site_id, legal_entity_id, pump_number, fcc_pump_number) VALUES
    ('d4000001-0000-4000-8000-000000000001', 'b2000001-0000-4000-8000-000000000001', 'a1000001-0000-4000-8000-000000000001', 1, 1),
    ('d4000002-0000-4000-8000-000000000002', 'b2000001-0000-4000-8000-000000000001', 'a1000001-0000-4000-8000-000000000001', 2, 2)
ON CONFLICT (site_id, pump_number) DO NOTHING;

-- =============================================================================
-- NOZZLES: 2 nozzles per pump (ULP + Diesel 50)
-- Pump 1: Nozzle 1 = ULP, Nozzle 2 = Diesel 50
-- Pump 2: Nozzle 1 = ULP, Nozzle 2 = Diesel 50
-- =============================================================================
INSERT INTO nozzles (id, pump_id, site_id, legal_entity_id, odoo_nozzle_number, fcc_nozzle_number, product_id) VALUES
    -- Pump 1 nozzles
    ('e5000001-0000-4000-8000-000000000001', 'd4000001-0000-4000-8000-000000000001', 'b2000001-0000-4000-8000-000000000001', 'a1000001-0000-4000-8000-000000000001', 1, 1, 'c3000001-0000-4000-8000-000000000001'),
    ('e5000002-0000-4000-8000-000000000002', 'd4000001-0000-4000-8000-000000000001', 'b2000001-0000-4000-8000-000000000001', 'a1000001-0000-4000-8000-000000000001', 2, 2, 'c3000002-0000-4000-8000-000000000002'),
    -- Pump 2 nozzles
    ('e5000003-0000-4000-8000-000000000003', 'd4000002-0000-4000-8000-000000000002', 'b2000001-0000-4000-8000-000000000001', 'a1000001-0000-4000-8000-000000000001', 1, 1, 'c3000001-0000-4000-8000-000000000001'),
    ('e5000004-0000-4000-8000-000000000004', 'd4000002-0000-4000-8000-000000000002', 'b2000001-0000-4000-8000-000000000001', 'a1000001-0000-4000-8000-000000000001', 2, 2, 'c3000002-0000-4000-8000-000000000002')
ON CONFLICT (pump_id, odoo_nozzle_number) DO NOTHING;

-- =============================================================================
-- OPERATOR: Test attendant
-- =============================================================================
INSERT INTO operators (id, legal_entity_id, operator_code, operator_name) VALUES
    ('f6000001-0000-4000-8000-000000000001', 'a1000001-0000-4000-8000-000000000001', 'ATT-001', 'Test Attendant')
ON CONFLICT (legal_entity_id, operator_code) DO NOTHING;

-- =============================================================================
-- FCC CONFIG: Radix for Lilongwe Station 01
--
-- Radix protocol specifics:
--   - Auth port: 10000 (pre-auth XML requests)
--   - Transaction port: 10001 (auth_port + 1, FIFO drain)
--   - SharedSecret: SHA-1 signing password (test value)
--   - UsnCode: Unique Station Number (100001)
--   - FccPumpAddressMap: JSON mapping FCC pump number -> {PumpAddr, Fp}
--     PumpAddr = physical dispenser address on the FCC
--     Fp = fuelling point within that dispenser (starts at 1)
-- =============================================================================
INSERT INTO fcc_configs (
    id, site_id, legal_entity_id,
    fcc_vendor, fcc_model, connection_protocol,
    host_address, port, credential_ref,
    transaction_mode, ingestion_mode,
    pull_interval_seconds, heartbeat_interval_seconds, heartbeat_timeout_seconds,
    -- Radix fields
    shared_secret, usn_code, auth_port, fcc_pump_address_map
) VALUES (
    'f7000001-0000-4000-8000-000000000001',
    'b2000001-0000-4000-8000-000000000001',
    'a1000001-0000-4000-8000-000000000001',
    'RADIX', 'Radix-P4000', 'REST',
    '192.168.1.100', 10001, 'dev/fcc/mw-ll-001/radix',
    'PULL', 'RELAY',
    15, 30, 90,
    -- Radix: test shared secret, USN, auth port, pump address map
    'dev-test-shared-secret-do-not-use-in-prod',
    100001,
    10000,
    '{"1": {"PumpAddr": 1, "Fp": 1}, "2": {"PumpAddr": 2, "Fp": 1}}'
) ON CONFLICT DO NOTHING;

-- =============================================================================
-- PORTAL SETTINGS: Default global settings
-- =============================================================================
INSERT INTO portal_settings (id, global_defaults_json, alert_configuration_json) VALUES (
    'a0000001-0000-4000-8000-000000000001',
    '{
        "defaultPullIntervalSeconds": 15,
        "defaultHeartbeatIntervalSeconds": 60,
        "defaultHeartbeatTimeoutSeconds": 180,
        "stalePendingThresholdDays": 7,
        "amountTolerancePercent": 1.0,
        "timeWindowMinutes": 60
    }',
    '{
        "heartbeatAlertThresholdMinutes": 5,
        "pendingTransactionAlertThresholdMinutes": 30,
        "dlqAlertThreshold": 10
    }'
) ON CONFLICT (id) DO NOTHING;

-- =============================================================================
-- LEGAL ENTITY SETTINGS: Malawi reconciliation overrides
-- =============================================================================
INSERT INTO legal_entity_settings_overrides (
    id, legal_entity_id,
    amount_tolerance_percent, amount_tolerance_absolute_minor_units,
    time_window_minutes, stale_pending_threshold_days
) VALUES (
    'a0000002-0000-4000-8000-000000000002',
    'a1000001-0000-4000-8000-000000000001',
    1.50, 500,
    120, 7
) ON CONFLICT (legal_entity_id) DO NOTHING;

COMMIT;

-- =============================================================================
-- VERIFICATION: Quick sanity check
-- =============================================================================
DO $$
DECLARE
    le_count int;
    site_count int;
    pump_count int;
    nozzle_count int;
    product_count int;
    fcc_count int;
BEGIN
    SELECT count(*) INTO le_count FROM legal_entities;
    SELECT count(*) INTO site_count FROM sites;
    SELECT count(*) INTO pump_count FROM pumps;
    SELECT count(*) INTO nozzle_count FROM nozzles;
    SELECT count(*) INTO product_count FROM products;
    SELECT count(*) INTO fcc_count FROM fcc_configs;

    RAISE NOTICE '========================================';
    RAISE NOTICE 'Seed data loaded successfully:';
    RAISE NOTICE '  Legal Entities: %', le_count;
    RAISE NOTICE '  Sites:          %', site_count;
    RAISE NOTICE '  Pumps:          %', pump_count;
    RAISE NOTICE '  Nozzles:        %', nozzle_count;
    RAISE NOTICE '  Products:       %', product_count;
    RAISE NOTICE '  FCC Configs:    %', fcc_count;
    RAISE NOTICE '========================================';
END
$$;
