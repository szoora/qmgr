namespace QMgr.Domain.Constants;

/// <summary>
/// The four purchasable functional modules. These are the API's <c>SubscriptionPlan</c> row
/// <c>Code</c> values (that table now represents "one purchasable, priced module" rather than a
/// whole tenant tier — see the modular subscription plan). Lives in Q-Mgr.Shared, not mirrored
/// per-project, because both API and Web already reference this project — the RoleCodes-style
/// per-project mirror is legacy, not a pattern to repeat for new shared constants.
/// </summary>
public static class ModuleCodes
{
    /// <summary>Live Queue Board, Counter Terminal, Self-Service Kiosk, Customer Display, Counters,
    /// Service Types, Tokens, Queue/Counter reports.</summary>
    public const string CoreQueue = "core-queue";

    /// <summary>Digital Signage, Campaign Marketing, Feedback &amp; Surveys.</summary>
    public const string EngagementCommunications = "engagement-communications";

    /// <summary>Visitor Management, Student Roster, Student Welfare Ledger.</summary>
    public const string VisitorSafeguarding = "visitor-safeguarding";

    /// <summary>API Clients, webhooks, partner integration adapters.</summary>
    public const string IntegrationsApi = "integrations-api";

    /// <summary>All four, in catalog display order.</summary>
    public static readonly string[] All = { CoreQueue, EngagementCommunications, VisitorSafeguarding, IntegrationsApi };
}
