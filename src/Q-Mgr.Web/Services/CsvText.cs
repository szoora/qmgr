namespace QMgr.Web.Services;

/// <summary>
/// Reading the CSV text an import page gets back from <c>rosterImport.sheetToCsv</c> (SheetJS in the browser turns Excel
/// and CSV alike into CSV). One home for splitting it, shared by the staff import and the timetable import.
/// </summary>
public static class CsvText
{
    /// <summary>The non-blank lines, with Windows line endings normalised.</summary>
    public static List<string> Lines(string? text)
        => (text ?? string.Empty).Replace("\r\n", "\n").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

    /// <summary>A small RFC-4180-ish splitter: commas, double-quoted fields, doubled quotes inside them.</summary>
    public static List<string> ParseLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result;
    }
}
