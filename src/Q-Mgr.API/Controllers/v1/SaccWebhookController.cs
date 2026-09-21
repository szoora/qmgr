using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces.Billing;
using QMgr.Infrastructure.Services.Billing;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Where the sacc.ug gateway tells Q-Mgr a payment has moved (2026-09-19). The address is registered
/// with the gateway from the platform Payments page, which stores the signing secret it returns.
///
/// <b>The body is a doorbell, not a verdict.</b> A verified callback names a reference; the ledger then
/// asks the gateway for that payment's status with Q-Mgr's own key and settles what the gateway says.
/// Nothing in the body — amount, status, phone — is applied as it stands, so a replayed or forged body
/// can at worst cause one extra status read. The reconciliation job covers a callback that never
/// arrives, so answering 200 to something this endpoint cannot use is always safe.
/// </summary>
[ApiController]
[Route("api/v1/payments/sacc")] // the callback path is SaccGatewayDefaults.WebhookPath — keep them together
[AllowAnonymous]
public class SaccWebhookController : ControllerBase
{
    private static readonly DistributedCacheEntryOptions SeenLifetime = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(3) };

    private readonly ISaccGateway _gateway;
    private readonly IPaymentLedger _ledger;
    private readonly IDistributedCache _cache;
    private readonly ILogger<SaccWebhookController> _logger;

    public SaccWebhookController(ISaccGateway gateway, IPaymentLedger ledger, IDistributedCache cache, ILogger<SaccWebhookController> logger)
    {
        _gateway = gateway;
        _ledger = ledger;
        _cache = cache;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync(cancellationToken);

        var config = await _gateway.GetConfigAsync();
        if (string.IsNullOrEmpty(config.WebhookSecret))
        {
            // Nothing to verify against: the webhook was never registered from this install. Refused, so
            // the gateway keeps retrying until it is — reconciliation settles the payment meanwhile.
            _logger.LogWarning("Gateway webhook refused: no signing secret is stored. Register the webhook on the platform Payments page");
            return Unauthorized(new { message = "Webhook not registered." });
        }

        var signature = Request.Headers["X-Webhook-Signature"].FirstOrDefault();
        if (!SaccWebhookSignature.Verify(body, signature, config.WebhookSecret, DateTimeOffset.UtcNow, out var reason))
        {
            _logger.LogWarning("Gateway webhook refused: {Reason}", reason);
            return Unauthorized(new { message = "Invalid signature." });
        }

        var eventName = Request.Headers["X-Webhook-Event"].FirstOrDefault();
        Guid referenceId;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            eventName ??= Text(root, "event");
            var data = root.TryGetProperty("data", out var d) ? d : root;
            if (!Guid.TryParse(Text(data, "reference_id") ?? Text(data, "referenceId"), out referenceId))
            {
                _logger.LogInformation("Gateway webhook {Event} carried no Q-Mgr reference; ignored", eventName);
                return Ok(new { received = true });
            }
        }
        catch (JsonException)
        {
            _logger.LogWarning("Gateway webhook with a valid signature carried a body that is not JSON");
            return BadRequest(new { message = "Body is not JSON." });
        }

        // A delivery the gateway retried after we had already processed it.
        var deliveryId = Request.Headers["X-Webhook-Id"].FirstOrDefault();
        var seenKey = string.IsNullOrWhiteSpace(deliveryId) ? null : $"sacc-webhook-seen:{deliveryId}";
        if (seenKey != null && await _cache.GetStringAsync(seenKey, cancellationToken) != null)
            return Ok(new { received = true, duplicate = true });

        var state = await _ledger.RefreshAsync(referenceId, cancellationToken);
        if (state == null)
        {
            // Not a ledger payment — the platform administrator's test prompt, or somebody else's
            // collection on a key that should not be shared.
            var isTest = await SaccTestPrompts.MarkWebhookAsync(_cache, referenceId, StateOf(eventName), cancellationToken);
            if (!isTest) _logger.LogWarning("Gateway webhook {Event} for unknown reference {Reference}", eventName, referenceId);
        }
        else
        {
            _logger.LogInformation("Gateway webhook {Event} for {Reference}: payment is {State}", eventName, referenceId, state);
        }

        if (seenKey != null) await _cache.SetStringAsync(seenKey, "1", SeenLifetime, cancellationToken);
        return Ok(new { received = true });
    }

    /// <summary>The event name as a ledger state — used only to label a test prompt's webhook.</summary>
    private static string? StateOf(string? eventName) => eventName switch
    {
        "payment.successful" => PaymentStates.Succeeded,
        "payment.failed" or "payment.rejected" or "payment.cancelled" => PaymentStates.Failed,
        "payment.expired" => PaymentStates.Abandoned,
        "payment.review" => PaymentStates.Review,
        "payment.pending" => PaymentStates.Pending,
        _ => null
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
