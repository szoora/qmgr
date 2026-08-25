using Microsoft.EntityFrameworkCore;
using QMgr.API.Domain.Entities;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

public interface IPlatformConfigurationService
{
    Task<T> GetSettingsAsync<T>(string category) where T : class, new();
    Task UpdateSettingsAsync<T>(string category, T settings, Guid updatedBy) where T : class;
    Task<PlatformConfiguration> GetConfigurationAsync(string category);
}

public class PlatformConfigurationService : IPlatformConfigurationService
{
    private readonly QMgrDbContext _dbContext;
    private readonly ILogger<PlatformConfigurationService> _logger;

    public PlatformConfigurationService(
        QMgrDbContext dbContext,
        ILogger<PlatformConfigurationService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<T> GetSettingsAsync<T>(string category) where T : class, new()
    {
        var config = await _dbContext.PlatformConfigurations
            .FirstOrDefaultAsync(c => c.Category == category && c.IsActive);

        if (config == null)
        {
            // Create default configuration
            var defaultSettings = new T();
            config = new PlatformConfiguration
            {
                Category = category,
                IsActive = true
            };
            config.SetSettings(defaultSettings);

            _dbContext.PlatformConfigurations.Add(config);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Created default configuration for category: {Category}", category);
            return defaultSettings;
        }

        return config.GetSettings<T>();
    }

    public async Task UpdateSettingsAsync<T>(string category, T settings, Guid updatedBy) where T : class
    {
        var config = await _dbContext.PlatformConfigurations
            .FirstOrDefaultAsync(c => c.Category == category);

        if (config == null)
        {
            config = new PlatformConfiguration
            {
                Category = category,
                IsActive = true
            };
            config.SetSettings(settings);
            config.UpdatedBy = updatedBy;
            _dbContext.PlatformConfigurations.Add(config);
        }
        else
        {
            config.SetSettings(settings);
            config.UpdatedAt = DateTime.UtcNow;
            config.UpdatedBy = updatedBy;
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Updated configuration for category: {Category} by user: {UserId}",
            category, updatedBy);
    }

    public async Task<PlatformConfiguration> GetConfigurationAsync(string category)
    {
        var config = await _dbContext.PlatformConfigurations
            .FirstOrDefaultAsync(c => c.Category == category && c.IsActive);

        if (config == null)
        {
            config = new PlatformConfiguration
            {
                Category = category,
                IsActive = true,
                SettingsJson = "{}"
            };
        }

        return config;
    }
}
