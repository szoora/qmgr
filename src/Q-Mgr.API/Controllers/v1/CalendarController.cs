using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Calendar;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The school calendar (plan TERM_PROGRAMME_CALENDAR_AND_GATES §3, §9; decisions D5, D8, D9 — 2026-09-23).
///
/// BASE PRODUCT (D8): no <c>[RequireModule]</c>, and no route here is in <c>ModuleRouteMap</c>. Every school keeps a
/// calendar whatever it has bought; importing a rota or a meeting schedule is Welfare &amp; Performance and lives
/// with the import.
///
/// Who sees what is <see cref="SchoolEventVisibility"/>, and nothing here re-derives it. An event the caller may not
/// see answers 404, never 403 — a 403 confirms it exists.
///
/// It inherits the Staff Performance base for its guards (branch ownership with the SuperAdmin bypass, the caller's
/// id, the post-aware permission lookup, the problem shapes) — not for anything about staff performance.
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
    private const int MaxClassNames = 40;

    private readonly IStaffPerformancePolicyService _policy;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly ILogger<CalendarController> _logger;

    public CalendarController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffPerformancePolicyService policy,
        IPlatformSettingsService platformSettings,
        ILogger<CalendarController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _policy = policy;
        _platformSettings = platformSettings;
        _logger = logger;
    }

    // =====================================================================================================
    // The calendar of a range
    // =====================================================================================================

    [HttpGet("branches/{branchId:guid}/calendar")]
    [ProducesResponseType(typeof(CalendarRangeDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCalendar(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;
        if (from is not { } start || to is not { } end) return BadRequestProblem("Choose a date range", "Pass from and to as yyyy-MM-dd.");
        if (end < start) return BadRequestProblem("The range ends before it starts.");
        if (end.DayNumber - start.DayNumber + 1 > MaxRangeDays) return BadRequestProblem($"Choose at most {MaxRangeDays} days.");

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var (me, departments) = await ViewerAsync();
        var manages = await HasPermissionAsync(Permissions.CalendarManage);

        var events = await SchoolEventQueries.InRangeAsync(Db, organizationId, branchId, start, end);
        var visible = SchoolEventVisibility.Ordered(events.Where(e => SchoolEventVisibility.CanSee(e, me, departments, manages))).ToList();

        // ---- The caller's duties: Session and Rota only — lessons are the timetable's ----
        var zone = await ZoneAsync(branchId);
        var fromUtc = LocalToUtc(start.ToDateTime(TimeOnly.MinValue), zone);
        var toUtc = LocalToUtc(end.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var duties = await MyDutiesQuery(Db.StaffDuties.AsNoTracking().Include(d => d.Parameter), organizationId, branchId, me, fromUtc, toUtc)
            .OrderBy(d => d.StartsAt).Take(1000).ToListAsync();

        var names = await BuildNamesAsync(SchoolEventMapping.NameIds(visible)
            .Concat(duties.SelectMany(d => d.RecorderUserIds.Concat(d.SupervisorUserIds).Concat(d.ExpectedUserIds ?? Array.Empty<Guid>())).Select(id => (Guid?)id)));
        var mayManageDuties = await HasPermissionAsync(Permissions.StaffDutiesManage);

        // ---- Terms: the policy periods overlapping the range ----
        var policy = await _policy.GetAsync(organizationId);
        var terms = new List<PerformancePeriodDto>();
        for (var year = start.Year; year <= end.Year; year++)
            foreach (var p in _policy.PeriodsForYear(policy, year))
                if (p.Start <= end && p.End >= start && terms.All(t => !string.Equals(t.Key, p.Key, StringComparison.OrdinalIgnoreCase)))
                    terms.Add(p);

        var settings = await CalendarSettingsStore.GetAsync(Db, organizationId);
        var national = await NationalCalendarStore.GetAsync(Db);

        return Ok(new CalendarRangeDto
        {
            From = start,
            To = end,
            Events = visible.Select(e => SchoolEventMapping.ToDto(e, names, manages)).ToList(),
            MyDuties = duties.Select(d => StaffPerformanceMapping.ToDto(d, names, me, mayManageDuties, d.ExpectedUserIds?.Length ?? 0, 0, null, null)).ToList(),
            Terms = terms.OrderBy(t => t.Start).ToList(),
            NationalDates = NationalCalendarStore.Overlapping(national, start, end).ToList(),
            Categories = settings.Categories,
            CanManage = manages
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
    // One event, and writing them
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
        var (me, departments) = await ViewerAsync();
        var manages = await HasPermissionAsync(Permissions.CalendarManage);
        if (e == null || !SchoolEventVisibility.ForBranch(e, branchId) || !SchoolEventVisibility.CanSee(e, me, departments, manages))
            return EventNotFound();

        var names = await BuildNamesAsync(SchoolEventMapping.NameIds(new[] { e }));
        return Ok(SchoolEventMapping.ToDto(e, names, manages));
    }

    [HttpPost("branches/{branchId:guid}/calendar/events")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(SchoolEventDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateEvent(Guid branchId, [FromBody] SaveSchoolEventRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var e = new SchoolEvent { OrganizationId = organizationId, CreatedBy = NullIfEmpty(CurrentUserId()) };
        var problem = await ApplyAsync(e, request, organizationId, branchId);
        if (problem != null) return problem;

        Db.SchoolEvents.Add(e);
        await Db.SaveChangesAsync();

        var names = await BuildNamesAsync(SchoolEventMapping.NameIds(new[] { e }));
        return CreatedAtAction(nameof(GetEvent), new { branchId, id = e.Id }, SchoolEventMapping.ToDto(e, names, true));
    }

    [HttpPut("branches/{branchId:guid}/calendar/events/{id:guid}")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(typeof(SchoolEventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateEvent(Guid branchId, Guid id, [FromBody] SaveSchoolEventRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var e = await Db.SchoolEvents.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId && x.IsActive);
        if (e == null || !SchoolEventVisibility.ForBranch(e, branchId)) return EventNotFound();

        var problem = await ApplyAsync(e, request, organizationId, branchId);
        if (problem != null) return problem;
        e.UpdatedAt = DateTime.UtcNow;
        e.UpdatedBy = NullIfEmpty(CurrentUserId());
        await Db.SaveChangesAsync();

        var names = await BuildNamesAsync(SchoolEventMapping.NameIds(new[] { e }));
        return Ok(SchoolEventMapping.ToDto(e, names, true));
    }

    /// <summary>Removes the event. One created by a programme import is the school's to delete like any other.</summary>
    [HttpDelete("branches/{branchId:guid}/calendar/events/{id:guid}")]
    [RequirePermission(Permissions.CalendarManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteEvent(Guid branchId, Guid id)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var e = await Db.SchoolEvents.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);
        if (e == null || !SchoolEventVisibility.ForBranch(e, branchId)) return EventNotFound();

        Db.SchoolEvents.Remove(e);
        await Db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Validates <paramref name="r"/> and writes it onto <paramref name="e"/>. Nothing is written when a problem is
    /// returned. Every refusal names the field in words a form can show.
    /// </summary>
    private async Task<IActionResult?> ApplyAsync(SchoolEvent e, SaveSchoolEventRequest r, Guid organizationId, Guid branchId)
    {
        var title = (r.Title ?? string.Empty).Trim();
        if (title.Length == 0) return BadRequestProblem("Give the event a title.");
        if (title.Length > 200) return BadRequestProblem("A title cannot exceed 200 characters.");

        if (r.StartsOn.Year < 2000 || r.StartsOn.Year > 2100) return BadRequestProblem("Choose the day the event starts.");
        if (r.EndsOn < r.StartsOn) return BadRequestProblem("The event ends before it starts.");
        if (r.EndsOn.DayNumber - r.StartsOn.DayNumber > MaxEventSpanDays)
            return BadRequestProblem($"An event can run for at most {MaxEventSpanDays} days.", "A longer stretch is a term — set it on the term dates instead.");

        var startTime = SchoolEventMapping.ParseTime(r.StartTime);
        var endTime = SchoolEventMapping.ParseTime(r.EndTime);
        if (!string.IsNullOrWhiteSpace(r.StartTime) && startTime == null) return BadRequestProblem("Use a start time like 08:30.");
        if (!string.IsNullOrWhiteSpace(r.EndTime) && endTime == null) return BadRequestProblem("Use an end time like 17:00.");
        if (endTime != null && startTime == null) return BadRequestProblem("An end time needs a start time.", "Leave both empty for an all-day event.");
        if (startTime != null && endTime != null && r.EndsOn == r.StartsOn && endTime <= startTime)
            return BadRequestProblem("The event ends before it starts.", "On a one-day event the end time must be after the start time.");

        const EventAudience known = EventAudience.Staff | EventAudience.Students | EventAudience.Guardians | EventAudience.Public;
        if (r.Audience == EventAudience.None || (r.Audience & ~known) != 0) return BadRequestProblem("Choose who the event is for.");

        if (r.BranchId is { } eventBranch && eventBranch != branchId)
            return BadRequestProblem("An event belongs to this branch or to every branch.");

        string? category = null;
        if (!string.IsNullOrWhiteSpace(r.Category))
        {
            var settings = await CalendarSettingsStore.GetAsync(Db, organizationId);
            category = CalendarSettingsStore.MatchCategory(settings, r.Category);
            if (category == null) return BadRequestProblem($"\"{r.Category.Trim()}\" is not one of the calendar's categories.", "Add it under Calendar settings first, or choose another.");
        }

        var classNames = new List<string>();
        foreach (var raw in r.ClassNames ?? new List<string>())
        {
            var c = (raw ?? string.Empty).Trim();
            if (c.Length == 0) continue;
            if (c.Length > 60) return BadRequestProblem("A class name cannot exceed 60 characters.");
            if (!classNames.Any(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase))) classNames.Add(c);
        }
        if (classNames.Count > MaxClassNames) return BadRequestProblem($"At most {MaxClassNames} classes.");

        var userIds = (r.ResponsibleUserIds ?? new List<Guid>()).Where(x => x != Guid.Empty).Distinct().ToList();
        if (userIds.Count > 50) return BadRequestProblem("At most 50 people may be named responsible.");
        if (userIds.Count > 0)
        {
            var found = await Db.Users.IgnoreQueryFilters()
                .CountAsync(u => userIds.Contains(u.Id) && u.OrganizationId == organizationId && u.IsActive);
            if (found != userIds.Count) return BadRequestProblem("Somebody named responsible is not an active member of staff here.");
        }

        var departmentIds = (r.ResponsibleDepartmentIds ?? new List<Guid>()).Where(x => x != Guid.Empty).Distinct().ToList();
        if (departmentIds.Count > 50) return BadRequestProblem("At most 50 departments may be named responsible.");
        if (departmentIds.Count > 0)
        {
            var found = await Db.Departments.IgnoreQueryFilters()
                .CountAsync(d => departmentIds.Contains(d.Id) && d.OrganizationId == organizationId);
            if (found != departmentIds.Count) return BadRequestProblem("A department named responsible does not exist here.");
        }

        e.BranchId = r.BranchId;
        e.Title = title;
        e.Description = Clean(r.Description, 2000);
        e.StartsOn = r.StartsOn;
        e.EndsOn = r.EndsOn;
        e.StartTime = startTime;
        e.EndTime = endTime;
        e.Category = category;
        e.Audience = r.Audience;
        e.ClassNames = classNames.ToArray();
        e.Location = Clean(r.Location, 200);
        e.ResponsibleText = Clean(r.ResponsibleText, 300);
        e.ResponsibleUserIds = userIds.ToArray();
        e.ResponsibleDepartmentIds = departmentIds.ToArray();
        return null;
    }

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

    /// <summary>The caller and their departments — the two facts the visibility rule reads.</summary>
    private async Task<(Guid Me, IReadOnlyCollection<Guid> Departments)> ViewerAsync()
    {
        var me = CurrentUserId();
        var departments = await Db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == me).Select(u => u.DepartmentIds).FirstOrDefaultAsync();
        return (me, departments ?? Array.Empty<Guid>());
    }

    private async Task<TimeZoneInfo> ZoneAsync(Guid branchId)
        => AppointmentScheduling.ResolveTimeZone(await Db.Branches.IgnoreQueryFilters().Where(b => b.Id == branchId).Select(b => b.Timezone).FirstOrDefaultAsync());

    /// <summary>A branch-local wall-clock time as UTC; a time that does not exist locally is read as UTC rather than throwing.</summary>
    internal static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        try { return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone); }
        catch (ArgumentException) { return DateTime.SpecifyKind(local, DateTimeKind.Utc); }
    }

    private static Guid? NullIfEmpty(Guid id) => id == Guid.Empty ? null : id;

    private IActionResult EventNotFound() => NotFoundProblem("Event not found");
}
