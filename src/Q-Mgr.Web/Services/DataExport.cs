using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;

namespace QMgr.Web.Services;

/// <summary>How a value should land in a spreadsheet cell. Text is the safe default.</summary>
public enum ExportType
{
    /// <summary>
    /// Stays text in Excel. This is what protects a student code like "007" from being read as the
    /// number 7, and a phone number like "+256771234567" from becoming scientific notation — the
    /// single most common way a spreadsheet export corrupts an identifier.
    /// </summary>
    Text = 0,
    Number = 1,
    Date = 2,
    Bool = 3
}

[Flags]
public enum ExportFormat
{
    None = 0,
    Csv = 1,
    Excel = 2,
    Json = 4,
    Pdf = 8,
    Print = 16,

    /// <summary>Everything. What a list page normally offers.</summary>
    All = Csv | Excel | Json | Pdf | Print,

    /// <summary>For data nobody prints — a long log, an impressions table.</summary>
    Data = Csv | Excel | Json
}

/// <summary>
/// One column of an export: what it is called, how to read it off a row, and how it should be
/// typed in a spreadsheet.
/// </summary>
/// <remarks>
/// Declared once by the calling page and used by every format, so a column added to the CSV cannot
/// be forgotten in the workbook — which is exactly how the eight hand-rolled exports this replaces
/// drifted apart from one another.
/// </remarks>
public sealed class ExportColumn<T>
{
    public ExportColumn(string header, Func<T, object?> value, ExportType type = ExportType.Text, int width = 18)
    {
        Header = header;
        Value = value;
        Type = type;
        Width = width;
    }

    public string Header { get; }
    public Func<T, object?> Value { get; }
    public ExportType Type { get; }

    /// <summary>Column width in characters, for the workbook. Nobody should open a file to ####.</summary>
    public int Width { get; }

    /// <summary>The cell as a string. Dates go out ISO-ish so every format sorts correctly.</summary>
    public string Read(T item)
    {
        var raw = Value(item);
        return raw switch
        {
            null => string.Empty,
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm"),
            DateOnly d => d.ToString("yyyy-MM-dd"),
            TimeOnly t => t.ToString("HH:mm"),
            bool b => b ? "Yes" : "No",
            decimal m => m.ToString("0.##"),
            double db => db.ToString("0.##"),
            Enum e => SplitPascalCase(e.ToString()),
            _ => raw.ToString() ?? string.Empty
        };
    }

    /// <summary>"UnderReview" reads as "Under Review" in a file somebody opens in Excel.</summary>
    private static string SplitPascalCase(string value)
    {
        if (value.Length < 2) return value;
        var sb = new StringBuilder(value.Length + 4);
        sb.Append(value[0]);
        for (var i = 1; i < value.Length; i++)
        {
            if (char.IsUpper(value[i]) && !char.IsUpper(value[i - 1])) sb.Append(' ');
            sb.Append(value[i]);
        }
        return sb.ToString();
    }
}

/// <summary>Where the rows come from, and whether there were more than we were willing to take.</summary>
/// <param name="Rows">Everything matching the caller's current filters, not the page on screen.</param>
/// <param name="Truncated">True when the row cap stopped the fetch short.</param>
public record ExportRows<T>(IReadOnlyList<T> Rows, bool Truncated);

/// <summary>The outcome, in the words the toast will use.</summary>
/// <summary>What an export produced. <see cref="Format"/> is set on success so a page can say (and log) what the user took away.</summary>
public record ExportResult(bool Success, int RowCount, bool Truncated, string Message, ExportFormat? Format = null);

/// <summary>
/// Turns declared columns and fetched rows into a file the browser saves.
/// </summary>
/// <remarks>
/// <para>
/// This exists because export had grown eight times over: <c>ReportsController</c>'s server-side
/// CSV actions on one side, and hand-rolled client-side CSV in Feedback Management, Welfare
/// Reports, Campaign impressions and the roster on the other — two mechanisms, inconsistent
/// gating, and not one page offering anything but CSV.
/// </para>
/// <para>
/// Every format is written in the browser. That is the project's no-server-dependencies rule
/// deciding the architecture: a real .xlsx or .pdf writer on the server is a NuGet package in the
/// deploy target, while SheetJS is already loaded app-wide for parsing roster uploads and the
/// browser's own print dialog is already how this app produces printed cards. PDF is therefore
/// print-to-PDF rather than a generated file — one extra click, no new library, and better
/// typography for a document carrying a branch letterhead.
/// </para>
/// </remarks>
public interface IDataExportService
{
    Task<ExportResult> ExportAsync<T>(
        ExportFormat format,
        string fileStem,
        string title,
        IReadOnlyList<ExportColumn<T>> columns,
        ExportRows<T> rows,
        string? subtitle = null);
}

