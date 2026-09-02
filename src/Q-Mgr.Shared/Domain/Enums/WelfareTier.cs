namespace QMgr.Domain.Enums;

/// <summary>
/// Loosely mirrors the PBIS Tier 1/2/3 escalation model without borrowing its exact vocabulary —
/// "Low/Medium/High" reads correctly for an Achievement (a High-tier achievement is a bigger
/// deal) as well as a Behavior or Welfare record, whereas "Tier 1/2/3" only makes sense for
/// behavior interventions.
/// </summary>
public enum WelfareTier
{
    Low = 0,
    Medium = 1,
    High = 2
}
