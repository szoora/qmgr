using System.Globalization;

namespace QMgr.Web.Services;

/// <summary>
/// The shared vocabulary for a bulk import's client-side stage: declare the columns, turn a sheet
/// into typed rows, validate them hard, and find duplicates before anything is sent.
///
/// <para>Q-Mgr has had two independent imports — the student roster and the staff list — each with
/// its own file input, its own header handling, its own preview table, its own progress panel and
/// its own idea of what "duplicate" means. They shared only the two genuinely shared primitives:
/// <c>rosterImport.sheetToCsv</c> (SheetJS in the browser, so the server needs no Excel library)
/// and <see cref="CsvText"/>. Everything above those was copied. This file plus
/// <c>QImportPanel</c> is the one home for the rest.</para>
///
/// <para><b>Validation is deliberately client-side AND server-side.</b> What happens here is a fast,
/// specific, row-numbered explanation a person can act on before committing — nothing here is a
/// security boundary. The server re-validates every row it is sent, because a client can post
/// whatever it likes.</para>
/// </summary>
public sealed class ImportColumn
{
    public ImportColumn(string name, bool required = false, params string[] aliases)
    {
        Name = name;
        Required = required;
        Aliases = aliases;
    }

    /// <summary>The canonical header, lower-cased when matched.</summary>
    public string Name { get; }

    /// <summary>A row missing this cell is refused rather than imported half-formed.</summary>
    public bool Required { get; }

    /// <summary>Other spellings a real school's spreadsheet uses for the same column.</summary>
    public IReadOnlyList<string> Aliases { get; }

    public bool Matches(string header)
    {
        var h = header.Trim().ToLowerInvariant();
        if (h == Name.ToLowerInvariant()) return true;
        foreach (var a in Aliases) if (h == a.ToLowerInvariant()) return true;
        return false;
    }
}

/// <summary>One parsed row, its source line number, and whatever is wrong with it.</summary>
public sealed class ImportPreviewRow<TRow>
{
    public int RowNumber { get; init; }
    public TRow? Row { get; init; }

    /// <summary>Blocking. The row is NOT sent. Null when the row is importable.</summary>
    public string? Error { get; set; }

    /// <summary>Non-blocking. The row IS sent; the reader is told what will happen to it.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>The raw cells, kept so the preview can show what the file actually said.</summary>
    public IReadOnlyList<string> Cells { get; init; } = Array.Empty<string>();

    public bool Importable => Error == null && Row != null;
}

/// <summary>What a parse produced: the rows, and anything wrong with the FILE rather than a row.</summary>
public sealed class ImportPreview<TRow>
{
    public List<ImportPreviewRow<TRow>> Rows { get; } = new();

    /// <summary>Missing required columns, an empty sheet, a header that matched nothing.</summary>
    public List<string> FileProblems { get; } = new();

    /// <summary>The headers as they were actually found, for the "columns we read" line.</summary>
    public List<string> Headers { get; init; } = new();

    public int Importable => Rows.Count(r => r.Importable);
    public int Rejected => Rows.Count(r => r.Error != null);
    public int Warned => Rows.Count(r => r.Importable && r.Warnings.Count > 0);
    public bool CanImport => FileProblems.Count == 0 && Importable > 0;
}

/// <summary>
/// Header mapping and the validators every import needs. Kept as plain static methods so a page's
/// row builder reads as a list of rules rather than a wall of string fiddling.
/// </summary>
public static class ImportParsing
{
    /// <summary>
    /// Splits the CSV that came back from SheetJS and maps its header row against the declared
    /// columns. A file whose first row is not a header (no declared column matched) is treated as
    /// data with positional columns, which is how a list pasted out of another system usually looks.
    /// </summary>
    public static (List<List<string>> Rows, Dictionary<string, int> Map, List<string> Headers, bool HasHeader)
        Split(string csvText, IReadOnlyList<ImportColumn> columns)
    {
        var lines = csvText.Replace("\r\n", "\n").Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var headers = new List<string>();
        if (lines.Count == 0) return (new(), map, headers, false);

        var first = CsvText.ParseLine(lines[0]);
        var matched = 0;
        for (var i = 0; i < first.Count; i++)
        {
            var col = columns.FirstOrDefault(c => c.Matches(first[i]));
            if (col != null) { map[col.Name] = i; matched++; }
        }

        var hasHeader = matched > 0;
        if (hasHeader)
        {
            headers = first.Select(h => h.Trim()).ToList();
            return (lines.Skip(1).Select(CsvText.ParseLine).ToList(), map, headers, true);
        }

        // No header: fall back to declared order.
        for (var i = 0; i < columns.Count; i++) map[columns[i].Name] = i;
        return (lines.Select(CsvText.ParseLine).ToList(), map, headers, false);
    }

