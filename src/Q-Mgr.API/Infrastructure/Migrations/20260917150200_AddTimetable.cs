using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTimetable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Timetables",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CycleDays = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PublishedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: false),
                    ReportedIssueKeys = table.Column<string[]>(type: "text[]", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Timetables", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimetableLessons",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TimetableId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleDay = table.Column<int>(type: "integer", nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ClassName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ClassNameNormalized = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeacherUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Room = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    RoomNormalized = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimetableLessons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimetableLessons_Subjects_SubjectId",
                        column: x => x.SubjectId,
                        principalSchema: "qmgr",
                        principalTable: "Subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimetableLessons_Timetables_TimetableId",
                        column: x => x.TimetableId,
                        principalSchema: "qmgr",
                        principalTable: "Timetables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_timetable_lessons_group",
                schema: "qmgr",
                table: "TimetableLessons",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "idx_timetable_lessons_teacher",
                schema: "qmgr",
                table: "TimetableLessons",
                column: "TeacherUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TimetableLessons_SubjectId",
                schema: "qmgr",
                table: "TimetableLessons",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "ux_timetable_lessons_teacher_class_slot",
                schema: "qmgr",
                table: "TimetableLessons",
                columns: new[] { "TimetableId", "CycleDay", "PeriodKey", "TeacherUserId", "ClassNameNormalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_timetable_lessons_teacher_slot",
                schema: "qmgr",
                table: "TimetableLessons",
                columns: new[] { "TimetableId", "CycleDay", "PeriodKey", "TeacherUserId" },
                unique: true,
                filter: "\"GroupId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_timetables_branch_status",
                schema: "qmgr",
                table: "Timetables",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_timetables_branch_from_published",
                schema: "qmgr",
                table: "Timetables",
                columns: new[] { "BranchId", "EffectiveFrom" },
                unique: true,
                filter: "\"Status\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TimetableLessons",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "Timetables",
                schema: "qmgr");
        }
    }
}
