using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QMgr.Application.Interfaces.Billing;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Email;

/// <summary>
/// Whose name an outbound email carries. The ONE reader of an organization's branding for email,
/// so the twenty-two places that send one do not each decide it.
/// </summary>
public interface IEmailBrandService
{
    /// <summary>
    /// The brand for an organization, or the platform's own when the id is null, the organization
    /// has white-labelling switched off, or it is not entitled to it.
    /// </summary>
    Task<EmailTemplates.EmailBrand> ForOrganizationAsync(Guid? organizationId, CancellationToken cancellationToken = default);
}

public sealed class EmailBrandService : IEmailBrandService
{
    /// <summary>
    /// Short. An email is sent from a background job as often as from a request, and an
    /// organization's branding changes about as often as its name does — but a tenant who has just
    /// turned white-labelling on should see it on the next message, not in an hour.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly QMgrDbContext _db;
    private readonly IFeatureFlagService _features;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EmailBrandService> _logger;

    public EmailBrandService(QMgrDbContext db, IFeatureFlagService features, IMemoryCache cache, ILogger<EmailBrandService> logger)
    {
        _db = db;
        _features = features;
        _cache = cache;
        _logger = logger;
    }

    public async Task<EmailTemplates.EmailBrand> ForOrganizationAsync(Guid? organizationId, CancellationToken cancellationToken = default)
    {
        if (organizationId is not { } id) return EmailTemplates.EmailBrand.Platform;

        var key = "email-brand:" + id;
        if (_cache.TryGetValue(key, out EmailTemplates.EmailBrand? cached) && cached != null)
            return cached;

        EmailTemplates.EmailBrand brand;
        try
        {
            var org = await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.Id == id)
                .Select(o => new { o.Name, o.BrandName, o.LogoUrl, o.PrimaryColor, o.WhitelabelEnabled })
                .FirstOrDefaultAsync(cancellationToken);

            if (org == null || !org.WhitelabelEnabled)
            {
                brand = EmailTemplates.EmailBrand.Platform;
            }
            else
            {
                var features = await _features.GetFeaturesAsync(id);
                brand = features.WhiteLabel
                    ? new EmailTemplates.EmailBrand(
                        Name: string.IsNullOrWhiteSpace(org.BrandName) ? org.Name : org.BrandName!,
                        // A colour from the database ends up inside an inline style attribute in an
                        // email body. Validated on write; re-checked here because a row predating
                        // that check must not be able to close the attribute.
                        Accent: IsHex(org.PrimaryColor) ? org.PrimaryColor! : EmailTemplates.EmailBrand.Platform.Accent,
                        LogoUrl: IsHttpUrl(org.LogoUrl) ? org.LogoUrl : null,
                        AttributionRemoved: features.RemoveAttribution)
                    : EmailTemplates.EmailBrand.Platform;
            }
        }
        catch (Exception ex)
        {
            // A branding lookup must never stop a password-reset email going out. Ours is the
            // right fallback: it is what every email said before this existed.
            _logger.LogWarning(ex, "Could not resolve the email brand for organization {OrganizationId}", id);
            brand = EmailTemplates.EmailBrand.Platform;
        }

        _cache.Set(key, brand, CacheFor);
        return brand;
    }

    private static bool IsHex(string? value)
        => value is { Length: >= 4 and <= 9 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    /// <summary>
    /// An absolute http(s) URL and nothing else — never a relative path, which a mail client
    /// cannot resolve, and never a "javascript:" or "data:" value.
    /// </summary>
    private static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
