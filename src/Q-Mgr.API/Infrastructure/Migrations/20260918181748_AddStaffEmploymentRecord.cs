using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffEmploymentRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DateOfBirth",
                schema: "qmgr",
                table: "users",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmergencyContactName",
                schema: "qmgr",
                table: "users",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmergencyContactPhone",
                schema: "qmgr",
                table: "users",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EmploymentEndDate",
                schema: "qmgr",
                table: "users",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EmploymentStartDate",
                schema: "qmgr",
                table: "users",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmploymentType",
                schema: "qmgr",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NationalId",
                schema: "qmgr",
                table: "users",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Qualification",
                schema: "qmgr",
                table: "users",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Sex",
                schema: "qmgr",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeachingRegistrationNumber",
                schema: "qmgr",
                table: "users",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "EmergencyContactName",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "EmergencyContactPhone",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "EmploymentEndDate",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "EmploymentStartDate",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "EmploymentType",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "NationalId",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Qualification",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Sex",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "TeachingRegistrationNumber",
                schema: "qmgr",
                table: "users");
        }
    }
}
