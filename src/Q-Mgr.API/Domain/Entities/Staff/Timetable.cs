using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// One version of a branch's timetable for a term (duty rota plan §3.2, §6). A table because a Draft and a Published
/// version must coexist while the timetable master edits — a status column on lessons cannot say "the published one".
///
/// A Published timetable is immutable: a change is a new Draft copied from it, published over it. At most one
/// Published version covers any date of a branch; publish enforces that under an advisory lock, backed by a partial
/// unique index on the start date (overlap itself cannot be a btree index, and a gist exclusion constraint would
/// need an extension this project does not install).
/// </summary>
public class Timetable : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The policy period (term) key it is for, e.g. "2026-T3".</summary>
    public string PeriodKey { get; set; } = string.Empty;

    /// <summary>Teaching days in one cycle: the bell schedule's teaching weekdays × 1 (a week) or × 2 (an A/B cycle). Fixed at creation.</summary>
    public int CycleDays { get; set; } = 5;

    public TimetableStatus Status { get; set; } = TimetableStatus.Draft;
    public DateTime? PublishedAt { get; set; }
    public Guid? PublishedByUserId { get; set; }

    /// <summary>The branch-local dates the version is in force. Cycle day 1 is the teaching weekday on or after <see cref="EffectiveFrom"/>'s week start.</summary>
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly EffectiveTo { get; set; }

    /// <summary>
    /// The integrity sweep's last reported set of hard issues, one short key each (plan §6.3). Stored as the set rather
    /// than one hash so the sweep can say how many are NEW: a clash fixed and another appearing is still one new clash.
    /// </summary>
    public string[] ReportedIssueKeys { get; set; } = Array.Empty<string>();

    public ICollection<TimetableLesson> Lessons { get; set; } = new List<TimetableLesson>();
}

/// <summary>
/// One teacher teaching one class one subject in one period of the cycle. One row per TEACHER so a unique index can
/// refuse a teacher double-booking in the database, even when two masters' checks both passed.
///
/// A joint lesson — an elective taught to S5A and S5B together, or two teachers co-teaching — is several rows sharing
/// a <see cref="GroupId"/>, and is not a class or teacher clash with itself.
/// </summary>
public class TimetableLesson : BaseAuditableEntity
{
    public Guid TimetableId { get; set; }

    /// <summary>1..Timetable.CycleDays.</summary>
    public int CycleDay { get; set; }

    /// <summary>A lesson period's key in the bell schedule ("P3").</summary>
    public string PeriodKey { get; set; } = string.Empty;

    /// <summary>The class as configured, and its match form — <c>Trim().ToLowerInvariant()</c>, the rule everywhere a class is matched.</summary>
    public string ClassName { get; set; } = string.Empty;
    public string ClassNameNormalized { get; set; } = string.Empty;

    public Guid SubjectId { get; set; }
    public Guid TeacherUserId { get; set; }

    public string? Room { get; set; }
    public string? RoomNormalized { get; set; }

    public Guid? GroupId { get; set; }

    public Timetable Timetable { get; set; } = null!;
    public Subject Subject { get; set; } = null!;
}
