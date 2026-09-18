using Microsoft.EntityFrameworkCore;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Billing;

namespace QMgr.Infrastructure.Data;

/// <summary>
/// Inserts any missing module row into the catalog (<c>subscription_plans</c>), in EVERY environment.
///
/// Why this exists: the catalog used to be seeded only by <see cref="DbSeeder.SeedModulesAsync"/>,
/// and <c>DbSeeder</c> is constructed in Program.cs inside <c>if (app.Environment.IsDevelopment())</c>.
/// So on a production box a module added to the code never appeared in the catalog, and the Module
/// Catalog editor's <c>NOT_SEEDED</c> advice ("restarting the API will add it") was only true on a
/// developer's machine. Found while planning the Staff Performance module (2026-09-16); the same
/// shape as <see cref="PlatformEmailDefaults"/> and <see cref="UploadLinkRepair"/>: a startup
/// reconciliation for something an existing install could not otherwise pick up.
///
/// INSERT-IF-MISSING ONLY. Prices, limits and names of an existing row are the Module Catalog
/// editor's to change (and "Apply to existing subscribers" is how a change reaches customers);
/// this never overwrites a row an administrator has edited.
/// </summary>
public class ModuleCatalogDefaults
{
    private readonly QMgrDbContext _db;
    private readonly ILogger<ModuleCatalogDefaults> _logger;

    public ModuleCatalogDefaults(QMgrDbContext db, ILogger<ModuleCatalogDefaults> logger)
    {
        _db = db;
        _logger = logger;
    }

    public sealed record ModuleDefaults(
        string Name, string Code, bool ShowAds, bool DedicatedSchema,
        int MaxBranches, int MaxDisplays, int MaxUsersPerBranch, int MaxCountersPerBranch,
        int MaxTokensPerMonth, int MaxApiCallsPerMonth, int MaxStorageMb,
        decimal MonthlyPriceUsd, decimal AnnualPriceUsd, decimal MonthlyPriceUgx, decimal AnnualPriceUgx,
        string? Description, string? Badge, int SortOrder);

    /// <summary>Shared with the migration that renames an existing Student Welfare row, so the two cannot differ.</summary>
    public const string StudentWelfareDescription =
        "Student roster and guardians, visiting-day passes, and the welfare ledger; plus staff performance: duties and registers, recognition, scoring, termly appraisals, staff notices, the activity log and every staff member's own portal.";

