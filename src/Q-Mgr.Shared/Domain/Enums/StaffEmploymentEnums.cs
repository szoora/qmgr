namespace QMgr.Domain.Enums;

/// <summary>
/// How somebody is engaged. Nullable on the user — a tenant that does not track it leaves it unset
/// rather than being made to guess, the same stance <see cref="PersonSex"/> takes.
///
/// <para>The values are the ones a Ugandan school's MoES staff return actually distinguishes:
/// government-paid teachers, PTA/board-paid teachers and volunteers are counted separately, and
/// "probation" decides whether an appraisal cycle is a confirmation decision. A bank or clinic on
/// this same product reads them as ordinary employment types, which is why none of the names is
/// school-specific.</para>
/// </summary>
public enum StaffEmploymentType
{
    /// <summary>Open-ended appointment. A government-paid teacher on the payroll is this.</summary>
    Permanent = 0,

    /// <summary>Fixed-term. PTA- or board-paid staff are usually here.</summary>
    Contract = 1,

    /// <summary>Appointed but not yet confirmed — the appraisal at the end is a decision, not a review.</summary>
    Probation = 2,

    /// <summary>Engaged for part of a full load.</summary>
    PartTime = 3,

    /// <summary>Unpaid or stipend-only.</summary>
    Volunteer = 4,

    /// <summary>Posted in from another institution or ministry, still on their employer's books.</summary>
    Seconded = 5
}

/// <summary>
/// The status the directory and every staff query read, derived from the employment dates rather
/// than stored, so the two can never disagree. See <c>StaffEmployment.StatusOf</c>.
/// </summary>
public enum StaffEmploymentStatus
{
    /// <summary>Started, not ended.</summary>
    Active = 0,

    /// <summary>A start date in the future — the account exists, the person has not begun.</summary>
    NotStarted = 1,

    /// <summary>An end date in the past. The person is KEPT, not deleted: their records, appraisals
    /// and the registers they appear on are history that must survive their leaving.</summary>
    Left = 2
}
