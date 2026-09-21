using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfServiceRequestDedupeKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_staff_config_requests_open",
                schema: "qmgr",
                table: "StaffConfigRequests");

            migrationBuilder.AddColumn<string>(
                name: "DedupeKey",
                schema: "qmgr",
                table: "StaffConfigRequests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ux_staff_config_requests_open",
                schema: "qmgr",
                table: "StaffConfigRequests",
                columns: new[] { "BranchId", "RequestedByUserId", "DedupeKey" },
                unique: true,
                filter: "\"State\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_staff_config_requests_open",
                schema: "qmgr",
                table: "StaffConfigRequests");

            migrationBuilder.DropColumn(
                name: "DedupeKey",
                schema: "qmgr",
                table: "StaffConfigRequests");

            migrationBuilder.CreateIndex(
                name: "ux_staff_config_requests_open",
                schema: "qmgr",
                table: "StaffConfigRequests",
                columns: new[] { "BranchId", "RequestedByUserId", "Kind", "CycleDay", "PeriodKey", "ClassNameNormalized", "SubjectId" },
                unique: true,
                filter: "\"State\" = 0");
        }
    }
}
