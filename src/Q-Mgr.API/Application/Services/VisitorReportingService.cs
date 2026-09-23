using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// What a report covers: one branch, or a whole organization.
///
/// TimeZoneId is carried on the scope rather than looked up per calculation because every day and
/// hour bucket in this file depends on it, and a report that silently mixed two clocks would be
/// wrong in a way nobody would spot on screen.
/// </summary>
public sealed record VisitorReportScope(Guid OrganizationId, Guid? BranchId, string Name, string TimeZoneId);

public interface IVisitorReportingService
{
    Task<VisitorReportDtoV2> BuildReportAsync(VisitorReportScope scope, VisitorReportFilter filter, CancellationToken ct = default);
    Task<VisitorExceptionsDto> BuildExceptionsAsync(VisitorReportScope scope, VisitorReportFilter filter, VisitorReportingSettingsDto settings, CancellationToken ct = default);
    Task<VisitorComplianceDto> BuildComplianceAsync(VisitorReportScope scope, VisitorReportFilter filter, bool consentRequired, VisitorRetentionSettingsDto retention, RetentionEvidenceDto evidence, CancellationToken ct = default);
    Task<string> BuildLogCsvAsync(VisitorReportScope scope, VisitorReportFilter filter, CancellationToken ct = default);

    /// <summary>Resolves the report's date range, defaulting to the trailing 7 days.</summary>
    (DateOnly From, DateOnly To, DateTime StartUtc, DateTime EndUtcExclusive) ResolveRange(VisitorReportFilter filter, string timeZoneId);
}

public sealed class VisitorReportingService : IVisitorReportingService
{
    private readonly QMgrDbContext _context;
    private readonly ILogger<VisitorReportingService> _logger;

    /// <summary>Mirrors VisitorsController.InductionValidityDays — the same 365-day window.</summary>
    private const int InductionValidityDays = 365;

    private const int TopN = 10;
    private const int MaxDetailRows = 500;

    public VisitorReportingService(QMgrDbContext context, ILogger<VisitorReportingService> logger)
    {
        _context = context;
        _logger = logger;
    }

