using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_modules",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrialEndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BillingCycle = table.Column<int>(type: "integer", nullable: false),
                    CurrentPeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StripeSubscriptionItemId = table.Column<string>(type: "text", nullable: true),
                    GrantedByPlatformAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    AdminNote = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_modules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_organization_modules_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_organization_modules_subscription_plans_ModuleId",
                        column: x => x.ModuleId,
                        principalSchema: "qmgr",
                        principalTable: "subscription_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_organization_modules_ModuleId",
                schema: "qmgr",
                table: "organization_modules",
                column: "ModuleId");

            migrationBuilder.CreateIndex(
                name: "IX_organization_modules_OrganizationId_ModuleId",
                schema: "qmgr",
                table: "organization_modules",
                columns: new[] { "OrganizationId", "ModuleId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_modules",
                schema: "qmgr");
        }
    }
}
