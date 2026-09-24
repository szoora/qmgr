using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// Three things, 2026-09-23:
    ///
    /// <para><b>Employment type becomes the school's own list.</b> <c>users.EmploymentType</c> held the
    /// <c>StaffEmploymentType</c> enum as an integer; it now holds the type's NAME. EF scaffolded this as an ALTER
    /// COLUMN from integer to varchar, which PostgreSQL performs with an implicit cast — every "Permanent" would have
    /// become the string "0", every "Contract" "1", and nothing anywhere would have said so. So it is ADD, BACKFILL,
    /// DROP, RENAME, the order CLAUDE.md requires for a field whose meaning changes (see
    /// AddClassTeachersAndWelfareVisibility and StaffGroupsAsVocabulary). Each stored value maps to exactly the name the
    /// reader seeds, so nothing reads differently afterwards.</para>
    ///
    /// <para><b>An exam-supervision series has a name and managers</b> — two columns on StaffDuties, carried on every
    /// slot of the series (the enhance-before-add rule; plan TIMETABLE_OWNERSHIP Phase 3).</para>
    ///
    /// <para><b>White-Label Plus says what it does.</b> Its catalogue text still sold removing "Powered by SACC
    /// Software", a line the sign-in pages stopped carrying on 2026-09-22. Rewritten only where the row still holds
    /// the text it shipped with: once an administrator has edited it in the Module Catalog, it is theirs.</para>
    /// </summary>
    public partial class ExamSeriesEmploymentTypesAndCatalogWording : Migration
    {
        private const string ShippedDescription =
            "Removes \"Powered by SACC Software\" from your sign-in pages, the Q-Mgr line from the app footer, and our name from the emails your organization sends. Requires white-label branding to be switched on.";
        private const string NewDescription =
            "Puts your organisation's name on the copyright line of your sign-in pages, the app footer and the emails your organisation sends, in place of ours. Requires white-label branding to be switched on.";

        private static string Sql(string s) => s.Replace("'", "''");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- Employment type: add, backfill, drop, rename ----------------------------------------------
            migrationBuilder.AddColumn<string>(
                name: "EmploymentTypeName",
                schema: "qmgr",
                table: "users",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE qmgr.users SET ""EmploymentTypeName"" = CASE ""EmploymentType""
    WHEN 0 THEN 'Permanent'
    WHEN 1 THEN 'Contract'
    WHEN 2 THEN 'Probation'
    WHEN 3 THEN 'Part time'
    WHEN 4 THEN 'Volunteer'
    WHEN 5 THEN 'Seconded'
    ELSE NULL END
 WHERE ""EmploymentType"" IS NOT NULL;");

            migrationBuilder.DropColumn(name: "EmploymentType", schema: "qmgr", table: "users");
            migrationBuilder.RenameColumn(name: "EmploymentTypeName", schema: "qmgr", table: "users", newName: "EmploymentType");

            // ---- Exam-supervision series --------------------------------------------------------------------
            migrationBuilder.AddColumn<Guid[]>(
                name: "SeriesManagerUserIds",
                schema: "qmgr",
                table: "StaffDuties",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<string>(
                name: "SeriesName",
                schema: "qmgr",
                table: "StaffDuties",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // ---- White-Label Plus wording ---------------------------------------------------------------------
            migrationBuilder.Sql($@"
UPDATE qmgr.subscription_plans SET ""Description"" = '{Sql(NewDescription)}'
 WHERE ""Code"" = 'white-label-plus' AND ""Description"" = '{Sql(ShippedDescription)}';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
UPDATE qmgr.subscription_plans SET ""Description"" = '{Sql(ShippedDescription)}'
 WHERE ""Code"" = 'white-label-plus' AND ""Description"" = '{Sql(NewDescription)}';");

            migrationBuilder.DropColumn(name: "SeriesManagerUserIds", schema: "qmgr", table: "StaffDuties");
            migrationBuilder.DropColumn(name: "SeriesName", schema: "qmgr", table: "StaffDuties");

            // Back to the enum. A name a school ADDED has no integer to go back to and becomes null — stated rather
            // than invented, the same stance the add-backfill-drop migrations before this one take.
            migrationBuilder.AddColumn<int>(name: "EmploymentTypeValue", schema: "qmgr", table: "users", type: "integer", nullable: true);
            migrationBuilder.Sql(@"
UPDATE qmgr.users SET ""EmploymentTypeValue"" = CASE upper(regexp_replace(""EmploymentType"", '[^A-Za-z0-9]', '', 'g'))
    WHEN 'PERMANENT' THEN 0
    WHEN 'CONTRACT' THEN 1
    WHEN 'PROBATION' THEN 2
    WHEN 'PARTTIME' THEN 3
    WHEN 'VOLUNTEER' THEN 4
    WHEN 'SECONDED' THEN 5
    ELSE NULL END
 WHERE ""EmploymentType"" IS NOT NULL;");
            migrationBuilder.DropColumn(name: "EmploymentType", schema: "qmgr", table: "users");
            migrationBuilder.RenameColumn(name: "EmploymentTypeValue", schema: "qmgr", table: "users", newName: "EmploymentType");
        }
    }
}
