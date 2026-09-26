using QMgr.Application.Import.Documents;
using QMgr.Domain.Enums;

namespace QMgr.Application.Import.Programme;

// =====================================================================================================
// The programme import's reading of a set of documents, BEFORE anything is sent to the server
// (plan TERM_PROGRAMME_CALENDAR_AND_GATES §4–§8). Everything here is what the reader is SHOWN and can
// correct; the request the server receives (ProgrammeImportRequest in CalendarDto.cs) is built from it
// once every "must decide" item has an answer. Plain classes with setters, because the page edits them
// in place and the Development-only read-document endpoint serialises them for the e2e suite.
// =====================================================================================================

/// <summary>What a column of a programme table holds, as the classifier read its header.</summary>
public enum ProgrammeColumnRole
{
    None = 0,
    Serial = 1,
    Week = 2,
    Date = 3,
    Time = 4,
    Title = 5,
    Venue = 6,
    Responsible = 7,
    Person = 8,
    Phone = 9,
    PeriodLabel = 10
}

/// <summary>What one row of a document will become.</summary>
public enum ProgrammeRowKind
{
    Event = 0,
    Meeting = 1,
    Rota = 2
}

/// <summary>
/// Who a meeting expects, as the reader chose it. Until 2026-09-26 a meeting whose attendance its TITLE did not give
/// defaulted to <see cref="EventOnly"/> with nothing asked — which is how a school's meetings silently failed to reach
/// the registers. It now defaults to <see cref="Undecided"/>, which is a question on the row: read from the document's
/// own words where they can be, asked where they cannot, never decided by the page alone.
/// </summary>
public enum MeetingAttendance
{
    /// <summary>Not a duty: imported as a calendar event only — because the READER said so, never by default.</summary>
    EventOnly = 0,
    /// <summary>Every active member of staff ("Staff Meeting").</summary>
    Everyone = 1,
    /// <summary>The heads of department.</summary>
    DepartmentHeads = 2,
    /// <summary>The people a rota in the same import names with this role ("Prep Supervisor").</summary>
    RoleHolders = 3,
    /// <summary>People and groups the reader picked, or the document's words read into an audience (<see cref="ProgrammeCandidate.StaffAudience"/>).</summary>
    Chosen = 4,
    /// <summary>Nobody has said yet: a must-decide question on the row.</summary>
    Undecided = 5
}

/// <summary>One table, as the classifier read it.</summary>
public sealed class ProgrammeTableReading
{
    public int TableIndex { get; set; }
    public ProgrammeTableKind Kind { get; set; }
    /// <summary>0..1: how sure the classifier is. Below 0.5 the page asks.</summary>
    public double Confidence { get; set; }
    public List<string> Reasons { get; set; } = new();
    public int HeaderRow { get; set; } = -1;
    public List<string> Headers { get; set; } = new();
    public List<ProgrammeColumnRole> Roles { get; set; } = new();
    /// <summary>Rows under the header that are not captions.</summary>
    public int DataRows { get; set; }
    /// <summary>A fingerprint of the header, so a corrected reading is remembered for the next file of the same shape.</summary>
    public string MappingKey { get; set; } = string.Empty;
}

/// <summary>One row of a document on its way to becoming an event, a meeting or a rota slot.</summary>
public sealed class ProgrammeCandidate
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int TableIndex { get; set; }
    /// <summary>1-based row of the table (as the reader sees it in Word), 0 for a note.</summary>
    public int RowNumber { get; set; }
    public ProgrammeTableKind TableKind { get; set; }
    public ProgrammeRowKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;
    public DateOnly? StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public bool OpenEnded { get; set; }
    public string? Location { get; set; }
    public string? ResponsibleText { get; set; }

    /// <summary>The cells as written, for the preview and for the reader to check the reading against.</summary>
    public string? WeekText { get; set; }
    public string? DateText { get; set; }
    public string? TimeText { get; set; }
    public string? PersonText { get; set; }
    public string? PhoneText { get; set; }

    public string? RotaName { get; set; }
    public string? Category { get; set; }
    public EventAudience Audience { get; set; } = EventAudience.Staff;
    public List<string> ClassNames { get; set; } = new();
    public string? SeriesName { get; set; }

    /// <summary>Found in a note under the table, not in the table itself.</summary>
    public bool FromNote { get; set; }
    /// <summary>The reader has not left it out.</summary>
    public bool Include { get; set; } = true;
    /// <summary>What the reading noticed about this row, in words.</summary>
    public List<string> Notes { get; set; } = new();
    public bool WeekdayMismatch { get; set; }
    public bool YearCorrected { get; set; }
    /// <summary>Set when this row is a duplicate of another and was merged into it.</summary>
    public string? MergedIntoId { get; set; }
    /// <summary>For an event that is the same thing as a meeting row: that meeting's candidate id.</summary>
    public string? SameAsMeetingId { get; set; }
    public string SourceKey { get; set; } = string.Empty;

    // ---- Resolution (filled by the page from the directory; never trusted by the server) ----
    public Guid? UserId { get; set; }
    public List<Guid> ResponsibleUserIds { get; set; } = new();
    public List<Guid> ResponsibleDepartmentIds { get; set; } = new();
    public MeetingAttendance Attendance { get; set; }
    public string? AttendanceRole { get; set; }
    public List<Guid> ExpectedUserIds { get; set; } = new();
    public List<Guid> RecorderUserIds { get; set; } = new();

    /// <summary>For <see cref="MeetingAttendance.Chosen"/>: who, as groups, roles, departments and people (2026-09-26).</summary>
    public QMgr.Application.DTOs.StaffAudienceDto? StaffAudience { get; set; }
    /// <summary>What the document's attendance words were read as, and what they could not be.</summary>
    public List<string> AudienceReadings { get; set; } = new();
    public List<string> AudienceUnresolved { get; set; } = new();
    /// <summary>The reader said who takes the register (possibly "duty managers only"), so it is not asked again.</summary>
    public bool RecordersDecided { get; set; }

    public bool IsDated => StartsOn.HasValue;
}

