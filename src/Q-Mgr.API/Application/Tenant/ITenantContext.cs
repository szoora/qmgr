using QMgr.Domain.Enums;

namespace QMgr.Application.Tenant;

/// <summary>
/// Provides the current tenant context for the request
/// </summary>
public interface ITenantContext
{
    /// <summary>Organization ID of the current tenant</summary>
    Guid OrganizationId { get; }

    /// <summary>Branch ID if user is scoped to a specific branch</summary>
    Guid? BranchId { get; }

    /// <summary>URL-safe tenant identifier (subdomain)</summary>
    string? TenantSlug { get; }

    /// <summary>Database schema name for dedicated tenants (null = shared schema)</summary>
    string? SchemaName { get; }

    /// <summary>Current subscription tier</summary>
    TenantTier Tier { get; }

    /// <summary>Current tenant status</summary>
    TenantStatus Status { get; }

    /// <summary>Whether tenant context has been resolved</summary>
    bool IsResolved { get; }

    /// <summary>Whether tenant uses a dedicated database schema</summary>
    bool UsesDedicatedSchema { get; }

    /// <summary>Whether to show ads (free tier only)</summary>
    bool ShowAds { get; }

    /// <summary>Whether the tenant is in an active state (can access features)</summary>
    bool IsActive { get; }

    /// <summary>Current user ID (if authenticated)</summary>
    Guid? UserId { get; }

    /// <summary>Current user role</summary>
    string? UserRole { get; }
}

/// <summary>
/// Mutable tenant context that can be set by middleware
/// </summary>
public interface ITenantContextAccessor
{
    /// <summary>Get or set the current tenant context</summary>
    ITenantContext? TenantContext { get; set; }
}
