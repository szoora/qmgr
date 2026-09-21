using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Payments;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>
/// The sacc.ug gateway client — see <see cref="ISaccGateway"/> for the contract and why it replaced
/// <c>MobileMoneyService</c>. Every request carries the key and targets an absolute address built
/// from the settings of the moment, so an edit in the platform's Payments page takes effect on the
/// next call without a restart.
/// </summary>
public sealed class SaccGateway : ISaccGateway
{
    /// <summary>The named HttpClient. Registered WITHOUT a retry policy on purpose: the gateway's own
    /// rule (CRM-4.6) is that a retried collect can prompt the payer twice.</summary>
    public const string HttpClientName = "SaccGateway";

    private const string CollectPath = "/api/epay/collect";
    private const string StatusPath = "/api/epay/status/";
    private const string ValidatePath = "/api/epay/validate/";
    private const string WebhooksPath = "/api/webhooks";

    /// <summary>Every event a payment can end on, plus Review. On any of them the ledger re-reads the
    /// payment from <c>api/epay/status</c>, so the gateway stays the one source of truth whatever the
    /// wording of a webhook's own status field.</summary>
    public static readonly string[] WebhookEvents =
    {
        "payment.successful", "payment.failed", "payment.rejected",
        "payment.cancelled", "payment.expired", "payment.review"
    };

    /// <summary>What a tenant is told when something is wrong on the platform's side. It never names
    /// an address, a key or a status code: those go to the log and to the platform administrator.</summary>
    public const string UnavailableMessage = "Mobile Money payments are not available right now. Please try again later.";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SaccGateway> _logger;

    public SaccGateway(
        IHttpClientFactory httpClientFactory,
        IPlatformSettingsService platformSettings,
        IConfiguration configuration,
        ILogger<SaccGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _platformSettings = platformSettings;
        _configuration = configuration;
        _logger = logger;
    }

    // ------------------------------------------------------------------ settings

    private async Task<(SaccGatewayConfig Config, string ApiKey)> ResolveAsync()
    {
        MobileMoneySettings? db = null;
        try
        {
            db = await _platformSettings.GetSettingsAsync<MobileMoneySettings>("MobileMoney");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the payment gateway settings; falling back to configuration");
        }

        static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        // The platform default (SaccGatewayDefaults) when nothing is stored or configured: switching the
        // gateway on and adding the key is then all a fresh install needs.
        var baseUrl = (First(db?.CrmApiUrl, _configuration["MobileMoney:CrmApiUrl"]) ?? SaccGatewayDefaults.BaseUrl).Trim().TrimEnd('/');
        var apiKey = First(db?.ApiKey, _configuration["MobileMoney:ApiKey"]) ?? string.Empty;
        var enabled = db?.Enabled ?? _configuration.GetValue("MobileMoney:Enabled", false);

        return (new SaccGatewayConfig(
            enabled, baseUrl, !string.IsNullOrWhiteSpace(apiKey),
            db?.CardsEnabled ?? false,
            string.IsNullOrWhiteSpace(db?.WebhookSecret) ? null : db!.WebhookSecret,
            string.IsNullOrWhiteSpace(db?.WebhookId) ? null : db!.WebhookId), apiKey);
    }

    public async Task<SaccGatewayConfig> GetConfigAsync() => (await ResolveAsync()).Config;

    public async Task<bool> IsMobileMoneyAvailableAsync() => (await ResolveAsync()).Config.MobileMoneyAvailable;

    public async Task<bool> AreCardsAvailableAsync() => (await ResolveAsync()).Config.CardsAvailable;

    public async Task<string?> PublicBaseUrlAsync() => await _platformSettings.GetPublicWebBaseUrlAsync();

