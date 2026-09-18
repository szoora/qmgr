using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDutyReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StaffDutyReports",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    DutyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorRole = table.Column<int>(type: "integer", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SectionsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    LinkedWelfareRecordIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReminderStage = table.Column<int>(type: "integer", nullable: false),
                    LastReminderAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffDutyReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffDutyReports_StaffDuties_DutyId",
                        column: x => x.DutyId,
                        principalSchema: "qmgr",
                        principalTable: "StaffDuties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffDutyReportAttachments",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffDutyReportAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffDutyReportAttachments_StaffDutyReports_ReportId",
                        column: x => x.ReportId,
                        principalSchema: "qmgr",
                        principalTable: "StaffDutyReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffDutyReportNotes",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffDutyReportNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffDutyReportNotes_StaffDutyReports_ReportId",
                        column: x => x.ReportId,
                        principalSchema: "qmgr",
                        principalTable: "StaffDutyReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_staff_duty_report_attachments_file_url",
                schema: "qmgr",
                table: "StaffDutyReportAttachments",
                column: "FileUrl");

            migrationBuilder.CreateIndex(
                name: "idx_staff_duty_report_attachments_report",
                schema: "qmgr",
                table: "StaffDutyReportAttachments",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "idx_staff_duty_report_notes_report",
                schema: "qmgr",
                table: "StaffDutyReportNotes",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "idx_staff_duty_reports_author",
                schema: "qmgr",
                table: "StaffDutyReports",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "idx_staff_duty_reports_branch_due",
                schema: "qmgr",
                table: "StaffDutyReports",
                columns: new[] { "BranchId", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "ux_staff_duty_reports_author_period",
                schema: "qmgr",
                table: "StaffDutyReports",
                columns: new[] { "DutyId", "AuthorUserId", "PeriodStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffDutyReportAttachments",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "StaffDutyReportNotes",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "StaffDutyReports",
                schema: "qmgr");
        }
    }
}
