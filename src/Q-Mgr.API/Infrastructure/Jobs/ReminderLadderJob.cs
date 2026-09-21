using System.Globalization;
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
        await RunAsync("rota-start", () => RotaStartAsync(now));
        await RunAsync("report-due", () => ReportDueAsync(now));
        await RunAsync("register-chase", () => RegisterChaseAsync(now));
        await RunAsync("lesson-start", () => LessonStartAsync(now));
        await RunAsync("my-day", () => MyDayAsync(now));
        await RunAsync("minute-action", () => MinuteActionAsync(now));
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
                        Message = string.Create(CultureInfo.InvariantCulture, $"{local:ddd dd MMM} at {local:HH:mm}{(string.IsNullOrWhiteSpace(duty.Location) ? "" : $", {duty.Location}")} — ")
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

    // ---- Rota slots: coming up (plan §4.2) ----------------------------------------------------------------

    /// <summary>
    /// The pre-duty ladder for a rota slot. The stage is claimed on the ROW (one claim per stage, however many people
    /// are on the slot), and each stage is sent only to the people who have not acknowledged — acknowledging stops the
    /// ladder for that person. A stage whose audience includes the supervisor also tells the administrator on duty who
    /// has not acknowledged, content-free, and the portal carries the same line as a to-do.
    /// </summary>
    /// <summary>
    /// Says out loud that a pre-start reminder was never sent, because every pre-start ladder filters
    /// <c>StartsAt &gt; now</c> and a duty that has already begun simply drops out of the query.
    ///
    /// <para>That is the right send rule — "your duty starts in ten minutes" is not worth sending once it
    /// has started — but the SILENCE was the bug. Found while hardening the e2e: one suite run saturated
    /// the Hangfire pool for about eighty seconds, and a sweep that lands after the start leaves no trace
    /// anywhere that somebody was never told. A real school's publish, materialisation and dispatch bursts
    /// are the same shape.</para>
    ///
    /// <para>This only reports. Whether a late reminder should be sent anyway, and in what words, is a
    /// product decision nobody has taken; until then the log is what makes the gap visible.</para>
    /// </summary>
    private async Task LogMissedPreStartAsync(DutyKind kind, DateTime now)
    {
        // A short look-back: anything older has been reported by an earlier run of this same sweep.
        var since = now.AddHours(-2);
        var missed = await _context.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.IsActive && d.Kind == kind
                        && d.StartsAt <= now && d.StartsAt > since
                        && d.ReminderSentAt == null)
            .CountAsync();

        if (missed > 0)
            _logger.LogWarning(
                "Reminder ladder: {Count} {Kind} duty(ies) started in the last two hours with no reminder ever sent — " +
                "the sweep did not run before they began", missed, kind);
    }

    internal async Task<int> RotaStartAsync(DateTime now)
    {
        var horizon = now.AddDays(MaxLeadDays);
        var duties = await _context.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Include(d => d.Branch)
            .Where(d => d.IsActive && d.Kind == DutyKind.Rota && d.StartsAt > now && d.StartsAt <= horizon)
            .OrderBy(d => d.StartsAt)
            .ToListAsync();

        await LogMissedPreStartAsync(DutyKind.Rota, now);

        var sent = 0;
        foreach (var duty in duties)
        {
            try
            {
                if (!await ModuleActiveAsync(duty.OrganizationId)) continue;
                var policy = await PolicyAsync(duty.OrganizationId);
                var zone = AppointmentScheduling.ResolveTimeZone(duty.Branch?.Timezone);
                var acks = StaffPerformanceMapping.ParseAcknowledgements(duty.Acknowledgements);
                var waiting = (duty.ExpectedUserIds ?? Array.Empty<Guid>()).Where(id => !acks.ContainsKey(id)).Distinct().ToList();
                // Everyone has acknowledged: the ladder has stopped for the whole slot. The stage is left where it is,
                // so a person added later still starts from the right stage rather than the first.
                if (waiting.Count == 0) continue;

                var stage = _ladders.DueStage(_policy.LadderFor(policy, ReminderSubject.RotaStart), duty.StartsAt, duty.ReminderStage, now, zone, policy.QuietHours);
                if (stage == null) continue;

                var claimed = await _context.StaffDuties.IgnoreQueryFilters()
                    .Where(d => d.Id == duty.Id && d.ReminderStage < stage.Stage && d.StartsAt == duty.StartsAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReminderStage, stage.Stage).SetProperty(d => d.ReminderSentAt, now));
                if (claimed == 0) continue;
                sent++;

                var channels = _ladders.ChannelsFor(stage);
                if (channels == NotificationChannel.None) continue; // stage 1: the digest carries "on duty next week"

                var local = TimeZoneInfo.ConvertTimeFromUtc(duty.StartsAt, zone);
                var localEnd = TimeZoneInfo.ConvertTimeFromUtc(duty.EndsAt, zone);
                var span = local.Date == localEnd.Date
                    ? string.Create(CultureInfo.InvariantCulture, $"{local:ddd dd MMM} from {local:HH:mm}")
                    : string.Create(CultureInfo.InvariantCulture, $"{local:ddd dd MMM} {local:HH:mm} to {localEnd:ddd dd MMM}");
                foreach (var userId in waiting)
                    await SafeSendAsync(new CreateNotificationRequest
                    {
                        UserId = userId,
                        OrganizationId = duty.OrganizationId,
                        BranchId = duty.BranchId,
                        Title = $"On duty: {duty.Title}",
                        Message = $"{span}. Open your portal and acknowledge it.",
                        Type = NotificationType.StaffPerformance,
                        Priority = stage.Interruptive ? NotificationPriority.High : NotificationPriority.Normal,
                        Channels = channels,
                        EventKey = NotificationEventKeys.StaffRotaReminder,
                        ActionUrl = "/portal#on-duty",
                        IconClass = "shield-check"
                    }, duty.Id);

                if (stage.Audience.HasFlag(ReminderAudience.Supervisor))
                    foreach (var supervisor in duty.SupervisorUserIds.Distinct())
                        await SafeSendAsync(new CreateNotificationRequest
                        {
                            UserId = supervisor,
                            OrganizationId = duty.OrganizationId,
                            BranchId = duty.BranchId,
                            Title = $"Not yet acknowledged: {duty.Title}",
                            // A count, never names: the portal to-do names them behind the login (plan §8.3).
                            Message = string.Create(CultureInfo.InvariantCulture, $"{waiting.Count} {(waiting.Count == 1 ? "person has" : "people have")} not acknowledged the duty starting {local:ddd dd MMM HH:mm}."),
                            Type = NotificationType.StaffPerformance,
                            Priority = NotificationPriority.Normal,
                            Channels = channels & ~NotificationChannel.Sms,
                            EventKey = NotificationEventKeys.StaffRotaUnacknowledged,
                            ActionUrl = "/portal",
                            IconClass = "person-exclamation"
                        }, duty.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rota reminder failed for duty {DutyId}", duty.Id);
            }
        }
        return sent;
    }

    // ---- Duty reports due and overdue (plan §4.4) -------------------------------------------------------

    /// <summary>
    /// First makes sure a row stands for every started report period of every rota slot under way or recently over, then
    /// walks the unwritten ones (Draft, Returned) whose due time has come. The stage is claimed on the REPORT row. Stage 1
    /// reaches the author; a stage with the Supervisor audience also tells the slot's administrators on duty; one with the
    /// Heads audience tells the review-permission holders. Every message names the slot and the period — never content.
    /// Submitting, a return being answered, or "no duty that day" stops it. A person swapped off the slot is not chased.
    /// </summary>
    internal async Task<int> ReportDueAsync(DateTime now)
    {
        var since = now.AddDays(-31);
        var slots = await _context.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Include(d => d.Branch)
            .Where(d => d.IsActive && d.Kind == DutyKind.Rota && d.ReportCadence != ReportCadence.None && d.StartsAt <= now && d.EndsAt >= since)
            .ToListAsync();

        foreach (var slot in slots)
        {
            try
            {
                if (!await ModuleActiveAsync(slot.OrganizationId)) continue;
                await StaffDutyReports.EnsureRowsAsync(_context, slot, await PolicyAsync(slot.OrganizationId), AppointmentScheduling.ResolveTimeZone(slot.Branch?.Timezone), now, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Duty report rows could not be ensured for duty {DutyId}", slot.Id);
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }

        var reports = await _context.StaffDutyReports.IgnoreQueryFilters().AsNoTracking()
            .Include(r => r.Duty).ThenInclude(d => d!.Branch)
            .Where(r => (r.Status == DutyReportStatus.Draft || r.Status == DutyReportStatus.Returned) && r.DueAt <= now && r.DueAt >= since && r.Duty!.IsActive)
            .OrderBy(r => r.DueAt)
            .Take(2000)
            .ToListAsync();

        var sent = 0;
        foreach (var report in reports)
        {
            try
            {
                var duty = report.Duty!;
                if (!await ModuleActiveAsync(report.OrganizationId)) continue;
                var stillOnSlot = (duty.ExpectedUserIds ?? Array.Empty<Guid>()).Contains(report.AuthorUserId) || duty.SupervisorUserIds.Contains(report.AuthorUserId);
                if (!stillOnSlot) continue;

                var policy = await PolicyAsync(report.OrganizationId);
                var zone = AppointmentScheduling.ResolveTimeZone(duty.Branch?.Timezone);
                var stage = _ladders.DueStage(_policy.LadderFor(policy, ReminderSubject.ReportDue), report.DueAt, report.ReminderStage, now, zone, policy.QuietHours);
                if (stage == null) continue;

                var claimed = await _context.StaffDutyReports.IgnoreQueryFilters()
                    .Where(r => r.Id == report.Id && r.ReminderStage < stage.Stage && (r.Status == DutyReportStatus.Draft || r.Status == DutyReportStatus.Returned))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReminderStage, stage.Stage).SetProperty(r => r.LastReminderAt, now));
                if (claimed == 0) continue;
                sent++;

                var channels = _ladders.ChannelsFor(stage);
                if (channels == NotificationChannel.None) continue;

                var period = StaffDutyReports.PeriodText(report.PeriodStart, report.PeriodEnd);
                var first = stage.Stage == 1 && stage.OffsetMinutes <= 0;
                await SafeSendAsync(new CreateNotificationRequest
                {
                    UserId = report.AuthorUserId,
                    OrganizationId = report.OrganizationId,
                    BranchId = report.BranchId,
                    Title = first ? $"Duty report due: {duty.Title}" : $"Duty report overdue: {duty.Title}",
                    Message = first ? $"Your report for {period} is due now." : $"Your report for {period} has not been submitted.",
                    Type = NotificationType.StaffPerformance,
                    Priority = first ? NotificationPriority.Normal : NotificationPriority.High,
                    Channels = channels & ~NotificationChannel.Sms,
                    EventKey = first ? NotificationEventKeys.StaffDutyReportDue : NotificationEventKeys.StaffDutyReportOverdue,
                    ActionUrl = $"/admin/staff/duty-reports/{report.Id}",
                    IconClass = "journal-x"
                }, report.Id);

                var escalate = new List<Guid>();
                if (stage.Audience.HasFlag(ReminderAudience.Supervisor)) escalate.AddRange(duty.SupervisorUserIds);
                if (stage.Audience.HasFlag(ReminderAudience.Heads))
                    escalate.AddRange(await StaffLookups.UsersWithPermissionAsync(_context, report.OrganizationId, Permissions.StaffDutyReportsReview));
                foreach (var userId in escalate.Where(u => u != report.AuthorUserId).Distinct())
                    await SafeSendAsync(new CreateNotificationRequest
                    {
                        UserId = userId,
                        OrganizationId = report.OrganizationId,
                        BranchId = report.BranchId,
                        Title = $"Duty report overdue: {duty.Title}",
                        Message = $"A report for {period} has not been submitted.",
                        Type = NotificationType.StaffPerformance,
                        Priority = NotificationPriority.Normal,
                        Channels = channels & ~NotificationChannel.Sms,
                        EventKey = NotificationEventKeys.StaffDutyReportOverdue,
                        ActionUrl = "/admin/staff/duties?tab=reports",
                        IconClass = "journal-x"
                    }, report.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Duty report reminder failed for report {ReportId}", report.Id);
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

    /// <summary>
    /// Action points out of a meeting's minutes (2026-09-20). Same shape as every other ladder here:
    /// the stage is CLAIMED with a conditional update on the row before anything is sent, so two
    /// workers cannot both send stage 2. That claim is the whole reason an action is a row rather
    /// than a line in the minutes blob.
    ///
    /// Only actions with a DATE and a PERSON are chased. An action minuted for a body rather than a
    /// name is recorded and not chased, and one with no date was never given a deadline — inventing
    /// one and then nagging about it would be the system making up the minutes.
    /// </summary>
    private async Task<int> MinuteActionAsync(DateTime now)
    {
        var since = now.AddDays(-MaxLeadDays);
        var actions = await _context.StaffMinuteActions.IgnoreQueryFilters().AsNoTracking()
            .Include(a => a.Duty)!.ThenInclude(d => d!.Branch)
            .Where(a => a.Status == MinuteActionStatus.Open
                        && a.AssignedUserId != null
                        && a.DueAt != null && a.DueAt > since && a.DueAt < now.AddDays(MaxLeadDays))
            .Take(500)
            .ToListAsync();

        var sent = 0;
        foreach (var action in actions)
        {
            try
            {
                if (!await ModuleActiveAsync(action.OrganizationId)) continue;
                var policy = await PolicyAsync(action.OrganizationId);
                var zone = AppointmentScheduling.ResolveTimeZone(action.Duty?.Branch?.Timezone);
                var stage = _ladders.DueStage(_policy.LadderFor(policy, ReminderSubject.MinuteActionDue),
                    action.DueAt!.Value, action.ReminderStage, now, zone, policy.QuietHours);
                if (stage == null) continue;

                var previous = action.ReminderStage;
                var claimed = await _context.StaffMinuteActions.IgnoreQueryFilters()
                    .Where(a => a.Id == action.Id && a.Status == MinuteActionStatus.Open && a.ReminderStage == previous)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReminderStage, stage.Stage));
                if (claimed == 0) continue;
                sent++;

                var channels = _ladders.ChannelsFor(stage);
                if (channels == NotificationChannel.None) continue;

                var overdue = action.DueAt.Value < now;
                await SafeSendAsync(new CreateNotificationRequest
                {
                    UserId = action.AssignedUserId!.Value,
                    OrganizationId = action.OrganizationId,
                    BranchId = action.BranchId,
                    Title = overdue ? "An action from the minutes is overdue" : "An action from the minutes is due",
                    Message = $"\"{Truncate(action.Text)}\" — from the minutes of \"{action.Duty?.Title ?? "a meeting"}\".",
                    Type = NotificationType.StaffPerformance,
                    Priority = overdue ? NotificationPriority.High : NotificationPriority.Normal,
                    Channels = channels,
                    EventKey = NotificationEventKeys.StaffMinuteAction,
                    ActionUrl = $"/admin/staff/duties/{action.DutyId}/minutes",
                    IconClass = "list-check"
                }, action.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Minute-action reminder failed for action {ActionId}", action.Id);
            }
        }
        return sent;
    }

    private static string Truncate(string s) => s.Length <= 90 ? s : s[..87] + "…";

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

    // ---- Lessons: about to start (plan §7.2) ---------------------------------------------------------------

    /// <summary>
    /// One in-app, time-sensitive reminder before each lesson at the policy's minutes (decision 8: 10, bell only). The
    /// stage is claimed on the lesson row. The person's own preferences decide email and SMS, which default to off.
    /// </summary>
    internal async Task<int> LessonStartAsync(DateTime now)
    {
        var horizon = now.AddHours(3);
        var lessons = await _context.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Include(d => d.Branch)
            .Where(d => d.IsActive && d.Kind == DutyKind.Lesson && d.StartsAt > now && d.StartsAt <= horizon && d.ExpectedUserIds != null)
            .OrderBy(d => d.StartsAt)
            .Take(3000)
            .ToListAsync();

        await LogMissedPreStartAsync(DutyKind.Lesson, now);

        var sent = 0;
        foreach (var lesson in lessons)
        {
            try
            {
                if (lesson.ExpectedUserIds!.Length == 0 || !await ModuleActiveAsync(lesson.OrganizationId)) continue;
                var policy = await PolicyAsync(lesson.OrganizationId);
                var zone = AppointmentScheduling.ResolveTimeZone(lesson.Branch?.Timezone);
                var stage = _ladders.DueStage(_policy.LadderFor(policy, ReminderSubject.LessonStart), lesson.StartsAt, lesson.ReminderStage, now, zone, policy.QuietHours);
                if (stage == null) continue;

                var claimed = await _context.StaffDuties.IgnoreQueryFilters()
                    .Where(d => d.Id == lesson.Id && d.ReminderStage < stage.Stage && d.StartsAt == lesson.StartsAt && d.IsActive)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReminderStage, stage.Stage).SetProperty(d => d.ReminderSentAt, now));
                if (claimed == 0) continue;
                sent++;

                var channels = _ladders.ChannelsFor(stage);
                if (channels == NotificationChannel.None) continue;
                var local = TimeZoneInfo.ConvertTimeFromUtc(lesson.StartsAt, zone);
                await SafeSendAsync(new CreateNotificationRequest
                {
                    UserId = lesson.ExpectedUserIds[0],
                    OrganizationId = lesson.OrganizationId,
                    BranchId = lesson.BranchId,
                    Title = string.Create(CultureInfo.InvariantCulture, $"{lesson.Title} at {local:HH:mm}"),
                    Message = string.IsNullOrWhiteSpace(lesson.Room) ? "Your next lesson." : $"Your next lesson, in {lesson.Room}.",
                    Type = NotificationType.StaffPerformance,
                    Priority = stage.Interruptive ? NotificationPriority.High : NotificationPriority.Normal,
                    Channels = channels,
                    EventKey = NotificationEventKeys.StaffLessonReminder,
                    ActionUrl = "/my-day",
                    IconClass = "journal-bookmark"
                }, lesson.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lesson reminder failed for duty {DutyId}", lesson.Id);
            }
        }
        return sent;
    }

    // ---- My Day: the morning digest (plan §7.2) -----------------------------------------------------------

    /// <summary>
    /// One message a morning per person with anything on — "Today: 6 lessons, on duty, 1 report due" — at the policy's
    /// My Day time, branch-local, never a message per item (NN/g's batching rule). Earlier lessons still unrecorded ride
    /// along as one line (plan §7.3's chase). Sent within four hours of the time, so a sweep that was down at 06:30 still
    /// sends at 07:00 but nobody is told "today" at teatime. Once a day: the job runs one sweep at a time, and a person
    /// who already has today's digest is skipped.
    /// </summary>
    internal async Task<int> MyDayAsync(DateTime now)
    {
        var from = now.AddDays(-2);
        var to = now.AddDays(2);
        var branches = await _context.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.IsActive && d.StartsAt < to && d.EndsAt > from)
            .Select(d => new { d.OrganizationId, d.BranchId }).Distinct().ToListAsync();

        var sent = 0;
        foreach (var b in branches)
        {
            try
            {
                if (!await ModuleActiveAsync(b.OrganizationId)) continue;
                var policy = await PolicyAsync(b.OrganizationId);
                var branch = await _context.Branches.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == b.BranchId).Select(x => new { x.Timezone, x.IsActive }).FirstOrDefaultAsync();
                if (branch == null || !branch.IsActive) continue;
                var zone = AppointmentScheduling.ResolveTimeZone(branch.Timezone);
                var local = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
                var at = (TimeOnly.TryParseExact(policy.MyDayLocalTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : new TimeOnly(6, 30)).ToTimeSpan();
                if (local.TimeOfDay < at || local.TimeOfDay > at.Add(TimeSpan.FromHours(4))) continue;

                var day = DateOnly.FromDateTime(local);
                var dayStart = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone);
                var dayEnd = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);

                var todays = await _context.StaffDuties.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.BranchId == b.BranchId && d.IsActive && d.StartsAt < dayEnd && d.EndsAt > dayStart)
                    .Select(d => new { d.Kind, d.StartsAt, d.ExpectedUserIds, d.RecorderUserIds, d.SupervisorUserIds })
                    .ToListAsync();
                var window = now.AddDays(-Math.Max(policy.UnrecordedLessonWindowDays, 1) - 7);
                var unrecorded = await _context.StaffDuties.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.BranchId == b.BranchId && d.IsActive && d.Kind == DutyKind.Lesson && d.EndsAt < dayStart && d.StartsAt >= window && d.ExpectedUserIds != null
                                && !_context.StaffPerformanceRecords.Any(r => r.DutyId == d.Id && r.Status == StaffRecordStatus.Final))
                    .Select(d => d.ExpectedUserIds!).ToListAsync();
                var reports = await _context.StaffDutyReports.IgnoreQueryFilters().AsNoTracking()
                    .Where(r => r.BranchId == b.BranchId && (r.Status == DutyReportStatus.Draft || r.Status == DutyReportStatus.Returned)
                                && r.DueAt < dayEnd && r.DueAt >= now.AddDays(-31) && r.Duty!.IsActive)
                    .Select(r => r.AuthorUserId).ToListAsync();

                var people = new Dictionary<Guid, (int Lessons, int Sessions, bool OnDuty, bool Supervising, int Reports, int Unrecorded)>();
                void Bump(Guid id, Func<(int Lessons, int Sessions, bool OnDuty, bool Supervising, int Reports, int Unrecorded), (int, int, bool, bool, int, int)> f)
                    => people[id] = f(people.TryGetValue(id, out var v) ? v : default);

                foreach (var d in todays)
                {
                    var expected = d.ExpectedUserIds ?? Array.Empty<Guid>();
                    switch (d.Kind)
                    {
                        case DutyKind.Lesson:
                            foreach (var id in expected) Bump(id, v => (v.Lessons + 1, v.Sessions, v.OnDuty, v.Supervising, v.Reports, v.Unrecorded));
                            break;
                        case DutyKind.Rota:
                            foreach (var id in expected) Bump(id, v => (v.Lessons, v.Sessions, true, v.Supervising, v.Reports, v.Unrecorded));
                            foreach (var id in d.SupervisorUserIds) Bump(id, v => (v.Lessons, v.Sessions, v.OnDuty, true, v.Reports, v.Unrecorded));
                            break;
                        default:
                            // A session for "everyone" (no expected list) is not on anyone's personal digest.
                            foreach (var id in expected.Concat(d.RecorderUserIds).Distinct()) Bump(id, v => (v.Lessons, v.Sessions + 1, v.OnDuty, v.Supervising, v.Reports, v.Unrecorded));
                            break;
                    }
                }
                foreach (var ids in unrecorded) foreach (var id in ids) Bump(id, v => (v.Lessons, v.Sessions, v.OnDuty, v.Supervising, v.Reports, v.Unrecorded + 1));
                foreach (var id in reports) Bump(id, v => (v.Lessons, v.Sessions, v.OnDuty, v.Supervising, v.Reports + 1, v.Unrecorded));
                if (people.Count == 0) continue;

                var userIds = people.Keys.ToList();
                var already = (await _context.Notifications.IgnoreQueryFilters().AsNoTracking()
                    .Where(n => n.UserId != null && userIds.Contains(n.UserId.Value) && n.EventKey == NotificationEventKeys.StaffMyDay && n.CreatedAt >= dayStart)
                    .Select(n => n.UserId!.Value).ToListAsync()).ToHashSet();
                var active = (await StaffLookups.BranchStaff(_context, b.OrganizationId, b.BranchId).Where(u => userIds.Contains(u.Id)).Select(u => u.Id).ToListAsync()).ToHashSet();

                foreach (var (userId, v) in people)
                {
                    if (already.Contains(userId) || !active.Contains(userId)) continue;
                    var parts = new List<string>();
                    if (v.Lessons > 0) parts.Add(v.Lessons == 1 ? "1 lesson" : $"{v.Lessons} lessons");
                    if (v.Sessions > 0) parts.Add(v.Sessions == 1 ? "1 meeting or session" : $"{v.Sessions} meetings or sessions");
                    if (v.OnDuty) parts.Add("on duty");
                    if (v.Supervising) parts.Add("supervising the duty");
                    if (v.Reports > 0) parts.Add(v.Reports == 1 ? "1 report due" : $"{v.Reports} reports due");
                    var title = parts.Count > 0 ? $"Today: {string.Join(", ", parts)}" : "Today";
                    var message = v.Unrecorded > 0
                        ? $"{(v.Unrecorded == 1 ? "1 earlier lesson is" : $"{v.Unrecorded} earlier lessons are")} still unrecorded. Open My School Day for the times, rooms and to mark them."
                        : "Open My School Day for the times and rooms.";
                    await SafeSendAsync(new CreateNotificationRequest
                    {
                        UserId = userId,
                        OrganizationId = b.OrganizationId,
                        BranchId = b.BranchId,
                        Title = title,
                        Message = message,
                        Type = NotificationType.StaffPerformance,
                        Priority = NotificationPriority.Low,
                        Channels = NotificationChannel.InApp | NotificationChannel.Email,
                        EventKey = NotificationEventKeys.StaffMyDay,
                        ActionUrl = "/my-day",
                        IconClass = "sunrise"
                    }, userId);
                    sent++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "My Day digest failed for branch {BranchId}", b.BranchId);
            }
        }
        return sent;
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
