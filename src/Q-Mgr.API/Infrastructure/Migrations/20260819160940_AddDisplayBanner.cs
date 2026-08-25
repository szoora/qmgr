using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplayBanner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DisplayBannerEnabled",
                schema: "qmgr",
                table: "branch_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DisplayBannerSettingsJson",
                schema: "qmgr",
                table: "branch_settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayBannerEnabled",
                schema: "qmgr",
                table: "branch_settings");

            migrationBuilder.DropColumn(
                name: "DisplayBannerSettingsJson",
                schema: "qmgr",
                table: "branch_settings");
        }
    }
}
