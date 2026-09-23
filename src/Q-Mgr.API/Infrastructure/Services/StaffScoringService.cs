using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
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
///    a recovered lesson offsets a missed one (the Lesson Recovery Schedule). A record on a parameter whose
///    OffsetsParameterId points here counts as one recovered occasion too.
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
    /// <param name="knownBefore">
    /// When set, only records CREATED before this instant count: "the score as it stood then", which is
    /// what the weekly digest compares against to report movement. Status is today's, so a record annulled
    /// since is left out of both figures rather than inflating last week's.
    /// </param>
    Task<StaffScoreDto> ComputeAsync(Guid organizationId, Guid branchId, Guid userId, PerformancePeriodDto period, bool includeRank, CancellationToken cancellationToken = default, DateTime? knownBefore = null);

    /// <summary>Every active staff member of the branch with a composite, for reports, leaderboards and ranks.</summary>
    Task<List<StaffScoreDto>> ComputeBranchAsync(Guid organizationId, Guid branchId, PerformancePeriodDto period, CancellationToken cancellationToken = default);
}

public class StaffScoringService : IStaffScoringService
{
    private readonly QMgrDbContext _db;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;

    public StaffScoringService(QMgrDbContext db, IStaffPerformancePolicyService policy, IMemoryCache cache, IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _policy = policy;
        _cache = cache;
        _scopeFactory = scopeFactory;
    }

    public async Task<StaffScoreDto> ComputeAsync(Guid organizationId, Guid branchId, Guid userId, PerformancePeriodDto period, bool includeRank, CancellationToken cancellationToken = default, DateTime? knownBefore = null)
    {
        var policy = await _policy.GetAsync(organizationId, cancellationToken);
        var parameters = await LoadParametersAsync(organizationId, cancellationToken);

        // The role's StaffGroup, NOT its Code — see the note on the branch projection below. Passing the code
        // here made StaffGroups.Applies false for every group-restricted parameter, so a person's own score and
        // breakdown silently left out Lesson Attendance, Lesson Observation, Exam Supervision and the rest.
        var staffGroup = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.Role.StaffGroup).FirstOrDefaultAsync(cancellationToken);
        var group = _policy.GroupFor(staffGroup);

