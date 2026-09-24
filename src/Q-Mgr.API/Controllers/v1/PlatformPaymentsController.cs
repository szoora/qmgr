using QMgr.Application.Branding;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Payments;
using QMgr.Infrastructure.Services.Billing;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The platform's side of payment collection (2026-09-19): the sacc.ug gateway's settings, a check that
/// the key works, the webhook registration, a real test prompt, and the reconciliation view. Everything
/// the Platform Settings page used to hold for "Mobile Money" lives here now — that editor knew three
/// fields and would have wiped the webhook secret on every save.
/// </summary>
[ApiController]
[Route("api/v1/platform/payments")]
[Produces("application/json")]
[Authorize]
public partial class PlatformPaymentsController : ControllerBase
{
    private const string Category = "MobileMoney";
    private const string Mask = "••••••••";
    private const string WebhookPath = SaccGatewayDefaults.WebhookPath;
    private const int MaxRangeDays = 366;

    private readonly IPlatformSettingsService _settings;
    private readonly ISaccGateway _gateway;
    private readonly IPaymentLedger _ledger;
    private readonly IStripeService _stripe;
    private readonly IDistributedCache _cache;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<PlatformPaymentsController> _logger;

    public PlatformPaymentsController(
        IPlatformSettingsService settings,
        ISaccGateway gateway,
        IPaymentLedger ledger,
        IStripeService stripe,
        IDistributedCache cache,
        IHostEnvironment environment,
        ILogger<PlatformPaymentsController> logger)
    {
        _settings = settings;
        _gateway = gateway;
        _ledger = ledger;
        _stripe = stripe;
        _cache = cache;
        _environment = environment;
        _logger = logger;
    }

    // ================================================================== settings

    [HttpGet("gateway")]
    [RequirePermission(Permissions.PlatformSettingsView)]
    public async Task<IActionResult> GetGateway() => Ok(await BuildDtoAsync());

    /// <summary>
    /// Save the gateway settings. Validated hard, because a wrong address or key does not fail here — it
    /// fails later, on a customer's payment. The key comes back as the mask when it is set; sending the
    /// mask back keeps the stored key.
    /// </summary>
    [HttpPut("gateway")]
    [RequirePermission(Permissions.PlatformSettingsEdit)]
    public async Task<IActionResult> UpdateGateway([FromBody] UpdateGatewaySettingsRequest request)
    {
        if (request is null) return BadRequest(Problem("INVALID_REQUEST", "The settings are missing."));

        var stored = await _settings.GetSettingsAsync<MobileMoneySettings>(Category) ?? new MobileMoneySettings();
        var errors = new Dictionary<string, string>();

        // Left empty, the address is the platform default — the SACC gateway.
        var baseUrl = (string.IsNullOrWhiteSpace(request.BaseUrl) ? SaccGatewayDefaults.BaseUrl : request.BaseUrl).Trim().TrimEnd('/');
        var baseProblem = BaseUrlProblem(baseUrl, required: request.Enabled);
        if (baseProblem != null) errors["baseUrl"] = baseProblem;

        var keyInput = (request.ApiKey ?? string.Empty).Trim();
        var keepStoredKey = keyInput == Mask;
        var apiKey = keepStoredKey ? stored.ApiKey : keyInput;
        if (keepStoredKey && string.IsNullOrEmpty(stored.ApiKey))
            errors["apiKey"] = "Enter the API key — none is stored yet.";
        else if (!keepStoredKey && keyInput.Length > 0 && !ApiKeyShape().IsMatch(keyInput))
            errors["apiKey"] = "An API key is 16 to 256 characters with no spaces. Paste it exactly as CRMPro showed it.";
        else if (request.Enabled && string.IsNullOrEmpty(apiKey))
            errors["apiKey"] = "An API key is required to switch the gateway on.";

        if (request.CardsEnabled && !request.Enabled)
            errors["cardsEnabled"] = "Cards go through the same gateway — switch the gateway on first.";

        if (errors.Count > 0)
            return BadRequest(new { error = "VALIDATION_FAILED", message = "Some settings need attention.", errors });

        // A registration belongs to the gateway it was made with. Point at another gateway and the old
        // secret would refuse every callback from the new one, so it is dropped and must be re-registered.
        var sameGateway = string.Equals(HostOf(stored.CrmApiUrl), HostOf(baseUrl), StringComparison.OrdinalIgnoreCase);
        var updated = new MobileMoneySettings
        {
            CrmApiUrl = baseUrl,
            ApiKey = apiKey,
            Enabled = request.Enabled,
            CardsEnabled = request.CardsEnabled,
            WebhookSecret = sameGateway ? stored.WebhookSecret : string.Empty,
            WebhookId = sameGateway ? stored.WebhookId : string.Empty,
        };

        if (!await _settings.UpdateSettingsAsync(Category, updated))
            return StatusCode(500, Problem("SAVE_FAILED", "The settings could not be saved."));

        _logger.LogInformation("Payment gateway settings saved: enabled {Enabled}, cards {Cards}, gateway {Host}",
            updated.Enabled, updated.CardsEnabled, HostOf(baseUrl));
        return Ok(await BuildDtoAsync());
    }

