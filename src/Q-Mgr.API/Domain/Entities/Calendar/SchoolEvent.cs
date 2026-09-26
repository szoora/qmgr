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

    // ---- The staff audience (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING §5.1, 2026-09-26) ----------------------
    // Who the event is FOR among staff, and therefore who is told about it and whose "My events" it is. Read through
    // StaffAudienceRule (Shared) and StaffAudience (API) only — a second copy of "is this person in this audience" is
    // how a notice and the calendar came to disagree about who a staff group contains. Columns on this row rather than
    // a table: the lists are small, never queried across events by member, and belong to exactly one event.

    /// <summary>
    /// Every member of staff. Only meaningful while <see cref="Audience"/> includes Staff. The migration set it true on
    /// every existing staff event, which is exactly what the Staff box meant before audiences could be narrowed.
    /// </summary>
    public bool AllStaff { get; set; } = true;

    /// <summary>Staff group NAMES from the school's own list (matched with <c>StaffGroups.Key</c>). Ignored when <see cref="AllStaff"/>.</summary>
    public string[] AudienceStaffGroups { get; set; } = Array.Empty<string>();

    /// <summary>Role codes ("admin", "head-teacher"). How "only the administrators" is said.</summary>
    public string[] AudienceRoleCodes { get; set; } = Array.Empty<string>();

    public Guid[] AudienceDepartmentIds { get; set; } = Array.Empty<Guid>();

    public Guid[] AudienceUserIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// "Only its audience can see it" — a disciplinary panel, an interview. Off, the event is on the whole-school
    /// calendar for any member of staff who chooses to look; on, only its audience, the people responsible and the
    /// calendar's keepers see it at all.
    /// </summary>
    public bool AudienceOnly { get; set; }

    /// <summary>Attendance is required of the audience (Arbor's "Required"); shown on My School Day. Not a register.</summary>
    public bool AttendanceRequired { get; set; }

    // ---- Lifecycle, notices and reminders ------------------------------------------------------------------------

    /// <summary>Cancelled events stay on the calendar struck through, so "was it cancelled or did I miss it" has an answer.</summary>
    public SchoolEventStatus Status { get; set; } = SchoolEventStatus.Scheduled;

    [MaxLength(300)]
    public string? CancelReason { get; set; }
    public DateTime? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }

    /// <summary>
    /// Bumped on a MATERIAL change — dates, times, venue, audience, status. A typo in the title is not one. It is the
    /// iCalendar SEQUENCE (RFC 5546) and the notice claim: a notice goes out when <see cref="NotifiedVersion"/> is
    /// below it, claimed by one conditional UPDATE so a retried or doubled save never tells anybody twice.
    /// </summary>
    public int Version { get; set; } = 1;

    public int NotifiedVersion { get; set; }

    /// <summary>The highest reminder stage sent, claimed with a conditional UPDATE (the duty ladder's rule). Reset when the event moves.</summary>
    public int ReminderStage { get; set; }

    /// <summary>The school's default ladder reminds; an event may switch it off.</summary>
    public bool RemindersOn { get; set; } = true;

    /// <summary>
    /// Set when somebody edits an IMPORTED event by hand. A re-import of the document then leaves the fields alone and
    /// says so, instead of silently putting back what the document said.
    /// </summary>
    public DateTime? EditedByHandAt { get; set; }

    /// <summary>How a series repeats, in words ("Weekly on Tuesday, term time, until 5 Dec 2026"). Rows share <see cref="SeriesId"/>.</summary>
    [MaxLength(160)]
    public string? Recurrence { get; set; }

    /// <summary>A Library document attached to the event (an agenda, a letter). Its own sharing rules decide who opens it.</summary>
    public Guid? LibraryDocumentId { get; set; }

    /// <summary>The dialog's idempotency key: a double press of "Add event" makes one event, not two.</summary>
    public Guid? ClientRequestId { get; set; }

    /// <summary>
    /// PostgreSQL's xmin, mapped as the row version (Npgsql; no column is added). Two people editing one event: the
    /// second save is refused with the first person's version rather than silently overwriting it.
    /// </summary>
    public uint RowVersion { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
}
