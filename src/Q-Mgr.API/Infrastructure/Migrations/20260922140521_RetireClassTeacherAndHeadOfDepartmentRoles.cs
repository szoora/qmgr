using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// Removes the <c>class-teacher</c> and <c>head-of-department</c> roles (plan §5 decision 3:
    /// <i>"what is the point in having them if they cannot be assigned? redundant features create
    /// more confusion"</i>).
    ///
    /// <para><b>Neither role ever worked on its own, which is why they go.</b> Each granted a row
    /// <i>scope</i> and none of the matching <i>permissions</i>: a class-teacher holder with no
    /// <c>ClassTeacherAssignment</c> saw no students at all, and one WITH an assignment was still
    /// refused by every welfare endpoint, because <c>GetUserPermissionsAsync</c> is one query,
    /// <c>Users → Role → RolePermissions</c>. The POST grants both halves now
    /// (<c>PostPermissionService</c>), so the roles have nothing left to do.</para>
    ///
    /// <para><b>REASSIGN BEFORE DELETE, and to a role whose SCOPE matches the post.</b> Holders move
    /// to <c>teacher</c> — <c>DataScope: AssignedClasses</c>, <c>StaffScope: SelfOnly</c> — never to
    /// <c>staff</c> or <c>support-staff</c>, which are <c>DataScope: Organization</c>. That is not a
    /// tidiness point: an organization-scoped role whose holder then picks up a derived welfare
    /// permission reads the WHOLE SCHOOL, because <c>StudentScopeService.ApplyAsync</c> begins
    /// <c>if (await IsUnscopedAsync()) return query;</c>. Plan §2.6 defect 1.</para>
    ///
    /// <para><b>Nobody loses access.</b> Every permission the two roles carried is now derived from
    /// the post the person actually holds, and the ranks did not move: <c>Rank</c> is relative and
    /// both codes sat below Manager, so no surviving role's <c>IsManagerOrAbove</c> answer changed.
    /// A holder with no post keeps exactly what <c>teacher</c> gives, which is what the deleted role
    /// effectively gave them anyway.</para>
    ///
    /// <para><b>Down cannot restore them and says so.</b> Re-creating the rows would not re-create
    /// the assignments that pointed at them, and the seeder no longer defines either. Down is a
    /// no-op rather than a lie.</para>
    /// </summary>
    public partial class RetireClassTeacherAndHeadOfDepartmentRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Schema-qualified: raw SQL does NOT inherit HasDefaultSchema("qmgr").
            //
            // Per organization, because roles are per organization for a tenant's own and null for
            // the seeded ones — the join finds the teacher role that belongs to the same
            // organization as the role being retired (both null for a seeded pair, which is the
            // normal case). A tenant that somehow has no teacher role is left alone by the join
            // rather than having its people orphaned onto a role id that does not exist.
            migrationBuilder.Sql(@"
                UPDATE qmgr.users u
                SET ""RoleId"" = t.""Id""
                FROM qmgr.roles old
                JOIN qmgr.roles t
                  ON lower(t.""Code"") = 'teacher'
                 AND t.""OrganizationId"" IS NOT DISTINCT FROM old.""OrganizationId""
                WHERE u.""RoleId"" = old.""Id""
                  AND lower(old.""Code"") IN ('class-teacher', 'head-of-department');");

            // The permission rows first: RolePermissions references Roles.
            migrationBuilder.Sql(@"
                DELETE FROM qmgr.role_permissions rp
                USING qmgr.roles r
                WHERE rp.""RoleId"" = r.""Id""
                  AND lower(r.""Code"") IN ('class-teacher', 'head-of-department');");

            // And only now the roles themselves. Guarded on nobody holding one, so a tenant whose
            // teacher role was missing keeps a role its people still point at rather than failing
            // the deploy on a foreign key.
            migrationBuilder.Sql(@"
                DELETE FROM qmgr.roles r
                WHERE lower(r.""Code"") IN ('class-teacher', 'head-of-department')
                  AND NOT EXISTS (SELECT 1 FROM qmgr.users u WHERE u.""RoleId"" = r.""Id"")
                  ;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Re-inserting two role rows would not put anybody back on them and
            // the seeder no longer defines either, so they would be rebuilt empty and then removed
            // again on the next start. An honest no-op beats a restore that restores nothing.
        }
    }
}
