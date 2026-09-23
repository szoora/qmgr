using System.Text;
using Microsoft.JSInterop;
using QMgr.Application.Import.Documents;

namespace QMgr.Web.Services;

/// <summary>
/// The Web's one door to the document readers (plan TERM_PROGRAMME_CALENDAR_AND_GATES §4): a Word file's bytes, or a
/// PDF's text runs, come from <c>wwwroot/js/importDocuments.js</c>; the Shared readers turn them into an
/// <see cref="ImportDocument"/>; and a chosen table becomes CSV text so the EXISTING sheet path (QImportPanel, the
/// timetable import) carries on unchanged — which is what "the document readers also feed the existing panel" means.
/// </summary>
public static class ImportDocumentReading
{
    /// <summary>What a file picker for an import accepts: every sheet format and every document format.</summary>
    public const string Accept = ".csv,.xlsx,.xls,.json,.ndjson,.docx,.doc,.pdf,text/csv,application/json";

    /// <summary>Reads the index-th file of the input as a document. Throws <see cref="ImportDocumentException"/> with a sentence to show.</summary>
    public static async Task<ImportDocument> ReadAsync(IJSRuntime js, string inputId, string fileName, int index = 0)
    {
        if (DocumentReader.IsPdfName(fileName))
        {
            var runs = await js.InvokeAsync<PdfTextRun[]>("importDocuments.pdfTextRuns", inputId, index);
            return DocumentReader.ReadPdf(fileName, runs ?? Array.Empty<PdfTextRun>());
        }
        var bytes = await js.InvokeAsync<byte[]>("importDocuments.readFileBytes", inputId, index);
        return DocumentReader.Read(fileName, bytes ?? Array.Empty<byte>());
    }

    /// <summary>
    /// A table as CSV text for the sheet path. Quoted field by field, WITHOUT the export writer's formula neutralising:
    /// this text never leaves the page, and an apostrophe in front of "+256…" would change the phone number it holds.
    /// Line breaks inside a cell become " / " so a row stays one CSV line.
    /// </summary>
    public static string ToCsv(ImportDocument doc, int tableIndex, int skipRows = 0)
    {
        var sb = new StringBuilder();
        foreach (var row in DocumentReader.RowsForCsv(doc, tableIndex).Skip(Math.Max(0, skipRows)))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(string.Join(",", row.Select(c => "\"" + c.Replace("\"", "\"\"") + "\"")));
        }
        return sb.ToString();
    }

    /// <summary>"Table 1 (12 rows)" — what a picker offers for each table of a document.</summary>
    public static List<string> TableLabels(ImportDocument doc)
        => doc.Tables.Select((t, i) => $"Table {i + 1} ({t.Rows.Count} row{(t.Rows.Count == 1 ? "" : "s")})").ToList();

    /// <summary>
    /// For an importer that expects the header on the first line: the best table, with every row above its header
    /// dropped (a title row, a crest line). <paramref name="isHeader"/> says whether a cell names an expected column.
    /// </summary>
    public static async Task<string> CsvFromHeaderAsync(IJSRuntime js, string inputId, string fileName, Func<string, bool> isHeader)
    {
        var doc = await ReadAsync(js, inputId, fileName);
        if (doc.Tables.Count == 0) throw new ImportDocumentException($"\"{fileName}\" has no table in it. The rows to import must be in a table.");
        var table = Math.Max(0, DocumentReader.BestTable(doc, isHeader));
        var rows = DocumentReader.RowsForCsv(doc, table);
        var header = 0;
        var best = 0;
        for (var r = 0; r < Math.Min(12, rows.Count); r++)
        {
            var score = rows[r].Count(isHeader);
            if (score > best) { best = score; header = r; }
        }
        return ToCsv(doc, table, header);
    }
}
