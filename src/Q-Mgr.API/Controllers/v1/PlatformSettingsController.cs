using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.API.Application.Services;
using QMgr.API.Authorization;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Platform;
using System.Text.Json;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/platform/settings")]
[Produces("application/json")]
[Authorize]
public class PlatformSettingsController : ControllerBase
{
    private readonly IPlatformSettingsService _settingsService;
    private readonly IPasswordValidationService _passwordValidationService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlatformSettingsController> _logger;

    // Stable synthetic id for the Security category card — it has no PlatformSetting DB row
    // (see BuildSecurityDtoAsync), so the Id can't come from one.
    private static readonly Guid SecurityCategoryId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public PlatformSettingsController(
        IPlatformSettingsService settingsService,
        IPasswordValidationService passwordValidationService,
        IConfiguration configuration,
        ILogger<PlatformSettingsController> logger)
    {
        _settingsService = settingsService;
        _passwordValidationService = passwordValidationService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Get all platform settings (SuperAdmin only)
    /// </summary>
    [HttpGet]
    [RequirePermission("platform.settings.view")]
    [ProducesResponseType(typeof(List<PlatformSettingDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllSettings()
    {
        var settings = await _settingsService.GetAllSettingsAsync();

        var dtos = settings.Select(s => new PlatformSettingDto
        {
            Id = s.Id,
            Category = s.Category,
            DisplayName = s.DisplayName,
            Description = s.Description,
            SettingsJson = s.SettingsJson,
            IsEnabled = s.IsEnabled,
            IsEditable = s.IsEditable,
            DisplayOrder = s.DisplayOrder,
            Icon = s.Icon,
            UpdatedAt = s.UpdatedAt
        }).ToList();

        dtos.Add(await BuildSecurityDtoAsync());

        return Ok(dtos.OrderBy(d => d.DisplayOrder).ToList());
    }

    /// <summary>
    /// Get settings by category
    /// </summary>
    [HttpGet("{category}")]
    [RequirePermission("platform.settings.view")]
    [ProducesResponseType(typeof(PlatformSettingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSettingByCategory(string category)
    {
        if (category == "Security")
        {
            return Ok(await BuildSecurityDtoAsync());
        }

        var setting = await _settingsService.GetSettingByCategoryAsync(category);

        if (setting == null)
        {
            return NotFound(new { message = $"Setting category '{category}' not found" });
        }

        var dto = new PlatformSettingDto
        {
            Id = setting.Id,
            Category = setting.Category,
            DisplayName = setting.DisplayName,
            Description = setting.Description,
            SettingsJson = setting.SettingsJson,
            IsEnabled = setting.IsEnabled,
            IsEditable = setting.IsEditable,
            DisplayOrder = setting.DisplayOrder,
            Icon = setting.Icon,
            UpdatedAt = setting.UpdatedAt
        };

        return Ok(dto);
    }

    /// <summary>
    /// Update settings for a category
    /// </summary>
    [HttpPut("{category}")]
    [RequirePermission("platform.settings.edit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSettings(string category, [FromBody] UpdateSettingsRequest request)
    {
        // Validate JSON
        try
        {
            JsonDocument.Parse(request.SettingsJson);
        }
        catch (JsonException)
        {
            return BadRequest(new { message = "Invalid JSON format" });
        }

        // Security is backed by PlatformConfiguration (via IPasswordValidationService), the one
        // canonical, actually-enforced security system — not a PlatformSetting DB row — so it
        // can't go through the generic row-lookup/UpdateTypedSettings path below.
        if (category == "Security")
        {
            return await UpdateSecuritySettingsAsync(request.SettingsJson);
        }

        // Get existing setting
        var setting = await _settingsService.GetSettingByCategoryAsync(category);
        if (setting == null)
        {
            return NotFound(new { message = $"Setting category '{category}' not found" });
        }

        if (!setting.IsEditable)
        {
            return BadRequest(new { message = "This setting is not editable" });
        }

        // Update based on category (with validation)
        bool success = category switch
        {
            "JWT" => await UpdateJwtSettingsAsync(request.SettingsJson),
            "CORS" => await UpdateTypedSettings<CorsSettings>(category, request.SettingsJson),
            "RateLimiting" => await UpdateTypedSettings<RateLimitSettings>(category, request.SettingsJson),
            "SaaS" => await UpdateTypedSettings<SaasSettings>(category, request.SettingsJson),
            "Stripe" => await UpdateTypedSettings<StripeSettings>(category, request.SettingsJson),
            "MobileMoney" => await UpdateTypedSettings<MobileMoneySettings>(category, request.SettingsJson),
            "Ads" => await UpdateTypedSettings<AdsSettings>(category, request.SettingsJson),
            "Email" => await UpdateTypedSettings<EmailSettings>(category, request.SettingsJson),
            _ => false
        };

        if (!success)
        {
            return BadRequest(new { message = "Failed to update settings" });
        }

        _logger.LogInformation("Platform settings updated: {Category} by user {UserId}",
            category, User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);

        return Ok(new { message = "Settings updated successfully" });
    }

    /// <summary>
    /// JWT: only the token lifetime is a genuine runtime setting (AuthController reads it per
    /// login). The signing secret, issuer and audience are bound into bearer validation at startup
    /// from server configuration (JWT__Secret / JWT:Issuer / JWT:Audience), so whatever the UI
    /// posts for those is discarded here rather than persisted as a misleading, never-read copy —
    /// and in particular a secret is never written to the database.
    /// </summary>
    private async Task<bool> UpdateJwtSettingsAsync(string json)
    {
        JwtSettings? posted;
        try
        {
            posted = JsonSerializer.Deserialize<JwtSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return false;
        }

        if (posted == null || posted.ExpiryMinutes < 5 || posted.ExpiryMinutes > 24 * 60)
            return false;

        var sanitized = new JwtSettings
        {
            Secret = string.Empty,
            Issuer = _configuration["JWT:Issuer"] ?? string.Empty,
            Audience = _configuration["JWT:Audience"] ?? string.Empty,
            ExpiryMinutes = posted.ExpiryMinutes
        };
        return await _settingsService.UpdateSettingsAsync("JWT", sanitized);
    }

    /// <summary>
    /// Builds the Security category card from the canonical, enforced settings
    /// (PlatformConfiguration "Security" via IPasswordValidationService), in the same flat JSON
    /// shape the admin UI already edits (MaxLoginAttempts, LockoutDurationMinutes, etc.).
    /// </summary>
    private async Task<PlatformSettingDto> BuildSecurityDtoAsync()
    {
        var security = await _passwordValidationService.GetSecuritySettingsAsync();

        var flat = new SecurityFlatDto
        {
            MaxLoginAttempts = security.PasswordPolicy.MaxFailedAttempts,
            LockoutDurationMinutes = security.PasswordPolicy.LockoutDurationMinutes,
            PasswordMinLength = security.PasswordPolicy.MinimumLength,
            RequireUppercase = security.PasswordPolicy.RequireUppercase,
            RequireDigit = security.PasswordPolicy.RequireDigits,
            RequireSpecialChar = security.PasswordPolicy.RequireSpecialCharacters,
            SessionTimeoutMinutes = security.Session.IdleTimeoutMinutes
        };

        return new PlatformSettingDto
        {
            Id = SecurityCategoryId,
            Category = "Security",
            DisplayName = "Security Settings",
            Description = "Platform security and authentication rules — password policy and account lockout are actively enforced at login.",
            SettingsJson = JsonSerializer.Serialize(flat, new JsonSerializerOptions { WriteIndented = true }),
            IsEnabled = true,
            IsEditable = true,
            DisplayOrder = 9,
            Icon = "shield-check",
            UpdatedAt = null
        };
    }

    private async Task<IActionResult> UpdateSecuritySettingsAsync(string settingsJson)
    {
        SecurityFlatDto? flat;
        try
        {
            flat = JsonSerializer.Deserialize<SecurityFlatDto>(settingsJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error deserializing Security settings");
            return BadRequest(new { message = "Failed to update settings" });
        }

        if (flat == null)
        {
            return BadRequest(new { message = "Failed to update settings" });
        }

        if (flat.MaxLoginAttempts < 3)
        {
            return BadRequest(new { message = "Max login attempts must be at least 3." });
        }

        if (flat.PasswordMinLength < 6)
        {
            return BadRequest(new { message = "Minimum password length cannot be less than 6 characters." });
        }

        // Merge into the existing canonical settings so fields this flat form doesn't expose
        // (password history/expiry, MFA, other session settings) keep their current values.
        var current = await _passwordValidationService.GetSecuritySettingsAsync();
        current.PasswordPolicy.MaxFailedAttempts = flat.MaxLoginAttempts;
        current.PasswordPolicy.LockoutDurationMinutes = flat.LockoutDurationMinutes;
        current.PasswordPolicy.MinimumLength = flat.PasswordMinLength;
        current.PasswordPolicy.RequireUppercase = flat.RequireUppercase;
        current.PasswordPolicy.RequireDigits = flat.RequireDigit;
        current.PasswordPolicy.RequireSpecialCharacters = flat.RequireSpecialChar;
        current.Session.IdleTimeoutMinutes = flat.SessionTimeoutMinutes;

        var userId = GetCurrentUserId() ?? Guid.Empty;
        await _passwordValidationService.UpdateSecuritySettingsAsync(current, userId);

        _logger.LogInformation("Platform settings updated: Security by user {UserId}", userId);

        return Ok(new { message = "Settings updated successfully" });
    }

    private Guid? GetCurrentUserId()
    {
        // Same "sub" claim-mapping fix as SecurityPolicyController.GetCurrentUserId.
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst("userId")?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    /// <summary>
    /// Reload settings cache
    /// </summary>
    [HttpPost("reload-cache")]
    [RequirePermission("platform.settings.edit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReloadCache()
    {
        await _settingsService.ReloadCacheAsync();

        _logger.LogInformation("Platform settings cache reloaded by user {UserId}",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);

        return Ok(new { message = "Cache reloaded successfully" });
    }

    private async Task<bool> UpdateTypedSettings<T>(string category, string settingsJson) where T : class
    {
        try
        {
            var settings = JsonSerializer.Deserialize<T>(settingsJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (settings == null)
            {
                return false;
            }

            return await _settingsService.UpdateSettingsAsync(category, settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deserializing settings for category: {Category}", category);
            return false;
        }
    }
}

public record PlatformSettingDto
{
    public Guid Id { get; init; }
    public string Category { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string SettingsJson { get; init; } = "{}";
    public bool IsEnabled { get; init; }
    public bool IsEditable { get; init; }
    public int DisplayOrder { get; init; }
    public string? Icon { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public record UpdateSettingsRequest
{
    public string SettingsJson { get; init; } = string.Empty;
}

/// <summary>
/// Flat shape the Security admin-UI tab edits — matches the Web project's local
/// SecuritySettingsModel field-for-field. Mapped to/from the canonical, nested
/// QMgr.API.Domain.Entities.SecuritySettings in PlatformSettingsController.
/// </summary>
public class SecurityFlatDto
{
    public int MaxLoginAttempts { get; set; } = 5;
    public int LockoutDurationMinutes { get; set; } = 15;
    public int PasswordMinLength { get; set; } = 8;
    public bool RequireUppercase { get; set; } = true;
    public bool RequireDigit { get; set; } = true;
    public bool RequireSpecialChar { get; set; } = false;
    public int SessionTimeoutMinutes { get; set; } = 60;
}
