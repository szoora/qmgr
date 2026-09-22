using System.ComponentModel.DataAnnotations;
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
    /// <summary>
    /// How much of this file the caller holds (duty rota plan §5.3): "Unscoped", "Pastoral" or "Teaching". A
    /// Teaching-tier row has already been blanked server-side — guardians, flags, the pastoral tier and consent
    /// are empty — so the UI uses this only to explain why, never to hide anything itself.
    /// </summary>
    public string AccessTier { get; init; } = "Unscoped";

    /// <summary>For a Teaching-tier row: the subjects the caller teaches this student's class.</summary>
    public List<string> TaughtSubjects { get; init; } = new();

    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? StudentCode { get; init; }
    public string? ClassName { get; init; }
    public bool IsActive { get; init; }
    public int GuardianCount { get; init; }
    public List<StudentGuardianDto> Guardians { get; init; } = new();

    // --- Welfare background. All nullable: a roster that only ever supplied name/code/class
    // returns exactly what it always did. ---

    public DateOnly? DateOfBirth { get; init; }
    public PersonSex? Sex { get; init; }
    public DateOnly? AdmissionDate { get; init; }
    public StudentResidency? Residency { get; init; }
    public string? House { get; init; }
    public string? DormitoryOrStream { get; init; }
    public string? PhotoUrl { get; init; }

    public string? HomeCountry { get; init; }
    public string? HomeDistrict { get; init; }
    public string? HomeAddress { get; init; }
    public StudentLivesWith? LivesWith { get; init; }
    public string? HomeLanguage { get; init; }
    public string? Religion { get; init; }

    // Health is special-category: these four are blanked server-side for a caller without the
    // pastoral tier rather than being omitted from the type, so one DTO serves every audience
    // and there is exactly one place that decides who sees what.
    public string? MedicalConditions { get; init; }
    public string? Allergies { get; init; }
    public string? RegularMedication { get; init; }
    public string? DisabilityOrLearningNeed { get; init; }

    public StudentFeesStatus? FeesStatus { get; init; }
    public string? SponsorName { get; init; }
    public StudentTransportMode? TransportMode { get; init; }
    public string? PreviousSchool { get; init; }

    /// <summary>Derived from <see cref="DateOfBirth"/> server-side so every screen agrees on it.</summary>
    public int? AgeYears { get; init; }

    /// <summary>Live flags only, ordered most severe first — the lens a staff member reads the timeline through.</summary>
    public List<StudentFlagDto> Flags { get; init; } = new();

    /// <summary>True when any guardian link carries a restriction. Lets a roster row show the warning without shipping the confidential reason to every caller.</summary>
    public bool HasGuardianRestriction { get; init; }

    // Data-processing consent — see Student.DataConsentGivenAt. Null GivenAt means not given
    // (or withdrawn); the roster shows a shield-check vs. a muted shield off this.
    public DateTime? DataConsentGivenAt { get; init; }
    public Guid? DataConsentRecordedByUserId { get; init; }
    public string? DataConsentNotes { get; init; }

    // --- Restricted tier: administrator only (welfare.restricted.view).
    // Blanked SERVER-SIDE for every other caller rather than omitted from the type, exactly like
    // the pastoral and confidential fields above — one DTO serves every audience and there is one
    // place that decides who sees what. Never hidden only in the markup: a field the API still
    // ships is one devtools click from being read.

    public string? RestrictedNotes { get; init; }
    public DateTime? RestrictedNotesUpdatedAt { get; init; }
    public string? RestrictedNotesUpdatedByName { get; init; }

    /// <summary>
    /// True when a restricted note exists — and, unlike the note itself, sent to every caller with
    /// the pastoral tier. Somebody handling this child needs to know that an administrator holds
    /// information about them, or they cannot know to ask; what that information IS stays gated.
    /// </summary>
    public bool HasRestrictedNotes { get; init; }
}

/// <summary>PUT body for a student's administrator-only restricted note. An empty/whitespace value clears the note and its stamps.</summary>
public record UpdateStudentRestrictedNotesRequest
{
    [MaxLength(4000, ErrorMessage = "Restricted notes cannot exceed 4000 characters")]
    public string? Notes { get; set; }
}

