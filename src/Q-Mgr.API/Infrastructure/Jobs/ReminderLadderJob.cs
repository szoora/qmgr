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
/// The one reminder sweep (duty rota plan §8.1), every 15 minutes. Each ladder is a method; each asks
/// <see cref="IReminderLadderService"/> which stage is due for a row, CLAIMS it with a conditional update so two
/// workers can never send the same stage twice (the notice fan-out pattern), and only then sends. A send that
/// fails after the claim is logged, not retried — a reminder that went to nine of ten people is better than
/// nine people reminded twice.
///
/// Replaces the hourly single-shot <c>staff-duty-reminders</c> and <c>staff-register-chase</c> jobs (removed at
/// registration). Two behaviour changes with it: a Session duty's reminder now reaches its named RECORDERS as well
/// as the people expected (a minute-taker who is not expected was never told), and both ladders honour quiet hours.
///
/// A job has no tenant: every query joins the organization explicitly, and the module is checked per organization.
/// </summary>
public class ReminderLadderJob
{
    private readonly QMgrDbContext _context;
    private readonly INotificationService _notifications;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly IReminderLadderService _ladders;
    private readonly IModuleAccessService _modules;
    private readonly ILogger<ReminderLadderJob> _logger;

    private readonly Dictionary<Guid, bool> _moduleActive = new();
    private readonly Dictionary<Guid, StaffPerformancePolicyDto> _policies = new();

    /// <summary>The furthest ahead any pre-start ladder may reach: the rota's stage 1 is a week, the editor allows a fortnight.</summary>
    internal const int MaxLeadDays = 15;

