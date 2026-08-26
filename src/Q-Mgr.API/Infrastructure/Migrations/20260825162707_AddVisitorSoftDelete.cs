using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitorSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "qmgr",
                table: "visitors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedByUserId",
                schema: "qmgr",
                table: "visitors",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_visitors_branch_deleted",
                schema: "qmgr",
                table: "visitors",
                columns: new[] { "BranchId", "DeletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_visitors_branch_deleted",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                schema: "qmgr",
                table: "visitors");
        }
    }
}
