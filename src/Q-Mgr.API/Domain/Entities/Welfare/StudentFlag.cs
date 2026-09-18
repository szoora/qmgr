using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Welfare;

/// <summary>
/// A standing vulnerability marker on a student — child protection plan, young carer, bereaved,
/// orphan, attendance concern, on medication. The lens through which any new chronology entry is
/// read: a late arrival from a child with no flags is a late arrival; the same lateness from a
/// child flagged as a young carer is a signal.
///
/// THE ONLY NEW TABLE in the welfare-background work, and the project's standing "enhance an
/// existing table before adding a new one" rule means the reason has to be written down. Three
/// cheaper shapes were tried first and each fails:
///
///  - Nullable columns on <c>Student</c>: there is no fixed set — schools want their own — and
///    each flag needs its own raised-by and review date, which columns cannot carry per-flag.
///  - A Postgres array of flag codes on <c>Student</c> (the shape
///    <c>WelfareRecord.AdditionalStudentIds</c> uses successfully): works right up to the point
///    where you ask "who raised this and when is it reviewed", which is the entire governance
///    value of a flag.
///  - A <c>WelfareRecord</c> with a flag category: genuinely tempting and would reuse everything,
///    but the ledger is deliberately append-only. A flag must be endable and reviewable in place,
///    and making records mutable to accommodate that would destroy the chronology guarantee the
///    ledger exists for.
///
/// So: its own table, with its own lifecycle. Its VOCABULARY, though, is not new — it reuses the
/// admin-managed <c>WelfareCategory</c> rather than making schools maintain a second taxonomy.
///
/// Branch-scoped with no global EF query filter, exactly like <c>Student</c> and
/// <c>WelfareRecord</c> — every controller action reaching one by ID must call
/// VerifyBranchOwnership explicitly.
/// </summary>
public class StudentFlag : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid StudentId { get; set; }

    /// <summary>The admin-managed category that names this flag — same taxonomy the ledger uses.</summary>
    public Guid CategoryId { get; set; }

    /// <summary>
    /// How widely this flag may be seen. A "wears glasses" flag and a "child protection plan"
    /// flag do not belong to the same audience — the same reasoning that forces Confidential on
    /// a Welfare-type record server-side.
    /// </summary>
    public WelfareTier Tier { get; set; } = WelfareTier.Low;

    /// <summary>
    /// How widely this flag may be seen — a DIFFERENT AXIS from <see cref="Tier"/>, which is
    /// severity. Before this existed the two were conflated: <c>Tier == High</c> was read as if it
    /// meant "confidential", so a high-severity but perfectly shareable flag ("severe nut allergy")
    /// was gated like a safeguarding one, and a low-severity but genuinely sensitive flag had no
    /// way to be protected at all.
    ///
    /// Tier still gates the flag's NOTES at the confidential rung (a High-tier flag's free text is
    /// where safeguarding detail ends up), which is why both are read. This field gates the whole
    /// flag, including its existence.
    /// </summary>
    public WelfareVisibility Visibility { get; set; } = WelfareVisibility.Standard;

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public Guid RaisedByUserId { get; set; }
    public DateTime RaisedAt { get; set; }

    /// <summary>
    /// When somebody must look at this again. Drives the same overdue sweep the ledger's action
    /// reminders use — a flag nobody has reviewed in a year is worse than no flag, because it
    /// looks like current knowledge.
    /// </summary>
    public DateTime? ReviewDueDate { get; set; }

    /// <summary>Gates re-notification to at most once every 24h, mirroring <c>WelfareRecord.ReminderSentAt</c>.</summary>
    public DateTime? ReminderSentAt { get; set; }

    /// <summary>Null means the flag is live. Ending a flag never deletes it — the history of having been flagged is itself welfare information.</summary>
    public DateTime? EndedAt { get; set; }
    public Guid? EndedByUserId { get; set; }

    [MaxLength(500)]
    public string? EndReason { get; set; }

    /// <summary>Convenience for queries and UI; equivalent to <c>EndedAt == null</c>.</summary>
    public new bool IsActive => EndedAt == null;

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual Student? Student { get; set; }
    public virtual WelfareCategory? Category { get; set; }
}
