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
    /// "Welfare &amp; Performance" (the display name since 2026-09-17; short on purpose). Student roster and
    /// guardians, visiting-day passes, the student welfare ledger (achievements, behaviour,
    /// safeguarding concerns, actions, statements and reports) AND staff performance: the parameter
    /// catalogue, duties and registers, the staff record with evidence, recognition, scoring, the
    /// termly appraisal, staff notices, the activity log and every staff member's own portal.
    ///
    /// ONE module on purpose (user decision, 2026-09-17): the same school buys both, and the staff side
    /// reuses the welfare machinery end to end. The code stays "student-welfare" because it is a wire
    /// format stored on every purchase; only the name changed.
    /// </summary>
    public const string StudentWelfare = "student-welfare";

    /// <summary>API Clients, webhooks, partner integration adapters.</summary>
    public const string IntegrationsApi = "integrations-api";

    /// <summary>
    /// "White-Label Plus" — an ADD-ON, not a functional module. It carries exactly one
    /// entitlement, <c>FeatureCodes.RemoveAttribution</c>: "Powered by SACC Software" comes off the
    /// tenant's sign-in pages, the Q-Mgr line comes out of the shell footer, and their outbound
    /// email stops signing itself with our name.
    ///
    /// It is a catalogue row rather than a switch so that it is billed, purchased, renewed,
    /// grandfathered and shown on the Modules tab by machinery that already exists and is already
    /// tested — no new billing path. It gates no routes and appears in no
    /// <c>ModuleRouteMap</c> entry, which is why <see cref="Functional"/> exists beside
    /// <see cref="All"/>.
    /// </summary>
    public const string WhiteLabelPlus = "white-label-plus";

    /// <summary>
    /// Staff Performance as a module of its own, 2026-09-16 to 2026-09-17: built, never deployed, then
    /// folded into <see cref="StudentWelfare"/>. Kept only so the migration that removes its catalog row
    /// and any dev grants has a name to point at. Nothing gates on it.
    /// </summary>
    public const string RetiredStaffPerformance = "staff-performance";

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
        IntegrationsApi,
        WhiteLabelPlus
    };

    /// <summary>
    /// The modules that are a PRODUCT — the ones that open screens. <see cref="WhiteLabelPlus"/> is
    /// purchasable but is not one of these: it removes a line of text and nothing else.
    ///
    /// The distinction is load-bearing in <c>FeatureFlagService.ApplyModuleGrants</c>, where
    /// "holds any module" turns ads off and grants report exports. A tenant who paid to take our
    /// name off their footer has not thereby bought a reporting feature.
    /// </summary>
    public static readonly string[] Functional =
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
