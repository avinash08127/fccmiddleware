# Coding Conventions

## 1. Output Location
- **Target:** `docs/specs/foundation/coding-conventions.md`
- **Why:** Coding conventions are foundational shared decisions per `docs/STRUCTURE.md` mapping.

## 2. Scope
- **TODO item:** 3.6 Coding Conventions
- **In scope:** .NET naming/structure/patterns, Kotlin naming/coroutine/Room/Ktor patterns, shared cross-language conventions
- **Out of scope:** Angular conventions (covered by Angular HLD standalone-component/feature-folder decisions), test conventions (TODO 3.4), logging infrastructure setup (TODO 3.5)

## 3. Source Traceability
- **HLD sections:** Cloud Backend — Internal Layering, Solution Structure, Observability; Edge Agent — Layered Architecture, Project Structure, Technology Stack
- **TODO dependencies:** Aligns with 3.1 Project Scaffolding (folder structure), 3.5 Observability (logging levels)

## 4. Key Decisions

| # | Decision | Why | Impact |
|---|----------|-----|--------|
| D1 | .NET: **vertical-slice by domain** (not layer-based) | HLD already defines Domain/Application/Infrastructure layers with feature subfolders (Ingestion/, Reconciliation/, PreAuth/). Vertical slices within each layer keep related code together. | Each feature's command, handler, validator, and repository live in one subfolder. |
| D2 | .NET: **Result\<T\> pattern** for domain errors; exceptions only for infrastructure faults | Domain errors (validation failure, duplicate, state violation) are expected outcomes, not exceptional. Exceptions for DB failures, network errors. | All command handlers return `Result<T>`. Controllers map `Result.Failure` to HTTP 4xx. Unhandled exceptions become 500. |
| D3 | Kotlin: **structured concurrency with SupervisorScope** per module | Prevents one failing coroutine (e.g., telemetry) from cancelling unrelated work (e.g., FCC polling). | Each top-level module (polling, sync, API) gets its own SupervisorJob under the service lifecycle scope. |

## 5. Detailed Specification

### 5.1 .NET Conventions

| Area | Convention |
|------|-----------|
| **Classes, methods, properties** | `PascalCase` — `TransactionService.NormalizeAsync()` |
| **Local variables, parameters** | `camelCase` — `var siteCode = request.SiteCode;` |
| **Private fields** | `_camelCase` — `private readonly ILogger _logger;` |
| **Constants** | `PascalCase` — `public const int MaxBatchSize = 500;` |
| **Interfaces** | `I` prefix — `IFccAdapter`, `ITransactionRepository` |
| **Async methods** | `Async` suffix — `IngestAsync()`. Always accept `CancellationToken ct` as last parameter. Always pass `ct` to downstream calls. |
| **CancellationToken** | Required on all public async methods. API controllers get it from framework. Background workers create linked tokens from `IHostApplicationLifetime.ApplicationStopping`. |
| **Result pattern** | `Result<T>` with `Result.Success(value)` / `Result.Failure(error)`. Error type carries `ErrorCode` (string enum) + `Message`. No exceptions for domain logic. |
| **Exceptions** | Throw only for infrastructure faults (DB connection, HTTP timeout). Global exception middleware catches and returns standard error envelope. |
| **Folder structure** | Vertical slices inside each layer: `Application/Ingestion/IngestTransactionCommand.cs`, `Application/Ingestion/IngestTransactionHandler.cs`. |
| **Namespace** | Mirrors folder: `FccMiddleware.Application.Ingestion`. |
| **File per type** | One public class/interface/record per file. Filename matches type name. |

### 5.2 .NET Logging Conventions

| Level | Use for | Examples |
|-------|---------|---------|
| **Information** | Successful business operations, state transitions | Transaction ingested, config pushed, agent registered |
| **Warning** | Recoverable issues, degraded operation | Duplicate detected, retry attempt, FCC heartbeat late |
| **Error** | Failed operations requiring attention | DB write failed, FCC adapter threw, upload batch rejected |
| **Debug** | Diagnostic detail for development | Raw payload received, dedup key computed, query parameters |

Rules:
- Use Serilog structured templates: `_logger.LogInformation("Transaction {TransactionId} ingested for site {SiteCode}", txnId, site)`
- Always include: `TransactionId`, `SiteCode`, `LegalEntityId`, `CorrelationId` where available
- Never log: FCC credentials, device tokens, customer TIN values, full raw payloads at Info level

### 5.3 Kotlin Conventions

