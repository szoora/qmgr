using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

/// <summary>
/// Letterhead info for printed Student Visitation Cards / Visiting Day Passes — the tenant's own
/// identity (school name, this branch's address, contact details), not the app's branding.
/// Deliberately its own DTO rather than reusing OrganizationBrandingDto, which is explicitly
/// anonymous-safe and excludes contact info by design (served to public kiosk/display screens);
/// this one is only ever fetched from an already-authenticated admin page.
/// </summary>
public record PrintLetterheadDto
{
    public string OrganizationName { get; init; } = string.Empty;
    public string? Address { get; init; }
    public string? ContactPhone { get; init; }
    public string? ContactEmail { get; init; }
    public string? LogoUrl { get; init; }
}

public record StudentDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? StudentCode { get; init; }
    public string? ClassName { get; init; }
    public bool IsActive { get; init; }
    public int GuardianCount { get; init; }
    public List<StudentGuardianDto> Guardians { get; init; } = new();
}

public record StudentGuardianDto
{
    public Guid Id { get; init; }
    public Guid VisitorProfileId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string Relationship { get; init; } = string.Empty;
}

// Mutable (not init-only) — these three are bound directly as Blazor form models via @bind in
// StudentRoster.razor, which requires settable properties (same reasoning as
// CheckInVisitorRequest/PreRegisterVisitorRequest in VisitorDto.cs).
public record CreateStudentRequest
{
    public string FullName { get; set; } = string.Empty;
    public string? StudentCode { get; set; }
    public string? ClassName { get; set; }
}

public record UpdateStudentRequest
{
    public string FullName { get; set; } = string.Empty;
    public string? StudentCode { get; set; }
    public string? ClassName { get; set; }
    public bool IsActive { get; set; } = true;
}

public record AddGuardianRequest
{
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string Relationship { get; set; } = "Guardian";
}

/// <summary>
/// One flat row as parsed client-side from an uploaded Excel/CSV file — one row = one
/// Student+Guardian pair. A student with three guardians on file is three rows sharing the same
/// StudentCode, not one row with a nested guardian list — flat rows are what a school's own
/// spreadsheet or SMIS export actually looks like.
/// </summary>
public record RosterImportRow
{
    public string? StudentCode { get; init; }
    public string StudentFullName { get; init; } = string.Empty;
    public string? ClassName { get; init; }
    public string GuardianFullName { get; init; } = string.Empty;
    public string? GuardianPhone { get; init; }
    public string? GuardianEmail { get; init; }
    public string? Relationship { get; init; }
}

public record StartRosterImportRequest
{
    public string? SourceFileName { get; init; }
    public List<RosterImportRow> Rows { get; init; } = new();
}

public record RosterImportJobDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public string? SourceFileName { get; init; }
    public string Source { get; init; } = "admin_ui";
    public RosterImportStatus Status { get; init; }
    public int TotalRows { get; init; }
    public int ProcessedRows { get; init; }
    public int CreatedCount { get; init; }
    public int UpdatedCount { get; init; }
    public int DuplicateCount { get; init; }
    public int FailedCount { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? FailureReason { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record RosterImportJobEntryDto
{
    public int RowNumber { get; init; }
    public string? StudentCode { get; init; }
    public string? StudentName { get; init; }
    public string? GuardianName { get; init; }
    public RosterImportRowOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>Pushed over the notification hub's branch group as the background job runs.</summary>
public record RosterImportProgressEvent
{
    public Guid JobId { get; init; }
    public Guid BranchId { get; init; }
    public RosterImportStatus Status { get; init; }
    public int TotalRows { get; init; }
    public int ProcessedRows { get; init; }
    public int CreatedCount { get; init; }
    public int UpdatedCount { get; init; }
    public int DuplicateCount { get; init; }
    public int FailedCount { get; init; }
}

/// <summary>
/// One (student, guardian) match for the visiting-day search-and-check-in flow — a search for
/// "kamau" can match either the student's or the guardian's name, so the result always carries
/// both sides regardless of which one the search term actually hit.
/// </summary>
public record StudentGuardianSearchResultDto
{
    public Guid StudentId { get; init; }
    public string StudentName { get; init; } = string.Empty;
    public string? StudentCode { get; init; }
    public string? ClassName { get; init; }
    public Guid GuardianProfileId { get; init; }
    public string GuardianName { get; init; } = string.Empty;
    public string? GuardianPhone { get; init; }
    public string? GuardianEmail { get; init; }
    public string Relationship { get; init; } = string.Empty;

    /// <summary>
    /// How many times this guardian's card has already been checked in today (any student,
    /// any branch visit) — surfaced at search time so front-desk staff can spot a card being
    /// reused more than a normal drop-off/pick-up pattern would explain, before completing
    /// another check-in against it.
    /// </summary>
    public int CheckInsToday { get; init; }

    // Lets the Check-In UI skip prompting for a flag-and-override reason when the card is
    // already flagged — the repeat-check-in gate in VisitorsController.CheckIn treats an
    // already-watchlisted profile as already past the gate.
    public bool GuardianIsWatchlisted { get; init; }
}

// Stored inside Branch.Settings under the "ClassColors" key — a plain className-to-hex-color
// map an admin defines themselves (Students.ClassName is free text, not a separate entity with
// its own color field, so this is deliberately just a lookup table rather than a schema change).
// A class with no entry here has no assigned color yet; the UI shows a neutral placeholder and
// prompts the admin to pick one rather than guessing at a color for them.
public record ClassColorSettingsDto
{
    public Dictionary<string, string> Colors { get; set; } = new();
}