    // ================================================================== check and register

    /// <summary>Ask the gateway whether the stored address and key work.</summary>
    [HttpPost("gateway/check")]
    [RequirePermission(Permissions.PlatformSettingsEdit)]
    public async Task<IActionResult> Check(CancellationToken cancellationToken)
    {
        var result = await _gateway.CheckAsync(cancellationToken);
        return Ok(new GatewayCheckDto(result.Reachable, result.Authorised, result.StatusCode, result.Message));
    }

    /// <summary>Register this install's callback address with the gateway and keep the signing secret
    /// it returns. Replaces an earlier registration rather than adding a second one.</summary>
    [HttpPost("gateway/webhook")]
    [RequirePermission(Permissions.PlatformSettingsEdit)]
    public async Task<IActionResult> RegisterWebhook([FromBody] RegisterWebhookRequest? request, CancellationToken cancellationToken)
    {
        var callbackUrl = string.IsNullOrWhiteSpace(request?.CallbackUrl) ? await CallbackUrlAsync() : request!.CallbackUrl!.Trim();
        var callbackProblem = CallbackProblem(callbackUrl);
        if (callbackProblem != null)
            return BadRequest(new { error = "VALIDATION_FAILED", message = callbackProblem, errors = new Dictionary<string, string> { ["callbackUrl"] = callbackProblem } });

        var stored = await _settings.GetSettingsAsync<MobileMoneySettings>(Category) ?? new MobileMoneySettings();
        var result = await _gateway.RegisterWebhookAsync(callbackUrl,
            string.IsNullOrWhiteSpace(stored.WebhookId) ? null : stored.WebhookId, cancellationToken);

        if (!result.Registered || string.IsNullOrWhiteSpace(result.Secret))
            return Ok(new WebhookRegistrationDto(false, null, callbackUrl,
                result.Registered ? "The gateway registered the webhook but returned no signing secret." : result.Message));

        stored.WebhookId = result.WebhookId ?? string.Empty;
        stored.WebhookSecret = result.Secret!;
        if (!await _settings.UpdateSettingsAsync(Category, stored))
            return StatusCode(500, Problem("SAVE_FAILED",
                "The gateway registered the webhook, but its signing secret could not be saved. Register it again."));

        _logger.LogInformation("Payment gateway webhook registered as {WebhookId} for {Callback}", result.WebhookId, callbackUrl);
        return Ok(new WebhookRegistrationDto(true, result.WebhookId, callbackUrl, "Registered. The gateway will call this address when a payment moves."));
    }

    // ================================================================== test prompt

