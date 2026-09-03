namespace QMgr.Domain.Constants;

/// <summary>
/// The purchasable functional modules. These are the API's <c>SubscriptionPlan</c> row
/// <c>Code</c> values (that table now represents "one purchasable, priced module" rather than a
/// whole tenant tier — see the modular subscription plan). Lives in Q-Mgr.Shared, not mirrored
/// per-project, because both API and Web already reference this project — the RoleCodes-style
/// per-project mirror is legacy, not a pattern to repeat for new shared constants.
/// </summary>
public static class ModuleCodes
{
    /// <summary>Live Queue Board, Counter Terminal, Self-Service Kiosk, Customer Display, Counters,
    /// Service Types, Tokens, Appointments, Queue/Counter reports.</summary>
    public const string CoreQueue = "core-queue";

    /// <summary>Digital Signage, Campaign Marketing, Feedback &amp; Surveys.</summary>
    public const string EngagementCommunications = "engagement-communications";

    /// <summary>
    /// Visitor check-in and check-out, badges, group passes, pre-registered arrivals, the
    /// watchlist, contractor induction, and the evacuation roll-call.
    /// </summary>
    public const string VisitorManagement = "visitor-management";

    /// <summary>
    /// Student roster and guardians, visiting-day passes, and the student welfare ledger
    /// (achievements, behaviour, safeguarding concerns, actions, statements and reports).
    /// </summary>
    public const string StudentWelfare = "student-welfare";

    /// <summary>API Clients, webhooks, partner integration adapters.</summary>
    public const string IntegrationsApi = "integrations-api";

    /// <summary>
    /// The retired "Visitor &amp; Safeguarding" module, which bundled visitor management together
    /// with the student roster and welfare ledger. Split into <see cref="VisitorManagement"/> and
    /// <see cref="StudentWelfare"/> because the two serve different buyers: "safeguarding" is
    /// education-sector language that means nothing to a bank or a clinic, and those customers
    /// were being asked to buy a student welfare ledger they would never open in order to get a
    /// visitor book. Kept as a constant, not deleted, because existing rows still carry this code
    /// until the migration that grants both successors has run everywhere.
    /// </summary>
    public const string LegacyVisitorSafeguarding = "visitor-safeguarding";

    /// <summary>Everything currently purchasable, in catalog display order.</summary>
    public static readonly string[] All =
    {
        CoreQueue,
        EngagementCommunications,
        VisitorManagement,
        StudentWelfare,
        IntegrationsApi
    };

    /// <summary>
    /// What an organization holding the retired module is entitled to. Used by the data migration
    /// and by the seeder's grandfathering step so nobody loses access in the split.
    /// </summary>
    public static readonly string[] LegacyVisitorSafeguardingSuccessors =
    {
        VisitorManagement,
        StudentWelfare
    };
}
