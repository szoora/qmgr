namespace QMgr.Web.Components.Shared.UI;

/// <summary>
/// One event in a <see cref="QActivityLog"/>: when, who, what happened, optional detail, where it
/// came from (address / browser) and whether it was a refused attempt. <paramref name="Ref"/> is an
/// optional per-row reference column (the share link an event belongs to, say) shown under the
/// log's RefHeader when any row carries one.
/// </summary>
public record QActivityRow(
    DateTime At,
    string Actor,
    string Summary,
    string? Detail,
    string? Where,
    bool Failed = false,
    string? Ref = null);
