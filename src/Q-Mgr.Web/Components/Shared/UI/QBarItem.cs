namespace QMgr.Web.Components.Shared.UI;

/// <summary>
/// One row of a <see cref="QBarList"/>. <paramref name="Count"/> is the text shown after the bar
/// (the raw Value formatted when null); <paramref name="Color"/> any CSS colour for the fill;
/// <paramref name="Href"/> makes the label a link; <paramref name="Hint"/> is muted text after the
/// label; <paramref name="Tag"/> is whatever a RowSuffix template needs to read back.
/// </summary>
public record QBarItem(
    string Label,
    decimal Value,
    string? Count = null,
    string? Color = null,
    string? Href = null,
    string? Hint = null,
    object? Tag = null);