    /// <summary>The catalog as shipped. One entry per <see cref="ModuleCodes.All"/> code.</summary>
    public static readonly IReadOnlyList<ModuleDefaults> Definitions = new[]
    {
        new ModuleDefaults(
            Name: "Core Queue Management", Code: ModuleCodes.CoreQueue, ShowAds: false, DedicatedSchema: false,
            MaxBranches: 5, MaxDisplays: 1, MaxUsersPerBranch: 10, MaxCountersPerBranch: 10,
            MaxTokensPerMonth: 20_000, MaxApiCallsPerMonth: 5_000, MaxStorageMb: 500,
            MonthlyPriceUsd: 19m, AnnualPriceUsd: 190m, MonthlyPriceUgx: 80_000m, AnnualPriceUgx: 800_000m,
            Description: "Live queue board, counter terminal, self-service kiosk, customer display, counters, service types, and tokens.",
            Badge: null, SortOrder: 0),
        new ModuleDefaults(
            Name: "Communication", Code: ModuleCodes.EngagementCommunications, ShowAds: false, DedicatedSchema: false,
            MaxBranches: 5, MaxDisplays: 10, MaxUsersPerBranch: 10, MaxCountersPerBranch: 2,
            MaxTokensPerMonth: 1_000, MaxApiCallsPerMonth: 5_000, MaxStorageMb: 5_000,
            MonthlyPriceUsd: 29m, AnnualPriceUsd: 290m, MonthlyPriceUgx: 120_000m, AnnualPriceUgx: 1_200_000m,
            Description: "Digital signage, campaign marketing (SMS/WhatsApp/email broadcasts), and customer feedback & surveys.",
            Badge: "Most Popular", SortOrder: 1),
        // "Visitor & Safeguarding" used to be one module covering both of these. It was split because
        // they sell to different people: a bank or clinic wants a visitor book and has no use for a
        // student welfare ledger, while "safeguarding" is education-sector language those buyers do
        // not recognise. Priced so either half alone costs less than the old bundle.
        new ModuleDefaults(
            Name: "Visitor Management", Code: ModuleCodes.VisitorManagement, ShowAds: false, DedicatedSchema: false,
            MaxBranches: 5, MaxDisplays: 1, MaxUsersPerBranch: 10, MaxCountersPerBranch: 2,
            MaxTokensPerMonth: 1_000, MaxApiCallsPerMonth: 5_000, MaxStorageMb: 1_000,
            MonthlyPriceUsd: 22m, AnnualPriceUsd: 220m, MonthlyPriceUgx: 95_000m, AnnualPriceUgx: 950_000m,
            Description: "Visitor check-in and check-out, badges and group passes, pre-registered arrivals, watchlist and contractor induction, and the evacuation roll-call.",
            Badge: null, SortOrder: 2),
        // "Welfare & Performance" (one module since 2026-09-17, user decision: the same school
        // buys both, and staff performance is built on the welfare machinery). MaxUsersPerBranch is 250
        // because "every staff member gets a login" is the staff side's whole point and POST /api/v1/users
        // returns 402 at the cap; storage is the larger of the two former modules'. The price is the
        // welfare module's; the Module Catalog editor is where it is changed.
        new ModuleDefaults(
            Name: "Welfare & Performance", Code: ModuleCodes.StudentWelfare, ShowAds: false, DedicatedSchema: false,
            MaxBranches: 5, MaxDisplays: 1, MaxUsersPerBranch: 250, MaxCountersPerBranch: 2,
            MaxTokensPerMonth: 1_000, MaxApiCallsPerMonth: 5_000, MaxStorageMb: 5_000,
            MonthlyPriceUsd: 25m, AnnualPriceUsd: 250m, MonthlyPriceUgx: 105_000m, AnnualPriceUgx: 1_050_000m,
            Description: StudentWelfareDescription,
            Badge: "For schools", SortOrder: 3),
        new ModuleDefaults(
            Name: "Integrations & API Access", Code: ModuleCodes.IntegrationsApi, ShowAds: false, DedicatedSchema: false,
            MaxBranches: 3, MaxDisplays: 1, MaxUsersPerBranch: 5, MaxCountersPerBranch: 2,
            MaxTokensPerMonth: 1_000, MaxApiCallsPerMonth: 100_000, MaxStorageMb: 200,
            MonthlyPriceUsd: 15m, AnnualPriceUsd: 150m, MonthlyPriceUgx: 60_000m, AnnualPriceUgx: 600_000m,
            Description: "API clients, webhooks, and partner integration adapters (hospital/pharmacy/banking).",
            Badge: null, SortOrder: 4),
    };

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _db.SubscriptionPlans.IgnoreQueryFilters()
                .Select(p => p.Code)
                .ToHashSetAsync(cancellationToken);

            var added = 0;
            foreach (var defaults in Definitions)
            {
                if (existing.Contains(defaults.Code)) continue;

                _db.SubscriptionPlans.Add(new SubscriptionPlan
                {
                    Id = Guid.NewGuid(),
                    Name = defaults.Name,
                    Code = defaults.Code,
                    Description = defaults.Description,
                    ShowAds = defaults.ShowAds,
                    RequiresDedicatedSchema = defaults.DedicatedSchema,
                    IsPublic = true,
                    SortOrder = defaults.SortOrder,
                    Badge = defaults.Badge,
                    TrialDays = 14,
                    MonthlyPriceUsd = defaults.MonthlyPriceUsd,
                    AnnualPriceUsd = defaults.AnnualPriceUsd,
                    MonthlyPriceUgx = defaults.MonthlyPriceUgx,
                    AnnualPriceUgx = defaults.AnnualPriceUgx,
                    MaxBranches = defaults.MaxBranches,
                    MaxDisplays = defaults.MaxDisplays,
                    MaxUsersPerBranch = defaults.MaxUsersPerBranch,
                    MaxCountersPerBranch = defaults.MaxCountersPerBranch,
                    MaxTokensPerMonth = defaults.MaxTokensPerMonth,
                    MaxApiCallsPerMonth = defaults.MaxApiCallsPerMonth,
                    MaxStorageMb = defaults.MaxStorageMb,
                    CreatedAt = DateTime.UtcNow
                });
                added++;
                _logger.LogInformation("Seeded {ModuleName} ({Code}) into the module catalog", defaults.Name, defaults.Code);
            }

            if (added > 0)
                await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // A missing catalog row is not worth a dead API; the editor reports NOT_SEEDED.
            _logger.LogError(ex, "Module catalog defaults could not be applied");
        }
    }
}
