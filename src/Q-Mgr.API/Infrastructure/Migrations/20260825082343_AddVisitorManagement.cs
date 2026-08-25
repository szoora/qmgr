using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitorManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "visitors",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    BadgeCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FullName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Company = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    IdNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PhotoUrl = table.Column<string>(type: "text", nullable: true),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    HostUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    HostName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsWatchlisted = table.Column<bool>(type: "boolean", nullable: false),
                    WatchlistReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CheckedInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CheckedOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visitors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_visitors_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_visitors_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_visitors_users_HostUserId",
                        column: x => x.HostUserId,
                        principalSchema: "qmgr",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "idx_visitors_branch_badge",
                schema: "qmgr",
                table: "visitors",
                columns: new[] { "BranchId", "BadgeCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_visitors_branch_checkedin",
                schema: "qmgr",
                table: "visitors",
                columns: new[] { "BranchId", "CheckedInAt" });

            migrationBuilder.CreateIndex(
                name: "idx_visitors_branch_status",
                schema: "qmgr",
                table: "visitors",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_visitors_HostUserId",
                schema: "qmgr",
                table: "visitors",
                column: "HostUserId");

            migrationBuilder.CreateIndex(
                name: "IX_visitors_OrganizationId",
                schema: "qmgr",
                table: "visitors",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "visitors",
                schema: "qmgr");
        }
    }
}
