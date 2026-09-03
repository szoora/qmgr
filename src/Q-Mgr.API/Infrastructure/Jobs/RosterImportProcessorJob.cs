using System.Globalization;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Entities.Welfare;
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
///
/// Handles both RosterImportKind values: the original student+guardian roster upload, and the
/// welfare ledger's historical-records backfill (same job table, same entries log, same
/// progress channel — only the per-row work differs; see ProcessWelfareRowAsync).
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

    // Accepted OccurredAt spellings for a welfare-history row, tried in order. Day-first forms
    // come before the invariant-culture fallback (which reads "3/4/2026" as March 4) because this
    // product's schools write dates day-first — an ambiguous slash date resolves to d/M/y here.
    private static readonly string[] WelfareDateFormats =
    {
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss",
        "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy HH:mm", "d/M/yyyy HH:mm",
        "dd-MM-yyyy", "d-M-yyyy", "dd-MM-yyyy HH:mm",
        "dd.MM.yyyy", "d.M.yyyy",
        "dd/MM/yy", "d/M/yy"
    };

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

        if (job.Kind == RosterImportKind.Welfare)
        {
            // Every imported record is attributed to the importer (WelfareRecord.ReportedByUserId
            // is non-nullable) — WelfareController.StartImport refuses to create an unattributable
            // job, so this only trips if a row was inserted some other way.
            if (!job.CreatedByUserId.HasValue)
            {
                await FailJobAsync(job, "Historical welfare imports must be attributed to a signed-in user — this job has no creator.");
                return;
            }

            var welfareRows = DeserializeRows<WelfareImportRow>(job);
            if (welfareRows == null) { await FailJobAsync(job, "Could not read the uploaded rows (corrupted payload)."); return; }

            var seenKeys = new HashSet<string>();
            await RunRowsAsync(job, welfareRows,
                (row, rowNumber) => ProcessWelfareRowAsync(job, row, rowNumber, seenKeys),
                row => new RosterImportJobEntry { StudentCode = row.StudentCode, GuardianName = row.Category });
        }
        else
        {
            var rosterRows = DeserializeRows<RosterImportRow>(job);
            if (rosterRows == null) { await FailJobAsync(job, "Could not read the uploaded rows (corrupted payload)."); return; }

            // Intra-file duplicate detection: same (StudentCode, guardian identifier) appearing twice
            // in one upload — a common real mistake when a school's export tool double-lists a
            // guardian who's authorized for two things the source system tracks separately.
            var seenPairs = new HashSet<string>();
            await RunRowsAsync(job, rosterRows,
                (row, rowNumber) => ProcessRowAsync(job, row, rowNumber, seenPairs),
                row => new RosterImportJobEntry { StudentCode = row.StudentCode, StudentName = row.StudentFullName, GuardianName = row.GuardianFullName });
        }
    }

    private List<TRow>? DeserializeRows<TRow>(RosterImportJob job)
    {
        try
        {
            return JsonSerializer.Deserialize<List<TRow>>(job.RowsJson) ?? new();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "RosterImportJob {JobId}: failed to deserialize RowsJson as {RowType}", job.Id, typeof(TRow).Name);
            return null;
        }
    }

    private async Task FailJobAsync(RosterImportJob job, string reason)
    {
        job.Status = RosterImportStatus.Failed;
        job.FailureReason = reason;
        job.CompletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await Broadcast(job);
    }

    /// <summary>
    /// The row loop both kinds share: per-row try/catch (a bad row never takes the job down),
    /// ChangeTracker reset on failure, one RosterImportJobEntry per row no matter what, progress
    /// saved every row and broadcast every Nth.
    /// </summary>
    private async Task RunRowsAsync<TRow>(RosterImportJob job, List<TRow> rows, Func<TRow, int, Task> processRow, Func<TRow, RosterImportJobEntry> fallbackEntry)
    {
        job.Status = RosterImportStatus.Processing;
        job.StartedAt = DateTime.UtcNow;
        job.TotalRows = rows.Count;
        await _context.SaveChangesAsync();
        await Broadcast(job);

        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 1;
            try
            {
                await processRow(rows[i], rowNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RosterImportJob {JobId}: unhandled error on row {RowNumber}", job.Id, rowNumber);
                _context.ChangeTracker.Clear(); // drop whatever this row half-tracked before it failed
                job.FailedCount++;
                var entry = fallbackEntry(rows[i]);
                entry.RosterImportJobId = job.Id;
                entry.RowNumber = rowNumber;
                entry.Outcome = RosterImportRowOutcome.Failed;
                entry.Message = "Unexpected error processing this row — see server logs.";
                _context.RosterImportJobEntries.Add(entry);
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
            "RosterImportJob {JobId} ({Kind}) complete: {Created} created, {Updated} updated, {Duplicate} duplicate, {Failed} failed of {Total}",
            job.Id, job.Kind, job.CreatedCount, job.UpdatedCount, job.DuplicateCount, job.FailedCount, job.TotalRows);
    }

    // ---------------------------------------------------------------------
    // Kind = Roster
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // Kind = Welfare — historical ledger backfill
    // ---------------------------------------------------------------------

    /// <summary>
    /// One historical welfare record. Mirrors WelfareController.CreateRecord's rules (same
    /// description limits, same points-sign rule, same server-forced Confidential for Welfare
    /// case type, same "category must already exist" stance — a name that doesn't match fails the
    /// row rather than creating a category nobody chose) minus the late-entry gate, which is
    /// meaningless for a backfill that is by definition entirely late. Status defaults to
    /// Resolved: history is closed unless the file says otherwise. Duplicate guard: the same
    /// student + case type + calendar day + description already on the ledger (or earlier in this
    /// file) is skipped, so re-uploading the same export twice doesn't double every record.
    /// The entry's GuardianName column carries the category name — there's no guardian in a
    /// welfare row, and the log needs something readable beside the student.
    /// </summary>
    private async Task ProcessWelfareRowAsync(RosterImportJob job, WelfareImportRow row, int rowNumber, HashSet<string> seenKeys)
    {
        var studentCode = string.IsNullOrWhiteSpace(row.StudentCode) ? null : row.StudentCode.Trim();
        var categoryName = string.IsNullOrWhiteSpace(row.Category) ? null : row.Category.Trim();
        var entry = new RosterImportJobEntry
        {
            RosterImportJobId = job.Id,
            RowNumber = rowNumber,
            StudentCode = studentCode,
            GuardianName = categoryName
        };

        void Fail(string message)
        {
            entry.Outcome = RosterImportRowOutcome.Failed;
            entry.Message = message;
            job.FailedCount++;
            _context.RosterImportJobEntries.Add(entry);
        }

        // --- Required fields ---
        var missing = new List<string>();
        if (studentCode == null) missing.Add("student code");
        if (string.IsNullOrWhiteSpace(row.CaseType)) missing.Add("case type");
        if (categoryName == null) missing.Add("category");
        if (string.IsNullOrWhiteSpace(row.OccurredAt)) missing.Add("date");
        var description = (row.Description ?? "").Trim();
        if (description.Length == 0) missing.Add("description");
        if (missing.Count > 0) { Fail($"Missing required field(s): {string.Join(", ", missing)}."); return; }

        // --- Case type ---
        if (!TryParseCaseType(row.CaseType!, out var caseType)) { Fail($"Unrecognized case type '{row.CaseType!.Trim()}' — use Achievement, Behavior, or Welfare."); return; }

        // --- Description limits (same numbers as a live record) ---
        if (description.Length < WelfareController.MinDescriptionLength) { Fail($"Description is too short — at least {WelfareController.MinDescriptionLength} characters."); return; }
        if (description.Length > WelfareController.MaxDescriptionLength) { Fail($"Description is too long — keep it under {WelfareController.MaxDescriptionLength} characters."); return; }

        // --- Date ---
        if (!TryParseOccurredAt(row.OccurredAt!, out var occurredAt)) { Fail($"Couldn't read the date '{row.OccurredAt!.Trim()}' — use YYYY-MM-DD or DD/MM/YYYY."); return; }
        if (occurredAt > DateTime.UtcNow.AddMinutes(5)) { Fail("Date can't be in the future."); return; }

        // --- Points (optional) ---
        int? points = null;
        if (!string.IsNullOrWhiteSpace(row.Points))
        {
            if (!int.TryParse(row.Points.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPoints)) { Fail($"Points '{row.Points.Trim()}' is not a whole number."); return; }
            points = parsedPoints;
        }
        var pointsError = WelfareController.PointsSignError(caseType, points);
        if (pointsError != null) { Fail(pointsError + "."); return; }

        // --- Tier (optional) ---
        WelfareTier? tier = null;
        if (!string.IsNullOrWhiteSpace(row.Tier))
        {
            if (!Enum.TryParse<WelfareTier>(row.Tier.Trim(), ignoreCase: true, out var parsedTier) || !Enum.IsDefined(parsedTier)) { Fail($"Unrecognized tier '{row.Tier.Trim()}' — use Low, Medium, or High."); return; }
            tier = parsedTier;
        }

        // --- Status (optional; Draft is never a valid imported state) ---
        var status = WelfareStatus.Resolved;
        if (!string.IsNullOrWhiteSpace(row.Status))
        {
            if (!TryParseStatus(row.Status, out status)) { Fail($"Unrecognized status '{row.Status.Trim()}' — use Open, UnderReview, ActionTaken, or Resolved."); return; }
        }

        // --- Student: matched by code within the organization, never created here ---
        var student = await _context.Students.FirstOrDefaultAsync(s =>
            s.OrganizationId == job.OrganizationId && s.StudentCode == studentCode && s.IsActive);
        if (student == null) { Fail($"No active student with code '{studentCode}' — import the roster first."); return; }
        entry.StudentName = student.FullName;
        entry.StudentId = student.Id;

        // --- Category: matched by name within organization + case type, never created here ---
        var lowerName = categoryName!.ToLowerInvariant();
        var category = await _context.WelfareCategories.FirstOrDefaultAsync(c =>
            c.OrganizationId == job.OrganizationId && c.CaseType == caseType && c.IsActive && c.Name.ToLower() == lowerName);
        if (category == null) { Fail($"No active {caseType} category named '{categoryName}' — add it under Welfare Categories first."); return; }

        // --- Duplicate guard: earlier in this file, or already on the ledger ---
        var dayStart = DateTime.SpecifyKind(occurredAt.Date, DateTimeKind.Utc);
        var dayEnd = dayStart.AddDays(1);
        var dupKey = $"{student.Id:N}|{(int)caseType}|{dayStart:yyyyMMdd}|{description.ToLowerInvariant()}";
        if (!seenKeys.Add(dupKey))
        {
            entry.Outcome = RosterImportRowOutcome.DuplicateInFile;
            entry.Message = "Same student, case type, date and description already appeared earlier in this file — skipped.";
            job.DuplicateCount++;
            _context.RosterImportJobEntries.Add(entry);
            return;
        }
        var alreadyOnLedger = await _context.WelfareRecords.AnyAsync(r =>
            r.StudentId == student.Id && r.CaseType == caseType
            && r.OccurredAt >= dayStart && r.OccurredAt < dayEnd
            && r.Description == description);
        if (alreadyOnLedger)
        {
            entry.Outcome = RosterImportRowOutcome.DuplicateInFile;
            entry.Message = "An identical record (same student, case type, date and description) is already on the ledger — skipped.";
            job.DuplicateCount++;
            _context.RosterImportJobEntries.Add(entry);
            return;
        }

        var record = new WelfareRecord
        {
            OrganizationId = job.OrganizationId,
            BranchId = student.BranchId,
            StudentId = student.Id,
            CategoryId = category.Id,
            CaseType = caseType,
            Tier = tier ?? category.DefaultTier,
            Points = points,
            Description = description,
            OccurredAt = occurredAt,
            Status = status,
            ActionTaken = string.IsNullOrWhiteSpace(row.ActionTaken) ? null : row.ActionTaken.Trim(),
            // SECURITY: server-forced, exactly as CreateRecord does — a safeguarding concern is
            // confidential regardless of what the spreadsheet says (it has no column for it).
            Confidential = caseType == WelfareCaseType.Welfare,
            ReportedByUserId = job.CreatedByUserId!.Value,
            CreatedBy = job.CreatedByUserId
        };
        _context.WelfareRecords.Add(record);

        entry.Outcome = RosterImportRowOutcome.Created;
        entry.Message = $"{caseType} record '{category.Name}' logged for {occurredAt:yyyy-MM-dd} ({status}).";
        job.CreatedCount++;
        _context.RosterImportJobEntries.Add(entry);
    }

    private static bool TryParseCaseType(string raw, out WelfareCaseType caseType)
    {
        var s = raw.Trim().ToLowerInvariant();
        switch (s)
        {
            case "achievement": case "achievements": case "merit": case "positive":
                caseType = WelfareCaseType.Achievement; return true;
            case "behavior": case "behaviour": case "incident": case "demerit": case "negative":
                caseType = WelfareCaseType.Behavior; return true;
            case "welfare": case "safeguarding": case "concern": case "wellbeing": case "well-being":
                caseType = WelfareCaseType.Welfare; return true;
        }
        caseType = default;
        return false;
    }

    private static bool TryParseStatus(string raw, out WelfareStatus status)
    {
        var s = raw.Trim().Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
        switch (s)
        {
            case "open": status = WelfareStatus.Open; return true;
            case "underreview": case "review": case "inreview": status = WelfareStatus.UnderReview; return true;
            case "actiontaken": case "action": case "inprogress": status = WelfareStatus.ActionTaken; return true;
            case "resolved": case "closed": case "complete": case "completed": status = WelfareStatus.Resolved; return true;
        }
        status = default;
        return false;
    }

    /// <summary>
    /// Parses a spreadsheet date cell to a UTC instant. Day-first formats win over the invariant
    /// fallback (see WelfareDateFormats); a bare date lands at midnight UTC. Always returns
    /// DateTimeKind.Utc — Npgsql rejects an Unspecified-kind value for this timestamptz column.
    /// </summary>
    private static bool TryParseOccurredAt(string raw, out DateTime occurredAt)
    {
        var s = raw.Trim();
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces;

        if (DateTime.TryParseExact(s, WelfareDateFormats, CultureInfo.InvariantCulture, styles, out occurredAt) ||
            DateTime.TryParse(s, CultureInfo.InvariantCulture, styles, out occurredAt))
        {
            occurredAt = DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc);
            return true;
        }

        // SheetJS with raw:false hands back a formatted string, but a CSV exported from some
        // systems carries the raw Excel serial (days since 1899-12-30) — accept that too.
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial > 20000 && serial < 80000)
        {
            occurredAt = DateTime.SpecifyKind(new DateTime(1899, 12, 30).AddDays(serial), DateTimeKind.Utc);
            return true;
        }

        occurredAt = default;
        return false;
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