/// <summary>PATCH body for a student's data-processing consent. Given=true stamps now + the caller; false clears the whole consent block.</summary>
public record UpdateStudentConsentRequest
{
    public bool Given { get; set; }
    public string? Notes { get; set; }
}

public record StudentGuardianDto
{
    public Guid Id { get; init; }
    public Guid VisitorProfileId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string Relationship { get; init; } = string.Empty;

    /// <summary>Always sent — gate staff cannot enforce a restriction they cannot see.</summary>
    public GuardianContactRestriction ContactRestriction { get; init; }

    /// <summary>Blanked for callers without the confidential tier: it names a third party and a legal circumstance. The restriction above stays visible either way.</summary>
    public string? RestrictionReason { get; init; }

    public bool? HasLegalCustody { get; init; }
    public bool IsPrimaryContact { get; init; }
    public int? ContactPriority { get; init; }
    public bool? LivesWithStudent { get; init; }
}

// Mutable (not init-only) — the request records below are bound directly as Blazor form models via @bind in
// StudentRoster.razor, which requires settable properties (same reasoning as
// CheckInVisitorRequest/PreRegisterVisitorRequest in VisitorDto.cs).
/// <summary>
/// The welfare-background fields shared by create and update, kept in one place so the two can
/// never drift — this codebase's recurring failure is a field added to one shape and forgotten on
/// the other. Validation attributes live here and are enforced by <c>ModelState</c> on the API
/// side and by the same attributes on the Blazor form, so a rule is written once.
/// </summary>
public abstract record StudentProfileFields
{
    [MaxLength(100, ErrorMessage = "House cannot exceed 100 characters")]
    public string? House { get; set; }

    [MaxLength(100, ErrorMessage = "Dormitory or stream cannot exceed 100 characters")]
    public string? DormitoryOrStream { get; set; }

    [MaxLength(500)]
    public string? PhotoUrl { get; set; }

    public DateOnly? DateOfBirth { get; set; }
    public PersonSex? Sex { get; set; }
    public DateOnly? AdmissionDate { get; set; }
    public StudentResidency? Residency { get; set; }

    [MaxLength(100, ErrorMessage = "Country cannot exceed 100 characters")]
    public string? HomeCountry { get; set; }

    [MaxLength(120, ErrorMessage = "Home district cannot exceed 120 characters")]
    public string? HomeDistrict { get; set; }

    [MaxLength(500, ErrorMessage = "Home address cannot exceed 500 characters")]
    public string? HomeAddress { get; set; }

    public StudentLivesWith? LivesWith { get; set; }

    [MaxLength(80)]
    public string? HomeLanguage { get; set; }

    [MaxLength(80)]
    public string? Religion { get; set; }

    [MaxLength(1000, ErrorMessage = "Medical conditions cannot exceed 1000 characters")]
    public string? MedicalConditions { get; set; }

    [MaxLength(1000, ErrorMessage = "Allergies cannot exceed 1000 characters")]
    public string? Allergies { get; set; }

    [MaxLength(1000, ErrorMessage = "Regular medication cannot exceed 1000 characters")]
    public string? RegularMedication { get; set; }

    [MaxLength(1000, ErrorMessage = "Disability or learning need cannot exceed 1000 characters")]
    public string? DisabilityOrLearningNeed { get; set; }

    public StudentFeesStatus? FeesStatus { get; set; }

    [MaxLength(200)]
    public string? SponsorName { get; set; }

    public StudentTransportMode? TransportMode { get; set; }

    [MaxLength(200)]
    public string? PreviousSchool { get; set; }
}

public record CreateStudentRequest : StudentProfileFields
{
    [Required(ErrorMessage = "Full name is required")]
    [MaxLength(255, ErrorMessage = "Full name cannot exceed 255 characters")]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(100, ErrorMessage = "Student code cannot exceed 100 characters")]
    public string? StudentCode { get; set; }

    [MaxLength(100, ErrorMessage = "Class cannot exceed 100 characters")]
    public string? ClassName { get; set; }
}

