using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using QMgr.Application.Tenant;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services.Storage;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// EXAM-SUPERVISION SERIES (2026-09-23, plan TIMETABLE_OWNERSHIP §1 and Phase 3).
///
/// <para>"Three timetables in a term" is one teaching timetable plus exam supervision, and exam supervision is not a
/// timetable: it is dated sittings with invigilators, rooms and times, and publishing it as a second
/// <c>Timetable</c> would archive the live one and stop every lesson in the school. What it lacked was a NAME to group
/// the forty slots under and an OWNER for the group. A series is the Session duties sharing a <c>SeriesId</c>; its
/// name and managers are carried on every row. Each slot is an ordinary Session duty, so the register, reminders, My
/// School Day and scoring (the seeded Exam Supervision parameter is a Duty) all already work.</para>
///
/// <para><b>Every slot goes through <see cref="ApplyAsync"/></b> — the same rules a hand-made duty obeys (a
/// parameter of the right kind, active people, at most 14 days, ends after it starts) — so a series cannot become a
/// way round them.</para>
///
/// <para>Who: <see cref="DutySeriesAccess"/>. Reading a series is for its managers, its invigilators and a holder of
/// staff.duties.manage; anybody else gets 404, never 403 — the same rule as a duty, since a 403 confirms it exists.</para>
/// </summary>
public partial class StaffDutiesController
{
    /// <summary>A school's end-of-year exams are a few hundred sittings at most; a batch above this is a mistake.</summary>
    private const int MaxSeriesSlots = 300;

    private IActionResult NotFoundSeries() => new NotFoundObjectResult(new ProblemDetails { Title = "Exam series not found", Status = StatusCodes.Status404NotFound });

    // ---- Read ---------------------------------------------------------------------------------------

