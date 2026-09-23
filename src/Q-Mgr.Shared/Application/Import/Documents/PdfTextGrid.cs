namespace QMgr.Application.Import.Documents;

/// <summary>One piece of text pdf.js found on a page: where it starts, how wide it is, what it says.</summary>
public sealed record PdfTextRun
{
    public int Page { get; init; }
    /// <summary>Left edge, in PDF points from the page's left.</summary>
    public double X { get; init; }
    /// <summary>Baseline, in PDF points from the page's TOP (the browser flips pdf.js's bottom-up y).</summary>
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public string Str { get; init; } = string.Empty;
}

/// <summary>
/// Turns the text runs pdf.js lifts out of a text PDF into a table (plan §4): a PDF has no table — only
/// pieces of text at positions — so rows are runs that share a baseline, and columns are the left edges the
/// rows agree on. Everything above the first row with several columns is the heading; single-line rows
/// after the last one are notes. A PDF with no text at all is a scan, and is refused in words (decision D3).
/// </summary>
public static class PdfTextGrid
{
    /// <summary>The sentence for a PDF with no text layer.</summary>
    public const string ScannedRefusal =
        "This PDF is a picture of a page (a scan or a photograph), so there is no text in it to read. Open the original Word or Excel file instead, or type the programme into the template.";

    public static ImportDocument Build(string fileName, IReadOnlyList<PdfTextRun> runs)
    {
        var usable = runs.Where(r => !string.IsNullOrWhiteSpace(r.Str)).ToList();
        if (usable.Count == 0) throw new ImportDocumentException(ScannedRefusal);

        var doc = new ImportDocument { FileName = fileName, Format = "pdf" };

        // ---- Rows: runs whose baselines are within a fraction of the text height of each other.
        var medianHeight = Median(usable.Select(r => r.Height > 0 ? r.Height : 10).ToList());
        var tolerance = Math.Max(2.0, medianHeight * 0.45);
        var lines = new List<List<PdfTextRun>>();
        foreach (var run in usable.OrderBy(r => r.Page).ThenBy(r => r.Y).ThenBy(r => r.X))
        {
            var line = lines.Count > 0 ? lines[^1] : null;
            if (line != null && line[0].Page == run.Page && Math.Abs(line[0].Y - run.Y) <= tolerance) line.Add(run);
            else lines.Add(new List<PdfTextRun> { run });
        }

        // ---- Cells within a line: runs closer than about two characters belong to one cell.
        var charWidth = Median(usable.Where(r => r.Str.Length > 0 && r.Width > 0).Select(r => r.Width / r.Str.Length).DefaultIfEmpty(5).ToList());
        var gap = Math.Max(4.0, charWidth * 2.2);
        var cellLines = new List<List<(double X, string Text)>>();
        foreach (var line in lines)
        {
            var cells = new List<(double X, double End, string Text)>();
            foreach (var run in line.OrderBy(r => r.X))
            {
                if (cells.Count > 0 && run.X - cells[^1].End <= gap)
                {
                    var prev = cells[^1];
                    var joiner = run.X - prev.End > charWidth * 0.3 ? " " : string.Empty;
                    cells[^1] = (prev.X, Math.Max(prev.End, run.X + run.Width), prev.Text + joiner + run.Str);
                }
                else cells.Add((run.X, run.X + run.Width, run.Str));
            }
            cellLines.Add(cells.Select(c => (c.X, ImportDocText.Tidy(c.Text))).Where(c => c.Item2.Length > 0).ToList());
        }

        // ---- Columns: the left edges that multi-cell lines share.
        var tableLines = cellLines.Select((cells, index) => (cells, index)).Where(l => l.cells.Count >= 2).ToList();
        if (tableLines.Count == 0)
        {
            foreach (var cells in cellLines)
                doc.Paragraphs.Add(new ImportDocParagraph { Text = string.Join(" ", cells.Select(c => c.Text)), BeforeTable = 0 });
            return doc;
        }

        var edges = tableLines.SelectMany(l => l.cells.Select(c => c.X)).OrderBy(x => x).ToList();
        var columns = new List<double>();
        var snap = Math.Max(8.0, charWidth * 3);
        foreach (var x in edges)
        {
            if (columns.Count > 0 && x - columns[^1] <= snap) continue;
            columns.Add(x);
        }

        var first = tableLines[0].index;
        var last = tableLines[^1].index;
        var table = new ImportDocTable();
        for (var i = 0; i < cellLines.Count; i++)
        {
            var cells = cellLines[i];
            if (i < first || i > last)
            {
                var text = string.Join(" ", cells.Select(c => c.Text));
                if (text.Length > 0)
                    doc.Paragraphs.Add(new ImportDocParagraph { Text = text, BeforeTable = i < first ? 0 : 1 });
                continue;
            }

            var slots = new string[columns.Count];
            foreach (var (x, text) in cells)
            {
                var col = 0;
                for (var c = 0; c < columns.Count; c++)
                    if (columns[c] - snap / 2 <= x) col = c;
                slots[col] = string.IsNullOrEmpty(slots[col]) ? text : slots[col] + " / " + text;
            }

            // A line with a single cell inside the table continues the cell above it (a wrapped line).
            if (cells.Count == 1 && table.Rows.Count > 0 && string.IsNullOrEmpty(slots[0]))
            {
                var prev = table.Rows[^1];
                for (var c = 0; c < slots.Length && c < prev.Count; c++)
                    if (!string.IsNullOrEmpty(slots[c])) prev[c].Text = ImportDocText.Tidy(prev[c].Text + " / " + slots[c]);
                continue;
            }
            table.Rows.Add(slots.Select(s => new ImportDocCell { Text = s ?? string.Empty }).ToList());
        }
        doc.Tables.Add(table);
        return doc;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }
}
