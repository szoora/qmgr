namespace QMgr.Domain.Enums;

/// <summary>
/// What KIND of visit this is — set per <see cref="Entities"/> visit row rather than on the
/// person's profile, because the same individual legitimately arrives in different capacities on
/// different days (a parent who is also the school's electrician is a Guest on visiting day and a
/// Contractor on the day they rewire the hall).
///
/// Only <see cref="Contractor"/> currently changes any behaviour: a contractor visit whose person
/// has no recorded site induction — or whose induction has lapsed — WARNS front-desk staff at
/// check-in (it never blocks; that's reserved for the watchlist). Everything else is a label used
/// for filtering, the evacuation roll-call, and reporting.
/// </summary>
public enum VisitorType
{
    Guest = 0,
    Contractor = 1,
    Staff = 2,
    Other = 3
}
