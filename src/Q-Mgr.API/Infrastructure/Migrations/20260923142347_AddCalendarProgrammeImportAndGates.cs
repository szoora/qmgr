using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarProgrammeImportAndGates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CheckedInByUserId",
                schema: "qmgr",
                table: "visitors",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CheckedOutByUserId",
                schema: "qmgr",
                table: "visitors",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryGate",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExitGate",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CalendarFeedCreatedAt",
                schema: "qmgr",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalendarFeedTokenHash",
                schema: "qmgr",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImportJobId",
                schema: "qmgr",
                table: "StaffDuties",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SchoolEvents",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    Category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Audience = table.Column<int>(type: "integer", nullable: false),
                    ClassNames = table.Column<string[]>(type: "text[]", nullable: false),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResponsibleText = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ResponsibleUserIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    ResponsibleDepartmentIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    DutyId = table.Column<Guid>(type: "uuid", nullable: true),
                    SeriesId = table.Column<Guid>(type: "uuid", nullable: true),
                    SeriesName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ImportJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchoolEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchoolEvents_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SchoolEvents_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_visitors_branch_entry_gate",
                schema: "qmgr",
                table: "visitors",
                columns: new[] { "BranchId", "EntryGate" },
                filter: "\"EntryGate\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_users_calendar_feed_token",
                schema: "qmgr",
                table: "users",
                column: "CalendarFeedTokenHash",
                unique: true,
                filter: "\"CalendarFeedTokenHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_staff_duties_import_job",
                schema: "qmgr",
                table: "StaffDuties",
                column: "ImportJobId",
                filter: "\"ImportJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_school_events_duty",
                schema: "qmgr",
                table: "SchoolEvents",
                column: "DutyId",
                filter: "\"DutyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_school_events_import_job",
                schema: "qmgr",
                table: "SchoolEvents",
                column: "ImportJobId",
                filter: "\"ImportJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_school_events_org_dates",
                schema: "qmgr",
                table: "SchoolEvents",
                columns: new[] { "OrganizationId", "StartsOn", "EndsOn" });

            migrationBuilder.CreateIndex(
                name: "idx_school_events_source_key",
                schema: "qmgr",
                table: "SchoolEvents",
                columns: new[] { "OrganizationId", "SourceKey" },
                filter: "\"SourceKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SchoolEvents_BranchId",
                schema: "qmgr",
                table: "SchoolEvents",
                column: "BranchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchoolEvents",
                schema: "qmgr");

            migrationBuilder.DropIndex(
                name: "idx_visitors_branch_entry_gate",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropIndex(
                name: "ux_users_calendar_feed_token",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropIndex(
                name: "idx_staff_duties_import_job",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "CheckedInByUserId",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "CheckedOutByUserId",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "EntryGate",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "ExitGate",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "CalendarFeedCreatedAt",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "CalendarFeedTokenHash",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ImportJobId",
                schema: "qmgr",
                table: "StaffDuties");
        }
    }
}
