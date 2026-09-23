namespace QMgr.Domain.Enums;

// Every enum here is stored as a plain int with no HasConversion. Values are APPENDED, never
// inserted, for the same reason WelfareStatus.Draft and WelfareCaseType.SupportPlan say so on
// themselves: slotting a value in the middle silently rewrites the meaning of every stored row.

/// <summary>
/// How widely a role may see OTHER STAFF inside a branch its permissions already reach. The second
/// subject axis beside <see cref="RoleDataScope"/>, which is about students; one enum could not
/// say "all students, my department's staff", so each subject has its own column on Role.
///
/// Self is never scope: every caller reads their own file through the portal regardless of this.
/// Enforced by StaffScopeService, which is the only place that reads it.
/// </summary>
public enum StaffDataScope
{
    /// <summary>Sees every staff member the tenant and permissions already allow. The default, and what every pre-existing role behaves as.</summary>
    Organization = 0,

    /// <summary>
    /// Sees members of the departments the caller heads or deputises. FAILS CLOSED: a head with no
    /// department sees nobody, exactly as a class teacher with no class sees no students.
    /// </summary>
    AssignedDepartments = 1,

    /// <summary>Sees staff whose <c>User.LineManagerUserId</c> is the caller. Fails closed the same way.</summary>
    DirectReports = 2,

    /// <summary>Sees nobody but themselves. Teachers and support staff.</summary>
    SelfOnly = 3
}

/// <summary>What kind of thing a PerformanceParameter measures. Decides the sign rule and the scoring formula.</summary>
public enum ParameterKind
{
    /// <summary>Being where you were expected: lesson attendance, meeting attendance. Scored by outcome ratio.</summary>
    Attendance = 0,
    /// <summary>A duty carried out or not: exam supervision, prep supervision. Scored by outcome ratio.</summary>
    Duty = 1,
    /// <summary>A rated observation on a written rubric (a lesson observation). Scored by mean rating.</summary>
    Observation = 2,
    /// <summary>Positive points for something done: co-curricular, schemes of work, professional development.</summary>
    Contribution = 3,
    /// <summary>Positive points from a colleague, within a monthly budget.</summary>
    Recognition = 4,
    /// <summary>Negative points: a conduct matter.</summary>
    Conduct = 5,
    /// <summary>Never scored. A welfare-of-staff record: a bereavement, a workload concern, a health matter.</summary>
    Wellbeing = 6
}

/// <summary>
/// THE STAFF GROUP IS A TENANT VOCABULARY, NOT AN ENUM (2026-09-22).
///
/// <para>What was here was a three-value enum resolved by
/// <c>roleCode == "support-staff" ? Support : Teaching</c> — so every custom role a school created,
/// and <c>admin</c>, <c>manager</c> and <c>viewer</c>, counted as TEACHING staff. A bulk import
/// defaults a missing role to <c>teacher</c>, so a school's whole list arrived teaching, and the
/// bursar, matron, driver and cook were then scored on Lesson Attendance and Lesson Observation.</para>
///
/// <para>It qualified as data by the test this project now applies: <b>does the value carry
/// behaviour?</b> One set-membership test read it — no formula, no sign rule, nothing it showed or
/// hid. Pure taxonomy is data, so it is a <c>VocabularyItemDto</c> list in
/// <c>StaffPerformancePolicyDto.StaffGroups</c> and a school may add Boarding, Administration or
/// Ancillary beside the two seeded ones. Contrast <c>ParameterKind</c>, which stays an enum because
/// each value IS a branch in the scorer and a tenant cannot author arithmetic.</para>
///
/// <para>A group is referenced by NAME. <b>Null means every group</b>, which retires the old
/// <c>AllStaff</c> member deliberately: "applies to everyone" is the absence of a restriction, not a
/// magic entry in a list somebody could rename or retire.</para>
/// </summary>
public static class StaffGroups
{
    /// <summary>Seeded on first policy read — exactly what the old enum resolved to, so no score moves.</summary>
    public const string Teaching = "Teaching staff";
    public const string Support = "Support staff";

    /// <summary>What a null group reads as on screen.</summary>
    public const string AllLabel = "All staff";

