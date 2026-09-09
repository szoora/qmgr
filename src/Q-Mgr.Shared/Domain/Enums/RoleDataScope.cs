namespace QMgr.Domain.Enums;

/// <summary>
/// How widely a role may see the rows inside a branch it already has permission to reach.
///
/// This is a SECOND axis to the permission table, not a replacement for it. A permission answers
/// "may this user read welfare records at all"; this answers "which students' welfare records".
/// Before this existed every welfare/roster query filtered on BranchId and nothing narrower, so
/// "welfare.view" could never mean "welfare.view for my own class" — which is exactly what a class
/// teacher needs and what the KCSIE need-to-know principle requires.
///
/// Deliberately on the ROLE rather than the USER: it is edited in the same place permissions are
/// edited, and a tenant can give a custom role (a head of year, a house parent) the same narrowing
/// without any new code.
/// </summary>
public enum RoleDataScope
{
    /// <summary>
    /// Sees every row in every branch the user's permissions and tenant already allow. The default,
    /// and the behaviour every role had before this field existed — so an unmigrated row reading 0
    /// keeps working exactly as it did.
    /// </summary>
    Organization = 0,

    /// <summary>
    /// Sees only students whose ClassName matches one of the caller's live ClassTeacherAssignment
    /// rows, and only records belonging to those students.
    ///
    /// FAILS CLOSED: a user with this scope and no assignments sees NOTHING, never the whole
    /// branch. The natural bug is an empty allow-list collapsing into a no-op WHERE clause, which
    /// would silently hand a newly-created class teacher the entire school roll — see
    /// StudentScopeService, where the empty case returns an explicitly empty queryable.
    /// </summary>
    AssignedClasses = 1
}
