using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

// =====================================================================================================
// The school calendar, the programme import and the personal calendar feed — the DTO contract between
// Q-Mgr.API and Q-Mgr.Web. Plan: docs/plans/TERM_PROGRAMME_CALENDAR_AND_GATES.md (2026-09-23).
// =====================================================================================================

/// <summary>
/// One school event: an activity, a programme item, a national day. Never a staff duty — a meeting that staff are
/// expected at is a Session <c>StaffDuty</c>, and an event that IS that meeting points at it through
/// <see cref="DutyId"/> so the two views show one thing (plan §3, decision D1).
/// </summary>
public record SchoolEventDto
{
    public Guid Id { get; init; }
    /// <summary>Null means every branch of the organization.</summary>
    public Guid? BranchId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }

    /// <summary>First day. An all-day event has no <see cref="StartTime"/> (iCalendar's DATE, not DATE-TIME).</summary>
    public DateOnly StartsOn { get; init; }
    /// <summary>Last day, inclusive. Equal to <see cref="StartsOn"/> for a one-day event.</summary>
    public DateOnly EndsOn { get; init; }
    /// <summary>Local time of day, "HH:mm". Null = all day.</summary>
    public string? StartTime { get; init; }
    /// <summary>Local time of day, "HH:mm". Null with a start time = "onwards" (no stated end).</summary>
    public string? EndTime { get; init; }

    /// <summary>A category name from the organization's calendar categories (data, not an enum).</summary>
    public string? Category { get; init; }
    public EventAudience Audience { get; init; } = EventAudience.Staff;
    /// <summary>"S.4, S.5 &amp; S.6" read into class names. Empty = the whole school.</summary>
    public List<string> ClassNames { get; init; } = new();
    public string? Location { get; init; }

    /// <summary>Who is responsible, as the document wrote it ("Senior Ladies &amp; Matrons").</summary>
    public string? ResponsibleText { get; init; }
    public List<Guid> ResponsibleUserIds { get; init; } = new();
    public List<string> ResponsibleNames { get; init; } = new();
    public List<Guid> ResponsibleDepartmentIds { get; init; } = new();

    /// <summary>The Session duty this event is, when it is also a staff meeting.</summary>
    public Guid? DutyId { get; init; }
    /// <summary>Groups the items of one programme ("Beginning of Term III programme").</summary>
    public Guid? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    /// <summary>The import that created it — the undo handle.</summary>
    public Guid? ImportJobId { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }

    /// <summary>True when the caller may edit or delete it (calendar.manage).</summary>
    public bool CanEdit { get; init; }

    // ---- Audience, lifecycle and the rest (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING, 2026-09-26) ----

    /// <summary>Who among staff it is for. Meaningful while <see cref="Audience"/> includes Staff.</summary>
    public StaffAudienceDto StaffAudience { get; init; } = new();
    /// <summary>The audience in words ("Teaching staff, Administrator").</summary>
    public string? StaffAudienceText { get; init; }
    /// <summary>Only its audience, the people responsible and the calendar's keepers can see it.</summary>
    public bool AudienceOnly { get; init; }
    public bool AttendanceRequired { get; init; }
    /// <summary>True when the event is the caller's own: for them, or theirs to run. What "My events" shows.</summary>
    public bool IsMine { get; init; }

    public SchoolEventStatus Status { get; init; }
    public string? CancelReason { get; init; }
    public DateTime? CancelledAt { get; init; }
    /// <summary>The iCalendar SEQUENCE: bumped on a material change.</summary>
    public int Version { get; init; } = 1;
    public bool RemindersOn { get; init; } = true;
    /// <summary>Set when an imported event was edited by hand; a re-import leaves it alone.</summary>
    public DateTime? EditedByHandAt { get; init; }
    public string? Recurrence { get; init; }
    public Guid? LibraryDocumentId { get; init; }
    public string? LibraryDocumentTitle { get; init; }
    /// <summary>Send it back with an edit; a save made against an older version is refused with 409.</summary>
    public uint RowVersion { get; init; }
    /// <summary>For an event that IS a meeting: may the caller take its register (a named recorder, or a duty manager).</summary>
    public bool CanOpenRegister { get; init; }
}

