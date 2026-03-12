using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FccDesktopAgent.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWebSocketOdooFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OrderUuid",
                table: "buffered_transactions",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OdooOrderId",
                table: "buffered_transactions",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AddToCart",
                table: "buffered_transactions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PaymentId",
                table: "buffered_transactions",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDiscard",
                table: "buffered_transactions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgedAt",
                table: "buffered_transactions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "OrderUuid", table: "buffered_transactions");
            migrationBuilder.DropColumn(name: "OdooOrderId", table: "buffered_transactions");
            migrationBuilder.DropColumn(name: "AddToCart", table: "buffered_transactions");
            migrationBuilder.DropColumn(name: "PaymentId", table: "buffered_transactions");
            migrationBuilder.DropColumn(name: "IsDiscard", table: "buffered_transactions");
            migrationBuilder.DropColumn(name: "AcknowledgedAt", table: "buffered_transactions");
        }
    }
}
