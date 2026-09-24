using System.Net;
using System.Text;
using QMgr.Application.Branding;

namespace QMgr.Infrastructure.Email;

/// <summary>
/// The one place every outbound HTML email gets its layout and branding. Before this existed,
/// nine templates across BillingJobs, AuthController, RegisterOrganizationCommandHandler and
/// TenantProvisioningService each inlined their own DOCTYPE/body/heading/button markup — with
/// two competing accent palettes (Bootstrap blue in billing, wine in auth) and two different
/// link-building conventions. Callers now supply content only; the chrome lives here.
/// </summary>
public static class EmailTemplates
{
    /// <summary>Our product's name, for the platform's own mail. From <see cref="ProductBrand"/>, never typed here.</summary>
    public const string AppName = ProductBrand.Name;

    /// <summary>Brand accent — the same wine as the app's --qm-primary (light theme).</summary>
    private const string Accent = "#7a2847";

    /// <summary>
    /// Who an email is FROM, as the reader sees it: the name in the body, the accent, the logo in
    /// the header, and whether our own name appears at the foot of it.
    ///
    /// THE ENVELOPE IS NOT IN HERE, AND THAT IS NOT A GAP. IONOS — and Google, and Microsoft —
    /// reject a From address that is not the authenticated mailbox, so carrying a tenant's address
    /// there turns a working relay into a 5xx on every send. The platform mailbox sends and the
    /// tenant's display name is shown (see ISmtpProfileResolver); a tenant that wants its own From
    /// address configures its own SMTP, which that resolver already supports. So the body is fully
    /// branded, the envelope is not, and anything else would be a promise the mail providers
    /// refuse to keep.
    /// </summary>
    /// <param name="Name">The APP's name — the school's own, exactly as typed, or ours (<c>ProductBrand.NameFor</c>).
    /// What a subject line says the email is about: "Reset your MARYHILL Dashboard password".</param>
    /// <param name="OrganizationName">The organisation, or null for the platform's own mail. What the
    /// signature and — once attribution is bought off — the copyright line name: a copyright belongs
    /// to the school, not to what the school calls its app.</param>
    public sealed record EmailBrand(string Name, string? OrganizationName, string Accent, string? LogoUrl, bool AttributionRemoved)
    {
        /// <summary>Ours. The default for every caller that does not know an organization —
        /// a sign-up confirmation for an organization that does not exist yet, a platform notice.</summary>
        public static readonly EmailBrand Platform = new(AppName, null, EmailTemplates.Accent, null, false);

        /// <summary>Who signs the email: the organisation when there is one, otherwise our product.</summary>
        public string Signatory => string.IsNullOrWhiteSpace(OrganizationName) ? Name : OrganizationName!;
    }
    private const string Danger = "#b42318";
    private const string Text = "#1f1f1f";
    private const string Muted = "#6b6b6b";
    private const string Rule = "#e6e6e6";

    public enum Tone { Info, Warning }

