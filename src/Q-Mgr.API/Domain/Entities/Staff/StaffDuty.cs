using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// A meeting, an exam session, a prep slot, a lesson: when, where, who is expected, and — the
/// minutes-taker delegation — who may take the register. A table because it has its own lifecycle
/// (scheduled, register open, closed), is foreign-keyed by records, and reminders and reports hang
/// off it. Expected attendees and named recorders are <c>uuid[]</c> columns, not join tables;
/// nothing is stored per link. Minutes are a Library document (<see cref="MinutesMediaContentId"/>),
/// which is what lets them also go on a playlist or a share link with no new code.
///
/// Taking the register is authorised by <c>me ∈ RecorderUserIds</c> (or staff.duties.manage), and
/// the records it writes are for <see cref="ExpectedUserIds"/> only. A teacher named recorder for
/// Tuesday's staff meeting can mark the Director of Studies absent from that meeting and nothing
/// else. Delegation is not scope.
///
/// No recurrence rule in v1: a "duplicate to next week" action covers the real use, and an RRULE
/// column is the appended upgrade if a school asks.
///
/// Branch-scoped with no global EF query filter; every by-ID action calls VerifyBranchOwnership.
/// </summary>
public class StaffDuty : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid ParameterId { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }

    /// <summary>Null means every active staff member in the branch is expected.</summary>
    public Guid[]? ExpectedUserIds { get; set; }

    /// <summary>Who may take the register. This is the delegation.</summary>
    public Guid[] RecorderUserIds { get; set; } = Array.Empty<Guid>();

    public DateTime? RegisterOpenedAt { get; set; }
    public DateTime? RegisterClosedAt { get; set; }
    public Guid? RegisterClosedByUserId { get; set; }

    /// <summary>The minutes, in the Library.</summary>
    public Guid? MinutesMediaContentId { get; set; }

    /// <summary>Set when the ahead-of-time reminder went out. One reminder per row, gated by a timestamp.</summary>
    public DateTime? ReminderSentAt { get; set; }

    /// <summary>Set when the recorders were chased for a register not taken after EndsAt.</summary>
    public DateTime? RegisterChaseSentAt { get; set; }

    public Guid CreatedByUserId { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual PerformanceParameter? Parameter { get; set; }
    public virtual ICollection<StaffPerformanceRecord> Records { get; set; } = new List<StaffPerformanceRecord>();
}
