using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Controllers.v1;
using QMgr.Application.Interfaces;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// The unattended half of scheduling: nudging customers before their appointment, and closing out
/// the ones who never turned up. Both sweeps are cross-tenant, which is why
/// <c>Appointment.OrganizationId</c> is denormalised onto the row — a background job has no
/// branch in hand and every notification channel is configured per organization.
/// <para>
/// No new "reminder" table: the single nullable <c>Appointment.ReminderSentAt</c> column carries
/// the whole "has this one been chased yet" fact, exactly as <c>WelfareRecord.ReminderSentAt</c>
/// does for the welfare sweep. Same reasoning as the project's enhance-before-you-add rule.
/// </para>
/// </summary>
public class AppointmentJobs
{
    /// <summary>
    /// How far ahead the reminder sweep looks. The per-branch lead time is read from each
    /// appointment's own branch settings inside the loop; this only has to be wide enough to
    /// contain the largest lead anyone is likely to configure, so a row is never missed.
    /// </summary>
    private const int ReminderScanHours = 72;

    /// <summary>
    /// How far back the no-show sweep looks. Anything older than this was either already resolved
    /// or belongs to a period nobody is going to reconcile now — sweeping the whole table every
    /// 15 minutes to find it would be pure cost.
    /// </summary>
    private const int NoShowScanDays = 7;

    private readonly QMgrDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly ILogger<AppointmentJobs> _logger;

    public AppointmentJobs(
        QMgrDbContext context,
        INotificationService notificationService,
        ILogger<AppointmentJobs> logger)
    {
        _context = context;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>
    /// Sends each upcoming appointment's reminder once, over SMS when a phone number is on file
    /// and email otherwise.
    /// <para>
    /// <c>ReminderSentAt</c> is stamped whether or not the channel accepted the message, and that
    /// is deliberate: an organization with no SMS gateway configured fails every send, and a
    /// retry-until-success rule would re-attempt those same rows on every run forever — while an
    /// organization whose gateway is merely flaky would re-send a duplicate reminder to a real
    /// customer's phone. Failing loudly in the log once beats either. A rescheduled appointment
    /// gets its ReminderSentAt cleared by the reschedule endpoint, so it is reminded about again
    /// at its new time.
    /// </para>
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task SendUpcomingRemindersAsync()
    {
        var now = DateTime.UtcNow;
        var horizon = now.AddHours(ReminderScanHours);

        var candidates = await _context.Appointments
            .Include(a => a.Branch)
            .Include(a => a.ServiceType)
            .Where(a => a.ReminderSentAt == null
                        && a.ScheduledAt > now
                        && a.ScheduledAt <= horizon
                        && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed))
            .OrderBy(a => a.ScheduledAt)
            .Take(500)
            .ToListAsync();

        var sent = 0;
        foreach (var appointment in candidates)
        {
            try
            {
                var settings = AppointmentScheduling.ReadSettings(appointment.Branch?.Settings);
                var dueAt = appointment.ScheduledAt.AddMinutes(-Math.Max(1, settings.ReminderLeadMinutes));
                if (now < dueAt)
                    continue; // Not yet inside this branch's reminder window.

                var timeZone = AppointmentScheduling.ResolveTimeZone(appointment.Branch?.Timezone);
                var local = TimeZoneInfo.ConvertTimeFromUtc(appointment.ScheduledAt, timeZone);
                var serviceName = appointment.ServiceType?.Name ?? "your appointment";
                var branchName = appointment.Branch?.Name ?? "our branch";

                var message =
                    $"Reminder: {serviceName} at {branchName} on {local:ddd d MMM} at {local:HH:mm}. " +
                    $"Reference {appointment.ReferenceCode}. Please arrive a few minutes early.";

                var delivered = false;

                if (!string.IsNullOrWhiteSpace(appointment.CustomerPhone))
                {
                    delivered = await _notificationService.SendSmsAsync(
                        appointment.OrganizationId, appointment.CustomerPhone, message);
                }

                if (!delivered && !string.IsNullOrWhiteSpace(appointment.CustomerEmail))
                {
                    delivered = await _notificationService.SendEmailAsync(
                        appointment.OrganizationId,
                        appointment.CustomerEmail,
                        $"Reminder: your appointment on {local:ddd d MMM}",
                        message,
                        isHtml: false);
                }

                if (!delivered)
                {
                    _logger.LogWarning(
                        "Appointment reminder for {AppointmentId} ({Reference}) could not be delivered — no working channel for organization {OrganizationId}",
                        appointment.Id, appointment.ReferenceCode, appointment.OrganizationId);
                }

                appointment.ReminderSentAt = now;
                appointment.UpdatedAt = now;
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send the reminder for appointment {AppointmentId}", appointment.Id);
            }
        }

        if (sent > 0)
            await _context.SaveChangesAsync();

        _logger.LogInformation("Appointment reminder sweep: {Sent} reminder(s) processed out of {Total} upcoming appointment(s)",
            sent, candidates.Count);
    }

    /// <summary>
    /// Closes out appointments whose end time plus the branch's grace period has passed with
    /// nobody checked in. Only Booked/Confirmed are touched — CheckedIn, Completed, Cancelled and
    /// an already-set NoShow are all terminal as far as this sweep is concerned.
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task MarkPastDueNoShowsAsync()
    {
        var now = DateTime.UtcNow;
        var floor = now.AddDays(-NoShowScanDays);

        var candidates = await _context.Appointments
            .Include(a => a.Branch)
            .Where(a => a.ScheduledAt >= floor
                        && a.ScheduledAt < now
                        && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed))
            .Take(1000)
            .ToListAsync();

        var marked = 0;
        foreach (var appointment in candidates)
        {
            var settings = AppointmentScheduling.ReadSettings(appointment.Branch?.Settings);
            var expiresAt = appointment.ScheduledAt
                .AddMinutes(appointment.DurationMinutes)
                .AddMinutes(Math.Max(0, settings.NoShowGraceMinutes));

            if (now < expiresAt)
                continue;

            appointment.Status = AppointmentStatus.NoShow;
            appointment.UpdatedAt = now;
            marked++;
        }

        if (marked > 0)
            await _context.SaveChangesAsync();

        _logger.LogInformation("Appointment no-show sweep: {Marked} appointment(s) marked as no-show", marked);
    }
}

public static class AppointmentJobsRegistration
{
    public static void RegisterRecurringJobs()
    {
        // Every 15 minutes. The reminder lead time is configurable per branch and can be as short
        // as a few minutes, so an hourly sweep would routinely fire a "one hour before" reminder
        // up to an hour late. ReminderSentAt makes a frequent sweep free — rows already handled
        // are filtered out in SQL.
        RecurringJob.AddOrUpdate<AppointmentJobs>(
            "appointment-reminders",
            job => job.SendUpcomingRemindersAsync(),
            "*/15 * * * *");

        // Same cadence: a no-show should be visible on the admin board within a quarter of an
        // hour of the grace period lapsing, not at the top of the next hour.
        RecurringJob.AddOrUpdate<AppointmentJobs>(
            "appointment-no-show-sweep",
            job => job.MarkPastDueNoShowsAsync(),
            "*/15 * * * *");
    }
}
