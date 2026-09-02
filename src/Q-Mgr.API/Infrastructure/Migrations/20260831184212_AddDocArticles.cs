using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocArticles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "doc_articles",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BodyHtml = table.Column<string>(type: "text", nullable: false),
                    CoverImageUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Industry = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doc_articles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_doc_articles_slug",
                schema: "qmgr",
                table: "doc_articles",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_doc_articles_status_order",
                schema: "qmgr",
                table: "doc_articles",
                columns: new[] { "Status", "DisplayOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "doc_articles",
                schema: "qmgr");
        }
    }
}
