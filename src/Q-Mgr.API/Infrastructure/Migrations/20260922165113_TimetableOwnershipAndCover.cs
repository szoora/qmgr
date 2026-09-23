using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TimetableOwnershipAndCover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "ManagerUserIds",
                schema: "qmgr",
                table: "Timetables",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<int>(
                name: "ReminderStage",
                schema: "qmgr",
                table: "Timetables",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CoverUserId",
                schema: "qmgr",
                table: "StaffConfigRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EffectiveOn",
                schema: "qmgr",
                table: "StaffConfigRequests",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TimetableLessonExceptions",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimetableId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimetableLessonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    CoverUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SourceRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimetableLessonExceptions", x => x.Id);
                    table.CheckConstraint("ck_timetable_lesson_exceptions_cover", "(\"Kind\" = 0 AND \"CoverUserId\" IS NOT NULL) OR (\"Kind\" <> 0 AND \"CoverUserId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_TimetableLessonExceptions_TimetableLessons_TimetableLessonId",
                        column: x => x.TimetableLessonId,
                        principalSchema: "qmgr",
                        principalTable: "TimetableLessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TimetableLessonExceptions_Timetables_TimetableId",
                        column: x => x.TimetableId,
                        principalSchema: "qmgr",
                        principalTable: "Timetables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_timetables_managers",
                schema: "qmgr",
                table: "Timetables",
                column: "ManagerUserIds")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "idx_timetable_lesson_exceptions_branch_date",
                schema: "qmgr",
                table: "TimetableLessonExceptions",
                columns: new[] { "BranchId", "Date" });

            migrationBuilder.CreateIndex(
                name: "idx_timetable_lesson_exceptions_cover_user",
                schema: "qmgr",
                table: "TimetableLessonExceptions",
                column: "CoverUserId",
                filter: "\"CoverUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TimetableLessonExceptions_TimetableId",
                schema: "qmgr",
                table: "TimetableLessonExceptions",
                column: "TimetableId");

            migrationBuilder.CreateIndex(
                name: "ux_timetable_lesson_exceptions_lesson_date",
                schema: "qmgr",
                table: "TimetableLessonExceptions",
                columns: new[] { "TimetableLessonId", "Date" },
                unique: true);

            // ---- Backfill -------------------------------------------------------------------------------------
            //
            // EVERY EXISTING VERSION IS APPOINTED TO WHOEVER CREATED IT. CreatedBy has been written since the
            // timetable shipped and never read for authorisation, so this is the closest thing to a record of who
            // the master was — and on an existing install it is what makes the feature useful on day one instead
            // of needing every version re-appointed by hand.
            //
            // IT GRANTS NOBODY ANY ACCESS THEY DID NOT ALREADY HAVE: creating a version required
            // timetable.manage, which already opened every version of every branch. What it changes is that
            // ANOTHER permission holder editing one of these now shows up as an override, which is the point.
            //
            // A draft with no creator recorded stays unowned, which is the pre-2026-09-22 behaviour and safe.
            migrationBuilder.Sql("""
                UPDATE qmgr."Timetables"
                SET "ManagerUserIds" = ARRAY["CreatedBy"]::uuid[]
                WHERE "CreatedBy" IS NOT NULL
                  AND ("ManagerUserIds" IS NULL OR cardinality("ManagerUserIds") = 0)
                  AND "Status" <> 2;
                """);

            // The self-service dedupe key gained two segments (the one-off date, and the colleague named on a
            // cover request). An open request written before this migration carries the six-segment form, so
            // without this an identical request filed afterwards would produce a different key and the partial
            // unique index would let both through — two rows for one thing, which a decider then has to
            // reconcile. Only OPEN rows matter: the index is partial on Pending.
            migrationBuilder.Sql("""
                UPDATE qmgr."StaffConfigRequests"
                SET "DedupeKey" = "DedupeKey" || '|-|-'
                WHERE "State" = 0
                  AND length("DedupeKey") - length(replace("DedupeKey", '|', '')) = 5;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TimetableLessonExceptions",
                schema: "qmgr");

            migrationBuilder.DropIndex(
                name: "idx_timetables_managers",
                schema: "qmgr",
                table: "Timetables");

            migrationBuilder.DropColumn(
                name: "ManagerUserIds",
                schema: "qmgr",
                table: "Timetables");

            migrationBuilder.DropColumn(
                name: "ReminderStage",
                schema: "qmgr",
                table: "Timetables");

            migrationBuilder.DropColumn(
                name: "CoverUserId",
                schema: "qmgr",
                table: "StaffConfigRequests");

            migrationBuilder.DropColumn(
                name: "EffectiveOn",
                schema: "qmgr",
                table: "StaffConfigRequests");
        }
    }
}
