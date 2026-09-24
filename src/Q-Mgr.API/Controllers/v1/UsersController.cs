using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.Import;
using QMgr.Application.Tenant;
using QMgr.Application.Interfaces;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Identity;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services.Storage;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/users")]
[Authorize]
[Produces("application/json")]
public class UsersController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UsersController> _logger;
    private readonly IPasswordValidationService _passwordValidation;
    private readonly INotificationHubService _notificationHub;
    private readonly QMgr.Infrastructure.Services.IStaffProfileChangeNotifier _profileChanges;
    private readonly QMgr.Application.Interfaces.Billing.IBillingService _billing;

    private Guid? GetCurrentUserIdOrNull()
    {
        var raw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// A ROLE HAS TWO GATES, AND SO DOES A PERSON (2026-09-24). RoleAssignmentGuard already bounded the role being
    /// GIVEN; nothing bounded the person being CHANGED. So anybody holding users.edit could move a superior DOWN,
    /// switch them off, delete them, or change the email address a password-reset link goes to — which is taking
    /// over their account by the side door ResetPassword closed. The rule is ResetPassword's: somebody else's
    /// account may be changed only when their CURRENT role is one the caller could have given them. One's own
    /// account is always one's own. Answers null when the change may go ahead.
    /// </summary>
    private async Task<IActionResult?> SubjectRefusalAsync(User subject, string title)
    {
        var actorId = GetCurrentUserIdOrNull();
        if (actorId is null) return Unauthorized();
        if (subject.Id == actorId.Value) return null;
        var role = subject.Role ?? await _dbContext.Roles.FindAsync(subject.RoleId);
        if (role == null) return null;
        var refusal = await RoleAssignmentGuard.RefusalAsync(_dbContext, actorId.Value, role);
        return refusal == null ? null : BadRequest(new ProblemDetails
        {
            Title = title,
            Detail = $"{role.Name} is above what you may change. {refusal}",
            Status = StatusCodes.Status400BadRequest
        });
    }

    public UsersController(
        QMgrDbContext dbContext,
        ITenantContextAccessor tenantAccessor,
        IMemoryCache cache,
        ILogger<UsersController> logger,
        IPasswordValidationService passwordValidation,
        INotificationHubService notificationHub,
        QMgr.Infrastructure.Services.IStaffProfileChangeNotifier profileChanges,
        QMgr.Application.Interfaces.Billing.IBillingService billing)
    {
        _billing = billing;
        _profileChanges = profileChanges;
        _notificationHub = notificationHub;
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _cache = cache;
        _logger = logger;
        _passwordValidation = passwordValidation;
    }

    /// <summary>Pushes a live "your permissions changed" signal to the user's open sessions.
    /// Best-effort: the server-side cache invalidation is what enforces the change; this only
    /// makes the client UI catch up without waiting for the next login.</summary>
    private async Task NotifyPermissionsChangedSafeAsync(Guid userId)
    {
        try { await _notificationHub.NotifyPermissionsChangedAsync(userId); }
        catch (Exception ex) { _logger.LogDebug(ex, "PermissionsChanged push failed for {UserId}", userId); }
    }

    /// <summary>
    /// Gets all users (including inactive for admin)
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.UsersView)]
    [ProducesResponseType(typeof(List<UserDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUsers([FromQuery] bool includeInactive = false, [FromQuery] Guid? branchId = null)
    {
        var query = _dbContext.Users.AsQueryable();

        // SECURITY: Filter by organization (except for SuperAdmin/PlatformAdmin)
        // Note: JWT role claim uses lowercase code with hyphen (e.g., "super-admin")
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Organization not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only see users from their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }
        // SuperAdmin/PlatformAdmin: can see all users (no filter)

        if (!includeInactive)
            query = query.Where(u => u.IsActive);

        if (branchId.HasValue)
            query = query.Where(u => u.AssignedBranchId == branchId.Value);

        var users = await query
            .Include(u => u.Role)
            .Include(u => u.AssignedBranch)
            .Include(u => u.AssignedCounter)
            .OrderBy(u => u.FirstName)
            .ThenBy(u => u.LastName)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email ?? string.Empty,
                FirstName = u.FirstName,
                LastName = u.LastName,
                OrganizationId = u.OrganizationId,
                Phone = u.Phone,
                EmployeeNumber = u.EmployeeNumber,
                RoleId = u.RoleId,
                RoleCode = u.Role.Code,
                RoleName = u.Role.Name,
                RoleColor = u.Role.Color,
                PhotoUrl = u.PhotoUrl,
                AssignedBranchId = u.AssignedBranchId,
                AssignedBranchName = u.AssignedBranch != null ? u.AssignedBranch.Name : null,
                AssignedCounterId = u.AssignedCounterId,
                AssignedCounterNumber = u.AssignedCounter != null ? u.AssignedCounter.CounterNumber : null,
                IsActive = u.IsActive,
                LastLogin = u.LastLogin,
                CreatedAt = u.CreatedAt
            })
            .ToListAsync();

        // Signed AFTER materialisation: UploadLinks.Sign cannot translate to SQL, so the
        // projection above carries the raw column and the token is minted here.
        users = users.Select(d => Named(d) with { PhotoUrl = UploadLinks.Sign(d.PhotoUrl) }).ToList();

        return Ok(users);
    }

    /// <summary>
    /// Gets a specific user by ID
    /// </summary>
    [HttpGet("{userId:guid}")]
    [RequirePermission(Permissions.UsersView)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUser(Guid userId)
    {
        // SECURITY: Build organization filter (except for SuperAdmin/PlatformAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        var query = _dbContext.Users
            .Include(u => u.Role)
            .Include(u => u.AssignedBranch)
            .Include(u => u.AssignedCounter)
            .Where(u => u.Id == userId);

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Organization not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only see users from their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }

        var user = await query
            .Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email ?? string.Empty,
                FirstName = u.FirstName,
                LastName = u.LastName,
                OrganizationId = u.OrganizationId,
                Phone = u.Phone,
                EmployeeNumber = u.EmployeeNumber,
                RoleId = u.RoleId,
                RoleCode = u.Role.Code,
                RoleName = u.Role.Name,
                RoleColor = u.Role.Color,
                PhotoUrl = u.PhotoUrl,
                AssignedBranchId = u.AssignedBranchId,
                AssignedBranchName = u.AssignedBranch != null ? u.AssignedBranch.Name : null,
                AssignedCounterId = u.AssignedCounterId,
                AssignedCounterNumber = u.AssignedCounter != null ? u.AssignedCounter.CounterNumber : null,
                IsActive = u.IsActive,
                LastLogin = u.LastLogin,
                CreatedAt = u.CreatedAt
            })
            .FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = $"User with ID '{userId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        return Ok(Named(user) with { PhotoUrl = UploadLinks.Sign(user.PhotoUrl) });
    }

    /// <summary>
    /// The name in this person's organisation's own order, and the key a list sorts them on — written
    /// after materialisation, because PersonNames cannot translate to SQL. The projection used to
    /// concatenate first name then surname in the query itself, whatever the school had chosen.
    /// </summary>
    private static UserDto Named(UserDto d) => d with
    {
        FullName = PersonNames.Display(d.OrganizationId, d.FirstName, d.LastName, d.Username),
        SortName = PersonNames.SortKey(d.OrganizationId, d.FirstName, d.LastName)
    };

    /// <summary>
    /// Creates a new user
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.UsersCreate)]
    [CheckLimit("users")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
    {
        // Validation
        if (string.IsNullOrWhiteSpace(request.Username))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Username is required.",
                Status = StatusCodes.Status400BadRequest
            });

        // AN EMAIL ADDRESS IS OPTIONAL. Most staff on a Ugandan school roll have none — 133 of the
        // first 184 imported — and this product's login has always accepted a username instead. What
        // is NOT optional is being able to reach them with a first password, which is the caller's
        // job: a slip, or a phone number for SMS. See docs/plans/STAFF_WITHOUT_EMAIL.md.
        if (!string.IsNullOrWhiteSpace(request.Email) && !ImportRules.LooksLikeEmail(request.Email))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = $"\"{request.Email}\" is not an email address. Leave it empty if this person has none.",
                Status = StatusCodes.Status400BadRequest
            });

        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Detail = "Password is required.",
                Status = StatusCodes.Status400BadRequest
            });

        // Validate password against platform policy
        var passwordValidation = await _passwordValidation.ValidatePasswordAsync(
            request.Password,
            request.Username,
            request.Email ?? string.Empty);

        if (!passwordValidation.IsValid)
            return BadRequest(new ProblemDetails
            {
                Title = "Password validation failed",
                Detail = passwordValidation.ErrorMessage,
                Status = StatusCodes.Status400BadRequest
            });

        // SECURITY: Get organization from tenant context (except for SuperAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        Guid organizationId;

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Organization not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });
            organizationId = tenantContext.OrganizationId;
        }
        else
        {
            // SuperAdmin can create users in specific organizations via request
            // If not specified, use default organization
            organizationId = request.OrganizationId ?? Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // Check for duplicate username within organization
        var existingUsername = await _dbContext.Users
            .AnyAsync(u => u.Username == request.Username && u.OrganizationId == organizationId);
        if (existingUsername)
            return BadRequest(new ProblemDetails
            {
                Title = "Duplicate username",
                Detail = $"A user with username '{request.Username}' already exists in this organization.",
                Status = StatusCodes.Status400BadRequest
            });

        // Check for duplicate email within organization. Nobody collides with "no address": that is
        // what NULL is for, and the unique indexes treat every one of them as distinct.
        var existingEmail = !string.IsNullOrWhiteSpace(request.Email) && await _dbContext.Users
            .AnyAsync(u => u.Email == request.Email && u.OrganizationId == organizationId);
        if (existingEmail)
            return BadRequest(new ProblemDetails
            {
                Title = "Duplicate email",
                Detail = $"A user with email '{request.Email}' already exists in this organization.",
                Status = StatusCodes.Status400BadRequest
            });

        // Validate role exists
        Role? role = null;
        if (request.RoleId.HasValue)
        {
            role = await _dbContext.Roles.FindAsync(request.RoleId.Value);
            if (role == null)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid role",
                    Detail = $"Role with ID '{request.RoleId}' was not found.",
                    Status = StatusCodes.Status400BadRequest
                });
        }
        else
        {
            // Default to Staff role
            role = await _dbContext.Roles.FirstOrDefaultAsync(r => r.Code == RoleCodes.Staff && r.OrganizationId == null);
            if (role == null)
                return BadRequest(new ProblemDetails
                {
                    Title = "Configuration error",
                    Detail = "Default 'staff' role not found. Please contact administrator.",
                    Status = StatusCodes.Status500InternalServerError
                });
        }

        // SECURITY: "nobody chooses their own privilege". Both this endpoint and UpdateUser resolved
        // the role with a bare FindAsync and assigned it, with no rank check at all — so a tenant
        // Admin holding users.create could POST the seeded super-admin role's id and mint a Platform
        // Administrator, a role that bypasses every permission check and reaches every organization.
        // RoleAssignmentGuard is the one rule for this (it refuses super-admin outright, requires
        // roles.edit for Tenant Admin, enforces rank, and refuses a custom role carrying permissions
        // the caller lacks); the join-request and staff-import paths already called it.
        var actorId = GetCurrentUserIdOrNull();
        if (actorId is null)
            return Unauthorized();

        var roleRefusal = await RoleAssignmentGuard.RefusalAsync(_dbContext, actorId.Value, role!);
        if (roleRefusal != null)
            return BadRequest(new ProblemDetails
            {
                Title = "Role cannot be assigned",
                Detail = roleRefusal,
                Status = StatusCodes.Status400BadRequest
            });

        // THE STAFF NUMBER IS REQUIRED and unique within the organisation, ignoring case. Until now
        // this endpoint wrote whatever it was given and left the unique index to throw, which the
        // browser sees as a 500 with no field named — and a platform administrator creating a
        // platform user is the one person who has no such number, so the role decides.
        var numberRequired = !RoleCodes.PlatformOnly.Contains(role!.Code);
        if (numberRequired || !string.IsNullOrWhiteSpace(request.EmployeeNumber))
        {
            var numberError = PersonCode.ValidateStaffNumber(request.EmployeeNumber);
            if (numberError != null)
                return BadRequest(new ProblemDetails
                {
                    Title = numberError,
                    Detail = "It is the school's own number for this person, and it is how somebody with no email address is recognised when the staff list is imported again.",
                    Status = StatusCodes.Status400BadRequest
                });

            var numberKey = PersonCode.Key(request.EmployeeNumber);
            if (await _dbContext.Users.AnyAsync(u => u.OrganizationId == organizationId && u.EmployeeNumber != null
                                                     && u.EmployeeNumber.ToUpper() == numberKey))
                return Conflict(new ProblemDetails
                {
                    Title = "Somebody already has that staff number",
                    Detail = "A staff number identifies one person in this organization — it is how anybody with no email address is recognised.",
                    Status = StatusCodes.Status409Conflict
                });
        }

        // Validate branch exists within organization if specified
        if (request.AssignedBranchId.HasValue)
        {
            var branchExists = await _dbContext.Branches
                .AnyAsync(b => b.Id == request.AssignedBranchId.Value && b.OrganizationId == organizationId && b.IsActive);
            if (!branchExists)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid branch",
                    Detail = $"Branch with ID '{request.AssignedBranchId}' was not found, is inactive, or does not belong to this organization.",
                    Status = StatusCodes.Status400BadRequest
                });
        }

        // Validate counter exists within organization if specified
        if (request.AssignedCounterId.HasValue)
        {
            // Counter belongs to branch, so verify branch belongs to organization
            var counterExists = await _dbContext.Counters
                .AnyAsync(c => c.Id == request.AssignedCounterId.Value && c.IsActive
                    && c.Branch != null && c.Branch.OrganizationId == organizationId);
            if (!counterExists)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid counter",
                    Detail = $"Counter with ID '{request.AssignedCounterId}' was not found, is inactive, or does not belong to this organization.",
                    Status = StatusCodes.Status400BadRequest
                });
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId, // SECURITY: Always from tenant context or SuperAdmin decision
            Username = request.Username.ToLowerInvariant(),
            // Null, never "": two people with no address must both be storable, and an empty string
            // would collide on the unique index the second time.
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName,
            LastName = request.LastName,
            Phone = request.Phone,
            EmployeeNumber = PersonCode.Normalize(request.EmployeeNumber),
            RoleId = role.Id,
            AssignedBranchId = request.AssignedBranchId,
            AssignedCounterId = request.AssignedCounterId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created user: {UserId} - {Username}", user.Id, user.Username);

        var dto = new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email ?? string.Empty,
            FirstName = user.FirstName,
            LastName = user.LastName,
            FullName = user.FullName,
            SortName = PersonNames.SortKey(user),
            Phone = user.Phone,
            EmployeeNumber = user.EmployeeNumber,
            RoleId = role.Id,
            RoleCode = role.Code,
            RoleName = role.Name,
            RoleColor = role.Color,
            PhotoUrl = UploadLinks.Sign(user.PhotoUrl),
            AssignedBranchId = user.AssignedBranchId,
            AssignedCounterId = user.AssignedCounterId,
            IsActive = user.IsActive,
            CreatedAt = user.CreatedAt
        };

        return CreatedAtAction(nameof(GetUser), new { userId = user.Id }, dto);
    }

    /// <summary>
    /// Updates an existing user
    /// </summary>
    [HttpPut("{userId:guid}")]
    [RequirePermission(Permissions.UsersEdit)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateUser(Guid userId, [FromBody] UpdateUserRequest request)
    {
        // SECURITY: Build organization filter (except for SuperAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        var query = _dbContext.Users
            .Include(u => u.Role)
            .Include(u => u.AssignedBranch)
            .Include(u => u.AssignedCounter)
            .Where(u => u.Id == userId);

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Organization not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only modify users from their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = $"User with ID '{userId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        if (await SubjectRefusalAsync(user, "You cannot change this person's account") is { } subjectRefusal)
            return subjectRefusal;

        // Check for duplicate email if changed within organization
        if (!string.IsNullOrWhiteSpace(request.Email) && request.Email.ToLowerInvariant() != user.Email)
        {
            var existingEmail = await _dbContext.Users
                .AnyAsync(u => u.Email == request.Email.ToLowerInvariant() && u.Id != userId && u.OrganizationId == user.OrganizationId);
            if (existingEmail)
                return BadRequest(new ProblemDetails
                {
                    Title = "Duplicate email",
                    Detail = $"A user with email '{request.Email}' already exists.",
                    Status = StatusCodes.Status400BadRequest
                });
            user.Email = request.Email.ToLowerInvariant();
        }

        // Validate role exists if changing
        var roleChanged = false;
        var previousRoleId = user.RoleId;
        if (request.RoleId.HasValue)
        {
            var role = await _dbContext.Roles.FindAsync(request.RoleId.Value);
            if (role == null)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid role",
                    Detail = $"Role with ID '{request.RoleId}' was not found.",
                    Status = StatusCodes.Status400BadRequest
                });
            // SECURITY: same rule as CreateUser — without it a caller with users.edit could promote
            // anyone, themselves included, straight into the platform administrator role.
            var actorIdForRole = GetCurrentUserIdOrNull();
            if (actorIdForRole is null)
                return Unauthorized();

            var roleRefusal = await RoleAssignmentGuard.RefusalAsync(_dbContext, actorIdForRole.Value, role);
            if (roleRefusal != null)
                return BadRequest(new ProblemDetails
                {
                    Title = "Role cannot be assigned",
                    Detail = roleRefusal,
                    Status = StatusCodes.Status400BadRequest
                });

            roleChanged = user.RoleId != role.Id;
            user.RoleId = role.Id;
        }

        // Validate branch exists within organization if specified
        if (request.AssignedBranchId.HasValue)
        {
            var branchExists = await _dbContext.Branches
                .AnyAsync(b => b.Id == request.AssignedBranchId.Value && b.OrganizationId == user.OrganizationId && b.IsActive);
            if (!branchExists)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid branch",
                    Detail = $"Branch with ID '{request.AssignedBranchId}' was not found or is inactive.",
                    Status = StatusCodes.Status400BadRequest
                });
        }

        // Validate counter exists within organization if specified
        if (request.AssignedCounterId.HasValue)
        {
            var counterExists = await _dbContext.Counters
                .AnyAsync(c => c.Id == request.AssignedCounterId.Value && c.IsActive
                    && c.Branch != null && c.Branch.OrganizationId == user.OrganizationId);
            if (!counterExists)
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid counter",
                    Detail = $"Counter with ID '{request.AssignedCounterId}' was not found, is inactive, or does not belong to this organization.",
                    Status = StatusCodes.Status400BadRequest
                });
        }

        // A staff number this request CARRIES is validated and must not repeat; one it omits is left
        // alone, because this endpoint is also how a bulk role change and a branch move are applied
        // and neither is about the number. What it may never do is blank out a number somebody has:
        // that is the identity of anybody here with no email address.
        if (request.EmployeeNumber != null)
        {
            var numberError = PersonCode.ValidateStaffNumber(request.EmployeeNumber);
            if (numberError != null)
                return BadRequest(new ProblemDetails { Title = numberError, Status = StatusCodes.Status400BadRequest });

            var numberKey = PersonCode.Key(request.EmployeeNumber);
            if (await _dbContext.Users.AnyAsync(u => u.OrganizationId == user.OrganizationId && u.Id != user.Id
                                                     && u.EmployeeNumber != null && u.EmployeeNumber.ToUpper() == numberKey))
                return Conflict(new ProblemDetails
                {
                    Title = "Somebody already has that staff number",
                    Detail = "A staff number identifies one person in this organization.",
                    Status = StatusCodes.Status409Conflict
                });
        }

        // Update fields
        user.FirstName = request.FirstName ?? user.FirstName;
        user.LastName = request.LastName ?? user.LastName;
        user.Phone = request.Phone ?? user.Phone;
        user.EmployeeNumber = PersonCode.Normalize(request.EmployeeNumber) ?? user.EmployeeNumber;
        user.AssignedBranchId = request.AssignedBranchId;
        user.AssignedCounterId = request.AssignedCounterId;
        user.UpdatedAt = DateTime.UtcNow;

        // Update password if provided
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            // Validate password against platform policy
            var orgName = await _dbContext.Organizations.IgnoreQueryFilters().Where(o => o.Id == user.OrganizationId).Select(o => o.Name).FirstOrDefaultAsync();
            var passwordValidation = await _passwordValidation.ValidatePasswordAsync(
                request.Password,
                user.Username,
                user.Email,
                orgName);

            if (!passwordValidation.IsValid)
                return BadRequest(new ProblemDetails
                {
                    Title = "Password validation failed",
                    Detail = passwordValidation.ErrorMessage,
                    Status = StatusCodes.Status400BadRequest
                });

            // A password an administrator typed is a temporary one (duty rota plan §12.3): the
            // administrator knows it, so the person must replace it at first sign-in, within 72 hours.
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
            user.MustChangePassword = true;
            user.TemporaryPasswordExpiresAt = DateTime.UtcNow.Add(QMgr.API.Application.Services.TemporaryPasswords.Lifetime);
            user.RefreshToken = null;
            user.RefreshTokenExpiry = null;
        }

        await _dbContext.SaveChangesAsync();

        // SECURITY: a stale cached permission set from the user's *old* role must not
        // outlive a role change by up to CacheDuration (5 min) — most important on
        // downgrade/revocation, where the old set may be more privileged than the new one.
        if (roleChanged)
        {
            // Cache, live push, activity log and staff.profile-changed — one service, shared with the bulk path.
            await _profileChanges.RoleChangedAsync(user.OrganizationId, user.Id, previousRoleId, user.RoleId, GetCurrentUserIdOrNull(), "edited");
        }

        _logger.LogInformation("Updated user: {UserId} - {Username}", user.Id, user.Username);

        // Reload navigation properties for response
        await _dbContext.Entry(user).Reference(u => u.Role).LoadAsync();
        await _dbContext.Entry(user).Reference(u => u.AssignedBranch).LoadAsync();
        await _dbContext.Entry(user).Reference(u => u.AssignedCounter).LoadAsync();

        return Ok(new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email ?? string.Empty,
            FirstName = user.FirstName,
            LastName = user.LastName,
            FullName = user.FullName,
            SortName = PersonNames.SortKey(user),
            Phone = user.Phone,
            EmployeeNumber = user.EmployeeNumber,
            RoleId = user.RoleId,
            RoleCode = user.Role.Code,
            RoleName = user.Role.Name,
            RoleColor = user.Role.Color,
            PhotoUrl = UploadLinks.Sign(user.PhotoUrl),
            AssignedBranchId = user.AssignedBranchId,
            AssignedBranchName = user.AssignedBranch?.Name,
            AssignedCounterId = user.AssignedCounterId,
            AssignedCounterNumber = user.AssignedCounter?.CounterNumber,
            IsActive = user.IsActive,
            LastLogin = user.LastLogin,
            CreatedAt = user.CreatedAt
        });
    }

    /// <summary>
    /// Toggles a user's active status
    /// </summary>
    [HttpPatch("{userId:guid}/toggle")]
    [RequirePermission(Permissions.UsersEdit)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleUser(Guid userId)
    {
        // SECURITY: Build organization filter (except for SuperAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        var query = _dbContext.Users
            .Include(u => u.Role)
            .Include(u => u.AssignedBranch)
            .Include(u => u.AssignedCounter)
            .Where(u => u.Id == userId);

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Organization not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only modify users from their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = $"User with ID '{userId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        if (await SubjectRefusalAsync(user, "You cannot switch this person's account on or off") is { } subjectRefusal)
            return subjectRefusal;

        // Re-enabling somebody takes a seat: the limit counts ACTIVE users (see UserSeats), so without this
        // disable-then-enable would be a way round it. Disabling never needs room.
        if (!user.IsActive)
        {
            var refusal = await UserSeats.RefusalAsync(_billing, user.OrganizationId, 1);
            if (refusal != null)
                return StatusCode(StatusCodes.Status402PaymentRequired, new
                {
                    error = "LIMIT_EXCEEDED",
                    limitType = UserSeats.LimitType,
                    message = refusal,
                    upgradeUrl = BillingLinks.Modules
                });
        }

        user.IsActive = !user.IsActive;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        // SECURITY: deactivation must take effect immediately, not after the permission
        // cache's TTL — otherwise a just-deactivated user keeps working permission-gated
        // endpoints for up to 5 more minutes on their still-valid JWT.
        _cache.InvalidateUserPermissions(user.Id);
        await NotifyPermissionsChangedSafeAsync(user.Id);

        _logger.LogInformation("Toggled user {UserId} active status to {IsActive}", userId, user.IsActive);

        return Ok(new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email ?? string.Empty,
            FirstName = user.FirstName,
            LastName = user.LastName,
            FullName = user.FullName,
            SortName = PersonNames.SortKey(user),
            Phone = user.Phone,
            EmployeeNumber = user.EmployeeNumber,
            RoleId = user.RoleId,
            RoleCode = user.Role.Code,
            RoleName = user.Role.Name,
            RoleColor = user.Role.Color,
            PhotoUrl = UploadLinks.Sign(user.PhotoUrl),
            AssignedBranchId = user.AssignedBranchId,
            AssignedBranchName = user.AssignedBranch?.Name,
            AssignedCounterId = user.AssignedCounterId,
            AssignedCounterNumber = user.AssignedCounter?.CounterNumber,
            IsActive = user.IsActive,
            LastLogin = user.LastLogin,
            CreatedAt = user.CreatedAt
        });
    }

    /// <summary>
    /// Deletes a user (soft delete)
    /// </summary>
    [HttpDelete("{userId:guid}")]
    [RequirePermission(Permissions.UsersDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteUser(Guid userId)
    {
        // SECURITY: Build organization filter (except for SuperAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        var query = _dbContext.Users.Where(u => u.Id == userId);

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Organization not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only delete users from their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = $"User with ID '{userId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        if (await SubjectRefusalAsync(user, "You cannot remove this person's account") is { } subjectRefusal)
            return subjectRefusal;

        // Soft delete
        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        // SECURITY: see ToggleUser — don't let a cached permission set outlive deletion.
        _cache.InvalidateUserPermissions(user.Id);

        _logger.LogInformation("Deleted (soft) user: {UserId} - {Username}", userId, user.Username);

        return NoContent();
    }

    /// <summary>
    /// Issues somebody a new temporary password: one the administrator types, or one generated here.
    ///
    /// <para>THIS IS THE ONLY WAY BACK IN FOR MOST OF THIS PRODUCT'S USERS. <c>User.Email</c> is
    /// nullable and 133 of the 184 staff on the first real school list this product imported have no
    /// address at all, so the self-service reset link can never reach them. An administrator issuing
    /// one by hand — read out, or on a printed slip — is not a fallback here, it is the path.</para>
    ///
    /// <para>The password is returned ONCE and never stored readably. <c>MustChangePassword</c> is
    /// set, so the token that password buys is change-only (15 minutes, no refresh token,
    /// <c>PasswordChangeOnlyMiddleware</c> refuses everything else) and the person chooses their own
    /// before they can do anything. That forced change is what makes an administrator-chosen
    /// password acceptable at all — it is the same bargain Google Workspace and Entra ID strike.</para>
    /// </summary>
    [HttpPost("{userId:guid}/reset-password")]
    [RequirePermission(Permissions.UsersEdit)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword(Guid userId, [FromBody] ResetPasswordRequest request)
    {
        // SECURITY: Build organization filter (except for SuperAdmin)
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(roleClaim);
        // Include(Role) because the rank guard below weighs this person's role against the
        // caller's; without it RoleAssignmentGuard reads a null navigation and cannot refuse.
        var query = _dbContext.Users.Include(u => u.Role).Where(u => u.Id == userId);

        if (!isSuperAdmin)
        {
            var tenantContext = _tenantAccessor.TenantContext;
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Organization not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            // Regular org users: only reset passwords for users in their organization
            query = query.Where(u => u.OrganizationId == tenantContext.OrganizationId);
        }

        var user = await query.FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new ProblemDetails
            {
                Title = "User not found",
                Detail = $"User with ID '{userId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        // RESETTING SOMEBODY'S PASSWORD IS TAKING OVER THEIR ACCOUNT, so it is bounded by rank —
        // the same rule, and the same one home, that CreateUser and UpdateUser were put behind on
        // 2026-09-18 after a tenant Admin holding users.edit could mint themselves a Platform
        // Administrator. This endpoint was the door beside those two: it checked users.edit and the
        // organization and NOTHING ELSE, so the same Admin could reset a Platform Administrator's
        // password, sign in as them and reach every organization. StaffOnboardingController.Reissue
        // has always refused this — "that would be taking over their account" — and this did not.
        //
        // A reset of one's OWN password is always allowed: that is not an escalation, and an
        // administrator locked out of their own account is the case this exists for.
        var actorId = GetCurrentUserIdOrNull();
        if (actorId == null) return Unauthorized();
        if (user.Id != actorId.Value)
        {
            var rankRefusal = await RoleAssignmentGuard.RefusalAsync(_dbContext, actorId.Value, user.Role);
            if (rankRefusal != null)
                return BadRequest(new ProblemDetails
                {
                    Title = "You cannot reset this person's password",
                    Detail = rankRefusal,
                    Status = StatusCodes.Status400BadRequest
                });
        }

        // Generated by default. An administrator reading a password down a phone line may type their
        // own instead, and it then meets the policy and the blocklist exactly as a self-chosen one
        // would — so never "pass", never the school's name, never this person's own username.
        var generated = string.IsNullOrWhiteSpace(request.NewPassword);
        var newPassword = generated
            ? QMgr.API.Application.Services.TemporaryPasswords.Generate()
            : request.NewPassword;

        if (!generated)
        {
            var organizationName = await _dbContext.Organizations.IgnoreQueryFilters().Where(o => o.Id == user.OrganizationId).Select(o => o.Name).FirstOrDefaultAsync();
            var passwordValidation = await _passwordValidation.ValidatePasswordAsync(
                newPassword,
                user.Username,
                user.Email,
                organizationName);

            if (!passwordValidation.IsValid)
                return BadRequest(new ProblemDetails
                {
                    Title = "Password validation failed",
                    Detail = passwordValidation.ErrorMessage + " Leave it empty to have one generated.",
                    Status = StatusCodes.Status400BadRequest
                });
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.RefreshToken = null; // Invalidate any existing refresh tokens
        user.RefreshTokenExpiry = null;
        // An administrator's reset issues a temporary password (duty rota plan §12.3): the person must
        // choose their own at first sign-in, and it stops working after 72 hours.
        user.MustChangePassword = true;
        user.TemporaryPasswordExpiresAt = DateTime.UtcNow.Add(QMgr.API.Application.Services.TemporaryPasswords.Lifetime);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        // The lockout goes with it. Somebody who has forgotten their password has usually just tried
        // it five times, so leaving the counter standing means the new password they were read over
        // the phone is refused too — and the administrator has no way to see why. Reissue has always
        // cleared both; this did not.
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        // The password is never logged, here or anywhere. Which ACCOUNT was reset, and by whom, is
        // the useful half and is what an audit of this asks for.
        _logger.LogInformation("Reset password for user {UserId} ({Username}) by {ActorId}; generated={Generated}",
            userId, user.Username, actorId, generated);

        // Returned ONCE. The slip shape is the one the import and the re-issue already hand back, so
        // a caller printing or reading out a password has one shape to know rather than three.
        return Ok(new QMgr.Application.DTOs.TemporaryPasswordSlipDto
        {
            UserId = user.Id,
            FullName = user.FullName,
            SortName = PersonNames.SortKey(user),
            Username = user.Username,
            Email = user.Email ?? string.Empty,
            TemporaryPassword = newPassword,
            ExpiresAt = user.TemporaryPasswordExpiresAt ?? DateTime.UtcNow
        });
    }

    /// <summary>
    /// Gets available roles from database
    /// </summary>
    [HttpGet("roles")]
    [RequirePermission(Permissions.RolesView)]
    [ProducesResponseType(typeof(List<RoleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRoles()
    {
        var roles = await _dbContext.Roles
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .Select(r => new RoleDto
            {
                Id = r.Id,
                Code = r.Code,
                Name = r.Name,
                Description = r.Description,
                Color = r.Color,
                Icon = r.Icon,
                IsSystem = r.IsSystem
            })
            .ToListAsync();

        return Ok(roles);
    }
}

#region Request/Response DTOs

public record UserDto
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string FullName { get; init; } = string.Empty;

    /// <summary>What a list sorts this person on, in the organisation's chosen order (PersonNames.SortKey).</summary>
    public string? SortName { get; init; }

    /// <summary>Whose naming order applies. Carried from the query to <c>Named</c>; never sent.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid OrganizationId { get; init; }
    public string? Phone { get; init; }
    public string? EmployeeNumber { get; init; }
    public Guid RoleId { get; init; }
    public string RoleCode { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public string? RoleColor { get; init; }

    /// <summary>The person's photograph, already signed. Null when they have not added one.</summary>
    public string? PhotoUrl { get; init; }
    public Guid? AssignedBranchId { get; init; }
    public string? AssignedBranchName { get; init; }
    public Guid? AssignedCounterId { get; init; }
    public string? AssignedCounterNumber { get; init; }
    public bool IsActive { get; init; }
    public DateTime? LastLogin { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record CreateUserRequest
{
    public Guid? OrganizationId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Phone { get; init; }
    public string? EmployeeNumber { get; init; }
    public Guid? RoleId { get; init; }
    public Guid? AssignedBranchId { get; init; }
    public Guid? AssignedCounterId { get; init; }
}

public record UpdateUserRequest
{
    public string? Email { get; init; }
    public string? Password { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Phone { get; init; }
    public string? EmployeeNumber { get; init; }
    public Guid? RoleId { get; init; }
    public Guid? AssignedBranchId { get; init; }
    public Guid? AssignedCounterId { get; init; }
}

public record ResetPasswordRequest
{
    /// <summary>
    /// The password to set — or EMPTY, which is the normal case and has one generated. An empty
    /// value is not a validation failure here: "generate one for me" is the default an administrator
    /// should be nudged towards, and making it an error would push them towards typing something
    /// memorable instead.
    /// </summary>
    public string NewPassword { get; init; } = string.Empty;
}

public record RoleDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Color { get; init; }
    public string? Icon { get; init; }
    public bool IsSystem { get; init; }
}

#endregion
