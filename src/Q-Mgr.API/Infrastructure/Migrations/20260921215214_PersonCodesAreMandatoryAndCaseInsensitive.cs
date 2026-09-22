using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// A student's admission number and a member of staff's staff number become real keys: unique
    /// within the organisation IGNORING CASE, and normalised (trimmed, internal whitespace
    /// collapsed) so a stray space cannot make one person two.
    ///
    /// <para><b>Mandatory is enforced on the WRITE PATH, not with NOT NULL, and that is deliberate.</b>
    /// The column stays nullable because a school that has been running for weeks has people on it
    /// who were entered before the rule — 138 of 143 on the development tenant alone. A NOT NULL
    /// constraint could only be satisfied by inventing a number for each of them, and a fabricated
    /// staff number in a MoES return is worse than a blank one. So every endpoint and every form
    /// now requires one, and the rows that predate it keep their null until somebody supplies the
    /// real number. The Staff Coverage report is where they surface.</para>
    ///
    /// <para><b>The case-folding index is raw SQL because EF cannot model a functional index</b>, and
    /// citext is ruled out by the standing no-Postgres-extensions rule. <c>upper()</c> needs nothing
    /// installed. It must stay byte-for-byte in step with <c>PersonCode.Key</c>, or a duplicate the
    /// API accepts is refused by Postgres as a 500 with no field named.</para>
    /// </summary>
    public partial class PersonCodesAreMandatoryAndCaseInsensitive : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. NORMALISE FIRST. regexp_replace collapses every internal run of whitespace to one
            //    space; btrim removes the ends. A value that was only whitespace becomes NULL rather
            //    than '', so "missing" has exactly one representation from here on.
            migrationBuilder.Sql(@"
                UPDATE qmgr.students
                   SET ""StudentCode"" = NULLIF(btrim(regexp_replace(""StudentCode"", '\s+', ' ', 'g')), '')
                 WHERE ""StudentCode"" IS NOT NULL
                   AND ""StudentCode"" IS DISTINCT FROM NULLIF(btrim(regexp_replace(""StudentCode"", '\s+', ' ', 'g')), '');");

            migrationBuilder.Sql(@"
                UPDATE qmgr.users
                   SET ""EmployeeNumber"" = NULLIF(btrim(regexp_replace(""EmployeeNumber"", '\s+', ' ', 'g')), '')
                 WHERE ""EmployeeNumber"" IS NOT NULL
                   AND ""EmployeeNumber"" IS DISTINCT FROM NULLIF(btrim(regexp_replace(""EmployeeNumber"", '\s+', ' ', 'g')), '');");

            // 2. RESOLVE COLLISIONS THAT ONLY EXIST ONCE CASE IS FOLDED. The old index was an exact
            //    match, so "MH/S/001" and "mh/s/001" have been allowed to coexist and a unique index
            //    over upper() would simply FAIL TO BUILD on that data — taking the whole deploy with
            //    it, on a customer's server, at the worst possible moment.
            //
            //    The later row (by CreatedAt, then Id, so the result is deterministic) is suffixed
            //    rather than deleted or merged: two people wearing one number is the school's data
            //    problem to settle, not ours, and a suffixed code is visibly wrong in every list and
            //    every export, which is how somebody comes to fix it. Deleting a student, or silently
            //    pointing two records at one row, would destroy the evidence that it happened.
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT ""Id"",
                           ROW_NUMBER() OVER (PARTITION BY ""OrganizationId"", upper(""StudentCode"")
                                              ORDER BY ""CreatedAt"", ""Id"") AS n
                      FROM qmgr.students
                     WHERE ""StudentCode"" IS NOT NULL AND ""IsActive"" = true
                )
                UPDATE qmgr.students s
                   SET ""StudentCode"" = left(s.""StudentCode"" || ' (' || r.n || ')', 100)
                  FROM ranked r
                 WHERE s.""Id"" = r.""Id"" AND r.n > 1;");

            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT ""Id"",
                           ROW_NUMBER() OVER (PARTITION BY ""OrganizationId"", upper(""EmployeeNumber"")
                                              ORDER BY ""CreatedAt"", ""Id"") AS n
                      FROM qmgr.users
                     WHERE ""EmployeeNumber"" IS NOT NULL
                )
                UPDATE qmgr.users u
                   SET ""EmployeeNumber"" = left(u.""EmployeeNumber"" || ' (' || r.n || ')', 50)
                  FROM ranked r
                 WHERE u.""Id"" = r.""Id"" AND r.n > 1;");

            // 3. Swap the exact-match indexes for case-folding ones. Same names, same filters, so
            //    nothing else in the model has to know; only the expression changes.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS qmgr.idx_students_org_code_unique;");
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX idx_students_org_code_unique
                    ON qmgr.students (""OrganizationId"", upper(""StudentCode""))
                 WHERE ""StudentCode"" IS NOT NULL AND ""IsActive"" = true;");

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS qmgr.idx_users_employee_number_unique;");
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX idx_users_employee_number_unique
                    ON qmgr.users (""OrganizationId"", upper(""EmployeeNumber""))
                 WHERE ""EmployeeNumber"" IS NOT NULL;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The normalisation and the collision suffixes are NOT undone: the original values are
            // not recoverable from the new ones, and inventing them back would be worse than keeping
            // the corrected data. Only the indexes go back to their exact-match form.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS qmgr.idx_students_org_code_unique;");
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX idx_students_org_code_unique
                    ON qmgr.students (""OrganizationId"", ""StudentCode"")
                 WHERE ""StudentCode"" IS NOT NULL AND ""IsActive"" = true;");

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS qmgr.idx_users_employee_number_unique;");
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX idx_users_employee_number_unique
                    ON qmgr.users (""OrganizationId"", ""EmployeeNumber"")
                 WHERE ""EmployeeNumber"" IS NOT NULL;");
        }
    }
}
