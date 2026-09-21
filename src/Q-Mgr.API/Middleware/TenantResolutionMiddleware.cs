using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using System.Security.Claims;

namespace QMgr.Middleware;

/// <summary>
/// Middleware that resolves the current tenant from various sources:
/// 1. Subdomain (sacc.{PlatformSettings.SaaS.BaseDomain})
/// 2. X-Tenant-Id header
/// 3. JWT claim (org_id)
/// 4. Query string (?tenant=sacc)
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
                "Tenant resolved: OrgId={OrganizationId}, Slug={Slug}, Status={Status}",
                tenantContext.OrganizationId,
                tenantContext.TenantSlug,
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
                // CORRECTNESS: TenantStatusMiddleware used to only gate on whatever Status this
                // row already had, and the only thing that ever advanced a Trialing org to
                // Suspended was BillingJobs.CheckExpiringTrialsAsync — a daily job at 9 AM UTC.
                // That left up to ~18 hours of free access past the real trial end before
                // anything actually gated the tenant. This org row is already re-read fresh on
                // every request (no caching to fight here), so self-heal it right here instead:
                // if the trial has genuinely ended and there's still no subscription, flip the
                // status now and use the corrected value for this same request's gating decision.
                // The nightly job still runs — it's what sends the "trial expired" email, which
                // this real-time path deliberately does not duplicate on every request.
                var effectiveStatus = org.Status;
                if (org.Status == TenantStatus.Trialing &&
                    org.TrialEndsAt.HasValue &&
                    org.TrialEndsAt.Value <= DateTime.UtcNow &&
                    !org.SubscriptionId.HasValue)
                {
                    var updated = await dbContext.Organizations
                        .Where(o => o.Id == org.Id && o.Status == TenantStatus.Trialing)
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, TenantStatus.Suspended));

                    if (updated > 0)
                    {
                        effectiveStatus = TenantStatus.Suspended;
                        _logger.LogInformation(
                            "Trial expired for organization {OrganizationId} — gated in real time instead of waiting for the nightly job",
                            org.Id);
                    }
                }

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
                    effectiveStatus,
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
                    return TenantContext.FromOrganization(org.Id, org.Slug, org.Status, org.SchemaName);
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
                return TenantContext.FromOrganization(org.Id, org.Slug, org.Status, org.SchemaName);
            }
        }

        // 3b. Try to resolve from a tenant's OWN domain.
        //
        // This used to be nested INSIDE the `if (!string.IsNullOrEmpty(slug))` block above, which
        // made it unreachable for every host it was written for: ExtractSubdomainAsync returns
        // null unless the host ends in the platform's base domain, and a tenant's own domain —
        // "dashboard.maryhillug.net" — by definition does not. So the custom-domain feature was
        // wired end to end and dead. It is a sibling of the subdomain branch, not a child of it.
        var byDomain = await ResolveCustomDomainAsync(dbContext, host);
        if (byDomain != null)
        {
            return TenantContext.FromOrganization(byDomain.Id, byDomain.Slug, byDomain.Status, byDomain.SchemaName);
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
                    return TenantContext.FromOrganization(org.Id, org.Slug, org.Status, org.SchemaName);
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

    /// <summary>
    /// The organization whose live <c>CustomDomain</c> is this host, cached for 30 minutes — the
    /// same window the SaaS settings use, and for the same reason: this runs on every request and
    /// a tenant's domain changes about once in its lifetime. A NEGATIVE answer is cached too, or
    /// the platform host itself pays a query per request for a row that will never exist.
    /// <see cref="CustomDomainCacheKey"/> is what <c>ICustomDomainService</c> evicts when a domain
    /// goes live or is released.
    /// </summary>
    private static async Task<Domain.Entities.Organization.Organization?> ResolveCustomDomainAsync(QMgrDbContext dbContext, string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        var key = CustomDomainCacheKey(host);
        if (CustomDomainCache.TryGetValue(key, out CustomDomainHit? cached) && cached != null)
            return cached.Organization;

        var normalized = host.Trim().ToLowerInvariant();
        var org = await dbContext.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.CustomDomain != null && o.CustomDomain.ToLower() == normalized);

        CustomDomainCache.Set(key, new CustomDomainHit(org), CustomDomainCacheFor);
        return org;
    }

    /// <summary>The cache key a custom-domain lookup is stored under. One home, so eviction cannot miss.</summary>
    public static string CustomDomainCacheKey(string host) => "tenant-by-domain:" + host.Trim().ToLowerInvariant();

    /// <summary>Evict one host, after a domain went live or was released.</summary>
    public static void ForgetCustomDomain(string? host)
    {
        if (!string.IsNullOrWhiteSpace(host))
            CustomDomainCache.Remove(CustomDomainCacheKey(host));
    }

    private sealed record CustomDomainHit(Domain.Entities.Organization.Organization? Organization);

    private static readonly TimeSpan CustomDomainCacheFor = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Static rather than an injected <c>IMemoryCache</c>: this middleware is constructed once per
    /// pipeline and the cache has to outlive the scoped DbContext each request brings with it.
    /// </summary>
    private static readonly MemoryCache CustomDomainCache =
        new(new MemoryCacheOptions());

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
