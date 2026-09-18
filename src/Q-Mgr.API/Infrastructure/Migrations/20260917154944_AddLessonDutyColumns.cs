using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonDutyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClassName",
                schema: "qmgr",
                table: "StaffDuties",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecoversDutyId",
                schema: "qmgr",
                table: "StaffDuties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Room",
                schema: "qmgr",
                table: "StaffDuties",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubjectId",
                schema: "qmgr",
                table: "StaffDuties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TimetableLessonId",
                schema: "qmgr",
                table: "StaffDuties",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_staff_duties_recovers",
                schema: "qmgr",
                table: "StaffDuties",
                column: "RecoversDutyId",
                filter: "\"RecoversDutyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_staff_duties_lesson_start",
                schema: "qmgr",
                table: "StaffDuties",
                columns: new[] { "TimetableLessonId", "StartsAt" },
                unique: true,
                filter: "\"TimetableLessonId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_staff_duties_recovers",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropIndex(
                name: "ux_staff_duties_lesson_start",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "ClassName",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "RecoversDutyId",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "Room",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                schema: "qmgr",
                table: "StaffDuties");

            migrationBuilder.DropColumn(
                name: "TimetableLessonId",
                schema: "qmgr",
                table: "StaffDuties");
        }
    }
}
