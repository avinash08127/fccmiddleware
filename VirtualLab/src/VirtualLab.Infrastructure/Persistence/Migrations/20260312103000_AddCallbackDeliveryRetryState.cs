using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VirtualLab.Infrastructure.Persistence;

#nullable disable

namespace VirtualLab.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(VirtualLabDbContext))]
    [Migration("20260312103000_AddCallbackDeliveryRetryState")]
    /// <inheritdoc />
    public partial class AddCallbackDeliveryRetryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgedAtUtc",
                table: "CallbackAttempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRetryCount",
                table: "CallbackAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAtUtc",
                table: "CallbackAttempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestUrl",
                table: "CallbackAttempts",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "CallbackAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcknowledgedAtUtc",
                table: "CallbackAttempts");

            migrationBuilder.DropColumn(
                name: "MaxRetryCount",
                table: "CallbackAttempts");

            migrationBuilder.DropColumn(
                name: "NextRetryAtUtc",
                table: "CallbackAttempts");

            migrationBuilder.DropColumn(
                name: "RequestUrl",
                table: "CallbackAttempts");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "CallbackAttempts");
        }
    }
}
