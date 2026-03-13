# Repository & Data Access Inventory — FCC Middleware Platform

> Structural inventory of all data access components: repositories, DbContexts, ORMs, caching, and file storage.

---

## 1. Desktop Edge Agent — Data Access

### DbContext

| Component | ORM | Database | Mode | Location |
|-----------|-----|----------|------|----------|
| `AgentDbContext` | EF Core | SQLite | WAL | `src/desktop-edge-agent/src/FccDesktopAgent.Core/Buffer/AgentDbContext.cs` |
| `AgentDbContextFactory` | EF Core | SQLite | Design-time | `src/desktop-edge-agent/src/FccDesktopAgent.Core/Buffer/AgentDbContextFactory.cs` |

### Entities

| Entity | Table Purpose | Location |
|--------|--------------|----------|
| `BufferedTransaction` | Locally buffered fuel transactions | `Buffer/Entities/BufferedTransaction.cs` |
| `PreAuthRecord` | Buffered pre-authorization records | `Buffer/Entities/BufferedPreAuth.cs` |
| `AgentConfigRecord` | Persisted agent configuration | `Buffer/Entities/AgentConfigRecord.cs` |
| `AuditLogEntry` | Local audit log | `Buffer/Entities/AuditLogEntry.cs` |
| `NozzleMapping` | Nozzle-to-product mappings | `Buffer/Entities/NozzleMapping.cs` |

### EF Configuration

| Configuration | Location |
|--------------|----------|
| `BufferEntityConfiguration` | `Buffer/Entities/BufferEntityConfiguration.cs` |

### Interceptors

| Interceptor | Purpose | Location |
|------------|---------|----------|
| `SqliteWalModeInterceptor` | Enables SQLite WAL mode on connection open | `Buffer/Interceptors/SqliteWalModeInterceptor.cs` |

### Migrations

| Migration | Date | Description |
|-----------|------|-------------|
| `InitialCreate` | 2026-03-11 | Initial schema |
| `AddPreAuthFailureReason` | 2026-03-12 | Add failure reason to pre-auth |
| `AddWebSocketOdooFields` | 2026-03-13 | Add WebSocket/Odoo fields |

### Buffer/Repository Services

| Component | Responsibility | Location |
|-----------|---------------|----------|
| `TransactionBufferManager` | CRUD for buffered transactions | `Buffer/TransactionBufferManager.cs` |
| `IntegrityChecker` | Validate buffer integrity | `Buffer/IntegrityChecker.cs` |
| `AgentDataDirectory` | Cross-platform data directory resolution | `Buffer/AgentDataDirectory.cs` |

### File Storage

| Component | Responsibility | Location |
|-----------|---------------|----------|
| `LocalOverrideManager` | Read/write `overrides.json` for local config tuning | `Config/LocalOverrideManager.cs` |
| `SiteDataManager` | Persist site data (products/pumps/nozzles) to JSON | `MasterData/SiteDataManager.cs` |
| `RegistrationManager` | Persist registration state to local filesystem | `Registration/RegistrationManager.cs` |

### Credential Storage

| Component | Mechanism | Location |
|-----------|-----------|----------|
| `PlatformCredentialStore` | DPAPI (Windows), Keychain (macOS), libsecret (Linux) | `Security/PlatformCredentialStore.cs` |

---

## 2. Legacy DOMS Implementation — Data Access

### DbContext / Connection Factory

| Component | ORM | Database | Location |
|-----------|-----|----------|----------|
| `AppDbContext` | None (ADO.NET) | SQL Server | `DppMiddleWareService/DbContext/AppDbContext.cs` |

### Repositories

| Repository | Interface | Responsibility | Location |
|-----------|-----------|---------------|----------|
| `TransactionRepository` | `ITransactionRepository` | Transaction CRUD via stored procedures | `Repository/TransactionRepository.cs` |
| `LogRepository` | `ILogRepository` | Application log persistence via stored procedures | `Repository/LogRepository.cs` |

### Data Access Pattern

| Aspect | Detail |
|--------|--------|
| Technology | ADO.NET (`Microsoft.Data.SqlClient`) |
| ORM | None |
| Pattern | Repository + stored procedures |
| Connection | `AppDbContext.CreateConnection()` → `SqlConnection` |
| Commands | `SqlCommand` with `CommandType.StoredProcedure` |
| Readers | `ExecuteReader`, `ExecuteReaderAsync` with manual mapping |
| Bulk inserts | Table-Valued Parameters (TVPs) |
| Transactions | `SqlConnection.BeginTransaction()` |

### Database Project

| Component | Type | Location |
|-----------|------|----------|
| `DppMiddleWareDatabase` | SSDT SQL Server Database Project | `DOMSRealImplementation/DppMiddleWareDatabase/` |

### Key Tables

| Table | Purpose |
|-------|---------|
| `Transactions` | Fuel transaction records |
| `PumpTransactions` | Pump-level transaction records |
| `PumpTxnTracker` | Transaction tracking state |
| `PumpStatus` | Current pump status |
| `FuelPumpStatusMaster` | Pump status master data |
| `AttendantMaster` | Attendant authorization records |
| `GradeMaster` | Fuel grade definitions |
| `EventDetails` | FCC event log |
| `Records` | General records |
| `Logs` | Application logs |
| `PumpBlockUnblockHistory` | Pump block/unblock audit trail |
| `FpAvailableGrades` | Available grades per pump |
| `FpAvailableSms` | Available service modes |
| `FpNozzleId` | Nozzle identification |
| `FpMinPresetValues` | Minimum preset values |
| `FpSupTransBufStatusCall` | Supervised transaction buffer status |
| `TransInSupBuffer` | Transactions in supervised buffer |

