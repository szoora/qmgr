using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.API.Application.Services;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Identity;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Email;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The public side of staff self-registration (duty rota plan §12.4): the join page's view of a link,
/// the email code, and the application itself. Anonymous by necessity; bounded by the rules in §13.18.
/// <list type="bullet">
/// <item><b>The code decides the organization.</b> Nothing here accepts an organization id. An unknown,
///   rotated, expired or switched-off code — or an organization without the module — is one 404.</item>
/// <item><b>Nothing about existing accounts leaks.</b> The code request and the application answer the same
///   way whether or not the address already has an account anywhere on the platform; the owner of an
///   existing address is emailed instead, and a duplicate is surfaced to the approver, never the applicant.</item>
/// <item><b>Rate limited per viewer and per code</b>, and a run of unknown codes from one address is logged.</item>
/// <item><b>The account it creates cannot sign in</b>: inactive, the viewer role as a placeholder, waiting for an approver.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/v1/public/join/{code}")]
[Produces("application/json")]
[AllowAnonymous]
public class PublicStaffJoinController : ControllerBase
{
    private readonly QMgrDbContext _db;
    private readonly IStaffOnboardingPolicyService _policy;
    private readonly IModuleAccessService _modules;
    private readonly IEmailCodeVerificationService _emailCodes;
    private readonly IPasswordValidationService _passwords;
    private readonly IEmailSender _email;
    private readonly INotificationService _notifications;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly IActivityLogger _activity;
    private readonly IDistributedCache _cache;
    private readonly ILogger<PublicStaffJoinController> _logger;

    private const string SentMessage = "If this email can be used to join, we have sent it a code. It may take a minute to arrive.";
    private const string AppliedMessage = "Thank you. Your request has been sent to the administrator. You will be emailed when it is approved.";

    public PublicStaffJoinController(
        QMgrDbContext db,
        IStaffOnboardingPolicyService policy,
        IModuleAccessService modules,
        IEmailCodeVerificationService emailCodes,
        IPasswordValidationService passwords,
        IEmailSender email,
        INotificationService notifications,
        IPlatformSettingsService platformSettings,
        IActivityLogger activity,
        IDistributedCache cache,
        ILogger<PublicStaffJoinController> logger)
    {
        _db = db;
        _policy = policy;
        _modules = modules;
        _emailCodes = emailCodes;
        _passwords = passwords;
        _email = email;
        _notifications = notifications;
        _platformSettings = platformSettings;
        _activity = activity;
        _cache = cache;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(JoinLinkInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetInfo(string code)
    {
        var (resolved, refusal) = await ResolveAsync(code);
        if (refusal != null) return refusal;
        var (orgId, policy) = resolved!.Value;

        var branches = await _db.Branches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.OrganizationId == orgId && b.IsActive)
            .OrderBy(b => b.Name)
            .Select(b => new JoinBranchOptionDto(b.Id, b.Name))
            .ToListAsync();
        var passwordPolicy = await _passwords.GetPasswordPolicyAsync();

        return Ok(new JoinLinkInfoDto
        {
            OrganizationName = await OrganizationNameAsync(orgId),
            Branches = branches,
            AllowedEmailDomains = policy.AllowedEmailDomains,
            PasswordMinimumLength = passwordPolicy.MinimumLength
        });
    }

    [HttpPost("email-code")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SendEmailCode(string code, [FromBody] JoinEmailCodeRequest request)
    {
        var (resolved, refusal) = await ResolveAsync(code);
        if (refusal != null) return refusal;
        var (orgId, policy) = resolved!.Value;

        if (!await ConsumeAsync($"join-send:ip:{ViewerAddress()}", 10, TimeSpan.FromHours(1))
            || !await ConsumeAsync($"join-send:code:{StaffOnboardingPolicyService.HashCode(code)}", 200, TimeSpan.FromHours(1)))
            return TooMany();

        var email = (request.Email ?? string.Empty).Trim();
        var normalized = RegistrationIdentity.NormalizeEmail(email);
        if (normalized == null || !email.Contains('@'))
            return BadRequest(new ProblemDetails { Title = "Enter a valid email address", Status = StatusCodes.Status400BadRequest });
        if (!DomainAllowed(policy, email))
            return BadRequest(new ProblemDetails { Title = "Use your school email address", Detail = $"Only addresses at {string.Join(", ", policy.AllowedEmailDomains)} can join.", Status = StatusCodes.Status400BadRequest });

        var orgName = await OrganizationNameAsync(orgId);
        var existing = await _db.Users.IgnoreQueryFilters().AsNoTracking().AnyAsync(u => u.NormalizedEmail == normalized);
        if (existing)
        {
            // The mailbox's owner learns an account exists; the page does not.
            await TellExistingOwnerAsync(email, orgName);
        }
        else
        {
            await _emailCodes.SendCodeAsync(email, $"Join {orgName}", orgName);
        }

        return Ok(new { message = SentMessage });
    }

    [HttpPost("apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Apply(string code, [FromBody] JoinApplicationRequest request)
    {
        var (resolved, refusal) = await ResolveAsync(code);
        if (refusal != null) return refusal;
        var (orgId, policy) = resolved!.Value;

        if (!await ConsumeAsync($"join-apply:ip:{ViewerAddress()}", 20, TimeSpan.FromHours(1))
            || !await ConsumeAsync($"join-apply:code:{StaffOnboardingPolicyService.HashCode(code)}", 300, TimeSpan.FromHours(1)))
            return TooMany();

        var firstName = (request.FirstName ?? "").Trim();
        var lastName = (request.LastName ?? "").Trim();
        var email = (request.Email ?? "").Trim();
        var normalized = RegistrationIdentity.NormalizeEmail(email);

        if (firstName.Length is 0 or > 100 || lastName.Length is 0 or > 100)
            return Problem400("Enter your first and last name");
        if (normalized == null || !email.Contains('@') || email.Length > 256)
            return Problem400("Enter a valid email address");
        if (!DomainAllowed(policy, email))
            return Problem400("Use your school email address", $"Only addresses at {string.Join(", ", policy.AllowedEmailDomains)} can join.");
        if (request.Password != request.ConfirmPassword)
            return Problem400("The passwords do not match");
        if ((request.Phone?.Length ?? 0) > 30 || (request.EmployeeNumber?.Length ?? 0) > 50 || (request.JobTitle?.Length ?? 0) > 100)
            return Problem400("One of the details is too long");

        var orgName = await OrganizationNameAsync(orgId);
        var validation = await _passwords.ValidatePasswordAsync(request.Password ?? "", email.Split('@')[0], email, orgName);
        if (!validation.IsValid)
            return Problem400("Choose a stronger password", validation.ErrorMessage);

        var (verified, codeMessage) = await _emailCodes.VerifyAndConsumeAsync(email, request.EmailCode ?? "");
        if (!verified)
            return Problem400("The email code did not work", codeMessage);

        // Branch: the applicant's choice within this organization, else the policy's default, else the only one.
        var branches = await _db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.OrganizationId == orgId && b.IsActive).Select(b => b.Id).ToListAsync();
        Guid? branchId = request.BranchId is { } chosen && branches.Contains(chosen) ? chosen
            : policy.DefaultBranchId is { } def && branches.Contains(def) ? def
            : branches.Count == 1 ? branches[0] : null;

        // An address that already has an account never reaches here with a valid code (none was sent), but
        // a race could; the answer stays the same either way.
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.NormalizedEmail == normalized))
            return Ok(new { message = AppliedMessage });

        var viewer = await _db.Roles.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(r => r.Code == RoleCodes.Viewer && r.OrganizationId == null);
        if (viewer == null)
        {
            _logger.LogError("Join application refused: the system viewer role is missing");
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "Joining is not available right now" });
        }