    /// <summary>
    /// Send a real UGX 500 prompt to a phone. It proves every link at once: the key collects, the phone
    /// rings, and — through <see cref="GetTestPrompt"/> — whether the signed webhook reached this install.
    /// It writes no ledger row: it belongs to no customer.
    /// </summary>
    [HttpPost("gateway/test-prompt")]
    [RequirePermission(Permissions.PlatformSettingsEdit)]
    public async Task<IActionResult> SendTestPrompt([FromBody] TestPromptRequest request, CancellationToken cancellationToken)
    {
        var phoneProblem = UgandaPhone.Problem(request?.PhoneNumber);
        if (phoneProblem != null)
            return BadRequest(new { error = "INVALID_PHONE", message = phoneProblem, errors = new Dictionary<string, string> { ["phoneNumber"] = phoneProblem } });

        var config = await _gateway.GetConfigAsync();
        if (!config.MobileMoneyAvailable)
            return BadRequest(Problem("NOT_CONFIGURED", "Switch the gateway on and save its address and API key before sending a test prompt."));

        var phone = UgandaPhone.Normalize(request!.PhoneNumber);
        var referenceId = Guid.NewGuid();
        var result = await _gateway.CollectAsync(new GatewayCollectRequest(
            referenceId, SaccTestPrompts.Amount, ProductBrand.Name + " gateway test", PaymentMethodCodes.MobileMoney, phone,
            null, null, null, $"TEST-{referenceId.ToString("N")[..8].ToUpperInvariant()}", ProductBrand.Name + " test"), cancellationToken);

        var message = result.Accepted
            ? $"Prompt sent to {UgandaPhone.Display(phone)}. Approve it on the phone to finish the test."
            : result.AdminMessage;
        await SaccTestPrompts.SetAsync(_cache, referenceId,
            new SaccTestPrompts.Entry(phone, result.State, result.IsFinal, false, message), cancellationToken);

        _logger.LogInformation("Gateway test prompt {Reference} to {Phone}: {State}", referenceId, UgandaPhone.Display(phone), result.State);
        return Ok(new TestPromptStatusDto(referenceId, result.State, result.IsFinal, false, message));
    }

    /// <summary>Where a test prompt stands: the gateway's status, and whether its webhook arrived.</summary>
    [HttpGet("gateway/test-prompt/{referenceId:guid}")]
    [RequirePermission(Permissions.PlatformSettingsView)]
    public async Task<IActionResult> GetTestPrompt(Guid referenceId, CancellationToken cancellationToken)
    {
        var entry = await SaccTestPrompts.GetAsync(_cache, referenceId, cancellationToken);
        if (entry == null) return NotFound(new { message = "No such test prompt, or it is more than a day old." });

        if (!entry.IsFinal)
        {
            var status = await _gateway.GetStatusAsync(referenceId, cancellationToken);
            if (status.Known)
            {
                // Re-read: the webhook may have marked it while the gateway was being asked.
                var latest = await SaccTestPrompts.GetAsync(_cache, referenceId, cancellationToken) ?? entry;
                entry = latest with
                {
                    State = status.State,
                    IsFinal = status.IsFinal,
                    Message = status.State switch
                    {
                        PaymentStates.Succeeded => "Paid. The key collects and the gateway confirmed the money.",
                        PaymentStates.Pending => "Waiting for the phone to approve the prompt.",
                        _ => status.Message
                    }
                };
                await SaccTestPrompts.SetAsync(_cache, referenceId, entry, cancellationToken);
            }
        }

        return Ok(new TestPromptStatusDto(referenceId, entry.State, entry.IsFinal, entry.WebhookReceived, entry.Message));
    }

    // ================================================================== reconciliation

