# Documentation Structure

This repository should keep architecture and detailed design artefacts in stable, predictable locations so they can be reused during implementation, testing, and future agent-driven work.

## Principles

- Keep source-of-truth documents in version control.
- Separate high-level design from implementation-ready specifications.
- Prefer one artefact per TODO item or tightly related decision set.
- Use filenames that are sortable and easy to reference from prompts, tickets, and pull requests.
- Keep machine-consumable contracts close to the human-readable specification that defines them.

## Folder Layout

### `/docs/requirements`

Source requirements and business context documents.

Recommended contents:
- `Requirements.md`
- `HighLevelRequirements.md`

### `/docs/hld`

High-level design documents for each major component.

Recommended contents:
- `cloud-backend-hld.md`
- `edge-agent-hld.md`
- `angular-portal-hld.md`

### `/docs/specs`

Implementation-ready detailed artefacts produced from `TODO.md`.

Subfolders:
- `/docs/specs/foundation` for foundational contracts and shared decisions
- `/docs/specs/state-machines` for lifecycle/state definitions and diagrams
- `/docs/specs/api` for endpoint-level API specifications and behavior rules
- `/docs/specs/data-models` for canonical models, field dictionaries, mapping tables
- `/docs/specs/security` for auth, registration, token, and data-protection specs
- `/docs/specs/config` for configuration object schemas and rollout behavior
- `/docs/specs/events` for event envelope/type definitions and event flow rules
- `/docs/specs/reconciliation` for matching rules, tolerance rules, and review flows
- `/docs/specs/error-handling` for error taxonomy, retries, quarantine, alerting
- `/docs/specs/testing` for test strategy, coverage matrix, and acceptance mapping

### `/docs/plans`

Sequencing, rollout, and implementation planning artefacts.

Examples:
- tier execution plans
- dependency maps
- sprint readiness checklists

### `/docs/diagrams`

Shared diagrams that support specs and HLDs.

Examples:
- sequence diagrams
- architecture diagrams
- cross-component flows

### `/contracts/openapi`

Authoritative OpenAPI definitions for external and internal HTTP APIs.

Recommended split:
- `cloud-public.yaml`
- `edge-local.yaml`
- `master-data-sync.yaml`

### `/schemas`

Machine-consumable schemas.

Subfolders:
- `/schemas/canonical` for canonical transaction and shared object schemas
- `/schemas/events` for event payload schemas
- `/schemas/config` for Edge Agent and cloud config schemas

### `/db`

Database design and migration artefacts.

Subfolders:
- `/db/ddl` for human-reviewed DDL design files
- `/db/migrations` for implementation migrations later
- `/db/reference` for seed/reference data strategy and lookup definitions

## Naming Convention

Use lowercase kebab-case filenames.

Recommended pattern:
- `tier-1-1-canonical-transaction-spec.md`
- `tier-1-2-transaction-lifecycle-state-machine.md`
- `tier-1-3-edge-upload-api-spec.md`
- `tier-1-4-cloud-schema-design.md`
- `tier-2-5-device-registration-security-flow.md`

For machine-readable files:
- `canonical-transaction.schema.json`
- `telemetry-payload.schema.json`
- `cloud-public.openapi.yaml`
- `001-initial-transactions-ddl.sql`

## Mapping Guide

Use this default mapping when generating artefacts from `TODO.md`:

- Canonical models, field mappings, enums, versioning:
  `/docs/specs/data-models`
- State machine definitions:
  `/docs/specs/state-machines`
- API contract specifications:
  `/docs/specs/api`
  and corresponding OpenAPI files in `/contracts/openapi`
- Database schema design:
  `/docs/specs/data-models`
  and supporting SQL in `/db/ddl`
- FCC adapter interfaces:
  `/docs/specs/foundation`
- Error handling:
  `/docs/specs/error-handling`
- Deduplication:
  `/docs/specs/reconciliation`
- Reconciliation rules:
  `/docs/specs/reconciliation`
- Configuration schema:
  `/docs/specs/config`
  and machine-readable schema in `/schemas/config`
- Security implementation:
  `/docs/specs/security`
- Event schema:
  `/docs/specs/events`
  and machine-readable schema in `/schemas/events`
- Testing strategy:
  `/docs/specs/testing`

## Minimum Standard For Each New Artefact

Each generated artefact should state:
- the exact TODO item addressed
- the target output file path
- source traceability to requirements and HLD
- concrete decisions made
- open questions, if any
- downstream implementation impacts

This structure is intended to let future developers and coding agents use the artefacts directly during implementation without having to rediscover where decisions were recorded.
