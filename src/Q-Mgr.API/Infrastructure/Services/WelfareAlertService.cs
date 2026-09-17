using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Tells a class teacher when a student in one of their classes has a case logged.
///
/// The whole point of the feature, and also the part most likely to do harm if it gets the
/// confidentiality rule wrong — so that rule is stated once, here, and nowhere else:
///
///   A NON-STANDARD RECORD ALERTS NOBODY THROUGH THIS PATH.
///
/// A Confidential (safeguarding) or Restricted record reaches the DSL/administrator through the
/// ledger itself and leaves no trace on a class teacher's bell, inbox or timeline. That is the
/// user's explicit decision (2026-09-09) and the defensible reading of KCSIE's need-to-know
/// principle: a form tutor holding safeguarding detail by default is precisely what the guidance
/// rules out. It is also why the suppression is a hard early return rather than a filter somewhere
/// downstream — there is exactly one line to read to know whether this can leak.
/// </summary>
public interface IWelfareAlertService
{
    /// <summary>
    /// Fan out the "new record" alert for a record that has just become real (created finalized, or
    /// finalized from a draft).
    ///
    /// NEVER THROWS. The record is already committed by the time this runs, and this project has a
    /// standing rule — written after ProtectSystem=strict made every walk-in check-in return 500
    /// AFTER the visit was on the books — that a side effect running after a committed transaction
    /// must not be able to fail the request. A teacher who was not emailed is a degraded success;
    /// a teacher being told their incident log failed when it did not is the worst possible answer.
    /// </summary>
    Task NotifyRecordLoggedAsync(Guid recordId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The live class teachers for a student, primary and assistants, excluding the given user IDs.
    /// Exposed because the overdue-reminder sweeps need the same recipient list and must not grow
    /// a second copy of the resolution rule.
    /// </summary>
    Task<List<ClassTeacherRecipient>> GetClassTeachersForStudentAsync(Guid studentId, IEnumerable<Guid>? exclude = null, CancellationToken cancellationToken = default);
}

public record ClassTeacherRecipient(Guid UserId, string FullName, string ClassName, ClassTeacherRole Role);

public class WelfareAlertService : IWelfareAlertService
{
    private readonly QMgrDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly ILogger<WelfareAlertService> _logger;

