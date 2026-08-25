using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Billing;

/// <summary>
/// Represents a payment transaction
/// </summary>
public class Payment : BaseEntity
{
    /// <summary>Organization that made the payment</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Subscription this payment is for (optional)</summary>
    public Guid? SubscriptionId { get; set; }

    /// <summary>Invoice this payment applies to (optional)</summary>
    public Guid? InvoiceId { get; set; }

    #region Payment Details

    /// <summary>Payment amount</summary>
    public decimal Amount { get; set; }

    /// <summary>Currency code (USD, UGX)</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Payment method used</summary>
    public PaymentMethod PaymentMethod { get; set; }

    /// <summary>Payment status</summary>
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    #endregion

    #region Reference IDs

    /// <summary>Internal reference ID</summary>
    public string ReferenceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>External reference from payment provider</summary>
    public string? ExternalReferenceId { get; set; }

    /// <summary>Stripe Payment Intent ID</summary>
    public string? StripePaymentIntentId { get; set; }

    /// <summary>Stripe Charge ID</summary>
    public string? StripeChargeId { get; set; }

    /// <summary>Mobile Money transaction reference</summary>
    public string? MobileMoneyTransactionId { get; set; }

    #endregion

    #region Mobile Money Details

    /// <summary>Phone number used for mobile money</summary>
    public string? MobileMoneyPhone { get; set; }

    /// <summary>Mobile money channel (MTN, Airtel)</summary>
    public string? MobileMoneyChannel { get; set; }

    #endregion

    #region Card Details (masked)

    /// <summary>Last 4 digits of card</summary>
    public string? CardLast4 { get; set; }

    /// <summary>Card brand (Visa, Mastercard, etc.)</summary>
    public string? CardBrand { get; set; }

    #endregion

    #region Timestamps

    /// <summary>When the payment was initiated</summary>
    public DateTime InitiatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the payment was completed</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>When the payment failed</summary>
    public DateTime? FailedAt { get; set; }

    /// <summary>When the payment was refunded</summary>
    public DateTime? RefundedAt { get; set; }

    #endregion

    #region Error Handling

    /// <summary>Error code if payment failed</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Error message if payment failed</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Number of retry attempts</summary>
    public int RetryCount { get; set; }

    /// <summary>Next retry scheduled time</summary>
    public DateTime? NextRetryAt { get; set; }

    #endregion

    #region Metadata

    /// <summary>Description/narrative</summary>
    public string? Description { get; set; }

    /// <summary>Additional metadata (JSON)</summary>
    public string? Metadata { get; set; }

    /// <summary>IP address of payer</summary>
    public string? IpAddress { get; set; }

    #endregion

    #region Refund

    /// <summary>Refund amount (if refunded)</summary>
    public decimal? RefundAmount { get; set; }

    /// <summary>Refund reason</summary>
    public string? RefundReason { get; set; }

    #endregion

    #region Navigation

    /// <summary>The organization</summary>
    public virtual Organization.Organization? Organization { get; set; }

    /// <summary>The subscription</summary>
    public virtual Subscription? Subscription { get; set; }

    /// <summary>The invoice</summary>
    public virtual Invoice? Invoice { get; set; }

    #endregion
}
