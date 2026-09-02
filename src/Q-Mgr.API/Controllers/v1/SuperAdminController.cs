using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Super admin controller for platform-wide management
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize]
[RequirePermission(Permissions.PlatformAdmin)]
[Produces("application/json")]
public class SuperAdminController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IBillingService _billingService;
    private readonly IUsageTrackingService _usageTrackingService;
    private readonly IModuleAccessService _moduleAccessService;
    private readonly ILogger<SuperAdminController> _logger;

    public SuperAdminController(
        QMgrDbContext dbContext,
        ITenantProvisioningService provisioningService,
        IBillingService billingService,
        IUsageTrackingService usageTrackingService,
        IModuleAccessService moduleAccessService,
        ILogger<SuperAdminController> logger)
    {
        _dbContext = dbContext;
        _provisioningService = provisioningService;
        _billingService = billingService;
        _usageTrackingService = usageTrackingService;
        _moduleAccessService = moduleAccessService;
        _logger = logger;
    }

    #region Tenant Management

    /// <summary>
    /// Get all tenants with pagination and filtering
    /// </summary>
    [HttpGet("tenants")]
    [ProducesResponseType(typeof(PagedResult<TenantSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTenants(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] TenantStatus? status = null,
        [FromQuery] TenantTier? tier = null,
        [FromQuery] string? search = null)
    {
        var query = _dbContext.Organizations.AsQueryable();

        if (status.HasValue)
            query = query.Where(o => o.Status == status.Value);

        if (tier.HasValue)
            query = query.Where(o => o.Tier == tier.Value);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(o =>
                o.Name.Contains(search) ||
                o.Slug.Contains(search) ||
                (o.ContactEmail != null && o.ContactEmail.Contains(search)));

        var totalCount = await query.CountAsync();

        var tenants = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new TenantSummary
            {
                Id = o.Id,
                Name = o.Name,
                Slug = o.Slug,
                Status = o.Status,
                Tier = o.Tier,
                ContactEmail = o.ContactEmail,
                CreatedAt = o.CreatedAt,
                TrialEndsAt = o.TrialEndsAt,
                OnboardingCompleted = o.OnboardingCompleted
            })
            .ToListAsync();

        return Ok(new PagedResult<TenantSummary>
        {
            Items = tenants,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        });
    }

    /// <summary>
    /// Get detailed tenant information
    /// </summary>
    [HttpGet("tenants/{id:guid}")]
    [ProducesResponseType(typeof(TenantDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTenant(Guid id)
    {
        var org = await _dbContext.Organizations
            .Include(o => o.Subscription)
                .ThenInclude(s => s!.Plan)
            .Include(o => o.Branches)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        var usage = await _usageTrackingService.GetCurrentUsageAsync(id);
        var limits = await _billingService.GetEffectiveLimitsAsync(id);
        var userCount = await _dbContext.Users.CountAsync(u => u.OrganizationId == id);

        return Ok(new TenantDetails
        {
            Id = org.Id,
            Name = org.Name,
            Slug = org.Slug,
            BrandName = org.BrandName,
            Status = org.Status,
            Tier = org.Tier,
            ContactEmail = org.ContactEmail,
            ContactPhone = org.ContactPhone,
            BillingEmail = org.BillingEmail,
            BillingPhone = org.BillingPhone,
            PreferredCurrency = org.PreferredCurrency,
            CustomDomain = org.CustomDomain,
            StripeCustomerId = org.StripeCustomerId,
            CreatedAt = org.CreatedAt,
            VerifiedAt = org.VerifiedAt,
            TrialEndsAt = org.TrialEndsAt,
            OnboardingCompleted = org.OnboardingCompleted,
            OnboardingStep = org.OnboardingStep,
            IndustryType = org.IndustryType,
            BranchCount = org.Branches.Count,
            UserCount = userCount,
            Subscription = org.Subscription != null ? new SubscriptionSummary
            {
                Id = org.Subscription.Id,
                PlanName = org.Subscription.Plan?.Name ?? "Unknown",
                Status = org.Subscription.Status,
                BillingCycle = org.Subscription.BillingCycle,
                CurrentPeriodEnd = org.Subscription.CurrentPeriodEnd
            } : null,
            Usage = new UsageSummary
            {
                TokensCreated = usage.TokensCreated,
                ApiCalls = usage.ApiCalls,
                ActiveUsers = usage.ActiveUsers,
                ActiveBranches = usage.ActiveBranches,
                MaxTokens = limits.MaxTokensPerMonth,
                MaxApiCalls = limits.MaxApiCallsPerMonth,
                MaxUsers = limits.MaxUsersPerBranch,
                MaxBranches = limits.MaxBranches,
                StorageUsedMb = usage.StorageUsedBytes / 1024 / 1024,
                MaxStorageMb = limits.MaxStorageMb,
                StorageQuotaOverrideMb = org.Subscription?.MaxStorageOverride
            }
        });
    }

    /// <summary>
    /// Suspend a tenant
    /// </summary>
    [HttpPost("tenants/{id:guid}/suspend")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SuspendTenant(Guid id, [FromBody] SuspendRequest request)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        await _provisioningService.SuspendTenantAsync(id, request.Reason);

        _logger.LogWarning(
            "Super admin suspended tenant {TenantId}. Reason: {Reason}",
            id, request.Reason);

        return Ok(new { message = "Tenant suspended successfully" });
    }

    /// <summary>
    /// Reactivate a suspended tenant
    /// </summary>
    [HttpPost("tenants/{id:guid}/reactivate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReactivateTenant(Guid id)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        await _provisioningService.ReactivateTenantAsync(id);

        _logger.LogInformation("Super admin reactivated tenant {TenantId}", id);

        return Ok(new { message = "Tenant reactivated successfully" });
    }

    /// <summary>
    /// Update tenant tier (upgrade/downgrade)
    /// </summary>
    [HttpPatch("tenants/{id:guid}/tier")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTenantTier(Guid id, [FromBody] UpdateTierRequest request)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        var previousTier = org.Tier;
        org.Tier = request.Tier;

        _dbContext.Organizations.Update(org);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Super admin changed tenant {TenantId} tier from {OldTier} to {NewTier}",
            id, previousTier, request.Tier);

        return Ok(new { message = "Tenant tier updated successfully", previousTier, newTier = request.Tier });
    }

    /// <summary>
    /// Every module's purchase status for one tenant — backs the "Manage Modules" panel that
    /// replaced the old single-select "Change Tier" modal.
    /// </summary>
    [HttpGet("tenants/{id:guid}/modules")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTenantModules(Guid id)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        var status = await _moduleAccessService.GetOrganizationModuleStatusAsync(id);
        return Ok(status);
    }

    /// <summary>
    /// Direct platform-admin grant — no payment collected, immediately Active. The literal
    /// "add/remove modules per customer request" lever.
    /// </summary>
    [HttpPut("tenants/{id:guid}/modules/{moduleCode}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GrantTenantModule(Guid id, string moduleCode, [FromBody] GrantModuleRequest? request)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        if (!ModuleCodes.All.Contains(moduleCode))
            return NotFound(new { error = "MODULE_NOT_FOUND", message = $"Unknown module '{moduleCode}'." });

        await _moduleAccessService.GrantAsync(id, moduleCode, CurrentUserId() ?? Guid.Empty, request?.Note);

        _logger.LogInformation("Super admin granted module {ModuleCode} to tenant {TenantId}", moduleCode, id);

        return Ok(new { message = "Module granted successfully" });
    }

    /// <summary>Direct platform-admin revoke — the other half of the manage-modules lever.</summary>
    [HttpDelete("tenants/{id:guid}/modules/{moduleCode}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeTenantModule(Guid id, string moduleCode, [FromQuery] string? note)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        if (!ModuleCodes.All.Contains(moduleCode))
            return NotFound(new { error = "MODULE_NOT_FOUND", message = $"Unknown module '{moduleCode}'." });

        await _moduleAccessService.RevokeAsync(id, moduleCode, note);

        _logger.LogInformation("Super admin revoked module {ModuleCode} from tenant {TenantId}", moduleCode, id);

        return Ok(new { message = "Module revoked successfully" });
    }

    public record GrantModuleRequest(string? Note);

    /// <summary>
    /// One-time data migration: every organization currently on a paid legacy Tier
    /// (Starter/Professional/Enterprise) gets all 4 modules grandfathered in as Active with no new
    /// charge, preserving continuity for existing paying customers. Free-tier organizations
    /// (which today have no Subscription row at all — the established "no subscription IS the
    /// free tier" convention) get zero OrganizationModule rows, matching their current state.
    ///
    /// Deliberately NOT run automatically anywhere (no startup hook, no recurring job) — this
    /// touches real customer access on a live production app and must be triggered explicitly by
    /// a platform admin who has reviewed the dry-run output first. Idempotent either way: an
    /// organization that already has any OrganizationModule rows (already migrated, or already
    /// self-served/admin-granted a module) is skipped, never double-granted or overwritten.
    /// </summary>
    [HttpPost("migrate-legacy-tenants-to-modules")]
    [ProducesResponseType(typeof(MigrationReport), StatusCodes.Status200OK)]
    public async Task<IActionResult> MigrateLegacyTenantsToModules([FromQuery] bool dryRun = true)
    {
        var paidOrgs = await _dbContext.Organizations
            .Where(o => o.Tier != TenantTier.Free && o.Status != TenantStatus.Deleted)
            .ToListAsync();

        var orgIdsWithModules = (await _dbContext.OrganizationModules
            .Select(om => om.OrganizationId)
            .Distinct()
            .ToListAsync())
            .ToHashSet();

        var toMigrate = paidOrgs.Where(o => !orgIdsWithModules.Contains(o.Id)).ToList();
        var alreadyMigrated = paidOrgs.Where(o => orgIdsWithModules.Contains(o.Id))
            .Select(o => new MigrationOrgSummary(o.Id, o.Name, o.Tier.ToString()))
            .ToList();

        var migrated = new List<MigrationOrgSummary>();

        if (!dryRun)
        {
            var operatorUserId = CurrentUserId() ?? Guid.Empty;
            foreach (var org in toMigrate)
            {
                foreach (var moduleCode in ModuleCodes.All)
                {
                    await _moduleAccessService.GrantAsync(org.Id, moduleCode, operatorUserId,
                        $"Migrated from legacy {org.Tier} tier");
                }
                migrated.Add(new MigrationOrgSummary(org.Id, org.Name, org.Tier.ToString()));

                _logger.LogInformation(
                    "Migrated organization {OrgId} ({OrgName}) from legacy {Tier} tier to all 4 modules",
                    org.Id, org.Name, org.Tier);
            }
        }

        var report = new MigrationReport(
            DryRun: dryRun,
            TotalPaidOrganizations: paidOrgs.Count,
            AlreadyMigrated: alreadyMigrated,
            Migrated: dryRun ? toMigrate.Select(o => new MigrationOrgSummary(o.Id, o.Name, o.Tier.ToString())).ToList() : migrated,
            Message: dryRun
                ? $"Dry run: {toMigrate.Count} organization(s) would be migrated. Re-run with dryRun=false to actually grant modules."
                : $"Migrated {migrated.Count} organization(s)."
        );

        return Ok(report);
    }

    public record MigrationOrgSummary(Guid Id, string Name, string Tier);

    public record MigrationReport(
        bool DryRun,
        int TotalPaidOrganizations,
        List<MigrationOrgSummary> AlreadyMigrated,
        List<MigrationOrgSummary> Migrated,
        string Message);

    private Guid? CurrentUserId()
    {
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : null;
    }

    /// <summary>
    /// Set (or clear) a tenant's per-tenant storage quota override. This is the platform-admin
    /// lever for the storage-conservation direction — content should mostly live on external
    /// platforms (YouTube, Vimeo, Google Drive, TikTok) that this app just links to rather than
    /// hosts, with local uploads capped per plan (SubscriptionPlan.MaxStorageMb, 100MB by
    /// default) and enforced at upload time in ContentController. This override exists for the
    /// occasional tenant who genuinely needs more room without changing their whole plan.
    /// </summary>
    [HttpPatch("tenants/{id:guid}/storage-quota")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStorageQuota(Guid id, [FromBody] UpdateStorageQuotaRequest request)
    {
        var subscription = await _dbContext.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == id);
        if (subscription == null)
            return NotFound(new { error = "SUBSCRIPTION_NOT_FOUND", message = "This tenant has no subscription to set a storage override on." });

        if (request.MaxStorageMb is < 0)
            return BadRequest(new { error = "INVALID_QUOTA", message = "Storage quota cannot be negative." });

        var previousOverride = subscription.MaxStorageOverride;
        subscription.MaxStorageOverride = request.MaxStorageMb;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Super admin changed tenant {TenantId} storage quota override from {OldMb}MB to {NewMb}MB",
            id, previousOverride?.ToString() ?? "plan default", request.MaxStorageMb?.ToString() ?? "plan default");

        var limits = await _billingService.GetEffectiveLimitsAsync(id);
        return Ok(new { message = "Storage quota updated successfully", effectiveMaxStorageMb = limits.MaxStorageMb });
    }

    /// <summary>
    /// Extend trial for a tenant
    /// </summary>
    [HttpPost("tenants/{id:guid}/extend-trial")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExtendTrial(Guid id, [FromBody] ExtendTrialRequest request)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        var newTrialEnd = (org.TrialEndsAt ?? DateTime.UtcNow).AddDays(request.Days);
        org.TrialEndsAt = newTrialEnd;

        if (org.Status == TenantStatus.Suspended && !org.SubscriptionId.HasValue)
            org.Status = TenantStatus.Trialing;

        _dbContext.Organizations.Update(org);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Super admin extended trial for tenant {TenantId} by {Days} days until {NewTrialEnd}",
            id, request.Days, newTrialEnd);

        return Ok(new { message = "Trial extended successfully", newTrialEndsAt = newTrialEnd });
    }

    #endregion

    #region Platform Statistics

    /// <summary>
    /// Get platform-wide statistics
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(PlatformStats), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlatformStats()
    {
        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonth = thisMonth.AddMonths(-1);

        // Calculate MRR (Monthly Recurring Revenue) first
        var activeSubscriptions = await _dbContext.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.Status == SubscriptionStatus.Active)
            .ToListAsync();

        var mrr = activeSubscriptions.Sum(s =>
            s.BillingCycle == BillingCycle.Monthly
                ? s.Plan?.MonthlyPriceUsd ?? 0
                : (s.Plan?.AnnualPriceUsd ?? 0) / 12);

        var stats = new PlatformStats
        {
            TotalOrganizations = await _dbContext.Organizations.CountAsync(),
            ActiveOrganizations = await _dbContext.Organizations
                .CountAsync(o => o.Status == TenantStatus.Active || o.Status == TenantStatus.Trialing),
            TrialingOrganizations = await _dbContext.Organizations
                .CountAsync(o => o.Status == TenantStatus.Trialing),
            SuspendedOrganizations = await _dbContext.Organizations
                .CountAsync(o => o.Status == TenantStatus.Suspended),

            TotalUsers = await _dbContext.Users.CountAsync(),
            TotalBranches = await _dbContext.Branches.CountAsync(),

            OrganizationsByTier = await _dbContext.Organizations
                .GroupBy(o => o.Tier)
                .Select(g => new TierCount { Tier = g.Key, Count = g.Count() })
                .ToListAsync(),

            NewOrganizationsThisMonth = await _dbContext.Organizations
                .CountAsync(o => o.CreatedAt >= thisMonth),

            NewOrganizationsLastMonth = await _dbContext.Organizations
                .CountAsync(o => o.CreatedAt >= lastMonth && o.CreatedAt < thisMonth),

            TotalTokensThisMonth = await _dbContext.UsageRecords
                .Where(u => u.Year == now.Year && u.Month == now.Month)
                .SumAsync(u => u.TokensCreated),

            TotalApiCallsThisMonth = await _dbContext.UsageRecords
                .Where(u => u.Year == now.Year && u.Month == now.Month)
                .SumAsync(u => u.ApiCalls),

            MonthlyRecurringRevenue = mrr
        };

        return Ok(stats);
    }

    /// <summary>
    /// Get recent activity across the platform
    /// </summary>
    [HttpGet("activity")]
    [ProducesResponseType(typeof(List<ActivityItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecentActivity([FromQuery] int limit = 50)
    {
        var recentOrgs = await _dbContext.Organizations
            .OrderByDescending(o => o.CreatedAt)
            .Take(limit)
            .Select(o => new ActivityItem
            {
                Type = "organization_created",
                Description = $"New organization: {o.Name}",
                EntityId = o.Id,
                EntityName = o.Name,
                Timestamp = o.CreatedAt
            })
            .ToListAsync();

        var recentSubscriptions = await _dbContext.Subscriptions
            .Include(s => s.Organization)
            .Include(s => s.Plan)
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .Select(s => new ActivityItem
            {
                Type = "subscription_created",
                Description = $"{s.Organization!.Name} subscribed to {s.Plan!.Name}",
                EntityId = s.Id,
                EntityName = s.Organization.Name,
                Timestamp = s.CreatedAt
            })
            .ToListAsync();

        var allActivity = recentOrgs
            .Concat(recentSubscriptions)
            .OrderByDescending(a => a.Timestamp)
            .Take(limit)
            .ToList();

        return Ok(allActivity);
    }

    #endregion

    #region Subscription Plans Management

    /// <summary>
    /// Get all subscription plans
    /// </summary>
    [HttpGet("plans")]
    [ProducesResponseType(typeof(List<PlanDetails>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlans()
    {
        // SubscriptionPlan rows now serve two purposes (see the modular subscription plan):
        // the legacy Free/Starter/Professional/Enterprise tier catalog this endpoint has always
        // shown, and the 4 new module-catalog rows (Code in ModuleCodes.All). Excluding the
        // latter here — the "Manage Modules" panel is the real place to see and manage those,
        // and mixing both into one flat list read as one undifferentiated, confusing catalog.
        var plans = await _dbContext.SubscriptionPlans
            .Where(p => !ModuleCodes.All.Contains(p.Code))
            .OrderBy(p => p.SortOrder)
            .Select(p => new PlanDetails
            {
                Id = p.Id,
                Name = p.Name,
                Code = p.Code,
                Description = p.Description,
                MonthlyPriceUsd = p.MonthlyPriceUsd,
                AnnualPriceUsd = p.AnnualPriceUsd,
                MonthlyPriceUgx = p.MonthlyPriceUgx,
                AnnualPriceUgx = p.AnnualPriceUgx,
                MaxBranches = p.MaxBranches,
                MaxUsersPerBranch = p.MaxUsersPerBranch,
                MaxTokensPerMonth = p.MaxTokensPerMonth,
                MaxApiCallsPerMonth = p.MaxApiCallsPerMonth,
                ShowAds = p.ShowAds,
                IsPublic = p.IsPublic,
                ActiveSubscriptions = p.Subscriptions.Count(s => s.Status == SubscriptionStatus.Active)
            })
            .ToListAsync();

        return Ok(plans);
    }

    #endregion
}

#region Request/Response Models

public record PagedResult<T>
{
    public List<T> Items { get; init; } = new();
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }
}

public record TenantSummary
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public TenantStatus Status { get; init; }
    public TenantTier Tier { get; init; }
    public string? ContactEmail { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? TrialEndsAt { get; init; }
    public bool OnboardingCompleted { get; init; }
}

public record TenantDetails : TenantSummary
{
    public string? BrandName { get; init; }
    public string? ContactPhone { get; init; }
    public string? BillingEmail { get; init; }
    public string? BillingPhone { get; init; }
    public string PreferredCurrency { get; init; } = "USD";
    public string? CustomDomain { get; init; }
    public string? StripeCustomerId { get; init; }
    public DateTime? VerifiedAt { get; init; }
    public int OnboardingStep { get; init; }
    public IndustryType IndustryType { get; init; }
    public int BranchCount { get; init; }
    public int UserCount { get; init; }
    public SubscriptionSummary? Subscription { get; init; }
    public UsageSummary? Usage { get; init; }
}

public record SubscriptionSummary
{
    public Guid Id { get; init; }
    public string PlanName { get; init; } = string.Empty;
    public SubscriptionStatus Status { get; init; }
    public BillingCycle BillingCycle { get; init; }
    public DateTime CurrentPeriodEnd { get; init; }
}

public record UsageSummary
{
    public int TokensCreated { get; init; }
    public int ApiCalls { get; init; }
    public int ActiveUsers { get; init; }
    public int ActiveBranches { get; init; }
    public int MaxTokens { get; init; }
    public int MaxApiCalls { get; init; }
    public int MaxUsers { get; init; }
    public int MaxBranches { get; init; }
    public long StorageUsedMb { get; init; }
    public int MaxStorageMb { get; init; }
    public int? StorageQuotaOverrideMb { get; init; }
}

public record SuspendRequest
{
    public string Reason { get; init; } = string.Empty;
}

public record UpdateStorageQuotaRequest
{
    /// <summary>Per-tenant storage quota override in MB. Null clears the override and falls
    /// back to the subscription plan's own default (SubscriptionPlan.MaxStorageMb).</summary>
    public int? MaxStorageMb { get; init; }
}

public record UpdateTierRequest
{
    public TenantTier Tier { get; init; }
}

public record ExtendTrialRequest
{
    public int Days { get; init; } = 14;
}

public record PlatformStats
{
    public int TotalOrganizations { get; init; }
    public int ActiveOrganizations { get; init; }
    public int TrialingOrganizations { get; init; }
    public int SuspendedOrganizations { get; init; }
    public int TotalUsers { get; init; }
    public int TotalBranches { get; init; }
    public List<TierCount> OrganizationsByTier { get; init; } = new();
    public int NewOrganizationsThisMonth { get; init; }
    public int NewOrganizationsLastMonth { get; init; }
    public int TotalTokensThisMonth { get; init; }
    public int TotalApiCallsThisMonth { get; init; }
    public decimal MonthlyRecurringRevenue { get; init; }
}

public record TierCount
{
    public TenantTier Tier { get; init; }
    public int Count { get; init; }
}

public record ActivityItem
{
    public string Type { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public Guid EntityId { get; init; }
    public string EntityName { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
}

public record PlanDetails
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string? Description { get; init; }
    public decimal MonthlyPriceUsd { get; init; }
    public decimal AnnualPriceUsd { get; init; }
    public decimal MonthlyPriceUgx { get; init; }
    public decimal AnnualPriceUgx { get; init; }
    public int MaxBranches { get; init; }
    public int MaxUsersPerBranch { get; init; }
    public int MaxTokensPerMonth { get; init; }
    public int MaxApiCallsPerMonth { get; init; }
    public bool ShowAds { get; init; }
    public bool IsPublic { get; init; }
    public int ActiveSubscriptions { get; init; }
}

#endregion
