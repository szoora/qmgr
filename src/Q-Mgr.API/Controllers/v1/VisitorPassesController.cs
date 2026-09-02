using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Filters;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Group visitor passes (a single QR badge admitting up to N people together) and the badge
/// scanner endpoint shared by both individual visit badges and group passes.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.VisitorSafeguarding)]
public class VisitorPassesController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IVisitorBadgeTokenService _badgeTokenService;
    private readonly IVisitorActivityBroadcaster _activityBroadcaster;

    public VisitorPassesController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        IVisitorBadgeTokenService badgeTokenService,
        IVisitorActivityBroadcaster activityBroadcaster)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _badgeTokenService = badgeTokenService;
        _activityBroadcaster = activityBroadcaster;
    }

    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails { Title = "Tenant not resolved", Status = StatusCodes.Status401Unauthorized });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
        {
            var superAdminBranchExists = await _context.Branches.AnyAsync(b => b.Id == branchId);
            return superAdminBranchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
        }

        var branchExists = await _context.Branches.AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);
        return branchExists ? null : NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
    }

    private async Task<Guid> ResolveOrganizationIdAsync(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext!;
        return RoleCodes.IsSuperAdmin(tenantContext.UserRole)
            ? (await _context.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync())
            : tenantContext.OrganizationId;
    }

    private Guid? CurrentUserId()
    {
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : null;
    }

    private static VisitorPassDto MapToDto(VisitorPass p, string? qrToken = null) => new()
    {
        Id = p.Id,
        BranchId = p.BranchId,
        Label = p.Label,
        MaxVisitors = p.MaxVisitors,
        CurrentVisitors = p.CurrentVisitors,
        ExpiresAt = p.ExpiresAt,
        RevokedAt = p.RevokedAt,
        CreatedAt = p.CreatedAt,
        QrToken = qrToken
    };

    [HttpGet("branches/{branchId:guid}/visitor-passes")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(List<VisitorPassDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPasses(Guid branchId, [FromQuery] bool activeOnly = true)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.VisitorPasses.Where(p => p.BranchId == branchId);
        if (activeOnly)
        {
            var now = DateTime.UtcNow;
            query = query.Where(p => p.RevokedAt == null && p.ExpiresAt > now);
        }

        var passes = await query.OrderByDescending(p => p.CreatedAt).ToListAsync();
        return Ok(passes.Select(p => MapToDto(p)).ToList());
    }

    /// <summary>
    /// Issues a new group pass — MaxVisitors caps how many people can be admitted under it at
    /// once (they can arrive/leave staggered; the cap is enforced live, not just at issuance).
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitor-passes")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(VisitorPassDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePass(Guid branchId, [FromBody] CreateVisitorPassRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.Label))
            return BadRequest(new ProblemDetails { Title = "A label is required (e.g. which group this pass is for)", Status = StatusCodes.Status400BadRequest });
        if (request.MaxVisitors < 1 || request.MaxVisitors > 200)
            return BadRequest(new ProblemDetails { Title = "MaxVisitors must be between 1 and 200", Status = StatusCodes.Status400BadRequest });
        if (request.ValidHours < 1 || request.ValidHours > 168)
            return BadRequest(new ProblemDetails { Title = "ValidHours must be between 1 and 168 (one week)", Status = StatusCodes.Status400BadRequest });

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var expiresAt = DateTime.UtcNow.AddHours(request.ValidHours);

        var pass = new VisitorPass
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            Label = request.Label,
            MaxVisitors = request.MaxVisitors,
            ExpiresAt = expiresAt,
            CreatedByUserId = CurrentUserId() ?? Guid.Empty
        };
        // TokenId isn't used for lookup validation (the signed token is self-describing), but is
        // kept as an audit trail of what was actually issued.
        pass.TokenId = Guid.NewGuid().ToString("N");

        _context.VisitorPasses.Add(pass);
        await _context.SaveChangesAsync();

        var qrToken = _badgeTokenService.IssuePassToken(pass.Id, branchId, expiresAt);
        return CreatedAtAction(nameof(GetPasses), new { branchId }, MapToDto(pass, qrToken));
    }

    [HttpPost("branches/{branchId:guid}/visitor-passes/{passId:guid}/revoke")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(VisitorPassDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokePass(Guid branchId, Guid passId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var pass = await _context.VisitorPasses.FirstOrDefaultAsync(p => p.Id == passId && p.BranchId == branchId);
        if (pass == null) return NotFound();

        pass.RevokedAt = DateTime.UtcNow;
        pass.RevokedByUserId = CurrentUserId();
        pass.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(MapToDto(pass));
    }

    /// <summary>
    /// The single scan endpoint for the front-desk/kiosk badge reader. Never trusts the token's
    /// own claim of validity — every scan re-checks the referenced Visit/Pass's live DB state
    /// (status, expiry, revocation), so a photographed or previously-used QR stops working the
    /// moment the underlying record does, independent of the token's own signed expiry.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitors/scan")]
    [RequirePermission(Permissions.VisitorsCheckIn)]
    [ProducesResponseType(typeof(VisitorScanResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Scan(Guid branchId, [FromBody] VisitorScanRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var payload = _badgeTokenService.TryDecode(request.Token);
        if (payload == null)
            return BadRequest(new ProblemDetails { Title = "Invalid or expired badge", Detail = "This QR code isn't recognized or has expired.", Status = StatusCodes.Status400BadRequest });

        if (payload.BranchId != branchId)
            return BadRequest(new ProblemDetails { Title = "Wrong branch", Detail = "This badge was issued for a different branch.", Status = StatusCodes.Status400BadRequest });

        return payload.Kind == VisitorBadgeTokenKind.Visit
            ? await ScanVisitBadge(branchId, payload.Id)
            : await ScanPassBadge(branchId, payload.Id, request.Direction);
    }

    private async Task<IActionResult> ScanVisitBadge(Guid branchId, Guid visitorId)
    {
        var visitor = await _context.Visitors.Include(v => v.VisitorProfile)
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId && v.DeletedAt == null);
        if (visitor == null) return NotFound(new ProblemDetails { Title = "Visit record not found", Status = StatusCodes.Status404NotFound });

        var profile = visitor.VisitorProfile!;

        // Checked ahead of and independently of Status: Status already blocks a normal second
        // scan (see below), but BadgeConsumedAt can never be reset by anything else that later
        // touches Status, so a photographed/shared QR stays dead for good, not just "until
        // Status happens to change back."
        if (visitor.BadgeConsumedAt.HasValue)
            return BadRequest(new ProblemDetails
            {
                Title = "Badge already used",
                Detail = $"This badge was already scanned at {visitor.BadgeConsumedAt.Value:HH:mm} — it's single-use and can't be scanned again.",
                Status = StatusCodes.Status400BadRequest
            });

        if (visitor.Status != VisitorStatus.CheckedIn)
            return BadRequest(new ProblemDetails
            {
                Title = "Already checked out",
                Detail = $"{profile.FullName}'s badge was already used to check out. Start a new visit at the desk to check them in again.",
                Status = StatusCodes.Status400BadRequest
            });

        visitor.Status = VisitorStatus.CheckedOut;
        visitor.CheckedOutAt = DateTime.UtcNow;
        visitor.BadgeConsumedAt = DateTime.UtcNow;
        visitor.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var dto = VisitorsController.MapToDto(visitor, profile);
        await _activityBroadcaster.BroadcastAsync(branchId, VisitorActivityKind.CheckedOut, dto);

        return Ok(new VisitorScanResultDto
        {
            Action = VisitorScanAction.CheckedOut,
            Message = $"{profile.FullName} checked out",
            Visitor = dto,
            IsWatchlisted = profile.IsWatchlisted
        });
    }

    /// <summary>
    /// CONCURRENCY: same pg_advisory_xact_lock pattern as VisitorsController.GenerateBadgeCodeAsync —
    /// without it, two simultaneous "in" scans of the same pass near capacity could both read
    /// CurrentVisitors below MaxVisitors and both increment, pushing the pass over its cap.
    /// </summary>
    private async Task<IActionResult> ScanPassBadge(Guid branchId, Guid passId, string? direction)
    {
        if (direction != "in" && direction != "out")
            return BadRequest(new ProblemDetails
            {
                Title = "Direction required",
                Detail = "A group pass can't tell arrival from departure on its own — specify \"in\" or \"out\" at the scanner.",
                Status = StatusCodes.Status400BadRequest
            });

        VisitorPass? pass = null;
        IActionResult? error = null;
        var lockKey = $"visitor-pass:{passId}";

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");

            pass = await _context.VisitorPasses.FirstOrDefaultAsync(p => p.Id == passId && p.BranchId == branchId);
            if (pass == null)
            {
                error = NotFound(new ProblemDetails { Title = "Pass not found", Status = StatusCodes.Status404NotFound });
                await transaction.RollbackAsync();
                return;
            }

            if (pass.RevokedAt != null)
            {
                error = BadRequest(new ProblemDetails { Title = "Pass revoked", Status = StatusCodes.Status400BadRequest });
                await transaction.RollbackAsync();
                return;
            }
            if (pass.ExpiresAt <= DateTime.UtcNow)
            {
                error = BadRequest(new ProblemDetails { Title = "Pass expired", Status = StatusCodes.Status400BadRequest });
                await transaction.RollbackAsync();
                return;
            }

            if (direction == "in")
            {
                if (pass.CurrentVisitors >= pass.MaxVisitors)
                {
                    error = BadRequest(new ProblemDetails
                    {
                        Title = "Pass at capacity",
                        Detail = $"This pass admits at most {pass.MaxVisitors} at once — all slots are currently in use.",
                        Status = StatusCodes.Status400BadRequest
                    });
                    await transaction.RollbackAsync();
                    return;
                }
                pass.CurrentVisitors++;
            }
            else
            {
                if (pass.CurrentVisitors <= 0)
                {
                    error = BadRequest(new ProblemDetails { Title = "No one checked in under this pass", Status = StatusCodes.Status400BadRequest });
                    await transaction.RollbackAsync();
                    return;
                }
                pass.CurrentVisitors--;
            }

            pass.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        });

        if (error != null) return error;

        return Ok(new VisitorScanResultDto
        {
            Action = direction == "in" ? VisitorScanAction.CheckedIn : VisitorScanAction.CheckedOut,
            Message = $"{pass!.Label}: {pass.CurrentVisitors}/{pass.MaxVisitors} currently on site",
            Pass = MapToDto(pass),
            IsWatchlisted = false
        });
    }
}
