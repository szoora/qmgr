using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.API.Authorization;
using QMgr.Application.Commands.Queue;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Enums;
using QMgr.Filters;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Scheduling — the "before you arrive" half of queue management. Every action is branch-scoped
/// and verifies branch ownership explicitly: Appointment has no global tenant query filter (same
/// as Token, Counter, Visitor and Feedback), and its rows carry real customer PII, so an
/// unverified branchId would be a cross-tenant read.
/// <para>
/// Permissions deliberately reuse the existing queue/token constants rather than inventing an
/// appointments.* family: an appointment is a queue ticket that has not happened yet, and every
/// role that may issue, view, cancel or manage tokens is exactly the role that should be able to
/// do the same to a booking. No new permission is needed, and adding one would mean re-seeding
/// permissions and re-granting five default roles for no behavioural difference.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/appointments")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — every action also carries its own [RequirePermission]
[RequireModule(ModuleCodes.CoreQueue)]
public class AppointmentsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly IMediator _mediator;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ILogger<AppointmentsController> _logger;

    public AppointmentsController(
        QMgrDbContext context,
        IMediator mediator,
        ITenantContextAccessor tenantAccessor,
        ILogger<AppointmentsController> logger)
    {
        _context = context;
        _mediator = mediator;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    // =======================================================================================
    // Reads
    // =======================================================================================

    /// <summary>
    /// Appointments in a date range, newest-first by scheduled time. <paramref name="from"/> and
    /// <paramref name="to"/> are UTC instants; omit both for "today onwards, next 7 days".
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.TokensView)]
    [ProducesResponseType(typeof(List<AppointmentDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAppointments(
        Guid branchId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] AppointmentStatus? status = null,
        [FromQuery] Guid? serviceTypeId = null,
        CancellationToken cancellationToken = default)
    {
        var verify = await VerifyBranchOwnership(branchId);
        if (verify != null) return verify;

        var rangeStart = AppointmentScheduling.ToUtc(from ?? DateTime.UtcNow.Date);
        var rangeEnd = AppointmentScheduling.ToUtc(to ?? rangeStart.AddDays(7));
        if (rangeEnd < rangeStart)
            return BadRequest(new ProblemDetails { Title = "Invalid range", Detail = "'to' must not be earlier than 'from'.", Status = StatusCodes.Status400BadRequest });

        // Hard ceiling so a mistyped range can't ask for the whole table.
        if ((rangeEnd - rangeStart).TotalDays > 400)
            rangeEnd = rangeStart.AddDays(400);

        var query = _context.Appointments
            .Where(a => a.BranchId == branchId && a.ScheduledAt >= rangeStart && a.ScheduledAt < rangeEnd);

        if (status.HasValue)
            query = query.Where(a => a.Status == status.Value);
        if (serviceTypeId.HasValue)
            query = query.Where(a => a.ServiceTypeId == serviceTypeId.Value);

        var results = await query
            .OrderBy(a => a.ScheduledAt)
            .Select(Projection)
            .ToListAsync(cancellationToken);

        return Ok(results);
    }

    /// <summary>One appointment. BranchId is part of the predicate, so a foreign id 404s.</summary>
    [HttpGet("{appointmentId:guid}")]
    [RequirePermission(Permissions.TokensView)]
    [ProducesResponseType(typeof(AppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAppointment(Guid branchId, Guid appointmentId, CancellationToken cancellationToken)
    {
        var verify = await VerifyBranchOwnership(branchId);
        if (verify != null) return verify;

        var dto = await _context.Appointments
            .Where(a => a.Id == appointmentId && a.BranchId == branchId)
            .Select(Projection)
            .FirstOrDefaultAsync(cancellationToken);

        return dto == null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Bookable slots for one service type on one calendar day (the branch's own local calendar).
    /// Free/busy only — no other customer's details are ever included, because the anonymous
    /// booking page calls the identical computation.
    /// </summary>
    [HttpGet("availability")]
    [RequirePermission(Permissions.TokensView)]
    [ProducesResponseType(typeof(AppointmentAvailabilityDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailability(
        Guid branchId,
        [FromQuery] Guid serviceTypeId,
        [FromQuery] string? date = null,
        CancellationToken cancellationToken = default)
    {
        var verify = await VerifyBranchOwnership(branchId);
        if (verify != null) return verify;

        var (result, error) = await ComputeAvailabilityAsync(_context, branchId, serviceTypeId, date, cancellationToken);
        if (error != null) return BadRequest(error);
        return Ok(result);
    }

    // =======================================================================================
    // Writes
    // =======================================================================================

    /// <summary>
    /// Books an appointment on behalf of a customer. Accepts either serviceTypeId or
    /// serviceTypeCode — the same either/or the inbound webhook's appointment.created payload
    /// uses, so an integration already speaking that shape can post it here unchanged.
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.TokensCreate)]
    [ProducesResponseType(typeof(AppointmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAppointment(Guid branchId, [FromBody] CreateAppointmentRequest request, CancellationToken cancellationToken)
    {
        var verify = await VerifyBranchOwnership(branchId);
        if (verify != null) return verify;

        var organizationId = await ResolveOrganizationIdAsync(branchId, cancellationToken);

        var outcome = await AppointmentScheduling.BookAsync(
            _context,
            branchId,
            organizationId,
            new AppointmentScheduling.BookingInput
            {
                ServiceTypeId = request.ServiceTypeId,
                ServiceTypeCode = request.ServiceTypeCode,
                CustomerName = request.CustomerName,
                CustomerPhone = request.CustomerPhone,
                CustomerEmail = request.CustomerEmail,
                ScheduledAt = request.ScheduledAt,
                DurationMinutes = request.DurationMinutes,
                Notes = request.Notes,
                ExternalReference = request.ExternalReference,
                ExternalSystem = request.ExternalSystem,
                CreatedByUserId = CurrentUserId(),
                // Staff booking on the phone routinely needs to slot someone in for "in 10
                // minutes" or a same-day gap the public page would refuse. The lead-time and
                // opening-hours rules exist to protect the *public* form from nonsense, not to
                // stop a receptionist doing their job.
                EnforceOpeningHours = false,
                EnforceLeadTime = false
            },
            cancellationToken);

        if (outcome.Error != null)
            return outcome.Conflict ? Conflict(outcome.Error) : BadRequest(outcome.Error);

        _logger.LogInformation("Appointment {AppointmentId} ({Reference}) booked for branch {BranchId} at {ScheduledAt:o}",
            outcome.Appointment!.Id, outcome.Appointment.ReferenceCode, branchId, outcome.Appointment.ScheduledAt);

        var dto = await _context.Appointments
            .Where(a => a.Id == outcome.Appointment.Id)
            .Select(Projection)
            .FirstAsync(cancellationToken);

        return CreatedAtAction(nameof(GetAppointment), new { branchId, appointmentId = dto.Id }, dto);
    }

    /// <summary>Moves an appointment to a new time (and optionally a new duration).</summary>
    [HttpPost("{appointmentId:guid}/reschedule")]
    [RequirePermission(Permissions.QueueManage)]
    [ProducesResponseType(typeof(AppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RescheduleAppointment(
        Guid branchId, Guid appointmentId, [FromBody] RescheduleAppointmentRequest request, CancellationToken cancellationToken)
    {
        var verify = await VerifyBranchOwnership(branchId);
        if (verify != null) return verify;

        var appointment = await _context.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId && a.BranchId == branchId, cancellationToken);
        if (appointment == null) return NotFound();

        if (appointment.Status is AppointmentStatus.Completed or AppointmentStatus.Cancelled or AppointmentStatus.CheckedIn)
            return Conflict(new { error = "NOT_RESCHEDULABLE", status = appointment.Status.ToString(), message = "Only a booked, confirmed or no-show appointment can be moved." });

        var newStart = AppointmentScheduling.ToUtc(request.ScheduledAt);
        if (newStart == default)
            return BadRequest(new { error = "INVALID_TIME", message = "A scheduled time is required." });

        var duration = AppointmentScheduling.ClampDuration(request.DurationMinutes ?? appointment.DurationMinutes);

        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
        var settings = AppointmentScheduling.ReadSettings(branch?.Settings);

        var conflict = await AppointmentScheduling.SlotIsFullAsync(
            _context, branchId, appointment.ServiceTypeId, newStart, duration, settings.CapacityPerSlot, appointment.Id, cancellationToken);
        if (conflict)
            return Conflict(new { error = "SLOT_FULL", message = "That time is already fully booked for this service." });

        appointment.ScheduledAt = newStart;
        appointment.DurationMinutes = duration;
        // A moved appointment must be reminded about again at its new time.
        appointment.ReminderSentAt = null;
        if (appointment.Status == AppointmentStatus.NoShow)
            appointment.Status = AppointmentStatus.Booked;
        appointment.UpdatedAt = DateTime.UtcNow;
        appointment.UpdatedBy = CurrentUserId();
        if (!string.IsNullOrWhiteSpace(request.Reason))
            appointment.Notes = AppointmentScheduling.Truncate($"{appointment.Notes}\nRescheduled: {request.Reason.Trim()}".Trim(), 1000);

        await _context.SaveChangesAsync(cancellationToken);

        var dto = await _context.Appointments.Where(a => a.Id == appointment.Id).Select(Projection).FirstAsync(cancellationToken);
        return Ok(dto);
    }

    /// <summary>Cancels an appointment. Terminal states are refused rather than silently re-cancelled.</summary>
    [HttpPost("{appointmentId:guid}/cancel")]
    [RequirePermission(Permissions.TokensCancel)]
    [ProducesResponseType(typeof(AppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelAppointment(
        Guid branchId, Guid appointmentId, [FromBody] CancelAppointmentRequest? request, CancellationToken cancellationToken)
    {
        var verify = await VerifyBranchOwnership(branchId);
        if (verify != null) return verify;

        var appointment = await _context.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId && a.BranchId == branchId, cancellationToken);
        if (appointment == null) return NotFound();

        if (appointment.Status is AppointmentStatus.Cancelled or AppointmentStatus.Completed)
            return Conflict(new { error = "ALREADY_TERMINAL", status = appointment.Status.ToString() });

        appointment.Status = AppointmentStatus.Cancelled;
        appointment.CancelledAt = DateTime.UtcNow;
        appointment.CancellationReason = AppointmentScheduling.Truncate(request?.Reason, 500);
        appointment.UpdatedAt = DateTime.UtcNow;
        appointment.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync(cancellationToken);

        var dto = await _context.Appointments.Where(a => a.Id == appointment.Id).Select(Projection).FirstAsync(cancellationToken);
        return Ok(dto);
    }

    /// <summary>Marks a booking as a no-show by hand (the recurring sweep does the same automatically).</summary>
    [HttpPost("{appointmentId:guid}/no-show")]
    [RequirePermission(Permissions.QueueManage)]
    [ProducesResponseType(typeof(AppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MarkNoShow(Guid branchId, Guid appointmentId, CancellationToken cancellationToken)
    {
        var verify = await VerifyBranchOwnership(branchId);
        if (verify != null) return verify;

        var appointment = await _context.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId && a.BranchId == branchId, cancellationToken);
        if (appointment == null) return NotFound();

        if (appointment.Status is not (AppointmentStatus.Booked or AppointmentStatus.Confirmed))
            return Conflict(new { error = "NOT_PENDING", status = appointment.Status.ToString(), message = "Only a booked or confirmed appointment can be marked as a no-show." });

        appointment.Status = AppointmentStatus.NoShow;
        appointment.UpdatedAt = DateTime.UtcNow;
        appointment.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync(cancellationToken);

        var dto = await _context.Appointments.Where(a => a.Id == appointment.Id).Select(Projection).FirstAsync(cancellationToken);
        return Ok(dto);
    }

    /// <summary>
    /// The arrival moment: turns the booking into a real queue ticket through the existing
    /// <see cref="CreateTokenCommand"/> — so it gets a proper display number, an estimated wait,
    /// the outbound token.created webhook and usage metering, exactly like a walk-in — and links
    /// the two records. Issued at <see cref="TokenPriority.Priority"/> because someone who booked
    /// ahead should not queue behind the walk-ins who arrived while they were travelling.
    /// </summary>
    [HttpPost("{appointmentId:guid}/check-in")]
    [RequirePermission(Permissions.QueueManage)]
    [ProducesResponseType(typeof(AppointmentCheckInResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CheckIn(Guid branchId, Guid appointmentId, CancellationToken cancellationToken)
    {
        var verify = await VerifyBranchOwnership(branchId);
        if (verify != null) return verify;

        var appointment = await _context.Appointments
            .Include(a => a.ServiceType)
            .FirstOrDefaultAsync(a => a.Id == appointmentId && a.BranchId == branchId, cancellationToken);
        if (appointment == null) return NotFound();

        if (appointment.Status is AppointmentStatus.CheckedIn && appointment.TokenId != null)
        {
            // Idempotent: a double-tap on the check-in button must not issue a second ticket.
            var existingDto = await _context.Appointments.Where(a => a.Id == appointment.Id).Select(Projection).FirstAsync(cancellationToken);
            return Ok(new AppointmentCheckInResult { Appointment = existingDto, TokenId = appointment.TokenId, AlreadyCheckedIn = true });
        }

        if (appointment.Status is AppointmentStatus.Cancelled or AppointmentStatus.Completed)
            return Conflict(new { error = "NOT_CHECKABLE", status = appointment.Status.ToString(), message = "A cancelled or completed appointment cannot be checked in." });

        var serviceTypeCode = appointment.ServiceType?.Code;
        if (string.IsNullOrWhiteSpace(serviceTypeCode))
            return Conflict(new { error = "SERVICE_TYPE_MISSING", message = "This appointment's service type no longer exists." });

        TokenDto token;
        try
        {
            token = await _mediator.Send(new CreateTokenCommand
            {
                BranchId = branchId,
                ServiceTypeCode = serviceTypeCode,
                Customer = new CustomerDto
                {
                    Name = appointment.CustomerName,
                    Phone = appointment.CustomerPhone,
                    Email = appointment.CustomerEmail
                },
                Source = TokenSource.Appointment,
                Priority = TokenPriority.Priority,
                ExternalReference = appointment.ExternalReference ?? appointment.ReferenceCode,
                ExternalSystem = appointment.ExternalSystem ?? AppointmentScheduling.AppointmentExternalSystem,
                EstimatedArrival = appointment.ScheduledAt
            }, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = "TOKEN_NOT_CREATED", message = ex.Message });
        }

        appointment.Status = AppointmentStatus.CheckedIn;
        appointment.CheckedInAt = DateTime.UtcNow;
        appointment.TokenId = token.Id;
        appointment.UpdatedAt = DateTime.UtcNow;
        appointment.UpdatedBy = CurrentUserId();
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Appointment {AppointmentId} checked in as token {TokenId} ({DisplayNumber})",
            appointment.Id, token.Id, token.DisplayNumber);

        var dto = await _context.Appointments.Where(a => a.Id == appointment.Id).Select(Projection).FirstAsync(cancellationToken);
        return Ok(new AppointmentCheckInResult { Appointment = dto, TokenId = token.Id, TokenDisplayNumber = token.DisplayNumber });
    }

    // =======================================================================================
    // Shared helpers
    // =======================================================================================

    /// <summary>
    /// Projection used by every read here. An <see cref="Expression"/> rather than a method, so
    /// EF Core translates it into the SQL SELECT instead of failing with "could not be
    /// translated" — and written exactly once, so no field is mapped two different ways in two
    /// places (the failure mode CLAUDE.md's DTO-drift note warns about).
    /// </summary>
    internal static readonly Expression<Func<Appointment, AppointmentDto>> Projection = a => new AppointmentDto
    {
        Id = a.Id,
        OrganizationId = a.OrganizationId,
        BranchId = a.BranchId,
        ServiceTypeId = a.ServiceTypeId,
        ReferenceCode = a.ReferenceCode,
        CustomerName = a.CustomerName,
        CustomerPhone = a.CustomerPhone,
        CustomerEmail = a.CustomerEmail,
        ScheduledAt = a.ScheduledAt,
        DurationMinutes = a.DurationMinutes,
        Status = a.Status,
        Notes = a.Notes,
        ExternalReference = a.ExternalReference,
        ExternalSystem = a.ExternalSystem,
        TokenId = a.TokenId,
        TokenDisplayNumber = a.Token != null ? a.Token.DisplayNumber : null,
        CreatedByUserId = a.CreatedByUserId,
        CheckedInAt = a.CheckedInAt,
        CancelledAt = a.CancelledAt,
        CancellationReason = a.CancellationReason,
        ReminderSentAt = a.ReminderSentAt,
        CreatedAt = a.CreatedAt,
        ServiceTypeName = a.ServiceType != null ? a.ServiceType.Name : null,
        ServiceTypeCode = a.ServiceType != null ? a.ServiceType.Code : null,
        ServiceTypeColor = a.ServiceType != null ? a.ServiceType.Color : null
    };

    internal static async Task<(AppointmentAvailabilityDto? Result, object? Error)> ComputeAvailabilityAsync(
        QMgrDbContext context, Guid branchId, Guid serviceTypeId, string? date, CancellationToken cancellationToken)
    {
        var branch = await context.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
        if (branch == null)
            return (null, new { error = "BRANCH_NOT_FOUND" });

        var serviceType = await context.ServiceTypes
            .FirstOrDefaultAsync(st => st.Id == serviceTypeId && st.BranchId == branchId, cancellationToken);
        if (serviceType == null)
            return (null, new { error = "SERVICE_TYPE_NOT_FOUND", message = "No such service type in this branch." });

        var settings = AppointmentScheduling.ReadSettings(branch.Settings);
        var timeZone = AppointmentScheduling.ResolveTimeZone(branch.Timezone);

        DateOnly localDate;
        if (string.IsNullOrWhiteSpace(date))
            localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
        else if (!DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, out localDate))
            return (null, new { error = "INVALID_DATE", message = "date must be yyyy-MM-dd." });

        var duration = AppointmentScheduling.ClampDuration(serviceType.AverageServiceTimeMinutes);
        var step = AppointmentScheduling.ClampDuration(settings.SlotIntervalMinutes ?? duration);

        var hours = AppointmentScheduling.GetOpeningHours(branch.OperatingHours, localDate.DayOfWeek);
        if (hours == null)
        {
            return (new AppointmentAvailabilityDto
            {
                BranchId = branchId,
                ServiceTypeId = serviceTypeId,
                Date = localDate.ToString("yyyy-MM-dd"),
                Timezone = timeZone.Id,
                IsOpen = false,
                SlotDurationMinutes = duration,
                Slots = new List<AppointmentSlotDto>()
            }, null);
        }

        var candidates = AppointmentScheduling.BuildSlots(localDate, hours.Value.Open, hours.Value.Close, step, duration, timeZone);

        // One bounded query for the whole day, then counted in memory. The alternative — a COUNT
        // per slot — is dozens of round trips for a single page render, and expressing the
        // overlap test (ScheduledAt + DurationMinutes) in SQL buys nothing at this row count.
        var windowStart = candidates.Count > 0 ? candidates[0].StartAt.AddHours(-AppointmentScheduling.MaxAppointmentHours) : DateTime.UtcNow;
        var windowEnd = candidates.Count > 0 ? candidates[^1].EndAt : DateTime.UtcNow;

        var existing = await context.Appointments
            .Where(a => a.BranchId == branchId
                        && a.ServiceTypeId == serviceTypeId
                        && a.ScheduledAt >= windowStart
                        && a.ScheduledAt < windowEnd
                        && a.Status != AppointmentStatus.Cancelled
                        && a.Status != AppointmentStatus.NoShow)
            .Select(a => new { a.ScheduledAt, a.DurationMinutes })
            .ToListAsync(cancellationToken);

        var capacity = Math.Max(1, settings.CapacityPerSlot);
        var cutoff = DateTime.UtcNow.AddMinutes(settings.MinimumLeadTimeMinutes);
        var horizon = DateTime.UtcNow.AddDays(Math.Max(1, settings.MaxAdvanceDays));

        var slots = new List<AppointmentSlotDto>(candidates.Count);
        foreach (var slot in candidates)
        {
            var taken = existing.Count(e => e.ScheduledAt < slot.EndAt && e.ScheduledAt.AddMinutes(e.DurationMinutes) > slot.StartAt);
            var remaining = Math.Max(0, capacity - taken);
            var bookable = remaining > 0 && slot.StartAt >= cutoff && slot.StartAt <= horizon;

            slots.Add(new AppointmentSlotDto
            {
                StartAt = slot.StartAt,
                EndAt = slot.EndAt,
                IsAvailable = bookable,
                RemainingCapacity = bookable ? remaining : 0
            });
        }

        return (new AppointmentAvailabilityDto
        {
            BranchId = branchId,
            ServiceTypeId = serviceTypeId,
            Date = localDate.ToString("yyyy-MM-dd"),
            Timezone = timeZone.Id,
            IsOpen = true,
            SlotDurationMinutes = duration,
            Slots = slots
        }, null);
    }

    private Guid? CurrentUserId()
    {
        if (_tenantAccessor.TenantContext?.UserId is Guid id && id != Guid.Empty)
            return id;
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var parsed) ? parsed : null;
    }

    private async Task<Guid> ResolveOrganizationIdAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var tenantContext = _tenantAccessor.TenantContext!;
        return RoleCodes.IsSuperAdmin(tenantContext.UserRole)
            ? await _context.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync(cancellationToken)
            : tenantContext.OrganizationId;
    }

    /// <summary>
    /// Appointment has no global EF tenant query filter (it is branch-scoped, like Token and
    /// Visitor), and its rows carry customer PII, so every action reaching one by branchId must
    /// verify ownership explicitly. SuperAdmin bypass matches every other VerifyBranchOwnership in
    /// this codebase — their JWT carries the Platform org's own org_id, so without it they would
    /// be locked out of every real tenant's branches.
    /// </summary>
    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
        {
            var exists = await _context.Branches.AnyAsync(b => b.Id == branchId);
            return exists ? null : NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' does not exist.",
                Status = StatusCodes.Status404NotFound
            });
        }

        var branchExists = await _context.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);

        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        return null;
    }
}

/// <summary>What check-in returns: the updated booking plus the ticket it just became.</summary>
public record AppointmentCheckInResult
{
    public AppointmentDto Appointment { get; init; } = new();
    public Guid? TokenId { get; init; }
    public string? TokenDisplayNumber { get; init; }
    public bool AlreadyCheckedIn { get; init; }
}

/// <summary>
/// The anonymous half of the booking surface, backing the public <c>/book/{branchId}</c> page.
/// <para>
/// It is a separate controller rather than [AllowAnonymous] actions on
/// <see cref="AppointmentsController"/> because that controller's class-level
/// <c>[RequireModule]</c> filter rejects any request with no resolved tenant context — which is
/// every anonymous request — so an anonymous action there would 401 before it ever ran. The
/// module gate is still enforced here, by <c>ModuleAccessMiddleware</c>: its route template sits
/// under the same <c>api/v1/branches/{branchId}/appointments</c> prefix registered in
/// <c>ModuleRouteMap.ApiRoutes</c>, and the middleware resolves the owning organization from the
/// branchId in the route precisely so anonymous customer-facing endpoints stop serving when a
/// tenant has not paid for the module.
/// </para>
/// <para>
/// Abuse controls, since anyone on the internet can call these: a fixed-window per-branch,
/// per-IP cap held in IDistributedCache (the same cache ModulesController uses for its pending
/// purchases), and a hard length cap on every free-text field. The availability response is
/// free/busy only and never names another customer.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/appointments/public")]
[Produces("application/json")]
[AllowAnonymous]
public class PublicAppointmentsController : ControllerBase
{
    private const int BookingsPerWindow = 5;
    private const int LookupsPerWindow = 120;
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(15);

    private readonly QMgrDbContext _context;
    private readonly IDistributedCache _cache;
    private readonly ILogger<PublicAppointmentsController> _logger;

    public PublicAppointmentsController(QMgrDbContext context, IDistributedCache cache, ILogger<PublicAppointmentsController> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>The services a member of the public may book. Narrow shape — no codes, no prefixes.</summary>
    [HttpGet("service-types")]
    [ProducesResponseType(typeof(List<BookableServiceTypeDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBookableServiceTypes(Guid branchId, CancellationToken cancellationToken)
    {
        if (!await AllowAsync("lookup", branchId, LookupsPerWindow, cancellationToken))
            return TooMany();

        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId && b.IsActive, cancellationToken);
        if (branch == null) return NotFound(new { error = "BRANCH_NOT_FOUND" });

        if (!AppointmentScheduling.ReadSettings(branch.Settings).PublicBookingEnabled)
            return NotFound(new { error = "BOOKING_DISABLED", message = "Online booking is not available for this location." });

        var services = await _context.ServiceTypes
            .Where(st => st.BranchId == branchId && st.IsActive)
            .OrderBy(st => st.Priority).ThenBy(st => st.Name)
            .Select(st => new BookableServiceTypeDto
            {
                Id = st.Id,
                Name = st.Name,
                Description = st.Description,
                DurationMinutes = st.AverageServiceTimeMinutes,
                Color = st.Color
            })
            .ToListAsync(cancellationToken);

        return Ok(services);
    }

    /// <summary>Free/busy for one service on one day. Identical computation to the staff endpoint.</summary>
    [HttpGet("availability")]
    [ProducesResponseType(typeof(AppointmentAvailabilityDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailability(
        Guid branchId, [FromQuery] Guid serviceTypeId, [FromQuery] string? date = null, CancellationToken cancellationToken = default)
    {
        if (!await AllowAsync("lookup", branchId, LookupsPerWindow, cancellationToken))
            return TooMany();

        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId && b.IsActive, cancellationToken);
        if (branch == null) return NotFound(new { error = "BRANCH_NOT_FOUND" });
        if (!AppointmentScheduling.ReadSettings(branch.Settings).PublicBookingEnabled)
            return NotFound(new { error = "BOOKING_DISABLED" });

        var (result, error) = await AppointmentsController.ComputeAvailabilityAsync(_context, branchId, serviceTypeId, date, cancellationToken);
        if (error != null) return BadRequest(error);
        return Ok(result);
    }

    /// <summary>Takes an anonymous booking and returns only the customer's own confirmation.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(PublicAppointmentConfirmationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Book(Guid branchId, [FromBody] PublicBookAppointmentRequest request, CancellationToken cancellationToken)
    {
        if (!await AllowAsync("book", branchId, BookingsPerWindow, cancellationToken))
            return TooMany();

        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId && b.IsActive, cancellationToken);
        if (branch == null) return NotFound(new { error = "BRANCH_NOT_FOUND" });

        var settings = AppointmentScheduling.ReadSettings(branch.Settings);
        if (!settings.PublicBookingEnabled)
            return NotFound(new { error = "BOOKING_DISABLED", message = "Online booking is not available for this location." });

        if (string.IsNullOrWhiteSpace(request.CustomerName) || string.IsNullOrWhiteSpace(request.CustomerPhone))
            return BadRequest(new { error = "MISSING_DETAILS", message = "Your name and a phone number are required." });

        var outcome = await AppointmentScheduling.BookAsync(
            _context,
            branchId,
            branch.OrganizationId,
            new AppointmentScheduling.BookingInput
            {
                ServiceTypeId = request.ServiceTypeId,
                // Length caps on every free-text field — this is an unauthenticated form.
                CustomerName = AppointmentScheduling.Truncate(request.CustomerName, 100)!,
                CustomerPhone = AppointmentScheduling.Truncate(request.CustomerPhone, 30),
                CustomerEmail = AppointmentScheduling.Truncate(request.CustomerEmail, 200),
                Notes = AppointmentScheduling.Truncate(request.Notes, 500),
                ScheduledAt = request.ScheduledAt,
                ExternalSystem = AppointmentScheduling.PublicBookingSystem,
                CreatedByUserId = null,
                // The public form must respect the branch's opening hours and lead time — unlike
                // a receptionist, an anonymous caller has no standing to slot themselves in.
                EnforceOpeningHours = true,
                EnforceLeadTime = true
            },
            cancellationToken);

        if (outcome.Error != null)
            return outcome.Conflict ? Conflict(outcome.Error) : BadRequest(outcome.Error);

        var appointment = outcome.Appointment!;
        var serviceTypeName = await _context.ServiceTypes
            .Where(st => st.Id == appointment.ServiceTypeId)
            .Select(st => st.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "Appointment";

        _logger.LogInformation("Public booking {Reference} taken for branch {BranchId} at {ScheduledAt:o}",
            appointment.ReferenceCode, branchId, appointment.ScheduledAt);

        return StatusCode(StatusCodes.Status201Created, new PublicAppointmentConfirmationDto
        {
            ReferenceCode = appointment.ReferenceCode,
            ScheduledAt = appointment.ScheduledAt,
            DurationMinutes = appointment.DurationMinutes,
            ServiceTypeName = serviceTypeName,
            BranchName = branch.Name,
            Timezone = AppointmentScheduling.ResolveTimeZone(branch.Timezone).Id
        });
    }

    private IActionResult TooMany() => StatusCode(StatusCodes.Status429TooManyRequests, new
    {
        error = "RATE_LIMITED",
        message = "Too many requests from this device. Please wait a few minutes and try again."
    });

    /// <summary>
    /// Fixed-window counter per bucket, branch and client IP, held in IDistributedCache. Read/
    /// increment/write is not atomic, so two requests landing in the same millisecond can share a
    /// slot in the count — acceptable for an abuse brake (it bounds a flood by orders of
    /// magnitude, which is the point) and the same level of rigour ModulesController's cache use
    /// applies. A cache outage fails open rather than locking legitimate customers out of booking.
    /// </summary>
    private async Task<bool> AllowAsync(string bucket, Guid branchId, int limit, CancellationToken cancellationToken)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)RateWindow.TotalSeconds;
        var key = $"appt-rl:{bucket}:{branchId}:{ip}:{window}";

        try
        {
            var current = await _cache.GetStringAsync(key, cancellationToken);
            var count = int.TryParse(current, out var parsed) ? parsed : 0;
            if (count >= limit)
            {
                _logger.LogWarning("Public appointment {Bucket} rate limit hit for branch {BranchId} from {RemoteIp}", bucket, branchId, ip);
                return false;
            }

            await _cache.SetStringAsync(key, (count + 1).ToString(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = RateWindow }, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rate-limit cache unavailable; allowing the request");
            return true;
        }
    }
}

/// <summary>
/// Slot maths, settings parsing and the booking write path — shared verbatim by the staff and
/// public controllers so the two can never disagree about what is bookable. Hand-rolled rather
/// than pulled from a scheduling library, per the project's standing no-new-server-dependency
/// rule.
/// </summary>
internal static class AppointmentScheduling
{
    internal const string AppointmentExternalSystem = "appointment";
    internal const string PublicBookingSystem = "public-booking";
    internal const string SettingsKey = "Appointments";

    /// <summary>Longest appointment we will look back for when testing slot overlap.</summary>
    internal const int MaxAppointmentHours = 8;

    private const int MinDurationMinutes = 5;
    private const int MaxDurationMinutes = 8 * 60;

    // Ambiguity-free alphabet: no I/O/0/1, so a code read down the phone survives the trip.
    private const string ReferenceAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Default working day, used only when Branch.OperatingHours is absent or unparseable.
    /// TenantProvisioningService already writes real hours for every branch it creates
    /// (Mon-Fri 08:00-17:00, Sat 09:00-13:00), so this is a safety net for hand-created or
    /// legacy rows rather than the normal path.
    /// </summary>
    private static readonly Dictionary<DayOfWeek, (TimeSpan Open, TimeSpan Close)> DefaultHours = new()
    {
        [DayOfWeek.Monday] = (new TimeSpan(8, 0, 0), new TimeSpan(17, 0, 0)),
        [DayOfWeek.Tuesday] = (new TimeSpan(8, 0, 0), new TimeSpan(17, 0, 0)),
        [DayOfWeek.Wednesday] = (new TimeSpan(8, 0, 0), new TimeSpan(17, 0, 0)),
        [DayOfWeek.Thursday] = (new TimeSpan(8, 0, 0), new TimeSpan(17, 0, 0)),
        [DayOfWeek.Friday] = (new TimeSpan(8, 0, 0), new TimeSpan(17, 0, 0))
    };

    /// <summary>Npgsql maps these columns to timestamptz, which refuses a non-UTC Kind.</summary>
    internal static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        // Unspecified means the wire carried no offset. Treating it as UTC is the only choice
        // that is stable regardless of which machine deserialized it.
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    internal static int ClampDuration(int minutes) => Math.Clamp(minutes <= 0 ? 15 : minutes, MinDurationMinutes, MaxDurationMinutes);

    internal static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    /// <summary>
    /// Reads the scheduling block out of the existing Branch.Settings JSON blob — the same
    /// read-a-key-out-of-the-merged-object pattern VisitorsController uses for "VisitorConsent"
    /// and "VisitingDay". A missing or malformed blob yields defaults rather than an error.
    /// </summary>
    internal static AppointmentSettingsDto ReadSettings(string? branchSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(branchSettingsJson)) return new AppointmentSettingsDto();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(branchSettingsJson, JsonOptions);
            if (root != null && root.TryGetValue(SettingsKey, out var element))
                return JsonSerializer.Deserialize<AppointmentSettingsDto>(element.GetRawText(), JsonOptions) ?? new AppointmentSettingsDto();
        }
        catch (JsonException) { /* malformed settings blob — treat as not configured */ }
        return new AppointmentSettingsDto();
    }

    /// <summary>Branch.Timezone is an IANA id ("Africa/Kampala"); an unknown one falls back to UTC.</summary>
    internal static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Opening window for one weekday, from the existing Branch.OperatingHours JSON
    /// (<c>{"monday":{"open":"08:00","close":"17:00"}, ...}</c> — the exact shape
    /// TenantProvisioningService writes). Null means closed that day.
    /// </summary>
    internal static (TimeSpan Open, TimeSpan Close)? GetOpeningHours(string? operatingHoursJson, DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(operatingHoursJson))
            return DefaultHours.TryGetValue(day, out var fallback) ? fallback : null;

        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(operatingHoursJson, JsonOptions);
            if (root == null)
                return DefaultHours.TryGetValue(day, out var fallback) ? fallback : null;

            var key = root.Keys.FirstOrDefault(k => string.Equals(k, day.ToString(), StringComparison.OrdinalIgnoreCase));
            // The branch has hours configured, and this weekday is not among them: genuinely closed.
            if (key == null) return null;

            var element = root[key];
            if (element.ValueKind != JsonValueKind.Object) return null;

            var open = ReadTime(element, "open");
            var close = ReadTime(element, "close");
            if (open == null || close == null || close <= open) return null;

            return (open.Value, close.Value);
        }
        catch (JsonException)
        {
            return DefaultHours.TryGetValue(day, out var fallback) ? fallback : null;
        }
    }

    private static TimeSpan? ReadTime(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind != JsonValueKind.String) return null;
            return TimeSpan.TryParse(property.Value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }
        return null;
    }

    /// <summary>
    /// Slot grid for one local day: starts every <paramref name="stepMinutes"/> from the opening
    /// time, keeping only those whose full <paramref name="durationMinutes"/> still fits before
    /// closing. Local wall-clock times are converted to UTC through the branch's zone; a start
    /// that does not exist locally (the spring-forward gap) is skipped rather than shifted.
    /// </summary>
    internal static List<(DateTime StartAt, DateTime EndAt)> BuildSlots(
        DateOnly localDate, TimeSpan open, TimeSpan close, int stepMinutes, int durationMinutes, TimeZoneInfo timeZone)
    {
        var slots = new List<(DateTime, DateTime)>();
        var step = Math.Max(MinDurationMinutes, stepMinutes);
        var dayStart = localDate.ToDateTime(TimeOnly.MinValue);

        for (var offset = open; offset + TimeSpan.FromMinutes(durationMinutes) <= close; offset += TimeSpan.FromMinutes(step))
        {
            var localStart = DateTime.SpecifyKind(dayStart + offset, DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(localStart)) continue;

            var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
            slots.Add((utcStart, utcStart.AddMinutes(durationMinutes)));

            // Guard against a pathological step that would loop forever on TimeSpan overflow.
            if (slots.Count > 500) break;
        }

        return slots;
    }

    /// <summary>True when the slot already holds <paramref name="capacity"/> live appointments.</summary>
    internal static async Task<bool> SlotIsFullAsync(
        QMgrDbContext context, Guid branchId, Guid serviceTypeId, DateTime startUtc, int durationMinutes,
        int capacity, Guid? excludeAppointmentId, CancellationToken cancellationToken)
    {
        var endUtc = startUtc.AddMinutes(durationMinutes);
        var lookBack = startUtc.AddHours(-MaxAppointmentHours);

        var overlapping = await context.Appointments
            .Where(a => a.BranchId == branchId
                        && a.ServiceTypeId == serviceTypeId
                        && a.ScheduledAt >= lookBack
                        && a.ScheduledAt < endUtc
                        && a.Status != AppointmentStatus.Cancelled
                        && a.Status != AppointmentStatus.NoShow
                        && (excludeAppointmentId == null || a.Id != excludeAppointmentId))
            .Select(a => new { a.ScheduledAt, a.DurationMinutes })
            .ToListAsync(cancellationToken);

        var taken = overlapping.Count(a => a.ScheduledAt < endUtc && a.ScheduledAt.AddMinutes(a.DurationMinutes) > startUtc);
        return taken >= Math.Max(1, capacity);
    }

    internal record BookingInput
    {
        public Guid? ServiceTypeId { get; init; }
        public string? ServiceTypeCode { get; init; }
        public string CustomerName { get; init; } = string.Empty;
        public string? CustomerPhone { get; init; }
        public string? CustomerEmail { get; init; }
        public DateTime ScheduledAt { get; init; }
        public int? DurationMinutes { get; init; }
        public string? Notes { get; init; }
        public string? ExternalReference { get; init; }
        public string? ExternalSystem { get; init; }
        public Guid? CreatedByUserId { get; init; }
        public bool EnforceOpeningHours { get; init; }
        public bool EnforceLeadTime { get; init; }
    }

    internal record BookingOutcome(Appointment? Appointment, object? Error, bool Conflict);

    /// <summary>
    /// The single booking write path, used by staff create and the public form alike.
    /// <para>
    /// CONCURRENCY: capacity is checked and the row inserted inside one transaction holding
    /// <c>pg_advisory_xact_lock</c> keyed on branch+service-type+slot — the same pattern
    /// TokenRepository.GetNextTokenNumberAsync and VisitorsController.GenerateBadgeCodeAsync use.
    /// Without it, two customers tapping the last remaining 10:00 slot at the same instant would
    /// both read "1 free" and both insert. The lock is per slot, so unrelated bookings never wait
    /// on each other — which is also why a unique database index on (BranchId, ScheduledAt) would
    /// have been the wrong tool: it would reject legitimate parallel bookings across service types
    /// and counters as well.
    /// </para>
    /// </summary>
    internal static async Task<BookingOutcome> BookAsync(
        QMgrDbContext context, Guid branchId, Guid organizationId, BookingInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.CustomerName))
            return new BookingOutcome(null, new { error = "MISSING_NAME", message = "A customer name is required." }, false);

        var branch = await context.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
        if (branch == null)
            return new BookingOutcome(null, new { error = "BRANCH_NOT_FOUND" }, false);

        ServiceType? serviceType = null;
        if (input.ServiceTypeId is Guid id && id != Guid.Empty)
            serviceType = await context.ServiceTypes.FirstOrDefaultAsync(st => st.Id == id && st.BranchId == branchId, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(input.ServiceTypeCode))
        {
            var code = input.ServiceTypeCode.Trim();
            serviceType = await context.ServiceTypes.FirstOrDefaultAsync(st => st.Code == code && st.BranchId == branchId, cancellationToken);
        }
        else
            return new BookingOutcome(null, new { error = "MISSING_SERVICE_TYPE", message = "serviceTypeId (or serviceTypeCode) is required." }, false);

        if (serviceType == null)
            return new BookingOutcome(null, new { error = "SERVICE_TYPE_NOT_FOUND", message = "No such service type in this branch." }, false);

        var scheduledAt = ToUtc(input.ScheduledAt);
        if (scheduledAt == default)
            return new BookingOutcome(null, new { error = "INVALID_TIME", message = "A scheduled time is required." }, false);

        var settings = ReadSettings(branch.Settings);
        var duration = ClampDuration(input.DurationMinutes ?? serviceType.AverageServiceTimeMinutes);
        var now = DateTime.UtcNow;

        if (scheduledAt > now.AddDays(Math.Max(1, settings.MaxAdvanceDays)))
            return new BookingOutcome(null, new { error = "TOO_FAR_AHEAD", message = $"Bookings are only accepted up to {settings.MaxAdvanceDays} days ahead." }, false);

        if (input.EnforceLeadTime && scheduledAt < now.AddMinutes(settings.MinimumLeadTimeMinutes))
            return new BookingOutcome(null, new { error = "TOO_SOON", message = "That time has passed or is too soon. Please pick a later slot." }, false);

        if (!input.EnforceLeadTime && scheduledAt < now.AddDays(-1))
            return new BookingOutcome(null, new { error = "IN_THE_PAST", message = "An appointment cannot be booked more than a day in the past." }, false);

        if (input.EnforceOpeningHours)
        {
            var timeZone = ResolveTimeZone(branch.Timezone);
            var local = TimeZoneInfo.ConvertTimeFromUtc(scheduledAt, timeZone);
            var hours = GetOpeningHours(branch.OperatingHours, local.DayOfWeek);
            if (hours == null || local.TimeOfDay < hours.Value.Open || local.TimeOfDay + TimeSpan.FromMinutes(duration) > hours.Value.Close)
                return new BookingOutcome(null, new { error = "OUTSIDE_OPENING_HOURS", message = "That time is outside this location's opening hours." }, false);
        }

        Appointment? created = null;
        object? error = null;
        var conflict = false;

        var lockKey = $"appointment-slot:{branchId}:{serviceType.Id}:{scheduledAt:yyyyMMddHHmm}";
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // Reset per attempt — the execution strategy may replay this delegate on a transient failure.
            created = null;
            error = null;
            conflict = false;

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)", cancellationToken);

            if (await SlotIsFullAsync(context, branchId, serviceType.Id, scheduledAt, duration, settings.CapacityPerSlot, null, cancellationToken))
            {
                error = new { error = "SLOT_FULL", message = "That time has just been taken. Please choose another slot." };
                conflict = true;
                await transaction.RollbackAsync(cancellationToken);
                return;
            }

            var appointment = new Appointment
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                ServiceTypeId = serviceType.Id,
                ReferenceCode = await GenerateReferenceCodeAsync(context, branchId, cancellationToken),
                CustomerName = Truncate(input.CustomerName, 255)!,
                CustomerPhone = Truncate(input.CustomerPhone, 50),
                CustomerEmail = Truncate(input.CustomerEmail, 255),
                ScheduledAt = DateTime.SpecifyKind(scheduledAt, DateTimeKind.Utc),
                DurationMinutes = duration,
                Status = AppointmentStatus.Booked,
                Notes = Truncate(input.Notes, 1000),
                ExternalReference = Truncate(input.ExternalReference, 100),
                ExternalSystem = Truncate(input.ExternalSystem, 100),
                CreatedByUserId = input.CreatedByUserId,
                CreatedBy = input.CreatedByUserId,
                CreatedAt = DateTime.UtcNow
            };

            context.Appointments.Add(appointment);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            created = appointment;
        });

        return new BookingOutcome(created, error, conflict);
    }

    /// <summary>
    /// Short, quotable, unguessable-enough reference. Retried on the (vanishingly unlikely)
    /// collision the unique (BranchId, ReferenceCode) index would otherwise reject.
    /// </summary>
    private static async Task<string> GenerateReferenceCodeAsync(QMgrDbContext context, Guid branchId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var code = "APT-" + RandomCode(6);
            var exists = await context.Appointments.AnyAsync(a => a.BranchId == branchId && a.ReferenceCode == code, cancellationToken);
            if (!exists) return code;
        }
        // Fall back to something that cannot collide rather than looping forever.
        return "APT-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
    }

    private static string RandomCode(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = ReferenceAlphabet[RandomNumberGenerator.GetInt32(ReferenceAlphabet.Length)];
        return new string(chars);
    }
}
