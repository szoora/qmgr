using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Enums;

namespace QMgr.API.Application.Services;

/// <summary>
/// The one home for "is this person currently on the staff?", derived from the employment dates
/// rather than stored, so a status and the dates it came from can never disagree.
///
/// <para><b>Why a leaver is kept rather than deactivated.</b> Before the dates existed the only way
/// to retire somebody was to clear <c>IsActive</c>, which drops them out of
/// <see cref="StaffLookups.BranchStaff"/> entirely — and with it out of the directory, out of every
/// historical register they were marked on, out of appraisals they signed and out of duty reports
/// naming them. That is not a leaver, that is an erasure. An end date takes them out of the
/// FORWARD-looking sets (registers, scoring denominators, notice audiences, rota generation) while
/// every record about them survives.</para>
/// </summary>
public static class StaffEmployment
{
    /// <summary>Their status on <paramref name="on"/>, which defaults to today.</summary>
    public static StaffEmploymentStatus StatusOf(DateOnly? start, DateOnly? end, DateOnly? on = null)
    {
        var day = on ?? DateOnly.FromDateTime(DateTime.UtcNow);

        // An end date that has passed wins over everything: somebody re-employed later gets a new
        // start date and a cleared end date, not two overlapping engagements on one row.
        if (end is { } e && e < day) return StaffEmploymentStatus.Left;
        if (start is { } s && s > day) return StaffEmploymentStatus.NotStarted;
        return StaffEmploymentStatus.Active;
    }

    /// <summary>Their status today.</summary>
    public static StaffEmploymentStatus StatusOf(User user, DateOnly? on = null)
        => StatusOf(user.EmploymentStartDate, user.EmploymentEndDate, on);

    /// <summary>
    /// True when the person should appear in forward-looking staff sets on <paramref name="on"/>.
    /// Expressed as a plain predicate as well as the EF filter below so the two cannot drift.
    /// </summary>
    public static bool IsCurrent(DateOnly? start, DateOnly? end, DateOnly? on = null)
        => StatusOf(start, end, on) == StaffEmploymentStatus.Active;

    /// <summary>
    /// The EF-translatable form of <see cref="IsCurrent"/>, for use inside a query.
    /// Kept beside it deliberately: an expression EF can translate cannot call the method above,
    /// so the only protection against the two drifting is that they sit in the same file.
    /// </summary>
    public static System.Linq.Expressions.Expression<Func<User, bool>> CurrentOn(DateOnly day)
        => u => (u.EmploymentEndDate == null || u.EmploymentEndDate >= day)
             && (u.EmploymentStartDate == null || u.EmploymentStartDate <= day);
}