| Area | Convention |
|------|-----------|
| **Package naming** | `com.fccmiddleware.agent.<module>` — e.g., `com.fccmiddleware.agent.buffer`, `com.fccmiddleware.agent.api.routes` |
| **Classes** | `PascalCase` — `TransactionBufferDao`, `ConnectivityManager` |
| **Functions, properties** | `camelCase` — `fetchTransactions()`, `val bufferDepth` |
| **Constants** | `SCREAMING_SNAKE_CASE` in companion objects — `const val MAX_RETRY_COUNT = 5` |
| **Coroutine dispatchers** | `Dispatchers.IO` for DB/network. `Dispatchers.Default` for CPU. Never `Dispatchers.Main` for background work. Inject dispatchers via constructor for testability. |
| **Coroutine scope** | `ForegroundService` owns root `CoroutineScope(SupervisorJob() + Dispatchers.Default)`. Each module (polling, sync, API) launches under this scope with its own `SupervisorJob`. Scope cancelled in `onDestroy()`. |
| **Suspend vs Flow** | One-shot operations: `suspend fun`. Streaming/observing: `Flow<T>`. |
| **Room entities** | `PascalCase` class, `snake_case` table/column names — `@Entity(tableName = "buffered_transactions") data class BufferedTransaction(@ColumnInfo(name = "fcc_transaction_id") ...)` |
| **Room DAOs** | Interface per entity. Method naming: `insert`, `update`, `delete`, `getBy...`, `findAll...`, `countBy...` — e.g., `getByFccTransactionId()`, `findAllPending()` |
| **Ktor routes** | One file per route group in `api/routes/`. Use extension function on `Route`: `fun Route.transactionRoutes(useCase: TransactionUseCase) { get("/api/transactions") { ... } }`. Register in `LocalApiServer.configureRouting()`. |
| **Nullability** | Prefer non-null types. Use nullable only when the domain requires it (e.g., `odooOrderId: String?`). |

### 5.4 Shared Cross-Language Conventions

| Area | Rule |
|------|------|
| **Date/time storage** | UTC `Instant` everywhere. Format: ISO 8601 `2026-03-11T14:30:00Z`. .NET: `DateTimeOffset` (UTC). Kotlin: `kotlinx.datetime.Instant`. |
| **Date/time display** | Convert to local timezone only in portal display layer. Edge Agent and Cloud never apply timezone offsets. |
| **Currency** | Store as `Long` minor units (cents/pence). 1234 = R12.34. .NET: `long`. Kotlin: `Long`. Never `float`/`double`/`decimal` for stored amounts. Use `BigDecimal` only for intermediate calculations if needed; convert back to `Long` before persistence. |
| **Middleware-generated IDs** | UUID v4, stored as `string`/`String`. .NET: `Guid.NewGuid().ToString()`. Kotlin: `UUID.randomUUID().toString()`. |
| **FCC-originated IDs** | Preserve as-is. Treat as opaque `string`. Never parse, pad, or re-format. |
| **JSON field naming** | `camelCase` in all API request/response payloads. .NET: `JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase`. Kotlin: `@SerialName` or kotlinx.serialization default. |
| **Boolean fields** | Prefix with `is`/`has`/`can` — `isActive`, `hasFiscalReceipt`. |
| **Collection fields** | Plural noun — `transactions`, `pumpStatuses`. |
| **Enum serialization** | Serialize as `SCREAMING_SNAKE_CASE` string in JSON — `"SYNCED_TO_ODOO"`, `"FULLY_ONLINE"`. |

## 6. Validation and Edge Cases

- **CancellationToken not passed:** Treat as a code-review blocker. All public async .NET methods must accept and propagate `CancellationToken`.
- **Floating-point currency:** Reject in code review. Static analysis rule recommended (TODO: configure in 3.1 scaffolding).
- **Timezone-aware dates in storage:** Reject. Only `Instant`/`DateTimeOffset(UTC)` in persistence and API layers.

## 7. Cross-Component Impact

| Component | Impact |
|-----------|--------|
| Cloud Backend | Result pattern, async conventions, logging levels, folder structure |
| Edge Agent | Coroutine scope management, Room naming, Ktor route organization, dispatcher injection |
| Portal | JSON camelCase, enum SCREAMING_SNAKE_CASE, ISO 8601 dates (consumption side) |
| All APIs | camelCase JSON, UTC dates, minor-unit currency, UUID v4 IDs |

## 8. Dependencies
- **Prerequisites:** None strictly required, but aligns with 3.1 Project Scaffolding
- **Downstream:** 3.1 Project Scaffolding (applies these conventions), 3.4 Testing Strategy (test naming conventions), all Tier 1–2 implementation
- **Next step:** Enforce via `.editorconfig` (.NET) and `ktlint` config (Kotlin) during 3.1 scaffolding

## 9. Open Questions
None.

## 10. Acceptance Checklist
- [ ] .NET naming, async, and Result pattern conventions documented and unambiguous
- [ ] Kotlin package, coroutine, Room, and Ktor conventions documented and unambiguous
- [ ] Shared date/time, currency, ID, and JSON conventions documented
- [ ] Logging level guidelines defined with concrete examples
- [ ] No conflicts with HLD architectural decisions
- [ ] Reviewed by at least one .NET and one Kotlin developer

## 11. Output Files to Create
| File | Purpose |
|------|---------|
| `docs/specs/foundation/coding-conventions.md` | This artefact |

## 12. Recommended Next TODO
**3.1 Project Scaffolding** — applies these conventions to the actual solution/project structure, `.editorconfig`, and linter configs.