public record UpdateStudentRequest : StudentProfileFields
{
    [Required(ErrorMessage = "Full name is required")]
    [MaxLength(255, ErrorMessage = "Full name cannot exceed 255 characters")]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(100, ErrorMessage = "Student code cannot exceed 100 characters")]
    public string? StudentCode { get; set; }

    [MaxLength(100, ErrorMessage = "Class cannot exceed 100 characters")]
    public string? ClassName { get; set; }

    public bool IsActive { get; set; } = true;
}

public record AddGuardianRequest
{
    [Required(ErrorMessage = "Guardian name is required")]
    [MaxLength(255, ErrorMessage = "Guardian name cannot exceed 255 characters")]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? Phone { get; set; }

    [EmailAddress(ErrorMessage = "Enter a valid email address")]
    [MaxLength(255)]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Relationship is required")]
    [MaxLength(100)]
    public string Relationship { get; set; } = "Guardian";
}

/// <summary>
/// Everything about the LINK between a guardian and one child, which is where a contact
/// restriction belongs — the organization-wide watchlist on the profile cannot say "may visit his
/// son, must not have contact with his daughter".
/// </summary>
public record UpdateGuardianLinkRequest
{
    [Required]
    [MaxLength(100)]
    public string Relationship { get; set; } = "Guardian";

    public GuardianContactRestriction ContactRestriction { get; set; } = GuardianContactRestriction.None;

    [MaxLength(1000, ErrorMessage = "Restriction reason cannot exceed 1000 characters")]
    public string? RestrictionReason { get; set; }

    public bool? HasLegalCustody { get; set; }
    public bool IsPrimaryContact { get; set; }

    [Range(1, 99, ErrorMessage = "Contact priority must be between 1 and 99")]
    public int? ContactPriority { get; set; }

    public bool? LivesWithStudent { get; set; }
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
    public RosterImportKind Kind { get; init; } = RosterImportKind.Roster;
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

    /// <summary>What the changed field held before and after — populated for a batch, null for an
    /// import, which changes many fields at once and has no single before-and-after to show.</summary>
    public string? PreviousValue { get; init; }
    public string? NewValue { get; init; }
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

// ============================================================================================
// Student flags — the standing vulnerability markers a chronology entry is read through.
// ============================================================================================

public record StudentFlagDto
{
    public Guid Id { get; init; }
    public Guid StudentId { get; init; }
    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public string? CategoryColor { get; init; }
    public WelfareTier Tier { get; init; }

    /// <summary>
    /// Standard / Confidential / Restricted — a DIFFERENT axis from <see cref="Tier"/>, which is
    /// severity. A Restricted flag is never returned at all to a caller without
    /// <c>welfare.restricted.view</c>; this field is here to label the chip, not to filter on.
    /// </summary>
    public WelfareVisibility Visibility { get; init; }

    /// <summary>Blanked for callers below this flag's tier — the chip stays, the detail does not.</summary>
    public string? Notes { get; init; }

    public Guid RaisedByUserId { get; init; }
    public string RaisedByName { get; init; } = string.Empty;
    public DateTime RaisedAt { get; init; }
    public DateTime? ReviewDueDate { get; init; }
    public DateTime? EndedAt { get; init; }
    public string? EndedByName { get; init; }
    public string? EndReason { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Live and past its review date. A flag nobody has revisited looks like current knowledge and is worse than no flag.</summary>
    public bool IsReviewOverdue { get; init; }
}

public record CreateStudentFlagRequest
{
    [Required(ErrorMessage = "Choose what the flag is")]
    public Guid CategoryId { get; set; }

    public WelfareTier Tier { get; set; } = WelfareTier.Low;

    /// <summary>Rejected server-side unless the caller holds the matching permission for the level asked for.</summary>
    public WelfareVisibility Visibility { get; set; } = WelfareVisibility.Standard;

    [MaxLength(2000, ErrorMessage = "Notes cannot exceed 2000 characters")]
    public string? Notes { get; set; }

    /// <summary>Optional, but strongly encouraged in the UI: a flag with no review date is one nobody will ever revisit.</summary>
    public DateTime? ReviewDueDate { get; set; }
}

public record EndStudentFlagRequest
{
    [Required(ErrorMessage = "Say why the flag is being ended")]
    [MaxLength(500, ErrorMessage = "Reason cannot exceed 500 characters")]
    public string EndReason { get; set; } = string.Empty;
}

public record UpdateStudentFlagRequest
{
    public WelfareTier Tier { get; set; } = WelfareTier.Low;

