using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomDomainVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CustomDomainAttempts",
                schema: "qmgr",
                table: "organizations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CustomDomainCertificateAt",
                schema: "qmgr",
                table: "organizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CustomDomainLastAttemptAt",
                schema: "qmgr",
                table: "organizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomDomainLastError",
                schema: "qmgr",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomDomainPending",
                schema: "qmgr",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomDomainVerificationToken",
                schema: "qmgr",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CustomDomainVerifiedAt",
                schema: "qmgr",
                table: "organizations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomDomainAttempts",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "CustomDomainCertificateAt",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "CustomDomainLastAttemptAt",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "CustomDomainLastError",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "CustomDomainPending",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "CustomDomainVerificationToken",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "CustomDomainVerifiedAt",
                schema: "qmgr",
                table: "organizations");
        }
    }
}
