using QMgr.API.Application.Services;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Who is told about a staff record, by visibility. The whole rule, stated once (plan §8):
///
///   Standard      → the subject (full record) and their line manager / head (unless they logged it)
///   Confidential  → the subject only, EXISTENCE AND TITLE ONLY, no content
///   Restricted    → nobody
///
/// The Confidential row deliberately departs from the welfare rule that a non-Standard record alerts
/// nobody: a child cannot be a recipient of their own record, an employee can and under s.24 of the
/// Data Protection and Privacy Act arguably should be (plan §13 decision 3, taken as proposed).
///
/// NEVER THROWS. The record is committed by the time this runs; a side effect after a committed
/// transaction must not fail the request (the standing rule since ProtectSystem=strict).
/// </summary>
public interface IStaffAlertService
{
    /// <summary>A record has just become Final (created final, or finalised from a draft, or written by a register).</summary>
    Task NotifyRecordLoggedAsync(Guid recordId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A batch of records has just become Final (the group log, 2026-09-23). Each SUBJECT is told about their own
    /// record exactly as <see cref="NotifyRecordLoggedAsync"/> tells them; a supervisor gets ONE summary for every
    /// record of theirs in the batch, not one per colleague. Same visibility rule. Returns how many people were told.
    /// </summary>
    Task<int> NotifyRecordsLoggedAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default);

    /// <summary>Recompute the subject's score and push it live; bell the subject about points when they want that.</summary>
    Task NotifyScoreUpdatedAsync(Guid organizationId, Guid branchId, Guid subjectUserId, CancellationToken cancellationToken = default);

    /// <summary>Who supervises this person: their line manager, else the heads of their departments. Excludes the given ids.</summary>
    Task<List<StaffSupervisor>> GetSupervisorsAsync(Guid userId, IEnumerable<Guid>? exclude = null, CancellationToken cancellationToken = default);
}

public record StaffSupervisor(Guid UserId, string FullName, string Relationship);

public class StaffAlertService : IStaffAlertService
{
    private readonly QMgrDbContext _db;
    private readonly INotificationService _notifications;
    private readonly INotificationHubService _hub;
    private readonly IStaffScoringService _scoring;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly ILogger<StaffAlertService> _logger;

    public StaffAlertService(
        QMgrDbContext db,
        INotificationService notifications,
        INotificationHubService hub,
        IStaffScoringService scoring,
        IStaffPerformancePolicyService policy,
        ILogger<StaffAlertService> logger)
    {
        _db = db;
        _notifications = notifications;
        _hub = hub;
        _scoring = scoring;
        _policy = policy;
        _logger = logger;
    }

