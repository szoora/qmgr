using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace QMgr.Web.Services;

public class CustomAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IAuthService _authService;
    private readonly ILogger<CustomAuthenticationStateProvider> _logger;

    public CustomAuthenticationStateProvider(
        IAuthService authService,
        ILogger<CustomAuthenticationStateProvider> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var user = await _authService.GetCurrentUserAsync();

            if (user == null)
            {
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, user.RoleCode),
                new Claim("OrganizationId", user.OrganizationId.ToString()),
                new Claim("RoleName", user.RoleName)
            };

            if (!string.IsNullOrEmpty(user.FullName))
            {
                claims.Add(new Claim("FullName", user.FullName));
            }

            if (user.BranchId.HasValue)
            {
                claims.Add(new Claim("BranchId", user.BranchId.Value.ToString()));
            }

            // Add permissions as claims
            foreach (var permission in user.Permissions)
            {
                claims.Add(new Claim("Permission", permission));
            }

            var identity = new ClaimsIdentity(claims, "custom");
            var claimsPrincipal = new ClaimsPrincipal(identity);

            return new AuthenticationState(claimsPrincipal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting authentication state");
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }
    }

    public void NotifyAuthenticationStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
