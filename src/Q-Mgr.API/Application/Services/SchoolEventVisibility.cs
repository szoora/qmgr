using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Calendar;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// WHO SEES A SCHOOL EVENT — the one home for it (plan TERM_PROGRAMME_CALENDAR_AND_GATES §3, D5; reworked by
/// CALENDAR_AUDIENCES_AND_IMPORT_ROUTING §5.1, E3/E4, 2026-09-26). The calendar page, a single event, My School Day,
/// the portal, the private feed and the reminders all ask here.
///
/// <para><b>Two layers.</b> The AUDIENCE decides who is told and whose <b>My events</b> it is
/// (<see cref="StaffAudience.IsMine"/>: in the staff audience, named responsible, or in a responsible department).
/// The SCHOOL CALENDAR stays readable by every member of staff who asks for it — the printed term programme is pinned
/// up for everyone anyway — EXCEPT an event marked <see cref="SchoolEvent.AudienceOnly"/> (a disciplinary panel, an
/// interview), which only its audience, the people responsible and the calendar's keepers see at all.</para>
///
/// <para>The keepers' override (<c>calendar.manage</c>) applies to the WHOLE-SCHOOL view only: in "My events" a Director
/// of Studies sees their own events, like anybody else, and their own day is not every event of the school.</para>
///
/// Filtering is in memory over a date-bounded set: the membership test needs a person's staff group through
/// <c>GroupFor</c>, which SQL cannot compute, and a term's events are hundreds, not millions. Nothing is truncated
/// BEFORE the filter any more (B8).
/// </summary>
public static class SchoolEventVisibility
{
    /// <summary>Does the event appear on this person's calendar in this scope?</summary>
    public static bool CanSee(SchoolEvent e, AudienceMemberDto? viewer, Guid viewerId, bool managesCalendar, bool wholeSchool)
        => wholeSchool
            ? managesCalendar || StaffAudience.IsMine(e, viewer, viewerId) || !e.AudienceOnly
            : StaffAudience.IsMine(e, viewer, viewerId);

    /// <summary>May this person open the event by id — whatever scope they are browsing in.</summary>
    public static bool CanOpen(SchoolEvent e, AudienceMemberDto? viewer, Guid viewerId, bool managesCalendar)
        => managesCalendar || !e.AudienceOnly || StaffAudience.IsMine(e, viewer, viewerId);

    /// <summary>Only an event the public was meant to see, and still on, may leave the building on a signage screen.</summary>
    public static bool IsPublic(SchoolEvent e)
        => (e.Audience & EventAudience.Public) == EventAudience.Public && !e.AudienceOnly && e.Status == SchoolEventStatus.Scheduled;

    /// <summary>An event for this branch: its own, or one for every branch (null).</summary>
    public static bool ForBranch(SchoolEvent e, Guid branchId) => e.BranchId == null || e.BranchId == branchId;

