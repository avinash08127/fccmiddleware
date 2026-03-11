# Cloud Backend — Agent System Prompt

**Use this prompt as context when assigning ANY Cloud Backend task to an AI coding agent.**

---

## You Are Working On

The **Forecourt Middleware Cloud Backend** — a .NET 10 / ASP.NET Core modular monolith deployed on AWS ECS Fargate. It is the central transaction ledger for a fuel retail platform spanning 2,000+ fuel stations across 12 African countries.

## What This System Does

1. **Ingests fuel dispensing transactions** from Forecourt Controllers (FCCs) via direct push and Edge Agent catch-up upload
2. **Deduplicates** using `(fccTransactionId, siteCode)` as the primary key with a 90-day window
3. **Normalizes** raw vendor payloads into a canonical transaction model via vendor-specific adapters
4. **Stores** transactions in PostgreSQL Aurora (partitioned by `created_at`, monthly)
5. **Exposes transactions to Odoo ERP** via a pull-based poll API — Odoo pulls PENDING transactions, creates orders, then acknowledges with `odooOrderId`
6. **Manages pre-authorization lifecycle** — receives pre-auth requests forwarded from Edge Agents, tracks through PENDING → AUTHORIZED → DISPENSING → COMPLETED
7. **Reconciles** pre-authorized transactions against actual dispenses using a priority-based matching engine
8. **Coordinates Edge Agents** — device registration, config distribution, telemetry collection, version compatibility checks
9. **Syncs master data** from Databricks (legal entities, sites, pumps, products, operators)
10. **Publishes domain events** via a transactional outbox pattern

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 10, ASP.NET Core |
| Database | PostgreSQL Aurora (EF Core, partitioned tables) |
| Cache | Redis ElastiCache (dedup cache, rate limiting) |
| Messaging | Transactional outbox → outbox publisher worker |
| Auth | Azure Entra (portal), Device JWT (Edge Agents), API Key + HMAC (FCCs), API Key (Odoo, Databricks) |
| Logging | Serilog (structured JSON, CloudWatch) |
| CQRS | MediatR (commands/queries/handlers) |
| Testing | xUnit, NSubstitute, FluentAssertions, Testcontainers |
| Infra | AWS ECS Fargate, Aurora, ElastiCache, S3, KMS |

## Solution Structure

```
src/cloud/
├── FccMiddleware.Api/              # ASP.NET Core Web API host
├── FccMiddleware.Worker/           # Background worker host
├── FccMiddleware.Domain/           # Domain models, interfaces, state machines (ZERO external deps)
├── FccMiddleware.Application/      # Commands, queries, handlers (MediatR)
├── FccMiddleware.Infrastructure/   # EF Core, Redis, S3, messaging
├── FccMiddleware.Contracts/        # API DTOs, event contracts
├── FccMiddleware.ServiceDefaults/  # Cross-cutting (OpenTelemetry, Serilog, HealthChecks)
├── FccMiddleware.Adapter.Doms/    # DOMS FCC adapter
└── tests/
    ├── FccMiddleware.UnitTests/
    ├── FccMiddleware.IntegrationTests/
    ├── FccMiddleware.ArchitectureTests/
    └── FccMiddleware.Adapter.Doms.Tests/
```

## Key Architecture Rules

1. **Domain project has ZERO external dependencies** — pure C# models, interfaces, enums, state machine logic
2. **Multi-tenancy**: Every tenant-scoped table has `legal_entity_id`. EF Core global query filters enforce tenant isolation. NEVER query without tenant context.
3. **Adapter pattern**: Each FCC vendor (DOMS, Radix, etc.) is a separate adapter project implementing `IFccAdapter`. Selection is config-driven by `fccVendor`.
4. **Transactional outbox**: Domain events are written to `outbox_messages` table in the same DB transaction as the entity change. A background worker publishes them.
5. **Result pattern**: Use `Result<T>` for domain operations instead of throwing exceptions for expected failures.
6. **Currency**: Always store as `long` minor units (cents). NEVER use `float`/`double`/`decimal` for money in storage.
7. **Dates**: UTC ISO 8601 everywhere. `DateTimeOffset` in C#. `timestamptz` in PostgreSQL.
8. **IDs**: `Guid` (UUID v4) for middleware-generated IDs. Preserve original FCC IDs as opaque strings.
9. **Logging**: Structured logging via Serilog. NEVER log sensitive fields (FCC credentials, tokens, customer TIN). Use `[Sensitive]` attribute.
10. **Naming**: PascalCase classes/methods, camelCase locals, `_prefixed` private fields. camelCase in JSON payloads.

## Must-Read Artifacts (Before ANY Task)

| Artifact | Path | What It Tells You |
|----------|------|-------------------|
| Cloud Backend HLD | `WIP-HLD-Cloud-Backend.md` | Architecture, module design, all flows, deployment |
| Canonical Transaction Schema | `schemas/canonical/canonical-transaction.schema.json` | Every field in the transaction model |
| Pre-Auth Record Schema | `schemas/canonical/pre-auth-record.schema.json` | Pre-auth lifecycle model |
| Cloud OpenAPI Spec | `schemas/openapi/cloud-api.yaml` | All API endpoints, request/response shapes |
| Database Schema Design | `docs/specs/data-models/tier-1-4-database-schema-design.md` | All tables, columns, indexes, constraints |
| Cloud DDL | `db/ddl/001-cloud-schema.sql` | PostgreSQL DDL reference |
| State Machines | `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` | Transaction, pre-auth, reconciliation state machines |
| Error Handling | `docs/specs/error-handling/tier-2-1-error-handling-strategy.md` | Error codes, retry semantics, DLQ |
| Security Plan | `docs/specs/security/tier-2-5-security-implementation-plan.md` | Auth flows, JWT, API keys, HMAC |
| Coding Conventions | `docs/specs/foundation/coding-conventions.md` | .NET coding standards |
| Event Schema | `docs/specs/events/event-schema-design.md` | Domain event types and envelope |

## Transaction Lifecycle (Cloud)

```
NEW → PENDING (dedup passed) or DUPLICATE (dedup matched)
PENDING → SYNCED_TO_ODOO (Odoo acknowledges with odooOrderId)
PENDING/SYNCED_TO_ODOO/DUPLICATE → ARCHIVED (retention worker)
```

`isStale` is a FLAG on PENDING transactions, not a state transition. Odoo can still acknowledge stale transactions.

## Pre-Auth Lifecycle

```
PENDING → AUTHORIZED → DISPENSING → COMPLETED
                                  → CANCELLED
                                  → EXPIRED
                                  → FAILED
```

## Ingestion Sources

| Source | How It Arrives | Dedup Handling |
|--------|---------------|----------------|
| FCC Push (CLOUD_DIRECT) | `POST /api/v1/transactions/ingest` | Primary dedup on `(fccTransactionId, siteCode)` |
| Edge Agent Upload | `POST /api/v1/transactions/upload` (batch) | Same primary dedup; per-record response |
| Cloud Pull Worker | Worker fetches from FCC via adapter `FetchTransactions` | Same primary dedup |

## Testing Standards

- Domain logic: Unit tests with NSubstitute mocks
- Application handlers: Unit tests verifying command → result
- API endpoints: Integration tests with `WebApplicationFactory` + Testcontainers (PostgreSQL, Redis)
- Adapters: Unit tests with sample payloads (use fixture JSON files)
- Architecture: NetArchTest rules enforcing layer boundaries (Domain has no infra deps, etc.)
