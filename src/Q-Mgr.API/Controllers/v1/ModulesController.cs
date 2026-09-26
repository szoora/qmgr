using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using System.Text.Json;

namespace QMgr.Controllers.v1;

/// <summary>
/// The modular subscription system: browsing the 4-module catalog, seeing what an organization
/// already owns, and self-service add/remove via Mobile Money. Platform-admin grant/revoke lives
/// on <see cref="SuperAdminController"/> instead — a direct administrative action, not a purchase.
/// </summary>
[ApiController]
[Route("api/v1/modules")]
[Authorize]
public class ModulesController : ControllerBase
{
    private readonly IModuleAccessService _moduleAccessService;
    private readonly IPaymentLedger _ledger;
    private readonly IStripeService _stripeService;
    private readonly QMgrDbContext _dbContext;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly ILogger<ModulesController> _logger;
    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly IConfiguration _configuration;

    public ModulesController(
        IModuleAccessService moduleAccessService,
        IPaymentLedger ledger,
        IStripeService stripeService,
        QMgrDbContext dbContext,
        ITenantContextAccessor tenantContextAccessor,
        ILogger<ModulesController> logger,
        IPlatformSettingsService platformSettingsService,
        IConfiguration configuration)
    {
        _moduleAccessService = moduleAccessService;
        _ledger = ledger;
        _stripeService = stripeService;
        _dbContext = dbContext;
        _tenantContextAccessor = tenantContextAccessor;
        _logger = logger;
        _platformSettingsService = platformSettingsService;
        _configuration = configuration;
    }

    /// <summary>The public site, for a link a browser or a payment gateway will follow — the same
    /// resolution BillingController uses. Never Request.Host: this API is reached from the Web
    /// server over its loopback, so the request host is an address no customer can open.</summary>
    private Task<string> GetBaseUrlAsync() => _platformSettingsService.GetPublicWebBaseUrlAsync();

    private Guid OrganizationId => _tenantContextAccessor.TenantContext?.OrganizationId ?? Guid.Empty;

    /// <summary>The full 4-module catalog — anonymous so the registration wizard's module picker
    /// (no account exists yet) can render it, the one public read of the catalog.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetCatalog()
    {
        var catalog = await _moduleAccessService.GetCatalogAsync();
        return Ok(catalog);
    }

    /// <summary>This organization's purchase status for every module in the catalog.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> GetMine()
    {
        if (_tenantContextAccessor.TenantContext is not { IsResolved: true })
            return Unauthorized(new { message = "Unable to determine your organization context." });

        var status = await _moduleAccessService.GetOrganizationModuleStatusAsync(OrganizationId);

        // Every page's module gate reads this, so it stays open to everyone signed in — but the
        // price the school agreed, its billing cycle and its trial and activation dates are the
        // bursar's business, not a teacher's (2026-09-25). Without billing.view the answer says
        // which modules are on and nothing about what they cost.
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var mayReadBilling = Guid.TryParse(userId, out var uid)
            && (await QMgr.API.Application.Services.PostPermissionService.EffectiveCodesAsync(_dbContext, uid))
                .Contains(Permissions.BillingView);
        if (!mayReadBilling)
            status = status.Select(m => m with
            {
                ActivatedAt = null,
                TrialEndsAt = null,
                AgreedPriceUgx = null,
                BillingCycle = null
            }).ToList();

        return Ok(status);
    }


    private const int MaxReasonLength = 500;

    /// <summary>Strict parse — an unrecognised cycle is a 400, not a silent fall-through to Monthly
    /// (the previous behaviour, which would have billed the wrong amount for a typo).</summary>
    private static bool TryParseCycle(string? value, out BillingCycle cycle)
    {
        cycle = BillingCycle.Monthly;
        if (string.IsNullOrWhiteSpace(value)) return true;
        return Enum.TryParse(value, ignoreCase: true, out cycle) && Enum.IsDefined(cycle);
    }

