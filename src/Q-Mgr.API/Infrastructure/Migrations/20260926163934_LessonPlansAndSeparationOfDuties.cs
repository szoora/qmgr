using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LessonPlansAndSeparationOfDuties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PlanReminderStage",
                schema: "qmgr",
                table: "StaffDuties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedByUserId",
                schema: "qmgr",
                table: "StaffAppraisals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TeachingPlans",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClassNames = table.Column<string[]>(type: "text[]", nullable: false),
                    ClassKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LessonDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DutyId = table.Column<Guid>(type: "uuid", nullable: true),
                    TimetableLessonId = table.Column<Guid>(type: "uuid", nullable: true),
                    SchemeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SchemeRowKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SectionsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    RowsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Stages = table.Column<int>(type: "integer", nullable: false, defaultValue: 2),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ForwardedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ForwardedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StageSkippedReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReturnedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReturnedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReturnReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    WaitingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TrailJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    Reflection = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReflectionAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FileUrl = table.Column<string>(type: "text", nullable: true),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    OriginalSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    FilePages = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    SupersedesId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReminderStage = table.Column<int>(type: "integer", nullable: false),
                    ReviewReminderStage = table.Column<int>(type: "integer", nullable: false),
                    LastRemindedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeachingPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeachingPlans_StaffDuties_DutyId",
                        column: x => x.DutyId,
                        principalSchema: "qmgr",
                        principalTable: "StaffDuties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TeachingPlans_Subjects_SubjectId",
                        column: x => x.SubjectId,
                        principalSchema: "qmgr",
                        principalTable: "Subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeachingPlans_TeachingPlans_SchemeId",
                        column: x => x.SchemeId,
                        principalSchema: "qmgr",
                        principalTable: "TeachingPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TeachingPlans_TeachingPlans_SupersedesId",
                        column: x => x.SupersedesId,
                        principalSchema: "qmgr",
                        principalTable: "TeachingPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "idx_teaching_plans_author_date",
                schema: "qmgr",
                table: "TeachingPlans",
                columns: new[] { "AuthorUserId", "LessonDate" });

            migrationBuilder.CreateIndex(
                name: "idx_teaching_plans_queue",
                schema: "qmgr",
                table: "TeachingPlans",
                columns: new[] { "BranchId", "Status", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "idx_teaching_plans_scheme_ref",
                schema: "qmgr",
                table: "TeachingPlans",
                column: "SchemeId",
                filter: "\"SchemeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TeachingPlans_DutyId",
                schema: "qmgr",
                table: "TeachingPlans",
                column: "DutyId");

            migrationBuilder.CreateIndex(
                name: "IX_TeachingPlans_SubjectId",
                schema: "qmgr",
                table: "TeachingPlans",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "ux_teaching_plans_client_request",
                schema: "qmgr",
                table: "TeachingPlans",
                columns: new[] { "OrganizationId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_teaching_plans_lesson",
                schema: "qmgr",
                table: "TeachingPlans",
                columns: new[] { "AuthorUserId", "DutyId" },
                unique: true,
                filter: "\"DutyId\" IS NOT NULL AND \"IsCurrent\" AND \"Status\" <> 5");

            migrationBuilder.CreateIndex(
                name: "ux_teaching_plans_open_revision",
                schema: "qmgr",
                table: "TeachingPlans",
                column: "SupersedesId",
                unique: true,
                filter: "\"SupersedesId\" IS NOT NULL AND NOT \"IsCurrent\" AND \"Status\" <> 5");

            migrationBuilder.CreateIndex(
                name: "ux_teaching_plans_scheme",
                schema: "qmgr",
                table: "TeachingPlans",
                columns: new[] { "BranchId", "AuthorUserId", "SubjectId", "ClassKey", "PeriodKey" },
                unique: true,
                filter: "\"Kind\" = 1 AND \"IsCurrent\" AND \"Status\" <> 5");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TeachingPlans",
                schema: "qmgr");

            migrationBuilder.DropColumn(
                name: "PlanReminderStage",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "ReviewedByUserId",
                schema: "qmgr",
                table: "StaffAppraisals");
        }
    }
}
