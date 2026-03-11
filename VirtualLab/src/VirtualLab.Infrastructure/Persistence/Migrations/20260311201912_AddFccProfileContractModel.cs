using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VirtualLab.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFccProfileContractModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SimulatedDelayMs",
                table: "FccSimulatorProfiles");

            migrationBuilder.RenameColumn(
                name: "SequenceRulesJson",
                table: "FccSimulatorProfiles",
                newName: "ValidationRulesJson");

            migrationBuilder.AddColumn<string>(
                name: "AuthConfigurationJson",
                table: "FccSimulatorProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EndpointSurfaceJson",
                table: "FccSimulatorProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExtensionConfigurationJson",
                table: "FccSimulatorProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FailureSimulationJson",
                table: "FccSimulatorProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthConfigurationJson",
                table: "FccSimulatorProfiles");

            migrationBuilder.DropColumn(
                name: "EndpointSurfaceJson",
                table: "FccSimulatorProfiles");

            migrationBuilder.DropColumn(
                name: "ExtensionConfigurationJson",
                table: "FccSimulatorProfiles");

            migrationBuilder.DropColumn(
                name: "FailureSimulationJson",
                table: "FccSimulatorProfiles");

            migrationBuilder.RenameColumn(
                name: "ValidationRulesJson",
                table: "FccSimulatorProfiles",
                newName: "SequenceRulesJson");

            migrationBuilder.AddColumn<int>(
                name: "SimulatedDelayMs",
                table: "FccSimulatorProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
