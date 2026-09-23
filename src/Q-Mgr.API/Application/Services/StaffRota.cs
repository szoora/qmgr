using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// The duty rota's rules (plan §4.1), in one home for the duty editor, the generator, extend and swap: the default
/// report cadence for a slot's length, the clash and fairness warnings, the seeded "Teacher on Duty" parameter, and
/// the content-free "you are on the rota" notice. A second copy of any of these next to one caller is how the editor
/// and the generator would come to disagree about what a clash is.
/// </summary>
public static class StaffRota
{
    public const string TeacherOnDutyParameter = "Teacher on Duty";

    /// <summary>Plan §15 decision 1: daily for a day-long slot, weekly for about a week, monthly for about a month.</summary>
    public static ReportCadence DefaultCadence(StaffPerformancePolicyDto policy, DateTime startsAt, DateTime endsAt)
    {
        var defaults = policy.DutyReportDefaults ?? new DutyReportDefaultsDto();
        var days = (endsAt - startsAt).TotalDays;
        return days <= 1.5 ? defaults.DayLongCadence : days <= 10 ? defaults.WeekLongCadence : defaults.MonthLongCadence;
    }

    public static TimeOnly? ParseLocalTime(string? value)
        => !string.IsNullOrWhiteSpace(value) && TimeOnly.TryParseExact(value.Trim(), "HH:mm", out var t) ? t : null;

    /// <summary>
    /// Warnings for a slot, never refusals (plan §4.1): the person is already on an overlapping rota slot or expected at
    /// an overlapping Session duty, the account is inactive, the person supervises their own slot, or they would pass
    /// the term's fairness limit. <paramref name="pendingSlots"/> lets a generator preview count the slots it is about
    /// to write as well as the stored ones.
    /// </summary>
    public static async Task<List<RotaWarningDto>> CheckAsync(
        QMgrDbContext db, IStaffPerformancePolicyService policyService, StaffPerformancePolicyDto policy,
        Guid organizationId, Guid branchId, DateTime startsAt, DateTime endsAt,
        IReadOnlyCollection<Guid> userIds, IReadOnlyCollection<Guid> supervisorIds, Guid? excludeDutyId,
        IReadOnlyList<(DateTime Start, DateTime End, IReadOnlyCollection<Guid> Users)>? pendingSlots = null,
        StaffPerformanceMapping.NameLookup? names = null)
    {
        var warnings = new List<RotaWarningDto>();
        var everyone = userIds.Concat(supervisorIds).Distinct().ToList();
        if (everyone.Count == 0) return warnings;
        names ??= await StaffLookups.LoadNamesAsync(db, everyone.Select(id => (Guid?)id));

        var inactive = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => everyone.Contains(u.Id) && (!u.IsActive || u.OrganizationId != organizationId))
            .Select(u => u.Id).ToListAsync();
        foreach (var id in inactive)
            warnings.Add(new RotaWarningDto { Kind = RotaWarningKind.Inactive, UserId = id, FullName = names[id], Message = $"{Name(names, id)}'s account is inactive." });

        foreach (var id in userIds.Intersect(supervisorIds))
            warnings.Add(new RotaWarningDto { Kind = RotaWarningKind.SupervisesSelf, UserId = id, FullName = names[id], Message = $"{Name(names, id)} is both on duty and supervising this slot." });

