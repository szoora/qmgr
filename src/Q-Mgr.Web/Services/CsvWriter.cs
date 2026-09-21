using System.Text;
using QMgr.Application.Import;

namespace QMgr.Web.Services;

/// <summary>
/// Builds the CSV files the admin pages hand to the browser. One home for the quoting rule,
/// because a second copy of an escaper is how one page starts producing files Excel can read and
/// another stops.
/// </summary>
public static class CsvWriter
{
    /// <summary>
    /// Quotes every field unconditionally and doubles any embedded quote — RFC 4180. Always
    /// quoting is deliberate: a comment containing a comma, a newline or a leading <c>=</c> is
    /// ordinary in feedback text, and quoting only "when necessary" is where that goes wrong.
    /// </summary>
    public static string Field(string? value)
        => "\"" + ImportRules.NeutraliseFormula(value).Replace("\"", "\"\"") + "\"";

    /// <summary>One row, already quoted.</summary>
    public static string Row(params string?[] fields) => string.Join(",", fields.Select(Field));

    /// <summary>
    /// A whole file. CRLF line endings, because that is what the spec says and what Excel on
    /// Windows expects; a BOM, so Excel reads it as UTF-8 rather than the local codepage and
    /// mangles every non-ASCII name — which in this app's market is most of them.
    /// </summary>
    public static string File(IEnumerable<string> rows) => "﻿" + string.Join("\r\n", rows);

    /// <summary>
    /// The data: URL the browser's downloadDataUrl helper takes. Kept here so the escaping and
    /// the charset declaration travel together with the writer that produced the text.
    /// </summary>
    public static string ToDataUrl(string csv) =>
        "data:text/csv;charset=utf-8," + Uri.EscapeDataString(csv);

    /// <summary>Convenience for the common "header row plus data rows" shape.</summary>
    public static string Build(IEnumerable<string?[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append('﻿');
        var first = true;
        foreach (var row in rows)
        {
            if (!first) sb.Append("\r\n");
            sb.Append(Row(row));
            first = false;
        }
        return sb.ToString();
    }
}