public class DataExportService : IDataExportService
{
    private readonly IJSRuntime _js;
    private readonly ILogger<DataExportService> _logger;

    public DataExportService(IJSRuntime js, ILogger<DataExportService> logger)
    {
        _js = js;
        _logger = logger;
    }

    public async Task<ExportResult> ExportAsync<T>(
        ExportFormat format,
        string fileStem,
        string title,
        IReadOnlyList<ExportColumn<T>> columns,
        ExportRows<T> rows,
        string? subtitle = null)
    {
        if (rows.Rows.Count == 0)
            return new ExportResult(false, 0, false, "Nothing matches the current filters, so there is nothing to export.");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        var name = $"{Slug(fileStem)}-{stamp}";

        try
        {
            switch (format)
            {
                case ExportFormat.Csv:
                    await _js.InvokeVoidAsync("qmgrExport.saveText", BuildCsv(columns, rows.Rows), "text/csv", $"{name}.csv");
                    break;

                case ExportFormat.Excel:
                    await _js.InvokeVoidAsync("qmgrExport.saveWorkbook",
                        title,
                        columns.Select(c => c.Header).ToArray(),
                        rows.Rows.Select(r => columns.Select(c => c.Read(r)).ToArray()).ToArray(),
                        columns.Select(c => c.Type.ToString().ToLowerInvariant()).ToArray(),
                        columns.Select(c => c.Width).ToArray(),
                        $"{name}.xlsx");
                    break;

                case ExportFormat.Json:
                    await _js.InvokeVoidAsync("qmgrExport.saveText", BuildJson(columns, rows.Rows), "application/json", $"{name}.json");
                    break;

                case ExportFormat.Pdf:
                case ExportFormat.Print:
                    await _js.InvokeVoidAsync("qmgrExport.printDocument", BuildPrintable(title, subtitle, columns, rows.Rows));
                    break;

                default:
                    return new ExportResult(false, 0, false, "That export format isn't available here.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed for {Title} as {Format}", title, format);
            return new ExportResult(false, 0, false, "The file couldn't be produced. Please try again.");
        }

        var count = rows.Rows.Count;
        var message = rows.Truncated
            ? $"Exported the first {count:N0} rows — narrow the filters to get the rest."
            : format is ExportFormat.Pdf or ExportFormat.Print
                ? $"{count:N0} row{(count == 1 ? "" : "s")} sent to the print view. Choose \"Save as PDF\" there to keep a copy."
                : $"{count:N0} row{(count == 1 ? "" : "s")} exported.";

        return new ExportResult(true, count, rows.Truncated, message, format);
    }

    // ---- writers ---------------------------------------------------------------------------

    private static string BuildCsv<T>(IReadOnlyList<ExportColumn<T>> columns, IReadOnlyList<T> rows)
    {
        var lines = new List<string?[]> { columns.Select(c => c.Header).ToArray()! };
        lines.AddRange(rows.Select(r => columns.Select(c => (string?)c.Read(r)).ToArray()));
        return CsvWriter.Build(lines);
    }

    /// <summary>
    /// Objects keyed by header, not a parallel array — a JSON export is read by a person or a
    /// script, and both want to see what a value is called next to it.
    /// </summary>
    private static string BuildJson<T>(IReadOnlyList<ExportColumn<T>> columns, IReadOnlyList<T> rows)
    {
        var payload = rows.Select(r =>
        {
            var o = new Dictionary<string, string>(columns.Count);
            foreach (var c in columns) o[c.Header] = c.Read(r);
            return o;
        }).ToList();

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The print/PDF document. Black on white with no theme tokens — this is going onto paper or
    /// into a PDF, where the app's dark palette prints as a block of ink. The header repeats on
    /// every page because a twelve-page roster is unreadable otherwise.
    /// </summary>
    private static string BuildPrintable<T>(string title, string? subtitle, IReadOnlyList<ExportColumn<T>> columns, IReadOnlyList<T> rows)
    {
        var head = string.Concat(columns.Select(c =>
            $"<th class=\"{(c.Type is ExportType.Number ? "num" : "")}\">{WebUtility.HtmlEncode(c.Header)}</th>"));

        var body = new StringBuilder();
        foreach (var row in rows)
        {
            body.Append("<tr>");
            foreach (var c in columns)
                body.Append($"<td class=\"{(c.Type is ExportType.Number ? "num" : "")}\">{WebUtility.HtmlEncode(c.Read(row))}</td>");
            body.Append("</tr>");
        }

        var sub = string.IsNullOrWhiteSpace(subtitle) ? "" : $"<p class=\"sub\">{WebUtility.HtmlEncode(subtitle)}</p>";

        return $$"""
            <html><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title><style>
                @page { size: A4 landscape; margin: 12mm; }
                body { font-family: 'Segoe UI', Arial, sans-serif; color: #000; margin: 0; font-size: 10pt; }
                h1 { font-size: 16pt; margin: 0 0 2mm; }
                .sub { font-size: 9pt; color: #555; margin: 0 0 5mm; }
                .meta { font-size: 8pt; color: #666; margin: 0 0 6mm; }
                table { border-collapse: collapse; width: 100%; }
                thead { display: table-header-group; }
                th, td { border: 1px solid #bbb; padding: 4px 6px; text-align: left; vertical-align: top; }
                th { background: #eee; font-size: 9pt; }
                td { font-size: 9pt; }
                .num { text-align: right; font-variant-numeric: tabular-nums; }
                tr { page-break-inside: avoid; }
            </style></head><body>
                <h1>{{WebUtility.HtmlEncode(title)}}</h1>
                {{sub}}
                <p class="meta">{{rows.Count:N0}} row(s) · generated {{DateTime.Now:d MMMM yyyy, HH:mm}}</p>
                <table><thead><tr>{{head}}</tr></thead><tbody>{{body}}</tbody></table>
            </body></html>
            """;
    }

    private static string Slug(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// Turns a CSV the <em>server</em> produced into the columns and rows the exporter renders.
/// </summary>
/// <remarks>
/// <para>
/// Three report exports — counter performance, customer feedback and the visitor log — are gated
/// on <c>Permissions.ReportsExport</c> <em>and</em> the <c>ExportReports</c> plan feature, and the
/// plan half of that is only enforced in the API. Rendering those rows straight from what the
/// page already has in memory would have handed exports to tenants whose plan does not include
/// them, so the fetch stays on the gated endpoint and only the <em>formatting</em> moves to the
/// browser: the server still decides whether there is a file at all, and its own CSV header stays
/// the single definition of the column list, which is why none of those pages restates it.
/// </para>
/// <para>
/// Everything arrives as text, so the workbook's cells are text too — a column of numbers from
/// here will not sum in Excel the way one built from typed <see cref="ExportColumn{T}"/> values
/// does. That is the price of not duplicating the server's column list, and it is the right side
/// of the trade for a report that is read rather than modelled.
/// </para>
/// </remarks>
public static class ExportCsvSource
{
    /// <summary>Parses RFC 4180 CSV: quoted fields, embedded commas, newlines and doubled quotes.</summary>
    public static (IReadOnlyList<ExportColumn<string[]>> Columns, ExportRows<string[]> Rows) Parse(string csv)
    {
        var records = ParseRecords(csv);
        if (records.Count == 0)
            return (Array.Empty<ExportColumn<string[]>>(), new ExportRows<string[]>(Array.Empty<string[]>(), false));

        var header = records[0];
        var columns = header
            .Select((h, i) => new ExportColumn<string[]>(
                string.IsNullOrWhiteSpace(h) ? $"Column {i + 1}" : h,
                row => i < row.Length ? row[i] : null,
                ExportType.Text,
                EstimateWidth(h)))
            .ToList();

        // A short row is padded rather than dropped — a trailing empty field the writer omitted
        // must not shift every later column left.
        var rows = records.Skip(1)
            .Where(r => r.Length > 0 && !(r.Length == 1 && string.IsNullOrEmpty(r[0])))
            .Select(r => r.Length == header.Length ? r : Resize(r, header.Length))
            .ToList();

        return (columns, new ExportRows<string[]>(rows, false));
    }

    private static string[] Resize(string[] row, int length)
    {
        var padded = new string[length];
        for (var i = 0; i < length; i++) padded[i] = i < row.Length ? row[i] : string.Empty;
        return padded;
    }

    private static int EstimateWidth(string header) => Math.Clamp(header.Length + 4, 10, 40);

    private static List<string[]> ParseRecords(string csv)
    {
        var records = new List<string[]>();
        if (string.IsNullOrEmpty(csv)) return records;

        // A BOM survives ReadAsStringAsync often enough to be worth stripping here rather than
        // letting it become part of the first column's name.
        if (csv[0] == '\uFEFF') csv = csv[1..];

        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];

            if (inQuotes)
            {
                if (c != '"') { field.Append(c); continue; }
                if (i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; continue; }
                inQuotes = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add(fields.ToArray());
                    fields.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(fields.ToArray());
        }

        return records;
    }
}