        // Overlaps: any active duty in the branch whose span meets this one and names the person.
        var overlapping = await db.StaffDuties.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.BranchId == branchId && d.IsActive
                        && d.Kind != DutyKind.Lesson && d.StartsAt < endsAt && d.EndsAt > startsAt
                        && (excludeDutyId == null || d.Id != excludeDutyId))
            .Select(d => new { d.Kind, d.Title, d.StartsAt, d.ExpectedUserIds, d.SupervisorUserIds })
            .ToListAsync();
        foreach (var id in everyone)
        {
            var rota = overlapping.FirstOrDefault(d => d.Kind == DutyKind.Rota && ((d.ExpectedUserIds?.Contains(id) ?? false) || d.SupervisorUserIds.Contains(id)));
            if (rota != null)
                warnings.Add(new RotaWarningDto { Kind = RotaWarningKind.OverlappingRota, UserId = id, FullName = names[id], Message = $"{Name(names, id)} is already on \"{rota.Title}\" at the same time." });
            else if (pendingSlots?.Any(p => p.Start < endsAt && p.End > startsAt && p.Users.Contains(id)) == true)
                warnings.Add(new RotaWarningDto { Kind = RotaWarningKind.OverlappingRota, UserId = id, FullName = names[id], Message = $"{Name(names, id)} is on another slot of this rota at the same time." });

            // A Session duty that expects "everyone" is not a clash worth naming for every person.
            var session = overlapping.FirstOrDefault(d => d.Kind == DutyKind.Session && d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(id));
            if (session != null)
                warnings.Add(new RotaWarningDto { Kind = RotaWarningKind.OverlappingSession, UserId = id, FullName = names[id], Message = $"{Name(names, id)} is expected at \"{session.Title}\" during this slot." });
        }

        // Fairness: slots on duty this term, including the pending ones before this slot.
        var limit = (policy.DutyReportDefaults ?? new DutyReportDefaultsDto()).MaxRotaSlotsPerTerm;
        if (limit > 0 && userIds.Count > 0)
        {
            var period = policyService.PeriodFor(policy, DateOnly.FromDateTime(startsAt));
            var from = period.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var to = period.End.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
            var stored = await db.StaffDuties.AsNoTracking()
                .Where(d => d.OrganizationId == organizationId && d.IsActive && d.Kind == DutyKind.Rota
                            && d.StartsAt >= from && d.StartsAt <= to && d.ExpectedUserIds != null
                            && (excludeDutyId == null || d.Id != excludeDutyId))
                .Select(d => d.ExpectedUserIds!)
                .ToListAsync();
            foreach (var id in userIds)
            {
                var count = stored.Count(ids => ids.Contains(id))
                            + (pendingSlots?.Count(p => p.Start >= from && p.Start <= to && p.Start < startsAt && p.Users.Contains(id)) ?? 0)
                            + 1;
                if (count > limit)
                    warnings.Add(new RotaWarningDto { Kind = RotaWarningKind.OverFairnessLimit, UserId = id, FullName = names[id], Message = $"This would be {Name(names, id)}'s slot {count} in {period.Name}, over the limit of {limit}." });
            }
        }
        return warnings;
    }

    /// <summary>A slot starting outside every term the tenant defined is a holiday. With no terms defined nothing is.</summary>
    public static bool IsHoliday(StaffPerformancePolicyDto policy, DateOnly date)
        => policy.Periods is { Count: > 0 } periods && !periods.Any(p => p.Start <= date && date <= p.End);

    /// <summary>
    /// The "Teacher on Duty" Duty parameter (plan §4.1), inserted by name when the tenant has none — the same
    /// by-name, idempotent shape as the automatic-credit parameters. Returns its id.
    /// </summary>
    public static async Task<Guid> EnsureTeacherOnDutyParameterAsync(QMgrDbContext db, Guid organizationId, ILogger logger)
    {
        var existing = await db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.Name.ToLower() == TeacherOnDutyParameter.ToLower())
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync();
        if (existing != null) return existing.Id;

        var maxSort = await db.PerformanceParameters.IgnoreQueryFilters().Where(p => p.OrganizationId == organizationId).MaxAsync(p => (int?)p.SortOrder) ?? 0;
        var parameter = new PerformanceParameter { OrganizationId = organizationId };
        StaffPerformanceMapping.Apply(parameter, new SavePerformanceParameterRequest
        {
            Name = TeacherOnDutyParameter,
            Kind = ParameterKind.Duty,
            DefaultPoints = 2,
            MaxPointsPerEntry = 2,
            Weight = 1,
            Purpose = "Rostered duty as teacher or administrator on duty: the supervisor records it completed or not completed at the end of the slot. Reports on time are evidence beside it, not points.",
            Color = "#5a6f8a",
            SortOrder = maxSort + 1
        });
        db.PerformanceParameters.Add(parameter);
        try
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Added the {Parameter} parameter for organization {OrganizationId}", TeacherOnDutyParameter, organizationId);
            return parameter.Id;
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "{Parameter} seed for {OrganizationId} collided with a concurrent seed; reading it back", TeacherOnDutyParameter, organizationId);
            db.Entry(parameter).State = EntityState.Detached;
            return await db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.OrganizationId == organizationId && p.Name.ToLower() == TeacherOnDutyParameter.ToLower())
                .Select(p => p.Id).FirstAsync();
        }
    }

    /// <summary>
    /// "You are on the duty rota" to each newly assigned person and supervisor. Names the slot's dates and links to the
    /// portal; carries nothing else (plan §8.3). Never throws: a notice that did not land must not undo a saved rota.
    /// </summary>
    public static async Task NotifyAssignedAsync(INotificationService notifications, ILogger logger, StaffDuty duty, IEnumerable<Guid> userIds, TimeZoneInfo zone, bool supervising)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(duty.StartsAt, zone);
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(duty.EndsAt, zone);
        var span = local.Date == localEnd.Date ? string.Create(CultureInfo.InvariantCulture, $"{local:ddd dd MMM}") : string.Create(CultureInfo.InvariantCulture, $"{local:ddd dd MMM} to {localEnd:ddd dd MMM}");
        foreach (var userId in userIds.Distinct())
        {
            try
            {
                await notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = userId,
                    OrganizationId = duty.OrganizationId,
                    BranchId = duty.BranchId,
                    Title = supervising ? $"You are supervising: {duty.Title}" : $"You are on the duty rota: {duty.Title}",
                    Message = $"{span}.",
                    Type = NotificationType.StaffPerformance,
                    Priority = NotificationPriority.Normal,
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    EventKey = NotificationEventKeys.StaffRotaAssigned,
                    ActionUrl = "/portal#on-duty",
                    IconClass = "calendar-week"
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rota assignment notice for duty {DutyId} could not reach user {UserId}", duty.Id, userId);
            }
        }
    }

    private static string Name(StaffPerformanceMapping.NameLookup names, Guid id) => names[id] is { Length: > 0 } n ? n : "This person";
}
