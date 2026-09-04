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
    /// Custom domain for white-label (e.g., "queue.getsacc.com")
    /// </summary>
    public string? CustomDomain { get; set; }

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
    public string PreferredCurrency { get; set; } = "USD";

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
