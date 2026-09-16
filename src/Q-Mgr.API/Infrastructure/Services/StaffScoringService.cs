using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Turns records into a score, per person per period, under the tenant's policy. Plan §7:
///
///  - Attendance / Duty: (present + 0.5·late + recovered) / (present + late + absent) → 0–100.
///    Excused is outside the denominator (MoES: organisational factors are not a performance gap);
///    a recovered lesson offsets a missed one (the Lesson Recovery Schedule).
///  - Contribution / Recognition / Conduct: signed points summed, clamped to ±cap, mapped to 0–100
///    around 50.
///  - Observation: mean rating ÷ scale × 100 (MoES Annex 6: points scored ÷ points expected).
///  - Wellbeing: never.
///
/// Composite = Σ(score × weight) ÷ Σ(weights of parameters with evidence). A parameter with no
/// evidence drops out of the denominator rather than scoring zero. Bands from policy. Only Final
/// records count; Draft and Annulled never do. Scores are never stored except as the frozen snapshot
/// on a signed appraisal.
/// </summary>
public interface IStaffScoringService
{
    Task<StaffScoreDto> ComputeAsync(Guid organizationId, Guid branchId, Guid userId, PerformancePeriodDto period, bool includeRank, CancellationToken cancellationToken = default);

    /// <summary>Every active staff member of the branch with a composite, for reports, leaderboards and ranks.</summary>
    Task<List<StaffScoreDto>> ComputeBranchAsync(Guid organizationId, Guid branchId, PerformancePeriodDto period, CancellationToken cancellationToken = default);
}

public class StaffScoringService : IStaffScoringService
{
    private readonly QMgrDbContext _db;
    private readonly IStaffPerformancePolicyService _policy;

    public StaffScoringService(QMgrDbContext db, IStaffPerformancePolicyService policy)
    {
        _db = db;
        _policy = policy;
    }

    public async Task<StaffScoreDto> ComputeAsync(Guid organizationId, Guid branchId, Guid userId, PerformancePeriodDto period, bool includeRank, CancellationToken cancellationToken = default)
    {
        var policy = await _policy.GetAsync(organizationId, cancellationToken);
        var parameters = await LoadParametersAsync(organizationId, cancellationToken);

        var roleCode = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.Role.Code).FirstOrDefaultAsync(cancellationToken);
        var group = _policy.GroupFor(roleCode);

