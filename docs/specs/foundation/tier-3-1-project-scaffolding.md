# Project Scaffolding Specification

## 1. Output Location
- **Target file:** `docs/specs/foundation/tier-3-1-project-scaffolding.md`
- **Why:** `docs/STRUCTURE.md` maps foundational contracts and shared decisions to `/docs/specs/foundation`. Project scaffolding is a foundational engineering artefact that defines the starting structure for all three components.

## 2. Scope
- **TODO item:** 3.1 Project Scaffolding (Cloud Backend, Edge Agent, Angular Portal)
- **In scope:** Solution/project structure creation, dependency injection setup, logging, configuration, database setup, health checks, API documentation generation, foreground service, boot receiver, routing, auth guard, HTTP interceptors, component library baseline
- **Out of scope:** CI/CD pipelines (3.3), branching strategy (3.2), testing strategy (3.4), observability setup (3.5), coding conventions (3.6), business logic implementation

## 3. Source Traceability
- **Requirements:** REQ-15 (Edge Agent), REQ-17 (multi-tenancy portal access)
- **HLD sections:**
  - Cloud Backend HLD §5 (Project Structure), §11 (Technology Direction)
  - Edge Agent HLD §5 (Project Structure), §11 (Technology Direction)
  - Angular Portal HLD §5 (Project Structure), §11 (Technology Direction)
- **Assumptions:** Tier 1 and Tier 2 artefacts provide the domain models and API contracts that scaffolding will stub. Scaffolding proceeds with placeholder types; real domain logic follows in Phase 0–1.

## 4. Key Decisions

| # | Decision | Why | Impact |
|---|----------|-----|--------|
| D1 | Edge Agent DI: **Koin** | Lightweight, Kotlin-native, no annotation processing, faster build times on constrained HHT. HLD §11 lists both Koin and Hilt as suitable. | Simpler module declarations. No Hilt Gradle plugin. Trade-off: less compile-time safety than Hilt. |
| D2 | Angular UI library: **PrimeNG** | HLD OQ-PO-2 recommends evaluating PrimeNG for its DataTable component, which suits the transaction browser and reconciliation workbench. | Richer data table, tree, and chart components out-of-the-box. Larger bundle than Angular Material — mitigated by lazy-loading. |
| D3 | Auth provider: **Azure Entra** (not Cognito) | TODO text says "Cognito" but all three HLDs consistently specify Azure Entra with MSAL. HLDs are authoritative. | Use `@azure/msal-angular` (MSAL v3). Requires Entra app registration per environment. |
| D4 | .NET target: **.NET 10** | Cloud Backend HLD §4.2 and §11 specify .NET 10 / ASP.NET Core. | Use `net10.0` TFM across all projects. |
| D5 | Edge Agent: single Android module | HLD §5.5 rationale — multi-module adds complexity without benefit at this scale. Package structure provides separation. | One `:app` Gradle module. Packages provide layering. |

## 5. Detailed Specification

### 5.1 Cloud Backend (.NET)

**Repository:** `fcc-middleware-cloud` (separate repo)

**Solution structure** — follow Cloud Backend HLD §5.2 exactly. Create these projects:

| Project | Type | Purpose | Key Dependencies |
|---------|------|---------|-----------------|
| `FccMiddleware.Api` | ASP.NET Core Web API | HTTP host, controllers, middleware | Serilog.AspNetCore, Swashbuckle.AspNetCore, AspNetCore.HealthChecks.UI |
| `FccMiddleware.Workers` | Worker Service | Background job host | Serilog, same domain/infra refs |
| `FccMiddleware.Domain` | Class Library | Domain models, interfaces, state machines | None (zero external deps) |
| `FccMiddleware.Application` | Class Library | Commands, queries, handlers | MediatR, FluentValidation |
| `FccMiddleware.Infrastructure` | Class Library | EF Core, Redis, S3, messaging | Npgsql.EntityFrameworkCore.PostgreSQL, StackExchange.Redis |
| `FccMiddleware.Contracts` | Class Library | API DTOs, event contracts | None |
| `FccMiddleware.ServiceDefaults` | Class Library | Cross-cutting config helpers | OpenTelemetry SDK, Serilog, HealthChecks |
| `Adapters/FccMiddleware.Adapter.Doms` | Class Library | DOMS FCC adapter (MVP) | Domain project ref |
| `tests/FccMiddleware.UnitTests` | xUnit | Domain + application tests | xUnit, NSubstitute, FluentAssertions |
| `tests/FccMiddleware.IntegrationTests` | xUnit | API + DB tests | Testcontainers, WebApplicationFactory |
| `tests/FccMiddleware.ArchitectureTests` | xUnit | Module boundary enforcement | NetArchTest.Rules |
| `tests/FccMiddleware.Adapter.Doms.Tests` | xUnit | DOMS adapter tests | xUnit, NSubstitute |

