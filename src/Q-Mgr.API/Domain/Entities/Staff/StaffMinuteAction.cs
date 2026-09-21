using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// One action point out of a meeting's minutes: what, who, by when.
///
/// A TABLE, and the standing "enhance an existing row before adding a table" constraint is what
/// argues FOR it here rather than against it. The minutes themselves are a document and live as
/// jsonb on <see cref="StaffDuty.MinutesJson"/> — sections, motions and corrections are attributes
/// of that document and are only ever read one meeting at a time. An action is not:
///
///   * It is queried ACROSS meetings, by PERSON — "my open actions" on the portal. A jsonb list on
///     each duty cannot be indexed that way and would mean scanning every meeting of the year.
///   * It has its own lifecycle — open, done, cancelled, with who closed it and when — that
///     outlives the draft it was written in and survives the minutes being corrected.
///   * It needs a <see cref="ReminderStage"/> COLUMN. Every ladder in this module claims its stage
///     with a conditional ExecuteUpdateAsync on the row BEFORE sending (see ReminderLadderJob); a
///     stage buried in a blob cannot be claimed that way, and read-modify-write of a jsonb map is
///     the exact bug the notice acknowledgements had.
///
/// Cancelled rather than deleted: the minutes said it, so the record keeps it.
/// </summary>
public class StaffMinuteAction : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>The meeting whose minutes carry this action. Cascades: no meeting, no action.</summary>
    public Guid DutyId { get; set; }

    [MaxLength(1000)]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Who is to do it. Nullable because a meeting can minute an action for a body rather than a
    /// person ("the Board of Governors to review"), and refusing to record that would make people
    /// pick a name that is not the truth.
    /// </summary>
    public Guid? AssignedUserId { get; set; }

    /// <summary>Null means "no date was agreed" — never "today". The chase only runs where one was.</summary>
    public DateTime? DueAt { get; set; }

    public MinuteActionStatus Status { get; set; } = MinuteActionStatus.Open;

    public DateTime? CompletedAt { get; set; }
    public Guid? CompletedByUserId { get; set; }

    [MaxLength(1000)]
    public string? CompletionNote { get; set; }

    /// <summary>
    /// The reminder ladder's claim column: the highest stage already sent. Claimed with a
    /// conditional UPDATE before anything goes out, so two workers cannot both send stage 2.
    /// </summary>
    public int ReminderStage { get; set; }

    #region Navigation Properties

    public virtual StaffDuty? Duty { get; set; }

    #endregion
}
