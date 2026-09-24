namespace QMgr.Application.DTOs;

/// <summary>
/// The posts a school holds that are neither a class nor a department (2026-09-24): the designated
/// safeguarding lead and their deputies, an acting head for a fixed period, and the house and
/// dormitory posts of a boarding school. Stored in <c>Organization.Settings["Leadership"]</c> and read
/// only through <c>LeadershipPosts</c> on the API; written only under <c>OrganizationSettingsLock</c>.
///
/// <para>Why one blob and not three tables: each is a handful of ids per school with no life of its own
/// beyond "who holds it now", which is the enhance-before-add rule. Houses are branch vocabulary, so a
/// house post carries its branch.</para>
/// </summary>
public sealed record LeadershipPostsDto
{
    /// <summary>The designated safeguarding lead (Keeping Children Safe in Education: a member of the senior leadership team).</summary>
    public Guid? SafeguardingLeadUserId { get; init; }

    /// <summary>Deputy safeguarding leads, trained to the same level and granted the same access. At most three.</summary>
    public List<Guid> DeputySafeguardingLeadUserIds { get; init; } = new();

    /// <summary>Somebody acting as head for a fixed period. Null when nobody is.</summary>
    public ActingHeadDto? ActingHead { get; init; }

    /// <summary>House and dormitory posts, per branch.</summary>
    public List<PastoralUnitPostDto> PastoralUnitPosts { get; init; } = new();
}

public sealed record ActingHeadDto
{
    public Guid UserId { get; init; }
    public DateOnly StartsOn { get; init; }
    /// <summary>Required. The grant lapses on its own after this day: nobody has to remember to take it back.</summary>
    public DateOnly EndsOn { get; init; }
    public string Reason { get; init; } = string.Empty;
    public Guid AppointedByUserId { get; init; }
    public DateTime AppointedAt { get; init; }

    /// <summary>True on the days the arrangement covers, inclusive.</summary>
    public bool IsActiveOn(DateOnly day) => day >= StartsOn && day <= EndsOn;
}

/// <summary>The unit a pastoral post covers, besides a class.</summary>
public enum PastoralUnitKind
{
    /// <summary>Matched against <c>Student.House</c>.</summary>
    House = 0,
    /// <summary>Matched against <c>Student.DormitoryOrStream</c>.</summary>
    Dormitory = 1,
}

public sealed record PastoralUnitPostDto
{
    public Guid BranchId { get; init; }
    public PastoralUnitKind Kind { get; init; }
    /// <summary>The house or dormitory NAME, as the branch's vocabulary spells it.</summary>
    public string Name { get; init; } = string.Empty;
    public Guid UserId { get; init; }
}

// ---- What the Web reads and writes -------------------------------------------------------------

public sealed record LeadershipPersonDto(Guid UserId, string FullName, string? RoleName);

/// <summary>Who holds each post, named. Readable by everybody in the school: KCSIE expects every member
/// of staff to know who the safeguarding lead is, and an acting head nobody knows about cannot act.</summary>
public sealed record LeadershipViewDto
{
    public LeadershipPersonDto? SafeguardingLead { get; init; }
    public List<LeadershipPersonDto> DeputySafeguardingLeads { get; init; } = new();
    public LeadershipPersonDto? ActingHead { get; init; }
    public DateOnly? ActingHeadStartsOn { get; init; }
    public DateOnly? ActingHeadEndsOn { get; init; }
    public string? ActingHeadReason { get; init; }
    /// <summary>True while the acting-head period covers today.</summary>
    public bool ActingHeadActive { get; init; }

    /// <summary>The caller may appoint the safeguarding leads (their ROLE holds everything the post grants).</summary>
    public bool CanAppointSafeguarding { get; init; }
    /// <summary>The caller may appoint an acting head.</summary>
    public bool CanAppointActingHead { get; init; }
    /// <summary>People who may hold either post, for the pickers. Empty unless the caller may appoint.</summary>
    public List<LeadershipPersonDto> SafeguardingCandidates { get; init; } = new();
    public List<LeadershipPersonDto> ActingHeadCandidates { get; init; } = new();
}

public sealed record UpdateSafeguardingLeadsRequest
{
    public Guid? LeadUserId { get; init; }
    public List<Guid> DeputyUserIds { get; init; } = new();
}

public sealed record UpdateActingHeadRequest
{
    /// <summary>Null ends the arrangement now.</summary>
    public Guid? UserId { get; init; }
    public DateOnly? StartsOn { get; init; }
    public DateOnly? EndsOn { get; init; }
    public string? Reason { get; init; }
}

public sealed record PastoralUnitPostViewDto(PastoralUnitKind Kind, string Name, Guid UserId, string FullName);

public sealed record PastoralUnitPostsViewDto
{
    public List<PastoralUnitPostViewDto> Posts { get; init; } = new();
    public List<string> Houses { get; init; } = new();
    public List<string> Dormitories { get; init; } = new();
}

public sealed record UpdatePastoralUnitPostsRequest
{
    public List<PastoralUnitPostInput> Posts { get; init; } = new();
}

public sealed record PastoralUnitPostInput(PastoralUnitKind Kind, string Name, Guid UserId);

// ---- The access review (R5) ---------------------------------------------------------------------

/// <summary>One person, and the access that matters most in a school's system.</summary>
public sealed record AccessReviewRowDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string SortName { get; init; } = string.Empty;
    public string? RoleName { get; init; }
    public bool RestrictedWelfare { get; init; }
    public bool ConfidentialWelfare { get; init; }
    public bool RestrictedStaff { get; init; }
    public bool ManagesAccounts { get; init; }
    public bool DesignsRoles { get; init; }
    public bool ManagesBilling { get; init; }
    /// <summary>Plain-language posts: "Class teacher of S4B", "Safeguarding lead", "Acting head to 30 Oct 2026".</summary>
    public List<string> Posts { get; init; } = new();
}

public sealed record AccessReviewDto
{
    public List<AccessReviewRowDto> Rows { get; init; } = new();
    public DateTime? LastReviewedAt { get; init; }
    public string? LastReviewedBy { get; init; }
    public string? LastReviewNote { get; init; }
    /// <summary>The caller may record a review (accounts plus the restricted rung: an Administrator, a Head Teacher or an acting head).</summary>
    public bool CanConfirm { get; init; }
}

public sealed record ConfirmAccessReviewRequest(string Note);
