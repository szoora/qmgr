using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// The staff group stops being a three-value enum and becomes a tenant vocabulary.
    ///
    /// <para><b>ADD, BACKFILL, THEN DROP — in that order.</b> EF scaffolded this as
    /// <c>DropColumn("AppliesTo")</c> followed by <c>AddColumn("AppliesToGroup")</c>, which would have
    /// silently reset every performance parameter to "applies to all staff": a Lesson Observation
    /// would have started applying to the bursar, and nothing anywhere would have said it had
    /// happened. It also scaffolded an <c>AlterColumn</c> from <c>integer</c> to
    /// <c>varchar</c> on the notices column, which PostgreSQL refuses without a USING clause. Same
    /// class as <c>20260909165244_AddClassTeachersAndWelfareVisibility</c>, whose header carries the
    /// rule: a migration that converts a meaningful field adds, backfills, then drops.</para>
    ///
    /// <para>The mapping is exactly what the old code resolved to, so no score moves:
    /// <c>0 = AllStaff → NULL</c> (the absence of a restriction, which is what "everybody" is now),
    /// <c>1 = TeachingStaff → 'Teaching staff'</c>, <c>2 = SupportStaff → 'Support staff'</c>. And
    /// <c>roles.StaffGroup</c> is filled from the ONE test the old code used —
    /// <c>Code = 'support-staff'</c> — so the first read after this deploy answers exactly what the
    /// last read before it did.</para>
    ///
    /// <para><b>Down is honest about what it cannot restore.</b> A tenant that has since added
    /// Boarding or Ancillary has parameters naming a group no enum member represents; those come
    /// back as 0 (all staff) rather than being guessed at, and the comment says so rather than
    /// pretending the conversion is lossless.</para>
    /// </summary>
    public partial class StaffGroupsAsVocabulary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- 1. ADD --------------------------------------------------------------------------

            migrationBuilder.AddColumn<string>(
                name: "StaffGroup",
                schema: "qmgr",
                table: "roles",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppliesToGroup",
                schema: "qmgr",
                table: "PerformanceParameters",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudienceStaffGroupName",
                schema: "qmgr",
                table: "StaffNotices",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            // ---- 2. BACKFILL ---------------------------------------------------------------------
            //
            // Schema-qualified explicitly: raw SQL does NOT inherit HasDefaultSchema("qmgr"), and an
            // unqualified name is resolved against the connection's search_path, which for this
            // database has none of these tables. That is a hard failure, not a silent one — see the
            // TokenRepository note in CLAUDE.md.

            // The role's group, from the single test the old GroupFor used. Every other role — every
            // custom one a school created, and admin, manager and viewer — resolved to teaching,
            // which is exactly the defect this replaces; it is preserved here rather than corrected,
            // because a migration must reproduce today's behaviour and a school corrects its own
            // roles in the role editor afterwards.
            migrationBuilder.Sql(@"
                UPDATE qmgr.roles
                SET ""StaffGroup"" = CASE WHEN lower(""Code"") = 'support-staff'
                                        THEN 'Support staff' ELSE 'Teaching staff' END;");

            migrationBuilder.Sql(@"
                UPDATE qmgr.""PerformanceParameters""
                SET ""AppliesToGroup"" = CASE ""AppliesTo""
                                            WHEN 1 THEN 'Teaching staff'
                                            WHEN 2 THEN 'Support staff'
                                            ELSE NULL
                                        END;");

            migrationBuilder.Sql(@"
                UPDATE qmgr.""StaffNotices""
                SET ""AudienceStaffGroupName"" = CASE ""AudienceStaffGroup""
                                                    WHEN 1 THEN 'Teaching staff'
                                                    WHEN 2 THEN 'Support staff'
                                                    ELSE NULL
                                                END;");

            // ---- 3. DROP, and only now ------------------------------------------------------------

            migrationBuilder.DropColumn(
                name: "AppliesTo",
                schema: "qmgr",
                table: "PerformanceParameters");

            migrationBuilder.DropColumn(
                name: "AudienceStaffGroup",
                schema: "qmgr",
                table: "StaffNotices");

            migrationBuilder.RenameColumn(
                name: "AudienceStaffGroupName",
                schema: "qmgr",
                table: "StaffNotices",
                newName: "AudienceStaffGroup");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Add the integer columns back, map what maps, and leave the rest at 0 (all staff). A
            // tenant that added its own groups after this shipped has parameters naming one no enum
            // member represents; guessing which of three buckets they belonged to would be worse than
            // widening them, which is at least visible on the parameters page.
            migrationBuilder.AddColumn<int>(
                name: "AppliesTo",
                schema: "qmgr",
                table: "PerformanceParameters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AudienceStaffGroupInt",
                schema: "qmgr",
                table: "StaffNotices",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(@"
                UPDATE qmgr.""PerformanceParameters""
                SET ""AppliesTo"" = CASE
                        WHEN ""AppliesToGroup"" = 'Teaching staff' THEN 1
                        WHEN ""AppliesToGroup"" = 'Support staff' THEN 2
                        ELSE 0 END;");

            migrationBuilder.Sql(@"
                UPDATE qmgr.""StaffNotices""
                SET ""AudienceStaffGroupInt"" = CASE
                        WHEN ""AudienceStaffGroup"" = 'Teaching staff' THEN 1
                        WHEN ""AudienceStaffGroup"" = 'Support staff' THEN 2
                        ELSE NULL END;");

            migrationBuilder.DropColumn(
                name: "AudienceStaffGroup",
                schema: "qmgr",
                table: "StaffNotices");

            migrationBuilder.RenameColumn(
                name: "AudienceStaffGroupInt",
                schema: "qmgr",
                table: "StaffNotices",
                newName: "AudienceStaffGroup");

            migrationBuilder.DropColumn(
                name: "AppliesToGroup",
                schema: "qmgr",
                table: "PerformanceParameters");

            migrationBuilder.DropColumn(
                name: "StaffGroup",
                schema: "qmgr",
                table: "roles");
        }
    }
}
