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

            // The module catalog — the purchasable SubscriptionPlan rows.
            // See ModuleAccessService.
            await SeedModulesAsync();

            await SeedVisitorSafeguardingSplitAsync();

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

            _logger.LogInformation("Database seeded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding database");
            throw;
        }
    }

    /// </summary>
    private async Task SeedModulesAsync()
    {
        var modules = new Dictionary<string, ModuleDefaults>
        {
            [ModuleCodes.CoreQueue] = new(
                Name: "Core Queue Management", Code: ModuleCodes.CoreQueue, ShowAds: false, DedicatedSchema: false,
                MaxBranches: 5, MaxDisplays: 1, MaxUsersPerBranch: 10, MaxCountersPerBranch: 10,
                MaxTokensPerMonth: 20_000, MaxApiCallsPerMonth: 5_000, MaxStorageMb: 500,
                MonthlyPriceUsd: 19m, AnnualPriceUsd: 190m, MonthlyPriceUgx: 80_000m, AnnualPriceUgx: 800_000m,
                Description: "Live queue board, counter terminal, self-service kiosk, customer display, counters, service types, and tokens.",
                Badge: null, SortOrder: 0),
            [ModuleCodes.EngagementCommunications] = new(
                Name: "Engagement & Communications", Code: ModuleCodes.EngagementCommunications, ShowAds: false, DedicatedSchema: false,
                MaxBranches: 5, MaxDisplays: 10, MaxUsersPerBranch: 10, MaxCountersPerBranch: 2,
                MaxTokensPerMonth: 1_000, MaxApiCallsPerMonth: 5_000, MaxStorageMb: 5_000,
                MonthlyPriceUsd: 29m, AnnualPriceUsd: 290m, MonthlyPriceUgx: 120_000m, AnnualPriceUgx: 1_200_000m,
                Description: "Digital signage, campaign marketing (SMS/WhatsApp/email broadcasts), and customer feedback & surveys.",
                Badge: "Most Popular", SortOrder: 1),
            // "Visitor & Safeguarding" used to be one module covering both of these. It was split
            // because they sell to different people: a bank or clinic wants a visitor book and has
            // no use for a student welfare ledger, while "safeguarding" is education-sector
            // language those buyers do not recognise. Priced so either half alone costs less than
            // the old bundle, and a school buying both pays slightly more than before for what is
            // now materially more feature.
            [ModuleCodes.VisitorManagement] = new(
                Name: "Visitor Management", Code: ModuleCodes.VisitorManagement, ShowAds: false, DedicatedSchema: false,
                MaxBranches: 5, MaxDisplays: 1, MaxUsersPerBranch: 10, MaxCountersPerBranch: 2,
                MaxTokensPerMonth: 1_000, MaxApiCallsPerMonth: 5_000, MaxStorageMb: 1_000,
                MonthlyPriceUsd: 22m, AnnualPriceUsd: 220m, MonthlyPriceUgx: 95_000m, AnnualPriceUgx: 950_000m,
                Description: "Visitor check-in and check-out, badges and group passes, pre-registered arrivals, watchlist and contractor induction, and the evacuation roll-call.",
                Badge: null, SortOrder: 2),
            [ModuleCodes.StudentWelfare] = new(
                Name: "Student Welfare", Code: ModuleCodes.StudentWelfare, ShowAds: false, DedicatedSchema: false,
                MaxBranches: 5, MaxDisplays: 1, MaxUsersPerBranch: 10, MaxCountersPerBranch: 2,
                MaxTokensPerMonth: 1_000, MaxApiCallsPerMonth: 5_000, MaxStorageMb: 2_000,
                MonthlyPriceUsd: 25m, AnnualPriceUsd: 250m, MonthlyPriceUgx: 105_000m, AnnualPriceUgx: 1_050_000m,
                Description: "Student roster and guardians, visiting-day passes, and the welfare ledger: achievements, behaviour, safeguarding concerns, assigned actions, statements and reports.",
                Badge: "For schools", SortOrder: 3),
            [ModuleCodes.IntegrationsApi] = new(
                Name: "Integrations & API Access", Code: ModuleCodes.IntegrationsApi, ShowAds: false, DedicatedSchema: false,
                MaxBranches: 3, MaxDisplays: 1, MaxUsersPerBranch: 5, MaxCountersPerBranch: 2,
                MaxTokensPerMonth: 1_000, MaxApiCallsPerMonth: 100_000, MaxStorageMb: 200,
                MonthlyPriceUsd: 15m, AnnualPriceUsd: 150m, MonthlyPriceUgx: 60_000m, AnnualPriceUgx: 600_000m,
                Description: "API clients, webhooks, and partner integration adapters (hospital/pharmacy/banking).",
                Badge: null, SortOrder: 4),
        };

        var changed = false;
        foreach (var (code, defaults) in modules)
        {
            var existing = await _context.SubscriptionPlans.FirstOrDefaultAsync(p => p.Code == code);
            if (existing == null)
            {
                _context.SubscriptionPlans.Add(new SubscriptionPlan
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
                changed = true;
                _logger.LogInformation("Seeded {ModuleName} module into the catalog", defaults.Name);
            }
        }

        if (changed)
            await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Moves anyone holding the retired "Visitor &amp; Safeguarding" module onto both of its
    /// successors, Visitor Management and Student Welfare, so the split costs nobody access to
    /// something they already paid for.
    /// <para>
    /// Runs on every boot and is idempotent: it only adds a successor row where one does not
    /// already exist, and it carries the original row's status, activation date, trial end and
    /// billing cycle across rather than resetting anyone to a fresh trial. The old row is left in
    /// place and its plan is retired from the catalog by <see cref="SeedModulesAsync"/> being the
    /// only writer of module rows — deleting it would break the audit trail of what was bought.
    /// </para>
    /// </summary>
    private async Task SeedVisitorSafeguardingSplitAsync()
    {
        var legacyPlan = await _context.SubscriptionPlans
            .FirstOrDefaultAsync(p => p.Code == ModuleCodes.LegacyVisitorSafeguarding);
        if (legacyPlan == null) return;

        var legacyGrants = await _context.OrganizationModules
            .Where(om => om.ModuleId == legacyPlan.Id)
            .ToListAsync();
        if (legacyGrants.Count == 0) return;

        var successorPlans = await _context.SubscriptionPlans
            .Where(p => ModuleCodes.LegacyVisitorSafeguardingSuccessors.Contains(p.Code))
            .ToListAsync();
        if (successorPlans.Count == 0)
        {
            _logger.LogWarning(
                "Cannot split the retired visitor-safeguarding module: neither successor plan is seeded yet");
            return;
        }

        var existing = (await _context.OrganizationModules
            .Where(om => successorPlans.Select(p => p.Id).Contains(om.ModuleId))
            .Select(om => new { om.OrganizationId, om.ModuleId })
            .ToListAsync())
            .Select(x => (x.OrganizationId, x.ModuleId))
            .ToHashSet();

        var added = 0;
        foreach (var grant in legacyGrants)
        {
            foreach (var plan in successorPlans)
            {
                if (existing.Contains((grant.OrganizationId, plan.Id))) continue;

                _context.OrganizationModules.Add(new OrganizationModule
                {
                    OrganizationId = grant.OrganizationId,
                    ModuleId = plan.Id,
                    Status = grant.Status,
                    ActivatedAt = grant.ActivatedAt,
                    TrialEndsAt = grant.TrialEndsAt,
                    BillingCycle = grant.BillingCycle,
                    GrantedByPlatformAdmin = true,
                    AdminNote = "Carried over automatically when Visitor & Safeguarding was split into Visitor Management and Student Welfare"
                });
                added++;
            }
        }

        // Retire the old plan row once its holders are safely on the successors. GetCatalogAsync
        // already filters to ModuleCodes.All, which no longer lists this code, so it could not be
        // bought either way — but leaving an active-looking row behind invites the next person to
        // query the table directly and conclude it is still on sale.
        var retired = false;
        if (legacyPlan.IsActive)
        {
            legacyPlan.IsActive = false;
            legacyPlan.Description =
                "Retired. Split into Visitor Management and Student Welfare; existing holders were granted both.";
            retired = true;
        }

        if (added == 0 && !retired) return;

        await _context.SaveChangesAsync();
        _logger.LogInformation(
            "Split the retired visitor-safeguarding module into its two successors for {Count} organization grant(s)",
            added);
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

    /// <summary>The shape of one module's seeded defaults. Named for the tiers it once described;
    /// every row it seeds is now a module.</summary>
    private record ModuleDefaults(
        string Name, string Code, bool ShowAds, bool DedicatedSchema,
        int MaxBranches, int MaxDisplays, int MaxUsersPerBranch, int MaxCountersPerBranch,
        int MaxTokensPerMonth, int MaxApiCallsPerMonth, int MaxStorageMb,
        decimal MonthlyPriceUsd, decimal AnnualPriceUsd, decimal MonthlyPriceUgx, decimal AnnualPriceUgx,
        string? Description, string? Badge, int SortOrder);
}
