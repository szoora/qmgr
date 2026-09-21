using QMgr.Domain.Entities.Platform;

namespace QMgr.Application.Interfaces;

/// <summary>
/// Service for managing platform-wide settings stored in database
/// </summary>
public interface IPlatformSettingsService
{
    /// <summary>
    /// Get settings for a specific category
    /// </summary>
    Task<T?> GetSettingsAsync<T>(string category) where T : class;

    /// <summary>
    /// Update settings for a specific category
    /// </summary>
    Task<bool> UpdateSettingsAsync<T>(string category, T settings) where T : class;

    /// <summary>
    /// Get all platform settings
    /// </summary>
    Task<List<PlatformSetting>> GetAllSettingsAsync();

    /// <summary>
    /// Get a single platform setting by category
    /// </summary>
    Task<PlatformSetting?> GetSettingByCategoryAsync(string category);

    /// <summary>
    /// Initialize default settings if they don't exist
    /// </summary>
    Task InitializeDefaultSettingsAsync();

    /// <summary>
    /// Reload settings cache (if caching is implemented)
    /// </summary>
    Task ReloadCacheAsync();

    /// <summary>
    /// The public address every link a person or a gateway follows is built on — the ONE reader of it
    /// (2026-09-19). The address the server actually answers on (<c>MediaStorage:PublicBaseUrl</c>, then
    /// <c>App:PublicWebBaseUrl</c>) wins; the SaaS setting's <c>BaseUrl</c> and then <c>SaaS:BaseUrl</c>
    /// decide only where neither is configured. Never ends with a slash. See <c>PublicWebBase</c>.
    /// </summary>
    Task<string> GetPublicWebBaseUrlAsync();
}
