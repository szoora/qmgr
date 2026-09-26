using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// THE IMPORT INBOX (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E11, 2026-09-26). A document is read once, in the
/// browser, and each of its tables is routed to the section that owns it. NOTHING IS WRITTEN ON UPLOAD: the job is a
/// <see cref="RosterImportJob"/> of kind <see cref="RosterImportKind.Inbox"/> whose RowsJson holds the staged sections —
/// no new table, the standing enhance-before-add rule. Each section is approved by a holder of its permission, and
/// approving it runs THAT importer's own commit (the programme import for events, meetings and rota; the staff, roll and
/// timetable importers for a handed-off table), so every rule an importer already enforces still holds.
///
/// <para><b>Four eyes is a school's choice</b> (<see cref="ImportSettingsDto.RequireSecondApprover"/>, off by default):
/// on, the person who uploaded a document may not approve any part of it — NIST SP 800-53 AC-5, and this codebase's
/// own rule that a meeting cannot adopt its own minutes. They may still WITHDRAW (reject) their own upload.</para>
///
/// <para><b>A decision is claimed under a lock</b> (<c>import-inbox:{job}</c>) and re-read inside it, so two approvers
/// pressing at once cannot both commit one section — the second finds it already decided.</para>
/// </summary>
public interface IImportInbox
{
    Task<ImportSettingsDto> SettingsAsync(Guid organizationId, CancellationToken ct = default);
    Task SaveSettingsAsync(Guid organizationId, ImportSettingsDto settings, CancellationToken ct = default);

    /// <summary>Stages a document and tells each section's owners. Returns the job, or a refusal in words.</summary>
    Task<(Guid? JobId, string? Refusal)> SubmitAsync(Guid organizationId, Guid branchId, Guid userId, SubmitImportRequest request,
        Func<string, Task<bool>> has, CancellationToken ct = default);

    Task<List<ImportInboxJobDto>> ListAsync(Guid organizationId, Guid branchId, Guid userId, Func<string, Task<bool>> has, bool awaitingOnly, CancellationToken ct = default);

    /// <summary>The staged content of one section, for its approver. Null when the caller may not see it.</summary>
    Task<ImportInboxStagedDto?> StagedAsync(Guid organizationId, Guid branchId, Guid jobId, string sectionKey, Func<string, Task<bool>> has, CancellationToken ct = default);

    /// <summary>
    /// Decides a section INSIDE the caller's transaction: locks the job, re-reads it, checks the section is still waiting
    /// and that this person may decide it, and records the decision. Returns a refusal in words, or null.
    /// </summary>
    Task<string?> DecideInTransactionAsync(Guid organizationId, Guid branchId, Guid jobId, string sectionKey, Guid userId, string state,
        string? note, Guid? resultJobId, Func<string, Task<bool>> has, CancellationToken ct = default);
}

public class ImportInbox : IImportInbox
{
    public const string SettingsKey = "Imports";
    private const int MaxSections = 40;
    private const int MaxCsvChars = 2_000_000;

    private readonly QMgrDbContext _db;
    private readonly INotificationService _notifications;
    private readonly ILogger<ImportInbox> _logger;

