using QMgr.Application.Import.Documents;
using QMgr.Domain.Enums;

namespace QMgr.Application.Import.Programme;

/// <summary>
/// Decides what kind of table a document holds (plan §4): a term's activities under week bands, a timed
/// daily programme, a schedule of meetings, a person-by-person duty rota, or a period-by-period rota. It reads
/// the header's vocabulary (WEEK, ACTIVITY, DAY/DATE, TIME, VENUE, CONVENER, MEETING, NAME, PHONE, TERMLY WEEK,
/// ADMINISTRATOR …) and checks the values under it have the shapes the header promises, and says WHY in
/// sentences the reader sees — because the reader can overrule it, and should know what they are overruling.
/// </summary>
public static class ProgrammeClassifier
{
    /// <summary>How far down a table to look for its header row (a title row or two above it is normal).</summary>
    public const int HeaderSearchRows = 8;

    /// <summary>What a header cell says its column holds.</summary>
    public static ProgrammeColumnRole RoleOf(string? header)
    {
        var words = ProgrammeText.Words(header);
        if (words.Count == 0) return ProgrammeColumnRole.None;
        var h = " " + string.Join(' ', words) + " ";
        bool Has(string w) => h.Contains(" " + w + " ", StringComparison.Ordinal);

        if (Has("PHONE") || Has("TEL") || Has("TELEPHONE") || Has("MOBILE") || Has("CONTACT") || Has("CONTACTS")) return ProgrammeColumnRole.Phone;
        if (Has("DUTY") && Has("WEEK")) return ProgrammeColumnRole.Date;
        if (Has("TERMLY") || (Has("WEEK") && (Has("NO") || Has("NUMBER")))) return ProgrammeColumnRole.PeriodLabel;
        if (Has("WEEK") || Has("WEEKS")) return ProgrammeColumnRole.Week;
        if (Has("TIME") || Has("TIMES")) return ProgrammeColumnRole.Time;
        if (Has("DATE") || Has("DATES") || h == " DAY " || h == " DAYS ") return ProgrammeColumnRole.Date;
        if (Has("MEETING") || Has("MEETINGS") || Has("ACTIVITY") || Has("ACTIVITIES") || Has("EVENT") || Has("EVENTS")
            || Has("PROGRAMME") || Has("ITEM") || Has("DESCRIPTION") || Has("PARTICULARS")) return ProgrammeColumnRole.Title;
        if (Has("VENUE") || Has("PLACE") || Has("LOCATION") || Has("WHERE") || Has("ROOM")) return ProgrammeColumnRole.Venue;
        if (Has("CONVENER") || Has("CONVENOR") || Has("RESPONSIBLE") || Has("CHARGE") || Has("ORGANISER") || Has("ORGANIZER") || Has("COORDINATOR"))
            return ProgrammeColumnRole.Responsible;
        if (Has("NAME") || Has("NAMES") || Has("ADMINISTRATOR") || Has("TEACHER") || Has("STAFF") || Has("OFFICER")) return ProgrammeColumnRole.Person;
        if (h == " S N " || h == " SN " || h == " NO " || h == " SNO " || h == " SERIAL " || Has("S N")) return ProgrammeColumnRole.Serial;
        return ProgrammeColumnRole.None;
    }

    /// <summary>The row in the first few that names the most column roles — never a caption row.</summary>
    public static int FindHeaderRow(ImportDocTable table, List<List<string>> grid)
    {
        var best = -1;
        var bestScore = 1; // at least two roles to count as a header
        for (var r = 0; r < Math.Min(HeaderSearchRows, grid.Count); r++)
        {
            if (table.IsCaptionRow(r)) continue;
            var roles = grid[r].Distinct(StringComparer.OrdinalIgnoreCase).Select(RoleOf).Where(x => x != ProgrammeColumnRole.None).ToList();
            var score = roles.Distinct().Count();
            if (score > bestScore) { bestScore = score; best = r; }
        }
        return best;
    }

