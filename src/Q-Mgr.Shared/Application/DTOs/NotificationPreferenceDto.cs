namespace QMgr.Application.DTOs;

/// <summary>
/// The event categories a person can be reached about, and the stable keys their preferences are
/// stored under. Adding a key here is what makes a new kind of notification preference-aware; an
/// event sent with no key falls back to "always deliver on whatever channels the caller asked for",
/// which is the behaviour everything had before preferences existed.
///
/// These strings are persisted in <c>Users.NotificationPreferences</c>. Renaming one silently
/// orphans every stored preference for it, so treat them as a wire format, not as labels.
/// </summary>
public static class NotificationEventKeys
{
    /// <summary>A student in one of your classes has a (non-confidential) case logged.</summary>
    public const string WelfareRecordLogged = "welfare.record-logged";

    /// <summary>A welfare follow-up assigned to you is past its due date.</summary>
    public const string WelfareActionOverdue = "welfare.action-overdue";

    /// <summary>A student flag you raised is past its review date.</summary>
    public const string WelfareFlagReviewOverdue = "welfare.flag-review-overdue";

    /// <summary>A visitor has arrived to see you.</summary>
    public const string VisitorArrived = "visitor.arrived";

    /// <summary>A document share link you created was opened for the first time.</summary>
    public const string DocumentShareOpened = "documents.share-opened";
    /// <summary>A link you issued is about to stop working — seven days' notice, once per link.</summary>
    public const string DocumentShareExpiring = "documents.share-expiring";

    // ---- Staff Performance Monitor (2026-09-16). Category "Staff Performance". ----

    /// <summary>A record was logged about me (Standard: full; Confidential: existence and title only).</summary>
    public const string StaffRecordLogged = "staff.record-logged";
    /// <summary>My points or band moved. Bell only by default.</summary>
    public const string StaffPointsEarned = "staff.points-earned";
    /// <summary>A colleague recognised me.</summary>
    public const string StaffRecognitionReceived = "staff.recognition-received";
    /// <summary>A duty I am expected at is coming up.</summary>
    public const string StaffDutyReminder = "staff.duty-reminder";
    /// <summary>A duty I am the recorder for has ended and its register is not taken.</summary>
    public const string StaffRegisterDue = "staff.register-due";
    /// <summary>My appraisal moved to a stage that needs me, or is overdue.</summary>
    public const string StaffAppraisalStage = "staff.appraisal-stage";
    /// <summary>A notice was published for me.</summary>
    public const string StaffNoticePublished = "staff.notice-published";
    /// <summary>My department, line manager or role changed.</summary>
    public const string StaffProfileChanged = "staff.profile-changed";
    /// <summary>The weekly digest.</summary>
    public const string StaffWeeklyDigest = "staff.weekly-digest";

    // ---- Duty rota, duty reports, lessons and the timetable (duty rota plan §8.2, 2026-09-17). ----
    // Every one of these names the thing and links to it; none carries report text, a comment or a
    // child's name (plan §8.3).

    /// <summary>I was put on a rota slot.</summary>
    public const string StaffRotaAssigned = "staff.rota-assigned";
    /// <summary>A rota slot I am on is approaching (the escalating ladder).</summary>
    public const string StaffRotaReminder = "staff.rota-reminder";
    /// <summary>Someone I supervise on a rota slot has not acknowledged it (supervisors).</summary>
    public const string StaffRotaUnacknowledged = "staff.rota-unacknowledged";
    /// <summary>A duty report of mine is due.</summary>
    public const string StaffDutyReportDue = "staff.duty-report-due";
    /// <summary>A duty report is overdue (escalated to supervisors and heads).</summary>
    public const string StaffDutyReportOverdue = "staff.duty-report-overdue";
    /// <summary>A duty report was submitted for me to read (reviewers).</summary>
    public const string StaffDutyReportSubmitted = "staff.duty-report-submitted";
    /// <summary>A comment was added to a duty report I wrote or reviewed.</summary>
    public const string StaffDutyReportComment = "staff.duty-report-comment";
    /// <summary>My duty report was returned for changes.</summary>
    public const string StaffDutyReportReturned = "staff.duty-report-returned";
    /// <summary>A lesson of mine starts soon. Bell only by default.</summary>
    public const string StaffLessonReminder = "staff.lesson-reminder";
    /// <summary>The morning "My Day" digest.</summary>
    public const string StaffMyDay = "staff.my-day";
    /// <summary>A lesson of mine was moved or cancelled.</summary>
    public const string StaffLessonChanged = "staff.lesson-changed";
    /// <summary>Lessons of mine are still unrecorded.</summary>
    public const string StaffLessonUnrecorded = "staff.lesson-unrecorded";
    /// <summary>A timetable I teach in was published.</summary>
    public const string StaffTimetablePublished = "staff.timetable-published";
    /// <summary>New clashes in a published timetable (timetable masters).</summary>
    public const string StaffTimetableClash = "staff.timetable-clash";
    /// <summary>The Monday lesson analysis for lesson supervisors and report readers (plan §11).</summary>
    public const string StaffLessonAnalysis = "staff.lesson-analysis";