    /// <summary>Checkout return URLs must point back at this deployment — anything else would let a
    /// crafted request turn the Stripe redirect into an open redirect.</summary>
    private bool IsSameOriginUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        if (url.StartsWith('/') && !url.StartsWith("//")) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, Request.Host.Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Buy a module, or pay for one on trial, through the sacc.ug gateway — Mobile Money or card
    /// (2026-09-19). The ledger writes the invoice and a pending payment BEFORE the gateway is asked,
    /// and the module is activated only when the gateway confirms the money (webhook, status read or
    /// reconciliation) — never by this request and never by a browser that keeps polling. Everything
    /// the payer typed is validated here and again in the ledger; nothing is written for a request
    /// that would be refused.
    /// </summary>
    [HttpPost("{moduleCode}/purchase")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> PurchaseModule(string moduleCode, [FromBody] ModulePurchaseRequest request, CancellationToken cancellationToken)
    {
        if (!ModuleCodes.All.Contains(moduleCode))
            return NotFound(new { message = $"Unknown module '{moduleCode}'." });
        if (request is null)
            return BadRequest(new { error = "INVALID_REQUEST", message = "The purchase details are missing." });

        var blocking = await _moduleAccessService.GetBlockingTrialModuleAsync(OrganizationId, moduleCode);
        if (blocking != null)
        {
            return BadRequest(new
            {
                error = "TRIAL_IN_PROGRESS",
                message = $"Complete payment for {blocking.Value.Name} before adding another module.",
                blockingModuleCode = blocking.Value.Code
            });
        }

        if (!TryParseCycle(request.BillingCycle, out var cycle))
            return BadRequest(new { error = "INVALID_BILLING_CYCLE", message = "Billing cycle must be Monthly or Annual." });

        try
        {
            var result = await _ledger.StartModulePurchaseAsync(OrganizationId, moduleCode, cycle, request, ClientIp(), cancellationToken);
            if (result.IsFinal && result.State != PaymentStates.Succeeded)
                return BadRequest(new { error = "PAYMENT_NOT_STARTED", message = result.Message, referenceId = result.ReferenceId });
            return Ok(result);
        }
        catch (PaymentRequestException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Code, message = ex.Message });
        }
    }

    /// <summary>Where a purchase stands. Read from the ledger; while it is still open the gateway is
    /// asked first, so a page that polls sees the verdict the moment the gateway has it. Another
    /// organization's payment answers 404, never 403.</summary>
    [HttpGet("purchase-status/{referenceId:guid}")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<IActionResult> CheckPurchaseStatus(Guid referenceId, CancellationToken cancellationToken)
    {
        var status = await _ledger.GetStatusAsync(OrganizationId, referenceId, refresh: true, cancellationToken);
        return status == null ? NotFound(new { message = "No such payment." }) : Ok(status);
    }

    private string? ClientIp() =>
        Request.Headers["X-Viewer-Ip"].FirstOrDefault()
        ?? Request.Headers["X-Real-IP"].FirstOrDefault()
        ?? HttpContext.Connection.RemoteIpAddress?.ToString();

    public record PurchaseModuleCardRequest(string BillingCycle = "Monthly", string? SuccessUrl = null, string? CancelUrl = null);

    /// <summary>Self-service add via card (Stripe) — the international/card-customer alternative
    /// to <see cref="PurchaseModule"/>'s Mobile Money path. First Stripe-paid module an org buys
    /// redirects to a Checkout Session (collects the card and creates the org's one shared
    /// multi-item subscription together); every module after that joins the existing subscription
    /// directly with no redirect, since Stripe already has a saved default payment method to
    /// charge (see IStripeService.AddSubscriptionItemAsync's doc comment).</summary>
    [HttpPost("{moduleCode}/purchase-card")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> PurchaseModuleCard(string moduleCode, [FromBody] PurchaseModuleCardRequest request)
    {
        if (!ModuleCodes.All.Contains(moduleCode))
            return NotFound(new { message = $"Unknown module '{moduleCode}'." });

        var plan = await _dbContext.SubscriptionPlans.FirstOrDefaultAsync(p => p.Code == moduleCode);
        if (plan == null)
            return NotFound(new { message = $"Module '{moduleCode}' is not in the catalog." });

        var blocking = await _moduleAccessService.GetBlockingTrialModuleAsync(OrganizationId, moduleCode);
        if (blocking != null)
        {
            return BadRequest(new
            {
                error = "TRIAL_IN_PROGRESS",
                message = $"Complete payment for {blocking.Value.Name} before adding another module.",
                blockingModuleCode = blocking.Value.Code
            });
        }

        if (!TryParseCycle(request.BillingCycle, out var cycle))
            return BadRequest(new { error = "INVALID_BILLING_CYCLE", message = "Billing cycle must be Monthly or Annual." });
        if (!IsSameOriginUrl(request.SuccessUrl) || !IsSameOriginUrl(request.CancelUrl))
            return BadRequest(new { error = "INVALID_RETURN_URL", message = "Return URLs must point back to this site." });

        var priceId = cycle == BillingCycle.Annual ? plan.StripePriceIdAnnual : plan.StripePriceIdMonthly;

        // Two independent prerequisites: platform-level credentials (Platform Settings / config)
        // and a per-module Stripe price id on the catalog row.
        if (string.IsNullOrEmpty(priceId) || !await _stripeService.IsConfiguredAsync())
        {
            return BadRequest(new
            {
                error = "STRIPE_NOT_CONFIGURED",
                message = $"Card payment isn't set up for {plan.Name} yet. Please use Mobile Money, or contact support."
            });
        }

        var org = await _dbContext.Organizations.FirstOrDefaultAsync(o => o.Id == OrganizationId);
        if (org == null)
            return Unauthorized(new { message = "Unable to determine your organization context." });

        var (existingCustomerId, existingSubscriptionId) = await _moduleAccessService.GetStripeModuleBillingAsync(OrganizationId);

        var customerId = existingCustomerId;
        if (string.IsNullOrEmpty(customerId))
        {
            customerId = await _stripeService.CreateCustomerAsync(org);
            await _moduleAccessService.SetStripeModuleBillingAsync(OrganizationId, customerId, null);
        }

        if (!string.IsNullOrEmpty(existingSubscriptionId))
        {
            // Org already has a shared multi-item Stripe subscription from an earlier card
            // purchase — join it directly, Stripe prorates and bills the saved payment method
            // automatically, no checkout redirect needed.
            var newItemId = await _stripeService.AddSubscriptionItemAsync(existingSubscriptionId, priceId);
            await _moduleAccessService.ActivateAsync(OrganizationId, moduleCode, cycle, newItemId);
            _logger.LogInformation("Added module {Module} to org {OrgId}'s existing Stripe subscription {SubscriptionId}", moduleCode, OrganizationId, existingSubscriptionId);
            return Ok(new { requiresCheckout = false, status = "Active", message = $"{plan.Name} activated and added to your card subscription." });
        }

        // Stripe sends the customer back to these, so they must be addresses the customer's browser
        // can open: the public site, never Request.Host (the loopback in production). A relative URL
        // a client passed is anchored to the same public base.
        var baseUrl = await GetBaseUrlAsync();
        string Anchor(string? url, bool succeeded) => string.IsNullOrWhiteSpace(url)
            ? BillingLinks.Absolute(baseUrl, BillingLinks.CheckoutReturn(succeeded))
            : url.StartsWith('/') ? BillingLinks.Absolute(baseUrl, url) : url;
        var successUrl = Anchor(request.SuccessUrl, succeeded: true);
        var cancelUrl = Anchor(request.CancelUrl, succeeded: false);
        var session = await _stripeService.CreateModuleCheckoutSessionAsync(OrganizationId, moduleCode, priceId, successUrl, cancelUrl, customerId, cycle.ToString());

        return Ok(new { requiresCheckout = true, checkoutUrl = session.Url });
    }

    /// <summary>Self-service remove — no refund logic here, just stops the module at end of the
    /// pattern already used for Subscription cancellation (immediate, matching CancelAtPeriodEnd's
    /// simplest case rather than the full proration BillingService.CancelSubscriptionAsync has).</summary>
    [HttpDelete("{moduleCode}")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> RemoveModule(string moduleCode, [FromQuery] string? reason)
    {
        if (reason?.Length > MaxReasonLength)
            return BadRequest(new { message = $"Reason must be at most {MaxReasonLength} characters." });

        if (!ModuleCodes.All.Contains(moduleCode))
            return NotFound(new { message = $"Unknown module '{moduleCode}'." });

        await _moduleAccessService.RevokeAsync(OrganizationId, moduleCode, reason);
        return NoContent();
    }
}
