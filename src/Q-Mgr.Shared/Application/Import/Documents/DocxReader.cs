using System.IO.Compression;
using System.Xml.Linq;

namespace QMgr.Application.Import.Documents;

/// <summary>
/// Reads a Word .docx (ECMA-376 WordprocessingML) into an <see cref="ImportDocument"/> with nothing but the
/// BCL: a .docx is a zip, <c>word/document.xml</c> is the body, and a table is <c>w:tbl</c> → <c>w:tr</c> →
/// <c>w:tc</c>. The two properties that carry meaning in a school's programme are honoured:
/// <c>w:gridSpan</c> (a cell across several columns) and <c>w:vMerge</c> (a cell down several rows — the week
/// band over five activities). No converter, no package (the standing no-server-dependencies rule).
/// </summary>
public static class DocxReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static ImportDocument Read(string fileName, byte[] bytes)
    {
        XDocument xml;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml")
                        ?? throw new ImportDocumentException($"\"{fileName}\" is a zip file but not a Word document.");
            using var stream = entry.Open();
            xml = XDocument.Load(stream, LoadOptions.None);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            throw new ImportDocumentException($"\"{fileName}\" could not be opened. It may be damaged, or protected with a password — save an unprotected copy and try again.");
        }

        var doc = new ImportDocument { FileName = fileName, Format = "docx" };
        var body = xml.Root?.Element(W + "body");
        if (body == null) return doc;
        foreach (var child in BlockChildren(body)) Visit(child, doc);
        return doc;
    }

    /// <summary>Body-level blocks, looking through content controls (<c>w:sdt</c>) that wrap them.</summary>
    private static IEnumerable<XElement> BlockChildren(XElement parent)
    {
        foreach (var el in parent.Elements())
        {
            if (el.Name == W + "sdt")
            {
                var content = el.Element(W + "sdtContent");
                if (content == null) continue;
                foreach (var inner in BlockChildren(content)) yield return inner;
            }
            else yield return el;
        }
    }

    private static void Visit(XElement el, ImportDocument doc)
    {
        if (el.Name == W + "p")
        {
            var text = ImportDocText.Tidy(TextOf(el));
            if (text.Length > 0) doc.Paragraphs.Add(new ImportDocParagraph { Text = text, BeforeTable = doc.Tables.Count });
        }
        else if (el.Name == W + "tbl")
        {
            doc.Tables.Add(ReadTable(el));
        }
    }

    private static ImportDocTable ReadTable(XElement tbl)
    {
        var table = new ImportDocTable();
        foreach (var tr in tbl.Elements(W + "tr"))
        {
            var row = new List<ImportDocCell>();
            var trPr = tr.Element(W + "trPr");
            var before = IntVal(trPr?.Element(W + "gridBefore"));
            if (before > 0) row.Add(new ImportDocCell { ColSpan = before });

            foreach (var tc in CellsOf(tr))
            {
                var pr = tc.Element(W + "tcPr");
                var span = Math.Max(1, IntVal(pr?.Element(W + "gridSpan")));
                var vMerge = pr?.Element(W + "vMerge");
                var continues = vMerge != null && (string?)vMerge.Attribute(W + "val") is not "restart";
                var paragraphs = new List<string>();
                foreach (var block in BlockChildren(tc))
                {
                    if (block.Name == W + "p") paragraphs.Add(TextOf(block));
                    // A table nested in a cell is read as that cell's text, row by row: it is almost
                    // always a layout trick, never a second record set.
                    else if (block.Name == W + "tbl")
                        foreach (var nested in ReadTable(block).Rows)
                            paragraphs.Add(string.Join(" ", nested.Select(c => c.Text)));
                }
                row.Add(new ImportDocCell
                {
                    Text = continues ? string.Empty : ImportDocText.JoinParagraphs(paragraphs),
                    ColSpan = span,
                    MergedFromAbove = continues
                });
            }

            var after = IntVal(trPr?.Element(W + "gridAfter"));
            if (after > 0) row.Add(new ImportDocCell { ColSpan = after });
            table.Rows.Add(row);
        }
        ComputeRowSpans(table);
        return table;
    }

    /// <summary>Cells of a row, looking through content controls and custom-XML wrappers.</summary>
    private static IEnumerable<XElement> CellsOf(XElement tr)
    {
        foreach (var el in tr.Elements())
        {
            if (el.Name == W + "tc") yield return el;
            else if (el.Name == W + "sdt" || el.Name == W + "customXml")
            {
                var content = el.Element(W + "sdtContent") ?? el;
                foreach (var inner in content.Elements(W + "tc")) yield return inner;
            }
        }
    }

    /// <summary>Counts, for each cell that starts a vertical merge, how many rows it covers.</summary>
    private static void ComputeRowSpans(ImportDocTable table)
    {
        // grid column → the cell currently open above it.
        var open = new Dictionary<int, ImportDocCell>();
        foreach (var row in table.Rows)
        {
            var col = 0;
            var seen = new HashSet<int>();
            foreach (var cell in row)
            {
                if (cell.MergedFromAbove && open.TryGetValue(col, out var start)) start.RowSpan++;
                else if (!cell.MergedFromAbove) open[col] = cell;
                seen.Add(col);
                col += Math.Max(1, cell.ColSpan);
            }
            foreach (var key in open.Keys.Where(k => !seen.Contains(k)).ToList()) open.Remove(key);
        }
    }

    private static int IntVal(XElement? el)
        => el != null && int.TryParse((string?)el.Attribute(W + "val"), out var v) ? v : 0;

    /// <summary>A paragraph's text: runs, tabs as spaces, line breaks as " / ". Deleted text (tracked changes) is skipped.</summary>
    private static string TextOf(XElement paragraph)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var n in paragraph.Descendants())
        {
            if (n.Ancestors(W + "del").Any()) continue;
            if (n.Name == W + "t") sb.Append(n.Value);
            else if (n.Name == W + "tab") sb.Append(' ');
            else if (n.Name == W + "br" || n.Name == W + "cr") sb.Append(" / ");
            else if (n.Name == W + "noBreakHyphen") sb.Append('-');
        }
        return sb.ToString();
    }
}
