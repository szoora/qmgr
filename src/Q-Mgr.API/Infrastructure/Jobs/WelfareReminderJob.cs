using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

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
    private readonly ILogger<WelfareReminderJob> _logger;

    public WelfareReminderJob(QMgrDbContext context, INotificationService notificationService, ILogger<WelfareReminderJob> logger)
    {
        _context = context;
        _notificationService = notificationService;
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
                    Channels = NotificationChannel.InApp,
                    ActionUrl = $"/admin/students/{record.StudentId}/welfare",
                    IconClass = "clock-history"
                });

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
    }
}
