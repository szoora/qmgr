using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Processes one RosterImportJob's rows in the background — a school roster can be thousands of
/// rows (a real visiting-day scenario is "over 2000 visitors in a day," per the request that
/// prompted this feature), and nothing about that belongs on a request thread. Reads the job's
/// stashed RowsJson (the request handler that created the job has no way to hand this job class
/// the original upload directly — Hangfire serializes only the job ID), validates and upserts
/// each row, and broadcasts live progress via IRosterImportBroadcaster as it goes. Every row
/// produces exactly one RosterImportJobEntry regardless of outcome — that table is the durable
/// "logger" this feature was asked for, not just the live broadcast.
/// </summary>
public class RosterImportProcessorJob
{
    private readonly QMgrDbContext _context;
    private readonly IRosterImportBroadcaster _broadcaster;
    private readonly ILogger<RosterImportProcessorJob> _logger;

    // Broadcasting every single row over SignalR would mean ~2000 messages in a few seconds for
    // a full school roster — enough to be its own performance problem. Every Nth row (plus always
    // at start/end) keeps the live progress feel without flooding the connection.
    private const int BroadcastEveryNRows = 10;

    public RosterImportProcessorJob(QMgrDbContext context, IRosterImportBroadcaster broadcaster, ILogger<RosterImportProcessorJob> logger)
    {
        _context = context;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)] // A partially-processed import must not silently re-run from row 1 — see the catch block below instead.
    public async Task ProcessAsync(Guid jobId)
    {
        var job = await _context.RosterImportJobs.FirstOrDefaultAsync(j => j.Id == jobId);
        if (job == null)
        {
            _logger.LogWarning("RosterImportJob {JobId} not found — nothing to process", jobId);
            return;
        }

        List<RosterImportRow> rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<RosterImportRow>>(job.RowsJson) ?? new();
        }
        catch (JsonException ex)
        {
            job.Status = RosterImportStatus.Failed;
            job.FailureReason = "Could not read the uploaded rows (corrupted payload).";
            job.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _logger.LogError(ex, "RosterImportJob {JobId}: failed to deserialize RowsJson", jobId);
            return;
        }

        job.Status = RosterImportStatus.Processing;
        job.StartedAt = DateTime.UtcNow;
        job.TotalRows = rows.Count;
        await _context.SaveChangesAsync();
        await Broadcast(job);

        // Intra-file duplicate detection: same (StudentCode, guardian identifier) appearing twice
        // in one upload — a common real mistake when a school's export tool double-lists a
        // guardian who's authorized for two things the source system tracks separately.
        var seenPairs = new HashSet<string>();

        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 1;
            try
            {
                await ProcessRowAsync(job, rows[i], rowNumber, seenPairs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RosterImportJob {JobId}: unhandled error on row {RowNumber}", jobId, rowNumber);
                _context.ChangeTracker.Clear(); // drop whatever this row half-tracked before it failed
                job.FailedCount++;
                _context.RosterImportJobEntries.Add(new RosterImportJobEntry
                {
                    RosterImportJobId = job.Id,
                    RowNumber = rowNumber,
                    StudentCode = rows[i].StudentCode,
                    StudentName = rows[i].StudentFullName,
                    GuardianName = rows[i].GuardianFullName,
                    Outcome = RosterImportRowOutcome.Failed,
                    Message = "Unexpected error processing this row — see server logs."
                });
            }

            job.ProcessedRows = rowNumber;
            await _context.SaveChangesAsync();

            if (rowNumber % BroadcastEveryNRows == 0 || rowNumber == rows.Count)
                await Broadcast(job);
        }

        job.Status = job.FailedCount > 0 ? RosterImportStatus.CompletedWithErrors : RosterImportStatus.Completed;
        job.CompletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await Broadcast(job);

        _logger.LogInformation(
            "RosterImportJob {JobId} complete: {Created} created, {Updated} updated, {Duplicate} duplicate, {Failed} failed of {Total}",
            jobId, job.CreatedCount, job.UpdatedCount, job.DuplicateCount, job.FailedCount, job.TotalRows);
    }

    private async Task ProcessRowAsync(RosterImportJob job, RosterImportRow row, int rowNumber, HashSet<string> seenPairs)
    {
        var entry = new RosterImportJobEntry
        {
            RosterImportJobId = job.Id,
            RowNumber = rowNumber,
            StudentCode = string.IsNullOrWhiteSpace(row.StudentCode) ? null : row.StudentCode.Trim(),
            StudentName = row.StudentFullName?.Trim(),
            GuardianName = row.GuardianFullName?.Trim()
        };

        // --- Validation ---
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(row.StudentFullName)) missing.Add("student name");
        if (string.IsNullOrWhiteSpace(row.GuardianFullName)) missing.Add("guardian name");
        var normPhone = VisitorMatching.NormalizePhone(row.GuardianPhone);
        var normEmail = VisitorMatching.NormalizeEmail(row.GuardianEmail);
        if (normPhone == null && normEmail == null) missing.Add("a guardian phone or email");

        if (missing.Count > 0)
        {
            entry.Outcome = RosterImportRowOutcome.Failed;
            entry.Message = $"Missing required field(s): {string.Join(", ", missing)}.";
            job.FailedCount++;
            _context.RosterImportJobEntries.Add(entry);
            return;
        }

        // --- Intra-file duplicate detection ---
        var pairKey = $"{entry.StudentCode ?? entry.StudentName}|{normPhone ?? normEmail}";
        if (!seenPairs.Add(pairKey))
        {
            entry.Outcome = RosterImportRowOutcome.DuplicateInFile;
            entry.Message = "Same student/guardian pair already appeared earlier in this file — skipped.";
            job.DuplicateCount++;
            _context.RosterImportJobEntries.Add(entry);
            return;
        }

        var wasNew = false;

        // --- Find-or-create Student (upsert by StudentCode when given) ---
        Student? student = null;
        if (entry.StudentCode != null)
        {
            student = await _context.Students.FirstOrDefaultAsync(s =>
                s.OrganizationId == job.OrganizationId && s.StudentCode == entry.StudentCode && s.IsActive);
        }

        if (student == null)
        {
            student = new Student
            {
                OrganizationId = job.OrganizationId,
                BranchId = job.BranchId,
                FullName = row.StudentFullName.Trim(),
                StudentCode = entry.StudentCode,
                ClassName = string.IsNullOrWhiteSpace(row.ClassName) ? null : row.ClassName.Trim()
            };
            _context.Students.Add(student);
            wasNew = true;
        }
        else
        {
            student.FullName = row.StudentFullName.Trim();
            if (!string.IsNullOrWhiteSpace(row.ClassName)) student.ClassName = row.ClassName.Trim();
        }

        // --- Find-or-create the guardian's VisitorProfile (same matching rule as check-in) ---
        VisitorProfile? profile = null;
        if (normEmail != null)
            profile = await _context.VisitorProfiles.FirstOrDefaultAsync(p =>
                p.OrganizationId == job.OrganizationId && p.DeletedAt == null && p.NormalizedEmail == normEmail);
        if (profile == null && normPhone != null)
            profile = await _context.VisitorProfiles.FirstOrDefaultAsync(p =>
                p.OrganizationId == job.OrganizationId && p.DeletedAt == null && p.NormalizedPhone == normPhone);

        if (profile == null)
        {
            profile = new VisitorProfile
            {
                OrganizationId = job.OrganizationId,
                FullName = row.GuardianFullName.Trim(),
                Phone = row.GuardianPhone?.Trim(),
                NormalizedPhone = normPhone,
                Email = row.GuardianEmail?.Trim(),
                NormalizedEmail = normEmail
            };
            _context.VisitorProfiles.Add(profile);
            wasNew = true;
        }
        else
        {
            if (profile.NormalizedEmail == null && normEmail != null) { profile.Email = row.GuardianEmail; profile.NormalizedEmail = normEmail; }
            if (profile.NormalizedPhone == null && normPhone != null) { profile.Phone = row.GuardianPhone; profile.NormalizedPhone = normPhone; }
        }

        // Student/profile need real Ids before the StudentGuardian link can reference them.
        await _context.SaveChangesAsync();

        var relationship = string.IsNullOrWhiteSpace(row.Relationship) ? "Guardian" : row.Relationship.Trim();
        var link = await _context.StudentGuardians.FirstOrDefaultAsync(g =>
            g.StudentId == student.Id && g.VisitorProfileId == profile.Id);

        if (link == null)
        {
            link = new StudentGuardian
            {
                StudentId = student.Id,
                VisitorProfileId = profile.Id,
                Relationship = relationship,
                IsActive = true
            };
            _context.StudentGuardians.Add(link);
            wasNew = true;
        }
        else
        {
            link.Relationship = relationship;
            link.IsActive = true;
        }

        entry.StudentId = student.Id;
        entry.GuardianProfileId = profile.Id;

        if (wasNew)
        {
            entry.Outcome = RosterImportRowOutcome.Created;
            entry.Message = "New student and/or guardian created.";
            job.CreatedCount++;
        }
        else
        {
            entry.Outcome = RosterImportRowOutcome.Updated;
            entry.Message = "Matched an existing student and guardian — details refreshed.";
            job.UpdatedCount++;
        }

        _context.RosterImportJobEntries.Add(entry);
    }

    private Task Broadcast(RosterImportJob job) => _broadcaster.BroadcastAsync(new RosterImportProgressEvent
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
