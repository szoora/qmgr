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
    /// How many organizations currently hold this module, active or in trial. This is the reach of
    /// a price change — though not all of them are repriced by one: see
    /// <see cref="GrandfatheredCount"/>.
    /// </summary>
    public int SubscriberCount { get; init; }

    /// <summary>
    /// How many of those holders are on a price this catalog row no longer shows, because it was
    /// captured when they bought and has been edited since. Their invoices are calculated from
    /// their own agreed price, not from the figures above.
    /// </summary>
    public int GrandfatheredCount { get; init; }

    /// <summary>
    /// The outcome of the request that returned this record: how many existing holders were moved
    /// onto the new price because the administrator asked for it. Always 0 when reading the
    /// catalog, and 0 on an update that left the prices alone or left the box unticked.
    /// </summary>
    public int RepricedCount { get; init; }
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

    /// <summary>
    /// Move every existing holder of this module onto the new price as well, instead of leaving
    /// them on the one they agreed to. Off by default: grandfathering is the whole point of the
    /// agreed price, and an administrator has to choose to override it — which is also how a price
    /// cut gets passed on, since otherwise existing customers would keep paying the old, higher
    /// figure. Ignored when the prices did not change.
    /// </summary>
    public bool ApplyToExistingSubscribers { get; init; }
}
