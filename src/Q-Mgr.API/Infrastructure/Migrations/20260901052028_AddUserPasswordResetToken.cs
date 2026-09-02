using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPasswordResetToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PasswordResetToken",
                schema: "qmgr",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordResetTokenExpiry",
                schema: "qmgr",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PasswordResetToken",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PasswordResetTokenExpiry",
                schema: "qmgr",
                table: "users");
        }
    }
}
