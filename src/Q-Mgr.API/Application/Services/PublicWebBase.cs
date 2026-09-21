using Microsoft.Extensions.Configuration;

namespace QMgr.API.Application.Services;

/// <summary>
/// The public address of this install — the origin every link a person or a gateway follows is built
/// on: a password reset, a verification email, a billing reminder, a card checkout's return, the payment
/// webhook's callback, a broadcast's unsubscribe link (2026-09-19).
///
/// <b>The address the server actually answers on wins.</b> <c>MediaStorage:PublicBaseUrl</c> is written
/// into the production unit as <c>https://$HostName</c> — the host nginx serves — and
/// <c>App:PublicWebBaseUrl</c> into the production appsettings with the same value. Only when neither
/// is set (development) does the platform SaaS setting's <c>BaseUrl</c> decide, then the <c>SaaS:BaseUrl</c>
/// configuration key.
///
/// It had eleven readers, in two orders. The payments code read the deploy's address first; the other
/// ten read the SaaS setting — which is SEEDED as <c>https://cashbook.ug</c>, a different site, so every
/// password-reset, onboarding and billing email on an install where nobody had edited that setting
/// linked to the wrong place. <c>IPlatformSettingsService.GetPublicWebBaseUrlAsync</c>
/// is the one reader; this class is its configuration half, shared with the one job that has no
/// settings service.
/// </summary>
public static class PublicWebBase
{
    /// <summary>What development falls back to when nothing at all is configured.</summary>
    public const string LastResort = "https://qmgr.app";

    /// <summary>The deploy-time address, or null when this install has none configured.</summary>
    public static string? FromDeployment(IConfiguration configuration) =>
        Clean(configuration["MediaStorage:PublicBaseUrl"]) ?? Clean(configuration["App:PublicWebBaseUrl"]);

    public static string? Clean(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? value!.Trim().TrimEnd('/')
            : null;
}
