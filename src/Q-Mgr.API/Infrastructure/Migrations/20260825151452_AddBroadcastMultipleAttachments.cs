using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBroadcastMultipleAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.CreateTable(
                name: "broadcast_attachments",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BroadcastId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broadcast_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_broadcast_attachments_broadcasts_BroadcastId",
                        column: x => x.BroadcastId,
                        principalSchema: "qmgr",
                        principalTable: "broadcasts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_broadcast_attachments_broadcast",
                schema: "qmgr",
                table: "broadcast_attachments",
                column: "BroadcastId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "broadcast_attachments",
                schema: "qmgr");

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
    }
}