**DI, Logging, Configuration setup (`FccMiddleware.Api/Program.cs` and `FccMiddleware.Workers/Program.cs`):**

| Concern | Implementation |
|---------|---------------|
| DI | Built-in `Microsoft.Extensions.DependencyInjection`. Register domain services, application handlers (MediatR), infrastructure implementations, adapter factory. |
| Logging | Serilog with `WriteTo.Console()` (structured JSON) + `WriteTo.AmazonCloudWatchLogs()`. Enrich with `CorrelationId`, `SiteCode`, `LegalEntityId`. |
| Configuration | `appsettings.json` → `appsettings.{Environment}.json` → environment variables. Environments: `Development`, `Staging`, `UAT`, `Production`. |

**EF Core + PostgreSQL:**

| Item | Detail |
|------|--------|
| DbContext | `FccMiddlewareDbContext` in `Infrastructure/Persistence/` |
| Connection string | `ConnectionStrings:FccMiddleware` in appsettings, overridden by env var in deployed environments |
| Initial migration | `001_InitialSchema` — create empty migration to validate tooling. Real schema follows from Tier 1.4 artefact. |
| Global query filter | Stub `legalEntityId` filter on `DbContext.OnModelCreating` for multi-tenancy |

**Health checks (`/health`):**

| Check | Package |
|-------|---------|
| PostgreSQL | `AspNetCore.HealthChecks.NpgSql` |
| Redis | `AspNetCore.HealthChecks.Redis` |
| Self | Built-in liveness |

Expose at `/health` (liveness) and `/health/ready` (readiness including DB + Redis).

**Swagger/OpenAPI:**

| Item | Detail |
|------|--------|
| Package | `Swashbuckle.AspNetCore` |
| Config | Enable XML comments. Group by controller. Include JWT bearer auth definition. |
| Path | `/swagger` (dev/staging only; disabled in production via config flag) |

### 5.2 Edge Agent (Kotlin/Android)

**Repository:** `fcc-edge-agent` (separate repo)

**Project structure** — follow Edge Agent HLD §5.3 exactly. Single `:app` module with the package tree at `com.fccmiddleware.edge`.

**Gradle build (Kotlin DSL):**

| File | Key Configuration |
|------|-------------------|
| `settings.gradle.kts` | `rootProject.name = "fcc-edge-agent"`, include `:app` |
| `build.gradle.kts` (root) | Kotlin plugin, Android Gradle plugin version declarations |
| `app/build.gradle.kts` | `compileSdk = 34`, `minSdk = 31` (Android 12), `targetSdk = 34`. Dependencies below. |

**Key dependencies (app/build.gradle.kts):**

| Dependency | Purpose |
|------------|---------|
| `io.ktor:ktor-server-core`, `ktor-server-cio` | Embedded HTTP server (CIO engine) |
| `io.ktor:ktor-client-okhttp` | Cloud API HTTP client |
| `io.ktor:ktor-serialization-kotlinx-json` | JSON serialization |
| `androidx.room:room-runtime`, `room-ktx`, `room-compiler` (KSP) | SQLite persistence |
| `io.insert-koin:koin-android` | Dependency injection |
| `org.jetbrains.kotlinx:kotlinx-coroutines-android` | Coroutine support |
| `org.jetbrains.kotlinx:kotlinx-serialization-json` | Canonical model serialization |
| `androidx.security:security-crypto` | EncryptedSharedPreferences |

**Room database setup:**

