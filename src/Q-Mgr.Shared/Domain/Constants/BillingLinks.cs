namespace QMgr.Domain.Constants;

/// <summary>
/// Every address a person can be sent to about billing — the ONE home for them (2026-09-19).
///
/// Billing is one hub, <c>/billing</c>, with five tabs; the five routes it replaced
/// (<c>/billing/overview</c>, <c>/modules</c>, <c>/invoices</c>, <c>/payment-methods</c>,
/// <c>/usage</c>) are deleted, not aliased. Lives in Q-Mgr.Shared because the API sends people
/// here too — in emails, in a refused request's <c>upgradeUrl</c>, in an account-status
/// <c>actionUrl</c> — and that is where the dead links were: seven places pointed at
/// <c>/billing/plans</c>, <c>/billing/update-payment</c> and <c>/billing/reactivate</c>, none of
/// which was ever a page in this repository, and two more at <c>/billing/success</c> and
/// <c>/billing/cancelled</c>. A billing address written as a string literal anywhere else is that
/// bug coming back; <c>scripts/e2e/route-audit.mjs</c> reads these values and resolves them.
/// </summary>
public static class BillingLinks
{
    /// <summary>The hub's route, and the only billing <c>@page</c>.</summary>
    public const string Hub = "/billing";

    /// <summary>The query key the hub owns (see HubTabs). Tab keys below are wire formats: they
    /// travel in emails and notifications that outlive a deploy.</summary>
    public const string TabKey = "tab";

    public const string OverviewTab = "overview";
    public const string ModulesTab = "modules";
    public const string InvoicesTab = "invoices";
    public const string PaymentTab = "payment";
    public const string UsageTab = "usage";

    public const string Overview = Hub + "?" + TabKey + "=" + OverviewTab;

    /// <summary>What to add, remove or pay for. Also where every module gate, every "upgrade"
    /// refusal and every trial-ending email sends people: there are no plans to pick any more,
    /// only modules (tiers were retired 2026-09-04).</summary>
    public const string Modules = Hub + "?" + TabKey + "=" + ModulesTab;

    public const string Invoices = Hub + "?" + TabKey + "=" + InvoicesTab;

    /// <summary>How the account pays. Also where a suspended account is sent to fix a failed
    /// payment — the old <c>/billing/update-payment</c>.</summary>
    public const string Payment = Hub + "?" + TabKey + "=" + PaymentTab;

    public const string Usage = Hub + "?" + TabKey + "=" + UsageTab;

    /// <summary>A cancelled account comes back by adding a module again — the old
    /// <c>/billing/reactivate</c>, which never existed.</summary>
    public const string Reactivate = Modules;

    /// <summary>One invoice, opened on the Invoices tab.</summary>
    public static string Invoice(Guid invoiceId) => $"{Invoices}&invoice={invoiceId}";

    /// <summary>Where a card checkout returns to: the Modules tab, which reads <c>checkout</c> and
    /// says what happened. <paramref name="succeeded"/> false is a cancelled checkout.</summary>
    public static string CheckoutReturn(bool succeeded) =>
        $"{Modules}&checkout={(succeeded ? "success" : "cancelled")}";

    /// <summary>An absolute link for an email or a payment gateway, from the public base URL.
    /// Never the request host of a Web-to-API call, which is the loopback in production.</summary>
    public static string Absolute(string baseUrl, string link) => baseUrl.TrimEnd('/') + link;
}
