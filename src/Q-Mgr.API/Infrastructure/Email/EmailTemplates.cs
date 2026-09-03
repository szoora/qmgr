using System.Net;

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
    public const string AppName = "Q-Mgr";

    /// <summary>Brand accent — the same wine as the app's --qm-primary (light theme).</summary>
    private const string Accent = "#7a2847";
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
    public static string Layout(
        string title,
        string? greeting,
        IEnumerable<string> paragraphs,
        string? ctaText = null,
        string? ctaUrl = null,
        string? footerNote = null,
        Tone tone = Tone.Info,
        bool showLinkFallback = false)
    {
        var headingColor = tone == Tone.Warning ? Danger : Accent;
        var body = string.Join("\n", paragraphs.Select(p => $"        <p style='margin: 0 0 14px 0;'>{p}</p>"));
        var greet = greeting == null ? "" : $"        <p style='margin: 0 0 14px 0;'>Hi {WebUtility.HtmlEncode(greeting)},</p>\n";
        var cta = "";
        if (!string.IsNullOrEmpty(ctaText) && !string.IsNullOrEmpty(ctaUrl))
        {
            var safeUrl = WebUtility.HtmlEncode(ctaUrl);
            cta = $@"        <div style='text-align: center; margin: 28px 0;'>
            <a href='{safeUrl}' style='background-color: {Accent}; color: #ffffff; padding: 12px 30px; text-decoration: none; border-radius: 4px; display: inline-block; font-weight: 600;'>{WebUtility.HtmlEncode(ctaText)}</a>
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

        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>{WebUtility.HtmlEncode(title)}</title>
</head>
<body style='margin: 0; padding: 0; background: #f4f4f5;'>
    <div style='max-width: 600px; margin: 0 auto; padding: 32px 20px; font-family: Arial, Helvetica, sans-serif; line-height: 1.6; color: {Text};'>
        <div style='background: #ffffff; border: 1px solid {Rule}; border-top: 3px solid {Accent}; border-radius: 4px; padding: 28px;'>
        <h1 style='color: {headingColor}; font-size: 22px; margin: 0 0 18px 0;'>{WebUtility.HtmlEncode(title)}</h1>
{greet}{body}
{cta}        <p style='margin: 18px 0 0 0;'>Best regards,<br>The {AppName} Team</p>
        </div>
        <hr style='border: none; border-top: 1px solid {Rule}; margin: 24px 0 14px 0;'>
{footer}        <p style='color: {Muted}; font-size: 12px; margin: 0;'>&copy; {DateTime.UtcNow.Year} {AppName} &middot; Queue Management</p>
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
}