    public WelfareAlertService(
        QMgrDbContext context,
        INotificationService notificationService,
        ILogger<WelfareAlertService> logger)
    {
        _context = context;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<List<ClassTeacherRecipient>> GetClassTeachersForStudentAsync(
        Guid studentId, IEnumerable<Guid>? exclude = null, CancellationToken cancellationToken = default)
    {
        var student = await _context.Students
            .AsNoTracking()
            .Where(s => s.Id == studentId)
            .Select(s => new { s.BranchId, s.ClassName })
            .FirstOrDefaultAsync(cancellationToken);

        if (student == null || string.IsNullOrWhiteSpace(student.ClassName))
            return new List<ClassTeacherRecipient>();

        // Same normalization rule as ClassTeachersController and StudentScopeService: Student.ClassName
        // is free text and the vocabulary is user-typed, so "S4B" and "s4b " are one class. A student
        // silently unreachable by their own class teacher because of a stray space is a safeguarding
        // failure, which is why the coverage endpoint surfaces class names that match nothing.
        var key = student.ClassName.Trim().ToLowerInvariant();
        var excluded = (exclude ?? Enumerable.Empty<Guid>()).ToHashSet();

        var rows = await _context.ClassTeacherAssignments
            .AsNoTracking()
            .Where(a => a.BranchId == student.BranchId
                        && a.EndedAt == null
                        // PASTORAL ONLY (duty rota plan §5.3): a subject teacher is never alerted about a
                        // child's welfare. Named roles, not "not SubjectTeacher", so a role appended later
                        // is excluded until somebody decides it should be told.
                        && (a.Role == ClassTeacherRole.ClassTeacher || a.Role == ClassTeacherRole.Assistant)
                        && a.ClassName.Trim().ToLower() == key)
            .Select(a => new
            {
                a.UserId,
                a.ClassName,
                a.Role,
                UserActive = a.User!.IsActive,
                a.User.FirstName,
                a.User.LastName
            })
            .ToListAsync(cancellationToken);

        return rows
            // A deactivated account cannot read the notification, and mailing a departed member of
            // staff about a child is its own disclosure. The coverage report is where a class left
            // uncovered this way becomes visible.
            .Where(r => r.UserActive && !excluded.Contains(r.UserId))
            .Select(r => new ClassTeacherRecipient(
                r.UserId,
                $"{r.FirstName} {r.LastName}".Trim(),
                r.ClassName,
                r.Role))
            .DistinctBy(r => r.UserId)
            .ToList();
    }

    public async Task NotifyRecordLoggedAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await _context.WelfareRecords
                .AsNoTracking()
                .Include(r => r.Student)
                .Include(r => r.Category)
                .FirstOrDefaultAsync(r => r.Id == recordId, cancellationToken);

            if (record == null) return;

            // ── The confidentiality rule. One line, one place. ──────────────────────────────
            if (record.Visibility != WelfareVisibility.Standard)
            {
                _logger.LogDebug(
                    "Welfare record {RecordId} is {Visibility}; no class-teacher alert by design",
                    recordId, record.Visibility);
                return;
            }

            // A draft is visible only to its author and is not yet a record of anything.
            if (record.Status == WelfareStatus.Draft) return;

            // Never notify the person who just wrote it, and never notify the assignee twice —
            // they already get their own assignment/reminder notifications.
            var exclude = new List<Guid> { record.ReportedByUserId };
            if (record.AssignedToUserId.HasValue) exclude.Add(record.AssignedToUserId.Value);

            // Every student the record touches, not only the one it was primarily filed against —
            // a fight involving three classes should reach all three class teachers.
            var studentIds = new List<Guid> { record.StudentId };
            if (record.AdditionalStudentIds != null) studentIds.AddRange(record.AdditionalStudentIds);

            var recipients = new Dictionary<Guid, ClassTeacherRecipient>();
            foreach (var studentId in studentIds.Distinct())
            {
                foreach (var r in await GetClassTeachersForStudentAsync(studentId, exclude, cancellationToken))
                    recipients.TryAdd(r.UserId, r);
            }

            if (recipients.Count == 0)
            {
                _logger.LogDebug("Welfare record {RecordId}: no live class teacher for the student's class", recordId);
                return;
            }

            var studentName = record.Student?.FullName ?? "A student";
            var className = record.Student?.ClassName;
            var categoryName = record.Category?.Name ?? record.CaseType.ToString();

            var reporterName = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == record.ReportedByUserId)
                .Select(u => (u.FirstName + " " + u.LastName).Trim())
                .FirstOrDefaultAsync(cancellationToken);

            var title = string.IsNullOrWhiteSpace(className)
                ? $"New {record.CaseType.ToString().ToLowerInvariant()} record"
                : $"New {record.CaseType.ToString().ToLowerInvariant()} record — {className}";

            var message = $"{categoryName} logged for {studentName}" +
                          (string.IsNullOrWhiteSpace(reporterName) ? "." : $" by {reporterName}.");

            foreach (var recipient in recipients.Values)
            {
                try
                {
                    await _notificationService.CreateInAppNotificationAsync(new CreateNotificationRequest
                    {
                        UserId = recipient.UserId,
                        OrganizationId = record.OrganizationId,
                        BranchId = record.BranchId,
                        Title = title,
                        Message = message,
                        Type = NotificationType.SystemAlert,
                        // A High-tier record is the one a class teacher should see before they next
                        // walk into that room; Low/Medium can wait for them to look.
                        Priority = record.Tier == WelfareTier.High ? NotificationPriority.High : NotificationPriority.Normal,
                        // InApp is the instant bell. Email/SMS are decided per recipient by
                        // NotificationService from their preferences and the org defaults — asking
                        // for them here does not force them on anyone who has opted out.
                        Channels = NotificationChannel.InApp | NotificationChannel.Email | NotificationChannel.Sms,
                        EventKey = NotificationEventKeys.WelfareRecordLogged,
                        ActionUrl = $"/admin/students/{record.StudentId}/welfare",
                        IconClass = "journal-text"
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    // One bad recipient must not cost the others their alert.
                    _logger.LogError(ex, "Failed to alert class teacher {UserId} about welfare record {RecordId}", recipient.UserId, recordId);
                }
            }

            _logger.LogInformation("Welfare record {RecordId}: alerted {Count} class teacher(s)", recordId, recipients.Count);
        }
        catch (Exception ex)
        {
            // NEVER THROWS — see the interface doc. The record is already committed; a failed
            // alert is a degraded success, not a failed request.
            _logger.LogError(ex, "Class-teacher alert fan-out failed for welfare record {RecordId}", recordId);
        }
    }
}
