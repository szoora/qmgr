using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CampaignId",
                schema: "qmgr",
                table: "playlist_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "campaigns",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_campaigns_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "campaign_impressions",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaContentId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_impressions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_campaign_impressions_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "qmgr",
                        principalTable: "campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_campaign_impressions_media_content_MediaContentId",
                        column: x => x.MediaContentId,
                        principalSchema: "qmgr",
                        principalTable: "media_content",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_items_CampaignId",
                schema: "qmgr",
                table: "playlist_items",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "idx_campaign_impressions_campaign_time",
                schema: "qmgr",
                table: "campaign_impressions",
                columns: new[] { "CampaignId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_campaign_impressions_MediaContentId",
                schema: "qmgr",
                table: "campaign_impressions",
                column: "MediaContentId");

            migrationBuilder.CreateIndex(
                name: "idx_campaigns_branch_dates",
                schema: "qmgr",
                table: "campaigns",
                columns: new[] { "BranchId", "StartDate", "EndDate" });

            migrationBuilder.AddForeignKey(
                name: "FK_playlist_items_campaigns_CampaignId",
                schema: "qmgr",
                table: "playlist_items",
                column: "CampaignId",
                principalSchema: "qmgr",
                principalTable: "campaigns",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_playlist_items_campaigns_CampaignId",
                schema: "qmgr",
                table: "playlist_items");

            migrationBuilder.DropTable(
                name: "campaign_impressions",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "campaigns",
                schema: "qmgr");

            migrationBuilder.DropIndex(
                name: "IX_playlist_items_CampaignId",
                schema: "qmgr",
                table: "playlist_items");

            migrationBuilder.DropColumn(
                name: "CampaignId",
                schema: "qmgr",
                table: "playlist_items");
        }
    }
}
