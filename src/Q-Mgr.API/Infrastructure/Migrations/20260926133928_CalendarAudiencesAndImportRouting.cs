using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CalendarAudiencesAndImportRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StaffDuties_OrganizationId",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.AddColumn<string>(
                name: "UiPreferences",
                schema: "qmgr",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                schema: "qmgr",
                table: "StaffDuties",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllStaff",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AttendanceRequired",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid[]>(
                name: "AudienceDepartmentIds",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<bool>(
                name: "AudienceOnly",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string[]>(
                name: "AudienceRoleCodes",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string[]>(
                name: "AudienceStaffGroups",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<Guid[]>(
                name: "AudienceUserIds",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledByUserId",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientRequestId",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EditedByHandAt",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LibraryDocumentId",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NotifiedVersion",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Recurrence",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderStage",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "RemindersOn",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "qmgr",
                table: "SchoolEvents",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateIndex(
                name: "idx_staff_duties_source_key",
                schema: "qmgr",
                table: "StaffDuties",
                columns: new[] { "OrganizationId", "SourceKey" },
                filter: "\"SourceKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_school_events_client_request",
                schema: "qmgr",
                table: "SchoolEvents",
                columns: new[] { "OrganizationId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            // HAND-WRITTEN. SchoolEvents.DutyId has pointed at a duty with no key behind it since the calendar shipped, so a
            // duty deleted since then left a link to nothing; the key below would refuse to build over one. Clear them.
            migrationBuilder.Sql(@"UPDATE qmgr.""SchoolEvents"" e SET ""DutyId"" = NULL
                WHERE e.""DutyId"" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM qmgr.""StaffDuties"" d WHERE d.""Id"" = e.""DutyId"");");

            // HAND-WRITTEN. An imported row is matched again on (organization, branch, SourceKey), and two imports racing
            // could each create it. The key is unique now — an expression index, since a null branch (a whole-school event)
            // must collide with another null branch, which a plain unique index over a nullable column never does.
            // Duplicates already stored keep their rows; the later ones lose their key (so a re-import updates the first).
            migrationBuilder.Sql(@"UPDATE qmgr.""SchoolEvents"" e SET ""SourceKey"" = NULL
                FROM (SELECT ""Id"", row_number() OVER (PARTITION BY ""OrganizationId"",
                          COALESCE(""BranchId"", '00000000-0000-0000-0000-000000000000'::uuid), ""SourceKey""
                          ORDER BY ""CreatedAt"", ""Id"") AS n
                      FROM qmgr.""SchoolEvents"" WHERE ""SourceKey"" IS NOT NULL) d
                WHERE e.""Id"" = d.""Id"" AND d.n > 1;");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_school_events_source_key ON qmgr.""SchoolEvents""
                (""OrganizationId"", COALESCE(""BranchId"", '00000000-0000-0000-0000-000000000000'::uuid), ""SourceKey"")
                WHERE ""SourceKey"" IS NOT NULL;");

            migrationBuilder.AddForeignKey(
                name: "FK_SchoolEvents_StaffDuties_DutyId",
                schema: "qmgr",
                table: "SchoolEvents",
                column: "DutyId",
                principalSchema: "qmgr",
                principalTable: "StaffDuties",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS qmgr.ux_school_events_source_key;");

            migrationBuilder.DropForeignKey(
                name: "FK_SchoolEvents_StaffDuties_DutyId",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropIndex(
                name: "idx_staff_duties_source_key",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropIndex(
                name: "ux_school_events_client_request",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "UiPreferences",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "AllStaff",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "AttendanceRequired",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "AudienceDepartmentIds",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "AudienceOnly",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "AudienceRoleCodes",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "AudienceStaffGroups",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "AudienceUserIds",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "CancelReason",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "ClientRequestId",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "EditedByHandAt",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "LibraryDocumentId",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "NotifiedVersion",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "Recurrence",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "ReminderStage",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "RemindersOn",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "qmgr",
                table: "SchoolEvents");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDuties_OrganizationId",
                schema: "qmgr",
                table: "StaffDuties",
                column: "OrganizationId");
        }
    }
}
