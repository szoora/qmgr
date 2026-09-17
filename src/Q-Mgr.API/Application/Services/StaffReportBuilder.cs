using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// Builds <see cref="StaffReportsDto"/> for a branch and a period, optionally restricted to a set
/// of visible staff. Shared by StaffReportsController (the page) and
/// StaffPerformanceJobs.SendMonthlySummaryAsync (the administrator email), so the figures in the
/// email are the figures on the page. Uses IgnoreQueryFilters plus the explicit organization
/// throughout, so it answers the same in a request and in a Hangfire worker.
/// </summary>
public static class StaffReportBuilder
{
    public static async Task<StaffReportsDto> BuildAsync(
        QMgrDbContext db,
        IStaffScoringService scoring,
        IStaffPerformancePolicyService policySvc,
        Guid organizationId,
        Guid branchId,
        PerformancePeriodDto period,
        StaffPerformancePolicyDto policy,
        HashSet<Guid>? visible,
        CancellationToken ct = default,
        bool includeConfidentialDetail = false)
    {
        var start = period.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = period.End.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var now = DateTime.UtcNow;

        var staff = await StaffLookups.BranchStaff(db, organizationId, branchId).ToListAsync(ct);
        if (visible != null) staff = staff.Where(s => visible.Contains(s.Id)).ToList();
        var staffIds = staff.Select(s => s.Id).ToHashSet();

        var scores = (await scoring.ComputeBranchAsync(organizationId, branchId, period, ct))
            .Where(s => staffIds.Contains(s.SubjectUserId))
            .ToList();
        var scoreByUser = scores.ToDictionary(s => s.SubjectUserId);

        var parameters = await db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);
        var parameterById = parameters.ToDictionary(x => x.Id);

