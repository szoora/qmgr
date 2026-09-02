using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWelfareWorkflowAndStatements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActionDueDate",
                schema: "qmgr",
                table: "WelfareRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActionTaken",
                schema: "qmgr",
                table: "WelfareRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid[]>(
                name: "AdditionalStudentIds",
                schema: "qmgr",
                table: "WelfareRecords",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedToUserId",
                schema: "qmgr",
                table: "WelfareRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributedToName",
                schema: "qmgr",
                table: "WelfareNotes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFinal",
                schema: "qmgr",
                table: "WelfareNotes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                schema: "qmgr",
                table: "WelfareNotes",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActionDueDate",
                schema: "qmgr",
                table: "WelfareRecords");

            migrationBuilder.DropColumn(
                name: "ActionTaken",
                schema: "qmgr",
                table: "WelfareRecords");

            migrationBuilder.DropColumn(
                name: "AdditionalStudentIds",
                schema: "qmgr",
                table: "WelfareRecords");

            migrationBuilder.DropColumn(
                name: "AssignedToUserId",
                schema: "qmgr",
                table: "WelfareRecords");

            migrationBuilder.DropColumn(
                name: "AttributedToName",
                schema: "qmgr",
                table: "WelfareNotes");

            migrationBuilder.DropColumn(
                name: "IsFinal",
                schema: "qmgr",
                table: "WelfareNotes");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "qmgr",
                table: "WelfareNotes");
        }
    }
}
