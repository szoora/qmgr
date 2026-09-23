using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Calendar;

/// <summary>
/// One school event: an activity on the term calendar, an item of a programme, a national day
/// (plan TERM_PROGRAMME_CALENDAR_AND_GATES §3, decision D1, 2026-09-23).
///
/// WHY A TABLE AND NOT A KIND OF DUTY. Every <c>StaffDuty</c> carries a <c>ParameterId</c> and every register,
/// reminder and scoring query assumes staff are expected at it and marked on it. Independence Day, the Christmas
/// carols and "lights out" have none of that; as duties they would be unscored, audience-less rows threaded through
/// every one of those queries. An event also has a life of its own: an audience beyond staff, an all-day form, and
/// a public face on signage. A meeting that IS a staff duty stays a duty; the event points at it (<see cref="DutyId"/>)
/// so the calendar and My School Day show one thing, not two.
///
/// Organization-scoped with a tenant query filter (like <c>StaffNotice</c>); <see cref="BranchId"/> null means every
/// branch. Times are LOCAL time of day, because a school event happens at "8:30am" in the school's own zone and an
/// all-day event (iCalendar DATE) has no instant at all.
/// </summary>
public class SchoolEvent : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }

    /// <summary>Local time of day. Null = all day.</summary>
    public TimeOnly? StartTime { get; set; }

    /// <summary>Local time of day. Null with a start time = "onwards".</summary>
    public TimeOnly? EndTime { get; set; }

    /// <summary>A name from the organization's calendar categories. Data, not an enum: it carries no behaviour.</summary>
    [MaxLength(60)]
    public string? Category { get; set; }

    public EventAudience Audience { get; set; } = EventAudience.Staff;

    /// <summary>Classes the event is for ("S.4", "S.5", "S.6"). Empty = the whole school.</summary>
    public string[] ClassNames { get; set; } = Array.Empty<string>();

    [MaxLength(200)]
    public string? Location { get; set; }

    /// <summary>Who is responsible, exactly as the document or the editor wrote it.</summary>
    [MaxLength(300)]
    public string? ResponsibleText { get; set; }

    public Guid[] ResponsibleUserIds { get; set; } = Array.Empty<Guid>();
    public Guid[] ResponsibleDepartmentIds { get; set; } = Array.Empty<Guid>();

    /// <summary>The Session duty this event is, when it is also a staff meeting.</summary>
    public Guid? DutyId { get; set; }

    /// <summary>Groups the items of one programme so it can be printed and undone as one.</summary>
    public Guid? SeriesId { get; set; }

    [MaxLength(200)]
    public string? SeriesName { get; set; }

    /// <summary>The programme import that created it: the undo handle.</summary>
    public Guid? ImportJobId { get; set; }

    /// <summary>
    /// Recognises the same line on a re-import (document kind + normalised title + first date). A re-import of an
    /// unchanged document creates nothing; a changed one updates the row and says which fields moved.
    /// </summary>
    [MaxLength(200)]
    public string? SourceKey { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
}
