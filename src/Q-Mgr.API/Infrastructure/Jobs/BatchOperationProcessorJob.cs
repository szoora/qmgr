using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Commits one batch in the background, writing a <see cref="RosterImportJobEntry"/> per record.
/// </summary>
/// <remarks>
/// <para>
/// It shares the import's job table, entry table and live progress channel — see
/// <see cref="RosterImportKind.Batch"/> for why that is a discriminator rather than a third table.
/// What it does not share is the per-row work, which is why this is its own processor class rather
/// than a fourth branch inside the already-large roster processor.
/// </para>
/// <para>
/// The batch is <b>re-resolved here</b> rather than trusting a preview the operator saw some
/// seconds ago. Between preview and commit a colleague may have promoted the same class, and the
/// resolver is the only thing that knows what is still true. The preview is a promise about
/// intent, not a set of instructions.
/// </para>
/// </remarks>
public class BatchOperationProcessorJob
{
    private readonly QMgrDbContext _context;
    private readonly IBatchOperationService _resolver;
    private readonly IRosterImportBroadcaster _broadcaster;
    private readonly ILogger<BatchOperationProcessorJob> _logger;

    // Same reasoning as the roster import: a 2000-row batch would otherwise be 2000 SignalR
    // messages in a few seconds, which is its own performance problem.
    private const int BroadcastEveryNRows = 10;