| Item | Detail |
|------|--------|
| Database class | `BufferDatabase` in `buffer/` package, annotated `@Database` |
| Initial entities | `BufferedTransaction`, `PreAuthRecord` — stub with `id`, `fccTransactionId`, `siteCode`, `status`, `createdAt`, `payload` (JSON string) |
| WAL mode | Enable via `openHelperFactory` or `.setJournalMode(JournalMode.WRITE_AHEAD_LOGGING)` |
| Initial migration | Auto-generate from Room schema export. Enable `exportSchema = true` in `@Database`. |
| DAO | `TransactionBufferDao` — stub with `@Insert`, `@Query("SELECT * FROM buffered_transactions WHERE status = :status")` |

**Ktor embedded server scaffold:**

| Item | Detail |
|------|--------|
| Class | `LocalApiServer` in `api/` package |
| Port | `8585` (localhost) + configurable LAN port |
| Engine | CIO |
| Routes | Stub 4 route files: `TransactionRoutes.kt`, `PreAuthRoutes.kt`, `PumpStatusRoutes.kt`, `StatusRoutes.kt` — each with placeholder 501 responses |
| Content negotiation | `kotlinx.serialization` JSON plugin |

**Foreground service + boot receiver:**

| Item | Detail |
|------|--------|
| Service | `EdgeAgentForegroundService` extends `Service`, returns `START_STICKY` |
| Notification | Persistent notification channel `fcc_edge_agent_channel`, title "FCC Edge Agent Running" |
| Boot receiver | `BootReceiver` with `RECEIVE_BOOT_COMPLETED` intent filter, starts foreground service |
| Manifest | Declare `<service android:foregroundServiceType="dataSync">`, `<receiver>` with boot intent, permissions: `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_DATA_SYNC`, `RECEIVE_BOOT_COMPLETED`, `INTERNET`, `ACCESS_NETWORK_STATE`, `ACCESS_WIFI_STATE` |

**Koin DI setup:**

| Item | Detail |
|------|--------|
| Application class | `FccEdgeApplication` extends `Application`, calls `startKoin { androidContext(this@FccEdgeApplication); modules(appModule) }` |
| Module file | `di/AppModule.kt` — declare `single { BufferDatabase.create(get()) }`, `single { LocalApiServer(get()) }`, `factory { TransactionBufferDao }` stubs |

### 5.3 Angular Portal

**Repository:** `fcc-admin-portal` (separate repo)

**Project creation and structure** — follow Angular Portal HLD §5.2 exactly.

| Step | Command / Action |
|------|-----------------|
| Create project | `ng new fcc-admin-portal --style=scss --routing --standalone` (Angular 18+ CLI) |
| Add PrimeNG | `ng add primeng` + `@primeng/themes` |
| Add MSAL | `npm install @azure/msal-browser @azure/msal-angular` |
| Add ESLint | `ng add @angular-eslint/schematics` + Prettier config |

**Routing and auth guard:**

| Item | Detail |
|------|--------|
| `app.routes.ts` | Top-level routes with `canActivate: [MsalGuard]` on all feature routes. Lazy-load each feature via `loadChildren`. |
| `auth.guard.ts` | Wraps `MsalGuard` from `@azure/msal-angular`. Redirects unauthenticated users to Entra login. |
| `role.guard.ts` | Reads `roles` claim from JWT. Blocks navigation if required role is missing. Returns to "Access Denied" route. |
| `auth.config.ts` | MSAL configuration: `clientId`, `authority` (Entra tenant), `redirectUri`, `scopes`. Values from `environment.ts`. |

**HTTP interceptors:**

| Interceptor | Responsibility |
|-------------|---------------|
| `auth.interceptor.ts` | Attach Azure Entra JWT bearer token to all API requests via `MsalInterceptor`. |
| `api.interceptor.ts` | Prepend `environment.apiBaseUrl`. Handle 401 (trigger silent token refresh), 403 (redirect to access-denied), 5xx (toast notification). |

**Component library / design system baseline:**

