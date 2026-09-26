using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The signed-in person's own view choices (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E7/E8, 2026-09-26). No permission
/// code: a person's own preferences are always theirs, the ProfileController rule. The answer carries the school's quiet
/// hours and the branch's time zone, so the browser can decide whether to chime without a second call.
/// </summary>
[ApiController]
[Route("api/v1/profile/ui-preferences")]
[Authorize]
[Produces("application/json")]
public class UiPreferencesController : ControllerBase
{
    private readonly QMgrDbContext _db;
    private readonly IUserPreferencesService _preferences;
    private readonly IStaffPerformancePolicyService _policy;

    public UiPreferencesController(QMgrDbContext db, IUserPreferencesService preferences, IStaffPerformancePolicyService policy)
    {
        _db = db;
        _preferences = preferences;
        _policy = policy;
    }

    [HttpGet]
    [ProducesResponseType(typeof(UserUiPreferencesEnvelopeDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get([FromQuery] Guid? branchId = null)
    {
        if (CurrentUserId() is not { } me) return Unauthorized();
        return Ok(await EnvelopeAsync(me, await _preferences.GetAsync(me), branchId));
    }

    [HttpPut]
    [ProducesResponseType(typeof(UserUiPreferencesEnvelopeDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Put([FromBody] UserUiPreferencesDto request, [FromQuery] Guid? branchId = null)
    {
        if (CurrentUserId() is not { } me) return Unauthorized();
        var saved = await _preferences.SaveAsync(me, request ?? new UserUiPreferencesDto());
        return Ok(await EnvelopeAsync(me, saved, branchId));
    }

    private async Task<UserUiPreferencesEnvelopeDto> EnvelopeAsync(Guid me, UserUiPreferencesDto preferences, Guid? branchId)
    {
        var user = await _db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == me)
            .Select(u => new { u.OrganizationId, u.AssignedBranchId }).FirstOrDefaultAsync();
        if (user == null) return new UserUiPreferencesEnvelopeDto { Preferences = preferences };

        var wanted = branchId ?? user.AssignedBranchId;
        var tz = await _db.Branches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.OrganizationId == user.OrganizationId && b.IsActive)
            .OrderBy(b => b.Id == wanted ? 0 : 1).ThenBy(b => b.CreatedAt)
            .Select(b => b.Timezone).FirstOrDefaultAsync();
        var policy = await _policy.GetAsync(user.OrganizationId);
        return new UserUiPreferencesEnvelopeDto { Preferences = preferences, QuietHours = policy.QuietHours, TimeZone = tz };
    }

    private Guid? CurrentUserId()
        => Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out var id) ? id : null;
}
