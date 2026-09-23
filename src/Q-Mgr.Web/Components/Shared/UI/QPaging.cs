namespace QMgr.Web.Components.Shared.UI;

/// <summary>
/// The page-side half of <see cref="QPager"/>: which rows of an in-memory list are on screen
/// (plan <c>docs/plans/STUDENT_ROSTER_AND_LIST_STANDARD.md</c> §1).
///
/// <para><b><see cref="Reset"/> must run on EVERY filter, search and branch change.</b> A reader on page
/// 7 who narrows the list to twelve rows is otherwise left on a page that no longer exists, looking at
/// nothing. <see cref="ClampTo"/> is the safety net for a list that shrinks for any other reason (a row
/// deleted, a reload), never a substitute for resetting on a filter change.</para>
///
/// <para>Usage on a page:
/// <code>
/// private readonly QPaging paging = new();
/// // markup:  @foreach (var x in paging.Apply(Visible)) { … }
/// // pager:   &lt;QPager Count="@Visible.Count" PageSize="@paging.PageSize" PageIndex="@paging.PageIndex"
/// //                  PageIndexChanged="@(i =&gt; paging.PageIndex = i)" PageSizeChanged="@paging.SetSize"
/// //                  PageSizeOptions="@QPagerDefaults.Sizes" AllowAll="true" StorageKey="…" /&gt;
/// // on a filter change: paging.Reset();
/// </code></para>
/// </summary>
public sealed class QPaging
{
    /// <summary>
    /// A page showing All above this many rows renders them through <c>&lt;Virtualize&gt;</c>. Every row of a
    /// Blazor Server list is diffed and shipped down the SignalR circuit, so 1,711 rows drawn at once would
    /// freeze the page on a school's connection; below this the plain loop is cheaper than the viewport maths.
    /// </summary>
    public const int VirtualizeAbove = 200;

    /// <summary>Zero-based.</summary>
    public int PageIndex { get; set; }

    /// <summary>Rows on a page; <see cref="QPager.All"/> (0) shows every row.</summary>
    public int PageSize { get; set; } = QPagerDefaults.Size;

    public bool IsAll => PageSize == QPager.All;

    /// <summary>True when every row is shown and there are enough of them to need <c>&lt;Virtualize&gt;</c>.</summary>
    public bool ShouldVirtualize(int count) => IsAll && count > VirtualizeAbove;

    /// <summary>The rows on the current page — or all of them.</summary>
    public IEnumerable<T> Apply<T>(IEnumerable<T> rows) => Page(rows, PageIndex, PageSize);

    // The static face is for the pages that already kept their own `pageIndex` and `PageSize` fields
    // before 2026-09-23 — the sweep moved them onto these rather than rewriting each page around an
    // instance. Both know that 0 is All: the hand-written `(count - 1) / PageSize` they replace divides by it.

    /// <summary>The rows on page <paramref name="pageIndex"/> of <paramref name="pageSize"/> — or all of them.</summary>
    public static IEnumerable<T> Page<T>(IEnumerable<T> rows, int pageIndex, int pageSize) =>
        pageSize <= 0 ? rows : rows.Skip(Math.Max(0, pageIndex) * pageSize).Take(pageSize);

    /// <summary>
    /// <paramref name="pageIndex"/>, pulled back onto the last page when the list has shrunk under it. Pure —
    /// safe to read from a render — where <see cref="ClampTo"/> moves the stored index.
    /// </summary>
    public static int SafeIndex(int pageIndex, int count, int pageSize) =>
        pageSize <= 0 || count <= 0 ? 0 : Math.Min(Math.Max(0, pageIndex), (count - 1) / pageSize);

    /// <summary>Back to the first page. Call on every filter, search and branch change.</summary>
    public void Reset() => PageIndex = 0;

    /// <summary>A new size from the pager's "Rows per page"; always starts again at page one.</summary>
    public void SetSize(int size)
    {
        PageSize = size < 0 ? QPagerDefaults.Size : size;
        Reset();
    }

    /// <summary>
    /// Keeps <see cref="PageIndex"/> valid after the list shrank to <paramref name="count"/> rows, and returns it.
    /// </summary>
    public int ClampTo(int count)
    {
        if (IsAll || PageSize <= 0 || count <= 0) { PageIndex = 0; return 0; }
        var last = (count - 1) / PageSize;
        if (PageIndex > last) PageIndex = last;
        if (PageIndex < 0) PageIndex = 0;
        return PageIndex;
    }
}

/// <summary>The one place a list page's sizes and default come from (plan decision L4: default 25).</summary>
public static class QPagerDefaults
{
    /// <summary>Carbon's threshold: long enough to rarely need the pager, short enough to paint at once.</summary>
    public const int Size = 25;

    /// <summary>The "Rows per page" choices; pass <c>AllowAll="true"</c> as well to offer All.</summary>
    public static readonly IReadOnlyList<int> Sizes = new[] { 10, 25, 50, 100 };
}