        var idList = staffIds.ToList();
        var records = await db.StaffPerformanceRecords.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.BranchId == branchId
                        && r.Status == StaffRecordStatus.Final
                        // RESTRICTED NEVER SCORES (found in Chrome by the e2e, 2026-09-16): a teacher's own
                        // breakdown showed Conduct: 4 records, -8 points, while they could see none — the score
                        // disclosed an investigation the record itself hides. Lower it to Confidential to count it.
                        && r.Visibility != QMgr.Domain.Enums.WelfareVisibility.Restricted
                        && r.OccurredAt >= start && r.OccurredAt < end
                        && idList.Contains(r.SubjectUserId))
            .Select(r => new { r.SubjectUserId, r.ParameterId, r.LoggedByUserId, r.Points, r.Rating, r.OccurredAt, r.DutyId, r.Visibility })
            .ToListAsync(ct);

        var duties = await db.StaffDuties.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.BranchId == branchId && d.IsActive
                        && d.StartsAt >= start && d.StartsAt < end)
            .Select(d => new { d.EndsAt, d.RegisterClosedAt })
            .ToListAsync(ct);

        var appraisals = await db.StaffAppraisals.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.BranchId == branchId && a.PeriodKey == period.Key && a.IsActive
                        && idList.Contains(a.SubjectUserId))
            .Select(a => a.Stage)
            .ToListAsync(ct);

        var departmentNames = await StaffLookups.LoadDepartmentNamesAsync(db, organizationId, ct);
        var names = await StaffLookups.LoadNamesAsync(db, records.Select(r => (Guid?)r.LoggedByUserId).Distinct(), ct);

        // ---- By department: a person in two departments counts in both; nobody is lost to "no department".
        var byDepartment = new List<DepartmentScoreDto>();
        var deptGroups = staff
            .SelectMany(u => (u.DepartmentIds is { Length: > 0 } ids ? ids.Select(id => (Guid?)id) : new Guid?[] { null }).Select(id => (DeptId: id, User: u)))
            .GroupBy(x => x.DeptId)
            .OrderBy(g => g.Key == null ? 1 : 0).ThenBy(g => g.Key.HasValue && departmentNames.TryGetValue(g.Key.Value, out var n) ? n : "");
        foreach (var g in deptGroups)
        {
            var members = g.Select(x => x.User.Id).ToList();
            var memberScores = members.Select(id => scoreByUser.GetValueOrDefault(id)).Where(s => s != null).Select(s => s!).ToList();
            byDepartment.Add(new DepartmentScoreDto
            {
                DepartmentId = g.Key,
                Name = g.Key.HasValue ? departmentNames.GetValueOrDefault(g.Key.Value, "Unknown department") : "No department",
                StaffCount = members.Count,
                AverageComposite = Average(memberScores.Select(s => s.Composite)),
                AttendanceRate = AttendanceRate(memberScores),
                RecognitionCount = memberScores.Sum(s => s.RecognitionReceived)
            });
        }

        // ---- By parameter
        var byParameter = parameters.Where(x => x.Kind != ParameterKind.Wellbeing).Select(x => new ParameterAggregateDto
        {
            ParameterId = x.Id,
            Name = x.Name,
            Kind = x.Kind,
            Color = x.Color,
            RecordCount = records.Count(r => r.ParameterId == x.Id),
            AverageScore = Average(scores.SelectMany(s => s.Breakdown).Where(b => b.ParameterId == x.Id).Select(b => b.Score))
        }).ToList();

        // ---- Bands
        var bands = policy.Bands.OrderByDescending(b => b.Rating).Select(b => new BandCountDto
        {
            Rating = b.Rating,
            Name = b.Name,
            Count = scores.Count(s => s.Band == b.Rating)
        }).ToList();

        // ---- Trend by week
        var trend = records
            .GroupBy(r => WeekStart(DateOnly.FromDateTime(r.OccurredAt)))
            .OrderBy(g => g.Key)
            .Select(g => new ScoreTrendPointDto { WeekStart = g.Key, Points = g.Sum(r => r.Points ?? 0), Records = g.Count() })
            .ToList();

        // ---- Observer dispersion: each observer's mean rating against the branch mean
        var observationIds = parameters.Where(x => x.Kind == ParameterKind.Observation).Select(x => x.Id).ToHashSet();
        var rated = records.Where(r => observationIds.Contains(r.ParameterId) && r.Rating.HasValue).ToList();
        var schoolMean = rated.Count > 0 ? Math.Round((decimal)rated.Average(r => r.Rating!.Value), 2) : 0m;
        var dispersion = rated.GroupBy(r => r.LoggedByUserId)
            .Select(g => new ObserverStatDto
            {
                ObserverUserId = g.Key,
                Name = names[g.Key] is { Length: > 0 } n ? n : "Unknown",
                Observations = g.Count(),
                MeanRating = Math.Round((decimal)g.Average(r => r.Rating!.Value), 2),
                SchoolMean = schoolMean
            })
            .OrderByDescending(o => o.Observations)
            .ToList();

        // ---- Observation pairs (plan §6.3: "a second observer on the same lesson is a second record linked
        //      by DutyId; the reports show the pair"). Paired by duty when the observers linked one, otherwise
        //      by person, parameter and day. Only for a reader who holds the confidential rung: a pair names
        //      the person observed and both ratings, and observations default to Confidential.
        var pairs = new List<ObservationPairDto>();
        if (includeConfidentialDetail)
        {
            var dutyTitles = await db.StaffDuties.AsNoTracking()
                .Where(d => d.OrganizationId == organizationId && d.BranchId == branchId && d.StartsAt >= start.AddDays(-1) && d.StartsAt < end)
                .Select(d => new { d.Id, d.Title })
                .ToDictionaryAsync(d => d.Id, d => d.Title, ct);
            var subjectNames = staff.ToDictionary(s => s.Id, StaffPerformanceMapping.FullName);
            pairs = rated
                .GroupBy(r => r.DutyId.HasValue
                    ? $"{r.SubjectUserId}|duty|{r.DutyId}"
                    : $"{r.SubjectUserId}|{r.ParameterId}|{DateOnly.FromDateTime(r.OccurredAt):yyyy-MM-dd}")
                .Where(g => g.Select(r => r.LoggedByUserId).Distinct().Count() >= 2)
                .Select(g =>
                {
                    var first = g.First();
                    var parameter = parameterById.GetValueOrDefault(first.ParameterId);
                    var ratings = g.GroupBy(r => r.LoggedByUserId)
                        .Select(o => new ObserverRatingDto
                        {
                            ObserverUserId = o.Key,
                            ObserverName = names[o.Key] is { Length: > 0 } on ? on : "Unknown",
                            Rating = o.OrderByDescending(r => r.OccurredAt).First().Rating!.Value
                        })
                        .OrderBy(o => o.ObserverName)
                        .ToList();
                    return new ObservationPairDto
                    {
                        SubjectUserId = first.SubjectUserId,
                        SubjectName = subjectNames.GetValueOrDefault(first.SubjectUserId, "Unknown"),
                        ParameterName = parameter?.Name ?? string.Empty,
                        RatingScale = parameter?.RatingScale,
                        ObservedOn = DateOnly.FromDateTime(g.Min(r => r.OccurredAt)),
                        DutyTitle = first.DutyId is { } d ? dutyTitles.GetValueOrDefault(d) : null,
                        Ratings = ratings,
                        Spread = ratings.Max(x => x.Rating) - ratings.Min(x => x.Rating)
                    };
                })
                .OrderByDescending(p => p.Spread).ThenByDescending(p => p.ObservedOn)
                .ToList();
        }

        // ---- Who logs what
        var whoLogs = records.GroupBy(r => r.LoggedByUserId)
            .Select(g => new LoggerCountDto
            {
                UserId = g.Key,
                Name = names[g.Key] is { Length: > 0 } n ? n : "Unknown",
                Count = g.Count(),
                ByKind = g.GroupBy(r => parameterById.TryGetValue(r.ParameterId, out var px) ? px.Kind.ToString() : "Unknown")
                    .ToDictionary(k => k.Key, k => k.Count())
            })
            .OrderByDescending(l => l.Count)
            .ToList();

        // ---- Leaderboard: individual ranks only when the tenant has switched the public board on.
        var leaderboard = new List<LeaderboardRowDto>();
        if (policy.LeaderboardMode == LeaderboardMode.Public)
        {
            var rank = 0;
            leaderboard = scores.Where(s => s.Composite.HasValue)
                .OrderByDescending(s => s.Composite)
                .Take(Math.Max(1, policy.LeaderboardTopN))
                .Select(s =>
                {
                    var u = staff.First(x => x.Id == s.SubjectUserId);
                    return new LeaderboardRowDto
                    {
                        Rank = ++rank,
                        UserId = s.SubjectUserId,
                        Name = StaffPerformanceMapping.FullName(u),
                        DepartmentName = StaffLookups.DepartmentNames(u.DepartmentIds, departmentNames),
                        Composite = s.Composite!.Value,
                        Band = s.Band ?? 0,
                        BandName = s.BandName ?? string.Empty
                    };
                })
                .ToList();
        }

        // ---- Coverage: the shared computation, restricted to visible staff for a scoped caller.
        var coverage = await StaffCoverageBuilder.BuildAsync(db, organizationId, branchId, period, policySvc, ct);
        if (visible != null)
        {
            coverage = coverage with
            {
                StaffWithoutDepartment = coverage.StaffWithoutDepartment.Where(s => visible.Contains(s.UserId)).ToList(),
                StaffWithoutLineManager = coverage.StaffWithoutLineManager.Where(s => visible.Contains(s.UserId)).ToList(),
                StaffWithoutAppraiserThisPeriod = coverage.StaffWithoutAppraiserThisPeriod.Where(s => visible.Contains(s.UserId)).ToList(),
                StaffWithoutObservationThisPeriod = coverage.StaffWithoutObservationThisPeriod.Where(s => visible.Contains(s.UserId)).ToList()
            };
        }

        var recognitionIds = parameters.Where(x => x.Kind == ParameterKind.Recognition).Select(x => x.Id).ToHashSet();

        return new StaffReportsDto
        {
            Period = period,
            StaffCount = staff.Count,
            RecordCount = records.Count,
            RecognitionCount = records.Count(r => recognitionIds.Contains(r.ParameterId)),
            DutyCount = duties.Count,
            RegistersNotTaken = duties.Count(d => d.EndsAt < now && d.RegisterClosedAt == null),
            AppraisalsByStage = Enum.GetValues<AppraisalStage>().ToDictionary(s => s.ToString(), s => appraisals.Count(a => a == s)),
            ByDepartment = byDepartment,
            ByParameter = byParameter,
            BandDistribution = bands,
            TrendByWeek = trend,
            ObserverDispersion = dispersion,
            ObservationPairs = pairs,
            WhoLogsWhat = whoLogs,
            Leaderboard = leaderboard,
            LeaderboardMode = policy.LeaderboardMode,
            Coverage = coverage
        };
    }

    /// <summary>
    /// What the PORTAL shows everyone, by the tenant's leaderboard mode (plan §1.5, decision 2): Private —
    /// nothing beyond the person's own position; Department — department averages, no names; Public — the
    /// department averages and a top-N board of names. Before 2026-09-17 the top-N board existed only
    /// inside the reports page (staff.reports.view), so "shown to everyone" reached nobody, and the
    /// Department mode had no effect at all.
    /// </summary>
    public static async Task<(List<DepartmentScoreDto> Departments, List<LeaderboardRowDto> Leaderboard)> BuildPortalBoardsAsync(
        QMgrDbContext db, IStaffScoringService scoring, Guid organizationId, Guid branchId,
        PerformancePeriodDto period, StaffPerformancePolicyDto policy, CancellationToken ct = default)
    {
        if (policy.LeaderboardMode == LeaderboardMode.Private) return (new(), new());

        var staff = await StaffLookups.BranchStaff(db, organizationId, branchId).ToListAsync(ct);
        var staffById = staff.ToDictionary(s => s.Id);
        var scores = (await scoring.ComputeBranchAsync(organizationId, branchId, period, ct))
            .Where(s => staffById.ContainsKey(s.SubjectUserId))
            .ToList();
        var departmentNames = await StaffLookups.LoadDepartmentNamesAsync(db, organizationId, ct);

        var departments = scores
            .SelectMany(s => (staffById[s.SubjectUserId].DepartmentIds is { Length: > 0 } ids ? ids : Array.Empty<Guid>()).Select(id => (DeptId: id, Score: s)))
            .GroupBy(x => x.DeptId)
            .Select(g => new DepartmentScoreDto
            {
                DepartmentId = g.Key,
                Name = departmentNames.GetValueOrDefault(g.Key, "Unknown department"),
                StaffCount = g.Count(),
                AverageComposite = Average(g.Select(x => x.Score.Composite)),
                AttendanceRate = AttendanceRate(g.Select(x => x.Score)),
                RecognitionCount = g.Sum(x => x.Score.RecognitionReceived)
            })
            // A department of one or two is a person, not a team: an "average" there is somebody's score.
            .Where(d => d.StaffCount >= 3 && d.AverageComposite.HasValue)
            .OrderByDescending(d => d.AverageComposite)
            .ToList();

        var leaderboard = new List<LeaderboardRowDto>();
        if (policy.LeaderboardMode == LeaderboardMode.Public)
        {
            var rank = 0;
            leaderboard = scores.Where(s => s.Composite.HasValue)
                .OrderByDescending(s => s.Composite)
                .Take(Math.Max(1, policy.LeaderboardTopN))
                .Select(s =>
                {
                    var u = staffById[s.SubjectUserId];
                    return new LeaderboardRowDto
                    {
                        Rank = ++rank,
                        UserId = s.SubjectUserId,
                        Name = StaffPerformanceMapping.FullName(u),
                        DepartmentName = StaffLookups.DepartmentNames(u.DepartmentIds, departmentNames),
                        Composite = s.Composite!.Value,
                        Band = s.Band ?? 0,
                        BandName = s.BandName ?? string.Empty
                    };
                })
                .ToList();
        }

        return (departments, leaderboard);
    }

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : Math.Round(list.Average(), 1);
    }

    /// <summary>(present + 0.5·late + recovered) ÷ expected over the Attendance/Duty parameters of the given scores, as a percentage.</summary>
    private static decimal? AttendanceRate(IEnumerable<StaffScoreDto> scores)
    {
        decimal numerator = 0, denominator = 0;
        foreach (var b in scores.SelectMany(s => s.Breakdown).Where(b => b.Kind is ParameterKind.Attendance or ParameterKind.Duty))
        {
            denominator += b.Expected;
            numerator += Math.Min(b.Expected, b.Present + 0.5m * b.Late + b.Recovered);
        }
        return denominator == 0 ? null : Math.Round(numerator / denominator * 100m, 1);
    }

    private static DateOnly WeekStart(DateOnly d)
    {
        var diff = ((int)d.DayOfWeek + 6) % 7;
        return d.AddDays(-diff);
    }
}
