using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentConsentAndImportKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DataConsentGivenAt",
                schema: "qmgr",
                table: "students",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DataConsentNotes",
                schema: "qmgr",
                table: "students",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DataConsentRecordedByUserId",
                schema: "qmgr",
                table: "students",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                schema: "qmgr",
                table: "roster_import_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataConsentGivenAt",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "DataConsentNotes",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "DataConsentRecordedByUserId",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "qmgr",
                table: "roster_import_jobs");
        }
    }
}
