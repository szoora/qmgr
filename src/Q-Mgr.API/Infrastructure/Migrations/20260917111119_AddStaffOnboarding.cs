using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "JoinRequestRejectedAt",
                schema: "qmgr",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                schema: "qmgr",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingApprovalAt",
                schema: "qmgr",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoUrl",
                schema: "qmgr",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TemporaryPasswordExpiresAt",
                schema: "qmgr",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JoinRequestRejectedAt",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PendingApprovalAt",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PhotoUrl",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "TemporaryPasswordExpiresAt",
                schema: "qmgr",
                table: "users");
        }
    }
}
