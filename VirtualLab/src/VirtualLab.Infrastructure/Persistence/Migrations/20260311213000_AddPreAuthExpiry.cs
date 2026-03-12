using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VirtualLab.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(VirtualLabDbContext))]
    [Migration("20260311213000_AddPreAuthExpiry")]
    public partial class AddPreAuthExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "PreAuthSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreAuthSessions_Status_ExpiresAtUtc",
                table: "PreAuthSessions",
                columns: new[] { "Status", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PreAuthSessions_Status_ExpiresAtUtc",
                table: "PreAuthSessions");

            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                table: "PreAuthSessions");
        }
    }
}
