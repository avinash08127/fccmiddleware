# Project Scaffolding

## 1. Output Location
- **Target:** `docs/specs/foundation/project-scaffolding.md`
- **Source code:** `src/cloud/`, `src/edge-agent/`, `src/portal/`
- **Why:** Project scaffolding is a foundational decision per `docs/STRUCTURE.md`.

## 2. Scope
- **TODO item:** 3.1 Project Scaffolding
- **In scope:** Solution/project creation, folder structure, NuGet/Gradle/npm dependency setup, Serilog configuration, health endpoints, Swagger, Result pattern, Room database with initial entities, Ktor server scaffold, foreground service, boot receiver, Koin DI, Angular feature-route scaffold with lazy loading
- **Out of scope:** CI/CD pipelines (TODO 3.3), test strategy details (TODO 3.4), auth guard implementation (TODO 2.5)

## 3. Source Traceability
- **HLD sections:** Cloud Backend — Solution Structure, Internal Layering; Edge Agent — Layered Architecture, Project Structure; Angular Portal — Feature-based folder structure
- **Prerequisite:** 3.6 Coding Conventions (applied)

## 4. What Was Scaffolded

### Cloud Backend (.NET 10)

| Project | Type | Purpose |
|---------|------|---------|
| `FccMiddleware.Api` | Web API | Controllers, Swagger, health endpoints, Serilog |
| `FccMiddleware.Worker` | Worker Service | Background processing host, Serilog |
| `FccMiddleware.Domain` | Class Library | Aggregates, value objects, interfaces, events |
| `FccMiddleware.Application` | Class Library | Commands/queries, handlers, MediatR, FluentValidation, Result\<T\> |
| `FccMiddleware.Infrastructure` | Class Library | EF Core (Npgsql), Redis, messaging |
| `FccMiddleware.Contracts` | Class Library | API DTOs, event contracts |
| `FccMiddleware.Adapter.Doms` | Class Library | DOMS FCC adapter implementation |
| `*.Tests` (×5) | xUnit | One test project per source project, NSubstitute, Testcontainers |

**Dependency flow:** Domain ← Application ← Infrastructure ← Api/Worker. Adapter.Doms → Domain only.

**Key packages:** Serilog.AspNetCore, MediatR, Swashbuckle, FluentValidation, Npgsql.EntityFrameworkCore.PostgreSQL, StackExchange.Redis, NSubstitute, Testcontainers.PostgreSql, Microsoft.AspNetCore.Mvc.Testing.

**Domain folder structure:**
- `Domain/` — Transactions, PreAuth, Reconciliation, Configuration, Adapters, Events, Common
- `Application/` — Ingestion, Reconciliation, PreAuth, Odoo, AgentCoordination, Common
- `Infrastructure/` — Persistence, Cache, Messaging, Services, Adapters
- `Api/` — Controllers, Middleware, Filters

**Includes:** `.editorconfig` enforcing naming conventions from coding-conventions spec.

### Edge Agent (Kotlin/Android)

| Package | Purpose |
|---------|---------|
| `com.fccmiddleware.agent.api` | Ktor embedded HTTP server + routes |
| `com.fccmiddleware.agent.buffer` | Room database, entities, DAOs |
| `com.fccmiddleware.agent.connectivity` | Connectivity state management |
| `com.fccmiddleware.agent.ingestion` | FCC polling workers |
| `com.fccmiddleware.agent.sync` | Cloud upload/status sync |
| `com.fccmiddleware.agent.preauth` | Pre-auth handler |
| `com.fccmiddleware.agent.adapter.doms` | DOMS FCC adapter |
| `com.fccmiddleware.agent.domain` | Canonical models, business rules |
| `com.fccmiddleware.agent.config` | Configuration management |
| `com.fccmiddleware.agent.telemetry` | Health metrics reporting |
| `com.fccmiddleware.agent.service` | Foreground service, boot receiver |
| `com.fccmiddleware.agent.ui` | Diagnostics/provisioning UI (Compose) |

**Key dependencies:** Ktor 3.0.3, Room 2.6.1, Koin 4.0.1, kotlinx.serialization, kotlinx.datetime, Timber, WorkManager, MockK.

**Includes:** FccAgentService (foreground, SupervisorJob scope), BootReceiver, LocalApiServer (Ktor CIO on port 8585), AppDatabase (WAL mode), BufferedTransactionEntity + PreAuthRecordEntity with Room DAOs.

### Angular Portal (Angular 20, standalone components)

| Folder | Purpose |
|--------|---------|
| `core/auth` | Auth service and guards |
| `core/api` | HTTP interceptor, API services |
| `core/models` | Shared TypeScript interfaces |
| `features/transactions` | Transaction browser |
| `features/reconciliation` | Reconciliation workbench |
| `features/edge-agents` | Agent health dashboard |
| `features/site-config` | Site configuration management |
| `features/audit-log` | Audit log viewer |
| `shared/` | Shared components, pipes, directives |

**Includes:** Lazy-loaded feature routes, API interceptor with base URL from environment config, environment files for dev/prod.

## 5. Build Verification
- .NET solution: **12/12 projects build, 0 errors, 0 warnings**
- Edge Agent: Gradle files created; requires Android SDK for compilation
- Portal: `ng new` completed; requires `npm install` to run

## 6. Acceptance Checklist
- [x] .NET solution created with API, Worker, Domain, Application, Infrastructure, Contracts, Adapter projects
- [x] Project references follow Clean Architecture dependency rules
- [x] NuGet packages installed: Serilog, MediatR, Swashbuckle, EF Core, FluentValidation, Redis, health checks
- [x] Test projects with xUnit, NSubstitute, Testcontainers
- [x] Serilog configured in both API and Worker Program.cs
- [x] Health endpoints (`/health`, `/health/live`, `/health/ready`)
- [x] Swagger enabled in Development
- [x] Result\<T\> pattern in Application.Common
- [x] .editorconfig with naming conventions
- [x] Domain folder structure matches HLD vertical slices
- [x] Kotlin/Android project with Gradle Kotlin DSL
- [x] Room database with WAL, initial entities and DAOs
- [x] Ktor embedded server scaffold
- [x] Foreground service with SupervisorJob scope
- [x] Boot receiver for auto-start
- [x] Koin DI setup
- [x] Angular project with standalone components, SCSS, routing
- [x] Feature-based folder structure with lazy-loaded routes
- [x] API interceptor and environment configs
- [ ] Angular `npm install` (deferred — run on dev machine)
- [ ] Android SDK setup for Edge Agent builds (deferred — requires device/emulator environment)

## 7. Recommended Next TODO
**3.2 Repository & Branching Strategy** — define monorepo structure, branching model, PR requirements, and commit conventions for this scaffolded codebase.
