using Microsoft.EntityFrameworkCore;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using System.Security.Claims;

namespace QMgr.Middleware;

/// <summary>
/// Middleware that resolves the current tenant from various sources:
/// 1. Subdomain (acme.qmgr.app)
/// 2. X-Tenant-Id header
/// 3. JWT claim (org_id)
/// 4. Query string (?tenant=acme)
/// </summary>
public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;
    private readonly IConfiguration _configuration;

    public TenantResolutionMiddleware(
        RequestDelegate next,
        ILogger<TenantResolutionMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContextAccessor tenantAccessor, QMgrDbContext dbContext, IPlatformSettingsService platformSettings)
    {
        var tenantContext = await ResolveTenantAsync(context, dbContext, platformSettings);
        tenantAccessor.TenantContext = tenantContext;

        if (tenantContext.IsResolved)
        {
            _logger.LogDebug(
                "Tenant resolved: OrgId={OrganizationId}, Slug={Slug}, Tier={Tier}, Status={Status}",
                tenantContext.OrganizationId,
                tenantContext.TenantSlug,
                tenantContext.Tier,
                tenantContext.Status);
        }

        await _next(context);
    }

    private async Task<ITenantContext> ResolveTenantAsync(HttpContext context, QMgrDbContext dbContext, IPlatformSettingsService platformSettings)
    {
        // 1. Try to resolve from JWT claim (authenticated user)
        var orgIdClaim = context.User.FindFirst("org_id")?.Value;
        if (!string.IsNullOrEmpty(orgIdClaim) && Guid.TryParse(orgIdClaim, out var orgIdFromClaim))
        {
            var org = await dbContext.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == orgIdFromClaim);

            if (org != null)
            {
                var branchIdClaim = context.User.FindFirst("branch_id")?.Value;
                Guid? branchId = null;
                if (!string.IsNullOrEmpty(branchIdClaim) && Guid.TryParse(branchIdClaim, out var bid))
                {
                    branchId = bid;
                }

                var userId = GetUserId(context);
                var userRole = context.User.FindFirst(ClaimTypes.Role)?.Value;

                return TenantContext.FromOrganization(
                    org.Id,
                    org.Slug,
                    org.Tier,
                    org.Status,
                    org.SchemaName,
                    branchId,
                    userId,
                    userRole);
            }
        }

        // 2. Try to resolve from X-Tenant-Id header
        if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var tenantIdHeader))
        {
            var tenantId = tenantIdHeader.FirstOrDefault();
            if (!string.IsNullOrEmpty(tenantId))
            {
                var org = await FindOrganizationAsync(dbContext, tenantId);
                if (org != null)
                {
                    return TenantContext.FromOrganization(org.Id, org.Slug, org.Tier, org.Status, org.SchemaName);
                }
            }
        }

        // 3. Try to resolve from subdomain
        var host = context.Request.Host.Host;
        var slug = await ExtractSubdomainAsync(host, platformSettings);
        if (!string.IsNullOrEmpty(slug))
        {
            var org = await dbContext.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Slug == slug);

            if (org != null)
            {
                return TenantContext.FromOrganization(org.Id, org.Slug, org.Tier, org.Status, org.SchemaName);
            }

            // Check custom domain
            org = await dbContext.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.CustomDomain == host);

            if (org != null)
            {
                return TenantContext.FromOrganization(org.Id, org.Slug, org.Tier, org.Status, org.SchemaName);
            }
        }

        // 4. Try to resolve from query string
        if (context.Request.Query.TryGetValue("tenant", out var tenantQuery))
        {
            var tenantSlug = tenantQuery.FirstOrDefault();
            if (!string.IsNullOrEmpty(tenantSlug))
            {
                var org = await FindOrganizationAsync(dbContext, tenantSlug);
                if (org != null)
                {
                    return TenantContext.FromOrganization(org.Id, org.Slug, org.Tier, org.Status, org.SchemaName);
                }
            }
        }

        // Tenant not resolved - return empty context
        return TenantContext.Empty;
    }

    private async Task<Domain.Entities.Organization.Organization?> FindOrganizationAsync(QMgrDbContext dbContext, string identifier)
    {
        // Try as GUID first
        if (Guid.TryParse(identifier, out var orgId))
        {
            return await dbContext.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == orgId);
        }

        // Try as slug
        return await dbContext.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Slug == identifier);
    }

    private async Task<string?> ExtractSubdomainAsync(string host, IPlatformSettingsService platformSettings)
    {
        // Was IConfiguration-only (appsettings.json), completely disconnected from the
        // "SaaS" PlatformSetting row the admin UI actually edits. GetSettingsAsync is
        // memory-cached (30 min, invalidated on save), so this is cheap on the per-request path.
        var saas = await platformSettings.GetSettingsAsync<SaasSettings>("SaaS");
        var baseDomain = saas?.BaseDomain ?? _configuration["SaaS:BaseDomain"] ?? "qmgr.app";

        // Handle localhost for development
        if (host.Contains("localhost"))
        {
            return null;
        }

        // Extract subdomain from host
        if (host.EndsWith($".{baseDomain}", StringComparison.OrdinalIgnoreCase))
        {
            var subdomain = host[..^(baseDomain.Length + 1)];
            if (!string.IsNullOrEmpty(subdomain) && subdomain != "www" && subdomain != "app")
            {
                return subdomain.ToLowerInvariant();
            }
        }

        return null;
    }

    private Guid? GetUserId(HttpContext context)
    {
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value;

        if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var userId))
        {
            return userId;
        }

        return null;
    }
}

/// <summary>
/// Extension methods for adding tenant resolution middleware
/// </summary>
public static class TenantResolutionMiddlewareExtensions
{
    /// <summary>
    /// Adds tenant resolution middleware to the pipeline
    /// </summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TenantResolutionMiddleware>();
    }
}