    // ---- Staff onboarding (duty rota plan §12, 2026-09-17). ----

    /// <summary>Staff join requests are waiting for approval (approvers). Content-free: a count and a link.</summary>
    public const string StaffJoinRequests = "staff.join-requests";

    /// <summary>Everything else — system alerts, queue events, and anything sent without a key.</summary>
    public const string General = "general";

    public static readonly IReadOnlyList<NotificationEventDefinition> All = new List<NotificationEventDefinition>
    {
        new(WelfareRecordLogged, "New record for a student in my class",
            "A behaviour, achievement or support-plan record is logged for one of your students. Safeguarding records are never sent here.",
            "Student Welfare", DefaultEmail: true, DefaultSms: false),
        new(WelfareActionOverdue, "My welfare follow-up is overdue",
            "A follow-up assigned to you has passed its due date and is still open.",
            "Student Welfare", DefaultEmail: true, DefaultSms: false),
        new(WelfareFlagReviewOverdue, "A flag I raised needs reviewing",
            "A standing flag you raised has passed its review date.",
            "Student Welfare", DefaultEmail: false, DefaultSms: false),
        new(VisitorArrived, "A visitor has arrived for me",
            "Someone has checked in at reception to see you.",
            "Visitors", DefaultEmail: false, DefaultSms: false),
        new(DocumentShareOpened, "A document I shared was opened",
            "The first time someone opens a share link you created. Only for links where you asked to be told.",
            "Document Library", DefaultEmail: true, DefaultSms: false),
        new(DocumentShareExpiring, "A link I created is about to expire",
            "Seven days' notice, once per link, so a document somebody is relying on does not stop working without warning.",
            "Document Library", DefaultEmail: true, DefaultSms: false),
        new(StaffRecordLogged, "A record was logged about me",
            "Attendance, duties, observations, contributions and conduct logged about you. A confidential record tells you only that one exists.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false),
        new(StaffPointsEarned, "My points or band changed",
            "Every time a finalised record moves your points this period.",
            "Staff Performance", DefaultEmail: false, DefaultSms: false),
        new(StaffRecognitionReceived, "A colleague recognised me",
            "Someone gave you recognition and said why.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false),
        new(StaffDutyReminder, "A duty I am expected at is coming up",
            "Sent ahead of a meeting, exam session or prep slot you are expected at.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false),
        new(StaffRegisterDue, "A register I should take is outstanding",
            "You are the named recorder for a duty that has ended with no register taken.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false),
        new(StaffAppraisalStage, "My appraisal needs me",
            "Self-assessment open, appraiser review awaited, moderation done, signed, or overdue.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false),
        new(StaffNoticePublished, "A staff notice was published for me",
            "A notice to your branch, department, role or staff group.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false),
        new(StaffProfileChanged, "My department, line manager or role changed",
            "Somebody changed who appraises you or which department you belong to.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false),
        new(StaffWeeklyDigest, "My weekly digest",
            "Recognition received, points and band movement, duties coming up, and anything awaiting your response.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false),
        new(StaffRotaAssigned, "I was put on the duty rota",
            "You were assigned to a rota slot, or a slot was swapped to you.",
            "Duty Rota", DefaultEmail: true, DefaultSms: false),
        new(StaffRotaReminder, "My duty is coming up",
            "Reminders before a rota slot you are on. They grow more urgent as it approaches and stop once you acknowledge.",
            "Duty Rota", DefaultEmail: true, DefaultSms: false),
        new(StaffRotaUnacknowledged, "Someone I supervise has not acknowledged their duty",
            "For the administrator on duty, shortly before the slot starts.",
            "Duty Rota", DefaultEmail: true, DefaultSms: false),
        new(StaffDutyReportDue, "My duty report is due",
            "A report for a duty you are on has reached its due time.",
            "Duty Rota", DefaultEmail: true, DefaultSms: false),
        new(StaffDutyReportOverdue, "A duty report is overdue",
            "Your report, or one you supervise, is past due.",
            "Duty Rota", DefaultEmail: true, DefaultSms: false),
        new(StaffDutyReportSubmitted, "A duty report was submitted for me to read",
            "For supervisors and reviewers. The message names the report, never its content.",
            "Duty Rota", DefaultEmail: false, DefaultSms: false),
        new(StaffDutyReportComment, "A comment on a duty report",
            "Someone commented on a report you wrote or review.",
            "Duty Rota", DefaultEmail: true, DefaultSms: false),
        new(StaffDutyReportReturned, "My duty report was returned for changes",
            "A reviewer asked you to revise a report.",
            "Duty Rota", DefaultEmail: true, DefaultSms: false),
        new(StaffLessonReminder, "A lesson of mine starts soon",
            "A few minutes before each lesson. In-app by default: the morning digest already carries the day.",
            "Lessons", DefaultEmail: false, DefaultSms: false),
        new(StaffMyDay, "My School Day — the morning digest",
            "Today's lessons, duties and reports due, in one message.",
            "Lessons", DefaultEmail: true, DefaultSms: false),
        new(StaffLessonChanged, "A lesson of mine moved or was cancelled",
            "Sent when the day's timetable changes after the morning digest.",
            "Lessons", DefaultEmail: false, DefaultSms: false),
        new(StaffLessonUnrecorded, "Lessons of mine are unrecorded",
            "Past lessons with no taught or missed mark.",
            "Lessons", DefaultEmail: true, DefaultSms: false),
        new(StaffTimetablePublished, "My timetable was published",
            "A timetable you teach in was published or replaced.",
            "Lessons", DefaultEmail: true, DefaultSms: false),
        new(StaffTimetableClash, "New timetable clashes",
            "For timetable masters: new clashes found in a published timetable.",
            "Lessons", DefaultEmail: true, DefaultSms: false),
        new(StaffLessonAnalysis, "The weekly lesson analysis",
            "Mondays, for lesson supervisors and report readers: last week's lessons taught, missed, recovered and unrecorded for the staff you oversee.",
            "Lessons", DefaultEmail: true, DefaultSms: false),
        new(StaffJoinRequests, "Staff join requests are waiting",
            "For approvers: people who registered through the join link and are waiting for a decision.",
            "User Management", DefaultEmail: true, DefaultSms: false),
        new(General, "Everything else",
            "System alerts and anything not covered above.",
            "General", DefaultEmail: false, DefaultSms: false),
    };

    public static bool IsKnown(string? key) => key != null && All.Any(e => e.Key == key);
}

public record NotificationEventDefinition(
    string Key,
    string Name,
    string Description,
    string Category,
    bool DefaultEmail,
    bool DefaultSms);

/// <summary>
/// One person's channel choices for one event category. The in-app bell is deliberately NOT
/// switchable: it is the record that the person was told, it costs nothing, and a notification
/// nobody can find later is worse than one nobody wanted.
/// </summary>
public record NotificationChannelPreference
{
    public string EventKey { get; set; } = string.Empty;
    public bool Email { get; set; }
    public bool Sms { get; set; }
}

/// <summary>
/// The whole preference blob for one user, as stored in <c>Users.NotificationPreferences</c> and as
/// rendered by the preferences panel. An event with no entry follows the organization default.
/// </summary>
public record UserNotificationPreferencesDto
{
    public List<NotificationChannelPreference> Events { get; set; } = new();

    /// <summary>
    /// A single master switch for email, applied on top of the per-event choices. Exists because
    /// "stop emailing me while I am on leave" is a thing people actually want, and hunting through
    /// five toggles to do it is how you end up with someone filtering the whole app to spam.
    /// </summary>
    public bool EmailEnabled { get; set; } = true;

    /// <summary>The same, for SMS. SMS costs the tenant money per message, so this defaults on but the per-event defaults are off.</summary>
    public bool SmsEnabled { get; set; } = true;

    /// <summary>
    /// When the Staff Performance weekly digest was last sent to this person (UTC). The idempotency
    /// gate for StaffPerformanceJobs.SendWeeklyDigestsAsync: lives in the preferences blob rather
    /// than a new column because it is per-person notification state, and is read and written ONLY
    /// through INotificationPreferenceResolver like everything else in this record. Not shown on
    /// the preferences panel.
    /// </summary>
    public DateTime? LastStaffDigestSentAt { get; set; }
}

/// <summary>
/// One delivery attempt, read back from <c>NotificationLog</c>. The answer to "was this actually
/// delivered?", which before 2026-09-09 had no answer at all: the table existed as a DbSet and
/// nothing in the codebase ever wrote a row to it.
/// </summary>
public record NotificationDeliveryDto
{
    public Guid Id { get; init; }
    public Guid NotificationId { get; init; }

    /// <summary>"Sms", "Email", "InApp", "Push". A string, not the enum — NotificationChannel lives in Q-Mgr.API, and this DTO crosses to Q-Mgr.Web, matching how NotificationDto already carries Type and Priority.</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>Masked before it leaves the API — an admin needs to see WHICH address failed, not to harvest a staff directory from the delivery log.</summary>
    public string Recipient { get; init; } = string.Empty;

    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int RetryCount { get; init; }
    public DateTime? LastRetryAt { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>The notification this attempt belongs to, so the delivery view reads as something a person can act on rather than a list of GUIDs.</summary>
    public string NotificationTitle { get; init; } = string.Empty;
}
