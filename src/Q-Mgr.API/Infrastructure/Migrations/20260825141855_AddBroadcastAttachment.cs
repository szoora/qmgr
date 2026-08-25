using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBroadcastAttachment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttachmentFileName",
                schema: "qmgr",
                table: "broadcasts",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentFilePath",
                schema: "qmgr",
                table: "broadcasts",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AttachmentFileSizeBytes",
                schema: "qmgr",
                table: "broadcasts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentMimeType",
                schema: "qmgr",
                table: "broadcasts",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentUrl",
                schema: "qmgr",
                table: "broadcasts",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttachmentFileName",
                schema: "qmgr",
                table: "broadcasts");

            migrationBuilder.DropColumn(
                name: "AttachmentFilePath",
                schema: "qmgr",
                table: "broadcasts");

            migrationBuilder.DropColumn(
                name: "AttachmentFileSizeBytes",
                schema: "qmgr",
                table: "broadcasts");

            migrationBuilder.DropColumn(
                name: "AttachmentMimeType",
                schema: "qmgr",
                table: "broadcasts");

            migrationBuilder.DropColumn(
                name: "AttachmentUrl",
                schema: "qmgr",
                table: "broadcasts");
        }
    }
}
