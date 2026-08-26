using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentRosterAndImportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StudentId",
                schema: "qmgr",
                table: "visitors",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StudentName",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "roster_import_jobs",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TotalRows = table.Column<int>(type: "integer", nullable: false),
                    ProcessedRows = table.Column<int>(type: "integer", nullable: false),
                    CreatedCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedCount = table.Column<int>(type: "integer", nullable: false),
                    DuplicateCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roster_import_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_roster_import_jobs_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_roster_import_jobs_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "students",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StudentCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ClassName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_students", x => x.Id);
                    table.ForeignKey(
                        name: "FK_students_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_students_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roster_import_job_entries",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RosterImportJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowNumber = table.Column<int>(type: "integer", nullable: false),
                    StudentCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StudentName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    GuardianName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuardianProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roster_import_job_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_roster_import_job_entries_roster_import_jobs_RosterImportJo~",
                        column: x => x.RosterImportJobId,
                        principalSchema: "qmgr",
                        principalTable: "roster_import_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "student_guardians",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitorProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Relationship = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_guardians", x => x.Id);
                    table.ForeignKey(
                        name: "FK_student_guardians_students_StudentId",
                        column: x => x.StudentId,
                        principalSchema: "qmgr",
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_student_guardians_visitor_profiles_VisitorProfileId",
                        column: x => x.VisitorProfileId,
                        principalSchema: "qmgr",
                        principalTable: "visitor_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_visitors_StudentId",
                schema: "qmgr",
                table: "visitors",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "idx_roster_import_job_entries_job_row",
                schema: "qmgr",
                table: "roster_import_job_entries",
                columns: new[] { "RosterImportJobId", "RowNumber" });

            migrationBuilder.CreateIndex(
                name: "idx_roster_import_jobs_branch_created",
                schema: "qmgr",
                table: "roster_import_jobs",
                columns: new[] { "BranchId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_roster_import_jobs_OrganizationId",
                schema: "qmgr",
                table: "roster_import_jobs",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "idx_student_guardians_profile",
                schema: "qmgr",
                table: "student_guardians",
                column: "VisitorProfileId");

            migrationBuilder.CreateIndex(
                name: "idx_student_guardians_unique_pair",
                schema: "qmgr",
                table: "student_guardians",
                columns: new[] { "StudentId", "VisitorProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_students_branch_active",
                schema: "qmgr",
                table: "students",
                columns: new[] { "BranchId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "idx_students_org_code_unique",
                schema: "qmgr",
                table: "students",
                columns: new[] { "OrganizationId", "StudentCode" },
                unique: true,
                filter: "\"StudentCode\" IS NOT NULL AND \"IsActive\" = true");

            migrationBuilder.AddForeignKey(
                name: "FK_visitors_students_StudentId",
                schema: "qmgr",
                table: "visitors",
                column: "StudentId",
                principalSchema: "qmgr",
                principalTable: "students",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_visitors_students_StudentId",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropTable(
                name: "roster_import_job_entries",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "student_guardians",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "roster_import_jobs",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "students",
                schema: "qmgr");

            migrationBuilder.DropIndex(
                name: "IX_visitors_StudentId",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "StudentId",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "StudentName",
                schema: "qmgr",
                table: "visitors");
        }
    }
}
