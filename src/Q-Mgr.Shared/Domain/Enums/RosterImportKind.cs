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
    Batch = 2,

    /// <summary>
    /// A staff list (StaffImportRow): name, email, role, departments, line manager. Each row becomes a
    /// User with a random password and an invitation sent through the existing password-reset link,
    /// so "invite" needed no new column. A fourth discriminator for the same reason the third was.
    /// </summary>
    Staff = 3,

    /// <summary>
    /// Timetable lessons (TimetableImportRow) exported from aSc Timetables, FET or a spreadsheet, into a DRAFT timetable
    /// (duty rota plan §6.2, Phase 6). Every row is checked like a lesson placed by hand; the draft's own diagnosis then
    /// shows what the import left to fix. A fifth discriminator for the same reason as the others.
    /// </summary>
    Timetable = 4,

    /// <summary>
    /// A term programme read from the school's own documents (plan TERM_PROGRAMME_CALENDAR_AND_GATES, 2026-09-23):
    /// calendar events, staff meetings (Session duties) and duty rotas (Rota duties) confirmed together in one batch.
    /// The job row is the batch's audit record and its undo handle; the rows it created carry its id.
    /// </summary>
    Programme = 5,

    /// <summary>
    /// A document waiting in the Import inbox (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E11, 2026-09-26): read once,
    /// each table routed to the section that owns it, nothing written until that section's owner approves it. The
    /// staged sections live in RowsJson; approving one runs that importer's own commit, which makes its own job.
    /// </summary>
    Inbox = 6
}