        var (start, end) = Bounds(period);
        var records = await _db.StaffPerformanceRecords.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.SubjectUserId == userId
                        && r.Status == StaffRecordStatus.Final
                        // RESTRICTED NEVER SCORES (found in Chrome by the e2e, 2026-09-16): a teacher's own
                        // breakdown showed Conduct: 4 records, -8 points, while they could see none — the score
                        // disclosed an investigation the record itself hides. Lower it to Confidential to count it.
                        && r.Visibility != QMgr.Domain.Enums.WelfareVisibility.Restricted
                        && r.OccurredAt >= start && r.OccurredAt < end
                        && (knownBefore == null || r.CreatedAt < knownBefore))
            .Select(r => new RecordRow(r.SubjectUserId, r.ParameterId, r.Outcome, r.Points, r.Rating, r.OccurredAt))
            .ToListAsync(cancellationToken);

        var score = Build(userId, period, policy, parameters, group, records);

        if (includeRank)
        {
            var all = await ComputeBranchCoreAsync(organizationId, branchId, period, policy, parameters, cancellationToken);
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
        return await ComputeBranchCoreAsync(organizationId, branchId, period, policy, parameters, cancellationToken);
    }

    // The role's STAFF GROUP, not its code. GroupFor takes the group — passing the code made every
    // group-restricted parameter vanish from every score (see the note on the projection below).
    private record StaffRow(Guid Id, string? StaffGroup);

    /// <summary>
    /// The whole branch's scores, shared between requests (2026-09-17). The portal needs them for every
    /// teacher's private rank and the department board, so before this every portal load read every
    /// record the branch holds for the period, twice, and the cost grew with the size of the school
    /// times the number of people opening the page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Freshness is by fingerprint, not invalidation.</b> The cache key carries everything the result
    /// depends on: the policy, the active parameters (with their last edit), the branch's staff and their
    /// roles, and the period's records as a count plus the latest <c>UpdatedAt ?? CreatedAt</c>. Logging,
    /// annulling or re-classifying a record changes that pair (the context stamps <c>UpdatedAt</c> on
    /// every modified row), so the next read misses and recomputes. There is no write path to remember,
    /// and nothing can serve a figure older than the data. The fingerprint is one aggregate over
    /// <c>idx_staff_records_branch_occurred</c>; the rows themselves are only read on a miss.
    /// <b>The one way to defeat it</b> is a raw <c>ExecuteUpdate</c> or SQL statement that changes a scored
    /// column (status, visibility, outcome, points, rating, occurred-at) without also setting
    /// <c>UpdatedAt</c>. None exists today — the only bulk update on the table sets <c>AcknowledgedAt</c>,
    /// which no score reads — and any future one must stamp <c>UpdatedAt</c>.
    /// </para>
    /// <para>
    /// A miss is computed once however many requests arrive together (the cached value is the task), in
    /// its own DI scope so no request's <c>DbContext</c> is shared, and without the caller's cancellation
    /// so one closed tab cannot fail the computation for everyone waiting on it. A failure is evicted.
    /// </para>
    /// <para>
    /// In-process, like the permission cache: the API runs as one process. A second instance would
    /// compute its own copy, never a wrong one.
    /// </para>
    /// </remarks>
    private async Task<List<StaffScoreDto>> ComputeBranchCoreAsync(Guid organizationId, Guid branchId, PerformancePeriodDto period,
        StaffPerformancePolicyDto policy, List<PerformanceParameter> parameters, CancellationToken cancellationToken)
    {
        var (start, end) = Bounds(period);

        var staff = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.IsActive
                        && (u.AssignedBranchId == branchId || u.AssignedBranchId == null)
                        && u.Role.Code != RoleCodes.SuperAdmin)
            .OrderBy(u => u.Id)
            // THE ROLE'S StaffGroup, NOT ITS Code. When StaffGroup replaced the StaffGroup enum on 2026-09-22,
            // IStaffPerformancePolicyService.GroupFor changed from taking a role CODE to taking the role's group —
            // and these two call sites went on passing the code. So a teacher's group resolved to "teacher",
            // StaffGroups.Applies("Teaching staff", "teacher") was false, and EVERY parameter with a group
            // silently vanished from EVERY staff score and breakdown: Lesson Attendance, Lesson Observation,
            // Exam Supervision, Records & Schemes of Work — the teaching half of the appraisal evidence.
            // Nothing errored; the breakdown simply came back short, and an appraisal froze the wrong score.
            // Found by e2e section 14's recovery assertions reading `undefined` where a score should have been.
            .Select(u => new StaffRow(u.Id, u.Role.StaffGroup))
            .ToListAsync(cancellationToken);

        // Every status and rung, deliberately: an annulment or a visibility change moves UpdatedAt even
        // though the row then stops counting.
        var fingerprint = await _db.StaffPerformanceRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.BranchId == branchId
                        && r.OccurredAt >= start && r.OccurredAt < end)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Latest = g.Max(r => r.UpdatedAt ?? r.CreatedAt) })
            .FirstOrDefaultAsync(cancellationToken);

        var key = string.Join('|', "staff-branch-scores", organizationId, branchId, period.Key, start.Ticks, end.Ticks,
            fingerprint?.Count ?? 0, fingerprint?.Latest.Ticks ?? 0,
            Stamp(JsonSerializer.Serialize(policy)),
            Stamp(string.Join(',', parameters.Select(p => $"{p.Id}:{(p.UpdatedAt ?? p.CreatedAt).Ticks}"))),
            Stamp(string.Join(',', staff.Select(s => $"{s.Id}:{s.StaffGroup}"))));

        var lazy = _cache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(10);
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return new Lazy<Task<List<StaffScoreDto>>>(
                () => ComputeBranchUncachedAsync(organizationId, branchId, period, policy, parameters, staff, start, end));
        })!;

        try
        {
            // A copy of the list, so a caller that filters in place cannot change what the next reader gets.
            return new List<StaffScoreDto>(await lazy.Value.WaitAsync(cancellationToken));
        }
        catch when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            _cache.Remove(key);
            throw;
        }
    }

    private static string Stamp(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 12);

    private async Task<List<StaffScoreDto>> ComputeBranchUncachedAsync(Guid organizationId, Guid branchId, PerformancePeriodDto period,
        StaffPerformancePolicyDto policy, List<PerformanceParameter> parameters, List<StaffRow> staff, DateTime start, DateTime end)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QMgrDbContext>();

        var ids = staff.Select(s => s.Id).ToList();
        var records = await db.StaffPerformanceRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.BranchId == branchId
                        && ids.Contains(r.SubjectUserId)
                        && r.Status == StaffRecordStatus.Final
                        // RESTRICTED NEVER SCORES (found in Chrome by the e2e, 2026-09-16): a teacher's own
                        // breakdown showed Conduct: 4 records, -8 points, while they could see none — the score
                        // disclosed an investigation the record itself hides. Lower it to Confidential to count it.
                        && r.Visibility != QMgr.Domain.Enums.WelfareVisibility.Restricted
                        && r.OccurredAt >= start && r.OccurredAt < end)
            .Select(r => new RecordRow(r.SubjectUserId, r.ParameterId, r.Outcome, r.Points, r.Rating, r.OccurredAt))
            .ToListAsync();

        var bySubject = records.ToLookup(r => r.SubjectUserId);
        return staff
            .Select(s => Build(s.Id, period, policy, parameters, _policy.GroupFor(s.StaffGroup), bySubject[s.Id].ToList()))
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
        List<PerformanceParameter> parameters, string? group, List<RecordRow> records)
    {
        var byParameter = records.ToLookup(r => r.ParameterId);
        var breakdown = new List<ParameterScoreDto>();
        decimal weighted = 0, weights = 0;

        foreach (var p in parameters)
        {
            if (p.Kind == ParameterKind.Wellbeing) continue;
            if (!StaffGroups.Applies(p.AppliesToGroup, group)) continue;

            var rows = byParameter[p.Id].ToList();
            // An automatic-credit parameter with automatic credit switched off can never gain evidence;
            // listing it as "no evidence yet" on every breakdown only raises a question nobody can act on.
            if (p.IsSystemSource && !policy.SystemAwardsEnabled && rows.Count == 0) continue;
            // Records on a parameter that OFFSETS this one (Lesson Recovery → Lesson Attendance) each count
            // as one recovered occasion here. Before 2026-09-17 the seeded Lesson Recovery parameter said it
            // offset a missed lesson and scoring ignored it; only a Recovered outcome logged on the attendance
            // parameter itself counted.
            var offsetting = p.Kind is ParameterKind.Attendance or ParameterKind.Duty
                ? parameters.Where(q => q.OffsetsParameterId == p.Id && q.Id != p.Id)
                    .SelectMany(q => byParameter[q.Id])
                    .Count(r => r.Outcome is not (DutyOutcome.Absent or DutyOutcome.NotCompleted or DutyOutcome.Excused))
                : 0;
            var item = ScoreParameter(p, rows, offsetting, policy);
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

    private static ParameterScoreDto ScoreParameter(PerformanceParameter p, List<RecordRow> rows, int offsettingRecovered, StaffPerformancePolicyDto policy)
    {
        var present = rows.Count(r => r.Outcome is DutyOutcome.Present or DutyOutcome.Completed);
        var late = rows.Count(r => r.Outcome == DutyOutcome.Late);
        var absent = rows.Count(r => r.Outcome is DutyOutcome.Absent or DutyOutcome.NotCompleted);
        var excused = rows.Count(r => r.Outcome == DutyOutcome.Excused);
        var recovered = rows.Count(r => r.Outcome == DutyOutcome.Recovered) + offsettingRecovered;
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
                    // What a late arrival is worth is the SCHOOL's decision, not arithmetic: some
                    // count three lates as an absence, some count late as absent outright. Clamped
                    // to 0–1 here because a late worth more than a present would score somebody up
                    // for being late. Excused stays out of the denominator and is NOT configurable —
                    // it is the MoES "organisational factors do not constitute a performance gap".
                    var lateCredit = Math.Clamp(policy.LateCreditFraction, 0m, 1m);
                    var numerator = Math.Min(denominator, present + lateCredit * late + recovered);
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
                    // Both dials are the school's: how many entries a term this is expected to
                    // attract when no explicit cap is set, and where a parameter with evidence sits
                    // before that evidence pushes it either way.
                    var perPeriod = Math.Max(1, policy.DefaultEntriesPerPeriod);
                    var neutral = Math.Clamp(policy.NeutralScore, 0m, 100m);
                    var cap = p.MaxPointsPerPeriod ?? Math.Max(1, p.MaxPointsPerEntry * perPeriod);
                    var clamped = Math.Clamp(points, -cap, cap);
                    // Asymmetric on purpose: the distance to 100 above neutral and to 0 below it, so
                    // a school that moves neutral does not get a scale that runs past either end.
                    var span = clamped >= 0 ? 100m - neutral : neutral;
                    score = Math.Round(neutral + span * clamped / cap, 1);
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
            EvidenceCount = rows.Count + offsettingRecovered,
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