    /// <summary>Rejected server-side unless the caller holds the matching permission — both for the level being set AND the level already there, so a lower-tier user cannot silently downgrade a flag they cannot see.</summary>
    public WelfareVisibility Visibility { get; set; } = WelfareVisibility.Standard;

    [MaxLength(2000, ErrorMessage = "Notes cannot exceed 2000 characters")]
    public string? Notes { get; set; }

    public DateTime? ReviewDueDate { get; set; }
}

/// <summary>
/// The one-page student picture: what a house parent reads in ninety seconds before a difficult
/// conversation. Assembled server-side in a single call rather than leaving the page to fan out
/// to five endpoints and stitch the answer together.
/// </summary>
public record StudentPictureDto
{
    public StudentDto Student { get; init; } = new();

    public int TotalRecords { get; init; }
    public int OpenActions { get; init; }
    public int OverdueActions { get; init; }
    public int OpenSupportPlans { get; init; }
    public int AchievementCount { get; init; }
    public int BehaviorCount { get; init; }
    public int WelfareConcernCount { get; init; }
    public int NetPoints { get; init; }

    public DateTime? LastRecordAt { get; init; }

    /// <summary>Twelve monthly buckets, oldest first — the sparkline behind the numbers.</summary>
    public List<StudentTrendPointDto> Trend { get; init; } = new();

    /// <summary>Counts by response stage, so "what have we actually tried" is answerable at a glance.</summary>
    public Dictionary<string, int> ByResponseStage { get; init; } = new();

    /// <summary>Plain-language observations from a GROUP BY — never a prediction, never a score.</summary>
    public List<string> Patterns { get; init; } = new();

    public List<WelfareRecordDto> RecentRecords { get; init; } = new();
}

public record StudentTrendPointDto
{
    public int Year { get; init; }
    public int Month { get; init; }
    public string Label { get; init; } = string.Empty;
    public int Achievements { get; init; }
    public int Behaviors { get; init; }
    public int Concerns { get; init; }
}

// ============================================================================================
// Branch vocabularies — small, user-configurable master data.
//
// Stored as a keyed section of Branch.Settings (a JSON column) rather than as tables. The rule
// this follows, so the next list added lands in the right place:
//
//   JSON here          small bounded list, no per-item lifecycle, nothing references it by ID,
//                      the value is COPIED onto the row as a plain label.
//   A real table       something foreign-keys to it, it carries its own dates/state/audit, the
//                      database has to protect it on delete, or it is reported on directly.
//
// Welfare categories stay a table by that rule and should not be moved here: WelfareRecord and
// StudentFlag both hold a real CategoryId with Restrict-on-delete.
// ============================================================================================

/// <summary>
/// One entry in a closed list. Closed means the field that uses it is a picker and a typo is not
/// possible — Class, House, Dormitory.
/// </summary>
public record VocabularyItemDto
{
    [Required(ErrorMessage = "Name is required")]
    [MaxLength(100, ErrorMessage = "Name cannot exceed 100 characters")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Hex colour, used by the roster pill and the printed visiting-day pass. Only Class uses it today.</summary>
    [MaxLength(9)]
    public string? Color { get; set; }

    /// <summary>
    /// Explicit order, because the natural one is wrong: S.1 … S.10 sorts as S.1, S.10, S.2
    /// alphabetically. A JSON array gives ordering for free, which is one place this beats a table.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>Retired entries stop being offered on new records but stay readable on old ones.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The year level a class stream belongs to ("S2" for "S2A"), grouping streams for reports, timetable
    /// views and bulk assignment (duty rota plan §5.1). Classes only; the editor round-trips it.
    /// </summary>
    [MaxLength(20)]
    public string? Level { get; set; }

    /// <summary>For a room: how many it seats. Rooms only.</summary>
    public int? Capacity { get; set; }

    /// <summary>For a room: "lab", "general", "hall" — free text the timetable can filter on. Rooms only.</summary>
    [MaxLength(40)]
    public string? RoomType { get; set; }
}

/// <summary>
/// Every configurable list for one branch. Closed lists are pickers; suggested lists offer known
/// values and still accept anything typed, because a closed list for these would only push real
/// answers into "Other" — the reasoning already recorded on StudentGuardian.Relationship.
/// </summary>
public record BranchVocabulariesDto
{
    public List<VocabularyItemDto> Classes { get; set; } = new();
    public List<VocabularyItemDto> Houses { get; set; } = new();
    public List<VocabularyItemDto> Dormitories { get; set; } = new();

