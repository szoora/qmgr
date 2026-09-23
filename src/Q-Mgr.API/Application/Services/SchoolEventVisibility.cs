using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Calendar;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// WHO SEES A SCHOOL EVENT — the one home for it (plan TERM_PROGRAMME_CALENDAR_AND_GATES §3, decision D5, 2026-09-23).
/// The calendar page, a single event, My School Day, the portal and the private .ics feed all ask here; a second copy
/// of this rule beside any of them is how an event meant for guardians one day reaches a teacher's phone.
///
/// <list type="bullet">
/// <item>A holder of <c>calendar.manage</c> sees EVERY event of the branch — they keep the calendar, and an event
/// they cannot see is one they cannot correct. That is the <see cref="CanSee"/> override.</item>
/// <item>Anybody else sees an event whose audience includes <b>Staff</b>, or one they are <b>responsible</b> for —
/// named in person, or through one of their departments.</item>
/// <item>An event for Students, Guardians or the Public ONLY is not shown to a non-manager at all (D5: those audiences
/// are stored so nothing is re-entered on the day they get a way in, and shown to nobody until then). Public events
/// reach the signage "Coming up" zone, which is the one anonymous reader.</item>
/// </list>
///
/// <see cref="IsPersonal"/> is the rule WITHOUT the manager override. My School Day, the portal and the feed use it:
/// they are about the person, and a Director of Studies' own day is not every event of the school.
///
/// Filtering is in memory over a date-bounded set, deliberately: it keeps the rule readable as one expression and
/// out of an EF translation of flag arithmetic over arrays, and the sets are small (a term's events).
/// </summary>
public static class SchoolEventVisibility
{
    public static bool CanSee(SchoolEvent e, Guid userId, IReadOnlyCollection<Guid> departmentIds, bool managesCalendar)
        => managesCalendar || IsPersonal(e, userId, departmentIds);

    public static bool IsPersonal(SchoolEvent e, Guid userId, IReadOnlyCollection<Guid> departmentIds)
        => (e.Audience & EventAudience.Staff) == EventAudience.Staff
           || (userId != Guid.Empty && e.ResponsibleUserIds.Contains(userId))
           || (departmentIds.Count > 0 && e.ResponsibleDepartmentIds.Any(d => departmentIds.Contains(d)));

    /// <summary>Only an event the public was meant to see may leave the building on a signage screen.</summary>
    public static bool IsPublic(SchoolEvent e) => (e.Audience & EventAudience.Public) == EventAudience.Public;

    /// <summary>An event for this branch: its own, or one for every branch (null).</summary>
    public static bool ForBranch(SchoolEvent e, Guid branchId) => e.BranchId == null || e.BranchId == branchId;

    /// <summary>The calendar's order: first day, all-day before timed, then time, then title.</summary>
    public static IEnumerable<SchoolEvent> Ordered(IEnumerable<SchoolEvent> events)
        => events.OrderBy(e => e.StartsOn).ThenBy(e => e.StartTime.HasValue).ThenBy(e => e.StartTime).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase);
}

/// <summary>SchoolEvent → SchoolEventDto, once. Names come from <see cref="StaffLookups.LoadNamesAsync"/> (PersonNames-formatted).</summary>
public static class SchoolEventMapping
{
    public static string? Time(TimeOnly? t) => t?.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static TimeOnly? ParseTime(string? hhmm)
        => string.IsNullOrWhiteSpace(hhmm) ? null
            : TimeOnly.TryParseExact(hhmm.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    public static SchoolEventDto ToDto(SchoolEvent e, StaffPerformanceMapping.NameLookup names, bool canEdit) => new()
    {
        Id = e.Id,
        BranchId = e.BranchId,
        Title = e.Title,
        Description = e.Description,
        StartsOn = e.StartsOn,
        EndsOn = e.EndsOn,
        StartTime = Time(e.StartTime),
        EndTime = Time(e.EndTime),
        Category = e.Category,
        Audience = e.Audience,
        ClassNames = e.ClassNames.ToList(),
        Location = e.Location,
        ResponsibleText = e.ResponsibleText,
        ResponsibleUserIds = e.ResponsibleUserIds.ToList(),
        ResponsibleNames = e.ResponsibleUserIds.Select(id => names[id]).Where(n => n.Length > 0).ToList(),
        ResponsibleDepartmentIds = e.ResponsibleDepartmentIds.ToList(),
        DutyId = e.DutyId,
        SeriesId = e.SeriesId,
        SeriesName = e.SeriesName,
        ImportJobId = e.ImportJobId,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
        CanEdit = canEdit
    };

    /// <summary>
    /// The anonymous shape, for a signage screen: what is on, when and where. Nobody's name, no department, no
    /// description, no link to a duty or an import — a public screen in a foyer is read by strangers.
    /// </summary>
    public static SchoolEventDto ToPublicDto(SchoolEvent e) => new()
    {
        Id = e.Id,
        BranchId = e.BranchId,
        Title = e.Title,
        StartsOn = e.StartsOn,
        EndsOn = e.EndsOn,
        StartTime = Time(e.StartTime),
        EndTime = Time(e.EndTime),
        Category = e.Category,
        Audience = EventAudience.Public,
        ClassNames = e.ClassNames.ToList(),
        Location = e.Location
    };

    /// <summary>Every id a set of events needs a name for.</summary>
    public static IEnumerable<Guid?> NameIds(IEnumerable<SchoolEvent> events)
        => events.SelectMany(e => e.ResponsibleUserIds).Select(id => (Guid?)id);
}

/// <summary>
/// The queries every reader of events shares: an organization's events for one branch over a date range, and the
/// PERSONAL subset of them (<see cref="SchoolEventVisibility.IsPersonal"/>) with names resolved. My School Day, the
/// portal and the feed call this rather than each writing the overlap predicate again.
/// </summary>
public static class SchoolEventQueries
{
    /// <summary>
    /// Events of <paramref name="organizationId"/> for <paramref name="branchId"/> (or every branch) that overlap
    /// [<paramref name="from"/>, <paramref name="to"/>]. The organization is filtered EXPLICITLY, never left to the
    /// tenant query filter, because the anonymous feed and a SuperAdmin both run with that filter off.
    /// </summary>
    public static Task<List<SchoolEvent>> InRangeAsync(QMgrDbContext db, Guid organizationId, Guid? branchId,
        DateOnly from, DateOnly to, CancellationToken ct = default)
        => db.SchoolEvents.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.OrganizationId == organizationId && e.IsActive
                            && (e.BranchId == null || branchId == null || e.BranchId == branchId)
                            && e.StartsOn <= to && e.EndsOn >= from)
                .OrderBy(e => e.StartsOn)
                .Take(2000)
            .ToListAsync(ct);

    /// <summary>The caller's own events in a range, as DTOs — staff-audience or theirs to run. No manager override.</summary>
    public static async Task<List<SchoolEventDto>> PersonalAsync(QMgrDbContext db, Guid organizationId, Guid branchId,
        Guid userId, IReadOnlyCollection<Guid> departmentIds, DateOnly from, DateOnly to, bool canEdit, int take, CancellationToken ct = default)
    {
        var events = await InRangeAsync(db, organizationId, branchId, from, to, ct);
        var mine = SchoolEventVisibility.Ordered(events.Where(e => SchoolEventVisibility.IsPersonal(e, userId, departmentIds))).Take(take).ToList();
        if (mine.Count == 0) return new();
        var names = await StaffLookups.LoadNamesAsync(db, SchoolEventMapping.NameIds(mine), ct);
        return mine.Select(e => SchoolEventMapping.ToDto(e, names, canEdit)).ToList();
    }
}
