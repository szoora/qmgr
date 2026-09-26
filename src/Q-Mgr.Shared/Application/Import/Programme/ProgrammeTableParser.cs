using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using QMgr.Application.Import.Documents;
using QMgr.Domain.Enums;

namespace QMgr.Application.Import.Programme;

/// <summary>
/// Turns a classified document into candidate rows (plan §4–§5): events under week bands, a timed programme's
/// items, meetings and events from a schedule of meetings, and rota assignments. Every row keeps the cells it
/// was read from so the reader can check the reading against the document, and every doubt becomes a note on
/// the row rather than a guess. Pure: no directory, no database — resolution and checks come after.
/// </summary>
public static class ProgrammeTableParser
{
    private static readonly Regex ThemeRx = new(@"^\s*THEME\s*[:\-]\s*(?<t>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RangeRx = new(@"\bFROM\s+(?<a>.+?)\s+(?:TO|UNTIL|TILL)\s+(?<b>.+?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ClassGroupRx = new(@"\bS\s*\.?\s*(?<first>[1-6])(?<rest>(?:\s*(?:,|&|and)\s*(?:S\s*\.?\s*)?[1-6])*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex YearInTitle = new(@"(?<!\d)(20\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex NoteDateRx = new(
        @"(?:(?:mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun)[a-z]*\.?,?\s+)?\d{1,2}(?:st|nd|rd|th)?\s+(?:of\s+)?[A-Za-z]{3,9}\.?,?\s*(?:\d{4})?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal) { "UNEB", "UCE", "UACE", "PDU", "HOD", "HODS", "MOGA", "FNT", "PTA", "SACCO", "ICT", "PE", "CRE", "IRE" };

    private static readonly string[] StudentWords = { "DORM", "DORMS", "DORMITORY", "DORMITORIES", "STUDENT", "STUDENTS", "PREFECT", "PREFECTS", "PARENT", "PARENTS", "ASSEMBLY", "ASSEMBLIES", "PUPILS", "LEARNERS" };

    /// <summary>
    /// Reads a file's bytes through <see cref="DocumentReader"/>; a file that cannot be read comes back with
    /// <see cref="ProgrammeFileReading.Refusal"/> set to the sentence to show, never an exception.
    /// </summary>
    public static ProgrammeFileReading ReadFile(string fileName, byte[] bytes)
    {
        try
        {
            return Read(DocumentReader.Read(fileName, bytes));
        }
        catch (ImportDocumentException ex)
        {
            return new ProgrammeFileReading { FileName = fileName, Refusal = ex.Message };
        }
    }

    /// <summary>Reads one document, every table in it.</summary>
    public static ProgrammeFileReading Read(ImportDocument doc)
    {
        var file = new ProgrammeFileReading { FileName = doc.FileName, Format = doc.Format, Document = doc };

        // ---- The heading: the title and theme a school writes above its table (or in caption rows inside it).
        var headingLines = doc.Heading.Select(p => p.Text).ToList();
        foreach (var t in Enumerable.Range(0, doc.Tables.Count))
        {
            var table = doc.Tables[t];
            for (var r = 0; r < Math.Min(4, table.Rows.Count); r++)
                if (table.IsCaptionRow(r))
                    headingLines.Add(table.Rows[r].First(c => !string.IsNullOrWhiteSpace(c.Text)).Text);
        }
        file.Theme = headingLines.Select(l => ThemeRx.Match(l)).Where(m => m.Success).Select(m => TitleCase(m.Groups["t"].Value.Trim())).FirstOrDefault();
        file.Title = headingLines.FirstOrDefault(l => ProgrammeText.Words(l).Any(w => w is "PROGRAMME" or "ACTIVITIES" or "SCHEDULE" or "ROTA" or "CALENDAR" or "MEETINGS"))
                     ?? headingLines.Skip(1).FirstOrDefault() ?? headingLines.FirstOrDefault();
        file.Year = SchoolDateText.YearOf(headingLines) ?? SchoolDateText.YearOf(new[] { doc.FileName });
        foreach (var line in headingLines)
        {
            var range = RangeRx.Match(line);
            if (!range.Success) continue;
            var a = SchoolDateText.Parse(range.Groups["a"].Value, file.Year);
            var b = SchoolDateText.Parse(range.Groups["b"].Value, file.Year);
            if (a.Start.HasValue && b.Start.HasValue && a.Start <= b.Start)
            {
                file.RangeStart = a.Start;
                file.RangeEnd = b.End ?? b.Start;
                break;
            }
        }

        for (var t = 0; t < doc.Tables.Count; t++)
        {
            var reading = ProgrammeClassifier.Classify(doc, t);
            file.Tables.Add(reading);
        }
        foreach (var reading in file.Tables) ParseTable(file, doc, reading);
        return file;
    }

    /// <summary>
    /// Parses (or re-parses, after the reader overrode the kind) one table into candidates, replacing whatever that
    /// table produced before.
    /// </summary>
    public static void ParseTable(ProgrammeFileReading file, ImportDocument doc, ProgrammeTableReading reading)
    {
        file.Candidates.RemoveAll(c => c.TableIndex == reading.TableIndex && !c.FromNote);
        file.NotOnRota.RemoveAll(n => n.RowNumber >= 0 && TableOfNotOnRota(n) == reading.TableIndex);
        if (reading.HeaderRow < 0 || reading.Kind is ProgrammeTableKind.Unknown or ProgrammeTableKind.Ignore)
        {
            file.Candidates.RemoveAll(c => c.TableIndex == reading.TableIndex);
            return;
        }

        var table = doc.Tables[reading.TableIndex];
        var grid = table.ToGrid();
        var rows = Enumerable.Range(reading.HeaderRow + 1, Math.Max(0, grid.Count - reading.HeaderRow - 1))
            .Where(r => !table.IsCaptionRow(r) && grid[r].Any(c => !string.IsNullOrWhiteSpace(c)))
            .ToList();

        string Cell(int r, ProgrammeColumnRole role)
        {
            var i = reading.Roles.IndexOf(role);
            return i >= 0 && i < grid[r].Count ? grid[r][i].Trim() : string.Empty;
        }

        IEnumerable<string> Cells(int r, ProgrammeColumnRole role)
            => reading.Roles.Select((x, i) => (x, i)).Where(p => p.x == role && p.i < grid[r].Count).Select(p => grid[r][p.i].Trim());

        switch (reading.Kind)
        {
            case ProgrammeTableKind.TermActivities:
                ParseActivities(file, reading, rows, Cell);
                break;
            case ProgrammeTableKind.DailyProgramme:
                ParseProgramme(file, reading, rows, Cell);
                ParseNotes(file, doc, reading);
                break;
            case ProgrammeTableKind.MeetingSchedule:
                ParseMeetings(file, reading, rows, Cell);
                ParseNotes(file, doc, reading);
                break;
            case ProgrammeTableKind.PersonRota:
                ParsePersonRota(file, reading, rows, Cell, Cells);
                break;
            case ProgrammeTableKind.PeriodRota:
                ParsePeriodRota(file, reading, rows, Cell);
                break;
        }
        file.TableRows = file.Tables.Where(x => x.Kind is not (ProgrammeTableKind.Unknown or ProgrammeTableKind.Ignore)).Sum(x => x.DataRows);
    }

    // NotOnRota entries carry their table in the high digits of RowNumber (table * 10000 + row) so a re-parse can clear them.
    private static int TableOfNotOnRota(NotOnRotaEntry n) => n.RowNumber / 10000;

    // ---- Term activities ---------------------------------------------------------------------------------

    private static void ParseActivities(ProgrammeFileReading file, ProgrammeTableReading reading, List<int> rows, Func<int, ProgrammeColumnRole, string> cell)
    {
        file.EmptyWeeks = 0;
        var seenBands = new HashSet<string>(StringComparer.Ordinal);
        var n = 0;
        foreach (var r in rows)
        {
            var week = cell(r, ProgrammeColumnRole.Week);
            var title = cell(r, ProgrammeColumnRole.Title);
            var dateText = cell(r, ProgrammeColumnRole.Date);
            var band = week.Length > 0 ? SchoolDateText.Parse(week, file.Year, DayOfWeek.Monday) : new SchoolDateResult { NotADate = true };
            if (week.Length > 0 && seenBands.Add(week) && band.YearCorrected && band.Note != null)
                file.Notes.Add($"Week \"{week}\": {band.Note}");

            if (title.Length == 0)
            {
                if (week.Length > 0 && string.IsNullOrWhiteSpace(dateText)) file.EmptyWeeks++;
                continue;
            }

            var c = NewCandidate(file, reading, r, ++n, ProgrammeRowKind.Event, title);
            c.WeekText = week;
            c.DateText = dateText;
            c.ResponsibleText = NullIfEmpty(cell(r, ProgrammeColumnRole.Responsible));
            c.Location = NullIfEmpty(cell(r, ProgrammeColumnRole.Venue));
            ApplyDate(c, dateText, file.Year);
            if (c.StartsOn is { } d && band.Start is { } bs && band.End is { } be && (d < bs || d > be))
                c.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"The date is outside its week band ({bs:d MMM} – {be:d MMM})."));
            var time = cell(r, ProgrammeColumnRole.Time);
            if (time.Length > 0) ApplyTime(c, time);
            Finish(c, file, "activities");
            MakeMeetingIfOne(c);
            file.Candidates.Add(c);
        }
    }

    // ---- A timed daily programme ---------------------------------------------------------------------------

    private static void ParseProgramme(ProgrammeFileReading file, ProgrammeTableReading reading, List<int> rows, Func<int, ProgrammeColumnRole, string> cell)
    {
        var series = file.Title != null ? TitleCase(file.Title) : TitleCase(Path.GetFileNameWithoutExtension(file.FileName));
        var n = 0;
        foreach (var r in rows)
        {
            var title = cell(r, ProgrammeColumnRole.Title);
            if (title.Length == 0) continue;
            var c = NewCandidate(file, reading, r, ++n, ProgrammeRowKind.Event, title);
            c.DateText = cell(r, ProgrammeColumnRole.Date);
            c.TimeText = cell(r, ProgrammeColumnRole.Time);
            c.Location = NullIfEmpty(cell(r, ProgrammeColumnRole.Venue));
            c.ResponsibleText = NullIfEmpty(cell(r, ProgrammeColumnRole.Responsible));
            c.SeriesName = series;
            c.Category = "Programme";
            ApplyDate(c, c.DateText, file.Year);
            ApplyTime(c, c.TimeText);
            Finish(c, file, "programme");
            c.Audience = EventAudience.Staff | EventAudience.Students;
            MakeMeetingIfOne(c);
            file.Candidates.Add(c);
        }
    }

    // ---- A schedule of meetings --------------------------------------------------------------------------

    private static void ParseMeetings(ProgrammeFileReading file, ProgrammeTableReading reading, List<int> rows, Func<int, ProgrammeColumnRole, string> cell)
    {
        var n = 0;
        foreach (var r in rows)
        {
            var title = cell(r, ProgrammeColumnRole.Title);
            if (title.Length == 0) continue;
            var dateText = cell(r, ProgrammeColumnRole.Date);
            var date = SchoolDateText.Parse(dateText, file.Year);
            var isMeeting = IsStaffMeeting(title) && !(date.Start.HasValue && date.End.HasValue && date.End != date.Start);
            var c = NewCandidate(file, reading, r, ++n, isMeeting ? ProgrammeRowKind.Meeting : ProgrammeRowKind.Event, title);
            c.DateText = dateText;
            c.TimeText = cell(r, ProgrammeColumnRole.Time);
            c.Location = NullIfEmpty(cell(r, ProgrammeColumnRole.Venue));
            c.ResponsibleText = NullIfEmpty(cell(r, ProgrammeColumnRole.Responsible));
            ApplyDate(c, dateText, file.Year);
            ApplyTime(c, c.TimeText);
            Finish(c, file, isMeeting ? "meeting" : "meetings");
            if (isMeeting)
            {
                c.Category = "Meetings";
                c.Attendance = DefaultAttendance(title);
                if (c.StartTime == null) c.Notes.Add("No time is written — a meeting needs a start time.");
                else if (c.EndTime == null) c.Notes.Add("No end time is written — the meeting will be held as one hour.");
            }
            file.Candidates.Add(c);
        }
    }

    /// <summary>
    /// A MEETING in a term-activities table or a timed programme is a meeting too (2026-09-26). Those two table kinds
    /// hard-coded every row as an event, so a staff meeting written in the term's activities — the commonest place a
    /// school writes one — could never get a register. A multi-day row stays an event (a register is one sitting); the
    /// row keeps its source key, so an event imported from the same line before is matched and linked, not doubled.
    /// </summary>
    private static void MakeMeetingIfOne(ProgrammeCandidate c)
    {
        if (!IsStaffMeeting(c.Title)) return;
        if (c.StartsOn is { } s && c.EndsOn is { } e && e != s) return;
        c.Kind = ProgrammeRowKind.Meeting;
        c.Category = "Meetings";
        c.Audience = EventAudience.Staff;
        c.Attendance = DefaultAttendance(c.Title);
        if (c.StartTime == null) c.Notes.Add("No time is written — a meeting needs a start time.");
    }

    /// <summary>
    /// A staff meeting (a Session duty with a register) rather than an event: the title says "meeting", "briefing",
    /// "conference" or "panel", and it is not a students' meeting — a dorm meeting, a parents' meeting, or an academic
    /// meeting for named classes. Retreats, assemblies and guidance sessions are events.
    /// </summary>
    public static bool IsStaffMeeting(string? title)
    {
        var words = ProgrammeText.Words(title);
        if (!words.Any(w => w is "MEETING" or "MEETINGS" or "BRIEFING" or "BRIEFINGS" or "CONFERENCE" or "PANEL")) return false;
        if (words.Any(w => StudentWords.Contains(w))) return false;
        if (ClassGroupRx.IsMatch(title ?? string.Empty)) return false;
        return true;
    }

    /// <summary>
    /// Who a meeting expects by default, from its title alone. Anything the title does not settle is
    /// <see cref="MeetingAttendance.Undecided"/>: the page then reads the attendance words, and asks what it cannot read.
    /// It was EventOnly until 2026-09-26, and a school's meetings disappeared from the registers with nothing asked.
    /// </summary>
    public static MeetingAttendance DefaultAttendance(string title)
    {
        var key = " " + ProgrammeText.TitleKey(title) + " ";
        if (Regex.IsMatch(key, @" (BEGINNING|END|START) TERM( \d+)? STAFF MEETING ") || key.Trim() == "STAFF MEETING" || key.Contains(" GENERAL STAFF MEETING ", StringComparison.Ordinal))
            return MeetingAttendance.Everyone;
        if (key.Contains(" HEADS DEPARTMENT", StringComparison.Ordinal) || key.Contains(" HODS ", StringComparison.Ordinal) || key.Contains(" HOD ", StringComparison.Ordinal))
            return MeetingAttendance.DepartmentHeads;
        return MeetingAttendance.Undecided;
    }

    // ---- Notes under a table -----------------------------------------------------------------------------

    /// <summary>
    /// "NOTE: Class meetings will take place on Wednesday 16th September, 2026 / from 2:00 – 2:20pm." A note with a
    /// date AND a time is a candidate row, marked "found in a note" — offered, and left for the reader to include.
    /// </summary>
    private static void ParseNotes(ProgrammeFileReading file, ImportDocument doc, ProgrammeTableReading reading)
    {
        file.Candidates.RemoveAll(c => c.TableIndex == reading.TableIndex && c.FromNote);
        var notes = doc.NotesAfter(reading.TableIndex).Select(p => p.Text).ToList();
        var n = 0;
        for (var i = 0; i < notes.Count; i++)
        {
            var text = notes[i];
            var dateMatch = NoteDateRx.Matches(text).FirstOrDefault(m => SchoolDateText.ContainsDate(m.Value));
            if (dateMatch == null) continue;
            var rest = text[(dateMatch.Index + dateMatch.Length)..];
            var time = TimeRangeText.Parse(rest);
            if (!time.HasTime && i + 1 < notes.Count) { time = TimeRangeText.Parse(notes[i + 1]); rest = notes[i + 1]; }
            if (!time.HasTime) continue;

            var title = NoteTitle(text[..dateMatch.Index]);
            if (title.Length == 0) continue;
            var c = NewCandidate(file, reading, 0, 900 + ++n, IsStaffMeeting(title) ? ProgrammeRowKind.Meeting : ProgrammeRowKind.Event, title);
            c.FromNote = true;
            c.DateText = dateMatch.Value.Trim();
            c.TimeText = rest.Trim().TrimEnd('.');
            c.Notes.Add("Found in a note under the table, not in the table itself.");
            ApplyDate(c, c.DateText, file.Year);
            c.StartTime = time.StartText;
            c.EndTime = time.EndText;
            c.Include = false;
            Finish(c, file, "note");
            if (c.Kind == ProgrammeRowKind.Meeting) c.Attendance = DefaultAttendance(title);
            file.Candidates.Add(c);
        }
    }

    private static string NoteTitle(string before)
    {
        var s = Regex.Replace(before, @"^\s*(?:N\.?B\.?|NOTE|PLEASE NOTE)\s*[:\-]?\s*", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+(?:will\s+(?:take\s+place|be\s+held|hold|be)|shall\s+(?:take\s+place|be\s+held)|takes?\s+place|is|are)\b.*$", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+(?:on|from)\s*$", string.Empty, RegexOptions.IgnoreCase);
        s = s.Trim().Trim(',', ':', '-', '.').Trim();
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    }

    // ---- Rotas ---------------------------------------------------------------------------------------------

    private static void ParsePersonRota(ProgrammeFileReading file, ProgrammeTableReading reading, List<int> rows,
        Func<int, ProgrammeColumnRole, string> cell, Func<int, ProgrammeColumnRole, IEnumerable<string>> cells)
    {
        var rotaName = RotaNameFor(file, ProgrammeTableKind.PersonRota);
        var n = 0;
        var people = 0;
        foreach (var r in rows)
        {
            var name = cell(r, ProgrammeColumnRole.Person);
            if (name.Length == 0) continue;
            people++;
            var phone = NullIfEmpty(cell(r, ProgrammeColumnRole.Phone));
            string? role = null;
            var dated = 0;
            foreach (var value in cells(r, ProgrammeColumnRole.Date))
            {
                if (value.Length == 0) continue;
                var date = SchoolDateText.Parse(value, file.Year);
                if (date.HasDate)
                {
                    var c = NewCandidate(file, reading, r, ++n, ProgrammeRowKind.Rota, rotaName);
                    c.PersonText = name;
                    c.PhoneText = phone;
                    c.RotaName = rotaName;
                    c.DateText = value;
                    c.StartsOn = date.Start;
                    c.EndsOn = date.End;
                    if (date.Note != null) c.Notes.Add(date.Note);
                    c.Category = null;
                    c.SourceKey = RotaKey(rotaName, name, date.Start!.Value);
                    file.Candidates.Add(c);
                    dated++;
                }
                else if (date.NotADate) role ??= value;
                else
                {
                    // Looks like a date but is not a real one ("31/09/2026"): keep it visible as undated.
                    var c = NewCandidate(file, reading, r, ++n, ProgrammeRowKind.Rota, rotaName);
                    c.PersonText = name;
                    c.PhoneText = phone;
                    c.RotaName = rotaName;
                    c.DateText = value;
                    c.Notes.Add(date.Note ?? $"\"{value}\" is not a date.");
                    c.SourceKey = $"rota:{ProgrammeText.Slug(rotaName, 40)}:{ProgrammeText.Slug(ProgrammeText.NameKey(name), 80)}:undated-{n}";
                    file.Candidates.Add(c);
                    dated++;
                }
            }
            if (dated == 0 || role != null)
                file.NotOnRota.Add(new NotOnRotaEntry
                {
                    FileName = file.FileName, RowNumber = reading.TableIndex * 10000 + r + 1, PersonText = name, PhoneText = phone,
                    Role = role == null ? null : TitleCase(role)
                });
        }
        file.RotaPeople = people;
    }

    private static void ParsePeriodRota(ProgrammeFileReading file, ProgrammeTableReading reading, List<int> rows, Func<int, ProgrammeColumnRole, string> cell)
    {
        var rotaName = RotaNameFor(file, ProgrammeTableKind.PeriodRota);
        var n = 0;
        var people = 0;
        foreach (var r in rows)
        {
            var name = cell(r, ProgrammeColumnRole.Person);
            if (name.Length == 0) continue;
            people++;
            var label = cell(r, ProgrammeColumnRole.PeriodLabel);
            var dateText = cell(r, ProgrammeColumnRole.Date);
            var c = NewCandidate(file, reading, r, ++n, ProgrammeRowKind.Rota, rotaName);
            c.PersonText = name;
            c.RotaName = rotaName;
            c.WeekText = label;
            c.DateText = dateText;
            ApplyDate(c, dateText, file.Year);
            if (RomanNumerals.Parse(label) is { } week) c.Notes.Add($"Week {week} of the term.");
            c.SourceKey = c.StartsOn is { } d ? RotaKey(rotaName, name, d) : $"rota:{ProgrammeText.Slug(rotaName, 40)}:{ProgrammeText.Slug(ProgrammeText.NameKey(name), 80)}:undated-{n}";
            file.Candidates.Add(c);
        }
        file.RotaPeople = people;
    }

    /// <summary>"Teacher on Duty" for a person rota, "Administrator on Duty" when the heading says so.</summary>
    public static string RotaNameFor(ProgrammeFileReading file, ProgrammeTableKind kind)
    {
        var words = ProgrammeText.Words((file.Title ?? string.Empty) + " " + file.FileName);
        if (words.Any(w => w.StartsWith("ADMINISTRAT", StringComparison.Ordinal))) return "Administrator on Duty";
        if (words.Contains("PREFECT") || words.Contains("PREFECTS")) return "Prefect on Duty";
        return kind == ProgrammeTableKind.PeriodRota ? "On Duty" : "Teacher on Duty";
    }

    public static string RotaKey(string rotaName, string personText, DateOnly date)
        => $"rota:{ProgrammeText.Slug(rotaName, 40)}:{ProgrammeText.Slug(ProgrammeText.NameKey(personText), 80)}:{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    // ---- Shared row handling ------------------------------------------------------------------------------

    private static ProgrammeCandidate NewCandidate(ProgrammeFileReading file, ProgrammeTableReading reading, int gridRow, int n, ProgrammeRowKind kind, string title)
        => new()
        {
            Id = $"{ProgrammeText.Slug(file.FileName, 24)}#{reading.TableIndex}#{gridRow + 1}#{n}",
            FileName = file.FileName,
            TableIndex = reading.TableIndex,
            RowNumber = gridRow == 0 && n >= 900 ? 0 : gridRow + 1,
            TableKind = reading.Kind,
            Kind = kind,
            Title = CleanTitle(title)
        };

    private static void ApplyDate(ProgrammeCandidate c, string? dateText, int? year)
    {
        var date = SchoolDateText.Parse(dateText, year);
        c.StartsOn = date.Start;
        c.EndsOn = date.End ?? date.Start;
        c.WeekdayMismatch = date.WeekdayMismatch;
        c.YearCorrected = date.YearCorrected;
        if (date.Note != null) c.Notes.Add(date.Note);
        else if (!date.HasDate && !string.IsNullOrWhiteSpace(dateText)) c.Notes.Add($"\"{dateText}\" is not a date — choose one, or leave the row out.");
        else if (!date.HasDate) c.Notes.Add("No date is written — choose one, or leave the row out.");
    }

    private static void ApplyTime(ProgrammeCandidate c, string? timeText)
    {
        if (string.IsNullOrWhiteSpace(timeText)) return;
        var time = TimeRangeText.Parse(timeText);
        c.StartTime = time.StartText;
        c.EndTime = time.EndText;
        c.OpenEnded = time.OpenEnded;
        if (time.Note != null) c.Notes.Add(time.Note);
    }

    private static void Finish(ProgrammeCandidate c, ProgrammeFileReading file, string kindShort)
    {
        c.ClassNames = ClassNamesIn(c.Title);
        c.Category ??= CategoryFor(c.Title);
        if (c.ClassNames.Count > 0 || ProgrammeText.Words(c.Title).Any(w => StudentWords.Contains(w) || w is "LESSONS" or "CLASSES"))
            c.Audience |= EventAudience.Students;
        if (file.Year is { } y)
            foreach (Match m in YearInTitle.Matches(c.Title))
            {
                var stated = int.Parse(m.Value, CultureInfo.InvariantCulture);
                if (stated != y && Math.Abs(stated - y) == 1)
                    c.Notes.Add($"The title says {stated} in a {y} programme — check it.");
            }
        var date = c.StartsOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated";
        var time = c.StartTime?.Replace(":", string.Empty, StringComparison.Ordinal);
        // A meeting's key carries its TIME (B7): two meetings of one title on one day were one key, and the second was
        // refused as "the same meeting twice". Activities keep the old shape so a document imported before still matches
        // its own events.
        var withTime = kindShort is "programme" or "meeting" && time != null;
        var key = $"{kindShort}:{ProgrammeText.Slug(c.Title, 120)}:{date}{(withTime ? ":" + time : string.Empty)}";
        c.SourceKey = key.Length <= 200 ? key : key[..200];
    }

    /// <summary>"S.4,5 &amp; 6" → S.4, S.5, S.6; "S.1,2&amp;3" → S.1, S.2, S.3; "S.4 &amp; S.6" → S.4, S.6.</summary>
    public static List<string> ClassNamesIn(string? title)
    {
        var names = new List<string>();
        foreach (Match m in ClassGroupRx.Matches(title ?? string.Empty))
        {
            names.Add("S." + m.Groups["first"].Value);
            foreach (Match d in Regex.Matches(m.Groups["rest"].Value, @"[1-6]"))
                names.Add("S." + d.Value);
        }
        return names.Distinct().ToList();
    }

    /// <summary>A category from the organization's defaults, guessed from the title. Data, never behaviour; the reader can change it.</summary>
    public static string? CategoryFor(string? title)
    {
        var w = ProgrammeText.Words(title);
        bool Any(params string[] words) => words.Any(x => w.Contains(x));
        if (Any("EXAMS", "EXAMINATIONS", "EXAMINATION", "UNEB", "UCE", "UACE", "INVIGILATOR")) return "Examinations";
        if (Any("MASS", "RETREAT", "RECOLLECTION", "SAINTS", "SOULS", "CAROLS", "THANKSGIVING", "SERVICE", "WORSHIP", "PRAYER", "CHAPEL")) return "Spiritual";
        if (Any("MEETING", "MEETINGS", "BRIEFING")) return "Meetings";
        if (Any("INDEPENDENCE", "HEROES", "MARTYRS", "LABOUR", "CHRISTMAS", "EASTER", "NATIONAL")) return "National days";
        if (Any("SPORTS", "GAMES", "CLUB", "CLUBS", "COLOUR", "TOURNAMENT", "ATHLETICS")) return "Sports & clubs";
        if (Any("SEMINAR", "LESSONS", "ACADEMIC", "CHALLENGE", "STUDY", "TERM", "REPORTING", "VISITATION")) return "Academic";
        return null;
    }

    private static string CleanTitle(string title) => ImportDocText.Tidy(title).Trim().TrimEnd('.', ',').Trim();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// "BEGINNING OF TERM III 2026 PROGRAMME" → "Beginning of Term III 2026 Programme". Text already in mixed case is
    /// left as the school wrote it.
    /// </summary>
    public static string TitleCase(string? text)
    {
        var s = ImportDocText.Tidy(text);
        var letters = s.Where(char.IsLetter).ToList();
        if (letters.Count == 0 || letters.Count(char.IsUpper) < letters.Count * 0.7) return s;
        var small = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "of", "and", "the", "for", "by", "in", "on", "at", "to", "a", "an", "or" };
        var sb = new StringBuilder();
        var words = s.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (i > 0) sb.Append(' ');
            var bare = w.Trim(',', '.', ':', ';', '(', ')');
            if (bare.Length > 1 && bare.All(ch => ch is 'I' or 'V' or 'X')) { sb.Append(w); continue; }
            if (i > 0 && small.Contains(bare)) { sb.Append(w.ToLowerInvariant()); continue; }
            if (Acronyms.Contains(bare)) { sb.Append(w); continue; }
            sb.Append(w.Length <= 1 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        }
        return sb.ToString();
    }
}