    /// <summary>The calendar's order: first day, all-day before timed, then time, then title.</summary>
    public static IEnumerable<SchoolEvent> Ordered(IEnumerable<SchoolEvent> events)
        => events.OrderBy(e => e.StartsOn).ThenBy(e => e.StartTime.HasValue).ThenBy(e => e.StartTime).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Everything a set of events needs to be shown: names, departments, role names, attachment titles, registers.</summary>
public sealed class SchoolEventLookups
{
    public StaffPerformanceMapping.NameLookup Names { get; init; } = StaffPerformanceMapping.NameLookup.Empty;
    public IReadOnlyDictionary<Guid, string> Departments { get; init; } = new Dictionary<Guid, string>();
    public IReadOnlyDictionary<string, string> Roles { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<Guid, string> Documents { get; init; } = new Dictionary<Guid, string>();
    /// <summary>Linked duty → who takes its register.</summary>
    public IReadOnlyDictionary<Guid, Guid[]> DutyRecorders { get; init; } = new Dictionary<Guid, Guid[]>();

    public static async Task<SchoolEventLookups> LoadAsync(QMgrDbContext db, Guid organizationId, IReadOnlyCollection<SchoolEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return new SchoolEventLookups();
        var names = await StaffLookups.LoadNamesAsync(db, events.SelectMany(e => e.ResponsibleUserIds.Concat(e.AudienceUserIds)).Select(id => (Guid?)id), ct);

        var deptIds = events.SelectMany(e => e.AudienceDepartmentIds.Concat(e.ResponsibleDepartmentIds)).Distinct().ToList();
        var departments = deptIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.Departments.IgnoreQueryFilters().AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var roleCodes = events.SelectMany(e => e.AudienceRoleCodes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (roleCodes.Count > 0)
            foreach (var r in await db.Roles.IgnoreQueryFilters().AsNoTracking()
                         .Where(r => roleCodes.Contains(r.Code) && (r.OrganizationId == null || r.OrganizationId == organizationId))
                         .Select(r => new { r.Code, r.Name }).ToListAsync(ct))
                roles.TryAdd(r.Code, r.Name);

        var docIds = events.Where(e => e.LibraryDocumentId.HasValue).Select(e => e.LibraryDocumentId!.Value).Distinct().ToList();
        var documents = docIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.MediaContents.IgnoreQueryFilters().AsNoTracking()
                .Where(m => docIds.Contains(m.Id) && m.OrganizationId == organizationId && m.IsActive).ToDictionaryAsync(m => m.Id, m => m.Name, ct);

        var dutyIds = events.Where(e => e.DutyId.HasValue).Select(e => e.DutyId!.Value).Distinct().ToList();
        var recorders = dutyIds.Count == 0 ? new Dictionary<Guid, Guid[]>()
            : await db.StaffDuties.AsNoTracking().Where(d => dutyIds.Contains(d.Id) && d.IsActive).ToDictionaryAsync(d => d.Id, d => d.RecorderUserIds, ct);

        return new SchoolEventLookups { Names = names, Departments = departments, Roles = roles, Documents = documents, DutyRecorders = recorders };
    }

    public string DescribeAudience(SchoolEvent e)
        => StaffAudienceRule.Describe(StaffAudience.Of(e),
            code => Roles.TryGetValue(code, out var n) ? n : code,
            id => Departments.TryGetValue(id, out var n) ? n : null,
            id => Names.Optional(id));
}

/// <summary>SchoolEvent → SchoolEventDto, once.</summary>
public static class SchoolEventMapping
{
    public static string? Time(TimeOnly? t) => t?.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static TimeOnly? ParseTime(string? hhmm)
        => string.IsNullOrWhiteSpace(hhmm) ? null
            : TimeOnly.TryParseExact(hhmm.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <param name="canManageDuties">The caller holds staff.duties.manage: they may open any meeting's register.</param>
    public static SchoolEventDto ToDto(SchoolEvent e, SchoolEventLookups lk, Guid viewerId, bool isMine, bool canEdit, bool canManageDuties) => new()
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
        ResponsibleNames = e.ResponsibleUserIds.Select(id => lk.Names[id]).Where(n => n.Length > 0).ToList(),
        ResponsibleDepartmentIds = e.ResponsibleDepartmentIds.ToList(),
        DutyId = e.DutyId,
        SeriesId = e.SeriesId,
        SeriesName = e.SeriesName,
        ImportJobId = e.ImportJobId,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
        CanEdit = canEdit,
        StaffAudience = StaffAudience.Of(e),
        StaffAudienceText = (e.Audience & EventAudience.Staff) == EventAudience.Staff ? lk.DescribeAudience(e) : null,
        AudienceOnly = e.AudienceOnly,
        AttendanceRequired = e.AttendanceRequired,
        IsMine = isMine,
        Status = e.Status,
        CancelReason = e.CancelReason,
        CancelledAt = e.CancelledAt,
        Version = e.Version,
        RemindersOn = e.RemindersOn,
        EditedByHandAt = e.EditedByHandAt,
        Recurrence = e.Recurrence,
        LibraryDocumentId = e.LibraryDocumentId,
        LibraryDocumentTitle = e.LibraryDocumentId is { } doc && lk.Documents.TryGetValue(doc, out var title) ? title : null,
        RowVersion = e.RowVersion,
        CanOpenRegister = e.DutyId is { } dutyId && lk.DutyRecorders.TryGetValue(dutyId, out var recorders)
                          && (canManageDuties || recorders.Contains(viewerId))
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
        Location = e.Location,
        Status = e.Status
    };
}

/// <summary>
/// The queries every reader of events shares: an organization's events for one branch over a date range, and the
/// PERSONAL subset of them with everything a page needs. My School Day, the portal and the feed call this rather than
/// each writing the overlap predicate again.
/// </summary>
public static class SchoolEventQueries
{
    /// <summary>A ceiling far above any real term, so a runaway import cannot make a page load a million rows.</summary>
    public const int Ceiling = 10000;

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
                .Take(Ceiling)
            .ToListAsync(ct);

    /// <summary>The caller's own events in a range, as DTOs. No manager override: this is about the person.</summary>
    public static async Task<List<SchoolEventDto>> PersonalAsync(QMgrDbContext db, IStaffPerformancePolicyService policy, Guid organizationId, Guid branchId,
        Guid userId, DateOnly from, DateOnly to, bool canEdit, bool canManageDuties, int take, CancellationToken ct = default)
    {
        var viewer = await StaffAudience.MemberAsync(db, policy, organizationId, userId, ct);
        var events = await InRangeAsync(db, organizationId, branchId, from, to, ct);
        var mine = SchoolEventVisibility.Ordered(events.Where(e => StaffAudience.IsMine(e, viewer, userId))).Take(take).ToList();
        if (mine.Count == 0) return new();
        var lk = await SchoolEventLookups.LoadAsync(db, organizationId, mine, ct);
        return mine.Select(e => SchoolEventMapping.ToDto(e, lk, userId, true, canEdit, canManageDuties)).ToList();
    }
}
