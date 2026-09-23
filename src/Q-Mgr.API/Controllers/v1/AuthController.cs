using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Interfaces;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services.Storage;
using QMgr.Infrastructure.Email;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly QMgrDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly IPasswordValidationService _passwordValidationService;
    private readonly ILogger<AuthController> _logger;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IEmailSender _emailSender;
    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly QMgr.Infrastructure.Services.IActivityLogger _activity;
    private readonly QMgr.Infrastructure.Email.IEmailBrandService _emailBrands;
    private readonly QMgr.Infrastructure.Services.Mobile.IDeviceSessionService _deviceSessions;
    private readonly QMgr.Infrastructure.Services.Mobile.IMobileHandoffService _handoff;

    public AuthController(
        IUnitOfWork unitOfWork,
        QMgrDbContext dbContext,
        IConfiguration configuration,
        IPasswordValidationService passwordValidationService,
        ILogger<AuthController> logger,
        ITenantContextAccessor tenantAccessor,
        IEmailSender emailSender,
        IPlatformSettingsService platformSettingsService,
        QMgr.Infrastructure.Email.IEmailBrandService emailBrands,
        QMgr.Infrastructure.Services.IActivityLogger activity,
        QMgr.Infrastructure.Services.Mobile.IDeviceSessionService deviceSessions,
        QMgr.Infrastructure.Services.Mobile.IMobileHandoffService handoff)
    {
        _deviceSessions = deviceSessions;
        _handoff = handoff;
        _emailBrands = emailBrands;
        _activity = activity;
        _unitOfWork = unitOfWork;
        _dbContext = dbContext;
        _configuration = configuration;
        _passwordValidationService = passwordValidationService;
        _logger = logger;
        _tenantAccessor = tenantAccessor;
        _emailSender = emailSender;
        _platformSettingsService = platformSettingsService;
    }

    /// <summary>
    /// Step 1: Identify user by email OR username and return tenant info.
    /// Supports both subdomain-scoped and global lookup.
    /// </summary>
    [HttpPost("identify")]
    [ProducesResponseType(typeof(IdentifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> IdentifyUser([FromBody] IdentifyRequest request)
    {
        var identifier = request.Email.Trim().ToLowerInvariant();
        var tenantContext = _tenantAccessor.TenantContext;

        // Build query — the "Email" field on the request is really a generic identifier: it
        // accepts either the user's email or their username.
        // A join request waiting for approval is identified too, so its owner reaches the password
        // step and is told, once their password checks out, that they are waiting (plan §12.4).
        var query = _dbContext.Users
            .Include(u => u.Organization)
            .Where(u => ((u.Email != null && u.Email.ToLower() == identifier) || u.Username.ToLower() == identifier)
                        && (u.IsActive || (u.PendingApprovalAt != null && u.JoinRequestRejectedAt == null)));

        // If subdomain is resolved, scope to that organization only
        if (tenantContext.IsResolved)
        {
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
            _logger.LogDebug("Tenant-scoped identify: {Identifier} in org {OrgId}", identifier, tenantContext.OrganizationId);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
        {
            var message = tenantContext.IsResolved
                ? $"No account found with this email/username in {tenantContext.TenantSlug} organization"
                : "No account found with this email or username";

            _logger.LogWarning("User identification failed for identifier: {Identifier}, Tenant: {Tenant}",
                identifier, tenantContext.TenantSlug ?? "none");
            return NotFound(new { message });
        }

        _logger.LogInformation("User identified: {Email}, Organization: {OrgName}", user.Email, user.Organization?.Name);

        return Ok(new IdentifyResponse
        {
            Email = user.Email ?? string.Empty,
            OrganizationId = user.OrganizationId,
            OrganizationName = user.Organization?.Name ?? "Unknown",
            OrganizationSlug = user.Organization?.Slug ?? "",
            HasPassword = !string.IsNullOrEmpty(user.PasswordHash)
        });
    }

    /// <summary>
    /// Step 2: User login with email or username, plus password.
    /// Supports both subdomain-scoped and organization-targeted login.
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        // Identifier, not Email: the mobile shell posts userName, the web posts email, and both
        // have always been matched against Username as well as Email.
        var identifier = request.Identifier.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(identifier))
            return Unauthorized(new { message = "Invalid email or password" });

        var tenantContext = _tenantAccessor.TenantContext;

        // Build query — "Email" on the request is a generic identifier: email or username.
        var query = _dbContext.Users
            .Include(u => u.Role)
            .ThenInclude(r => r.RolePermissions)
            .ThenInclude(rp => rp.Permission)
            .Include(u => u.Organization)
            .Where(u => ((u.Email != null && u.Email.ToLower() == identifier) || u.Username.ToLower() == identifier)
                        && (u.IsActive || (u.PendingApprovalAt != null && u.JoinRequestRejectedAt == null)));

        // Priority 1: Subdomain-scoped (tenant resolved from URL)
        if (tenantContext.IsResolved)
        {
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
            _logger.LogDebug("Subdomain-scoped login: {Identifier} in org {OrgId}", identifier, tenantContext.OrganizationId);
        }
        // Priority 2: Organization ID from frontend (two-step login)
        else if (request.OrganizationId.HasValue)
        {
            query = query.Where(u => u.OrganizationId == request.OrganizationId.Value);
            _logger.LogDebug("Organization-targeted login: {Identifier} in org {OrgId}", identifier, request.OrganizationId);
        }
        // Priority 3: Global lookup (not recommended in production, for dev only)
        else
        {
            _logger.LogWarning("Global login attempt (no tenant context): {Identifier}", identifier);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
        {
            _logger.LogWarning("Login failed for identifier: {Identifier}, Tenant: {Tenant}",
                identifier, tenantContext.TenantSlug ?? request.OrganizationId?.ToString() ?? "global");
            return Unauthorized(new { message = "Invalid email or password" });
        }

        var securitySettings = await _passwordValidationService.GetSecuritySettingsAsync();
        var lockoutPolicy = securitySettings.PasswordPolicy;

        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow)
        {
            _logger.LogWarning("Login blocked — account locked out for user: {Email} until {LockoutEnd}", user.Email, user.LockoutEnd);
            var minutesRemaining = (int)Math.Ceiling((user.LockoutEnd.Value - DateTime.UtcNow).TotalMinutes);
            return Unauthorized(new { message = $"Account locked due to too many failed login attempts. Try again in {minutesRemaining} minute(s)." });
        }

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            _logger.LogWarning("Invalid password for user: {Email}", user.Email);

            if (lockoutPolicy.EnableAccountLockout)
            {
                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= lockoutPolicy.MaxFailedAttempts)
                {
                    user.LockoutEnd = DateTime.UtcNow.AddMinutes(lockoutPolicy.LockoutDurationMinutes);
                    _logger.LogWarning("Account locked out for user: {Email} after {Attempts} failed attempts", user.Email, user.FailedLoginAttempts);
                }
                await _dbContext.SaveChangesAsync();
            }

            return Unauthorized(new { message = "Invalid email or password" });
        }

        // A join request (plan §12.4). Told only AFTER the password matched, so the answer confirms
        // nothing to someone who does not hold it. No token of any kind.
        if (!user.IsActive)
        {
            return Unauthorized(new { error = "PENDING_APPROVAL", message = "Your request is waiting for your administrator's approval. You will be emailed when you can sign in." });
        }

        // A temporary password (plan §12.3). Expired: refused, with who to ask. Valid: a token that can
        // only change the password — no refresh token, fifteen minutes, and PasswordChangeOnlyMiddleware
        // refuses it everywhere else. The failed-attempt counter resets as for any correct password.
        if (user.MustChangePassword)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;
            await _dbContext.SaveChangesAsync();

            if (user.TemporaryPasswordExpiresAt is null || user.TemporaryPasswordExpiresAt <= DateTime.UtcNow)
            {
                _logger.LogWarning("Sign-in refused for {Username}: temporary password expired", user.Username);
                return Unauthorized(new { error = "TEMPORARY_PASSWORD_EXPIRED", message = "Your temporary password has expired — ask your administrator for a new one." });
            }

            var changeToken = GenerateJwtToken(user, TemporaryPasswords.ChangeTokenMinutes, passwordChangeOnly: true);
            _logger.LogInformation("User {Username} signed in with a temporary password; issued a password-change-only token", user.Username);
            return Ok(new LoginResponse
            {
                AccessToken = changeToken,
                RefreshToken = string.Empty,
                ExpiresIn = TemporaryPasswords.ChangeTokenMinutes * 60,
                MustChangePassword = true,
                // THE ONE HAND-BUILT UserInfo THAT IS CORRECT, and deliberately NOT BuildUserInfoWithPostsAsync:
                // this token can only change the password, so it advertises NO permissions at all. Routing it
                // through the builder would hand a full permission list to a session that cannot use one.
                User = new UserInfo
                {
                    Id = user.Id,
                    Username = user.Username,
                    Email = user.Email ?? string.Empty,
                    FullName = user.FullName,
                    RoleId = user.RoleId,
                    RoleCode = user.Role.Code,
                    RoleName = user.Role.Name,
                    RoleColor = user.Role.Color,
                    OrganizationId = user.OrganizationId,
                    OrganizationName = user.Organization?.Name,
                    BranchId = user.AssignedBranchId,
                    PhotoUrl = UploadLinks.Sign(user.PhotoUrl),
                    Permissions = new List<string>(),
                    MustChangePassword = true
                }
            });
        }

        var expiryMinutes = await GetTokenExpiryMinutesAsync();
        var token = GenerateJwtToken(user, expiryMinutes);

        // A DEVICE gets its own session row; a browser keeps the single column it always used.
        //
        // The two must not be mixed: writing User.RefreshToken for a phone is what made a mobile
        // sign-in evict the browser and vice versa, because that column holds exactly one value
        // for the whole account. See UserDeviceSession for the full reasoning.
        string refreshToken;
        var isDevice = !string.IsNullOrWhiteSpace(request.DeviceId);

        if (isDevice)
        {
            user.LastLogin = DateTime.UtcNow;
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;
            await _dbContext.SaveChangesAsync();

            refreshToken = await _deviceSessions.IssueAsync(
                user, request.DeviceId, request.DeviceName, request.Platform,
                request.AppVersion, request.AppVersionCode);
        }
        else
        {
            refreshToken = GenerateRefreshToken();
            user.RefreshToken = refreshToken;
            user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);
            user.LastLogin = DateTime.UtcNow;
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;

            await _dbContext.SaveChangesAsync();
        }

        _logger.LogInformation("User {Username} logged in successfully", user.Username);

        // The first row of a person's activity trail (plan §11). Explicit actor and organization:
        // there is no authenticated principal on this request yet. Never throws.
        await _activity.RecordAsync(ActivityActions.SignedIn, "User", user.Id, user.Id,
            $"{(string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName)} signed in",
            organizationId: user.OrganizationId, branchId: user.AssignedBranchId, actorUserId: user.Id);

        // ONE builder, so the role's permissions and the POST-derived ones cannot diverge between
        // the five responses that carry a UserInfo. This was written out by hand here, which is why
        // the derived welfare set reached the mobile handoff and the refresh and NOT the sign-in
        // that every teacher actually uses — found by signing in as one, not by reading the code.
        return Ok(new LoginResponse
        {
            AccessToken = token,
            RefreshToken = refreshToken,
            ExpiresIn = expiryMinutes * 60,
            User = await BuildUserInfoWithPostsAsync(user)
        });
    }

    /// <summary>
    /// API client token endpoint (OAuth2 client credentials)
    /// </summary>
    [HttpPost("token")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetToken([FromBody] ClientCredentialsRequest request)
    {
        var client = await _unitOfWork.ApiClients.FirstOrDefaultAsync(
            c => c.ClientId == request.ClientId && c.IsActive);

        if (client == null)
        {
            return Unauthorized(new { error = "invalid_client", error_description = "Client not found" });
        }

        if (!BCrypt.Net.BCrypt.Verify(request.ClientSecret, client.ClientSecretHash))
        {
            return Unauthorized(new { error = "invalid_client", error_description = "Invalid client credentials" });
        }

        var token = GenerateApiClientToken(client);

        client.LastUsedAt = DateTime.UtcNow;
        await _unitOfWork.ApiClients.UpdateAsync(client);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("API client {ClientId} obtained token", client.ClientId);

        return Ok(new TokenResponse
        {
            AccessToken = token,
            TokenType = "Bearer",
            ExpiresIn = 3600
        });
    }

    /// <summary>
    /// Signs the caller out server-side: revokes their refresh token, so a copy of it lifted from
    /// the browser stops working, and records the sign-out in the activity log. The Web calls it
    /// best-effort before clearing local storage; the access token itself expires on its own.
    /// </summary>
    [HttpPost("logout")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Logout([FromBody] MobileLogoutRequest? request = null)
    {
        var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(raw, out var userId)) return NoContent();

        var user = await _dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return NoContent();

        // ── A DEVICE signing out ────────────────────────────────────────────────────────────
        //
        // A BODY is what distinguishes the two callers, and it has to be, because nothing else can:
        // the bearer token says who, never which handset. The browser's sign-out posts no body at
        // all and falls through to the block below, exactly as it always did. A caller that sends
        // a body is asking for a revocation, so it must name what to revoke — and if it names
        // nothing it gets a 400 rather than a 204, because a "signed out" that revoked nothing is
        // the worst available outcome and a success code makes it indistinguishable from the real
        // thing. (e2e 25.12; it read 204 until this was split out.)
        //
        // The user always comes from the bearer token and never from this body, so a caller cannot
        // revoke somebody else's device by naming it — which is why the deviceId needs no ownership
        // check of its own.
        if (request is not null)
        {
            if (request.AllDevices)
            {
                var count = await _deviceSessions.RevokeAllAsync(userId, "Signed out of all devices.");
                // Every device AND the browser: "sign out everywhere" that leaves the web session
                // alive is not what anyone means by it, and this is the lost-phone path.
                user.RefreshToken = null;
                user.RefreshTokenExpiry = null;
                await _dbContext.SaveChangesAsync();
                await RecordSignOutAsync(user);
                return Ok(new { message = $"Signed out of {count} device(s)." });
            }

            var device = request.DeviceId;
            if (string.IsNullOrWhiteSpace(device) && !string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                // Derive the device from the token the caller holds. Only the middle segment is
                // read, and no validation is needed for the reason above.
                var parts = request.RefreshToken.Split('.', 3);
                if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[1])) device = parts[1];
            }

            if (string.IsNullOrWhiteSpace(device))
            {
                // Say so rather than silently doing nothing. A "signed out" that revoked nothing is
                // the worst available outcome, and a 200 here is indistinguishable from success.
                return BadRequest(new { message = "Specify the device to sign out, or set allDevices to sign out everywhere." });
            }

            await _deviceSessions.RevokeAsync(userId, device, "Signed out on this device.");
            await RecordSignOutAsync(user);
            return Ok(new { message = "Signed out on this device." });
        }

        // ── The browser signing out (unchanged) ────────────────────────────────────────────
        //
        // Clears this account's one web refresh token. Deliberately does NOT touch device
        // sessions: closing a browser tab must not sign the person's phone out.
        user.RefreshToken = null;
        user.RefreshTokenExpiry = null;
        await _dbContext.SaveChangesAsync();

        await _activity.RecordAsync(ActivityActions.SignedOut, "User", user.Id, user.Id,
            $"{(string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName)} signed out",
            organizationId: user.OrganizationId, branchId: user.AssignedBranchId, actorUserId: user.Id);

        return NoContent();
    }

    /// <summary>
    /// Refresh access token
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
    {
        // A DEVICE token carries its own identity — {userId}.{deviceId}.{secret} — so it is
        // recognised by shape rather than by a flag the caller sets, and validating it is one
        // keyed lookup. A browser token is opaque base64 and falls through to the column below.
        //
        // Note base64 contains no '.', so the two shapes cannot be confused: a three-part value
        // whose first part parses as a Guid is a device token and nothing else is.
        if (LooksLikeDeviceToken(request.RefreshToken))
        {
            var redeemed = await _deviceSessions.RedeemAsync(request.RefreshToken);
            if (!redeemed.Succeeded || redeemed.User is null)
            {
                // 401, never 400 or 500: this is what tells the app to stop retrying and ask for a
                // password. A 5xx here makes a signed-out handset retry forever.
                return Unauthorized(new { message = redeemed.Error ?? "Invalid or expired refresh token" });
            }

            var deviceExpiry = await GetTokenExpiryMinutesAsync();
            var deviceUser = redeemed.User;

            return Ok(new LoginResponse
            {
                AccessToken = GenerateJwtToken(deviceUser, deviceExpiry),
                // The ROTATED token from the store, not a freshly generated one: the store's copy
                // is the only value whose hash was persisted, and anything else fails on next use.
                RefreshToken = redeemed.RefreshToken ?? string.Empty,
                ExpiresIn = deviceExpiry * 60,
                User = await BuildUserInfoWithPostsAsync(deviceUser)
            });
        }

        var user = await _dbContext.Users
            .Include(u => u.Role)
            .ThenInclude(r => r.RolePermissions)
            .ThenInclude(rp => rp.Permission)
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u =>
                u.RefreshToken == request.RefreshToken &&
                u.RefreshTokenExpiry > DateTime.UtcNow &&
                u.IsActive &&
                !u.MustChangePassword);

        if (user == null)
        {
            return Unauthorized(new { message = "Invalid or expired refresh token" });
        }

        var expiryMinutes = await GetTokenExpiryMinutesAsync();
        var token = GenerateJwtToken(user, expiryMinutes);
        var newRefreshToken = GenerateRefreshToken();

        user.RefreshToken = newRefreshToken;
        user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);

        await _dbContext.SaveChangesAsync();

        // Get user's permissions
        var permissions = user.Role.RolePermissions
            .Select(rp => rp.Permission.Code)
            .ToList();

        return Ok(new LoginResponse
        {
            AccessToken = token,
            ExpiresIn = expiryMinutes * 60,
            RefreshToken = newRefreshToken,
            User = await BuildUserInfoWithPostsAsync(user)
        });
    }

    /// <summary>
    /// Get current user info (requires authentication)
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)
            ?? User.FindFirst("sub");

        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return Unauthorized(new { message = "Invalid token" });
        }

        var user = await _dbContext.Users
            .Include(u => u.Role)
            .ThenInclude(r => r.RolePermissions)
            .ThenInclude(rp => rp.Permission)
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);

        if (user == null)
        {
            return Unauthorized(new { message = "User not found" });
        }

        // THE POSTS, NOT JUST THE ROLE. This handler hand-built its own UserInfo from
        // user.Role.RolePermissions and so was the ONE place out of six that never picked up derived post
        // permissions (2026-09-22) — a class teacher or department head read back 4 permissions here where
        // /auth/login correctly gave them 12.
        //
        // It matters because this is the REFRESH path: the browser persists UserInfo in localStorage, and
        // AuthService.RefreshCurrentUserAsync re-reads it from here. So every `@if (HasPermission(…))` in the
        // app silently lost the derived permissions on a refresh while the API gate went on honouring them —
        // the UI disappearing from under somebody who could still make the calls.
        //
        // A sixth copy of this shape is how it happened at all. Never build a UserInfo by hand: call the
        // builder.
        return Ok(await BuildUserInfoWithPostsAsync(user));
    }

    /// <summary>
    /// Token lifetime is the one JWT setting the Platform Settings UI can genuinely change at
    /// runtime — the signing secret, issuer and audience are bound into the bearer validation
    /// pipeline at startup from server configuration (JWT__Secret etc.), so they are deliberately
    /// not read from the database here.
    /// </summary>
    private async Task<int> GetTokenExpiryMinutesAsync()
    {
        try
        {
            var jwt = await _platformSettingsService.GetSettingsAsync<JwtSettings>("JWT");
            if (jwt is { ExpiryMinutes: > 0 and <= 24 * 60 }) return jwt.ExpiryMinutes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read JWT platform settings; using configuration");
        }
        return int.TryParse(_configuration["JWT:ExpiryMinutes"], out var m) && m > 0 ? m : 60;
    }

    private string GenerateJwtToken(QMgr.Domain.Entities.Identity.User user, int expiryMinutes, bool passwordChangeOnly = false)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JWT:Secret"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            // A staff member may have no address at all; an empty email claim would be a lie that
            // anything reading the token has to special-case. It is simply absent instead.
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.Code), // Use role code from database
            new Claim("role_id", user.RoleId.ToString()),
            new Claim("org_id", user.OrganizationId.ToString()),
            new Claim("branch_id", user.AssignedBranchId?.ToString() ?? "")
        };
        if (passwordChangeOnly)
            claims.Add(new Claim(TemporaryPasswords.ChangeOnlyClaim, "true"));

        var token = new JwtSecurityToken(
            issuer: _configuration["JWT:Issuer"],
            audience: _configuration["JWT:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expiryMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string GenerateApiClientToken(QMgr.Domain.Entities.Integration.ApiClient client)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JWT:Secret"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, client.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("client_id", client.ClientId),
            new("org_id", client.OrganizationId.ToString()),
            new("system_type", client.SystemType ?? "custom"),
            // Same marker ApiKeyAuthenticationMiddleware sets for the X-API-Key path — without it,
            // PermissionAuthorizationHandler falls through to its user-permission lookup, treating
            // `sub` (this ApiClient's Id) as a User.Id that never matches, silently denying every
            // [RequirePermission] endpoint regardless of the client's configured scopes. Found live
            // 2026-08-31 testing the ERP Bridge: token issuance succeeded but every scoped call 403'd.
            new("auth_method", "api_key")
        };

        // Add scopes
        if (client.Scopes != null)
        {
            foreach (var scope in client.Scopes)
            {
                claims.Add(new Claim("scope", scope));
            }
        }

        var token = new JwtSecurityToken(
            issuer: _configuration["JWT:Issuer"],
            audience: _configuration["JWT:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    /// <summary>
    /// Step 1 of self-service password reset: requests a reset link by email. Always returns a
    /// generic success response whether or not the email matches an account (and whether or not
    /// sending the email actually succeeds) - same "don't leak which emails are registered"
    /// convention as ResendVerificationCommandHandler.
    /// </summary>
    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        const string genericMessage = "If an account exists for that email address, we've sent a link to reset the password.";

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return Ok(new { message = genericMessage });
        }

        try
        {
            var email = request.Email.Trim().ToLowerInvariant();
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email && u.IsActive);

            if (user != null)
            {
                var token = GenerateRefreshToken();
                user.PasswordResetToken = token;
                user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
                await _dbContext.SaveChangesAsync();

                await SendPasswordResetEmailAsync(user.Email!, user.FirstName, token, user.OrganizationId);
                _logger.LogInformation("Password reset requested for {Email}", user.Email);
            }
        }
        catch (Exception ex)
        {
            // Still return the generic success message - a delivery failure shouldn't tell an
            // attacker anything different than "no such account" would.
            _logger.LogError(ex, "Error processing forgot-password request for {Email}", request.Email);
        }

        return Ok(new { message = genericMessage });
    }

    /// <summary>
    /// Step 2 of self-service password reset: exchanges a valid, unexpired token for a new
    /// password. Also clears the token (single-use) and revokes the existing refresh token, so a
    /// reset forces re-login everywhere rather than leaving an old session silently valid.
    /// </summary>
    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword([FromBody] SelfServiceResetPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Email, token, and new password are all required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (request.NewPassword != request.ConfirmPassword)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Passwords do not match.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email && u.IsActive);

        // Deliberately the same "invalid or expired" message whether the email doesn't exist,
        // the token doesn't match, or it's simply expired - never confirms which case applies.
        const string invalidTokenMessage = "This reset link is invalid or has expired. Please request a new one.";

        if (user == null ||
            string.IsNullOrEmpty(user.PasswordResetToken) ||
            user.PasswordResetToken != request.Token ||
            user.PasswordResetTokenExpiry == null ||
            user.PasswordResetTokenExpiry < DateTime.UtcNow)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid or expired link",
                Detail = invalidTokenMessage,
                Status = StatusCodes.Status400BadRequest
            });
        }

        var organizationName = await _dbContext.Organizations.IgnoreQueryFilters().Where(o => o.Id == user.OrganizationId).Select(o => o.Name).FirstOrDefaultAsync();
        var passwordValidation = await _passwordValidationService.ValidatePasswordAsync(
            request.NewPassword, user.Username, user.Email, organizationName);

        if (!passwordValidation.IsValid)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Password validation failed",
                Detail = passwordValidation.ErrorMessage,
                Status = StatusCodes.Status400BadRequest
            });
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        // A reset through the emailed link is the person choosing their own password, so any temporary
        // one an administrator issued is gone with it.
        user.MustChangePassword = false;
        user.TemporaryPasswordExpiresAt = null;
        // Force re-login everywhere - a leaked reset link shouldn't also inherit whatever
        // refresh token an attacker's own prior session might already hold.
        user.RefreshToken = null;
        user.RefreshTokenExpiry = null;
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;

        // Roll the credential version, which signs out every MOBILE DEVICE on its next refresh.
        // Clearing User.RefreshToken above only ever covered the browser, so before this a
        // password reset left every handset signed in with the old credential — which is exactly
        // the situation a reset exists to end.
        QMgr.Infrastructure.Services.Mobile.CredentialStamps.Roll(user);

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Password reset completed for {Email}", user.Email);

        return Ok(new { message = "Your password has been reset. You can now sign in with your new password." });
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    //  The mobile shell's session endpoints.
    //
    //  Kept here rather than in a controller of their own because they share this controller's
    //  route prefix, its JWT generation and its user-info mapping — and a second controller on
    //  `api/v1/auth` would be a second place to look for the same thing.
    //  See docs/plans/MOBILE_APP_INTEGRATION.md §3.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Devices currently holding a live session for the signed-in user — the "your devices" screen,
    /// and the answer to a lost phone.
    /// </summary>
    [HttpGet("devices")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    [ProducesResponseType(typeof(MobileEnvelope<List<DeviceSessionDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Devices([FromQuery] string? deviceId = null)
    {
        if (!TryCurrentUserId(out var userId)) return Unauthorized();

        var devices = await _deviceSessions.ListAsync(userId, deviceId);
        return Ok(MobileEnvelope<List<DeviceSessionDto>>.Ok(devices.ToList()));
    }

    /// <summary>
    /// Step 1 of moving a signed-in native identity into the app's own WebView: exchange the bearer
    /// token for a code that is single-use and dead in 60 seconds.
    ///
    /// <para>Step 2 is <b>not here</b>. Q-Mgr's web session lives in <c>localStorage</c>, not in a
    /// cookie, so there is nothing an API redirect could set — the code is redeemed by the Web
    /// project's <c>/mobile-session</c> page, which calls <see cref="WebSessionRedeem"/>
    /// server-to-server and then writes the session the same way an ordinary sign-in does.</para>
    /// </summary>
    [HttpPost("web-handoff")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    [ProducesResponseType(typeof(MobileEnvelope<HandoffCodeDto>), StatusCodes.Status200OK)]
    public IActionResult WebHandoff()
    {
        if (!TryCurrentUserId(out var userId)) return Unauthorized();

        var (code, expiresAt) = _handoff.Issue(userId);
        return Ok(MobileEnvelope<HandoffCodeDto>.Ok(new HandoffCodeDto
        {
            Code = code,
            ExpiresAt = expiresAt
        }));
    }

    /// <summary>
    /// Step 2, called SERVER-TO-SERVER by the Web project's <c>/mobile-session</c> page — never by a
    /// browser, and never navigated to.
    ///
    /// <para>That is the whole difference from the ERP's version of this endpoint. There, the
    /// browser navigates here and the response is a redirect that sets a cookie, which is why every
    /// failure path had to redirect rather than return JSON. Here the caller is C# and wants an
    /// answer it can act on, so JSON is correct — and the redirect discipline moves to the Blazor
    /// page, which IS navigated to.</para>
    ///
    /// <para>Anonymous because the whole point is that the caller holds no token yet; the code is
    /// the credential, and it was minted against a bearer token one hop ago.</para>
    /// </summary>
    /// <summary>
    /// The NAVIGATION half of the handoff: the app points its WebView here, and this sends the
    /// browser on to the Blazor page that can actually establish the session.
    ///
    /// <para><b>Why this exists at all.</b> The shell was written against a cookie-authenticated web
    /// UI, where navigating to this endpoint sets a cookie and redirects, and it therefore performs a
    /// <b>GET</b>. Q-Mgr's web is Blazor Server with its session in <c>localStorage</c>, so the
    /// redemption has to happen in the Web project and the endpoint below is a POST returning JSON.
    /// Those two facts met on a real handset: the WebView issued
    /// <c>GET /api/v1/auth/web-session?code=…</c>, ASP.NET answered <b>405 Method Not Allowed</b>,
    /// and Android rendered <c>ERR_HTTP_RESPONSE_CODE_FAILURE</c> — a blank error page immediately
    /// after a successful sign-in. Found on a device, 2026-09-22; no curl suite could have caught it,
    /// because a suite posts the body the API documents.</para>
    ///
    /// <para><b>Fixed here rather than in the app, deliberately.</b> An installed APK cannot be
    /// reissued to every handset that already has it, so the server meets the contract the shipped
    /// binary uses. This redirect keeps working for any build of the app, old or new.</para>
    ///
    /// <para><b>It does NOT redeem.</b> The code is single-use and short-lived; consuming it here
    /// would leave the Blazor page with nothing to redeem. This only forwards it.</para>
    /// </summary>
    [HttpGet("web-session")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public IActionResult WebSessionNavigate([FromQuery] string? code, [FromQuery] string? returnUrl)
    {
        // A missing code is somebody opening the URL by hand. Send them to sign in rather than to a
        // page whose only job is to fail.
        if (string.IsNullOrWhiteSpace(code))
            return Redirect("/login");

        // returnUrl is attacker-supplied on an anonymous endpoint, so only a LOCAL path is ever
        // passed on: "//evil.example" and "https://evil.example" are both absolute to a browser.
        // The Blazor page validates it again — this is the first of two, not the only one.
        var target = !string.IsNullOrWhiteSpace(returnUrl)
                     && returnUrl.StartsWith('/')
                     && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl
            : "/";

        return Redirect($"/mobile-session?code={Uri.EscapeDataString(code)}&returnUrl={Uri.EscapeDataString(target)}");
    }

    [HttpPost("web-session")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> WebSessionRedeem([FromBody] WebSessionRequest request)
    {
        // Consumed FIRST. A failure below must not leave a replayable code behind.
        var userId = _handoff.Redeem(request.Code);
        if (userId is null)
            return Unauthorized(new { message = "That sign-in link has expired. Please try again from the app." });

        var user = await _dbContext.Users
            .IgnoreQueryFilters()
            .Include(u => u.Role)
                .ThenInclude(r => r.RolePermissions)
                    .ThenInclude(rp => rp.Permission)
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u => u.Id == userId.Value);

        if (user == null || !user.IsActive)
            return Unauthorized(new { message = "That session is no longer valid." });

        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow)
            return Unauthorized(new { message = "This account is locked. Contact your administrator." });

        var expiryMinutes = await GetTokenExpiryMinutesAsync();

        // A web session, so the BROWSER's token column — not a device session. The WebView is a
        // browser: giving it a device token would put two rotating credentials on one handset,
        // either of which could revoke the other by racing it.
        var webRefresh = GenerateRefreshToken();
        user.RefreshToken = webRefresh;
        user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);
        await _dbContext.SaveChangesAsync();

        return Ok(new LoginResponse
        {
            AccessToken = GenerateJwtToken(user, expiryMinutes),
            RefreshToken = webRefresh,
            ExpiresIn = expiryMinutes * 60,
            User = await BuildUserInfoWithPostsAsync(user)
        });
    }

    // ── Shared helpers for the endpoints above ───────────────────────────────────────────────

    /// <summary>
    /// True when this looks like a per-device token — <c>{userId}.{deviceId}.{secret}</c>.
    ///
    /// <para>Recognised by SHAPE rather than by a flag the caller sets, because the caller is a
    /// client and a client-supplied discriminator is a client-supplied choice of code path. The two
    /// shapes cannot be confused: a browser token is raw base64, which contains no '.', so a
    /// three-part value whose first part parses as a Guid is a device token and nothing else is.</para>
    /// </summary>
    private static bool LooksLikeDeviceToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.', 3);
        return parts.Length == 3
               && parts.All(p => !string.IsNullOrWhiteSpace(p))
               && Guid.TryParse(parts[0], out _);
    }

    private bool TryCurrentUserId(out Guid userId)
    {
        var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(raw, out userId);
    }

    /// <summary>
    /// The one mapping from a loaded <c>User</c> to the wire's <c>UserInfo</c>.
    ///
    /// <para>Extracted because login, refresh and the handoff all had to build it, and three
    /// hand-written copies of an object initialiser is how a field gets added to two of them. This
    /// project has no auto-mapper — Mapster was removed in 2026-08 with zero call sites — so a
    /// missed field here is a silent runtime difference, not a compile error.</para>
    ///
    /// <para><b>Signs the photo link</b>, which is what makes a picture appear at all: the link is
    /// gated and dies in an hour, so it is minted at the point the DTO reaches a caller.</para>
    /// </summary>
    /// <summary>
    /// <see cref="BuildUserInfo"/> plus what the person's POSTS grant.
    ///
    /// <para>This is the half the browser reads: <c>UserInfo.Permissions</c> is what every
    /// <c>@if (HasPermission(...))</c> in the Web renders on, and it is PERSISTED in localStorage at
    /// sign-in. If the API's gate derived a post's permissions and this did not, a class teacher
    /// would be allowed through every endpoint and shown no button to reach them — which is the
    /// original defect wearing the opposite face.</para>
    /// </summary>
    private async Task<UserInfo> BuildUserInfoWithPostsAsync(QMgr.Domain.Entities.Identity.User user)
    {
        var info = BuildUserInfo(user);
        var posts = await PostPermissionService.GrantsForAsync(_dbContext, user.Id);
        if (!posts.Any) return info;

        var merged = new List<string>(info.Permissions);
        foreach (var code in posts.Permissions)
            if (!merged.Contains(code, StringComparer.OrdinalIgnoreCase)) merged.Add(code);
        return info with { Permissions = merged };
    }

    private static UserInfo BuildUserInfo(QMgr.Domain.Entities.Identity.User user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        Email = user.Email ?? string.Empty,
        FullName = user.FullName,
        RoleId = user.RoleId,
        RoleCode = user.Role.Code,
        RoleName = user.Role.Name,
        RoleColor = user.Role.Color,
        OrganizationId = user.OrganizationId,
        OrganizationName = user.Organization?.Name,
        BranchId = user.AssignedBranchId,
        PhotoUrl = UploadLinks.Sign(user.PhotoUrl),
        Permissions = user.Role.RolePermissions.Select(rp => rp.Permission.Code).ToList()
    };

    /// <summary>
    /// The sign-out row on a person's activity trail. Never throws — a failed audit write must not
    /// fail a sign-out that has already happened.
    /// </summary>
    private async Task RecordSignOutAsync(QMgr.Domain.Entities.Identity.User user)
    {
        await _activity.RecordAsync(ActivityActions.SignedOut, "User", user.Id, user.Id,
            $"{(string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName)} signed out",
            organizationId: user.OrganizationId, branchId: user.AssignedBranchId, actorUserId: user.Id);
    }

    private async Task SendPasswordResetEmailAsync(string toEmail, string? firstName, string token, Guid? organizationId = null)
    {
        var baseUrl = await _platformSettingsService.GetPublicWebBaseUrlAsync();
        var resetUrl = $"{baseUrl}/reset-password?email={Uri.EscapeDataString(toEmail)}&token={Uri.EscapeDataString(token)}";

        // A password-reset mail is the one a member of staff is most likely to look at twice,
        // because they are checking it is genuine — so it is the one that most needs their own
        // school's name on it rather than ours. Falls back to the platform brand for an
        // organization that is not white-labelled, which is what it has always said.
        var brand = await _emailBrands.ForOrganizationAsync(organizationId);

        var subject = $"Reset your {brand.Name} password";
        var htmlBody = EmailTemplates.Layout(
            "Reset your password",
            firstName,
            new[]
            {
                $"We received a request to reset the password on your {EmailTemplates.P(brand.Name)} account. Click the button below to choose a new one.",
                "This link will expire in 1 hour."
            },
            "Reset Password",
            resetUrl,
            footerNote: "If you didn't request this, you can safely ignore this email; your password will not be changed.",
            showLinkFallback: true,
            brand: brand);

        await _emailSender.SendAsync(toEmail, subject, htmlBody);
    }
}

public record IdentifyRequest
{
    public string Email { get; init; } = string.Empty;
}

public record IdentifyResponse
{
    public string Email { get; init; } = string.Empty;
    public Guid OrganizationId { get; init; }
    public string OrganizationName { get; init; } = string.Empty;
    public string OrganizationSlug { get; init; } = string.Empty;
    public bool HasPassword { get; init; }
}

public record LoginRequest
{
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public Guid? OrganizationId { get; init; }

    // ── The mobile shell's fields ────────────────────────────────────────────────────────
    //
    // All optional, so the Blazor web posts exactly what it always did and gets exactly what it
    // always got. Supplying DeviceId is what switches this sign-in onto a per-device session
    // (UserDeviceSession) instead of the browser's single User.RefreshToken column — which is how
    // a phone signing in stopped evicting the browser.

    /// <summary>
    /// The mobile shell accepts an email OR a username and posts it as <c>userName</c>. Both land
    /// on the same identifier: <see cref="Email"/> here has never meant "an email address" — it is
    /// matched against <c>Username</c> too, a 2026-08-21 decision, and 133 of the 184 staff on the
    /// first real school roll have no address at all.
    /// </summary>
    public string? UserName { get; init; }

    /// <summary>Present means "this is a device, give it its own session".</summary>
    public string? DeviceId { get; init; }

    /// <summary>What its owner will recognise in a device list — "Samsung SM-A155F".</summary>
    public string? DeviceName { get; init; }

    public string? Platform { get; init; }
    public string? AppVersion { get; init; }
    public long AppVersionCode { get; init; }

    /// <summary>The identifier actually used, whichever field carried it.</summary>
    public string Identifier =>
        !string.IsNullOrWhiteSpace(Email) ? Email
        : (UserName ?? string.Empty);
}

public record LoginResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public int ExpiresIn { get; init; }
    public UserInfo? User { get; init; }
    /// <summary>The token is password-change-only; the client must send the person to set their password.</summary>
    public bool MustChangePassword { get; init; }
}

public record ClientCredentialsRequest
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
}

public record TokenResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string TokenType { get; init; } = "Bearer";
    public int ExpiresIn { get; init; }
}

public record RefreshTokenRequest
{
    public string RefreshToken { get; init; } = string.Empty;
}

/// <summary>
/// The body of the server-to-server handoff redemption. Its own record rather than a bare string
/// parameter so the route cannot be reached by a GET — this exchanges a credential, and a GET would
/// put it in a log.
/// </summary>
public record WebSessionRequest
{
    public string Code { get; init; } = string.Empty;
}

public record ForgotPasswordRequest
{
    public string Email { get; init; } = string.Empty;
}

public record SelfServiceResetPasswordRequest
{
    public string Email { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public string NewPassword { get; init; } = string.Empty;
    public string ConfirmPassword { get; init; } = string.Empty;
}
