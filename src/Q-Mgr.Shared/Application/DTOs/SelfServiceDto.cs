using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

// =====================================================================================================
// STAFF SELF-SERVICE CONFIGURATION — the contract between Q-Mgr.API and Q-Mgr.Web.
//
// THE RULE THAT DECIDES WHAT MAY BE INSTANT. A live ClassTeacherAssignment with Role = SubjectTeacher
// is not merely a record of who teaches what: StudentScopeService reads it and grants
// StudentAccessTier.Teaching over every child in that class, and a Teaching-tier caller may file a
// welfare record about them. So declaring a class you teach IS A DATA-ACCESS GRANT, and it can never
// be self-approved however aggressive the validation around it.
//
// Claiming a free period for a class you ALREADY hold grants nothing new — the access came with the
// assignment — so that can be instant. Everything below is that one asymmetry, written down:
//
//   Tier 1  Declare  reaches nobody else         instant
//   Tier 2  Claim    collides, cannot widen      instant, but only what is provably free
//   Tier 3  Request  widens access, or collides  decided by somebody else, never by the asker
//
// EVERY REQUEST TYPE HERE IS SHAPED SO IT CANNOT EXPRESS WHAT THE CALLER MAY NOT DECIDE — the
// UpdateStaffContactRequest rule. A claim carries no teacher id, so it cannot be made on somebody
// else's behalf, and no timetable status, so it cannot reach a published version. The shape is the
// enforcement; the handler is only the second line.
// =====================================================================================================

/// <summary>
/// One teacher's teaching preferences, stored beside the unavailability list in
/// Branch.Settings[Timetable] and read through ITimetableSettingsService only.
///
/// NOT A NEW TABLE and not a new blob key: TimetableChecker already loads that blob to do its work, so
/// these cost no extra query, they are branch-scoped exactly as the timetable is, and they are written
/// under the BranchSettingsLock that every other writer of that column takes.
///
/// These are the timetabling literature's SOFT constraints. They never refuse a placement; they become
/// issues the timetable master weighs. That is the whole argument for collecting them — a preference is
/// cheap before the timetable is built and expensive afterwards.
/// </summary>
public record StaffTeachingPreferenceDto
{
    public Guid UserId { get; set; }

    /// <summary>Filled on read for a caller who may see names. Never trusted on write.</summary>
    public string? UserName { get; set; }

    /// <summary>Beyond this many lessons back to back the checker raises TooManyConsecutive.</summary>
    [Range(1, 12)] public int? MaxConsecutivePeriods { get; set; }

    /// <summary>A cycle day the teacher would rather keep light. 1-based; null for none.</summary>
    public int? PreferredLightCycleDay { get; set; }

    /// <summary>Rooms this teacher would rather teach in, best first. A preference, NEVER a reservation.</summary>
    public List<string> PreferredRooms { get; set; } = new();

    /// <summary>Stamped by the server, so a teacher cannot backdate their own declaration.</summary>
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>What a teacher sends to change their own declarations. It carries no user id: the caller IS the subject.</summary>
public record UpdateMyTeachingDeclarationsRequest
{
    /// <summary>Replaces the caller's own unavailability, and only theirs. Null leaves it untouched.</summary>
    public List<MyUnavailabilityLine>? Unavailability { get; set; }

    /// <summary>Replaces the caller's own preferences. Null leaves them untouched.</summary>
    public StaffTeachingPreferenceDto? Preferences { get; set; }
}

/// <summary>
/// One unavailability window as the OWNER states it. Deliberately NOT TeacherUnavailabilityDto: that
/// one carries a UserId, and a request shaped to carry a UserId is a request that can be pointed at
/// somebody else however carefully the handler is written.
/// </summary>
public record MyUnavailabilityLine
{
    [Range(1, 14)] public int CycleDay { get; set; } = 1;

    /// <summary>Null means the whole day.</summary>
    [MaxLength(20)] public string? PeriodKey { get; set; }

    public UnavailabilityReason Reason { get; set; } = UnavailabilityReason.Unspecified;

