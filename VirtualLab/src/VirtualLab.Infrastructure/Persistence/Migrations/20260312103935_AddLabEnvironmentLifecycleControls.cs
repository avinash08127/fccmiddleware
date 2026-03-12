using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VirtualLab.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLabEnvironmentLifecycleControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SettingsJson",
                table: "LabEnvironments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettingsJson",
                table: "LabEnvironments");
        }
    }
}
