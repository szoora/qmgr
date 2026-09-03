using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.API.Authorization;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
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
    private readonly IMobileMoneyService _mobileMoneyService;
    private readonly IStripeService _stripeService;
    private readonly QMgrDbContext _dbContext;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IDistributedCache _cache;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ModulesController> _logger;
    private const string PendingPurchasePrefix = "module-purchase:";

    public ModulesController(
        IModuleAccessService moduleAccessService,
        IMobileMoneyService mobileMoneyService,
        IStripeService stripeService,
        QMgrDbContext dbContext,
        ITenantContextAccessor tenantContextAccessor,
        IDistributedCache cache,
        IWebHostEnvironment environment,
        ILogger<ModulesController> logger)
    {
        _moduleAccessService = moduleAccessService;
        _mobileMoneyService = mobileMoneyService;
        _stripeService = stripeService;
        _dbContext = dbContext;
        _tenantContextAccessor = tenantContextAccessor;
        _cache = cache;
        _environment = environment;
        _logger = logger;
    }

    private Guid OrganizationId => _tenantContextAccessor.TenantContext?.OrganizationId ?? Guid.Empty;

    /// <summary>The full 4-module catalog — anonymous so the registration wizard's module picker
    /// (no account exists yet) can render it, same pattern as the existing GET billing/plans.</summary>
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
        return Ok(status);
    }

    public record PurchaseModuleRequest(string PhoneNumber, string BillingCycle = "Monthly");

    /// <summary>Self-service add — collects payment via Mobile Money and activates on success.
    /// In Development with no gateway configured, simulates an instant success so the flow can be
    /// demonstrated end to end without real MTN/Airtel sandbox credentials.</summary>
    [HttpPost("{moduleCode}/purchase")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> PurchaseModule(string moduleCode, [FromBody] PurchaseModuleRequest request)
    {
        if (!ModuleCodes.All.Contains(moduleCode))
            return NotFound(new { message = $"Unknown module '{moduleCode}'." });

        var catalog = await _moduleAccessService.GetCatalogAsync();
        var module = catalog.FirstOrDefault(m => m.Code == moduleCode);
        if (module == null)
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

        var cycle = request.BillingCycle.Equals("Annual", StringComparison.OrdinalIgnoreCase) ? BillingCycle.Annual : BillingCycle.Monthly;
        var amount = cycle == BillingCycle.Annual ? module.AnnualPriceUgx : module.MonthlyPriceUgx;

        if (_environment.IsDevelopment())
        {
            // Dev-only simulation: MobileMoneyService.CollectPaymentAsync returns a clean
            // "DISABLED" failure whenever MobileMoney:Enabled isn't set (true in every local dev
            // environment, since no gateway credentials exist here) — real behavior, not a bug.
            // Rather than let that dead-end the whole registration→purchase flow during local
            // testing, probe it first and fall back to an instant simulated success so the flow
            // is actually demonstrable. Never runs outside Development.
            var probe = await _mobileMoneyService.CollectPaymentAsync(OrganizationId, request.PhoneNumber, amount, "UGX", $"probe-{moduleCode}");
            if (!probe.Success && probe.ErrorCode == "DISABLED")
            {
                await _moduleAccessService.ActivateAsync(OrganizationId, moduleCode, cycle);
                _logger.LogInformation("[DEV SIMULATION] Activated module {Module} for org {OrgId} — no Mobile Money gateway configured", moduleCode, OrganizationId);
                return Ok(new { simulated = true, status = "Active", message = "Mobile Money isn't configured in this environment — activated immediately for testing." });
            }
        }

        var narrative = $"Q-Mgr {module.Name} module ({cycle})";
        var result = await _mobileMoneyService.CollectPaymentAsync(OrganizationId, request.PhoneNumber, amount, "UGX", narrative);

        if (!result.Success || result.TransactionId == null)
        {
            return BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage ?? "Payment could not be initiated.", customerMessage = result.CustomerMessage });
        }

        // Track what this transaction is for so the status-check endpoint below knows what to
        // activate once the customer confirms on their phone — ephemeral (30 min), not a
        // permanent record; a real Payment/Invoice row is out of scope for this pass.
        var pending = JsonSerializer.Serialize(new { OrganizationId, ModuleCode = moduleCode, BillingCycle = cycle.ToString() });
        await _cache.SetStringAsync($"{PendingPurchasePrefix}{result.TransactionId}", pending,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) });

        return Ok(new { transactionId = result.TransactionId, status = result.Status.ToString(), message = "Payment initiated. Please check your phone to confirm." });
    }

    /// <summary>Poll after PurchaseModule — activates the pending module once the gateway confirms success.</summary>
    [HttpGet("purchase-status/{transactionId}")]
    public async Task<IActionResult> CheckPurchaseStatus(string transactionId)
    {
        var result = await _mobileMoneyService.CheckPaymentStatusAsync(transactionId);

        if (result.Status == MobileMoneyPaymentStatus.Succeeded)
        {
            var pendingJson = await _cache.GetStringAsync($"{PendingPurchasePrefix}{transactionId}");
            if (pendingJson != null)
            {
                var pending = JsonSerializer.Deserialize<PendingPurchase>(pendingJson);
                if (pending != null)
                {
                    var cycle = pending.BillingCycle == "Annual" ? BillingCycle.Annual : BillingCycle.Monthly;
                    await _moduleAccessService.ActivateAsync(pending.OrganizationId, pending.ModuleCode, cycle);
                    await _cache.RemoveAsync($"{PendingPurchasePrefix}{transactionId}");
                }
            }
        }

        return Ok(new { status = result.Status.ToString(), errorMessage = result.ErrorMessage });
    }

    private record PendingPurchase(Guid OrganizationId, string ModuleCode, string BillingCycle);

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

        var cycle = request.BillingCycle.Equals("Annual", StringComparison.OrdinalIgnoreCase) ? BillingCycle.Annual : BillingCycle.Monthly;
        var priceId = cycle == BillingCycle.Annual ? plan.StripePriceIdAnnual : plan.StripePriceIdMonthly;

        if (string.IsNullOrEmpty(priceId))
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

        var successUrl = request.SuccessUrl ?? $"{Request.Scheme}://{Request.Host}/billing/modules?checkout=success";
        var cancelUrl = request.CancelUrl ?? $"{Request.Scheme}://{Request.Host}/billing/modules?checkout=cancelled";
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
        if (!ModuleCodes.All.Contains(moduleCode))
            return NotFound(new { message = $"Unknown module '{moduleCode}'." });

        await _moduleAccessService.RevokeAsync(OrganizationId, moduleCode, reason);
        return NoContent();
    }
}
