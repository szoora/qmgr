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
}
