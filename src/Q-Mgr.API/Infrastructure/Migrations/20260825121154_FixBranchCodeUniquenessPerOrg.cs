using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixBranchCodeUniquenessPerOrg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_branches_code",
                schema: "qmgr",
                table: "branches");

            migrationBuilder.DropIndex(
                name: "IX_branches_OrganizationId",
                schema: "qmgr",
                table: "branches");

            migrationBuilder.CreateIndex(
                name: "idx_branches_org_code",
                schema: "qmgr",
                table: "branches",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_branches_org_code",
                schema: "qmgr",
                table: "branches");

            migrationBuilder.CreateIndex(
                name: "idx_branches_code",
                schema: "qmgr",
                table: "branches",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_branches_OrganizationId",
                schema: "qmgr",
                table: "branches",
                column: "OrganizationId");
        }
    }
}
