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
    bool GrantedByPlatformAdmin);
