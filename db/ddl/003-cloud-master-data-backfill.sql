-- =============================================================================
-- Forecourt Middleware — master-data defect remediation
-- Addresses:
--   DBIP-01 legal entity identity split/backfill
--   DBIP-02 site_uses_pre_auth persistence for existing environments
--
-- Notes:
--   1. Existing deployments may already have legal_entities.country_code populated
--      with business codes. This script preserves that legacy value in business_code.
--   2. Correct country_code/country_name/odoo_company_id values must be supplied by
--      the next Databricks legal-entity sync, which now carries those fields explicitly.
--   3. country_name defaults to the current legal entity name as a safe interim value.
--   4. odoo_company_id defaults to a clearly synthetic placeholder to make any
--      incomplete backfill obvious in read models and operations tooling.
-- =============================================================================

BEGIN;

ALTER TABLE legal_entities
    ADD COLUMN IF NOT EXISTS business_code varchar(50),
    ADD COLUMN IF NOT EXISTS country_name varchar(100),
    ADD COLUMN IF NOT EXISTS odoo_company_id varchar(100);

UPDATE legal_entities
SET business_code = country_code
WHERE business_code IS NULL OR btrim(business_code) = '';

UPDATE legal_entities
SET country_name = name
WHERE country_name IS NULL OR btrim(country_name) = '';

UPDATE legal_entities
SET odoo_company_id = 'legacy-unmapped:' || id::text
WHERE odoo_company_id IS NULL OR btrim(odoo_company_id) = '';

ALTER TABLE legal_entities
    ALTER COLUMN business_code SET NOT NULL,
    ALTER COLUMN country_name SET NOT NULL,
    ALTER COLUMN odoo_company_id SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_indexes
        WHERE schemaname = current_schema()
          AND indexname = 'uq_legal_entities_business_code') THEN
        EXECUTE 'CREATE UNIQUE INDEX uq_legal_entities_business_code ON legal_entities (business_code)';
    END IF;
END
$$;

ALTER TABLE sites
    ADD COLUMN IF NOT EXISTS site_uses_pre_auth boolean NOT NULL DEFAULT false;

COMMIT;
