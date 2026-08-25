using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Billing;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Entities.Organization;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Enums;

namespace QMgr.Infrastructure.Data;

public class DbSeeder
{
    private readonly QMgrDbContext _context;
    private readonly ILogger<DbSeeder> _logger;

    public DbSeeder(QMgrDbContext context, ILogger<DbSeeder> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task SeedAsync()
    {
        try
        {
            // Always seed RBAC data (permissions and system roles)
            await SeedRbacDataAsync();

            // Always ensure every non-Free-tier organization has a real active Subscription
            // backing its displayed Tier — otherwise FeatureFlagService silently treats it as
            // Free-tier (zero entitlements) regardless of what Tier the admin UI shows.
            await SeedSubscriptionsAsync();

            // Check if demo data already exists (exclude platform org)
            var platformOrgId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
            if (await _context.Organizations.AnyAsync(o => o.Id != platformOrgId))
            {
                _logger.LogInformation("Database already seeded");
                return;
            }

            _logger.LogInformation("Seeding database...");

            // Create Organization
            var orgId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var organization = new Domain.Entities.Organization.Organization
            {
                Id = orgId,
                Name = "Demo Organization",
                BrandName = "Q-Mgr Demo",
                ContactEmail = "admin@qmgr.demo",
                Slug = "demo",
                Status = TenantStatus.Active,
                Tier = TenantTier.Professional,
                OnboardingCompleted = true,
                VerifiedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            _context.Organizations.Add(organization);

            // Create Branch
            var branchId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var branch = new Branch
            {
                Id = branchId,
                OrganizationId = orgId,
                Name = "Main Branch",
                Code = "MAIN",
                Address = "123 Demo Street",
                Timezone = "UTC",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            _context.Branches.Add(branch);

            // Create Service Types
            var serviceTypes = new List<ServiceType>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    BranchId = branchId,
                    Name = "General Inquiry",
                    Code = "GEN",
                    Prefix = "G",
                    Description = "General customer inquiries",
                    AverageServiceTimeMinutes = 5,
                    Priority = 1,
                    Color = "#4CAF50",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    BranchId = branchId,
                    Name = "Account Services",
                    Code = "ACC",
                    Prefix = "A",
                    Description = "Account opening, closing, modifications",
                    AverageServiceTimeMinutes = 15,
                    Priority = 2,
                    Color = "#2196F3",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    BranchId = branchId,
                    Name = "Teller Services",
                    Code = "TELL",
                    Prefix = "T",
                    Description = "Cash deposits, withdrawals, transfers",
                    AverageServiceTimeMinutes = 8,
                    Priority = 3,
                    Color = "#FF9800",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    BranchId = branchId,
                    Name = "Loan Applications",
                    Code = "LOAN",
                    Prefix = "L",
                    Description = "Personal and business loans",
                    AverageServiceTimeMinutes = 30,
                    Priority = 4,
                    Color = "#9C27B0",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    BranchId = branchId,
                    Name = "Customer Support",
                    Code = "CS",
                    Prefix = "C",
                    Description = "Customer support and complaints",
                    AverageServiceTimeMinutes = 10,
                    Priority = 5,
                    Color = "#E91E63",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                }
            };
            _context.ServiceTypes.AddRange(serviceTypes);

            // Create Counters
            var counters = new List<Counter>();
            for (int i = 1; i <= 5; i++)
            {
                var counter = new Counter
                {
                    Id = Guid.NewGuid(),
                    BranchId = branchId,
                    CounterNumber = i.ToString(),
                    DisplayName = $"Counter {i}",
                    Status = i <= 3 ? CounterStatus.Active : CounterStatus.Closed,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                counters.Add(counter);
            }
            _context.Counters.AddRange(counters);

            await _context.SaveChangesAsync();

            // Create CounterServiceTypes (assign service types to counters)
            var counterServiceTypes = new List<CounterServiceType>();
            foreach (var counter in counters)
            {
                foreach (var serviceType in serviceTypes)
                {
                    counterServiceTypes.Add(new CounterServiceType
                    {
                        Id = Guid.NewGuid(),
                        CounterId = counter.Id,
                        ServiceTypeId = serviceType.Id,
                        Priority = serviceType.Priority,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
            _context.CounterServiceTypes.AddRange(counterServiceTypes);

            // Create some sample tokens
            var random = new Random();
            var tokenNumber = 1;
            foreach (var serviceType in serviceTypes.Take(3))
            {
                for (int i = 0; i < random.Next(2, 5); i++)
                {
                    var token = new Token
                    {
                        Id = Guid.NewGuid(),
                        BranchId = branchId,
                        ServiceTypeId = serviceType.Id,
                        TokenNumber = tokenNumber.ToString("D4"),
                        DisplayNumber = $"{serviceType.Prefix}{tokenNumber:D3}",
                        Status = TokenStatus.Waiting,
                        Priority = i == 0 ? TokenPriority.Priority : TokenPriority.Normal,
                        Source = TokenSource.Kiosk,
                        CustomerName = $"Customer {tokenNumber}",
                        CustomerPhone = $"555-{tokenNumber:D4}",
                        EstimatedWaitMinutes = serviceType.AverageServiceTimeMinutes * (i + 1),
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow.AddMinutes(-random.Next(5, 30))
                    };
                    _context.Tokens.Add(token);
                    tokenNumber++;
                }
            }

            // Get role IDs for user creation
            var adminRole = await _context.Roles.FirstAsync(r => r.Code == RoleCodes.Admin && r.OrganizationId == null);
            var staffRole = await _context.Roles.FirstAsync(r => r.Code == RoleCodes.Staff && r.OrganizationId == null);

            // Note: SuperAdmin user is created in SeedRbacDataAsync() to ensure it's always available

            // Create Admin User (organization-level)
            var adminUser = new User
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                Username = "admin",
                Email = "admin@qmgr.demo",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
                FirstName = "System",
                LastName = "Administrator",
                RoleId = adminRole.Id,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            _context.Users.Add(adminUser);

            // Create Staff User
            var staffUser = new User
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                Username = "agent1",
                Email = "agent1@qmgr.demo",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("agent123"),
                FirstName = "John",
                LastName = "Agent",
                RoleId = staffRole.Id,
                AssignedBranchId = branchId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            _context.Users.Add(staffUser);

            await _context.SaveChangesAsync();

            // Covers the freshly-created Demo Organization above (the earlier call in SeedAsync
            // only saw orgs that existed before this method ran).
            await SeedSubscriptionsAsync();

            _logger.LogInformation("Database seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding database");
            throw;
        }
    }

    /// <summary>
    /// Ensures every organization on a paid Tier (Starter/Professional/Enterprise) has a real,
    /// active Subscription linked to a matching SubscriptionPlan. Idempotent — safe to call on
    /// every startup. Free-tier organizations are intentionally left without a subscription
    /// (FeatureFlagService's no-subscription fallback IS the free tier).
    /// </summary>
    private async Task SeedSubscriptionsAsync()
    {
        var tierPlans = new Dictionary<TenantTier, TierPlanDefaults>
        {
            [TenantTier.Free] = new(
                Name: "Free", Code: "free", ShowAds: true, DedicatedSchema: false,
                MaxBranches: 1, MaxDisplays: 1, MaxUsersPerBranch: 3, MaxCountersPerBranch: 2,
                MaxTokensPerMonth: 500, MaxApiCallsPerMonth: 1_000, MaxStorageMb: 100,
                MonthlyPriceUsd: 0, AnnualPriceUsd: 0, MonthlyPriceUgx: 0, AnnualPriceUgx: 0,
                Description: "For trying Q-Mgr out with a single branch.", Badge: null, SortOrder: 0),
            [TenantTier.Starter] = new(
                Name: "Starter", Code: "starter", ShowAds: true, DedicatedSchema: false,
                MaxBranches: 3, MaxDisplays: 2, MaxUsersPerBranch: 5, MaxCountersPerBranch: 5,
                MaxTokensPerMonth: 5_000, MaxApiCallsPerMonth: 10_000, MaxStorageMb: 1_000,
                MonthlyPriceUsd: 29m, AnnualPriceUsd: 290m, MonthlyPriceUgx: 110_000m, AnnualPriceUgx: 1_100_000m,
                Description: "For a single growing business with a few branches.", Badge: null, SortOrder: 1),
            [TenantTier.Professional] = new(
                Name: "Professional", Code: "professional", ShowAds: false, DedicatedSchema: false,
                MaxBranches: 10, MaxDisplays: 5, MaxUsersPerBranch: 15, MaxCountersPerBranch: 15,
                MaxTokensPerMonth: 50_000, MaxApiCallsPerMonth: 100_000, MaxStorageMb: 10_000,
                MonthlyPriceUsd: 79m, AnnualPriceUsd: 790m, MonthlyPriceUgx: 300_000m, AnnualPriceUgx: 3_000_000m,
                Description: "For multi-branch operations that need analytics and SMS.", Badge: "Most Popular", SortOrder: 2),
            [TenantTier.Enterprise] = new(
                Name: "Enterprise", Code: "enterprise", ShowAds: false, DedicatedSchema: true,
                MaxBranches: 50, MaxDisplays: 25, MaxUsersPerBranch: 100, MaxCountersPerBranch: 100,
                MaxTokensPerMonth: 500_000, MaxApiCallsPerMonth: 1_000_000, MaxStorageMb: 100_000,
                MonthlyPriceUsd: 199m, AnnualPriceUsd: 1_990m, MonthlyPriceUgx: 750_000m, AnnualPriceUgx: 7_500_000m,
                Description: "For large, multi-region deployments needing a dedicated schema.", Badge: null, SortOrder: 3),
        };

        var changed = false;

        // Ensure the full plan catalog (Free/Starter/Professional/Enterprise) always exists, (Free/Starter/Professional/Enterprise) always exists,
        // independent of whether any organization currently sits on that tier. Previously the
        // only place a SubscriptionPlan row was created was inside the per-org loop below, which
        // only runs for orgs already on a paid tier — so a plan a customer had never been put on
        // (e.g. Starter, if every seeded org happened to be Free or Enterprise) would never exist
        // at all, and GET api/v1/billing/plans (the public "Available Plans" list every trialing
        // customer sees to pick a plan) silently showed only whichever plans happened to have
        // been created as that side effect. Caught live: a fresh trial org saw exactly one
        // plan card ("Enterprise") on the upgrade page.
        foreach (var (tier, defaults) in tierPlans)
        {
            var existing = await _context.SubscriptionPlans.FirstOrDefaultAsync(p => p.Code == defaults.Code);
            if (existing == null)
            {
                _context.SubscriptionPlans.Add(new SubscriptionPlan
                {
                    Id = Guid.NewGuid(),
                    Name = defaults.Name,
                    Code = defaults.Code,
                    Description = defaults.Description,
                    Tier = tier,
                    ShowAds = defaults.ShowAds,
                    RequiresDedicatedSchema = defaults.DedicatedSchema,
                    IsPublic = true,
                    SortOrder = defaults.SortOrder,
                    Badge = defaults.Badge,
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
                    Features = BuildFeaturesJson(tier),
                    CreatedAt = DateTime.UtcNow
                });
                changed = true;
                _logger.LogInformation("Seeded {Tier} subscription plan into the public catalog", tier);
            }
            else if (existing.MonthlyPriceUsd == 0 && defaults.MonthlyPriceUsd > 0)
            {
                // Backfills pricing on a plan row that was created before this method set prices
                // (e.g. the Enterprise row auto-created by the per-org loop below, pre-fix).
                existing.Description ??= defaults.Description;
                existing.Badge ??= defaults.Badge;
                existing.SortOrder = defaults.SortOrder;
                existing.MonthlyPriceUsd = defaults.MonthlyPriceUsd;
                existing.AnnualPriceUsd = defaults.AnnualPriceUsd;
                existing.MonthlyPriceUgx = defaults.MonthlyPriceUgx;
                existing.AnnualPriceUgx = defaults.AnnualPriceUgx;
                changed = true;
                _logger.LogInformation("Backfilled pricing on the existing {Tier} subscription plan", tier);
            }
        }

        // Flush now — the sync/per-org queries below hit the database directly (FirstOrDefaultAsync
        // over a DbSet issues real SQL), so a plan added above but not yet saved would be invisible
        // to them and get re-created as a duplicate row with the same Code.
        if (changed)
        {
            await _context.SaveChangesAsync();
        }

        // Sync numeric limits + Features on every plan this method manages (matched by Code) to
        // the tier-appropriate values above — independent of the per-org loop below, since that
        // loop skips orgs that already have a subscription and would otherwise never revisit an
        // already-linked plan. Safe to run unconditionally every startup: this seeder is these
        // plans' sole owner (no admin UI edits SubscriptionPlan rows).
        //
        // This exists because the first version of this method only set Tier/ShowAds/
        // RequiresDedicatedSchema/Features on creation, leaving every numeric limit (MaxBranches,
        // MaxTokensPerMonth, etc.) at SubscriptionPlan's raw field defaults — Free-tier-level
        // values — regardless of the plan's real tier. Caught live: "Platform Administration"
        // (Enterprise) was capped at 1 branch, the same limit as the Free tier, via Tenant
        // Management's own usage-stats display.
        var knownCodes = tierPlans.Values.Select(p => p.Code).ToList();
        var managedPlans = await _context.SubscriptionPlans
            .Where(p => knownCodes.Contains(p.Code))
            .ToListAsync();
        foreach (var managedPlan in managedPlans)
        {
            var defaults = tierPlans.Values.First(p => p.Code == managedPlan.Code);
            if (managedPlan.Features == null)
            {
                managedPlan.Features = BuildFeaturesJson(managedPlan.Tier);
                changed = true;
            }
            if (managedPlan.MaxBranches != defaults.MaxBranches ||
                managedPlan.MaxDisplays != defaults.MaxDisplays ||
                managedPlan.MaxUsersPerBranch != defaults.MaxUsersPerBranch ||
                managedPlan.MaxCountersPerBranch != defaults.MaxCountersPerBranch ||
                managedPlan.MaxTokensPerMonth != defaults.MaxTokensPerMonth ||
                managedPlan.MaxApiCallsPerMonth != defaults.MaxApiCallsPerMonth ||
                managedPlan.MaxStorageMb != defaults.MaxStorageMb)
            {
                managedPlan.MaxBranches = defaults.MaxBranches;
                managedPlan.MaxDisplays = defaults.MaxDisplays;
                managedPlan.MaxUsersPerBranch = defaults.MaxUsersPerBranch;
                managedPlan.MaxCountersPerBranch = defaults.MaxCountersPerBranch;
                managedPlan.MaxTokensPerMonth = defaults.MaxTokensPerMonth;
                managedPlan.MaxApiCallsPerMonth = defaults.MaxApiCallsPerMonth;
                managedPlan.MaxStorageMb = defaults.MaxStorageMb;
                changed = true;
                _logger.LogInformation("Corrected usage limits on {PlanName} plan to match its {Tier} tier", managedPlan.Name, managedPlan.Tier);
            }
        }

        var orgsOnPaidTiers = await _context.Organizations
            .Where(o => o.Tier != TenantTier.Free)
            .ToListAsync();

        foreach (var org in orgsOnPaidTiers)
        {
            if (!tierPlans.TryGetValue(org.Tier, out var planInfo))
                continue;

            // Deliberately "any subscription row at all", NOT "any Active/Trialing row" — this
            // loop's job is to backfill a subscription for an org that has never had one (e.g.
            // legacy/migrated data), not to paper over one that's PastDue/Suspended/Cancelled/
            // Expired for a real reason. The previous, narrower check re-created a brand new
            // decade-long Active subscription for ANY paid-tier org lacking an Active/Trialing
            // row, on every single API restart — including one that had just been correctly
            // suspended for a failed payment, silently undoing the suspension and reactivating
            // the org for free the next time the process restarted. Found live: suspended a test
            // org to simulate a failed payment, restarted the API for an unrelated fix, and found
            // a second brand-new Active Subscription row had appeared for it.
            var hasAnySubscription = await _context.Subscriptions.AnyAsync(s => s.OrganizationId == org.Id);
            if (hasAnySubscription)
                continue;

            // The plan-catalog step above guarantees a row for every tier in tierPlans already
            // exists (and is saved) by this point.
            var plan = await _context.SubscriptionPlans.FirstOrDefaultAsync(p => p.Code == planInfo.Code);
            if (plan == null)
            {
                _logger.LogWarning("No SubscriptionPlan found for code {Code} — skipping subscription seed for {OrgName}", planInfo.Code, org.Name);
                continue;
            }

            _context.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = org.Id,
                PlanId = plan.Id,
                Status = SubscriptionStatus.Active,
                BillingCycle = BillingCycle.Monthly,
                StartDate = DateTime.UtcNow,
                CurrentPeriodStart = DateTime.UtcNow,
                CurrentPeriodEnd = DateTime.UtcNow.AddYears(10),
                CreatedAt = DateTime.UtcNow
            });
            changed = true;
            _logger.LogInformation("Seeded {Tier} subscription for organization {OrgName} ({OrgId})", org.Tier, org.Name, org.Id);
        }

        if (changed)
            await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Mirrors FeatureFlagService.BuildFeaturesFromPlan's tier switch exactly, so a seeded
    /// plan's Features JSON (which BillingService/UsageLimitMiddleware read) agrees with what
    /// FeatureFlagService itself would compute for the same tier.
    /// </summary>
    private static string BuildFeaturesJson(TenantTier tier)
    {
        var (apiAccess, sms, email, push, branding, whiteLabel, analytics, export, displays, serviceTypes, support, dedicated, webhook) = tier switch
        {
            TenantTier.Free => (false, false, false, false, false, false, false, false, false, false, false, false, false),
            TenantTier.Starter => (true, false, true, false, false, false, false, true, false, true, false, false, true),
            TenantTier.Professional => (true, true, true, true, true, false, true, true, true, true, false, false, true),
            TenantTier.Enterprise => (true, true, true, true, true, true, true, true, true, true, true, true, true),
            _ => (false, false, false, false, false, false, false, false, false, false, false, false, false)
        };

        var features = new Dictionary<string, bool>
        {
            [FeatureCodes.ApiAccess] = apiAccess,
            [FeatureCodes.SmsNotifications] = sms,
            [FeatureCodes.EmailNotifications] = email,
            [FeatureCodes.PushNotifications] = push,
            [FeatureCodes.CustomBranding] = branding,
            [FeatureCodes.WhiteLabel] = whiteLabel,
            [FeatureCodes.AdvancedAnalytics] = analytics,
            [FeatureCodes.ExportReports] = export,
            [FeatureCodes.MultipleDisplays] = displays,
            [FeatureCodes.CustomServiceTypes] = serviceTypes,
            [FeatureCodes.PrioritySupport] = support,
            [FeatureCodes.DedicatedSchema] = dedicated,
            [FeatureCodes.WebhookIntegration] = webhook
        };

        return System.Text.Json.JsonSerializer.Serialize(features);
    }

    /// <summary>
    /// Seeds permissions and system roles. This is idempotent and can be run multiple times.
    /// </summary>
    private async Task SeedRbacDataAsync()
    {
        var permissionsAdded = false;
        var rolesAdded = false;
        var rolePermissionsAdded = false;

        // Seed permissions
        var existingPermissions = await _context.Permissions
            .Select(p => p.Code)
            .ToHashSetAsync();

        foreach (var permDef in Permissions.All)
        {
            if (!existingPermissions.Contains(permDef.Code))
            {
                _context.Permissions.Add(new Permission
                {
                    Id = Guid.NewGuid(),
                    Code = permDef.Code,
                    Name = permDef.Name,
                    Description = permDef.Description,
                    Category = permDef.Category,
                    SortOrder = permDef.SortOrder,
                    IsVisible = permDef.IsVisible,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                permissionsAdded = true;
            }
        }

        if (permissionsAdded)
        {
            await _context.SaveChangesAsync();
            _logger.LogInformation("Permissions seeded");
        }

        // Get permission lookup (after potential seeding)
        var permissionLookup = await _context.Permissions
            .ToDictionaryAsync(p => p.Code, p => p.Id);

        // Seed system roles (OrganizationId = null)
        var existingRoles = await _context.Roles
            .Where(r => r.OrganizationId == null)
            .ToDictionaryAsync(r => r.Code, r => r.Id);

        foreach (var (roleCode, roleDef) in Permissions.DefaultRoles)
        {
            if (!existingRoles.ContainsKey(roleCode))
            {
                var role = new Role
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = null, // System-wide role
                    Name = roleDef.Name,
                    Code = roleDef.Code,
                    Description = roleDef.Description,
                    Color = roleDef.Color,
                    Icon = roleDef.Icon,
                    SortOrder = roleDef.SortOrder,
                    IsSystem = true,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                _context.Roles.Add(role);
                existingRoles[roleCode] = role.Id; // Add to lookup for permissions
                rolesAdded = true;
            }
        }

        if (rolesAdded)
        {
            await _context.SaveChangesAsync();
            _logger.LogInformation("System roles seeded");
        }

        // Seed role permissions for all system roles (handles both new and existing roles)
        var existingRolePermissions = await _context.RolePermissions
            .Select(rp => new { rp.RoleId, rp.PermissionId })
            .ToListAsync();

        var existingRolePermissionSet = existingRolePermissions
            .Select(rp => (rp.RoleId, rp.PermissionId))
            .ToHashSet();

        foreach (var (roleCode, roleDef) in Permissions.DefaultRoles)
        {
            if (!existingRoles.TryGetValue(roleCode, out var roleId))
                continue;

            foreach (var permCode in roleDef.Permissions)
            {
                if (!permissionLookup.TryGetValue(permCode, out var permId))
                    continue;

                if (!existingRolePermissionSet.Contains((roleId, permId)))
                {
                    _context.RolePermissions.Add(new RolePermission
                    {
                        RoleId = roleId,
                        PermissionId = permId,
                        GrantedAt = DateTime.UtcNow
                    });
                    rolePermissionsAdded = true;
                }
            }
        }

        if (rolePermissionsAdded)
        {
            await _context.SaveChangesAsync();
            _logger.LogInformation("Role permissions seeded");
        }

        // Seed platform SuperAdmin user (idempotent)
        // SuperAdmin should be created regardless of organization existence
        var superAdminExists = await _context.Users.AnyAsync(u => u.Username == "superadmin");
        if (!superAdminExists)
        {
            var superAdminRole = await _context.Roles.FirstOrDefaultAsync(r => r.Code == RoleCodes.SuperAdmin && r.OrganizationId == null);
            if (superAdminRole != null)
            {
                // Get first organization or create a platform organization
                var org = await _context.Organizations.FirstOrDefaultAsync();
                if (org == null)
                {
                    // Create a platform organization for SuperAdmin
                    org = new Domain.Entities.Organization.Organization
                    {
                        Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                        Name = "Platform Administration",
                        BrandName = "Q-Mgr Platform",
                        ContactEmail = "admin@qmgr.platform",
                        Slug = "platform",
                        Status = TenantStatus.Active,
                        Tier = TenantTier.Enterprise,
                        OnboardingCompleted = true,
                        VerifiedAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.Organizations.Add(org);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Platform organization created");
                }

                var superAdminUser = new User
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = org.Id, // Associated with platform org but has platform-wide permissions
                    Username = "superadmin",
                    Email = "support@getsacc.com",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin"),
                    FirstName = "Platform",
                    LastName = "Administrator",
                    RoleId = superAdminRole.Id,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                _context.Users.Add(superAdminUser);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Platform SuperAdmin user seeded");
            }
        }
    }

    private record TierPlanDefaults(
        string Name, string Code, bool ShowAds, bool DedicatedSchema,
        int MaxBranches, int MaxDisplays, int MaxUsersPerBranch, int MaxCountersPerBranch,
        int MaxTokensPerMonth, int MaxApiCallsPerMonth, int MaxStorageMb,
        decimal MonthlyPriceUsd, decimal AnnualPriceUsd, decimal MonthlyPriceUgx, decimal AnnualPriceUgx,
        string? Description, string? Badge, int SortOrder);
}