/// <summary>How a new event repeats (E12, Arbor's model): every week or fortnight, in term time, until a date.</summary>
public record RepeatRuleDto
{
    /// <summary>"weekly" | "fortnightly".</summary>
    public string Frequency { get; set; } = "weekly";
    public DateOnly Until { get; set; }
    /// <summary>Skip holidays: a date outside every term, or on a national holiday, is left out.</summary>
    public bool TermTimeOnly { get; set; } = true;
}

public static class EventEditScopes
{
    public const string This = "this";
    public const string Following = "following";
    public const string All = "all";
    public static string Normalize(string? s) => s is Following or All ? s : This;
}

public record SaveSchoolEventRequest
{
    public Guid? BranchId { get; set; }

    public StaffAudienceDto StaffAudience { get; set; } = new();
    public bool AudienceOnly { get; set; }
    public bool AttendanceRequired { get; set; }
    public bool RemindersOn { get; set; } = true;
    /// <summary>Tell the audience now. Ignored for an event that has already happened, and for a change that is not material.</summary>
    public bool NotifyAudience { get; set; } = true;
    /// <summary>The version the editor was looking at. Null skips the check (a create).</summary>
    public uint? RowVersion { get; set; }
    /// <summary>The dialog's idempotency key, fixed for the life of one open dialog.</summary>
    public Guid? ClientRequestId { get; set; }
    /// <summary>On a create only: make a series.</summary>
    public RepeatRuleDto? Repeat { get; set; }
    /// <summary>On an edit of one occurrence of a series: this one, this and following, or all.</summary>
    public string? EditScope { get; set; }
    public Guid? LibraryDocumentId { get; set; }

    [Required(ErrorMessage = "Give the event a title.")]
    [MaxLength(200, ErrorMessage = "A title cannot exceed 200 characters.")]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }

    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Use a time like 08:30.")]
    public string? StartTime { get; set; }

    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Use a time like 17:00.")]
    public string? EndTime { get; set; }

    [MaxLength(60)]
    public string? Category { get; set; }

    public EventAudience Audience { get; set; } = EventAudience.Staff;
    public List<string> ClassNames { get; set; } = new();

    [MaxLength(200)]
    public string? Location { get; set; }

    [MaxLength(300)]
    public string? ResponsibleText { get; set; }
    public List<Guid> ResponsibleUserIds { get; set; } = new();
    public List<Guid> ResponsibleDepartmentIds { get; set; } = new();
}

/// <summary>What the calendar page and the print sheet read: the events of a date range, plus the term that frames it.</summary>
public record CalendarRangeDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public List<SchoolEventDto> Events { get; init; } = new();
    /// <summary>Staff duties in the range the caller is expected at, records, or supervises (Session and Rota only — lessons are the timetable's).</summary>
    public List<StaffDutyDto> MyDuties { get; init; } = new();
    /// <summary>Terms overlapping the range, from the policy periods.</summary>
    public List<PerformancePeriodDto> Terms { get; init; } = new();
    /// <summary>National dates overlapping the range (Ministry, UNEB) — reference only, decision D9.</summary>
    public List<NationalCalendarEntryDto> NationalDates { get; init; } = new();
    /// <summary>The organization's calendar categories, for the filter chips and the editor.</summary>
    public List<string> Categories { get; init; } = new();
    public bool CanManage { get; init; }

    /// <summary>What was asked for: "mine" or "all".</summary>
    public string Scope { get; init; } = CalendarScopes.Mine;
    /// <summary>Today on the SCHOOL's clock (B9) — the Web never reads the server's own.</summary>
    public DateOnly Today { get; init; }
    /// <summary>The branch's time zone id, so duty instants are drawn on the school's clock.</summary>
    public string? TimeZone { get; init; }
    /// <summary>May the caller manage duties — for "Give it a register" and "Open the register".</summary>
    public bool CanManageDuties { get; init; }
}

/// <summary>Cancel an event (it stays on the calendar, struck through) and tell its audience.</summary>
public record CancelSchoolEventRequest
{
    [MaxLength(300)]
    public string? Reason { get; set; }
    public bool NotifyAudience { get; set; } = true;
    public string? EditScope { get; set; }
    public uint? RowVersion { get; set; }
}

/// <summary>Who would be double-booked by an event, asked before saving. Warns; never refuses (D6 of the calendar plan).</summary>
public record EventClashRequest
{
    public StaffAudienceDto StaffAudience { get; set; } = new();
    public List<Guid> ResponsibleUserIds { get; set; } = new();
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
}

