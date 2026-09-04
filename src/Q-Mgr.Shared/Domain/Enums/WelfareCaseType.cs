namespace QMgr.Domain.Enums;

/// <summary>
/// The three record families the Student Welfare Ledger tracks — deliberately not just
/// "positive/negative" (the PBIS behavior-tool convention) because a Welfare concern isn't about
/// fault at all (a home situation, a health issue) the way an Achievement or a Behavior incident
/// is. See docs/TASK_TRACKER.md's welfare-plan research for the reasoning.
/// </summary>
public enum WelfareCaseType
{
    Achievement = 0,
    Behavior = 1,
    Welfare = 2,

    /// <summary>
    /// An open, owned, reviewable plan rather than a thing that happened — assess, plan, do,
    /// review. Deliberately a case type and NOT a new table: a plan is a record with
    /// <c>Status = Open</c>, an <c>AssignedToUserId</c> owner, an <c>ActionDueDate</c> review
    /// date, and its reviews written as the <c>WelfareNote</c> entries the ledger already
    /// supports, so the whole lifecycle falls out of machinery that is already built and already
    /// carries reminders.
    ///
    /// APPENDED, never inserted — this enum is stored as an integer, so slotting a value in the
    /// middle would silently rewrite the meaning of every existing row (the same reasoning that
    /// put <c>WelfareStatus.Draft</c> at 4).
    /// </summary>
    SupportPlan = 3
}
