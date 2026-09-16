namespace QMgr.Web.Components.Shared.UI;

/// <summary>
/// One tab of a <see cref="QTabs"/> strip. Badge is a count shown after the label; BadgeVariant
/// tints it (primary when null, or "danger" for a count that asks somebody to act).
/// </summary>
public record QTab(string Key, string Label, string? Icon = null, int? Badge = null, string? BadgeVariant = null);