public record EventClashDto
{
    public int AudienceCount { get; init; }
    /// <summary>People teaching a lesson in the window (lessons are known two weeks ahead).</summary>
    public int Teaching { get; init; }
    /// <summary>People on another duty or meeting in the window.</summary>
    public int OnDuty { get; init; }
    public List<string> Examples { get; init; } = new();
    public bool LessonsKnown { get; init; } = true;
}

/// <summary>"Give this a register": turn an event into a staff meeting whose attendance is taken (E10).</summary>
public record EventRegisterRequest
{
    /// <summary>Who is expected. Null = the event's own staff audience.</summary>
    public StaffAudienceDto? Expected { get; set; }
    /// <summary>Who takes the register. At least one: a register nobody may take is not a register.</summary>
    public List<Guid> RecorderUserIds { get; set; } = new();
    /// <summary>Needed when the event is all day: a register needs a time.</summary>
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public bool NotifyPeople { get; set; } = true;
}

/// <summary>What an import left without a register, or without anybody to take one (E10, "Import health").</summary>
public record ProgrammeImportHealthDto
{
    public Guid JobId { get; init; }
    public DateTime CreatedAt { get; init; }
    public List<string> SourceFiles { get; init; } = new();
    public List<ImportHealthItemDto> MeetingsWithoutRegister { get; init; } = new();
    public List<ImportHealthItemDto> RegistersWithoutRecorder { get; init; } = new();
    public List<ImportHealthItemDto> Refused { get; init; } = new();
    /// <summary>False for an import made before refusals were kept (26 Sep 2026): its refusals are not known.</summary>
    public bool RefusalsRecorded { get; init; }
}

public record ImportHealthItemDto
{
    public Guid? EventId { get; init; }
    public Guid? DutyId { get; init; }
    public string Title { get; init; } = string.Empty;
    public DateOnly Date { get; init; }
    public string? StartTime { get; init; }
    public string? Location { get; init; }
    public string? ResponsibleText { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool InPast { get; init; }
}

/// <summary>
/// The organization's calendar settings, in <c>Organization.Settings["Calendar"]</c>, written only through
/// <c>OrganizationSettingsLock.MutateAsync</c>. Categories are data (they carry no behaviour); the per-day
/// Teacher-on-Duty expectation is decision D7.
/// </summary>
public record CalendarSettingsDto
{
    public List<string> Categories { get; set; } = new()
    {
        "Academic", "Examinations", "Spiritual", "Meetings", "Sports & clubs", "National days", "Programme", "Holidays"
    };

    /// <summary>How many people a daily rota expects on one day (decision D7). A gap is fewer, a double is more.</summary>
    [Range(1, 20)]
    public int PeoplePerRotaDay { get; set; } = 1;

    /// <summary>Named exceptions to <see cref="PeoplePerRotaDay"/> — reporting day may want two.</summary>
    public Dictionary<string, int> PeoplePerRotaDayOn { get; set; } = new();
}

// ---- The national calendar (decision D9: kept by the platform administrator, warnings only) ---------------

public record NationalCalendarEntryDto
{
    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    /// <summary>"Term", "Holiday", "Examination", "Public holiday" … free text, shown as a label.</summary>
    [MaxLength(60)]
    public string? Kind { get; set; }
    [MaxLength(300)]
    public string? Source { get; set; }
}

public record NationalCalendarDto
{
    public List<NationalCalendarEntryDto> Entries { get; set; } = new();
}

// ---- The personal calendar feed (RFC 5545) ---------------------------------------------------------------

/// <summary>
/// The caller's private feed. <see cref="Url"/> is returned ONCE, when the link is created or replaced; the
/// server keeps only a hash of the secret, the same rule as a document share's slug.
/// </summary>
public record CalendarFeedDto
{
    public bool Exists { get; init; }
    public DateTime? CreatedAt { get; init; }
    public string? Url { get; init; }
}

// ---- The programme import (plan §4–§8) -------------------------------------------------------------------

/// <summary>
/// Everything the reader decided, sent to the server for a check (<see cref="Preview"/> = true) or to be written.
/// The server re-checks every row itself: nothing the browser concluded is trusted, the client only saves a round
/// trip. Both calls take the same body, so what the preview said is what the commit does.
/// </summary>
public record ProgrammeImportRequest
{
    public bool Preview { get; set; } = true;

