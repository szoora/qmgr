using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using QMgr.API.Authorization;
using QMgr.Domain.Constants;

namespace QMgr.API.Extensions;

/// <summary>
/// Extension methods for configuring authorization with RBAC.
/// </summary>
public static class AuthorizationExtensions
{
    /// <summary>
    /// Adds RBAC authorization with permission-based policies.
    /// </summary>
    public static IServiceCollection AddRbacAuthorization(this IServiceCollection services)
    {
        // Register the permission authorization handler
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        // Configure authorization with permission-based policies
        services.AddAuthorization(options =>
        {
            // Register a policy for each permission
            foreach (var permission in Permissions.All)
            {
                var policyName = $"{RequirePermissionAttribute.PolicyPrefix}{permission.Code}";
                options.AddPolicy(policyName, policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.Requirements.Add(new PermissionRequirement(permission.Code));
                });
            }

            // Default policy requires authentication
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            // Fallback policy for non-attributed endpoints
            options.FallbackPolicy = null; // Allow anonymous by default, use [Authorize] explicitly
        });

        return services;
    }

    /// <summary>
    /// Adds a custom authorization policy provider that can create policies on-the-fly
    /// for permission-based authorization.
    /// </summary>
    public static IServiceCollection AddRbacPolicyProvider(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        return services;
    }
}

/// <summary>
/// Custom policy provider that creates permission-based policies on-the-fly.
/// This allows using [RequirePermission("custom.permission")] even if the permission
/// isn't in the Permissions.All list.
/// </summary>
public class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallbackProvider;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallbackProvider = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync()
    {
        return _fallbackProvider.GetDefaultPolicyAsync();
    }

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
    {
        return _fallbackProvider.GetFallbackPolicyAsync();
    }

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        // Check if this is a permission-based policy
        if (policyName.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var permission = policyName[RequirePermissionAttribute.PolicyPrefix.Length..];

            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission))
                .Build();

            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        // Fall back to the default provider
        return _fallbackProvider.GetPolicyAsync(policyName);
    }
}
