using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Billing;

/// <summary>
/// One purchasable module: what it is called, what it costs, and the limits holding it grants.
/// </summary>
/// <remarks>
/// This table used to hold two unrelated things — the four pricing tiers and the module catalog —
/// told apart only by whether <see cref="Code"/> appeared in <c>ModuleCodes</c>. The tier system was
/// retired on 2026-09-04, so every row here is now a module and <c>Tier</c> is gone with it.
/// An organization's holding of one of these is an
/// <see cref="OrganizationModule"/>, and that is what an invoice line is generated from.
/// </remarks>
public class SubscriptionPlan : BaseEntity
{
    /// <summary>Display name, e.g. "Core Queue Management"</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Module code, e.g. "core-queue". Immutable: route gating and middleware key off it.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Description shown on the marketplace card and in the sign-up wizard</summary>
    public string? Description { get; set; }


    #region Pricing (Dual Currency)

    /// <summary>Monthly price in USD (for international/card payments)</summary>
    public decimal MonthlyPriceUsd { get; set; }

    /// <summary>Annual price in USD (usually discounted)</summary>
    public decimal AnnualPriceUsd { get; set; }

    /// <summary>Monthly price in UGX (for Mobile Money payments)</summary>
    public decimal MonthlyPriceUgx { get; set; }

    /// <summary>Annual price in UGX</summary>
    public decimal AnnualPriceUgx { get; set; }

    #endregion

    #region Stripe Integration

    /// <summary>Stripe Price ID for monthly billing</summary>
    public string? StripePriceIdMonthly { get; set; }

    /// <summary>Stripe Price ID for annual billing</summary>
    public string? StripePriceIdAnnual { get; set; }

    #endregion

    #region Limits

    /// <summary>Maximum branches allowed</summary>
    public int MaxBranches { get; set; } = 1;

    /// <summary>Maximum digital signage displays allowed (org-wide)</summary>
    public int MaxDisplays { get; set; } = 1;

    /// <summary>Maximum users per branch</summary>
    public int MaxUsersPerBranch { get; set; } = 2;

    /// <summary>Maximum counters per branch</summary>
    public int MaxCountersPerBranch { get; set; } = 2;

    /// <summary>Maximum tokens per month</summary>
    public int MaxTokensPerMonth { get; set; } = 100;

    /// <summary>Maximum API calls per month</summary>
    public int MaxApiCallsPerMonth { get; set; } = 0;

    /// <summary>Maximum storage in MB</summary>
    public int MaxStorageMb { get; set; } = 100;

    #endregion

    #region Features

    /// <summary>JSON object defining feature flags (e.g., {"sms": true, "api": false})</summary>
    public string? Features { get; set; }

    /// <summary>Whether to show ads on customer-facing displays</summary>
    public bool ShowAds { get; set; } = true;

    /// <summary>Whether this plan gets a dedicated database schema</summary>
    public bool RequiresDedicatedSchema { get; set; }

    /// <summary>Trial period in days (0 = no trial)</summary>
    public int TrialDays { get; set; } = 14;

    #endregion

    #region Display

    /// <summary>Sort order for display (lower = first)</summary>
    public int SortOrder { get; set; }

    /// <summary>Whether this plan is visible to public</summary>
    public bool IsPublic { get; set; } = true;

    /// <summary>Badge text (e.g., "Most Popular", "Best Value")</summary>
    public string? Badge { get; set; }

    #endregion

    #region Navigation


    #endregion
}
