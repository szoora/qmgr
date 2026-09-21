using System.Globalization;
using QMgr.Application.Import;
using QMgr.Domain.Identity;

namespace QMgr.Web.Services;

/// <summary>
/// The shared vocabulary for a bulk import's client-side stage: read the sheet, MAP its columns onto
/// the fields this feature wants, clean what comes through, turn it into typed rows, validate them
/// hard, and find duplicates before anything is sent.
///
/// <para>Q-Mgr has had two independent imports — the student roster and the staff list — each with
/// its own file input, its own header handling, its own preview table, its own progress panel and
/// its own idea of what "duplicate" means. They shared only the two genuinely shared primitives:
/// <c>rosterImport.sheetToCsv</c> (SheetJS in the browser, so the server needs no Excel library)
/// and <see cref="CsvText"/>. Everything above those was copied. This file plus
/// <c>QImportPanel</c> is the one home for the rest.</para>
///
/// <para><b>Mapping is dynamic, and that is the point (2026-09-21).</b> Matching a header against a
/// list of aliases only ever works for a file somebody wrote for us. A real school's export says
/// "Staff Name" in one cell of a title row, splits a class into "Class" and "Stream", and calls an
/// email column "Mail Address" — so the reader is SHOWN what was matched, can change any of it, and
/// can join two columns into one field. The aliases survive as the automatic first guess, which is
/// what makes the common case still take one click.</para>
///
/// <para><b>Validation is deliberately client-side AND server-side.</b> What happens here is a fast,
/// specific, row-numbered explanation a person can act on before committing — nothing here is a
/// security boundary. The server re-validates every row it is sent, because a client can post
/// whatever it likes.</para>
/// </summary>
public enum ImportFieldKind
{
    /// <summary>Trimmed, collapsed, stripped of invisible characters. What every field gets.</summary>
    Text = 0,

    /// <summary>A person's name: also un-SHOUTED when the file is in capitals and the reader agrees.</summary>
    Name = 1,

    /// <summary>Trimmed and lower-cased — an address is not case-sensitive and "  A@B.com " is a typo waiting.</summary>
    Email = 2,

    /// <summary>Normalised to +256… when it can be read as a Ugandan number.</summary>
    Phone = 3,

    /// <summary>Left as written; the row builder parses it day-first through <see cref="ImportParsing.ParseDate"/>.</summary>
    Date = 4,

    /// <summary>A code, reference or number: trimmed, never re-cased — "S2a" and "S2A" may differ.</summary>
    Code = 5,
}

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

    /// <summary>What the mapping step calls this field. Defaults to <see cref="Name"/> split on case.</summary>
    public string? Label { get; init; }

    /// <summary>One line under the field in the mapping step: what it is for, or what shape it takes.</summary>
    public string? Hint { get; init; }

    /// <summary>How this field's values are cleaned. See <see cref="ImportFieldKind"/>.</summary>
    public ImportFieldKind Kind { get; init; } = ImportFieldKind.Text;

    /// <summary>
    /// Another field that can stand in for this one, so REQUIRED can mean "this, or that". The staff
    /// import needs a first and last name OR a single combined name column, and a file carrying the
    /// combined one is complete rather than missing two required columns.
    /// </summary>
    public string? SatisfiedBy { get; init; }

    /// <summary>
    /// Headers that are JOINED ONTO this field when the file carries them: a school's export writes a
    /// class as "Class" = S1 and "Stream" = A, and Q-Mgr stores the one value S1A. Declaring it here
    /// means the join is the automatic guess — the reader still sees it in the mapping step and can
    /// take it apart.
    /// </summary>
    public IReadOnlyList<string> AlsoJoin { get; init; } = Array.Empty<string>();

    /// <summary>What goes between the joined columns. Empty for S1 + A = S1A.</summary>
    public string DefaultJoiner { get; init; } = string.Empty;

    /// <summary>The display name for the mapping step.</summary>
    public string DisplayName => Label ?? Split(Name);

    /// <summary>
    /// Case-, space-, underscore- and hyphen-insensitive, so "Student Code", "student_code",
    /// "student-code" and "StudentCode" are one column. Matches the normalisation rosterImport.js
    /// has always used, because a school's own spreadsheet export really does write all four.
    /// </summary>
    public bool Matches(string header)
    {
        var h = Normalize(header);
        if (h.Length == 0) return false;
        if (h == Normalize(Name)) return true;
        foreach (var a in Aliases) if (h == Normalize(a)) return true;
        return false;
    }

    internal static string Normalize(string s)
    {
        var t = (s ?? string.Empty).Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t.Replace(" ", string.Empty);
    }

    private static string Split(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]) && !char.IsUpper(pascal[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? pascal[i] : char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }
}

