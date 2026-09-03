using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Entities.Organization;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Enums;
using QMgr.Domain.Interfaces;
using QMgr.Infrastructure.Data;
using Role = QMgr.Domain.Entities.Identity.Role;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Service for provisioning and managing tenant organizations
/// </summary>
public class TenantProvisioningService : ITenantProvisioningService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly QMgrDbContext _dbContext;
    private readonly IDistributedCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantProvisioningService> _logger;

    private const string VerificationTokenPrefix = "email_verify:";
    private const int VerificationTokenExpiryHours = 24;
    private const int TrialDaysDefault = 14;

    // Reserved slugs that cannot be used
    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "app", "admin", "dashboard", "login", "register", "signup",
        "billing", "support", "help", "docs", "www", "mail", "email",
        "static", "assets", "cdn", "img", "images", "css", "js",
        "qmgr", "queue", "test", "demo", "trial", "enterprise"
    };

    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly IEmailSender _emailSender;

    public TenantProvisioningService(
        IUnitOfWork unitOfWork,
        QMgrDbContext dbContext,
        IDistributedCache cache,
        IConfiguration configuration,
        IPlatformSettingsService platformSettingsService,
        IEmailSender emailSender,
        ILogger<TenantProvisioningService> logger)
    {
        _unitOfWork = unitOfWork;
        _dbContext = dbContext;
        _cache = cache;
        _configuration = configuration;
        _platformSettingsService = platformSettingsService;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task<TenantProvisioningResult> ProvisionTenantAsync(
        ProvisionTenantRequest request,
        CancellationToken cancellationToken = default)
    {
        // Check if email already exists
        var existingUser = await _unitOfWork.Users.FirstOrDefaultAsync(
            u => u.Email.ToLower() == request.AdminEmail.ToLower(),
            cancellationToken);

        if (existingUser != null)
        {
            return TenantProvisioningResult.Failed("An account with this email address already exists.");
        }

        // Generate or validate slug
        var slug = request.Slug;
        if (string.IsNullOrEmpty(slug))
        {
            slug = await GenerateUniqueSlugAsync(request.OrganizationName, cancellationToken);
        }
        else
        {
            if (!await ValidateSlugAvailabilityAsync(slug, cancellationToken))
            {
                return TenantProvisioningResult.Failed($"The slug '{slug}' is already in use.");
            }
        }

        Organization? organization = null;
        User? adminUser = null;
        string? verificationToken = null;

        // Was IConfiguration-only, completely disconnected from the "SaaS" PlatformSetting row
        // the admin UI actually edits. GetSettingsAsync is memory-cached (30 min, invalidated on
        // save). Resolved once here, outside the transaction below, rather than per-attempt inside it.
        var saasSettings = await _platformSettingsService.GetSettingsAsync<SaasSettings>("SaaS");
        var trialDays = saasSettings?.TrialDays ?? _configuration.GetValue("SaaS:TrialDays", TrialDaysDefault);

        try
        {
            await _unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                // Parse industry type
                var industryType = IndustryType.Service;
                if (!string.IsNullOrEmpty(request.IndustryType))
                {
                    Enum.TryParse<IndustryType>(request.IndustryType, true, out industryType);
                }

                // Calculate trial end date
                var trialEndsAt = DateTime.UtcNow.AddDays(trialDays);

                // Create organization
                organization = new Organization
                {
                    Name = request.OrganizationName,
                    Slug = slug,
                    ContactEmail = request.AdminEmail,
                    ContactPhone = request.ContactPhone,
                    BillingEmail = request.AdminEmail,
                    BillingPhone = request.AdminPhone,
                    PreferredCurrency = request.PreferredCurrency,
                    IndustryType = industryType,
                    Status = TenantStatus.Pending,
                    Tier = TenantTier.Free, // Start at free tier
                    TrialEndsAt = trialEndsAt,
                    OnboardingCompleted = false,
                    OnboardingStep = 0
                };

                await _unitOfWork.Organizations.AddAsync(organization, ct);
                await _unitOfWork.SaveChangesAsync(ct);

                // Get the admin role for new organization owner
                var adminRole = await _dbContext.Roles
                    .FirstOrDefaultAsync(r => r.Code == RoleCodes.Admin && r.OrganizationId == null, ct)
                    ?? throw new InvalidOperationException($"System '{RoleCodes.Admin}' role not found. Please run database seeding.");

                // Create admin user
                var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.AdminPassword);
                adminUser = new User
                {
                    OrganizationId = organization.Id,
                    Username = request.AdminEmail, // Use email as username
                    Email = request.AdminEmail,
                    PasswordHash = passwordHash,
                    FirstName = request.AdminFirstName,
                    LastName = request.AdminLastName,
                    Phone = request.AdminPhone,
                    RoleId = adminRole.Id, // Admin role for organization owner
                    IsActive = true
                };

                await _unitOfWork.Users.AddAsync(adminUser, ct);
                await _unitOfWork.SaveChangesAsync(ct);

                // Generate verification token
                verificationToken = await GenerateVerificationTokenAsync(organization.Id, ct);
            }, cancellationToken);

            _logger.LogInformation(
                "Provisioned new tenant: {OrganizationId} with slug {Slug} for {Email}",
                organization!.Id, slug, request.AdminEmail);

            return TenantProvisioningResult.Succeeded(
                organization.Id,
                adminUser!.Id,
                slug,
                verificationToken!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision tenant for {Email}", request.AdminEmail);
            return TenantProvisioningResult.Failed("An error occurred while creating your organization. Please try again.");
        }
    }

    public async Task SeedDefaultDataAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var organization = await _unitOfWork.Organizations.GetByIdAsync(organizationId, cancellationToken);
        if (organization == null)
        {
            throw new InvalidOperationException($"Organization {organizationId} not found.");
        }

        // Create default branch
        var branch = new Branch
        {
            OrganizationId = organizationId,
            Name = "Main Branch",
            Code = "MAIN",
            Address = organization.Address,
            Timezone = "Africa/Kampala", // Default for Uganda
            OperatingHours = "{\"monday\":{\"open\":\"08:00\",\"close\":\"17:00\"},\"tuesday\":{\"open\":\"08:00\",\"close\":\"17:00\"},\"wednesday\":{\"open\":\"08:00\",\"close\":\"17:00\"},\"thursday\":{\"open\":\"08:00\",\"close\":\"17:00\"},\"friday\":{\"open\":\"08:00\",\"close\":\"17:00\"},\"saturday\":{\"open\":\"09:00\",\"close\":\"13:00\"}}"
        };

        await _unitOfWork.Branches.AddAsync(branch, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Create default service types based on industry
        var serviceTypes = GetDefaultServiceTypes(organization.IndustryType, branch.Id);
        foreach (var serviceType in serviceTypes)
        {
            await _unitOfWork.ServiceTypes.AddAsync(serviceType, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Seeded default data for organization {OrganizationId}: 1 branch, {ServiceTypeCount} service types",
            organizationId, serviceTypes.Count);
    }

    public async Task<bool> ValidateSlugAvailabilityAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return false;

        // Check reserved slugs
        if (ReservedSlugs.Contains(slug))
            return false;

        // Check format
        if (!Regex.IsMatch(slug, @"^[a-z0-9][a-z0-9-]*[a-z0-9]$|^[a-z0-9]$"))
            return false;

        // Check length
        if (slug.Length < 3 || slug.Length > 50)
            return false;

        // Check database
        var exists = await _dbContext.Organizations
            .AnyAsync(o => o.Slug.ToLower() == slug.ToLower(), cancellationToken);

        return !exists;
    }

    public async Task<string> GenerateUniqueSlugAsync(string organizationName, CancellationToken cancellationToken = default)
    {
        // Generate base slug from name
        var baseSlug = GenerateSlugFromName(organizationName);

        // Check if available
        if (await ValidateSlugAvailabilityAsync(baseSlug, cancellationToken))
            return baseSlug;

        // Add random suffix
        for (int i = 0; i < 10; i++)
        {
            var suffix = GenerateRandomSuffix(4);
            var candidateSlug = $"{baseSlug}-{suffix}";

            if (candidateSlug.Length > 50)
                candidateSlug = $"{baseSlug[..Math.Min(baseSlug.Length, 45)]}-{suffix}";

            if (await ValidateSlugAvailabilityAsync(candidateSlug, cancellationToken))
                return candidateSlug;
        }

        // Fallback to UUID-based slug
        return $"org-{Guid.NewGuid().ToString("N")[..12]}";
    }

    public async Task<string> GenerateVerificationTokenAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        // Generate secure token
        var tokenBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(tokenBytes);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        // Store in cache with expiry
        var cacheKey = $"{VerificationTokenPrefix}{organizationId}";
        await _cache.SetStringAsync(
            cacheKey,
            token,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(VerificationTokenExpiryHours)
            },
            cancellationToken);

        return token;
    }

    public async Task<bool> VerifyEmailAsync(Guid organizationId, string verificationToken, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{VerificationTokenPrefix}{organizationId}";
        var storedToken = await _cache.GetStringAsync(cacheKey, cancellationToken);

        if (string.IsNullOrEmpty(storedToken) || storedToken != verificationToken)
        {
            _logger.LogWarning("Invalid verification token for organization {OrganizationId}", organizationId);
            return false;
        }

        var organization = await _unitOfWork.Organizations.GetByIdAsync(organizationId, cancellationToken);
        if (organization == null)
        {
            return false;
        }

        await MarkVerifiedAsync(organization, cancellationToken);
        _logger.LogInformation("Email verified for organization {OrganizationId}", organizationId);
        return true;
    }

    public async Task<bool> AdminVerifyAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var organization = await _unitOfWork.Organizations.GetByIdAsync(organizationId, cancellationToken);
        if (organization == null || organization.Status != TenantStatus.Pending)
        {
            return false;
        }

        await MarkVerifiedAsync(organization, cancellationToken);
        _logger.LogInformation("Organization {OrganizationId} verified directly by a platform admin (no token)", organizationId);
        return true;
    }

    /// <summary>Shared tail of both VerifyEmailAsync (after a real token check) and AdminVerifyAsync
    /// (admin vouching directly, no token) — status/VerifiedAt, clears any pending cached token,
    /// and seeds the org's default branch/service types exactly like a real self-verified signup.</summary>
    private async Task MarkVerifiedAsync(Organization organization, CancellationToken cancellationToken)
    {
        organization.Status = TenantStatus.Trialing;
        organization.VerifiedAt = DateTime.UtcNow;

        await _unitOfWork.Organizations.UpdateAsync(organization, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _cache.RemoveAsync($"{VerificationTokenPrefix}{organization.Id}", cancellationToken);

        await SeedDefaultDataAsync(organization.Id, cancellationToken);
    }

    public async Task<bool> ResendVerificationEmailAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var organization = await _unitOfWork.Organizations.GetByIdAsync(organizationId, cancellationToken);
        if (organization == null || organization.Status != TenantStatus.Pending)
        {
            return false;
        }

        var adminUser = await _unitOfWork.Users.FirstOrDefaultAsync(
            u => u.OrganizationId == organizationId, cancellationToken);
        if (adminUser == null)
        {
            return false;
        }

        var token = await GenerateVerificationTokenAsync(organizationId, cancellationToken);
        var sent = await SendVerificationEmailAsync(adminUser, organization, token, cancellationToken);

        _logger.LogInformation("Resent verification email for organization {OrganizationId} (sent={Sent})", organizationId, sent);
        return sent;
    }

    private async Task<bool> SendVerificationEmailAsync(User user, Organization organization, string token, CancellationToken cancellationToken)
    {
        var saas = await _platformSettingsService.GetSettingsAsync<SaasSettings>("SaaS");
        var baseUrl = (saas?.BaseUrl ?? "https://qmgr.app").TrimEnd('/');
        var verificationUrl = $"{baseUrl}/verify?org={organization.Id}&token={token}";

        var subject = "Verify your Q-Mgr account";
        var htmlBody = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>Verify your email</title>
</head>
<body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
    <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
        <h1 style='color: #2563eb;'>Verify your email</h1>
        <p>Hi {user.FirstName},</p>
        <p>You requested a new verification link for your Q-Mgr account. Click the button below to verify your email:</p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='{verificationUrl}' style='background-color: #2563eb; color: white; padding: 12px 30px; text-decoration: none; border-radius: 6px; display: inline-block;'>Verify Email Address</a>
        </div>
        <p>Or copy and paste this link into your browser:</p>
        <p style='word-break: break-all; color: #666;'>{verificationUrl}</p>
        <p>This link will expire in 24 hours.</p>
        <hr style='border: none; border-top: 1px solid #eee; margin: 30px 0;'>
        <p style='color: #666; font-size: 14px;'>
            If you didn't request this email, you can safely ignore it.
        </p>
    </div>
</body>
</html>";

        return await _emailSender.SendAsync(user.Email, subject, htmlBody, cancellationToken);
    }

    public async Task SuspendTenantAsync(Guid organizationId, string reason, CancellationToken cancellationToken = default)
    {
        var organization = await _unitOfWork.Organizations.GetByIdAsync(organizationId, cancellationToken);
        if (organization == null)
        {
            throw new InvalidOperationException($"Organization {organizationId} not found.");
        }

        organization.Status = TenantStatus.Suspended;
        // Could store reason in Settings JSON field

        await _unitOfWork.Organizations.UpdateAsync(organization, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogWarning("Tenant {OrganizationId} suspended. Reason: {Reason}", organizationId, reason);
    }

    public async Task ReactivateTenantAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var organization = await _unitOfWork.Organizations.GetByIdAsync(organizationId, cancellationToken);
        if (organization == null)
        {
            throw new InvalidOperationException($"Organization {organizationId} not found.");
        }

        // Determine appropriate status based on subscription
        organization.Status = organization.SubscriptionId.HasValue
            ? TenantStatus.Active
            : TenantStatus.Trialing;

        await _unitOfWork.Organizations.UpdateAsync(organization, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Tenant {OrganizationId} reactivated", organizationId);
    }

    public async Task CompleteOnboardingAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var organization = await _unitOfWork.Organizations.GetByIdAsync(organizationId, cancellationToken);
        if (organization == null)
        {
            throw new InvalidOperationException($"Organization {organizationId} not found.");
        }

        organization.OnboardingCompleted = true;

        await _unitOfWork.Organizations.UpdateAsync(organization, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Onboarding completed for organization {OrganizationId}", organizationId);
    }

    #region Private Helpers

    private static string GenerateSlugFromName(string name)
    {
        // Convert to lowercase
        var slug = name.ToLowerInvariant();

        // Replace spaces and special chars with hyphens
        slug = Regex.Replace(slug, @"[^a-z0-9]+", "-");

        // Remove leading/trailing hyphens
        slug = slug.Trim('-');

        // Limit length
        if (slug.Length > 50)
            slug = slug[..50].TrimEnd('-');

        // Ensure minimum length
        if (slug.Length < 3)
            slug = $"{slug}-org";

        return slug;
    }

    private static string GenerateRandomSuffix(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var random = new byte[length];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(random);

        var sb = new StringBuilder(length);
        foreach (var b in random)
        {
            sb.Append(chars[b % chars.Length]);
        }
        return sb.ToString();
    }

    private static List<Domain.Entities.Queue.ServiceType> GetDefaultServiceTypes(IndustryType industryType, Guid branchId)
    {
        var serviceTypes = industryType switch
        {
            IndustryType.Health => new List<(string Code, string Name, string Prefix, int AvgTime, int Priority)>
            {
                ("GEN", "General Consultation", "G", 15, 1),
                ("LAB", "Laboratory", "L", 10, 2),
                ("PHARM", "Pharmacy", "P", 5, 3),
                ("CASH", "Cashier", "C", 5, 4)
            },
            IndustryType.Business => new List<(string Code, string Name, string Prefix, int AvgTime, int Priority)>
            {
                ("DOC", "Document/Account Services", "D", 15, 1),
                ("PAY", "Payments", "P", 10, 2),
                ("INQ", "Inquiries", "I", 10, 3),
                ("REG", "Registration", "R", 20, 4)
            },
            IndustryType.Communications => new List<(string Code, string Name, string Prefix, int AvgTime, int Priority)>
            {
                ("BILL", "Bill Payment", "B", 5, 1),
                ("SUB", "Subscriptions", "S", 15, 2),
                ("TECH", "Technical Support", "T", 20, 3),
                ("SIM", "SIM Services", "M", 10, 4)
            },
            IndustryType.Education => new List<(string Code, string Name, string Prefix, int AvgTime, int Priority)>
            {
                ("ADM", "Admissions", "A", 20, 1),
                ("FEES", "Fees Payment", "F", 10, 2),
                ("RECS", "Student Records", "R", 15, 3),
                ("VISIT", "Visitor Check-In", "V", 5, 4)
            },
            _ => new List<(string Code, string Name, string Prefix, int AvgTime, int Priority)>
            {
                ("GEN", "General Service", "G", 10, 1),
                ("INQ", "Inquiry", "I", 5, 2),
                ("PAY", "Payment", "P", 5, 3)
            }
        };

        return serviceTypes.Select(st => new Domain.Entities.Queue.ServiceType
        {
            BranchId = branchId,
            Code = st.Code,
            Name = st.Name,
            Prefix = st.Prefix,
            AverageServiceTimeMinutes = st.AvgTime,
            Priority = st.Priority
        }).ToList();
    }

    #endregion
}
