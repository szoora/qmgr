using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Import.Programme;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Calendar;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The programme import (plan TERM_PROGRAMME_CALENDAR_AND_GATES §7–§8): the page reads the documents in the browser
/// session and sends what the reader decided; THIS re-checks every row itself — nothing the page concluded is trusted —
/// and either answers what would happen (<c>Preview</c>, which writes NOTHING) or writes it as one batch with one undo.
///
/// <list type="bullet">
/// <item><b>Gates (D8).</b> <c>calendar.manage</c> for anything; a request carrying meetings or rota slots ALSO needs
/// <c>staff.duties.manage</c> and the Welfare &amp; Performance module, because those become staff duties with
/// registers and reminders — the calendar itself is base product.</item>
/// <item><b>Re-import is the normal case.</b> An event is found by its <c>SourceKey</c>, then by title and first day; a
/// meeting by title and day; a rota slot by rota name, person and day. Identical → Unchanged; different → Update,
/// naming the FIELDS that move. Nothing a new file omits is deleted.</item>
/// <item><b>One batch.</b> The commit runs through the execution strategy, in one transaction, under
/// <c>pg_advisory_xact_lock</c> on the branch's rota key — the same key the rota generator takes — and re-evaluates
/// every row INSIDE the lock, so two simultaneous commits of one body produce one set of rows and the second finds
/// everything Unchanged.</item>
/// <item><b>One notice per person</b>, after the commit: "You are Teacher on Duty on Wed 23 Sep and Wed 11 Nov." —
/// never one per slot.</item>
/// <item><b>Undo</b> removes the events and cancels the duties carrying the job's id, EXCEPT a duty whose register was
/// opened or that any record points at — the timetable's rule: a register taken is history.</item>
/// </list>
///
/// The job and audit row is a <see cref="RosterImportJob"/> of kind <see cref="RosterImportKind.Programme"/> — the
/// table every import already uses — with its file names in <c>SourceFileName</c> and a small summary in
/// <c>RowsJson</c>. No column was added for it.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
public class ProgrammeImportController : StaffPerformanceControllerBase
{
    private const string AliasesKey = "ImportAliases";
    private const string ActionCommitted = "calendar.import.committed";
    private const string ActionUndone = "calendar.import.undone";
    private const int MaxEvents = 1000;
    private const int MaxMeetings = 300;
    private const int MaxRotaSlots = 1500;
    private const int MaxSpanDays = 92;
    private const int DefaultMeetingMinutes = 60;

    private readonly IStaffPerformancePolicyService _policy;
    private readonly INotificationService _notifications;
    private readonly IModuleAccessService _modules;
    private readonly IImportInbox _inbox;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ProgrammeImportController> _logger;

    private static readonly JsonSerializerOptions ReadJson = new() { PropertyNameCaseInsensitive = true };

