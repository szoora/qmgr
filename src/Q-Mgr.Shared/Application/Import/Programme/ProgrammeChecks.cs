using System.Globalization;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;

namespace QMgr.Application.Import.Programme;

/// <summary>
/// The checks across every file of one import (plan §7), the one home for them. Nothing here writes or decides:
/// it lists what the reader must decide, what they should know, what could be fixed by one edit, and what was
/// merged — and the page puts a control beside each.
///
/// <list type="bullet">
/// <item><b>Must decide</b>: two documents disagree about when something happens; a day a rota leaves uncovered;
/// a written weekday that disagrees with its date; a row with no date; a meeting with no time.</item>
/// <item><b>Warnings</b>: a doubled rota day; a year repaired by the weekday check; a title naming another year;
/// a term that differs from the national calendar; the same person written two ways across documents.</item>
/// <item><b>Suggestions</b>: ONLY a one-edit change to a date (one digit, the month, or the year) that fills a gap
/// AND clears a double without creating a new problem. Offered, never applied without a press.</item>
/// <item><b>Merged</b>: the same event in two or three documents, kept once.</item>
/// </list>
/// </summary>
public static class ProgrammeChecks
{
    public static ProgrammeCheckResult Run(IReadOnlyList<ProgrammeFileReading> files, CalendarSettingsDto? settings = null, NationalCalendarDto? national = null)
    {
        settings ??= new CalendarSettingsDto();
        var result = new ProgrammeCheckResult();
        var all = files.SelectMany(f => f.Candidates).ToList();
        var seq = 0;
        string NextId(string prefix) => $"{prefix}-{++seq}";

        // Reset any previous merge decisions made by an earlier run of the checks (not by the reader).
        foreach (var c in all.Where(c => c.MergedIntoId != null && c.MergedIntoId.StartsWith("auto:", StringComparison.Ordinal)))
            c.MergedIntoId = null;
        foreach (var c in all) c.SameAsMeetingId = null;

        DuplicatesAndConflicts(files, result, NextId);
        RowProblems(files, all, result, NextId);
        RotaCoverage(files, all, settings, result, NextId);
        SamePersonTwoWays(files, result, NextId);
        NationalDifferences(files, all, national, result, NextId);
        return result;
    }

    // ---- Duplicates and conflicts --------------------------------------------------------------------------

    /// <summary>Title words for comparing across documents: folded, with "S" (the class prefix) and years dropped.</summary>
    public static HashSet<string> SimilarityWords(string? title)
        => ProgrammeText.Words(title).Where(w => w != "S" && !(w.Length == 4 && w.All(char.IsDigit))).ToHashSet(StringComparer.Ordinal);

    public static double Jaccard(HashSet<string> a, HashSet<string> b)
        => a.Count == 0 || b.Count == 0 ? 0 : a.Intersect(b).Count() / (double)a.Union(b).Count();

    /// <summary>Two titles that name the same thing on the same day: one contains the other, or most words are shared.</summary>
    public static bool SameThing(string? a, string? b)
    {
        var wa = SimilarityWords(a);
        var wb = SimilarityWords(b);
        if (wa.Count == 0 || wb.Count == 0) return false;
        var (small, large) = wa.Count <= wb.Count ? (wa, wb) : (wb, wa);
        return (small.Count >= 2 && small.IsSubsetOf(large)) || Jaccard(wa, wb) > 0.5;
    }

    /// <summary>Titles so alike that different dates for them must be a disagreement, not two occasions.</summary>
    public static bool StronglyAlike(string? a, string? b) => Jaccard(SimilarityWords(a), SimilarityWords(b)) >= 0.75;

