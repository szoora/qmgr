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
    Assistant = 1
}
