using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

public record WelfareCategoryDto
{
    public Guid Id { get; init; }
    public WelfareCaseType CaseType { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public WelfareTier DefaultTier { get; init; }
    public int? DefaultPoints { get; init; }
    public string? Color { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }
}

// Mutable (not init-only) — bound directly as a Blazor form model via @bind, same reasoning as
// CreateStudentRequest/UpdateStudentRequest in RosterDto.cs.
public record CreateWelfareCategoryRequest
{
    public WelfareCaseType CaseType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public WelfareTier DefaultTier { get; set; } = WelfareTier.Low;
    public int? DefaultPoints { get; set; }
    public string? Color { get; set; }
    public int SortOrder { get; set; }
}

public record UpdateWelfareCategoryRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public WelfareTier DefaultTier { get; set; } = WelfareTier.Low;
    public int? DefaultPoints { get; set; }
    public string? Color { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public record WelfareAttachmentDto
{
    public Guid Id { get; init; }
    public string FileUrl { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record WelfareNoteDto
{
    public Guid Id { get; init; }
    public string Body { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public WelfareNoteKind Kind { get; init; } = WelfareNoteKind.Note;
    public bool IsFinal { get; init; }
    public string? AttributedToName { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record WelfareNotificationDto
{
    public Guid Id { get; init; }
    public string GuardianName { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string SentByName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

/// <summary>One row of a student's chronology — every category together, reverse-chronological (the CPOMS lesson: the pattern across categories is the point).</summary>
public record WelfareRecordDto
{
    public Guid Id { get; init; }
    public Guid StudentId { get; init; }
    public string StudentName { get; init; } = string.Empty;
    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public string? CategoryColor { get; init; }
    public WelfareCaseType CaseType { get; init; }
    public WelfareTier Tier { get; init; }
    public int? Points { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? Location { get; init; }
    public DateTime OccurredAt { get; init; }
    public WelfareStatus Status { get; init; }

    /// <summary>
    /// Standard / Confidential / Restricted. Replaced a <c>bool Confidential</c> on 2026-09-09.
    /// A record is only ever returned to a caller who holds the matching permission, so this is
    /// here to label the row in the UI, not to be trusted as a client-side filter.
    /// </summary>
    public WelfareVisibility Visibility { get; init; }

    /// <summary>Kept as a convenience for anything that only wants "is this above Standard" — derived, never stored, so it can never disagree with <see cref="Visibility"/>.</summary>
    public bool Confidential => Visibility != WelfareVisibility.Standard;

    public string ReportedByName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string? ActionTaken { get; init; }
    public Guid? AssignedToUserId { get; init; }
    public string? AssignedToName { get; init; }
    public DateTime? ActionDueDate { get; init; }

    /// <summary>Where the response sits on the graduated ladder — what makes "what did we try first" answerable.</summary>
    public WelfareResponseStage? ResponseStage { get; init; }

    public string? Antecedent { get; init; }
    public WelfarePerceivedFunction? PerceivedFunction { get; init; }
    public List<Guid> AdditionalStudentIds { get; init; } = new();
    public List<string> AdditionalStudentNames { get; init; } = new();
    public List<WelfareAttachmentDto> Attachments { get; init; } = new();
    public List<WelfareNoteDto> Notes { get; init; } = new();
    public List<WelfareNotificationDto> Notifications { get; init; } = new();
}

// Mutable — bound as a Blazor EditForm model.
public record CreateWelfareRecordRequest
{
    public Guid StudentId { get; set; }
    public Guid CategoryId { get; set; }
    public WelfareCaseType CaseType { get; set; }
    public WelfareTier Tier { get; set; } = WelfareTier.Low;
    public int? Points { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Location { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Optional. A Welfare case is forced to at least Confidential server-side whatever this says,
    /// and asking for a rung the caller does not hold the permission for is rejected — you cannot
    /// file a record into a tier you could not then read.
    /// </summary>
    public WelfareVisibility Visibility { get; set; } = WelfareVisibility.Standard;

    /// <summary>Other students this same incident also applies to, beyond StudentId above — see WelfareRecord.AdditionalStudentIds.</summary>
    public List<Guid> AdditionalStudentIds { get; set; } = new();

    /// <summary>When true, skips the description-length/late-entry validation and saves as WelfareStatus.Draft instead of Resolved — for a mobile quick-log left unfinished. FinalizeRecord re-runs full validation when the author comes back to it.</summary>
    public bool SaveAsDraft { get; set; }

    // --- The graduated response ---

    public WelfareResponseStage? ResponseStage { get; set; }

    [MaxLength(500, ErrorMessage = "Antecedent cannot exceed 500 characters")]
    public string? Antecedent { get; set; }

    public WelfarePerceivedFunction? PerceivedFunction { get; set; }

    [MaxLength(1000, ErrorMessage = "Action taken cannot exceed 1000 characters")]
    public string? ActionTaken { get; set; }

    public Guid? AssignedToUserId { get; set; }
    public DateTime? ActionDueDate { get; set; }

    /// <summary>
    /// Set by the client only after the user has answered the escalation prompt. The server
    /// re-derives whether the prompt was warranted rather than trusting this — it governs a
    /// confirmation, never an authorization, so a stale or absent value can only ever cost an
    /// extra prompt, never let something through that should have been questioned.
    /// </summary>
    public bool EscalationAcknowledged { get; set; }
}

/// <summary>
/// One record for a group of students (plan STUDENT_ROSTER_AND_LIST_STANDARD §3, 2026-09-23). The server runs the
/// SAME single-record creation per student, so every rule a single record obeys (scope, late entry, closed
/// periods, category rules) still binds. Welfare case type is refused outright (decision L7): a confidential
/// concern is each child's own. Synchronous, one transaction, at most 200 students.
/// </summary>
public record BulkWelfareRecordRequest
{
    public List<Guid> StudentIds { get; set; } = new();

    /// <summary>
    /// False (default, L1): one record per student. True: ONE record — the first student as its subject and the rest
    /// in AdditionalStudentIds — for a single behaviour incident with several students in it. Behaviour only.
    /// </summary>
    public bool OneIncident { get; set; }

    /// <summary>The record written for each student; its StudentId and AdditionalStudentIds are ignored.</summary>
    public CreateWelfareRecordRequest Record { get; set; } = new();
}

/// <summary>
/// The group log's limits and refusals, in ONE place so the dialog and the server say the same words
/// (plan STUDENT_ROSTER_AND_LIST_STANDARD §3).
/// </summary>
public static class WelfareBulkLimits
{
    /// <summary>At most this many students in one group log. Synchronous and one transaction, so it is capped.</summary>
    public const int MaxStudents = 200;

    public const string CapMessage = "A record can be logged for at most 200 students at a time. Select fewer and log the rest separately.";

    /// <summary>Decision L7.</summary>
    public const string WelfareRefusal = "A welfare concern is logged for one student at a time — it is that child's own record.";
}

public record BulkWelfareRecordResultDto
{
    /// <summary>Records written (1 for one incident; one per student otherwise).</summary>
    public int Created { get; init; }
    public int Students { get; init; }
    public List<Guid> RecordIds { get; init; } = new();
    /// <summary>Groups the batch on the welfare activity log (one line, not forty).</summary>
    public Guid BatchId { get; init; }
    /// <summary>People told, after coalescing — one message each (L5).</summary>
    public int PeopleAlerted { get; init; }
}

/// <summary>
/// The answer to "what was tried before this?", computed for one student in the current term.
/// Drives the escalation prompt — guidance, never a barrier, because a member of staff dealing
/// with a real emergency must not be argued with by a form.
/// </summary>
public record EscalationCheckDto
{
    public bool WouldPrompt { get; init; }
    public int PreventiveCount { get; init; }
    public int RestorativeCount { get; init; }
    public int CorrectiveCount { get; init; }
    public int PunitiveCount { get; init; }
    public int OpenSupportPlans { get; init; }

    /// <summary>Plain sentence for the dialog — written server-side so every client says the same thing.</summary>
    public string Message { get; init; } = string.Empty;
}

public record AddWelfareNoteRequest
{
    public string Body { get; set; } = string.Empty;
    public WelfareNoteKind Kind { get; set; } = WelfareNoteKind.Note;
    public bool IsFinal { get; set; }
    public string? AttributedToName { get; set; }
}

public record UpdateWelfareActionRequest
{
    public string? ActionTaken { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public DateTime? ActionDueDate { get; set; }
}

public record UpdateWelfareStatusRequest
{
    public WelfareStatus Status { get; set; }
}

/// <summary>
/// Moves a record between visibility rungs — the ONE mutable field on an otherwise append-only
/// record, because a record whose sensitivity is discovered later must be raisable in place.
///
/// The change is not silent: the endpoint writes a WelfareNote recording who changed it, from what
/// to what, and why, so the chronology still carries it. A reason is required when LOWERING, since
/// widening who can read a safeguarding record is the direction that needs justifying.
/// </summary>
public record UpdateWelfareVisibilityRequest
{
    public WelfareVisibility Visibility { get; set; }

    [MaxLength(500, ErrorMessage = "Reason cannot exceed 500 characters")]
    public string? Reason { get; set; }
}

/// <summary>
/// Refines an existing record's interpretation — the trigger, staff's read of what it achieved,
/// and what was done about it. A full replace of these three, never a merge: the caller posts all
/// three, so null genuinely means "clear this" rather than "leave alone", matching the create
/// form's clearable pickers. The factual account of the incident is not editable and is not here.
/// </summary>
public record UpdateWelfareInterpretationRequest
{
    public WelfareResponseStage? ResponseStage { get; set; }
    public string? Antecedent { get; set; }
    public WelfarePerceivedFunction? PerceivedFunction { get; set; }
}

/// <summary>Aggregate counts for a branch's Welfare Dashboard — category/tier mix and the per-staff category distribution the equity/consistency-audit case (see the welfare-plan §03) argues a school should be able to check on its own process, not just an individual student's history.</summary>
public record WelfareSummaryDto
{
    public int TotalRecords { get; init; }
    public int OpenActionsCount { get; init; }
    public int OverdueActionsCount { get; init; }
    public List<WelfareCategoryCountDto> ByCategory { get; init; } = new();
    public List<WelfareStaffCountDto> ByStaff { get; init; } = new();

    /// <summary>
    /// The classes these figures actually cover, when the caller's role is class-scoped. Empty for
    /// an unscoped caller, which is every role that is not a class teacher.
    ///
    /// Carried on the summary rather than fetched separately because a scoped total is the more
    /// dangerous number of the two: "42 behaviour records" read by a form tutor as the school's
    /// figure, when it is their own class's, is a wrong conclusion drawn from a correct query. The
    /// scope is enforced server-side either way — this only makes it legible.
    /// </summary>
    public List<string> ScopedToClasses { get; init; } = new();
}

public record WelfareCategoryCountDto
{
    public string CategoryName { get; init; } = string.Empty;
    public WelfareCaseType CaseType { get; init; }
    public int Count { get; init; }
}

public record WelfareStaffCountDto
{
    public string StaffName { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary>
/// The guardian's contact is looked up server-side from StudentGuardian/VisitorProfile — the
/// caller only says which guardian (by StudentGuardian link id) and which channel, and gets back
/// an editable draft to review before actually sending (SendWelfareNotificationRequest).
/// </summary>
public record WelfareNotificationDraftDto
{
    public Guid GuardianLinkId { get; init; }
    public string GuardianName { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string SuggestedMessage { get; init; } = string.Empty;
    public bool HasContactInfo { get; init; }
}

public record SendWelfareNotificationRequest
{
    public Guid GuardianLinkId { get; set; }
    public string Channel { get; set; } = "Sms";
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// One flat row of a historical welfare-ledger import, as parsed client-side from an uploaded
/// Excel/CSV (rosterImport.js, kind "welfare") — every field is the raw cell text, deliberately
/// left as strings so the background processor (RosterImportProcessorJob) does all parsing and
/// validation server-side and logs a per-row reason for anything it rejects, the same way the
/// roster import does. Category is matched by name within the organization and case type — a
/// name that doesn't exist fails the row rather than silently creating a category nobody chose.
/// </summary>
public record WelfareImportRow
{
    public string? StudentCode { get; init; }
    public string? CaseType { get; init; }
    public string? Category { get; init; }
    public string? OccurredAt { get; init; }
    public string? Description { get; init; }
    public string? Points { get; init; }
    public string? Tier { get; init; }
    public string? ActionTaken { get; init; }
    public string? Status { get; init; }
}

public record StartWelfareImportRequest
{
    public string? SourceFileName { get; init; }
    public List<WelfareImportRow> Rows { get; init; } = new();
}

/// <summary>
/// The little a welfare timeline needs before it can render: who the page is about, and the
/// branch's own list of interventions to suggest on the action field.
///
/// It exists because the page used to get both from the roster, which is gated on
/// <c>students.view</c> — so a caller holding only <c>welfare.view</c> got a page with no name on
/// it. That gate was not protecting anything: <see cref="WelfareRecordDto.StudentName"/> is
/// already returned to a plain <c>welfare.view</c> caller on every record in the timeline, so the
/// name was disclosed and then not displayed.
///
/// What it deliberately does NOT carry is anything the roster tier owns — no guardians, no home
/// or family context, no health summary, and above all no way to enumerate the branch. Listing
/// every student is the exposure WelfareController.SearchRecords already reasons about when it
/// gates branch-wide search at <c>welfare.reports.view</c> rather than plain <c>welfare.view</c>;
/// the same reasoning applies here, so this endpoint answers about one named student and nothing
/// else. Class and code are included because a safeguarding record has to be attached to the
/// right child, and a name alone does not distinguish two of them.
/// </summary>
public record WelfareTimelineContextDto
{
    public Guid StudentId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? StudentCode { get; init; }
    public string? ClassName { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Suggestions for the action/intervention field — the branch's ActionsTaken list.</summary>
    public List<string> ActionsTaken { get; init; } = new();
}

/// <summary>
/// Cohort and disproportionality reporting. Counts alone would only say which group misbehaves
/// most; the share of records that reached a punitive response is the number that says something
/// about the school rather than about the children.
/// </summary>
public record WelfareCohortReportDto
{
    public int WindowDays { get; init; }
    public int TotalRecords { get; init; }
    public List<CohortSliceDto> ByHouse { get; init; } = new();
    public List<CohortSliceDto> ByResidency { get; init; } = new();
    public List<CohortSliceDto> BySex { get; init; } = new();
    public List<CohortSliceDto> ByFeesStatus { get; init; } = new();
}

public record CohortSliceDto
{
    public string Label { get; init; } = string.Empty;
    public int TotalRecords { get; init; }
    public int PunitiveCount { get; init; }

    /// <summary>Percentage of this slice's records that reached a punitive response.</summary>
    public double PunitiveShare { get; init; }
}
