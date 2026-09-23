using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// The clash checker (duty rota plan §6.2) and the integrity sweep's diagnosis (§6.3) — one function, so what the
/// master sees while building is exactly what the sweep re-checks against the world as it later is.
///
/// Hard breaches block publishing with no override; soft ones are shown and acknowledged with a note. Every issue
/// carries a short stable <see cref="TimetableIssueDto.Key"/> so the sweep can tell a new clash from an old one.
/// </summary>
public static class TimetableChecker
{
    /// <summary>How far ahead a Session duty (a meeting, an invigilation) is checked against lessons: the window lessons are materialised for (plan §7.1). A meeting in November does not block publishing a term's timetable today; the daily sweep reaches it in time.</summary>
    public const int SessionLookAheadDays = 14;

    /// <summary>A run of this many free teaching periods between a teacher's first and last lesson of a day is an idle gap.</summary>
    public const int IdleGapPeriods = 3;

    /// <summary>Everything the diagnosis reads besides the lessons, loaded once.</summary>
    public sealed record Context(
        TimetableSettingsDto Settings,
        TeachingLoadNormsDto Norms,
        IReadOnlyDictionary<Guid, (string Name, bool Active)> Teachers,
        IReadOnlyDictionary<Guid, (string Name, string Code, bool Active)> Subjects,
        IReadOnlyList<(Guid Id, Guid UserId, string ClassNorm, string ClassName, Guid SubjectId, int? PeriodsPerWeek)> Assignments,
        IReadOnlyDictionary<string, (string Name, string? Level)> Classes,
        IReadOnlySet<string> Rooms,
        IReadOnlyList<(Guid UserId, DateTime StartsAt, DateTime EndsAt, string Title)> Sessions,
        TimeZoneInfo Zone,
        DateOnly Today);

    public static async Task<Context> LoadContextAsync(QMgrDbContext db, Timetable timetable, TimetableSettingsDto settings,
        StaffPerformancePolicyDto policy, TimeZoneInfo zone, IEnumerable<TimetableLesson> lessons, CancellationToken ct = default)
    {
        var branchSettings = await db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == timetable.BranchId).Select(b => b.Settings).FirstOrDefaultAsync(ct);
        var vocab = StudentsController.ReadVocabularies(branchSettings);

