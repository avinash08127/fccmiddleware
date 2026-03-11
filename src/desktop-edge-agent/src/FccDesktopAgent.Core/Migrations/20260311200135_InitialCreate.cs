using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FccDesktopAgent.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_config",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AppliedAt = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_config", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "buffered_transactions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    FccTransactionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SiteCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PumpNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    NozzleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    VolumeMicrolitres = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinorUnits = table.Column<long>(type: "INTEGER", nullable: false),
                    UnitPriceMinorPerLitre = table.Column<long>(type: "INTEGER", nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    StartedAt = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: false),
                    FiscalReceiptNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FccVendor = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttendantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SyncStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IngestionSource = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RawPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    UploadAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastUploadAttemptAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastUploadError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_buffered_transactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "nozzles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SiteCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OdooPumpNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    FccPumpNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    OdooNozzleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    FccNozzleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nozzles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pre_auth_records",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SiteCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OdooOrderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PumpNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    NozzleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestedAmount = table.Column<long>(type: "INTEGER", nullable: false),
                    UnitPrice = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsCloudSynced = table.Column<bool>(type: "INTEGER", nullable: false),
                    VehicleNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CustomerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CustomerTaxId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CustomerBusinessName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AttendantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FccCorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FccAuthorizationCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MatchedFccTransactionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ActualAmount = table.Column<long>(type: "INTEGER", nullable: true),
                    ActualVolume = table.Column<long>(type: "INTEGER", nullable: true),
                    AmountVariance = table.Column<long>(type: "INTEGER", nullable: true),
                    VarianceBps = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestedAt = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorizedAt = table.Column<string>(type: "TEXT", nullable: true),
                    DispensingAt = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CancelledAt = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiredAt = table.Column<string>(type: "TEXT", nullable: true),
                    FailedAt = table.Column<string>(type: "TEXT", nullable: true),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pre_auth_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sync_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LastFccSequence = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastUploadAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastStatusSyncAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastConfigSyncAt = table.Column<string>(type: "TEXT", nullable: true),
                    PendingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_state", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_al_time",
                table: "audit_log",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_bt_cleanup",
                table: "buffered_transactions",
                columns: new[] { "SyncStatus", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_bt_dedup",
                table: "buffered_transactions",
                columns: new[] { "FccTransactionId", "SiteCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bt_local_api",
                table: "buffered_transactions",
                columns: new[] { "SyncStatus", "PumpNumber", "CompletedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_bt_sync_status",
                table: "buffered_transactions",
                columns: new[] { "SyncStatus", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_nozzles_fcc_lookup",
                table: "nozzles",
                columns: new[] { "SiteCode", "FccPumpNumber", "FccNozzleNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_nozzles_odoo_lookup",
                table: "nozzles",
                columns: new[] { "SiteCode", "OdooPumpNumber", "OdooNozzleNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_par_expiry",
                table: "pre_auth_records",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "ix_par_idemp",
                table: "pre_auth_records",
                columns: new[] { "OdooOrderId", "SiteCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_par_unsent",
                table: "pre_auth_records",
                columns: new[] { "IsCloudSynced", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_config");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "buffered_transactions");

            migrationBuilder.DropTable(
                name: "nozzles");

            migrationBuilder.DropTable(
                name: "pre_auth_records");

            migrationBuilder.DropTable(
                name: "sync_state");
        }
    }
}
