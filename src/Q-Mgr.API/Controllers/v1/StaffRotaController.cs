using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The duty rota (plan §4.1): generate a rotation, extend it, cancel a series, swap two people, check a slot for
/// clashes, and the fairness strip. A rota slot is an ordinary <see cref="StaffDuty"/> of kind Rota — this controller
/// only writes many of them at once; editing one slot, acknowledging it and taking its register stay on
/// <see cref="StaffDutiesController"/>.
///
/// Everything that writes is <c>staff.duties.manage</c>, runs synchronously (never a background job), and runs under
/// <c>pg_advisory_xact_lock</c> on the branch so two generators, or a generate and a swap, cannot interleave. A caller
/// whose staff scope is narrower than the organization may place only people inside it: a person outside scope reads
/// as "not found", never as "not yours" (plan §13.7).
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/staff/rota")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffRotaController : StaffPerformanceControllerBase
{
    private readonly IStaffPerformancePolicyService _policy;
    private readonly INotificationService _notifications;
    private readonly ILogger<StaffRotaController> _logger;

    private const int MaxSlots = 60;

    public StaffRotaController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        INotificationService notifications,
        ILogger<StaffRotaController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>The seeded "Teacher on Duty" parameter's id (inserted if missing) and the policy's rota defaults, for the generator form.</summary>
    [HttpGet("defaults")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    public async Task<IActionResult> GetDefaults(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var parameterId = await StaffRota.EnsureTeacherOnDutyParameterAsync(Db, organizationId, _logger);
        var policy = await _policy.GetAsync(organizationId);
        return Ok(new { parameterId, reportDefaults = policy.DutyReportDefaults ?? new DutyReportDefaultsDto() });
    }

    // ---- Generate -------------------------------------------------------------------------------------

    [HttpPost("generate")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    [ProducesResponseType(typeof(RotaGenerateResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RotaGenerateResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Generate(Guid branchId, [FromBody] GenerateRotaRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);

        var problem = await ValidateGenerateAsync(branchId, organizationId, request);
        if (problem != null) return problem;

        var plan = await PlanAsync(branchId, organizationId, request, staffOffset: 0, supervisorOffset: 0);
        if (plan.Error != null) return plan.Error;
        if (request.Preview) return Ok(plan.Result);

        var seriesId = Guid.NewGuid();
        var duties = await WriteAsync(branchId, organizationId, request, plan, seriesId);
        await Activity.RecordAsync(ActivityActions.RotaGenerated, nameof(StaffDuty), seriesId, null,
            string.Create(CultureInfo.InvariantCulture, $"Rota \"{request.Title}\" generated: {duties.Count} {request.Span.ToString().ToLowerInvariant()} slot(s) from {request.StartDate:dd MMM yyyy}"),
            new { request.Span, Slots = duties.Count, plan.Result.Skipped, People = request.StaffUserIds.Count, Supervisors = request.SupervisorUserIds.Count }, branchId, organizationId);
        await NotifyAsync(branchId, duties);

        return StatusCode(StatusCodes.Status201Created, plan.Result with { SeriesId = seriesId, Preview = false });
    }

    // ---- Extend ---------------------------------------------------------------------------------------

    /// <summary>
    /// Continues a series from its last slot: same span, times, title, parameter, report cadence and people-per-slot,
    /// with the rotation read back from the order people first appear in the series (the pattern is not stored).
    /// </summary>
    [HttpPost("series/{seriesId:guid}/extend")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    [ProducesResponseType(typeof(RotaGenerateResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RotaGenerateResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Extend(Guid branchId, Guid seriesId, [FromBody] ExtendRotaRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);

        var series = await Db.StaffDuties.AsNoTracking()
            .Where(d => d.SeriesId == seriesId && d.BranchId == branchId && d.OrganizationId == organizationId && d.IsActive && d.Kind == DutyKind.Rota)
            .OrderBy(d => d.StartsAt)
            .ToListAsync();
        if (series.Count == 0) return NotFoundProblem("Rota not found");
        if (request.Slots is < 1 or > MaxSlots) return BadRequestProblem($"Extend by 1 to {MaxSlots} slots");

        var zone = await ZoneAsync(branchId);
        var last = series[^1];
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(last.StartsAt, zone);
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(last.EndsAt, zone);
        var lengthDays = (localEnd.Date - localStart.Date).TotalDays + 1;
        var span = lengthDays <= 1 ? RotaSpan.Day : lengthDays <= 10 ? RotaSpan.Week : RotaSpan.Month;

        // The rotation, as first seen, and where the last slot left it.
        var staffOrder = OrderOfAppearance(series.Select(d => d.ExpectedUserIds ?? Array.Empty<Guid>()));
        var supervisorOrder = OrderOfAppearance(series.Select(d => d.SupervisorUserIds));
        var peoplePerSlot = Math.Max(1, last.ExpectedUserIds?.Length ?? 1);
        var supervisorsPerSlot = last.SupervisorUserIds.Length;
        var staffOffset = NextIndex(staffOrder, last.ExpectedUserIds ?? Array.Empty<Guid>());
        var supervisorOffset = NextIndex(supervisorOrder, last.SupervisorUserIds);

        var nextStart = span switch
        {
            RotaSpan.Day => DateOnly.FromDateTime(localStart.Date.AddDays(1)),
            RotaSpan.Week => DateOnly.FromDateTime(localStart.Date.AddDays(7)),
            _ => DateOnly.FromDateTime(localStart.Date.AddMonths(1))
        };
        var generate = new GenerateRotaRequest
        {
            ParameterId = last.ParameterId,
            Title = last.Title,
            Location = last.Location,
            Span = span,
            StartDate = nextStart,
            Slots = request.Slots,
            DayStartLocalTime = localStart.ToString("HH:mm"),
            DayEndLocalTime = localEnd.ToString("HH:mm"),
            StaffUserIds = staffOrder,
            PeoplePerSlot = peoplePerSlot,
            SupervisorUserIds = supervisorOrder,
            SupervisorsPerSlot = supervisorsPerSlot,
            ReportCadence = last.ReportCadence,
            ReportDueLocalTime = last.ReportDueLocalTime?.ToString("HH:mm"),
            // A day rota that has never put anyone on at a weekend keeps skipping weekends.
            SkipWeekends = span == RotaSpan.Day && series.All(d => TimeZoneInfo.ConvertTimeFromUtc(d.StartsAt, zone).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)),
            SkipHolidays = true,
            Preview = request.Preview
        };

        var problem = await ValidateGenerateAsync(branchId, organizationId, generate);
        if (problem != null) return problem;
        var plan = await PlanAsync(branchId, organizationId, generate, staffOffset, supervisorOffset);
        if (plan.Error != null) return plan.Error;
        if (request.Preview) return Ok(plan.Result with { SeriesId = seriesId });

        var duties = await WriteAsync(branchId, organizationId, generate, plan, seriesId);
        await Activity.RecordAsync(ActivityActions.RotaExtended, nameof(StaffDuty), seriesId, null,
            string.Create(CultureInfo.InvariantCulture, $"Rota \"{last.Title}\" extended by {duties.Count} slot(s) from {nextStart:dd MMM yyyy}"),
            new { Slots = duties.Count, plan.Result.Skipped }, branchId, organizationId);
        await NotifyAsync(branchId, duties);
        return StatusCode(StatusCodes.Status201Created, plan.Result with { SeriesId = seriesId, Preview = false });
    }

    // ---- Cancel a series ------------------------------------------------------------------------------

    /// <summary>
    /// Stops a series. Slots that have not started are cancelled. A slot UNDER WAY whose register is not closed is
    /// ENDED NOW rather than kept (2026-09-24): it used to be left running to its end, so cancelling a weekly rota on a
    /// Thursday changed nothing anybody could see, pressing again reported 0, and the slot went on asking for daily
    /// reports to Saturday. Its days so far stay as history — acknowledgements, reports filed, the close-out still to
    /// take — because reports, reminders and the register chase all read EndsAt. A closed-out or finished slot is kept.
    /// </summary>
    [HttpDelete("series/{seriesId:guid}")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    public async Task<IActionResult> CancelSeries(Guid branchId, Guid seriesId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var now = DateTime.UtcNow;

        var series = await Db.StaffDuties
            .Where(d => d.SeriesId == seriesId && d.BranchId == branchId && d.OrganizationId == organizationId && d.IsActive && d.Kind == DutyKind.Rota)
            .ToListAsync();
        if (series.Count == 0) return NotFoundProblem("Rota not found");
        if (!await AllInScopeAsync(branchId, series.SelectMany(d => (d.ExpectedUserIds ?? Array.Empty<Guid>()).Concat(d.SupervisorUserIds))))
            return NotFoundProblem("Rota not found");

        var open = series.Where(d => d.StartsAt > now && d.RegisterClosedAt == null).ToList();
        var running = series.Where(d => d.StartsAt <= now && d.EndsAt > now && d.RegisterClosedAt == null).ToList();
        foreach (var d in open)
        {
            d.IsActive = false;
            d.UpdatedAt = now;
            d.UpdatedBy = CurrentUserId();
        }
        foreach (var d in running)
        {
            d.EndsAt = now;
            d.UpdatedAt = now;
            d.UpdatedBy = CurrentUserId();
        }
        await Db.SaveChangesAsync();

        var kept = series.Count - open.Count - running.Count;
        await Activity.RecordAsync(ActivityActions.RotaSeriesCancelled, nameof(StaffDuty), seriesId, null,
            $"Rota \"{series[0].Title}\": {open.Count} upcoming slot(s) cancelled, {running.Count} ended now, {kept} kept",
            new { Cancelled = open.Count, Ended = running.Count, Kept = kept }, branchId, organizationId);
        return Ok(new { cancelled = open.Count, ended = running.Count, kept });
    }

    // ---- Swap -----------------------------------------------------------------------------------------

    [HttpPost("swap")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    [ProducesResponseType(typeof(List<StaffDutyDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Swap(Guid branchId, [FromBody] SwapRotaRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        if (request.FirstDutyId == request.SecondDutyId) return BadRequestProblem("Choose two different slots");
        if (request.FirstUserId == request.SecondUserId) return BadRequestProblem("Choose two different people");
        if (!await AllInScopeAsync(branchId, new[] { request.FirstUserId, request.SecondUserId }))
            return StaffMemberNotFound();

        var now = DateTime.UtcNow;
        StaffDuty? first = null, second = null;
        IActionResult? refusal = null;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            refusal = null;
            await using var tx = await Db.Database.BeginTransactionAsync();
            var lockKey = $"staff-rota:{branchId}";
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");

            first = await Db.StaffDuties.FirstOrDefaultAsync(d => d.Id == request.FirstDutyId && d.BranchId == branchId && d.OrganizationId == organizationId && d.IsActive && d.Kind == DutyKind.Rota);
            second = await Db.StaffDuties.FirstOrDefaultAsync(d => d.Id == request.SecondDutyId && d.BranchId == branchId && d.OrganizationId == organizationId && d.IsActive && d.Kind == DutyKind.Rota);
            if (first == null || second == null) { refusal = NotFoundProblem("Rota slot not found"); return; }
            if (first.EndsAt < now || second.EndsAt < now || first.RegisterClosedAt != null || second.RegisterClosedAt != null)
            { refusal = BadRequestProblem("A finished slot cannot be swapped", "Both slots must still be to come."); return; }
            var a = first.ExpectedUserIds ?? Array.Empty<Guid>();
            var b = second.ExpectedUserIds ?? Array.Empty<Guid>();
            if (!a.Contains(request.FirstUserId) || !b.Contains(request.SecondUserId))
            { refusal = BadRequestProblem("Each person must be on the slot they are swapping out of"); return; }
            if (a.Contains(request.SecondUserId) || b.Contains(request.FirstUserId))
            { refusal = BadRequestProblem("One of them is already on the other slot"); return; }

            first.ExpectedUserIds = a.Select(id => id == request.FirstUserId ? request.SecondUserId : id).ToArray();
            second.ExpectedUserIds = b.Select(id => id == request.SecondUserId ? request.FirstUserId : id).ToArray();
            // An acknowledgement belongs to the person on the slot; whoever left takes theirs with them.
            foreach (var (duty, leaving) in new[] { (first, request.FirstUserId), (second, request.SecondUserId) })
            {
                var acks = StaffPerformanceMapping.ParseAcknowledgements(duty.Acknowledgements);
                if (acks.Remove(leaving)) duty.Acknowledgements = StaffPerformanceMapping.SerializeAcknowledgements(acks);
                duty.UpdatedAt = now;
                duty.UpdatedBy = CurrentUserId();
            }
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        if (refusal != null) return refusal;

        var names = await BuildNamesAsync(new Guid?[] { request.FirstUserId, request.SecondUserId });
        // One event for the exchange, as the plan asks; it names both people, so each person's trail carries it via the subject of the first.
        await Activity.RecordAsync(ActivityActions.RotaSwapped, nameof(StaffDuty), first!.Id, request.FirstUserId,
            string.Create(CultureInfo.InvariantCulture, $"Rota swap: {names[request.FirstUserId]} ({first.Title}, {first.StartsAt:dd MMM}) and {names[request.SecondUserId]} ({second!.Title}, {second.StartsAt:dd MMM})"),
            new { FirstDutyId = first.Id, SecondDutyId = second.Id, request.FirstUserId, request.SecondUserId, Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim() },
            branchId, organizationId);

        var zone = await ZoneAsync(branchId);
        await StaffRota.NotifyAssignedAsync(_notifications, _logger, first, new[] { request.SecondUserId }, zone, supervising: false);
        await StaffRota.NotifyAssignedAsync(_notifications, _logger, second, new[] { request.FirstUserId }, zone, supervising: false);
        foreach (var supervisor in first.SupervisorUserIds.Concat(second.SupervisorUserIds).Distinct())
        {
            try
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = supervisor,
                    OrganizationId = organizationId,
                    BranchId = branchId,
                    Title = "A rota swap affects a slot you supervise",
                    Message = string.Create(CultureInfo.InvariantCulture, $"{first.Title}, {TimeZoneInfo.ConvertTimeFromUtc(first.StartsAt, zone):ddd dd MMM} and {TimeZoneInfo.ConvertTimeFromUtc(second.StartsAt, zone):ddd dd MMM}."),
                    Type = NotificationType.StaffPerformance,
                    Channels = NotificationChannel.InApp,
                    EventKey = NotificationEventKeys.StaffRotaAssigned,
                    ActionUrl = "/admin/staff/duties?tab=rota",
                    IconClass = "arrow-left-right"
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "Swap notice could not reach supervisor {UserId}", supervisor); }
        }

        return Ok(new { swapped = true, firstDutyId = first.Id, secondDutyId = second.Id });
    }

    // ---- Check and fairness ---------------------------------------------------------------------------

    [HttpPost("check")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    [ProducesResponseType(typeof(List<RotaWarningDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Check(Guid branchId, [FromBody] CheckRotaSlotRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var startsAt = DateTime.SpecifyKind(request.StartsAt, DateTimeKind.Utc);
        var endsAt = DateTime.SpecifyKind(request.EndsAt, DateTimeKind.Utc);
        if (endsAt <= startsAt) return BadRequestProblem("The slot must end after it starts");

        var warnings = await StaffRota.CheckAsync(Db, _policy, policy, organizationId, branchId, startsAt, endsAt,
            (request.UserIds ?? new()).Distinct().ToList(), (request.SupervisorUserIds ?? new()).Distinct().ToList(), request.ExcludeDutyId);
        if (StaffRota.IsHoliday(policy, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(startsAt, await ZoneAsync(branchId)))))
            warnings.Add(new RotaWarningDto { Kind = RotaWarningKind.OutsideTerm, Message = "This slot starts outside every term the policy defines." });
        return Ok(warnings);
    }

    /// <summary>
    /// Rota slots per person in a term (plan §4.1, Arbor's cover statistics), for everyone the caller's staff scope
    /// reaches — a head of department sees their department's fairness, not the school's.
    /// </summary>
    [HttpGet("fairness")]
    [ProducesResponseType(typeof(RotaFairnessDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Fairness(Guid branchId, [FromQuery] string? period = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (!await HasPermissionAsync(Permissions.StaffDutiesManage) && !await HasPermissionAsync(Permissions.StaffRecordsView))
            return StatusCode(StatusCodes.Status403Forbidden);

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var p = _policy.FindPeriod(policy, period) ?? _policy.PeriodFor(policy, DateOnly.FromDateTime(DateTime.UtcNow));
        var from = p.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = p.End.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        var slots = await Db.StaffDuties.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.BranchId == branchId && d.IsActive && d.Kind == DutyKind.Rota && d.StartsAt >= from && d.StartsAt <= to)
            .Select(d => new { d.ExpectedUserIds, d.SupervisorUserIds, d.Acknowledgements })
            .ToListAsync();
        var visible = await StaffScope.GetVisibleUserIdsAsync(branchId);
        var staff = await StaffLookups.BranchStaff(Db, organizationId, branchId).ToListAsync();
        var limit = (policy.DutyReportDefaults ?? new DutyReportDefaultsDto()).MaxRotaSlotsPerTerm;

        var rows = staff
            .Where(u => visible == null || visible.Contains(u.Id))
            .Select(u =>
            {
                var onDuty = slots.Count(s => s.ExpectedUserIds != null && s.ExpectedUserIds.Contains(u.Id));
                return new RotaFairnessRowDto
                {
                    UserId = u.Id,
                    FullName = StaffPerformanceMapping.FullName(u),
                    OnDutySlots = onDuty,
                    SupervisingSlots = slots.Count(s => s.SupervisorUserIds.Contains(u.Id)),
                    Acknowledged = slots.Count(s => s.ExpectedUserIds != null && s.ExpectedUserIds.Contains(u.Id) && StaffPerformanceMapping.ParseAcknowledgements(s.Acknowledgements).ContainsKey(u.Id)),
                    OverLimit = limit > 0 && onDuty > limit
                };
            })
            .Where(r => r.OnDutySlots > 0 || r.SupervisingSlots > 0)
            .OrderByDescending(r => r.OnDutySlots).ThenBy(r => r.FullName)
            .ToList();

        return Ok(new RotaFairnessDto { PeriodKey = p.Key, PeriodName = p.Name, Limit = limit, Rows = rows });
    }

    // ---- Planning and writing -------------------------------------------------------------------------

    private sealed record PlannedSlot(DateTime StartsAt, DateTime EndsAt, List<Guid> Users, List<Guid> Supervisors);
    private sealed record Plan(RotaGenerateResultDto Result, List<PlannedSlot> Slots, IActionResult? Error);

    private async Task<IActionResult?> ValidateGenerateAsync(Guid branchId, Guid organizationId, GenerateRotaRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequestProblem("A title is required");
        if (request.Slots is < 1 or > MaxSlots) return BadRequestProblem($"Generate 1 to {MaxSlots} slots at a time");
        if (!Enum.IsDefined(request.Span)) return BadRequestProblem("Unrecognised span");
        if (request.StaffUserIds is not { Count: > 0 }) return BadRequestProblem("Choose the staff to rotate through");
        if (request.PeoplePerSlot < 1 || request.PeoplePerSlot > request.StaffUserIds.Distinct().Count())
            return BadRequestProblem("People per slot must be at least one and no more than the people in the rotation");
        if (request.SupervisorsPerSlot < 0 || (request.SupervisorUserIds.Count > 0 && request.SupervisorsPerSlot > request.SupervisorUserIds.Distinct().Count()))
            return BadRequestProblem("Supervisors per slot cannot exceed the supervisors in the rotation");
        if (StaffRota.ParseLocalTime(request.DayStartLocalTime) == null || StaffRota.ParseLocalTime(request.DayEndLocalTime) == null)
            return BadRequestProblem("Start and end times must be 24-hour times such as 07:00");
        if (!string.IsNullOrWhiteSpace(request.ReportDueLocalTime) && StaffRota.ParseLocalTime(request.ReportDueLocalTime) == null)
            return BadRequestProblem("The report due time must be a 24-hour time such as 18:00");
        if (request.ReportCadence is { } c && !Enum.IsDefined(c)) return BadRequestProblem("Unrecognised report cadence");

        if (request.ParameterId == Guid.Empty)
            request.ParameterId = await StaffRota.EnsureTeacherOnDutyParameterAsync(Db, organizationId, _logger);
        var parameter = await Db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.ParameterId && p.OrganizationId == organizationId);
        if (parameter == null || !parameter.IsActive) return BadRequestProblem("Parameter not found", "Choose an active Duty or Attendance parameter.");
        if (parameter.Kind is not (ParameterKind.Attendance or ParameterKind.Duty))
            return BadRequestProblem("A rota needs a Duty or Attendance parameter");

        var people = request.StaffUserIds.Concat(request.SupervisorUserIds).Distinct().ToList();
        var active = await Db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => people.Contains(u.Id) && u.OrganizationId == organizationId && u.IsActive)
            .Select(u => u.Id).ToListAsync();
        // Not in the organization, inactive, or outside a scoped caller's reach: all read as "not found" (plan §13.7).
        if (active.Count != people.Count || !await AllInScopeAsync(branchId, people))
            return BadRequestProblem("Some people could not be found", "Every person on a rota must be an active member of staff you can manage.");
        return null;
    }

    /// <summary>Lays out the slots (dates, people, supervisors) and their warnings. Writes nothing.</summary>
    private async Task<Plan> PlanAsync(Guid branchId, Guid organizationId, GenerateRotaRequest request, int staffOffset, int supervisorOffset)
    {
        var zone = await ZoneAsync(branchId);
        var policy = await _policy.GetAsync(organizationId);
        var dayStart = StaffRota.ParseLocalTime(request.DayStartLocalTime)!.Value;
        var dayEnd = StaffRota.ParseLocalTime(request.DayEndLocalTime)!.Value;
        var staff = request.StaffUserIds.Distinct().ToList();
        var supervisors = request.SupervisorUserIds.Distinct().ToList();

        var slots = new List<PlannedSlot>();
        var skipped = 0;
        var date = request.StartDate;
        var p = staffOffset;
        var q = supervisorOffset;
        for (var guard = 0; slots.Count < request.Slots && guard < request.Slots * 6 + 60; guard++)
        {
            var (first, last) = request.Span switch
            {
                RotaSpan.Day => (date, date),
                RotaSpan.Week => (date, date.AddDays(6)),
                _ => (date, date.AddMonths(1).AddDays(-1))
            };
            var next = request.Span switch { RotaSpan.Day => date.AddDays(1), RotaSpan.Week => date.AddDays(7), _ => date.AddMonths(1) };

            var weekend = request.Span == RotaSpan.Day && request.SkipWeekends && first.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (weekend || (request.SkipHolidays && StaffRota.IsHoliday(policy, first)))
            {
                skipped++;
                date = next;
                continue;
            }

            var startsAt = TimeZoneInfo.ConvertTimeToUtc(first.ToDateTime(dayStart), zone);
            var endsAt = TimeZoneInfo.ConvertTimeToUtc(last.ToDateTime(dayEnd), zone);
            if (endsAt <= startsAt)
                return new Plan(new RotaGenerateResultDto(), slots, BadRequestProblem("Each slot must end after it starts", "For a one-day slot, the end time must be after the start time."));

            var users = Enumerable.Range(0, request.PeoplePerSlot).Select(k => staff[(p + k) % staff.Count]).Distinct().ToList();
            p += request.PeoplePerSlot;
            var sups = supervisors.Count == 0 || request.SupervisorsPerSlot == 0
                ? new List<Guid>()
                : Enumerable.Range(0, request.SupervisorsPerSlot).Select(k => supervisors[(q + k) % supervisors.Count]).Distinct().ToList();
            if (sups.Count > 0) q += request.SupervisorsPerSlot;

            slots.Add(new PlannedSlot(startsAt, endsAt, users, sups));
            date = next;
        }

        var names = await BuildNamesAsync(staff.Concat(supervisors).Select(id => (Guid?)id));
        var pending = slots.Select(s => (s.StartsAt, s.EndsAt, (IReadOnlyCollection<Guid>)s.Users.Concat(s.Supervisors).ToList())).ToList();
        var previews = new List<RotaSlotPreviewDto>();
        for (var i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            var others = pending.Where((_, j) => j != i).ToList();
            var warnings = await StaffRota.CheckAsync(Db, _policy, policy, organizationId, branchId, s.StartsAt, s.EndsAt, s.Users, s.Supervisors, null, others, names);
            previews.Add(new RotaSlotPreviewDto
            {
                StartsAt = s.StartsAt,
                EndsAt = s.EndsAt,
                UserIds = s.Users,
                Names = s.Users.Select(id => names[id]).ToList(),
                SupervisorUserIds = s.Supervisors,
                SupervisorNames = s.Supervisors.Select(id => names[id]).ToList(),
                Warnings = warnings
            });
        }

        var cadence = request.ReportCadence ?? (slots.Count > 0 ? StaffRota.DefaultCadence(policy, slots[0].StartsAt, slots[0].EndsAt) : ReportCadence.None);
        return new Plan(new RotaGenerateResultDto { Preview = true, Slots = previews, Skipped = skipped, ReportCadence = cadence }, slots, null);
    }

    private async Task<List<StaffDuty>> WriteAsync(Guid branchId, Guid organizationId, GenerateRotaRequest request, Plan plan, Guid seriesId)
    {
        var me = CurrentUserId();
        var dueTime = StaffRota.ParseLocalTime(request.ReportDueLocalTime);
        var duties = new List<StaffDuty>();
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            foreach (var stale in duties) Db.Entry(stale).State = EntityState.Detached;
            duties.Clear();
            await using var tx = await Db.Database.BeginTransactionAsync();
            var lockKey = $"staff-rota:{branchId}";
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");
            foreach (var slot in plan.Slots)
            {
                var duty = new StaffDuty
                {
                    OrganizationId = organizationId,
                    BranchId = branchId,
                    ParameterId = request.ParameterId,
                    Kind = DutyKind.Rota,
                    SeriesId = seriesId,
                    Title = request.Title.Trim().Length > 200 ? request.Title.Trim()[..200] : request.Title.Trim(),
                    Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
                    StartsAt = slot.StartsAt,
                    EndsAt = slot.EndsAt,
                    ExpectedUserIds = slot.Users.ToArray(),
                    SupervisorUserIds = slot.Supervisors.ToArray(),
                    RecorderUserIds = slot.Supervisors.ToArray(),
                    ReportCadence = plan.Result.ReportCadence,
                    ReportDueLocalTime = dueTime,
                    CreatedByUserId = me,
                    CreatedBy = me
                };
                Db.StaffDuties.Add(duty);
                duties.Add(duty);
            }
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        return duties;
    }

    private async Task NotifyAsync(Guid branchId, List<StaffDuty> duties)
    {
        var zone = await ZoneAsync(branchId);
        foreach (var duty in duties.Where(d => d.EndsAt > DateTime.UtcNow))
        {
            await StaffRota.NotifyAssignedAsync(_notifications, _logger, duty, duty.ExpectedUserIds ?? Array.Empty<Guid>(), zone, supervising: false);
            await StaffRota.NotifyAssignedAsync(_notifications, _logger, duty, duty.SupervisorUserIds, zone, supervising: true);
        }
    }

    private async Task<bool> AllInScopeAsync(Guid branchId, IEnumerable<Guid> userIds)
    {
        var visible = await StaffScope.GetVisibleUserIdsAsync(branchId);
        return visible == null || userIds.All(visible.Contains);
    }

    private async Task<TimeZoneInfo> ZoneAsync(Guid branchId)
        => AppointmentScheduling.ResolveTimeZone(await Db.Branches.Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());

    private static List<Guid> OrderOfAppearance(IEnumerable<Guid[]> slots)
    {
        var order = new List<Guid>();
        foreach (var slot in slots)
            foreach (var id in slot)
                if (!order.Contains(id)) order.Add(id);
        return order;
    }

    /// <summary>The rotation index just after the last person the last slot used.</summary>
    private static int NextIndex(List<Guid> order, Guid[] lastSlot)
        => order.Count == 0 || lastSlot.Length == 0 ? 0 : (order.IndexOf(lastSlot[^1]) + 1) % order.Count;
}
