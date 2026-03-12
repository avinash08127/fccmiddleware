using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VirtualLab.Infrastructure.Persistence;

#nullable disable

namespace VirtualLab.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(VirtualLabDbContext))]
    [Migration("20260312140000_AddPumpLayoutCoordinates")]
    public partial class AddPumpLayoutCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LayoutX",
                table: "Pumps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LayoutY",
                table: "Pumps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE Pumps
                SET LayoutX = 120 + (((PumpNumber - 1) % 5) * 240),
                    LayoutY = 100 + (((PumpNumber - 1) / 5) * 260)
                WHERE LayoutX = 0 AND LayoutY = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LayoutX",
                table: "Pumps");

            migrationBuilder.DropColumn(
                name: "LayoutY",
                table: "Pumps");
        }
    }
}
