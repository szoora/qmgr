using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services.Storage;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Duties and their registers — a meeting, an exam session, a prep slot, a lesson: when, where,
/// who is expected, and who may take the register (plan §6 item 2, Phase 2).
///
/// TWO different authorisations live here and must not be confused:
///  - Creating, editing, cancelling and duplicating a duty is <c>staff.duties.manage</c>.
///  - Taking its register is <c>caller ∈ RecorderUserIds</c> OR <c>staff.duties.manage</c>. That
///    is the minutes-taker DELEGATION, not the staff scope: a teacher named recorder for Tuesday's
///    staff meeting can mark the Director of Studies absent from that meeting and nothing else,
///    because every entry must name someone on the duty's expected list.
/// The register is written synchronously here, bounded by the expected list, so the rule about
/// Hangfire workers being unable to scope never arises (plan §5.3).
///
/// Direct-DbContext controller like WelfareController; no global EF query filter on StaffDuty, so
/// every by-id action verifies the branch first. Records a register writes are append-only:
/// reopening annuls the old row and writes a new one.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffDutiesController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IStaffScopeService _scope;
    private readonly IStaffAlertService _alerts;
    private readonly IActivityLogger _activity;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly QMgr.Application.Interfaces.INotificationService _notifications;
    private readonly ILogger<StaffDutiesController> _logger;

    private const int MaxRangeDays = 400;

    public StaffDutiesController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService scope,
        IStaffAlertService alerts,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        QMgr.Application.Interfaces.INotificationService notifications,
        ILogger<StaffDutiesController> logger)
    {
        _policy = policy;
        _notifications = notifications;
        _context = context;
        _tenantAccessor = tenantAccessor;
        _scope = scope;
        _alerts = alerts;
        _activity = activity;
        _logger = logger;
    }

    // ---- Guards (the WelfareController shape) ----------------------------------------------------

    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails { Title = "Organization not resolved", Status = StatusCodes.Status401Unauthorized });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
        {
            var superAdminBranchExists = await _context.Branches.AnyAsync(b => b.Id == branchId);
            return superAdminBranchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
        }

        var branchExists = await _context.Branches.AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        return branchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
    }

    private async Task<Guid> ResolveOrganizationIdAsync(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext!;
        return RoleCodes.IsSuperAdmin(tenantContext.UserRole)
            ? await _context.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync()
            : tenantContext.OrganizationId;
    }

    private Guid CurrentUserId()
    {
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : Guid.Empty;
    }

    private readonly Dictionary<string, bool> _permissionCache = new();

    private async Task<bool> HasPermissionAsync(string code)
    {
        if (RoleCodes.IsSuperAdmin(_tenantAccessor.TenantContext?.UserRole)) return true;
        if (_permissionCache.TryGetValue(code, out var cached)) return cached;

        var userId = CurrentUserId();
        var has = userId != Guid.Empty && await _context.Users
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(u => u.Role.RolePermissions)
            .AnyAsync(rp => rp.Permission.Code == code);
        return _permissionCache[code] = has;
    }

    private static IActionResult NotFoundDuty() => new NotFoundObjectResult(new ProblemDetails { Title = "Duty not found", Status = StatusCodes.Status404NotFound });

    private static IActionResult Problem400(string title, string? detail = null)
        => new BadRequestObjectResult(new ProblemDetails { Title = title, Detail = detail, Status = StatusCodes.Status400BadRequest });

    // ---- Read -------------------------------------------------------------------------------------

    /// <summary>
    /// Duties in a date window. A holder of duties.manage or records.view sees every active duty
    /// whose expected list intersects their visible staff (or that they record); anyone else sees
    /// only the duties they are expected at or record. A duty expected of "everyone" is visible to
    /// everyone in the branch.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/staff/duties")]
    [ProducesResponseType(typeof(List<StaffDutyDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDuties(Guid branchId, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null, [FromQuery] bool includeCancelled = false, [FromQuery] DutyKind? kind = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var start = DateTime.SpecifyKind(from ?? DateTime.UtcNow.AddDays(-7), DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(to ?? start.AddDays(35), DateTimeKind.Utc);
        if (end <= start) end = start.AddDays(1);
        if ((end - start).TotalDays > MaxRangeDays) end = start.AddDays(MaxRangeDays);

        var canManage = await HasPermissionAsync(Permissions.StaffDutiesManage);
        var canView = canManage || await HasPermissionAsync(Permissions.StaffRecordsView);

        var query = _context.StaffDuties.AsNoTracking()
            .Include(d => d.Parameter)
            .Where(d => d.OrganizationId == organizationId && d.BranchId == branchId
                        && d.StartsAt < end && d.EndsAt >= start);
        if (!includeCancelled || !canManage) query = query.Where(d => d.IsActive);
        if (kind is { } k) query = query.Where(d => d.Kind == k);

        var duties = await query.OrderBy(d => d.StartsAt).ToListAsync();

        if (canView)
        {
            var visible = await _scope.GetVisibleUserIdsAsync(branchId);
            if (visible != null)
                duties = duties.Where(d => d.ExpectedUserIds == null
                                           || d.Kind == DutyKind.Rota
                                           || d.ExpectedUserIds.Any(visible.Contains)
                                           || d.RecorderUserIds.Contains(me)
                                           || d.SupervisorUserIds.Contains(me)
                                           || d.CreatedByUserId == me).ToList();
        }
        else
        {
            // The duty rota is a displayed school record (MoES BRMS: "a displayed duty roster"), so every member of
            // the branch sees who is on duty when: names and dates only; reports and acknowledgements stay gated.
            duties = duties.Where(d => d.ExpectedUserIds == null || d.Kind == DutyKind.Rota || d.ExpectedUserIds.Contains(me)
                                       || d.RecorderUserIds.Contains(me) || d.SupervisorUserIds.Contains(me)).ToList();
        }

        return Ok(await MapManyAsync(duties, organizationId, branchId, me, canManage));
    }

    // ---- Write ------------------------------------------------------------------------------------

    [HttpPost("branches/{branchId:guid}/staff/duties")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    [ProducesResponseType(typeof(StaffDutyDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateDuty(Guid branchId, [FromBody] SaveStaffDutyRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var duty = new StaffDuty { OrganizationId = organizationId, BranchId = branchId, CreatedByUserId = CurrentUserId(), CreatedBy = CurrentUserId() };

        var error = await ApplyAsync(duty, request, organizationId);
        if (error != null) return error;

        _context.StaffDuties.Add(duty);
        await _context.SaveChangesAsync();

        if (duty.Kind == DutyKind.Rota)
            await NotifyRotaChangesAsync(duty, Array.Empty<Guid>(), Array.Empty<Guid>());

        await _activity.RecordAsync(ActivityActions.DutyCreated, nameof(StaffDuty), duty.Id, null,
            string.Create(CultureInfo.InvariantCulture, $"Duty \"{duty.Title}\" created for {duty.StartsAt:dd MMM yyyy HH:mm} UTC ({(duty.ExpectedUserIds == null ? "everyone" : $"{duty.ExpectedUserIds.Length} expected")}, {duty.RecorderUserIds.Length} recorder(s))"),
            new { duty.ParameterId, duty.StartsAt, duty.EndsAt, Expected = duty.ExpectedUserIds?.Length, Recorders = duty.RecorderUserIds.Length }, branchId, organizationId);

        var dto = await MapOneAsync(duty.Id, organizationId, branchId);
        return CreatedAtAction(nameof(GetDuties), new { branchId }, dto);
    }

    [HttpPut("branches/{branchId:guid}/staff/duties/{dutyId:guid}")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    [ProducesResponseType(typeof(StaffDutyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateDuty(Guid branchId, Guid dutyId, [FromBody] SaveStaffDutyRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var duty = await _context.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId);
        if (duty == null || !duty.IsActive) return NotFoundDuty();
        if (duty.RegisterClosedAt.HasValue)
            return Problem400("This duty's register is closed", "A duty cannot be edited once its register has been closed. Reopen the register first, or duplicate the duty.");

        var before = new { duty.Title, duty.StartsAt, duty.EndsAt, duty.ParameterId, Expected = duty.ExpectedUserIds?.Length, Recorders = duty.RecorderUserIds.Length };
        var expectedBefore = duty.ExpectedUserIds?.ToArray() ?? Array.Empty<Guid>();
        var supervisorsBefore = duty.SupervisorUserIds.ToArray();
        var error = await ApplyAsync(duty, request, organizationId);
        if (error != null) return error;

        duty.UpdatedAt = DateTime.UtcNow;
        duty.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync();

        if (duty.Kind == DutyKind.Rota)
            await NotifyRotaChangesAsync(duty, expectedBefore, supervisorsBefore);

        await _activity.RecordAsync(ActivityActions.DutyUpdated, nameof(StaffDuty), duty.Id, null,
            $"Duty \"{duty.Title}\" updated",
            new { Before = before, After = new { duty.Title, duty.StartsAt, duty.EndsAt, duty.ParameterId, Expected = duty.ExpectedUserIds?.Length, Recorders = duty.RecorderUserIds.Length } },
            branchId, organizationId);

        return Ok(await MapOneAsync(duty.Id, organizationId, branchId));
    }

    /// <summary>Cancels a duty (IsActive = false). Refused once the register is closed: the records it wrote are history.</summary>
    [HttpDelete("branches/{branchId:guid}/staff/duties/{dutyId:guid}")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelDuty(Guid branchId, Guid dutyId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var duty = await _context.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId);
        if (duty == null || !duty.IsActive) return NotFoundDuty();
        if (duty.RegisterClosedAt.HasValue)
            return Problem400("This duty's register is closed", "A duty whose register has been taken cannot be cancelled; the records it produced stand.");

        duty.IsActive = false;
        duty.UpdatedAt = DateTime.UtcNow;
        duty.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.DutyCancelled, nameof(StaffDuty), duty.Id, null,
            string.Create(CultureInfo.InvariantCulture, $"Duty \"{duty.Title}\" on {duty.StartsAt:dd MMM yyyy} cancelled"), null, branchId, organizationId);

        return NoContent();
    }

    /// <summary>A copy at a new start, keeping the duration, the expected list and the recorders — the "same meeting next week" action that stands in for a recurrence rule in v1.</summary>
    [HttpPost("branches/{branchId:guid}/staff/duties/{dutyId:guid}/duplicate")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    [ProducesResponseType(typeof(StaffDutyDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DuplicateDuty(Guid branchId, Guid dutyId, [FromBody] DuplicateStaffDutyRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var source = await _context.StaffDuties.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId);
        if (source == null) return NotFoundDuty();

        var newStart = DateTime.SpecifyKind(request.NewStartsAt, DateTimeKind.Utc);
        var duty = new StaffDuty
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            ParameterId = source.ParameterId,
            Title = source.Title,
            Description = source.Description,
            Location = source.Location,
            StartsAt = newStart,
            EndsAt = newStart + (source.EndsAt - source.StartsAt),
            ExpectedUserIds = source.ExpectedUserIds?.ToArray(),
            RecorderUserIds = source.RecorderUserIds.ToArray(),
            Kind = source.Kind == DutyKind.Lesson ? DutyKind.Session : source.Kind,
            SupervisorUserIds = source.SupervisorUserIds.ToArray(),
            ReportCadence = source.ReportCadence,
            ReportDueLocalTime = source.ReportDueLocalTime,
            CreatedByUserId = CurrentUserId(),
            CreatedBy = CurrentUserId()
        };
        _context.StaffDuties.Add(duty);
        await _context.SaveChangesAsync();

        if (duty.Kind == DutyKind.Rota)
            await NotifyRotaChangesAsync(duty, Array.Empty<Guid>(), Array.Empty<Guid>());

        await _activity.RecordAsync(ActivityActions.DutyCreated, nameof(StaffDuty), duty.Id, null,
            string.Create(CultureInfo.InvariantCulture, $"Duty \"{duty.Title}\" duplicated to {duty.StartsAt:dd MMM yyyy HH:mm} UTC"),
            new { DuplicatedFrom = source.Id, duty.StartsAt, duty.EndsAt }, branchId, organizationId);

        return CreatedAtAction(nameof(GetDuties), new { branchId }, await MapOneAsync(duty.Id, organizationId, branchId));
    }

    // ---- Acknowledgement (plan §4.2) ------------------------------------------------------------------

    /// <summary>
    /// "Seen, I'm on duty". Only a person the rota slot names may acknowledge it, and only for themselves; anyone else
    /// gets 404. The first timestamp stands, and the write is one atomic jsonb merge guarded by jsonb_exists (the
    /// notice acknowledgement race, found by the e2e on 2026-09-16). Acknowledging stops the pre-duty ladder for them.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/staff/duties/{dutyId:guid}/acknowledge")]
    [ProducesResponseType(typeof(StaffDutyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeDuty(Guid branchId, Guid dutyId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var duty = await _context.StaffDuties.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId && d.IsActive);
        if (duty == null || duty.Kind != DutyKind.Rota || duty.ExpectedUserIds == null || !duty.ExpectedUserIds.Contains(me))
            return NotFoundDuty();

        var key = me.ToString();
        var stamp = DateTime.UtcNow.ToString("O");
        // Raw SQL: the table is schema-qualified explicitly (CLAUDE.md, "Raw SQL must schema-qualify").
        var written = await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE qmgr.\"StaffDuties\" SET \"Acknowledgements\" = COALESCE(\"Acknowledgements\", '{{}}'::jsonb) || jsonb_build_object({key}::text, {stamp}::text) WHERE \"Id\" = {duty.Id} AND NOT jsonb_exists(COALESCE(\"Acknowledgements\", '{{}}'::jsonb), {key}::text)");

        if (written > 0)
            await _activity.RecordAsync(ActivityActions.DutyAcknowledged, nameof(StaffDuty), duty.Id, me,
                $"Duty \"{duty.Title}\" acknowledged", null, branchId, organizationId);

        return Ok(await MapOneAsync(duty.Id, organizationId, branchId));
    }

    // ---- The register -------------------------------------------------------------------------------

    /// <summary>One row per expected person with the outcome already recorded, if any. Recorder or duties.manage.</summary>
    [HttpGet("branches/{branchId:guid}/staff/duties/{dutyId:guid}/register")]
    [ProducesResponseType(typeof(StaffRegisterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRegister(Guid branchId, Guid dutyId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var duty = await _context.StaffDuties.AsNoTracking().Include(d => d.Parameter)
            .FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId);
        if (duty == null) return NotFoundDuty();

        var me = CurrentUserId();
        var canManage = await HasPermissionAsync(Permissions.StaffDutiesManage);
        // 404, not 403: a non-recorder learns nothing about whether the duty exists.
        if (!canManage && !duty.RecorderUserIds.Contains(me)) return NotFoundDuty();

        return Ok(await BuildRegisterAsync(duty, organizationId, branchId, me, canManage));
    }

    /// <summary>
    /// Writes the register: one Final record per entry, points from the parameter by outcome.
    /// Authorised by delegation (<c>caller ∈ RecorderUserIds</c>) or duties.manage — NOT by the
    /// staff scope. Every entry must name someone on the expected list. Re-marking a person annuls
    /// their earlier row (with an Annulment note "Register reopened") and writes a new one; nothing
    /// is edited in place. Subjects are told afterwards, synchronously, bounded by the expected list.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/staff/duties/{dutyId:guid}/register")]
    [ProducesResponseType(typeof(StaffRegisterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SubmitRegister(Guid branchId, Guid dutyId, [FromBody] SubmitRegisterRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var duty = await _context.StaffDuties.Include(d => d.Parameter)
            .FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId);
        if (duty == null || !duty.IsActive) return NotFoundDuty();

        var me = CurrentUserId();
        var canManage = await HasPermissionAsync(Permissions.StaffDutiesManage);
        if (!canManage && !duty.RecorderUserIds.Contains(me)) return NotFoundDuty();

        if (duty.Parameter == null || !duty.Parameter.IsActive)
            return Problem400("This duty's parameter has been retired", "Reassign the duty to an active Attendance or Duty parameter before taking its register.");

        var policy = await _policy.GetAsync(organizationId);
        if (_policy.ClosureFor(policy, duty.StartsAt) is { } closure)
            return new ConflictObjectResult(new ProblemDetails
            {
                Title = $"{_policy.FindPeriod(policy, closure.Key)?.Name ?? closure.Key} is closed",
                Detail = "A register dated in a closed period would change figures that have been signed off. An approver can reopen the period first.",
                Status = StatusCodes.Status409Conflict
            });

        var entries = request.Entries ?? new List<RegisterEntryRequest>();
        if (entries.Count == 0 && !request.Close)
            return Problem400("Nothing to record", "Mark at least one person, or close the register.");

        // The expected set bounds every write. Null = every active member of the branch.
        var expected = duty.ExpectedUserIds?.ToHashSet()
                       ?? (await StaffLookups.BranchStaff(_context, organizationId, branchId).Select(u => u.Id).ToListAsync()).ToHashSet();

        var names = await StaffLookups.LoadNamesAsync(_context, entries.Select(e => (Guid?)e.UserId), default);
        foreach (var entry in entries)
        {
            if (!expected.Contains(entry.UserId))
                return Problem400($"{(names[entry.UserId] is { Length: > 0 } n ? n : "That person")} is not expected at this duty", "A register can only mark the people the duty expects.");
            if (entry.Outcome == DutyOutcome.NotApplicable)
                return Problem400("Choose an outcome for every entry", "Present, Late, Absent, Excused, Recovered, Completed or Not completed.");
            // Plan §4.4: a rota slot is closed out Completed or Not completed "with a reason", per person.
            if (duty.Kind == DutyKind.Rota && entry.Outcome is not (DutyOutcome.Completed or DutyOutcome.NotCompleted or DutyOutcome.Excused))
                return Problem400("A rota slot is closed out as Completed, Not completed or Excused");
            if (duty.Kind == DutyKind.Rota && entry.Outcome == DutyOutcome.NotCompleted && string.IsNullOrWhiteSpace(entry.Note))
                return Problem400($"Say why {(names[entry.UserId] is { Length: > 0 } who ? who : "this person")} did not complete the duty", "Not completed needs a reason.");
            // INTEGRITY (duty rota plan §7.3, §13.8): nobody marks their own attendance — a register is somebody
            // else's account of who was there. The one exception is a Lesson duty, where the teacher's own
            // "taught" is recorded as a SELF-REPORT for a lesson supervisor to confirm or override.
            if (entry.UserId == me && duty.Kind != DutyKind.Lesson)
                return Problem400("You cannot mark yourself", "Another recorder, or somebody who manages duties, marks your own attendance.");
        }
        if (entries.GroupBy(e => e.UserId).Any(g => g.Count() > 1))
            return Problem400("A person appears more than once in this register");

        // CONCURRENCY (found by the e2e, 2026-09-16): reading the existing Final rows and writing the
        // new ones were two unguarded steps, so four simultaneous submits of the same register each
        // read "nobody marked yet" and each inserted a row — measured: four Final records per person.
        // A phone on a flaky connection re-sending a register is exactly this. The whole read-annul-
        // insert now runs in one transaction holding pg_advisory_xact_lock keyed on the duty (the
        // project's standing pattern), and the duty's open/closed state is re-read under the lock.
        var wasClosed = false;
        var now = DateTime.UtcNow;
        var userIds = entries.Select(e => e.UserId).ToList();
        var created = new List<StaffPerformanceRecord>();
        var annulled = 0;

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // A replayed attempt starts clean: nothing from a failed attempt stays tracked.
            foreach (var stale in created) _context.Entry(stale).State = EntityState.Detached;
            created.Clear();
            annulled = 0;

            await using var tx = await _context.Database.BeginTransactionAsync();
            var lockKey = $"staff-register:{duty.Id}";
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");
            await _context.Entry(duty).ReloadAsync();
            wasClosed = duty.RegisterClosedAt.HasValue;

            var existing = await _context.StaffPerformanceRecords
                .Where(r => r.DutyId == duty.Id && r.Status == StaffRecordStatus.Final && userIds.Contains(r.SubjectUserId))
                .ToListAsync();

            foreach (var entry in entries)
            {
                foreach (var old in existing.Where(r => r.SubjectUserId == entry.UserId))
                {
                    old.Status = StaffRecordStatus.Annulled;
                    old.UpdatedAt = now;
                    old.UpdatedBy = me;
                    _context.StaffPerformanceNotes.Add(new StaffPerformanceNote
                    {
                        RecordId = old.Id,
                        AuthorUserId = me,
                        Kind = StaffNoteKind.Annulment,
                        Body = "Register reopened"
                    });
                    annulled++;
                }

                var note = string.IsNullOrWhiteSpace(entry.Note) ? null : entry.Note.Trim();
                var description = note == null ? duty.Title : $"{duty.Title} — {note}";
                var record = new StaffPerformanceRecord
                {
                    OrganizationId = organizationId,
                    BranchId = branchId,
                    SubjectUserId = entry.UserId,
                    ParameterId = duty.ParameterId,
                    DutyId = duty.Id,
                    Outcome = entry.Outcome,
                    Points = PointsFor(duty.Parameter, entry.Outcome),
                    Description = description.Length > 2000 ? description[..2000] : description,
                    OccurredAt = duty.StartsAt,
                    Source = entry.UserId == me ? RecordSource.SelfReport : RecordSource.Register,
                    Status = StaffRecordStatus.Final,
                    Visibility = WelfareVisibility.Standard,
                    LoggedByUserId = me,
                    CreatedBy = me
                };
                _context.StaffPerformanceRecords.Add(record);
                created.Add(record);
            }

            duty.RegisterOpenedAt ??= now;
            if (request.Close)
            {
                duty.RegisterClosedAt = now;
                duty.RegisterClosedByUserId = me;
            }
            else if (wasClosed)
            {
                duty.RegisterClosedAt = null;
                duty.RegisterClosedByUserId = null;
            }
            duty.UpdatedAt = now;
            duty.UpdatedBy = me;

            await _context.SaveChangesAsync();
            await tx.CommitAsync();
        });

        if (wasClosed)
            await _activity.RecordAsync(ActivityActions.RegisterReopened, nameof(StaffDuty), duty.Id, null,
                $"Register for \"{duty.Title}\" reopened: {annulled} earlier row(s) annulled", new { Annulled = annulled }, branchId, organizationId);
        await _activity.RecordAsync(ActivityActions.RegisterSubmitted, nameof(StaffDuty), duty.Id, null,
            $"Register for \"{duty.Title}\": {created.Count} marked ({Tally(entries)}){(request.Close ? ", closed" : "")}",
            new { Marked = created.Count, Closed = request.Close, Outcomes = entries.GroupBy(e => e.Outcome).ToDictionary(g => g.Key.ToString(), g => g.Count()) },
            branchId, organizationId);

        // One line per person on THEIR file too, so the person marked absent sees who marked it and when,
        // and a department head's scoped log (which follows subjects) shows the register at all. Before
        // 2026-09-17 the only register event had no subject, so neither was true.
        var markedNames = await StaffLookups.LoadNamesAsync(_context, created.Select(r => (Guid?)r.SubjectUserId));
        foreach (var record in created)
            await _activity.RecordAsync(ActivityActions.RegisterSubmitted, nameof(StaffPerformanceRecord), record.Id, record.SubjectUserId,
                $"{markedNames[record.SubjectUserId]} marked {record.Outcome} on the register for \"{duty.Title}\"",
                new { DutyId = duty.Id, record.Outcome, record.Points }, branchId, organizationId);

        // After the commit, bounded by the expected list, never a background job. The alert service
        // never throws; a failed bell must not fail a register that is already on the books.
        foreach (var record in created)
            await _alerts.NotifyRecordLoggedAsync(record.Id);

        _logger.LogInformation("Register for duty {DutyId} written by {UserId}: {Count} record(s), {Annulled} annulled, closed={Closed}", duty.Id, me, created.Count, annulled, request.Close);

        return Ok(await BuildRegisterAsync(duty, organizationId, branchId, me, canManage));
    }

    /// <summary>The minutes are a Library document (MediaContent), which is what lets them go on a playlist or a share link with no new code. Null clears.</summary>
    [HttpPut("branches/{branchId:guid}/staff/duties/{dutyId:guid}/minutes")]
    [ProducesResponseType(typeof(StaffDutyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AttachMinutes(Guid branchId, Guid dutyId, [FromBody] AttachMinutesRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var duty = await _context.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId && d.BranchId == branchId && d.OrganizationId == organizationId);
        if (duty == null || !duty.IsActive) return NotFoundDuty();

        var me = CurrentUserId();
        var canManage = await HasPermissionAsync(Permissions.StaffDutiesManage);
        if (!canManage && !duty.RecorderUserIds.Contains(me)) return NotFoundDuty();

        string? mediaName = null;
        if (request.MediaContentId is { } mediaId)
        {
            mediaName = await _context.MediaContents.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.Id == mediaId && m.OrganizationId == organizationId && m.IsActive)
                .Select(m => m.Name)
                .FirstOrDefaultAsync();
            if (mediaName == null)
                return Problem400("Library document not found", "The minutes must be a document in this organization's Library.");
        }

        duty.MinutesMediaContentId = request.MediaContentId;
        duty.UpdatedAt = DateTime.UtcNow;
        duty.UpdatedBy = me;
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.MinutesAttached, nameof(StaffDuty), duty.Id, null,
            request.MediaContentId.HasValue ? $"Minutes \"{mediaName}\" attached to \"{duty.Title}\"" : $"Minutes removed from \"{duty.Title}\"",
            new { request.MediaContentId }, branchId, organizationId);

        return Ok(await MapOneAsync(duty.Id, organizationId, branchId));
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    /// <summary>Validates and copies a save request onto a duty. Null when valid.</summary>
    private async Task<IActionResult?> ApplyAsync(StaffDuty duty, SaveStaffDutyRequest request, Guid organizationId)
    {
        var parameter = await _context.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.ParameterId && p.OrganizationId == organizationId);
        if (parameter == null || !parameter.IsActive)
            return Problem400("Parameter not found", "Choose an active parameter for this duty.");
        if (parameter.Kind is not (ParameterKind.Attendance or ParameterKind.Duty))
            return Problem400("A duty needs an Attendance or Duty parameter", $"\"{parameter.Name}\" is a {parameter.Kind} parameter; its records are logged directly, not through a register.");

        // Kinds (plan §3.1). A lesson is materialised from the published timetable, never typed in; a duty keeps the
        // kind it was created with, because its reminders, register and reports all follow from it.
        if (request.Kind == DutyKind.Lesson)
            return Problem400("Lessons come from the timetable", "Publish a timetable to schedule lessons; they cannot be created as duties by hand.");
        var existing = _context.Entry(duty).State is EntityState.Unchanged or EntityState.Modified;
        if (existing && duty.Kind != request.Kind)
            return Problem400("A duty's kind cannot be changed", "Cancel it and create the other kind instead.");
        var isRota = request.Kind == DutyKind.Rota;

        var startsAt = DateTime.SpecifyKind(request.StartsAt, DateTimeKind.Utc);
        var endsAt = DateTime.SpecifyKind(request.EndsAt, DateTimeKind.Utc);
        if (endsAt <= startsAt) return Problem400("The duty must end after it starts");
        if (!isRota && (endsAt - startsAt).TotalDays > 14) return Problem400("A single duty cannot span more than 14 days", "Create one duty per session, or make it a rota slot.");
        if (isRota && (endsAt - startsAt).TotalDays > 92) return Problem400("A rota slot cannot span more than 92 days", "Generate a rota of shorter slots instead.");

        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return Problem400("A title is required");

        var expected = request.ExpectedUserIds is { Count: > 0 } ? request.ExpectedUserIds.Distinct().ToList() : null;
        var supervisors = isRota ? (request.SupervisorUserIds ?? new List<Guid>()).Distinct().ToList() : new List<Guid>();
        // The administrators on duty take the slot's close-out register (plan §4.4), so they are its recorders too.
        var recorders = (request.RecorderUserIds ?? new List<Guid>()).Concat(supervisors).Distinct().ToList();
        if (isRota && expected == null)
            return Problem400("Name the people on duty", "A rota slot is a named person's duty; it cannot expect everyone.");
        if (!isRota && request.SupervisorUserIds is { Count: > 0 })
            return Problem400("Only a rota slot has administrators on duty");

        TimeOnly? dueTime = null;
        if (isRota && !string.IsNullOrWhiteSpace(request.ReportDueLocalTime))
        {
            dueTime = StaffRota.ParseLocalTime(request.ReportDueLocalTime);
            if (dueTime == null) return Problem400("The report due time must be a 24-hour time such as 18:00");
        }
        if (request.ReportCadence is { } cadence && !Enum.IsDefined(cadence)) return Problem400("Unrecognised report cadence");

        var referenced = (expected ?? new List<Guid>()).Concat(recorders).Distinct().ToList();
        if (referenced.Count > 0)
        {
            var active = await _context.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => referenced.Contains(u.Id) && u.OrganizationId == organizationId && u.IsActive)
                .Select(u => u.Id)
                .ToListAsync();
            var missing = referenced.Except(active).ToList();
            if (missing.Count > 0)
                return Problem400("Some people are not active members of this organization", $"{missing.Count} of the selected user(s) could not be found or are inactive.");
        }

        duty.ParameterId = parameter.Id;
        duty.Title = title.Length > 200 ? title[..200] : title;
        duty.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        duty.Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim();
        var moved = duty.StartsAt != startsAt;
        var endMoved = duty.EndsAt != endsAt;
        duty.StartsAt = startsAt;
        duty.EndsAt = endsAt;
        duty.ExpectedUserIds = expected?.ToArray();
        duty.RecorderUserIds = recorders.ToArray();
        duty.Kind = request.Kind;
        duty.SupervisorUserIds = supervisors.ToArray();
        if (isRota)
        {
            var policy = await _policy.GetAsync(organizationId);
            duty.ReportCadence = request.ReportCadence ?? StaffRota.DefaultCadence(policy, startsAt, endsAt);
            duty.ReportDueLocalTime = dueTime;
            // Somebody taken off the slot no longer has an acknowledgement on it.
            var acks = StaffPerformanceMapping.ParseAcknowledgements(duty.Acknowledgements);
            if (acks.Keys.Any(id => !expected!.Contains(id)))
                duty.Acknowledgements = StaffPerformanceMapping.SerializeAcknowledgements(acks.Where(a => expected!.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value));
        }
        else
        {
            duty.ReportCadence = ReportCadence.None;
            duty.ReportDueLocalTime = null;
        }
        // A rescheduled duty deserves a fresh reminder ladder (duty rota plan §4.2: rescheduling resets); a chase
        // for a register on the old end time is moot. Only a real move resets — editing a title must not re-page.
        if (moved && startsAt > DateTime.UtcNow)
        {
            duty.ReminderSentAt = null;
            duty.ReminderStage = 0;
            // Plan §4.2: rescheduling resets the ladder AND the acknowledgements; "seen" was for the old dates.
            duty.Acknowledgements = "{}";
        }
        if (endMoved && endsAt > DateTime.UtcNow) duty.RegisterChaseSentAt = null;
        return null;
    }

    /// <summary>
    /// Points by outcome from the parameter's DefaultPoints: Present/Completed/Recovered +dp, Late
    /// half of dp rounded toward zero (never below 0), Absent/NotCompleted −dp, Excused 0. The
    /// magnitude is capped by MaxPointsPerEntry like every other record.
    /// </summary>
    /// <summary>Tells the people newly placed on a rota slot, and its new supervisors. Never fails the save.</summary>
    private async Task NotifyRotaChangesAsync(StaffDuty duty, IReadOnlyCollection<Guid> expectedBefore, IReadOnlyCollection<Guid> supervisorsBefore)
    {
        try
        {
            if (duty.EndsAt < DateTime.UtcNow) return;
            var zone = AppointmentScheduling.ResolveTimeZone(await _context.Branches.Where(b => b.Id == duty.BranchId).Select(b => b.Timezone).FirstOrDefaultAsync());
            await StaffRota.NotifyAssignedAsync(_notifications, _logger, duty, (duty.ExpectedUserIds ?? Array.Empty<Guid>()).Except(expectedBefore), zone, supervising: false);
            await StaffRota.NotifyAssignedAsync(_notifications, _logger, duty, duty.SupervisorUserIds.Except(supervisorsBefore), zone, supervising: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rota assignment notices failed for duty {DutyId}", duty.Id);
        }
    }

    internal static int? PointsFor(PerformanceParameter parameter, DutyOutcome outcome)
    {
        var dp = Math.Abs(parameter.DefaultPoints ?? 0);
        if (parameter.MaxPointsPerEntry > 0) dp = Math.Min(dp, parameter.MaxPointsPerEntry);
        return outcome switch
        {
            DutyOutcome.Present or DutyOutcome.Completed or DutyOutcome.Recovered => dp,
            DutyOutcome.Late => Math.Max(0, dp / 2),
            DutyOutcome.Absent or DutyOutcome.NotCompleted => -dp,
            _ => 0
        };
    }

    private static string Tally(IEnumerable<RegisterEntryRequest> entries)
        => string.Join(", ", entries.GroupBy(e => e.Outcome).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));

    private async Task<StaffRegisterDto> BuildRegisterAsync(StaffDuty duty, Guid organizationId, Guid branchId, Guid me, bool canManage)
    {
        var staffQuery = StaffLookups.BranchStaff(_context, organizationId, branchId);
        if (duty.ExpectedUserIds is { } ids)
        {
            var list = ids.ToList();
            // Expected people are shown even if they have since left or moved branch: a register
            // for a meeting that happened is about who was expected then.
            staffQuery = _context.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Role)
                .Where(u => list.Contains(u.Id) && u.OrganizationId == organizationId);
        }
        var staff = await staffQuery.OrderBy(u => u.LastName).ThenBy(u => u.FirstName).ToListAsync();
        var departmentNames = await StaffLookups.LoadDepartmentNamesAsync(_context, organizationId);

        var records = await _context.StaffPerformanceRecords.AsNoTracking()
            .Where(r => r.DutyId == duty.Id && r.Status == StaffRecordStatus.Final)
            .Select(r => new { r.Id, r.SubjectUserId, r.Outcome, r.Description })
            .ToListAsync();
        var byUser = records.GroupBy(r => r.SubjectUserId).ToDictionary(g => g.Key, g => g.First());

        var prefix = duty.Title + " — ";
        var rows = staff.Select(u =>
        {
            byUser.TryGetValue(u.Id, out var r);
            string? note = r != null && r.Description.StartsWith(prefix, StringComparison.Ordinal) ? r.Description[prefix.Length..] : null;
            return new RegisterRowDto
            {
                UserId = u.Id,
                FullName = StaffPerformanceMapping.FullName(u),
                PhotoUrl = UploadLinks.Sign(u.PhotoUrl),
                JobTitle = u.JobTitle,
                DepartmentNames = StaffLookups.DepartmentNames(u.DepartmentIds, departmentNames),
                Outcome = r?.Outcome,
                Note = note,
                RecordId = r?.Id
            };
        }).ToList();

        var dto = (await MapManyAsync(new List<StaffDuty> { duty }, organizationId, branchId, me, canManage)).First();
        return new StaffRegisterDto { Duty = dto, Rows = rows };
    }

    private async Task<StaffDutyDto> MapOneAsync(Guid dutyId, Guid organizationId, Guid branchId)
    {
        var duty = await _context.StaffDuties.AsNoTracking().Include(d => d.Parameter).FirstAsync(d => d.Id == dutyId);
        var me = CurrentUserId();
        return (await MapManyAsync(new List<StaffDuty> { duty }, organizationId, branchId, me, await HasPermissionAsync(Permissions.StaffDutiesManage))).First();
    }

    private async Task<List<StaffDutyDto>> MapManyAsync(List<StaffDuty> duties, Guid organizationId, Guid branchId, Guid me, bool canManage)
    {
        if (duties.Count == 0) return new List<StaffDutyDto>();

        var dutyIds = duties.Select(d => d.Id).ToList();
        var marks = await _context.StaffPerformanceRecords.AsNoTracking()
            .Where(r => r.DutyId != null && dutyIds.Contains(r.DutyId.Value) && r.Status == StaffRecordStatus.Final)
            .Select(r => new { DutyId = r.DutyId!.Value, r.SubjectUserId, r.Outcome })
            .ToListAsync();
        var marksByDuty = marks.ToLookup(m => m.DutyId);

        int? everyone = null;
        if (duties.Any(d => d.ExpectedUserIds == null))
            everyone = await StaffLookups.BranchStaff(_context, organizationId, branchId).CountAsync();

        var mediaIds = duties.Where(d => d.MinutesMediaContentId.HasValue).Select(d => d.MinutesMediaContentId!.Value).Distinct().ToList();
        var mediaUrls = mediaIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await _context.MediaContents.IgnoreQueryFilters().AsNoTracking()
                .Where(m => mediaIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.FileUrl);

        var names = await StaffLookups.LoadNamesAsync(_context,
            duties.SelectMany(d => d.RecorderUserIds.Select(id => (Guid?)id))
                .Concat(duties.Select(d => d.RegisterClosedByUserId))
                .Concat(duties.SelectMany(d => d.SupervisorUserIds.Select(id => (Guid?)id)))
                // Rota slots name their people; a Session duty's expected list can be the whole branch and is counted, not named.
                .Concat(duties.Where(d => d.Kind == DutyKind.Rota).SelectMany(d => (d.ExpectedUserIds ?? Array.Empty<Guid>()).Select(id => (Guid?)id))), default);

        return duties.Select(d =>
        {
            var dutyMarks = marksByDuty[d.Id].ToList();
            return StaffPerformanceMapping.ToDto(d, names, me, canManage,
                expectedCount: d.ExpectedUserIds?.Length ?? everyone ?? 0,
                markedCount: dutyMarks.Select(m => m.SubjectUserId).Distinct().Count(),
                myOutcome: dutyMarks.FirstOrDefault(m => m.SubjectUserId == me)?.Outcome,
                minutesUrl: d.MinutesMediaContentId.HasValue && mediaUrls.TryGetValue(d.MinutesMediaContentId.Value, out var url) ? url : null);
        }).ToList();
    }
}