    /// <summary>
    /// Does something restricted to <paramref name="required"/> apply to somebody in
    /// <paramref name="actual"/>? A null or blank requirement means everybody.
    /// </summary>
    public static bool Applies(string? required, string? actual)
        => string.IsNullOrWhiteSpace(required)
        || string.Equals(Key(required), Key(actual), StringComparison.Ordinal);

    /// <summary>
    /// The comparison form: letters and digits only, upper-cased. The same rule
    /// <c>ClassName.Key</c> uses, so "Support staff", "support-staff" and "SUPPORT STAFF" are one
    /// group and a stray space cannot split one in two.
    /// </summary>
    public static string Key(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}

/// <summary>
/// The outcome of a duty for one person. <see cref="Excused"/> is the MoES guidelines' "organisational
/// factors do not constitute a performance gap" and is removed from the denominator; <see cref="Recovered"/>
/// is the Lesson Recovery Schedule and offsets an <see cref="Absent"/>.
/// </summary>
public enum DutyOutcome
{
    NotApplicable = 0,
    Present = 1,
    Late = 2,
    Absent = 3,
    Excused = 4,
    Recovered = 5,
    Completed = 6,
    NotCompleted = 7
}

/// <summary>Where a record came from. <see cref="System"/> rows are labelled "automatic" on the timeline so nobody mistakes them for a colleague's judgement.</summary>
public enum RecordSource
{
    Manual = 0,
    Register = 1,
    Observation = 2,
    Recognition = 3,
    System = 4,
    Import = 5,
    /// <summary>A teacher marked their own lesson taught (duty rota plan §7.3). Shown as a self-report until a lesson supervisor confirms or overrides it.</summary>
    SelfReport = 6
}

/// <summary>
/// A record is never rewritten. <see cref="Annulled"/> is the "void" that keeps the row: a note of
/// kind Annulment says why, and the row drops out of scoring.
/// </summary>
public enum StaffRecordStatus
{
    Draft = 0,
    Final = 1,
    Annulled = 2
}

/// <summary>What a follow-up entry on a staff record is. A <see cref="Response"/> may only be written by the record's subject.</summary>
public enum StaffNoteKind
{
    Note = 0,
    Response = 1,
    VisibilityChange = 2,
    Annulment = 3,
    Moderation = 4
}

/// <summary>The termly appraisal workflow. Order is the order of the cycle; the score is evidence, the rating is a decision.</summary>
public enum AppraisalStage
{
    /// <summary>Targets agreed; evidence accumulating.</summary>
    Open = 0,
    /// <summary>The subject is rating themselves.</summary>
    SelfAssessment = 1,
    /// <summary>The appraiser is reviewing.</summary>
    AppraiserReview = 2,
    /// <summary>A Director of Studies or administrator is moderating.</summary>
    Moderation = 3,
    /// <summary>Frozen. Score, breakdown and rating never change again.</summary>
    Signed = 4,
    /// <summary>The subject appealed; reopens to Moderation.</summary>
    Appealed = 5
}

/// <summary>Who may see individual rankings. Tenant policy; the research in the plan's §1.5 is why the default is Private.</summary>
public enum LeaderboardMode
{
    /// <summary>A person sees only their own position; the school sees departments.</summary>
    Private = 0,
    /// <summary>Department averages are visible to everyone with the reports permission.</summary>
    Department = 1,
    /// <summary>A public top-N board. Off unless the tenant switches it on.</summary>
    Public = 2
}

// ---- Duty rota, duty reports and the timetable (duty rota plan §3.2, 2026-09-17). Persisted: append only. ----

/// <summary>What kind of expectation a StaffDuty is. Rota slots and lessons are duties so they share the register, reminders, portal and scoring.</summary>
public enum DutyKind
{
    /// <summary>A meeting, invigilation or prep slot — every duty before the rota existed. At most 14 days.</summary>
    Session = 0,
    /// <summary>On duty for a span: a day, a week, a month, any custom span up to 92 days.</summary>
    Rota = 1,
    /// <summary>One lesson occurrence, materialised from a published timetable. At most one day.</summary>
    Lesson = 2
}

/// <summary>How often the people on a rota slot write a report.</summary>
public enum ReportCadence
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    /// <summary>Once, at the end of the slot.</summary>
    EndOfDuty = 4
}

