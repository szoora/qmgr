using QMgr.Domain.Enums;

namespace QMgr.Application.Tenant;

/// <summary>
/// Default implementation of ITenantContext
/// </summary>
public class TenantContext : ITenantContext
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }
    public string? TenantSlug { get; set; }
    public string? SchemaName { get; set; }
    public TenantTier Tier { get; set; } = TenantTier.Free;
    public TenantStatus Status { get; set; } = TenantStatus.Pending;
    public bool IsResolved { get; set; }
    public Guid? UserId { get; set; }
    public string? UserRole { get; set; }

    public bool UsesDedicatedSchema => !string.IsNullOrEmpty(SchemaName);
    public bool ShowAds => Tier == TenantTier.Free;
    public bool IsActive => Status == TenantStatus.Active || Status == TenantStatus.Trialing;

    /// <summary>
    /// Create an empty/unresolved tenant context
    /// </summary>
    public static TenantContext Empty => new() { IsResolved = false };

    /// <summary>
    /// Create a tenant context from organization data
    /// </summary>
    public static TenantContext FromOrganization(
        Guid organizationId,
        string? slug,
        TenantTier tier,
        TenantStatus status,
        string? schemaName = null,
        Guid? branchId = null,
        Guid? userId = null,
        string? userRole = null)
    {
        return new TenantContext
        {
            OrganizationId = organizationId,
            TenantSlug = slug,
            Tier = tier,
            Status = status,
            SchemaName = schemaName,
            BranchId = branchId,
            UserId = userId,
            UserRole = userRole,
            IsResolved = true
        };
    }
}

/// <summary>
/// Accessor for getting/setting the current tenant context
/// Uses AsyncLocal for thread-safety in async contexts
/// </summary>
public class TenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<TenantContextHolder> _tenantContextCurrent = new();

    public ITenantContext? TenantContext
    {
        get => _tenantContextCurrent.Value?.Context;
        set
        {
            var holder = _tenantContextCurrent.Value;
            if (holder != null)
            {
                // Clear current context trapped in the AsyncLocals
                holder.Context = null;
            }

            if (value != null)
            {
                // Use an object indirection to hold the context
                _tenantContextCurrent.Value = new TenantContextHolder { Context = value };
            }
        }
    }

    private class TenantContextHolder
    {
        public ITenantContext? Context;
    }
}
