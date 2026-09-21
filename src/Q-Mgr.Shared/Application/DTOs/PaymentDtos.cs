namespace QMgr.Application.DTOs;

// Everything about collecting money that crosses between Q-Mgr.API and Q-Mgr.Web (2026-09-19).
// One copy of each shape, here, for the reason CLAUDE.md's "SSoT: DTO duplication" section gives:
// every hand-written copy of a billing shape in this codebase has drifted, and a drifted shape is a
// silent runtime bug, not a compile error.

/// <summary>
/// Which ways to pay are available to a tenant right now. A provider is available only when it is
/// switched on AND its credentials are present — the one rule, decided in the API
/// (<c>PaymentProviders</c>), never re-derived on a page.
/// </summary>
public record PaymentProvidersDto(
    // Mobile Money through the sacc.ug gateway.
    bool MobileMoneyEnabled,
    // Some card route is available (the gateway's Pesapal channel, or Stripe).
    bool CardEnabled,
    // "sacc" or "stripe" — which route a card takes. Null when no card route is available.
    string? CardProvider,
    // Kept for callers that read it; true only when Stripe is the card route.
    bool StripeEnabled);

/// <summary>How a payment is being made.</summary>
public static class PaymentMethodCodes
{
    public const string MobileMoney = "mobile";
    public const string Card = "card";
}

/// <summary>The states of one collection, as the ledger records them. They are the gateway's own
/// (CRMApi <c>CollectionState</c>) so nothing is translated twice.</summary>
public static class PaymentStates
{
    public const string Pending = "Pending";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Abandoned = "Abandoned";

    // Held for a human at the gateway. NOT final: the money may yet be applied or returned.
    public const string Review = "Review";

    public static bool IsFinal(string? state) => state is Succeeded or Failed or Abandoned;
}

/// <summary>Start buying (or paying for a trialing) module.</summary>
public record ModulePurchaseRequest(
    string BillingCycle,
    string Method,
    string? PhoneNumber,
    string? PayerEmail,
    string? PayerName);

/// <summary>Pay one open invoice by Mobile Money.</summary>
public record PayInvoiceRequest(string PhoneNumber);

/// <summary>
/// The answer to starting a payment. <c>ReferenceId</c> is the ledger's payment id AND the gateway's
/// reference — one id end to end. <c>RedirectUrl</c> is set for a card payment (the payer goes to the
/// card page and comes back). <c>Simulated</c> is Development only, when no gateway is configured.
/// </summary>
public record PaymentStartDto(
    Guid? ReferenceId,
    string State,
    bool IsFinal,
    string Message,
    string? RedirectUrl = null,
    bool Simulated = false);

/// <summary>Where a payment stands, read from the ledger (which the gateway keeps current).</summary>
public record PaymentStatusDto(
    Guid ReferenceId,
    string State,
    bool IsFinal,
    string Message,
    decimal Amount,
    string Currency,
    string? Description,
    DateTime InitiatedAt,
    DateTime? CompletedAt);

/// <summary>The number renewals are charged to — Mobile Money's "payment method on file".</summary>
public record RenewalNumberDto(string? PhoneNumber, string? Display, string? Operator);

public record UpdateRenewalNumberRequest(string PhoneNumber);

// ------------------------------------------------------------------ platform: the gateway

/// <summary>The sacc.ug gateway's settings as the platform administrator sees them. Secrets come
/// back as the mask ("••••••••") when set and empty when not — the Platform Settings rule.</summary>
public record GatewaySettingsDto(
    bool Enabled,
    string? BaseUrl,
    string? ApiKey,
    bool CardsEnabled,
    string? WebhookSecret,
    string? WebhookId,
    // The address the gateway must call back — shown so it can be registered.
    string CallbackUrl,
    // Mobile Money is usable right now (switched on, address and key present).
    bool MobileMoneyAvailable,
    // Cards are usable right now through the gateway.
    bool CardsAvailable,
    // Stripe: stays off unless configured; shown so its state is never a surprise.
    bool StripeAvailable);

public record UpdateGatewaySettingsRequest(
    bool Enabled,
    string BaseUrl,
    string ApiKey,
    bool CardsEnabled);

/// <summary>The result of asking the gateway whether the key works.</summary>
public record GatewayCheckDto(bool Reachable, bool Authorised, int? StatusCode, string Message);

public record RegisterWebhookRequest(string? CallbackUrl);

public record WebhookRegistrationDto(bool Registered, string? WebhookId, string CallbackUrl, string Message);

public record TestPromptRequest(string PhoneNumber);

/// <summary>A test prompt's progress: what the gateway says, and whether its signed webhook has
/// reached Q-Mgr — the second half is what proves the callback address is right.</summary>
public record TestPromptStatusDto(
    Guid ReferenceId,
    string State,
    bool IsFinal,
    bool WebhookReceived,
    string Message);

// ------------------------------------------------------------------ platform: reconciliation

public record ReconciliationDayDto(
    DateOnly Day,
    int Sent,
    int Confirmed,
    int Failed,
    int Abandoned,
    int InReview,
    int Pending,
    decimal ConfirmedAmount);

public record ReconciliationPaymentDto(
    Guid ReferenceId,
    string OrganizationName,
    string Description,
    string Method,
    string? Phone,
    decimal Amount,
    string Currency,
    string State,
    DateTime InitiatedAt,
    DateTime? CompletedAt,
    string? Error,
    // Who decided a payment the gateway held for review, what they decided and why — null otherwise.
    string? Resolution = null);

public record ReconciliationDto(
    IReadOnlyList<ReconciliationDayDto> Days,
    int Sent,
    int Confirmed,
    int Failed,
    int Abandoned,
    int InReview,
    int Pending,
    decimal ConfirmedAmount,
    IReadOnlyList<ReconciliationPaymentDto> Recent,
    // Every payment still held for review, whatever its date: each needs a person to decide it.
    IReadOnlyList<ReconciliationPaymentDto>? AwaitingReview = null);

/// <summary>A platform administrator deciding a payment held for review (2026-09-19). Outcome is
/// <c>received</c> (the money arrived: settle it as paid) or <c>not-received</c> (settle it as failed).
/// The note is required and kept on the payment.</summary>
public record ResolveReviewRequest(string Outcome, string Note);

/// <summary>
/// The platform's defaults for the SACC payment gateway (user direction, 2026-09-19: "by default use
/// sacc crm configurations, api is https://sacc.ug, also call backs … platform default configurations,
/// not for tenants"). The address a fresh install collects through, and the path on this install the
/// gateway calls back — the full callback is this install's public address plus <see cref="WebhookPath"/>.
/// Nothing here is shown to or chosen by a tenant.
/// </summary>
public static class SaccGatewayDefaults
{
    public const string BaseUrl = "https://sacc.ug";
    public const string WebhookPath = "/api/v1/payments/sacc/webhook";
}

public static class ReviewOutcomes
{
    public const string Received = "received";
    public const string NotReceived = "not-received";
}
