using System.Security.Claims;
using Hangfire.Annotations;
using Hangfire.Dashboard;
using QMgr.Domain.Constants;

namespace QMgr.Infrastructure.Jobs;

/// <summary>
/// Authorization filter for Hangfire dashboard access.
/// Only allows access to super admins in production,
/// and all users in development.
/// </summary>
public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly bool _allowAll;

    public HangfireAuthorizationFilter(bool allowAll = false)
    {
        _allowAll = allowAll;
    }

    public bool Authorize([NotNull] DashboardContext context)
    {
        // In development, allow all access
        if (_allowAll)
            return true;

        var httpContext = context.GetHttpContext();

        // Must be authenticated
        if (!httpContext.User.Identity?.IsAuthenticated ?? true)
            return false;

        // Must be a SuperAdmin - check using the constant to avoid case/typo issues
        var roleClaim = httpContext.User.FindFirst(ClaimTypes.Role)?.Value;
        return RoleCodes.IsSuperAdmin(roleClaim);
    }
}
