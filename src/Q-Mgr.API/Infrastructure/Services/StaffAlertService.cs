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
                .Select(u => new { u.Id, u.FirstName, u.LastName })
                .FirstOrDefaultAsync(cancellationToken);
            if (lm != null) result.Add(new StaffSupervisor(lm.Id, $"{lm.FirstName} {lm.LastName}".Trim(), "Line manager"));
        }

        if (me.DepartmentIds is { Length: > 0 })
        {
            var deptIds = me.DepartmentIds;
            var heads = await _db.Departments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => deptIds.Contains(d.Id) && d.IsActive && d.HeadUserId != null)
                .Select(d => new { d.Name, d.HeadUserId, d.Head!.FirstName, d.Head.LastName, HeadActive = d.Head.IsActive })
                .ToListAsync(cancellationToken);
            foreach (var h in heads)
            {
                if (!h.HeadActive || h.HeadUserId == null || excluded.Contains(h.HeadUserId.Value) || h.HeadUserId == userId) continue;
                if (result.Any(r => r.UserId == h.HeadUserId.Value)) continue;
                result.Add(new StaffSupervisor(h.HeadUserId.Value, $"{h.FirstName} {h.LastName}".Trim(), $"Head of {h.Name}"));
            }
        }

        return result;
    }

    public async Task NotifyRecordLoggedAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await _db.StaffPerformanceRecords.AsNoTracking()
                .Include(r => r.Parameter)
                .Include(r => r.Subject)
                .FirstOrDefaultAsync(r => r.Id == recordId, cancellationToken);
            if (record == null || record.Status != StaffRecordStatus.Final) return;

            // ── The visibility rule. One place. ─────────────────────────────────────────────────
            if (record.Visibility == WelfareVisibility.Restricted)
            {
                _logger.LogDebug("Staff record {RecordId} is Restricted; nobody is alerted by design", recordId);
                return;
            }

            var parameterName = record.Parameter?.Name ?? "a record";
            var loggerName = await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.Id == record.LoggedByUserId)
                .Select(u => (u.FirstName + " " + u.LastName).Trim())
                .FirstOrDefaultAsync(cancellationToken);
            var actionUrl = "/portal";

            var isRecognition = record.Parameter?.Kind == ParameterKind.Recognition;
            var isSystem = record.Source == RecordSource.System;

            // The subject. A Confidential record tells them a record exists and what it is called —
            // never its content, never who else is involved.
            if (record.SubjectUserId != record.LoggedByUserId)
            {
                var title = isRecognition ? "You were recognised" : $"{parameterName} logged about you";
                var message = record.Visibility == WelfareVisibility.Confidential
                    ? $"A confidential {parameterName.ToLowerInvariant()} record was filed about you. Open your portal to see it and respond."
                    : isRecognition
                        ? $"{loggerName ?? "A colleague"}: {Trim(record.Description, 160)}"
                        : $"{OutcomeText(record)}{Trim(record.Description, 140)}" + (isSystem ? " (automatic)" : loggerName == null ? "" : $" — logged by {loggerName}");

                await SendAsync(new CreateNotificationRequest
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
                    ActionUrl = actionUrl,
                    IconClass = isRecognition ? "award" : "journal-check"
                }, cancellationToken);
            }

            // Their supervisors, for a Standard record only, and never the person who logged it.
            if (record.Visibility == WelfareVisibility.Standard && !isRecognition)
            {
                var subjectName = record.Subject?.FullName ?? "a member of staff";
                foreach (var s in await GetSupervisorsAsync(record.SubjectUserId, new[] { record.LoggedByUserId }, cancellationToken))
                {
                    await SendAsync(new CreateNotificationRequest
                    {
                        UserId = s.UserId,
                        OrganizationId = record.OrganizationId,
                        BranchId = record.BranchId,
                        Title = $"{parameterName} — {subjectName}",
                        Message = $"{OutcomeText(record)}{Trim(record.Description, 140)}" + (loggerName == null ? "" : $" — logged by {loggerName}"),
                        Type = NotificationType.StaffPerformance,
                        Priority = NotificationPriority.Normal,
                        Channels = NotificationChannel.InApp,
                        EventKey = NotificationEventKeys.StaffRecordLogged,
                        ActionUrl = $"/admin/staff/{record.SubjectUserId}/timeline",
                        IconClass = "journal-text"
                    }, cancellationToken);
                }
            }

            await NotifyScoreUpdatedAsync(record.OrganizationId, record.BranchId, record.SubjectUserId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Staff record alert fan-out failed for {RecordId}", recordId);
        }
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

    private async Task SendAsync(CreateNotificationRequest request, CancellationToken ct)
    {
        try { await _notifications.CreateInAppNotificationAsync(request, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to notify {UserId}: {Title}", request.UserId, request.Title); }
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
