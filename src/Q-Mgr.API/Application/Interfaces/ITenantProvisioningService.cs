using QMgr.Domain.Entities.Organization;

namespace QMgr.Application.Interfaces;

/// <summary>
/// Service for provisioning and managing tenant organizations in the SaaS platform
/// </summary>
public interface ITenantProvisioningService
{
    /// <summary>
    /// Provision a new tenant organization with admin user
    /// </summary>
    Task<TenantProvisioningResult> ProvisionTenantAsync(ProvisionTenantRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seed default data for a new organization (service types, settings, etc.)
    /// </summary>
    Task SeedDefaultDataAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate if a slug is available for use
    /// </summary>
    Task<bool> ValidateSlugAvailabilityAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate a unique slug from an organization name
    /// </summary>
    Task<string> GenerateUniqueSlugAsync(string organizationName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verify email and activate tenant
    /// </summary>
    Task<bool> VerifyEmailAsync(Guid organizationId, string verificationToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate email verification token
    /// </summary>
    Task<string> GenerateVerificationTokenAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Regenerates the verification token and re-sends the verification email for this
    /// organization's admin user — the single source of truth for "send/resend a verification
    /// email," used by both the self-service ResendVerificationCommandHandler (looked up by
    /// email) and SuperAdminController's platform-admin "Resend Verification" action (looked up
    /// by organization ID directly). False if the org doesn't exist, isn't Pending, or has no
    /// user to send to.</summary>
    Task<bool> ResendVerificationEmailAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Platform-admin override: marks an organization verified without a token — for
    /// when the verification email never arrived (e.g. unconfigured SMTP) and the admin has
    /// otherwise confirmed the account is legitimate. Does exactly what a token-verified
    /// VerifyEmailAsync does (status, VerifiedAt, default branch/service-type seeding) via the
    /// same shared path, just skipping the token check since the admin is vouching for the
    /// account directly rather than the customer proving email ownership. False if the org
    /// doesn't exist or isn't Pending.</summary>
    Task<bool> AdminVerifyAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Suspend a tenant
    /// </summary>
    Task SuspendTenantAsync(Guid organizationId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reactivate a suspended tenant
    /// </summary>
    Task ReactivateTenantAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Complete onboarding for a tenant
    /// </summary>
    Task CompleteOnboardingAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Request model for provisioning a new tenant
/// </summary>
public record ProvisionTenantRequest
{
    /// <summary>Organization name</summary>
    public string OrganizationName { get; init; } = string.Empty;

    /// <summary>Desired slug (optional, will be generated if not provided)</summary>
    public string? Slug { get; init; }

    /// <summary>Admin user email</summary>
    public string AdminEmail { get; init; } = string.Empty;

    /// <summary>Admin user password</summary>
    public string AdminPassword { get; init; } = string.Empty;

    /// <summary>Admin first name</summary>
    public string AdminFirstName { get; init; } = string.Empty;

    /// <summary>Admin last name</summary>
    public string AdminLastName { get; init; } = string.Empty;

    /// <summary>Admin phone (optional)</summary>
    public string? AdminPhone { get; init; }

    /// <summary>Organization contact phone</summary>
    public string? ContactPhone { get; init; }

    /// <summary>Industry type for default setup</summary>
    public string? IndustryType { get; init; }

    /// <summary>Preferred currency (USD, UGX)</summary>
    public string PreferredCurrency { get; init; } = "USD";

    /// <summary>Source of registration (web, api, referral)</summary>
    public string? Source { get; init; }

    /// <summary>Referral code if applicable</summary>
    public string? ReferralCode { get; init; }
}

/// <summary>
/// Result of tenant provisioning
/// </summary>
public record TenantProvisioningResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public Guid OrganizationId { get; init; }
    public Guid AdminUserId { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string? VerificationToken { get; init; }

    public static TenantProvisioningResult Succeeded(Guid orgId, Guid userId, string slug, string? verificationToken) => new()
    {
        Success = true,
        OrganizationId = orgId,
        AdminUserId = userId,
        Slug = slug,
        VerificationToken = verificationToken
    };

    public static TenantProvisioningResult Failed(string error) => new()
    {
        Success = false,
        ErrorMessage = error
    };
}
