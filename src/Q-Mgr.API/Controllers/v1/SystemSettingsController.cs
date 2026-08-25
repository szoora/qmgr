using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Backs the /admin/settings page (SystemSettings.razor). Was previously a complete no-op —
/// the page's Save button showed a fake success toast with zero HTTP calls, so every field
/// was decorative. Deliberately excludes display theme and SMS/Email/Push notification
/// toggles, which already have real, correctly-wired homes elsewhere (Organization.DisplayTheme
/// via BrandingSettings.razor; NotificationSettings via NotificationSettings.razor) — see
/// SystemSettingsDto's own doc comment for why those aren't duplicated here.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[Produces("application/json")]
public class SystemSettingsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ILogger<SystemSettingsController> _logger;

    public SystemSettingsController(QMgrDbContext context, ILogger<SystemSettingsController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet("branches/{branchId:guid}/system-settings")]
    [ProducesResponseType(typeof(SystemSettingsResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSystemSettings(Guid branchId)
    {
        var settings = await _context.BranchSettings
            .FirstOrDefaultAsync(s => s.BranchId == branchId);

        if (settings == null)
            return Ok(new SystemSettingsResponseDto());

        SystemSettingsDto dto;
        try
        {
            dto = string.IsNullOrEmpty(settings.SystemSettingsJson)
                ? new SystemSettingsDto()
                : JsonSerializer.Deserialize<SystemSettingsDto>(settings.SystemSettingsJson) ?? new SystemSettingsDto();
        }
        catch
        {
            dto = new SystemSettingsDto();
        }

        return Ok(new SystemSettingsResponseDto
        {
            Settings = dto,
            VoiceLanguage = settings.VoiceLanguage ?? "en-US"
        });
    }

    [HttpPut("branches/{branchId:guid}/system-settings")]
    [RequirePermission(Permissions.SettingsEdit)]
    [ProducesResponseType(typeof(SystemSettingsResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSystemSettings(Guid branchId, [FromBody] UpdateSystemSettingsRequest request)
    {
        var settings = await _context.BranchSettings
            .FirstOrDefaultAsync(s => s.BranchId == branchId);

        if (settings == null)
        {
            settings = new QMgr.Domain.Entities.Organization.BranchSettings
            {
                Id = Guid.NewGuid(),
                BranchId = branchId
            };
            _context.BranchSettings.Add(settings);
        }

        settings.SystemSettingsJson = JsonSerializer.Serialize(request.Settings);
        settings.VoiceLanguage = request.VoiceLanguage;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Updated system settings for branch {BranchId}", branchId);

        return Ok(new SystemSettingsResponseDto { Settings = request.Settings, VoiceLanguage = request.VoiceLanguage });
    }
}
