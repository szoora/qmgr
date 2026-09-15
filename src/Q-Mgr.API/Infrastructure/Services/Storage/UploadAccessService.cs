using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Services.Storage;

/// <summary>
/// Signed, expiring access tokens for gated uploads. See <see cref="IUploadAccessService"/>.
///
/// Uses the same time-limited data-protection primitive as the visitor badge QR codes
/// (<see cref="VisitorBadgeTokenService"/>): the payload is nothing but the file name, so a token
/// minted for one file cannot be replayed against another, and the key ring is the shared one both
/// processes persist to /var/lib/qmgr/dataprotection-keys in production.
/// </summary>
public class UploadAccessService : IUploadAccessService
{
    public const string RelativeFolder = "uploads/media";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeSpan _defaultLifetime;
    private readonly ILogger<UploadAccessService> _logger;

    public UploadAccessService(IDataProtectionProvider provider, IConfiguration configuration, ILogger<UploadAccessService> logger)
    {
        _protector = provider.CreateProtector("Uploads.Access.v1").ToTimeLimitedDataProtector();
        // One hour: long enough for a page that was opened and read, short enough that a link
        // pasted into a chat is dead by the time it is found again.
        var minutes = configuration.GetValue<int?>("MediaStorage:AccessTokenMinutes") ?? 60;
        _defaultLifetime = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60));
        _logger = logger;
    }

    public string? Sign(string? url, TimeSpan? lifetime = null)
    {
        var fileName = FileNameOf(url);
        if (fileName == null || url == null) return url;
        var q = url.IndexOf('?');
        if (q >= 0 && QueryHelpers.ParseQuery(url[q..]).ContainsKey(IUploadAccessService.TokenQueryKey)) return url;

        var token = _protector.Protect(fileName, DateTimeOffset.UtcNow.Add(lifetime ?? _defaultLifetime));
        return QueryHelpers.AddQueryString(url, IUploadAccessService.TokenQueryKey, token);
    }

    public bool IsValid(string fileName, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(fileName)) return false;
        try
        {
            var payload = _protector.Unprotect(token);
            return string.Equals(payload, fileName, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            // Expired, tampered, or signed with a since-rotated key — all one answer: no.
            _logger.LogDebug(ex, "Rejected an invalid or expired upload access token for {FileName}", fileName);
            return false;
        }
    }

    public string? Strip(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || FileNameOf(url) == null) return url;
        var q = url.IndexOf('?');
        if (q < 0) return url;

        var kept = QueryHelpers.ParseQuery(url[q..])
            .Where(kv => kv.Key != IUploadAccessService.TokenQueryKey)
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        var bare = url[..q];
        return kept.Count == 0 ? bare : QueryHelpers.AddQueryString(bare, kept!);
    }

    /// <summary>
    /// The stored file name when <paramref name="url"/> is one of this app's own upload links
    /// (absolute or relative), otherwise null.
    /// </summary>
    public static string? FileNameOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var path = url;
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        var marker = "/" + RelativeFolder + "/";
        var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var name = path[(at + marker.Length)..];
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\')) return null;
        return name;
    }
}
