using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Welfare;

/// <summary>
/// A follow-up entry on an otherwise-immutable WelfareRecord — how an "edit" actually works here.
/// This is the record-level building block for CPOMS's whole-child chronology; the full
/// Open/UnderReview/ActionTaken case workflow (Phase 2) reads this same thread.
/// </summary>
public class WelfareNote : BaseEntity
{
    public Guid RecordId { get; set; }

    public string Body { get; set; } = string.Empty;
    public Guid AuthorUserId { get; set; }

    /// <summary>Note vs. a formal Statement — added post-MVP rather than a new entity.</summary>
    public WelfareNoteKind Kind { get; set; } = WelfareNoteKind.Note;

    /// <summary>Only meaningful when Kind == Statement: locks it from being treated as "still editable" in the UI. Doesn't actually mutate anything — every WelfareNote is already append-only.</summary>
    public bool IsFinal { get; set; }

    /// <summary>Who a Statement is attributed to (student, staff, or witness) — a plain name, since a witness usually isn't a Q-Mgr user and can't be an AuthorUserId FK. Null for a plain Note, where AuthorUserId is the whole story.</summary>
    public string? AttributedToName { get; set; }

    public virtual WelfareRecord? Record { get; set; }
}
