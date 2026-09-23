using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Identity;
using QMgr.Filters;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Email;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Onboarding staff (duty rota plan §12): the join link and its settings, the join-request queue with
/// approval, the onboarding status page, re-issuing access, and the acceptable-use notice the
/// first-sign-in checklist asks for.
/// <para>
/// Organization-level, not branch-level: a join request belongs to the organization until an approver
/// places it in a branch. Every read and write filters by the caller's organization explicitly.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/staff-onboarding")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.StudentWelfare)]
public class StaffOnboardingController : StaffPerformanceControllerBase
{
    private readonly IStaffOnboardingPolicyService _policy;
    private readonly IBillingService _billing;
    private readonly INotificationService _notifications;
    private readonly IEmailSender _email;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly IStaffProfileChangeNotifier _profileChanges;
    private readonly INotificationHubService _hub;
    private readonly ILogger<StaffOnboardingController> _logger;
    private readonly QMgr.Infrastructure.Email.IEmailBrandService _emailBrands;
    private readonly IPasswordValidationService _passwords;

    /// <summary>A bulk re-issue is synchronous (each password is hashed on the request), so it is capped.</summary>
    private const int MaxReissue = 200;

    public StaffOnboardingController(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IStaffScopeService staffScope,
        IActivityLogger activity,
        IStaffOnboardingPolicyService policy,
        IBillingService billing,
        INotificationService notifications,
        IEmailSender email,
        IPlatformSettingsService platformSettings,
        IStaffProfileChangeNotifier profileChanges,
        INotificationHubService hub,
        QMgr.Infrastructure.Email.IEmailBrandService emailBrands,
        IPasswordValidationService passwords,
        ILogger<StaffOnboardingController> logger)
        : base(db, tenantAccessor, staffScope, activity)
    {
        _emailBrands = emailBrands;
        _passwords = passwords;
        _policy = policy;
        _billing = billing;
        _notifications = notifications;
        _email = email;
        _platformSettings = platformSettings;
        _profileChanges = profileChanges;
        _hub = hub;
        _logger = logger;
    }

    // =====================================================================================================
    // Settings and the join link
    // =====================================================================================================

