using System.Text;

namespace QMgr.Application.Import.Documents;

/// <summary>
/// A document read into tables and the paragraphs around them — the one shape every reader produces
/// (plan TERM_PROGRAMME_CALENDAR_AND_GATES §4). A Word table, a binary Word 97–2003 table and the text
/// runs pdf.js lifts out of a PDF all become this, so the classifier, the parsers and the existing
/// spreadsheet import never learn which format a school happened to save in.
///
/// <para>Merged cells are KEPT as merges here (<see cref="ImportDocCell.ColSpan"/>,
/// <see cref="ImportDocCell.MergedFromAbove"/>) rather than filled in by the reader, because a merge
/// carries meaning — the week band over five activities, the one date shared by two meetings — and a
/// caller that wants a flat grid asks for one through <see cref="ToGrid"/>, which fills each merged
/// value only within its own region.</para>
/// </summary>
public sealed class ImportDocument
{
    public string FileName { get; set; } = string.Empty;

    /// <summary>"docx", "doc", "pdf", "sheet".</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>Body paragraphs in document order, each saying which table it sits before or after.</summary>
    public List<ImportDocParagraph> Paragraphs { get; set; } = new();

    public List<ImportDocTable> Tables { get; set; } = new();

    /// <summary>The paragraphs that come before the first table — where a school writes its title and theme.</summary>
    public IEnumerable<ImportDocParagraph> Heading => Paragraphs.Where(p => p.BeforeTable == 0);

    /// <summary>Paragraphs directly after table <paramref name="tableIndex"/> (and before the next one): its notes.</summary>
    public IEnumerable<ImportDocParagraph> NotesAfter(int tableIndex) => Paragraphs.Where(p => p.AfterTable == tableIndex);

    /// <summary>
    /// Table <paramref name="tableIndex"/> as rows of strings with every merge FILLED: a horizontal span
    /// repeats its text across the columns it covers, a vertical merge repeats the text of the cell that
    /// started it down the rows it covers — and only those. Ragged rows are padded to the widest row.
    /// </summary>
    public List<List<string>> ToGrid(int tableIndex)
        => tableIndex >= 0 && tableIndex < Tables.Count ? Tables[tableIndex].ToGrid() : new List<List<string>>();

    /// <summary>Everything the document says, as plain lines — what a classifier reads when there is no table.</summary>
    public string AllText()
    {
        var sb = new StringBuilder();
        foreach (var p in Paragraphs) sb.AppendLine(p.Text);
        foreach (var t in Tables)
            foreach (var row in t.Rows)
                sb.AppendLine(string.Join(" | ", row.Select(c => c.Text)));
        return sb.ToString();
    }
}

public sealed class ImportDocParagraph
{
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The index of the table this paragraph comes BEFORE (0 = before the first table). A paragraph after
    /// the last table carries <c>Tables.Count</c>.
    /// </summary>
    public int BeforeTable { get; set; }

    /// <summary>The index of the table this paragraph comes AFTER, or -1 when it precedes every table.</summary>
    public int AfterTable => BeforeTable - 1;
}

public sealed class ImportDocTable
{
    public List<List<ImportDocCell>> Rows { get; set; } = new();

    /// <summary>The number of grid columns: a cell spanning three counts three.</summary>
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Sum(c => Math.Max(1, c.ColSpan)));

    /// <summary>
    /// A caption row — "MARYHILL HIGH SCHOOL" across the whole table, "WE WISH YOU A BLESSED TERM" across
    /// four of five columns: one piece of text spanning at least half the columns and nothing else in the
    /// row. It is a title or a sign-off, never a record.
    /// </summary>
    public bool IsCaptionRow(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count) return false;
        var row = Rows[rowIndex];
        var filled = row.Where(c => !string.IsNullOrWhiteSpace(c.Text) && !c.MergedFromAbove).ToList();
        if (filled.Count != 1) return false;
        var width = Math.Max(1, ColumnCount);
        return filled[0].ColSpan * 2 >= width && width > 1;
    }

    public List<List<string>> ToGrid()
    {
        var width = ColumnCount;
        var grid = new List<List<string>>(Rows.Count);
        // For each grid column, the text of the cell that started the vertical merge covering it.
        var above = new string[Math.Max(width, 1)];
        foreach (var row in Rows)
        {
            var line = new List<string>(width);
            foreach (var cell in row)
            {
                var span = Math.Max(1, cell.ColSpan);
                for (var k = 0; k < span; k++)
                {
                    var col = line.Count;
                    string text;
                    if (cell.MergedFromAbove) text = col < above.Length ? above[col] ?? string.Empty : string.Empty;
                    else
                    {
                        text = cell.Text;
                        if (col < above.Length) above[col] = text;
                    }
                    line.Add(text);
                }
            }
            while (line.Count < width) line.Add(string.Empty);
            grid.Add(line);
        }
        return grid;
    }
}

public sealed class ImportDocCell
{
    /// <summary>The cell's text; paragraphs inside one cell are joined with " / ".</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>How many grid columns this cell covers (Word's gridSpan). 1 for an ordinary cell.</summary>
    public int ColSpan { get; set; } = 1;

    /// <summary>How many rows a vertical merge that STARTS here covers (1 when it does not merge down).</summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>True when this cell continues a vertical merge from the row above (Word's vMerge "continue").</summary>
    public bool MergedFromAbove { get; set; }
}

/// <summary>
/// A document the readers cannot turn into text, with the sentence to show the person — a scanned PDF,
/// an encrypted file, a format we do not read (decision D3: no OCR, so a picture of a page is refused in
/// words rather than guessed at).
/// </summary>
public sealed class ImportDocumentException : Exception
{
    public ImportDocumentException(string message) : base(message) { }
}

/// <summary>Shared cell-text tidying for every reader: whitespace collapsed, the " / " joins trimmed.</summary>
public static class ImportDocText
{
    public static string Tidy(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var raw in value)
        {
            var ch = raw;
            if (ch == (char)0x00A0 || ch == (char)0x09 || ch == (char)0x0B || ch == (char)0x0C) ch = ' ';
            else if (ch == (char)0x2014 || ch == (char)0x2012 || ch == (char)0x2212) ch = (char)0x2013;
            else if (ch == (char)0x2018 || ch == (char)0x2019 || ch == (char)0x02BC) ch = (char)0x27;
            else if (ch == (char)0x201C || ch == (char)0x201D) ch = (char)0x22;
            if (ch == (char)0x200B || ch == (char)0xFEFF || (char.IsControl(ch) && ch != (char)0x0A)) continue;
            if (ch == ' ') { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        var text = sb.ToString().Trim();
        // Empty paragraphs inside a cell leave " / " at either end, or doubled in the middle.
        while (text.StartsWith("/ ", StringComparison.Ordinal) || text == "/") text = text.Length <= 2 ? string.Empty : text[2..].Trim();
        while (text.EndsWith(" /", StringComparison.Ordinal)) text = text[..^2].Trim();
        while (text.Contains(" / / ", StringComparison.Ordinal)) text = text.Replace(" / / ", " / ", StringComparison.Ordinal);
        return text;
    }

    /// <summary>Joins the paragraphs of one cell, dropping empty ones.</summary>
    public static string JoinParagraphs(IEnumerable<string> paragraphs)
        => Tidy(string.Join(" / ", paragraphs.Select(Tidy).Where(p => p.Length > 0)));
}
