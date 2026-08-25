using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueFeedbackTokenIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_feedback_token",
                schema: "qmgr",
                table: "feedbacks");

            migrationBuilder.CreateIndex(
                name: "idx_feedback_token",
                schema: "qmgr",
                table: "feedbacks",
                column: "TokenId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_feedback_token",
                schema: "qmgr",
                table: "feedbacks");

            migrationBuilder.CreateIndex(
                name: "idx_feedback_token",
                schema: "qmgr",
                table: "feedbacks",
                column: "TokenId");
        }
    }
}
