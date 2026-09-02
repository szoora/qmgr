using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;

namespace QMgr.Filters;

/// <summary>
/// Attribute to require a specific feature to be enabled for the current tenant.
/// Returns 403 Forbidden if the feature is not available on the current plan.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequireFeatureAttribute : Attribute, IAsyncActionFilter
{
    public string FeatureCode { get; }
    public string? ErrorMessage { get; set; }

    public RequireFeatureAttribute(string featureCode)
    {
        FeatureCode = featureCode;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenantAccessor = context.HttpContext.RequestServices.GetRequiredService<ITenantContextAccessor>();
        var featureFlagService = context.HttpContext.RequestServices.GetRequiredService<IFeatureFlagService>();

        var tenantContext = tenantAccessor.TenantContext;

        if (tenantContext == null || !tenantContext.IsResolved)
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "TENANT_NOT_RESOLVED",
                message = "Unable to determine tenant context"
            });
            return;
        }

        var isEnabled = await featureFlagService.IsFeatureEnabledAsync(tenantContext.OrganizationId, FeatureCode);

        if (!isEnabled)
        {
            var message = ErrorMessage ?? $"The '{FeatureCode}' feature is not available on your current plan. Please upgrade to access this feature.";

            context.Result = new ObjectResult(new
            {
                error = "FEATURE_NOT_AVAILABLE",
                feature = FeatureCode,
                message,
                upgradeUrl = "/billing/plans"
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }
}

/// <summary>
/// Requires one of the four purchasable modules (see <c>ModuleCodes</c>) to be active for the
/// current tenant. Returns 403 if it isn't — mirrors <see cref="RequireFeatureAttribute"/> exactly
/// (same response shape, same tenant-resolution check) so it composes freely alongside
/// <c>[RequirePermission]</c> the same proven way <c>RequireFeatureAttribute</c> already does.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequireModuleAttribute : Attribute, IAsyncActionFilter
{
    public string ModuleCode { get; }
    public string? ErrorMessage { get; set; }

    public RequireModuleAttribute(string moduleCode)
    {
        ModuleCode = moduleCode;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenantAccessor = context.HttpContext.RequestServices.GetRequiredService<ITenantContextAccessor>();
        var moduleAccessService = context.HttpContext.RequestServices.GetRequiredService<QMgr.Application.Interfaces.Billing.IModuleAccessService>();

        var tenantContext = tenantAccessor.TenantContext;

        if (tenantContext == null || !tenantContext.IsResolved)
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "TENANT_NOT_RESOLVED",
                message = "Unable to determine tenant context"
            });
            return;
        }

        // SuperAdmin manages every tenant's modules directly — never gated by what any one
        // tenant happens to have purchased, same bypass used throughout the codebase for
        // [RequirePermission] and VerifyBranchOwnership.
        if (QMgr.Domain.Constants.RoleCodes.IsSuperAdmin(tenantContext.UserRole))
        {
            await next();
            return;
        }

        var isActive = await moduleAccessService.IsModuleActiveAsync(tenantContext.OrganizationId, ModuleCode);

        if (!isActive)
        {
            var message = ErrorMessage ?? $"The '{ModuleCode}' module is not active for your organization. Add it from Billing to access this feature.";

            context.Result = new ObjectResult(new
            {
                error = "MODULE_NOT_PURCHASED",
                module = ModuleCode,
                message,
                purchaseUrl = "/billing/modules"
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }
}

/// <summary>
/// Attribute to require a minimum subscription tier.
/// Returns 403 Forbidden if the current tier is lower than required.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireTierAttribute : Attribute, IAsyncActionFilter
{
    public string RequiredTier { get; }
    public string? ErrorMessage { get; set; }

    public RequireTierAttribute(string requiredTier)
    {
        RequiredTier = requiredTier;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenantAccessor = context.HttpContext.RequestServices.GetRequiredService<ITenantContextAccessor>();
        var featureFlagService = context.HttpContext.RequestServices.GetRequiredService<IFeatureFlagService>();

        var tenantContext = tenantAccessor.TenantContext;

        if (tenantContext == null || !tenantContext.IsResolved)
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "TENANT_NOT_RESOLVED",
                message = "Unable to determine tenant context"
            });
            return;
        }

        var hasMinTier = await featureFlagService.HasMinimumTierAsync(tenantContext.OrganizationId, RequiredTier);

        if (!hasMinTier)
        {
            var message = ErrorMessage ?? $"This feature requires a '{RequiredTier}' plan or higher. Please upgrade your subscription.";

            context.Result = new ObjectResult(new
            {
                error = "TIER_REQUIRED",
                requiredTier = RequiredTier,
                currentTier = tenantContext.Tier.ToString(),
                message,
                upgradeUrl = "/billing/plans"
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }
}

/// <summary>
/// Attribute to check a usage limit before executing an action.
/// Returns 402 Payment Required if the limit is exceeded.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class CheckLimitAttribute : Attribute, IAsyncActionFilter
{
    public string LimitType { get; }
    public string? ErrorMessage { get; set; }

    public CheckLimitAttribute(string limitType)
    {
        LimitType = limitType;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenantAccessor = context.HttpContext.RequestServices.GetRequiredService<ITenantContextAccessor>();
        var billingService = context.HttpContext.RequestServices.GetRequiredService<IBillingService>();

        var tenantContext = tenantAccessor.TenantContext;

        if (tenantContext == null || !tenantContext.IsResolved)
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "TENANT_NOT_RESOLVED",
                message = "Unable to determine tenant context"
            });
            return;
        }

        var limitCheck = await billingService.CheckLimitAsync(tenantContext.OrganizationId, LimitType);

        if (!limitCheck.IsWithinLimit)
        {
            var message = ErrorMessage ?? $"You have exceeded your {LimitType} limit ({limitCheck.CurrentUsage}/{limitCheck.MaxAllowed}). Please upgrade your plan for more capacity.";

            context.Result = new ObjectResult(new
            {
                error = "LIMIT_EXCEEDED",
                limitType = LimitType,
                current = limitCheck.CurrentUsage,
                limit = limitCheck.MaxAllowed,
                message,
                upgradeUrl = "/billing/plans"
            })
            {
                StatusCode = StatusCodes.Status402PaymentRequired
            };
            return;
        }

        // Add limit info to response headers
        context.HttpContext.Response.Headers.Append($"X-Limit-{LimitType}-Current", limitCheck.CurrentUsage.ToString());
        context.HttpContext.Response.Headers.Append($"X-Limit-{LimitType}-Max", limitCheck.MaxAllowed.ToString());

        await next();
    }
}
