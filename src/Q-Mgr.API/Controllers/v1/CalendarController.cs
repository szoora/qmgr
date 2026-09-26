using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application;
using QMgr.Application.DTOs;
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
/// The school calendar (plan TERM_PROGRAMME_CALENDAR_AND_GATES §3, §9; decisions D5, D8, D9 — 2026-09-23; reworked by
/// CALENDAR_AUDIENCES_AND_IMPORT_ROUTING, 2026-09-26).
///
/// BASE PRODUCT (D8): no <c>[RequireModule]</c>, and no route here is in <c>ModuleRouteMap</c>. Every school keeps a
/// calendar whatever it has bought. The one exception is "Give this a register", which makes a staff duty and so needs
/// Welfare &amp; Performance and the duty permission — checked in the action, the way the programme import checks it.
///
/// Who sees what is <see cref="SchoolEventVisibility"/>; who is in an audience is <see cref="StaffAudience"/>; who is
/// told is <see cref="ISchoolEventNotifier"/>. Nothing here re-derives any of the three. An event the caller may not
/// see answers 404, never 403 — a 403 confirms it exists.
///
/// <para><b>Writes are serialised with the programme import</b> (B6): every write takes the same
/// <c>pg_advisory_xact_lock</c> on the branch's rota key the import takes, so a hand edit can no longer land between
/// the import's evaluation and its apply and be silently overwritten. Two keepers editing one event are told, not
/// overwritten (B5): the row version is xmin and a stale save is answered 409 <c>EVENT_CHANGED</c>.</para>
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
public class CalendarController : StaffPerformanceControllerBase
{
    /// <summary>The widest range one read may ask for: a school year and a margin.</summary>
    public const int MaxRangeDays = 400;
    /// <summary>The longest single event ("X to Y" in a programme) — a term, not a year.</summary>
    public const int MaxEventSpanDays = 92;
    /// <summary>The most occurrences one "repeats" may make: a school year of weeks.</summary>
    public const int MaxOccurrences = 52;
    private const int MaxClassNames = 40;
    private const int MaxAudienceEntries = 200;

    private readonly IStaffPerformancePolicyService _policy;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly ISchoolEventNotifier _notifier;
    private readonly IModuleAccessService _modules;
    private readonly INotificationService _notifications;
    private readonly ILogger<CalendarController> _logger;