| Item | Detail |
|------|--------|
| Theme | PrimeNG Lara Light theme. Custom SCSS variables in `styles/_variables.scss` for brand colours. |
| Shell | `core/layout/shell.component.ts` — sidebar + header + `<router-outlet>`. |
| Shared components (stubs) | `data-table/`, `status-badge/`, `date-range-picker/`, `loading-spinner/`, `empty-state/` — create component files with minimal template. |
| Environment files | `environment.ts` (dev), `environment.staging.ts`, `environment.prod.ts` — with `apiBaseUrl`, `msalClientId`, `msalAuthority`, `msalRedirectUri` |

## 6. Validation and Edge Cases
- **.NET:** Verify `dotnet build` succeeds with zero warnings. Verify `dotnet test` discovers test projects. Verify `/health` returns 200. Verify `/swagger` renders the UI.
- **Edge Agent:** Verify `./gradlew assembleDebug` succeeds. Verify Room schema export generates JSON. Verify foreground service starts and shows notification on emulator (API 31+). Verify Ktor server responds to `GET http://localhost:8585/api/status` with 501.
- **Angular:** Verify `ng build` succeeds. Verify `ng serve` loads the app shell. Verify MSAL redirect flow reaches Entra login (requires Entra app registration). Verify PrimeNG theme renders correctly.

## 7. Cross-Component Impact
- **Shared contracts:** The `FccMiddleware.Contracts` NuGet package (Cloud) will later be used to generate TypeScript API clients for the Angular portal via NSwag. Scaffolding creates the project but does not populate contracts yet.
- **Canonical model:** Both Cloud `FccMiddleware.Domain` and Edge `adapter/common/CanonicalTransaction.kt` will implement the same model defined in Tier 1.1. Scaffolding creates stub files only.

## 8. Dependencies
- **Prerequisites:** None — this is Tier 3 and can proceed in parallel with Tier 1/2 artefact work.
- **Downstream TODOs affected:**
  - 3.3 CI/CD Pipeline Design — depends on repository and project structure being established
  - 3.4 Testing Strategy — depends on test project structure
  - 5.2 Phase 0 — consumes the scaffolded projects directly
- **External prerequisites:**
  - Azure Entra app registration (needed for Angular auth guard to function beyond stub)
  - PostgreSQL instance (needed for EF Core migration validation; Testcontainers suffices for local dev)

## 9. Open Questions
None. All decisions resolved from HLD context.

## 10. Acceptance Checklist
- [ ] Cloud: Solution with all 12 projects builds cleanly (`dotnet build`)
- [ ] Cloud: `FccMiddleware.Api` starts and serves `/health` → 200 and `/swagger` → Swagger UI
- [ ] Cloud: `FccMiddleware.Workers` starts without errors
- [ ] Cloud: Serilog writes structured JSON logs to console
- [ ] Cloud: `appsettings.Development.json` exists with PostgreSQL connection string placeholder
- [ ] Cloud: EF Core initial migration created and can be applied to a local/test PostgreSQL
- [ ] Cloud: All test projects discovered by `dotnet test` (0 tests is acceptable at scaffold stage)
- [ ] Edge: `./gradlew assembleDebug` succeeds
- [ ] Edge: Room schema export JSON generated in `app/schemas/`
- [ ] Edge: Foreground service starts on emulator with persistent notification
- [ ] Edge: Boot receiver declared in manifest
- [ ] Edge: Ktor server starts on port 8585 and returns 501 for stub routes
- [ ] Edge: Koin modules resolve without runtime errors
- [ ] Portal: `ng build` succeeds with zero errors
- [ ] Portal: App shell renders with sidebar, header, and router outlet
- [ ] Portal: MSAL configuration present in environment files
- [ ] Portal: Auth guard redirects unauthenticated users
- [ ] Portal: API interceptor attaches base URL
- [ ] Portal: PrimeNG theme applied and visible
- [ ] Portal: Shared stub components exist in `shared/components/`

## 11. Output Files to Create
| File | Type |
|------|------|
| `docs/specs/foundation/tier-3-1-project-scaffolding.md` | This specification |

No machine-readable companion needed. The HLD project structure trees (Cloud §5.2, Edge §5.3, Portal §5.2) serve as the authoritative directory blueprints.

## 12. Recommended Next TODO
**3.2 Repository & Branching Strategy** — should be established immediately after scaffolding so the new repositories have correct branch protection, naming conventions, and PR requirements from first commit.
