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
    Welfare = 1,

    /// <summary>
    /// A bulk edit of records already in the system — promote a class, set a house, mark leavers,
    /// reassign open welfare actions, check out everyone still on site. Not an import at all: the
    /// rows are ids the operator selected on a list, not a file they uploaded.
    /// <para>
    /// It reuses this table for the same reason the welfare backfill does, and the standing
    /// enhance-before-you-add rule is why it is a third discriminator rather than a third table: a
    /// batch has rows, a per-row outcome, progress, counts and a history, which is precisely what
    /// RosterImportJob and RosterImportJobEntry already model. It also means batch history and
    /// import history share one screen — to an administrator asking "what happened to the roster
    /// last week", they are the same question.
    /// </para>
    /// </summary>
    Batch = 2
}
