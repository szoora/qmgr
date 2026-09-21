namespace QMgr.Application.Interfaces.Billing;

/// <summary>
/// The sacc.ug payment gateway (SACC's CRM, "CRM API Gateway v2.0") — the ONE place its contract lives
/// (2026-09-19). It replaced <c>IMobileMoneyService</c>, which was written against an imagined API:
/// it posted to <c>/api/v2/payments/collect</c> (404 on the live gateway), sent a body the gateway does
/// not read, expected a finished verdict from a call that answers "accepted, still waiting", and had
/// nothing to receive the gateway's callbacks.
///
/// The contract, read from the gateway's own source (E:\CRM, CRMApi) and confirmed live:
/// <list type="bullet">
/// <item><c>POST api/epay/collect</c> (scope <c>payments:collect</c>) answers 202 with
///   <c>{ referenceId, state, isFinal, statusPollUrl, pollAfterSeconds, redirectUrl? }</c>.</item>
/// <item><c>GET api/epay/status/{referenceId}</c> (scope <c>payments:status</c>), readable only by the
///   key that started the payment; 404 for anything else.</item>
/// <item><c>POST api/webhooks</c> (scope <c>webhooks:manage</c>) registers the callback and returns
///   its signing secret.</item>
/// <item>Authentication is <c>X-API-Key</c>. Amounts are UGX. The reference must be a UUID.</item>
/// </list>
///
/// Nothing outside this interface builds a gateway URL, reads a gateway response or verifies a
/// gateway signature.
/// </summary>
public interface ISaccGateway
{
    /// <summary>The settings as resolved right now (Platform Settings, then configuration).</summary>
    Task<SaccGatewayConfig> GetConfigAsync();

    /// <summary>Mobile Money can be collected: switched on, a valid address and a key are present.</summary>
    Task<bool> IsMobileMoneyAvailableAsync();

    /// <summary>Cards can be collected through the gateway's Pesapal channel as well.</summary>
    Task<bool> AreCardsAvailableAsync();

    /// <summary>Start a collection. Never retried here: a retried collect can prompt a payer twice,
    /// and the gateway's idempotency key (our reference) is what makes a repeat safe.</summary>
    Task<GatewayCollectResult> CollectAsync(GatewayCollectRequest request, CancellationToken cancellationToken = default);

    /// <summary>What the gateway has decided about a collection this key started.</summary>
    Task<GatewayStatusResult> GetStatusAsync(Guid referenceId, CancellationToken cancellationToken = default);

    /// <summary>Is the gateway reachable, and does it accept the key?</summary>
    Task<GatewayCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Register (or replace) Q-Mgr's webhook subscription and return its signing secret.</summary>
    /// <summary>The public origin people and the gateway reach this install on — the ONE reader of it
    /// for payments: <c>MediaStorage:PublicBaseUrl</c> (set in the production unit), then the SaaS
    /// settings' base URL. Null when neither is set. Builds the webhook callback and the card return.</summary>
    Task<string?> PublicBaseUrlAsync();

    Task<GatewayWebhookResult> RegisterWebhookAsync(string callbackUrl, string? existingWebhookId, CancellationToken cancellationToken = default);
}

/// <summary>The gateway's settings as resolved. The API key itself is never exposed from here.</summary>
public record SaccGatewayConfig(
    bool Enabled,
    string? BaseUrl,
    bool HasApiKey,
    bool CardsEnabled,
    string? WebhookSecret,
    string? WebhookId)
{
    /// <summary>An absolute http(s) address with nothing after the host but an optional path.</summary>
    public bool HasValidBaseUrl =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    public bool MobileMoneyAvailable => Enabled && HasValidBaseUrl && HasApiKey;
    public bool CardsAvailable => MobileMoneyAvailable && CardsEnabled;
}

/// <summary>One collection. <paramref name="ReferenceId"/> is Q-Mgr's payment id and is sent as the
/// gateway's ReferenceId AND its IdempotencyKey, so the same payment can never be started twice.</summary>
public record GatewayCollectRequest(
    Guid ReferenceId,
    decimal Amount,
    string Narrative,
    string Method,
    string? PhoneNumber,
    string? PayerEmail,
    string? PayerName,
    string? ReturnUrl,
    string? ExternalId,
    string? PayerMessage);

/// <summary>
/// The gateway's answer to a collect. <c>Accepted</c> means the gateway holds the payment (it may still
/// be pending); a refusal carries two messages — <c>AdminMessage</c> for the log and the platform
/// administrator, <c>CustomerMessage</c> for a tenant, which never names the gateway's address, key or
/// status codes.
/// </summary>
public record GatewayCollectResult(
    bool Accepted,
    string State,
    bool IsFinal,
    string? RedirectUrl,
    string? Channel,
    string? ErrorCode,
    string AdminMessage,
    string CustomerMessage);

/// <summary><c>Known</c> is false when the gateway has no such payment for this key (404).</summary>
public record GatewayStatusResult(
    bool Known,
    string State,
    bool IsFinal,
    decimal? ConfirmedAmount,
    string? Provider,
    string? Channel,
    string? ErrorCode,
    string Message);

public record GatewayCheckResult(bool Reachable, bool Authorised, int? StatusCode, string Message);

public record GatewayWebhookResult(bool Registered, string? WebhookId, string? Secret, string Message);
