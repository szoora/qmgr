using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// The timetable integrity sweep (duty rota plan §6.3), daily. Re-diagnoses every published timetable still in force
/// against the world as it now is — a teacher deactivated, an assignment ended, a class renamed away or retired, a
/// Session duty placed over a lesson, a room retired — and tells the timetable masters once per NEW hard breach.
///
/// The set of hard issue keys last reported is stored on the timetable. The new set is claimed with a conditional
/// update against the old one before anything is sent, so two workers cannot announce the same clash twice, and a
/// clash nobody has fixed is not re-announced tomorrow. A job has no tenant: the module is checked per organization.
/// </summary>
public class TimetableIntegrityJob
{
    public const string JobId = "timetable-integrity";

    private readonly QMgrDbContext _context;
    private readonly ITimetableSettingsService _settings;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly IReminderLadderService _ladders;
    private readonly INotificationService _notifications;
    private readonly IModuleAccessService _modules;
    private readonly ILogger<TimetableIntegrityJob> _logger;
    private readonly IActivityLogger _activity;

    public TimetableIntegrityJob(QMgrDbContext context, ITimetableSettingsService settings, IStaffPerformancePolicyService policy,
        IReminderLadderService ladders, INotificationService notifications, IModuleAccessService modules, ILogger<TimetableIntegrityJob> logger, IActivityLogger activity)
    {
        _context = context;
        _settings = settings;
        _policy = policy;
        _ladders = ladders;
        _notifications = notifications;
        _modules = modules;
        _logger = logger;
        _activity = activity;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 900)]
    public async Task SweepAsync()
    {
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var candidates = await _context.Timetables.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Status == TimetableStatus.Published && t.EffectiveTo >= utcToday.AddDays(-1))
            .Select(t => new { t.Id, t.OrganizationId })
            .ToListAsync();

        var moduleActive = new Dictionary<Guid, bool>();
        foreach (var candidate in candidates)
        {
            try
            {
                if (!moduleActive.TryGetValue(candidate.OrganizationId, out var active))
                    active = moduleActive[candidate.OrganizationId] = await _modules.IsModuleActiveAsync(candidate.OrganizationId, ModuleCodes.StudentWelfare);
                if (!active) continue;
                await CheckAsync(candidate.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Timetable integrity check failed for {TimetableId}", candidate.Id);
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Diagnoses one timetable and announces new hard clashes. Returns how many were new.</summary>
    internal async Task<int> CheckAsync(Guid timetableId)
    {
        var timetable = await _context.Timetables.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == timetableId);
        if (timetable == null || timetable.Status != TimetableStatus.Published) return 0;

        var lessons = await _context.TimetableLessons.IgnoreQueryFilters().AsNoTracking().Where(l => l.TimetableId == timetableId).ToListAsync();
        var settings = await _settings.ReadAsync(timetable.BranchId);
        var policy = await _policy.GetAsync(timetable.OrganizationId);
        var branch = await _context.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == timetable.BranchId).Select(b => new { b.Name, b.Timezone }).FirstAsync();
        var zone = AppointmentScheduling.ResolveTimeZone(branch.Timezone);

        var ctx = await TimetableChecker.LoadContextAsync(_context, timetable, settings, policy, zone, lessons);
        var diagnosis = TimetableChecker.Diagnose(timetable, lessons, ctx);
        // The health report's trend (plan §11): one line a day with the counts, whether or not anything changed.
        try
        {
            await _activity.RecordAsync(ActivityActions.TimetableChecked, nameof(Timetable), timetable.Id, null,
                $"Timetable '{timetable.Name}' checked: {diagnosis.HardCount} hard, {diagnosis.SoftCount} soft",
                new { Hard = diagnosis.HardCount, Soft = diagnosis.SoftCount }, timetable.BranchId, timetable.OrganizationId);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not record the timetable check for {TimetableId}", timetableId); }
        var current = diagnosis.Issues.Where(i => i.Severity == TimetableIssueSeverity.Hard).Select(i => i.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var previous = timetable.ReportedIssueKeys ?? Array.Empty<string>();
        var fresh = current.Except(previous).ToList();

        if (current.SequenceEqual(previous.OrderBy(k => k, StringComparer.Ordinal))) return 0;

        // Claim: only the worker that still sees the previous set writes the new one, and only it sends.
        var claimed = await _context.Timetables.IgnoreQueryFilters()
            .Where(t => t.Id == timetableId && t.ReportedIssueKeys == previous)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReportedIssueKeys, current));
        if (claimed == 0 || fresh.Count == 0) return 0;

        var stage = _policy.LadderFor(policy, ReminderSubject.TimetableClash).Stages.OrderBy(st => st.Stage).FirstOrDefault();
        var channels = stage == null ? NotificationChannel.InApp : _ladders.ChannelsFor(stage);
        var masters = await StaffLookups.UsersWithPermissionAsync(_context, timetable.OrganizationId, Permissions.TimetableManage);
        var masterIds = await StaffLookups.BranchStaff(_context, timetable.OrganizationId, timetable.BranchId)
            .Where(u => masters.Contains(u.Id) && (u.Role.StaffScope == StaffDataScope.Organization))
            .Select(u => u.Id).ToListAsync();

        foreach (var master in masterIds)
        {
            try
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = master,
                    OrganizationId = timetable.OrganizationId,
                    BranchId = timetable.BranchId,
                    Title = $"{fresh.Count} new timetable clash{(fresh.Count == 1 ? "" : "es")} in {branch.Name}",
                    Message = $"Found in the published timetable '{timetable.Name}'. Open it to see them.",
                    Type = NotificationType.StaffPerformance,
                    Priority = NotificationPriority.High,
                    Channels = channels,
                    EventKey = NotificationEventKeys.StaffTimetableClash,
                    ActionUrl = $"/admin/timetable?t={timetable.Id}",
                    IconClass = "exclamation-triangle"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Timetable clash notice for {TimetableId} could not reach {UserId}", timetableId, master);
            }
        }
        _logger.LogInformation("Timetable {TimetableId}: {New} new hard clash(es) announced to {Masters} master(s)", timetableId, fresh.Count, masterIds.Count);
        return fresh.Count;
    }
}

public static class TimetableIntegrityJobRegistration
{
    /// <summary>05:00 UTC — 08:00 in Kampala, before the first lessons have gone far.</summary>
    public static void RegisterRecurringJobs()
        => RecurringJob.AddOrUpdate<TimetableIntegrityJob>(TimetableIntegrityJob.JobId, job => job.SweepAsync(), "0 5 * * *");
}