    /// <summary>File names, for the job record and the history list.</summary>
    public List<string> SourceFiles { get; set; } = new();

    public List<ProgrammeEventRow> Events { get; set; } = new();
    public List<ProgrammeMeetingRow> Meetings { get; set; } = new();
    public List<ProgrammeRotaRow> RotaSlots { get; set; } = new();

    /// <summary>Set a term's dates and theme from the documents (decision: offered, not forced).</summary>
    public ProgrammeTermUpdate? Term { get; set; }

    /// <summary>Matches the reader confirmed, remembered for next time (plan §6).</summary>
    public ImportAliasesDto? LearnedAliases { get; set; }

    /// <summary>Tell each person their duties once, after the batch commits (decision D8 of the rota: one message per person).</summary>
    public bool NotifyPeople { get; set; } = true;

    /// <summary>
    /// Set when an approver commits one section of a document waiting in the Import inbox (E11): the commit is checked
    /// against that section — still waiting, the caller may approve it, not the uploader when a second approver is
    /// required — and marks it approved in the same transaction. Null for an import made directly.
    /// </summary>
    public Guid? InboxJobId { get; set; }
    public string? InboxSection { get; set; }
}

public record ProgrammeTermUpdate
{
    /// <summary>The policy period key to update, or null to add a new one.</summary>
    public string? Key { get; set; }
    [Required, MaxLength(60)]
    public string Name { get; set; } = string.Empty;
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    [MaxLength(200)]
    public string? Theme { get; set; }
}

public record ProgrammeEventRow
{
    /// <summary>Stable across re-imports of the same line: file-kind + normalised title + first date.</summary>
    [Required, MaxLength(200)]
    public string SourceKey { get; set; } = string.Empty;
    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    [MaxLength(2000)]
    public string? Description { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    [MaxLength(60)]
    public string? Category { get; set; }
    public EventAudience Audience { get; set; } = EventAudience.Staff;
    public List<string> ClassNames { get; set; } = new();
    [MaxLength(200)]
    public string? Location { get; set; }
    [MaxLength(300)]
    public string? ResponsibleText { get; set; }
    public List<Guid> ResponsibleUserIds { get; set; } = new();
    public List<Guid> ResponsibleDepartmentIds { get; set; } = new();
    /// <summary>Items of one programme share a series name ("Beginning of Term III programme").</summary>
    [MaxLength(200)]
    public string? SeriesName { get; set; }
    /// <summary>The <see cref="ProgrammeMeetingRow.SourceKey"/> this event is the same thing as, when it is also a staff meeting.</summary>
    public string? SameAsMeetingKey { get; set; }
    /// <summary>Who among staff it is for. Null = every member of staff, which is what an imported event always was.</summary>
    public StaffAudienceDto? StaffAudience { get; set; }
    /// <summary>
    /// For a MEETING sent as an event only: why, in the reader's words ("Event only — you chose it", "Welfare &amp;
    /// Performance is not held"). Kept on the import so Import health can say why a meeting has no register (E10).
    /// </summary>
    [MaxLength(300)]
    public string? NoRegisterReason { get; set; }
}

public record ProgrammeMeetingRow
{
    [Required, MaxLength(200)]
    public string SourceKey { get; set; } = string.Empty;
    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    /// <summary>"HH:mm"; null → the policy's default meeting start is NOT invented — the row is refused as undated in time.</summary>
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    [MaxLength(200)]
    public string? Location { get; set; }
    [MaxLength(300)]
    public string? ConvenerText { get; set; }
    /// <summary>Who may take the register (the convener's office resolved to people). Empty = duty managers only.</summary>
    public List<Guid> RecorderUserIds { get; set; } = new();
    /// <summary>Null = every active staff member ("Staff Meeting"); otherwise the resolved attendance.</summary>
    public List<Guid>? ExpectedUserIds { get; set; }
    /// <summary>How the attendance was decided, shown back to the reader ("department heads", "the 11 prep supervisors").</summary>
    [MaxLength(300)]
    public string? ExpectedText { get; set; }
}

public record ProgrammeRotaRow
{
    [Required, MaxLength(200)]
    public string SourceKey { get; set; } = string.Empty;
    /// <summary>"Teacher on Duty", "Administrator on Duty" — the slot's title and the series it joins.</summary>
    [Required, MaxLength(120)]
    public string RotaName { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    /// <summary>A gate or room, when the rota says where ("Gate duty · Upper Gate").</summary>
    [MaxLength(200)]
    public string? Location { get; set; }
}

/// <summary>What the server found for one row, in the order the rows were sent.</summary>
public record ProgrammeRowResultDto
{
    public string Kind { get; init; } = string.Empty;       // "event" | "meeting" | "rota"
    public string SourceKey { get; init; } = string.Empty;
    public ProgrammeRowOutcome Outcome { get; init; }
    /// <summary>For Update: what would change, by FIELD ("time 17:00 → 16:30", "venue").</summary>
    public List<string> Changes { get; init; } = new();
    /// <summary>For Refused: the reason, in words the reader can act on.</summary>
    public string? Reason { get; init; }
    /// <summary>Warnings that do not block the row (a person already on duty, a date outside term, a holiday).</summary>
    public List<string> Warnings { get; init; } = new();
    /// <summary>The record this row created or would update.</summary>
    public Guid? RecordId { get; init; }
}

public record ProgrammeImportResultDto
{
    public bool Preview { get; init; }
    /// <summary>The job created by a commit — the undo handle. Null on a preview.</summary>
    public Guid? JobId { get; init; }
    public List<ProgrammeRowResultDto> Rows { get; init; } = new();
    public int EventsCreated { get; init; }
    public int EventsUpdated { get; init; }
    public int MeetingsCreated { get; init; }
    public int MeetingsUpdated { get; init; }
    public int RotaSlotsCreated { get; init; }
    public int Unchanged { get; init; }
    public int Refused { get; init; }
    /// <summary>People who will be (preview) or were (commit) told their duties.</summary>
    public int PeopleNotified { get; init; }
    public bool TermUpdated { get; init; }
}

/// <summary>A past programme import, for the history list and its undo button.</summary>
public record ProgrammeImportJobDto
{
    public Guid Id { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CreatedByName { get; init; }
    public List<string> SourceFiles { get; init; } = new();
    public int EventsCreated { get; init; }
    public int MeetingsCreated { get; init; }
    public int RotaSlotsCreated { get; init; }
    public bool Undone { get; init; }
    public DateTime? UndoneAt { get; init; }
    /// <summary>Records the undo will keep because a register was already taken on them.</summary>
    public int Protected { get; init; }
    /// <summary>Meetings this import left without a register, registers with nobody to take them, rows refused (E10).</summary>
    public int NeedsAttention { get; init; }
}

public record ProgrammeUndoResultDto
{
    public int EventsRemoved { get; init; }
    public int DutiesRemoved { get; init; }
    /// <summary>Duties kept because their register had been taken (the timetable's rule).</summary>
    public int DutiesKept { get; init; }
    /// <summary>Existing events and meetings the import UPDATED, put back as they were (2026-09-25).</summary>
    public int UpdatesRestored { get; init; }
    /// <summary>Updated rows left as they are, each named with why: edited since, or a register taken.</summary>
    public List<string> UpdatesLeft { get; init; } = new();
}

/// <summary>
/// Learned matches, in <c>Organization.Settings["ImportAliases"]</c> (plan §6). Keys are normalised with
/// <c>ImportMatching.NameKey</c> so "MRS. KARUGABA GRACE TUSHABE" and "Karugaba Grace Tushabe" are one key.
/// An alias names the USER, so a later change of name on the directory does not break it.
/// </summary>
public record ImportAliasesDto
{
    public Dictionary<string, Guid> People { get; set; } = new();
    public Dictionary<string, OfficeAliasDto> Offices { get; set; } = new();
    /// <summary>Venue as written → room or gate name.</summary>
    public Dictionary<string, string> Venues { get; set; } = new();
}

public record OfficeAliasDto
{
    public List<Guid> DepartmentIds { get; set; } = new();
    public List<Guid> UserIds { get; set; } = new();
    /// <summary>Roles the words mean ("Administration" → the Administrator and the Head Teacher) — learned from an answer (2026-09-26).</summary>
    public List<string> RoleCodes { get; set; } = new();
    public List<string> StaffGroups { get; set; } = new();
    /// <summary>The words mean every member of staff ("Whole staff").</summary>
    public bool AllStaff { get; set; }
}
