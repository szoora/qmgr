namespace QMgr.Web.Components.Shared.UI;

/// <summary>
/// How much of a timeline is on screen, and the "Load older" step.
///
/// <para><b>Why a timeline pages at all.</b> Every item in a <see cref="QTimeline"/> is a card with
/// a marker, an optional day header, chips and its own actions — a real component subtree, not a
/// row. A staff file or a student's welfare chronology grows without limit, so rendering the whole
/// history built hundreds of subtrees on first paint. On Blazor Server that is not just layout: the
/// renderer diffs every one of them and ships the result down a SignalR circuit, so the cost lands
/// on the wire and on a school's connection rather than on the browser alone.</para>
///
/// <para>Three pages hold a timeline — the staff file, the student welfare chronology and a
/// person's own portal — and each would otherwise keep its own counter, its own page size and its
/// own idea of when to reset. This is that one home. It deliberately pages what is RENDERED rather
/// than what is fetched: the queries are already scoped by period and the payloads are small, so
/// the expensive half is the render, and paging it needs no round trip to show the next page.</para>
///
/// <para>Usage on a page:
/// <code>
/// private readonly QTimelinePaging paging = new();
/// // in the load, once the list is known:  paging.Reset();
/// // in the markup:  @foreach (var x in Entries.Take(paging.Shown))
/// // on the component: HasMore="@paging.HasMore(total)" OnLoadOlder="@paging.ShowMore"
/// </code>
/// </para>
/// </summary>
public sealed class QTimelinePaging
{
    /// <summary>
    /// How many items a page shows before the reader asks for more. Twenty-five is about two
    /// screens on a desktop: enough that the control is rarely needed, few enough that the first
    /// paint is cheap.
    /// </summary>
    public const int PageSize = 25;

    /// <summary>How many items are currently rendered.</summary>
    public int Shown { get; private set; } = PageSize;

    /// <summary>True when the list is longer than what is on screen.</summary>
    public bool HasMore(int total) => total > Shown;

    /// <summary>Reveals the next page. No fetch — the items are already in hand.</summary>
    public void ShowMore() => Shown += PageSize;

    /// <summary>
    /// Back to the first page. Call this whenever the underlying list CHANGES — a new period, a
    /// different person, a branch switch — or the reader keeps whatever depth they had scrolled to
    /// on a list that is no longer the same list.
    /// </summary>
    public void Reset() => Shown = PageSize;
}
