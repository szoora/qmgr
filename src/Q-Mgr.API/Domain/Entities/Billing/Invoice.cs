using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Billing;

/// <summary>
/// Represents a billing invoice for a subscription period
/// </summary>
public class Invoice : BaseAuditableEntity
{
    /// <summary>Organization this invoice belongs to</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Subscription this invoice is for</summary>
    public Guid SubscriptionId { get; set; }

    #region Invoice Details

    /// <summary>Invoice number (e.g., INV-2024-0001)</summary>
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>Invoice status</summary>
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    /// <summary>Currency code (USD, UGX)</summary>
    public string Currency { get; set; } = "USD";

    #endregion

    #region Amounts

    /// <summary>Subtotal before tax/discount</summary>
    public decimal Subtotal { get; set; }

    /// <summary>Tax amount</summary>
    public decimal TaxAmount { get; set; }

    /// <summary>Discount amount</summary>
    public decimal DiscountAmount { get; set; }

    /// <summary>Total amount due</summary>
    public decimal Total { get; set; }

    /// <summary>Amount already paid</summary>
    public decimal AmountPaid { get; set; }

    /// <summary>Amount remaining</summary>
    public decimal AmountDue => Total - AmountPaid;

    #endregion

    #region Billing Period

    /// <summary>Start of billing period</summary>
    public DateTime PeriodStart { get; set; }

    /// <summary>End of billing period</summary>
    public DateTime PeriodEnd { get; set; }

    /// <summary>Invoice date</summary>
    public DateTime InvoiceDate { get; set; } = DateTime.UtcNow;

    /// <summary>Due date for payment</summary>
    public DateTime DueDate { get; set; }

    /// <summary>When the invoice was paid</summary>
    public DateTime? PaidAt { get; set; }

    #endregion

    #region Stripe Integration

    /// <summary>Stripe Invoice ID</summary>
    public string? StripeInvoiceId { get; set; }

    /// <summary>Stripe hosted invoice URL</summary>
    public string? StripeInvoiceUrl { get; set; }

    /// <summary>Stripe PDF URL</summary>
    public string? StripePdfUrl { get; set; }

    #endregion

    #region Line Items (JSON)

    /// <summary>JSON array of line items</summary>
    public string? LineItems { get; set; }

    #endregion

    #region Billing Details

    /// <summary>Billing email address</summary>
    public string? BillingEmail { get; set; }

    /// <summary>Billing name/company</summary>
    public string? BillingName { get; set; }

    /// <summary>Billing address (JSON)</summary>
    public string? BillingAddress { get; set; }

    #endregion

    #region Notes

    /// <summary>Internal notes</summary>
    public string? Notes { get; set; }

    /// <summary>Footer text for invoice</summary>
    public string? FooterText { get; set; }

    #endregion

    #region Navigation

    /// <summary>The organization</summary>
    public virtual Organization.Organization? Organization { get; set; }

    /// <summary>The subscription</summary>
    public virtual Subscription? Subscription { get; set; }

    /// <summary>Payments applied to this invoice</summary>
    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();

    #endregion
}