/// <summary>
/// The sheet as it was read: every row, plus WHICH row turned out to be the header. A real export
/// puts a title in row 1 ("Staff List") and the headers in row 2, so assuming row 1 reads every
/// header as a column of data and every column as unmapped.
/// </summary>
public sealed class ImportGrid
{
    public List<List<string>> AllRows { get; init; } = new();

    /// <summary>Zero-based index into <see cref="AllRows"/>, or -1 when no row looked like a header.</summary>
    public int HeaderRowIndex { get; set; } = -1;

    public bool HasHeader => HeaderRowIndex >= 0;

    public List<string> Headers => HasHeader
        ? AllRows[HeaderRowIndex].Select(h => h.Trim()).ToList()
        : Enumerable.Range(1, ColumnCount).Select(i => $"Column {i}").ToList();

    public IEnumerable<List<string>> DataRows => AllRows.Skip(HeaderRowIndex + 1);

    public int ColumnCount => AllRows.Count == 0 ? 0 : AllRows.Max(r => r.Count);

    /// <summary>The line number in the reader's OWN file, so an error points at something they can see.</summary>
    public int LineNumberOf(int dataRowIndex) => HeaderRowIndex + 2 + dataRowIndex;

    /// <summary>Up to three non-empty values from a column, for the mapping step's "we saw" line.</summary>
    public List<string> SampleOf(int columnIndex, int take = 3)
    {
        var samples = new List<string>();
        foreach (var row in DataRows)
        {
            if (columnIndex < 0 || columnIndex >= row.Count) continue;
            var v = row[columnIndex].Trim();
            if (v.Length == 0 || samples.Contains(v)) continue;
            samples.Add(v);
            if (samples.Count == take) break;
        }
        return samples;
    }
}

/// <summary>
/// How ONE declared field is filled from the file. Usually a single column; sometimes two joined,
/// which is how "Class" + "Stream" become the one class name Q-Mgr stores, and it is the reason this
/// is a type rather than an int.
/// </summary>
public sealed class ImportBinding
{
    /// <summary>Column indexes in the file, in the order they are joined. Empty means "not mapped".</summary>
    public List<int> Sources { get; set; } = new();

    /// <summary>What goes between two joined columns. "" for S1+A = S1A, " " for a name, "-", "/".</summary>
    public string Joiner { get; set; } = string.Empty;

    public bool IsMapped => Sources.Count > 0;

    public int Primary => Sources.Count > 0 ? Sources[0] : -1;
}

/// <summary>
/// The whole mapping: which file column fills which field, plus the file-level choices that decide
/// how the values are cleaned. Serialisable, so a school's mapping for its own export is remembered
/// and the second term's import is one click.
/// </summary>
public sealed class ImportMapping
{
    public Dictionary<string, ImportBinding> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which way round a combined name column is written. There is no way to detect this from the
    /// data — "Abaho Jude" is surname-first in Kampala and given-name-first in Cork — so it is asked
    /// rather than guessed, and the mapping step shows what the answer does to the first rows.
    /// </summary>
    public NameOrder NameOrder { get; set; } = NameOrder.GivenFirst;

    /// <summary>Turn "ABAASA BARBRA" into "Abaasa Barbra". Never touches a name that is already mixed case.</summary>
    public bool FixShouting { get; set; } = true;

    /// <summary>Read 0772…, +256772… and 256 772 111 222 as the same number.</summary>
    public bool NormalisePhones { get; set; } = true;

    public ImportBinding For(string column)
    {
        if (!Bindings.TryGetValue(column, out var b)) Bindings[column] = b = new ImportBinding();
        return b;
    }

    public bool IsMapped(string column) => Bindings.TryGetValue(column, out var b) && b.IsMapped;
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

    /// <summary>The mapped, cleaned cells, so the preview shows what will actually be imported.</summary>
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

    /// <summary>What the cleaning step changed, counted — "168 names tidied", "173 phone numbers".</summary>
    public Dictionary<string, int> Cleaned { get; } = new();

    public int Importable => Rows.Count(r => r.Importable);
    public int Rejected => Rows.Count(r => r.Error != null);
    public int Warned => Rows.Count(r => r.Importable && r.Warnings.Count > 0);
    public bool CanImport => FileProblems.Count == 0 && Importable > 0;
}