    public async Task<List<StaffSupervisor>> GetSupervisorsAsync(Guid userId, IEnumerable<Guid>? exclude = null, CancellationToken cancellationToken = default)
    {
        var excluded = (exclude ?? Enumerable.Empty<Guid>()).ToHashSet();
        var result = new List<StaffSupervisor>();

        var me = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.LineManagerUserId, u.DepartmentIds })
            .FirstOrDefaultAsync(cancellationToken);
        if (me == null) return result;

        if (me.LineManagerUserId.HasValue && !excluded.Contains(me.LineManagerUserId.Value))
        {
            var lm = await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.Id == me.LineManagerUserId.Value && u.IsActive)
                .Select(u => new { u.Id, u.OrganizationId, u.FirstName, u.LastName })
                .FirstOrDefaultAsync(cancellationToken);
            if (lm != null) result.Add(new StaffSupervisor(lm.Id, PersonNames.Display(lm.OrganizationId, lm.FirstName, lm.LastName), "Line manager"));
        }

        if (me.DepartmentIds is { Length: > 0 })
        {
            var deptIds = me.DepartmentIds;
            var heads = await _db.Departments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => deptIds.Contains(d.Id) && d.IsActive && d.HeadUserId != null)
                .Select(d => new { d.Name, d.HeadUserId, d.OrganizationId, d.Head!.FirstName, d.Head.LastName, HeadActive = d.Head.IsActive })
                .ToListAsync(cancellationToken);
            foreach (var h in heads)
            {
                if (!h.HeadActive || h.HeadUserId == null || excluded.Contains(h.HeadUserId.Value) || h.HeadUserId == userId) continue;
                if (result.Any(r => r.UserId == h.HeadUserId.Value)) continue;
                result.Add(new StaffSupervisor(h.HeadUserId.Value, PersonNames.Display(h.OrganizationId, h.FirstName, h.LastName), $"Head of {h.Name}"));
            }
        }

        return result;
    }

    public Task NotifyRecordLoggedAsync(Guid recordId, CancellationToken cancellationToken = default)
        => NotifyRecordsLoggedAsync(new[] { recordId }, cancellationToken);

    /// <summary>What one supervisor is told about, gathered across every record of a batch.</summary>
    private sealed class SupervisorDigest
    {
        public readonly List<Domain.Entities.Staff.StaffPerformanceRecord> Records = new();
        public readonly HashSet<string> Relationships = new();
    }

    public async Task<int> NotifyRecordsLoggedAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default)
    {
        var told = new HashSet<Guid>();
        try
        {
            var ids = recordIds.Distinct().ToList();
            if (ids.Count == 0) return 0;

            var loaded = await _db.StaffPerformanceRecords.AsNoTracking()
                .Include(r => r.Parameter)
                .Include(r => r.Subject)
                .Where(r => ids.Contains(r.Id))
                .ToListAsync(cancellationToken);
            var records = loaded.Where(r => r.Status == StaffRecordStatus.Final).ToList();

            // ── The visibility rule. One place. ─────────────────────────────────────────────────
            var alertable = new List<Domain.Entities.Staff.StaffPerformanceRecord>();
            foreach (var record in records)
            {
                if (record.Visibility == WelfareVisibility.Restricted)
                {
                    _logger.LogDebug("Staff record {RecordId} is Restricted; nobody is alerted by design", record.Id);
                    continue;
                }
                alertable.Add(record);
            }
            if (alertable.Count == 0) return 0;

            var loggerIds = alertable.Select(r => r.LoggedByUserId).Distinct().ToList();
            var loggerNames = await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => loggerIds.Contains(u.Id))
                .Select(u => new { u.Id, u.OrganizationId, u.FirstName, u.LastName })
                .ToListAsync(cancellationToken);
            var nameOf = loggerNames.ToDictionary(u => u.Id, u => PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName));
            string? LoggerName(Domain.Entities.Staff.StaffPerformanceRecord r) => nameOf.TryGetValue(r.LoggedByUserId, out var n) ? n : null;

            var digests = new Dictionary<Guid, SupervisorDigest>();

            foreach (var record in alertable)
            {
                var parameterName = record.Parameter?.Name ?? "a record";
                var loggerName = LoggerName(record);
                var isRecognition = record.Parameter?.Kind == ParameterKind.Recognition;
                var isSystem = record.Source == RecordSource.System;

                // The subject — ALWAYS their own message, batch or not: the record is about them. A Confidential
                // record tells them a record exists and what it is called — never its content, never who else is involved.
                if (record.SubjectUserId != record.LoggedByUserId)
                {
                    var title = isRecognition ? "You were recognised" : $"{parameterName} logged about you";
                    var message = record.Visibility == WelfareVisibility.Confidential
                        ? $"A confidential {parameterName.ToLowerInvariant()} record was filed about you. Open your portal to see it and respond."
                        : isRecognition
                            ? $"{loggerName ?? "A colleague"}: {Trim(record.Description, 160)}"
                            : $"{OutcomeText(record)}{Trim(record.Description, 140)}" + (isSystem ? " (automatic)" : loggerName == null ? "" : $" — logged by {loggerName}");

                    if (await SendAsync(new CreateNotificationRequest
                    {
                        UserId = record.SubjectUserId,
                        OrganizationId = record.OrganizationId,
                        BranchId = record.BranchId,
                        Title = title,
                        Message = message,
                        Type = NotificationType.StaffPerformance,
                        Priority = record.Points is < 0 ? NotificationPriority.High : NotificationPriority.Normal,
                        Channels = NotificationChannel.InApp | NotificationChannel.Email,
                        EventKey = isRecognition ? NotificationEventKeys.StaffRecognitionReceived : NotificationEventKeys.StaffRecordLogged,
                        ActionUrl = "/portal",
                        IconClass = isRecognition ? "award" : "journal-check"
                    }, cancellationToken))
                        told.Add(record.SubjectUserId);
                }

                // Their supervisors, for a Standard record only, and never the person who logged it — gathered, so a
                // head whose whole department is in a batch hears about it ONCE.
                if (record.Visibility == WelfareVisibility.Standard && !isRecognition)
                {
                    foreach (var s in await GetSupervisorsAsync(record.SubjectUserId, new[] { record.LoggedByUserId }, cancellationToken))
                    {
                        if (!digests.TryGetValue(s.UserId, out var digest)) digests[s.UserId] = digest = new SupervisorDigest();
                        digest.Records.Add(record);
                        digest.Relationships.Add(s.Relationship);
                    }
                }
            }

            foreach (var (supervisorId, digest) in digests)
            {
                var first = digest.Records[0];
                var parameterName = first.Parameter?.Name ?? "a record";
                var loggerName = LoggerName(first);
                string title, message, actionUrl;
                if (digest.Records.Count == 1)
                {
                    // One record: exactly what the single create has always sent.
                    var subjectName = first.Subject?.FullName ?? "a member of staff";
                    title = $"{parameterName} — {subjectName}";
                    message = $"{OutcomeText(first)}{Trim(first.Description, 140)}" + (loggerName == null ? "" : $" — logged by {loggerName}");
                    actionUrl = $"/admin/staff/{first.SubjectUserId}/timeline";
                }
                else
                {
                    // A batch: ONE summary. Every record of a group log shares its parameter and wording, so the first
                    // one speaks for all; the names are people this supervisor already supervises.
                    var people = digest.Records.Select(r => r.SubjectUserId).Distinct().Count();
                    var names = digest.Records.Select(r => r.Subject?.FullName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
                    var shown = string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : "");
                    var departments = digest.Relationships.Where(r => r.StartsWith("Head of ", StringComparison.Ordinal))
                        .Select(r => r["Head of ".Length..]).ToList();
                    var where = departments.Count == 1 && digest.Relationships.Count == 1 ? $" in {departments[0]}" : "";
                    var sameParameter = digest.Records.All(r => r.ParameterId == first.ParameterId);
                    var what = sameParameter ? parameterName : "Records";
                    title = $"{what} logged for {people} people{where}";
                    message = $"{OutcomeText(first)}{Trim(first.Description, 120)} — {shown}" + (loggerName == null ? "" : $". Logged by {loggerName}");
                    actionUrl = "/admin/staff/records";
                }

                if (await SendAsync(new CreateNotificationRequest
                {
                    UserId = supervisorId,
                    Title = title,
                    Message = message,
                    ActionUrl = actionUrl,
                    OrganizationId = first.OrganizationId,
                    BranchId = first.BranchId,
                    Type = NotificationType.StaffPerformance,
                    Priority = NotificationPriority.Normal,
                    Channels = NotificationChannel.InApp,
                    EventKey = NotificationEventKeys.StaffRecordLogged,
                    IconClass = "journal-text"
                }, cancellationToken))
                    told.Add(supervisorId);
            }

            foreach (var subject in alertable.Select(r => new { r.OrganizationId, r.BranchId, r.SubjectUserId }).Distinct())
                await NotifyScoreUpdatedAsync(subject.OrganizationId, subject.BranchId, subject.SubjectUserId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Staff record alert fan-out failed for {Count} record(s)", recordIds.Count);
        }
        return told.Count;
    }

    public async Task NotifyScoreUpdatedAsync(Guid organizationId, Guid branchId, Guid subjectUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var policy = await _policy.GetAsync(organizationId, cancellationToken);
            var period = _policy.PeriodFor(policy, DateOnly.FromDateTime(DateTime.UtcNow));
            var score = await _scoring.ComputeAsync(organizationId, branchId, subjectUserId, period, includeRank: false, cancellationToken);

            await _hub.SendStaffScoreUpdatedAsync(subjectUserId, new StaffScoreUpdatedEvent(subjectUserId, period.Key, score.Composite, score.Points, score.Band));

            // Bell only by default (the event key's DefaultEmail is false); the person can turn it off.
            await SendAsync(new CreateNotificationRequest
            {
                UserId = subjectUserId,
                OrganizationId = organizationId,
                BranchId = branchId,
                Title = "Your score moved",
                Message = score.Composite.HasValue
                    ? $"{period.Name}: {score.Points} points, composite {score.Composite:0.#} ({score.BandName})."
                    : $"{period.Name}: {score.Points} points so far.",
                Type = NotificationType.StaffPerformance,
                Priority = NotificationPriority.Low,
                Channels = NotificationChannel.InApp,
                EventKey = NotificationEventKeys.StaffPointsEarned,
                ActionUrl = "/portal",
                IconClass = "graph-up-arrow"
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Staff score push failed for {UserId}", subjectUserId);
        }
    }

    /// <summary>True when the notification was written. Never throws.</summary>
    private async Task<bool> SendAsync(CreateNotificationRequest request, CancellationToken ct)
    {
        try { await _notifications.CreateInAppNotificationAsync(request, ct); return true; }
        catch (Exception ex) { _logger.LogError(ex, "Failed to notify {UserId}: {Title}", request.UserId, request.Title); return false; }
    }

    private static string OutcomeText(Domain.Entities.Staff.StaffPerformanceRecord r) => r.Outcome switch
    {
        DutyOutcome.Present => "Present. ",
        DutyOutcome.Late => "Late. ",
        DutyOutcome.Absent => "Absent. ",
        DutyOutcome.Excused => "Excused. ",
        DutyOutcome.Recovered => "Recovered. ",
        DutyOutcome.Completed => "Completed. ",
        DutyOutcome.NotCompleted => "Not completed. ",
        _ => r.Points is { } p && p != 0 ? $"{(p > 0 ? "+" : "")}{p} points. " : ""
    };

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
