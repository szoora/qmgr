using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentClassificationAndShareReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Classification",
                schema: "qmgr",
                table: "media_content",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationReason",
                schema: "qmgr",
                table: "media_content",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClassificationSetAt",
                schema: "qmgr",
                table: "media_content",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassificationSetByUserId",
                schema: "qmgr",
                table: "media_content",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiryWarningSentAt",
                schema: "qmgr",
                table: "document_shares",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                schema: "qmgr",
                table: "document_shares",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_content_OrganizationId_Classification",
                schema: "qmgr",
                table: "media_content",
                columns: new[] { "OrganizationId", "Classification" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_content_OrganizationId_Classification",
                schema: "qmgr",
                table: "media_content");

            migrationBuilder.DropColumn(
                name: "Classification",
                schema: "qmgr",
                table: "media_content");

            migrationBuilder.DropColumn(
                name: "ClassificationReason",
                schema: "qmgr",
                table: "media_content");

            migrationBuilder.DropColumn(
                name: "ClassificationSetAt",
                schema: "qmgr",
                table: "media_content");

            migrationBuilder.DropColumn(
                name: "ClassificationSetByUserId",
                schema: "qmgr",
                table: "media_content");

            migrationBuilder.DropColumn(
                name: "ExpiryWarningSentAt",
                schema: "qmgr",
                table: "document_shares");

            migrationBuilder.DropColumn(
                name: "Reason",
                schema: "qmgr",
                table: "document_shares");
        }
    }
}
