using QMgr.Domain.Common;
using QMgr.Domain.Entities.Billing;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Organization;

public class Organization : BaseAuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? BrandName { get; set; }
    public string? LogoUrl { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Website { get; set; }
    public string? Address { get; set; }
    public string? Settings { get; set; } // JSON

    #region Whitelabel Branding (granted by module, see FeatureFlagService)

    /// <summary>
    /// Hex color (e.g. "#0058cc") applied as --qm-primary on this tenant's
    /// public-facing screens (customer display, kiosk). Null = platform default.
    /// </summary>
    public string? PrimaryColor { get; set; }

    /// <summary>
    /// Hex color applied as --qm-secondary. Null = platform default.
    /// </summary>
    public string? SecondaryColor { get; set; }

    /// <summary>
    /// Hex color applied as --qm-accent-* for highlights/CTAs. Null = platform default.
    /// </summary>
    public string? AccentColor { get; set; }

    /// <summary>
    /// Custom favicon URL for this tenant's public-facing screens. Null = platform default.
    /// </summary>
    public string? FaviconUrl { get; set; }

    /// <summary>
    /// Whether this tenant has whitelabel branding enabled. Independent of whether
    /// colors are set, so it can be toggled off without losing saved values.
    /// Actual effect is still gated by tier — see HasWhitelabelAccess.
    /// </summary>
    public bool WhitelabelEnabled { get; set; }

    #endregion

    /// <summary>
    /// Theme for this organization's public-facing display screens (customer
    /// queue display / kiosk) — "dark" or "light". Not tier-gated (unlike the
    /// whitelabel branding above): every organization can pick either, it's a
    /// basic display preference, not a paid customization.
    /// </summary>
    public string DisplayTheme { get; set; } = "dark";

    /// <summary>
    /// The industry type determines kiosk theming and default service types
    /// </summary>
    public IndustryType IndustryType { get; set; } = IndustryType.Service;

    #region SaaS / Multi-Tenancy Fields

    /// <summary>
    /// URL-safe unique identifier for tenant (e.g., "sacc" for sacc.{PlatformSettings.SaaS.BaseDomain})
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Canonical form of <see cref="Name"/> for duplicate detection: lower-cased, punctuation and
    /// legal suffixes removed, words sorted, so "Kampala Pharmacy Ltd." and "The Pharmacy, Kampala"
    /// reduce to the same value. Indexed but deliberately NOT unique, because two genuinely different
    /// customers can share a name, and a second branch of one business is a legitimate sign-up.
    /// This feeds a review queue, not a refusal.
    /// </summary>
    public string? NormalizedName { get; set; }

    /// <summary>
    /// Coarse bucket for <see cref="NormalizedName"/>, so a near-match search compares a handful of
    /// rows rather than scanning the table. Exists because fuzzy matching happens in process: no
    /// database extension is used for this.
    /// </summary>
    public string? NameBlockingKey { get; set; }

    /// <summary>
    /// The tenant's own host, LIVE (e.g. "dashboard.maryhillug.net"). Only ever written once
    /// ownership has been proved and a certificate exists — <see cref="CustomDomainPending"/> is
    /// where an unproved claim sits. Unique across organizations: two tenants answering on one
    /// host is a cross-tenant leak, not a clash.
    /// </summary>
    public string? CustomDomain { get; set; }

    /// <summary>
    /// The host being verified. Separate from <see cref="CustomDomain"/> so that an unverified
    /// claim can never route traffic: the middleware matches the live column alone.
    /// </summary>
    public string? CustomDomainPending { get; set; }

    /// <summary>
    /// The random value the tenant publishes as a TXT record at <c>_qmgr-verify.&lt;domain&gt;</c>.
    /// Shown once, in the DNS instructions, and compared in constant time.
    /// </summary>
    public string? CustomDomainVerificationToken { get; set; }

    /// <summary>When the TXT record was found and matched. Null until proved.</summary>
    public DateTime? CustomDomainVerifiedAt { get; set; }

    /// <summary>
    /// When certbot last issued a certificate for the host. Null until it succeeded, and it is
    /// the routing gate: the nginx server block is only written once this is set.
    /// </summary>
    public DateTime? CustomDomainCertificateAt { get; set; }

    /// <summary>
    /// Failed verification attempts since the claim was made. BACK-OFF IS A HARD REQUIREMENT, not
    /// politeness: a failing domain retried in a loop spends the box's weekly ACME budget and
    /// blocks issuance for every other tenant. The daily sweep gives up at
    /// <c>CustomDomainService.MaxAttempts</c> (a week's worth) and waits for a human to retry.
    /// </summary>
    public int CustomDomainAttempts { get; set; }

    /// <summary>When verification was last attempted, so the sweep does not re-try within the hour.</summary>
    public DateTime? CustomDomainLastAttemptAt { get; set; }

    /// <summary>
    /// What failed last, in the tenant administrator's own words. Separate steps read very
    /// differently — "DNS record not found yet" sends somebody to their registrar, "certificate
    /// could not be issued" sends them to us — and a single "failed" would send them to the wrong
    /// place.
    /// </summary>
    public string? CustomDomainLastError { get; set; }

    /// <summary>
    /// Current tenant status in the SaaS platform
    /// </summary>
    public TenantStatus Status { get; set; } = TenantStatus.Pending;

    /// <summary>
    /// Database schema name for a dedicated-schema tenant (null = shared schema)
    /// </summary>
    public string? SchemaName { get; set; }

    #endregion

    #region Subscription Fields

    /// <summary>
    /// Current subscription ID
    /// </summary>
    public Guid? SubscriptionId { get; set; }

    /// <summary>
    /// Stripe Customer ID for card payments
    /// </summary>
    public string? StripeCustomerId { get; set; }

    /// <summary>
    /// When the trial period ends
    /// </summary>
    public DateTime? TrialEndsAt { get; set; }

    /// <summary>
    /// Email for billing notifications (defaults to ContactEmail)
    /// </summary>
    public string? BillingEmail { get; set; }

    /// <summary>
    /// Phone number for mobile money payments
    /// </summary>
    public string? BillingPhone { get; set; }

    /// <summary>
    /// Preferred currency (USD, UGX)
    /// </summary>
    /// <summary>UGX unless an organization chose otherwise (owner decision, 2026-09-19): modules are sold
    /// in UGX and the sacc.ug gateway collects UGX only.</summary>
    public string PreferredCurrency { get; set; } = "UGX";

    #endregion

    #region Onboarding

    /// <summary>
    /// Whether onboarding wizard has been completed
    /// </summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>
    /// Current onboarding step (for resuming)
    /// </summary>
    public int OnboardingStep { get; set; }

    /// <summary>
    /// When the organization was verified (email verification)
    /// </summary>
    public DateTime? VerifiedAt { get; set; }

    #endregion

    #region Navigation Properties

    /// <summary>
    /// Branches belonging to this organization
    /// </summary>
    public virtual ICollection<Branch> Branches { get; set; } = new List<Branch>();

    /// <summary>
    /// Current subscription
    /// </summary>
    public virtual Subscription? Subscription { get; set; }

    /// <summary>
    /// All subscriptions (history)
    /// </summary>
    public virtual ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();

    /// <summary>
    /// Usage records for billing
    /// </summary>
    public virtual ICollection<UsageRecord> UsageRecords { get; set; } = new List<UsageRecord>();

    /// <summary>
    /// Ad impressions (shown to organizations holding no paid module)
    /// </summary>
    public virtual ICollection<AdImpression> AdImpressions { get; set; } = new List<AdImpression>();

    #endregion

    #region Helper Properties

    /// <summary>
    /// Check if organization is in an active state (can use the platform)
    /// </summary>
    public new bool IsActive => Status == TenantStatus.Active || Status == TenantStatus.Trialing;

    /// <summary>
    /// Check if organization uses a dedicated schema
    /// </summary>
    public bool UsesDedicatedSchema => !string.IsNullOrEmpty(SchemaName);

    /// <summary>
    /// Get effective billing email
    /// </summary>
    public string EffectiveBillingEmail => BillingEmail ?? ContactEmail ?? string.Empty;

    #endregion
}