        var (start, end) = Bounds(period);
        var records = await _db.StaffPerformanceRecords.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.SubjectUserId == userId
                        && r.Status == StaffRecordStatus.Final
                        && r.OccurredAt >= start && r.OccurredAt < end)
            .Select(r => new RecordRow(r.SubjectUserId, r.ParameterId, r.Outcome, r.Points, r.Rating, r.OccurredAt))
            .ToListAsync(cancellationToken);

        var score = Build(userId, period, policy, parameters, group, records);

        if (includeRank)
        {
            var all = await ComputeBranchAsync(organizationId, branchId, period, cancellationToken);
            var ranked = all.Where(s => s.Composite.HasValue).OrderByDescending(s => s.Composite).ToList();
            var index = ranked.FindIndex(s => s.SubjectUserId == userId);
            score = score with { RankInBranch = index >= 0 ? index + 1 : null, RankedOutOf = ranked.Count };
        }

        return score;
    }

    public async Task<List<StaffScoreDto>> ComputeBranchAsync(Guid organizationId, Guid branchId, PerformancePeriodDto period, CancellationToken cancellationToken = default)
    {
        var policy = await _policy.GetAsync(organizationId, cancellationToken);
        var parameters = await LoadParametersAsync(organizationId, cancellationToken);
        var (start, end) = Bounds(period);

        var staff = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.IsActive
                        && (u.AssignedBranchId == branchId || u.AssignedBranchId == null)
                        && u.Role.Code != RoleCodes.SuperAdmin)
            .Select(u => new { u.Id, RoleCode = u.Role.Code })
            .ToListAsync(cancellationToken);

        var ids = staff.Select(s => s.Id).ToList();
        var records = await _db.StaffPerformanceRecords.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.BranchId == branchId
                        && ids.Contains(r.SubjectUserId)
                        && r.Status == StaffRecordStatus.Final
                        && r.OccurredAt >= start && r.OccurredAt < end)
            .Select(r => new RecordRow(r.SubjectUserId, r.ParameterId, r.Outcome, r.Points, r.Rating, r.OccurredAt))
            .ToListAsync(cancellationToken);

        var bySubject = records.ToLookup(r => r.SubjectUserId);
        return staff
            .Select(s => Build(s.Id, period, policy, parameters, _policy.GroupFor(s.RoleCode), bySubject[s.Id].ToList()))
            .ToList();
    }

    private async Task<List<PerformanceParameter>> LoadParametersAsync(Guid organizationId, CancellationToken ct)
        => await _db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.IsActive)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

    private static (DateTime Start, DateTime End) Bounds(PerformancePeriodDto period)
        => (period.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            period.End.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    private record RecordRow(Guid SubjectUserId, Guid ParameterId, DutyOutcome Outcome, int? Points, int? Rating, DateTime OccurredAt);

    private StaffScoreDto Build(Guid userId, PerformancePeriodDto period, StaffPerformancePolicyDto policy,
        List<PerformanceParameter> parameters, StaffGroup group, List<RecordRow> records)
    {
        var byParameter = records.ToLookup(r => r.ParameterId);
        var breakdown = new List<ParameterScoreDto>();
        decimal weighted = 0, weights = 0;

        foreach (var p in parameters)
        {
            if (p.Kind == ParameterKind.Wellbeing) continue;
            if (p.AppliesTo != StaffGroup.AllStaff && p.AppliesTo != group) continue;

            var rows = byParameter[p.Id].ToList();
            var item = ScoreParameter(p, rows);
            breakdown.Add(item);

            if (item.Score.HasValue && p.Weight > 0)
            {
                weighted += item.Score.Value * p.Weight;
                weights += p.Weight;
            }
        }

        decimal? composite = weights > 0 ? Math.Round(weighted / weights, 1) : null;
        var band = composite.HasValue ? _policy.BandFor(policy, composite.Value) : null;

        var recognitionIds = parameters.Where(p => p.Kind == ParameterKind.Recognition).Select(p => p.Id).ToHashSet();
        var dutyIds = parameters.Where(p => p.Kind is ParameterKind.Attendance or ParameterKind.Duty).Select(p => p.Id).ToHashSet();

        var trend = records
            .GroupBy(r => WeekStart(DateOnly.FromDateTime(r.OccurredAt)))
            .OrderBy(g => g.Key)
            .Select(g => new ScoreTrendPointDto { WeekStart = g.Key, Points = g.Sum(r => r.Points ?? 0), Records = g.Count() })
            .ToList();

        return new StaffScoreDto
        {
            SubjectUserId = userId,
            Period = period,
            Composite = composite,
            Band = band?.Rating,
            BandName = band?.Name,
            Points = records.Sum(r => r.Points ?? 0),
            RecognitionReceived = records.Count(r => recognitionIds.Contains(r.ParameterId)),
            DutiesExpected = records.Count(r => dutyIds.Contains(r.ParameterId) && r.Outcome is DutyOutcome.Present or DutyOutcome.Late or DutyOutcome.Absent or DutyOutcome.Completed or DutyOutcome.NotCompleted),
            DutiesAttended = records.Count(r => dutyIds.Contains(r.ParameterId) && r.Outcome is DutyOutcome.Present or DutyOutcome.Late or DutyOutcome.Completed),
            Breakdown = breakdown,
            Trend = trend
        };
    }

    private static ParameterScoreDto ScoreParameter(PerformanceParameter p, List<RecordRow> rows)
    {
        var present = rows.Count(r => r.Outcome is DutyOutcome.Present or DutyOutcome.Completed);
        var late = rows.Count(r => r.Outcome == DutyOutcome.Late);
        var absent = rows.Count(r => r.Outcome is DutyOutcome.Absent or DutyOutcome.NotCompleted);
        var excused = rows.Count(r => r.Outcome == DutyOutcome.Excused);
        var recovered = rows.Count(r => r.Outcome == DutyOutcome.Recovered);
        var points = rows.Sum(r => r.Points ?? 0);

        decimal? score = null;
        decimal? meanRating = null;

        switch (p.Kind)
        {
            case ParameterKind.Attendance:
            case ParameterKind.Duty:
            {
                var denominator = present + late + absent;
                if (denominator > 0)
                {
                    var numerator = Math.Min(denominator, present + 0.5m * late + recovered);
                    score = Math.Round(numerator / denominator * 100m, 1);
                }
                break;
            }
            case ParameterKind.Observation:
            {
                var rated = rows.Where(r => r.Rating.HasValue).Select(r => r.Rating!.Value).ToList();
                if (rated.Count > 0 && p.RatingScale is > 0)
                {
                    meanRating = Math.Round((decimal)rated.Average(), 2);
                    score = Math.Round(meanRating.Value / p.RatingScale.Value * 100m, 1);
                }
                break;
            }
            case ParameterKind.Contribution:
            case ParameterKind.Recognition:
            case ParameterKind.Conduct:
            {
                if (rows.Count > 0)
                {
                    var cap = p.MaxPointsPerPeriod ?? Math.Max(1, p.MaxPointsPerEntry * 10);
                    var clamped = Math.Clamp(points, -cap, cap);
                    score = Math.Round(50m + 50m * clamped / cap, 1);
                    score = Math.Clamp(score.Value, 0m, 100m);
                }
                break;
            }
        }

        return new ParameterScoreDto
        {
            ParameterId = p.Id,
            Name = p.Name,
            Kind = p.Kind,
            Color = p.Color,
            Weight = p.Weight,
            Score = score,
            EvidenceCount = rows.Count,
            Points = points,
            Expected = present + late + absent,
            Present = present,
            Late = late,
            Absent = absent,
            Excused = excused,
            Recovered = recovered,
            MeanRating = meanRating,
            RatingScale = p.RatingScale
        };
    }

    private static DateOnly WeekStart(DateOnly d)
    {
        var diff = ((int)d.DayOfWeek + 6) % 7; // Monday = 0
        return d.AddDays(-diff);
    }
}