    /// <summary>Rooms lessons are held in (duty rota plan §3.1, §6.1), name-matched like classes for clash checking.</summary>
    public List<VocabularyItemDto> Rooms { get; set; } = new();

    public List<string> HomeLanguages { get; set; } = new();
    public List<string> Religions { get; set; } = new();
    public List<string> GuardianRelationships { get; set; } = new();
    public List<string> ActionsTaken { get; set; } = new();
}

/// <summary>
/// Saving the lists. Renames are explicit rather than inferred from a diff: a diff cannot tell a
/// rename apart from a delete plus an add, and getting that wrong either orphans every student's
/// class or silently rewrites the wrong one.
/// </summary>
public record UpdateBranchVocabulariesRequest
{
    public BranchVocabulariesDto Vocabularies { get; set; } = new();

    /// <summary>Old name to new name, applied to every student holding the old value in this branch.</summary>
    public Dictionary<string, string> ClassRenames { get; set; } = new();
    public Dictionary<string, string> HouseRenames { get; set; } = new();
    public Dictionary<string, string> DormitoryRenames { get; set; } = new();
}

/// <summary>What the save actually did, so the UI can report it rather than claim a silent success.</summary>
public record UpdateBranchVocabulariesResultDto
{
    public BranchVocabulariesDto Vocabularies { get; init; } = new();
    public int StudentsRenamed { get; init; }
    public List<string> Warnings { get; init; } = new();
}

/// <summary>How many students hold a value — what the delete guard reports before anything is removed.</summary>
public record VocabularyUsageDto
{
    public string Name { get; init; } = string.Empty;
    public int StudentCount { get; init; }
}

/// <summary>
/// "Which of these children are already on the roll, and what would this file change about them?"
/// Read-only and side-effect free, so it is safe to call on every file a reader opens.
///
/// <para>It takes the ROWS rather than a list of admission numbers, because the two useful answers —
/// what differs, and which names look like one child entered twice — cannot be worked out from keys
/// alone. See <see cref="StaffImportPrecheckRequest"/>, which is the same shape on the staff side.</para>
/// </summary>
public record RosterImportPrecheckRequest
{
    public List<RosterImportRow> Rows { get; set; } = new();
}

/// <summary>One child the import would land on rather than add.</summary>
public record RosterImportExistingDto
{
    /// <summary>The admission number this row matched on — the key the import upserts by.</summary>
    public string StudentCode { get; init; } = string.Empty;

    /// <summary>Their name as it stands on the roll, so the reader recognises them.</summary>
    public string FullName { get; init; } = string.Empty;

    /// <summary>The class they are in now. Shown because a class move is the change a reader most wants to see coming.</summary>
    public string? ClassName { get; init; }

    /// <summary>What this file would overwrite, in the reader's words ("name", "class", "guardian phone"). Empty means nothing is different.</summary>
    public List<string> Changes { get; init; } = new();
}

/// <summary>
/// The answer. A re-import of last term's sheet is the normal case in a school, so the reader is
/// told before they commit how much of it lands on children already on the roll and how much of THAT
/// would actually change anything — rather than reading it in the summary afterwards.
/// </summary>
public record RosterImportPrecheckDto
{
    public List<RosterImportExistingDto> Existing { get; init; } = new();

    /// <summary>How many of <see cref="Existing"/> this file would actually change something about.</summary>
    public int ChangedCount => Existing.Count(e => e.Changes.Count > 0);

    /// <summary>Names that look like one child entered twice. A question for the reader, never a refusal.</summary>
    public List<ImportPossibleDuplicateDto> PossibleDuplicates { get; init; } = new();
}
