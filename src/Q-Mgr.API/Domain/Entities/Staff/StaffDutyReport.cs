using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// A duty report (duty rota plan §4.3): one per author per report period of a rota slot. A table because it has its
/// own lifecycle (draft, submitted, returned, reviewed, "no duty that day"), several exist per slot, and it is read
/// and reviewed on its own — no existing row could carry it.
///
/// Append-only once submitted: the text is locked, corrections are Response notes, a return keeps the returned
/// version in its note. Incidents about children are LINKS to welfare records (<see cref="LinkedWelfareRecordIds"/>),
/// never typed in; a reader sees a linked record only if their own welfare access allows it.
///
/// Who may read it lives in <c>StaffDutyReportAccess</c> and nowhere else: the controller and the upload authorizer
/// both call it, so a report and its evidence can never disagree.
/// </summary>
public class StaffDutyReport : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid DutyId { get; set; }
    public Guid AuthorUserId { get; set; }
    public DutyReportAuthorRole AuthorRole { get; set; }

    /// <summary>The first and last branch-local day the report covers.</summary>
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>The period's last day at the slot's (or the policy's) due time, in UTC — the reminder ladder's anchor.</summary>
    public DateTime DueAt { get; set; }

    public DutyReportStatus Status { get; set; } = DutyReportStatus.Draft;

    /// <summary>jsonb: { "sectionKey": "answer", … } against the tenant's template (plan §15 decision 1).</summary>
    public string SectionsJson { get; set; } = "{}";

    public string? Summary { get; set; }

    /// <summary>Welfare or discipline records the author linked — ids only, never copied content.</summary>
    public Guid[] LinkedWelfareRecordIds { get; set; } = Array.Empty<Guid>();

    /// <summary>Standard or Confidential (the author marks it). Restricted is not offered on a duty report.</summary>
    public WelfareVisibility Visibility { get; set; } = WelfareVisibility.Standard;

    public DateTime? SubmittedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }

    /// <summary>The highest overdue-reminder stage sent (plan §4.4). Claimed by a conditional UPDATE.</summary>
    public int ReminderStage { get; set; }
    public DateTime? LastReminderAt { get; set; }

    public virtual StaffDuty? Duty { get; set; }
    public virtual ICollection<StaffDutyReportNote> Notes { get; set; } = new List<StaffDutyReportNote>();
    public virtual ICollection<StaffDutyReportAttachment> Attachments { get; set; } = new List<StaffDutyReportAttachment>();
}

/// <summary>
/// A comment, response, review, return or reopen on a duty report (plan §3.2). Append-only. A Response only from the
/// report's author; Review and Return only from a reviewer. A Return keeps the returned text in <see cref="SnapshotJson"/>
/// so "the returned version stays in the notes" is literally true.
/// </summary>
public class StaffDutyReportNote : BaseEntity
{
    public Guid ReportId { get; set; }
    public Guid AuthorUserId { get; set; }
    public DutyReportNoteKind Kind { get; set; }
    public string Body { get; set; } = string.Empty;

    /// <summary>For a Return: the sections and summary as they were when returned.</summary>
    public string? SnapshotJson { get; set; }

    public virtual StaffDutyReport? Report { get; set; }
}

/// <summary>
/// Evidence on a duty report — a photograph of a broken window, a signed roll-call sheet. <c>WelfareAttachment</c>'s
/// columns against a report, in its own table so <c>UploadAuthorizer</c> can classify the file by the table that
/// points at it (UploadOwnerKind.DutyReportAttachment).
/// </summary>
public class StaffDutyReportAttachment : BaseEntity
{
    public Guid ReportId { get; set; }

    public string FileUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    public Guid UploadedByUserId { get; set; }

    public virtual StaffDutyReport? Report { get; set; }
}
