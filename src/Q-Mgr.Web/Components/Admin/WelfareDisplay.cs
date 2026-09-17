using QMgr.Domain.Enums;

namespace QMgr.Web.Components.Admin;

/// <summary>
/// The one home for how welfare enums colour on screen. WelfareStatusColor lived as a private copy in
/// WelfareOpenActions and WelfareReports, with a third colour map as CSS classes in
/// StudentWelfareTimeline — three places for "Open is concern-teal" to drift apart. Tokens only, so
/// the colours follow the theme; QChip tints the background from them.
/// </summary>
public static class WelfareDisplay
{
    public static string StatusColor(WelfareStatus status) => status switch
    {
        WelfareStatus.Draft => "var(--qm-primary)",
        WelfareStatus.Open => "var(--qm-welfare-concern)",
        WelfareStatus.UnderReview => "var(--qm-accent-orange)",
        WelfareStatus.ActionTaken => "var(--qm-primary)",
        WelfareStatus.Resolved => "var(--qm-accent-green)",
        _ => "var(--qm-text-muted)"
    };
}
