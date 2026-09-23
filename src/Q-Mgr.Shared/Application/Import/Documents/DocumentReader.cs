namespace QMgr.Application.Import.Documents;

/// <summary>
/// The one door into the document readers (plan §4). It decides by the file's MAGIC BYTES, never by its
/// name — a .doc renamed .docx, or a "Programme.doc" that is really an HTML page saved by a website, reads
/// correctly or is refused in words rather than crashing a parser built for something else.
///
/// <para>PDF is the exception: a PDF's text is laid out by pdf.js in the BROWSER (it already ships for the
/// flip-book), and only the text runs come back — <see cref="ReadPdf"/> takes those. Handing the raw bytes of
/// a PDF to <see cref="Read"/> is refused with a sentence that says so, because a server-side PDF text
/// extractor is a dependency this project does not take.</para>
/// </summary>
public static class DocumentReader
{
    /// <summary>File extensions a document import accepts, for a picker's accept attribute.</summary>
    public const string DocumentExtensions = ".docx,.doc,.pdf";

    public static bool IsDocumentName(string? fileName)
        => fileName != null && (fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                                || fileName.EndsWith(".doc", StringComparison.OrdinalIgnoreCase)
                                || fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    public static bool IsPdfName(string? fileName) => fileName != null && fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikePdf(byte[] bytes) => bytes.Length >= 5 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' && bytes[4] == '-';

    public static ImportDocument Read(string fileName, byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) throw new ImportDocumentException($"\"{fileName}\" is empty.");

        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
            return DocxReader.Read(fileName, bytes);

        if (bytes.Length >= 8 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0
            && bytes[4] == 0xA1 && bytes[5] == 0xB1 && bytes[6] == 0x1A && bytes[7] == 0xE1)
            return DocReader.Read(fileName, bytes);

        if (LooksLikePdf(bytes))
            throw new ImportDocumentException($"\"{fileName}\" is a PDF. A PDF is read in your browser — choose it on the import page rather than sending the file.");

        if (bytes.Length >= 5 && bytes[0] == '{' && bytes[1] == '\\' && bytes[2] == 'r' && bytes[3] == 't' && bytes[4] == 'f')
            throw new ImportDocumentException($"\"{fileName}\" is a Rich Text file. Open it in Word and save it as .docx.");

        throw new ImportDocumentException($"\"{fileName}\" is not a Word document or a PDF this page can read. Save it as .docx and try again.");
    }

    /// <summary>A PDF, from the text runs the browser extracted. No runs = a scan, refused (decision D3).</summary>
    public static ImportDocument ReadPdf(string fileName, IReadOnlyList<PdfTextRun> runs) => PdfTextGrid.Build(fileName, runs);

    /// <summary>
    /// The table that best fits a set of expected column names — the header-row idea of the spreadsheet
    /// import, applied to every table of a document: in each table's first rows, count the cells that match
    /// one of the names; the table with the most wins, and on a tie the one with more rows. Returns -1 when
    /// the document has no table.
    /// </summary>
    public static int BestTable(ImportDocument doc, Func<string, bool> isExpectedHeader)
    {
        var best = -1;
        var bestScore = -1;
        var bestRows = -1;
        for (var t = 0; t < doc.Tables.Count; t++)
        {
            var grid = doc.ToGrid(t);
            var score = 0;
            for (var r = 0; r < Math.Min(12, grid.Count); r++)
                score = Math.Max(score, grid[r].Distinct(StringComparer.OrdinalIgnoreCase).Count(cell => isExpectedHeader(cell)));
            if (score > bestScore || (score == bestScore && grid.Count > bestRows))
            {
                best = t;
                bestScore = score;
                bestRows = grid.Count;
            }
        }
        return best;
    }

    /// <summary>
    /// A table as rows for a CSV writer: merged cells filled, caption rows kept (the header finder skips
    /// them itself), and line breaks inside a cell turned into " / " so a row stays one line of CSV.
    /// </summary>
    public static List<List<string>> RowsForCsv(ImportDocument doc, int tableIndex)
        => doc.ToGrid(tableIndex)
            .Select(r => r.Select(c => c.Replace("\r", " ").Replace("\n", " / ")).ToList())
            .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
            .ToList();
}
