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
}