    [HttpGet("branches/{branchId:guid}/staff/duty-series")]
    [ProducesResponseType(typeof(List<DutySeriesDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDutySeries(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.StaffDutiesManage);

        var slots = await _context.StaffDuties.AsNoTracking()
            .Where(d => d.BranchId == branchId && d.OrganizationId == organizationId && d.IsActive
                        && d.SeriesId != null && d.SeriesName != null && d.Kind == DutyKind.Session)
            .ToListAsync();

        var result = new List<DutySeriesDto>();
        foreach (var group in slots.GroupBy(d => d.SeriesId!.Value))
        {
            var list = group.ToList();
            if (!MayRead(list, me, holds)) continue;
            result.Add(await SummaryAsync(list, me, holds));
        }
        return Ok(result.OrderByDescending(s => s.FirstStartsAt).ToList());
    }

    [HttpGet("branches/{branchId:guid}/staff/duty-series/{seriesId:guid}")]
    [ProducesResponseType(typeof(DutySeriesDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDutySeriesDetail(Guid branchId, Guid seriesId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.StaffDutiesManage);

        var slots = await ActiveSlotsAsync(branchId, organizationId, seriesId, tracked: false);
        if (slots.Count == 0 || !MayRead(slots, me, holds)) return NotFoundSeries();
        return Ok(await DetailAsync(slots, organizationId, branchId, me, holds));
    }

    // ---- Create -------------------------------------------------------------------------------------

    [HttpPost("branches/{branchId:guid}/staff/duty-series")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    [ProducesResponseType(typeof(DutySeriesWriteResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateDutySeries(Guid branchId, [FromBody] CreateDutySeriesRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Problem400("Give the series a name", "For example \"End of Term 3 exams 2026\".");
        if (name.Length > 200) return Problem400("The name is too long", "200 characters at most.");
        if (request.Slots.Count == 0) return Problem400("Add at least one sitting", "A series is its sittings; it cannot exist empty.");
        if (request.Slots.Count > MaxSeriesSlots) return Problem400($"A batch is capped at {MaxSeriesSlots} sittings");

        var managers = request.ManagerUserIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (await RefuseUnknownPeopleAsync(organizationId, managers) is { } unknown) return unknown;

        var (parameterId, parameterError) = await ResolveSeriesParameterAsync(organizationId, request.ParameterId);
        if (parameterError != null) return parameterError;

        var seriesId = Guid.NewGuid();
        var built = new List<StaffDuty>();
        for (var i = 0; i < request.Slots.Count; i++)
        {
            var (slot, error) = await BuildSlotAsync(request.Slots[i], i, name, parameterId, managers, seriesId, organizationId, branchId, me);
            if (error != null) return error;
            built.Add(slot!);
        }

        _context.StaffDuties.AddRange(built);
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.DutySeriesSaved, nameof(StaffDuty), seriesId, null,
            $"Exam series \"{name}\" created with {built.Count} sitting(s) and {managers.Length} manager(s)",
            new { Sittings = built.Count, Managers = managers.Length }, branchId, organizationId);

        var warnings = await ClashWarningsAsync(built, organizationId, branchId);
        await NotifyInvigilatorsAsync(built.Select(s => (s, (IEnumerable<Guid>)(s.ExpectedUserIds ?? Array.Empty<Guid>()))).ToList(), name, branchId);

        var detail = await DetailAsync(built, organizationId, branchId, me, holds: true);
        return CreatedAtAction(nameof(GetDutySeriesDetail), new { branchId, seriesId }, new DutySeriesWriteResultDto { Detail = detail, Warnings = warnings });
    }

    // ---- Rename, appoint ------------------------------------------------------------------------------

    [HttpPut("branches/{branchId:guid}/staff/duty-series/{seriesId:guid}")]
    [ProducesResponseType(typeof(DutySeriesDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateDutySeries(Guid branchId, Guid seriesId, [FromBody] UpdateDutySeriesRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.StaffDutiesManage);

        // Every row of the series, cancelled ones included, so a later reinstatement reads the current name.
        var rows = await _context.StaffDuties
            .Where(d => d.BranchId == branchId && d.OrganizationId == organizationId && d.SeriesId == seriesId && d.SeriesName != null)
            .ToListAsync();
        var live = rows.Where(r => r.IsActive).ToList();
        if (live.Count == 0 || !MayRead(live, me, holds)) return NotFoundSeries();
        if (!DutySeriesAccess.MayWrite(live[0], me, holds))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "This series is not yours to change",
                Detail = "Its managers and administrators can change it.",
                Status = StatusCodes.Status403Forbidden
            });

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Problem400("Give the series a name");
        if (name.Length > 200) return Problem400("The name is too long", "200 characters at most.");

        var before = live[0].SeriesManagerUserIds;
        var after = before;
        if (request.ManagerUserIds is { } requested)
        {
            var wanted = requested.Where(x => x != Guid.Empty).Distinct().ToArray();
            if (!wanted.OrderBy(x => x).SequenceEqual(before.OrderBy(x => x)))
            {
                // Appointing is the permission holder's act alone, or a manager could hand the series to anybody.
                if (!holds)
                    return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                    {
                        Title = "Only an administrator appoints who runs a series",
                        Detail = "A manager can rename the series and change its sittings, but not who manages it.",
                        Status = StatusCodes.Status403Forbidden
                    });
                if (await RefuseUnknownPeopleAsync(organizationId, wanted) is { } unknown) return unknown;
                after = wanted;
            }
        }

        var oldName = live[0].SeriesName;
        foreach (var row in rows)
        {
            row.SeriesName = name;
            row.SeriesManagerUserIds = after;
            // Managers take the registers, and every reader of recorders already knows that — so they are kept in.
            row.RecorderUserIds = DutySeriesAccess.Recorders(row.RecorderUserIds, before, after);
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = me;
        }
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.DutySeriesSaved, nameof(StaffDuty), seriesId, null,
            oldName == name ? $"Exam series \"{name}\" managers changed" : $"Exam series renamed from \"{oldName}\" to \"{name}\"",
            new { Before = before.Length, After = after.Length }, branchId, organizationId);

        return Ok(await DetailAsync(live, organizationId, branchId, me, holds));
    }

    // ---- Sittings -------------------------------------------------------------------------------------

    [HttpPost("branches/{branchId:guid}/staff/duty-series/{seriesId:guid}/slots")]
    [ProducesResponseType(typeof(DutySeriesWriteResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddSeriesSlots(Guid branchId, Guid seriesId, [FromBody] List<DutySeriesSlotRequest> request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.StaffDutiesManage);

        var existing = await ActiveSlotsAsync(branchId, organizationId, seriesId, tracked: false);
        if (existing.Count == 0 || !MayRead(existing, me, holds)) return NotFoundSeries();
        if (!DutySeriesAccess.MayWrite(existing[0], me, holds)) return NotFoundSeries();
        if (request.Count == 0) return Problem400("Add at least one sitting");
        if (existing.Count + request.Count > MaxSeriesSlots) return Problem400($"A series is capped at {MaxSeriesSlots} sittings");

        var first = existing[0];
        var built = new List<StaffDuty>();
        for (var i = 0; i < request.Count; i++)
        {
            var (slot, error) = await BuildSlotAsync(request[i], i, first.SeriesName!, first.ParameterId, first.SeriesManagerUserIds, seriesId, organizationId, branchId, me);
            if (error != null) return error;
            built.Add(slot!);
        }
        _context.StaffDuties.AddRange(built);
        await _context.SaveChangesAsync();

        foreach (var slot in built)
            await _activity.RecordAsync(ActivityActions.DutyCreated, nameof(StaffDuty), slot.Id, null,
                string.Create(CultureInfo.InvariantCulture, $"Sitting \"{slot.Title}\" added to exam series \"{slot.SeriesName}\" for {slot.StartsAt:dd MMM yyyy HH:mm} UTC"),
                new { slot.StartsAt, slot.EndsAt, Invigilators = slot.ExpectedUserIds?.Length }, branchId, organizationId);

        var warnings = await ClashWarningsAsync(built, organizationId, branchId);
        await NotifyInvigilatorsAsync(built.Select(s => (s, (IEnumerable<Guid>)(s.ExpectedUserIds ?? Array.Empty<Guid>()))).ToList(), first.SeriesName!, branchId);

        var all = existing.Concat(built).ToList();
        return Ok(new DutySeriesWriteResultDto { Detail = await DetailAsync(all, organizationId, branchId, me, holds), Warnings = warnings });
    }

    [HttpPut("branches/{branchId:guid}/staff/duty-series/{seriesId:guid}/slots/{dutyId:guid}")]
    [ProducesResponseType(typeof(DutySeriesWriteResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSeriesSlot(Guid branchId, Guid seriesId, Guid dutyId, [FromBody] DutySeriesSlotRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.StaffDutiesManage);

        var duty = await _context.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId && d.SeriesId == seriesId && d.BranchId == branchId
                                                                       && d.OrganizationId == organizationId && d.IsActive && d.SeriesName != null);
        if (duty == null || !DutySeriesAccess.MayWrite(duty, me, holds)) return NotFoundSeries();
        if (duty.RegisterClosedAt.HasValue)
            return Problem400("This sitting's register is closed", "A sitting cannot be changed once its register has been taken.");
        if (request.InvigilatorUserIds.Count == 0) return Problem400("Name at least one invigilator");

        var invigilatorsBefore = duty.ExpectedUserIds?.ToArray() ?? Array.Empty<Guid>();
        var error = await ApplyAsync(duty, SaveRequestFor(request, duty.SeriesName!, duty.ParameterId, duty.RecorderUserIds), organizationId);
        if (error != null) return error;
        duty.UpdatedAt = DateTime.UtcNow;
        duty.UpdatedBy = me;
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.DutyUpdated, nameof(StaffDuty), duty.Id, null,
            $"Sitting \"{duty.Title}\" in exam series \"{duty.SeriesName}\" updated", null, branchId, organizationId);

        var warnings = await ClashWarningsAsync(new List<StaffDuty> { duty }, organizationId, branchId);
        var added = (duty.ExpectedUserIds ?? Array.Empty<Guid>()).Except(invigilatorsBefore);
        await NotifyInvigilatorsAsync(new List<(StaffDuty, IEnumerable<Guid>)> { (duty, added) }, duty.SeriesName!, branchId);

        var all = await ActiveSlotsAsync(branchId, organizationId, seriesId, tracked: false);
        return Ok(new DutySeriesWriteResultDto { Detail = await DetailAsync(all, organizationId, branchId, me, holds), Warnings = warnings });
    }

    [HttpDelete("branches/{branchId:guid}/staff/duty-series/{seriesId:guid}/slots/{dutyId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelSeriesSlot(Guid branchId, Guid seriesId, Guid dutyId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var holds = await HasPermissionAsync(Permissions.StaffDutiesManage);

        var duty = await _context.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId && d.SeriesId == seriesId && d.BranchId == branchId
                                                                       && d.OrganizationId == organizationId && d.IsActive && d.SeriesName != null);
        if (duty == null || !DutySeriesAccess.MayWrite(duty, me, holds)) return NotFoundSeries();
        if (duty.RegisterClosedAt.HasValue)
            return Problem400("This sitting's register is closed", "A sitting whose register has been taken cannot be cancelled; the records it produced stand.");

        duty.IsActive = false;
        duty.UpdatedAt = DateTime.UtcNow;
        duty.UpdatedBy = me;
        await _context.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.DutyCancelled, nameof(StaffDuty), duty.Id, null,
            string.Create(CultureInfo.InvariantCulture, $"Sitting \"{duty.Title}\" on {duty.StartsAt:dd MMM yyyy} cancelled in exam series \"{duty.SeriesName}\""),
            null, branchId, organizationId);
        return NoContent();
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private async Task<List<StaffDuty>> ActiveSlotsAsync(Guid branchId, Guid organizationId, Guid seriesId, bool tracked)
    {
        var q = _context.StaffDuties.Where(d => d.BranchId == branchId && d.OrganizationId == organizationId && d.SeriesId == seriesId
                                                && d.IsActive && d.SeriesName != null && d.Kind == DutyKind.Session);
        if (!tracked) q = q.AsNoTracking();
        return await q.OrderBy(d => d.StartsAt).ToListAsync();
    }

    /// <summary>A manager, an invigilator of any sitting, or a holder of the permission.</summary>
    private static bool MayRead(IReadOnlyList<StaffDuty> slots, Guid me, bool holds)
        => holds || slots.Any(s => DutySeriesAccess.IsManager(s, me) || (s.ExpectedUserIds?.Contains(me) ?? false) || s.RecorderUserIds.Contains(me));

    private static SaveStaffDutyRequest SaveRequestFor(DutySeriesSlotRequest slot, string seriesName, Guid parameterId, IEnumerable<Guid> recorders) => new()
    {
        Kind = DutyKind.Session,
        ParameterId = parameterId,
        Title = string.IsNullOrWhiteSpace(slot.Title) ? seriesName : slot.Title.Trim(),
        Description = slot.Description,
        Location = slot.Location,
        StartsAt = slot.StartsAt,
        EndsAt = slot.EndsAt,
        ExpectedUserIds = slot.InvigilatorUserIds.Where(x => x != Guid.Empty).Distinct().ToList(),
        RecorderUserIds = recorders.Distinct().ToList()
    };

    /// <summary>One sitting, through <see cref="ApplyAsync"/> — the rules every duty obeys — with the series stamped on.</summary>
    private async Task<(StaffDuty? Slot, IActionResult? Error)> BuildSlotAsync(DutySeriesSlotRequest request, int index, string seriesName,
        Guid parameterId, Guid[] managers, Guid seriesId, Guid organizationId, Guid branchId, Guid me)
    {
        // An invigilation with nobody named would read as "everyone in the branch" to ApplyAsync — never right here.
        if (request.InvigilatorUserIds.Count(x => x != Guid.Empty) == 0)
            return (null, Problem400($"Sitting {index + 1}: name at least one invigilator"));

        var duty = new StaffDuty
        {
            OrganizationId = organizationId, BranchId = branchId, CreatedByUserId = me, CreatedBy = me,
            SeriesId = seriesId, SeriesName = seriesName, SeriesManagerUserIds = managers
        };
        var error = await ApplyAsync(duty, SaveRequestFor(request, seriesName, parameterId, managers), organizationId);
        if (error is ObjectResult { Value: ProblemDetails problem })
        {
            problem.Title = $"Sitting {index + 1}: {problem.Title}";
            return (null, error);
        }
        return error != null ? (null, error) : (duty, null);
    }

    /// <summary>The parameter a series scores on: the one asked for (a Duty or Attendance parameter), else the
    /// organization's seeded "Exam Supervision", else its first active Duty parameter.</summary>
    private async Task<(Guid, IActionResult?)> ResolveSeriesParameterAsync(Guid organizationId, Guid? requested)
    {
        var parameters = await _context.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.IsActive && (p.Kind == ParameterKind.Duty || p.Kind == ParameterKind.Attendance))
            .OrderBy(p => p.SortOrder).Select(p => new { p.Id, p.Name, p.Kind }).ToListAsync();
        if (requested is { } id)
            return parameters.Any(p => p.Id == id) ? (id, null)
                : (Guid.Empty, Problem400("Choose an active Duty or Attendance parameter for the series"));
        var chosen = parameters.FirstOrDefault(p => string.Equals(p.Name, "Exam Supervision", StringComparison.OrdinalIgnoreCase))
                     ?? parameters.FirstOrDefault(p => p.Kind == ParameterKind.Duty);
        return chosen == null
            ? (Guid.Empty, Problem400("There is no Duty parameter to score invigilation on", "Add one — for example \"Exam Supervision\" — under Staff Performance setup."))
            : (chosen.Id, null);
    }

    private async Task<IActionResult?> RefuseUnknownPeopleAsync(Guid organizationId, IReadOnlyCollection<Guid> userIds)
    {
        if (userIds.Count == 0) return null;
        var active = await _context.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => userIds.Contains(u.Id) && u.OrganizationId == organizationId && u.IsActive).Select(u => u.Id).ToListAsync();
        var missing = userIds.Except(active).Count();
        return missing == 0 ? null
            : Problem400("Some people are not active members of this organization", $"{missing} of the selected user(s) could not be found or are inactive.");
    }

    private async Task<DutySeriesDto> SummaryAsync(IReadOnlyList<StaffDuty> slots, Guid me, bool holds)
    {
        var first = slots[0];
        var managers = first.SeriesManagerUserIds;
        var names = await _context.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => managers.Contains(u.Id)).Select(u => new { u.Id, u.OrganizationId, u.FirstName, u.LastName }).ToListAsync();
        var parameterName = await _context.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Id == first.ParameterId).Select(p => p.Name).FirstOrDefaultAsync() ?? string.Empty;
        var now = DateTime.UtcNow;
        return new DutySeriesDto
        {
            SeriesId = first.SeriesId!.Value,
            Name = first.SeriesName!,
            ParameterId = first.ParameterId,
            ParameterName = parameterName,
            ManagerUserIds = managers.ToList(),
            ManagerNames = managers.Select(id => names.FirstOrDefault(n => n.Id == id) is { } n ? PersonNames.Display(n.OrganizationId, n.FirstName, n.LastName) : "(unknown)").ToList(),
            SlotCount = slots.Count,
            FirstStartsAt = slots.Min(s => s.StartsAt),
            LastEndsAt = slots.Max(s => s.EndsAt),
            RegistersOutstanding = slots.Count(s => s.EndsAt < now && s.RegisterClosedAt == null),
            IAmManager = managers.Contains(me),
            CanWrite = DutySeriesAccess.MayWrite(first, me, holds),
            CanAppoint = holds
        };
    }

    private async Task<DutySeriesDetailDto> DetailAsync(List<StaffDuty> slots, Guid organizationId, Guid branchId, Guid me, bool holds)
    {
        var ordered = slots.OrderBy(s => s.StartsAt).ToList();
        return new DutySeriesDetailDto
        {
            Series = await SummaryAsync(ordered, me, holds),
            Slots = await MapManyAsync(ordered, organizationId, branchId, me, holds)
        };
    }

    /// <summary>
    /// An invigilator already expected somewhere else that overlaps — another sitting, a meeting, a lesson, a rota
    /// slot. WARNED, never refused: a school doubles up on purpose sometimes, and the person running the series is the
    /// one who knows. The same call the rota\'s own warnings make.
    /// </summary>
    private async Task<List<string>> ClashWarningsAsync(IReadOnlyList<StaffDuty> slots, Guid organizationId, Guid branchId)
    {
        var warnings = new List<string>();
        try
        {
            if (slots.Count == 0) return warnings;
            var from = slots.Min(s => s.StartsAt);
            var to = slots.Max(s => s.EndsAt);
            var ids = slots.Select(s => s.Id).ToHashSet();
            var others = await _context.StaffDuties.AsNoTracking()
                .Where(d => d.BranchId == branchId && d.OrganizationId == organizationId && d.IsActive && d.ExpectedUserIds != null
                            && d.StartsAt < to && d.EndsAt > from)
                .Select(d => new { d.Id, d.Title, d.StartsAt, d.EndsAt, d.ExpectedUserIds, d.Kind })
                .ToListAsync();
            others = others.Where(o => !ids.Contains(o.Id)).ToList();
            var people = slots.SelectMany(s => s.ExpectedUserIds ?? Array.Empty<Guid>()).Distinct().ToList();
            var names = await _context.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => people.Contains(u.Id)).Select(u => new { u.Id, u.OrganizationId, u.FirstName, u.LastName }).ToListAsync();
            var zone = AppointmentScheduling.ResolveTimeZone(await _context.Branches.Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());

            foreach (var slot in slots)
                foreach (var person in slot.ExpectedUserIds ?? Array.Empty<Guid>())
                    foreach (var clash in others.Where(o => o.ExpectedUserIds!.Contains(person) && o.StartsAt < slot.EndsAt && o.EndsAt > slot.StartsAt))
                    {
                        var who = names.FirstOrDefault(n => n.Id == person) is { } n ? PersonNames.Display(n.OrganizationId, n.FirstName, n.LastName) : "Somebody";
                        var at = TimeZoneInfo.ConvertTimeFromUtc(clash.StartsAt, zone);
                        warnings.Add(string.Create(CultureInfo.InvariantCulture,
                            $"{who} is also on \"{clash.Title}\"{(clash.Kind == DutyKind.Lesson ? " (a lesson)" : "")} at {at:HH:mm} on {at:ddd dd MMM}, during \"{slot.Title}\"."));
                        if (warnings.Count >= 25) return warnings;
                    }
        }
        catch (Exception ex)
        {
            // A warning that cannot be worked out must not fail a save that has already committed.
            _logger.LogError(ex, "Invigilation clash warnings could not be worked out");
        }
        return warnings;
    }

    /// <summary>ONE notice per invigilator per write — "3 sittings, the first on Mon 30 Nov" — never one per sitting.</summary>
    private async Task NotifyInvigilatorsAsync(List<(StaffDuty Slot, IEnumerable<Guid> People)> assignments, string seriesName, Guid branchId)
    {
        try
        {
            var future = assignments.Where(a => a.Slot.EndsAt > DateTime.UtcNow).ToList();
            if (future.Count == 0) return;
            var zone = AppointmentScheduling.ResolveTimeZone(await _context.Branches.Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());
            var byPerson = future.SelectMany(a => a.People.Distinct().Select(p => (Person: p, a.Slot))).GroupBy(x => x.Person);
            foreach (var group in byPerson)
            {
                var sittings = group.Select(g => g.Slot).OrderBy(s => s.StartsAt).ToList();
                var first = TimeZoneInfo.ConvertTimeFromUtc(sittings[0].StartsAt, zone);
                try
                {
                    await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                    {
                        UserId = group.Key,
                        OrganizationId = sittings[0].OrganizationId,
                        BranchId = branchId,
                        Title = $"Exam invigilation: {seriesName}",
                        Message = string.Create(CultureInfo.InvariantCulture,
                            $"{sittings.Count} sitting{(sittings.Count == 1 ? "" : "s")}, the first \"{sittings[0].Title}\" on {first:ddd dd MMM} at {first:HH:mm}{(string.IsNullOrWhiteSpace(sittings[0].Location) ? "" : $" in {sittings[0].Location}")}."),
                        Type = NotificationType.StaffPerformance,
                        Priority = NotificationPriority.Normal,
                        Channels = NotificationChannel.InApp | NotificationChannel.Email,
                        EventKey = NotificationEventKeys.StaffInvigilationAssigned,
                        ActionUrl = "/my-day",
                        IconClass = "pencil-square"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Invigilation notice could not reach user {UserId}", group.Key);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invigilation notices failed for series {SeriesName}", seriesName);
        }
    }
}