    [HttpGet("settings")]
    [RequirePermission(Permissions.UsersApprove)]
    [ProducesResponseType(typeof(StaffOnboardingSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSettings()
    {
        var orgId = CurrentOrganizationId();
        if (orgId == null) return TenantNotResolved();
        return Ok(await BuildSettingsAsync(orgId.Value));
    }

    [HttpPut("settings")]
    [RequirePermission(Permissions.UsersApprove)]
    [ProducesResponseType(typeof(StaffOnboardingSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveSettings([FromBody] SaveStaffOnboardingSettingsRequest request)
    {
        var orgId = CurrentOrganizationId();
        if (orgId == null) return TenantNotResolved();

        var domains = (request.AllowedEmailDomains ?? new())
            .Select(d => d.Trim().TrimStart('@').ToLowerInvariant())
            .Where(d => d.Length > 0)
            .Distinct()
            .ToList();
        if (domains.Any(d => !d.Contains('.') || d.Contains(' ') || d.Contains('@') || d.Length > 120))
            return BadRequestProblem("An allowed domain does not look right", "Write domains like \"school.ac.ug\", one per entry, without the @.");
        if (request.RequestExpiryDays is < 1 or > 90)
            return BadRequestProblem("Requests must expire after 1 to 90 days");
        if (request.JoinLinkValidDays is < 0 or > 365)
            return BadRequestProblem("A join link can be valid for at most 365 days");

        if (request.DefaultBranchId is { } branchId && !await Db.Branches.IgnoreQueryFilters().AnyAsync(b => b.Id == branchId && b.OrganizationId == orgId && b.IsActive))
            return BadRequestProblem("That branch is not one of this organization's active branches");

        if (request.AcceptableUseNoticeId is { } noticeId
            && !await Db.StaffNotices.IgnoreQueryFilters().AnyAsync(n => n.Id == noticeId && n.OrganizationId == orgId && n.IsActive && n.RequiresAcknowledgement))
            return BadRequestProblem("That notice cannot be the acceptable-use notice", "Pick a notice of this organization that asks staff to confirm they have read it.");

        await _policy.MutateAsync(orgId.Value, policy =>
        {
            policy.JoinEnabled = request.JoinEnabled;
            policy.AllowedEmailDomains = domains;
            policy.DefaultBranchId = request.DefaultBranchId;
            policy.RequestExpiryDays = request.RequestExpiryDays;
            policy.AcceptableUseNoticeId = request.AcceptableUseNoticeId;
            if (request.JoinLinkValidDays is { } days)
                policy.JoinCodeExpiresAt = days == 0 ? null : DateTime.UtcNow.AddDays(days);
            return Task.FromResult(true);
        });

        await Activity.RecordAsync(ActivityActions.JoinSettingsSaved, "StaffOnboarding", orgId, null,
            $"Staff sign-up settings saved: join link {(request.JoinEnabled ? "on" : "off")}",
            new { request.JoinEnabled, Domains = domains.Count, request.RequestExpiryDays, HasNotice = request.AcceptableUseNoticeId != null },
            organizationId: orgId);

        return Ok(await BuildSettingsAsync(orgId.Value));
    }

    /// <summary>
    /// Issues a new join link, invalidating the old one at once (plan §12.4). The code is returned once and
    /// cannot be shown again: only its hash is stored, as for a document share's slug.
    /// </summary>
    [HttpPost("join-link/rotate")]
    [RequirePermission(Permissions.UsersApprove)]
    [ProducesResponseType(typeof(JoinLinkIssuedDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RotateJoinLink([FromQuery] int? validDays)
    {
        var orgId = CurrentOrganizationId();
        if (orgId == null) return TenantNotResolved();
        if (validDays is < 0 or > 365) return BadRequestProblem("A join link can be valid for at most 365 days");

        var code = StaffOnboardingPolicyService.GenerateCode();
        DateTime? expires = null;
        await _policy.MutateAsync(orgId.Value, policy =>
        {
            var firstLink = policy.JoinCodeHash == null;
            policy.JoinCodeHash = StaffOnboardingPolicyService.HashCode(code);
            policy.JoinCodeIssuedAt = DateTime.UtcNow;
            if (validDays is { } d) policy.JoinCodeExpiresAt = d == 0 ? null : DateTime.UtcNow.AddDays(d);
            else if (policy.JoinCodeExpiresAt is { } old && old <= DateTime.UtcNow) policy.JoinCodeExpiresAt = null;
            if (firstLink) policy.JoinEnabled = true;
            expires = policy.JoinCodeExpiresAt;
            return Task.FromResult(true);
        });

        await Activity.RecordAsync(ActivityActions.JoinLinkRotated, "StaffOnboarding", orgId, null,
            "Staff join link issued; any earlier link stopped working", organizationId: orgId);

        return Ok(new JoinLinkIssuedDto { Code = code, Path = $"/join/{code}", ExpiresAt = expires });
    }

    /// <summary>
    /// Creates the organization's acceptable-use and data-protection notice (plan §12.3) from a default text
    /// the school can edit on the Notices page, requiring acknowledgement, and points the checklist at it.
    /// </summary>
    [HttpPost("acceptable-use-notice")]
    [RequirePermission(Permissions.UsersApprove)]
    [ProducesResponseType(typeof(StaffOnboardingSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAcceptableUseNotice()
    {
        var orgId = CurrentOrganizationId();
        if (orgId == null) return TenantNotResolved();

        var orgName = await Db.Organizations.IgnoreQueryFilters().Where(o => o.Id == orgId).Select(o => o.BrandName ?? o.Name).FirstOrDefaultAsync() ?? "the school";
        var notice = new StaffNotice
        {
            OrganizationId = orgId.Value,
            BranchId = null,
            Title = "Acceptable use and data protection",
            BodyHtml = DefaultAcceptableUseHtml(orgName),
            PublishAt = DateTime.UtcNow,
            IsPinned = true,
            RequiresAcknowledgement = true,
            PublishedByUserId = CurrentUserId(),
            CreatedBy = CurrentUserId()
        };
        Db.StaffNotices.Add(notice);
        await Db.SaveChangesAsync();

        await _policy.MutateAsync(orgId.Value, policy =>
        {
            policy.AcceptableUseNoticeId = notice.Id;
            return Task.FromResult(true);
        });

        await Activity.RecordAsync(ActivityActions.NoticePublished, nameof(StaffNotice), notice.Id, null,
            $"Notice '{notice.Title}' published as the acceptable-use notice", organizationId: orgId);

        return Ok(await BuildSettingsAsync(orgId.Value));
    }

    private async Task<StaffOnboardingSettingsDto> BuildSettingsAsync(Guid orgId)
    {
        var policy = await _policy.GetAsync(orgId);
        string? noticeTitle = null;
        if (policy.AcceptableUseNoticeId is { } id)
            noticeTitle = await Db.StaffNotices.IgnoreQueryFilters().Where(n => n.Id == id).Select(n => n.Title).FirstOrDefaultAsync();

        var cutoff = DateTime.UtcNow.AddDays(-policy.RequestExpiryDays);
        return new StaffOnboardingSettingsDto
        {
            JoinEnabled = policy.JoinEnabled,
            HasJoinLink = policy.JoinCodeHash != null,
            JoinCodeIssuedAt = policy.JoinCodeIssuedAt,
            JoinCodeExpiresAt = policy.JoinCodeExpiresAt,
            AllowedEmailDomains = policy.AllowedEmailDomains,
            DefaultBranchId = policy.DefaultBranchId,
            RequestExpiryDays = policy.RequestExpiryDays,
            PurgeAfterDays = policy.PurgeAfterDays,
            AcceptableUseNoticeId = noticeTitle == null ? null : policy.AcceptableUseNoticeId,
            AcceptableUseNoticeTitle = noticeTitle,
            PendingRequests = await Db.Users.IgnoreQueryFilters().CountAsync(u => u.OrganizationId == orgId && !u.IsActive
                && u.PendingApprovalAt != null && u.JoinRequestRejectedAt == null && u.PendingApprovalAt > cutoff)
        };
    }

    private static string DefaultAcceptableUseHtml(string orgName)
    {
        var o = System.Net.WebUtility.HtmlEncode(orgName);
        return $"""
            <p>This account gives you access to information about students and colleagues at {o}. Please read and confirm the following.</p>
            <ul>
              <li><strong>Use it for your work only.</strong> Look up a student or a colleague only when your role needs it. Every record you open is logged.</li>
              <li><strong>Keep your password to yourself.</strong> Never share it, never sign in for someone else, and sign out on shared computers.</li>
              <li><strong>Children's information is special.</strong> Welfare, health and discipline records are shared only with those who need to know, and never outside the school's systems — not by message apps, not by personal email, not in photos of the screen.</li>
              <li><strong>Report a concern, don't investigate it.</strong> Log what you saw and tell the designated safeguarding lead.</li>
              <li><strong>Data protection.</strong> Personal data is handled under Uganda's Data Protection and Privacy Act, 2019: collected for a purpose, kept accurate, kept no longer than needed, and kept secure.</li>
              <li><strong>If something goes wrong</strong> — a lost phone, a shared password, a record sent to the wrong person — tell the administrator the same day.</li>
            </ul>
            <p>By confirming, you agree to use the system on these terms.</p>
            """;
    }

    // =====================================================================================================
    // Join requests
    // =====================================================================================================

    [HttpGet("requests")]
    [RequirePermission(Permissions.UsersApprove)]
    [ProducesResponseType(typeof(List<JoinRequestDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRequests([FromQuery] bool includeClosed = false)
    {
        var orgId = CurrentOrganizationId();
        if (orgId == null) return TenantNotResolved();

        var policy = await _policy.GetAsync(orgId.Value);
        var now = DateTime.UtcNow;

        var rows = await Db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.OrganizationId == orgId && !u.IsActive && u.PendingApprovalAt != null)
            .OrderBy(u => u.PendingApprovalAt)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.Phone, u.NormalizedPhone, u.EmployeeNumber, u.JobTitle, u.AssignedBranchId, BranchName = u.AssignedBranch != null ? u.AssignedBranch.Name : null, u.PendingApprovalAt, u.JoinRequestRejectedAt })
            .ToListAsync();

        var result = new List<JoinRequestDto>();
        foreach (var r in rows)
        {
            var expiresAt = r.PendingApprovalAt!.Value.AddDays(policy.RequestExpiryDays);
            var state = r.JoinRequestRejectedAt != null ? JoinRequestState.Rejected
                : expiresAt <= now ? JoinRequestState.Expired
                : JoinRequestState.Pending;
            if (!includeClosed && state != JoinRequestState.Pending) continue;

            result.Add(new JoinRequestDto
            {
                UserId = r.Id,
                FirstName = r.FirstName ?? "",
                LastName = r.LastName ?? "",
                FullName = PersonNames.Display(orgId.Value, r.FirstName, r.LastName, r.Email),
                SortName = PersonNames.SortKey(orgId.Value, r.FirstName, r.LastName),
                Email = r.Email ?? string.Empty,
                Phone = r.Phone,
                EmployeeNumber = r.EmployeeNumber,
                JobTitle = r.JobTitle,
                BranchId = r.AssignedBranchId,
                BranchName = r.BranchName,
                RequestedAt = r.PendingApprovalAt.Value,
                ExpiresAt = expiresAt,
                RejectedAt = r.JoinRequestRejectedAt,
                State = state,
                DuplicateSignals = await DuplicateSignalsAsync(orgId.Value, r.Id, r.FirstName, r.LastName, r.NormalizedPhone, r.EmployeeNumber)
            });
        }
        return Ok(result);
    }

    /// <summary>Signals that this applicant may already be on the system (plan §12.4), computed with the one normalisation home.</summary>
    private async Task<List<JoinDuplicateSignalDto>> DuplicateSignalsAsync(Guid orgId, Guid applicantId, string? firstName, string? lastName, string? normalizedPhone, string? employeeNumber)
    {
        var signals = new List<JoinDuplicateSignalDto>();
        var others = Db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.OrganizationId == orgId && u.Id != applicantId && u.PendingApprovalAt == null);

        if (!string.IsNullOrEmpty(normalizedPhone))
        {
            var match = await others.Where(u => u.NormalizedPhone == normalizedPhone).Select(u => new { u.Id, u.OrganizationId, u.FirstName, u.LastName }).FirstOrDefaultAsync();
            if (match != null) signals.Add(new("Phone", $"The same phone number is on {PersonNames.Display(match.OrganizationId, match.FirstName, match.LastName)}'s account.", match.Id));
        }
        if (!string.IsNullOrWhiteSpace(employeeNumber))
        {
            var emp = PersonCode.Normalize(employeeNumber)!;
            var empKey = PersonCode.Key(employeeNumber);
            var match = await others.Where(u => u.EmployeeNumber != null && u.EmployeeNumber.ToUpper() == empKey).Select(u => new { u.Id, u.OrganizationId, u.FirstName, u.LastName }).FirstOrDefaultAsync();
            if (match != null) signals.Add(new("EmployeeNumber", $"Employee number {emp} belongs to {PersonNames.Display(match.OrganizationId, match.FirstName, match.LastName)}.", match.Id));
        }
        if (!string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(lastName))
        {
            var key = RegistrationIdentity.NormalizeOrganizationName($"{firstName} {lastName}");
            var candidates = await others.Where(u => u.LastName != null && u.LastName.ToLower() == lastName.Trim().ToLower())
                .Select(u => new { u.Id, u.FirstName, u.LastName }).Take(20).ToListAsync();
            var match = candidates.FirstOrDefault(c => RegistrationIdentity.NormalizeOrganizationName(PersonName.Join(c.FirstName, c.LastName)) == key);
            if (match != null) signals.Add(new("Name", $"An account with the same name already exists.", match.Id));
        }
        return signals;
    }

    /// <summary>
    /// Approves a join request (plan §12.4): the role, branch, departments, line manager and class-teacher
    /// assignments in one step. Refused when the role is above the approver's own, when the request has
    /// expired or been decided, and past the seat cap (402). The activation is a conditional update under a
    /// lock on the organization's seats, so two approvals of one request create one account.
    /// </summary>
    [HttpPost("requests/{userId:guid}/approve")]
    [RequirePermission(Permissions.UsersApprove)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(Guid userId, [FromBody] ApproveJoinRequest request)
    {
        var orgId = CurrentOrganizationId();
        if (orgId == null) return TenantNotResolved();

        var applicant = await Db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.OrganizationId == orgId && !u.IsActive && u.PendingApprovalAt != null);
        if (applicant == null) return NotFoundProblem("Join request not found");
        if (applicant.JoinRequestRejectedAt != null) return ConflictProblem("This request was rejected", "A rejected request cannot be approved. The person can apply again after it is removed.");

        var policy = await _policy.GetAsync(orgId.Value);
        if (applicant.PendingApprovalAt!.Value.AddDays(policy.RequestExpiryDays) <= DateTime.UtcNow)
            return ConflictProblem("This request has expired", $"Requests expire after {policy.RequestExpiryDays} days unanswered. Ask the person to apply again.");

        // The email is verified before a request exists (the code gate on the join page); it cannot have
        // been taken meanwhile because Users.Email is unique, so a clash would already have failed.

        var role = await Db.Roles.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.RoleId && r.IsActive && (r.OrganizationId == null || r.OrganizationId == orgId));
        if (role == null) return BadRequestProblem("Choose a role", "The role was not found in this organization.");

        var refusal = await RoleAssignmentGuard.RefusalAsync(Db, CurrentUserId(), role);
        if (refusal != null)
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails { Title = "You cannot assign that role", Detail = refusal, Status = StatusCodes.Status403Forbidden });

        var branchId = request.BranchId ?? applicant.AssignedBranchId ?? policy.DefaultBranchId;
        if (branchId == null || !await Db.Branches.IgnoreQueryFilters().AnyAsync(b => b.Id == branchId && b.OrganizationId == orgId && b.IsActive))
            return BadRequestProblem("Choose the branch this person works at");

        var departments = (request.DepartmentIds ?? new()).Distinct().ToList();
        if (departments.Count > 0)
        {
            var known = await Db.Departments.IgnoreQueryFilters().CountAsync(d => departments.Contains(d.Id) && d.OrganizationId == orgId && d.IsActive);
            if (known != departments.Count) return BadRequestProblem("A department was not found in this organization");
        }
        if (request.LineManagerUserId is { } lm && !await Db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == lm && u.OrganizationId == orgId && u.IsActive))
            return BadRequestProblem("The line manager was not found in this organization");

        IActionResult? outcome = null;
        var strategy = Db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            outcome = null;
            Db.ChangeTracker.Clear();
            await using var tx = await Db.Database.BeginTransactionAsync();
            var lockKey = $"org-seats:{orgId}";
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");

            var limit = await _billing.CheckLimitAsync(orgId.Value, "users");
            if (limit.MaxAllowed >= 0 && limit.CurrentUsage >= limit.MaxAllowed)
            {
                outcome = StatusCode(StatusCodes.Status402PaymentRequired, new
                {
                    error = "LIMIT_EXCEEDED",
                    limitType = "users",
                    current = limit.CurrentUsage,
                    limit = limit.MaxAllowed,
                    message = $"Your plan allows {limit.MaxAllowed} users and {limit.CurrentUsage} are in use. Free a seat or add capacity before approving.",
                    upgradeUrl = BillingLinks.Modules
                });
                return;
            }

            var now = DateTime.UtcNow;
            var updated = await Db.Users.IgnoreQueryFilters()
                .Where(u => u.Id == userId && u.OrganizationId == orgId && !u.IsActive && u.PendingApprovalAt != null && u.JoinRequestRejectedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.IsActive, true)
                    .SetProperty(u => u.PendingApprovalAt, (DateTime?)null)
                    .SetProperty(u => u.RoleId, role.Id)
                    .SetProperty(u => u.AssignedBranchId, branchId)
                    .SetProperty(u => u.DepartmentIds, departments.Count > 0 ? departments.ToArray() : null)
                    .SetProperty(u => u.LineManagerUserId, request.LineManagerUserId)
                    .SetProperty(u => u.UpdatedAt, now)
                    .SetProperty(u => u.UpdatedBy, CurrentUserId()));
            if (updated == 0)
            {
                outcome = ConflictProblem("This request has already been decided", "Somebody else approved or rejected it a moment ago.");
                return;
            }
            await tx.CommitAsync();
        });
        if (outcome != null) return outcome;

        var messages = await TeachingAssignments.ApplyClassTeacherOfAsync(Db, orgId.Value, branchId.Value, userId,
            (request.ClassTeacherOf ?? new()).SelectMany(TeachingAssignments.SplitClasses), CurrentUserId());
        messages.AddRange(await TeachingAssignments.ApplyTeachesAsync(Db, orgId.Value, branchId.Value, userId, request.Teaches, CurrentUserId()));

        var name = PersonNames.Display(applicant.OrganizationId, applicant.FirstName, applicant.LastName);
        await Activity.RecordAsync(ActivityActions.JoinApproved, "User", userId, userId,
            $"Join request approved: {name} as {role.Name}", new { Role = role.Code, Branch = branchId }, branchId, orgId);

        // The assigned role takes effect through the one notifier (cache, live push, activity, staff notice).
        try { await _profileChanges.RoleChangedAsync(orgId.Value, userId, null, role.Id, CurrentUserId(), "join request approval"); }
        catch (Exception ex) { _logger.LogWarning(ex, "Profile-change notification failed after approving {UserId}", userId); }

        if (!string.IsNullOrWhiteSpace(applicant.Email))
            await SendApprovedAsync(orgId.Value, applicant.Email, applicant.FirstName, applicant.Phone);

        return Ok(new { approved = true, messages });
    }

    [HttpPost("requests/{userId:guid}/reject")]
    [RequirePermission(Permissions.UsersApprove)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reject(Guid userId, [FromBody] RejectJoinRequest? request)
    {
        var orgId = CurrentOrganizationId();
        if (orgId == null) return TenantNotResolved();

        var applicant = await Db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId && u.OrganizationId == orgId && !u.IsActive && u.PendingApprovalAt != null)
            .Select(u => new { u.Email, u.FirstName })
            .FirstOrDefaultAsync();
        if (applicant == null) return NotFoundProblem("Join request not found");

        var updated = await Db.Users.IgnoreQueryFilters()
            .Where(u => u.Id == userId && u.OrganizationId == orgId && !u.IsActive && u.PendingApprovalAt != null && u.JoinRequestRejectedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.JoinRequestRejectedAt, DateTime.UtcNow).SetProperty(u => u.UpdatedAt, DateTime.UtcNow));
        if (updated == 0) return ConflictProblem("This request has already been decided");

        // Without the applicant's details: the row is purged after the policy window, the event stays.
        await Activity.RecordAsync(ActivityActions.JoinRejected, "User", null, null,
            "A staff join request was rejected", new { HasReason = !string.IsNullOrWhiteSpace(request?.Reason) }, organizationId: orgId);

        try
        {
            var orgName = await OrganizationDisplayNameAsync(orgId.Value);
            var brand = await _emailBrands.ForOrganizationAsync(orgId.Value);
            var html = EmailTemplates.Layout("Your request was not approved", applicant.FirstName, new[]
            {
                $"Your request to join {EmailTemplates.B(orgName)} was not approved.",
                string.IsNullOrWhiteSpace(request?.Reason) ? "If you think this is a mistake, speak to the school's administrator." : $"The administrator wrote: {EmailTemplates.P(request!.Reason!.Trim())}"
            }, brand: brand);
            if (!string.IsNullOrWhiteSpace(applicant.Email))
            await _email.SendAsync(applicant.Email, $"Your request to join {orgName}", html);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not send the rejection email"); }

        return Ok(new { rejected = true });
    }

    private async Task SendApprovedAsync(Guid orgId, string email, string? firstName, string? phone)
    {
        try
        {
            var orgName = await OrganizationDisplayNameAsync(orgId);
            var baseUrl = await BaseUrlAsync();
            var brand = await _emailBrands.ForOrganizationAsync(orgId);
            var html = EmailTemplates.Layout("Your account is approved", firstName, new[]
            {
                $"Your request to join {EmailTemplates.B(orgName)} has been approved.",
                "Sign in with the email address and password you chose when you applied."
            }, "Sign in", EmailTemplates.Link(baseUrl, "/login"), brand: brand);
            await _email.SendAsync(email, $"Your {brand.Name} account is approved", html);

            if (!string.IsNullOrWhiteSpace(phone))
                await _notifications.SendSmsAsync(orgId, phone, $"Your {brand.Name} account is approved. Sign in at {baseUrl}/login");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not tell an approved applicant"); }
    }

    // =====================================================================================================
    // Onboarding status and re-issue
    // =====================================================================================================

    [HttpGet("status")]
    [RequirePermission(Permissions.UsersView)]
    [ProducesResponseType(typeof(OnboardingStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus([FromQuery] Guid? branchId)
    {
        var orgId = CurrentOrganizationId();
        if (orgId == null) return TenantNotResolved();
        if (branchId is { } b)
        {
            var branchError = await VerifyBranchOwnership(b);
            if (branchError != null) return branchError;
        }

        var policy = await _policy.GetAsync(orgId.Value);
        var now = DateTime.UtcNow;

        var usersQuery = Db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.OrganizationId == orgId && u.IsActive && u.Role.Code != RoleCodes.SuperAdmin && (branchId == null || u.AssignedBranchId == branchId));
        var users = await PersonNames.OrderByName(usersQuery, orgId.Value)
            .Select(u => new
            {
                u.Id, u.OrganizationId, u.FirstName, u.LastName, u.Username, u.Email, u.Phone, RoleName = u.Role.Name, u.CreatedAt, u.LastLogin,
                u.MustChangePassword, u.TemporaryPasswordExpiresAt, u.PasswordResetToken, u.PasswordResetTokenExpiry
            })
            .ToListAsync();

        var acknowledged = new HashSet<string>();
        Guid? noticeId = null;
        if (policy.AcceptableUseNoticeId is { } id)
        {
            var acks = await Db.StaffNotices.IgnoreQueryFilters().AsNoTracking().Where(n => n.Id == id && n.OrganizationId == orgId).Select(n => n.Acknowledgements).FirstOrDefaultAsync();
            if (acks != null)
            {
                noticeId = id;
                try { acknowledged = (JsonSerializer.Deserialize<Dictionary<string, string>>(acks) ?? new()).Keys.ToHashSet(); }
                catch (JsonException) { }
            }
        }

        var rows = users.Select(u => new OnboardingStatusRowDto
        {
            UserId = u.Id,
            FullName = PersonNames.Display(u.OrganizationId, u.FirstName, u.LastName, u.Username),
            SortName = PersonNames.SortKey(u.OrganizationId, u.FirstName, u.LastName),
            Username = u.Username,
            Email = u.Email ?? string.Empty,
            Phone = u.Phone,
            RoleName = u.RoleName,
            CreatedAt = u.CreatedAt,
            LastLogin = u.LastLogin,
            MustChangePassword = u.MustChangePassword,
            TemporaryPasswordExpiresAt = u.TemporaryPasswordExpiresAt,
            TemporaryPasswordExpired = u.MustChangePassword && (u.TemporaryPasswordExpiresAt == null || u.TemporaryPasswordExpiresAt <= now),
            InvitationPending = u.PasswordResetToken != null && u.PasswordResetTokenExpiry > now,
            AcknowledgedAcceptableUse = noticeId == null || acknowledged.Contains(u.Id.ToString())
        }).ToList();

        var cutoff = now.AddDays(-policy.RequestExpiryDays);
        return Ok(new OnboardingStatusDto
        {
            NotSignedIn = rows.Where(r => r.LastLogin == null && !r.TemporaryPasswordExpired).ToList(),
            TemporaryPasswordExpired = rows.Where(r => r.TemporaryPasswordExpired).ToList(),
            NotAcknowledged = noticeId == null ? new() : rows.Where(r => r.LastLogin != null && !r.AcknowledgedAcceptableUse).ToList(),
            AcceptableUseNoticeId = noticeId,
            PendingJoinRequests = await Db.Users.IgnoreQueryFilters().CountAsync(u => u.OrganizationId == orgId && !u.IsActive
                && u.PendingApprovalAt != null && u.JoinRequestRejectedAt == null && u.PendingApprovalAt > cutoff)
        });
    }

    /// <summary>
    /// Re-issues access (plan §12.5): a new temporary password (slips, or SMS) or a new invitation, the old
    /// one invalidated at once. Temporary passwords come back once and are never stored readably. Nobody can
    /// re-issue access for someone whose role ranks above their own — that would be taking over their account.
    /// </summary>
    [HttpPost("reissue")]
    [RequirePermission(Permissions.UsersEdit)]
    [ProducesResponseType(typeof(ReissueAccessResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reissue([FromBody] ReissueAccessRequest request)
    {
        var orgId = CurrentOrganizationId();
        if (orgId == null) return TenantNotResolved();

        var ids = (request.UserIds ?? new()).Distinct().ToList();
        if (ids.Count == 0) return BadRequestProblem("Choose at least one person");
        if (ids.Count > MaxReissue) return BadRequestProblem($"Re-issue at most {MaxReissue} people at a time");

        var users = await Db.Users.IgnoreQueryFilters().Include(u => u.Role)
            .Where(u => ids.Contains(u.Id) && u.OrganizationId == orgId && u.IsActive)
            .ToListAsync();

        var orgName = await OrganizationDisplayNameAsync(orgId.Value);
        var baseUrl = await BaseUrlAsync();

        // ONE PASSWORD FOR THE WHOLE BATCH, when the administrator typed one. Checked BEFORE
        // anything is written, so a refused password changes nobody — a half-applied re-issue would
        // leave some people on a password the administrator holds and others on one nobody does.
        //
        // The check is StaffImportController's, deliberately: the batch password on an import and
        // the batch password on a re-issue are the same decision, and two copies of "is this good
        // enough to hand two hundred people" is how one of them ends up laxer than the other.
        string? chosen = null;
        if (!string.IsNullOrWhiteSpace(request.TemporaryPassword)
            && request.Mode is StaffImportDeliveryMode.Slips or StaffImportDeliveryMode.Sms)
        {
            var usernames = users.Select(u => u.Username).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            var check = await _passwords.ValidatePasswordAsync(request.TemporaryPassword, null, null, orgName, usernames);
            if (!check.IsValid)
                return BadRequestProblem("That password is refused",
                    check.ErrorMessage + " Leave it empty to generate a different password for each person, which is safer.");

            foreach (var u in users)
            {
                var local = (u.Email ?? string.Empty).Split('@')[0];
                if ((!string.IsNullOrWhiteSpace(u.Username) && request.TemporaryPassword.Contains(u.Username, StringComparison.OrdinalIgnoreCase))
                    || (local.Length >= 3 && request.TemporaryPassword.Contains(local, StringComparison.OrdinalIgnoreCase)))
                    return BadRequestProblem("That password is refused",
                        "It contains the username or email address of somebody in this group.");
            }
            chosen = request.TemporaryPassword;
        }
        var slips = new List<TemporaryPasswordSlipDto>();
        var messages = new List<string>();
        var reissued = 0;

        foreach (var user in users)
        {
            var refusal = user.Id == CurrentUserId() ? null : await RoleAssignmentGuard.RefusalAsync(Db, CurrentUserId(), user.Role);
            if (refusal != null)
            {
                messages.Add($"{user.FullName}: not re-issued — their role ranks above yours.");
                continue;
            }

            user.RefreshToken = null;
            user.RefreshTokenExpiry = null;
            user.UpdatedAt = DateTime.UtcNow;
            user.UpdatedBy = CurrentUserId();

            if (request.Mode == StaffImportDeliveryMode.Invitation)
            {
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
                user.MustChangePassword = false;
                user.TemporaryPasswordExpiresAt = null;
                user.PasswordResetToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
                user.PasswordResetTokenExpiry = DateTime.UtcNow.AddDays(7);
                await Db.SaveChangesAsync();

                var link = $"{baseUrl}/reset-password?email={Uri.EscapeDataString(user.Email ?? string.Empty)}&token={Uri.EscapeDataString(user.PasswordResetToken)}";
                var brand = await _emailBrands.ForOrganizationAsync(orgId);
                var html = EmailTemplates.Layout($"Your access to {orgName}", user.FirstName, new[]
                {
                    $"A new invitation to {EmailTemplates.B(orgName)}'s workspace, signed in as {EmailTemplates.B(user.Username)}.",
                    "Choose your password with the button below. The link is valid for 7 days, and any earlier link or temporary password no longer works."
                }, "Set my password", link, showLinkFallback: true, brand: brand);
                var sent = await _email.SendAsync(user.Email, $"Your {brand.Name} invitation", html);
                if (!sent) messages.Add($"{user.FullName}: the invitation email could not be sent.");

                await Activity.RecordAsync(ActivityActions.InvitationIssued, "User", user.Id, user.Id,
                    $"New invitation issued to {user.FullName}", organizationId: orgId, branchId: user.AssignedBranchId);
            }
            else
            {
                var temporary = chosen ?? TemporaryPasswords.Generate();
                var expires = DateTime.UtcNow.Add(TemporaryPasswords.Lifetime);
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(temporary);
                user.MustChangePassword = true;
                user.TemporaryPasswordExpiresAt = expires;
                user.PasswordResetToken = null;
                user.PasswordResetTokenExpiry = null;
                user.FailedLoginAttempts = 0;
                user.LockoutEnd = null;
                await Db.SaveChangesAsync();

                if (request.Mode == StaffImportDeliveryMode.Sms)
                {
                    if (string.IsNullOrWhiteSpace(user.Phone)) messages.Add($"{user.FullName}: no phone number, so no SMS — print their slip instead.");
                    else
                    {
                        var sms = await _notifications.SendSmsAsync(orgId.Value, user.Phone, TemporaryPasswordSms(user.Username, temporary, baseUrl));
                        if (sms.Outcome != ChannelSendOutcome.Sent) messages.Add($"{user.FullName}: the SMS was not sent ({sms.Reason ?? sms.Outcome.ToString()}) — print their slip instead.");
                    }
                }

                slips.Add(new TemporaryPasswordSlipDto
                {
                    UserId = user.Id,
                    FullName = user.FullName,
                    Username = user.Username,
                    Email = user.Email ?? string.Empty,
                    TemporaryPassword = temporary,
                    ExpiresAt = expires
                });

                await Activity.RecordAsync(ActivityActions.TemporaryPasswordIssued, "User", user.Id, user.Id,
                    $"Temporary password issued to {user.FullName}{(request.Mode == StaffImportDeliveryMode.Sms ? " by SMS" : "")}",
                    organizationId: orgId, branchId: user.AssignedBranchId);
            }

            // Their permissions and session state just changed underneath any open tab.
            try { await _hub.NotifyPermissionsChangedAsync(user.Id); } catch { /* the next request picks it up */ }
            reissued++;
        }

        if (users.Count < ids.Count)
            messages.Add($"{ids.Count - users.Count} of the people chosen are not active members of this organization.");

        return Ok(new ReissueAccessResultDto { Reissued = reissued, Slips = slips, Messages = messages });
    }

    /// <summary>The text of a temporary password by SMS (plan §12.2): username, password, where, and when it expires — no school or student data.</summary>
    internal static string TemporaryPasswordSms(string username, string temporaryPassword, string baseUrl)
        => $"Q-Mgr sign-in: username {username}, temporary password {temporaryPassword}. Sign in at {baseUrl}/login within 72 hours and choose your own password.";

    private async Task<string> OrganizationDisplayNameAsync(Guid orgId)
        => await Db.Organizations.IgnoreQueryFilters().Where(o => o.Id == orgId).Select(o => o.BrandName ?? o.Name).FirstOrDefaultAsync() ?? "your organization";

    private Task<string> BaseUrlAsync() => _platformSettings.GetPublicWebBaseUrlAsync();
}
