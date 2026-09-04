namespace QMgr.Application.DTOs;

/// <summary>One entry in the 4-module catalog — same shape regardless of organization.</summary>
public record ModuleCatalogItem(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    decimal MonthlyPriceUsd,
    decimal MonthlyPriceUgx,
    decimal AnnualPriceUsd,
    decimal AnnualPriceUgx,
    int TrialDays,
    int MaxBranches,
    int MaxDisplays,
    int MaxUsersPerBranch,
    int MaxCountersPerBranch,
    int MaxTokensPerMonth,
    int MaxApiCallsPerMonth,
    int MaxStorageMb);

/// <summary>One organization's purchase status for one module — <c>Status</c> is a plain string
/// (not the API-only <c>OrganizationModuleStatus</c> enum) because Web only ever displays it; the
/// API serializes it as a string (JsonStringEnumConverter is registered globally), so this needs
/// no enum reference at all.</summary>
public record OrganizationModuleStatusDto(
    string ModuleCode,
    string ModuleName,
    bool Purchased,
    string? Status,
    DateTime? ActivatedAt,
    DateTime? TrialEndsAt,
    bool GrantedByPlatformAdmin,

    /// <summary>The UGX price this organization agreed to for one period, if one was captured
    /// when the module was activated. Null means the module tracks the catalog's current list
    /// price. Shown to the customer so a grandfathered price is visible rather than a surprise
    /// on the invoice.</summary>
    decimal? AgreedPriceUgx = null,

    /// <summary>Which cycle <see cref="AgreedPriceUgx"/> is the price of ("Monthly"/"Annual"),
    /// as a string for the same reason <see cref="Status"/> is one.</summary>
    string? BillingCycle = null);
