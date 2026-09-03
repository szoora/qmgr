namespace QMgr.Application.DTOs;

/// <summary>
/// A card on file at the payment gateway, as returned by GET api/v1/billing/payment-methods.
/// Lives in Q-Mgr.Shared so the API (which builds it from Stripe's PaymentMethod) and the Web
/// billing pages (which render it) share one shape — the Web side previously kept its own
/// record with different property names (Brand/Last4/ExpMonth), so every card rendered blank.
/// </summary>
public record PaymentMethodDto(
    string Id,
    string Type,
    string? CardBrand,
    string? CardLast4,
    int? CardExpMonth,
    int? CardExpYear,
    bool IsDefault,
    DateTime? CreatedAt = null);
