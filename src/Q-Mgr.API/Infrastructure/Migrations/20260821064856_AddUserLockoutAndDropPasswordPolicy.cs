using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLockoutAndDropPasswordPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasswordPolicies",
                schema: "qmgr");

            migrationBuilder.AddColumn<int>(
                name: "FailedLoginAttempts",
                schema: "qmgr",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockoutEnd",
                schema: "qmgr",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedLoginAttempts",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LockoutEnd",
                schema: "qmgr",
                table: "users");

            migrationBuilder.CreateTable(
                name: "PasswordPolicies",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowedSpecialCharacters = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EnableAccountLockout = table.Column<bool>(type: "boolean", nullable: false),
                    EnablePasswordExpiry = table.Column<bool>(type: "boolean", nullable: false),
                    EnablePasswordHistory = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxFailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    MaximumLength = table.Column<int>(type: "integer", nullable: false),
                    MinimumLength = table.Column<int>(type: "integer", nullable: false),
                    MinimumUniqueCharacters = table.Column<int>(type: "integer", nullable: false),
                    PasswordExpiryDays = table.Column<int>(type: "integer", nullable: false),
                    PasswordHistoryCount = table.Column<int>(type: "integer", nullable: false),
                    PreventCommonPasswords = table.Column<bool>(type: "boolean", nullable: false),
                    PreventUserInfoInPassword = table.Column<bool>(type: "boolean", nullable: false),
                    RequireDigits = table.Column<bool>(type: "boolean", nullable: false),
                    RequireLowercase = table.Column<bool>(type: "boolean", nullable: false),
                    RequireSpecialCharacters = table.Column<bool>(type: "boolean", nullable: false),
                    RequireUppercase = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordPolicies", x => x.Id);
                });
        }
    }
}
