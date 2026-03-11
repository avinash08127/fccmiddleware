using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VirtualLab.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LabEnvironments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SeedVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    DeterministicSeed = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeededAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabEnvironments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FccSimulatorProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LabEnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    VendorFamily = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AuthMode = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryMode = table.Column<int>(type: "INTEGER", nullable: false),
                    PreAuthMode = table.Column<int>(type: "INTEGER", nullable: false),
                    EndpointBasePath = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    RequestTemplatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseTemplatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    FieldMappingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SequenceRulesJson = table.Column<string>(type: "TEXT", nullable: false),
                    SimulatedDelayMs = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FccSimulatorProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FccSimulatorProfiles_LabEnvironments_LabEnvironmentId",
                        column: x => x.LabEnvironmentId,
                        principalTable: "LabEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LabEnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Grade = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Products_LabEnvironments_LabEnvironmentId",
                        column: x => x.LabEnvironmentId,
                        principalTable: "LabEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScenarioDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LabEnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScenarioKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    DeterministicSeed = table.Column<int>(type: "INTEGER", nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReplaySignature = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScenarioDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScenarioDefinitions_LabEnvironments_LabEnvironmentId",
                        column: x => x.LabEnvironmentId,
                        principalTable: "LabEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LabEnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActiveFccSimulatorProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TimeZone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ExternalReference = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    InboundAuthMode = table.Column<int>(type: "INTEGER", nullable: false),
                    ApiKeyHeaderName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ApiKeyValue = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BasicAuthUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BasicAuthPassword = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DeliveryMode = table.Column<int>(type: "INTEGER", nullable: false),
                    PreAuthMode = table.Column<int>(type: "INTEGER", nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sites_FccSimulatorProfiles_ActiveFccSimulatorProfileId",
                        column: x => x.ActiveFccSimulatorProfileId,
                        principalTable: "FccSimulatorProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sites_LabEnvironments_LabEnvironmentId",
                        column: x => x.LabEnvironmentId,
                        principalTable: "LabEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CallbackTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LabEnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CallbackUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AuthMode = table.Column<int>(type: "INTEGER", nullable: false),
                    ApiKeyHeaderName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ApiKeyValue = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BasicAuthUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BasicAuthPassword = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallbackTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CallbackTargets_LabEnvironments_LabEnvironmentId",
                        column: x => x.LabEnvironmentId,
                        principalTable: "LabEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CallbackTargets_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Pumps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PumpNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    FccPumpNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pumps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Pumps_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScenarioRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScenarioDefinitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReplaySeed = table.Column<int>(type: "INTEGER", nullable: false),
                    ReplaySignature = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    InputSnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResultSummaryJson = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScenarioRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScenarioRuns_ScenarioDefinitions_ScenarioDefinitionId",
                        column: x => x.ScenarioDefinitionId,
                        principalTable: "ScenarioDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScenarioRuns_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Nozzles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PumpId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NozzleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    FccNozzleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nozzles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Nozzles_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Nozzles_Pumps_PumpId",
                        column: x => x.PumpId,
                        principalTable: "Pumps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PreAuthSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PumpId = table.Column<Guid>(type: "TEXT", nullable: true),
                    NozzleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ScenarioRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExternalReference = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ReservedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    AuthorizedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    FinalAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    FinalVolume = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    RawRequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalRequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    RawResponseJson = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalResponseJson = table.Column<string>(type: "TEXT", nullable: false),
                    TimelineJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AuthorizedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreAuthSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreAuthSessions_Nozzles_NozzleId",
                        column: x => x.NozzleId,
                        principalTable: "Nozzles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreAuthSessions_Pumps_PumpId",
                        column: x => x.PumpId,
                        principalTable: "Pumps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreAuthSessions_ScenarioRuns_ScenarioRunId",
                        column: x => x.ScenarioRunId,
                        principalTable: "ScenarioRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PreAuthSessions_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SimulatedTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PumpId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NozzleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PreAuthSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ScenarioRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExternalTransactionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DeliveryMode = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Volume = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RawPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    RawHeadersJson = table.Column<string>(type: "TEXT", nullable: false),
                    DeliveryCursor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TimelineJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimulatedTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SimulatedTransactions_Nozzles_NozzleId",
                        column: x => x.NozzleId,
                        principalTable: "Nozzles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SimulatedTransactions_PreAuthSessions_PreAuthSessionId",
                        column: x => x.PreAuthSessionId,
                        principalTable: "PreAuthSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SimulatedTransactions_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SimulatedTransactions_Pumps_PumpId",
                        column: x => x.PumpId,
                        principalTable: "Pumps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SimulatedTransactions_ScenarioRuns_ScenarioRunId",
                        column: x => x.ScenarioRunId,
                        principalTable: "ScenarioRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SimulatedTransactions_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CallbackAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CallbackTargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SimulatedTransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestHeadersJson = table.Column<string>(type: "TEXT", nullable: false),
                    RequestPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseHeadersJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResponsePayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    AttemptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallbackAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CallbackAttempts_CallbackTargets_CallbackTargetId",
                        column: x => x.CallbackTargetId,
                        principalTable: "CallbackTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CallbackAttempts_SimulatedTransactions_SimulatedTransactionId",
                        column: x => x.SimulatedTransactionId,
                        principalTable: "SimulatedTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LabEventLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FccSimulatorProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PreAuthSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SimulatedTransactionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ScenarioRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    RawPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabEventLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabEventLogs_FccSimulatorProfiles_FccSimulatorProfileId",
                        column: x => x.FccSimulatorProfileId,
                        principalTable: "FccSimulatorProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LabEventLogs_PreAuthSessions_PreAuthSessionId",
                        column: x => x.PreAuthSessionId,
                        principalTable: "PreAuthSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LabEventLogs_ScenarioRuns_ScenarioRunId",
                        column: x => x.ScenarioRunId,
                        principalTable: "ScenarioRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LabEventLogs_SimulatedTransactions_SimulatedTransactionId",
                        column: x => x.SimulatedTransactionId,
                        principalTable: "SimulatedTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LabEventLogs_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CallbackAttempts_CallbackTargetId_AttemptedAtUtc",
                table: "CallbackAttempts",
                columns: new[] { "CallbackTargetId", "AttemptedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CallbackAttempts_CorrelationId",
                table: "CallbackAttempts",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_CallbackAttempts_SimulatedTransactionId_AttemptNumber",
                table: "CallbackAttempts",
                columns: new[] { "SimulatedTransactionId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CallbackTargets_LabEnvironmentId",
                table: "CallbackTargets",
                column: "LabEnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CallbackTargets_SiteId_IsActive",
                table: "CallbackTargets",
                columns: new[] { "SiteId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_CallbackTargets_TargetKey",
                table: "CallbackTargets",
                column: "TargetKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FccSimulatorProfiles_LabEnvironmentId_AuthMode_DeliveryMode_PreAuthMode",
                table: "FccSimulatorProfiles",
                columns: new[] { "LabEnvironmentId", "AuthMode", "DeliveryMode", "PreAuthMode" });

            migrationBuilder.CreateIndex(
                name: "IX_FccSimulatorProfiles_LabEnvironmentId_ProfileKey",
                table: "FccSimulatorProfiles",
                columns: new[] { "LabEnvironmentId", "ProfileKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabEnvironments_Key",
                table: "LabEnvironments",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabEventLogs_Category_OccurredAtUtc",
                table: "LabEventLogs",
                columns: new[] { "Category", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LabEventLogs_CorrelationId_OccurredAtUtc",
                table: "LabEventLogs",
                columns: new[] { "CorrelationId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LabEventLogs_FccSimulatorProfileId",
                table: "LabEventLogs",
                column: "FccSimulatorProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_LabEventLogs_PreAuthSessionId",
                table: "LabEventLogs",
                column: "PreAuthSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LabEventLogs_ScenarioRunId",
                table: "LabEventLogs",
                column: "ScenarioRunId");

            migrationBuilder.CreateIndex(
                name: "IX_LabEventLogs_SimulatedTransactionId",
                table: "LabEventLogs",
                column: "SimulatedTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_LabEventLogs_SiteId_Category_OccurredAtUtc",
                table: "LabEventLogs",
                columns: new[] { "SiteId", "Category", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Nozzles_ProductId_State",
                table: "Nozzles",
                columns: new[] { "ProductId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Nozzles_PumpId_NozzleNumber",
                table: "Nozzles",
                columns: new[] { "PumpId", "NozzleNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreAuthSessions_CorrelationId",
                table: "PreAuthSessions",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_PreAuthSessions_NozzleId",
                table: "PreAuthSessions",
                column: "NozzleId");

            migrationBuilder.CreateIndex(
                name: "IX_PreAuthSessions_PumpId",
                table: "PreAuthSessions",
                column: "PumpId");

            migrationBuilder.CreateIndex(
                name: "IX_PreAuthSessions_ScenarioRunId",
                table: "PreAuthSessions",
                column: "ScenarioRunId");

            migrationBuilder.CreateIndex(
                name: "IX_PreAuthSessions_SiteId_CreatedAtUtc",
                table: "PreAuthSessions",
                columns: new[] { "SiteId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_LabEnvironmentId_ProductCode",
                table: "Products",
                columns: new[] { "LabEnvironmentId", "ProductCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pumps_SiteId_FccPumpNumber",
                table: "Pumps",
                columns: new[] { "SiteId", "FccPumpNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Pumps_SiteId_PumpNumber",
                table: "Pumps",
                columns: new[] { "SiteId", "PumpNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioDefinitions_LabEnvironmentId_ScenarioKey",
                table: "ScenarioDefinitions",
                columns: new[] { "LabEnvironmentId", "ScenarioKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioRuns_ReplaySignature",
                table: "ScenarioRuns",
                column: "ReplaySignature");

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioRuns_ScenarioDefinitionId",
                table: "ScenarioRuns",
                column: "ScenarioDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioRuns_SiteId_StartedAtUtc",
                table: "ScenarioRuns",
                columns: new[] { "SiteId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SimulatedTransactions_CorrelationId",
                table: "SimulatedTransactions",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SimulatedTransactions_ExternalTransactionId",
                table: "SimulatedTransactions",
                column: "ExternalTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_SimulatedTransactions_NozzleId",
                table: "SimulatedTransactions",
                column: "NozzleId");

            migrationBuilder.CreateIndex(
                name: "IX_SimulatedTransactions_PreAuthSessionId",
                table: "SimulatedTransactions",
                column: "PreAuthSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SimulatedTransactions_ProductId",
                table: "SimulatedTransactions",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SimulatedTransactions_PumpId",
                table: "SimulatedTransactions",
                column: "PumpId");

            migrationBuilder.CreateIndex(
                name: "IX_SimulatedTransactions_ScenarioRunId",
                table: "SimulatedTransactions",
                column: "ScenarioRunId");

            migrationBuilder.CreateIndex(
                name: "IX_SimulatedTransactions_SiteId_OccurredAtUtc",
                table: "SimulatedTransactions",
                columns: new[] { "SiteId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SimulatedTransactions_SiteId_Status_OccurredAtUtc",
                table: "SimulatedTransactions",
                columns: new[] { "SiteId", "Status", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Sites_ActiveFccSimulatorProfileId",
                table: "Sites",
                column: "ActiveFccSimulatorProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Sites_LabEnvironmentId_ActiveFccSimulatorProfileId",
                table: "Sites",
                columns: new[] { "LabEnvironmentId", "ActiveFccSimulatorProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_Sites_SiteCode",
                table: "Sites",
                column: "SiteCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sites_SiteCode_IsActive",
                table: "Sites",
                columns: new[] { "SiteCode", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CallbackAttempts");

            migrationBuilder.DropTable(
                name: "LabEventLogs");

            migrationBuilder.DropTable(
                name: "CallbackTargets");

            migrationBuilder.DropTable(
                name: "SimulatedTransactions");

            migrationBuilder.DropTable(
                name: "PreAuthSessions");

            migrationBuilder.DropTable(
                name: "Nozzles");

            migrationBuilder.DropTable(
                name: "ScenarioRuns");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "Pumps");

            migrationBuilder.DropTable(
                name: "ScenarioDefinitions");

            migrationBuilder.DropTable(
                name: "Sites");

            migrationBuilder.DropTable(
                name: "FccSimulatorProfiles");

            migrationBuilder.DropTable(
                name: "LabEnvironments");
        }
    }
}
