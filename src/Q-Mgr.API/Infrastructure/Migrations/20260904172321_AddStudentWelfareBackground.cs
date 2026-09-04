using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentWelfareBackground : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Antecedent",
                schema: "qmgr",
                table: "WelfareRecords",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PerceivedFunction",
                schema: "qmgr",
                table: "WelfareRecords",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResponseStage",
                schema: "qmgr",
                table: "WelfareRecords",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "AdmissionDate",
                schema: "qmgr",
                table: "students",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Allergies",
                schema: "qmgr",
                table: "students",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DateOfBirth",
                schema: "qmgr",
                table: "students",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisabilityOrLearningNeed",
                schema: "qmgr",
                table: "students",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DormitoryOrStream",
                schema: "qmgr",
                table: "students",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FeesStatus",
                schema: "qmgr",
                table: "students",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HomeAddress",
                schema: "qmgr",
                table: "students",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HomeDistrict",
                schema: "qmgr",
                table: "students",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HomeLanguage",
                schema: "qmgr",
                table: "students",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "House",
                schema: "qmgr",
                table: "students",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LivesWith",
                schema: "qmgr",
                table: "students",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MedicalConditions",
                schema: "qmgr",
                table: "students",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoUrl",
                schema: "qmgr",
                table: "students",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousSchool",
                schema: "qmgr",
                table: "students",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegularMedication",
                schema: "qmgr",
                table: "students",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Religion",
                schema: "qmgr",
                table: "students",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Residency",
                schema: "qmgr",
                table: "students",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Sex",
                schema: "qmgr",
                table: "students",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SponsorName",
                schema: "qmgr",
                table: "students",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TransportMode",
                schema: "qmgr",
                table: "students",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContactPriority",
                schema: "qmgr",
                table: "student_guardians",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContactRestriction",
                schema: "qmgr",
                table: "student_guardians",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "HasLegalCustody",
                schema: "qmgr",
                table: "student_guardians",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPrimaryContact",
                schema: "qmgr",
                table: "student_guardians",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "LivesWithStudent",
                schema: "qmgr",
                table: "student_guardians",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RestrictionReason",
                schema: "qmgr",
                table: "student_guardians",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StudentFlags",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tier = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RaisedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewDueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReminderSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentFlags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudentFlags_WelfareCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "qmgr",
                        principalTable: "WelfareCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentFlags_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentFlags_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentFlags_students_StudentId",
                        column: x => x.StudentId,
                        principalSchema: "qmgr",
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_students_branch_house",
                schema: "qmgr",
                table: "students",
                columns: new[] { "BranchId", "House" });

            migrationBuilder.CreateIndex(
                name: "idx_student_flags_review_due",
                schema: "qmgr",
                table: "StudentFlags",
                columns: new[] { "BranchId", "ReviewDueDate" },
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_student_flags_student_active",
                schema: "qmgr",
                table: "StudentFlags",
                columns: new[] { "StudentId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StudentFlags_CategoryId",
                schema: "qmgr",
                table: "StudentFlags",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentFlags_OrganizationId",
                schema: "qmgr",
                table: "StudentFlags",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudentFlags",
                schema: "qmgr");

            migrationBuilder.DropIndex(
                name: "idx_students_branch_house",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "Antecedent",
                schema: "qmgr",
                table: "WelfareRecords");

            migrationBuilder.DropColumn(
                name: "PerceivedFunction",
                schema: "qmgr",
                table: "WelfareRecords");

            migrationBuilder.DropColumn(
                name: "ResponseStage",
                schema: "qmgr",
                table: "WelfareRecords");

            migrationBuilder.DropColumn(
                name: "AdmissionDate",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "Allergies",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "DisabilityOrLearningNeed",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "DormitoryOrStream",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "FeesStatus",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "HomeAddress",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "HomeDistrict",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "HomeLanguage",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "House",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "LivesWith",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "MedicalConditions",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "PhotoUrl",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "PreviousSchool",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "RegularMedication",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "Religion",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "Residency",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "Sex",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "SponsorName",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "TransportMode",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "ContactPriority",
                schema: "qmgr",
                table: "student_guardians");

            migrationBuilder.DropColumn(
                name: "ContactRestriction",
                schema: "qmgr",
                table: "student_guardians");

            migrationBuilder.DropColumn(
                name: "HasLegalCustody",
                schema: "qmgr",
                table: "student_guardians");

            migrationBuilder.DropColumn(
                name: "IsPrimaryContact",
                schema: "qmgr",
                table: "student_guardians");

            migrationBuilder.DropColumn(
                name: "LivesWithStudent",
                schema: "qmgr",
                table: "student_guardians");

            migrationBuilder.DropColumn(
                name: "RestrictionReason",
                schema: "qmgr",
                table: "student_guardians");
        }
    }
}
