using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// The staff-facing half of the welfare-action workflow that the welfare-plan's Phase 2 named but
/// never scheduled: CreateRecord/UpdateAction already send an immediate in-app notification the
/// moment a follow-up is assigned, but nothing ever chased it back up if the assignee let the due
/// date pass. This sweeps every open, assigned, overdue WelfareRecord and pushes a reminder through
/// the same in-app notification hub other real-time events already use — no new "reminder" table,
/// per the same reasoning WelfareRecord.ReminderSentAt's own doc comment gives.
/// </summary>
public class WelfareReminderJob
{
    private readonly QMgrDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly IWelfareAlertService _alerts;
    private readonly ILogger<WelfareReminderJob> _logger;

    public WelfareReminderJob(
        QMgrDbContext context,
        INotificationService notificationService,
        IWelfareAlertService alerts,
        ILogger<WelfareReminderJob> logger)
    {
        _context = context;
        _notificationService = notificationService;
        _alerts = alerts;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3)]
    public async Task SendOverdueActionRemindersAsync()
    {
        var now = DateTime.UtcNow;
        var renotifyBefore = now.AddDays(-1);

        // Same "open, assigned, past its due date" definition GetSummary's OverdueActionsCount
        // already uses, plus excluding Draft (a draft is only visible to its own author — it can't
        // carry a real assignment yet) and requiring AssignedToUserId (nobody to remind otherwise).
        var overdue = await _context.WelfareRecords
            .Include(r => r.Student)
            .Include(r => r.Category)
            .Where(r => r.Status != WelfareStatus.Resolved
                        && r.Status != WelfareStatus.Draft
                        && r.AssignedToUserId != null
                        && r.ActionDueDate != null
                        && r.ActionDueDate < now
                        && (r.ReminderSentAt == null || r.ReminderSentAt < renotifyBefore))
            .ToListAsync();

        var sent = 0;
        foreach (var record in overdue)
        {
            try
            {
                var studentName = record.Student?.FullName ?? "a student";
                var categoryName = record.Category?.Name ?? "a welfare record";
                var daysOverdue = (int)Math.Ceiling((now - record.ActionDueDate!.Value).TotalDays);

                await _notificationService.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = record.AssignedToUserId,
                    OrganizationId = record.OrganizationId,
                    BranchId = record.BranchId,
                    Title = "Overdue welfare follow-up",
                    Message = $"The follow-up for {studentName} ({categoryName}) was due {daysOverdue} day{(daysOverdue == 1 ? "" : "s")} ago and is still marked \"{record.Status}\".",
                    Type = NotificationType.SystemAlert,
                    Priority = NotificationPriority.High,
                    // Email as well as the bell now: an overdue follow-up that only exists inside
                    // the app is one the assignee sees when they next happen to log in, which for
                    // a part-time member of staff can be days. The recipient can still turn email
                    // off for this category — that is what the event key is for.
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    EventKey = NotificationEventKeys.WelfareActionOverdue,
                    ActionUrl = $"/admin/students/{record.StudentId}/welfare",
                    IconClass = "clock-history"
                });

                // The class teacher too, for a Standard record about one of their students — an
                // overdue follow-up on a child in your form is your business even when somebody
                // else owns the action. Suppressed for Confidential/Restricted inside the alert
                // service, and the assignee is excluded so nobody is told twice.
                if (record.Visibility == WelfareVisibility.Standard)
                {
                    var exclude = new[] { record.AssignedToUserId!.Value, record.ReportedByUserId };
                    foreach (var teacher in await _alerts.GetClassTeachersForStudentAsync(record.StudentId, exclude))
                    {
                        await _notificationService.CreateInAppNotificationAsync(new CreateNotificationRequest
                        {
                            UserId = teacher.UserId,
                            OrganizationId = record.OrganizationId,
                            BranchId = record.BranchId,
                            Title = $"Overdue follow-up — {teacher.ClassName}",
                            Message = $"The follow-up for {studentName} ({categoryName}) was due {daysOverdue} day{(daysOverdue == 1 ? "" : "s")} ago and is still open.",
                            Type = NotificationType.SystemAlert,
                            Priority = NotificationPriority.Normal,
                            Channels = NotificationChannel.InApp | NotificationChannel.Email,
                            EventKey = NotificationEventKeys.WelfareActionOverdue,
                            ActionUrl = $"/admin/students/{record.StudentId}/welfare",
                            IconClass = "clock-history"
                        });
                    }
                }

                record.ReminderSentAt = now;
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send overdue-action reminder for welfare record {RecordId}", record.Id);
            }
        }

        if (sent > 0)
            await _context.SaveChangesAsync();

        _logger.LogInformation("Welfare overdue-action reminder sweep: {Sent} reminder(s) sent out of {Total} overdue record(s)", sent, overdue.Count);
    }

    /// <summary>
    /// The same sweep, for standing flags rather than one-off actions. A flag nobody has revisited
    /// is worse than no flag at all, because it reads as current knowledge — "child protection
    /// plan" raised two years ago and never reviewed will still be shaping how staff treat a child
    /// long after the situation changed.
    ///
    /// Deliberately part of this job rather than a new one: it is the identical "somebody must
    /// look at this again" mechanic, down to the 24-hour re-notification gate, and a second
    /// Hangfire job doing the same thing to a different table is two schedules to keep in step.
    ///
    /// The reminder goes to whoever raised the flag, since there is no assignee on a flag — the
    /// person who knew enough to raise it is the person who can judge whether it still applies.
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task SendOverdueFlagReviewRemindersAsync()
    {
        var now = DateTime.UtcNow;
        var renotifyBefore = now.AddDays(-1);

        var overdue = await _context.StudentFlags
            .Include(f => f.Student)
            .Include(f => f.Category)
            .Where(f => f.EndedAt == null
                        && f.ReviewDueDate != null
                        && f.ReviewDueDate < now
                        && f.RaisedByUserId != Guid.Empty
                        && (f.ReminderSentAt == null || f.ReminderSentAt < renotifyBefore))
            .ToListAsync();

        var sent = 0;
        foreach (var flag in overdue)
        {
            try
            {
                var studentName = flag.Student?.FullName ?? "a student";
                var flagName = flag.Category?.Name ?? "a flag";
                var daysOverdue = (int)Math.Ceiling((now - flag.ReviewDueDate!.Value).TotalDays);

                await _notificationService.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = flag.RaisedByUserId,
                    OrganizationId = flag.OrganizationId,
                    BranchId = flag.BranchId,
                    Title = "Flag review overdue",
                    Message = $"The \"{flagName}\" flag on {studentName} was due for review {daysOverdue} day{(daysOverdue == 1 ? "" : "s")} ago. Confirm it still applies, or end it.",
                    Type = NotificationType.SystemAlert,
                    Priority = NotificationPriority.High,
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    EventKey = NotificationEventKeys.WelfareFlagReviewOverdue,
                    ActionUrl = $"/admin/students/{flag.StudentId}/picture",
                    IconClass = "flag"
                });

                flag.ReminderSentAt = now;
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send flag-review reminder for student flag {FlagId}", flag.Id);
            }
        }

        if (sent > 0)
            await _context.SaveChangesAsync();

        _logger.LogInformation("Student-flag review reminder sweep: {Sent} reminder(s) sent out of {Total} overdue flag(s)", sent, overdue.Count);
    }
}

public static class WelfareReminderJobRegistration
{
    public static void RegisterRecurringJobs()
    {
        // Hourly — catches a record going overdue within an hour of its due date passing, while
        // ReminderSentAt's own 24h gate keeps a still-ignored assignment from paging its assignee
        // more than once a day.
        RecurringJob.AddOrUpdate<WelfareReminderJob>(
            "welfare-overdue-action-reminders",
            job => job.SendOverdueActionRemindersAsync(),
            Cron.Hourly);

        // Daily, not hourly: a flag review is due on a DATE, not at a moment, and chasing it
        // within the hour would wake somebody at 01:00 about a review that is one minute late.
        RecurringJob.AddOrUpdate<WelfareReminderJob>(
            "welfare-overdue-flag-reviews",
            job => job.SendOverdueFlagReviewRemindersAsync(),
            Cron.Daily(7));
    }
}
