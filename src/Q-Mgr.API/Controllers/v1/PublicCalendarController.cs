using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The two anonymous readers of the calendar (plan TERM_PROGRAMME_CALENDAR_AND_GATES §9, 2026-09-23):
///
/// <list type="bullet">
/// <item><b>The private feed</b>, <c>/api/v1/public/calendar/{secret}.ics</c>. A calendar application cannot sign in,
/// so the link IS the credential: 160 random bits, of which the server keeps only the SHA-256. Replacing or removing
/// the link on the calendar page makes this answer 404 at once — the hash is read on every request, never cached.
/// An unknown secret and an inactive person read the same.</item>
/// <item><b>The signage "Coming up" zone</b>. Public events only — the audience the school marked Public — in the
/// slim shape <see cref="SchoolEventMapping.ToPublicDto"/>, which carries no names.</item>
/// </list>
///
/// No tenant is resolved on either (an anonymous request has none), so the tenant query filter is off and every
/// query names its organization explicitly.
/// </summary>
[ApiController]
[Route("api/v1/public")]
[AllowAnonymous]
public partial class PublicCalendarController : ControllerBase
{
    /// <summary>The feed's window: recent history for context, a school year ahead.</summary>
    private const int FeedDaysBack = 60;
    private const int FeedDaysAhead = 365;

    private readonly QMgrDbContext _db;
    private readonly QMgr.Infrastructure.Services.IStaffPerformancePolicyService _policy;
    private readonly ILogger<PublicCalendarController> _logger;

    public PublicCalendarController(QMgrDbContext db, QMgr.Infrastructure.Services.IStaffPerformancePolicyService policy, ILogger<PublicCalendarController> logger)
    {
        _db = db;
        _policy = policy;
        _logger = logger;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{20,64}$")]
    private static partial Regex SecretShape();

    /// <param name="categories">Only these calendar categories, comma-separated ("Academic,Meetings"). Empty = every one.
    /// A phone can subscribe to the same link twice with different categories, which is how "subscribe per category"
    /// works without a second secret.</param>
    [HttpGet("calendar/{token}.ics")]
    [Produces("text/calendar")]
    public async Task<IActionResult> GetFeed(string token, [FromQuery] string? categories = null)
    {
        if (string.IsNullOrEmpty(token) || !SecretShape().IsMatch(token)) return NotFound();

        var hash = CalendarController.FeedTokenHash(token);
        var user = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.CalendarFeedTokenHash == hash)
            .Select(u => new { u.Id, u.OrganizationId, u.IsActive, u.AssignedBranchId })
            .FirstOrDefaultAsync();
        if (user == null || !user.IsActive) return NotFound();

        // The person's branch: the one they are assigned to, else the organization's first active branch.
        var branch = await _db.Branches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.OrganizationId == user.OrganizationId && b.IsActive)
            .OrderBy(b => b.Id == user.AssignedBranchId ? 0 : 1).ThenBy(b => b.CreatedAt)
            .Select(b => new { b.Id, b.Timezone })
            .FirstOrDefaultAsync();
        var zone = AppointmentScheduling.ResolveTimeZone(branch?.Timezone);
        var orgName = await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Id == user.OrganizationId).Select(o => o.Name).FirstOrDefaultAsync() ?? "School";

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        var from = today.AddDays(-FeedDaysBack);
        var to = today.AddDays(FeedDaysAhead);

        // Everything personal: the events this person is in the audience of or responsible for — the same rule My School
        // Day reads (StaffAudience.IsMine), without the calendar-keeper's whole-school view.
        var viewer = await StaffAudience.MemberAsync(_db, _policy, user.OrganizationId, user.Id);
        var wanted = (categories ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var events = await SchoolEventQueries.InRangeAsync(_db, user.OrganizationId, branch?.Id, from, to);
        var mine = SchoolEventVisibility.Ordered(events.Where(e => StaffAudience.IsMine(e, viewer, user.Id)
                                                                  && (wanted.Count == 0 || (e.Category != null && wanted.Contains(e.Category))))).ToList();

        var fromUtc = CalendarController.LocalToUtc(from.ToDateTime(TimeOnly.MinValue), zone);
        var toUtc = CalendarController.LocalToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var duties = branch == null
            ? new List<QMgr.Domain.Entities.Staff.StaffDuty>()
            : await CalendarController.MyDutiesQuery(_db.StaffDuties.AsNoTracking(), user.OrganizationId, branch.Id, user.Id, fromUtc, toUtc)
                .OrderBy(d => d.StartsAt).Take(1000).ToListAsync();

        var ics = new IcsWriter($"{orgName} — my calendar");
        foreach (var e in mine) IcsEvents.Write(ics, e, zone);

        // A duty that IS an event on this feed is written once, as the event (B1's rule, in the feed). A category filter
        // means "these kinds of school event", which a duty is not.
        var linked = mine.Where(e => e.DutyId.HasValue).Select(e => e.DutyId!.Value).ToHashSet();
        if (wanted.Count == 0)
            foreach (var d in duties.Where(d => !linked.Contains(d.Id)))
            {
                var category = d.Kind == DutyKind.Rota ? "Duty" : "Meeting";
                var modified = d.UpdatedAt ?? d.CreatedAt;
                ics.TimedEvent($"{d.Id}@qmgr", modified, d.StartsAt, d.EndsAt, d.Title, d.Location, d.Description, category, modified);
            }

        // Calendar applications poll; a short private cache spares the database a burst of identical polls.
        Response.Headers.CacheControl = "private, max-age=300";
        return Content(ics.Finish(), "text/calendar; charset=utf-8");
    }

    /// <summary>The signage "Coming up" zone: PUBLIC events of this branch from today, soonest first.</summary>
    [HttpGet("branches/{branchId:guid}/events/upcoming")]
    [ProducesResponseType(typeof(List<SchoolEventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUpcoming(Guid branchId, [FromQuery] int days = 14, [FromQuery] int take = 8)
    {
        days = Math.Clamp(days, 1, 60);
        take = Math.Clamp(take, 1, 50);

        var branch = await _db.Branches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.Id == branchId && b.IsActive)
            .Select(b => new { b.OrganizationId, b.Timezone })
            .FirstOrDefaultAsync();
        if (branch == null) return NotFound();

        var zone = AppointmentScheduling.ResolveTimeZone(branch.Timezone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        var events = await SchoolEventQueries.InRangeAsync(_db, branch.OrganizationId, branchId, today, today.AddDays(days - 1));

        return Ok(SchoolEventVisibility.Ordered(events.Where(SchoolEventVisibility.IsPublic))
            .Take(take)
            .Select(SchoolEventMapping.ToPublicDto)
            .ToList());
    }
}
