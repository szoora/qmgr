namespace QMgr.Domain.Enums;

/// <summary>
/// MVP always created a record as Resolved (no case-workflow UI yet); Phase 2 wires up the real
/// Open/UnderReview/ActionTaken transitions. Draft was added after MVP for the mobile quick-log
/// flow — a record a staff member started but hasn't finished/finalized yet, visible only to its
/// own author until finalized. Appended at the end (not inserted) since this enum is stored as a
/// plain int with no HasConversion — renumbering would silently reinterpret every existing row.
/// </summary>
public enum WelfareStatus
{
    Open = 0,
    UnderReview = 1,
    ActionTaken = 2,
    Resolved = 3,
    Draft = 4
}