public enum DutyReportAuthorRole
{
    /// <summary>A person on duty, reporting on the period.</summary>
    OnDuty = 0,
    /// <summary>The administrator on duty, reporting on their supervision of the teacher(s) on duty.</summary>
    Supervisor = 1
}

public enum DutyReportStatus
{
    Draft = 0,
    Submitted = 1,
    Reviewed = 2,
    /// <summary>Sent back for changes; the author edits and resubmits, the returned version stays in the notes.</summary>
    Returned = 3,
    /// <summary>"No duty that day" — a closure or cancellation, supervisor-approved. Stops the reminder ladder.</summary>
    NoDuty = 4
}

/// <summary>A follow-up entry on a duty report. A Response only from the author; Review and Return only from a reviewer.</summary>
public enum DutyReportNoteKind
{
    Comment = 0,
    Response = 1,
    Review = 2,
    Return = 3,
    Reopen = 4
}

public enum TimetableStatus
{
    Draft = 0,
    Published = 1,
    Archived = 2,
    /// <summary>
    /// Published, and past its <c>EffectiveTo</c>. DERIVED ON READ, never stored — the same call as
    /// StaffEmploymentStatus, so the label and the dates can never disagree.
    ///
    /// Nothing about lesson materialisation changes: every query already filters on the dates, so an
    /// expired version correctly stopped producing lessons before this value existed. What it fixes is
    /// the REPORTING — the list used to show a dead version exactly as it showed the live one.
    /// Persisted rows only ever hold 0, 1 or 2.
    /// </summary>
    Expired = 3
}

/// <summary>
/// A ONE-DAY departure from the published timetable (2026-09-22). The other half of a swap: a permanent
/// trade is a re-published version, and a one-off is this.
///
/// THERE ARE ONLY TWO KINDS, AND DELIBERATELY NO "MOVED". A one-off swap between two teachers is TWO
/// covers — each takes the other's lesson at its own time — which is both simpler and more honest: a
/// one-off does not change WHEN a class is taught, only WHO teaches it, so a class, a room and a
/// cohort's day are never disturbed and the third-party problem a permanent swap has cannot arise.
/// Moving a lesson to another slot for one day IS a timetable change and goes through a re-publish.
/// </summary>
public enum LessonExceptionKind
{
    /// <summary>Somebody else teaches it on that date. The duty becomes theirs; the usual teacher has no duty that period.</summary>
    Cover = 0,
    /// <summary>It does not happen on that date. The duty is cancelled and the teacher is told.</summary>
    Cancelled = 1
}

// ---- Minutes of a meeting (2026-09-20). See Application/DTOs/MinutesDto.cs for the standards these follow. ----

/// <summary>
/// Where a set of minutes is in its life. The gap between Draft and Approved is the whole legal
/// point — minutes become the official record when the body ADOPTS them, normally at its next
/// meeting — so it is a stored status, not an inference from which fields happen to be filled.
/// </summary>
public enum MinutesStatus
{
    /// <summary>Nothing written for this meeting yet.</summary>
    None = 0,
    /// <summary>Being written. Readable by the people who may write them, and by nobody else.</summary>
    Draft = 1,
    /// <summary>Circulated to the people who were expected, for correction before adoption.</summary>
    Circulated = 2,
    /// <summary>Adopted. The official record; every change from here is an append-only correction.</summary>
    Approved = 3
}

/// <summary>How a motion was decided. Deferred and Noted are real outcomes and are lost if only carried/not-carried exist.</summary>
public enum MinutesDecisionOutcome
{
    Carried = 0,
    NotCarried = 1,
    Deferred = 2,
    /// <summary>Agreed without a formal vote — the common case in a school staff meeting.</summary>
    Noted = 3
}

/// <summary>An action point's state. Cancelled rather than deleted: the minutes said it, so the record keeps it.</summary>
public enum MinuteActionStatus
{
    Open = 0,
    Done = 1,
    Cancelled = 2
}