    /// <summary>The cell for a declared column, or empty when the file does not carry it.</summary>
    public static string Cell(IReadOnlyList<string> cells, Dictionary<string, int> map, string column)
        => map.TryGetValue(column, out var i) && i >= 0 && i < cells.Count
            ? cells[i].Trim()
            : string.Empty;

    // ---- the validators ------------------------------------------------------------------------

    /// <summary>RFC-shaped enough to catch a typo without refusing a valid oddity.</summary>
    public static bool LooksLikeEmail(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var at = s.IndexOf('@');
        if (at <= 0 || at != s.LastIndexOf('@')) return false;
        var domain = s[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.')
               && !s.Contains(' ') && s.Length <= 254;
    }

    /// <summary>
    /// Ugandan mobile numbers in any of the shapes a school actually types: 0770…, +256770…,
    /// 256770…, with spaces or dashes. Returns null when it cannot be read as a phone number.
    /// </summary>
    public static string? NormalizePhone(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        if (digits.Length < 9) return null;
        if (digits.StartsWith("256") && digits.Length == 12) return "+" + digits;
        if (digits.StartsWith("0") && digits.Length == 10) return "+256" + digits[1..];
        if (digits.Length == 9) return "+256" + digits;
        return "+" + digits; // an international number we do not recognise: keep it, do not refuse it
    }

    /// <summary>
    /// A date in the day-first shapes this product's schools write, then the ISO and invariant
    /// fallbacks. Deliberately the same precedence the welfare import uses, so "3/4/2026" means
    /// 3 April in both places rather than March 4 in one of them.
    /// </summary>
    public static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
        "dd.MM.yyyy", "d.M.yyyy", "dd MMM yyyy", "d MMM yyyy", "dd MMMM yyyy"
    };

    public static DateOnly? ParseDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (DateOnly.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        if (DateOnly.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return d;
        return null;
    }

    /// <summary>
    /// Marks every row whose key repeats within the FILE. The first occurrence is kept and warned;
    /// the later ones are refused, because importing the same person twice is the mistake this
    /// catches and silently keeping the last one hides which row won.
    /// </summary>
    public static void FlagDuplicatesWithinFile<TRow>(
        ImportPreview<TRow> preview, Func<TRow, string?> keySelector, string noun)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in preview.Rows)
        {
            if (!row.Importable) continue;
            var key = keySelector(row.Row!);
            if (string.IsNullOrWhiteSpace(key)) continue;

            if (seen.TryGetValue(key, out var firstRow))
            {
                row.Error = $"Duplicate {noun} — the same value is already on row {firstRow} of this file.";
            }
            else
            {
                seen[key] = row.RowNumber;
            }
        }
    }
}

/// <summary>
/// One row of a downloadable import template: the example values, positionally aligned to the
/// declared <see cref="ImportColumn"/>s. A tiny type, but it lets the template go through the same
/// QDataExport every other download in this app uses rather than growing a second writer.
/// </summary>
public sealed class ImportTemplateRow
{
    public ImportTemplateRow(IReadOnlyList<string> cells) => Cells = cells;

    public IReadOnlyList<string> Cells { get; }

    /// <summary>The cell at <paramref name="index"/>, or empty when the example is shorter.</summary>
    public string At(int index) => index >= 0 && index < Cells.Count ? Cells[index] : string.Empty;
}
