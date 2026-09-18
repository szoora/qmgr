using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// Duty reports (plan §4.3, §4.4) in one home: the report periods of a rota slot, the rows that stand for them, WHO may
/// read or act on a report, and the mapping. The controller, the reminder sweep, the portal and the upload authorizer all
/// call this — a second copy of the read rule next to any of them is how a report and its evidence would come to disagree.
/// </summary>
public static class StaffDutyReports
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- Periods and rows ----------------------------------------------------------------------------

    /// <summary>
    /// The report periods of a slot, as branch-local days: one per day (Daily), per seven days from the first day
    /// (Weekly), per calendar month from the first day (Monthly), or the whole slot (EndOfDuty). None has none.
    /// </summary>
    public static List<(DateOnly Start, DateOnly End)> Periods(StaffDuty duty, TimeZoneInfo zone)
    {
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(duty.StartsAt, zone));
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(duty.EndsAt, zone));
        var periods = new List<(DateOnly, DateOnly)>();
        if (last < first) return periods;
        switch (duty.ReportCadence)
        {
            case ReportCadence.Daily:
                for (var d = first; d <= last; d = d.AddDays(1)) periods.Add((d, d));
                break;
            case ReportCadence.Weekly:
                for (var d = first; d <= last; d = d.AddDays(7)) periods.Add((d, Min(d.AddDays(6), last)));
                break;
            case ReportCadence.Monthly:
                for (var d = first; d <= last; d = d.AddMonths(1)) periods.Add((d, Min(d.AddMonths(1).AddDays(-1), last)));
                break;
            case ReportCadence.EndOfDuty:
                periods.Add((first, last));
                break;
        }
        return periods;
    }

    /// <summary>A period's due time in UTC: its last day at the slot's (or the policy's) due time, never before the slot ends.</summary>
    public static DateTime DueAt(StaffDuty duty, DateOnly periodEnd, StaffPerformancePolicyDto policy, TimeZoneInfo zone)
    {
        var dueTime = duty.ReportDueLocalTime
                      ?? StaffRota.ParseLocalTime((policy.DutyReportDefaults ?? new DutyReportDefaultsDto()).DueLocalTime)
                      ?? new TimeOnly(18, 0);
        var due = TimeZoneInfo.ConvertTimeToUtc(periodEnd.ToDateTime(dueTime), zone);
        var slotEndDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(duty.EndsAt, zone));
        return periodEnd == slotEndDay && due < duty.EndsAt ? duty.EndsAt : due;
    }

    /// <summary>The people who write reports on a slot: each person on duty, and each administrator on duty.</summary>
    public static IEnumerable<(Guid UserId, DutyReportAuthorRole Role)> Authors(StaffDuty duty)
        => (duty.ExpectedUserIds ?? Array.Empty<Guid>()).Distinct().Select(id => (id, DutyReportAuthorRole.OnDuty))
            .Concat(duty.SupervisorUserIds.Distinct().Where(id => !(duty.ExpectedUserIds ?? Array.Empty<Guid>()).Contains(id)).Select(id => (id, DutyReportAuthorRole.Supervisor)));

    /// <summary>
    /// Makes sure a Draft row stands for every author of every period of <paramref name="duty"/> that has STARTED. Idempotent;
    /// two callers racing are settled by the unique index (the loser's insert is dropped, the row is there either way).
    /// </summary>
    public static async Task EnsureRowsAsync(QMgrDbContext db, StaffDuty duty, StaffPerformancePolicyDto policy, TimeZoneInfo zone, DateTime nowUtc, ILogger logger)
    {
        if (duty.Kind != DutyKind.Rota || !duty.IsActive || duty.ReportCadence == ReportCadence.None) return;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        var started = Periods(duty, zone).Where(p => p.Start <= today).ToList();
        if (started.Count == 0) return;

        var existing = (await db.StaffDutyReports.AsNoTracking()
                .Where(r => r.DutyId == duty.Id)
                .Select(r => new { r.AuthorUserId, r.PeriodStart })
                .ToListAsync())
            .Select(r => (r.AuthorUserId, r.PeriodStart))
            .ToHashSet();

        var added = new List<StaffDutyReport>();
        foreach (var (start, end) in started)
            foreach (var (userId, role) in Authors(duty))
            {
                if (existing.Contains((userId, start))) continue;
                added.Add(new StaffDutyReport
                {
                    OrganizationId = duty.OrganizationId,
                    BranchId = duty.BranchId,
                    DutyId = duty.Id,
                    AuthorUserId = userId,
                    AuthorRole = role,
                    PeriodStart = start,
                    PeriodEnd = end,
                    DueAt = DueAt(duty, end, policy, zone),
                    CreatedBy = userId
                });
            }
        if (added.Count == 0) return;

        db.StaffDutyReports.AddRange(added);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            // A concurrent caller made some of the same rows. Drop ours and add one at a time so the rest still land.
            foreach (var r in added) db.Entry(r).State = EntityState.Detached;
            logger.LogDebug(ex, "Duty report rows for duty {DutyId} raced; retrying one by one", duty.Id);
            foreach (var r in added)
            {
                db.StaffDutyReports.Add(r);
                try { await db.SaveChangesAsync(); }
                catch (DbUpdateException) { db.Entry(r).State = EntityState.Detached; }
            }
        }
    }

    // ---- Access ------------------------------------------------------------------------------------------

    /// <summary>What a caller may do with one report. <see cref="CanRead"/> false means 404.</summary>
    public sealed record Access(bool CanRead, bool IsAuthor, bool CanEdit, bool CanComment, bool CanRespond, bool CanReview, bool CanMarkNoDuty);

    /// <summary>
    /// The read rule (plan §4.3, §13.4):
    ///  - the author, always (a draft is theirs alone);
    ///  - the slot's supervisors, for the reports of the people on duty;
    ///  - a person on duty reads the supervisor's report about them only when the tenant switches that on — never a
    ///    colleague's report on the same slot;
    ///  - holders of staff.dutyreports.view with the author inside their staff scope;
    ///  - a Confidential report additionally needs staff.confidential.view, except for its author and the slot's supervisors.
    /// Reviewing (comment, return, mark reviewed) is staff.dutyreports.review with the author in scope, or the slot's
    /// supervisor for an on-duty report — never the author, so nobody reviews their own report.
    /// </summary>
    public static async Task<Access> AccessForAsync(Guid callerId, StaffDutyReport report, StaffDuty duty, StaffPerformancePolicyDto policy,
        Func<string, Task<bool>> hasPermission, Func<Guid, Guid, Task<bool>> canSeeStaff)
    {
        var none = new Access(false, false, false, false, false, false, false);
        if (callerId == Guid.Empty) return none;

        var isAuthor = report.AuthorUserId == callerId;
        var isSupervisor = duty.SupervisorUserIds.Contains(callerId);
        var isOnDutyColleague = (duty.ExpectedUserIds ?? Array.Empty<Guid>()).Contains(callerId);
        var open = report.Status is DutyReportStatus.Draft or DutyReportStatus.Returned;
        var mayManage = await hasPermission(Permissions.StaffDutiesManage);

        if (isAuthor)
            return new Access(true, true, CanEdit: open, CanComment: false, CanRespond: report.Status != DutyReportStatus.Draft, CanReview: false,
                CanMarkNoDuty: open && (mayManage || isSupervisor));

        // Nobody but the author reads a draft.
        if (report.Status == DutyReportStatus.Draft && !(isSupervisor || mayManage))
            return none;

        var supervisesThis = isSupervisor && report.AuthorRole == DutyReportAuthorRole.OnDuty;
        var colleagueReadsSupervisor = isOnDutyColleague && report.AuthorRole == DutyReportAuthorRole.Supervisor
                                       && (policy.DutyReportDefaults?.OnDutyMayReadSupervisorReport ?? false);

        var inScopeViewer = await hasPermission(Permissions.StaffDutyReportsView) && await canSeeStaff(report.BranchId, report.AuthorUserId);
        var inScopeReviewer = await hasPermission(Permissions.StaffDutyReportsReview) && await canSeeStaff(report.BranchId, report.AuthorUserId);

        var canRead = supervisesThis || colleagueReadsSupervisor || inScopeViewer || inScopeReviewer;
        if (canRead && report.Visibility == WelfareVisibility.Confidential && !supervisesThis)
            canRead = await hasPermission(Permissions.StaffConfidentialView);
        // A supervisor or duty manager sees a draft exists only to mark "no duty that day"; its text stays the author's.
        if (report.Status == DutyReportStatus.Draft)
            return new Access(false, false, false, false, false, false, CanMarkNoDuty: supervisesThis || mayManage);
        if (!canRead) return none;

        var reviewer = supervisesThis || inScopeReviewer;
        var submitted = report.Status is DutyReportStatus.Submitted or DutyReportStatus.Reviewed or DutyReportStatus.Returned;
        return new Access(true, false, CanEdit: false, CanComment: reviewer && submitted, CanRespond: false,
            CanReview: reviewer && report.Status == DutyReportStatus.Submitted,
            CanMarkNoDuty: open && (supervisesThis || mayManage));
    }

    // ---- Mapping -----------------------------------------------------------------------------------------

    public static Dictionary<string, string> ParseSections(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static string SerializeSections(Dictionary<string, string> sections) => JsonSerializer.Serialize(sections, Json);

    public static DutyReportSummaryDto ToSummary(StaffDutyReport r, StaffDuty duty, StaffPerformanceMapping.NameLookup names, int commentCount, DateTime nowUtc) => new()
    {
        Id = r.Id,
        DutyId = r.DutyId,
        DutyTitle = duty.Title,
        AuthorUserId = r.AuthorUserId,
        AuthorName = names[r.AuthorUserId],
        AuthorRole = r.AuthorRole,
        PeriodStart = r.PeriodStart,
        PeriodEnd = r.PeriodEnd,
        DueAt = r.DueAt,
        Status = r.Status,
        IsOverdue = r.Status is DutyReportStatus.Draft or DutyReportStatus.Returned && r.DueAt < nowUtc,
        SubmittedLate = r.SubmittedAt.HasValue && r.SubmittedAt.Value > r.DueAt,
        SubmittedAt = r.SubmittedAt,
        ReviewedAt = r.ReviewedAt,
        CommentCount = commentCount,
        Visibility = r.Visibility
    };

    /// <summary>"Mon 22 Sep" or "Mon 22 Sep – Sun 28 Sep", invariant (plan §8.3: a message names the period, never the content).</summary>
    public static string PeriodText(DateOnly start, DateOnly end)
        => start == end
            ? start.ToString("ddd dd MMM", CultureInfo.InvariantCulture)
            : $"{start.ToString("ddd dd MMM", CultureInfo.InvariantCulture)} – {end.ToString("ddd dd MMM", CultureInfo.InvariantCulture)}";

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
