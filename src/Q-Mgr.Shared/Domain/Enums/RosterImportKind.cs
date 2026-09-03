namespace QMgr.Domain.Enums;

/// <summary>
/// What a RosterImportJob's rows actually are. The welfare-ledger's historical-records import
/// reuses the roster import's job/entry tables, background processor, progress broadcast and
/// history UI wholesale (see the project's "widen an existing table before adding a new one"
/// convention) — this discriminator is the only schema change that took.
/// </summary>
public enum RosterImportKind
{
    /// <summary>Student + guardian rows (RosterImportRow) — the original visiting-day roster upload.</summary>
    Roster = 0,

    /// <summary>Historical welfare-ledger rows (WelfareImportRow) — achievements/behavior/welfare records backfilled from a school's previous system.</summary>
    Welfare = 1
}
