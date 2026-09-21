using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

// =====================================================================================================
// MINUTES OF A MEETING — the DTO contract between Q-Mgr.API and Q-Mgr.Web.
//
// Built 2026-09-20, after the user asked what the industry standard for digital minutes is and what
// this project should do. Three bodies of practice, and the shape below is where they meet:
//
//   * ROBERT'S RULES OF ORDER decides the CONTENT. Minutes record what was DONE, not what was said:
//     who was present and absent, whether there was a quorum, the wording of each motion with its
//     mover and seconder, and how it was decided.
//   * OPEN-MEETING AND RECORDS LAW decides the LIFECYCLE. Minutes are a DRAFT until the body adopts
//     them by motion at its NEXT meeting; adoption is what makes them the official record, and a
//     court treats adopted minutes as conclusive evidence of what was decided. Hence
//     MinutesStatus and ApprovedAtDutyId — the meeting that adopted them, not just a date.
//   * ISO 15489 decides the QUALITIES: authentic, reliable, complete and unaltered, usable. Hence
//     the append-only corrections below (an adopted record is never edited in place, exactly as a
//     welfare record is never edited and a closed register is annulled rather than overwritten),
//     and the activity events the controller writes at each transition.
//
// The product shape — template from the agenda, motions and actions captured live, review, adoption,
// publish with a trail — is what board portals (Diligent Minutes, OnBoard, BoardEffect) sell. The two
// things they cannot do and this can: ATTENDANCE IS NOT RETYPED (it is the register that was already
// taken, so Present/Late/Excused already means present, late and apologies), and an ACTION IS A REAL
// TO-DO for a real user in the same system that will chase them.
//
// DELIBERATELY ABSENT: transcription or AI summarisation (needs a recording pipeline and a server
// dependency this project has ruled out) and e-signatures (adoption by motion at the next meeting IS
// the legal act; a signature would be decoration on top of it).
// =====================================================================================================

/// <summary>One answer to one section of the template. The key is a WIRE FORMAT: answers are stored under it.</summary>
public record MinutesSectionAnswerDto
{
    [MaxLength(40)] public string Key { get; set; } = string.Empty;
    [MaxLength(8000)] public string Body { get; set; } = string.Empty;
}

/// <summary>
/// A motion and how it was decided. Mover and seconder are free text rather than user ids on
/// purpose: a governor, a parent or a district officer can move a motion at a school meeting and
/// will never have a Q-Mgr login.
/// </summary>
public record MinutesDecisionDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(2000)] public string Text { get; set; } = string.Empty;
    [MaxLength(120)] public string? MovedBy { get; set; }
    [MaxLength(120)] public string? SecondedBy { get; set; }
    public MinutesDecisionOutcome Outcome { get; set; } = MinutesDecisionOutcome.Noted;
    /// <summary>Counts are optional: most school meetings agree by consensus and record no vote.</summary>
    public int? VotesFor { get; set; }
    public int? VotesAgainst { get; set; }
    public int? Abstentions { get; set; }
}

/// <summary>
/// A correction made AFTER adoption. Never an edit: the adopted text stands and the correction sits
/// beside it, with who made it and why — ISO 15489's integrity property in this codebase's existing
/// idiom (a welfare visibility change writes a note; a reopened register annuls and rewrites).
/// </summary>
public record MinutesCorrectionDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime At { get; set; }
    public Guid ByUserId { get; set; }
    [MaxLength(120)] public string ByName { get; set; } = string.Empty;
    [MaxLength(4000)] public string Text { get; set; } = string.Empty;
}

/// <summary>An action point. A ROW, not a line in the blob — see StaffMinuteAction for why.</summary>
public record MinuteActionDto
{
    public Guid Id { get; set; }
    public Guid DutyId { get; set; }
    [MaxLength(120)] public string DutyTitle { get; set; } = string.Empty;
    [MaxLength(1000)] public string Text { get; set; } = string.Empty;
    public Guid? AssignedUserId { get; set; }
    [MaxLength(120)] public string? AssignedName { get; set; }
    public DateTime? DueAt { get; set; }
    public MinuteActionStatus Status { get; set; }
    public DateTime? CompletedAt { get; set; }
    [MaxLength(120)] public string? CompletedByName { get; set; }
    [MaxLength(1000)] public string? CompletionNote { get; set; }
    /// <summary>Open, has a due date, and that date has passed. Derived — never stored, because it is only time passing.</summary>
    public bool IsOverdue { get; set; }
}

/// <summary>What the register already knows, so the minutes never ask anyone to retype it.</summary>
public record MinutesAttendanceDto
{
    public int Expected { get; set; }
    public int Present { get; set; }
    public int Late { get; set; }
    public int Absent { get; set; }
    /// <summary>Excused IS "apologies received" in the language of minutes.</summary>
    public int Apologies { get; set; }
    public int Unmarked { get; set; }
    public List<string> PresentNames { get; set; } = new();
    public List<string> AbsentNames { get; set; } = new();
    public List<string> ApologyNames { get; set; } = new();
    /// <summary>True when the register has been closed: until then these figures are provisional and the page says so.</summary>
    public bool RegisterClosed { get; set; }

