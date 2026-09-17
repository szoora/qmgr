using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubjectsAndTieredAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                schema: "qmgr",
                table: "StaffDuties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReminderStage",
                schema: "qmgr",
                table: "StaffDuties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PeriodsPerWeek",
                schema: "qmgr",
                table: "ClassTeacherAssignments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubjectId",
                schema: "qmgr",
                table: "ClassTeacherAssignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Subjects",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subjects_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "qmgr",
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subjects_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_staff_duties_kind_start",
                schema: "qmgr",
                table: "StaffDuties",
                columns: new[] { "Kind", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassTeacherAssignments_SubjectId",
                schema: "qmgr",
                table: "ClassTeacherAssignments",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "ux_subject_teacher_once_per_class_subject",
                schema: "qmgr",
                table: "ClassTeacherAssignments",
                columns: new[] { "BranchId", "ClassName", "UserId", "SubjectId" },
                unique: true,
                filter: "\"EndedAt\" IS NULL AND \"Role\" = 2");

            migrationBuilder.CreateIndex(
                name: "idx_subjects_department",
                schema: "qmgr",
                table: "Subjects",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "ux_subjects_org_code_active",
                schema: "qmgr",
                table: "Subjects",
                columns: new[] { "OrganizationId", "Code" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.AddForeignKey(
                name: "FK_ClassTeacherAssignments_Subjects_SubjectId",
                schema: "qmgr",
                table: "ClassTeacherAssignments",
                column: "SubjectId",
                principalSchema: "qmgr",
                principalTable: "Subjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClassTeacherAssignments_Subjects_SubjectId",
                schema: "qmgr",
                table: "ClassTeacherAssignments");

            migrationBuilder.DropTable(
                name: "Subjects",
                schema: "qmgr");

            migrationBuilder.DropIndex(
                name: "idx_staff_duties_kind_start",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropIndex(
                name: "IX_ClassTeacherAssignments_SubjectId",
                schema: "qmgr",
                table: "ClassTeacherAssignments");

            migrationBuilder.DropIndex(
                name: "ux_subject_teacher_once_per_class_subject",
                schema: "qmgr",
                table: "ClassTeacherAssignments");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "ReminderStage",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "PeriodsPerWeek",
                schema: "qmgr",
                table: "ClassTeacherAssignments");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                schema: "qmgr",
                table: "ClassTeacherAssignments");
        }
    }
}