        var assignments = await db.ClassTeacherAssignments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.BranchId == timetable.BranchId && a.EndedAt == null && a.Role == ClassTeacherRole.SubjectTeacher && a.SubjectId != null)
            .Select(a => new { a.Id, a.UserId, a.ClassName, SubjectId = a.SubjectId!.Value, a.PeriodsPerWeek })
            .ToListAsync(ct);

        var lessonList = lessons.ToList();
        var userIds = lessonList.Select(l => l.TeacherUserId).Concat(assignments.Select(a => a.UserId)).Distinct().ToList();
        var teachers = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Username, u.IsActive, u.OrganizationId })
            .ToDictionaryAsync(u => u.Id, u => (Name: PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName, u.Username), Active: u.IsActive && u.OrganizationId == timetable.OrganizationId), ct);

        var subjects = await db.Subjects.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == timetable.OrganizationId)
            .ToDictionaryAsync(s => s.Id, s => (s.Name, s.Code, s.IsActive), ct);

        var from = timetable.EffectiveFrom > Today(zone) ? timetable.EffectiveFrom : Today(zone);
        var to = new[] { timetable.EffectiveTo, Today(zone).AddDays(SessionLookAheadDays) }.Min();
        var sessions = new List<(Guid, DateTime, DateTime, string)>();
        if (from <= to)
        {
            var fromUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), zone);
            var toUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
            var rows = await db.StaffDuties.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.BranchId == timetable.BranchId && d.IsActive && d.Kind == DutyKind.Session && d.StartsAt < toUtc && d.EndsAt > fromUtc && d.ExpectedUserIds != null)
                .Select(d => new { d.StartsAt, d.EndsAt, d.Title, d.ExpectedUserIds })
                .ToListAsync(ct);
            foreach (var r in rows)
                foreach (var u in r.ExpectedUserIds!)
                    sessions.Add((u, r.StartsAt, r.EndsAt, r.Title));
        }

        return new Context(
            settings,
            policy.TeachingLoadNorms ?? new TeachingLoadNormsDto(),
            teachers,
            subjects.ToDictionary(kv => kv.Key, kv => (kv.Value.Name, kv.Value.Code, kv.Value.IsActive)),
            assignments.Select(a => (a.Id, a.UserId, TimetableCycle.Normalize(a.ClassName), a.ClassName, a.SubjectId, a.PeriodsPerWeek)).ToList(),
            vocab.Classes.Where(c => c.IsActive).GroupBy(c => TimetableCycle.Normalize(c.Name)).ToDictionary(g => g.Key, g => (g.First().Name, g.First().Level)),
            vocab.Rooms.Where(r => r.IsActive).Select(r => TimetableCycle.Normalize(r.Name)).ToHashSet(),
            sessions,
            zone,
            Today(zone));
    }

    private static DateOnly Today(TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));

    public static TimetableDiagnosisDto Diagnose(Timetable timetable, IReadOnlyList<TimetableLesson> lessons, Context ctx)
    {
        var issues = new List<TimetableIssueDto>();
        var s = ctx.Settings;
        var cycleDays = timetable.CycleDays;
        var teachingDayCount = Math.Max(1, TimetableCycle.TeachingDays(s).Count);
        var weeks = Math.Max(1, cycleDays / teachingDayCount);

        string TeacherName(Guid id) => ctx.Teachers.TryGetValue(id, out var t) ? t.Name : "A former member of staff";
        string ClassName(string norm, string fallback) => ctx.Classes.TryGetValue(norm, out var c) ? c.Name : fallback;
        string Day(int d) => TimetableCycle.CycleDayLabel(s, cycleDays, d);
        // A joint lesson (several rows, one group) is one lesson; every other row is its own.
        static Guid Identity(TimetableLesson l) => l.GroupId ?? l.Id;
        static string SlotKey(TimetableLesson l) => $"{l.CycleDay}|{l.PeriodKey.ToLowerInvariant()}";

        void Add(TimetableIssueKind kind, string resource, string resourceKey, string resourceName, int? day, string? period, string message, IEnumerable<TimetableLesson> involved, string? extra = null)
        {
            var key = ShortHash($"{(int)kind}|{resource}|{resourceKey}|{day}|{period?.ToLowerInvariant()}|{extra}");
            if (issues.Any(i => i.Key == key)) return;
            issues.Add(new TimetableIssueDto
            {
                Key = key,
                Kind = kind,
                Severity = (int)kind < 20 ? TimetableIssueSeverity.Hard : TimetableIssueSeverity.Soft,
                Resource = resource,
                ResourceKey = resourceKey,
                ResourceName = resourceName,
                CycleDay = day,
                PeriodKey = period,
                Message = message,
                LessonIds = involved.Select(l => l.Id).Distinct().ToList()
            });
        }

        // ---- Per lesson ----
        foreach (var l in lessons)
        {
            var teacher = TeacherName(l.TeacherUserId);
            var cls = ClassName(l.ClassNameNormalized, l.ClassName);
            var subject = ctx.Subjects.TryGetValue(l.SubjectId, out var sub) ? sub : (Name: "A removed subject", Code: "?", Active: false);
            var at = $"{Day(l.CycleDay)} {l.PeriodKey}";

            if (TimetableCycle.LessonPeriodOn(s, cycleDays, l.CycleDay, l.PeriodKey) == null)
                Add(TimetableIssueKind.OutsideBellSchedule, "class", l.ClassNameNormalized, cls, l.CycleDay, l.PeriodKey,
                    $"{cls} {subject.Code} is placed at {at}, which is not a teaching period in the bell schedule.", new[] { l });
            if (!ctx.Teachers.TryGetValue(l.TeacherUserId, out var t) || !t.Active)
                Add(TimetableIssueKind.TeacherInactive, "teacher", l.TeacherUserId.ToString(), teacher, l.CycleDay, l.PeriodKey,
                    $"{teacher} is no longer an active member of staff but teaches {cls} {subject.Code} at {at}.", new[] { l });
            if (!subject.Active)
                Add(TimetableIssueKind.SubjectRetired, "class", l.ClassNameNormalized, cls, l.CycleDay, l.PeriodKey,
                    $"{subject.Name} is retired but {cls} has it at {at}.", new[] { l });
            if (!ctx.Classes.ContainsKey(l.ClassNameNormalized))
                Add(TimetableIssueKind.UnknownClass, "class", l.ClassNameNormalized, l.ClassName, l.CycleDay, l.PeriodKey,
                    $"{l.ClassName} is not a configured, active class, but has {subject.Code} at {at}.", new[] { l });
            if (l.RoomNormalized is { Length: > 0 } room && !ctx.Rooms.Contains(room))
                Add(TimetableIssueKind.UnknownRoom, "room", room, l.Room ?? room, l.CycleDay, l.PeriodKey,
                    $"{l.Room} is not a configured, active room, but {cls} {subject.Code} is in it at {at}.", new[] { l });
            if (!ctx.Assignments.Any(a => a.UserId == l.TeacherUserId && a.ClassNorm == l.ClassNameNormalized && a.SubjectId == l.SubjectId))
                Add(TimetableIssueKind.TeacherNotAssigned, "teacher", l.TeacherUserId.ToString(), teacher, l.CycleDay, l.PeriodKey,
                    $"{teacher} is not assigned {subject.Name} in {cls}, but teaches it at {at}.", new[] { l });

            foreach (var u in s.Unavailability.Where(u => u.UserId == l.TeacherUserId && u.CycleDay == l.CycleDay
                                                         && (u.PeriodKey == null || string.Equals(u.PeriodKey, l.PeriodKey, StringComparison.OrdinalIgnoreCase))))
                Add(TimetableIssueKind.TeacherUnavailable, "teacher", l.TeacherUserId.ToString(), teacher, l.CycleDay, l.PeriodKey,
                    $"{teacher} is unavailable at {at}{(u.Note is { } note ? $" ({note})" : "")} but teaches {cls} {subject.Code}.", new[] { l });
        }

        // ---- Double bookings, per slot ----
        foreach (var slot in lessons.GroupBy(SlotKey))
        {
            var first = slot.First();
            var at = $"{Day(first.CycleDay)} {first.PeriodKey}";

            foreach (var byTeacher in slot.GroupBy(l => l.TeacherUserId).Where(g => g.Select(Identity).Distinct().Count() > 1))
                Add(TimetableIssueKind.TeacherDoubleBooked, "teacher", byTeacher.Key.ToString(), TeacherName(byTeacher.Key), first.CycleDay, first.PeriodKey,
                    $"{TeacherName(byTeacher.Key)} is booked for {string.Join(" and ", byTeacher.Select(l => l.ClassName).Distinct())} at {at}.", byTeacher);

            foreach (var byClass in slot.GroupBy(l => l.ClassNameNormalized).Where(g => g.Select(Identity).Distinct().Count() > 1))
                Add(TimetableIssueKind.ClassDoubleBooked, "class", byClass.Key, ClassName(byClass.Key, byClass.First().ClassName), first.CycleDay, first.PeriodKey,
                    $"{ClassName(byClass.Key, byClass.First().ClassName)} has two lessons at {at}: {string.Join(" and ", byClass.GroupBy(Identity).Select(g => SubjectCode(ctx, g.First().SubjectId)))}.", byClass);

            foreach (var byRoom in slot.Where(l => !string.IsNullOrEmpty(l.RoomNormalized)).GroupBy(l => l.RoomNormalized!).Where(g => g.Select(Identity).Distinct().Count() > 1))
                Add(TimetableIssueKind.RoomDoubleBooked, "room", byRoom.Key, byRoom.First().Room ?? byRoom.Key, first.CycleDay, first.PeriodKey,
                    $"{byRoom.First().Room} holds {string.Join(" and ", byRoom.GroupBy(Identity).Select(g => $"{string.Join(" + ", g.Select(l => l.ClassName).Distinct())} {SubjectCode(ctx, g.First().SubjectId)}"))} at {at}.", byRoom);
        }

        // ---- A Session duty over a lesson, in the look-ahead window ----
        if (ctx.Sessions.Count > 0)
        {
            var from = timetable.EffectiveFrom > ctx.Today ? timetable.EffectiveFrom : ctx.Today;
            var to = new[] { timetable.EffectiveTo, ctx.Today.AddDays(SessionLookAheadDays) }.Min();
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                if (TimetableCycle.CycleDayOn(s, cycleDays, timetable.EffectiveFrom, date) is not { } cycleDay) continue;
                foreach (var l in lessons.Where(l => l.CycleDay == cycleDay))
                {
                    if (TimetableCycle.LessonPeriodOn(s, cycleDays, cycleDay, l.PeriodKey) is not { } period) continue;
                    var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimetableCycle.ParseTime(period.Start)!.Value), ctx.Zone);
                    var end = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimetableCycle.ParseTime(period.End)!.Value), ctx.Zone);
                    foreach (var session in ctx.Sessions.Where(x => x.UserId == l.TeacherUserId && x.StartsAt < end && x.EndsAt > start))
                        Add(TimetableIssueKind.TeacherOnSessionDuty, "teacher", l.TeacherUserId.ToString(), TeacherName(l.TeacherUserId), cycleDay, l.PeriodKey,
                            string.Create(CultureInfo.InvariantCulture, $"{TeacherName(l.TeacherUserId)} is expected at \"{session.Title}\" on {date:ddd dd MMM yyyy} during {l.ClassName} {SubjectCode(ctx, l.SubjectId)} ({l.PeriodKey})."),
                            new[] { l }, extra: date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }
            }
        }

        // ---- Soft: a teacher's load ----
        foreach (var byTeacher in lessons.GroupBy(l => l.TeacherUserId))
        {
            var id = byTeacher.Key;
            var name = TeacherName(id);
            // A joint lesson in one slot is one period of teaching.
            var slots = byTeacher.GroupBy(SlotKey).Select(g => g.First()).ToList();
            var perWeek = (decimal)slots.Count / weeks;
            if (perWeek > ctx.Norms.MaxLessonsPerWeek)
                Add(TimetableIssueKind.OverWeeklyMaximum, "teacher", id.ToString(), name, null, null,
                    string.Create(CultureInfo.InvariantCulture, $"{name} teaches {perWeek:0.#} {Lessons(perWeek)} a week; the norm is at most {ctx.Norms.MaxLessonsPerWeek}."), byTeacher);

            foreach (var day in slots.GroupBy(l => l.CycleDay))
            {
                if (day.Count() > ctx.Norms.MaxPeriodsPerDay)
                    Add(TimetableIssueKind.OverDailyMaximum, "teacher", id.ToString(), name, day.Key, null,
                        $"{name} teaches {day.Count()} periods on {Day(day.Key)}; the norm is at most {ctx.Norms.MaxPeriodsPerDay}.", day);

                var order = (TimetableCycle.DayTypeOf(s, cycleDays, day.Key)?.Periods ?? new()).OrderBy(p => TimetableCycle.ParseTime(p.Start)).ToList();
                var taught = day.Select(l => l.PeriodKey.ToLowerInvariant()).ToHashSet();
                int run = 0, maxRun = 0, gap = 0, maxGap = 0;
                var started = false;
                var lastTaughtIndex = order.FindLastIndex(p => taught.Contains(p.Key.ToLowerInvariant()));
                for (var i = 0; i < order.Count; i++)
                {
                    var p = order[i];
                    if (p.Kind != BellPeriodKind.Lesson) { run = 0; continue; }
                    if (taught.Contains(p.Key.ToLowerInvariant()))
                    {
                        run++; maxRun = Math.Max(maxRun, run);
                        started = true; maxGap = Math.Max(maxGap, gap); gap = 0;
                    }
                    else
                    {
                        run = 0;
                        if (started && i < lastTaughtIndex) gap++;
                    }
                }
                if (maxRun > ctx.Norms.MaxConsecutivePeriods)
                    Add(TimetableIssueKind.TooManyConsecutive, "teacher", id.ToString(), name, day.Key, null,
                        $"{name} teaches {maxRun} periods in a row on {Day(day.Key)}; the norm is at most {ctx.Norms.MaxConsecutivePeriods}.", day);
                if (maxGap >= IdleGapPeriods)
                    Add(TimetableIssueKind.IdleGap, "teacher", id.ToString(), name, day.Key, null,
                        $"{name} waits {maxGap} free periods between lessons on {Day(day.Key)}.", day);
            }
        }

        // Under the minimum: anybody with a subject to teach, placed or not. The mixed-level minimum applies to a
        // teacher with classes on both O-Level (S1–S4) and A-Level (S5–S6) (plan §15 decision 7).
        foreach (var byTeacher in ctx.Assignments.GroupBy(a => a.UserId))
        {
            var placed = (decimal)lessons.Where(l => l.TeacherUserId == byTeacher.Key).GroupBy(SlotKey).Count() / weeks;
            var levels = byTeacher.Select(a => LevelBand(ctx.Classes.TryGetValue(a.ClassNorm, out var c) ? c.Level : null, a.ClassName)).ToHashSet();
            var min = levels.Contains('A') && levels.Contains('O') ? ctx.Norms.MinLessonsPerWeekMixedLevel : ctx.Norms.MinLessonsPerWeek;
            if (placed < min)
                Add(TimetableIssueKind.UnderWeeklyMinimum, "teacher", byTeacher.Key.ToString(), TeacherName(byTeacher.Key), null, null,
                    string.Create(CultureInfo.InvariantCulture, $"{TeacherName(byTeacher.Key)} teaches {placed:0.#} {Lessons(placed)} a week; the norm is at least {min}."),
                    lessons.Where(l => l.TeacherUserId == byTeacher.Key));
        }

        // ---- Soft: a class's day ----
        foreach (var byClassDay in lessons.GroupBy(l => (l.ClassNameNormalized, l.CycleDay)))
        {
            var order = (TimetableCycle.DayTypeOf(s, cycleDays, byClassDay.Key.CycleDay)?.Periods ?? new())
                .Where(p => p.Kind == BellPeriodKind.Lesson).OrderBy(p => TimetableCycle.ParseTime(p.Start))
                .Select(p => p.Key.ToLowerInvariant()).ToList();
            foreach (var bySubject in byClassDay.GroupBy(l => l.SubjectId))
            {
                // A double period (adjacent) is one sitting; the same subject again later in the day is the breach.
                var positions = bySubject.Select(l => order.IndexOf(l.PeriodKey.ToLowerInvariant())).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
                var sittings = positions.Count == 0 ? 0 : 1 + positions.Zip(positions.Skip(1), (a, b) => b - a > 1 ? 1 : 0).Sum();
                if (sittings > 1)
                {
                    var cls = ClassName(byClassDay.Key.ClassNameNormalized, bySubject.First().ClassName);
                    Add(TimetableIssueKind.SubjectTwiceInDay, "class", byClassDay.Key.ClassNameNormalized, cls, byClassDay.Key.CycleDay, null,
                        $"{cls} has {SubjectCode(ctx, bySubject.Key)} {sittings} separate times on {Day(byClassDay.Key.CycleDay)}.", bySubject, extra: bySubject.Key.ToString());
                }
            }
        }

        // ---- Planned against placed, and the unplaced list ----
        var unplaced = new List<UnplacedLoadDto>();
        foreach (var a in ctx.Assignments)
        {
            var placed = (decimal)lessons.Where(l => l.TeacherUserId == a.UserId && l.ClassNameNormalized == a.ClassNorm && l.SubjectId == a.SubjectId).GroupBy(SlotKey).Count() / weeks;
            var planned = a.PeriodsPerWeek ?? 0;
            var subject = ctx.Subjects.TryGetValue(a.SubjectId, out var sub) ? sub : (Name: "A removed subject", Code: "?", Active: false);
            unplaced.Add(new UnplacedLoadDto
            {
                AssignmentId = a.Id, TeacherUserId = a.UserId, TeacherName = TeacherName(a.UserId), ClassName = a.ClassName,
                SubjectId = a.SubjectId, SubjectName = subject.Name, SubjectCode = subject.Code, Planned = planned, Placed = placed
            });
            if (planned > 0 && placed != planned)
                Add(TimetableIssueKind.PlannedNotPlaced, "teacher", a.UserId.ToString(), TeacherName(a.UserId), null, null,
                    string.Create(CultureInfo.InvariantCulture, $"{TeacherName(a.UserId)}: {a.ClassName} {subject.Code} is planned at {planned} a week and placed at {placed:0.#}."),
                    lessons.Where(l => l.TeacherUserId == a.UserId && l.ClassNameNormalized == a.ClassNorm && l.SubjectId == a.SubjectId), extra: a.Id.ToString());
        }

        var ordered = issues.OrderBy(i => i.Severity).ThenBy(i => i.Kind).ThenBy(i => i.CycleDay ?? 0).ThenBy(i => i.ResourceName, StringComparer.OrdinalIgnoreCase).ToList();
        return new TimetableDiagnosisDto
        {
            HardCount = ordered.Count(i => i.Severity == TimetableIssueSeverity.Hard),
            SoftCount = ordered.Count(i => i.Severity == TimetableIssueSeverity.Soft),
            Issues = ordered,
            Unplaced = unplaced.OrderBy(u => u.Placed >= u.Planned).ThenBy(u => u.ClassName, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.SubjectCode).ToList()
        };
    }

    /// <summary>
    /// Periods a week the published timetable in force today places, per (teacher, normalised class, subject) — what
    /// coverage and "My teaching" compare the planned load against. Empty when nothing is published.
    /// </summary>
    public static async Task<Dictionary<(Guid UserId, string ClassNorm, Guid SubjectId), decimal>> PlacedPerWeekAsync(QMgrDbContext db, Guid branchId, CancellationToken ct = default)
    {
        var branch = await db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == branchId).Select(b => new { b.Settings, b.Timezone }).FirstOrDefaultAsync(ct);
        var result = new Dictionary<(Guid, string, Guid), decimal>();
        if (branch == null) return result;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, AppointmentScheduling.ResolveTimeZone(branch.Timezone)));
        var timetable = await db.Timetables.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.BranchId == branchId && t.Status == TimetableStatus.Published && t.EffectiveFrom <= today && t.EffectiveTo >= today)
            .OrderByDescending(t => t.PublishedAt).FirstOrDefaultAsync(ct);
        if (timetable == null) return result;

        var settings = new QMgr.Infrastructure.Services.TimetableSettingsService(db).Read(branch.Settings);
        var weeks = Math.Max(1, timetable.CycleDays / Math.Max(1, TimetableCycle.TeachingDays(settings).Count));
        var rows = await db.TimetableLessons.IgnoreQueryFilters().AsNoTracking().Where(l => l.TimetableId == timetable.Id)
            .Select(l => new { l.TeacherUserId, l.ClassNameNormalized, l.SubjectId, l.CycleDay, l.PeriodKey }).ToListAsync(ct);
        foreach (var g in rows.GroupBy(r => (r.TeacherUserId, r.ClassNameNormalized, r.SubjectId)))
            result[g.Key] = (decimal)g.Select(r => (r.CycleDay, r.PeriodKey.ToLowerInvariant())).Distinct().Count() / weeks;
        return result;
    }

    private static string Lessons(decimal n) => n == 1 ? "lesson" : "lessons";

    private static string SubjectCode(Context ctx, Guid subjectId) => ctx.Subjects.TryGetValue(subjectId, out var s) ? s.Code : "?";

    /// <summary>'A' for S5/S6, 'O' for S1–S4, '-' otherwise (a primary school, a class with no recognisable level).</summary>
    private static char LevelBand(string? level, string className)
    {
        var text = (string.IsNullOrWhiteSpace(level) ? className : level).Replace(" ", "").Replace(".", "").ToUpperInvariant();
        if (text.Length < 2 || text[0] != 'S' || !char.IsDigit(text[1])) return '-';
        if (text.Length > 2 && char.IsDigit(text[2])) return '-';
        return text[1] switch { '1' or '2' or '3' or '4' => 'O', '5' or '6' => 'A', _ => '-' };
    }

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