    public ProgrammeImportController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        INotificationService notifications,
        IModuleAccessService modules,
        IImportInbox inbox,
        IWebHostEnvironment environment,
        ILogger<ProgrammeImportController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _inbox = inbox;
        _policy = policy;
        _notifications = notifications;
        _modules = modules;
        _environment = environment;
        _logger = logger;
    }

    // ---- What the page needs --------------------------------------------------------------------------

    [HttpGet("branches/{branchId:guid}/calendar/import/context")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(ProgrammeImportContextDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetContext(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);

        var staff = await StaffLookups.BranchStaff(Db, organizationId, branchId)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Username, u.Email, u.EmployeeNumber, u.Phone, u.AlternatePhone, u.JobTitle, u.DepartmentIds, u.OrganizationId })
            .ToListAsync();
        var people = staff.Select(u => new StaffCandidate
        {
            UserId = u.Id,
            FullName = PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName, u.Username),
            FirstName = u.FirstName,
            LastName = u.LastName,
            EmployeeNumber = u.EmployeeNumber,
            Email = u.Email,
            Username = u.Username,
            PhoneKey = StaffNameResolver.PhoneKey(organizationId, u.Phone) ?? StaffNameResolver.PhoneKey(organizationId, u.AlternatePhone),
            JobTitle = u.JobTitle,
            DepartmentIds = u.DepartmentIds?.ToList() ?? new List<Guid>(),
            IsActive = true
        }).OrderBy(p => p.FullName, StringComparer.OrdinalIgnoreCase).ToList();

        var departments = await Db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.IsActive && (d.BranchId == null || d.BranchId == branchId))
            .Select(d => new OfficeDepartment(d.Id, d.Name, d.Code, d.HeadUserId))
            .ToListAsync();

        var settingsJson = await OrganizationSettingsJsonAsync(organizationId);
        var policy = _policy.ReadPolicy(settingsJson);
        var dutiesRefusal = await DutiesRefusalAsync(organizationId);

        return Ok(new ProgrammeImportContextDto
        {
            OrganizationId = organizationId,
            People = people,
            Departments = departments,
            Aliases = ReadAliases(settingsJson),
            Settings = CalendarSettingsStore.Read(settingsJson),
            National = await NationalCalendarStore.GetAsync(Db),
            Terms = policy.Periods?.OrderBy(p => p.Start).ToList() ?? new List<PerformancePeriodDto>(),
            CanImportDuties = dutiesRefusal == null,
            DutiesRefusal = dutiesRefusal,
            Audience = await CalendarController.AudienceOptionsAsync(Db, _policy, organizationId, branchId)
        });
    }

    [HttpGet("calendar/import/aliases")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(ImportAliasesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAliases()
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();
        return Ok(ReadAliases(await OrganizationSettingsJsonAsync(organizationId.Value)));
    }

    // ---- Preview and commit -----------------------------------------------------------------------------

    /// <summary>
    /// Preview or commit. Gated in code rather than by attribute (2026-09-26): an import made directly needs
    /// calendar.manage as it always did; one that commits a section of a document waiting in the Import inbox needs the
    /// permission that OWNS that section — a holder of staff.duties.manage approving the meetings of a programme need not
    /// also keep the calendar. The inbox checks that, under its own lock, inside this commit's transaction.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/calendar/import")]
    [ProducesResponseType(typeof(ProgrammeImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProgrammeImportResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Import(Guid branchId, [FromBody] ProgrammeImportRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);

        request.Events ??= new();
        request.Meetings ??= new();
        request.RotaSlots ??= new();
        request.SourceFiles ??= new();

        if (request.InboxJobId is { } inboxJob)
        {
            if (await _inbox.StagedAsync(organizationId, branchId, inboxJob, request.InboxSection ?? string.Empty, HasPermissionAsync) == null)
                return NotFoundProblem("That part of the document is not waiting for you.");
        }
        else if (!await HasPermissionAsync(Permissions.CalendarManage))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "You cannot import the term programme",
                Detail = "Importing needs permission to manage the calendar. Send the document for approval from the Import inbox instead.",
                Status = StatusCodes.Status403Forbidden
            });
        if (request.Events.Count + request.Meetings.Count + request.RotaSlots.Count == 0 && request.Term == null)
            return BadRequestProblem("Nothing to import", "The documents gave no events, meetings or rota slots.");
        if (request.Events.Count > MaxEvents || request.Meetings.Count > MaxMeetings || request.RotaSlots.Count > MaxRotaSlots)
            return BadRequestProblem("Too many rows in one import",
                $"Import at most {MaxEvents} events, {MaxMeetings} meetings and {MaxRotaSlots} rota slots at a time — split the documents.");

        // D8: staff duties are Welfare & Performance, and need the duty permission.
        if (request.Meetings.Count > 0 || request.RotaSlots.Count > 0)
        {
            if (!await HasPermissionAsync(Permissions.StaffDutiesManage))
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "You cannot import meetings or duty rotas",
                    Detail = "Meetings and rota slots become staff duties with registers, and need permission to manage duties. Leave them out to import the events only.",
                    Status = StatusCodes.Status403Forbidden
                });
            if (!IsSuperAdmin && !await _modules.IsModuleActiveAsync(organizationId, ModuleCodes.StudentWelfare))
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "MODULE_NOT_PURCHASED",
                    module = ModuleCodes.StudentWelfare,
                    message = "Meetings and duty rotas are part of Welfare & Performance. Add it from Billing, or leave them out to import the events only.",
                    purchaseUrl = BillingLinks.Modules
                });
        }

        if (request.Preview)
        {
            var preview = await EvaluateAsync(branchId, organizationId, request, meetingParameterId: null);
            return Ok(preview.ToResult(preview: true, jobId: null));
        }

        // Written outside the batch, as the rota generator does: a parameter that already exists is only read.
        var meetingParameterId = request.Meetings.Count > 0 ? await EnsureMeetingParameterAsync(organizationId) : (Guid?)null;
        var rotaParameterId = request.RotaSlots.Count > 0 ? await StaffRota.EnsureTeacherOnDutyParameterAsync(Db, organizationId, _logger) : Guid.Empty;

        var me = CurrentUserId();
        Evaluation? plan = null;
        RosterImportJob? job = null;
        var createdDuties = new List<StaffDuty>();
        var createdEvents = new List<SchoolEvent>();
        var termUpdated = false;
        string? inboxRefusal = null;

        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            foreach (var stale in createdDuties) Db.Entry(stale).State = EntityState.Detached;
            foreach (var stale in createdEvents) Db.Entry(stale).State = EntityState.Detached;
            if (job != null) Db.Entry(job).State = EntityState.Detached;
            createdDuties.Clear();
            createdEvents.Clear();
            termUpdated = false;

            await using var tx = await Db.Database.BeginTransactionAsync();
            var lockKey = $"staff-rota:{branchId}";
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");

            // An inbox section is CLAIMED before anything is written, in this transaction: two approvers pressing at once
            // cannot both commit it, and a refusal (already decided, the uploader under four eyes) writes nothing at all.
            var jobId = Guid.NewGuid();
            inboxRefusal = null;
            if (request.InboxJobId is { } inbox)
            {
                inboxRefusal = await _inbox.DecideInTransactionAsync(organizationId, branchId, inbox, request.InboxSection ?? string.Empty, me,
                    ImportSectionStates.Approved, null, jobId, HasPermissionAsync);
                if (inboxRefusal != null) return;
            }

            // Re-read under the lock: a commit that ran a moment ago has made these rows Unchanged.
            plan = await EvaluateAsync(branchId, organizationId, request, meetingParameterId);
            job = new RosterImportJob
            {
                Id = jobId,
                OrganizationId = organizationId,
                BranchId = branchId,
                CreatedByUserId = me == Guid.Empty ? null : me,
                SourceFileName = Truncate(string.Join(" · ", request.SourceFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim())), 255),
                Kind = RosterImportKind.Programme,
                Status = RosterImportStatus.Completed,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
            Db.RosterImportJobs.Add(job);

            await ApplyAsync(plan, job, branchId, organizationId, me, meetingParameterId, rotaParameterId, createdEvents, createdDuties);

            job.TotalRows = plan.Rows.Count;
            job.ProcessedRows = plan.Rows.Count;
            job.CreatedCount = plan.EventsCreated + plan.MeetingsCreated + plan.RotaCreated;
            job.UpdatedCount = plan.EventsUpdated + plan.MeetingsUpdated;
            job.DuplicateCount = plan.Unchanged;
            job.FailedCount = plan.Refused;
            if (plan.Refused > 0) job.Status = RosterImportStatus.CompletedWithErrors;
            job.RowsJson = JsonSerializer.Serialize(new ProgrammeJobSummary
            {
                SourceFiles = request.SourceFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList(),
                EventsCreated = plan.EventsCreated,
                EventsUpdated = plan.EventsUpdated,
                MeetingsCreated = plan.MeetingsCreated,
                MeetingsUpdated = plan.MeetingsUpdated,
                RotaSlotsCreated = plan.RotaCreated,
                // Import health (E10) and undo (B13) read these back.
                RefusedRecorded = true,
                Refused = RefusedRows(request, plan),
                Matched = plan.Events.Where(x => x.ExistingId.HasValue).Select(x => x.ExistingId!.Value)
                    .Concat(plan.Meetings.Where(x => x.ExistingId.HasValue).Select(x => x.ExistingId!.Value))
                    .Concat(plan.Rota.Where(x => x.ExistingId.HasValue).Select(x => x.ExistingId!.Value)).Distinct().ToList(),
                NoRegisterReasons = request.Events.Where(e => !string.IsNullOrWhiteSpace(e.NoRegisterReason) && !string.IsNullOrWhiteSpace(e.SourceKey))
                    .GroupBy(e => e.SourceKey.Trim()).ToDictionary(g => g.Key, g => Truncate(g.First().NoRegisterReason!.Trim(), 300))
            });

            // Term dates and learned aliases: the one writer of Organization.Settings, inside this transaction.
            if (request.Term != null || request.LearnedAliases != null)
                termUpdated = await WriteSettingsAsync(organizationId, request, plan);

            await Db.SaveChangesAsync();

            // The snapshots of UPDATED rows, stamped with the UpdatedAt this save gave them: undo
            // restores a row only while it still carries that stamp, i.e. nobody has edited it since.
            if (plan.MeetingsBefore.Count > 0 || plan.EventsBefore.Count > 0)
            {
                var summary = ReadSummary(job.RowsJson);
                summary.MeetingsBefore = plan.MeetingsBefore.Select(x => x.Before with { AppliedUpdatedAt = x.Entity.UpdatedAt }).ToList();
                summary.EventsBefore = plan.EventsBefore.Select(x => x.Before with { AppliedUpdatedAt = x.Entity.UpdatedAt }).ToList();
                job.RowsJson = JsonSerializer.Serialize(summary);
                await Db.SaveChangesAsync();
            }
            await tx.CommitAsync();
        });

        if (inboxRefusal != null) return ConflictProblem(inboxRefusal);
        if (plan == null || job == null) return StatusCode(StatusCodes.Status500InternalServerError);

        await Activity.RecordAsync(ActionCommitted, nameof(RosterImportJob), job.Id, null,
            string.Create(CultureInfo.InvariantCulture,
                $"Programme imported from {request.SourceFiles.Count} file(s): {plan.EventsCreated} event(s), {plan.MeetingsCreated} meeting(s), {plan.RotaCreated} rota slot(s) created; {plan.Unchanged} unchanged."),
            new { job.Id, plan.EventsCreated, plan.EventsUpdated, plan.MeetingsCreated, plan.RotaCreated, plan.Unchanged, plan.Refused }, branchId, organizationId);

        var notified = request.NotifyPeople ? await NotifyPeopleAsync(branchId, organizationId, createdDuties, createdEvents) : 0;
        var result = plan.ToResult(preview: false, jobId: job.Id) with { PeopleNotified = notified, TermUpdated = termUpdated };
        return StatusCode(StatusCodes.Status201Created, result);
    }

    // ---- History and undo --------------------------------------------------------------------------------

    [HttpGet("branches/{branchId:guid}/calendar/import/jobs")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(List<ProgrammeImportJobDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetJobs(Guid branchId, [FromQuery] int limit = 30)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);

        var jobs = await Db.RosterImportJobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.OrganizationId == organizationId && j.BranchId == branchId && j.Kind == RosterImportKind.Programme)
            .OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(limit, 1, 100))
            .ToListAsync();
        var jobIds = jobs.Select(j => j.Id).ToList();
        var protectedCounts = await ProtectedDutiesQuery(organizationId, jobIds)
            .GroupBy(d => d.ImportJobId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        var names = await BuildNamesAsync(jobs.Select(j => j.CreatedByUserId));
        var attention = await AttentionCountsAsync(organizationId, jobs);

        return Ok(jobs.Select(j =>
        {
            var summary = ReadSummary(j.RowsJson);
            return new ProgrammeImportJobDto
            {
                Id = j.Id,
                CreatedAt = j.CreatedAt,
                CreatedByName = j.CreatedByUserId.HasValue ? names[j.CreatedByUserId] : null,
                SourceFiles = summary.SourceFiles.Count > 0 ? summary.SourceFiles : (j.SourceFileName ?? string.Empty).Split(" · ", StringSplitOptions.RemoveEmptyEntries).ToList(),
                EventsCreated = summary.EventsCreated,
                MeetingsCreated = summary.MeetingsCreated,
                RotaSlotsCreated = summary.RotaSlotsCreated,
                Undone = summary.UndoneAt.HasValue,
                UndoneAt = summary.UndoneAt,
                Protected = protectedCounts.TryGetValue(j.Id, out var p) ? p : 0,
                NeedsAttention = summary.UndoneAt.HasValue ? 0 : attention.GetValueOrDefault(j.Id)
            };
        }).ToList());
    }

    // ---- Import health (E10): what an import left without a register ------------------------------------

    /// <summary>
    /// What one import left for somebody to finish: meetings it wrote as calendar events only, registers with nobody
    /// named to take them, and the rows the server refused. "Give this a register" on the calendar turns such an event
    /// into a meeting with a register, so an earlier upload is repaired without importing it again.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/calendar/import/jobs/{jobId:guid}/health")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(ProgrammeImportHealthDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Health(Guid branchId, Guid jobId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var job = await Db.RosterImportJobs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.OrganizationId == organizationId && j.BranchId == branchId && j.Kind == RosterImportKind.Programme);
        if (job == null) return NotFoundProblem("Import not found");

        var summary = ReadSummary(job.RowsJson);
        var zone = await ZoneAsync(branchId);
        var today = QMgr.Application.BranchClock.Today(zone);

        var events = await EventOnlyMeetingsAsync(organizationId, new List<Guid> { jobId });
        var withoutRecorder = await Db.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.ImportJobId == jobId && d.IsActive && d.Kind == DutyKind.Session && d.RecorderUserIds.Length == 0)
            .OrderBy(d => d.StartsAt).ToListAsync();

        return Ok(new ProgrammeImportHealthDto
        {
            JobId = job.Id,
            CreatedAt = job.CreatedAt,
            SourceFiles = summary.SourceFiles,
            MeetingsWithoutRegister = events.Select(e => new ImportHealthItemDto
            {
                EventId = e.Id,
                Title = e.Title,
                Date = e.StartsOn,
                StartTime = SchoolEventMapping.Time(e.StartTime),
                Location = e.Location,
                ResponsibleText = e.ResponsibleText,
                Reason = (e.SourceKey != null ? summary.NoRegisterReasons.GetValueOrDefault(e.SourceKey) : null)
                         ?? "Imported as a calendar event only, so nobody takes a register.",
                InPast = e.StartsOn < today
            }).ToList(),
            RegistersWithoutRecorder = withoutRecorder.Select(d =>
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(d.StartsAt, zone);
                return new ImportHealthItemDto
                {
                    DutyId = d.Id,
                    Title = d.Title,
                    Date = DateOnly.FromDateTime(local),
                    StartTime = local.ToString("HH:mm", CultureInfo.InvariantCulture),
                    Location = d.Location,
                    Reason = "Nobody is named to take the register, so only duty managers can.",
                    InPast = DateOnly.FromDateTime(local) < today
                };
            }).ToList(),
            Refused = summary.Refused,
            RefusalsRecorded = summary.RefusedRecorded
        });
    }

    /// <summary>Events an import made that read as a staff meeting and have no live register.</summary>
    private async Task<List<SchoolEvent>> EventOnlyMeetingsAsync(Guid organizationId, List<Guid> jobIds)
    {
        var events = await Db.SchoolEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.ImportJobId != null && jobIds.Contains(e.ImportJobId.Value)
                        && e.IsActive && e.Status == SchoolEventStatus.Scheduled
                        && (e.DutyId == null || !Db.StaffDuties.Any(d => d.Id == e.DutyId && d.IsActive)))
            .OrderBy(e => e.StartsOn).ThenBy(e => e.StartTime)
            .ToListAsync();
        return events.Where(e => ProgrammeTableParser.IsStaffMeeting(e.Title)).ToList();
    }

    private async Task<Dictionary<Guid, int>> AttentionCountsAsync(Guid organizationId, List<RosterImportJob> jobs)
    {
        var ids = jobs.Select(j => j.Id).ToList();
        var counts = ids.ToDictionary(id => id, _ => 0);
        if (ids.Count == 0) return counts;
        foreach (var e in await EventOnlyMeetingsAsync(organizationId, ids)) counts[e.ImportJobId!.Value]++;
        foreach (var d in await Db.StaffDuties.IgnoreQueryFilters().AsNoTracking()
                     .Where(d => d.OrganizationId == organizationId && d.ImportJobId != null && ids.Contains(d.ImportJobId.Value)
                                 && d.IsActive && d.Kind == DutyKind.Session && d.RecorderUserIds.Length == 0)
                     .Select(d => d.ImportJobId!.Value).ToListAsync())
            counts[d]++;
        foreach (var j in jobs) counts[j.Id] += ReadSummary(j.RowsJson).Refused.Count;
        return counts;
    }

    /// <summary>The refused rows of a commit, named: the title and date the reader would recognise, and the reason.</summary>
    private static List<ImportHealthItemDto> RefusedRows(ProgrammeImportRequest request, Evaluation plan)
        => plan.Rows.Where(r => r.Outcome == ProgrammeRowOutcome.Refused).Select(r =>
        {
            var (title, date) = r.Kind switch
            {
                "event" => request.Events.Where(e => e.SourceKey?.Trim() == r.SourceKey).Select(e => (e.Title, e.StartsOn)).FirstOrDefault(),
                "meeting" => request.Meetings.Where(m => m.SourceKey?.Trim() == r.SourceKey).Select(m => (m.Title, m.Date)).FirstOrDefault(),
                _ => request.RotaSlots.Where(s => s.SourceKey?.Trim() == r.SourceKey).Select(s => (s.RotaName, s.StartsOn)).FirstOrDefault()
            };
            return new ImportHealthItemDto { Title = title ?? r.SourceKey, Date = date, Reason = r.Reason ?? "Refused." };
        }).ToList();

    [HttpPost("branches/{branchId:guid}/calendar/import/jobs/{jobId:guid}/undo")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(ProgrammeUndoResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Undo(Guid branchId, Guid jobId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);

        var exists = await Db.RosterImportJobs.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(j => j.Id == jobId && j.OrganizationId == organizationId && j.BranchId == branchId && j.Kind == RosterImportKind.Programme);
        if (!exists) return NotFoundProblem("Import not found");

        // Undoing duties is the same act as importing them (D8).
        var hasDuties = await Db.StaffDuties.IgnoreQueryFilters().AnyAsync(d => d.OrganizationId == organizationId && d.ImportJobId == jobId && d.IsActive);
        if (hasDuties && !await HasPermissionAsync(Permissions.StaffDutiesManage))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "You cannot undo meetings or duty rotas",
                Detail = "This import created staff duties, and removing them needs permission to manage duties.",
                Status = StatusCodes.Status403Forbidden
            });

        var eventsRemoved = 0;
        var dutiesRemoved = 0;
        var dutiesKept = 0;
        var updatesRestored = 0;
        var updatesLeft = new List<string>();
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            eventsRemoved = dutiesRemoved = dutiesKept = updatesRestored = 0;
            updatesLeft.Clear();
            Db.ChangeTracker.Clear();
            await using var tx = await Db.Database.BeginTransactionAsync();
            var lockKey = $"staff-rota:{branchId}";
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");

            var job = await Db.RosterImportJobs.IgnoreQueryFilters().FirstAsync(j => j.Id == jobId);
            var kept = (await ProtectedDutiesQuery(organizationId, new List<Guid> { jobId }).Select(d => d.Id).ToListAsync()).ToHashSet();

            // B13: a LATER import that found these rows "Unchanged" relies on them — undoing this one must not pull them
            // out from under it. Matched ids are recorded on every commit since 2026-09-26.
            var laterJobs = await Db.RosterImportJobs.IgnoreQueryFilters().AsNoTracking()
                .Where(j => j.OrganizationId == organizationId && j.BranchId == branchId && j.Kind == RosterImportKind.Programme
                            && j.Id != jobId && j.CreatedAt > job.CreatedAt)
                .Select(j => new { j.CreatedAt, j.RowsJson }).ToListAsync();
            var reliedOn = new Dictionary<Guid, DateTime>();
            foreach (var later in laterJobs)
            {
                var laterSummary = ReadSummary(later.RowsJson);
                if (laterSummary.UndoneAt != null) continue;
                // A re-submission that created and changed nothing (the same document sent twice) built nothing on these
                // rows; it is the same import, not a later one. Only an import that did something of its own relies on them.
                if (laterSummary.EventsCreated + laterSummary.EventsUpdated + laterSummary.MeetingsCreated
                    + laterSummary.MeetingsUpdated + laterSummary.RotaSlotsCreated == 0) continue;
                foreach (var id in laterSummary.Matched) reliedOn.TryAdd(id, later.CreatedAt);
            }

            var duties = await Db.StaffDuties.IgnoreQueryFilters()
                .Where(d => d.OrganizationId == organizationId && d.ImportJobId == jobId && d.IsActive)
                .ToListAsync();
            var now = DateTime.UtcNow;
            var me = CurrentUserId();
            foreach (var d in duties)
            {
                // B13: the register's own lock, then look again — a register opened a moment ago is history now.
                var registerKey = $"staff-register:{d.Id}";
                await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({registerKey})::bigint)");
                if (!kept.Contains(d.Id))
                {
                    await Db.Entry(d).ReloadAsync();
                    if (d.RegisterOpenedAt != null || await Db.StaffPerformanceRecords.IgnoreQueryFilters().AnyAsync(r => r.DutyId == d.Id)) kept.Add(d.Id);
                }
                if (kept.Contains(d.Id)) { dutiesKept++; continue; }
                if (reliedOn.TryGetValue(d.Id, out var reliedAt))
                {
                    updatesLeft.Add($"{d.Title} (the import of {reliedAt:dd MMM yyyy} relies on it)");
                    dutiesKept++;
                    continue;
                }
                d.IsActive = false;
                d.UpdatedAt = now;
                d.UpdatedBy = me == Guid.Empty ? null : me;
                dutiesRemoved++;
            }

            var events = await Db.SchoolEvents.IgnoreQueryFilters()
                .Where(e => e.OrganizationId == organizationId && e.ImportJobId == jobId)
                .ToListAsync();
            foreach (var e in events)
            {
                // An event that IS a meeting whose register was taken stays with it.
                if (e.DutyId is { } dutyId && kept.Contains(dutyId)) continue;
                if (reliedOn.TryGetValue(e.Id, out var reliedAt))
                {
                    updatesLeft.Add($"{e.Title} (the import of {reliedAt:dd MMM yyyy} relies on it)");
                    continue;
                }
                Db.SchoolEvents.Remove(e);
                eventsRemoved++;
            }

            var summary = ReadSummary(job.RowsJson);

            // Rows the import UPDATED go back to what they were (2026-09-25). A row somebody has
            // edited since keeps its edit and is named; so does a meeting whose register was taken.
            foreach (var before in summary.MeetingsBefore)
            {
                var d = await Db.StaffDuties.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == before.Id && x.OrganizationId == organizationId);
                if (d == null) continue;
                if (!SameStamp(d.UpdatedAt, before.AppliedUpdatedAt)) { updatesLeft.Add($"{d.Title} (changed since the import)"); continue; }
                if (d.RegisterOpenedAt != null || await Db.StaffPerformanceRecords.IgnoreQueryFilters().AnyAsync(r => r.DutyId == d.Id))
                { updatesLeft.Add($"{d.Title} (its register was taken)"); continue; }
                d.Title = before.Title;
                d.StartsAt = before.StartsAt;
                d.EndsAt = before.EndsAt;
                d.Location = before.Location;
                d.ExpectedUserIds = before.ExpectedUserIds;
                d.RecorderUserIds = before.RecorderUserIds ?? Array.Empty<Guid>();
                d.UpdatedBy = me == Guid.Empty ? null : me;
                updatesRestored++;
            }
            foreach (var before in summary.EventsBefore)
            {
                var e = await Db.SchoolEvents.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == before.Id && x.OrganizationId == organizationId);
                if (e == null) continue;
                if (!SameStamp(e.UpdatedAt, before.AppliedUpdatedAt)) { updatesLeft.Add($"{e.Title} (changed since the import)"); continue; }
                before.ApplyTo(e);
                e.UpdatedBy = me == Guid.Empty ? null : me;
                updatesRestored++;
            }

            summary.UndoneAt = now;
            summary.EventsRemoved = eventsRemoved;
            summary.DutiesRemoved = dutiesRemoved;
            summary.DutiesKept = dutiesKept;
            summary.UpdatesRestored = updatesRestored;
            summary.UpdatesLeft = updatesLeft.ToList();
            job.RowsJson = JsonSerializer.Serialize(summary);

            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        });

        await Activity.RecordAsync(ActionUndone, nameof(RosterImportJob), jobId, null,
            $"Programme import undone: {eventsRemoved} event(s) removed, {dutiesRemoved} duty slot(s) cancelled, {dutiesKept} kept because their register was taken, {updatesRestored} updated row(s) put back, {updatesLeft.Count} left because they changed since.",
            new { jobId, eventsRemoved, dutiesRemoved, dutiesKept, updatesRestored, updatesLeft }, branchId, organizationId);

        return Ok(new ProgrammeUndoResultDto
        {
            EventsRemoved = eventsRemoved, DutiesRemoved = dutiesRemoved, DutiesKept = dutiesKept,
            UpdatesRestored = updatesRestored, UpdatesLeft = updatesLeft
        });
    }

    /// <summary>Duties of these jobs that a register has touched: opened, or pointed at by any record.</summary>
    private IQueryable<StaffDuty> ProtectedDutiesQuery(Guid organizationId, List<Guid> jobIds)
        => Db.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.ImportJobId != null && jobIds.Contains(d.ImportJobId.Value) && d.IsActive
                        && (d.RegisterOpenedAt != null || Db.StaffPerformanceRecords.IgnoreQueryFilters().Any(r => r.DutyId == d.Id)));

    // ---- Development only: read a document the way the page does ----------------------------------------

    /// <summary>
    /// Development only (404 elsewhere): the documents posted as multipart files, read by the same Shared readers the
    /// page runs, with the classification, the candidate rows and the checks across them. It exists so the e2e suite
    /// can assert what the readers make of a school's real documents without driving a browser — and it writes nothing.
    /// </summary>
    [HttpPost("dev/import/read-document")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    [ProducesResponseType(typeof(ProgrammeReadResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReadDocument()
    {
        if (!_environment.IsDevelopment()) return NotFound();
        if (!Request.HasFormContentType) return BadRequestProblem("Send the documents as multipart files.");
        var form = await Request.ReadFormAsync();
        if (form.Files.Count == 0) return BadRequestProblem("No file was sent.");

        var files = new List<ProgrammeFileReading>();
        foreach (var f in form.Files)
        {
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            files.Add(ProgrammeTableParser.ReadFile(f.FileName, ms.ToArray()));
        }

        CalendarSettingsDto settings = new();
        NationalCalendarDto national = new();
        var organizationId = CurrentOrganizationId();
        if (organizationId != null)
        {
            settings = await CalendarSettingsStore.GetAsync(Db, organizationId.Value);
            national = await NationalCalendarStore.GetAsync(Db);
        }
        var checks = ProgrammeChecks.Run(files, settings, national);
        return Ok(new ProgrammeReadResultDto { Files = files, Checks = checks, Summaries = files.Select(ProgrammeFileSummaryDto.Of).ToList() });
    }

    // ---- Evaluation: what each row would do --------------------------------------------------------------

    private sealed class Evaluation
    {
        /// <summary>What each row an import UPDATED looked like before it (2026-09-25), so undo can put it back.</summary>
        public List<(MeetingBefore Before, StaffDuty Entity)> MeetingsBefore { get; } = new();
        public List<(EventBefore Before, SchoolEvent Entity)> EventsBefore { get; } = new();
        public List<ProgrammeRowResultDto> Rows { get; } = new();
        public List<(ProgrammeEventRow Row, int Index, Guid? ExistingId, ProgrammeRowOutcome Outcome, bool HandEdited)> Events { get; } = new();
        public List<(ProgrammeMeetingRow Row, int Index, Guid? ExistingId, ProgrammeRowOutcome Outcome, DateTime StartsAt, DateTime EndsAt, Guid[]? Expected, Guid[] Recorders)> Meetings { get; } = new();
        public List<(ProgrammeRotaRow Row, int Index, Guid? ExistingId, ProgrammeRowOutcome Outcome, DateTime StartsAt, DateTime EndsAt)> Rota { get; } = new();
        public HashSet<Guid> Departments { get; set; } = new();
        /// <summary>"kind|sourceKey" → the id a commit gave the new record, so the answer names it.</summary>
        public Dictionary<string, Guid> CreatedIds { get; } = new(StringComparer.Ordinal);
        public HashSet<Guid> ActiveUsers { get; set; } = new();
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public int EventsCreated, EventsUpdated, MeetingsCreated, MeetingsUpdated, RotaCreated, Unchanged, Refused, PeopleToNotify;

        public ProgrammeImportResultDto ToResult(bool preview, Guid? jobId) => new()
        {
            Preview = preview,
            JobId = jobId,
            Rows = Rows.Select(r => r.RecordId == null && CreatedIds.TryGetValue(r.Kind + "|" + r.SourceKey, out var id) ? r with { RecordId = id } : r).ToList(),
            EventsCreated = EventsCreated,
            EventsUpdated = EventsUpdated,
            MeetingsCreated = MeetingsCreated,
            MeetingsUpdated = MeetingsUpdated,
            RotaSlotsCreated = RotaCreated,
            Unchanged = Unchanged,
            Refused = Refused,
            PeopleNotified = PeopleToNotify
        };
    }

    private async Task<Evaluation> EvaluateAsync(Guid branchId, Guid organizationId, ProgrammeImportRequest request, Guid? meetingParameterId)
    {
        var eval = new Evaluation { Zone = await ZoneAsync(branchId) };
        var zone = eval.Zone;
        var settingsJson = await OrganizationSettingsJsonAsync(organizationId);
        var policy = _policy.ReadPolicy(settingsJson);
        var settings = CalendarSettingsStore.Read(settingsJson);
        var national = await NationalCalendarStore.GetAsync(Db);

        // Everyone the request names, checked once: active, in this organization.
        var named = request.RotaSlots.Select(r => r.UserId)
            .Concat(request.Meetings.SelectMany(m => (m.ExpectedUserIds ?? new List<Guid>()).Concat(m.RecorderUserIds ?? new List<Guid>())))
            .Concat(request.Events.SelectMany(e => e.ResponsibleUserIds ?? new List<Guid>()))
            .Where(id => id != Guid.Empty).Distinct().ToList();
        eval.ActiveUsers = (await Db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => named.Contains(u.Id) && u.OrganizationId == organizationId && u.IsActive)
            .Select(u => u.Id).ToListAsync()).ToHashSet();
        var departmentIds = (await Db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId).Select(d => d.Id).ToListAsync()).ToHashSet();
        eval.Departments = departmentIds;

        var allDates = request.Events.SelectMany(e => new[] { e.StartsOn, e.EndsOn == default ? e.StartsOn : e.EndsOn })
            .Concat(request.Meetings.Select(m => m.Date))
            .Concat(request.RotaSlots.SelectMany(r => new[] { r.StartsOn, r.EndsOn == default ? r.StartsOn : r.EndsOn }))
            .Where(d => d.Year >= 2000 && d.Year <= 2100).ToList();
        var minDate = allDates.Count > 0 ? allDates.Min() : DateOnly.FromDateTime(DateTime.UtcNow);
        var maxDate = allDates.Count > 0 ? allDates.Max() : minDate;

        // ---- Events ----
        var sourceKeys = request.Events.Select(e => (e.SourceKey ?? string.Empty).Trim()).Where(k => k.Length > 0).Distinct().ToList();
        var existingEvents = await Db.SchoolEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && (e.BranchId == null || e.BranchId == branchId)
                        && ((e.SourceKey != null && sourceKeys.Contains(e.SourceKey)) || (e.StartsOn >= minDate && e.StartsOn <= maxDate)))
            .ToListAsync();
        var seenEventKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < request.Events.Count; i++)
        {
            var row = request.Events[i];
            var key = (row.SourceKey ?? string.Empty).Trim();
            var title = (row.Title ?? string.Empty).Trim();
            var warnings = new List<string>();
            string? refusal = null;
            var end = row.EndsOn == default ? row.StartsOn : row.EndsOn;
            var start = StaffRota.ParseLocalTime(row.StartTime);
            var finish = StaffRota.ParseLocalTime(row.EndTime);
            if (key.Length == 0) refusal = "The row has no source key.";
            else if (!seenEventKeys.Add(key)) refusal = "The same row appears twice in this import.";
            else if (title.Length == 0) refusal = "Give the event a title.";
            else if (title.Length > 200) refusal = "A title cannot exceed 200 characters.";
            else if (row.StartsOn.Year is < 2000 or > 2100) refusal = "Choose the day the event starts.";
            else if (end < row.StartsOn) refusal = "The event ends before it starts.";
            else if (end.DayNumber - row.StartsOn.DayNumber > MaxSpanDays) refusal = $"An event can run for at most {MaxSpanDays} days.";
            else if (!string.IsNullOrWhiteSpace(row.StartTime) && start == null) refusal = "Use a start time like 08:30.";
            else if (!string.IsNullOrWhiteSpace(row.EndTime) && finish == null) refusal = "Use an end time like 17:00.";
            else if (start == null && finish != null) refusal = "An end time needs a start time.";
            else if (start != null && finish != null && end == row.StartsOn && finish < start) refusal = "The event ends before it starts.";
            if (refusal != null)
            {
                Refuse(eval, "event", key, refusal);
                continue;
            }

            var category = CalendarSettingsStore.MatchCategory(settings, row.Category);
            if (!string.IsNullOrWhiteSpace(row.Category) && category == null) warnings.Add($"\"{row.Category}\" is not one of the school's calendar categories — imported without one.");
            var responsible = (row.ResponsibleUserIds ?? new List<Guid>()).Where(eval.ActiveUsers.Contains).Distinct().ToArray();
            if ((row.ResponsibleUserIds?.Count ?? 0) > responsible.Length) warnings.Add("Somebody named as responsible is not an active member of staff and was left off.");
            var departments = (row.ResponsibleDepartmentIds ?? new List<Guid>()).Where(departmentIds.Contains).Distinct().ToArray();
            AddCalendarWarnings(warnings, policy, national, row.StartsOn, end);

            var titleKey = ProgrammeText.TitleKey(title);
            var existing = existingEvents.FirstOrDefault(e => e.SourceKey == key)
                           ?? existingEvents.FirstOrDefault(e => e.StartsOn == row.StartsOn && ProgrammeText.TitleKey(e.Title) == titleKey);
            var candidate = new SchoolEvent
            {
                Title = title, Description = Clean(row.Description, 2000), StartsOn = row.StartsOn, EndsOn = end, StartTime = start, EndTime = finish,
                Category = category, Audience = row.Audience == EventAudience.None ? EventAudience.Staff : row.Audience,
                ClassNames = (row.ClassNames ?? new List<string>()).Select(c => c.Trim()).Where(c => c.Length > 0 && c.Length <= 60).Distinct().Take(40).ToArray(),
                Location = Clean(row.Location, 200), ResponsibleText = Clean(row.ResponsibleText, 300),
                ResponsibleUserIds = responsible, ResponsibleDepartmentIds = departments, SeriesName = Clean(row.SeriesName, 200)
            };
            StaffAudience.Apply(candidate, row.StaffAudience ?? StaffAudienceDto.Everyone());
            var changes = existing == null ? new List<string>() : EventChanges(existing, candidate);

            // B6: an event somebody edited BY HAND keeps their edit. A re-import used to put back what the document said
            // over every field, silently; it now says which of the document's changes it did not apply.
            var handEdited = false;
            if (existing?.EditedByHandAt is { } editedAt && changes.Count > 0)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Edited by hand on {editedAt:dd MMM yyyy}, so the document's {string.Join(", ", changes)} {(changes.Count == 1 ? "was" : "were")} not applied."));
                changes.Clear();
                handEdited = true;
            }
            // An event imported before as an event only, now the same line of a meeting with a register: linked, not doubled.
            if (existing != null && existing.DutyId == null && !string.IsNullOrWhiteSpace(row.SameAsMeetingKey)) changes.Add("linked to its register");

            var outcome = existing == null ? ProgrammeRowOutcome.New : changes.Count == 0 ? ProgrammeRowOutcome.Unchanged : ProgrammeRowOutcome.Update;
            Count(eval, "event", outcome);
            eval.Events.Add((row, i, existing?.Id, outcome, handEdited));
            eval.Rows.Add(new ProgrammeRowResultDto { Kind = "event", SourceKey = key, Outcome = outcome, Changes = changes, Warnings = warnings, RecordId = existing?.Id });
        }

        // ---- Meetings ----
        var sessions = request.Meetings.Count == 0 ? new List<StaffDuty>() : await Db.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.BranchId == branchId && d.IsActive && d.Kind == DutyKind.Session
                        && d.StartsAt >= minDate.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                        && d.StartsAt <= maxDate.AddDays(2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            .ToListAsync();
        var meetingParameterActive = true;
        if (request.Meetings.Count > 0)
        {
            var parameter = await Db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.OrganizationId == organizationId && (meetingParameterId != null ? p.Id == meetingParameterId : p.Name.ToLower() == MeetingParameter.Name.ToLower()))
                .Select(p => new { p.IsActive }).FirstOrDefaultAsync();
            meetingParameterActive = parameter?.IsActive ?? true;
        }
        var seenMeetingKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < request.Meetings.Count; i++)
        {
            var row = request.Meetings[i];
            var key = (row.SourceKey ?? string.Empty).Trim();
            var title = (row.Title ?? string.Empty).Trim();
            var start = StaffRota.ParseLocalTime(row.StartTime);
            var finish = StaffRota.ParseLocalTime(row.EndTime);
            var warnings = new List<string>();
            string? refusal = null;
            if (key.Length == 0) refusal = "The row has no source key.";
            else if (!seenMeetingKeys.Add(key)) refusal = "The same meeting appears twice in this import.";
            else if (title.Length == 0) refusal = "Give the meeting a title.";
            else if (title.Length > 200) refusal = "A title cannot exceed 200 characters.";
            else if (row.Date.Year is < 2000 or > 2100) refusal = "Choose the day of the meeting.";
            else if (start == null) refusal = "A meeting needs a start time (like 09:00) — its register opens then.";
            else if (!string.IsNullOrWhiteSpace(row.EndTime) && finish == null) refusal = "Use an end time like 10:00.";
            if (refusal != null)
            {
                Refuse(eval, "meeting", key, refusal);
                continue;
            }

            if (finish == null || finish <= start)
            {
                finish = start!.Value.AddMinutes(DefaultMeetingMinutes);
                if (finish <= start) finish = new TimeOnly(23, 59);
                warnings.Add("No end time — held as one hour.");
            }
            var startsAt = TimeZoneInfo.ConvertTimeToUtc(row.Date.ToDateTime(start!.Value), zone);
            var endsAt = TimeZoneInfo.ConvertTimeToUtc(row.Date.ToDateTime(finish.Value), zone);

            Guid[]? expected = null;
            if (row.ExpectedUserIds != null)
            {
                expected = row.ExpectedUserIds.Where(eval.ActiveUsers.Contains).Distinct().ToArray();
                if (expected.Length == 0)
                {
                    Refuse(eval, "meeting", key, "Nobody named as expected is an active member of staff — choose who is expected, or import it as an event.");
                    continue;
                }
                if (expected.Length < row.ExpectedUserIds.Distinct().Count()) warnings.Add("Somebody named as expected is not an active member of staff and was left off.");
            }
            var recorders = (row.RecorderUserIds ?? new List<Guid>()).Where(eval.ActiveUsers.Contains).Distinct().ToArray();
            if (recorders.Length == 0) warnings.Add("Nobody is named to take the register — only duty managers will be able to.");
            if (!meetingParameterActive) warnings.Add("The Meeting Attendance parameter is retired — its register cannot be taken until it is reinstated.");
            if (StaffRota.IsHoliday(policy, row.Date)) warnings.Add("The date is outside every term.");
            AddNationalWarnings(warnings, national, row.Date, row.Date);
            if (_policy.ClosureFor(policy, startsAt) != null) warnings.Add("The date is in a closed period — its register cannot be taken until the period is reopened.");

            var titleKey = ProgrammeText.TitleKey(title);
            var localDay = row.Date;
            // B7: by the line it came from first. Title and day only for a meeting imported before keys were kept — and
            // never a meeting some other line of a document claims, or two meetings of one title on one day would merge.
            var existing = sessions.FirstOrDefault(d => d.SourceKey == key)
                           ?? sessions.FirstOrDefault(d => d.SourceKey == null && ProgrammeText.TitleKey(d.Title) == titleKey
                                                           && DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(d.StartsAt, zone)) == localDay);
            var changes = new List<string>();
            if (existing != null)
            {
                if (existing.StartsAt != startsAt || existing.EndsAt != endsAt)
                    changes.Add(string.Create(CultureInfo.InvariantCulture,
                        $"time {TimeZoneInfo.ConvertTimeFromUtc(existing.StartsAt, zone):HH:mm}–{TimeZoneInfo.ConvertTimeFromUtc(existing.EndsAt, zone):HH:mm} → {start:HH:mm}–{finish:HH:mm}"));
                if (!string.Equals(existing.Location ?? string.Empty, Clean(row.Location, 200) ?? string.Empty, StringComparison.Ordinal)) changes.Add("venue");
                if (!SameSet(existing.ExpectedUserIds, expected)) changes.Add("who is expected");
                if (!SameSet(existing.RecorderUserIds, recorders)) changes.Add("who takes the register");
                if (existing.Title != title) changes.Add("title");
                if (changes.Count > 0 && existing.RegisterOpenedAt != null)
                {
                    Refuse(eval, "meeting", key, "Its register has already been taken — change it on the register page.", existing.Id);
                    continue;
                }
            }
            var outcome = existing == null ? ProgrammeRowOutcome.New : changes.Count == 0 ? ProgrammeRowOutcome.Unchanged : ProgrammeRowOutcome.Update;
            Count(eval, "meeting", outcome);
            eval.Meetings.Add((row, i, existing?.Id, outcome, startsAt, endsAt, expected, recorders));
            eval.Rows.Add(new ProgrammeRowResultDto { Kind = "meeting", SourceKey = key, Outcome = outcome, Changes = changes, Warnings = warnings, RecordId = existing?.Id });
        }

        // ---- Rota slots ----
        var dayStart = StaffRota.ParseLocalTime(new GenerateRotaRequest().DayStartLocalTime)!.Value;
        var dayEnd = StaffRota.ParseLocalTime(new GenerateRotaRequest().DayEndLocalTime)!.Value;
        var rotaDuties = request.RotaSlots.Count == 0 ? new List<StaffDuty>() : await Db.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.BranchId == branchId && d.IsActive && d.Kind == DutyKind.Rota
                        && d.StartsAt >= minDate.AddDays(-2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                        && d.StartsAt <= maxDate.AddDays(2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            .ToListAsync();
        var names = await StaffLookups.LoadNamesAsync(Db, request.RotaSlots.Select(r => (Guid?)r.UserId));
        var planned = new List<(DateTime Start, DateTime End, IReadOnlyCollection<Guid> Users)>();
        var seenRotaKeys = new HashSet<string>(StringComparer.Ordinal);
        var rotaChecks = new List<(int Index, ProgrammeRotaRow Row, string Key, DateTime StartsAt, DateTime EndsAt, StaffDuty? Existing, List<string> Warnings, ProgrammeRowOutcome Outcome, List<string> Changes)>();
        for (var i = 0; i < request.RotaSlots.Count; i++)
        {
            var row = request.RotaSlots[i];
            var key = (row.SourceKey ?? string.Empty).Trim();
            var rotaName = (row.RotaName ?? string.Empty).Trim();
            var end = row.EndsOn == default ? row.StartsOn : row.EndsOn;
            string? refusal = null;
            if (key.Length == 0) refusal = "The row has no source key.";
            else if (!seenRotaKeys.Add(key)) refusal = "The same slot appears twice in this import.";
            else if (rotaName.Length == 0) refusal = "Name the rota (for example \"Teacher on Duty\").";
            else if (rotaName.Length > 120) refusal = "A rota name cannot exceed 120 characters.";
            else if (!eval.ActiveUsers.Contains(row.UserId)) refusal = "The person is not an active member of staff.";
            else if (row.StartsOn.Year is < 2000 or > 2100) refusal = "Choose the day of the duty.";
            else if (end < row.StartsOn) refusal = "The duty ends before it starts.";
            else if (end.DayNumber - row.StartsOn.DayNumber > MaxSpanDays) refusal = $"A rota slot can run for at most {MaxSpanDays} days.";
            if (refusal != null)
            {
                Refuse(eval, "rota", key, refusal);
                continue;
            }

            var startsAt = TimeZoneInfo.ConvertTimeToUtc(row.StartsOn.ToDateTime(dayStart), zone);
            var endsAt = TimeZoneInfo.ConvertTimeToUtc(end.ToDateTime(dayEnd), zone);
            var existing = rotaDuties.FirstOrDefault(d => string.Equals(d.Title, rotaName, StringComparison.OrdinalIgnoreCase)
                                                          && (d.ExpectedUserIds?.Contains(row.UserId) ?? false)
                                                          && DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(d.StartsAt, zone)) == row.StartsOn);
            var changes = new List<string>();
            if (existing != null)
            {
                var existingEnd = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(existing.EndsAt, zone));
                if (existingEnd != end) changes.Add(string.Create(CultureInfo.InvariantCulture, $"last day {existingEnd:ddd d MMM} → {end:ddd d MMM}"));
                if (!string.Equals(existing.Location ?? string.Empty, Clean(row.Location, 200) ?? string.Empty, StringComparison.Ordinal)) changes.Add("where");
            }
            // A rota row is never updated in place — a changed slot is a new slot beside the old one, which the reader
            // cancels on the rota page. That keeps an acknowledged slot's history intact.
            var outcome = existing == null || changes.Count > 0 ? ProgrammeRowOutcome.New : ProgrammeRowOutcome.Unchanged;
            var warnings = new List<string>();
            if (existing != null && changes.Count > 0) warnings.Add($"A slot for this person and day already exists ({string.Join(", ", changes)}); a new one will be added beside it.");
            if (StaffRota.IsHoliday(policy, row.StartsOn)) warnings.Add("The date is outside every term.");
            AddNationalWarnings(warnings, national, row.StartsOn, end);
            if (outcome == ProgrammeRowOutcome.New) planned.Add((startsAt, endsAt, new[] { row.UserId }));
            rotaChecks.Add((i, row, key, startsAt, endsAt, existing, warnings, outcome, changes));
        }
        foreach (var r in rotaChecks)
        {
            if (r.Outcome == ProgrammeRowOutcome.New)
            {
                var others = planned.Where(p => !(p.Start == r.StartsAt && p.End == r.EndsAt && p.Users.Contains(r.Row.UserId))).ToList();
                var rotaWarnings = await StaffRota.CheckAsync(Db, _policy, policy, organizationId, branchId, r.StartsAt, r.EndsAt,
                    new[] { r.Row.UserId }, Array.Empty<Guid>(), null, others, names);
                r.Warnings.AddRange(rotaWarnings.Select(w => w.Message));
            }
            Count(eval, "rota", r.Outcome);
            eval.Rota.Add((r.Row, r.Index, r.Existing?.Id, r.Outcome, r.StartsAt, r.EndsAt));
            eval.Rows.Add(new ProgrammeRowResultDto { Kind = "rota", SourceKey = r.Key, Outcome = r.Outcome, Changes = r.Changes, Warnings = r.Warnings, RecordId = r.Existing?.Id });
        }

        eval.PeopleToNotify = request.NotifyPeople
            ? eval.Rota.Where(r => r.Outcome == ProgrammeRowOutcome.New && r.EndsAt > DateTime.UtcNow).Select(r => r.Row.UserId)
                .Concat(eval.Meetings.Where(m => m.Outcome == ProgrammeRowOutcome.New && m.EndsAt > DateTime.UtcNow && m.Expected != null).SelectMany(m => m.Expected!))
                .Distinct().Count()
            : 0;
        return eval;
    }

    private static void Refuse(Evaluation eval, string kind, string key, string reason, Guid? recordId = null)
    {
        eval.Refused++;
        eval.Rows.Add(new ProgrammeRowResultDto { Kind = kind, SourceKey = key, Outcome = ProgrammeRowOutcome.Refused, Reason = reason, RecordId = recordId });
    }

    private static void Count(Evaluation eval, string kind, ProgrammeRowOutcome outcome)
    {
        switch (outcome)
        {
            case ProgrammeRowOutcome.Unchanged: eval.Unchanged++; break;
            case ProgrammeRowOutcome.New when kind == "event": eval.EventsCreated++; break;
            case ProgrammeRowOutcome.Update when kind == "event": eval.EventsUpdated++; break;
            case ProgrammeRowOutcome.New when kind == "meeting": eval.MeetingsCreated++; break;
            case ProgrammeRowOutcome.Update when kind == "meeting": eval.MeetingsUpdated++; break;
            case ProgrammeRowOutcome.New when kind == "rota": eval.RotaCreated++; break;
        }
    }

    /// <summary>What differs between a stored event and the row, by FIELD — never by value for text a reader wrote.</summary>
    private static List<string> EventChanges(SchoolEvent existing, SchoolEvent row)
    {
        var changes = new List<string>();
        if (existing.Title != row.Title) changes.Add("title");
        if (existing.StartsOn != row.StartsOn || existing.EndsOn != row.EndsOn)
            changes.Add(string.Create(CultureInfo.InvariantCulture, $"dates {existing.StartsOn:d MMM}–{existing.EndsOn:d MMM} → {row.StartsOn:d MMM}–{row.EndsOn:d MMM}"));
        if (existing.StartTime != row.StartTime || existing.EndTime != row.EndTime)
            changes.Add($"time {TimeText(existing.StartTime, existing.EndTime)} → {TimeText(row.StartTime, row.EndTime)}");
        if ((existing.Location ?? string.Empty) != (row.Location ?? string.Empty)) changes.Add("venue");
        if ((existing.Category ?? string.Empty) != (row.Category ?? string.Empty)) changes.Add("category");
        if ((existing.ResponsibleText ?? string.Empty) != (row.ResponsibleText ?? string.Empty)
            || !SameSet(existing.ResponsibleUserIds, row.ResponsibleUserIds) || !SameSet(existing.ResponsibleDepartmentIds, row.ResponsibleDepartmentIds))
            changes.Add("responsible");
        if (existing.Audience != row.Audience) changes.Add("audience");
        if (AudienceKey(existing) != AudienceKey(row)) changes.Add("who it is for");
        if (!existing.ClassNames.OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(row.ClassNames.OrderBy(c => c, StringComparer.Ordinal))) changes.Add("classes");
        if ((existing.Description ?? string.Empty) != (row.Description ?? string.Empty)) changes.Add("description");
        if ((existing.SeriesName ?? string.Empty) != (row.SeriesName ?? string.Empty)) changes.Add("programme");
        return changes;
    }

    private static string AudienceKey(SchoolEvent e)
        => string.Join("|", e.AllStaff, string.Join(",", e.AudienceStaffGroups.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            string.Join(",", e.AudienceRoleCodes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            string.Join(",", e.AudienceDepartmentIds.OrderBy(x => x)), string.Join(",", e.AudienceUserIds.OrderBy(x => x)));

    private static string TimeText(TimeOnly? start, TimeOnly? end)
        => start == null ? "all day" : end == null ? $"{start:HH:mm} onwards" : string.Create(CultureInfo.InvariantCulture, $"{start:HH:mm}–{end:HH:mm}");

    private static bool SameSet(IEnumerable<Guid>? a, IEnumerable<Guid>? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return a.ToHashSet().SetEquals(b);
    }

    private static void AddCalendarWarnings(List<string> warnings, StaffPerformancePolicyDto policy, NationalCalendarDto national, DateOnly from, DateOnly to)
    {
        if (StaffRota.IsHoliday(policy, from)) warnings.Add("The date is outside every term.");
        AddNationalWarnings(warnings, national, from, to);
    }

    /// <summary>Decision D9: the national calendar only ever warns.</summary>
    private static void AddNationalWarnings(List<string> warnings, NationalCalendarDto national, DateOnly from, DateOnly to)
    {
        foreach (var e in NationalCalendarStore.Overlapping(national, from, to)
                     .Where(e => (e.Kind ?? string.Empty).Contains("holiday", StringComparison.OrdinalIgnoreCase)))
            warnings.Add($"The date falls on {e.Title}.");
    }

    // ---- Writing -----------------------------------------------------------------------------------------

    private async Task ApplyAsync(Evaluation plan, RosterImportJob job, Guid branchId, Guid organizationId, Guid me,
        Guid? meetingParameterId, Guid rotaParameterId, List<SchoolEvent> createdEvents, List<StaffDuty> createdDuties)
    {
        var now = DateTime.UtcNow;
        var actor = me == Guid.Empty ? (Guid?)null : me;
        var policy = await _policy.GetAsync(organizationId);

        // Meetings first: an event that is the same thing as a meeting points at its duty.
        var meetingIdsByKey = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var m in plan.Meetings)
        {
            var key = m.Row.SourceKey.Trim();
            if (m.Outcome == ProgrammeRowOutcome.Unchanged && m.ExistingId is { } same) { meetingIdsByKey[key] = same; continue; }
            if (m.Outcome == ProgrammeRowOutcome.Update && m.ExistingId is { } id)
            {
                // B6: FirstOrDefault — a row removed meanwhile is skipped, never a 500 for the whole batch.
                var duty = await Db.StaffDuties.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == id);
                if (duty == null) continue;
                plan.MeetingsBefore.Add((new MeetingBefore
                {
                    Id = duty.Id, Title = duty.Title, StartsAt = duty.StartsAt, EndsAt = duty.EndsAt, Location = duty.Location,
                    ExpectedUserIds = duty.ExpectedUserIds, RecorderUserIds = duty.RecorderUserIds
                }, duty));
                duty.Title = m.Row.Title.Trim();
                duty.StartsAt = m.StartsAt;
                duty.EndsAt = m.EndsAt;
                duty.Location = Clean(m.Row.Location, 200);
                duty.ExpectedUserIds = m.Expected;
                duty.RecorderUserIds = m.Recorders;
                duty.SourceKey = Truncate(key, 200);
                duty.UpdatedAt = now;
                duty.UpdatedBy = actor;
                meetingIdsByKey[key] = duty.Id;
                continue;
            }
            var created = new StaffDuty
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                ParameterId = meetingParameterId ?? Guid.Empty,
                Kind = DutyKind.Session,
                Title = Truncate(m.Row.Title.Trim(), 200),
                Description = Clean(DescribeMeeting(m.Row), 2000),
                Location = Clean(m.Row.Location, 200),
                StartsAt = m.StartsAt,
                EndsAt = m.EndsAt,
                ExpectedUserIds = m.Expected,
                RecorderUserIds = m.Recorders,
                ImportJobId = job.Id,
                SourceKey = Truncate(key, 200),
                CreatedByUserId = me,
                CreatedBy = actor
            };
            Db.StaffDuties.Add(created);
            createdDuties.Add(created);
            meetingIdsByKey[key] = created.Id;
            plan.CreatedIds["meeting|" + key] = created.Id;
        }

        // Rota slots: one SeriesId per rota name in this batch, the Teacher on Duty parameter, the policy's cadence.
        var defaults = policy.DutyReportDefaults ?? new DutyReportDefaultsDto();
        var dueTime = StaffRota.ParseLocalTime(defaults.DueLocalTime);
        var series = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in plan.Rota.Where(r => r.Outcome == ProgrammeRowOutcome.New))
        {
            var rotaName = Truncate(r.Row.RotaName.Trim(), 200);
            if (!series.TryGetValue(rotaName, out var seriesId)) series[rotaName] = seriesId = Guid.NewGuid();
            var duty = new StaffDuty
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                ParameterId = rotaParameterId,
                Kind = DutyKind.Rota,
                SeriesId = seriesId,
                Title = rotaName,
                Location = Clean(r.Row.Location, 200),
                StartsAt = r.StartsAt,
                EndsAt = r.EndsAt,
                ExpectedUserIds = new[] { r.Row.UserId },
                SupervisorUserIds = Array.Empty<Guid>(),
                RecorderUserIds = Array.Empty<Guid>(),
                ReportCadence = StaffRota.DefaultCadence(policy, r.StartsAt, r.EndsAt),
                ReportDueLocalTime = dueTime,
                ImportJobId = job.Id,
                CreatedByUserId = me,
                CreatedBy = actor
            };
            Db.StaffDuties.Add(duty);
            createdDuties.Add(duty);
            plan.CreatedIds["rota|" + r.Row.SourceKey.Trim()] = duty.Id;
        }

        // Events: one SeriesId per series name in this batch (reusing an existing series of the same name).
        var eventSeries = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in plan.Events)
        {
            if (e.Outcome == ProgrammeRowOutcome.Unchanged) continue;
            var row = e.Row;
            var end = row.EndsOn == default ? row.StartsOn : row.EndsOn;
            SchoolEvent target;
            if (e.Outcome == ProgrammeRowOutcome.Update && e.ExistingId is { } id)
            {
                var found = await Db.SchoolEvents.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id);
                if (found == null) continue; // B6: removed meanwhile
                target = found;
                plan.EventsBefore.Add((EventBefore.Of(target), target));
                target.UpdatedAt = now;
                target.UpdatedBy = actor;
                // Hand-edited: nothing of the document is written over it; only the link to its register, when that is new.
                if (e.HandEdited)
                {
                    if (!string.IsNullOrWhiteSpace(row.SameAsMeetingKey) && meetingIdsByKey.TryGetValue(row.SameAsMeetingKey.Trim(), out var linkId))
                        target.DutyId = linkId;
                    continue;
                }
                // The change is announced in the import's one summary per person, not once per event.
                target.Version++;
                target.NotifiedVersion = target.Version;
            }
            else
            {
                target = new SchoolEvent { OrganizationId = organizationId, BranchId = branchId, ImportJobId = job.Id, CreatedBy = actor, Version = 1, NotifiedVersion = 1 };
                Db.SchoolEvents.Add(target);
                createdEvents.Add(target);
                plan.CreatedIds["event|" + row.SourceKey.Trim()] = target.Id;
            }

            var settingsCategory = row.Category;
            target.Title = Truncate(row.Title.Trim(), 200);
            target.Description = Clean(row.Description, 2000);
            target.StartsOn = row.StartsOn;
            target.EndsOn = end;
            target.StartTime = StaffRota.ParseLocalTime(row.StartTime);
            target.EndTime = StaffRota.ParseLocalTime(row.EndTime);
            target.Category = CalendarSettingsStore.MatchCategory(await CalendarSettingsCachedAsync(organizationId), settingsCategory);
            target.Audience = row.Audience == EventAudience.None ? EventAudience.Staff : row.Audience;
            target.ClassNames = (row.ClassNames ?? new List<string>()).Select(c => c.Trim()).Where(c => c.Length > 0 && c.Length <= 60).Distinct().Take(40).ToArray();
            target.Location = Clean(row.Location, 200);
            target.ResponsibleText = Clean(row.ResponsibleText, 300);
            target.ResponsibleUserIds = (row.ResponsibleUserIds ?? new List<Guid>()).Where(plan.ActiveUsers.Contains).Distinct().ToArray();
            target.ResponsibleDepartmentIds = (row.ResponsibleDepartmentIds ?? new List<Guid>()).Where(plan.Departments.Contains).Distinct().ToArray();
            StaffAudience.Apply(target, row.StaffAudience ?? StaffAudienceDto.Everyone());
            target.SourceKey = Truncate(row.SourceKey.Trim(), 200);
            target.SeriesName = Clean(row.SeriesName, 200);
            if (target.SeriesName is { } seriesName)
            {
                if (!eventSeries.TryGetValue(seriesName, out var sid))
                {
                    sid = await Db.SchoolEvents.IgnoreQueryFilters().AsNoTracking()
                        .Where(x => x.OrganizationId == organizationId && x.SeriesName == seriesName && x.SeriesId != null)
                        .Select(x => x.SeriesId!.Value).FirstOrDefaultAsync();
                    if (sid == Guid.Empty) sid = Guid.NewGuid();
                    eventSeries[seriesName] = sid;
                }
                target.SeriesId = sid;
            }
            else target.SeriesId = null;
            if (!string.IsNullOrWhiteSpace(row.SameAsMeetingKey) && meetingIdsByKey.TryGetValue(row.SameAsMeetingKey.Trim(), out var dutyId))
                target.DutyId = dutyId;
        }
    }

    private CalendarSettingsDto? _calendarSettings;
    private async Task<CalendarSettingsDto> CalendarSettingsCachedAsync(Guid organizationId)
        => _calendarSettings ??= await CalendarSettingsStore.GetAsync(Db, organizationId);

    private static string? DescribeMeeting(ProgrammeMeetingRow row)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.ConvenerText)) parts.Add($"Convener: {row.ConvenerText.Trim()}");
        if (!string.IsNullOrWhiteSpace(row.ExpectedText)) parts.Add($"Expected: {row.ExpectedText.Trim()}");
        return parts.Count == 0 ? null : string.Join(". ", parts) + ".";
    }

    /// <summary>
    /// The term and the learned aliases, through the ONE writer of <c>Organization.Settings</c>. Joins the batch's
    /// transaction; re-reads under the lock, so a policy edit made a moment ago is not dropped.
    /// </summary>
    private async Task<bool> WriteSettingsAsync(Guid organizationId, ProgrammeImportRequest request, Evaluation plan)
    {
        var departmentIds = (await Db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId).Select(d => d.Id).ToListAsync()).ToHashSet();
        var aliasPeople = request.LearnedAliases?.People?.Values.Concat(request.LearnedAliases.Offices?.Values.SelectMany(o => o.UserIds) ?? Enumerable.Empty<Guid>()).Distinct().ToList() ?? new List<Guid>();
        var activeAliasPeople = (await Db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => aliasPeople.Contains(u.Id) && u.OrganizationId == organizationId && u.IsActive).Select(u => u.Id).ToListAsync()).ToHashSet();

        var termUpdated = false;
        await OrganizationSettingsLock.MutateAsync(Db, organizationId, org =>
        {
            var changed = false;
            if (request.Term is { } term && TermProblem(term) == null)
            {
                var policy = _policy.ReadPolicy(org.Settings);
                policy.Periods ??= new List<PerformancePeriodDto>();
                var key = string.IsNullOrWhiteSpace(term.Key)
                    ? $"{term.Start.Year}-{ProgrammeText.Slug(term.Name, 24)}"
                    : term.Key.Trim();
                var closed = _policy.ClosureOf(policy, key) != null;
                var overlaps = policy.Periods.Any(p => !string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase) && p.Start <= term.End && term.Start <= p.End);
                if (!closed && !overlaps)
                {
                    policy.Periods.RemoveAll(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
                    policy.Periods.Add(new PerformancePeriodDto
                    {
                        Key = key, Name = term.Name.Trim(), Start = term.Start, End = term.End,
                        Theme = string.IsNullOrWhiteSpace(term.Theme) ? null : Truncate(term.Theme.Trim(), 200)
                    });
                    policy.Periods = policy.Periods.OrderBy(p => p.Start).ToList();
                    org.Settings = _policy.WritePolicy(org.Settings, policy);
                    changed = termUpdated = true;
                }
            }

            if (request.LearnedAliases is { } learned)
            {
                var aliases = ReadAliases(org.Settings);
                foreach (var (k, v) in learned.People ?? new Dictionary<string, Guid>())
                {
                    var key = StaffNameResolver.AliasKey(k);
                    if (key.Length > 0 && key.Length <= 200 && activeAliasPeople.Contains(v)) aliases.People[key] = v;
                }
                foreach (var (k, v) in learned.Offices ?? new Dictionary<string, OfficeAliasDto>())
                {
                    var key = OfficeResolver.AliasKey(k);
                    if (key.Length == 0 || key.Length > 200 || v == null) continue;
                    aliases.Offices[key] = new OfficeAliasDto
                    {
                        DepartmentIds = (v.DepartmentIds ?? new List<Guid>()).Where(departmentIds.Contains).Distinct().ToList(),
                        UserIds = (v.UserIds ?? new List<Guid>()).Where(activeAliasPeople.Contains).Distinct().ToList()
                    };
                }
                foreach (var (k, v) in learned.Venues ?? new Dictionary<string, string>())
                {
                    var key = ProgrammeText.TitleKey(k);
                    if (key.Length > 0 && key.Length <= 200 && !string.IsNullOrWhiteSpace(v) && v.Length <= 200) aliases.Venues[key] = v.Trim();
                }
                // A list that only ever grows is capped, oldest first by insertion.
                if (aliases.People.Count > 3000) aliases.People = aliases.People.Skip(aliases.People.Count - 3000).ToDictionary(x => x.Key, x => x.Value);
                org.Settings = OrganizationSettingsLock.WithKey(org.Settings, AliasesKey, aliases);
                changed = true;
            }
            return changed;
        });
        return termUpdated;
    }

    private static string? TermProblem(ProgrammeTermUpdate term)
    {
        if (string.IsNullOrWhiteSpace(term.Name) || term.Name.Trim().Length > 60) return "Name the term.";
        if (term.End < term.Start) return "The term ends before it starts.";
        if (term.End.DayNumber - term.Start.DayNumber > 200) return "A term cannot run for more than 200 days.";
        return null;
    }

    // ---- One notice per person ------------------------------------------------------------------------------

    /// <summary>
    /// "You are Teacher on Duty on Wed 23 Sep and Wed 11 Nov. 7 events were added to your calendar." — ONE notice per
    /// person listing every new duty of this import AND how many of its events are for them (calendar-audiences plan E5),
    /// after the commit; never one per row. A notice that did not land never undoes the import. Only what is still ahead
    /// is mentioned, and a meeting that expects everyone is not announced to everyone.
    /// </summary>
    private async Task<int> NotifyPeopleAsync(Guid branchId, Guid organizationId, List<StaffDuty> duties, List<SchoolEvent> events)
    {
        var zone = await ZoneAsync(branchId);
        var now = DateTime.UtcNow;
        var today = QMgr.Application.BranchClock.Today(zone);
        var perPerson = new Dictionary<Guid, List<StaffDuty>>();
        foreach (var d in duties.Where(d => d.EndsAt > now && d.ExpectedUserIds != null))
            foreach (var id in d.ExpectedUserIds!)
            {
                if (!perPerson.TryGetValue(id, out var list)) perPerson[id] = list = new List<StaffDuty>();
                list.Add(d);
            }

        // The events are counted per person through the ONE audience rule. An event that IS a meeting is not counted
        // twice: the meeting's own sentence covers it.
        var eventCounts = new Dictionary<Guid, int>();
        var upcoming = events.Where(e => e.EndsOn >= today && e.DutyId == null && (e.Audience & EventAudience.Staff) == EventAudience.Staff).ToList();
        if (upcoming.Count > 0)
        {
            var members = await StaffAudience.MembersAsync(Db, _policy, organizationId, branchId);
            foreach (var e in upcoming)
                foreach (var id in StaffAudience.RecipientsOf(e, members))
                    eventCounts[id] = eventCounts.GetValueOrDefault(id) + 1;
        }
        foreach (var id in eventCounts.Keys) perPerson.TryAdd(id, new List<StaffDuty>());

        var sent = 0;
        foreach (var (userId, list) in perPerson)
        {
            var sentences = new List<string>();
            foreach (var group in list.Where(d => d.Kind == DutyKind.Rota).GroupBy(d => d.Title).OrderBy(g => g.Min(d => d.StartsAt)))
                sentences.Add($"You are {group.Key} on {JoinDates(group.OrderBy(d => d.StartsAt).Select(d => Span(d, zone)).ToList())}.");
            foreach (var m in list.Where(d => d.Kind == DutyKind.Session).OrderBy(d => d.StartsAt))
                sentences.Add(string.Create(CultureInfo.InvariantCulture, $"You are expected at {m.Title} on {TimeZoneInfo.ConvertTimeFromUtc(m.StartsAt, zone):ddd d MMM} at {TimeZoneInfo.ConvertTimeFromUtc(m.StartsAt, zone):HH:mm}."));
            var eventCount = eventCounts.GetValueOrDefault(userId);
            if (eventCount > 0) sentences.Add(eventCount == 1 ? "1 event was added to your calendar." : $"{eventCount} events were added to your calendar.");
            if (sentences.Count == 0) continue;
            var hasRota = list.Any(d => d.Kind == DutyKind.Rota);
            try
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = userId,
                    OrganizationId = organizationId,
                    BranchId = branchId,
                    Title = hasRota ? "Your duties this term" : list.Count > 0 ? "Meetings you are expected at" : "Your calendar was updated",
                    Message = Truncate(string.Join(" ", sentences), 1000),
                    Type = list.Count > 0 ? NotificationType.StaffPerformance : NotificationType.Calendar,
                    Priority = NotificationPriority.Normal,
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    // A person whose preferences turn one of these off is still told of the other: the key follows
                    // what the message is mostly about.
                    EventKey = hasRota ? NotificationEventKeys.StaffRotaAssigned
                             : list.Count > 0 ? NotificationEventKeys.StaffDutyReminder
                             : NotificationEventKeys.CalendarImportSummary,
                    ActionUrl = hasRota ? "/portal#on-duty" : list.Count > 0 ? "/my-day" : "/calendar",
                    IconClass = "calendar-week"
                });
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Programme import notice could not reach user {UserId}", userId);
            }
        }
        return sent;
    }

    private static string Span(StaffDuty d, TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTimeFromUtc(d.StartsAt, zone);
        var end = TimeZoneInfo.ConvertTimeFromUtc(d.EndsAt, zone);
        return start.Date == end.Date
            ? string.Create(CultureInfo.InvariantCulture, $"{start:ddd d MMM}")
            : string.Create(CultureInfo.InvariantCulture, $"{start:ddd d MMM} to {end:ddd d MMM}");
    }

    private static string JoinDates(List<string> dates)
        => dates.Count <= 1 ? string.Join(string.Empty, dates) : string.Join(", ", dates.Take(dates.Count - 1)) + " and " + dates[^1];

    // ---- Small helpers ----------------------------------------------------------------------------------------

    private async Task<string?> DutiesRefusalAsync(Guid organizationId)
    {
        if (!await HasPermissionAsync(Permissions.StaffDutiesManage))
            return "Meetings and duty rotas need permission to manage duties — only the events will be imported.";
        if (!IsSuperAdmin && !await _modules.IsModuleActiveAsync(organizationId, ModuleCodes.StudentWelfare))
            return "Meetings and duty rotas are part of Welfare & Performance — only the events will be imported.";
        return null;
    }

    private Task<Guid> EnsureMeetingParameterAsync(Guid organizationId) => MeetingParameter.EnsureAsync(Db, organizationId, _logger);

    private Task<string?> OrganizationSettingsJsonAsync(Guid organizationId)
        => Db.Organizations.IgnoreQueryFilters().AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.Settings).FirstOrDefaultAsync();

    private static ImportAliasesDto ReadAliases(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return new ImportAliasesDto();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsJson);
            if (root != null && root.TryGetValue(AliasesKey, out var element) && element.ValueKind == JsonValueKind.Object)
            {
                var aliases = JsonSerializer.Deserialize<ImportAliasesDto>(element.GetRawText(), ReadJson) ?? new ImportAliasesDto();
                aliases.People ??= new();
                aliases.Offices ??= new();
                aliases.Venues ??= new();
                return aliases;
            }
        }
        catch (JsonException) { /* a malformed blob reads as no aliases */ }
        return new ImportAliasesDto();
    }

    private sealed class ProgrammeJobSummary
    {
        public List<string> SourceFiles { get; set; } = new();
        public int EventsCreated { get; set; }
        public int EventsUpdated { get; set; }
        public int MeetingsCreated { get; set; }
        public int MeetingsUpdated { get; set; }
        public int RotaSlotsCreated { get; set; }
        public DateTime? UndoneAt { get; set; }
        public int EventsRemoved { get; set; }
        public int DutiesRemoved { get; set; }
        public int DutiesKept { get; set; }
        public int UpdatesRestored { get; set; }
        public List<string> UpdatesLeft { get; set; } = new();
        public List<MeetingBefore> MeetingsBefore { get; set; } = new();
        public List<EventBefore> EventsBefore { get; set; } = new();
        /// <summary>Rows the server refused, named (since 2026-09-26; earlier imports did not keep them).</summary>
        public List<ImportHealthItemDto> Refused { get; set; } = new();
        public bool RefusedRecorded { get; set; }
        /// <summary>Existing rows this import matched (Unchanged or Update): undoing an EARLIER import must not remove them (B13).</summary>
        public List<Guid> Matched { get; set; } = new();
        /// <summary>For a meeting the reader sent as an event only: why, by source key (Import health).</summary>
        public Dictionary<string, string> NoRegisterReasons { get; set; } = new();
    }

    /// <summary>Postgres keeps microseconds and .NET ticks 100ns, so a stamp read back is equal to the millisecond.</summary>
    private static bool SameStamp(DateTime? a, DateTime? b)
        => a.HasValue && b.HasValue && Math.Abs((a.Value - b.Value).TotalMilliseconds) < 1;

    /// <summary>A meeting as it was before an import updated it — the fields the import writes, and no others.</summary>
    private sealed record MeetingBefore
    {
        public Guid Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public DateTime StartsAt { get; init; }
        public DateTime EndsAt { get; init; }
        public string? Location { get; init; }
        public Guid[]? ExpectedUserIds { get; init; }
        public Guid[]? RecorderUserIds { get; init; }
        public DateTime? AppliedUpdatedAt { get; init; }
    }

    /// <summary>An event as it was before an import updated it — the fields ApplyAsync writes, and no others.</summary>
    private sealed record EventBefore
    {
        public Guid Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Description { get; init; }
        public DateOnly StartsOn { get; init; }
        public DateOnly EndsOn { get; init; }
        public TimeOnly? StartTime { get; init; }
        public TimeOnly? EndTime { get; init; }
        public string? Category { get; init; }
        public EventAudience Audience { get; init; }
        public string[]? ClassNames { get; init; }
        public string? Location { get; init; }
        public string? ResponsibleText { get; init; }
        public Guid[]? ResponsibleUserIds { get; init; }
        public Guid[]? ResponsibleDepartmentIds { get; init; }
        public string? SourceKey { get; init; }
        public string? SeriesName { get; init; }
        public Guid? SeriesId { get; init; }
        public Guid? DutyId { get; init; }
        public DateTime? AppliedUpdatedAt { get; init; }

        public static EventBefore Of(SchoolEvent e) => new()
        {
            Id = e.Id, Title = e.Title, Description = e.Description, StartsOn = e.StartsOn, EndsOn = e.EndsOn,
            StartTime = e.StartTime, EndTime = e.EndTime, Category = e.Category, Audience = e.Audience,
            ClassNames = e.ClassNames, Location = e.Location, ResponsibleText = e.ResponsibleText,
            ResponsibleUserIds = e.ResponsibleUserIds, ResponsibleDepartmentIds = e.ResponsibleDepartmentIds,
            SourceKey = e.SourceKey, SeriesName = e.SeriesName, SeriesId = e.SeriesId, DutyId = e.DutyId
        };

        public void ApplyTo(SchoolEvent e)
        {
            e.Title = Title; e.Description = Description; e.StartsOn = StartsOn; e.EndsOn = EndsOn;
            e.StartTime = StartTime; e.EndTime = EndTime; e.Category = Category; e.Audience = Audience;
            e.ClassNames = ClassNames ?? Array.Empty<string>(); e.Location = Location; e.ResponsibleText = ResponsibleText;
            e.ResponsibleUserIds = ResponsibleUserIds ?? Array.Empty<Guid>(); e.ResponsibleDepartmentIds = ResponsibleDepartmentIds ?? Array.Empty<Guid>();
            e.SourceKey = SourceKey; e.SeriesName = SeriesName; e.SeriesId = SeriesId; e.DutyId = DutyId;
        }
    }

    private static ProgrammeJobSummary ReadSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{')) return new ProgrammeJobSummary();
        try { return JsonSerializer.Deserialize<ProgrammeJobSummary>(json, ReadJson) ?? new ProgrammeJobSummary(); }
        catch (JsonException) { return new ProgrammeJobSummary(); }
    }

    private async Task<TimeZoneInfo> ZoneAsync(Guid branchId)
        => AppointmentScheduling.ResolveTimeZone(await Db.Branches.Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static new string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