    public BatchOperationProcessorJob(
        QMgrDbContext context,
        IBatchOperationService resolver,
        IRosterImportBroadcaster broadcaster,
        ILogger<BatchOperationProcessorJob> logger,
        QMgr.Infrastructure.Services.IStaffProfileChangeNotifier profileChanges)
    {
        _profileChanges = profileChanges;
        _context = context;
        _resolver = resolver;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    private readonly QMgr.Infrastructure.Services.IStaffProfileChangeNotifier _profileChanges;

    /// <summary>Role changes applied in this run, followed up (cache, push, activity, notification) once they are saved.</summary>
    private readonly List<(Guid UserId, Guid? OldRoleId, Guid NewRoleId)> _roleChanges = new();

    [AutomaticRetry(Attempts = 0)] // A half-applied batch must never silently re-run from the top.
    public async Task ProcessAsync(Guid jobId)
    {
        var job = await _context.RosterImportJobs.FirstOrDefaultAsync(j => j.Id == jobId);
        if (job == null)
        {
            _logger.LogWarning("Batch job {JobId} vanished before it could run", jobId);
            return;
        }

        job.Status = RosterImportStatus.Processing;
        job.StartedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await BroadcastAsync(job);

        try
        {
            var request = JsonSerializer.Deserialize<BatchRequest>(job.RowsJson)
                          ?? throw new InvalidOperationException("The batch's own request could not be read back.");

            var resolved = await _resolver.ResolveAsync(job.BranchId, request);

            if (resolved.BlockingError != null)
            {
                job.Status = RosterImportStatus.Failed;
                job.FailureReason = resolved.BlockingError;
                job.CompletedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                await BroadcastAsync(job);
                return;
            }

            var rowNumber = 0;
            foreach (var row in resolved.Rows)
            {
                rowNumber++;

                var entry = new RosterImportJobEntry
                {
                    RosterImportJobId = job.Id,
                    RowNumber = rowNumber,
                    StudentName = row.Label,
                    StudentCode = row.SubLabel,
                    Outcome = row.Outcome,
                    Message = row.Message,
                    PreviousValue = row.PreviousValue,
                    NewValue = row.NewValue,
                    StudentId = row.Id
                };

                if (row.Outcome == RosterImportRowOutcome.Updated)
                {
                    try
                    {
                        await ApplyAsync(request, row);
                        job.UpdatedCount++;
                    }
                    catch (Exception ex)
                    {
                        // One record failing is a row-level outcome, not a job-level one — the other
                        // 730 have no reason to be abandoned.
                        entry.Outcome = RosterImportRowOutcome.Failed;
                        entry.Message = $"Could not be saved: {ex.Message}";
                        job.FailedCount++;
                        _logger.LogError(ex, "Batch {JobId} row {Row} failed", job.Id, rowNumber);
                    }
                }
                else if (row.Outcome == RosterImportRowOutcome.Failed) job.FailedCount++;
                else job.DuplicateCount++; // Skipped rows are counted here — see the summary note below.

                _context.RosterImportJobEntries.Add(entry);
                job.ProcessedRows = rowNumber;

                if (rowNumber % BroadcastEveryNRows == 0)
                {
                    await _context.SaveChangesAsync();
                    await BroadcastAsync(job);
                }
            }

            job.TotalRows = rowNumber;
            job.Status = RosterImportStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            await BroadcastAsync(job);
            await FollowUpRoleChangesAsync(job);

            _logger.LogInformation(
                "Batch {JobId} ({Operation}) finished: {Updated} changed, {Skipped} skipped, {Failed} failed of {Total}",
                job.Id, request.Operation, job.UpdatedCount, job.DuplicateCount, job.FailedCount, job.TotalRows);
        }
        catch (Exception ex)
        {
            job.Status = RosterImportStatus.Failed;
            job.FailureReason = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            await BroadcastAsync(job);
            await FollowUpRoleChangesAsync(job);
            _logger.LogError(ex, "Batch {JobId} failed outright", jobId);
        }
    }

    /// <summary>
    /// After the rows are saved: every role this run changed gets the same follow-up the single-user editor
    /// gives — permission cache dropped, live push, activity event, staff.profile-changed. Before 2026-09-17
    /// a bulk demotion left the old permissions cached for up to five minutes.
    /// </summary>
    private async Task FollowUpRoleChangesAsync(RosterImportJob job)
    {
        foreach (var (userId, oldRoleId, newRoleId) in _roleChanges)
            await _profileChanges.RoleChangedAsync(job.OrganizationId, userId, oldRoleId, newRoleId, job.CreatedByUserId, "bulk change");
        _roleChanges.Clear();
    }

    /// <summary>
    /// Writes one record's change — and only the field named by the operation.
    /// </summary>
    /// <remarks>
    /// Each branch loads the tracked entity and assigns a single property. Nothing goes through the
    /// full-replace <c>PUT</c>, so no unrelated field can be cleared as a side effect.
    /// </remarks>
    private async Task ApplyAsync(BatchRequest request, BatchRowPreviewDto row)
    {
        switch (request.Operation)
        {
            case BatchOperation.AdvanceClass:
            {
                var s = await _context.Students.FirstAsync(x => x.Id == row.Id);
                s.ClassName = row.NewValue;
                s.UpdatedAt = DateTime.UtcNow;
                break;
            }

            case BatchOperation.SetField:
            {
                var s = await _context.Students.FirstAsync(x => x.Id == row.Id);
                switch (request.Field)
                {
                    case BatchField.ClassName: s.ClassName = row.NewValue; break;
                    case BatchField.House: s.House = row.NewValue; break;
                    case BatchField.DormitoryOrStream: s.DormitoryOrStream = row.NewValue; break;
                    case BatchField.SponsorName: s.SponsorName = row.NewValue; break;
                    case BatchField.Residency:
                        s.Residency = Enum.TryParse<StudentResidency>(row.NewValue, true, out var res) ? res : null; break;
                    case BatchField.FeesStatus:
                        s.FeesStatus = Enum.TryParse<StudentFeesStatus>(row.NewValue, true, out var fee) ? fee : null; break;
                    case BatchField.TransportMode:
                        s.TransportMode = Enum.TryParse<StudentTransportMode>(row.NewValue, true, out var tr) ? tr : null; break;
                }
                s.UpdatedAt = DateTime.UtcNow;
                break;
            }

            case BatchOperation.Deactivate:
            {
                var s = await _context.Students.FirstAsync(x => x.Id == row.Id);
                s.IsActive = false;
                s.UpdatedAt = DateTime.UtcNow;
                break;
            }

            case BatchOperation.SetConsent:
            {
                var s = await _context.Students.FirstAsync(x => x.Id == row.Id);
                var giving = string.Equals(row.NewValue, "Recorded", StringComparison.OrdinalIgnoreCase);
                s.DataConsentGivenAt = giving ? DateTime.UtcNow : null;
                if (giving && !string.IsNullOrWhiteSpace(request.Reason)) s.DataConsentNotes = request.Reason;
                s.UpdatedAt = DateTime.UtcNow;
                break;
            }

            case BatchOperation.AssignWelfareAction:
            {
                var r = await _context.WelfareRecords.FirstAsync(x => x.Id == row.Id);
                r.AssignedToUserId = request.TargetUserId;
                r.UpdatedAt = DateTime.UtcNow;
                break;
            }

            case BatchOperation.SetWelfareReviewDate:
            {
                var r = await _context.WelfareRecords.FirstAsync(x => x.Id == row.Id);
                r.ActionDueDate = request.DateValue.HasValue
                    ? DateTime.SpecifyKind(request.DateValue.Value, DateTimeKind.Utc)
                    : null;
                r.UpdatedAt = DateTime.UtcNow;
                break;
            }

            case BatchOperation.SetWelfareStatus:
            {
                var r = await _context.WelfareRecords.FirstAsync(x => x.Id == row.Id);
                if (Enum.TryParse<WelfareStatus>(row.NewValue, true, out var st)) r.Status = st;
                r.UpdatedAt = DateTime.UtcNow;
                break;
            }

            case BatchOperation.CheckOutVisitors:
            {
                var v = await _context.Visitors.FirstAsync(x => x.Id == row.Id);
                v.CheckedOutAt = DateTime.UtcNow;
                v.UpdatedAt = DateTime.UtcNow;
                break;
            }

            case BatchOperation.CancelTokens:
            {
                var t = await _context.Tokens.FirstAsync(x => x.Id == row.Id);
                t.Status = TokenStatus.Cancelled;
                break;
            }

            case BatchOperation.CancelAppointments:
            {
                var a = await _context.Appointments.FirstAsync(x => x.Id == row.Id);
                a.Status = AppointmentStatus.Cancelled;
                a.CancellationReason = string.IsNullOrWhiteSpace(request.Reason) ? "Cancelled in bulk" : request.Reason;
                a.UpdatedAt = DateTime.UtcNow;
                break;
            }

            case BatchOperation.SetUserActive:
            {
                var u = await _context.Users.FirstAsync(x => x.Id == row.Id);
                u.IsActive = string.Equals(row.NewValue, "Active", StringComparison.OrdinalIgnoreCase);
                u.UpdatedAt = DateTime.UtcNow;
                break;
            }

            case BatchOperation.SetUserRole:
            {
                var u = await _context.Users.FirstAsync(x => x.Id == row.Id);
                if (Guid.TryParse(row.NewValue, out var roleId) && u.RoleId != roleId)
                {
                    _roleChanges.Add((u.Id, u.RoleId, roleId));
                    u.RoleId = roleId;
                }
                u.UpdatedAt = DateTime.UtcNow;
                break;
            }
        }
    }

    private Task BroadcastAsync(RosterImportJob job) => _broadcaster.BroadcastAsync(new RosterImportProgressEvent
    {
        JobId = job.Id,
        BranchId = job.BranchId,
        Status = job.Status,
        TotalRows = job.TotalRows,
        ProcessedRows = job.ProcessedRows,
        CreatedCount = job.CreatedCount,
        UpdatedCount = job.UpdatedCount,
        DuplicateCount = job.DuplicateCount,
        FailedCount = job.FailedCount
    });
}
