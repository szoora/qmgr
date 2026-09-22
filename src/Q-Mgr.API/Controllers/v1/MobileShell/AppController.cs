using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services.Mobile;

namespace QMgr.API.Controllers.v1.MobileShell;

/// <summary>
/// The mobile app's own endpoints: is there a newer build, what builds exist, and this device
/// reporting in.
///
/// <para><b>Update and releases are ANONYMOUS by design.</b> A device that must take a mandatory
/// security update may well be signed out — that is often exactly why it needs the update — so
/// gating it behind a session strands the handsets that most need it. Nothing here discloses tenant
/// data: it is a list of published builds of one app, identical for every school.</para>
///
/// <para><b>Check-in is authenticated</b>, because it carries a push token and attaches it to a
/// person.</para>
/// </summary>
[ApiController]
[Route("api/v1/app")]
[Produces("application/json")]
public class AppController : ControllerBase
{
    private readonly IAppDistributionService _distribution;
    private readonly IDeviceSessionService _devices;
    private readonly QMgrDbContext _db;
    private readonly ILogger<AppController> _log;

    public AppController(IAppDistributionService distribution, IDeviceSessionService devices,
                        QMgrDbContext db, ILogger<AppController> log)
    {
        _distribution = distribution;
        _devices = devices;
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Is there a newer build than the one asking?
    ///
    /// <para>The comparison is on the INTEGER <c>versionCode</c>, and <c>mandatory</c> is sticky
    /// across every release between the device's build and the newest — both rules live in
    /// <see cref="IAppDistributionService"/> so there is one place they can be got wrong.</para>
    /// </summary>
    [HttpGet("update")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(MobileEnvelope<AppUpdateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromQuery] string platform = "android",
                                            [FromQuery] long versionCode = 0,
                                            CancellationToken ct = default)
    {
        var result = await _distribution.CheckAsync(platform, versionCode, ct);
        return Ok(MobileEnvelope<AppUpdateResponse>.Ok(result));
    }

    /// <summary>
    /// Every published build, newest first.
    ///
    /// <para>Separate from <see cref="Update"/> because the two answer different questions for
    /// different callers: the app polls update and only ever wants the newest, while a person on the
    /// download page occasionally needs an EARLIER build — a device that cannot run the current
    /// release, or a site holding back while a regression is looked at.</para>
    /// </summary>
    [HttpGet("releases")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(MobileEnvelope<AppReleasesResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Releases([FromQuery] string platform = "android",
                                              CancellationToken ct = default)
    {
        var result = await _distribution.ReleasesAsync(platform, ct);
        return Ok(MobileEnvelope<AppReleasesResponse>.Ok(result));
    }

    /// <summary>
    /// This device reporting what it is and, when it has one, its push token.
    ///
    /// <para><b>It creates nothing.</b> A check-in from a device with no live session is answered
    /// honestly rather than used to mint one — the session is created by signing in, and only
    /// there. So a handset whose session was revoked cannot quietly re-register itself for
    /// notifications.</para>
    /// </summary>
    [HttpPost("checkin")]
    [Authorize]
    [ProducesResponseType(typeof(MobileEnvelope<DeviceCheckInResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckIn([FromBody] DeviceCheckInRequest request, CancellationToken ct = default)
    {
        var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(raw, out var userId)) return Unauthorized();

        var session = await _devices.CheckInAsync(userId, request, ct);

        // The bell's count, so it is right before the notification list has loaded. Cheap, and it
        // saves the app a second round trip on every resume.
        var unread = await _db.Notifications
            .IgnoreQueryFilters()
            .CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);

        return Ok(MobileEnvelope<DeviceCheckInResponse>.Ok(new DeviceCheckInResponse
        {
            // Server-controlled on purpose: how often a fleet phones home is an estate decision,
            // and changing it should not need an app release.
            NextCheckInSeconds = 21600,
            UnreadCount = unread,
            PushRegistered = session?.CanReceivePush ?? false
        }));
    }
}
