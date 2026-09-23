namespace QMgr.Domain.Enums;

/// <summary>
/// Who a school event is for (plan TERM_PROGRAMME_CALENDAR_AND_GATES §3, decision D5). Flags, because "S.4 Grand Mass"
/// is for students AND staff. Staff and Public are shown today; Students and Guardians are STORED so nothing is
/// re-entered on the day those people get a way in, and are not shown to anybody until then.
/// </summary>
[Flags]
public enum EventAudience
{
    None = 0,
    Staff = 1,
    Students = 2,
    Guardians = 4,
    /// <summary>Visible anonymously: the signage "Coming up" zone and nothing else reads it without a sign-in.</summary>
    Public = 8
}

/// <summary>What kind of table a document holds, as the programme import's classifier decided it (overridable).</summary>
public enum ProgrammeTableKind
{
    /// <summary>Not recognised; the reader is asked what it is.</summary>
    Unknown = 0,
    /// <summary>Week band → activity, date, responsible ("Activities of Term III").</summary>
    TermActivities = 1,
    /// <summary>Day → time, activity, venue, responsible ("Beginning of Term programme").</summary>
    DailyProgramme = 2,
    /// <summary>Date → time, meeting, venue, convener ("Schedule of Meetings").</summary>
    MeetingSchedule = 3,
    /// <summary>Person → phone, one or more duty dates ("Staff Duty Rota").</summary>
    PersonRota = 4,
    /// <summary>Period → person ("Administrative Weekly Duty Rota").</summary>
    PeriodRota = 5,
    /// <summary>The reader said to leave this table out.</summary>
    Ignore = 6
}

/// <summary>What the server found for one row of a programme import (preview and commit use the same answer).</summary>
public enum ProgrammeRowOutcome
{
    /// <summary>Will be created.</summary>
    New = 0,
    /// <summary>A row with the same source key or title and date exists and something differs; <c>Changes</c> names what.</summary>
    Update = 1,
    /// <summary>Already there and identical: a re-import of the same document creates nothing.</summary>
    Unchanged = 2,
    /// <summary>Refused, with the reason.</summary>
    Refused = 3
}