/// <summary>
/// Header detection, mapping, cleaning and the validators every import needs. Kept as plain static
/// methods so a page's row builder reads as a list of rules rather than a wall of string fiddling.
/// </summary>
public static class ImportParsing
{
    /// <summary>How far down a sheet to look for the header row before giving up on finding one.</summary>
    private const int HeaderSearchRows = 12;

    /// <summary>
    /// Turns the CSV that came back from SheetJS into a grid and works out which row is the header:
    /// the row in the first few that matches the most declared columns. A file whose header row is
    /// row 7 under six lines of school crest and address is a normal export, not an odd one.
    /// </summary>
    public static ImportGrid ReadGrid(string csvText, IReadOnlyList<ImportColumn> columns)
    {
        var grid = new ImportGrid
        {
            AllRows = (csvText ?? string.Empty).Replace("\r\n", "\n").Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(CsvText.ParseLine)
                .Select(c => c.ToList())
                .ToList()
        };

        var best = -1;
        var bestScore = 0;
        for (var i = 0; i < Math.Min(HeaderSearchRows, grid.AllRows.Count); i++)
        {
            var row = grid.AllRows[i];
            var score = row.Count(cell => columns.Any(c => c.Matches(cell)));
            // A title row ("Staff List") matches nothing and loses; a row that ties with an earlier
            // one keeps the earlier one, because the first real header is the header.
            if (score > bestScore) { bestScore = score; best = i; }
        }

        grid.HeaderRowIndex = best;
        return grid;
    }

    /// <summary>
    /// The first guess at a mapping: every declared column bound to the file column whose header
    /// matches its name or one of its aliases. What the reader then corrects, rather than what they
    /// have to build from nothing.
    /// </summary>
    public static ImportMapping AutoMap(ImportGrid grid, IReadOnlyList<ImportColumn> columns)
    {
        var mapping = new ImportMapping();
        if (!grid.HasHeader)
        {
            // No header at all: fall back to the template's own order, which is what this panel has
            // always done and is the only sane reading of a headerless file.
            for (var i = 0; i < columns.Count && i < grid.ColumnCount; i++)
                mapping.For(columns[i].Name).Sources.Add(i);
            return mapping;
        }

        var headers = grid.Headers;
        var taken = new HashSet<int>();
        foreach (var column in columns)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (taken.Contains(i) || !column.Matches(headers[i])) continue;
                mapping.For(column.Name).Sources.Add(i);
                taken.Add(i);
                break;
            }