    /// <summary>What was sent, confirmed, failed, abandoned and held for review per day.</summary>
    [HttpGet("reconciliation")]
    [RequirePermission(Permissions.PlatformSettingsView)]
    public async Task<IActionResult> GetReconciliation([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? end.AddDays(-29);
        if (start > end) (start, end) = (end, start);
        if (end.DayNumber - start.DayNumber >= MaxRangeDays)
            return BadRequest(Problem("RANGE_TOO_LONG", "Choose a period of a year or less."));

        return Ok(await _ledger.GetReconciliationAsync(start, end, cancellationToken));
    }

    /// <summary>
    /// Decide a payment the gateway held for review — the amount received differed from the amount due,
    /// or the gateway's operator is holding it. "received" settles it as paid; "not-received" as failed.
    /// Final, and the note (ten characters or more) is kept on the payment with who decided it.
    /// </summary>
    [HttpPost("{referenceId:guid}/resolve")]
    [RequirePermission(Permissions.PlatformSettingsEdit)]
    public async Task<IActionResult> ResolveReview(Guid referenceId, [FromBody] ResolveReviewRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>();
        var outcome = (request?.Outcome ?? string.Empty).Trim().ToLowerInvariant();
        if (outcome is not (ReviewOutcomes.Received or ReviewOutcomes.NotReceived))
            errors["outcome"] = "Choose whether the money was received.";
        var note = (request?.Note ?? string.Empty).Trim();
        if (note.Length < 10)
            errors["note"] = "Say what you checked, in ten characters or more — it is kept on the payment.";
        else if (note.Length > 500)
            errors["note"] = "Keep the note to 500 characters.";
        if (errors.Count > 0)
            return BadRequest(new { error = "VALIDATION_FAILED", message = "Some fields need attention.", errors });

        var by = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                 ?? User.FindFirst("email")?.Value
                 ?? User.Identity?.Name
                 ?? "a platform administrator";
        try
        {
            var state = await _ledger.ResolveReviewAsync(referenceId, outcome == ReviewOutcomes.Received, note, by, cancellationToken);
            if (state == null) return NotFound(new { message = "No such payment." });
            return Ok(new { state, message = state == PaymentStates.Succeeded ? "Marked received. The invoice is paid." : "Marked not received." });
        }
        catch (PaymentRequestException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Code, message = ex.Message });
        }
    }

    // ================================================================== helpers

    private async Task<GatewaySettingsDto> BuildDtoAsync()
    {
        var stored = await _settings.GetSettingsAsync<MobileMoneySettings>(Category) ?? new MobileMoneySettings();
        var config = await _gateway.GetConfigAsync();
        return new GatewaySettingsDto(
            stored.Enabled,
            string.IsNullOrWhiteSpace(stored.CrmApiUrl) ? SaccGatewayDefaults.BaseUrl : stored.CrmApiUrl,
            string.IsNullOrEmpty(stored.ApiKey) ? string.Empty : Mask,
            stored.CardsEnabled,
            string.IsNullOrEmpty(stored.WebhookSecret) ? string.Empty : Mask,
            string.IsNullOrWhiteSpace(stored.WebhookId) ? null : stored.WebhookId,
            await CallbackUrlAsync(),
            config.MobileMoneyAvailable,
            config.CardsAvailable,
            await _stripe.IsConfiguredAsync());
    }

    private async Task<string> CallbackUrlAsync()
    {
        var origin = await _gateway.PublicBaseUrlAsync() ?? $"{Request.Scheme}://{Request.Host}";
        return origin.TrimEnd('/') + WebhookPath;
    }

    /// <summary>https only, except a loopback address in Development (the local gateway stub).</summary>
    private string? BaseUrlProblem(string value, bool required)
    {
        if (value.Length == 0) return required ? "The gateway address is required to switch the gateway on." : null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return "Enter a full address, e.g. https://sacc.ug.";
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return "The gateway address takes no query string — just https://sacc.ug.";
        if (uri.AbsolutePath.Contains("/api", StringComparison.OrdinalIgnoreCase))
            return "Enter the site address only (https://sacc.ug), not an API path.";
        if (uri.Scheme == Uri.UriSchemeHttps) return null;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && _environment.IsDevelopment()) return null;
        return "The gateway address must start with https:// — an API key must never travel in the clear.";
    }

    private string? CallbackProblem(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "The callback address is not a full URL.";
        if (!uri.AbsolutePath.EndsWith(WebhookPath, StringComparison.OrdinalIgnoreCase))
            return $"The callback address must end with {WebhookPath}.";
        if (uri.Scheme == Uri.UriSchemeHttps) return null;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && _environment.IsDevelopment()) return null;
        return "The callback address must be https:// and reachable from the internet. Set the platform's public address first.";
    }

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : null;

    private static object Problem(string code, string message) => new { error = code, message };

    [GeneratedRegex(@"^\S{16,256}$")]
    private static partial Regex ApiKeyShape();
}