    public ImportInbox(QMgrDbContext db, INotificationService notifications, ILogger<ImportInbox> logger)
    {
        _db = db;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>The permission that owns — approves — a section.</summary>
    public static IReadOnlyList<string> PermissionsFor(string kind) => kind switch
    {
        ImportSectionKinds.Events => new[] { Permissions.CalendarManage },
        ImportSectionKinds.Meetings or ImportSectionKinds.Rota => new[] { Permissions.StaffDutiesManage },
        ImportSectionKinds.Timetable => new[] { Permissions.TimetableManage },
        ImportSectionKinds.Staff => new[] { Permissions.StaffStructureManage, Permissions.UsersCreate },
        ImportSectionKinds.Students => new[] { Permissions.StudentsManage },
        _ => new[] { Permissions.SettingsEdit }
    };

    public static async Task<bool> OwnsAsync(string kind, Func<string, Task<bool>> has)
    {
        foreach (var p in PermissionsFor(kind)) if (!await has(p)) return false;
        return true;
    }

    // ---- Settings --------------------------------------------------------------------------------------------

    public async Task<ImportSettingsDto> SettingsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var json = await _db.Organizations.IgnoreQueryFilters().AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.Settings).FirstOrDefaultAsync(ct);
        return ReadSettings(json);
    }

    public static ImportSettingsDto ReadSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ImportSettingsDto();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (root != null && root.TryGetValue(SettingsKey, out var e) && e.ValueKind == JsonValueKind.Object)
                return JsonSerializer.Deserialize<ImportSettingsDto>(e.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ImportSettingsDto();
        }
        catch (JsonException) { }
        return new ImportSettingsDto();
    }

    public async Task SaveSettingsAsync(Guid organizationId, ImportSettingsDto settings, CancellationToken ct = default)
        => await OrganizationSettingsLock.MutateAsync(_db, organizationId, org =>
        {
            org.Settings = OrganizationSettingsLock.WithKey(org.Settings, SettingsKey, settings);
            return true;
        });

    // ---- Submitting ------------------------------------------------------------------------------------------

    public async Task<(Guid? JobId, string? Refusal)> SubmitAsync(Guid organizationId, Guid branchId, Guid userId, SubmitImportRequest request,
        Func<string, Task<bool>> has, CancellationToken ct = default)
    {
        var sections = (request.Sections ?? new()).Where(s => s != null).ToList();
        if (sections.Count == 0) return (null, "Nothing to send: no table was routed to a section.");
        if (sections.Count > MaxSections) return (null, $"At most {MaxSections} tables in one document.");
        foreach (var s in sections)
        {
            if (!ImportSectionKinds.All.Contains(s.Kind)) return (null, $"\"{s.Title}\" was routed to a section that does not exist.");
            if (ImportSectionKinds.IsProgramme(s.Kind) && s.Programme == null) return (null, $"\"{s.Title}\" has nothing to import.");
            if (ImportSectionKinds.IsHandoff(s.Kind) && string.IsNullOrWhiteSpace(s.Csv)) return (null, $"\"{s.Title}\" has no rows.");
            if ((s.Csv?.Length ?? 0) > MaxCsvChars) return (null, $"\"{s.Title}\" is too large to hold for approval — import it directly.");
        }

        // Anybody who owns at least one section of what they upload may upload it; nobody else has a reason to.
        var ownsAny = false;
        foreach (var s in sections) ownsAny |= await OwnsAsync(s.Kind, has);
        if (!ownsAny) return (null, "You do not approve any of the sections this document was routed to, so it cannot be sent from your account.");

        var payload = new ImportInboxPayload
        {
            SourceFiles = (request.SourceFiles ?? new()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Take(20).ToList(),
            Sections = sections.Select((s, i) => new StagedSection
            {
                Key = $"s{i + 1}",
                Kind = s.Kind,
                Title = Trim(s.Title, 200),
                RouteReason = s.RouteReason == null ? null : Trim(s.RouteReason, 400),
                RowCount = Math.Max(0, s.RowCount),
                Programme = s.Programme is { } p ? p with { Preview = false, InboxJobId = null, InboxSection = null } : null,
                Csv = s.Csv,
                FileName = s.FileName == null ? null : Trim(s.FileName, 255)
            }).ToList()
        };

        var job = new RosterImportJob
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            CreatedByUserId = userId == Guid.Empty ? null : userId,
            SourceFileName = Trim(string.Join(" · ", payload.SourceFiles), 255),
            Kind = RosterImportKind.Inbox,
            Status = RosterImportStatus.Pending,
            TotalRows = payload.Sections.Sum(s => s.RowCount),
            RowsJson = JsonSerializer.Serialize(payload)
        };
        _db.RosterImportJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        await TellOwnersAsync(organizationId, branchId, userId, job.Id, payload, ct);
        return (job.Id, null);
    }

    /// <summary>ONE notice per owner, naming every section of the document they approve. Never the uploader.</summary>
    private async Task TellOwnersAsync(Guid organizationId, Guid branchId, Guid uploader, Guid jobId, ImportInboxPayload payload, CancellationToken ct)
    {
        try
        {
            var perPerson = new Dictionary<Guid, List<string>>();
            foreach (var group in payload.Sections.GroupBy(s => s.Kind))
            {
                List<Guid>? owners = null;
                foreach (var permission in PermissionsFor(group.Key))
                {
                    var holders = await NotificationAudience.HoldersAsync(_db, organizationId, permission, ct, branchId);
                    owners = owners == null ? holders : owners.Intersect(holders).ToList();
                }
                foreach (var id in owners ?? new())
                {
                    if (id == uploader) continue;
                    if (!perPerson.TryGetValue(id, out var list)) perPerson[id] = list = new();
                    list.Add(ImportSectionKinds.Label(group.Key));
                }
            }
            var files = payload.SourceFiles.Count == 0 ? "A document" : string.Join(", ", payload.SourceFiles.Take(3));
            foreach (var (userId, labels) in perPerson)
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = userId,
                    OrganizationId = organizationId,
                    BranchId = branchId,
                    Title = "A document is waiting for your approval",
                    Message = $"{files}: {string.Join(", ", labels.Distinct())}. Nothing is added until it is approved.",
                    Type = NotificationType.Imports,
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    EventKey = NotificationEventKeys.ImportsAwaitingReview,
                    ActionUrl = $"/imports?job={jobId}",
                    IconClass = "inbox"
                }, ct);
            }
        }
        catch (Exception ex)
        {
            // The document is staged; a notice that did not land is a degraded success, never a failed upload.
            _logger.LogError(ex, "Import inbox {JobId}: could not tell the owners", jobId);
        }
    }

    // ---- Reading ---------------------------------------------------------------------------------------------

    public async Task<List<ImportInboxJobDto>> ListAsync(Guid organizationId, Guid branchId, Guid userId, Func<string, Task<bool>> has, bool awaitingOnly, CancellationToken ct = default)
    {
        var jobs = await _db.RosterImportJobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.OrganizationId == organizationId && j.BranchId == branchId && j.Kind == RosterImportKind.Inbox
                        && (!awaitingOnly || j.Status == RosterImportStatus.Pending))
            .OrderByDescending(j => j.CreatedAt).Take(100).ToListAsync(ct);
        var settings = await SettingsAsync(organizationId, ct);
        var owns = new Dictionary<string, bool>();
        foreach (var kind in ImportSectionKinds.All) owns[kind] = await OwnsAsync(kind, has);

        var payloads = jobs.Select(j => (Job: j, Payload: Read(j.RowsJson))).ToList();
        var names = await StaffLookups.LoadNamesAsync(_db, payloads.SelectMany(p => p.Payload.Sections.Select(s => s.DecidedByUserId)).Concat(jobs.Select(j => j.CreatedByUserId)), ct);

        var result = new List<ImportInboxJobDto>();
        foreach (var (job, payload) in payloads)
        {
            // A person sees a document when they uploaded it or approve any part of it.
            if (job.CreatedByUserId != userId && !payload.Sections.Any(s => owns.GetValueOrDefault(s.Kind))) continue;
            result.Add(new ImportInboxJobDto
            {
                Id = job.Id,
                BranchId = job.BranchId,
                CreatedAt = job.CreatedAt,
                CreatedByUserId = job.CreatedByUserId,
                CreatedByName = job.CreatedByUserId.HasValue ? names[job.CreatedByUserId] : null,
                SourceFiles = payload.SourceFiles,
                RequireSecondApprover = settings.RequireSecondApprover,
                Done = payload.Sections.All(s => s.State != ImportSectionStates.Awaiting),
                Sections = payload.Sections.Select(s =>
                {
                    var whyNot = WhyNot(s, job.CreatedByUserId, userId, owns.GetValueOrDefault(s.Kind), settings);
                    return new ImportInboxSectionDto
                    {
                        Key = s.Key, Kind = s.Kind, Title = s.Title, RowCount = s.RowCount, RouteReason = s.RouteReason, State = s.State,
                        DecidedByName = s.DecidedByUserId.HasValue ? names[s.DecidedByUserId] : null, DecidedAt = s.DecidedAt, Note = s.Note,
                        ResultJobId = s.ResultJobId, Handoff = ImportSectionKinds.IsHandoff(s.Kind),
                        CanDecide = whyNot == null, WhyNot = whyNot
                    };
                }).ToList()
            });
        }
        return result;
    }

    public async Task<ImportInboxStagedDto?> StagedAsync(Guid organizationId, Guid branchId, Guid jobId, string sectionKey, Func<string, Task<bool>> has, CancellationToken ct = default)
    {
        var job = await _db.RosterImportJobs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.OrganizationId == organizationId && j.BranchId == branchId && j.Kind == RosterImportKind.Inbox, ct);
        var section = job == null ? null : Read(job.RowsJson).Sections.FirstOrDefault(s => s.Key == sectionKey);
        if (section == null || !await OwnsAsync(section.Kind, has)) return null;
        return new ImportInboxStagedDto { Kind = section.Kind, Programme = section.Programme, Csv = section.Csv, FileName = section.FileName };
    }

    // ---- Deciding --------------------------------------------------------------------------------------------

    public async Task<string?> DecideInTransactionAsync(Guid organizationId, Guid branchId, Guid jobId, string sectionKey, Guid userId, string state,
        string? note, Guid? resultJobId, Func<string, Task<bool>> has, CancellationToken ct = default)
    {
        var lockKey = $"import-inbox:{jobId}";
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)", ct);

        var job = await _db.RosterImportJobs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.OrganizationId == organizationId && j.BranchId == branchId && j.Kind == RosterImportKind.Inbox, ct);
        if (job == null) return "That document is not in the inbox.";
        await _db.Entry(job).ReloadAsync(ct);
        var payload = Read(job.RowsJson);
        var section = payload.Sections.FirstOrDefault(s => s.Key == sectionKey);
        if (section == null) return "That part of the document is not in the inbox.";
        if (section.State != ImportSectionStates.Awaiting)
            return section.State == ImportSectionStates.Approved ? "Somebody has already approved this part." : "This part was rejected.";

        var settings = await SettingsAsync(organizationId, ct);
        var owns = await OwnsAsync(section.Kind, has);
        // The uploader may always WITHDRAW their own upload; approving is the owner's act, and with four eyes on, never theirs.
        var withdrawing = state == ImportSectionStates.Rejected && job.CreatedByUserId == userId;
        if (!withdrawing && WhyNot(section, job.CreatedByUserId, userId, owns, settings) is { } refusal) return refusal;

        section.State = state;
        section.DecidedByUserId = userId == Guid.Empty ? null : userId;
        section.DecidedAt = DateTime.UtcNow;
        section.Note = string.IsNullOrWhiteSpace(note) ? null : Trim(note, 500);
        section.ResultJobId = resultJobId;
        if (state == ImportSectionStates.Approved) { section.Programme = null; section.Csv = null; } // the importer's own job holds it now
        job.RowsJson = JsonSerializer.Serialize(payload);
        job.ProcessedRows = payload.Sections.Where(s => s.State != ImportSectionStates.Awaiting).Sum(s => s.RowCount);
        if (payload.Sections.All(s => s.State != ImportSectionStates.Awaiting))
        {
            job.Status = payload.Sections.Any(s => s.State == ImportSectionStates.Rejected) ? RosterImportStatus.CompletedWithErrors : RosterImportStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return null;
    }

    private static string? WhyNot(StagedSection s, Guid? uploader, Guid userId, bool owns, ImportSettingsDto settings)
    {
        if (s.State != ImportSectionStates.Awaiting) return "Already decided.";
        if (!owns) return $"Approved by holders of {string.Join(" and ", PermissionsFor(s.Kind))}.";
        if (settings.RequireSecondApprover && uploader.HasValue && uploader == userId) return "A second person must approve what you uploaded.";
        return null;
    }

    // ---- Payload ---------------------------------------------------------------------------------------------

    public static ImportInboxPayload Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ImportInboxPayload();
        try { return JsonSerializer.Deserialize<ImportInboxPayload>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ImportInboxPayload(); }
        catch (JsonException) { return new ImportInboxPayload(); }
    }

    private static string Trim(string s, int max) { s = s.Trim(); return s.Length <= max ? s : s[..max]; }
}

public sealed class ImportInboxPayload
{
    public List<string> SourceFiles { get; set; } = new();
    public List<StagedSection> Sections { get; set; } = new();
}

public sealed class StagedSection
{
    public string Key { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? RouteReason { get; set; }
    public int RowCount { get; set; }
    public string State { get; set; } = ImportSectionStates.Awaiting;
    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Note { get; set; }
    public Guid? ResultJobId { get; set; }
    public ProgrammeImportRequest? Programme { get; set; }
    public string? Csv { get; set; }
    public string? FileName { get; set; }
}
