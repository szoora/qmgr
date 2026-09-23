using QMgr.Infrastructure.Services;
using QMgr.Infrastructure.Services.Billing;
using QMgr.Application.DTOs;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Super admin controller for platform-wide management
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize]
[RequirePermission(Permissions.PlatformAdmin)]
[Produces("application/json")]
public class SuperAdminController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IBillingService _billingService;
    private readonly IUsageTrackingService _usageTrackingService;
    private readonly IModuleAccessService _moduleAccessService;
    private readonly ICustomDomainService _customDomains;
    private readonly IFeatureFlagService _featureFlags;
    private readonly ITenantLifecycleService _lifecycle;
    private readonly ITenantPurgeService _purge;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SuperAdminController> _logger;

    public SuperAdminController(
        QMgrDbContext dbContext,
        ITenantProvisioningService provisioningService,
        IBillingService billingService,
        IUsageTrackingService usageTrackingService,
        IModuleAccessService moduleAccessService,
        ICustomDomainService customDomains,
        IFeatureFlagService featureFlags,
        ITenantLifecycleService lifecycle,
        ITenantPurgeService purge,
        IHostEnvironment environment,
        IConfiguration configuration,
        ILogger<SuperAdminController> logger)
    {
        _dbContext = dbContext;
        _provisioningService = provisioningService;
        _billingService = billingService;
        _usageTrackingService = usageTrackingService;
        _moduleAccessService = moduleAccessService;
        _customDomains = customDomains;
        _featureFlags = featureFlags;
        _lifecycle = lifecycle;
        _purge = purge;
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
    }

    #region Tenant lifecycle and purge

    /// <summary>Where this tenant is in its life, when its next automatic move is due, and its history.</summary>
    [HttpGet("tenants/{id:guid}/lifecycle")]
    [ProducesResponseType(typeof(TenantLifecycleStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLifecycle(Guid id, CancellationToken ct)
        => Ok(await _lifecycle.GetStatusAsync(id, ct));

    /// <summary>
    /// Moves a tenant to another state by hand. Every move is written down, and the clock on the
    /// new state starts now. <c>Deleted</c> is refused: a tenant reaches it only by being purged.
    /// </summary>
    [HttpPost("tenants/{id:guid}/lifecycle")]
    [ProducesResponseType(typeof(TenantLifecycleStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetLifecycle(Guid id, [FromBody] TransitionTenantRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<TenantStatus>(request?.Status, ignoreCase: true, out var target))
            return BadRequest(new { error = "UNKNOWN_STATUS", message = $"'{request?.Status}' is not a tenant status." });

        var actor = User.Identity?.Name ?? "platform administrator";
        var result = await _lifecycle.TransitionAsync(id, target, actor, CurrentUserId(), request?.Reason, ct);
        return result.Ok
            ? Ok(result.Status)
            : BadRequest(new ProblemDetails { Title = result.Error, Status = StatusCodes.Status400BadRequest });
    }

    /// <summary>
    /// What this tenant is made of, before anything is deleted: rows per table, files, background
    /// jobs, cache keys, external references. READ-ONLY, and the thing a purge is checked against.
    /// </summary>
    [HttpGet("tenants/{id:guid}/residue")]
    [ProducesResponseType(typeof(TenantResidueDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetResidue(Guid id, CancellationToken ct)
        => Ok(await _purge.InventoryAsync(id, ct));

    /// <summary>
    /// Empties the tenant out of every table, deletes its files, removes its background jobs, and
    /// verifies that nothing is left. IRREVERSIBLE.
    ///
    /// Two gates, both deliberate. The tenant must already be in <c>PendingDeletion</c> — a purge
    /// is the end of a lifecycle, never a shortcut through it — and the caller must type the
    /// organisation's name back, because a misclick must not reach this.
    /// </summary>
    [HttpPost("tenants/{id:guid}/purge")]
    [ProducesResponseType(typeof(TenantPurgeResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PurgeTenant(Guid id, [FromBody] PurgeTenantRequest request, CancellationToken ct)
    {
        var org = await _dbContext.Organizations.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (org == null) return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        if (org.Status != TenantStatus.PendingDeletion)
            return Conflict(new
            {
                error = "NOT_SCHEDULED",
                message = $"'{org.Name}' is {org.Status}. Schedule it for deletion first — a purge is the end of the lifecycle, not a shortcut through it."
            });

        if (!string.Equals(request?.ConfirmName?.Trim(), org.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "NAME_MISMATCH", message = "Type the organisation's name exactly to confirm. This cannot be undone." });

        var actor = User.Identity?.Name ?? "platform administrator";
        _logger.LogWarning("Super admin {Actor} is purging tenant {TenantId} ({Name})", actor, id, org.Name);

        var result = await _purge.PurgeAsync(id, actor, CurrentUserId(), request?.Reason, ct);
        return result.Ok ? Ok(result) : StatusCode(StatusCodes.Status500InternalServerError, result);
    }

    /// <summary>
    /// Gives this caller's address its sign-up budget back. DEVELOPMENT ONLY — 404 anywhere else.
    /// The purge suite has to create a tenant in order to destroy one, and three sign-ups an hour
    /// is right in production and unworkable for a suite run repeatedly against a dev box.
    /// </summary>
    [HttpPost("registration-budget/reset")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetRegistrationBudget([FromServices] IRegistrationGuardService guard, CancellationToken ct)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        await guard.ResetAttemptBudgetAsync(HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return Ok(new { reset = true });
    }

    /// <summary>The certificates: what was purged, when, by whom, and whether the completeness check passed.</summary>
    [HttpGet("purge-certificates")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPurgeCertificates([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var rows = await _dbContext.TenantPurgeCertificates.AsNoTracking()
            .OrderByDescending(c => c.PurgedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);
        return Ok(rows);
    }

    #endregion

    #region Custom domain

    // Platform-only, on the existing SuperAdmin gate, and that IS the decision (plan decision 2):
    // a tenant does not self-serve a domain, because the certificate step reaches the web server's
    // own configuration. The tenant sees the live domain read-only on their Branding page with one
    // line telling them who to ask.

    [HttpGet("tenants/{id:guid}/custom-domain")]
    [ProducesResponseType(typeof(CustomDomainStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCustomDomain(Guid id, CancellationToken ct)
        => Ok(await _customDomains.GetStatusAsync(id, ct));

    /// <summary>Claims a domain and returns the exact TXT record to create. Writes nothing live.</summary>
    [HttpPut("tenants/{id:guid}/custom-domain")]
    [ProducesResponseType(typeof(CustomDomainStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestCustomDomain(Guid id, [FromBody] RequestCustomDomainRequest request, CancellationToken ct)
    {
        var result = await _customDomains.RequestAsync(id, request?.Domain ?? string.Empty, ct);
        return result.Ok
            ? Ok(result.Status)
            : BadRequest(new ProblemDetails { Title = result.Error, Status = StatusCodes.Status400BadRequest });
    }

    /// <summary>
    /// Runs whatever step is outstanding. A 400 here is NOT a dead end — it names the step that
    /// failed and the status it returns says where the claim now stands, so the same button is
    /// pressed again once the DNS record appears.
    /// </summary>
    [HttpPost("tenants/{id:guid}/custom-domain/verify")]
    [ProducesResponseType(typeof(CustomDomainStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyCustomDomain(Guid id, CancellationToken ct)
    {
        var result = await _customDomains.VerifyAsync(id, ct);
        return result.Ok
            ? Ok(result.Status)
            : BadRequest(new ProblemDetails { Title = result.Error, Status = StatusCodes.Status400BadRequest, Extensions = { ["status"] = result.Status } });
    }

    /// <summary>
    /// Publishes a TXT record into the DNS STUB, so the verification flow can be exercised end to
    /// end without owning a zone. 404 anywhere but Development with <c>Dns:Stub</c> set — the same
    /// shape as the Development-only weekly-analysis trigger, and for the same reason: a test hook
    /// that can be reached on a real server is not a test hook.
    /// </summary>
    [HttpPost("tenants/{id:guid}/custom-domain/stub-dns")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PublishStubDns(Guid id, [FromQuery] string? value, CancellationToken ct)
    {
        if (!_environment.IsDevelopment() || !_configuration.GetValue("Dns:Stub", false))
            return NotFound();

        var status = await _customDomains.GetStatusAsync(id, ct);
        if (string.IsNullOrEmpty(status.VerificationRecordName))
            return BadRequest(new { message = "That tenant has no domain waiting to be verified." });

        // Defaults to the token the service actually minted, so the happy path needs no argument;
        // pass a value to exercise the "a record exists but does not match" refusal.
        QMgr.Infrastructure.Services.Domains.StubDnsTxtLookup.Publish(
            status.VerificationRecordName,
            value ?? status.VerificationRecordValue ?? string.Empty);

        return Ok(new { published = status.VerificationRecordName });
    }

    [HttpDelete("tenants/{id:guid}/custom-domain")]
    [ProducesResponseType(typeof(CustomDomainStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReleaseCustomDomain(Guid id, CancellationToken ct)
    {
        var result = await _customDomains.ReleaseAsync(id, ct);
        return result.Ok
            ? Ok(result.Status)
            : BadRequest(new ProblemDetails { Title = result.Error, Status = StatusCodes.Status400BadRequest });
    }

    #endregion

    #region Feature overrides

    /// <summary>
    /// A negotiated entitlement, recorded on the tenant's own row. The same reasoning as
    /// <c>AgreedPriceUgx</c>: what a customer actually gets is written down, never inferred from a
    /// catalogue row. It can only ever turn a flag ON — taking away something a module grants is a
    /// refund question, not a switch, so the resolver ORs this in after the module grants.
    /// </summary>
    [HttpPut("tenants/{id:guid}/feature-overrides/{code}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetFeatureOverride(Guid id, string code, [FromBody] SetFeatureOverrideRequest request, CancellationToken ct)
    {
        if (!FeatureOverrideCodes.Contains(code))
            return BadRequest(new { error = "UNKNOWN_FEATURE", message = $"'{code}' is not a feature that can be overridden." });

        // Through the one writer of Organization.Settings, re-read under its lock (2026-09-23) — this
        // merged over a copy read with no lock, so a tenant saving its own settings at the same moment
        // could lose the override or lose their save.
        var found = await OrganizationSettingsLock.MutateAsync(_dbContext, id, org =>
        {
            var root = string.IsNullOrEmpty(org.Settings)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(org.Settings) ?? new();

            var overrides = root.TryGetValue(FeatureFlagService.OverridesKey, out var existing) && existing.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, bool>>(existing.GetRawText()) ?? new()
                : new Dictionary<string, bool>();

            // OFF is a REMOVAL, not a stored false: a stored false reads as "this tenant is refused
            // this feature", which is not a thing this system has. Absent means "whatever they bought".
            if (request?.Enabled == true) overrides[code] = true; else overrides.Remove(code);

            org.Settings = OrganizationSettingsLock.WithKey(org.Settings, FeatureFlagService.OverridesKey, overrides.Count > 0 ? overrides : null);
            return true;
        }, ct);
        if (!found) return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        // The entitlement cache is five minutes. Without this the administrator who just granted it
        // watches nothing happen and grants it again.
        await _featureFlags.InvalidateCacheAsync(id);

        _logger.LogInformation("Super admin {Action} the '{Code}' override for tenant {TenantId}",
            request?.Enabled == true ? "granted" : "removed", code, id);

        return Ok(new { code, enabled = request?.Enabled == true });
    }

    /// <summary>
    /// The features a platform administrator may hand out by hand. Deliberately a short list, not
    /// every constant on <c>FeatureCodes</c>: an override is for a negotiated deal, and anything
    /// that is simply part of a module should be sold as that module.
    /// </summary>
    private static readonly HashSet<string> FeatureOverrideCodes = new(StringComparer.Ordinal)
    {
        FeatureCodes.WhiteLabel,
        FeatureCodes.RemoveAttribution
    };

    #endregion

    #region Tenant Management

    /// <summary>
    /// Get all tenants with pagination and filtering
    /// </summary>
    [HttpGet("tenants")]
    [ProducesResponseType(typeof(PagedResult<TenantSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTenants(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] TenantStatus? status = null,
        [FromQuery] string? search = null)
    {
        var query = _dbContext.Organizations.AsQueryable();

        if (status.HasValue)
            query = query.Where(o => o.Status == status.Value);


        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(o =>
                o.Name.Contains(search) ||
                o.Slug.Contains(search) ||
                (o.ContactEmail != null && o.ContactEmail.Contains(search)));

        var totalCount = await query.CountAsync();

        var tenants = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new TenantSummary
            {
                Id = o.Id,
                Name = o.Name,
                Slug = o.Slug,
                Status = o.Status,
                ContactEmail = o.ContactEmail,
                CreatedAt = o.CreatedAt,
                TrialEndsAt = o.TrialEndsAt,
                OnboardingCompleted = o.OnboardingCompleted
            })
            .ToListAsync();

        return Ok(new PagedResult<TenantSummary>
        {
            Items = tenants,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        });
    }

    /// <summary>
    /// Get detailed tenant information
    /// </summary>
    /// <summary>The overrides currently set on a tenant, so the dialog can show them as switches.</summary>
    private static Dictionary<string, bool> ReadFeatureOverrides(string? settingsJson)
    {
        if (string.IsNullOrEmpty(settingsJson)) return new();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsJson);
            if (root != null && root.TryGetValue(FeatureFlagService.OverridesKey, out var element) && element.ValueKind == JsonValueKind.Object)
                return JsonSerializer.Deserialize<Dictionary<string, bool>>(element.GetRawText()) ?? new();
        }
        catch (JsonException)
        {
            // A malformed blob reads as "no overrides" — the same call the resolver makes.
        }
        return new();
    }

    [HttpGet("tenants/{id:guid}")]
    [ProducesResponseType(typeof(TenantDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTenant(Guid id)
    {
        var org = await _dbContext.Organizations
            .Include(o => o.Subscription)
            .Include(o => o.Branches)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        var usage = await _usageTrackingService.GetCurrentUsageAsync(id);
        var limits = await _billingService.GetEffectiveLimitsAsync(id);
        var userCount = await _dbContext.Users.CountAsync(u => u.OrganizationId == id);

        return Ok(new TenantDetails
        {
            Id = org.Id,
            Name = org.Name,
            Slug = org.Slug,
            BrandName = org.BrandName,
            Status = org.Status,
            ContactEmail = org.ContactEmail,
            ContactPhone = org.ContactPhone,
            BillingEmail = org.BillingEmail,
            BillingPhone = org.BillingPhone,
            PreferredCurrency = org.PreferredCurrency,
            CustomDomain = org.CustomDomain,
            CustomDomainStatus = await _customDomains.GetStatusAsync(id),
            Lifecycle = await _lifecycle.GetStatusAsync(id),
            FeatureOverrides = ReadFeatureOverrides(org.Settings),
            StripeCustomerId = org.StripeCustomerId,
            CreatedAt = org.CreatedAt,
            VerifiedAt = org.VerifiedAt,
            TrialEndsAt = org.TrialEndsAt,
            OnboardingCompleted = org.OnboardingCompleted,
            OnboardingStep = org.OnboardingStep,
            IndustryType = org.IndustryType,
            BranchCount = org.Branches.Count,
            UserCount = userCount,
            Subscription = org.Subscription != null ? new SubscriptionSummary
            {
                Id = org.Subscription.Id,
                PlanName = string.Join(", ", await _dbContext.OrganizationModules.Where(om => om.OrganizationId == org.Id && om.Status == OrganizationModuleStatus.Active).Select(om => om.Module!.Name).ToListAsync()),
                Status = org.Subscription.Status,
                BillingCycle = org.Subscription.BillingCycle,
                CurrentPeriodEnd = org.Subscription.CurrentPeriodEnd
            } : null,
            Usage = new UsageSummary
            {
                TokensCreated = usage.TokensCreated,
                ApiCalls = usage.ApiCalls,
                ActiveUsers = usage.ActiveUsers,
                ActiveBranches = usage.ActiveBranches,
                MaxTokens = limits.MaxTokensPerMonth,
                MaxApiCalls = limits.MaxApiCallsPerMonth,
                MaxUsers = limits.MaxUsersPerBranch,
                MaxBranches = limits.MaxBranches,
                StorageUsedMb = usage.StorageUsedBytes / 1024 / 1024,
                MaxStorageMb = limits.MaxStorageMb,
                StorageQuotaOverrideMb = org.Subscription?.MaxStorageOverride
            }
        });
    }

    /// <summary>
    /// Suspend a tenant
    /// </summary>
    [HttpPost("tenants/{id:guid}/suspend")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SuspendTenant(Guid id, [FromBody] SuspendRequest request)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        await _provisioningService.SuspendTenantAsync(id, request.Reason);

        _logger.LogWarning(
            "Super admin suspended tenant {TenantId}. Reason: {Reason}",
            id, request.Reason);

        return Ok(new { message = "Tenant suspended successfully" });
    }

    /// <summary>
    /// Reactivate a suspended tenant
    /// </summary>
    [HttpPost("tenants/{id:guid}/reactivate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReactivateTenant(Guid id)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        await _provisioningService.ReactivateTenantAsync(id);

        _logger.LogInformation("Super admin reactivated tenant {TenantId}", id);

        return Ok(new { message = "Tenant reactivated successfully" });
    }

    /// <summary>
    /// Every module's purchase status for one tenant — backs the "Manage Modules" panel that
    /// replaced the old single-select "Change Tier" modal.
    /// </summary>
    [HttpGet("tenants/{id:guid}/modules")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTenantModules(Guid id)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        var status = await _moduleAccessService.GetOrganizationModuleStatusAsync(id);
        return Ok(status);
    }

    /// <summary>
    /// Direct platform-admin grant — no payment collected, immediately Active. The literal
    /// "add/remove modules per customer request" lever.
    /// </summary>
    [HttpPut("tenants/{id:guid}/modules/{moduleCode}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GrantTenantModule(Guid id, string moduleCode, [FromBody] GrantModuleRequest? request)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        if (!ModuleCodes.All.Contains(moduleCode))
            return NotFound(new { error = "MODULE_NOT_FOUND", message = $"Unknown module '{moduleCode}'." });

        await _moduleAccessService.GrantAsync(id, moduleCode, CurrentUserId() ?? Guid.Empty, request?.Note);

        _logger.LogInformation("Super admin granted module {ModuleCode} to tenant {TenantId}", moduleCode, id);

        return Ok(new { message = "Module granted successfully" });
    }

    /// <summary>Direct platform-admin revoke — the other half of the manage-modules lever.</summary>
    [HttpDelete("tenants/{id:guid}/modules/{moduleCode}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeTenantModule(Guid id, string moduleCode, [FromQuery] string? note)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        if (!ModuleCodes.All.Contains(moduleCode))
            return NotFound(new { error = "MODULE_NOT_FOUND", message = $"Unknown module '{moduleCode}'." });

        await _moduleAccessService.RevokeAsync(id, moduleCode, note);

        _logger.LogInformation("Super admin revoked module {ModuleCode} from tenant {TenantId}", moduleCode, id);

        return Ok(new { message = "Module revoked successfully" });
    }

    public record GrantModuleRequest(string? Note);

    /// <summary>Re-sends the verification email for a tenant still Pending — for when the
    /// original send failed (e.g. platform SMTP wasn't configured yet) or the 24-hour token
    /// expired before the customer got to it.</summary>
    [HttpPost("tenants/{id:guid}/resend-verification")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResendTenantVerification(Guid id)
    {
        var sent = await _provisioningService.ResendVerificationEmailAsync(id);
        if (!sent)
        {
            return BadRequest(new
            {
                error = "RESEND_FAILED",
                message = "Could not resend — the organization doesn't exist, isn't pending verification, or the platform's email settings aren't configured."
            });
        }

        _logger.LogInformation("Super admin resent verification email for tenant {TenantId}", id);
        return Ok(new { message = "Verification email resent." });
    }

    /// <summary>Platform-admin override: verifies a tenant directly, no token, no email round
    /// trip. For when the verification email can never arrive (SMTP genuinely unconfigured) and
    /// the admin has otherwise confirmed the account is legitimate — the same real effect as the
    /// customer clicking a working verification link (branch/service-type seeding included), not
    /// a shortcut that leaves the account in a half-set-up state.</summary>
    [HttpPost("tenants/{id:guid}/verify")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyTenant(Guid id)
    {
        var verified = await _provisioningService.AdminVerifyAsync(id);
        if (!verified)
        {
            return BadRequest(new
            {
                error = "VERIFY_FAILED",
                message = "Could not verify — the organization doesn't exist or isn't pending verification."
            });
        }

        _logger.LogInformation("Tenant {TenantId} verified directly by super admin {AdminId}", id, CurrentUserId());
        return Ok(new { message = "Organization verified." });
    }


    private Guid? CurrentUserId()
    {
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : null;
    }

    /// <summary>
    /// Set (or clear) a tenant's per-tenant storage quota override. This is the platform-admin
    /// lever for the storage-conservation direction — content should mostly live on external
    /// platforms (YouTube, Vimeo, Google Drive, TikTok) that this app just links to rather than
    /// hosts, with local uploads capped per plan (SubscriptionPlan.MaxStorageMb, 100MB by
    /// default) and enforced at upload time in ContentController. This override exists for the
    /// occasional tenant who genuinely needs more room without changing their whole plan.
    /// </summary>
    [HttpPatch("tenants/{id:guid}/storage-quota")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStorageQuota(Guid id, [FromBody] UpdateStorageQuotaRequest request)
    {
        var subscription = await _dbContext.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == id);
        if (subscription == null)
            return NotFound(new { error = "SUBSCRIPTION_NOT_FOUND", message = "This tenant has no subscription to set a storage override on." });

        if (request.MaxStorageMb is < 0)
            return BadRequest(new { error = "INVALID_QUOTA", message = "Storage quota cannot be negative." });

        var previousOverride = subscription.MaxStorageOverride;
        subscription.MaxStorageOverride = request.MaxStorageMb;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Super admin changed tenant {TenantId} storage quota override from {OldMb}MB to {NewMb}MB",
            id, previousOverride?.ToString() ?? "plan default", request.MaxStorageMb?.ToString() ?? "plan default");

        var limits = await _billingService.GetEffectiveLimitsAsync(id);
        return Ok(new { message = "Storage quota updated successfully", effectiveMaxStorageMb = limits.MaxStorageMb });
    }

    /// <summary>
    /// Extend trial for a tenant
    /// </summary>
    [HttpPost("tenants/{id:guid}/extend-trial")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExtendTrial(Guid id, [FromBody] ExtendTrialRequest request)
    {
        var org = await _dbContext.Organizations.FindAsync(id);
        if (org == null)
            return NotFound(new { error = "TENANT_NOT_FOUND", message = "Tenant not found" });

        var newTrialEnd = (org.TrialEndsAt ?? DateTime.UtcNow).AddDays(request.Days);
        org.TrialEndsAt = newTrialEnd;

        if (org.Status == TenantStatus.Suspended && !org.SubscriptionId.HasValue)
            org.Status = TenantStatus.Trialing;

        _dbContext.Organizations.Update(org);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Super admin extended trial for tenant {TenantId} by {Days} days until {NewTrialEnd}",
            id, request.Days, newTrialEnd);

        return Ok(new { message = "Trial extended successfully", newTrialEndsAt = newTrialEnd });
    }

    #endregion

    #region Platform Statistics

    /// <summary>
    /// Get platform-wide statistics
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(PlatformStats), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlatformStats()
    {
        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonth = thisMonth.AddMonths(-1);

        // Calculate MRR (Monthly Recurring Revenue) first
        var activeSubscriptions = await _dbContext.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active)
            .ToListAsync();

        // Summed from the modules organizations hold, at the price each of them agreed to.
        var mrr = await _billingService.GetPlatformMonthlyRecurringRevenueUsdAsync();

        var stats = new PlatformStats
        {
            TotalOrganizations = await _dbContext.Organizations.CountAsync(),
            ActiveOrganizations = await _dbContext.Organizations
                .CountAsync(o => o.Status == TenantStatus.Active || o.Status == TenantStatus.Trialing),
            TrialingOrganizations = await _dbContext.Organizations
                .CountAsync(o => o.Status == TenantStatus.Trialing),
            SuspendedOrganizations = await _dbContext.Organizations
                .CountAsync(o => o.Status == TenantStatus.Suspended),

            TotalUsers = await _dbContext.Users.CountAsync(),
            TotalBranches = await _dbContext.Branches.CountAsync(),

            OrganizationsByModule = await _dbContext.OrganizationModules
                .Where(om => om.Status == OrganizationModuleStatus.Active)
                .GroupBy(om => om.Module!.Name)
                .Select(g => new ModuleCount { Module = g.Key, Count = g.Select(x => x.OrganizationId).Distinct().Count() })
                .ToListAsync(),

            NewOrganizationsThisMonth = await _dbContext.Organizations
                .CountAsync(o => o.CreatedAt >= thisMonth),

            NewOrganizationsLastMonth = await _dbContext.Organizations
                .CountAsync(o => o.CreatedAt >= lastMonth && o.CreatedAt < thisMonth),

            TotalTokensThisMonth = await _dbContext.UsageRecords
                .Where(u => u.Year == now.Year && u.Month == now.Month)
                .SumAsync(u => u.TokensCreated),

            TotalApiCallsThisMonth = await _dbContext.UsageRecords
                .Where(u => u.Year == now.Year && u.Month == now.Month)
                .SumAsync(u => u.ApiCalls),

            MonthlyRecurringRevenue = mrr
        };

        return Ok(stats);
    }

    /// <summary>
    /// Get recent activity across the platform
    /// </summary>
    [HttpGet("activity")]
    [ProducesResponseType(typeof(List<ActivityItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecentActivity([FromQuery] int limit = 50)
    {
        var recentOrgs = await _dbContext.Organizations
            .OrderByDescending(o => o.CreatedAt)
            .Take(limit)
            .Select(o => new ActivityItem
            {
                Type = "organization_created",
                Description = $"New organization: {o.Name}",
                EntityId = o.Id,
                EntityName = o.Name,
                Timestamp = o.CreatedAt
            })
            .ToListAsync();

        var recentSubscriptions = await _dbContext.Subscriptions
            .Include(s => s.Organization)
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .Select(s => new ActivityItem
            {
                Type = "subscription_created",
                Description = $"{s.Organization!.Name} opened a billing account",
                EntityId = s.Id,
                EntityName = s.Organization.Name,
                Timestamp = s.CreatedAt
            })
            .ToListAsync();

        var allActivity = recentOrgs
            .Concat(recentSubscriptions)
            .OrderByDescending(a => a.Timestamp)
            .Take(limit)
            .ToList();

        return Ok(allActivity);
    }

    #endregion

    #region Subscription Plans Management

    /// <summary>
    /// Get all subscription plans
    /// </summary>
    [HttpGet("plans")]
    [ProducesResponseType(typeof(List<PlanDetails>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlans()
    {
        // Every row in subscription_plans is a module now. This endpoint used to exclude module
        // codes because the table also held the four pricing tiers and mixing both read as one
        // undifferentiated catalog; with the tiers retired on 2026-09-04 that filter matched
        // everything and the endpoint returned an empty list. It shows the module catalog, which
        // is what the platform dashboard is asking it for.
        var plans = await _dbContext.SubscriptionPlans
            .OrderBy(p => p.SortOrder)
            .Select(p => new PlanDetails
            {
                Id = p.Id,
                Name = p.Name,
                Code = p.Code,
                Description = p.Description,
                MonthlyPriceUsd = p.MonthlyPriceUsd,
                AnnualPriceUsd = p.AnnualPriceUsd,
                MonthlyPriceUgx = p.MonthlyPriceUgx,
                AnnualPriceUgx = p.AnnualPriceUgx,
                MaxBranches = p.MaxBranches,
                MaxUsersPerBranch = p.MaxUsersPerBranch,
                MaxTokensPerMonth = p.MaxTokensPerMonth,
                MaxApiCallsPerMonth = p.MaxApiCallsPerMonth,
                ShowAds = p.ShowAds,
                IsPublic = p.IsPublic,
                ActiveSubscriptions = _dbContext.OrganizationModules.Count(om => om.ModuleId == p.Id && om.Status == OrganizationModuleStatus.Active)
            })
            .ToListAsync();

        return Ok(plans);
    }

    #endregion
}

#region Request/Response Models

public record PagedResult<T>
{
    public List<T> Items { get; init; } = new();
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }
}

public record TenantSummary
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public TenantStatus Status { get; init; }
    public string? ContactEmail { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? TrialEndsAt { get; init; }
    public bool OnboardingCompleted { get; init; }
}

public record TenantDetails : TenantSummary
{
    public string? BrandName { get; init; }
    public string? ContactPhone { get; init; }
    public string? BillingEmail { get; init; }
    public string? BillingPhone { get; init; }
    public string PreferredCurrency { get; init; } = "UGX";
    public string? CustomDomain { get; init; }

    /// <summary>Where the tenant's own domain has got to, including the DNS record still to be created.</summary>
    public CustomDomainStatusDto? CustomDomainStatus { get; init; }

    /// <summary>Where the tenant is in its life, and when the next automatic move is due.</summary>
    public TenantLifecycleStatusDto? Lifecycle { get; init; }

    /// <summary>Feature codes granted by hand on this tenant's row, outside any module they hold.</summary>
    public Dictionary<string, bool> FeatureOverrides { get; init; } = new();

    public string? StripeCustomerId { get; init; }
    public DateTime? VerifiedAt { get; init; }
    public int OnboardingStep { get; init; }
    public IndustryType IndustryType { get; init; }
    public int BranchCount { get; init; }
    public int UserCount { get; init; }
    public SubscriptionSummary? Subscription { get; init; }
    public UsageSummary? Usage { get; init; }
}

public record SubscriptionSummary
{
    public Guid Id { get; init; }
    public string PlanName { get; init; } = string.Empty;
    public SubscriptionStatus Status { get; init; }
    public BillingCycle BillingCycle { get; init; }
    public DateTime CurrentPeriodEnd { get; init; }
}

public record UsageSummary
{
    public int TokensCreated { get; init; }
    public int ApiCalls { get; init; }
    public int ActiveUsers { get; init; }
    public int ActiveBranches { get; init; }
    public int MaxTokens { get; init; }
    public int MaxApiCalls { get; init; }
    public int MaxUsers { get; init; }
    public int MaxBranches { get; init; }
    public long StorageUsedMb { get; init; }
    public int MaxStorageMb { get; init; }
    public int? StorageQuotaOverrideMb { get; init; }
}

public record SuspendRequest
{
    public string Reason { get; init; } = string.Empty;
}

public record SetFeatureOverrideRequest
{
    public bool Enabled { get; init; }
}

public record UpdateStorageQuotaRequest
{
    /// <summary>Per-tenant storage quota override in MB. Null clears the override and falls
    /// back to the subscription plan's own default (SubscriptionPlan.MaxStorageMb).</summary>
    public int? MaxStorageMb { get; init; }
}


public record ExtendTrialRequest
{
    public int Days { get; init; } = 14;
}

public record PlatformStats
{
    public int TotalOrganizations { get; init; }
    public int ActiveOrganizations { get; init; }
    public int TrialingOrganizations { get; init; }
    public int SuspendedOrganizations { get; init; }
    public int TotalUsers { get; init; }
    public int TotalBranches { get; init; }
    public List<ModuleCount> OrganizationsByModule { get; init; } = new();
    public int NewOrganizationsThisMonth { get; init; }
    public int NewOrganizationsLastMonth { get; init; }
    public int TotalTokensThisMonth { get; init; }
    public int TotalApiCallsThisMonth { get; init; }
    public decimal MonthlyRecurringRevenue { get; init; }
}

public record ModuleCount
{
    public string Module { get; init; } = string.Empty;
    public int Count { get; init; }
}

public record ActivityItem
{
    public string Type { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public Guid EntityId { get; init; }
    public string EntityName { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
}

public record PlanDetails
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string? Description { get; init; }
    public decimal MonthlyPriceUsd { get; init; }
    public decimal AnnualPriceUsd { get; init; }
    public decimal MonthlyPriceUgx { get; init; }
    public decimal AnnualPriceUgx { get; init; }
    public int MaxBranches { get; init; }
    public int MaxUsersPerBranch { get; init; }
    public int MaxTokensPerMonth { get; init; }
    public int MaxApiCallsPerMonth { get; init; }
    public bool ShowAds { get; init; }
    public bool IsPublic { get; init; }
    public int ActiveSubscriptions { get; init; }
}

#endregion
