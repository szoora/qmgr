using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentWelfareLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WelfareCategories",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseType = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DefaultTier = table.Column<int>(type: "integer", nullable: false),
                    DefaultPoints = table.Column<int>(type: "integer", nullable: true),
                    Color = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WelfareCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WelfareCategories_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WelfareRecords",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseType = table.Column<int>(type: "integer", nullable: false),
                    Tier = table.Column<int>(type: "integer", nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Location = table.Column<string>(type: "text", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Confidential = table.Column<bool>(type: "boolean", nullable: false),
                    ReportedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WelfareRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WelfareRecords_WelfareCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "qmgr",
                        principalTable: "WelfareCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WelfareRecords_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WelfareRecords_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WelfareRecords_students_StudentId",
                        column: x => x.StudentId,
                        principalSchema: "qmgr",
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WelfareAttachments",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileUrl = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WelfareAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WelfareAttachments_WelfareRecords_RecordId",
                        column: x => x.RecordId,
                        principalSchema: "qmgr",
                        principalTable: "WelfareRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WelfareNotes",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WelfareNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WelfareNotes_WelfareRecords_RecordId",
                        column: x => x.RecordId,
                        principalSchema: "qmgr",
                        principalTable: "WelfareRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WelfareNotifications",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuardianVisitorProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    SentByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WelfareNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WelfareNotifications_WelfareRecords_RecordId",
                        column: x => x.RecordId,
                        principalSchema: "qmgr",
                        principalTable: "WelfareRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WelfareAttachments_RecordId",
                schema: "qmgr",
                table: "WelfareAttachments",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_WelfareCategories_OrganizationId",
                schema: "qmgr",
                table: "WelfareCategories",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WelfareNotes_RecordId",
                schema: "qmgr",
                table: "WelfareNotes",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_WelfareNotifications_RecordId",
                schema: "qmgr",
                table: "WelfareNotifications",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_WelfareRecords_BranchId",
                schema: "qmgr",
                table: "WelfareRecords",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_WelfareRecords_CategoryId",
                schema: "qmgr",
                table: "WelfareRecords",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_WelfareRecords_OrganizationId",
                schema: "qmgr",
                table: "WelfareRecords",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WelfareRecords_StudentId",
                schema: "qmgr",
                table: "WelfareRecords",
                column: "StudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WelfareAttachments",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "WelfareNotes",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "WelfareNotifications",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "WelfareRecords",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "WelfareCategories",
                schema: "qmgr");
        }
    }
}