    /// <summary>
    /// Wraps content in the standard layout.
    /// </summary>
    /// <param name="title">Heading shown at the top of the message.</param>
    /// <param name="greeting">Optional name for a "Hi Name," line; null to omit.</param>
    /// <param name="paragraphs">Body paragraphs — HTML fragments; use <see cref="P"/>/<see cref="B"/> to encode user data.</param>
    /// <param name="ctaText">Button label, or null for no button.</param>
    /// <param name="ctaUrl">Button target (absolute URL).</param>
    /// <param name="footerNote">Optional small-print line under the rule (HTML fragment).</param>
    /// <param name="tone">Warning tone colours the heading red (payment failed, suspended).</param>
    /// <param name="showLinkFallback">Prints the CTA URL as text under the button (verification / reset links).</param>
    /// <param name="brand">Whose email this is. Null is the platform's own — every existing caller
    /// passes nothing and is unchanged, which is why this is last and optional.</param>
    public static string Layout(
        string title,
        string? greeting,
        IEnumerable<string> paragraphs,
        string? ctaText = null,
        string? ctaUrl = null,
        string? footerNote = null,
        Tone tone = Tone.Info,
        bool showLinkFallback = false,
        EmailBrand? brand = null)
    {
        var who = brand ?? EmailBrand.Platform;
        var accent = who.Accent;
        var headingColor = tone == Tone.Warning ? Danger : accent;
        var body = string.Join("\n", paragraphs.Select(p => $"        <p style='margin: 0 0 14px 0;'>{p}</p>"));
        var greet = greeting == null ? "" : $"        <p style='margin: 0 0 14px 0;'>Hi {WebUtility.HtmlEncode(greeting)},</p>\n";
        var cta = "";
        if (!string.IsNullOrEmpty(ctaText) && !string.IsNullOrEmpty(ctaUrl))
        {
            var safeUrl = WebUtility.HtmlEncode(ctaUrl);
            cta = $@"        <div style='text-align: center; margin: 28px 0;'>
            <a href='{safeUrl}' style='background-color: {accent}; color: #ffffff; padding: 12px 30px; text-decoration: none; border-radius: 4px; display: inline-block; font-weight: 600;'>{WebUtility.HtmlEncode(ctaText)}</a>
        </div>
";
            if (showLinkFallback)
            {
                cta += $@"        <p style='margin: 0 0 14px 0; color: {Muted}; font-size: 13px;'>Or copy and paste this link into your browser:</p>
        <p style='margin: 0 0 14px 0; word-break: break-all; color: {Muted}; font-size: 13px;'>{safeUrl}</p>
";
            }
        }

        var footer = string.IsNullOrEmpty(footerNote)
            ? ""
            : $"        <p style='color: {Muted}; font-size: 13px; margin: 0 0 10px 0;'>{footerNote}</p>\n";

        // A logo, where the organization has one. An <img> in an email is blocked by default in
        // most clients, so the name still has to carry the message on its own — this is the
        // decoration, never the content.
        var logo = string.IsNullOrWhiteSpace(who.LogoUrl)
            ? ""
            : $"        <div style='margin: 0 0 18px 0;'><img src='{WebUtility.HtmlEncode(who.LogoUrl)}' alt='{WebUtility.HtmlEncode(who.Name)}' style='max-height: 48px; max-width: 220px;'></div>\n";

        // THE THIRD ATTRIBUTION STRING. The other two are the six public sign-in pages and the
        // shell footer; all three move together on one flag, or a tenant pays to remove our name
        // and still reads it at the foot of their own password-reset mail.
        var attribution = who.AttributionRemoved
            ? $"&copy; {DateTime.UtcNow.Year} {WebUtility.HtmlEncode(who.Signatory)}"
            : $"&copy; {DateTime.UtcNow.Year} {ProductBrand.Company}";   // as QCopyright on the web and the app

        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>{WebUtility.HtmlEncode(title)}</title>
</head>
<body style='margin: 0; padding: 0; background: #f4f4f5;'>
    <div style='max-width: 600px; margin: 0 auto; padding: 32px 20px; font-family: Arial, Helvetica, sans-serif; line-height: 1.6; color: {Text};'>
        <div style='background: #ffffff; border: 1px solid {Rule}; border-top: 3px solid {accent}; border-radius: 4px; padding: 28px;'>
{logo}        <h1 style='color: {headingColor}; font-size: 22px; margin: 0 0 18px 0;'>{WebUtility.HtmlEncode(title)}</h1>
{greet}{body}
{cta}        <p style='margin: 18px 0 0 0;'>Best regards,<br>{WebUtility.HtmlEncode(who.Signatory)}</p>
        </div>
        <hr style='border: none; border-top: 1px solid {Rule}; margin: 24px 0 14px 0;'>
{footer}        <p style='color: {Muted}; font-size: 12px; margin: 0;'>{attribution}</p>
    </div>
</body>
</html>";
    }

