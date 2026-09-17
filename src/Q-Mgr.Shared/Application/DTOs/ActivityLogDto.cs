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

    public const string DutyCreated = "staff.duty.created";
    public const string DutyUpdated = "staff.duty.updated";
    public const string DutyCancelled = "staff.duty.cancelled";
    public const string RegisterSubmitted = "staff.duty.register-submitted";
    public const string RegisterReopened = "staff.duty.register-reopened";
    public const string MinutesAttached = "staff.duty.minutes-attached";

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
    public const string TimetableCreated = "timetable.created";
    public const string TimetableLessonPlaced = "timetable.lesson-placed";
    public const string TimetableLessonRemoved = "timetable.lesson-removed";
    public const string TimetablePublished = "timetable.published";
    public const string TimetableArchived = "timetable.archived";
    public const string TimetableImported = "timetable.imported";
    public const string LessonFlagged = "staff.lesson.flagged";
    public const string LessonFlagOverridden = "staff.lesson.flag-overridden";
    public const string LessonCancelled = "staff.lesson.cancelled";
    public const string RecoveryScheduled = "staff.lesson.recovery-scheduled";
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
