using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml.Linq;
using QMgr.Application.DTOs;
using QMgr.Application.Import.Documents;

namespace QMgr.API.Application.Services;

/// <summary>
/// THE SCHOOL'S OWN FORMAT AS A WORD FILE, AND BACK (lesson plans plan §5.2, decision L7). A template is written with
/// <see cref="ZipArchive"/> and a few hundred characters of WordprocessingML — no library, no server dependency — from the
/// school's own sections, with the header filled where the product knows it. A filled template uploaded back is READ with
/// the existing <see cref="DocxReader"/> into the form for the teacher to check, and never stored: a Word-based
/// workflow then costs no storage at all.
///
/// <para>Each section's KEY travels in the document's custom properties (<c>s.&lt;key&gt;</c> = title, <c>c.&lt;key&gt;</c> for
/// a scheme column), so a template downloaded before the school renamed a section still reads back into the right place.
/// A section is matched by the first cell of its row starting with the title the template carried.</para>
/// </summary>
public static class TeachingPlanTemplates
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string KindProperty = "SaccTemplate";
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties";
    private static readonly XNamespace Vt = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";
    public const string ProcedureHeader = "Phase";

    // ---- Writing -------------------------------------------------------------------------------------------

    public static byte[] LessonPlan(IReadOnlyList<PlanSectionDto> sections, IReadOnlyList<string> phases, PlanHeaderDto header, string? dateText)
    {
        var body = new StringBuilder();
        body.Append(Para($"LESSON PLAN", bold: true, size: 28, center: true));
        if (!string.IsNullOrWhiteSpace(header.SchoolName)) body.Append(Para(header.SchoolName!, bold: true, size: 22, center: true));

        var headerRows = new List<(string, string)>
        {
            ("Teacher", header.TeacherName ?? ""), ("Subject", header.SubjectName ?? ""), ("Class", header.ClassText ?? ""),
            ("Date", dateText ?? ""), ("Time", header.Time ?? ""), ("Duration (minutes)", header.DurationMinutes?.ToString() ?? ""),
            ("Number of learners", header.Learners?.ToString() ?? ""), ("Term", header.TermName ?? ""), ("Week", header.Week?.ToString() ?? ""),
            ("Room", header.Room ?? "")
        };
        body.Append(Table(new[] { 2800, 7000 }, headerRows.Select(r => new[] { Cell(r.Item1, bold: true), Cell(r.Item2) }), headerRow: null));
        body.Append(Para(""));

        var props = new Dictionary<string, string> { [KindProperty] = "lesson-plan" };
        var rows = new List<string[]>();
        foreach (var s in sections.Where(s => s.Kind != PlanSectionKind.Procedure))
        {
            props["s." + s.Key] = s.Title;
            var hint = s.Kind is PlanSectionKind.Choice or PlanSectionKind.MultiChoice
                ? $"{(s.Kind == PlanSectionKind.MultiChoice ? "Write the ones that apply" : "Write one")}: {string.Join("; ", s.Choices)}"
                : s.Hint;
            rows.Add(new[] { Cell(s.Title + (s.Required ? " *" : ""), bold: true, hint: hint), Cell("") });
        }
        body.Append(Table(new[] { 2800, 7000 }, rows, headerRow: new[] { Cell("Section", bold: true), Cell("Your entry", bold: true) }, minRowHeight: 900));
        body.Append(Para(""));

        if (sections.FirstOrDefault(s => s.Kind == PlanSectionKind.Procedure) is { } procedure)
        {
            props["s." + procedure.Key] = procedure.Title;
            body.Append(Para(procedure.Title, bold: true, size: 22));
            var steps = phases.Select(ph => new[] { Cell(ph, bold: true), Cell(""), Cell(""), Cell("") });
            body.Append(Table(new[] { 1800, 1100, 3450, 3450 }, steps,
                headerRow: new[] { Cell(ProcedureHeader, bold: true), Cell("Minutes", bold: true), Cell("Teacher's activities", bold: true), Cell("Learners' activities", bold: true) },
                minRowHeight: 1400));
        }
        body.Append(Para("Fill this in, save it, and upload it on the lesson's plan: it is read into the form. Rows marked * are required.", size: 16, italic: true));
        return Package(body.ToString(), props, landscape: false);
    }

    public static byte[] Scheme(IReadOnlyList<PlanColumnDto> columns, string title, string? schoolName, IReadOnlyList<SchemeRowDto> rows, int weeks)
    {
        var body = new StringBuilder();
        body.Append(Para("SCHEME OF WORK", bold: true, size: 28, center: true));
        if (!string.IsNullOrWhiteSpace(schoolName)) body.Append(Para(schoolName!, bold: true, size: 22, center: true));
        body.Append(Para(title, bold: true, size: 20, center: true));

        var props = new Dictionary<string, string> { [KindProperty] = "scheme-of-work" };
        foreach (var c in columns) props["c." + c.Key] = c.Title;
        var width = 15000;
        var fixedCols = new[] { 700, 800 };
        var each = Math.Max(900, (width - fixedCols.Sum()) / Math.Max(1, columns.Count));
        var widths = fixedCols.Concat(columns.Select(_ => each)).ToArray();
        var header = new[] { Cell("Week", bold: true), Cell("Periods", bold: true) }.Concat(columns.Select(c => Cell(c.Title + (c.Required ? " *" : ""), bold: true))).ToArray();

        var lines = rows.Count > 0
            ? rows.Select(r => new[] { Cell(r.Week.ToString()), Cell(r.Periods?.ToString() ?? "") }.Concat(columns.Select(c => Cell(r.Cells.GetValueOrDefault(c.Key) ?? ""))).ToArray())
            : Enumerable.Range(1, Math.Clamp(weeks, 1, 20)).Select(w => new[] { Cell(w.ToString()), Cell("") }.Concat(columns.Select(_ => Cell(""))).ToArray());
        body.Append(Table(widths, lines, headerRow: header, minRowHeight: 700));
        body.Append(Para("Fill this in, save it, and upload it on the scheme: it is read into the form. Columns marked * are required.", size: 16, italic: true));
        return Package(body.ToString(), props, landscape: true);
    }

    private static byte[] Package(string bodyXml, Dictionary<string, string> props, bool landscape)
    {
        var page = landscape
            ? "<w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/>"
            : "<w:pgSz w:w=\"11906\" w:h=\"16838\"/>";
        var document = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<w:document xmlns:w=\"{W.NamespaceName}\"><w:body>{bodyXml}" +
            $"<w:sectPr>{page}<w:pgMar w:top=\"720\" w:right=\"720\" w:bottom=\"720\" w:left=\"720\" w:header=\"360\" w:footer=\"360\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";
        var pid = 2;
        var custom = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<Properties xmlns=\"{Cp.NamespaceName}\" xmlns:vt=\"{Vt.NamespaceName}\">" +
            string.Concat(props.Select(kv => $"<property fmtid=\"{{D5CDD505-2E9C-101B-9397-08002B2CF9AE}}\" pid=\"{pid++}\" name=\"{Esc(kv.Key)}\"><vt:lpwstr>{Esc(kv.Value)}</vt:lpwstr></property>")) +
            "</Properties>";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/docProps/custom.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.custom-properties+xml\"/></Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties\" Target=\"docProps/custom.xml\"/></Relationships>");
            Add(zip, "word/document.xml", document);
            Add(zip, "word/_rels/document.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"></Relationships>");
            Add(zip, "docProps/custom.xml", custom);
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? string.Empty;

    private const string Font = "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\"/>";

    private static string Run(string text, bool bold = false, int size = 20, bool italic = false)
        => $"<w:r><w:rPr>{Font}{(bold ? "<w:b/>" : "")}{(italic ? "<w:i/>" : "")}<w:sz w:val=\"{size}\"/></w:rPr><w:t xml:space=\"preserve\">{Esc(text)}</w:t></w:r>";

    private static string Para(string text, bool bold = false, int size = 20, bool center = false, bool italic = false)
        => $"<w:p><w:pPr>{(center ? "<w:jc w:val=\"center\"/>" : "")}<w:spacing w:after=\"60\"/></w:pPr>{(text.Length > 0 ? Run(text, bold, size, italic) : "")}</w:p>";

    private static string Cell(string text, bool bold = false, string? hint = null)
        => Para(text, bold, 18) + (string.IsNullOrWhiteSpace(hint) ? "" : Para(hint!, size: 15, italic: true));

    private static string Table(int[] widths, IEnumerable<string[]> rows, string[]? headerRow, int minRowHeight = 0)
    {
        var sb = new StringBuilder();
        sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>");
        foreach (var edge in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            sb.Append($"<w:{edge} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/>");
        sb.Append("</w:tblBorders><w:tblCellMar><w:left w:w=\"80\" w:type=\"dxa\"/><w:right w:w=\"80\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>");
        foreach (var w in widths) sb.Append($"<w:gridCol w:w=\"{w}\"/>");
        sb.Append("</w:tblGrid>");
        void Row(string[] cells, bool isHeader)
        {
            sb.Append("<w:tr>");
            if (isHeader) sb.Append("<w:trPr><w:tblHeader/></w:trPr>");
            else if (minRowHeight > 0) sb.Append($"<w:trPr><w:trHeight w:val=\"{minRowHeight}\"/></w:trPr>");
            for (var i = 0; i < cells.Length; i++)
                sb.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{widths[Math.Min(i, widths.Length - 1)]}\" w:type=\"dxa\"/>{(isHeader ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"EDE7EA\"/>" : "")}</w:tcPr>{cells[i]}</w:tc>");
            sb.Append("</w:tr>");
        }
        if (headerRow != null) Row(headerRow, true);
        foreach (var r in rows) Row(r, false);
        sb.Append("</w:tbl>");
        return sb.ToString();
    }

    // ---- Reading back --------------------------------------------------------------------------------------

    /// <summary>
    /// Reads a filled template into a plan's content, against the school's CURRENT sections. Nothing is stored.
    /// </summary>
    public static ReadTemplateResultDto Read(byte[] bytes, string fileName, IReadOnlyList<PlanSectionDto> sections, IReadOnlyList<PlanColumnDto> columns)
    {
        var props = ReadProperties(bytes);
        var doc = DocxReader.Read(fileName, bytes);
        var kindText = props.GetValueOrDefault(KindProperty);
        var isScheme = kindText == "scheme-of-work" || (kindText == null && doc.Tables.Any(t => t.ToGrid().FirstOrDefault()?.FirstOrDefault()?.Trim() == "Week"));
        var result = new ReadTemplateResultDto { Kind = isScheme ? TeachingPlanKind.SchemeOfWork : TeachingPlanKind.LessonPlan };
        if (kindText == null) result.Warnings.Add("This does not look like one of the school's templates; it was read as well as it could be. Check every section.");

        if (!isScheme)
        {
            // title (as the document carried it) → key: the document's own properties first, then today's titles.
            var byTitle = new List<(string Title, string Key)>();
            foreach (var kv in props.Where(p => p.Key.StartsWith("s.", StringComparison.Ordinal)))
                byTitle.Add((kv.Value, kv.Key[2..]));
            foreach (var s in sections) byTitle.Add((s.Title, s.Key));
            var known = sections.ToDictionary(s => s.Key, StringComparer.Ordinal);
            var content = new LessonPlanContentDto();

            for (var ti = 0; ti < doc.Tables.Count; ti++)
            {
                var grid = doc.ToGrid(ti);
                if (grid.Count == 0) continue;
                if (grid[0].FirstOrDefault()?.Trim() == ProcedureHeader)
                {
                    foreach (var row in grid.Skip(1))
                    {
                        string At(int i) => i < row.Count ? row[i].Trim() : string.Empty;
                        if (At(2).Length == 0 && At(3).Length == 0) continue;
                        content.Procedure.Add(new ProcedureStepDto
                        {
                            Phase = At(0), Minutes = int.TryParse(At(1), out var m) && m is > 0 and <= 600 ? m : null,
                            Teacher = At(2).Length > 0 ? At(2) : null, Learner = At(3).Length > 0 ? At(3) : null
                        });
                    }
                    result.SectionsRead++;
                    continue;
                }
                foreach (var row in grid)
                {
                    if (row.Count < 2) continue;
                    var label = row[0].Trim();
                    var value = string.Join("\n", row.Skip(1).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct()).Trim();
                    if (value.Length == 0) continue;
                    var match = byTitle.FirstOrDefault(b => b.Title.Length > 0 && label.StartsWith(b.Title, StringComparison.OrdinalIgnoreCase));
                    if (match.Key == null || !known.TryGetValue(match.Key, out var section))
                    {
                        if (label.StartsWith("Number of learners", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var n))
                            content.Header.Learners = n;
                        continue;
                    }
                    if (section.Kind == PlanSectionKind.MultiChoice || section.Kind == PlanSectionKind.Choice)
                    {
                        var picked = section.Choices.Where(c => value.Contains(c, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (picked.Count == 0) { result.Warnings.Add($"{section.Title}: \"{Short(value)}\" matched none of the choices, so it was left blank."); continue; }
                        value = section.Kind == PlanSectionKind.Choice ? picked[0] : string.Join("; ", picked);
                    }
                    content.Answers[section.Key] = value;
                    result.SectionsRead++;
                }
            }
            result.Content = content;
        }
        else
        {
            var byTitle = props.Where(p => p.Key.StartsWith("c.", StringComparison.Ordinal)).Select(p => (Title: p.Value, Key: p.Key[2..]))
                .Concat(columns.Select(c => (Title: c.Title, Key: c.Key))).ToList();
            var known = columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            var rows = new List<SchemeRowDto>();
            for (var ti = 0; ti < doc.Tables.Count; ti++)
            {
                var grid = doc.ToGrid(ti);
                if (grid.Count < 2 || grid[0].FirstOrDefault()?.Trim() != "Week") continue;
                var map = grid[0].Select(h => byTitle.FirstOrDefault(b => h.Trim().TrimEnd('*', ' ').Equals(b.Title, StringComparison.OrdinalIgnoreCase)).Key).ToList();
                var periodsCol = grid[0].FindIndex(h => h.Trim().StartsWith("Periods", StringComparison.OrdinalIgnoreCase));
                foreach (var line in grid.Skip(1))
                {
                    if (!int.TryParse(line.FirstOrDefault()?.Trim(), out var week)) continue;
                    var cells = new Dictionary<string, string>();
                    for (var i = 1; i < line.Count && i < map.Count; i++)
                        if (map[i] is { } key && known.Contains(key) && !string.IsNullOrWhiteSpace(line[i])) cells[key] = line[i].Trim();
                    if (cells.Count == 0) continue;
                    rows.Add(new SchemeRowDto
                    {
                        Key = Guid.NewGuid().ToString("N")[..12], Week = Math.Clamp(week, 1, 52), Cells = cells,
                        Periods = periodsCol > 0 && periodsCol < line.Count && int.TryParse(line[periodsCol].Trim(), out var pp) && pp is > 0 and <= 60 ? pp : null
                    });
                }
                result.SectionsRead += rows.Count;
            }
            result.Rows = rows;
            if (rows.Count == 0) result.Warnings.Add("No week had anything written in it.");
        }
        if (result.SectionsRead == 0) result.Warnings.Add("Nothing was read. Fill the template in and save it before uploading.");
        return result;
    }

    private static string Short(string s) => s.Length <= 40 ? s : s[..40] + "…";

    private static Dictionary<string, string> ReadProperties(byte[] bytes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            var entry = zip.GetEntry("docProps/custom.xml");
            if (entry == null) return result;
            using var stream = entry.Open();
            var xml = XDocument.Load(stream);
            foreach (var p in xml.Root?.Elements(Cp + "property") ?? Enumerable.Empty<XElement>())
                if (p.Attribute("name")?.Value is { } name) result[name] = p.Value;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { }
        return result;
    }
}