    public static ProgrammeTableReading Classify(ImportDocument doc, int tableIndex)
    {
        var table = doc.Tables[tableIndex];
        var grid = table.ToGrid();
        var reading = new ProgrammeTableReading { TableIndex = tableIndex };
        var header = FindHeaderRow(table, grid);
        reading.HeaderRow = header;
        if (header < 0)
        {
            reading.Kind = ProgrammeTableKind.Unknown;
            reading.Reasons.Add("No header row was found (a row naming columns such as DATE, ACTIVITY, TIME or NAME).");
            reading.DataRows = grid.Count;
            return reading;
        }

        reading.Headers = grid[header].ToList();
        reading.Roles = reading.Headers.Select(RoleOf).ToList();
        // A header cell repeated across a horizontal span names one column, not several.
        for (var c = 1; c < reading.Roles.Count; c++)
            if (reading.Roles[c] != ProgrammeColumnRole.None && reading.Headers[c] == reading.Headers[c - 1] && reading.Roles[c] != ProgrammeColumnRole.Date)
                reading.Roles[c] = ProgrammeColumnRole.None;
        reading.MappingKey = string.Join('|', reading.Headers.Select(ProgrammeText.TitleKey));

        var dataRows = Enumerable.Range(header + 1, Math.Max(0, grid.Count - header - 1))
            .Where(r => !table.IsCaptionRow(r) && grid[r].Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        reading.DataRows = dataRows.Count;

        var roles = reading.Roles;
        bool HasRole(ProgrammeColumnRole role) => roles.Contains(role);
        List<string> ValuesOf(ProgrammeColumnRole role)
            => dataRows.SelectMany(r => roles.Select((x, i) => (x, i)).Where(p => p.x == role).Select(p => grid[r][p.i]))
                .Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        double Share(List<string> values, Func<string, bool> test) => values.Count == 0 ? 0 : values.Count(test) / (double)values.Count;

        var dates = ValuesOf(ProgrammeColumnRole.Date);
        var numericDateShare = Share(dates, v => System.Text.RegularExpressions.Regex.IsMatch(v, @"\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4}"));
        var anyDateShare = Share(dates, v => SchoolDateText.ContainsDate(v));
        var titleHeader = reading.Headers.Where((_, i) => roles[i] == ProgrammeColumnRole.Title).FirstOrDefault() ?? string.Empty;
        var heading = string.Join(' ', doc.Heading.Select(p => p.Text)) + " " + string.Join(' ', grid.Take(header).SelectMany(r => r).Distinct());
        var headingWords = ProgrammeText.Words(heading + " " + doc.FileName);

        var expected = new List<ProgrammeColumnRole>();
        if (HasRole(ProgrammeColumnRole.Person) && (HasRole(ProgrammeColumnRole.Phone) || numericDateShare >= 0.5) && !HasRole(ProgrammeColumnRole.PeriodLabel))
        {
            reading.Kind = ProgrammeTableKind.PersonRota;
            expected.AddRange(new[] { ProgrammeColumnRole.Person, ProgrammeColumnRole.Phone, ProgrammeColumnRole.Date });
            reading.Reasons.Add("A NAME column with a phone or duty-date column: one person per row.");
            if (numericDateShare > 0) reading.Reasons.Add($"{Math.Round(numericDateShare * 100)}% of the date cells are dates like 12/09/2026; the rest are job titles or blank.");
        }
        else if (HasRole(ProgrammeColumnRole.Person) && (HasRole(ProgrammeColumnRole.PeriodLabel) || HasRole(ProgrammeColumnRole.Date)))
        {
            reading.Kind = ProgrammeTableKind.PeriodRota;
            expected.AddRange(new[] { ProgrammeColumnRole.Person, ProgrammeColumnRole.Date });
            reading.Reasons.Add("A person for each period (a TERMLY WEEK or a date range): a period rota.");
            if (headingWords.Contains("ROTA")) reading.Reasons.Add("The heading says ROTA.");
        }
        else if (HasRole(ProgrammeColumnRole.Title) && ProgrammeText.Words(titleHeader).Any(w => w.StartsWith("MEETING", StringComparison.Ordinal))
                 || (headingWords.Contains("MEETINGS") && HasRole(ProgrammeColumnRole.Title) && HasRole(ProgrammeColumnRole.Date)))
        {
            reading.Kind = ProgrammeTableKind.MeetingSchedule;
            expected.AddRange(new[] { ProgrammeColumnRole.Date, ProgrammeColumnRole.Time, ProgrammeColumnRole.Title, ProgrammeColumnRole.Venue, ProgrammeColumnRole.Responsible });
            reading.Reasons.Add("The title column is headed MEETING: a schedule of meetings.");
        }
        else if (HasRole(ProgrammeColumnRole.Week) && HasRole(ProgrammeColumnRole.Title))
        {
            reading.Kind = ProgrammeTableKind.TermActivities;
            expected.AddRange(new[] { ProgrammeColumnRole.Week, ProgrammeColumnRole.Title, ProgrammeColumnRole.Date, ProgrammeColumnRole.Responsible });
            reading.Reasons.Add("Activities grouped under WEEK bands: a term's activities.");
        }
        else if (HasRole(ProgrammeColumnRole.Time) && HasRole(ProgrammeColumnRole.Title) && HasRole(ProgrammeColumnRole.Date))
        {
            reading.Kind = ProgrammeTableKind.DailyProgramme;
            expected.AddRange(new[] { ProgrammeColumnRole.Date, ProgrammeColumnRole.Time, ProgrammeColumnRole.Title, ProgrammeColumnRole.Venue, ProgrammeColumnRole.Responsible });
            reading.Reasons.Add("A DAY/DATE, a TIME and an ACTIVITY on each row: a timed programme.");
        }
        else if (HasRole(ProgrammeColumnRole.Date) && HasRole(ProgrammeColumnRole.Title))
        {
            reading.Kind = ProgrammeTableKind.TermActivities;
            expected.AddRange(new[] { ProgrammeColumnRole.Title, ProgrammeColumnRole.Date, ProgrammeColumnRole.Responsible });
            reading.Reasons.Add("A date and an activity on each row: a list of activities.");
        }
        else
        {
            reading.Kind = ProgrammeTableKind.Unknown;
            reading.Reasons.Add("The columns do not look like a programme, a schedule of meetings or a rota.");
            reading.Confidence = 0;
            return reading;
        }

        var found = expected.Count(HasRole);
        var confidence = expected.Count == 0 ? 0 : found / (double)expected.Count;
        if (reading.Kind is ProgrammeTableKind.PersonRota or ProgrammeTableKind.PeriodRota)
        {
            if (headingWords.Contains("ROTA") || headingWords.Contains("DUTY")) confidence = Math.Min(1, confidence + 0.2);
        }
        else if (dates.Count > 0)
        {
            reading.Reasons.Add($"{Math.Round(anyDateShare * 100)}% of the date cells read as dates.");
            confidence = confidence * 0.7 + anyDateShare * 0.3;
        }
        reading.Confidence = Math.Round(Math.Clamp(confidence, 0, 1), 2);
        return reading;
    }
}
