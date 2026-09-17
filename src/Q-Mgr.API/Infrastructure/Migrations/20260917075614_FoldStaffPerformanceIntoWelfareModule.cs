using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// Staff Performance stops being a module of its own and becomes part of Student Welfare, renamed
    /// "Welfare &amp; Performance" (user decision, 2026-09-17). Data only; no schema change.
    ///
    /// 1. The student-welfare catalog row takes the new name, description and limits — but only where
    ///    each field still holds the value it shipped with. The Module Catalog editor owns a row once an
    ///    administrator has changed it, and a migration must not silently undo that.
    /// 2. The staff-performance catalog row and any organization's hold on it are removed. The module was
    ///    never deployed, so the only holders are development and test tenants; a tenant that held it
    ///    keeps every staff feature through student-welfare, which is what now gates them.
    ///
    /// Down restores nothing: re-creating a separate module would need its grants back, and nobody ever
    /// bought it.
    /// </summary>
    public partial class FoldStaffPerformanceIntoWelfareModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE qmgr.subscription_plans SET ""Name"" = 'Welfare & Performance'
 WHERE ""Code"" = 'student-welfare' AND ""Name"" = 'Student Welfare';

UPDATE qmgr.subscription_plans
   SET ""Description"" = 'Student roster and guardians, visiting-day passes, and the welfare ledger; plus staff performance: duties and registers, recognition, scoring, termly appraisals, staff notices, the activity log and every staff member''s own portal.'
 WHERE ""Code"" = 'student-welfare'
   AND ""Description"" = 'Student roster and guardians, visiting-day passes, and the welfare ledger: achievements, behaviour, safeguarding concerns, assigned actions, statements and reports.';

-- Every staff member gets a login: the user cap that shipped with Student Welfare (10 a branch) would
-- refuse a school's staff list at the eleventh person. Raised only where it is still the default.
UPDATE qmgr.subscription_plans SET ""MaxUsersPerBranch"" = 250
 WHERE ""Code"" = 'student-welfare' AND ""MaxUsersPerBranch"" = 10;
UPDATE qmgr.subscription_plans SET ""MaxStorageMb"" = 5000
 WHERE ""Code"" = 'student-welfare' AND ""MaxStorageMb"" = 2000;

DELETE FROM qmgr.organization_modules
 WHERE ""ModuleId"" IN (SELECT ""Id"" FROM qmgr.subscription_plans WHERE ""Code"" = 'staff-performance');
DELETE FROM qmgr.subscription_plans WHERE ""Code"" = 'staff-performance';
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
