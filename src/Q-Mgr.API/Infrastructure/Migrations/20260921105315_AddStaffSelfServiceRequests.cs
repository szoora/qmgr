using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffSelfServiceRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StaffConfigRequests",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TimetableId = table.Column<Guid>(type: "uuid", nullable: true),
                    CycleDay = table.Column<int>(type: "integer", nullable: true),
                    PeriodKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ClassName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ClassNameNormalized = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Room = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    PeriodsPerWeek = table.Column<int>(type: "integer", nullable: true),
                    MyLessonId = table.Column<Guid>(type: "uuid", nullable: true),
                    TheirLessonId = table.Column<Guid>(type: "uuid", nullable: true),
                    CounterpartUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CounterpartAgreedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ResultLessonId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReminderStage = table.Column<int>(type: "integer", nullable: false),
                    LastRemindedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffConfigRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_staff_config_requests_branch_state",
                schema: "qmgr",
                table: "StaffConfigRequests",
                columns: new[] { "BranchId", "State" });

            migrationBuilder.CreateIndex(
                name: "idx_staff_config_requests_counterpart",
                schema: "qmgr",
                table: "StaffConfigRequests",
                columns: new[] { "CounterpartUserId", "State" },
                filter: "\"CounterpartUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_staff_config_requests_mine",
                schema: "qmgr",
                table: "StaffConfigRequests",
                columns: new[] { "RequestedByUserId", "State" });

            migrationBuilder.CreateIndex(
                name: "ux_staff_config_requests_open",
                schema: "qmgr",
                table: "StaffConfigRequests",
                columns: new[] { "BranchId", "RequestedByUserId", "Kind", "CycleDay", "PeriodKey", "ClassNameNormalized", "SubjectId" },
                unique: true,
                filter: "\"State\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffConfigRequests",
                schema: "qmgr");
        }
    }
}
