using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Web.Components.Admin.Staff;

/// <summary>
/// The one home for how Staff Performance enums read and colour on screen, shared by the portal,
/// the admin pages and the print route so a Late is amber and a Recognition is gold everywhere.
/// Colours are --qm tokens or the welfare accent tokens already in qm-theme.css; no new hex.
/// </summary>
public static class StaffDisplay
{
    public static string KindLabel(ParameterKind kind) => kind switch
    {
        ParameterKind.Attendance => "Attendance",
        ParameterKind.Duty => "Duty",
        ParameterKind.Observation => "Observation",
        ParameterKind.Contribution => "Contribution",
        ParameterKind.Recognition => "Recognition",
        ParameterKind.Conduct => "Conduct",
        ParameterKind.Wellbeing => "Wellbeing",
        _ => kind.ToString()
    };

    /// <summary>A CSS colour for a kind, used when the parameter has no colour of its own.</summary>
    public static string KindColor(ParameterKind kind) => kind switch
    {
        ParameterKind.Attendance => "var(--qm-primary)",
        ParameterKind.Duty => "var(--qm-welfare-achievement)",
        ParameterKind.Observation => "var(--qm-welfare-concern)",
        ParameterKind.Contribution => "var(--qm-success)",
        ParameterKind.Recognition => "var(--qm-accent-yellow)",
        ParameterKind.Conduct => "var(--qm-welfare-behavior)",
        ParameterKind.Wellbeing => "var(--qm-text-muted)",
        _ => "var(--qm-text-secondary)"
    };

    public static string KindIcon(ParameterKind kind) => kind switch
    {
        ParameterKind.Attendance => "calendar-check",
        ParameterKind.Duty => "clipboard-check",
        ParameterKind.Observation => "eye",
        ParameterKind.Contribution => "plus-circle",
        ParameterKind.Recognition => "award",
        ParameterKind.Conduct => "exclamation-triangle",
        ParameterKind.Wellbeing => "heart",
        _ => "journal"
    };

    public static string OutcomeLabel(DutyOutcome outcome) => outcome switch
    {
        DutyOutcome.NotApplicable => "",
        DutyOutcome.Present => "Present",
        DutyOutcome.Late => "Late",
        DutyOutcome.Absent => "Absent",
        DutyOutcome.Excused => "Excused",
        DutyOutcome.Recovered => "Recovered",
        DutyOutcome.Completed => "Completed",
        DutyOutcome.NotCompleted => "Not completed",
        _ => outcome.ToString()
    };

    /// <summary>QChip variant for an outcome: present/completed/recovered good, late warning, absent/not-completed danger, excused muted.</summary>
    public static string OutcomeVariant(DutyOutcome outcome) => outcome switch
    {
        DutyOutcome.Present or DutyOutcome.Completed or DutyOutcome.Recovered => "success",
        DutyOutcome.Late => "warning",
        DutyOutcome.Absent or DutyOutcome.NotCompleted => "danger",
        DutyOutcome.Excused => "muted",
        _ => "default"
    };

    public static string VisibilityLabel(WelfareVisibility v) => v switch
    {
        WelfareVisibility.Standard => "Standard",
        WelfareVisibility.Confidential => "Confidential",
        WelfareVisibility.Restricted => "Restricted",
        _ => v.ToString()
    };

    public static string VisibilityVariant(WelfareVisibility v) => v switch
    {
        WelfareVisibility.Confidential => "warning",
        WelfareVisibility.Restricted => "danger",
        _ => "muted"
    };

    public static string StatusLabel(StaffRecordStatus s) => s switch
    {
        StaffRecordStatus.Draft => "Draft",
        StaffRecordStatus.Final => "Final",
        StaffRecordStatus.Annulled => "Annulled",
        _ => s.ToString()
    };

    public static string SourceLabel(RecordSource s) => s switch
    {
        RecordSource.Manual => "Logged",
        RecordSource.Register => "From register",
        RecordSource.Observation => "Observation",
        RecordSource.Recognition => "Recognition",
        RecordSource.System => "Automatic",
        RecordSource.Import => "Imported",
        _ => s.ToString()
    };

    public static string StageLabel(AppraisalStage s) => s switch
    {
        AppraisalStage.Open => "Open — targets",
        AppraisalStage.SelfAssessment => "Self-assessment",
        AppraisalStage.AppraiserReview => "Appraiser review",
        AppraisalStage.Moderation => "Moderation",
        AppraisalStage.Signed => "Signed",
        AppraisalStage.Appealed => "Appealed",
        _ => s.ToString()
    };

    public static string StageVariant(AppraisalStage s) => s switch
    {
        AppraisalStage.Signed => "success",
        AppraisalStage.Appealed => "danger",
        AppraisalStage.Moderation => "warning",
        _ => "info"
    };

    /// <summary>Band 5..1 to a QStatTile/QChip variant: 5–4 success, 3 info, 2 warning, 1 danger.</summary>
    public static string BandVariant(int? band) => band switch
    {
        5 or 4 => "success",
        3 => "info",
        2 => "warning",
        1 => "danger",
        _ => "secondary"
    };

    public static string ScopeLabel(StaffDataScope s) => s switch
    {
        StaffDataScope.Organization => "Whole organization",
        StaffDataScope.AssignedDepartments => "Assigned departments",
        StaffDataScope.DirectReports => "Direct reports",
        StaffDataScope.SelfOnly => "Self only",
        _ => s.ToString()
    };

    public static string LeaderboardLabel(LeaderboardMode m) => m switch
    {
        LeaderboardMode.Private => "Private — each person sees only their own position",
        LeaderboardMode.Department => "Departments — department averages (teams of three or more) on everyone's portal; no names",
        LeaderboardMode.Public => "Public — department averages and a top-N board of names on everyone's portal",
        _ => m.ToString()
    };

    public static string Points(int? points) => points switch
    {
        null => "",
        > 0 => $"+{points}",
        _ => points.Value.ToString()
    };

    public static string Composite(decimal? c) => c.HasValue ? c.Value.ToString("0.#") : "—";

    // ---- Lessons (duty rota plan §7.3) ----

    public static string LessonStatusLabel(LessonStatus s) => s switch
    {
        LessonStatus.Scheduled => "Scheduled",
        LessonStatus.Unrecorded => "Unrecorded",
        LessonStatus.Taught => "Taught",
        LessonStatus.TaughtSelfReported => "Taught (self-reported)",
        LessonStatus.NotTaughtSelfReported => "Not taught (self-reported)",
        LessonStatus.MissedWithPermission => "Missed, with permission",
        LessonStatus.MissedWithoutPermission => "Missed, without permission",
        LessonStatus.RecoveryScheduled => "Recovery scheduled",
        LessonStatus.Recovered => "Recovered",
        LessonStatus.NotRecovered => "Not recovered",
        LessonStatus.Cancelled => "Cancelled",
        _ => s.ToString()
    };

    public static string LessonStatusVariant(LessonStatus s) => s switch
    {
        LessonStatus.Taught or LessonStatus.Recovered => "success",
        LessonStatus.TaughtSelfReported or LessonStatus.RecoveryScheduled => "info",
        LessonStatus.Unrecorded or LessonStatus.NotTaughtSelfReported or LessonStatus.MissedWithPermission => "warning",
        LessonStatus.MissedWithoutPermission or LessonStatus.NotRecovered => "danger",
        _ => "muted"
    };
}
