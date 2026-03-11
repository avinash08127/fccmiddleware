# Seed Data Strategy

**Source:** Tier 1.4 Database Schema Design

## Approach

Seed data is loaded via EF Core `HasData()` in entity configurations for static reference data. Environment-specific test data is loaded via a separate migration or startup script gated by environment flag.

## Static Seed Data (All Environments)

### Legal Entities (Initial 5 Countries)

| country_code | name | currency_code | tax_authority_code | default_timezone | fiscalization_required |
|---|---|---|---|---|---|
| MW | Malawi | MWK | MRA | Africa/Blantyre | true |
| TZ | Tanzania | TZS | TRA | Africa/Dar_es_Salaam | true |
| BW | Botswana | BWP | BURS | Africa/Gaborone | false |
| ZM | Zambia | ZMW | ZRA | Africa/Lusaka | false |
| NA | Namibia | NAD | NamRA | Africa/Windhoek | false |

These are the initial 5 countries per REQ-1 / AC-1.1. The remaining 7 legal entities are added via Databricks sync when those countries are onboarded.

### Default Products (Per Legal Entity)

Seeded per legal entity. Exact product codes come from Odoo master data sync. For development and testing, seed a minimal set:

| product_code | product_name |
|---|---|
| PETROL_ULP | Unleaded Petrol |
| DIESEL_50 | Diesel 50ppm |
| DIESEL_500 | Diesel 500ppm |

Actual product data is authoritative only from Databricks sync (REQ-11).

## Environment-Specific Test Data (dev, staging Only)

Loaded by a conditional startup task (`IHostedService`) that checks `ASPNETCORE_ENVIRONMENT != Production`.

- 2 test legal entities (MW, TZ) with complete configuration
- 3 test sites per legal entity with different operating models (COCO, CODO, DODO)
- 2 pumps per site, 2 nozzles per pump
- 1 FCC config per site (DOMS vendor, PUSH mode, CLOUD_DIRECT ingestion)
- 1 agent registration per site (for integration testing)

Test data uses deterministic UUIDs (UUID v5 with a fixed namespace) so that integration tests can reference stable IDs.

## Edge Agent

No seed data. Edge databases are created empty on first launch. Configuration arrives via the registration and config-pull flows.
