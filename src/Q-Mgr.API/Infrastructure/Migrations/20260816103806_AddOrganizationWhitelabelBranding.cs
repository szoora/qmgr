using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationWhitelabelBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccentColor",
                schema: "qmgr",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FaviconUrl",
                schema: "qmgr",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrimaryColor",
                schema: "qmgr",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecondaryColor",
                schema: "qmgr",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WhitelabelEnabled",
                schema: "qmgr",
                table: "organizations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccentColor",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "FaviconUrl",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "PrimaryColor",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "SecondaryColor",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "WhitelabelEnabled",
                schema: "qmgr",
                table: "organizations");
        }
    }
}
