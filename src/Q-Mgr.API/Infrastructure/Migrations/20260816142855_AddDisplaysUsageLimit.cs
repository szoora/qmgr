using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplaysUsageLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxDisplaysOverride",
                schema: "qmgr",
                table: "subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxDisplays",
                schema: "qmgr",
                table: "subscription_plans",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxDisplaysOverride",
                schema: "qmgr",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "MaxDisplays",
                schema: "qmgr",
                table: "subscription_plans");
        }
    }
}