            // A field that is normally spread over two columns picks the second one up here, so
            // "Class" + "Stream" arrives as one class name without anybody having to say so.
            if (column.AlsoJoin.Count == 0 || !mapping.IsMapped(column.Name)) continue;
            for (var i = 0; i < headers.Count; i++)
            {
                if (taken.Contains(i)) continue;
                var header = ImportColumn.Normalize(headers[i]);
                if (!column.AlsoJoin.Any(a => ImportColumn.Normalize(a) == header)) continue;
                var binding = mapping.For(column.Name);
                binding.Sources.Add(i);
                binding.Joiner = column.DefaultJoiner;
                taken.Add(i);
                break;
            }
        }
        return mapping;
    }

    /// <summary>
    /// A stable fingerprint of a file's header row, so a remembered mapping is only ever re-applied
    /// to a file of the same shape. A different export gets the automatic guess instead of somebody
    /// else's answer silently applied to the wrong columns.
    /// </summary>
    public static string Fingerprint(ImportGrid grid)
        => string.Join('|', grid.Headers.Select(ImportColumn.Normalize).Where(h => h.Length > 0));

    /// <summary>
    /// Required fields that nothing fills, said once for the file rather than once per row. A field
    /// with <see cref="ImportColumn.SatisfiedBy"/> is satisfied when its stand-in is mapped.
    /// </summary>
    public static List<string> MappingProblems(IReadOnlyList<ImportColumn> columns, ImportMapping mapping)
    {
        var problems = new List<string>();
        foreach (var c in columns.Where(c => c.Required))
        {
            if (mapping.IsMapped(c.Name)) continue;
            if (c.SatisfiedBy is { Length: > 0 } alt && mapping.IsMapped(alt)) continue;

            var alternative = c.SatisfiedBy is { Length: > 0 } s ? $", or map \"{s}\" instead" : string.Empty;
            problems.Add($"\"{c.DisplayName}\" is required — choose the column it comes from{alternative}.");
        }
        return problems;
    }

    /// <summary>
    /// Applies the mapping and the cleaning to one raw row, producing cells in the DECLARED column
    /// order with a map into them. That is what keeps a page's BuildRow unchanged by any of this: it
    /// still receives (cells, map, lineNumber) and still asks for a column by name — it simply now
    /// receives values that have been joined, trimmed, un-shouted and normalised first.
    /// </summary>
    public static (List<string> Cells, Dictionary<string, int> Map) Resolve(
        IReadOnlyList<string> raw, IReadOnlyList<ImportColumn> columns, ImportMapping mapping,
        Dictionary<string, int>? cleaned = null)
    {
        var cells = new List<string>(columns.Count);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            map[column.Name] = i;
            var binding = mapping.Bindings.TryGetValue(column.Name, out var b) ? b : null;
            if (binding is null || !binding.IsMapped) { cells.Add(string.Empty); continue; }

            var joined = string.Join(binding.Joiner, binding.Sources
                .Select(s => s >= 0 && s < raw.Count ? raw[s].Trim() : string.Empty)
                .Where(v => v.Length > 0));

            cells.Add(Sanitise(joined, column.Kind, mapping, cleaned));
        }

        return (cells, map);
    }

    /// <summary>
    /// The cleaning itself, by field kind. Everything is trimmed, collapsed and stripped of the
    /// invisible characters a copy-and-paste leaves behind — those are never wanted and there is no
    /// switch for them. What a reader CAN turn off is anything that changes a value they might have
    /// meant: re-casing a shouted name, and rewriting a phone number.
    /// </summary>
    public static string Sanitise(string value, ImportFieldKind kind, ImportMapping mapping,
        Dictionary<string, int>? cleaned = null)
    {
        var clean = PersonName.Clean(value);
        if (clean.Length == 0) return string.Empty;
        var before = clean;

        switch (kind)
        {
            case ImportFieldKind.Name:
                if (mapping.FixShouting) clean = PersonName.FixShouting(clean);
                if (cleaned is not null && clean != before) Count(cleaned, "names tidied out of capitals");
                break;

            case ImportFieldKind.Email:
                clean = clean.ToLowerInvariant().Replace(" ", string.Empty);
                if (cleaned is not null && clean != before) Count(cleaned, "email addresses tidied");
                break;

            case ImportFieldKind.Phone:
                if (mapping.NormalisePhones && NormalizePhone(clean) is { } normalised)
                {
                    clean = normalised;
                    if (cleaned is not null && clean != before) Count(cleaned, "phone numbers normalised");
                }
                break;
        }

        if (cleaned is not null && kind is ImportFieldKind.Text or ImportFieldKind.Code or ImportFieldKind.Date
            && clean != value.Trim())
            Count(cleaned, "values with stray spacing");

        return clean;
    }

    private static void Count(Dictionary<string, int> tally, string what)
        => tally[what] = tally.TryGetValue(what, out var n) ? n + 1 : 1;

    /// <summary>The cell for a declared column, or empty when the file does not carry it.</summary>
    public static string Cell(IReadOnlyList<string> cells, Dictionary<string, int> map, string column)
        => map.TryGetValue(column, out var i) && i >= 0 && i < cells.Count
            ? cells[i].Trim()
            : string.Empty;

    // ---- the validators ------------------------------------------------------------------------

    // THE VALIDATORS LIVE IN Q-Mgr.Shared AND THE SERVER RUNS THE SAME ONES (ImportRules). These two
    // are kept as the names every row builder already calls; what they must never become again is a
    // second implementation, because a preview that says a row is good and an import that refuses it
    // for a differently-worded reason is the worst answer an import can give.
    public static bool LooksLikeEmail(string s) => ImportRules.LooksLikeEmail(s);

    public static string? NormalizePhone(string s) => ImportRules.NormalizePhone(s);

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
/// What the reader decided on the review step, carried into the page's own start call. A record
/// rather than a bool so the next decision — a dry run, a branch override — does not change every
/// caller's signature again.
/// </summary>
public sealed class ImportOptions
{
    /// <summary>
    /// True updates the records that already exist; false adds only the missing ones. Asked before
    /// anything is sent, because "23 of these 184 people are already here" is a decision, not a
    /// result to be reported afterwards.
    /// </summary>
    public bool UpdateExisting { get; set; } = true;
}

/// <summary>How many of the rows about to be imported are already on file, and a few of their names.</summary>
public sealed record ImportExisting(int Count, IReadOnlyList<string> Sample)
{
    public static readonly ImportExisting None = new(0, Array.Empty<string>());
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
