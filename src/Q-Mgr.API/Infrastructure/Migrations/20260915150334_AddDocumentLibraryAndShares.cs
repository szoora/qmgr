using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentLibraryAndShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsShareable",
                schema: "qmgr",
                table: "media_content",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                schema: "qmgr",
                table: "media_content",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedByUserId",
                schema: "qmgr",
                table: "media_content",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedFrom",
                schema: "qmgr",
                table: "media_content",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                schema: "qmgr",
                table: "media_content",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "document_shares",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaContentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlugHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PasscodeHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NotBefore = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MaxViews = table.Column<int>(type: "integer", nullable: true),
                    ViewCount = table.Column<int>(type: "integer", nullable: false),
                    AllowDownload = table.Column<bool>(type: "boolean", nullable: false),
                    RequireEmail = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedEmailsJson = table.Column<string>(type: "jsonb", nullable: true),
                    Watermark = table.Column<int>(type: "integer", nullable: false),
                    NotifyOnFirstOpen = table.Column<bool>(type: "boolean", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokeReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstOpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastOpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_shares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_shares_media_content_MediaContentId",
                        column: x => x.MediaContentId,
                        principalSchema: "qmgr",
                        principalTable: "media_content",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_share_events",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Page = table.Column<int>(type: "integer", nullable: true),
                    DwellSeconds = table.Column<int>(type: "integer", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_share_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_share_events_document_shares_ShareId",
                        column: x => x.ShareId,
                        principalSchema: "qmgr",
                        principalTable: "document_shares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_share_events_CreatedAt",
                schema: "qmgr",
                table: "document_share_events",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_document_share_events_ShareId_CreatedAt",
                schema: "qmgr",
                table: "document_share_events",
                columns: new[] { "ShareId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_document_shares_MediaContentId",
                schema: "qmgr",
                table: "document_shares",
                column: "MediaContentId");

            migrationBuilder.CreateIndex(
                name: "IX_document_shares_SlugHash",
                schema: "qmgr",
                table: "document_shares",
                column: "SlugHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_share_events",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "document_shares",
                schema: "qmgr");

            migrationBuilder.DropColumn(
                name: "IsShareable",
                schema: "qmgr",
                table: "media_content");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                schema: "qmgr",
                table: "media_content");

            migrationBuilder.DropColumn(
                name: "PublishedByUserId",
                schema: "qmgr",
                table: "media_content");

            migrationBuilder.DropColumn(
                name: "PublishedFrom",
                schema: "qmgr",
                table: "media_content");

            migrationBuilder.DropColumn(
                name: "Summary",
                schema: "qmgr",
                table: "media_content");
        }
    }
}
