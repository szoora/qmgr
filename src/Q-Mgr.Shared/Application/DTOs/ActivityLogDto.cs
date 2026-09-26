namespace QMgr.Application.DTOs;

/// <summary>
/// One row of the activity log: who did what, to which record, from where. The Summary is
/// already written at the actor's visibility, so a reader without the rung sees "Restricted
/// record created for J. Okello" and never its content.
/// </summary>
public record ActivityEventDto
{
    public Guid Id { get; init; }
    public Guid? ActorUserId { get; init; }
    public string? ActorName { get; init; }
    public Guid? SubjectUserId { get; init; }
    public string? SubjectName { get; init; }
    /// <summary>A stable constant such as <c>staff.record.created</c>; see ActivityActions.</summary>
    public string Action { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public Guid? EntityId { get; init; }
    public string Summary { get; init; } = string.Empty;
    /// <summary>The rung of what the event is about. The summary is already redacted to it; the subject never receives a Restricted one.</summary>
    public QMgr.Domain.Enums.WelfareVisibility Visibility { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public DateTime OccurredAt { get; init; }
}

public record ActivityLogPageDto
{
    public List<ActivityEventDto> Items { get; init; } = new();
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    /// <summary>Days after which IpAddress and UserAgent are blanked; the row stays.</summary>
    public int AttributionRetentionDays { get; init; }
    public Dictionary<string, int> CountsByAction { get; init; } = new();
    public List<string> ScopedToDepartments { get; init; } = new();
}

/// <summary>
/// The action constants. Persisted on ActivityEvent.Action, so they are a wire format: never rename one.
/// </summary>
public static class ActivityActions
{
    public const string SignedIn = "auth.signed-in";
    public const string SignedOut = "auth.signed-out";

    public const string RecordCreated = "staff.record.created";
    public const string RecordFinalized = "staff.record.finalized";
    public const string RecordNoteAdded = "staff.record.note-added";
    public const string RecordResponded = "staff.record.responded";
    public const string RecordAcknowledged = "staff.record.acknowledged";
    public const string RecordAnnulled = "staff.record.annulled";
    public const string RecordVisibilityChanged = "staff.record.visibility-changed";
    public const string RecordPointsCorrected = "staff.record.points-corrected";
    public const string RecordEvidenceAdded = "staff.record.evidence-added";
    public const string RecordViewed = "staff.record.viewed";
    public const string TimelineViewed = "staff.timeline.viewed";
    public const string TimelineExported = "staff.timeline.exported";
    public const string RecognitionGiven = "staff.recognition.given";

    // Access (2026-09-24): the leadership posts, the access review, and the leaver sweep.
    public const string SafeguardingLeadsChanged = "access.safeguarding-leads-changed";
    public const string ActingHeadChanged = "access.acting-head-changed";
    public const string PastoralPostsChanged = "access.pastoral-posts-changed";
    public const string AccessReviewed = "access.reviewed";
    public const string LeaverDeactivated = "access.leaver-deactivated";
    /// <summary>One record logged for a group of staff (the Staff Directory's "Log a record", 2026-09-23) — ONE line for the batch, naming no individual; each record still writes its own RecordCreated.</summary>
    public const string RecordsBulkLogged = "staff.records.bulk-logged";

    public const string DutyCreated = "staff.duty.created";
    public const string DutyUpdated = "staff.duty.updated";
    public const string DutyCancelled = "staff.duty.cancelled";
    /// <summary>An exam-supervision series was created, renamed, or given different managers (2026-09-23).</summary>
    public const string DutySeriesSaved = "staff.duty-series.saved";
    public const string RegisterSubmitted = "staff.duty.register-submitted";
    public const string RegisterReopened = "staff.duty.register-reopened";
    public const string MinutesAttached = "staff.duty.minutes-attached";
    // Minutes of a meeting (2026-09-20). One event per transition, because the transitions are what
    // make a set of minutes the official record and "who adopted this, and when" has to be answerable.
    public const string MinutesSaved = "staff.duty.minutes-saved";
    public const string MinutesCirculated = "staff.duty.minutes-circulated";
    public const string MinutesApproved = "staff.duty.minutes-approved";
    public const string MinutesCorrected = "staff.duty.minutes-corrected";
    public const string MinuteActionCompleted = "staff.duty.minute-action-completed";

    // Staff self-service configuration (2026-09-21). Every write a member of staff makes to shared
    // data is attributable: forty writers instead of one means "who changed this" starts being asked.
    public const string SelfServiceDeclared = "staff.self-service.declared";
    public const string SelfServiceClaimed = "staff.self-service.claimed";
    public const string SelfServiceReleased = "staff.self-service.released";
    public const string SelfServiceRequested = "staff.self-service.requested";
    public const string SelfServiceDecided = "staff.self-service.decided";
    public const string SelfServiceWithdrawn = "staff.self-service.withdrawn";

    public const string AppraisalOpened = "staff.appraisal.opened";
    public const string AppraisalTargetsSet = "staff.appraisal.targets-set";
    public const string AppraisalSelfSubmitted = "staff.appraisal.self-submitted";
    public const string AppraisalReviewed = "staff.appraisal.reviewed";
    public const string AppraisalModerated = "staff.appraisal.moderated";
    public const string AppraisalSigned = "staff.appraisal.signed";
    public const string AppraisalAppealed = "staff.appraisal.appealed";
    public const string AppraisalReportPublished = "staff.appraisal.report-published";

    public const string NoticePublished = "staff.notice.published";
    public const string NoticeUpdated = "staff.notice.updated";
    public const string NoticeWithdrawn = "staff.notice.withdrawn";
    public const string NoticeAcknowledged = "staff.notice.acknowledged";

    public const string StructureDepartmentSaved = "staff.structure.department-saved";
    public const string StructureMemberUpdated = "staff.structure.member-updated";
    public const string ParameterSaved = "staff.parameter.saved";
    public const string PolicySaved = "staff.policy.saved";
    public const string StaffImportStarted = "staff.import.started";
    public const string FileExported = "staff.file.exported";
    public const string ReportPublished = "staff.report.published";
    public const string ListExported = "staff.list.exported";

    public const string PeriodClosed = "staff.period.closed";
    public const string PeriodReopened = "staff.period.reopened";
    public const string RoleChanged = "staff.structure.role-changed";

    /// <summary>
    /// An administrator wrote a member of staff's own record — employment terms, qualification,
    /// registration number, contact detail. Confidential: the summary names the FIELDS that moved
    /// and never their values, and the values sit in DetailJson, which no endpoint returns.
    /// </summary>
    public const string StaffProfileUpdated = "staff.profile.updated";

    /// <summary>A member of staff maintained their own contact detail from their portal.</summary>
    public const string StaffContactSelfUpdated = "staff.profile.contact-self-updated";

    /// <summary>A member of staff was created from the Staff Directory rather than Users & Roles.</summary>
    public const string StaffMemberCreated = "staff.structure.member-created";

    // ---- Onboarding (duty rota plan §12, 2026-09-17). Summaries never carry an applicant's details. ----
    public const string PasswordChanged = "auth.password-changed";
    public const string TemporaryPasswordIssued = "onboarding.temporary-password-issued";
    public const string InvitationIssued = "onboarding.invitation-issued";
    public const string JoinLinkRotated = "onboarding.join-link-rotated";
    public const string JoinSettingsSaved = "onboarding.settings-saved";
    public const string JoinRequested = "onboarding.join-requested";
    public const string JoinApproved = "onboarding.join-approved";
    public const string JoinRejected = "onboarding.join-rejected";
    public const string JoinRequestsPurged = "onboarding.join-requests-purged";
    public const string PhoneConfirmed = "auth.phone-confirmed";
    public const string ProfilePhotoChanged = "auth.profile-photo-changed";

    // ---- Subjects and assignments (plan §5). ----
    public const string SubjectSaved = "staff.subject.saved";
    public const string SubjectTeacherAssigned = "staff.subject-teacher.assigned";
    public const string SubjectTeacherEnded = "staff.subject-teacher.ended";

    // ---- Duty rota and reports (plan §4). Report text, comments and linked records never appear in a summary. ----
    public const string RotaGenerated = "staff.rota.generated";
    public const string RotaExtended = "staff.rota.extended";
    public const string RotaSeriesCancelled = "staff.rota.series-cancelled";
    public const string RotaSwapped = "staff.rota.swapped";
    public const string DutyAcknowledged = "staff.duty.acknowledged";
    public const string TeachingPlanCreated = "staff.plan.created";
    public const string TeachingPlanSubmitted = "staff.plan.submitted";
    public const string TeachingPlanForwarded = "staff.plan.forwarded";
    public const string TeachingPlanApproved = "staff.plan.approved";
    public const string TeachingPlanReturned = "staff.plan.returned";
    public const string TeachingPlanWithdrawn = "staff.plan.withdrawn";
    public const string TeachingPlanStageSkipped = "staff.plan.stage-skipped";
    public const string TeachingPlanRevised = "staff.plan.revised";
    public const string TeachingPlanFileAdded = "staff.plan.file-added";
    public const string TeachingPlanSettingsChanged = "staff.plan.settings-changed";
    public const string CurriculumChanged = "staff.curriculum.changed";
    public const string DutyReportSubmitted = "staff.duty-report.submitted";
    public const string DutyReportViewed = "staff.duty-report.viewed";
    public const string DutyReportCommented = "staff.duty-report.commented";
    public const string DutyReportReturned = "staff.duty-report.returned";
    public const string DutyReportReviewed = "staff.duty-report.reviewed";
    public const string DutyReportReopened = "staff.duty-report.reopened";
    public const string DutyReportNoDuty = "staff.duty-report.no-duty";
    public const string DutyReportEvidenceAdded = "staff.duty-report.evidence-added";
    public const string DutyReportPackPublished = "staff.duty-report.pack-published";

    // ---- Timetable and lessons (plan §6, §7). ----
    public const string TimetableSettingsSaved = "timetable.settings-saved";
    public const string TimetableRoomsSaved = "timetable.rooms-saved";
    public const string TimetableCreated = "timetable.created";
    public const string TimetableLessonPlaced = "timetable.lesson-placed";
    public const string TimetableLessonRemoved = "timetable.lesson-removed";
    public const string TimetablePublished = "timetable.published";
    public const string TimetableArchived = "timetable.archived";
    public const string TimetableImported = "timetable.imported";
    /// <summary>The appointed master(s) of a version changed (2026-09-22).</summary>
    public const string TimetableManagersSet = "timetable.managers-set";
    /// <summary>
    /// A write by somebody who is not a named manager of the version (2026-09-22). The override is allowed —
    /// a school must not be locked out of its own timetable — so this line, and the notice to the managers,
    /// is the whole control. NIST SP 800-53 AC-5's maker–checker intent without a two-person workflow.
    /// </summary>
    public const string TimetableOverridden = "timetable.overridden";
    /// <summary>A one-day cover or cancellation against a published timetable (2026-09-22).</summary>
    public const string TimetableExceptionSet = "timetable.exception-set";
    public const string TimetableExceptionWithdrawn = "timetable.exception-withdrawn";
    /// <summary>The integrity sweep's daily count of a published timetable's clashes — the timetable health trend (plan §11).</summary>
    public const string TimetableChecked = "timetable.checked";
    public const string LessonFlagged = "staff.lesson.flagged";
    public const string LessonFlagOverridden = "staff.lesson.flag-overridden";
    public const string LessonCancelled = "staff.lesson.cancelled";
    public const string RecoveryScheduled = "staff.lesson.recovery-scheduled";

    // ---- The school calendar (calendar-audiences plan, B22, 2026-09-26). Event CRUD wrote no log line at all. ----
    public const string CalendarEventCreated = "calendar.event-created";
    public const string CalendarEventUpdated = "calendar.event-updated";
    public const string CalendarEventCancelled = "calendar.event-cancelled";
    public const string CalendarEventReinstated = "calendar.event-reinstated";
    public const string CalendarEventDeleted = "calendar.event-deleted";
    /// <summary>An event was turned into a meeting with a register ("Give this a register", E10).</summary>
    public const string CalendarEventRegisterGiven = "calendar.event-register-given";

    // ---- The Import inbox (E11) ----
    public const string ImportSubmitted = "imports.submitted";
    public const string ImportSectionApproved = "imports.section-approved";
    public const string ImportSectionRejected = "imports.section-rejected";
}

/// <summary>The things a page can report it exported or published (POST …/staff/activity/exports). Wire format.</summary>
public static class StaffExportKinds
{
    public const string Directory = "directory";
    public const string Records = "records";
    public const string Timeline = "timeline";
    public const string Reports = "reports";
    public const string NoticeAcknowledgements = "notice-acknowledgements";
    public const string Activity = "activity";
    /// <summary>A printed or published pack of duty reports (duty rota plan §4.3).</summary>
    public const string DutyReports = "duty-reports";
    /// <summary>A printed or published timetable (duty rota plan §6, §13.13).</summary>
    public const string Timetable = "timetable";
    /// <summary>A printed or published lessons report (Annex 4 and 5, duty rota plan §11).</summary>
    public const string TeachingReports = "teaching-reports";
    /// <summary>
    /// A published appraisal report — one named person's ratings, comments and moderation. Its own
    /// kind since 2026-09-18: it used to be recorded as a "timeline", which read wrong in the
    /// activity log and made a published appraisal indistinguishable from a performance file.
    /// </summary>
    public const string Appraisal = "appraisal";
    /// <summary>
    /// Printed or published minutes of a meeting (2026-09-20). Its own kind rather than "duty-reports":
    /// the two are gated differently and reading "a duty report pack was published" when somebody
    /// published the minutes of the staff meeting would be the wrong entry in the log.
    /// </summary>
    public const string Minutes = "minutes";
}

public record RecordStaffExportRequest
{
    public string Kind { get; set; } = string.Empty;
    /// <summary>The person the export is about (a timeline), checked against the caller's scope.</summary>
    public Guid? SubjectUserId { get; set; }
    /// <summary>CSV, XLSX, PDF.</summary>
    public string? Format { get; set; }
    public int? RowCount { get; set; }
    /// <summary>True for Publish to Library, with the new document's id and name.</summary>
    public bool Published { get; set; }
    public Guid? MediaContentId { get; set; }
    public string? DocumentName { get; set; }
    public string? PeriodKey { get; set; }
}

// ---- Welfare (2026-09-18) ---------------------------------------------------------------------
// The staff constants above are all "staff.*" and every one of their permission gates is about a
// member of staff, so a published welfare report could not be recorded through them without writing
// an activity row about a CHILD into the staff log. These are the welfare-side equivalents.

/// <summary>
/// Welfare activity actions. Persisted on <c>ActivityEvent.Action</c>, so a wire format: never rename one.
/// </summary>
public static class WelfareActivityActions
{
    /// <summary>A welfare report rendered in the browser and published into the Document Library.</summary>
    public const string ReportPublished = "welfare.report.published";

    /// <summary>A welfare list (the records search) exported as CSV/XLSX/PDF.</summary>
    public const string ListExported = "welfare.list.exported";

    /// <summary>One named student's welfare chronology exported or printed.</summary>
    public const string TimelineExported = "welfare.timeline.exported";

    /// <summary>One record logged for a group of students (the roster's "Log a record", 2026-09-23) — ONE line for the batch, naming no child.</summary>
    public const string RecordsBulkLogged = "welfare.records.bulk-logged";
}

/// <summary>The things a welfare page can report it exported or published. Wire format.</summary>
public static class WelfareExportKinds
{
    /// <summary>The welfare records search on Welfare Reports.</summary>
    public const string Records = "records";

    /// <summary>One student's welfare chronology — the A4 report route.</summary>
    public const string Timeline = "timeline";

    /// <summary>The open-actions list.</summary>
    public const string OpenActions = "open-actions";
}

/// <summary>
/// What a welfare page reports after producing a file in the browser. Mirrors
/// <see cref="RecordStaffExportRequest"/>, but its subject is a STUDENT: the endpoint checks that
/// student against <c>IStudentScopeService</c>, so a class teacher cannot write a line about a child
/// they could not have exported in the first place.
/// </summary>
public record RecordWelfareExportRequest
{
    public string Kind { get; set; } = string.Empty;
    /// <summary>The student the export is about (a timeline), checked against the caller's class scope.</summary>
    public Guid? SubjectStudentId { get; set; }
    /// <summary>CSV, XLSX, PDF.</summary>
    public string? Format { get; set; }
    public int? RowCount { get; set; }
    /// <summary>True for Publish to Library, with the new document's id and name.</summary>
    public bool Published { get; set; }
    public Guid? MediaContentId { get; set; }
    public string? DocumentName { get; set; }
    /// <summary>The period the export covered, as the page captioned it.</summary>
    public string? PeriodCaption { get; set; }
}

/// <summary>
/// Bulk-import activity. Persisted on <c>ActivityEvent.Action</c>, so a wire format: never rename one.
///
/// Added 2026-09-18: until then a bulk import — which can create hundreds of LOGIN ACCOUNTS in one
/// go, or backfill a school's whole welfare history — wrote no audit row at all. The job's live
/// SignalR progress told you what was happening WHILE it ran and nothing afterwards said it had.
/// </summary>
public static class ImportActivityActions
{
    /// <summary>A bulk import was accepted and queued. Carries the row count and the file name.</summary>
    public const string Started = "import.started";

    /// <summary>It finished. Carries the full tally: created, updated, duplicates, failed.</summary>
    public const string Completed = "import.completed";

    /// <summary>It could not run at all — a malformed payload, or a row set that would not deserialize.</summary>
    public const string Failed = "import.failed";
}