    // ---- Quorum. Computed on read from the policy and the register; never stored. ----
    /// <summary>Null when the tenant does not track quorum (the default).</summary>
    public int? QuorumRequired { get; set; }
    /// <summary>Null when quorum is not tracked. Robert's Rules: without a quorum no business may be transacted.</summary>
    public bool? QuorumMet { get; set; }
}

/// <summary>The whole minutes record for one meeting.</summary>
public record DutyMinutesDto
{
    public Guid DutyId { get; set; }
    [MaxLength(200)] public string DutyTitle { get; set; } = string.Empty;
    [MaxLength(200)] public string? Location { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }

    public MinutesStatus Status { get; set; }
    public DateTime? CirculatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    [MaxLength(120)] public string? ApprovedByName { get; set; }
    /// <summary>The meeting that adopted them. The date alone does not say which meeting did it.</summary>
    public Guid? ApprovedAtDutyId { get; set; }
    [MaxLength(200)] public string? ApprovedAtDutyTitle { get; set; }
    public DateTime? UpdatedAt { get; set; }
    [MaxLength(120)] public string? UpdatedByName { get; set; }

    /// <summary>The sections to fill, from the tenant's template or the default one.</summary>
    public List<DutyReportSectionDto> Template { get; set; } = new();
    public List<MinutesSectionAnswerDto> Sections { get; set; } = new();
    public List<MinutesDecisionDto> Decisions { get; set; } = new();
    public List<MinuteActionDto> Actions { get; set; } = new();
    public List<MinutesCorrectionDto> Corrections { get; set; } = new();
    public MinutesAttendanceDto Attendance { get; set; } = new();

    /// <summary>The adopted PDF in the Library, attached automatically when they are approved.</summary>
    public Guid? MinutesMediaContentId { get; set; }
    public string? MinutesFileUrl { get; set; }

    // ---- For the caller ----
    public bool CanWrite { get; set; }
    public bool CanApprove { get; set; }
    /// <summary>Approved, or circulated and the caller was expected at the meeting.</summary>
    public bool CanRead { get; set; }
}

/// <summary>Saves the draft. Everything is replaced wholesale: the minutes ARE this document until adopted.</summary>
public record SaveMinutesRequest
{
    public List<MinutesSectionAnswerDto> Sections { get; set; } = new();
    public List<MinutesDecisionDto> Decisions { get; set; } = new();
    public List<SaveMinuteActionRequest> Actions { get; set; } = new();
}

public record SaveMinuteActionRequest
{
    /// <summary>Empty for a new action; an existing id updates that row so its reminder stage and history survive.</summary>
    public Guid? Id { get; set; }
    [Required, MaxLength(1000)] public string Text { get; set; } = string.Empty;
    public Guid? AssignedUserId { get; set; }
    public DateTime? DueAt { get; set; }
    public MinuteActionStatus Status { get; set; } = MinuteActionStatus.Open;
}

public record ApproveMinutesRequest
{
    /// <summary>The meeting adopting them. Optional: a small school adopts by circulation and names no meeting.</summary>
    public Guid? ApprovedAtDutyId { get; set; }
}

public record CorrectMinutesRequest
{
    [Required, MaxLength(4000)] public string Text { get; set; } = string.Empty;
}

public record CompleteMinuteActionRequest
{
    [MaxLength(1000)] public string? Note { get; set; }
}

/// <summary>Tenant settings for minutes, in the staff-performance policy blob. One reader, as always.</summary>
public record MinutesDefaultsDto
{
    /// <summary>
    /// Percentage of the expected attendance that constitutes a quorum. ZERO MEANS NOT TRACKED, which
    /// is the default: most school staff meetings do not have a constitutional quorum, and a page
    /// announcing "quorum not met" at every meeting would train people to ignore it.
    /// </summary>
    public int QuorumPercent { get; set; }

    /// <summary>Days after the meeting by which the draft should be circulated. Drives the chase.</summary>
    public int CirculateWithinDays { get; set; } = 3;

    /// <summary>Publish the adopted minutes to the Library automatically. On: the adopted PDF is the archival copy.</summary>
    public bool PublishOnApproval { get; set; } = true;
}

/// <summary>
/// The default minutes sections, in Shared so the API's policy service and the policy editor read
/// ONE copy — the same arrangement as <see cref="DutyReportTemplateDefaults"/>. The order is
/// Robert's standard order of business, trimmed to what a school staff meeting actually has.
/// </summary>
public static class MinutesTemplateDefaults
{
    public static IReadOnlyList<DutyReportSectionDto> Sections => new List<DutyReportSectionDto>
    {
        new() { Key = "agenda", Title = "Agenda", Hint = "What the meeting was called to consider.", Required = true },
        new() { Key = "previous", Title = "Minutes of the previous meeting", Hint = "Read, corrected and adopted — or deferred." },
        new() { Key = "matters", Title = "Matters arising", Hint = "What has happened since, on the actions from last time." },
        new() { Key = "discussion", Title = "Discussion", Hint = "The substance of each agenda item. Decisions go below, not here." },
        new() { Key = "aob", Title = "Any other business", Hint = "Raised from the floor." },
        new() { Key = "next", Title = "Next meeting", Hint = "Date, time and place if they were agreed." },
    };
}
