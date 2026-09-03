namespace QMgr.Application.DTOs;

/// <summary>
/// One module as the platform's own catalog editor sees it — every field an administrator may
/// change, plus the context needed to change it responsibly.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ModuleCatalogItem"/>, which is what the public marketplace and the
/// sign-up wizard read. That one is deliberately a narrow projection for customers; this one
/// carries the editable surface and the subscriber count, and is only ever served to a platform
/// administrator.
/// </remarks>
public record ModuleCatalogAdminDto
{
    public Guid Id { get; init; }

    /// <summary>Immutable. The code is wired into route gating and permission checks in code.</summary>
    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Badge { get; init; }
    public int SortOrder { get; init; }
    public int TrialDays { get; init; }

    public decimal MonthlyPriceUsd { get; init; }
    public decimal AnnualPriceUsd { get; init; }
    public decimal MonthlyPriceUgx { get; init; }
    public decimal AnnualPriceUgx { get; init; }

    public int MaxBranches { get; init; }
    public int MaxDisplays { get; init; }
    public int MaxUsersPerBranch { get; init; }
    public int MaxCountersPerBranch { get; init; }
    public int MaxTokensPerMonth { get; init; }
    public int MaxApiCallsPerMonth { get; init; }
    public int MaxStorageMb { get; init; }

    /// <summary>An inactive module disappears from the marketplace and the sign-up wizard.</summary>
    public bool IsActive { get; init; }

    public bool IsPublic { get; init; }

    /// <summary>
    /// How many organizations currently hold this module, active or in trial. This is the blast
    /// radius of a price change: nothing stores what a subscriber agreed to pay, so a renewal
    /// invoice is priced from this row at the moment it is generated.
    /// </summary>
    public int SubscriberCount { get; init; }
}

/// <summary>
/// An administrator's edit to one module. The code is not here on purpose — it identifies the
/// module in the route and cannot be changed.
/// </summary>
public record UpdateModuleCatalogRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }

    /// <summary>Short ribbon shown on the card, such as "Most Popular". Blank clears it.</summary>
    public string? Badge { get; init; }

    public int SortOrder { get; init; }
    public int TrialDays { get; init; }

    public decimal MonthlyPriceUsd { get; init; }
    public decimal AnnualPriceUsd { get; init; }
    public decimal MonthlyPriceUgx { get; init; }
    public decimal AnnualPriceUgx { get; init; }

    public int MaxBranches { get; init; }
    public int MaxDisplays { get; init; }
    public int MaxUsersPerBranch { get; init; }
    public int MaxCountersPerBranch { get; init; }
    public int MaxTokensPerMonth { get; init; }
    public int MaxApiCallsPerMonth { get; init; }
    public int MaxStorageMb { get; init; }

    public bool IsActive { get; init; }
    public bool IsPublic { get; init; }
}
