namespace QMgr.Application.DTOs;

/// <summary>
/// One invoice, as GET api/v1/billing/invoices and GET api/v1/billing/invoices/{id} return it.
///
/// Moved here from BillingController on 2026-09-19. The Web kept two private copies: the Invoices
/// page's matched, and the Billing Overview's read an <c>Amount</c> and a <c>Description</c> the
/// API has never sent — so every "recent invoice" on the overview showed a total of 0, silently,
/// because System.Text.Json fills a missing property with its default. One shape, one home.
/// </summary>
public class InvoiceDto
{
    public Guid Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal Subtotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public DateTime InvoiceDate { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? PdfUrl { get; set; }
    public string? LineItems { get; set; }

    // "Bill To" block — only populated on the single-invoice detail endpoint, not the list.
    public string? BillingName { get; set; }
    public string? BillingEmail { get; set; }
    public string? OrganizationAddress { get; set; }
    public string? OrganizationPhone { get; set; }
}
