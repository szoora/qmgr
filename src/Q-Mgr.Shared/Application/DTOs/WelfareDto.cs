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
    public bool Confidential { get; init; }
    public string ReportedByName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string? ActionTaken { get; init; }
    public Guid? AssignedToUserId { get; init; }
    public string? AssignedToName { get; init; }
    public DateTime? ActionDueDate { get; init; }
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

    /// <summary>Other students this same incident also applies to, beyond StudentId above — see WelfareRecord.AdditionalStudentIds.</summary>
    public List<Guid> AdditionalStudentIds { get; set; } = new();

    /// <summary>When true, skips the description-length/late-entry validation and saves as WelfareStatus.Draft instead of Resolved — for a mobile quick-log left unfinished. FinalizeRecord re-runs full validation when the author comes back to it.</summary>
    public bool SaveAsDraft { get; set; }
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

/// <summary>Aggregate counts for a branch's Welfare Dashboard — category/tier mix and the per-staff category distribution the equity/consistency-audit case (see the welfare-plan §03) argues a school should be able to check on its own process, not just an individual student's history.</summary>
public record WelfareSummaryDto
{
    public int TotalRecords { get; init; }
    public int OpenActionsCount { get; init; }
    public int OverdueActionsCount { get; init; }
    public List<WelfareCategoryCountDto> ByCategory { get; init; } = new();
    public List<WelfareStaffCountDto> ByStaff { get; init; } = new();
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
