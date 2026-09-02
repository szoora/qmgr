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
    Welfare = 2
}