### Key Stored Procedures

| Stored Procedure | Purpose |
|-----------------|---------|
| `sp_InsertTransaction` | Insert new transaction |
| `sp_GetPendingTransactions` | Get pending (unsynced) transactions |
| `sp_GetUnsyncedTransactions` | Get transactions not yet synced |
| `sp_MarkTransactionsSynced` | Mark transactions as synced |
| `sp_UpdateTransactionSyncStatus` | Update sync status |
| `sp_InsertEvent` | Insert FCC event |
| `sp_InsertEvent_FpStatus` | Insert pump status event |
| `sp_InsertEvent_FpSupTransBufStatus` | Insert supervised buffer status event |
| `sp_InsertLog` | Insert application log |
| `sp_GetLatestTransactions` | Get latest transactions |
| `sp_AddPumpTransaction` | Add pump transaction |
| `sp_UpdatePumpTransactions` | Update pump transactions |
| `sp_UpsertGradePrice_Bulk` | Bulk upsert fuel grade prices |
| `sp_UpsertAttendantPumpCount` | Upsert attendant pump count |
| `sp_GetAllFpStatusWithEvents` | Get all pump statuses with events |
| `sp_GetAllPumpTransactions` | Get all pump transactions |

---

## 3. Cloud Backend — Data Access

### DbContext

| Component | ORM | Database | Location |
|-----------|-----|----------|----------|
| `FccMiddlewareDbContext` | EF Core | PostgreSQL | `src/cloud/FccMiddleware.Infrastructure/Persistence/FccMiddlewareDbContext.cs` |

### EF Configurations

| Configuration | Location |
|--------------|----------|
| `AgentRegistrationConfiguration` | `Persistence/Configurations/AgentRegistrationConfiguration.cs` |
| `FccConfigConfiguration` | `Persistence/Configurations/FccConfigConfiguration.cs` |
| `ReconciliationRecordConfiguration` | `Persistence/Configurations/ReconciliationRecordConfiguration.cs` |
| `TransactionConfiguration` | `Persistence/Configurations/TransactionConfiguration.cs` |

### Repositories / Data Providers

| Component | Responsibility | Location |
|-----------|---------------|----------|
| `SiteFccConfigProvider` | Provide site FCC configuration from DB | `Repositories/SiteFccConfigProvider.cs` |

### Caching

| Component | Technology | Responsibility | Location |
|-----------|-----------|---------------|----------|
| `RedisDeduplicationService` | Redis | Message deduplication | `Deduplication/RedisDeduplicationService.cs` |
| `RedisRegistrationThrottleService` | Redis | Rate-limit registration attempts | `Security/RedisRegistrationThrottleService.cs` |

### File/Object Storage

| Component | Technology | Responsibility | Location |
|-----------|-----------|---------------|----------|
| `S3RawPayloadArchiver` | AWS S3 | Archive raw transaction payloads | `Storage/S3RawPayloadArchiver.cs` |

### DbContext Interfaces (Application Layer)

| Interface | Responsibility | Location |
|-----------|---------------|----------|
| `IIngestDbContext` | Transaction ingestion data access | `Application/Ingestion/IIngestDbContext.cs` |
| `IReconciliationDbContext` | Reconciliation data access | `Application/Reconciliation/IReconciliationDbContext.cs` |
| `ITelemetryDbContext` | Telemetry data access | `Application/Telemetry/ITelemetryDbContext.cs` |

---

## 4. VirtualLab — Data Access

### DbContext

| Component | ORM | Database | Location |
|-----------|-----|----------|----------|
| `VirtualLabDbContext` | EF Core | SQLite | `VirtualLab/src/VirtualLab.Infrastructure/Persistence/VirtualLabDbContext.cs` |
| `VirtualLabDesignTimeDbContextFactory` | EF Core | SQLite | `VirtualLab/src/VirtualLab.Infrastructure/Persistence/VirtualLabDesignTimeDbContextFactory.cs` |

### EF Configurations

| Configuration | Entity | Location |
|--------------|--------|----------|
| `LabEnvironmentConfiguration` | `LabEnvironment` | `Persistence/Configurations/` |
| `SiteConfiguration` | `Site` | `Persistence/Configurations/` |
| `FccSimulatorProfileConfiguration` | `FccSimulatorProfile` | `Persistence/Configurations/` |
| `PumpConfiguration` | `Pump` | `Persistence/Configurations/` |
| `NozzleConfiguration` | `Nozzle` | `Persistence/Configurations/` |
| `ProductConfiguration` | `Product` | `Persistence/Configurations/` |
| `PreAuthSessionConfiguration` | `PreAuthSession` | `Persistence/Configurations/` |
| `CallbackTargetConfiguration` | `CallbackTarget` | `Persistence/Configurations/` |
| `CallbackAttemptConfiguration` | `CallbackAttempt` | `Persistence/Configurations/` |
| `LabEventLogConfiguration` | `LabEventLog` | `Persistence/Configurations/` |
| `ScenarioDefinitionConfiguration` | `ScenarioDefinition` | `Persistence/Configurations/` |
| `ScenarioRunConfiguration` | `ScenarioRun` | `Persistence/Configurations/` |

### Seed Data

| Component | Responsibility | Location |
|-----------|---------------|----------|
| `VirtualLabSeedService` | Seed initial test data | `Persistence/Seed/VirtualLabSeedService.cs` |
| `SeedProfileFactory` | Create seed FCC profiles | `FccProfiles/SeedProfileFactory.cs` |

### Data Access Pattern

- No repository layer — services query `VirtualLabDbContext` directly
- Scoped DbContext lifetime