    /// <summary>Optional and short. The reason above is what the grid uses; this is for a decider only.</summary>
    [MaxLength(200)] public string? Note { get; set; }
}

/// <summary>
/// The cycle, slot by slot, as ONE PERSON may see it.
///
/// A teacher sees Free / Mine / Taken and nothing more: no colleague's name, no subject, no class
/// (user decision, 2026-09-21). A holder of timetable.manage sees the names too — the same single code
/// that gates building, checking and publishing, so there is no second rule to drift from the first.
/// </summary>
public record TimetableOpeningsDto
{
    public Guid TimetableId { get; set; }
    public string TimetableName { get; set; } = string.Empty;
    public TimetableStatus Status { get; set; }
    public int CycleDays { get; set; }

    /// <summary>The class and subject this grid was computed for.</summary>
    public string ClassName { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public string SubjectName { get; set; } = string.Empty;

    /// <summary>Periods a week the assignment plans, and how many are already placed.</summary>
    public int? PlannedPerWeek { get; set; }
    public int PlacedPerWeek { get; set; }

    /// <summary>True when the caller holds timetable.manage, so the held-by fields are filled.</summary>
    public bool ShowsNames { get; set; }

    /// <summary>False when the school has direct claims switched off; the grid is then read-only.</summary>
    public bool CanClaim { get; set; }

    /// <summary>Why not, in the reader's terms, when CanClaim is false.</summary>
    public string? CannotClaimReason { get; set; }

    /// <summary>
    /// WHAT THE CALLER BELIEVED WHEN THEY READ IT. A claim sends this back; the server re-reads under
    /// the lock and refuses a stale claim naming what moved, rather than applying it to a slot the
    /// teacher never saw. Without it a teacher clicks a free period and lands on one that filled while
    /// they were still reading the grid.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    public List<OpeningDayDto> Days { get; set; } = new();
}

public record OpeningDayDto
{
    public int CycleDay { get; set; }
    public string Label { get; set; } = string.Empty;
    public List<OpeningSlotDto> Slots { get; set; } = new();
}

public record OpeningSlotDto
{
    [MaxLength(20)] public string PeriodKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Start { get; set; } = string.Empty;
    public string End { get; set; } = string.Empty;
    public SlotState State { get; set; }

    /// <summary>The caller's own lesson in this slot, for a move or a release. Null otherwise.</summary>
    public Guid? MyLessonId { get; set; }

    /// <summary>The caller's own lesson's room, so a move can carry it across.</summary>
    public string? MyRoom { get; set; }

    /// <summary>Filled ONLY when ShowsNames is true on the grid.</summary>
    public string? HeldByName { get; set; }
    public string? HeldSubjectName { get; set; }
    public string? HeldClassName { get; set; }

    /// <summary>Rooms with nothing in them this period. A teacher may name one; it is still not a reservation.</summary>
    public List<string> FreeRooms { get; set; } = new();
}

/// <summary>
/// Place one lesson. NO TEACHER ID: the caller is the teacher, which is what makes it impossible to
/// claim on somebody else's behalf. NO TIMETABLE STATUS: only a Draft is ever written.
/// </summary>
public record ClaimSlotRequest
{
    [Range(1, 14)] public int CycleDay { get; set; }
    [Required, MaxLength(20)] public string PeriodKey { get; set; } = string.Empty;
    [Required, MaxLength(100)] public string ClassName { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    [MaxLength(60)] public string? Room { get; set; }

    /// <summary>The grid this was chosen from. A mismatch is refused with what changed.</summary>
    [Required] public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Moving rather than placing: the caller's own lesson to vacate in the same act.</summary>
    public Guid? MoveFromLessonId { get; set; }
}

/// <summary>The answer to a claim. A refusal names the STEP, never just no.</summary>
public record ClaimResultDto
{
    public bool Ok { get; set; }
    public Guid? LessonId { get; set; }

    /// <summary>What to tell the reader. Null when Ok.</summary>
    public string? Refusal { get; set; }

    /// <summary>True when the grid had moved; the client re-reads rather than arguing.</summary>
    public bool Stale { get; set; }

    /// <summary>Set when a request would resolve the refusal — "already taken, ask for a swap".</summary>
    public ConfigRequestKind? OfferRequest { get; set; }

    /// <summary>The grid as it is NOW, so a refusal costs no extra round trip.</summary>
    public TimetableOpeningsDto? Openings { get; set; }
}

/// <summary>
/// One request, whatever it asks for. ONE QUEUE and one decider — a holder of timetable.manage (user
/// decision, 2026-09-21) — because a queue only one role can clear is a queue that sits.
///
/// The DIRECT write paths do not move: the class-teachers endpoint keeps classes.teachers.manage and
/// the lessons endpoint keeps timetable.manage. What this changes is who may approve somebody else's
/// request, never who may write directly.
/// </summary>
public record StaffConfigRequestDto
{
    public Guid Id { get; set; }
    public ConfigRequestKind Kind { get; set; }
    public ConfigRequestState State { get; set; }

