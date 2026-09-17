namespace QMgr.Domain.Enums;

/// <summary>
/// Which seat a staff member holds on a class. Both seats are notified and both are scoped
/// identically — the distinction is only who the roster and student profile name as "the" class
/// teacher, and that exactly one person can hold that seat at a time.
/// </summary>
public enum ClassTeacherRole
{
    /// <summary>
    /// The class teacher / form tutor. At most one live assignment of this kind per class, enforced
    /// by a partial unique index rather than a check in code — ending an assignment frees the seat
    /// with no soft-delete dance.
    /// </summary>
    ClassTeacher = 0,

    /// <summary>
    /// An assistant class teacher / co-tutor. Unlimited per class: a large stream may have two, and
    /// a school that wants cover during a term's absence should not have to end the substantive
    /// assignment to arrange it.
    /// </summary>
    Assistant = 1,

    /// <summary>
    /// A subject teacher of the class (duty rota plan §5.2), appended 2026-09-17. NOT a pastoral seat:
    /// it carries <c>SubjectId</c> and <c>PeriodsPerWeek</c>, gives the TEACHING tier of the student scope
    /// (name, photo, class — no guardians, welfare, discipline or pastoral fields), and is never notified
    /// by the welfare alert. Anything that means "the class teacher(s)" must filter to ClassTeacher and
    /// Assistant explicitly; <c>IStudentScopeService</c> is the one place that tells the tiers apart.
    /// </summary>
    SubjectTeacher = 2
}
