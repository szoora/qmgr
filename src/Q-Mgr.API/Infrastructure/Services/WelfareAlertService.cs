using QMgr.API.Application.Services;
using Microsoft.EntityFrameworkCore;
using QMgr.Application;
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
    /// The same fan-out for several records at once — a group log (plan STUDENT_ROSTER_AND_LIST_STANDARD §3,
    /// decision L5). Recipients are computed for each record exactly as for one, then COALESCED: each person
    /// receives ONE notification however many of the records reached them ("Achievement logged for 47
    /// students in S2A: Science fair merit"), never forty. A person reached by only one record gets the
    /// single-record message word for word — <see cref="NotifyRecordLoggedAsync"/> IS this with one id.
    /// Returns how many people were told. NEVER THROWS, for the same reason.
    /// </summary>
    Task<int> NotifyRecordsLoggedAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default);

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
                a.User.OrganizationId,
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
                PersonNames.Display(r.OrganizationId, r.FirstName, r.LastName),
                r.ClassName,
                r.Role))
            .DistinctBy(r => r.UserId)
            .ToList();
    }

    public Task NotifyRecordLoggedAsync(Guid recordId, CancellationToken cancellationToken = default)
        => NotifyRecordsLoggedAsync(new[] { recordId }, cancellationToken);

    /// <summary>What one recipient is told about, gathered across every record of a batch.</summary>
    private sealed class RecipientDigest
    {
        public readonly Dictionary<Guid, WelfareRecord> Records = new();
        public readonly HashSet<Guid> StudentIds = new();
        public readonly SortedSet<string> Classes = new(NaturalOrder.Comparer!);
    }

    public async Task<int> NotifyRecordsLoggedAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default)
    {
        try
        {
            var ids = recordIds.Distinct().ToList();
            if (ids.Count == 0) return 0;

            var records = await _context.WelfareRecords
                .AsNoTracking()
                .Include(r => r.Student)
                .Include(r => r.Category)
                .Where(r => ids.Contains(r.Id))
                .ToListAsync(cancellationToken);

            // ── The confidentiality rule. One line, one place. ──────────────────────────────
            // A non-Standard record alerts nobody; a draft is visible only to its author and is not yet a
            // record of anything. Both are filtered HERE, before a single recipient is looked up.
            var alertable = records
                .Where(r => r.Visibility == WelfareVisibility.Standard && r.Status != WelfareStatus.Draft)
                .ToList();
            if (alertable.Count < records.Count)
                _logger.LogDebug("{Suppressed} welfare record(s) above Standard or in draft; no class-teacher alert by design",
                    records.Count - alertable.Count);
            if (alertable.Count == 0) return 0;

            // Class teachers per student, looked up once however many records name the student.
            var teachersByStudent = new Dictionary<Guid, List<ClassTeacherRecipient>>();
            var digests = new Dictionary<Guid, RecipientDigest>();

            foreach (var record in alertable)
            {
                // Never notify the person who just wrote it, and never notify the assignee twice —
                // they already get their own assignment/reminder notifications.
                var exclude = new HashSet<Guid> { record.ReportedByUserId };
                if (record.AssignedToUserId.HasValue) exclude.Add(record.AssignedToUserId.Value);

                // Every student the record touches, not only the one it was primarily filed against —
                // a fight involving three classes should reach all three class teachers.
                var studentIds = new List<Guid> { record.StudentId };
                if (record.AdditionalStudentIds != null) studentIds.AddRange(record.AdditionalStudentIds);

                foreach (var studentId in studentIds.Distinct())
                {
                    if (!teachersByStudent.TryGetValue(studentId, out var teachers))
                        teachersByStudent[studentId] = teachers = await GetClassTeachersForStudentAsync(studentId, null, cancellationToken);

                    foreach (var t in teachers.Where(t => !exclude.Contains(t.UserId)))
                    {
                        if (!digests.TryGetValue(t.UserId, out var digest))
                            digests[t.UserId] = digest = new RecipientDigest();
                        digest.Records.TryAdd(record.Id, record);
                        digest.StudentIds.Add(studentId);
                        if (!string.IsNullOrWhiteSpace(t.ClassName)) digest.Classes.Add(t.ClassName.Trim());
                    }
                }
            }

            if (digests.Count == 0)
            {
                _logger.LogDebug("Welfare record(s) {RecordIds}: no live class teacher for the students' classes", string.Join(",", ids));
                return 0;
            }

            var reporterIds = alertable.Select(r => r.ReportedByUserId).Distinct().ToList();
            var reporterNames = (await _context.Users
                    .AsNoTracking()
                    .Where(u => reporterIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.OrganizationId, u.FirstName, u.LastName })
                    .ToListAsync(cancellationToken))
                .ToDictionary(u => u.Id, u => PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName));

            var alerted = 0;
            foreach (var (userId, digest) in digests)
            {
                var told = digest.Records.Values.ToList();
                var first = told[0];
                string title, message, actionUrl;

                if (told.Count == 1)
                {
                    // ONE record: exactly the message a single log has always sent.
                    var studentName = first.Student?.FullName ?? "A student";
                    var className = first.Student?.ClassName;
                    var categoryName = first.Category?.Name ?? first.CaseType.ToString();
                    var reporterName = reporterNames.GetValueOrDefault(first.ReportedByUserId);

                    title = string.IsNullOrWhiteSpace(className)
                        ? $"New {first.CaseType.ToString().ToLowerInvariant()} record"
                        : $"New {first.CaseType.ToString().ToLowerInvariant()} record — {className}";
                    message = $"{categoryName} logged for {studentName}" +
                              (string.IsNullOrWhiteSpace(reporterName) ? "." : $" by {reporterName}.");
                    actionUrl = $"/admin/students/{first.StudentId}/welfare";
                }
                else
                {
                    // SEVERAL records (a group log): ONE message saying how many, never one per child (L5).
                    var caseTypes = told.Select(r => r.CaseType).Distinct().ToList();
                    var categories = told.Select(r => r.Category?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
                    var reporters = told.Select(r => r.ReportedByUserId).Distinct().ToList();
                    var n = digest.StudentIds.Count;
                    var noun = caseTypes.Count == 1 ? caseTypes[0].ToString() : "Welfare";
                    var classes = digest.Classes.Count == 0 ? null : string.Join(", ", digest.Classes);
                    var reporterName = reporters.Count == 1 ? reporterNames.GetValueOrDefault(reporters[0]) : null;

                    title = classes == null
                        ? $"New {noun.ToLowerInvariant()} records"
                        : $"New {noun.ToLowerInvariant()} records — {classes}";
                    message = $"{noun} logged for {n} student{(n == 1 ? "" : "s")}"
                              + (classes == null ? "" : $" in {classes}")
                              + (categories.Count == 1 ? $": {categories[0]}" : "")
                              + (string.IsNullOrWhiteSpace(reporterName) ? "." : $" by {reporterName}.");
                    actionUrl = n == 1 ? $"/admin/students/{digest.StudentIds.First()}/welfare" : "/admin/students/roster";
                }

                try
                {
                    await _notificationService.CreateInAppNotificationAsync(new CreateNotificationRequest
                    {
                        UserId = userId,
                        OrganizationId = first.OrganizationId,
                        BranchId = first.BranchId,
                        Title = title,
                        Message = message,
                        Type = NotificationType.SystemAlert,
                        // A High-tier record is the one a class teacher should see before they next
                        // walk into that room; Low/Medium can wait for them to look.
                        Priority = told.Any(r => r.Tier == WelfareTier.High) ? NotificationPriority.High : NotificationPriority.Normal,
                        // InApp is the instant bell. Email/SMS are decided per recipient by
                        // NotificationService from their preferences and the org defaults — asking
                        // for them here does not force them on anyone who has opted out.
                        Channels = NotificationChannel.InApp | NotificationChannel.Email | NotificationChannel.Sms,
                        EventKey = NotificationEventKeys.WelfareRecordLogged,
                        ActionUrl = actionUrl,
                        IconClass = "journal-text"
                    }, cancellationToken);
                    alerted++;
                }
                catch (Exception ex)
                {
                    // One bad recipient must not cost the others their alert.
                    _logger.LogError(ex, "Failed to alert class teacher {UserId} about welfare record(s) {RecordIds}", userId, string.Join(",", digest.Records.Keys));
                }
            }

            _logger.LogInformation("Welfare record(s) {Count}: alerted {Alerted} class teacher(s)", alertable.Count, alerted);
            return alerted;
        }
        catch (Exception ex)
        {
            // NEVER THROWS — see the interface doc. The records are already committed; a failed
            // alert is a degraded success, not a failed request.
            _logger.LogError(ex, "Class-teacher alert fan-out failed for welfare record(s) {RecordIds}", string.Join(",", recordIds));
            return 0;
        }
    }
}