    public Guid RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }

    /// <summary>The asker's own words. Shown to the decider; it is why the request exists.</summary>
    [MaxLength(500)] public string? Reason { get; set; }

    /// <summary>A sentence a person can read without opening the payload.</summary>
    public string Summary { get; set; } = string.Empty;

    public int? CycleDay { get; set; }
    public string? PeriodKey { get; set; }
    public string? ClassName { get; set; }
    public Guid? SubjectId { get; set; }
    public string? SubjectName { get; set; }
    public string? Room { get; set; }
    public int? PeriodsPerWeek { get; set; }

    /// <summary>The colleague whose lesson a swap would move, and whether they have agreed.</summary>
    public Guid? CounterpartUserId { get; set; }
    public string? CounterpartName { get; set; }
    public bool? CounterpartAgreed { get; set; }

    public Guid? DecidedByUserId { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAt { get; set; }
    [MaxLength(500)] public string? DecisionReason { get; set; }

    /// <summary>True when the reader may decide it — AND is not the one who asked.</summary>
    public bool CanIDecide { get; set; }

    /// <summary>True when the reader may take it back.</summary>
    public bool CanIWithdraw { get; set; }
}

/// <summary>
/// What a teacher sends. NO REQUESTER ID and no state: the caller is the asker and the state is always
/// Pending. The shape is why this cannot file a request in somebody else's name.
/// </summary>
public record CreateConfigRequestRequest
{
    public ConfigRequestKind Kind { get; set; }
    [MaxLength(500)] public string? Reason { get; set; }

    [Range(1, 14)] public int? CycleDay { get; set; }
    [MaxLength(20)] public string? PeriodKey { get; set; }
    [MaxLength(60)] public string? Room { get; set; }

    /// <summary>SlotSwap: the caller's own lesson being offered.</summary>
    public Guid? MyLessonId { get; set; }

    /// <summary>SlotSwap: the colleague's lesson being asked for.</summary>
    public Guid? TheirLessonId { get; set; }

    [MaxLength(100)] public string? ClassName { get; set; }
    public Guid? SubjectId { get; set; }

    /// <summary>ClassAssignment: how many periods a week the teacher expects to need.</summary>
    [Range(1, 60)] public int? PeriodsPerWeek { get; set; }
}

/// <summary>
/// A decision. The reason is REQUIRED on a refusal and optional on an approval: a refusal with no
/// reason trains people to stop asking and go back to the corridor, which is the workload this whole
/// feature exists to remove.
/// </summary>
public record DecideConfigRequestRequest
{
    public bool Approve { get; set; }
    [MaxLength(500)] public string? Reason { get; set; }
}

/// <summary>
/// The school's dials. Kept in Organization.Settings[StaffPerformance] and read through
/// IStaffPerformancePolicyService only — the one reader that already takes the organization lock.
/// NOT a fourth writer of Branch.Settings, which has three and a standing warning about a fourth.
///
/// OFF BY DEFAULT, the same call as StaffOnboardingPolicyDto.JoinEnabled: a school holding
/// safeguarding records turns a capability on deliberately, it does not discover it is already on.
/// </summary>
public record SelfServicePolicyDto
{
    /// <summary>The master switch. False means My Workspace shows none of this at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Tier 1. Safe on its own: a declaration reaches nobody else.</summary>
    public bool AllowDeclarations { get; set; } = true;

    /// <summary>
    /// Tier 2. When false a teacher may still READ the grid and ASK — every placement becomes a
    /// request. A school that wants the workload gone but the control kept runs exactly this.
    /// </summary>
    public bool AllowDirectClaims { get; set; }

    /// <summary>Tier 3 is never switched off: it is the safe path, and closing it sends people back to email.</summary>
    public bool AllowRequests { get; set; } = true;

    /// <summary>A teacher may not place more than their assignment plans.</summary>
    public bool ClaimsCappedByPlannedLoad { get; set; } = true;

    /// <summary>Stops one person filling the settings blob. 0 uses the default.</summary>
    [Range(0, 200)] public int MaxUnavailabilityLinesPerTeacher { get; set; }
}