    private static void DuplicatesAndConflicts(IReadOnlyList<ProgrammeFileReading> files, ProgrammeCheckResult result, Func<string, string> nextId)
    {
        var rows = files.SelectMany(f => f.Candidates)
            .Where(c => c.Include && c.Kind != ProgrammeRowKind.Rota && c.StartsOn.HasValue && c.MergedIntoId == null)
            .ToList();

        // Within one file: exactly the same title, date and time is typed twice.
        foreach (var g in rows.GroupBy(c => (c.FileName, ProgrammeText.TitleKey(c.Title), c.StartsOn, c.StartTime)).Where(g => g.Count() > 1))
        {
            var keep = g.First();
            foreach (var dup in g.Skip(1)) dup.MergedIntoId = "auto:" + keep.Id;
            result.Issues.Add(new ProgrammeIssue
            {
                Id = nextId("merged"), Severity = ProgrammeIssueSeverity.Merged, Kind = ProgrammeIssueKind.Duplicate,
                CandidateIds = g.Select(c => c.Id).ToList(), Date = keep.StartsOn,
                Message = $"\"{keep.Title}\" is written {g.Count()} times on {SchoolDateText.Short(keep.StartsOn!.Value)} in {keep.FileName} — kept once."
            });
        }

        // Across files: the same event on the same day → merged; strongly alike on different days → the reader decides.
        var groups = new List<List<ProgrammeCandidate>>();
        foreach (var c in rows.Where(c => c.MergedIntoId == null))
        {
            var group = groups.FirstOrDefault(g => g.Any(o => o.FileName != c.FileName && o.StartsOn == c.StartsOn && SameThing(o.Title, c.Title)));
            if (group != null) group.Add(c);
            else groups.Add(new List<ProgrammeCandidate> { c });
        }
        foreach (var g in groups.Where(g => g.Count > 1))
        {
            // A meeting keeps its duty; an event that is the same thing points at it. Otherwise the first document wins.
            var meeting = g.FirstOrDefault(c => c.Kind == ProgrammeRowKind.Meeting);
            var keep = meeting ?? g[0];
            foreach (var other in g.Where(o => o != keep))
            {
                if (meeting != null && other.Kind == ProgrammeRowKind.Event && g.Count(x => x.Kind == ProgrammeRowKind.Event) == 1)
                    other.SameAsMeetingId = meeting.Id;
                else other.MergedIntoId = "auto:" + keep.Id;
            }
            result.Issues.Add(new ProgrammeIssue
            {
                Id = nextId("merged"), Severity = ProgrammeIssueSeverity.Merged, Kind = ProgrammeIssueKind.Duplicate,
                CandidateIds = g.Select(c => c.Id).ToList(), Date = keep.StartsOn,
                Message = $"\"{keep.Title}\" on {SchoolDateText.Short(keep.StartsOn!.Value)} is in {string.Join(" and ", g.Select(c => c.FileName).Distinct())} — kept once."
            });
        }

        var live = rows.Where(c => c.MergedIntoId == null).ToList();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in live)
        {
            foreach (var b in live)
            {
                if (a.FileName == b.FileName || string.CompareOrdinal(a.Id, b.Id) >= 0 || a.StartsOn == b.StartsOn) continue;
                if (!StronglyAlike(a.Title, b.Title)) continue;
                // A thing on both dates in both files is two occasions, not a disagreement.
                if (live.Any(x => x.FileName == a.FileName && x != a && x.StartsOn == b.StartsOn && StronglyAlike(x.Title, a.Title))) continue;
                var pair = a.Id + "|" + b.Id;
                if (!reported.Add(pair)) continue;
                result.Issues.Add(new ProgrammeIssue
                {
                    Id = nextId("conflict"), Severity = ProgrammeIssueSeverity.MustDecide, Kind = ProgrammeIssueKind.Conflict,
                    CandidateIds = new List<string> { a.Id, b.Id },
                    Options = new List<DateOnly> { a.StartsOn!.Value, b.StartsOn!.Value }.Distinct().OrderBy(d => d).ToList(),
                    Message = $"The documents disagree: \"{a.Title}\" is on {Describe(a)} in {a.FileName}, and \"{b.Title}\" on {Describe(b)} in {b.FileName}. Which is right?"
                });
            }
        }
    }

    private static string Describe(ProgrammeCandidate c)
        => SchoolDateText.Short(c.StartsOn!.Value) + (c.StartTime != null ? " at " + c.StartTime : string.Empty);

    // ---- Per-row problems ------------------------------------------------------------------------------------

    private static void RowProblems(IReadOnlyList<ProgrammeFileReading> files, List<ProgrammeCandidate> all, ProgrammeCheckResult result, Func<string, string> nextId)
    {
        foreach (var file in files)
            foreach (var note in file.Notes.Where(n => n.Contains(" read as ", StringComparison.Ordinal)))
                result.Issues.Add(new ProgrammeIssue
                {
                    Id = nextId("year"), Severity = ProgrammeIssueSeverity.Warning, Kind = ProgrammeIssueKind.YearCorrected,
                    Message = $"{file.FileName}: {note}"
                });

        // What a document says about a year is reported even for a row merged into another document's copy.
        foreach (var c in all.Where(c => c.Include))
        {
            if (c.YearCorrected)
                result.Issues.Add(new ProgrammeIssue
                {
                    Id = nextId("year"), Severity = ProgrammeIssueSeverity.Warning, Kind = ProgrammeIssueKind.YearCorrected,
                    CandidateIds = { c.Id }, Date = c.StartsOn,
                    Message = $"\"{c.Title}\": {c.Notes.FirstOrDefault(n => n.Contains(" read as ", StringComparison.Ordinal))}"
                });
            foreach (var note in c.Notes.Where(n => n.StartsWith("The title says ", StringComparison.Ordinal)))
                result.Issues.Add(new ProgrammeIssue
                {
                    Id = nextId("title-year"), Severity = ProgrammeIssueSeverity.Warning, Kind = ProgrammeIssueKind.TitleYear,
                    CandidateIds = { c.Id }, Date = c.StartsOn, Message = $"\"{c.Title}\": {note}"
                });
        }

        foreach (var c in all.Where(c => c.Include && c.MergedIntoId == null))
        {
            if (!c.StartsOn.HasValue)
                result.Issues.Add(new ProgrammeIssue
                {
                    Id = nextId("undated"), Severity = ProgrammeIssueSeverity.MustDecide, Kind = ProgrammeIssueKind.Undated,
                    CandidateIds = { c.Id },
                    Message = $"\"{Label(c)}\" has no date ({(string.IsNullOrWhiteSpace(c.DateText) ? "nothing written" : $"\"{c.DateText}\"")}) — choose one, or leave it out."
                });
            if (c.WeekdayMismatch && c.StartsOn is { } d)
                result.Issues.Add(new ProgrammeIssue
                {
                    Id = nextId("weekday"), Severity = ProgrammeIssueSeverity.MustDecide, Kind = ProgrammeIssueKind.WeekdayMismatch,
                    CandidateIds = { c.Id }, Date = d,
                    Options = WeekdayOptions(c.DateText, d),
                    Message = $"\"{c.Title}\": {c.Notes.FirstOrDefault(n => n.Contains(" not a ", StringComparison.Ordinal)) ?? "the weekday and the date disagree."}"
                });
            if (c.Kind == ProgrammeRowKind.Meeting && c.Attendance != MeetingAttendance.EventOnly && c.StartTime == null)
                result.Issues.Add(new ProgrammeIssue
                {
                    Id = nextId("no-time"), Severity = ProgrammeIssueSeverity.MustDecide, Kind = ProgrammeIssueKind.NoTime,
                    CandidateIds = { c.Id }, Date = c.StartsOn,
                    Message = $"\"{c.Title}\" has no time. A meeting with a register needs one — add it, or import it as an event."
                });
        }
    }

    /// <summary>For a weekday that disagrees: the date as written, and the nearest date that IS that weekday.</summary>
    private static List<DateOnly> WeekdayOptions(string? text, DateOnly date)
    {
        var options = new List<DateOnly> { date };
        var words = ProgrammeText.Words(text);
        var named = Enum.GetValues<DayOfWeek>().FirstOrDefault(d => words.Any(w => d.ToString().ToUpperInvariant().StartsWith(w, StringComparison.Ordinal) && w.Length >= 3));
        if (words.Any(w => w.Length >= 3 && Enum.GetValues<DayOfWeek>().Any(d => d.ToString().ToUpperInvariant().StartsWith(w, StringComparison.Ordinal))))
        {
            var back = date;
            while (back.DayOfWeek != named) back = back.AddDays(-1);
            var forward = date;
            while (forward.DayOfWeek != named) forward = forward.AddDays(1);
            options.Add(date.DayNumber - back.DayNumber <= forward.DayNumber - date.DayNumber ? back : forward);
        }
        return options.Distinct().ToList();
    }

    // ---- Rota coverage ---------------------------------------------------------------------------------------

    private static void RotaCoverage(IReadOnlyList<ProgrammeFileReading> files, List<ProgrammeCandidate> all, CalendarSettingsDto settings, ProgrammeCheckResult result, Func<string, string> nextId)
    {
        foreach (var rota in all.Where(c => c.Kind == ProgrammeRowKind.Rota && c.Include && c.StartsOn.HasValue).GroupBy(c => c.RotaName ?? string.Empty))
        {
            var slots = rota.ToList();
            var file = files.FirstOrDefault(f => f.FileName == slots[0].FileName);
            var daily = slots.All(s => s.EndsOn == null || s.EndsOn == s.StartsOn);
            var from = file?.RangeStart ?? slots.Min(s => s.StartsOn!.Value);
            var to = file?.RangeEnd ?? slots.Max(s => (s.EndsOn ?? s.StartsOn)!.Value);
            if (to < from || to.DayNumber - from.DayNumber > 400) continue;

            var perDay = new Dictionary<DateOnly, List<ProgrammeCandidate>>();
            foreach (var s in slots)
                for (var d = s.StartsOn!.Value; d <= (s.EndsOn ?? s.StartsOn).Value && d.DayNumber - s.StartsOn.Value.DayNumber < 400; d = d.AddDays(1))
                {
                    if (!perDay.TryGetValue(d, out var list)) perDay[d] = list = new List<ProgrammeCandidate>();
                    list.Add(s);
                }

            var gaps = new List<DateOnly>();
            var doubles = new List<DateOnly>();
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var need = daily ? Needed(settings, d) : 1;
                var have = perDay.TryGetValue(d, out var l) ? l.Count : 0;
                if (have < need) gaps.Add(d);
                else if (have > need) doubles.Add(d);
            }

            // Suggestions first, so a gap can name the fix beside it.
            var suggestions = new Dictionary<DateOnly, ProgrammeIssue>();
            if (daily)
            {
                foreach (var gap in gaps)
                {
                    var fixes = new List<(ProgrammeCandidate Slot, DateOnly From)>();
                    foreach (var dbl in doubles)
                        foreach (var slot in perDay[dbl])
                        {
                            if (!OneEditApart(dbl, gap)) continue;
                            // No new problem: the person is not already on the gap day, and the double stays covered.
                            if (perDay.TryGetValue(gap, out var there) && there.Any(x => SamePerson(x, slot))) continue;
                            if (perDay[dbl].Count - 1 < Needed(settings, dbl)) continue;
                            fixes.Add((slot, dbl));
                        }
                    if (fixes.Count == 0) continue;
                    // Several slips could explain it: prefer the one that also spreads that person's duties further apart
                    // (a school spaces each person's turns). Two equally good fixes are a guess, and a guess is not offered.
                    var ranked = fixes.Select(f => (f.Slot, f.From, Gain: SpacingGain(slots, f.Slot, f.From, gap)))
                        .OrderByDescending(f => f.Gain).ToList();
                    if (ranked.Count > 1 && ranked[0].Gain == ranked[1].Gain) continue;
                    var (fixSlot, fixFrom, _) = ranked[0];
                    var issue = new ProgrammeIssue
                    {
                        Id = nextId("suggest"), Severity = ProgrammeIssueSeverity.Suggestion, Kind = ProgrammeIssueKind.DateSuggestion,
                        CandidateIds = { fixSlot.Id }, Date = gap, SuggestCandidateId = fixSlot.Id, SuggestFrom = fixFrom, SuggestTo = gap,
                        PersonText = fixSlot.PersonText,
                        Message = string.Create(CultureInfo.InvariantCulture,
                            $"{fixSlot.PersonText}: {fixFrom:dd/MM} → {gap:dd/MM} fills {SchoolDateText.Short(gap)} and clears the double on {SchoolDateText.Short(fixFrom)} — one digit, probably a typing slip.")
                    };
                    suggestions[gap] = issue;
                    result.Issues.Add(issue);
                }
            }

            foreach (var gap in gaps)
            {
                suggestions.TryGetValue(gap, out var fix);
                result.Issues.Add(new ProgrammeIssue
                {
                    Id = nextId("gap"), Severity = ProgrammeIssueSeverity.MustDecide, Kind = ProgrammeIssueKind.RotaGap,
                    Date = gap, SuggestCandidateId = fix?.SuggestCandidateId, SuggestFrom = fix?.SuggestFrom, SuggestTo = fix?.SuggestTo,
                    Message = $"Nobody is {rota.Key} on {SchoolDateText.Short(gap)}" + (fix != null ? $" — {fix.Message}" : " — choose somebody, or leave the gap.")
                });
            }
            foreach (var dbl in doubles)
            {
                var names = perDay[dbl].Select(s => s.PersonText).Distinct().ToList();
                result.Issues.Add(new ProgrammeIssue
                {
                    Id = nextId("double"), Severity = ProgrammeIssueSeverity.Warning, Kind = ProgrammeIssueKind.RotaDouble,
                    Date = dbl, CandidateIds = perDay[dbl].Select(s => s.Id).ToList(),
                    Message = $"{names.Count} people are {rota.Key} on {SchoolDateText.Short(dbl)}: {string.Join(" and ", names)}."
                });
            }
        }
    }

    /// <summary>How many people a daily rota expects on a date (decision D7), with a named exception by ISO date or weekday.</summary>
    public static int Needed(CalendarSettingsDto settings, DateOnly date)
    {
        var map = settings.PeoplePerRotaDayOn ?? new Dictionary<string, int>();
        if (map.TryGetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var byDate)) return Math.Max(0, byDate);
        foreach (var (key, value) in map)
            if (string.Equals(key, date.DayOfWeek.ToString(), StringComparison.OrdinalIgnoreCase)) return Math.Max(0, value);
        return Math.Max(1, settings.PeoplePerRotaDay);
    }

    /// <summary>
    /// One typing slip apart: the dates written dd/MM/yyyy differ in exactly one character, or in the month alone, or
    /// in the year alone. "02/11/2026" and "02/12/2026" are one digit apart.
    /// </summary>
    public static bool OneEditApart(DateOnly a, DateOnly b)
    {
        if (a == b) return false;
        var sa = a.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var sb = b.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        if (sa.Zip(sb).Count(p => p.First != p.Second) == 1) return true;
        if (a.Day == b.Day && a.Year == b.Year) return true;
        if (a.Day == b.Day && a.Month == b.Month) return true;
        return false;
    }

    /// <summary>How many days further from the person's nearest other duty the slot would be after the move (0 with no other duty).</summary>
    private static int SpacingGain(List<ProgrammeCandidate> slots, ProgrammeCandidate slot, DateOnly from, DateOnly to)
    {
        var others = slots.Where(s => s != slot && SamePerson(s, slot) && s.StartsOn.HasValue).Select(s => s.StartsOn!.Value).ToList();
        if (others.Count == 0) return 0;
        var before = others.Min(o => Math.Abs(o.DayNumber - from.DayNumber));
        var after = others.Min(o => Math.Abs(o.DayNumber - to.DayNumber));
        return after - before;
    }

    private static bool SamePerson(ProgrammeCandidate a, ProgrammeCandidate b)
        => (a.UserId.HasValue && a.UserId == b.UserId) || ProgrammeText.NameKey(a.PersonText) == ProgrammeText.NameKey(b.PersonText);

    // ---- The same person written two ways -------------------------------------------------------------------

    private static void SamePersonTwoWays(IReadOnlyList<ProgrammeFileReading> files, ProgrammeCheckResult result, Func<string, string> nextId)
    {
        var people = files.SelectMany(f => f.Candidates.Where(c => c.PersonText != null).Select(c => (f.FileName, Text: c.PersonText!))
                .Concat(f.NotOnRota.Select(n => (f.FileName, Text: n.PersonText))))
            .GroupBy(p => ProgrammeText.NameKey(p.Text)).Select(g => g.First()).ToList();
        for (var i = 0; i < people.Count; i++)
            for (var j = i + 1; j < people.Count; j++)
            {
                if (people[i].FileName == people[j].FileName) continue;
                var (likely, reason) = StaffNameResolver.Likeness(people[i].Text, people[j].Text);
                if (!likely) continue;
                result.Issues.Add(new ProgrammeIssue
                {
                    Id = nextId("person"), Severity = ProgrammeIssueSeverity.Warning, Kind = ProgrammeIssueKind.NameQuestion,
                    PersonText = people[i].Text,
                    Message = $"\"{people[i].Text}\" ({people[i].FileName}) and \"{people[j].Text}\" ({people[j].FileName}) — the same person? {reason}"
                });
            }
    }

    // ---- The national calendar (decision D9: warnings only) ---------------------------------------------------

    private static void NationalDifferences(IReadOnlyList<ProgrammeFileReading> files, List<ProgrammeCandidate> all, NationalCalendarDto? national, ProgrammeCheckResult result, Func<string, string> nextId)
    {
        if (national?.Entries == null || national.Entries.Count == 0) return;
        var term = TermFrom(files);
        if (term == null) return;
        var nationalTerm = national.Entries
            .Where(e => (e.Kind ?? string.Empty).Contains("term", StringComparison.OrdinalIgnoreCase) && e.StartsOn <= term.Value.End && e.EndsOn >= term.Value.Start)
            .OrderBy(e => Math.Abs(e.StartsOn.DayNumber - term.Value.Start.DayNumber)).FirstOrDefault();
        if (nationalTerm != null && (nationalTerm.StartsOn != term.Value.Start || nationalTerm.EndsOn != term.Value.End))
            result.Issues.Add(new ProgrammeIssue
            {
                Id = nextId("national"), Severity = ProgrammeIssueSeverity.Warning, Kind = ProgrammeIssueKind.NationalDifference,
                Message = string.Create(CultureInfo.InvariantCulture,
                    $"The documents run the term {term.Value.Start:d MMM} – {term.Value.End:d MMM yyyy}; {nationalTerm.Title} ({nationalTerm.Source ?? "national calendar"}) is {nationalTerm.StartsOn:d MMM} – {nationalTerm.EndsOn:d MMM yyyy}.")
            });

        foreach (var holiday in national.Entries.Where(e => (e.Kind ?? string.Empty).Contains("holiday", StringComparison.OrdinalIgnoreCase)))
            foreach (var c in all.Where(c => c.Include && c.MergedIntoId == null && c.Kind == ProgrammeRowKind.Meeting && c.StartsOn >= holiday.StartsOn && c.StartsOn <= holiday.EndsOn))
                result.Issues.Add(new ProgrammeIssue
                {
                    Id = nextId("national"), Severity = ProgrammeIssueSeverity.Warning, Kind = ProgrammeIssueKind.NationalDifference,
                    CandidateIds = { c.Id }, Date = c.StartsOn,
                    Message = $"\"{c.Title}\" is on {SchoolDateText.Short(c.StartsOn!.Value)}, which is {holiday.Title}."
                });
    }

    /// <summary>
    /// The term the documents describe: a rota's own "from … to …" when one says so, else the first and last dated
    /// rows of a term's activities. Null when nothing says.
    /// </summary>
    public static (DateOnly Start, DateOnly End)? TermFrom(IReadOnlyList<ProgrammeFileReading> files)
    {
        var activities = files.SelectMany(f => f.Candidates).Where(c => c.TableKind == ProgrammeTableKind.TermActivities && c.StartsOn.HasValue).ToList();
        if (activities.Count > 0)
            return (activities.Min(c => c.StartsOn!.Value), activities.Max(c => (c.EndsOn ?? c.StartsOn)!.Value));
        var ranged = files.Where(f => f.RangeStart.HasValue && f.RangeEnd.HasValue).ToList();
        if (ranged.Count > 0) return (ranged.Min(f => f.RangeStart!.Value), ranged.Max(f => f.RangeEnd!.Value));
        return null;
    }

    /// <summary>What a row is called in a sentence: the title, or for a rota slot the person.</summary>
    public static string Label(ProgrammeCandidate c) => c.Kind == ProgrammeRowKind.Rota ? $"{c.PersonText} ({c.RotaName})" : c.Title;

    /// <summary>People a rota lists with a role ("Prep Supervisor"), for a meeting of that role's attendance.</summary>
    public static List<NotOnRotaEntry> RoleHolders(IReadOnlyList<ProgrammeFileReading> files, string role)
    {
        var key = SimilarityWords(role).Select(w => w.TrimEnd('S')).ToHashSet(StringComparer.Ordinal);
        return files.SelectMany(f => f.NotOnRota)
            .Where(n => n.Role != null && SimilarityWords(n.Role).Select(w => w.TrimEnd('S')).ToHashSet(StringComparer.Ordinal).SetEquals(key))
            .ToList();
    }

    /// <summary>The distinct roles rotas list, for the attendance picker ("Prep Supervisor", "Administrator").</summary>
    public static List<string> Roles(IReadOnlyList<ProgrammeFileReading> files)
        => files.SelectMany(f => f.NotOnRota).Where(n => n.Role != null).Select(n => n.Role!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.Ordinal).ToList();

    /// <summary>The role a meeting title is about, when a rota in the import lists people with it ("Prep Supervisors Meeting").</summary>
    public static string? RoleForMeeting(IReadOnlyList<ProgrammeFileReading> files, string title)
    {
        var words = SimilarityWords(title).Select(w => w.TrimEnd('S')).ToHashSet(StringComparer.Ordinal);
        return Roles(files).FirstOrDefault(r =>
        {
            var rw = SimilarityWords(r).Select(w => w.TrimEnd('S')).ToList();
            return rw.Count > 0 && rw.All(words.Contains);
        });
    }
}