    public CalendarController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        IPlatformSettingsService platformSettings,
        ISchoolEventNotifier notifier,
        IModuleAccessService modules,
        INotificationService notifications,
        ILogger<CalendarController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _platformSettings = platformSettings;
        _notifier = notifier;
        _modules = modules;
        _notifications = notifications;
        _logger = logger;
    }

    // =====================================================================================================
    // The calendar of a range
    // =====================================================================================================

    /// <param name="scope">"mine" (the default): events for me, mine to run, my duties. "all": the whole school's calendar.</param>
    [HttpGet("branches/{branchId:guid}/calendar")]
    [ProducesResponseType(typeof(CalendarRangeDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCalendar(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] string? scope = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (from is not { } start || to is not { } end) return BadRequestProblem("Choose a date range", "Pass from and to as yyyy-MM-dd.");
        if (end < start) return BadRequestProblem("The range ends before it starts.");
        if (end.DayNumber - start.DayNumber + 1 > MaxRangeDays) return BadRequestProblem($"Choose at most {MaxRangeDays} days.");

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var viewer = await StaffAudience.MemberAsync(Db, _policy, organizationId, me);
        var manages = await HasPermissionAsync(Permissions.CalendarManage);
        var mayManageDuties = await HasPermissionAsync(Permissions.StaffDutiesManage);
        var wanted = CalendarScopes.Normalize(scope);
        var wholeSchool = wanted == CalendarScopes.All;

        var events = await SchoolEventQueries.InRangeAsync(Db, organizationId, branchId, start, end);
        var visible = SchoolEventVisibility.Ordered(events.Where(e => SchoolEventVisibility.CanSee(e, viewer, me, manages, wholeSchool))).ToList();
        var lk = await SchoolEventLookups.LoadAsync(Db, organizationId, visible);

        // ---- The caller's duties: Session and Rota only — lessons are the timetable's ----
        var zone = await ZoneAsync(branchId);
        var fromUtc = BranchClock.ToUtc(start.ToDateTime(TimeOnly.MinValue), zone);
        var toUtc = BranchClock.ToUtc(end.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var duties = await MyDutiesQuery(Db.StaffDuties.AsNoTracking().Include(d => d.Parameter), organizationId, branchId, me, fromUtc, toUtc)
            .OrderBy(d => d.StartsAt).Take(1000).ToListAsync();

        var names = await BuildNamesAsync(duties.SelectMany(d => d.RecorderUserIds.Concat(d.SupervisorUserIds).Concat(d.ExpectedUserIds ?? Array.Empty<Guid>())).Select(id => (Guid?)id));

        // ---- Terms: the policy periods overlapping the range ----
        var policy = await _policy.GetAsync(organizationId);
        var terms = new List<PerformancePeriodDto>();
        for (var year = start.Year; year <= end.Year; year++)
            foreach (var p in _policy.PeriodsForYear(policy, year))
                if (p.Start <= end && p.End >= start && terms.All(t => !string.Equals(t.Key, p.Key, StringComparison.OrdinalIgnoreCase)))
                    terms.Add(p);

        var settings = await CalendarSettingsStore.GetAsync(Db, organizationId);
        var national = await NationalCalendarStore.GetAsync(Db);
        var tz = await Db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync();

        return Ok(new CalendarRangeDto
        {
            From = start,
            To = end,
            Events = visible.Select(e => SchoolEventMapping.ToDto(e, lk, me, StaffAudience.IsMine(e, viewer, me), manages, mayManageDuties)).ToList(),
            // B3: "You take the register" is for a NAMED recorder. A duty manager may open every register, but that is a
            // different fact — passing their permission here made every meeting read "You take the register" to them.
            MyDuties = duties.Select(d => StaffPerformanceMapping.ToDto(d, names, me, false, d.ExpectedUserIds?.Length ?? 0, 0, null, null)).ToList(),
            Terms = terms.OrderBy(t => t.Start).ToList(),
            NationalDates = NationalCalendarStore.Overlapping(national, start, end).ToList(),
            Categories = settings.Categories,
            CanManage = manages,
            CanManageDuties = mayManageDuties,
            Scope = wanted,
            Today = BranchClock.Today(zone),
            TimeZone = tz
        });
    }

    /// <summary>
    /// The duties a person is on, in a UTC window: expected at, recording or supervising. "Expected = null" means the
    /// whole branch and is honoured for a Session only — a rota slot always names its people. Shared with the feed.
    /// </summary>
    internal static IQueryable<StaffDuty> MyDutiesQuery(IQueryable<StaffDuty> source, Guid organizationId, Guid? branchId, Guid me, DateTime fromUtc, DateTime toUtc)
        => source.Where(d => d.OrganizationId == organizationId && d.IsActive
                             && (d.Kind == DutyKind.Session || d.Kind == DutyKind.Rota)
                             && (branchId == null || d.BranchId == branchId)
                             && d.StartsAt < toUtc && d.EndsAt > fromUtc
                             && ((d.ExpectedUserIds != null && d.ExpectedUserIds.Contains(me))
                                 || d.RecorderUserIds.Contains(me)
                                 || d.SupervisorUserIds.Contains(me)
                                 || (d.Kind == DutyKind.Session && d.ExpectedUserIds == null)));

    // =====================================================================================================
    // One event
    // =====================================================================================================

    [HttpGet("branches/{branchId:guid}/calendar/events/{id:guid}")]
    [ProducesResponseType(typeof(SchoolEventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEvent(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var e = await Db.SchoolEvents.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId && x.IsActive);
        var me = CurrentUserId();
        var viewer = await StaffAudience.MemberAsync(Db, _policy, organizationId, me);
        var manages = await HasPermissionAsync(Permissions.CalendarManage);
        if (e == null || !SchoolEventVisibility.ForBranch(e, branchId) || !SchoolEventVisibility.CanOpen(e, viewer, me, manages))
            return EventNotFound();

        return Ok(await DtoAsync(e, organizationId, viewer, manages));
    }

    /// <summary>"Add to my calendar": one event as an .ics file (RFC 5545), for anybody who may open it.</summary>
    [HttpGet("branches/{branchId:guid}/calendar/events/{id:guid}/ics")]
    [Produces("text/calendar")]
    public async Task<IActionResult> GetEventIcs(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var e = await Db.SchoolEvents.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId && x.IsActive);
        var me = CurrentUserId();
        var viewer = await StaffAudience.MemberAsync(Db, _policy, organizationId, me);
        var manages = await HasPermissionAsync(Permissions.CalendarManage);
        if (e == null || !SchoolEventVisibility.ForBranch(e, branchId) || !SchoolEventVisibility.CanOpen(e, viewer, me, manages))
            return EventNotFound();

        var ics = new IcsWriter(e.Title);
        IcsEvents.Write(ics, e, await ZoneAsync(branchId));
        return File(Encoding.UTF8.GetBytes(ics.Finish()), "text/calendar; charset=utf-8", IcsEvents.FileName(e));
    }

    // =====================================================================================================
    // Writing
    // =====================================================================================================

    [HttpPost("branches/{branchId:guid}/calendar/events")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(SchoolEventDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateEvent(Guid branchId, [FromBody] SaveSchoolEventRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();

        // B18: a double press of "Add event" answers with the event the first press made.
        if (request.ClientRequestId is { } key && await ExistingForRequestAsync(organizationId, key) is { } already)
            return Ok(await DtoAsync(already, organizationId, null, true));

        var (value, problem) = await ValidateAsync(request, organizationId, branchId);
        if (problem != null) return problem;

        // ---- The occurrences: one, or a series (E12) ----
        var dates = new List<DateOnly> { value!.StartsOn };
        string? recurrence = null;
        if (request.Repeat is { } repeat)
        {
            var (occurrences, words, repeatProblem) = await OccurrencesAsync(organizationId, value, repeat);
            if (repeatProblem != null) return repeatProblem;
            dates = occurrences;
            recurrence = words;
        }

        var seriesId = dates.Count > 1 ? Guid.NewGuid() : (Guid?)null;
        var span = value.EndsOn.DayNumber - value.StartsOn.DayNumber;
        var created = new List<SchoolEvent>();
        foreach (var (date, index) in dates.Select((d, i) => (d, i)))
        {
            var e = new SchoolEvent
            {
                OrganizationId = organizationId,
                CreatedBy = NullIfEmpty(me),
                SeriesId = seriesId,
                Recurrence = recurrence,
                // Only the FIRST occurrence carries the idempotency key; the index is unique.
                ClientRequestId = index == 0 ? request.ClientRequestId : null,
                Version = 1,
                // A series tells its audience ONCE, about the first occurrence and how it repeats. Every other
                // occurrence is marked told, so a later change to one of them is news and the rest are not.
                NotifiedVersion = index == 0 && request.NotifyAudience ? 0 : 1
            };
            Write(e, value, date, date.AddDays(span));
            Db.SchoolEvents.Add(e);
            created.Add(e);
        }

        try
        {
            await Db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (request.ClientRequestId is { } raced)
        {
            // Two presses raced past the lookup: the unique index let one through. Answer with that one.
            _logger.LogInformation(ex, "Duplicate create for request {RequestId}; answering with the first", raced);
            foreach (var c in created) Db.Entry(c).State = EntityState.Detached;
            if (await ExistingForRequestAsync(organizationId, raced) is { } first)
                return Ok(await DtoAsync(first, organizationId, null, true));
            throw;
        }

        var head = created[0];
        await Activity.RecordAsync(ActivityActions.CalendarEventCreated, "SchoolEvent", head.Id, null,
            dates.Count > 1 ? $"Event added: {head.Title} ({dates.Count} occurrences)" : $"Event added: {head.Title}",
            new { head.StartsOn, occurrences = dates.Count }, branchId, organizationId);

        if (request.NotifyAudience) await _notifier.NotifyAsync(head.Id, SchoolEventNoticeKind.Added, me);

        return CreatedAtAction(nameof(GetEvent), new { branchId, id = head.Id }, await DtoAsync(head, organizationId, null, true));
    }

    [HttpPut("branches/{branchId:guid}/calendar/events/{id:guid}")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(SchoolEventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateEvent(Guid branchId, Guid id, [FromBody] SaveSchoolEventRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var (value, problem) = await ValidateAsync(request, organizationId, branchId);
        if (problem != null) return problem;

        var scope = EventEditScopes.Normalize(request.EditScope);
        IActionResult? refusal = null;
        var toNotify = new List<(Guid Id, bool Material)>();
        SchoolEvent? head = null;

        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            refusal = null;
            toNotify.Clear();
            await using var tx = await Db.Database.BeginTransactionAsync();
            await LockBranchAsync(branchId);

            head = await Db.SchoolEvents.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId && x.IsActive);
            if (head == null || !SchoolEventVisibility.ForBranch(head, branchId)) { refusal = EventNotFound(); return; }
            if (request.RowVersion is { } seen && seen != head.RowVersion) { refusal = Changed(); return; }

            var shift = value!.StartsOn.DayNumber - head.StartsOn.DayNumber;
            var span = value.EndsOn.DayNumber - value.StartsOn.DayNumber;
            var rows = await SeriesRowsAsync(head, scope);

            foreach (var e in rows)
            {
                var before = Snapshot(e);
                var starts = e.Id == head.Id ? value.StartsOn : e.StartsOn.AddDays(shift);
                Write(e, value, starts, starts.AddDays(span));
                // A series edit keeps its own recurrence words; a single occurrence edited on its own leaves the series.
                if (scope == EventEditScopes.This && e.SeriesId != null && rows.Count == 1 && (shift != 0 || before.StartTime != e.StartTime))
                    e.Recurrence = null;

                // B2: the meeting this event IS moves with it — or the save is refused, if its register was taken.
                if (e.DutyId is { } dutyId)
                {
                    var dutyProblem = await MoveLinkedDutyAsync(e, dutyId, branchId, before);
                    if (dutyProblem != null) { refusal = dutyProblem; return; }
                }

                var material = IsMaterial(before, e);
                if (material)
                {
                    e.Version++;
                    if (before.StartsOn != e.StartsOn || before.StartTime != e.StartTime) e.ReminderStage = 0;
                }
                if (e.ImportJobId != null || e.SourceKey != null) e.EditedByHandAt = DateTime.UtcNow;
                e.UpdatedAt = DateTime.UtcNow;
                e.UpdatedBy = NullIfEmpty(me);
                if (material) toNotify.Add((e.Id, true));
            }

            // B5: the version the editor saw is the version that must still be there when the row is written.
            if (request.RowVersion is { } original) Db.Entry(head).Property(x => x.RowVersion).OriginalValue = original;
            try
            {
                await Db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                refusal = Changed();
                return;
            }
            await tx.CommitAsync();
        });

        if (refusal != null)
        {
            Db.ChangeTracker.Clear();
            return refusal;
        }

        await Activity.RecordAsync(ActivityActions.CalendarEventUpdated, "SchoolEvent", head!.Id, null,
            $"Event changed: {head.Title}" + (scope != EventEditScopes.This ? $" ({(scope == EventEditScopes.All ? "the whole series" : "this and following")})" : ""),
            new { scope, changed = toNotify.Count }, branchId, organizationId);

        // One notice per change, not one per occurrence: the first changed occurrence carries it, the rest are marked told.
        if (request.NotifyAudience && toNotify.Count > 0)
        {
            await _notifier.NotifyAsync(toNotify[0].Id, SchoolEventNoticeKind.Changed, me);
            await MarkToldAsync(toNotify.Skip(1).Select(t => t.Id).ToList());
        }
        else if (toNotify.Count > 0)
        {
            await MarkToldAsync(toNotify.Select(t => t.Id).ToList());
        }

        var fresh = await Db.SchoolEvents.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == head.Id);
        return Ok(await DtoAsync(fresh, organizationId, null, true));
    }

    /// <summary>
    /// Cancels an event: it stays on the calendar struck through, with its reason, and its audience is told. The meeting
    /// it IS is taken off the duties too — unless its register was taken, which is history and is kept.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/calendar/events/{id:guid}/cancel")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(SchoolEventDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelEvent(Guid branchId, Guid id, [FromBody] CancelSchoolEventRequest request)
        => await SetStatusAsync(branchId, id, request, SchoolEventStatus.Cancelled);

    /// <summary>Puts a cancelled event back on. Its audience is told it is on again.</summary>
    [HttpPost("branches/{branchId:guid}/calendar/events/{id:guid}/reinstate")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(SchoolEventDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReinstateEvent(Guid branchId, Guid id, [FromBody] CancelSchoolEventRequest request)
        => await SetStatusAsync(branchId, id, request, SchoolEventStatus.Scheduled);

    private async Task<IActionResult> SetStatusAsync(Guid branchId, Guid id, CancelSchoolEventRequest request, SchoolEventStatus status)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var me = CurrentUserId();
        var reason = Clean(request.Reason, 300);
        var scope = EventEditScopes.Normalize(request.EditScope);
        IActionResult? refusal = null;
        var changed = new List<Guid>();
        var keptRegisters = 0;
        SchoolEvent? head = null;

        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            refusal = null;
            changed.Clear();
            keptRegisters = 0;
            await using var tx = await Db.Database.BeginTransactionAsync();
            await LockBranchAsync(branchId);

            head = await Db.SchoolEvents.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId && x.IsActive);
            if (head == null || !SchoolEventVisibility.ForBranch(head, branchId)) { refusal = EventNotFound(); return; }
            if (request.RowVersion is { } seen && seen != head.RowVersion) { refusal = Changed(); return; }

            foreach (var e in await SeriesRowsAsync(head, scope))
            {
                if (e.Status == status) continue;
                e.Status = status;
                e.Version++;
                e.UpdatedAt = DateTime.UtcNow;
                e.UpdatedBy = NullIfEmpty(me);
                if (status == SchoolEventStatus.Cancelled)
                {
                    e.CancelReason = reason;
                    e.CancelledAt = DateTime.UtcNow;
                    e.CancelledByUserId = NullIfEmpty(me);
                }
                else
                {
                    e.CancelReason = null;
                    e.CancelledAt = null;
                    e.CancelledByUserId = null;
                    e.ReminderStage = 0;
                }
                if (e.DutyId is { } dutyId)
                {
                    var duty = await Db.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId);
                    if (duty != null)
                    {
                        if (await RegisterTakenAsync(duty)) keptRegisters++;
                        else duty.IsActive = status == SchoolEventStatus.Scheduled;
                    }
                }
                changed.Add(e.Id);
            }
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        });

        if (refusal != null)
        {
            Db.ChangeTracker.Clear();
            return refusal;
        }

        var cancelled = status == SchoolEventStatus.Cancelled;
        await Activity.RecordAsync(cancelled ? ActivityActions.CalendarEventCancelled : ActivityActions.CalendarEventReinstated,
            "SchoolEvent", head!.Id, null,
            (cancelled ? "Event cancelled: " : "Event back on: ") + head.Title + (changed.Count > 1 ? $" ({changed.Count} occurrences)" : "")
            + (keptRegisters > 0 ? $"; {keptRegisters} register(s) already taken were kept" : ""),
            new { reason, scope, keptRegisters }, branchId, organizationId);

        if (changed.Count > 0)
        {
            if (request.NotifyAudience)
            {
                await _notifier.NotifyAsync(changed[0], cancelled ? SchoolEventNoticeKind.Cancelled : SchoolEventNoticeKind.Changed, me);
                await MarkToldAsync(changed.Skip(1).ToList());
            }
            else await MarkToldAsync(changed);
        }

        var fresh = await Db.SchoolEvents.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == head.Id);
        return Ok(await DtoAsync(fresh, organizationId, null, true));
    }

    /// <summary>
    /// Removes an event — for a mistake nobody needed telling about. Something people were expecting is CANCELLED
    /// instead, which keeps the record and tells them. The meeting it IS goes with it, unless its register was taken:
    /// then the delete is refused, because the register is history and this is the only way to find the meeting.
    /// </summary>
    [HttpDelete("branches/{branchId:guid}/calendar/events/{id:guid}")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteEvent(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        IActionResult? refusal = null;
        string title = string.Empty;

        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            refusal = null;
            await using var tx = await Db.Database.BeginTransactionAsync();
            await LockBranchAsync(branchId);

            var e = await Db.SchoolEvents.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);
            if (e == null || !SchoolEventVisibility.ForBranch(e, branchId)) { refusal = EventNotFound(); return; }
            title = e.Title;

            if (e.DutyId is { } dutyId && await Db.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId) is { } duty)
            {
                if (await RegisterTakenAsync(duty))
                {
                    refusal = ConflictProblem("This meeting's register has been taken",
                        "Cancel the event instead: the register is kept, and the meeting stays findable. A taken register is never deleted.");
                    return;
                }
                duty.IsActive = false;
            }

            Db.SchoolEvents.Remove(e);
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        });

        if (refusal != null)
        {
            Db.ChangeTracker.Clear();
            return refusal;
        }

        await Activity.RecordAsync(ActivityActions.CalendarEventDeleted, "SchoolEvent", id, null, $"Event deleted: {title}", null, branchId, organizationId);
        return NoContent();
    }

    // =====================================================================================================
    // Who it is for: the picker's options, and the clash check
    // =====================================================================================================

    /// <summary>
    /// What the audience picker offers (E2). Served by the calendar, which is base product, so a school without Welfare
    /// &amp; Performance can still target "Teaching staff" or "the administrators": a person's staff group resolves on the
    /// server through the policy's defaults, with or without the module.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/calendar/audience-options")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(AudienceOptionsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAudienceOptions(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        return Ok(await AudienceOptionsAsync(Db, _policy, organizationId, branchId));
    }

    /// <summary>The one builder of the picker's options — the calendar and the programme import both call it.</summary>
    internal static async Task<AudienceOptionsDto> AudienceOptionsAsync(QMgrDbContext db, IStaffPerformancePolicyService policyService, Guid organizationId, Guid branchId)
    {
        var members = await StaffAudience.MembersAsync(db, policyService, organizationId, branchId);
        var policy = await policyService.GetAsync(organizationId);
        var groups = (policy.StaffGroups ?? new()).Where(g => g.IsActive).OrderBy(g => g.SortOrder).Select(g => g.Name).ToList();
        foreach (var held in members.Select(m => m.StaffGroup).Where(g => !string.IsNullOrWhiteSpace(g)))
            if (!groups.Any(g => StaffGroups.Key(g) == StaffGroups.Key(held))) groups.Add(held!);

        var roles = members.Where(m => m.RoleCode != null)
            .GroupBy(m => m.RoleCode!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AudienceRoleDto(g.Key, g.First().RoleName ?? g.Key))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var departments = await db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.IsActive && (d.BranchId == null || d.BranchId == branchId))
            .OrderBy(d => d.Name).Select(d => new AudienceDepartmentDto(d.Id, d.Name, d.HeadUserId)).ToListAsync();

        var classTeachers = await db.ClassTeacherAssignments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.BranchId == branchId && a.EndedAt == null
                        && (a.Role == ClassTeacherRole.ClassTeacher || a.Role == ClassTeacherRole.Assistant))
            .Select(a => a.UserId).Distinct().ToListAsync();

        return new AudienceOptionsDto
        {
            StaffGroups = groups,
            Roles = roles,
            Departments = departments,
            People = members.OrderBy(m => m.SortName ?? m.FullName, StringComparer.OrdinalIgnoreCase).ToList(),
            ClassTeacherUserIds = classTeachers.Where(id => members.Any(m => m.UserId == id)).ToList()
        };
    }

    /// <summary>
    /// Who in an audience is already teaching or on another duty when an event would run — asked before saving, so a
    /// keeper can see "12 of the 42 people are teaching then". It warns and never refuses (D6 of the calendar plan).
    /// Lessons are materialised two weeks ahead, so beyond that only duties are known, and the answer says so.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/calendar/clashes")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(EventClashDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckClashes(Guid branchId, [FromBody] EventClashRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (request.EndsOn < request.StartsOn) return BadRequestProblem("The event ends before it starts.");

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var members = await StaffAudience.MembersAsync(Db, _policy, organizationId, branchId);
        var people = StaffAudience.Resolve(request.StaffAudience ?? new StaffAudienceDto(), members)
            .Concat(request.ResponsibleUserIds ?? new()).Distinct().ToList();
        if (people.Count == 0) return Ok(new EventClashDto());

        var zone = await ZoneAsync(branchId);
        var startTime = SchoolEventMapping.ParseTime(request.StartTime) ?? new TimeOnly(7, 0);
        var endTime = SchoolEventMapping.ParseTime(request.EndTime) ?? (request.StartTime == null ? new TimeOnly(18, 0) : startTime.AddHours(1));
        var lastDay = request.EndsOn.DayNumber - request.StartsOn.DayNumber > 13 ? request.StartsOn.AddDays(13) : request.EndsOn;

        var windows = new List<(DateTime From, DateTime To)>();
        for (var day = request.StartsOn; day <= lastDay; day = day.AddDays(1))
            windows.Add((BranchClock.ToUtc(day.ToDateTime(startTime), zone), BranchClock.ToUtc(day.ToDateTime(endTime), zone)));
        var outerFrom = windows.Min(w => w.From);
        var outerTo = windows.Max(w => w.To);

        var duties = await Db.StaffDuties.AsNoTracking()
            .Where(d => d.BranchId == branchId && d.IsActive && d.StartsAt < outerTo && d.EndsAt > outerFrom)
            .Select(d => new { d.Kind, d.StartsAt, d.EndsAt, d.ExpectedUserIds, d.RecorderUserIds, d.SupervisorUserIds, d.Title })
            .ToListAsync();

        var teaching = new HashSet<Guid>();
        var onDuty = new HashSet<Guid>();
        foreach (var d in duties.Where(d => windows.Any(w => d.StartsAt < w.To && d.EndsAt > w.From)))
        {
            var who = (d.ExpectedUserIds ?? Array.Empty<Guid>()).Concat(d.RecorderUserIds).Concat(d.SupervisorUserIds).Where(people.Contains);
            foreach (var p in who) (d.Kind == DutyKind.Lesson ? teaching : onDuty).Add(p);
        }
        onDuty.ExceptWith(teaching);

        var names = members.ToDictionary(m => m.UserId, m => m.FullName);
        var examples = teaching.Concat(onDuty).Take(6).Select(id => names.TryGetValue(id, out var n) ? n : "").Where(n => n.Length > 0).ToList();
        var zoneToday = BranchClock.Today(zone);
        return Ok(new EventClashDto
        {
            AudienceCount = people.Count,
            Teaching = teaching.Count,
            OnDuty = onDuty.Count,
            Examples = examples,
            LessonsKnown = lastDay <= zoneToday.AddDays(14)
        });
    }

    // =====================================================================================================
    // "Give this a register" (E10): an event becomes a staff meeting whose attendance is taken
    // =====================================================================================================

    [HttpPost("branches/{branchId:guid}/calendar/events/{id:guid}/register")]
    [RequirePermission(Permissions.StaffDutiesManage)]
    [ProducesResponseType(typeof(SchoolEventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GiveRegister(Guid branchId, Guid id, [FromBody] EventRegisterRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        if (!IsSuperAdmin && !await _modules.IsModuleActiveAsync(organizationId, ModuleCodes.StudentWelfare))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "MODULE_NOT_PURCHASED",
                module = ModuleCodes.StudentWelfare,
                message = "Registers are part of Welfare & Performance. Add it from Billing to take attendance at meetings.",
                purchaseUrl = BillingLinks.Modules
            });

        var me = CurrentUserId();
        var recorders = (request.RecorderUserIds ?? new()).Where(x => x != Guid.Empty).Distinct().ToList();
        if (recorders.Count == 0) return BadRequestProblem("Choose who takes the register.", "A register nobody may take is not a register.");

        var members = await StaffAudience.MembersAsync(Db, _policy, organizationId, branchId);
        if (recorders.Any(r => members.All(m => m.UserId != r)))
            return BadRequestProblem("Somebody chosen to take the register is not an active member of staff at this branch.");

        IActionResult? refusal = null;
        StaffDuty? duty = null;
        SchoolEvent? e = null;
        var meetingParameterId = await MeetingParameter.EnsureAsync(Db, organizationId, _logger);

        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            refusal = null;
            if (duty != null) Db.Entry(duty).State = EntityState.Detached;
            duty = null;
            await using var tx = await Db.Database.BeginTransactionAsync();
            await LockBranchAsync(branchId);

            e = await Db.SchoolEvents.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId && x.IsActive);
            if (e == null || !SchoolEventVisibility.ForBranch(e, branchId)) { refusal = EventNotFound(); return; }
            if (e.DutyId is { } existing && await Db.StaffDuties.AnyAsync(d => d.Id == existing && d.IsActive))
            {
                refusal = ConflictProblem("This event already has a register.");
                return;
            }
            if (e.Status == SchoolEventStatus.Cancelled) { refusal = ConflictProblem("The event is cancelled.", "Put it back on first."); return; }

            var start = e.StartTime ?? SchoolEventMapping.ParseTime(request.StartTime);
            if (start == null) { refusal = BadRequestProblem("Give the meeting a start time.", "The event is all day; a register needs a time."); return; }
            var end = (e.StartTime != null ? e.EndTime : SchoolEventMapping.ParseTime(request.EndTime)) ?? start.Value.AddHours(1);
            if (end <= start) { refusal = BadRequestProblem("The meeting must end after it starts."); return; }

            var expected = request.Expected ?? (((e.Audience & EventAudience.Staff) == EventAudience.Staff) ? StaffAudience.Of(e) : StaffAudienceDto.Everyone());
            List<Guid>? expectedIds = expected.AllStaff ? null : StaffAudience.Resolve(expected, members).Union(e.ResponsibleUserIds).Distinct().ToList();
            if (expectedIds is { Count: 0 }) { refusal = BadRequestProblem("Nobody is expected.", "Choose who the meeting is for."); return; }

            var zone = await ZoneAsync(branchId);
            duty = new StaffDuty
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                ParameterId = meetingParameterId,
                Kind = DutyKind.Session,
                Title = e.Title,
                Description = e.Description,
                Location = e.Location,
                StartsAt = BranchClock.ToUtc(e.StartsOn.ToDateTime(start.Value), zone),
                EndsAt = BranchClock.ToUtc(e.StartsOn.ToDateTime(end), zone),
                ExpectedUserIds = expectedIds?.ToArray(),
                RecorderUserIds = recorders.ToArray(),
                CreatedByUserId = me,
                ImportJobId = e.ImportJobId,
                SourceKey = e.SourceKey
            };
            Db.StaffDuties.Add(duty);
            e.DutyId = duty.Id;
            if (e.StartTime == null) { e.StartTime = start; e.EndTime = end; e.Version++; }
            e.UpdatedAt = DateTime.UtcNow;
            e.UpdatedBy = NullIfEmpty(me);
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        });

        if (refusal != null)
        {
            Db.ChangeTracker.Clear();
            return refusal;
        }

        await Activity.RecordAsync(ActivityActions.CalendarEventRegisterGiven, "SchoolEvent", e!.Id, null,
            $"Register added to {e.Title}", new { dutyId = duty!.Id, expected = duty.ExpectedUserIds?.Length }, branchId, organizationId);

        if (request.NotifyPeople && e.StartsOn >= BranchClock.Today(await ZoneAsync(branchId)))
        {
            var people = (duty.ExpectedUserIds ?? members.Select(m => m.UserId).ToArray()).Concat(duty.RecorderUserIds).Distinct().Where(p => p != me).ToList();
            await _notifications.NotifyManyAsync(people, new CreateNotificationRequest
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                Title = $"Meeting: {e.Title}",
                Message = $"{SchoolEventText.WhenAndWhere(e)}. Attendance will be taken.",
                Type = NotificationType.StaffPerformance,
                Channels = NotificationChannel.InApp | NotificationChannel.Email,
                EventKey = NotificationEventKeys.StaffDutyReminder,
                ActionUrl = SchoolEventText.Link(e),
                IconClass = "clipboard-check"
            });
        }

        var fresh = await Db.SchoolEvents.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == e.Id);
        return Ok(await DtoAsync(fresh, organizationId, null, await HasPermissionAsync(Permissions.CalendarManage)));
    }

    // =====================================================================================================
    // Validation and writing
    // =====================================================================================================

    /// <summary>A request, checked and cleaned. Written onto one row or a series by <see cref="Write"/>.</summary>
    private sealed record ValidEvent(
        Guid? BranchId, string Title, string? Description, DateOnly StartsOn, DateOnly EndsOn, TimeOnly? StartTime, TimeOnly? EndTime,
        string? Category, EventAudience Audience, string[] ClassNames, string? Location, string? ResponsibleText, Guid[] ResponsibleUserIds,
        Guid[] ResponsibleDepartmentIds, StaffAudienceDto StaffAudience, bool AudienceOnly, bool AttendanceRequired, bool RemindersOn,
        Guid? LibraryDocumentId);

    /// <summary>Every refusal names the field in words a form can show. Nothing is written.</summary>
    private async Task<(ValidEvent? Value, IActionResult? Problem)> ValidateAsync(SaveSchoolEventRequest r, Guid organizationId, Guid branchId)
    {
        var title = (r.Title ?? string.Empty).Trim();
        if (title.Length == 0) return (null, Field("title", "Give the event a title."));
        if (title.Length > 200) return (null, Field("title", "A title cannot exceed 200 characters."));

        if (r.StartsOn.Year < 2000 || r.StartsOn.Year > 2100) return (null, Field("startsOn", "Choose the day the event starts."));
        if (r.EndsOn < r.StartsOn) return (null, Field("endsOn", "The event ends before it starts."));
        if (r.EndsOn.DayNumber - r.StartsOn.DayNumber > MaxEventSpanDays)
            return (null, Field("endsOn", $"An event can run for at most {MaxEventSpanDays} days. A longer stretch is a term — set it on the term dates instead."));

        var startTime = SchoolEventMapping.ParseTime(r.StartTime);
        var endTime = SchoolEventMapping.ParseTime(r.EndTime);
        if (!string.IsNullOrWhiteSpace(r.StartTime) && startTime == null) return (null, Field("startTime", "Use a start time like 08:30."));
        if (!string.IsNullOrWhiteSpace(r.EndTime) && endTime == null) return (null, Field("endTime", "Use an end time like 17:00."));
        if (endTime != null && startTime == null) return (null, Field("startTime", "An end time needs a start time. Leave both empty for an all-day event."));
        // B16: a timed event over several days runs at those times EACH day, so the end must follow the start whatever the span.
        if (startTime != null && endTime != null && endTime <= startTime)
            return (null, Field("endTime", "The end time must be after the start time. An event over several days runs at these times each day."));

        const EventAudience known = EventAudience.Staff | EventAudience.Students | EventAudience.Guardians | EventAudience.Public;
        if (r.Audience == EventAudience.None || (r.Audience & ~known) != 0) return (null, Field("audience", "Choose who the event is for."));

        if (r.BranchId is { } eventBranch && eventBranch != branchId)
            return (null, BadRequestProblem("An event belongs to this branch or to every branch."));

        string? category = null;
        if (!string.IsNullOrWhiteSpace(r.Category))
        {
            var settings = await CalendarSettingsStore.GetAsync(Db, organizationId);
            category = CalendarSettingsStore.MatchCategory(settings, r.Category);
            if (category == null) return (null, Field("category", $"\"{r.Category.Trim()}\" is not one of the calendar's categories. Add it under Calendar settings first."));
        }

        var classNames = new List<string>();
        foreach (var raw in r.ClassNames ?? new List<string>())
        {
            var c = (raw ?? string.Empty).Trim();
            if (c.Length == 0) continue;
            if (c.Length > 60) return (null, Field("classNames", "A class name cannot exceed 60 characters."));
            if (!classNames.Any(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase))) classNames.Add(c);
        }
        if (classNames.Count > MaxClassNames) return (null, Field("classNames", $"At most {MaxClassNames} classes."));

        var userIds = (r.ResponsibleUserIds ?? new List<Guid>()).Where(x => x != Guid.Empty).Distinct().ToList();
        if (userIds.Count > 50) return (null, Field("responsibleUserIds", "At most 50 people may be named responsible."));
        var departmentIds = (r.ResponsibleDepartmentIds ?? new List<Guid>()).Where(x => x != Guid.Empty).Distinct().ToList();
        if (departmentIds.Count > 50) return (null, Field("responsibleDepartmentIds", "At most 50 departments may be named responsible."));

        // ---- The staff audience (E2) ----
        var audience = r.StaffAudience ?? StaffAudienceDto.Everyone();
        var forStaff = (r.Audience & EventAudience.Staff) == EventAudience.Staff;
        if (forStaff && audience.IsEmpty)
            return (null, Field("staffAudience", "Choose which staff it is for: everyone, or at least one group, role, department or person."));
        if (audience.StaffGroups.Count + audience.RoleCodes.Count + audience.DepartmentIds.Count + audience.UserIds.Count > MaxAudienceEntries)
            return (null, Field("staffAudience", $"Choose at most {MaxAudienceEntries} groups, roles, departments and people."));

        var allUsers = userIds.Concat(audience.UserIds.Where(x => x != Guid.Empty)).Distinct().ToList();
        if (allUsers.Count > 0)
        {
            var found = await Db.Users.IgnoreQueryFilters().CountAsync(u => allUsers.Contains(u.Id) && u.OrganizationId == organizationId && u.IsActive);
            if (found != allUsers.Count) return (null, Field("responsibleUserIds", "Somebody named is not an active member of staff here."));
        }
        var allDepartments = departmentIds.Concat(audience.DepartmentIds.Where(x => x != Guid.Empty)).Distinct().ToList();
        if (allDepartments.Count > 0)
        {
            var found = await Db.Departments.IgnoreQueryFilters().CountAsync(d => allDepartments.Contains(d.Id) && d.OrganizationId == organizationId);
            if (found != allDepartments.Count) return (null, Field("responsibleDepartmentIds", "A department named does not exist here."));
        }
        if (audience.RoleCodes.Count > 0)
        {
            var codes = audience.RoleCodes.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var known2 = await Db.Roles.IgnoreQueryFilters().Where(x => codes.Contains(x.Code) && (x.OrganizationId == null || x.OrganizationId == organizationId))
                .Select(x => x.Code).Distinct().CountAsync();
            if (known2 != codes.Count) return (null, Field("staffAudience", "A role chosen does not exist here."));
        }

        if (r.LibraryDocumentId is { } doc && !await Db.MediaContents.IgnoreQueryFilters().AnyAsync(m => m.Id == doc && m.OrganizationId == organizationId && m.IsActive))
            return (null, Field("libraryDocumentId", "That document is not in the Library any more."));

        return (new ValidEvent(r.BranchId, title, Clean(r.Description, 2000), r.StartsOn, r.EndsOn, startTime, endTime, category, r.Audience,
            classNames.ToArray(), Clean(r.Location, 200), Clean(r.ResponsibleText, 300), userIds.ToArray(), departmentIds.ToArray(),
            audience, r.AudienceOnly, r.AttendanceRequired, r.RemindersOn, r.LibraryDocumentId), null);
    }

    private static void Write(SchoolEvent e, ValidEvent v, DateOnly startsOn, DateOnly endsOn)
    {
        e.BranchId = v.BranchId;
        e.Title = v.Title;
        e.Description = v.Description;
        e.StartsOn = startsOn;
        e.EndsOn = endsOn;
        e.StartTime = v.StartTime;
        e.EndTime = v.EndTime;
        e.Category = v.Category;
        e.Audience = v.Audience;
        e.ClassNames = v.ClassNames;
        e.Location = v.Location;
        e.ResponsibleText = v.ResponsibleText;
        e.ResponsibleUserIds = v.ResponsibleUserIds;
        e.ResponsibleDepartmentIds = v.ResponsibleDepartmentIds;
        StaffAudience.Apply(e, v.StaffAudience);
        e.AudienceOnly = v.AudienceOnly;
        e.AttendanceRequired = v.AttendanceRequired;
        e.RemindersOn = v.RemindersOn;
        e.LibraryDocumentId = v.LibraryDocumentId;
    }

    /// <summary>The facts a notice is about. A change to anything else — a typo in the title, the notes — is not news.</summary>
    private sealed record Facts(DateOnly StartsOn, DateOnly EndsOn, TimeOnly? StartTime, TimeOnly? EndTime, string? Location,
        EventAudience Audience, string Audiences, bool AttendanceRequired);

    private static Facts Snapshot(SchoolEvent e) => new(e.StartsOn, e.EndsOn, e.StartTime, e.EndTime, e.Location, e.Audience,
        string.Join("|", e.AllStaff, string.Join(",", e.AudienceStaffGroups.OrderBy(x => x)), string.Join(",", e.AudienceRoleCodes.OrderBy(x => x)),
            string.Join(",", e.AudienceDepartmentIds.OrderBy(x => x)), string.Join(",", e.AudienceUserIds.OrderBy(x => x))),
        e.AttendanceRequired);

    private static bool IsMaterial(Facts before, SchoolEvent after) => before != Snapshot(after);

    /// <summary>The rows an edit or a cancellation reaches: the one, this and following, or the whole series.</summary>
    private async Task<List<SchoolEvent>> SeriesRowsAsync(SchoolEvent head, string scope)
    {
        if (scope == EventEditScopes.This || head.SeriesId is not { } seriesId || head.Recurrence == null) return new List<SchoolEvent> { head };
        var rows = await Db.SchoolEvents.IgnoreQueryFilters()
            .Where(x => x.SeriesId == seriesId && x.OrganizationId == head.OrganizationId && x.IsActive
                        && (scope == EventEditScopes.All || x.StartsOn >= head.StartsOn))
            .OrderBy(x => x.StartsOn).ToListAsync();
        if (rows.All(x => x.Id != head.Id)) rows.Insert(0, head);
        return rows;
    }

    /// <summary>
    /// B2: the meeting an event IS moves with it. A meeting whose register was taken cannot move — that register says
    /// who was there at the time it records — so the save is refused with the reason, and a title or venue fix still goes.
    /// </summary>
    private async Task<IActionResult?> MoveLinkedDutyAsync(SchoolEvent e, Guid dutyId, Guid branchId, Facts before)
    {
        var duty = await Db.StaffDuties.FirstOrDefaultAsync(d => d.Id == dutyId);
        if (duty == null || !duty.IsActive) return null;

        var timeMoved = before.StartsOn != e.StartsOn || before.StartTime != e.StartTime || before.EndTime != e.EndTime;
        if (timeMoved)
        {
            if (await RegisterTakenAsync(duty))
                return ConflictProblem("This meeting's register has been taken, so its date and time cannot change.",
                    "Its title, venue and notes can still be changed. For a meeting held on another day, add a new event.");
            if (e.StartTime is not { } st)
                return Field("startTime", "This event is also a staff meeting with a register, and a register needs a time.");
            var zone = await ZoneAsync(branchId);
            duty.StartsAt = BranchClock.ToUtc(e.StartsOn.ToDateTime(st), zone);
            duty.EndsAt = BranchClock.ToUtc(e.StartsOn.ToDateTime(e.EndTime ?? st.AddHours(1)), zone);
            duty.ReminderStage = 0;
        }
        duty.Title = e.Title;
        duty.Location = e.Location;
        duty.Description = e.Description;
        duty.UpdatedAt = DateTime.UtcNow;
        return null;
    }

    /// <summary>A register is history once it was opened or anybody was marked on it — the import's rule, one copy.</summary>
    private Task<bool> RegisterTakenAsync(StaffDuty duty)
        => duty.RegisterOpenedAt != null
            ? Task.FromResult(true)
            : Db.StaffPerformanceRecords.IgnoreQueryFilters().AnyAsync(r => r.DutyId == duty.Id);

    /// <summary>Weekly or fortnightly occurrences, in term time, skipping national holidays (E12).</summary>
    private async Task<(List<DateOnly> Dates, string? Words, IActionResult? Problem)> OccurrencesAsync(Guid organizationId, ValidEvent v, RepeatRuleDto repeat)
    {
        var step = string.Equals(repeat.Frequency, "fortnightly", StringComparison.OrdinalIgnoreCase) ? 14 : 7;
        if (repeat.Until <= v.StartsOn) return (new(), null, Field("repeatUntil", "Repeat until a day after the first one."));
        if (repeat.Until.DayNumber - v.StartsOn.DayNumber > MaxRangeDays) return (new(), null, Field("repeatUntil", "Repeat for at most a school year."));
        if (v.EndsOn > v.StartsOn && v.EndsOn.DayNumber - v.StartsOn.DayNumber >= step)
            return (new(), null, Field("repeatUntil", "An event that lasts longer than its repeat cannot repeat."));

        var policy = await _policy.GetAsync(organizationId);
        var terms = new List<PerformancePeriodDto>();
        for (var year = v.StartsOn.Year; year <= repeat.Until.Year; year++) terms.AddRange(_policy.PeriodsForYear(policy, year));
        var national = await NationalCalendarStore.GetAsync(Db);
        bool IsHoliday(DateOnly d) => national.Entries.Any(n => n.StartsOn <= d && n.EndsOn >= d
            && (n.Kind ?? "").Contains("holiday", StringComparison.OrdinalIgnoreCase));
        // A school that has defined its own terms decides term time; one on the derived defaults has no holidays in them,
        // so the national calendar's holidays are the only thing that can take a week out.
        var ownTerms = policy.Periods is { Count: > 0 };
        bool InTerm(DateOnly d) => !ownTerms || terms.Any(t => t.Start <= d && t.End >= d);

        var dates = new List<DateOnly>();
        for (var d = v.StartsOn; d <= repeat.Until && dates.Count < MaxOccurrences; d = d.AddDays(step))
        {
            if (d != v.StartsOn && repeat.TermTimeOnly && (!InTerm(d) || IsHoliday(d))) continue;
            dates.Add(d);
        }
        if (dates.Count < 2) return (new(), null, Field("repeatUntil", "That repeat gives only one date in term time."));

        var words = string.Create(CultureInfo.InvariantCulture,
            $"{(step == 14 ? "Every other" : "Every")} {v.StartsOn.DayOfWeek}{(repeat.TermTimeOnly ? ", term time" : "")}, until {repeat.Until:dd MMM yyyy}");
        return (dates, words, null);
    }

    private async Task<SchoolEvent?> ExistingForRequestAsync(Guid organizationId, Guid key)
        => await Db.SchoolEvents.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.ClientRequestId == key);

    /// <summary>Marks rows as told at their current version — a series edit tells its audience once, not per week.</summary>
    private async Task MarkToldAsync(List<Guid> ids)
    {
        if (ids.Count == 0) return;
        await Db.SchoolEvents.IgnoreQueryFilters().Where(x => ids.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NotifiedVersion, x => x.Version));
    }

    /// <summary>The import's lock (<c>staff-rota:{branch}</c>): a hand edit and an import commit can no longer interleave (B6).</summary>
    private Task LockBranchAsync(Guid branchId)
    {
        var lockKey = $"staff-rota:{branchId}";
        return Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");
    }

    private async Task<SchoolEventDto> DtoAsync(SchoolEvent e, Guid organizationId, AudienceMemberDto? viewer, bool canEdit)
    {
        var me = CurrentUserId();
        viewer ??= await StaffAudience.MemberAsync(Db, _policy, organizationId, me);
        var lk = await SchoolEventLookups.LoadAsync(Db, organizationId, new[] { e });
        return SchoolEventMapping.ToDto(e, lk, me, StaffAudience.IsMine(e, viewer, me), canEdit || await HasPermissionAsync(Permissions.CalendarManage),
            await HasPermissionAsync(Permissions.StaffDutiesManage));
    }

    private IActionResult Changed() => Conflict(new
    {
        error = "EVENT_CHANGED",
        message = "Somebody changed this event while you were editing it. Their version is now shown — make your change again."
    });

    /// <summary>A refusal the form can put beside the field (<c>errors: { field: message }</c>).</summary>
    private IActionResult Field(string field, string message)
        => BadRequest(new { message, errors = new Dictionary<string, string> { [field] = message } });

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        return t.Length <= max ? t : t[..max];
    }

    // =====================================================================================================
    // Settings
    // =====================================================================================================

    [HttpGet("calendar/settings")]
    [ProducesResponseType(typeof(CalendarSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSettings()
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();
        return Ok(await CalendarSettingsStore.GetAsync(Db, organizationId.Value));
    }

    [HttpPut("calendar/settings")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(CalendarSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSettings([FromBody] CalendarSettingsDto request)
    {
        var organizationId = CurrentOrganizationId();
        if (organizationId == null) return TenantNotResolved();

        var (clean, error) = CalendarSettingsStore.Normalize(request);
        if (clean == null) return BadRequestProblem(error ?? "The settings could not be saved.");

        // Through the ONE writer of Organization.Settings, so a save here never drops another key.
        var found = await OrganizationSettingsLock.MutateAsync(Db, organizationId.Value, org =>
        {
            org.Settings = OrganizationSettingsLock.WithKey(org.Settings, CalendarSettingsStore.Key, clean);
            return true;
        });
        if (!found) return NotFoundProblem("Organization not found");

        return Ok(await CalendarSettingsStore.GetAsync(Db, organizationId.Value));
    }

    // =====================================================================================================
    // The personal feed (RFC 5545) — the link is shown once; the server keeps only its hash
    // =====================================================================================================

    [HttpGet("calendar/feed")]
    [ProducesResponseType(typeof(CalendarFeedDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFeed()
    {
        var me = CurrentUserId();
        var user = await Db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == me).Select(u => new { u.CalendarFeedTokenHash, u.CalendarFeedCreatedAt }).FirstOrDefaultAsync();
        if (user == null) return Unauthorized();
        return Ok(new CalendarFeedDto { Exists = user.CalendarFeedTokenHash != null, CreatedAt = user.CalendarFeedTokenHash != null ? user.CalendarFeedCreatedAt : null });
    }

    /// <summary>Creates the link, or REPLACES it — the old link stops working at once.</summary>
    [HttpPost("calendar/feed")]
    [ProducesResponseType(typeof(CalendarFeedDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateFeed()
    {
        var me = CurrentUserId();
        var user = await Db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == me && u.IsActive);
        if (user == null) return Unauthorized();

        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(20)); // 160 bits
        user.CalendarFeedTokenHash = FeedTokenHash(secret);
        user.CalendarFeedCreatedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        // A calendar application fetches this from outside, so it is the PUBLIC address — never the request host of
        // a Web-to-API call. nginx sends /api/ on the public host to the API.
        var baseUrl = (await _platformSettings.GetPublicWebBaseUrlAsync()).TrimEnd('/');
        return Ok(new CalendarFeedDto
        {
            Exists = true,
            CreatedAt = user.CalendarFeedCreatedAt,
            Url = $"{baseUrl}/api/v1/public/calendar/{secret}.ics"
        });
    }

    [HttpDelete("calendar/feed")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteFeed()
    {
        var me = CurrentUserId();
        var user = await Db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == me);
        if (user == null) return Unauthorized();
        user.CalendarFeedTokenHash = null;
        user.CalendarFeedCreatedAt = null;
        await Db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>The one rule for what is stored of a feed secret: lower-case hex SHA-256 of its UTF-8 bytes.</summary>
    internal static string FeedTokenHash(string secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    // =====================================================================================================
    // The national calendar (D9): read by everyone signed in, kept by the platform administrator
    // =====================================================================================================

    [HttpGet("calendar/national")]
    [ProducesResponseType(typeof(NationalCalendarDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNationalCalendar([FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var calendar = await NationalCalendarStore.GetAsync(Db);
        if (from == null && to == null) return Ok(calendar);
        var start = from ?? DateOnly.MinValue;
        var end = to ?? DateOnly.MaxValue;
        if (end < start) return BadRequestProblem("The range ends before it starts.");
        return Ok(new NationalCalendarDto { Entries = NationalCalendarStore.Overlapping(calendar, start, end).ToList() });
    }

    [HttpGet("platform/national-calendar")]
    [RequirePermission(Permissions.PlatformSettingsView)]
    [ProducesResponseType(typeof(NationalCalendarDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlatformNationalCalendar() => Ok(await NationalCalendarStore.GetAsync(Db));

    [HttpPut("platform/national-calendar")]
    [RequirePermission(Permissions.PlatformSettingsEdit)]
    [ProducesResponseType(typeof(NationalCalendarDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdatePlatformNationalCalendar([FromBody] NationalCalendarDto request)
    {
        var (clean, error) = NationalCalendarStore.Normalize(request ?? new NationalCalendarDto());
        if (clean == null) return BadRequestProblem(error ?? "The national calendar could not be saved.");
        await NationalCalendarStore.SaveAsync(Db, clean, NullIfEmpty(CurrentUserId()));
        _logger.LogInformation("National calendar saved: {Count} entries by {UserId}", clean.Entries.Count, CurrentUserId());
        return Ok(await NationalCalendarStore.GetAsync(Db));
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    private async Task<TimeZoneInfo> ZoneAsync(Guid branchId)
        => BranchClock.Resolve(await Db.Branches.IgnoreQueryFilters().Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());

    /// <summary>A branch-local wall-clock time as UTC; kept for the import and the feed, which call it by this name.</summary>
    internal static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone) => BranchClock.ToUtc(local, zone);

    private static Guid? NullIfEmpty(Guid id) => id == Guid.Empty ? null : id;

    private IActionResult EventNotFound() => NotFoundProblem("Event not found");
}