    /// <summary>HTML-encodes plain text for use inside a paragraph.</summary>
    public static string P(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Bold, encoded.</summary>
    public static string B(string text) => $"<strong>{WebUtility.HtmlEncode(text)}</strong>";

    /// <summary>Joins a base URL and a site-relative path.</summary>
    public static string Link(string baseUrl, string path)
        => $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    // =====================================================================================================
    // Report-email building blocks (promoted from VisitorReportEmail on 2026-09-16, Staff Performance
    // Phase 3). Inline styles only — mail clients strip stylesheets — and long tables are truncated
    // with an explicit count of what was left out, because a silently shortened list reads as "that
    // is all of them". VisitorReportEmail delegates to these; the staff digests build on them too, so
    // there is ONE copy of the rule for what a table or a KPI block looks like in an email.
    // =====================================================================================================

    /// <summary>Report palette. Distinct from the transactional Layout's on purpose: reports are denser and read on a phone.</summary>
    public const string ReportInk = "#1b1317";
    public const string ReportMuted = "#6d5b63";
    public const string ReportRule = "#e7dde1";
    public const string ReportWine = "#7a2847";
    public const string ReportDanger = "#a3302a";

    /// <summary>Report shell: heading, subtitle, body, small-print footer. <paramref name="footer"/> is plain text.</summary>
    public static string ReportShell(string title, string subtitle, string body, string footer = "Sent by " + ProductBrand.Name + ". Times are shown in the branch's local timezone.") => $@"
<div style=""font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:{ReportInk};max-width:720px;margin:0 auto;padding:8px"">
  <h1 style=""font-size:20px;margin:0 0 4px;color:{ReportWine}"">{P(title)}</h1>
  <p style=""margin:0 0 18px;color:{ReportMuted};font-size:13px"">{P(subtitle)}</p>
  {body}
  <p style=""margin-top:26px;padding-top:12px;border-top:1px solid {ReportRule};color:{ReportMuted};font-size:11px"">
    {P(footer)}
  </p>
</div>";

    /// <summary>
    /// An inline-styled table. Cells are HTML fragments (encode user data with <see cref="P"/>);
    /// headers are plain text. <paramref name="totalCount"/> larger than the rows given prints a
    /// "Showing n of N" line rather than pretending the list is complete.
    /// </summary>
    public static string ReportTable(string[] headers, IEnumerable<string[]> rows, int totalCount, string emptyText = "Nothing to report.")
    {
        var body = new StringBuilder();
        var shown = 0;

        foreach (var row in rows)
        {
            body.Append("<tr>");
            foreach (var cell in row)
                body.Append($@"<td style=""padding:7px 10px;border-bottom:1px solid {ReportRule};font-size:13px;vertical-align:top"">{cell}</td>");
            body.Append("</tr>");
            shown++;
        }

        if (shown == 0)
            return $@"<p style=""margin:0 0 18px;color:{ReportMuted};font-size:13px"">{P(emptyText)}</p>";

        var head = string.Concat(headers.Select(h =>
            $@"<th align=""left"" style=""padding:7px 10px;border-bottom:2px solid {ReportRule};font-size:11px;letter-spacing:.05em;text-transform:uppercase;color:{ReportMuted}"">{P(h)}</th>"));

        var omitted = totalCount > shown
            ? $@"<p style=""margin:6px 0 18px;color:{ReportMuted};font-size:12px"">Showing {shown} of {totalCount}. Open the report in the app for the rest.</p>"
            : @"<div style=""height:18px""></div>";

        return $@"<table cellspacing=""0"" cellpadding=""0"" style=""width:100%;border-collapse:collapse""><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table>{omitted}";
    }

    /// <summary>A section heading inside a report.</summary>
    public static string ReportSection(string heading) =>
        $@"<h2 style=""font-size:14px;margin:20px 0 8px;color:{ReportInk}"">{P(heading)}</h2>";

    /// <summary>A KPI block: big number, small uppercase label. <paramref name="alert"/> paints the number red.</summary>
    public static string ReportStat(string label, string value, bool alert = false) => $@"
<div style=""display:inline-block;min-width:120px;margin:0 14px 12px 0"">
  <div style=""font-size:24px;font-weight:700;color:{(alert ? ReportDanger : ReportInk)}"">{P(value)}</div>
  <div style=""font-size:11px;letter-spacing:.05em;text-transform:uppercase;color:{ReportMuted}"">{P(label)}</div>
</div>";

    /// <summary>A highlighted note (a caveat, an all-clear). <paramref name="text"/> is plain text.</summary>
    public static string ReportCallout(string text, bool danger = false) =>
        $@"<p style=""margin:14px 0 0;padding:10px 12px;background:{(danger ? "#f8e6e4" : "#e4f0ea")};border-left:3px solid {(danger ? ReportDanger : "#2c6f4c")};color:{ReportInk};font-size:13px"">{P(text)}</p>";
}