    // -----------------------------------------------------------------------------------------
    // Time
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The branch's clock, or UTC if the configured zone id is not one this host knows.
    ///
    /// Every timestamp in this database is UTC. Bucketing report figures on the raw UTC value put a
    /// 21:00 check-in in Kampala (UTC+3) at midnight — the WRONG HOUR and, past 21:00, the wrong
    /// DAY, which silently moved Saturday visiting-day traffic onto Sunday. Everything below that
    /// produces a day or an hour goes through here first.
    /// </summary>
    private TimeZoneInfo ResolveZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Falling back to UTC is wrong-but-consistent; failing the whole report over a bad
            // timezone string would take out the one screen someone is trying to read.
            _logger.LogWarning("Unknown branch timezone '{TimeZoneId}' — visitor report buckets fall back to UTC", timeZoneId);
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTime ToLocal(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    /// <summary>
    /// Converts a local calendar day boundary back to the UTC instant to compare against stored
    /// timestamps. Npgsql rejects a Kind=Unspecified DateTime against "timestamp with time zone",
    /// so the result is always stamped Utc explicitly.
    /// </summary>
    private static DateTime LocalDayStartToUtc(DateOnly day, TimeZoneInfo zone)
    {
        var localMidnight = day.ToDateTime(TimeOnly.MinValue);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified), zone), DateTimeKind.Utc);
    }

    public (DateOnly From, DateOnly To, DateTime StartUtc, DateTime EndUtcExclusive) ResolveRange(VisitorReportFilter filter, string timeZoneId)
    {
        var zone = ResolveZone(timeZoneId);
        var todayLocal = DateOnly.FromDateTime(ToLocal(DateTime.UtcNow, zone));

        var to = filter.To ?? todayLocal;
        var from = filter.From ?? to.AddDays(-6);
        if (from > to) (from, to) = (to, from);

        return (from, to, LocalDayStartToUtc(from, zone), LocalDayStartToUtc(to.AddDays(1), zone));
    }

    // -----------------------------------------------------------------------------------------
    // Querying
    // -----------------------------------------------------------------------------------------

    private IQueryable<Visitor> ScopedVisits(VisitorReportScope scope) =>
        scope.BranchId.HasValue
            ? _context.Visitors.Where(v => v.BranchId == scope.BranchId.Value && v.DeletedAt == null)
            : _context.Visitors.Where(v => v.OrganizationId == scope.OrganizationId && v.DeletedAt == null);

    /// <summary>
    /// Applies the shared filter. Deliberately the ONLY place filters are translated to SQL, so the
    /// summary on screen and the CSV underneath it can never be answering different questions —
    /// which is exactly what happened when the report and its export took separate parameters.
    /// </summary>
    private static IQueryable<Visitor> ApplyFilter(IQueryable<Visitor> query, VisitorReportFilter filter)
    {
        if (filter.VisitorType.HasValue) query = query.Where(v => v.VisitorType == filter.VisitorType.Value);
        if (filter.Status.HasValue) query = query.Where(v => v.Status == filter.Status.Value);
        if (filter.RosterOnly) query = query.Where(v => v.StudentId != null);
        if (filter.WatchlistOnly) query = query.Where(v => v.VisitorProfile!.IsWatchlisted);

        if (!string.IsNullOrWhiteSpace(filter.HostName))
        {
            var host = filter.HostName.Trim().ToLower();
            query = query.Where(v => v.HostName.ToLower().Contains(host));
        }

        if (!string.IsNullOrWhiteSpace(filter.Company))
        {
            var company = filter.Company.Trim().ToLower();
            query = query.Where(v => v.VisitorProfile!.Company != null && v.VisitorProfile.Company.ToLower().Contains(company));
        }

        // Came in OR left by this gate (plan §10). A visit stores the gate's name as the list held it that day, and the
        // filter is picked from that same list, so a case-insensitive match is the whole comparison.
        if (!string.IsNullOrWhiteSpace(filter.Gate))
        {
            var gate = filter.Gate.Trim().ToLower();
            query = query.Where(v => (v.EntryGate != null && v.EntryGate.ToLower() == gate) || (v.ExitGate != null && v.ExitGate.ToLower() == gate));
        }

        return query;
    }

    /// <summary>The flattened row shape every aggregate below is computed from — one query, not ten.</summary>
    private sealed record Row(
        Guid VisitorId, Guid ProfileId, string BadgeCode, string FullName, string? Phone, string? Email,
        string? Company, bool IsWatchlisted, string Purpose, string HostName, Guid? StudentId, string? StudentName,
        VisitorStatus Status, VisitorType VisitorType,
        DateTime CreatedAt, DateTime? CheckedInAt, DateTime? CheckedOutAt, DateTime? ScheduledAt,
        DateTime? ConsentGivenAt, DateTime? InductionCompletedAt, string? InductionNotes,
        string? WatchlistOverrideReason, string? WatchlistReason,
        string? EntryGate, string? ExitGate, Guid? CheckedInByUserId);

    private async Task<List<Row>> LoadRowsAsync(VisitorReportScope scope, DateTime startUtc, DateTime endUtc, VisitorReportFilter filter, CancellationToken ct)
    {
        var query = ApplyFilter(ScopedVisits(scope), filter)
            .Where(v => v.CreatedAt >= startUtc && v.CreatedAt < endUtc);

        return await query
            .Select(v => new Row(
                v.Id, v.VisitorProfileId, v.BadgeCode, v.VisitorProfile!.FullName, v.VisitorProfile.Phone,
                v.VisitorProfile.Email, v.VisitorProfile.Company, v.VisitorProfile.IsWatchlisted,
                v.Purpose, v.HostName, v.StudentId, v.StudentName,
                v.Status, v.VisitorType,
                v.CreatedAt, v.CheckedInAt, v.CheckedOutAt, v.ScheduledAt,
                v.ConsentGivenAt, v.VisitorProfile.InductionCompletedAt, v.VisitorProfile.InductionNotes,
                v.WatchlistOverrideReason, v.VisitorProfile.WatchlistReason,
                v.EntryGate, v.ExitGate, v.CheckedInByUserId))
            .ToListAsync(ct);
    }

    // -----------------------------------------------------------------------------------------
    // The report
    // -----------------------------------------------------------------------------------------

    public async Task<VisitorReportDtoV2> BuildReportAsync(VisitorReportScope scope, VisitorReportFilter filter, CancellationToken ct = default)
    {
        var zone = ResolveZone(scope.TimeZoneId);
        var (from, to, startUtc, endUtc) = ResolveRange(filter, scope.TimeZoneId);
        var rows = await LoadRowsAsync(scope, startUtc, endUtc, filter, ct);

        var checkedIn = rows.Where(r => r.CheckedInAt.HasValue).ToList();
        var withDwell = checkedIn.Where(r => r.CheckedOutAt.HasValue).ToList();

        return new VisitorReportDtoV2
        {
            From = from,
            To = to,
            TimeZoneId = scope.TimeZoneId,
            ScopeName = scope.Name,
            FilterDescription = filter.Describe(),

            TotalVisits = rows.Count,
            UniqueVisitors = rows.Select(r => r.ProfileId).Distinct().Count(),
            WatchlistIncidents = rows.Count(r => r.IsWatchlisted),
            RosterCheckIns = rows.Count(r => r.StudentId != null),

            AvgDwellMinutes = withDwell.Count > 0
                ? Math.Round(withDwell.Average(r => (r.CheckedOutAt!.Value - r.CheckedInAt!.Value).TotalMinutes), 1)
                : 0,
            DwellDenominator = withDwell.Count,

            ConsentCompliancePercent = checkedIn.Count > 0
                ? Math.Round(100.0 * checkedIn.Count(r => r.ConsentGivenAt.HasValue) / checkedIn.Count, 1)
                : 0,
            ConsentDenominator = checkedIn.Count,

            VisitsByDay = BuildVisitsByDay(rows, zone, from, to),
            VisitsByHour = BuildVisitsByHour(rows, zone),
            VisitsByDayHour = BuildDayHourGrid(rows, zone),
            TopHosts = BuildTopHosts(rows),
            ByVisitorType = BuildByVisitorType(rows),
            ByCompany = BuildByCompany(rows),
            DwellDistribution = BuildDwellDistribution(withDwell),
            FrequentVisitors = BuildFrequentVisitors(rows, 3),
            PreRegistration = BuildPreRegistrationOutcome(rows),
            Audience = await BuildAudienceAsync(scope, rows, startUtc, ct),
            Branches = scope.BranchId.HasValue ? new() : await BuildBranchComparisonAsync(scope, startUtc, endUtc, filter, ct),
            Gates = BuildGates(rows, zone)
        };
    }

    /// <summary>
    /// Visitors by gate (plan §10): who came in by each, who left by each, who is still inside by the gate they came
    /// in, and arrivals by local hour per gate — the question a school asks before its next visiting day ("how many
    /// people do we need on the lower gate at 10?"). Visits that recorded no gate are one "Not recorded" row so the
    /// columns still add up to the report's totals; when no visit in range carries a gate at all, the list is empty
    /// rather than a single row that says nothing.
    /// </summary>
    private static List<VisitorGateCountDto> BuildGates(List<Row> rows, TimeZoneInfo zone)
    {
        if (!rows.Any(r => r.EntryGate != null || r.ExitGate != null)) return new();

        static string Label(string? gate) => string.IsNullOrWhiteSpace(gate) ? VisitorReportDtoV2.NoGateLabel : gate;

        var arrived = rows.Where(r => r.CheckedInAt.HasValue).ToList();
        var left = rows.Where(r => r.CheckedOutAt.HasValue).ToList();
        var names = arrived.Select(r => Label(r.EntryGate))
            .Concat(left.Select(r => Label(r.ExitGate)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names
            .Select(name =>
            {
                var entries = arrived.Where(r => string.Equals(Label(r.EntryGate), name, StringComparison.OrdinalIgnoreCase)).ToList();
                var byHour = new int[24];
                foreach (var r in entries) byHour[ToLocal(r.CheckedInAt!.Value, zone).Hour]++;
                return new VisitorGateCountDto
                {
                    Gate = name,
                    Entries = entries.Count,
                    Exits = left.Count(r => string.Equals(Label(r.ExitGate), name, StringComparison.OrdinalIgnoreCase)),
                    OnSiteNow = entries.Count(r => r.Status == VisitorStatus.CheckedIn),
                    ArrivalsByHour = byHour
                };
            })
            .OrderBy(g => g.Gate == VisitorReportDtoV2.NoGateLabel)
            .ThenByDescending(g => g.Entries)
            .ThenBy(g => g.Gate, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every day in the range, including the empty ones. A line chart that skips a zero-visit
    /// Sunday draws a straight line through it and quietly reports traffic that did not happen.
    /// </summary>
    private static List<DayCountDto> BuildVisitsByDay(List<Row> rows, TimeZoneInfo zone, DateOnly from, DateOnly to)
    {
        var counts = rows
            .GroupBy(r => DateOnly.FromDateTime(ToLocal(r.CreatedAt, zone)))
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<DayCountDto>();
        for (var day = from; day <= to; day = day.AddDays(1))
            result.Add(new DayCountDto { Day = day, Count = counts.GetValueOrDefault(day) });
        return result;
    }

    private static List<HourCountDto> BuildVisitsByHour(List<Row> rows, TimeZoneInfo zone)
    {
        var byHour = rows.GroupBy(r => ToLocal(r.CreatedAt, zone).Hour).ToDictionary(g => g.Key, g => g.Count());
        return Enumerable.Range(0, 24)
            .Select(h => new HourCountDto { Hour = h, Count = byHour.GetValueOrDefault(h) })
            .ToList();
    }

    /// <summary>
    /// The 7 × 24 grid. Only non-empty cells are returned — a full grid is 168 rows of mostly
    /// zeroes on the wire, and the client fills the gaps when it draws.
    /// </summary>
    private static List<DayHourCountDto> BuildDayHourGrid(List<Row> rows, TimeZoneInfo zone) =>
        rows
            .Select(r => ToLocal(r.CreatedAt, zone))
            .GroupBy(local => ((int)local.DayOfWeek, local.Hour))
            .Select(g => new DayHourCountDto { DayOfWeek = g.Key.Item1, Hour = g.Key.Hour, Count = g.Count() })
            .OrderBy(c => c.DayOfWeek).ThenBy(c => c.Hour)
            .ToList();

    private static List<HostVisitCountDto> BuildTopHosts(List<Row> rows) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.HostName))
            .GroupBy(r => r.HostName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new HostVisitCountDto { HostName = g.Key, Count = g.Count() })
            .OrderByDescending(h => h.Count).ThenBy(h => h.HostName)
            .Take(TopN).ToList();

    private static List<LabelCountDto> BuildByVisitorType(List<Row> rows) =>
        Enum.GetValues<VisitorType>()
            .Select(t => new LabelCountDto { Label = t.ToString(), Count = rows.Count(r => r.VisitorType == t) })
            .Where(l => l.Count > 0)
            .OrderByDescending(l => l.Count).ToList();

    private static List<LabelCountDto> BuildByCompany(List<Row> rows) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.Company))
            .GroupBy(r => r.Company!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new LabelCountDto { Label = g.Key, Count = g.Count() })
            .OrderByDescending(l => l.Count).ThenBy(l => l.Label)
            .Take(TopN).ToList();

    /// <summary>
    /// Fixed buckets rather than quantiles: a front-desk supervisor reads "11 visits ran over two
    /// hours" instantly and reads "p90 = 137 minutes" not at all.
    /// </summary>
    private static List<DwellBucketDto> BuildDwellDistribution(List<Row> withDwell)
    {
        var edges = new (string Label, int Min, int? Max)[]
        {
            ("Under 15 min", 0, 15),
            ("15–30 min", 15, 30),
            ("30–60 min", 30, 60),
            ("1–2 hours", 60, 120),
            ("2–4 hours", 120, 240),
            ("Over 4 hours", 240, null)
        };

        var minutes = withDwell
            .Select(r => (r.CheckedOutAt!.Value - r.CheckedInAt!.Value).TotalMinutes)
            .Where(m => m >= 0) // a negative dwell means clock skew or a corrected timestamp; not a bucket
            .ToList();

        return edges.Select(e => new DwellBucketDto
        {
            Label = e.Label,
            MinMinutes = e.Min,
            MaxMinutes = e.Max,
            Count = minutes.Count(m => m >= e.Min && (e.Max == null || m < e.Max.Value))
        }).ToList();
    }

    private static List<FrequentVisitorDto> BuildFrequentVisitors(List<Row> rows, int threshold) =>
        rows.GroupBy(r => r.ProfileId)
            .Select(g => new FrequentVisitorDto
            {
                VisitorProfileId = g.Key,
                FullName = g.First().FullName,
                Phone = g.First().Phone,
                IsWatchlisted = g.First().IsWatchlisted,
                VisitCount = g.Count()
            })
            .Where(f => f.VisitCount >= threshold)
            .OrderByDescending(f => f.VisitCount).ThenBy(f => f.FullName)
            .Take(20).ToList();

    /// <summary>
    /// Booked-in-advance outcomes. "Booked" counts every visit that was created ahead of the day
    /// rather than walked up to the desk — a ScheduledAt, or a status that only a pre-registration
    /// path can produce. A walk-in has neither, so it never lands in the denominator and cannot
    /// flatter the arrival rate.
    /// </summary>
    private static PreRegistrationOutcomeDto BuildPreRegistrationOutcome(List<Row> rows)
    {
        var booked = rows.Where(r =>
            r.ScheduledAt.HasValue ||
            r.Status is VisitorStatus.PreRegistered or VisitorStatus.Expected or VisitorStatus.NoShow or VisitorStatus.Cancelled).ToList();

        if (booked.Count == 0) return new PreRegistrationOutcomeDto();

        var arrived = booked.Count(r => r.CheckedInAt.HasValue);
        var cancelled = booked.Count(r => r.Status == VisitorStatus.Cancelled);
        // Anything booked that never arrived and was not cancelled is a no-show, whether or not
        // the sweep has got round to stamping the status yet.
        var noShow = booked.Count(r => !r.CheckedInAt.HasValue && r.Status != VisitorStatus.Cancelled);

        return new PreRegistrationOutcomeDto
        {
            Booked = booked.Count,
            Arrived = arrived,
            NoShow = noShow,
            Cancelled = cancelled,
            ArrivalRatePercent = Math.Round(100.0 * arrived / booked.Count, 1),
            NoShowRatePercent = Math.Round(100.0 * noShow / booked.Count, 1)
        };
    }

    /// <summary>
    /// New versus returning, decided by whether the profile has any visit BEFORE the range starts —
    /// not by the visit count inside it. Someone who came twice this week but has been coming for
    /// years is a returning visitor, and counting them as new would overstate reach.
    /// </summary>
    private async Task<NewVsReturningDto> BuildAudienceAsync(VisitorReportScope scope, List<Row> rows, DateTime startUtc, CancellationToken ct)
    {
        var profileIds = rows.Select(r => r.ProfileId).Distinct().ToList();
        if (profileIds.Count == 0) return new NewVsReturningDto();

        var seenBefore = await ScopedVisits(scope)
            .Where(v => profileIds.Contains(v.VisitorProfileId) && v.CreatedAt < startUtc)
            .Select(v => v.VisitorProfileId)
            .Distinct()
            .ToListAsync(ct);

        var returning = seenBefore.Count;
        var newVisitors = profileIds.Count - returning;

        return new NewVsReturningDto
        {
            NewVisitors = newVisitors,
            ReturningVisitors = returning,
            ReturningPercent = profileIds.Count > 0 ? Math.Round(100.0 * returning / profileIds.Count, 1) : 0
        };
    }

    private async Task<List<BranchVisitSummaryDto>> BuildBranchComparisonAsync(
        VisitorReportScope scope, DateTime startUtc, DateTime endUtc, VisitorReportFilter filter, CancellationToken ct)
    {
        var perBranch = await ApplyFilter(ScopedVisits(scope), filter)
            .Where(v => v.CreatedAt >= startUtc && v.CreatedAt < endUtc)
            .Select(v => new
            {
                v.BranchId,
                BranchName = v.Branch!.Name,
                v.VisitorProfileId,
                v.Status,
                v.CheckedInAt,
                v.CheckedOutAt
            })
            .ToListAsync(ct);

        return perBranch
            .GroupBy(v => (v.BranchId, v.BranchName))
            .Select(g =>
            {
                var dwell = g.Where(v => v.CheckedInAt.HasValue && v.CheckedOutAt.HasValue).ToList();
                return new BranchVisitSummaryDto
                {
                    BranchId = g.Key.BranchId,
                    BranchName = g.Key.BranchName,
                    TotalVisits = g.Count(),
                    UniqueVisitors = g.Select(v => v.VisitorProfileId).Distinct().Count(),
                    StillOnSite = g.Count(v => v.Status == VisitorStatus.CheckedIn),
                    AvgDwellMinutes = dwell.Count > 0
                        ? Math.Round(dwell.Average(v => (v.CheckedOutAt!.Value - v.CheckedInAt!.Value).TotalMinutes), 1)
                        : 0
                };
            })
            .OrderByDescending(b => b.TotalVisits)
            .ToList();
    }

    // -----------------------------------------------------------------------------------------
    // Exceptions
    // -----------------------------------------------------------------------------------------

    public async Task<VisitorExceptionsDto> BuildExceptionsAsync(
        VisitorReportScope scope, VisitorReportFilter filter, VisitorReportingSettingsDto settings, CancellationToken ct = default)
    {
        var zone = ResolveZone(scope.TimeZoneId);
        var (from, to, startUtc, endUtc) = ResolveRange(filter, scope.TimeZoneId);
        var now = DateTime.UtcNow;
        var todayLocalStartUtc = LocalDayStartToUtc(DateOnly.FromDateTime(ToLocal(now, zone)), zone);

        // OPEN visits are deliberately NOT restricted to the reported range. Somebody checked in
        // eight days ago and never checked out is the single most important row this whole module
        // can surface, and a report defaulting to "last 7 days" would hide exactly that person.
        var openVisits = await ApplyFilter(ScopedVisits(scope), filter)
            .Where(v => v.Status == VisitorStatus.CheckedIn && v.CheckedInAt != null)
            .Select(v => new Row(
                v.Id, v.VisitorProfileId, v.BadgeCode, v.VisitorProfile!.FullName, v.VisitorProfile.Phone,
                v.VisitorProfile.Email, v.VisitorProfile.Company, v.VisitorProfile.IsWatchlisted,
                v.Purpose, v.HostName, v.StudentId, v.StudentName, v.Status, v.VisitorType,
                v.CreatedAt, v.CheckedInAt, v.CheckedOutAt, v.ScheduledAt, v.ConsentGivenAt,
                v.VisitorProfile.InductionCompletedAt, v.VisitorProfile.InductionNotes,
                v.WatchlistOverrideReason, v.VisitorProfile.WatchlistReason,
                v.EntryGate, v.ExitGate, v.CheckedInByUserId))
            .ToListAsync(ct);

        var rangeRows = await LoadRowsAsync(scope, startUtc, endUtc, filter, ct);

        var stillOnSite = openVisits
            .Where(r => r.CheckedInAt!.Value < todayLocalStartUtc)
            .OrderBy(r => r.CheckedInAt)
            .Select(r => ToException(r, VisitorExceptionKind.StillOnSite, now, zone))
            .ToList();

        var overstayed = openVisits
            .Where(r => r.CheckedInAt!.Value >= todayLocalStartUtc)
            .Where(r => (now - r.CheckedInAt!.Value).TotalHours >= ThresholdFor(r.VisitorType, settings))
            .OrderByDescending(r => now - r.CheckedInAt!.Value)
            .Select(r => ToException(r, VisitorExceptionKind.Overstayed, now, zone))
            .ToList();

        var noHost = rangeRows
            .Where(r => r.CheckedInAt.HasValue && string.IsNullOrWhiteSpace(r.HostName))
            .OrderByDescending(r => r.CheckedInAt)
            .Take(MaxDetailRows)
            .Select(r => ToException(r, VisitorExceptionKind.NoHostRecorded, now, zone))
            .ToList();

        var inductionLapsed = rangeRows
            .Where(r => r.VisitorType == VisitorType.Contractor && r.CheckedInAt.HasValue)
            .Where(r => !WasInductionValidOn(r.InductionCompletedAt, r.CheckedInAt!.Value))
            .OrderByDescending(r => r.CheckedInAt)
            .Take(MaxDetailRows)
            .Select(r => ToException(r, VisitorExceptionKind.InductionLapsed, now, zone))
            .ToList();

        var frequent = rangeRows
            .GroupBy(r => r.ProfileId)
            .Where(g => g.Count() >= settings.FrequentVisitorThreshold)
            .OrderByDescending(g => g.Count())
            .Select(g =>
            {
                var latest = g.OrderByDescending(r => r.CreatedAt).First();
                return ToException(latest, VisitorExceptionKind.FrequentVisitor, now, zone,
                    string.Create(CultureInfo.InvariantCulture, $"{g.Count()} visits between {from:MMM dd} and {to:MMM dd}."));
            })
            .Take(50)
            .ToList();

        return new VisitorExceptionsDto
        {
            From = from,
            To = to,
            TimeZoneId = scope.TimeZoneId,
            ScopeName = scope.Name,
            OverstayThresholdHours = settings.OverstayThresholdHours,
            FrequentVisitorThreshold = settings.FrequentVisitorThreshold,
            StillOnSite = stillOnSite,
            Overstayed = overstayed,
            NoHostRecorded = noHost,
            InductionLapsed = inductionLapsed,
            FrequentVisitors = frequent
        };
    }

    private static int ThresholdFor(VisitorType type, VisitorReportingSettingsDto settings) =>
        type == VisitorType.Contractor
            ? settings.ContractorOverstayThresholdHours ?? settings.OverstayThresholdHours
            : settings.OverstayThresholdHours;

    /// <summary>
    /// Whether the induction was valid ON THE DAY OF THE VISIT — not as of now. A contractor whose
    /// induction was current in March and lapsed in August was compliant in March, and a report
    /// that judged history by today's date would invent a breach that never happened.
    /// </summary>
    private static bool WasInductionValidOn(DateTime? completedAt, DateTime visitAt) =>
        completedAt.HasValue && completedAt.Value.AddDays(InductionValidityDays) > visitAt;

    private static VisitorExceptionDto ToException(Row r, VisitorExceptionKind kind, DateTime now, TimeZoneInfo zone, string? detail = null)
    {
        double? hoursOnSite = r.CheckedInAt.HasValue && !r.CheckedOutAt.HasValue
            ? Math.Round((now - r.CheckedInAt.Value).TotalHours, 1)
            : null;

        return new VisitorExceptionDto
        {
            Kind = kind,
            VisitorId = r.VisitorId,
            VisitorProfileId = r.ProfileId,
            BadgeCode = r.BadgeCode,
            FullName = r.FullName,
            Phone = r.Phone,
            Company = r.Company,
            HostName = r.HostName,
            Purpose = r.Purpose,
            VisitorType = r.VisitorType,
            CheckedInAt = r.CheckedInAt,
            CheckedOutAt = r.CheckedOutAt,
            IsWatchlisted = r.IsWatchlisted,
            HoursOnSite = hoursOnSite,
            Detail = detail ?? DescribeException(r, kind, hoursOnSite, zone)
        };
    }

    private static string DescribeException(Row r, VisitorExceptionKind kind, double? hoursOnSite, TimeZoneInfo zone) => kind switch
    {
        VisitorExceptionKind.StillOnSite when r.CheckedInAt.HasValue =>
            string.Create(CultureInfo.InvariantCulture, $"Checked in {ToLocal(r.CheckedInAt.Value, zone):MMM dd 'at' HH:mm} and never checked out — {hoursOnSite:0.#} hours ago."),
        VisitorExceptionKind.Overstayed =>
            $"On site {hoursOnSite:0.#} hours, longer than expected for a {r.VisitorType.ToString().ToLowerInvariant()} visit.",
        VisitorExceptionKind.NoHostRecorded =>
            "Admitted with no host recorded, so there is no member of staff accountable for this visit.",
        VisitorExceptionKind.InductionLapsed when r.InductionCompletedAt.HasValue =>
            string.Create(CultureInfo.InvariantCulture, $"Contractor admitted on a site induction that expired {ToLocal(r.InductionCompletedAt.Value.AddDays(InductionValidityDays), zone):MMM dd, yyyy}."),
        VisitorExceptionKind.InductionLapsed =>
            "Contractor admitted with no site induction on record at all.",
        _ => ""
    };

    // -----------------------------------------------------------------------------------------
    // Compliance
    // -----------------------------------------------------------------------------------------

    public async Task<VisitorComplianceDto> BuildComplianceAsync(
        VisitorReportScope scope, VisitorReportFilter filter, bool consentRequired,
        VisitorRetentionSettingsDto retention, RetentionEvidenceDto evidence, CancellationToken ct = default)
    {
        var (from, to, startUtc, endUtc) = ResolveRange(filter, scope.TimeZoneId);
        var rows = await LoadRowsAsync(scope, startUtc, endUtc, filter, ct);

        var contractors = rows.Where(r => r.VisitorType == VisitorType.Contractor && r.CheckedInAt.HasValue).ToList();
        var contractorsValid = contractors.Count(r => WasInductionValidOn(r.InductionCompletedAt, r.CheckedInAt!.Value));

        var checkedIn = rows.Where(r => r.CheckedInAt.HasValue).ToList();
        var consentCaptured = checkedIn.Count(r => r.ConsentGivenAt.HasValue);

        var oldestRetained = await ScopedVisits(scope)
            .OrderBy(v => v.CreatedAt)
            .Select(v => (DateTime?)v.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return new VisitorComplianceDto
        {
            From = from,
            To = to,
            ScopeName = scope.Name,

            ContractorVisits = contractors.Count,
            ContractorVisitsWithValidInduction = contractorsValid,
            InductionCompliancePercent = contractors.Count > 0
                ? Math.Round(100.0 * contractorsValid / contractors.Count, 1)
                : 100,

            // A branch with the consent requirement switched off has no consent obligation, so it
            // reports as fully compliant with a zero denominator rather than as 0% — the caller
            // reads ConsentRequired to tell "nothing to do" from "nothing done".
            ConsentRequired = consentRequired,
            ConsentRequiredVisits = consentRequired ? checkedIn.Count : 0,
            ConsentCapturedVisits = consentCaptured,
            ConsentCompliancePercent = !consentRequired
                ? 100
                : checkedIn.Count > 0
                    ? Math.Round(100.0 * consentCaptured / checkedIn.Count, 1)
                    : 100,

            InductionRegister = contractors
                .OrderByDescending(r => r.CheckedInAt)
                .Take(MaxDetailRows)
                .Select(r => new InductionRecordDto
                {
                    VisitorId = r.VisitorId,
                    BadgeCode = r.BadgeCode,
                    FullName = r.FullName,
                    Company = r.Company,
                    CheckedInAt = r.CheckedInAt,
                    InductionCompletedAt = r.InductionCompletedAt,
                    InductionExpiresAt = r.InductionCompletedAt?.AddDays(InductionValidityDays),
                    ValidOnVisitDate = WasInductionValidOn(r.InductionCompletedAt, r.CheckedInAt!.Value),
                    InductionNotes = r.InductionNotes
                })
                .ToList(),

            WatchlistActivity = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.WatchlistOverrideReason) || r.IsWatchlisted)
                .OrderByDescending(r => r.CheckedInAt ?? r.CreatedAt)
                .Take(MaxDetailRows)
                .Select(r => new WatchlistActivityDto
                {
                    VisitorId = r.VisitorId,
                    VisitorProfileId = r.ProfileId,
                    BadgeCode = r.BadgeCode,
                    FullName = r.FullName,
                    OccurredAt = r.CheckedInAt ?? r.CreatedAt,
                    Event = !string.IsNullOrWhiteSpace(r.WatchlistOverrideReason)
                        ? "Admitted on manager override"
                        : "Flagged visitor on site",
                    Reason = r.WatchlistReason,
                    OverrideReason = r.WatchlistOverrideReason
                })
                .ToList(),

            // Only a real gap when the branch actually asks for consent. Listing every visit at a
            // branch that does not require it buried the induction and watchlist findings under
            // hundreds of rows that needed no action.
            ConsentGaps = (consentRequired ? checkedIn : new List<Row>())
                .Where(r => !r.ConsentGivenAt.HasValue)
                .OrderByDescending(r => r.CheckedInAt)
                .Take(MaxDetailRows)
                .Select(r => new ConsentGapDto
                {
                    VisitorId = r.VisitorId,
                    BadgeCode = r.BadgeCode,
                    FullName = r.FullName,
                    CheckedInAt = r.CheckedInAt,
                    Purpose = r.Purpose
                })
                .ToList(),

            Retention = evidence with
            {
                RetentionDays = retention.RetentionDays,
                OldestRetainedVisitAt = oldestRetained
            }
        };
    }

    // -----------------------------------------------------------------------------------------
    // Export
    // -----------------------------------------------------------------------------------------

    public async Task<string> BuildLogCsvAsync(VisitorReportScope scope, VisitorReportFilter filter, CancellationToken ct = default)
    {
        var zone = ResolveZone(scope.TimeZoneId);
        var (_, _, startUtc, endUtc) = ResolveRange(filter, scope.TimeZoneId);
        var rows = await LoadRowsAsync(scope, startUtc, endUtc, filter, ct);

        var admittedBy = await VisitorGates.StaffNamesAsync(_context, rows.Select(r => r.CheckedInByUserId));

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Badge Code,Full Name,Phone,Email,Company,Type,Purpose,Host,Student,Status,Checked In,Checked Out,Dwell (min),Consent,Watchlisted,Override Reason,Entry Gate,Exit Gate,Admitted By");

        foreach (var r in rows.OrderBy(r => r.CreatedAt))
        {
            var dwell = r.CheckedInAt.HasValue && r.CheckedOutAt.HasValue
                ? Math.Round((r.CheckedOutAt.Value - r.CheckedInAt.Value).TotalMinutes).ToString("0")
                : "";

            csv.AppendLine(string.Join(",", new[]
            {
                Csv(r.BadgeCode), Csv(r.FullName), Csv(r.Phone), Csv(r.Email), Csv(r.Company),
                Csv(r.VisitorType.ToString()), Csv(r.Purpose), Csv(r.HostName), Csv(r.StudentName),
                Csv(r.Status.ToString()),
                // Local time, matching the day and hour buckets in the summary this export sits
                // under. A CSV in UTC beside a chart in branch time is how two "correct" numbers
                // end up disagreeing in a meeting.
                Csv(r.CheckedInAt.HasValue ? ToLocal(r.CheckedInAt.Value, zone).ToString("yyyy-MM-dd HH:mm") : ""),
                Csv(r.CheckedOutAt.HasValue ? ToLocal(r.CheckedOutAt.Value, zone).ToString("yyyy-MM-dd HH:mm") : ""),
                Csv(dwell),
                Csv(r.ConsentGivenAt.HasValue ? "Yes" : "No"),
                Csv(r.IsWatchlisted ? "Yes" : "No"),
                Csv(r.WatchlistOverrideReason),
                Csv(r.EntryGate), Csv(r.ExitGate),
                Csv(r.CheckedInByUserId is { } by ? admittedBy.GetValueOrDefault(by) : null)
            }));
        }

        return csv.ToString();
    }

    private static string Csv(string? value)
    {
        value ??= "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
