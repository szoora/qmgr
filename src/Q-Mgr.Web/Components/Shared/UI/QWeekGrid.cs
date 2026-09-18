namespace QMgr.Web.Components.Shared.UI;

/// <summary>A column of <see cref="QWeekGrid{TItem}"/>: a cycle day ("Mon", "Day 6") or a date.</summary>
/// <param name="Key">Matched against the item's column key. Stable, never shown.</param>
/// <param name="IsToday">Marks the column so the eye lands on it (the week view of a timetable, a rota).</param>
public sealed record QWeekGridColumn(string Key, string Label, string? SubLabel = null, bool IsToday = false);

/// <summary>A row of <see cref="QWeekGrid{TItem}"/>: a period ("P3", "10:20–11:00") or an hour.</summary>
/// <param name="IsBreak">A break, lunch or assembly: drawn as one band across the week, never selectable, never a slot.</param>
public sealed record QWeekGridRow(string Key, string Label, string? SubLabel = null, bool IsBreak = false);

/// <summary>What a cell is saying, besides what is in it. The timetable's diagnosis and a rota's warnings both map onto these.</summary>
public enum QWeekGridCellState
{
    None = 0,
    /// <summary>Draws the eye without alarm: a search hit, the lesson being moved, the current period.</summary>
    Highlight,
    /// <summary>A soft breach: over a daily maximum, the same subject twice in a day.</summary>
    Warning,
    /// <summary>A hard breach: a teacher, class or room booked twice. Never publishable.</summary>
    Conflict,
    /// <summary>Nothing may be placed here: a teacher's declared unavailability, a closure.</summary>
    Unavailable
}

/// <summary>One slot of the grid.</summary>
public readonly record struct QWeekGridSlot(string RowKey, string ColumnKey);

/// <summary>Handed to the cell template: the slot, what is in it, and how it should look.</summary>
public sealed record QWeekGridCell<TItem>(QWeekGridRow Row, QWeekGridColumn Column, IReadOnlyList<TItem> Items, QWeekGridCellState State, bool IsSelected)
{
    public QWeekGridSlot Slot => new(Row.Key, Column.Key);
    public bool IsEmpty => Items.Count == 0;
}
