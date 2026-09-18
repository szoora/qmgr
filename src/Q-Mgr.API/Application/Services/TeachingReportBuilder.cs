using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// The teaching reports (duty rota plan §11): teaching load, lessons taught with the MoES Annex 4 summary and Annex 5
/// recovery schedule, duty rota and report compliance, and timetable health. One builder for the Reports page, the print
/// route, the dashboard tiles and the weekly lesson analysis email, so a figure reads the same everywhere.
///
/// <paramref name="visible"/> is the caller's staff scope (null = everyone): every row is a teacher in it, and every
/// aggregate is built from those rows only. Lesson statuses come from <see cref="StaffLessons.StatusOf"/>, never re-derived.
/// </summary>
public static class TeachingReportBuilder
{
    public const string ReplacedDescription = "No longer on the published timetable.";

    public static async Task<TeachingReportsDto> BuildAsync(QMgrDbContext db, ITimetableSettingsService settingsService, Guid organizationId, Guid branchId,
        PerformancePeriodDto period, StaffPerformancePolicyDto policy, HashSet<Guid>? visible, IReadOnlyList<string> scopedDepartments,
        bool includeTimetableHealth, TimeZoneInfo zone, DateTime nowUtc, CancellationToken ct = default)
    {
        var norms = policy.TeachingLoadNorms ?? new TeachingLoadNormsDto();
        var branchSettings = await db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync(ct);
        var vocab = StudentsController.ReadVocabularies(branchSettings);
        var levels = vocab.Classes.GroupBy(c => TimetableCycle.Normalize(c.Name)).ToDictionary(g => g.Key, g => g.First().Level);
        var departments = await StaffLookups.LoadDepartmentNamesAsync(db, organizationId);
        bool InScope(Guid id) => visible == null || visible.Contains(id);

        // ---- Teaching load ----
        var assignments = await db.ClassTeacherAssignments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.BranchId == branchId && a.EndedAt == null && a.Role == ClassTeacherRole.SubjectTeacher && a.SubjectId != null)
            .Select(a => new { a.UserId, a.ClassName, SubjectId = a.SubjectId!.Value, a.PeriodsPerWeek })
            .ToListAsync(ct);
        var placed = await TimetableChecker.PlacedPerWeekAsync(db, branchId, ct);
        var subjects = await db.Subjects.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == organizationId).ToDictionaryAsync(s => s.Id, s => new { s.Name, s.Code }, ct);
        var teacherIds = assignments.Select(a => a.UserId).Concat(placed.Keys.Select(k => k.UserId)).Distinct().Where(InScope).ToList();
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => teacherIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Username, u.DepartmentIds }).ToListAsync(ct);
        string NameOf(Guid id) => users.FirstOrDefault(u => u.Id == id) is { } u ? ($"{u.FirstName} {u.LastName}".Trim() is { Length: > 0 } n ? n : u.Username) : "A former member of staff";
        List<string> DepartmentsOf(Guid id) => (users.FirstOrDefault(u => u.Id == id)?.DepartmentIds ?? Array.Empty<Guid>()).Select(d => departments.GetValueOrDefault(d)).Where(n => n != null).Select(n => n!).ToList();
        string LevelOf(string className) => levels.TryGetValue(TimetableCycle.Normalize(className), out var l) && !string.IsNullOrWhiteSpace(l) ? l! : (SuggestLevel(className) ?? "Other");

        var load = new List<TeachingLoadRowDto>();
        foreach (var id in teacherIds)
        {
            var mine = assignments.Where(a => a.UserId == id).ToList();
            var placedMine = placed.Where(p => p.Key.UserId == id).ToList();
            var planned = mine.Sum(a => a.PeriodsPerWeek ?? 0);
            var timetabled = placedMine.Sum(p => p.Value);
            var bands = mine.Select(a => LevelBand(LevelOf(a.ClassName))).ToHashSet();
            var mixed = bands.Contains('A') && bands.Contains('O');
            var min = mixed ? norms.MinLessonsPerWeekMixedLevel : norms.MinLessonsPerWeek;
            var measure = placed.Count > 0 ? timetabled : planned;
            var subjectRows = mine.GroupBy(a => a.SubjectId).Select(g => new TeachingLoadSubjectDto
            {
                SubjectCode = subjects.GetValueOrDefault(g.Key)?.Code ?? "?", SubjectName = subjects.GetValueOrDefault(g.Key)?.Name ?? "A removed subject",
                Classes = g.Select(a => a.ClassName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList(),
                Planned = g.Sum(a => a.PeriodsPerWeek ?? 0),
                Timetabled = placedMine.Where(p => p.Key.SubjectId == g.Key).Sum(p => p.Value)
            }).OrderBy(s => s.SubjectCode).ToList();
            load.Add(new TeachingLoadRowDto
            {
                UserId = id, Name = NameOf(id), Departments = DepartmentsOf(id) is { Count: > 0 } ds ? string.Join(", ", ds) : null,
                Planned = planned, Timetabled = timetabled, MixedLevel = mixed, Minimum = min, Maximum = norms.MaxLessonsPerWeek,
                Band = measure == 0 ? LoadBand.NotTeaching : measure < min ? LoadBand.Under : measure > norms.MaxLessonsPerWeek ? LoadBand.Over : LoadBand.Within,
                BySubject = subjectRows
            });
        }
        load = load.OrderBy(r => r.Band == LoadBand.Within).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        List<LoadAggregateDto> Aggregate(IEnumerable<(string Key, string Name, Guid Teacher, int Planned, decimal Timetabled)> parts)
            => parts.GroupBy(p => p.Key).Select(g => new LoadAggregateDto
            {
                Key = g.Key, Name = g.First().Name, Teachers = g.Select(p => p.Teacher).Distinct().Count(),
                Planned = g.Sum(p => p.Planned), Timetabled = g.Sum(p => p.Timetabled),
                Under = g.Select(p => p.Teacher).Distinct().Count(t => load.First(r => r.UserId == t).Band == LoadBand.Under),
                Over = g.Select(p => p.Teacher).Distinct().Count(t => load.First(r => r.UserId == t).Band == LoadBand.Over)
            }).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var loadByDepartment = Aggregate(load.SelectMany(r => (DepartmentsOf(r.UserId) is { Count: > 0 } ds ? ds : new List<string> { "No department" })
            .Select(d => (d, d, r.UserId, r.Planned, r.Timetabled))));
        var loadBySubject = Aggregate(assignments.Where(a => InScope(a.UserId)).Select(a => (a.SubjectId.ToString(), subjects.GetValueOrDefault(a.SubjectId)?.Name ?? "?", a.UserId, a.PeriodsPerWeek ?? 0,
            placed.GetValueOrDefault((a.UserId, TimetableCycle.Normalize(a.ClassName), a.SubjectId)))));
        var loadByLevel = Aggregate(assignments.Where(a => InScope(a.UserId)).Select(a => (LevelOf(a.ClassName), LevelOf(a.ClassName), a.UserId, a.PeriodsPerWeek ?? 0,
            placed.GetValueOrDefault((a.UserId, TimetableCycle.Normalize(a.ClassName), a.SubjectId)))));

        // ---- Lessons taught ----
        var periodStart = TimeZoneInfo.ConvertTimeToUtc(period.Start.ToDateTime(TimeOnly.MinValue), zone);
        var periodEnd = new[] { TimeZoneInfo.ConvertTimeToUtc(period.End.AddDays(1).ToDateTime(TimeOnly.MinValue), zone), nowUtc }.Min();
        var lessonRows = await db.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.RecoversDutyId == null && d.ExpectedUserIds != null
                        && d.StartsAt >= periodStart && d.StartsAt < periodEnd && (d.IsActive || d.Description != ReplacedDescription))
            .ToListAsync(ct);
        lessonRows = lessonRows.Where(d => d.ExpectedUserIds!.Length > 0 && InScope(d.ExpectedUserIds[0])).ToList();
        var items = await StaffLessons.ToItemsAsync(db, lessonRows, Guid.Empty, _ => false, policy, zone, nowUtc, ct);
        items = items.Where(i => i.Status != LessonStatus.Cancelled).ToList();

        LessonCountsDto Count(string key, string name, IEnumerable<LessonItemDto> group)
        {
            var list = group.ToList();
            int taught = list.Count(i => i.Status is LessonStatus.Taught or LessonStatus.TaughtSelfReported);
            int withPermission = list.Count(i => i.Status == LessonStatus.MissedWithPermission || (i.Status == LessonStatus.RecoveryScheduled && i.Outcome == DutyOutcome.Excused));
            int without = list.Count(i => i.Status is LessonStatus.MissedWithoutPermission or LessonStatus.NotTaughtSelfReported || (i.Status == LessonStatus.RecoveryScheduled && i.Outcome != DutyOutcome.Excused));
            int recovered = list.Count(i => i.Status == LessonStatus.Recovered);
            int notRecovered = list.Count(i => i.Status == LessonStatus.NotRecovered);
            int unrecorded = list.Count(i => i.Status == LessonStatus.Unrecorded);
            var counting = taught + without + recovered + notRecovered;
            return new LessonCountsDto
            {
                Key = key, Name = name, Scheduled = list.Count, Taught = taught, MissedWithPermission = withPermission, MissedWithoutPermission = without,
                Recovered = recovered, NotRecovered = notRecovered, Unrecorded = unrecorded,
                TaughtPercent = counting == 0 ? null : Math.Round((taught + recovered) * 100m / counting, 1)
            };
        }

        var byTeacher = items.GroupBy(i => i.TeacherUserId).Select(g => Count(g.Key.ToString(), g.First().TeacherName, g)).OrderBy(c => c.TaughtPercent ?? 101).ThenBy(c => c.Name).ToList();
        var itemDepartments = items.Select(i => (Item: i, Departments: DepartmentsOfAny(i.TeacherUserId))).ToList();
        List<string> DepartmentsOfAny(Guid teacher)
        {
            var ds = users.Any(u => u.Id == teacher) ? DepartmentsOf(teacher) : new List<string>();
            return ds.Count > 0 ? ds : new List<string> { "No department" };
        }
        var byDepartment = itemDepartments.SelectMany(x => x.Departments.Select(d => (d, x.Item))).GroupBy(x => x.d).Select(g => Count(g.Key, g.Key, g.Select(x => x.Item))).OrderBy(c => c.Name).ToList();
        var bySubject = items.GroupBy(i => i.SubjectName).Select(g => Count(g.Key, g.Key, g)).OrderBy(c => c.Name).ToList();
        var byClass = items.GroupBy(i => i.ClassName).Select(g => Count(g.Key, g.Key, g)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var byLevel = items.GroupBy(i => LevelOf(i.ClassName.Split(" + ")[0])).Select(g => Count(g.Key, g.Key, g)).OrderBy(c => c.Name).ToList();
        var byWeek = items.GroupBy(i => WeekStart(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(i.StartsAt, zone))))
            .OrderBy(g => g.Key).Select(g => new WeeklyLessonsDto { WeekStart = g.Key, Counts = Count(g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g.Key.ToString("dd MMM", CultureInfo.InvariantCulture), g) }).ToList();

        var recoverySchedule = items.Where(i => i.Outcome is DutyOutcome.Absent or DutyOutcome.Excused)
            .OrderBy(i => i.StartsAt)
            .Select(i => new RecoveryScheduleRowDto
            {
                DutyId = i.DutyId, TeacherName = i.TeacherName, ClassName = i.ClassName, SubjectName = i.SubjectName, MissedAt = i.StartsAt,
                Reason = i.FlagNote, RecoveryAt = i.RecoveryStartsAt, Status = i.Status
            }).ToList();

        // ---- Duty rota and reports ----
        var rota = await db.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.BranchId == branchId && d.IsActive && d.Kind == DutyKind.Rota && d.StartsAt >= periodStart && d.StartsAt < TimeZoneInfo.ConvertTimeToUtc(period.End.AddDays(1).ToDateTime(TimeOnly.MinValue), zone))
            .Select(d => new { d.Id, d.StartsAt, d.ExpectedUserIds, d.SupervisorUserIds, d.Acknowledgements })
            .ToListAsync(ct);
        var reports = await db.StaffDutyReports.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.BranchId == branchId && r.Duty!.IsActive && r.PeriodStart >= period.Start && r.PeriodStart <= period.End)
            .Select(r => new { r.AuthorUserId, r.DutyId, r.DueAt, r.Status, r.SubmittedAt, r.ReviewedAt, r.ReviewedByUserId })
            .ToListAsync(ct);
        var rotaPeople = rota.SelectMany(d => (d.ExpectedUserIds ?? Array.Empty<Guid>()).Concat(d.SupervisorUserIds)).Concat(reports.Select(r => r.AuthorUserId)).Distinct().Where(InScope).ToList();
        var rotaNames = await StaffLookups.LoadNamesAsync(db, rotaPeople.Select(id => (Guid?)id), ct);
        var rotaRows = new List<RotaComplianceRowDto>();
        foreach (var id in rotaPeople)
        {
            var onDuty = rota.Where(d => d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(id)).ToList();
            var acks = onDuty.Select(d => StaffPerformanceMapping.ParseAcknowledgements(d.Acknowledgements).TryGetValue(id, out var at) ? (DateTime?)at : null).ToList();
            var dueReports = reports.Where(r => r.AuthorUserId == id && r.DueAt <= nowUtc).ToList();
            var reviewed = reports.Where(r => r.ReviewedByUserId == id && r.ReviewedAt != null && r.SubmittedAt != null).ToList();
            rotaRows.Add(new RotaComplianceRowDto
            {
                UserId = id, Name = rotaNames[id] is { Length: > 0 } n ? n : "A former member of staff",
                OnDutySlots = onDuty.Count, SupervisedSlots = rota.Count(d => d.SupervisorUserIds.Contains(id)),
                Acknowledged = acks.Count(a => a != null),
                AcknowledgedBeforeStart = onDuty.Zip(acks).Count(p => p.Second != null && p.Second <= p.First.StartsAt),
                ReportsDue = dueReports.Count,
                ReportsOnTime = dueReports.Count(r => r.SubmittedAt != null && r.SubmittedAt <= r.DueAt),
                ReportsLate = dueReports.Count(r => r.SubmittedAt != null && r.SubmittedAt > r.DueAt),
                ReportsOverdue = dueReports.Count(r => r.Status is DutyReportStatus.Draft or DutyReportStatus.Returned),
                ReportsReviewed = reports.Count(r => r.AuthorUserId == id && r.Status == DutyReportStatus.Reviewed),
                ReportsReturned = reports.Count(r => r.AuthorUserId == id && r.Status == DutyReportStatus.Returned),
                ReviewTurnaroundHours = reviewed.Count == 0 ? null : Math.Round((decimal)reviewed.Average(r => (r.ReviewedAt!.Value - r.SubmittedAt!.Value).TotalHours), 1)
            });
        }
        rotaRows = rotaRows.OrderByDescending(r => r.ReportsOverdue).ThenByDescending(r => r.OnDutySlots).ThenBy(r => r.Name).ToList();

        return new TeachingReportsDto
        {
            Period = period,
            ScopedToDepartments = scopedDepartments.ToList(),
            Norms = norms,
            Load = load,
            LoadByDepartment = loadByDepartment,
            LoadBySubject = loadBySubject,
            LoadByLevel = loadByLevel,
            LessonsTotal = Count("all", "All lessons", items),
            LessonsByTeacher = byTeacher,
            LessonsByDepartment = byDepartment,
            LessonsBySubject = bySubject,
            LessonsByClass = byClass,
            LessonsByLevel = byLevel,
            LessonsByWeek = byWeek,
            RecoverySchedule = recoverySchedule,
            Rota = rotaRows,
            Timetable = includeTimetableHealth ? await HealthAsync(db, settingsService, policy, branchId, zone, ct) : null
        };
    }

    /// <summary>The version in force today: its clashes now, the load still to place, room use, and the sweep's trend.</summary>
    public static async Task<TimetableHealthDto> HealthAsync(QMgrDbContext db, ITimetableSettingsService settingsService, StaffPerformancePolicyDto policy, Guid branchId, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        var timetable = await db.Timetables.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.BranchId == branchId && t.Status == TimetableStatus.Published && t.EffectiveFrom <= today && t.EffectiveTo >= today)
            .OrderByDescending(t => t.PublishedAt).FirstOrDefaultAsync(ct);
        if (timetable == null) return new TimetableHealthDto();

        var lessons = await db.TimetableLessons.IgnoreQueryFilters().AsNoTracking().Where(l => l.TimetableId == timetable.Id).ToListAsync(ct);
        var settings = await settingsService.ReadAsync(branchId, ct);
        var context = await TimetableChecker.LoadContextAsync(db, timetable, settings, policy, zone, lessons, ct);
        var diagnosis = TimetableChecker.Diagnose(timetable, lessons, context);

        var slots = Enumerable.Range(1, timetable.CycleDays).Sum(d => TimetableCycle.LessonPeriodsOf(settings, timetable.CycleDays, d).Count);
        var branchSettings = await db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync(ct);
        var rooms = StudentsController.ReadVocabularies(branchSettings).Rooms.Where(r => r.IsActive).Select(r => r.Name).ToList();
        var roomUse = rooms.Select(r =>
        {
            var norm = TimetableCycle.Normalize(r);
            var used = lessons.Where(l => l.RoomNormalized == norm).Select(l => (l.CycleDay, l.PeriodKey.ToLowerInvariant())).Distinct().Count();
            return new RoomUseDto { Room = r, PeriodsUsed = used, PeriodsAvailable = slots, Percent = slots == 0 ? 0 : Math.Round(used * 100m / slots, 1) };
        }).OrderByDescending(r => r.Percent).ToList();

        var trendRows = await db.ActivityEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Action == QMgr.Application.DTOs.ActivityActions.TimetableChecked && e.EntityId == timetable.Id)
            .OrderByDescending(e => e.OccurredAt).Take(30).Select(e => new { e.OccurredAt, e.DetailJson }).ToListAsync(ct);
        var trend = trendRows.OrderBy(e => e.OccurredAt).Select(e =>
        {
            int hard = 0, soft = 0;
            try
            {
                using var doc = JsonDocument.Parse(e.DetailJson ?? "{}");
                if (doc.RootElement.TryGetProperty("Hard", out var h)) hard = h.GetInt32();
                if (doc.RootElement.TryGetProperty("Soft", out var s)) soft = s.GetInt32();
            }
            catch (JsonException) { }
            return new ClashTrendPointDto { At = e.OccurredAt, Hard = hard, Soft = soft };
        }).ToList();

        return new TimetableHealthDto
        {
            TimetableId = timetable.Id, TimetableName = timetable.Name, Hard = diagnosis.HardCount, Soft = diagnosis.SoftCount,
            UnplacedAssignments = diagnosis.Unplaced.Count(u => u.Planned > 0 && u.Placed < u.Planned),
            UnplacedPeriods = diagnosis.Unplaced.Where(u => u.Placed < u.Planned).Sum(u => u.Planned - u.Placed),
            TopIssues = diagnosis.Issues.Take(12).ToList(),
            Rooms = roomUse,
            Trend = trend
        };
    }

    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static char LevelBand(string level)
    {
        var text = level.Replace(" ", "").Replace(".", "").ToUpperInvariant();
        if (text.Length < 2 || text[0] != 'S' || !char.IsDigit(text[1]) || (text.Length > 2 && char.IsDigit(text[2]))) return '-';
        return text[1] switch { '1' or '2' or '3' or '4' => 'O', '5' or '6' => 'A', _ => '-' };
    }

    private static string? SuggestLevel(string className)
    {
        var compact = new string(className.Where(ch => ch != '.' && ch != ' ').ToArray());
        var m = System.Text.RegularExpressions.Regex.Match(compact, @"^([A-Za-z]{1,3}\d{1,2})");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }
}
