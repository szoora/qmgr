using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MinutesApprovedAt",
                schema: "qmgr",
                table: "StaffDuties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MinutesApprovedAtDutyId",
                schema: "qmgr",
                table: "StaffDuties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MinutesApprovedByUserId",
                schema: "qmgr",
                table: "StaffDuties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MinutesCirculatedAt",
                schema: "qmgr",
                table: "StaffDuties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MinutesJson",
                schema: "qmgr",
                table: "StaffDuties",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinutesReminderStage",
                schema: "qmgr",
                table: "StaffDuties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinutesStatus",
                schema: "qmgr",
                table: "StaffDuties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "MinutesUpdatedAt",
                schema: "qmgr",
                table: "StaffDuties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MinutesUpdatedByUserId",
                schema: "qmgr",
                table: "StaffDuties",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StaffMinuteActions",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    DutyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReminderStage = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffMinuteActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffMinuteActions_StaffDuties_DutyId",
                        column: x => x.DutyId,
                        principalSchema: "qmgr",
                        principalTable: "StaffDuties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_staff_duties_minutes_status",
                schema: "qmgr",
                table: "StaffDuties",
                columns: new[] { "BranchId", "MinutesStatus" },
                filter: "\"MinutesStatus\" <> 0");

            migrationBuilder.CreateIndex(
                name: "idx_staff_minute_actions_assignee",
                schema: "qmgr",
                table: "StaffMinuteActions",
                columns: new[] { "AssignedUserId", "Status" },
                filter: "\"AssignedUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_staff_minute_actions_branch_due",
                schema: "qmgr",
                table: "StaffMinuteActions",
                columns: new[] { "BranchId", "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "idx_staff_minute_actions_duty",
                schema: "qmgr",
                table: "StaffMinuteActions",
                column: "DutyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffMinuteActions",
                schema: "qmgr");

            migrationBuilder.DropIndex(
                name: "idx_staff_duties_minutes_status",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "MinutesApprovedAt",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "MinutesApprovedAtDutyId",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "MinutesApprovedByUserId",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "MinutesCirculatedAt",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "MinutesJson",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "MinutesReminderStage",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "MinutesStatus",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "MinutesUpdatedAt",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "MinutesUpdatedByUserId",
                schema: "qmgr",
                table: "StaffDuties");
        }
    }
}
