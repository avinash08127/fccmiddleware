using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VirtualLab.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddForecourtSimulationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "SimulatedTransactions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SimulationStateJson",
                table: "Nozzles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "SimulatedTransactions");

            migrationBuilder.DropColumn(
                name: "SimulationStateJson",
                table: "Nozzles");
        }
    }
}