/// <summary>A person a rota lists without a duty date — a job title in the date column, or nothing.</summary>
public sealed class NotOnRotaEntry
{
    public string FileName { get; set; } = string.Empty;
    public int RowNumber { get; set; }
    public string PersonText { get; set; } = string.Empty;
    public string? PhoneText { get; set; }
    /// <summary>"Prep Supervisor", "Administrator", "Chaplain" — or null when the date cells were blank.</summary>
    public string? Role { get; set; }
    public Guid? UserId { get; set; }
}

/// <summary>One document, read.</summary>
public sealed class ProgrammeFileReading
{
    public string FileName { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    /// <summary>The heading the school wrote ("ACTIVITIES OF TERM III 2026").</summary>
    public string? Title { get; set; }
    /// <summary>"THEME: …" from the heading.</summary>
    public string? Theme { get; set; }
    /// <summary>The year the document is about.</summary>
    public int? Year { get; set; }
    /// <summary>"FROM SATURDAY 12TH SEPTEMBER 2026 TO FRIDAY 4TH DECEMBER 2026", when a rota says so.</summary>
    public DateOnly? RangeStart { get; set; }
    public DateOnly? RangeEnd { get; set; }
    public List<ProgrammeTableReading> Tables { get; set; } = new();
    public List<ProgrammeCandidate> Candidates { get; set; } = new();
    public List<NotOnRotaEntry> NotOnRota { get; set; } = new();
    /// <summary>File-level notes: a year repaired in a week band, a note that could not be read.</summary>
    public List<string> Notes { get; set; } = new();
    /// <summary>Week bands with nothing in them.</summary>
    public int EmptyWeeks { get; set; }
    /// <summary>Data rows across the file's tables (not counting captions or the header).</summary>
    public int TableRows { get; set; }
    /// <summary>Set when the file could not be read at all.</summary>
    public string? Refusal { get; set; }
    /// <summary>The document itself, for the page's "show me the table" view and the dev endpoint.</summary>
    public ImportDocument? Document { get; set; }

    /// <summary>People a person rota lists (with or without a date).</summary>
    public int RotaPeople { get; set; }
}

public enum ProgrammeIssueSeverity
{
    /// <summary>Nothing is imported until this is answered.</summary>
    MustDecide = 0,
    Warning = 1,
    /// <summary>An offered fix; never applied without a press.</summary>
    Suggestion = 2,
    /// <summary>Duplicates that were merged by default.</summary>
    Merged = 3
}

public enum ProgrammeIssueKind
{
    Conflict = 0,
    RotaGap = 1,
    RotaDouble = 2,
    WeekdayMismatch = 3,
    Undated = 4,
    YearCorrected = 5,
    Duplicate = 6,
    NationalDifference = 7,
    DateSuggestion = 8,
    TitleYear = 9,
    NoTime = 10,
    NameQuestion = 11,
    NotOnRota = 12,
    /// <summary>A meeting whose attendance is not known yet (2026-09-26).</summary>
    AttendanceUnclear = 13,
    /// <summary>A meeting with a register and nobody named to take it.</summary>
    NoRecorder = 14
}

/// <summary>Something the reader should see, and — for MustDecide — answer, before anything is sent.</summary>
public sealed class ProgrammeIssue
{
    public string Id { get; set; } = string.Empty;
    public ProgrammeIssueSeverity Severity { get; set; }
    public ProgrammeIssueKind Kind { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> CandidateIds { get; set; } = new();
    public DateOnly? Date { get; set; }
    /// <summary>For a conflict: the dates the documents disagree between.</summary>
    public List<DateOnly> Options { get; set; } = new();
    /// <summary>For a date suggestion (and the gap it fills): the row, and its date before and after.</summary>
    public string? SuggestCandidateId { get; set; }
    public DateOnly? SuggestFrom { get; set; }
    public DateOnly? SuggestTo { get; set; }
    /// <summary>For a name question: the name as written.</summary>
    public string? PersonText { get; set; }
    /// <summary>Set by the page when the reader answers.</summary>
    public bool Resolved { get; set; }
}

/// <summary>Everything the checks found across every file of one import.</summary>
public sealed class ProgrammeCheckResult
{
    public List<ProgrammeIssue> Issues { get; set; } = new();
    public IEnumerable<ProgrammeIssue> MustDecide => Issues.Where(i => i.Severity == ProgrammeIssueSeverity.MustDecide);
    public IEnumerable<ProgrammeIssue> Warnings => Issues.Where(i => i.Severity == ProgrammeIssueSeverity.Warning);
    public IEnumerable<ProgrammeIssue> Suggestions => Issues.Where(i => i.Severity == ProgrammeIssueSeverity.Suggestion);
    public IEnumerable<ProgrammeIssue> Merged => Issues.Where(i => i.Severity == ProgrammeIssueSeverity.Merged);
}