    private HttpRequestMessage Request(HttpMethod method, SaccGatewayConfig config, string apiKey, string path)
    {
        var request = new HttpRequestMessage(method, config.BaseUrl + path);
        request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    // ------------------------------------------------------------------ collect

    public async Task<GatewayCollectResult> CollectAsync(GatewayCollectRequest request, CancellationToken cancellationToken = default)
    {
        var (config, apiKey) = await ResolveAsync();
        var isCard = request.Method == PaymentMethodCodes.Card;

        if (!config.MobileMoneyAvailable || (isCard && !config.CardsAvailable))
        {
            return Refused("NOT_CONFIGURED",
                isCard ? "Card payments through the gateway are not switched on or not configured."
                       : "The payment gateway is not switched on, or its address or API key is missing.");
        }

        var body = new Dictionary<string, object?>
        {
            ["referenceId"] = request.ReferenceId.ToString(),
            // One payment, one idempotency key: the gateway answers a repeat with the original
            // payment instead of a second prompt on the payer's phone.
            ["idempotencyKey"] = request.ReferenceId.ToString(),
            ["amount"] = request.Amount,
            ["narrative"] = request.Narrative,
            ["externalId"] = request.ExternalId,
            ["payerMessage"] = request.PayerMessage,
            // "Other": the gateway records the money and provisions nothing of its own. Subscription
            // and License would make it activate SACC products.
            ["service"] = "Other",
            ["metadata"] = new Dictionary<string, string> { ["source"] = "qmgr" }
        };
        if (isCard)
        {
            body["method"] = "card";
            body["payerEmail"] = request.PayerEmail;
            body["payerName"] = request.PayerName;
            body["returnUrl"] = request.ReturnUrl;
            if (!string.IsNullOrWhiteSpace(request.PhoneNumber)) body["phoneNumber"] = UgandaPhone.Normalize(request.PhoneNumber);
        }
        else
        {
            body["method"] = "mobile";
            body["phoneNumber"] = UgandaPhone.Normalize(request.PhoneNumber);
        }

        using var message = Request(HttpMethod.Post, config, apiKey, CollectPath);
        message.Content = JsonContent.Create(body);

        HttpResponseMessage response;
        try
        {
            response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            // The request may or may not have reached the gateway. It is NOT retried (a retry can
            // prompt twice); the payment stays pending and the reconciliation job asks the gateway,
            // by this same reference, what became of it.
            _logger.LogWarning(ex, "Gateway collect for {Reference} did not answer; left pending for reconciliation", request.ReferenceId);
            return new GatewayCollectResult(true, PaymentStates.Pending, false, null, null, "NO_ANSWER",
                $"The gateway at {config.BaseUrl} did not answer ({ex.GetType().Name}). The payment is pending until reconciliation asks it.",
                "Your payment request has been sent. If a prompt appears on your phone, approve it.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var root = Parse(text);
                var state = Str(root, "state") ?? PaymentStates.Pending;
                var isFinal = Bool(root, "isFinal") ?? PaymentStates.IsFinal(state);
                var gatewayMessage = Str(root, "message") ?? string.Empty;
                var errorCode = Str(root, "errorCode");

                // A refusal the CRM labels as PENDING (found 2026-09-19 with an Airtel number). When no
                // provider can take the payer's network — Airtel with no Airtel route switched on — the
                // CRM returns early through PaymentResponse.Failed(...), which never runs ApplyState: the
                // body carries status "Failed", an errorCode and the reason, but state "Pending",
                // isFinal false, NO referenceId, over HTTP 202. Believing `state` showed the payer
                // "approve the prompt on your phone" for a prompt that was never sent, and the status
                // poll then 404ed until the payment was abandoned. A collect the gateway really holds
                // always echoes our reference; one that does not, and says Failed, was refused.
                var providerStatus = Str(root, "status");
                var echoedReference = Str(root, "referenceId");
                var refusedAsPending = !PaymentStates.IsFinal(state)
                    && string.IsNullOrWhiteSpace(echoedReference)
                    && (string.Equals(providerStatus, "Failed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(providerStatus, "Cancelled", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(errorCode));
                if (refusedAsPending)
                {
                    _logger.LogWarning("Gateway refused collect {Reference} but labelled it {State}: {Code} {Message}",
                        request.ReferenceId, state, errorCode, gatewayMessage);
                    return new GatewayCollectResult(true, PaymentStates.Failed, true, null, Str(root, "channel"), errorCode ?? "REFUSED",
                        $"The gateway refused the payment ({errorCode ?? providerStatus}): {gatewayMessage}".Trim(),
                        PayerRefusal(errorCode, request.PhoneNumber, gatewayMessage, isCard));
                }

                if (state == PaymentStates.Failed && isFinal)
                {
                    _logger.LogWarning("Gateway refused collect {Reference}: {Code} {Message}", request.ReferenceId, errorCode, gatewayMessage);
                    return new GatewayCollectResult(true, state, true, null, Str(root, "channel"), errorCode,
                        $"The gateway failed the payment at once: {errorCode} {gatewayMessage}".Trim(),
                        PayerRefusal(errorCode, request.PhoneNumber, gatewayMessage, isCard));
                }

                return new GatewayCollectResult(true, state, isFinal, Str(root, "redirectUrl"), Str(root, "channel"), errorCode,
                    $"Accepted by the gateway ({(int)response.StatusCode}), state {state}.",
                    isCard ? "Continue to the card page to pay." : "Check your phone and approve the payment.");
            }

            var code = (int)response.StatusCode;
            _logger.LogWarning("Gateway collect {Reference} refused with {Status}: {Body}", request.ReferenceId, code, Truncate(text));

            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest => new GatewayCollectResult(false, PaymentStates.Failed, true, null, null, "VALIDATION",
                    $"The gateway rejected the request (400): {FirstProblem(text)}",
                    $"The payment could not be started: {FirstProblem(text)}"),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Refused("UNAUTHORISED",
                    $"The gateway refused Q-Mgr's API key ({code}). Check the key, and that it holds payments:collect and allows this server's IP address."),
                HttpStatusCode.NotFound => Refused("NOT_FOUND",
                    $"The gateway address {config.BaseUrl} has no {CollectPath} (404). Check the Gateway URL — it should be https://sacc.ug."),
                HttpStatusCode.Conflict => new GatewayCollectResult(false, PaymentStates.Failed, true, null, null, "CONFLICT",
                    "The gateway already holds a different payment under this reference (409).",
                    "This payment was already started. Refresh to see where it stands."),
                (HttpStatusCode)429 => Refused("RATE_LIMITED", "The gateway is rate-limiting Q-Mgr (429)."),
                _ => Refused("GATEWAY_ERROR", $"The gateway answered {code}: {Truncate(text)}")
            };
        }

        static GatewayCollectResult Refused(string code, string adminMessage) =>
            new(false, PaymentStates.Failed, true, null, null, code, adminMessage, UnavailableMessage);
    }

    /// <summary>
    /// What the payer is told when the gateway refuses a payment before sending any prompt. The
    /// gateway's own reason ("Yo! Payments is not configured", "No payment provider is configured for
    /// Airtel") is for the platform administrator and goes to the log; the payer needs to know which
    /// number will work.
    /// </summary>
    internal static string PayerRefusal(string? errorCode, string? phoneNumber, string gatewayMessage, bool isCard)
    {
        if (isCard)
            return "Card payments are not available right now. Please pay with Mobile Money.";

        if (string.Equals(errorCode, "InvalidPhoneNumber", StringComparison.OrdinalIgnoreCase))
            return "That number cannot be charged by Mobile Money. Check it and try again.";

        if (string.Equals(errorCode, "ServiceUnavailable", StringComparison.OrdinalIgnoreCase)
            || gatewayMessage.Contains("provider", StringComparison.OrdinalIgnoreCase))
        {
            return UgandaPhone.Operator(phoneNumber) switch
            {
                "Airtel" => "Airtel Money is not available right now. Please pay with an MTN number.",
                "MTN" => "MTN Mobile Money is not available right now. Please try again shortly, or pay with an Airtel number.",
                { } other => $"{other} numbers cannot be charged right now. Please pay with an MTN number.",
                null => UnavailableMessage
            };
        }

        return "The payment could not be started. Please try again shortly.";
    }

    // ------------------------------------------------------------------ status

    public async Task<GatewayStatusResult> GetStatusAsync(Guid referenceId, CancellationToken cancellationToken = default)
    {
        var (config, apiKey) = await ResolveAsync();
        if (!config.HasValidBaseUrl || !config.HasApiKey)
            return new GatewayStatusResult(true, PaymentStates.Pending, false, null, null, null, "NOT_CONFIGURED",
                "The gateway is not configured, so the payment cannot be checked yet.");

        try
        {
            using var message = Request(HttpMethod.Get, config, apiKey, StatusPath + referenceId);
            using var response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(message, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new GatewayStatusResult(false, PaymentStates.Pending, false, null, null, null, "NOT_FOUND",
                    "The gateway has no payment with this reference for Q-Mgr's key.");

            if (!response.IsSuccessStatusCode)
                return new GatewayStatusResult(true, PaymentStates.Pending, false, null, null, null, "GATEWAY_ERROR",
                    $"The gateway answered {(int)response.StatusCode} to a status check.");

            var root = Parse(text);
            var state = Str(root, "state") ?? PaymentStates.Pending;
            return new GatewayStatusResult(true, state,
                Bool(root, "isFinal") ?? PaymentStates.IsFinal(state),
                Dec(root, "confirmedAmount"),
                Str(root, "provider"),
                Str(root, "channel"),
                Str(root, "errorCode"),
                Str(root, "message") ?? string.Empty);
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Gateway status check for {Reference} did not answer", referenceId);
            return new GatewayStatusResult(true, PaymentStates.Pending, false, null, null, null, "NO_ANSWER",
                "The gateway did not answer the status check.");
        }
    }

    // ------------------------------------------------------------------ check

    public async Task<GatewayCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var (config, apiKey) = await ResolveAsync();
        if (!config.HasValidBaseUrl) return new GatewayCheckResult(false, false, null, "Enter the gateway address, e.g. https://sacc.ug.");
        if (!config.HasApiKey) return new GatewayCheckResult(false, false, null, "Enter the API key issued to Q-Mgr in CRMPro.");

        try
        {
            // A status read of a reference that cannot exist: it needs a working key with
            // payments:status and moves no money. 404 is the healthy answer.
            using var message = Request(HttpMethod.Get, config, apiKey, StatusPath + Guid.Empty);
            using var response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(message, cancellationToken);
            var code = (int)response.StatusCode;
            return response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.OK or HttpStatusCode.BadRequest =>
                    new GatewayCheckResult(true, true, code, "The gateway is reachable and accepts the key."),
                HttpStatusCode.Unauthorized => new GatewayCheckResult(true, false, code,
                    "The gateway does not recognise the key (401). Check it was copied in full."),
                HttpStatusCode.Forbidden => new GatewayCheckResult(true, false, code,
                    "The key is recognised but not allowed (403): it needs payments:collect, payments:status and webhooks:manage, and must allow this server's IP address."),
                _ => new GatewayCheckResult(true, false, code, $"The gateway answered {code}.")
            };
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            return new GatewayCheckResult(false, false, null, $"The gateway at {config.BaseUrl} could not be reached: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ webhook registration

    public async Task<GatewayWebhookResult> RegisterWebhookAsync(string callbackUrl, string? existingWebhookId, CancellationToken cancellationToken = default)
    {
        var (config, apiKey) = await ResolveAsync();
        if (!config.HasValidBaseUrl || !config.HasApiKey)
            return new GatewayWebhookResult(false, null, null, "Save the gateway address and API key first.");

        var client = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            // One subscription for Q-Mgr: an old one is removed so the gateway never signs with a
            // secret Q-Mgr no longer holds. A 404 there just means it is already gone.
            if (!string.IsNullOrWhiteSpace(existingWebhookId))
            {
                using var delete = Request(HttpMethod.Delete, config, apiKey, $"{WebhooksPath}/{Uri.EscapeDataString(existingWebhookId)}");
                using var _ = await client.SendAsync(delete, cancellationToken);
            }

            using var message = Request(HttpMethod.Post, config, apiKey, WebhooksPath);
            message.Content = JsonContent.Create(new
            {
                name = "Q-Mgr",
                description = "Q-Mgr module purchases and renewals",
                callbackUrl,
                events = WebhookEvents
            });
            using var response = await client.SendAsync(message, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                return new GatewayWebhookResult(false, null, null, code switch
                {
                    401 or 403 => $"The gateway refused to register the webhook ({code}): the key needs webhooks:manage.",
                    400 => $"The gateway rejected the webhook: {FirstProblem(text)}",
                    _ => $"The gateway answered {code}: {Truncate(text)}"
                });
            }

            var root = Parse(text);
            var id = Str(root, "id");
            var secret = Str(root, "secret");
            return string.IsNullOrWhiteSpace(secret)
                ? new GatewayWebhookResult(false, id, null, "The gateway registered the webhook but returned no signing secret.")
                : new GatewayWebhookResult(true, id, secret, "The webhook is registered.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            return new GatewayWebhookResult(false, null, null, $"The gateway could not be reached: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ JSON helpers

    private static JsonElement Parse(string text)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    /// <summary>Case-insensitive, so camelCase and PascalCase both read.</summary>
    private static JsonElement? Prop(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement root, string name) => Prop(root, name) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        }
        : null;

    private static bool? Bool(JsonElement root, string name) => Prop(root, name) is { } v && v.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? v.GetBoolean() : null;

    private static decimal? Dec(JsonElement root, string name) => Prop(root, name) is { } v && v.ValueKind == JsonValueKind.Number
        ? v.GetDecimal()
        : Str(root, name) is { } s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>The first message out of a ValidationProblemDetails, or the body's own message.</summary>
    private static string FirstProblem(string text)
    {
        var root = Parse(text);
        if (Prop(root, "errors") is { ValueKind: JsonValueKind.Object } errors)
            foreach (var field in errors.EnumerateObject())
                if (field.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in field.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String) return item.GetString()!;
        return Str(root, "message") ?? Str(root, "title") ?? "the request was not valid.";
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
