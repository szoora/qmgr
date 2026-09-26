using QMgr.Application.DTOs;

namespace QMgr.Web.Components.Admin.Staff.Plans;

/// <summary>
/// Every label and chip colour a lesson plan or scheme of work carries, in ONE place (the StaffDisplay rule), so the
/// planbook, the review queue, the editor and the print say the same thing about the same state.
/// </summary>
public static class PlanDisplay
{
    public static string Status(TeachingPlanStatus s) => TeachingPlanRules.StatusText(s);

    public static string Variant(TeachingPlanStatus? s) => s switch
    {
        TeachingPlanStatus.Approved => "success",
        TeachingPlanStatus.Submitted or TeachingPlanStatus.Forwarded => "info",
        TeachingPlanStatus.Returned => "warning",
        TeachingPlanStatus.Draft => "muted",
        _ => "default"
    };

    public static string Kind(TeachingPlanKind k) => k == TeachingPlanKind.SchemeOfWork ? "Scheme of work" : "Lesson plan";
    public static string KindWord(TeachingPlanKind k) => k == TeachingPlanKind.SchemeOfWork ? "scheme of work" : "lesson plan";

    public static string Size(long? bytes) => bytes is not { } b ? "—" : b < 1024 ? $"{b} B" : $"{b / 1024.0:0.#} KB";

    public static string Trail(PlanTrailKind k) => k switch
    {
        PlanTrailKind.Comment => "commented",
        PlanTrailKind.Submitted => "submitted",
        PlanTrailKind.Forwarded => "forwarded it for approval",
        PlanTrailKind.Approved => "approved it",
        PlanTrailKind.Returned => "returned it for changes",
        PlanTrailKind.Withdrawn => "withdrew it",
        PlanTrailKind.StageSkipped => "Head-of-department stage skipped",
        PlanTrailKind.Reflection => "wrote the self-evaluation",
        PlanTrailKind.FileAdded => "attached a PDF",
        PlanTrailKind.FileRemoved => "removed the PDF",
        PlanTrailKind.Revised => "started a revision",
        _ => k.ToString()
    };

    /// <summary>The planbook's word for a lesson's own outcome (from the lesson flags).</summary>
    public static string? Outcome(string? lessonStatus) => lessonStatus switch
    {
        "Taught" or "TaughtSelfReported" or "Recovered" => "Taught",
        "MissedWithPermission" or "MissedWithoutPermission" or "NotRecovered" or "NotTaughtSelfReported" => "Missed",
        "RecoveryScheduled" => "Recovery scheduled",
        "Cancelled" => "Cancelled",
        _ => null
    };
}
