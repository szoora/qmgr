using Microsoft.AspNetCore.Authorization;

namespace QMgr.API.Authorization;

/// <summary>
/// Requires ANY ONE of several permission codes — the endpoint is reachable by a caller holding at
/// least one of them.
/// </summary>
/// <remarks>
/// Added 2026-09-18 for the welfare reports, which answer to either the branch-wide
/// <c>welfare.reports.view</c> a manager holds or the <c>welfare.reports.own</c> a class teacher
/// holds. Two codes rather than one so a school can withhold the page from a class-teacher role
/// without touching what a manager reads.
///
/// This is an OR, so reach for it only when the codes really are alternative routes to the same
/// endpoint. Two permissions that gate different things still want two endpoints, or an explicit
/// check in the method — stacking <see cref="RequirePermissionAttribute"/> twice is an AND.
/// </remarks>
/// <example>
/// [RequirePermissionAny(Permissions.WelfareReportsView, Permissions.WelfareReportsOwn)]
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequirePermissionAnyAttribute : AuthorizeAttribute
{
    /// <summary>Separates the codes inside the generated policy name.</summary>
    public const char Separator = '|';

    public RequirePermissionAnyAttribute(params string[] permissions)
        : base($"{RequirePermissionAttribute.PolicyPrefix}{string.Join(Separator, permissions)}")
    {
        if (permissions is null || permissions.Length == 0)
            throw new ArgumentException("At least one permission code is required.", nameof(permissions));
    }
}