    public ReminderLadderJob(
        QMgrDbContext context,
        INotificationService notifications,
        IStaffPerformancePolicyService policy,
        IReminderLadderService ladders,
        IModuleAccessService modules,
        ILogger<ReminderLadderJob> logger)
    {
        _context = context;
        _notifications = notifications;
        _policy = policy;
        _ladders = ladders;
        _modules = modules;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)] // the next sweep is fifteen minutes away and the claims make it safe
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task SweepAsync()
    {
        var now = DateTime.UtcNow;
        await RunAsync("session-start", () => SessionStartAsync(now));
        await RunAsync("register-chase", () => RegisterChaseAsync(now));
    }

    private async Task RunAsync(string name, Func<Task<int>> ladder)
    {
        try
        {
            var sent = await ladder();
            if (sent > 0) _logger.LogInformation("Reminder ladder {Ladder}: {Sent} stage(s) sent", name, sent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reminder ladder {Ladder} failed; the next sweep retries", name);
        }
        finally
        {
            _context.ChangeTracker.Clear();
        }
    }

    // ---- Session duties: coming up -------------------------------------------------------------------------

    internal async Task<int> SessionStartAsync(DateTime now)
    {
        var horizon = now.AddDays(MaxLeadDays);
        var duties = await _context.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Include(d => d.Branch)
            .Include(d => d.Parameter)
            .Where(d => d.IsActive && d.Kind == DutyKind.Session && d.StartsAt > now && d.StartsAt <= horizon)
            .OrderBy(d => d.StartsAt)
            .ToListAsync();

        var sent = 0;
        foreach (var duty in duties)
        {
            try
            {
                if (!await ModuleActiveAsync(duty.OrganizationId)) continue;
                var policy = await PolicyAsync(duty.OrganizationId);
                var zone = AppointmentScheduling.ResolveTimeZone(duty.Branch?.Timezone);
                var stage = _ladders.DueStage(_policy.LadderFor(policy, ReminderSubject.SessionStart), duty.StartsAt, duty.ReminderStage, now, zone, policy.QuietHours);
                if (stage == null) continue;

                var claimed = await _context.StaffDuties.IgnoreQueryFilters()
                    .Where(d => d.Id == duty.Id && d.ReminderStage < stage.Stage && d.StartsAt == duty.StartsAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReminderStage, stage.Stage).SetProperty(d => d.ReminderSentAt, now));
                if (claimed == 0) continue;
                sent++;

                var channels = _ladders.ChannelsFor(stage);
                if (channels == NotificationChannel.None) continue; // a digest-only stage: the digest carries it

                var local = TimeZoneInfo.ConvertTimeFromUtc(duty.StartsAt, zone);
                var expected = duty.ExpectedUserIds?.ToList()
                               ?? await StaffLookups.BranchStaff(_context, duty.OrganizationId, duty.BranchId).Select(u => u.Id).ToListAsync();
                foreach (var userId in expected.Concat(duty.RecorderUserIds).Distinct())
                {
                    var recorderOnly = !expected.Contains(userId);
                    await SafeSendAsync(new CreateNotificationRequest
                    {
                        UserId = userId,
                        OrganizationId = duty.OrganizationId,
                        BranchId = duty.BranchId,
                        Title = $"Coming up: {duty.Title}",
                        Message = $"{local:ddd dd MMM} at {local:HH:mm}{(string.IsNullOrWhiteSpace(duty.Location) ? "" : $", {duty.Location}")} — "
                                  + (recorderOnly ? "you are named to take its register." : $"{duty.Parameter?.Name ?? "duty"}."),
                        Type = NotificationType.StaffPerformance,
                        Priority = stage.Interruptive ? NotificationPriority.High : NotificationPriority.Normal,
                        Channels = channels,
                        EventKey = NotificationEventKeys.StaffDutyReminder,
                        ActionUrl = "/portal",
                        IconClass = "calendar-event"
                    }, duty.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session reminder failed for duty {DutyId}", duty.Id);
            }
        }
        return sent;
    }

    // ---- Registers not taken ------------------------------------------------------------------------------

    /// <summary>
    /// A duty that has ended with its register open. The stage already sent is DERIVED, not stored: stages fall due
    /// in order and each send collapses to the highest one due, so the highest stage whose due time is at or before
    /// <c>RegisterChaseSentAt</c> is exactly the stage last sent. The claim is optimistic on that timestamp.
    /// Lesson duties are chased by the unrecorded-lesson digest instead (plan §7.3), never a bell per lesson.
    /// </summary>
    internal async Task<int> RegisterChaseAsync(DateTime now)
    {
        var since = now.AddDays(-MaxLeadDays);
        var duties = await _context.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Include(d => d.Branch)
            .Where(d => d.IsActive && d.Kind != DutyKind.Lesson && d.EndsAt < now && d.EndsAt > since && d.RegisterClosedAt == null)
            .ToListAsync();

        var sent = 0;
        foreach (var duty in duties)
        {
            try
            {
                if (!await ModuleActiveAsync(duty.OrganizationId)) continue;
                var policy = await PolicyAsync(duty.OrganizationId);
                var zone = AppointmentScheduling.ResolveTimeZone(duty.Branch?.Timezone);
                var ladder = _policy.LadderFor(policy, ReminderSubject.RegisterChase);
                var sentStage = duty.RegisterChaseSentAt is { } last
                    ? ladder.Stages.Where(s => _ladders.DueAtUtc(s, duty.EndsAt, zone) <= last).Select(s => s.Stage).DefaultIfEmpty(0).Max()
                    : 0;
                var stage = _ladders.DueStage(ladder, duty.EndsAt, sentStage, now, zone, policy.QuietHours);
                if (stage == null) continue;

                var previous = duty.RegisterChaseSentAt;
                var claimed = await _context.StaffDuties.IgnoreQueryFilters()
                    .Where(d => d.Id == duty.Id && d.RegisterClosedAt == null && d.RegisterChaseSentAt == previous)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.RegisterChaseSentAt, now));
                if (claimed == 0) continue;
                sent++;

                var channels = _ladders.ChannelsFor(stage);
                if (channels == NotificationChannel.None) continue;

                foreach (var recorder in duty.RecorderUserIds.Distinct())
                    await SafeSendAsync(new CreateNotificationRequest
                    {
                        UserId = recorder,
                        OrganizationId = duty.OrganizationId,
                        BranchId = duty.BranchId,
                        Title = $"Register not taken: {duty.Title}",
                        Message = $"The duty ended {Ago(now - duty.EndsAt)} and its register has not been closed. You are a named recorder.",
                        Type = NotificationType.StaffPerformance,
                        Priority = NotificationPriority.High,
                        Channels = channels,
                        EventKey = NotificationEventKeys.StaffRegisterDue,
                        ActionUrl = "/admin/staff/duties",
                        IconClass = "clipboard-x"
                    }, duty.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Register chase failed for duty {DutyId}", duty.Id);
            }
        }
        return sent;
    }

    // ---------------------------------------------------------------------------------------------------------

    private async Task SafeSendAsync(CreateNotificationRequest request, Guid aboutId)
    {
        try
        {
            await _notifications.CreateInAppNotificationAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reminder about {AboutId} could not reach user {UserId}", aboutId, request.UserId);
        }
    }

    private async Task<bool> ModuleActiveAsync(Guid organizationId)
    {
        if (_moduleActive.TryGetValue(organizationId, out var active)) return active;
        return _moduleActive[organizationId] = await _modules.IsModuleActiveAsync(organizationId, ModuleCodes.StudentWelfare);
    }

    private async Task<StaffPerformancePolicyDto> PolicyAsync(Guid organizationId)
    {
        if (_policies.TryGetValue(organizationId, out var policy)) return policy;
        return _policies[organizationId] = await _policy.GetAsync(organizationId);
    }

    internal static string Ago(TimeSpan span)
    {
        if (span.TotalHours < 1) return "less than an hour ago";
        if (span.TotalHours < 48) return $"{(int)span.TotalHours} hour(s) ago";
        return $"{(int)span.TotalDays} day(s) ago";
    }
}

public static class ReminderLadderJobRegistration
{
    public static void RegisterRecurringJobs()
    {
        RecurringJob.AddOrUpdate<ReminderLadderJob>("staff-reminder-ladder", job => job.SweepAsync(), "*/15 * * * *");
        // The single-shot sweeps this replaces. Removing the recurring entries is what stops them running on an
        // existing server, where Hangfire's storage still holds them.
        RecurringJob.RemoveIfExists("staff-duty-reminders");
        RecurringJob.RemoveIfExists("staff-register-chase");
    }
}
