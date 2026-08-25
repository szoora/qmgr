using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketingBroadcasts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "broadcasts",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MessageBody = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    AudienceTagFilter = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SendStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SendCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TotalRecipients = table.Column<int>(type: "integer", nullable: false),
                    SentCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broadcasts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_broadcasts_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_broadcasts_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "contacts",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    FullName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Tags = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    OptedOut = table.Column<bool>(type: "boolean", nullable: false),
                    OptedOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OptOutToken = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contacts_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_contacts_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "broadcast_recipients",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BroadcastId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broadcast_recipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_broadcast_recipients_broadcasts_BroadcastId",
                        column: x => x.BroadcastId,
                        principalSchema: "qmgr",
                        principalTable: "broadcasts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_broadcast_recipients_contacts_ContactId",
                        column: x => x.ContactId,
                        principalSchema: "qmgr",
                        principalTable: "contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_broadcast_recipients_status",
                schema: "qmgr",
                table: "broadcast_recipients",
                columns: new[] { "BroadcastId", "Status" });

            migrationBuilder.CreateIndex(
                name: "idx_broadcast_recipients_unique",
                schema: "qmgr",
                table: "broadcast_recipients",
                columns: new[] { "BroadcastId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_broadcast_recipients_ContactId",
                schema: "qmgr",
                table: "broadcast_recipients",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "idx_broadcasts_org_status",
                schema: "qmgr",
                table: "broadcasts",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "idx_broadcasts_status_scheduled",
                schema: "qmgr",
                table: "broadcasts",
                columns: new[] { "Status", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_broadcasts_BranchId",
                schema: "qmgr",
                table: "broadcasts",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "idx_contacts_optout_token",
                schema: "qmgr",
                table: "contacts",
                column: "OptOutToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_contacts_org_optedout",
                schema: "qmgr",
                table: "contacts",
                columns: new[] { "OrganizationId", "OptedOut" });

            migrationBuilder.CreateIndex(
                name: "IX_contacts_BranchId",
                schema: "qmgr",
                table: "contacts",
                column: "BranchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "broadcast_recipients",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "broadcasts",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "contacts",
                schema: "qmgr");
        }
    }
}
