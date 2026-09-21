using QMgr.Application.Import;
using QMgr.Infrastructure.Services;
using System.Globalization;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using QMgr.API.Application.Services;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Identity;
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
    private readonly IActivityLogger _activity;
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

    // Staff imports (Kind = Staff) invite each new account by the existing reset-link email; these
    // two are what AuthController.SendPasswordResetEmailAsync uses, resolved here the same way.
    private readonly IEmailSender _emailSender;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly INotificationService _notifications;

    public RosterImportProcessorJob(
        QMgrDbContext context,
        IRosterImportBroadcaster broadcaster,
        IEmailSender emailSender,
        IPlatformSettingsService platformSettings,
        IDataProtectionProvider dataProtection,
        INotificationService notifications,
        IActivityLogger activity,
        ILogger<RosterImportProcessorJob> logger)
    {
        _context = context;
        _broadcaster = broadcaster;
        _activity = activity;
        _emailSender = emailSender;
        _platformSettings = platformSettings;
        _dataProtection = dataProtection;
        _notifications = notifications;
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

        if (job.Kind == RosterImportKind.Staff)
        {
            await ProcessStaffJobAsync(job);
            return;
        }

        if (job.Kind == RosterImportKind.Timetable)
        {
            await ProcessTimetableJobAsync(job);
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
        await RecordImportActivityAsync(job, ImportActivityActions.Failed,
            $"{job.Kind} import could not run: {reason}", new { job.Kind, Reason = reason, job.SourceFileName });
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

        await RecordImportActivityAsync(job, ImportActivityActions.Started,
            $"Started a {job.Kind} import of {rows.Count} row(s)"
                + (string.IsNullOrWhiteSpace(job.SourceFileName) ? "" : $" from \"{job.SourceFileName}\""),
            new { job.Kind, Rows = rows.Count, job.SourceFileName, job.Source });

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

        await RecordImportActivityAsync(job, ImportActivityActions.Completed,
            $"{job.Kind} import finished: {job.CreatedCount} created, {job.UpdatedCount} updated, "
                + $"{job.DuplicateCount} duplicate, {job.FailedCount} failed of {job.TotalRows} row(s)",
            new { job.Kind, job.TotalRows, job.CreatedCount, job.UpdatedCount, job.DuplicateCount, job.FailedCount, job.SourceFileName });
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
            entry.Outcome = RosterImportRowOutcome.AlreadyExists;
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
            // confidential regardless of what the spreadsheet says (it has no column for it, and
            // deliberately never will: a bulk upload is not the place to grant a visibility tier).
            Visibility = caseType == WelfareCaseType.Welfare ? WelfareVisibility.Confidential : WelfareVisibility.Standard,
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

    // ---------------------------------------------------------------------
    // Kind = Staff — bulk staff onboarding (Staff Performance Phase 6)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Creates User rows from a staff list and invites each by the reset-link flow. RowsJson holds
    /// the whole StartStaffImportRequest (rows + SendInvites). Two passes: rows first (create
    /// accounts, one entry per row), then line managers, because a manager may be a later row of
    /// the same file. The entry's StudentName column carries the person's name, StudentCode the
    /// employee number and GuardianName the email, so the per-row log reads without a join.
    /// </summary>
    /// <summary>
    /// A timetable export into a draft (duty rota plan §6.2, Phase 6). Each row resolves day, period, class, subject,
    /// teacher and room the way a master placing it by hand would, and is refused for the same reasons: not a teaching
    /// period that day, an unknown class, room or subject, a teacher who is not on the branch's staff, or a teacher
    /// already teaching something else then. Rows with the same day, period, subject and teacher for different classes
    /// become one joint lesson. Clashes a hand placement would allow (a class or room double-booked) are imported and
    /// left for the draft's diagnosis. The per-row log carries the slot in StudentCode, the class in StudentName and the
    /// teacher in GuardianName.
    /// </summary>
    private async Task ProcessTimetableJobAsync(RosterImportJob job)
    {
        TimetableImportPayload? payload;
        try { payload = JsonSerializer.Deserialize<TimetableImportPayload>(job.RowsJson); }
        catch (JsonException) { payload = null; }
        if (payload == null) { await FailJobAsync(job, "Could not read the uploaded rows (corrupted payload)."); return; }

        var timetable = await _context.Timetables.FirstOrDefaultAsync(t => t.Id == payload.TimetableId && t.BranchId == job.BranchId);
        if (timetable == null || timetable.Status != TimetableStatus.Draft)
        {
            await FailJobAsync(job, "The timetable is no longer a draft, so nothing was imported into it.");
            return;
        }

        var branchSettings = await _context.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == job.BranchId).Select(b => b.Settings).FirstOrDefaultAsync();
        var settings = new QMgr.Infrastructure.Services.TimetableSettingsService(_context).Read(branchSettings);
        var vocab = StudentsController.ReadVocabularies(branchSettings);
        var classes = vocab.Classes.Where(c => c.IsActive).GroupBy(c => TimetableCycle.Normalize(c.Name)).ToDictionary(g => g.Key, g => g.First().Name);
        var rooms = vocab.Rooms.Where(r => r.IsActive).GroupBy(r => TimetableCycle.Normalize(r.Name)).ToDictionary(g => g.Key, g => g.First().Name);
        var subjectRows = await _context.Subjects.IgnoreQueryFilters().AsNoTracking().Where(x => x.OrganizationId == job.OrganizationId && x.IsActive).ToListAsync();
        var staff = await StaffLookups.BranchStaff(_context, job.OrganizationId, job.BranchId)
            .Select(u => new { u.Id, u.Email, u.Username, u.FirstName, u.LastName }).ToListAsync();

        // Day names: the cycle's own labels ("mon", "mon a"), full weekday names in a one-week cycle, and the number.
        var days = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var teachingDays = TimetableCycle.TeachingDays(settings);
        for (var d = 1; d <= timetable.CycleDays; d++)
        {
            days[TimetableCycle.CycleDayLabel(settings, timetable.CycleDays, d)] = d;
            days[d.ToString(CultureInfo.InvariantCulture)] = d;
            if (TimetableCycle.WeekdayOf(settings, timetable.CycleDays, d) is { } w)
            {
                var full = w.Day.ToString();
                if (timetable.CycleDays == teachingDays.Count) days.TryAdd(full, d);
                days.TryAdd($"{full} {(char)('A' + w.Week)}", d);
            }
        }

        if (payload.ReplaceExisting)
        {
            var removed = await _context.TimetableLessons.Where(l => l.TimetableId == timetable.Id).ExecuteDeleteAsync();
            _logger.LogInformation("Timetable import {JobId}: removed {Count} existing lesson(s) from draft {TimetableId} first", job.Id, removed, timetable.Id);
        }

        var lessons = await _context.TimetableLessons.Where(l => l.TimetableId == timetable.Id).ToListAsync();
        var creator = job.CreatedByUserId;

        await RunRowsAsync(job, payload.Rows, async (row, rowNumber) =>
        {
            var entry = new RosterImportJobEntry
            {
                RosterImportJobId = job.Id, RowNumber = rowNumber,
                StudentCode = Truncate($"{row.Day} {row.Period}".Trim(), 50), StudentName = Truncate(row.Class, 200), GuardianName = Truncate(row.Teacher, 200)
            };
            void Fail(string message)
            {
                entry.Outcome = RosterImportRowOutcome.Failed;
                entry.Message = message;
                job.FailedCount++;
                _context.RosterImportJobEntries.Add(entry);
            }

            if (!days.TryGetValue((row.Day ?? "").Trim(), out var cycleDay)) { Fail($"Day '{row.Day}' is not a day of this timetable's cycle."); return; }
            var periods = TimetableCycle.LessonPeriodsOf(settings, timetable.CycleDays, cycleDay);
            var periodText = (row.Period ?? "").Trim();
            var startText = TimetableCycle.ParseTime(periodText.Length == 4 && periodText[1] == ':' ? "0" + periodText : periodText)?.ToString("HH:mm", CultureInfo.InvariantCulture);
            var period = periods.FirstOrDefault(p => string.Equals(p.Key, periodText, StringComparison.OrdinalIgnoreCase))
                         ?? periods.FirstOrDefault(p => string.Equals(p.Label, periodText, StringComparison.OrdinalIgnoreCase))
                         ?? (startText == null ? null : periods.FirstOrDefault(p => p.Start == startText));
            if (period == null) { Fail($"'{row.Period}' is not a teaching period on {TimetableCycle.CycleDayLabel(settings, timetable.CycleDays, cycleDay)}."); return; }

            if (!classes.TryGetValue(TimetableCycle.Normalize(row.Class), out var className)) { Fail($"'{row.Class}' is not a configured, active class."); return; }

            var subjectText = (row.Subject ?? "").Trim();
            var subject = subjectRows.FirstOrDefault(x => string.Equals(x.Code, subjectText, StringComparison.OrdinalIgnoreCase))
                          ?? subjectRows.FirstOrDefault(x => string.Equals(x.Name, subjectText, StringComparison.OrdinalIgnoreCase));
            if (subject == null) { Fail($"'{row.Subject}' is not an active subject (by code or name)."); return; }

            var teacherText = (row.Teacher ?? "").Trim();
            var byEmailOrUser = staff.Where(u => string.Equals(u.Email, teacherText, StringComparison.OrdinalIgnoreCase) || string.Equals(u.Username, teacherText, StringComparison.OrdinalIgnoreCase)).ToList();
            var byName = staff.Where(u => string.Equals($"{u.FirstName} {u.LastName}".Trim(), teacherText, StringComparison.OrdinalIgnoreCase)).ToList();
            var teachers = byEmailOrUser.Count > 0 ? byEmailOrUser : byName;
            if (teachers.Count == 0) { Fail($"'{row.Teacher}' is not on this branch's staff (by email, username or full name)."); return; }
            if (teachers.Count > 1) { Fail($"'{row.Teacher}' matches {teachers.Count} members of staff; use their email instead."); return; }
            var teacherId = teachers[0].Id;

            string? room = null;
            if (!string.IsNullOrWhiteSpace(row.Room))
            {
                if (!rooms.TryGetValue(TimetableCycle.Normalize(row.Room), out var roomName)) { Fail($"'{row.Room}' is not a configured room."); return; }
                room = roomName;
            }

            var inSlot = lessons.Where(l => l.CycleDay == cycleDay && string.Equals(l.PeriodKey, period.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            var classNorm = TimetableCycle.Normalize(className);
            if (inSlot.Any(l => l.TeacherUserId == teacherId && l.ClassNameNormalized == classNorm && l.SubjectId == subject.Id))
            {
                entry.Outcome = RosterImportRowOutcome.DuplicateInFile;
                entry.Message = "Already in the draft — skipped.";
                job.DuplicateCount++;
                _context.RosterImportJobEntries.Add(entry);
                return;
            }
            var mine = inSlot.Where(l => l.TeacherUserId == teacherId).ToList();
            if (mine.Any(l => l.SubjectId != subject.Id))
            {
                Fail($"{teachers[0].FirstName} {teachers[0].LastName} already teaches {mine[0].ClassName} then.");
                return;
            }

            // The same teacher, subject and slot for another class: a joint lesson — one group, no clash with itself.
            Guid? groupId = null;
            if (mine.Count > 0)
            {
                groupId = mine.Select(l => l.GroupId).FirstOrDefault(g => g != null) ?? Guid.NewGuid();
                foreach (var l in mine) l.GroupId = groupId;
            }

            var lesson = new TimetableLesson
            {
                TimetableId = timetable.Id, CycleDay = cycleDay, PeriodKey = period.Key, ClassName = className, ClassNameNormalized = classNorm,
                SubjectId = subject.Id, TeacherUserId = teacherId, Room = room, RoomNormalized = room == null ? null : TimetableCycle.Normalize(room),
                GroupId = groupId, CreatedBy = creator
            };
            _context.TimetableLessons.Add(lesson);
            lessons.Add(lesson);
            entry.Outcome = RosterImportRowOutcome.Created;
            entry.Message = $"{TimetableCycle.CycleDayLabel(settings, timetable.CycleDays, cycleDay)} {period.Key}: {className} {subject.Code}{(groupId != null ? " (joint lesson)" : "")}{(room == null ? "" : $", {room}")}";
            job.CreatedCount++;
            _context.RosterImportJobEntries.Add(entry);
        },
        row => new RosterImportJobEntry { StudentCode = Truncate($"{row.Day} {row.Period}".Trim(), 50), StudentName = Truncate(row.Class, 200), GuardianName = Truncate(row.Teacher, 200) });

        timetable.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    private static string Truncate(string? value, int max) => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private async Task ProcessStaffJobAsync(RosterImportJob job)
    {
        if (!job.CreatedByUserId.HasValue)
        {
            await FailJobAsync(job, "A staff import must be attributed to a signed-in user — this job has no creator.");
            return;
        }

        StaffImportJobPayload? request;
        try { request = JsonSerializer.Deserialize<StaffImportJobPayload>(job.RowsJson); }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "RosterImportJob {JobId}: failed to deserialize RowsJson as StartStaffImportRequest", job.Id);
            request = null;
        }
        if (request == null) { await FailJobAsync(job, "Could not read the uploaded rows (corrupted payload)."); return; }

        var context = await StaffImportContext.LoadAsync(_context, job.OrganizationId, job.BranchId);
        var created = new List<(Guid UserId, string? LineManagerEmail)>();
        var protector = _dataProtection.CreateProtector(TemporaryPasswords.ImportProtectorPurpose).ToTimeLimitedDataProtector();

        try
        {
            await RunRowsAsync(job, request.Rows,
                (row, rowNumber) =>
                {
                    var index = rowNumber - 1;
                    // A payload written before per-row delivery existed has no ResolvedDelivery: fall back to SendInvites.
                    var mode = request.ResolvedDelivery.TryGetValue(index, out var m) ? m : (request.SendInvites ? StaffImportDeliveryMode.Invitation : null);
                    string? temporary = null;
                    if (request.ProtectedTemporaryPasswords.TryGetValue(index, out var sealedPassword))
                    {
                        try { temporary = protector.Unprotect(sealedPassword); }
                        catch (Exception ex) { _logger.LogWarning(ex, "RosterImportJob {JobId}: a temporary password could not be unsealed (key expired?)", job.Id); }
                    }
                    return ProcessStaffRowAsync(job, row, rowNumber, mode, temporary, context, created, request.NameOrder, request.UpdateExisting);
                },
                row => new RosterImportJobEntry { StudentName = $"{row.FirstName} {row.LastName}".Trim(), StudentCode = row.EmployeeNumber, GuardianName = row.Email });
        }
        finally
        {
            // The sealed temporary passwords have done their work: rewrite the payload without them, so the
            // job row never keeps even an encrypted copy (plan §12.2).
            if (request.ProtectedTemporaryPasswords.Count > 0)
            {
                try
                {
                    request.ProtectedTemporaryPasswords.Clear();
                    var sealedJob = await _context.RosterImportJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
                    if (sealedJob != null)
                    {
                        sealedJob.RowsJson = JsonSerializer.Serialize(request);
                        await _context.SaveChangesAsync();
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "RosterImportJob {JobId}: could not strip the sealed temporary passwords from the payload", job.Id); }
            }
        }

        // Second pass: line managers, now that every row's account exists. Matched by normalized
        // email against the organization's users (existing or just created).
        var linked = 0;
        foreach (var (userId, managerEmail) in created.Where(c => !string.IsNullOrWhiteSpace(c.LineManagerEmail)))
        {
            try
            {
                // A line manager is named by their email address, or — since most school staff have
                // none — by their staff number, which is unique within the organization.
                var normalized = RegistrationIdentity.NormalizeEmail(managerEmail);
                var byNumber = (managerEmail ?? string.Empty).Trim();
                if (normalized == null && byNumber.Length == 0) continue;
                var managerId = await _context.Users.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => u.OrganizationId == job.OrganizationId && u.IsActive && u.Id != userId
                                && ((normalized != null && u.NormalizedEmail == normalized)
                                    || (byNumber.Length > 0 && u.EmployeeNumber == byNumber)))
                    .Select(u => (Guid?)u.Id)
                    .FirstOrDefaultAsync();
                if (managerId == null)
                {
                    _logger.LogWarning("RosterImportJob {JobId}: line manager {Email} not found for user {UserId}", job.Id, managerEmail, userId);
                    continue;
                }
                await _context.Users.IgnoreQueryFilters().Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.LineManagerUserId, managerId).SetProperty(u => u.UpdatedAt, DateTime.UtcNow));
                linked++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RosterImportJob {JobId}: could not set line manager for user {UserId}", job.Id, userId);
            }
        }

        _logger.LogInformation("RosterImportJob {JobId} (Staff): {Linked} line manager link(s) set in the second pass", job.Id, linked);
    }

    /// <summary>What every staff row needs and none should re-query: the roles a row may name, the departments by code, the organization's name for the invitation.</summary>
    private sealed class StaffImportContext
    {
        public Dictionary<string, Domain.Entities.Identity.Role> RolesByCode { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Guid> DepartmentsByCode { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string OrganizationName { get; private set; } = "your organization";
        public string BaseUrl { get; set; } = "https://qmgr.app";

        public static async Task<StaffImportContext> LoadAsync(QMgrDbContext db, Guid organizationId, Guid branchId)
        {
            var ctx = new StaffImportContext();

            // System roles and this organization's own — never the platform SuperAdmin, never the
            // Tenant Admin: an import file must not be able to mint an administrator.
            var roles = await db.Roles.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.IsActive && (r.OrganizationId == null || r.OrganizationId == organizationId))
                .ToListAsync();
            foreach (var r in roles.Where(r => !RoleCodes.IsSuperAdmin(r.Code) && !RoleCodes.IsAdmin(r.Code)))
                ctx.RolesByCode.TryAdd(r.Code, r);

            var departments = await db.Departments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.OrganizationId == organizationId && d.IsActive && (d.BranchId == null || d.BranchId == branchId))
                .Select(d => new { d.Code, d.Id })
                .ToListAsync();
            foreach (var d in departments.Where(d => !string.IsNullOrWhiteSpace(d.Code)))
                ctx.DepartmentsByCode.TryAdd(d.Code.Trim(), d.Id);

            ctx.OrganizationName = await db.Organizations.IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.Id == organizationId).Select(o => o.BrandName ?? o.Name).FirstOrDefaultAsync() ?? ctx.OrganizationName;
            return ctx;
        }
    }

    /// <summary>
    /// The person is already here and the import was told to update them. ONLY their details move, and
    /// a value the file leaves blank is left alone rather than blanked: a staff list that happens not
    /// to carry a column must never erase what somebody typed into the product. Role, permissions,
    /// branch, username, email and password are untouched by design.
    /// </summary>
    private async Task UpdateExistingStaffAsync(RosterImportJob job, StaffImportRow row, RosterImportJobEntry entry,
        Guid userId, string firstName, string lastName)
    {
        var user = await _context.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            entry.Outcome = RosterImportRowOutcome.Failed;
            entry.Message = "That account could not be re-read to update it.";
            job.FailedCount++;
            _context.RosterImportJobEntries.Add(entry);
            return;
        }

        var changed = new List<string>();
        void Set(string field, string? value, Action<string> apply, string? current)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var v = value.Trim();
            if (string.Equals(v, current, StringComparison.Ordinal)) return;
            apply(v);
            changed.Add(field);
        }

        Set("first name", firstName, v => user.FirstName = v, user.FirstName);
        Set("surname", lastName, v => user.LastName = v, user.LastName);
        Set("phone", row.Phone, v => user.Phone = v, user.Phone);
        Set("employee number", row.EmployeeNumber, v => user.EmployeeNumber = v, user.EmployeeNumber);
        Set("job title", row.JobTitle, v => user.JobTitle = v, user.JobTitle);
        Set("qualification", row.Qualification, v => user.Qualification = v, user.Qualification);
        Set("registration number", row.TeachingRegistrationNumber, v => user.TeachingRegistrationNumber = v, user.TeachingRegistrationNumber);
        Set("national ID", row.NationalId, v => user.NationalId = v, user.NationalId);
        Set("emergency contact", row.EmergencyContactName, v => user.EmergencyContactName = v, user.EmergencyContactName);
        Set("emergency phone", row.EmergencyContactPhone, v => user.EmergencyContactPhone = v, user.EmergencyContactPhone);

        if (StaffFieldParsing.Date(row.StartDate) is { } start && user.EmploymentStartDate != start)
        { user.EmploymentStartDate = start; changed.Add("start date"); }
        if (StaffFieldParsing.Date(row.EndDate) is { } end && user.EmploymentEndDate != end)
        { user.EmploymentEndDate = end; changed.Add("end date"); }
        if (StaffFieldParsing.Date(row.DateOfBirth) is { } dob && user.DateOfBirth != dob)
        { user.DateOfBirth = dob; changed.Add("date of birth"); }
        if (StaffFieldParsing.EmploymentType(row.EmploymentType) is { } terms && user.EmploymentType != terms)
        { user.EmploymentType = terms; changed.Add("employment terms"); }
        if (StaffFieldParsing.Sex(row.Sex) is { } sex && user.Sex != sex)
        { user.Sex = sex; changed.Add("sex"); }

        user.UpdatedAt = DateTime.UtcNow;
        entry.Outcome = RosterImportRowOutcome.Updated;
        entry.Message = changed.Count == 0
            ? "Already here, and nothing in the file was different."
            : "Updated: " + string.Join(", ", changed) + ".";
        job.UpdatedCount++;
        _context.RosterImportJobEntries.Add(entry);
    }

    private async Task ProcessStaffRowAsync(RosterImportJob job, StaffImportRow row, int rowNumber, StaffImportDeliveryMode? delivery, string? temporaryPassword, StaffImportContext ctx, List<(Guid, string?)> created, NameOrder nameOrder = NameOrder.GivenFirst, bool updateExisting = false)
    {
        var sendInvites = delivery == StaffImportDeliveryMode.Invitation;
        var firstName = (row.FirstName ?? "").Trim();
        var lastName = (row.LastName ?? "").Trim();

        // A row may arrive with ONE combined name instead of two — that is how most school exports are
        // written ("Staff Name: Abaho Jude"), and the browser has usually split it already. This is the
        // same split, by the same shared rule, for a caller that did not: an integration posting rows
        // straight at the API, or an older client. The server never invents the order; it uses the one
        // the batch carries. See docs/plans/BULK_IMPORT_SYSTEM.md §4.1.
        if ((firstName.Length == 0 || lastName.Length == 0) && !string.IsNullOrWhiteSpace(row.FullName))
        {
            var parts = PersonName.Split(row.FullName, nameOrder);
            if (firstName.Length == 0) firstName = parts.GivenName;
            if (lastName.Length == 0) lastName = parts.FamilyName;
        }
        var email = (row.Email ?? "").Trim();
        var entry = new RosterImportJobEntry
        {
            RosterImportJobId = job.Id,
            RowNumber = rowNumber,
            StudentName = $"{firstName} {lastName}".Trim(),
            StudentCode = string.IsNullOrWhiteSpace(row.EmployeeNumber) ? null : row.EmployeeNumber.Trim(),
            GuardianName = string.IsNullOrWhiteSpace(email) ? null : email
        };

        void Fail(string message)
        {
            entry.Outcome = RosterImportRowOutcome.Failed;
            entry.Message = message;
            job.FailedCount++;
            _context.RosterImportJobEntries.Add(entry);
        }

        var missing = new List<string>();
        if (firstName.Length == 0 && lastName.Length == 0) missing.Add("name");
        if (missing.Count > 0) { Fail($"Missing required field(s): {string.Join(", ", missing)}."); return; }

        // AN EMAIL ADDRESS IS OPTIONAL — most staff on a school roll have none. It is required only
        // for an INVITATION, which has nowhere to go without one; a slip or an SMS needs no address
        // and the person signs in with the username built below. docs/plans/STAFF_WITHOUT_EMAIL.md.
        string? normalizedEmail = null;
        if (email.Length > 0)
        {
            normalizedEmail = RegistrationIdentity.NormalizeEmail(email);
            // The SAME shape check the browser ran (ImportRules), so the preview and the import cannot
            // disagree about what an address is, plus the normalisation that identity is keyed on.
            if (normalizedEmail == null || !ImportRules.LooksLikeEmail(email)) { Fail($"'{email}' is not a valid email address."); return; }
        }
        else if (delivery == StaffImportDeliveryMode.Invitation)
        {
            Fail("No email address, so an invitation cannot be sent. Give them one, or import with temporary password slips or SMS.");
            return;
        }

        var roleCode = string.IsNullOrWhiteSpace(row.RoleCode) ? RoleCodes.Teacher : row.RoleCode.Trim();
        if (!ctx.RolesByCode.TryGetValue(roleCode, out var role))
        {
            Fail(RoleCodes.IsSuperAdmin(roleCode) || RoleCodes.IsAdmin(roleCode)
                ? $"Role '{roleCode}' cannot be assigned by import — add administrators one at a time."
                : $"Unrecognized role code '{roleCode}' — use a system role such as teacher, support-staff or head-of-department, or one of this organization's own roles.");
            return;
        }

        // The import refuses a role above the importer's (plan §13.17) — the same guard the join approval uses.
        var roleRefusal = await RoleAssignmentGuard.RefusalAsync(_context, job.CreatedByUserId!.Value, role);
        if (roleRefusal != null) { Fail(roleRefusal); return; }

        if (delivery is StaffImportDeliveryMode.Slips or StaffImportDeliveryMode.Sms && string.IsNullOrEmpty(temporaryPassword))
        {
            Fail("This row's temporary password could not be read back (the import waited too long in the queue). Re-issue access for this person from the Onboarding page.");
            return;
        }

        // Existing account, by email (globally unique). Left untouched unless the person importing
        // asked for their details to be updated — and even then only their DETAILS: role, permissions,
        // branch and password are never an import's to change. Somebody belonging to another tenant is
        // never updated from here whatever was asked, because they are not this organization's to edit.
        // WHO IS THIS PERSON ALREADY: the address where there is one, otherwise the school's own
        // staff number, which carries a per-organization unique index for exactly this reason. A row
        // with neither cannot be recognised at all and is always treated as somebody new.
        var employeeNumber = string.IsNullOrWhiteSpace(row.EmployeeNumber) ? null : row.EmployeeNumber.Trim();
        var existing = normalizedEmail != null
            ? await _context.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.NormalizedEmail == normalizedEmail)
                .Select(u => new { u.Id, u.OrganizationId })
                .FirstOrDefaultAsync()
            : employeeNumber == null ? null
            : await _context.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.OrganizationId == job.OrganizationId && u.EmployeeNumber == employeeNumber)
                .Select(u => new { u.Id, u.OrganizationId })
                .FirstOrDefaultAsync();
        if (existing != null)
        {
            if (updateExisting && existing.OrganizationId == job.OrganizationId)
            {
                await UpdateExistingStaffAsync(job, row, entry, existing.Id, firstName, lastName);
                return;
            }

            entry.Outcome = RosterImportRowOutcome.AlreadyExists;
            entry.Message = existing.OrganizationId == job.OrganizationId
                ? (normalizedEmail != null
                    ? "An account with this email already exists in this organization — left unchanged."
                    : $"Staff number {employeeNumber} already belongs to somebody in this organization — left unchanged.")
                : "An account with this email already exists elsewhere on the platform — left unchanged.";
            job.DuplicateCount++;
            _context.RosterImportJobEntries.Add(entry);
            return;
        }

        // Username: the one given, else the email's local part; made unique with a numeric suffix
        // because idx_users_username is global.
        var baseUsername = string.IsNullOrWhiteSpace(row.Username)
            ? PersonName.SuggestUsername(firstName, lastName, email, employeeNumber)
            : PersonName.SuggestUsername(null, null, row.Username.Trim() + "@x", null);
        var username = baseUsername;
        for (var suffix = 2; await _context.Users.IgnoreQueryFilters().AnyAsync(u => u.Username == username); suffix++)
            username = $"{baseUsername}{suffix}";

        // Departments by code; an unknown code is reported, not fatal — the person still gets an account.
        var departmentIds = new List<Guid>();
        var unknownCodes = new List<string>();
        foreach (var code in (row.DepartmentCodes ?? new List<string>()).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct())
        {
            if (ctx.DepartmentsByCode.TryGetValue(code, out var id)) departmentIds.Add(id);
            else unknownCodes.Add(code);
        }

        // A random password nobody knows; the invitation (or a later "forgot password") sets the real one.
        var randomPassword = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var user = new Domain.Entities.Identity.User
        {
            Id = Guid.NewGuid(),
            OrganizationId = job.OrganizationId,
            Username = username,
            // Null, never "": two staff with no address must both be storable, and an empty
            // string would collide on the unique index the second time.
            Email = email.Length == 0 ? null : email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(randomPassword),
            FirstName = firstName,
            LastName = lastName,
            Phone = string.IsNullOrWhiteSpace(row.Phone) ? null : row.Phone.Trim(),
            EmployeeNumber = entry.StudentCode,
            JobTitle = string.IsNullOrWhiteSpace(row.JobTitle) ? null : row.JobTitle.Trim(),
            // The staff record, parsed by the SAME code the browser previewed with
            // (StaffFieldParsing in Q-Mgr.Shared) so the two answers cannot disagree.
            EmploymentStartDate = StaffFieldParsing.Date(row.StartDate),
            EmploymentEndDate = StaffFieldParsing.Date(row.EndDate),
            EmploymentType = StaffFieldParsing.EmploymentType(row.EmploymentType),
            Qualification = StaffFieldParsing.Text(row.Qualification, 120),
            TeachingRegistrationNumber = StaffFieldParsing.Text(row.TeachingRegistrationNumber, 60),
            DateOfBirth = StaffFieldParsing.Date(row.DateOfBirth),
            Sex = StaffFieldParsing.Sex(row.Sex),
            NationalId = StaffFieldParsing.Text(row.NationalId, 40),
            EmergencyContactName = StaffFieldParsing.Text(row.EmergencyContactName, 120),
            EmergencyContactPhone = StaffFieldParsing.Text(row.EmergencyContactPhone, 40),
            RoleId = role.Id,
            AssignedBranchId = job.BranchId,
            DepartmentIds = departmentIds.Count > 0 ? departmentIds.ToArray() : null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = job.CreatedByUserId
        };

        string? inviteNote = null;
        if (!string.IsNullOrEmpty(temporaryPassword))
        {
            // A temporary password (plan §12.2–12.3): hashed only, forced change at first sign-in, 72 hours.
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(temporaryPassword);
            user.MustChangePassword = true;
            user.TemporaryPasswordExpiresAt = DateTime.UtcNow.Add(TemporaryPasswords.Lifetime);
        }
        else if (sendInvites)
        {
            // Exactly AuthController.ForgotPassword's token, with a 7-day expiry rather than 1 hour:
            // an invitation waits for someone who may not open their mail today.
            user.PasswordResetToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
            user.PasswordResetTokenExpiry = DateTime.UtcNow.AddDays(7);
        }

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        if (sendInvites && string.IsNullOrEmpty(temporaryPassword))
        {
            var delivered = await SendStaffInviteAsync(user, ctx);
            inviteNote = delivered ? " Invitation sent." : " Invitation could not be sent — they can use Forgot password.";
        }
        else if (delivery == StaffImportDeliveryMode.Slips)
        {
            inviteNote = " Temporary password on the printed slip.";
        }
        else if (delivery == StaffImportDeliveryMode.Sms)
        {
            if (string.IsNullOrWhiteSpace(user.Phone)) inviteNote = " No phone number, so no SMS — print this person's slip.";
            else
            {
                if (ctx.BaseUrl == "https://qmgr.app")
                    ctx.BaseUrl = await _platformSettings.GetPublicWebBaseUrlAsync();
                var sms = await _notifications.SendSmsAsync(job.OrganizationId, user.Phone,
                    StaffOnboardingController.TemporaryPasswordSms(user.Username, temporaryPassword!, ctx.BaseUrl));
                inviteNote = sms.IsSent ? " Temporary password sent by SMS." : $" The SMS was not sent ({sms.Reason ?? sms.Outcome.ToString()}) — print this person's slip.";
            }
        }

        // Class-teacher and subject assignments from the row (plan §12.5), with the rules the class-teachers page applies.
        var assignmentNotes = new List<string>();
        if (job.BranchId != Guid.Empty)
        {
            assignmentNotes.AddRange(await TeachingAssignments.ApplyClassTeacherOfAsync(_context, job.OrganizationId, job.BranchId, user.Id,
                TeachingAssignments.SplitClasses(row.ClassTeacherOf), job.CreatedByUserId!.Value));
            assignmentNotes.AddRange(await TeachingAssignments.ApplyTeachesAsync(_context, job.OrganizationId, job.BranchId, user.Id, row.Teaches, job.CreatedByUserId!.Value));
        }
        if (assignmentNotes.Count > 0) inviteNote = (inviteNote ?? "") + " " + string.Join(" ", assignmentNotes);

        created.Add((user.Id, string.IsNullOrWhiteSpace(row.LineManagerEmail) ? null : row.LineManagerEmail.Trim()));

        entry.Outcome = RosterImportRowOutcome.Created;
        entry.Message = $"Created as {role.Name} ({username})."
                        + (departmentIds.Count > 0 ? $" {departmentIds.Count} department(s)." : "")
                        + (unknownCodes.Count > 0 ? $" Unknown department code(s) ignored: {string.Join(", ", unknownCodes)}." : "")
                        + (inviteNote ?? "");
        entry.NewValue = user.Id.ToString();
        job.CreatedCount++;
        _context.RosterImportJobEntries.Add(entry);
    }

    /// <summary>The reset-link email, worded as an invitation. Same link shape as AuthController.SendPasswordResetEmailAsync so /reset-password handles it unchanged. Never throws.</summary>
    private async Task<bool> SendStaffInviteAsync(Domain.Entities.Identity.User user, StaffImportContext ctx)
    {
        try
        {
            if (ctx.BaseUrl == "https://qmgr.app")
                ctx.BaseUrl = await _platformSettings.GetPublicWebBaseUrlAsync();
            var resetUrl = $"{ctx.BaseUrl}/reset-password?email={Uri.EscapeDataString(user.Email ?? string.Empty)}&token={Uri.EscapeDataString(user.PasswordResetToken!)}";

            var subject = $"You have been added to {ctx.OrganizationName} on {Email.EmailTemplates.AppName} — set your password";
            var html = Email.EmailTemplates.Layout(
                $"Welcome to {ctx.OrganizationName}",
                user.FirstName,
                new[]
                {
                    $"An account has been created for you on {Email.EmailTemplates.B(ctx.OrganizationName)}'s {Email.EmailTemplates.AppName} workspace, signed in as {Email.EmailTemplates.B(user.Username)} ({Email.EmailTemplates.P(user.Email)}).",
                    "Choose your password with the button below. The link is valid for 7 days; after that, use \"Forgot password\" on the sign-in page to get a new one."
                },
                "Set my password",
                resetUrl,
                footerNote: "If you were not expecting this, you can ignore it; no account is usable until a password is set.",
                showLinkFallback: true);

            return await _emailSender.SendAsync(user.Email, subject, html);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Staff import: invitation to {Email} could not be sent", user.Email);
            return false;
        }
    }


    /// <summary>
    /// The audit row a bulk import leaves behind. Written from the SHARED row loop, so every kind
    /// — roster, welfare, staff, timetable, batch — gets one without each remembering to.
    ///
    /// <para>The live SignalR progress says what is happening WHILE the job runs; this says that it
    /// happened, afterwards, to somebody reading the log next week. An import can create hundreds of
    /// login accounts in one go or backfill a school's entire welfare history, and until 2026-09-18
    /// it wrote nothing at all.</para>
    ///
    /// <para>The actor is the person who STARTED the import, passed explicitly: this runs in a
    /// Hangfire worker where there is no HttpContext and no claims to read one from. Never throws —
    /// IActivityLogger swallows and logs, and an audit row must not fail the import it describes.</para>
    /// </summary>
    private async Task RecordImportActivityAsync(RosterImportJob job, string action, string summary, object? detail)
    {
        await _activity.RecordAsync(
            action,
            entityType: $"import:{job.Kind}",
            entityId: job.Id,
            subjectUserId: null,
            summary: summary,
            detail: detail,
            branchId: job.BranchId,
            organizationId: job.OrganizationId,
            actorUserId: job.CreatedByUserId);
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