        var baseUsername = new string(email[..email.IndexOf('@')].ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        if (baseUsername.Length < 3) baseUsername = $"staff{Guid.NewGuid():N}"[..12];
        var username = baseUsername;
        for (var suffix = 2; await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Username == username); suffix++)
            username = $"{baseUsername}{suffix}";

        var user = new QMgr.Domain.Entities.Identity.User
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Username = username,
            Email = email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = firstName,
            LastName = lastName,
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
            EmployeeNumber = string.IsNullOrWhiteSpace(request.EmployeeNumber) ? null : request.EmployeeNumber.Trim(),
            JobTitle = string.IsNullOrWhiteSpace(request.JobTitle) ? null : request.JobTitle.Trim(),
            RoleId = viewer.Id,
            AssignedBranchId = branchId,
            IsActive = false,
            PendingApprovalAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        _db.Users.Add(user);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The unique email or username index: a second application raced this one. Same answer.
            return Ok(new { message = AppliedMessage });
        }

        // No applicant details in the log (plan §12.4: purged later; the event without them stays).
        await _activity.RecordAsync(ActivityActions.JoinRequested, "User", null, null,
            "A staff join request was submitted through the join link", organizationId: orgId, branchId: branchId);

        await NotifyApproversAsync(orgId);

        return Ok(new { message = AppliedMessage });
    }

    // ---------------------------------------------------------------------------------------------------

    private async Task<((Guid OrganizationId, StaffOnboardingPolicyDto Policy)? Resolved, IActionResult? Refusal)> ResolveAsync(string code)
    {
        var resolved = await _policy.ResolveCodeAsync(code);
        if (resolved == null || !await _modules.IsModuleActiveAsync(resolved.Value.OrganizationId, ModuleCodes.StudentWelfare))
        {
            var failures = await CountAsync($"join-badcode:{ViewerAddress()}", TimeSpan.FromHours(1));
            if (failures == 10 || failures % 50 == 0)
                _logger.LogWarning("Staff join: {Failures} unknown or expired join codes from one address this hour", failures);
            if (failures > 30) return (null, TooMany());
            return (null, NotFound(new ProblemDetails { Title = "This join link is not valid", Detail = "It may have expired or been replaced. Ask the school for the current link.", Status = StatusCodes.Status404NotFound }));
        }
        return (resolved, null);
    }

    private static bool DomainAllowed(StaffOnboardingPolicyDto policy, string email)
    {
        if (policy.AllowedEmailDomains.Count == 0) return true;
        var domain = email[(email.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant();
        return policy.AllowedEmailDomains.Any(d => domain == d || domain.EndsWith("." + d));
    }

    /// <summary>The reader's address as the Web relays it (X-Viewer-Ip), else the forwarded or connection address.</summary>
    private string ViewerAddress()
    {
        var relayed = Request.Headers["X-Viewer-Ip"].FirstOrDefault()?.Trim();
        var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim();
        return !string.IsNullOrWhiteSpace(relayed) ? relayed
            : !string.IsNullOrWhiteSpace(forwarded) ? forwarded
            : HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private async Task<int> CountAsync(string key, TimeSpan window)
    {
        var bucket = $"{key}:{DateTime.UtcNow.Ticks / window.Ticks}";
        var n = (int.TryParse(await _cache.GetStringAsync(bucket), out var v) ? v : 0) + 1;
        await _cache.SetStringAsync(bucket, n.ToString(), new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = window });
        return n;
    }

    private async Task<bool> ConsumeAsync(string key, int limit, TimeSpan window) => await CountAsync(key, window) <= limit;

    private ObjectResult TooMany()
    {
        Response.Headers.RetryAfter = "3600";
        return StatusCode(StatusCodes.Status429TooManyRequests, new ProblemDetails { Title = "Too many attempts", Detail = "Please try again later.", Status = StatusCodes.Status429TooManyRequests });
    }

    private BadRequestObjectResult Problem400(string title, string? detail = null)
        => BadRequest(new ProblemDetails { Title = title, Detail = detail, Status = StatusCodes.Status400BadRequest });

    private async Task<string> OrganizationNameAsync(Guid orgId)
        => await _db.Organizations.IgnoreQueryFilters().Where(o => o.Id == orgId).Select(o => o.BrandName ?? o.Name).FirstOrDefaultAsync() ?? "the school";

    private async Task TellExistingOwnerAsync(string email, string orgName)
    {
        try
        {
            var saas = await _platformSettings.GetSettingsAsync<SaasSettings>("SaaS");
            var baseUrl = (saas?.BaseUrl ?? "https://qmgr.app").TrimEnd('/');
            var html = EmailTemplates.Layout("You already have an account", null, new[]
            {
                $"Someone used this address to ask to join {EmailTemplates.B(orgName)} on {EmailTemplates.AppName}, but it already has an account.",
                "Sign in instead. If you have forgotten your password, use \"Forgot password\" on the sign-in page. If this was not you, you can ignore this email."
            }, "Sign in", EmailTemplates.Link(baseUrl, "/login"));
            await _email.SendAsync(email, $"About your {EmailTemplates.AppName} account", html);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not email the owner of an existing address about a join attempt"); }
    }

    /// <summary>
    /// A content-free bell to each approver — "join requests are waiting", a count and a link (plan §12.4) —
    /// at most once in twelve hours per approver, so a busy onboarding morning is one message, not forty.
    /// </summary>
    private async Task NotifyApproversAsync(Guid orgId)
    {
        try
        {
            var policy = await _policy.GetAsync(orgId);
            var cutoff = DateTime.UtcNow.AddDays(-policy.RequestExpiryDays);
            var waiting = await _db.Users.IgnoreQueryFilters().CountAsync(u => u.OrganizationId == orgId && !u.IsActive
                && u.PendingApprovalAt != null && u.JoinRequestRejectedAt == null && u.PendingApprovalAt > cutoff);

            var approvers = await StaffLookups.UsersWithPermissionAsync(_db, orgId, Permissions.UsersApprove);
            var since = DateTime.UtcNow.AddHours(-12);
            var recentlyTold = await _db.Notifications.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.OrganizationId == orgId && n.EventKey == NotificationEventKeys.StaffJoinRequests && n.CreatedAt >= since && n.UserId != null)
                .Select(n => n.UserId!.Value).Distinct().ToListAsync();

            foreach (var approver in approvers.Except(recentlyTold))
            {
                await _notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = approver,
                    OrganizationId = orgId,
                    EventKey = NotificationEventKeys.StaffJoinRequests,
                    Title = waiting == 1 ? "1 join request is waiting" : $"{waiting} join requests are waiting",
                    Message = "Staff have asked to join through the join link and are waiting for approval.",
                    Type = QMgr.Domain.Entities.Notification.NotificationType.SystemAlert,
                    ActionUrl = "/admin/users?tab=requests",
                    Channels = QMgr.Domain.Entities.Notification.NotificationChannel.InApp | QMgr.Domain.Entities.Notification.NotificationChannel.Email
                });
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not notify approvers of a join request"); }
    }
}
