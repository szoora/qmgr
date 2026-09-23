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
    /// <summary>
    /// Somebody who is not a named manager changed a timetable I manage (2026-09-22). The administrator
    /// override stays — a school cannot be locked out of its own timetable by one person's absence — so the
    /// abuse concern is answered by never letting it be silent.
    /// </summary>
    public const string StaffTimetableOverride = "staff.timetable-override";
    /// <summary>A timetable I manage is running out and nothing follows it.</summary>
    public const string StaffTimetableExpiring = "staff.timetable-expiring";
    /// <summary>A lesson of mine is covered by somebody else, or I am covering one, on one date.</summary>
    public const string StaffLessonCover = "staff.lesson-cover";
    /// <summary>The Monday lesson analysis for lesson supervisors and report readers (plan §11).</summary>
    public const string StaffLessonAnalysis = "staff.lesson-analysis";

    // ---- Staff onboarding (duty rota plan §12, 2026-09-17). ----

    /// <summary>Staff join requests are waiting for approval (approvers). Content-free: a count and a link.</summary>
    public const string StaffJoinRequests = "staff.join-requests";

    // ---- Minutes of a meeting (2026-09-20). ----

    /// <summary>Minutes of a meeting I was expected at were circulated for correction, or adopted.</summary>
    public const string StaffMinutes = "staff.minutes";

    /// <summary>An action point minuted for me is due, or overdue.</summary>
    public const string StaffMinuteAction = "staff.minute-action";

    // ---- Billing (2026-09-23). Sent only to holders of billing.view — see NotificationAudience. ----

    /// <summary>A payment was received, is being checked, or did not go through.</summary>
    public const string BillingPayment = "billing.payment";

    /// <summary>A module trial is ending soon, or has ended.</summary>
    public const string BillingTrial = "billing.trial";

    /// <summary>Everything else — system alerts, queue events, and anything sent without a key.</summary>
    public const string General = "general";

    public static readonly IReadOnlyList<NotificationEventDefinition> All = new List<NotificationEventDefinition>
    {
        new(WelfareRecordLogged, "New record for a student in my class",
            "A behaviour, achievement or support-plan record is logged for one of your students. Safeguarding records are never sent here.",
            "Student Welfare", DefaultEmail: true, DefaultSms: false, DefaultPush: true),
        new(WelfareActionOverdue, "My welfare follow-up is overdue",
            "A follow-up assigned to you has passed its due date and is still open.",
            "Student Welfare", DefaultEmail: true, DefaultSms: false, DefaultPush: true),
        new(WelfareFlagReviewOverdue, "A flag I raised needs reviewing",
            "A standing flag you raised has passed its review date.",
            "Student Welfare", DefaultEmail: false, DefaultSms: false),
        new(VisitorArrived, "A visitor has arrived for me",
            "Someone has checked in at reception to see you.",
            "Visitors", DefaultEmail: false, DefaultSms: false, DefaultPush: true),
        new(DocumentShareOpened, "A document I shared was opened",
            "The first time someone opens a share link you created. Only for links where you asked to be told.",
            "Document Library", DefaultEmail: true, DefaultSms: false),
        new(DocumentShareExpiring, "A link I created is about to expire",
            "Seven days' notice, once per link, so a document somebody is relying on does not stop working without warning.",
            "Document Library", DefaultEmail: true, DefaultSms: false),
        new(StaffRecordLogged, "A record was logged about me",
            "Attendance, duties, observations, contributions and conduct logged about you. A confidential record tells you only that one exists.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false, DefaultPush: true),
        new(StaffPointsEarned, "My points or band changed",
            "Every time a finalised record moves your points this period.",
            "Staff Performance", DefaultEmail: false, DefaultSms: false),
        new(StaffRecognitionReceived, "A colleague recognised me",
            "Someone gave you recognition and said why.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false),
        new(StaffDutyReminder, "A duty I am expected at is coming up",
            "Sent ahead of a meeting, exam session or prep slot you are expected at.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false, DefaultPush: true),
        new(StaffRegisterDue, "A register I should take is outstanding",
            "You are the named recorder for a duty that has ended with no register taken.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false, DefaultPush: true),
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
            "Duty Rota", DefaultEmail: true, DefaultSms: false, DefaultPush: true),
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
            "Lessons", DefaultEmail: true, DefaultSms: false, DefaultPush: true),
        new(StaffTimetableOverride, "Somebody else changed a timetable I manage",
            "An administrator changed a timetable you are the appointed master of.",
            "Lessons", DefaultEmail: true, DefaultSms: false),
        new(StaffTimetableExpiring, "A timetable I manage is running out",
            "A published timetable you manage ends soon and nothing follows it.",
            "Lessons", DefaultEmail: true, DefaultSms: false),
        new(StaffLessonCover, "Cover for a lesson",
            "A lesson of yours is covered by a colleague, or you are asked to cover one.",
            "Lessons", DefaultEmail: true, DefaultSms: false),
        new(StaffLessonAnalysis, "The weekly lesson analysis",
            "Mondays, for lesson supervisors and report readers: last week's lessons taught, missed, recovered and unrecorded for the staff you oversee.",
            "Lessons", DefaultEmail: true, DefaultSms: false),
        new(StaffJoinRequests, "Staff join requests are waiting",
            "For approvers: people who registered through the join link and are waiting for a decision.",
            "User Management", DefaultEmail: true, DefaultSms: false),
        new(StaffMinutes, "Minutes of a meeting I attended",
            "Draft minutes circulated for correction, and the moment they are adopted as the record of that meeting.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false),
        new(StaffMinuteAction, "An action minuted for me",
            "An action point the minutes gave you, as its date approaches and after it passes.",
            "Staff Performance", DefaultEmail: true, DefaultSms: false, DefaultPush: true),
        new(BillingPayment, "A payment for this school",
            "A payment was received, is being checked, or did not go through. Only for people who can open Billing.",
            "Billing", DefaultEmail: false, DefaultSms: false, Permission: "billing.view"),
        new(BillingTrial, "A trial is ending",
            "A module's free trial ends soon, or has ended. Only for people who can open Billing.",
            "Billing", DefaultEmail: false, DefaultSms: false, Permission: "billing.view"),
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
    bool DefaultSms,

    /// <summary>
    /// Whether this event reaches a handset's lock screen by default.
    ///
    /// <para><b>Defaults to FALSE, and the short list of exceptions is deliberate.</b> A push is an
    /// interruption somebody else chose for you, and this module has already learned what happens
    /// when a channel carries everything: eleven identical "Recognition logged about you" rows
    /// buried the appraisal that actually needed answering. So only the events a school would
    /// genuinely interrupt a member of staff for are opted in — a concern logged about a child in
    /// their class, a follow-up now overdue, a visitor waiting at reception, a register not taken, a
    /// duty about to start, an action point falling due. Digests, points movements and
    /// acknowledgements stay in the bell where they can be read when convenient.</para>
    /// </summary>
    bool DefaultPush = false,

    /// <summary>
    /// The permission a person needs to be SENT this kind of notification, when it is not everyone's.
    /// The preferences panel shows the row only to its holders — offering a teacher a switch for
    /// "a payment for this school" would say such notices exist and invite them to expect one.
    /// The sender decides the audience; this only keeps the panel honest about it.
    /// </summary>
    string? Permission = null);

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

    /// <summary>
    /// Whether this event reaches a handset. Nullable, unlike the two above, because this property
    /// arrived after people had already saved preferences: a stored row with no value must mean
    /// "follow the default" rather than "off", or shipping push would have silently opted out
    /// everybody who had ever touched the preferences panel.
    /// </summary>
    public bool? Push { get; set; }
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
    /// The master switch for notifications on a phone. Defaults on, because a handset that receives
    /// nothing is indistinguishable from a broken one and the per-event defaults are already a short
    /// list. Somebody who genuinely wants silence turns this off once rather than nine times.
    ///
    /// <para>The OS permission is a second, independent gate the server cannot see: this being on
    /// does not mean Android will display anything, which is why
    /// <c>UserDeviceSession.PushPermitted</c> exists beside it.</para>
    /// </summary>
    public bool PushEnabled { get; set; } = true;

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
