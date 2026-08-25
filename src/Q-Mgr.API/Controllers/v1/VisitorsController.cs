using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — every action already has its own [RequirePermission]
public class VisitorsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly INotificationService _notificationService;
    private readonly ILogger<VisitorsController> _logger;

    public VisitorsController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        INotificationService notificationService,
        ILogger<VisitorsController> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>
    /// Visitor (like Counter/Token/Feedback) has no global EF query filter — it's branch-scoped,
    /// not directly org-scoped — so every action reaching one by branchId must verify ownership
    /// explicitly. Visitor rows carry real PII (name, phone, email, ID number), so this is a
    /// genuine data-exposure boundary. SuperAdmin bypass matches every other VerifyBranchOwnership
    /// in this codebase.
    /// </summary>
    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
            return null;

        var branchExists = await _context.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);

        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        return null;
    }

    /// <summary>
    /// CONCURRENCY: mirrors TokenRepository.GetNextTokenNumberAsync — a plain read-then-increment
    /// of "today's last badge number" would let two front-desk check-ins issued at the same instant
    /// compute the same badge code. pg_advisory_xact_lock, keyed per branch+day, serializes
    /// concurrent callers for the lifetime of the enclosing transaction.
    /// </summary>
    private async Task<string> GenerateBadgeCodeAsync(Guid branchId)
    {
        var today = DateTime.UtcNow.Date;
        var lockKey = $"visitor-badge:{branchId}:{today:yyyyMMdd}";
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");

        var countToday = await _context.Visitors
            .CountAsync(v => v.BranchId == branchId && v.CreatedAt >= today);

        return $"V-{today:yyyyMMdd}-{(countToday + 1):D4}";
    }

    private static VisitorDto MapToDto(Visitor v) => new()
    {
        Id = v.Id,
        BranchId = v.BranchId,
        BadgeCode = v.BadgeCode,
        FullName = v.FullName,
        Phone = v.Phone,
        Email = v.Email,
        Company = v.Company,
        IdNumber = v.IdNumber,
        PhotoUrl = v.PhotoUrl,
        Purpose = v.Purpose,
        HostUserId = v.HostUserId,
        HostName = v.HostName,
        Status = v.Status,
        IsWatchlisted = v.IsWatchlisted,
        WatchlistReason = v.WatchlistReason,
        ScheduledAt = v.ScheduledAt,
        CheckedInAt = v.CheckedInAt,
        CheckedOutAt = v.CheckedOutAt,
        CreatedAt = v.CreatedAt,
        Notes = v.Notes
    };

    private async Task NotifyHostAsync(Visitor visitor, Guid organizationId, Guid branchId)
    {
        if (visitor.HostUserId is not { } hostUserId) return;

        try
        {
            await _notificationService.CreateInAppNotificationAsync(new CreateNotificationRequest
            {
                UserId = hostUserId,
                BranchId = branchId,
                OrganizationId = organizationId,
                Title = "Visitor arrived",
                Message = $"{visitor.FullName} has checked in to see you ({visitor.Purpose}).",
                Type = NotificationType.VisitorArrived,
                Priority = NotificationPriority.High
            });
        }
        catch (Exception ex)
        {
            // Host notification failing shouldn't fail the check-in itself — the visitor is
            // still on the log and can be found by front desk staff.
            _logger.LogError(ex, "Failed to notify host {HostUserId} of visitor {VisitorId} arrival", hostUserId, visitor.Id);
        }
    }

    /// <summary>
    /// Lists visitors for a branch, optionally filtered by status. Defaults to today's visitors.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/visitors")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(List<VisitorDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVisitors(
        Guid branchId,
        [FromQuery] VisitorStatus? status = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] bool watchlistOnly = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.Visitors.Where(v => v.BranchId == branchId);

        if (status.HasValue)
            query = query.Where(v => v.Status == status.Value);

        query = query.Where(v => v.CreatedAt >= (fromDate ?? DateTime.UtcNow.Date));

        if (watchlistOnly)
            query = query.Where(v => v.IsWatchlisted);

        var visitors = await query
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => v)
            .ToListAsync();

        return Ok(visitors.Select(MapToDto).ToList());
    }

    [HttpGet("branches/{branchId:guid}/visitors/summary")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(VisitorSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var today = DateTime.UtcNow.Date;
        var todaysVisitors = await _context.Visitors
            .Where(v => v.BranchId == branchId && v.CreatedAt >= today)
            .ToListAsync();

        return Ok(new VisitorSummaryDto
        {
            CurrentlyOnSite = todaysVisitors.Count(v => v.Status == VisitorStatus.CheckedIn),
            TotalToday = todaysVisitors.Count,
            PreRegisteredUpcoming = todaysVisitors.Count(v => v.Status == VisitorStatus.PreRegistered),
            WatchlistedOnSite = todaysVisitors.Count(v => v.Status == VisitorStatus.CheckedIn && v.IsWatchlisted)
        });
    }

    [HttpGet("branches/{branchId:guid}/visitors/{visitorId:guid}")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVisitor(Guid branchId, Guid visitorId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var visitor = await _context.Visitors
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId);

        if (visitor == null) return NotFound();
        return Ok(MapToDto(visitor));
    }

    /// <summary>
    /// Pre-register a visitor ahead of their arrival (Status = PreRegistered).
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitors/pre-register")]
    [RequirePermission(Permissions.VisitorsCheckIn)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> PreRegister(Guid branchId, [FromBody] PreRegisterVisitorRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.FullName))
            return BadRequest(new ProblemDetails { Title = "Full name is required", Status = StatusCodes.Status400BadRequest });

        var tenantContext = _tenantAccessor.TenantContext!;
        var organizationId = RoleCodes.IsSuperAdmin(tenantContext.UserRole)
            ? (await _context.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync())
            : tenantContext.OrganizationId;

        Visitor visitor = null!;
        // Npgsql's retrying execution strategy (EnableRetryOnFailure) doesn't allow a raw
        // BeginTransactionAsync — the whole retriable unit must go through
        // CreateExecutionStrategy().ExecuteAsync, same requirement as IUnitOfWork.ExecuteInTransactionAsync
        // uses elsewhere in this codebase (CreateTokenCommandHandler etc.).
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();

            var badgeCode = await GenerateBadgeCodeAsync(branchId);
            visitor = new Visitor
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                BadgeCode = badgeCode,
                FullName = request.FullName,
                Phone = request.Phone,
                Email = request.Email,
                Company = request.Company,
                Purpose = request.Purpose,
                HostUserId = request.HostUserId,
                HostName = request.HostName,
                Status = VisitorStatus.PreRegistered,
                ScheduledAt = request.ScheduledAt,
                Notes = request.Notes
            };
            _context.Visitors.Add(visitor);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        });

        return CreatedAtAction(nameof(GetVisitor), new { branchId, visitorId = visitor.Id }, MapToDto(visitor));
    }

    /// <summary>
    /// Walk-in check-in: creates and checks in a visitor in one step.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitors/checkin")]
    [RequirePermission(Permissions.VisitorsCheckIn)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CheckIn(Guid branchId, [FromBody] CheckInVisitorRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.FullName))
            return BadRequest(new ProblemDetails { Title = "Full name is required", Status = StatusCodes.Status400BadRequest });

        var tenantContext = _tenantAccessor.TenantContext!;
        var organizationId = RoleCodes.IsSuperAdmin(tenantContext.UserRole)
            ? (await _context.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync())
            : tenantContext.OrganizationId;

        // Flag on arrival, not just for staff to notice later — check name+ID against anyone
        // already on this branch's watchlist.
        var isWatchlisted = !string.IsNullOrWhiteSpace(request.IdNumber) && await _context.Visitors
            .AnyAsync(v => v.BranchId == branchId && v.IsWatchlisted &&
                           v.IdNumber == request.IdNumber);

        Visitor visitor = null!;
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();

            var badgeCode = await GenerateBadgeCodeAsync(branchId);
            visitor = new Visitor
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                BadgeCode = badgeCode,
                FullName = request.FullName,
                Phone = request.Phone,
                Email = request.Email,
                Company = request.Company,
                IdNumber = request.IdNumber,
                PhotoUrl = request.PhotoUrl,
                Purpose = request.Purpose,
                HostUserId = request.HostUserId,
                HostName = request.HostName,
                Status = VisitorStatus.CheckedIn,
                CheckedInAt = DateTime.UtcNow,
                IsWatchlisted = isWatchlisted,
                WatchlistReason = isWatchlisted ? "Matches an existing watchlist entry by ID number" : null,
                Notes = request.Notes
            };
            _context.Visitors.Add(visitor);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        });

        await NotifyHostAsync(visitor, organizationId, branchId);

        return CreatedAtAction(nameof(GetVisitor), new { branchId, visitorId = visitor.Id }, MapToDto(visitor));
    }

    /// <summary>
    /// Checks in a previously pre-registered visitor on arrival.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitors/{visitorId:guid}/checkin")]
    [RequirePermission(Permissions.VisitorsCheckIn)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CheckInExisting(Guid branchId, Guid visitorId, [FromBody] CheckInVisitorRequest? request = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var visitor = await _context.Visitors
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId);

        if (visitor == null) return NotFound();

        if (visitor.Status != VisitorStatus.PreRegistered)
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot check in",
                Detail = $"Visitor is already '{visitor.Status}', not pre-registered.",
                Status = StatusCodes.Status400BadRequest
            });

        if (request != null)
        {
            if (!string.IsNullOrWhiteSpace(request.IdNumber)) visitor.IdNumber = request.IdNumber;
            if (!string.IsNullOrWhiteSpace(request.PhotoUrl)) visitor.PhotoUrl = request.PhotoUrl;
        }

        visitor.Status = VisitorStatus.CheckedIn;
        visitor.CheckedInAt = DateTime.UtcNow;
        visitor.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await NotifyHostAsync(visitor, visitor.OrganizationId, branchId);

        return Ok(MapToDto(visitor));
    }

    [HttpPost("branches/{branchId:guid}/visitors/{visitorId:guid}/checkout")]
    [RequirePermission(Permissions.VisitorsCheckOut)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CheckOut(Guid branchId, Guid visitorId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        // CONCURRENCY: atomic conditional update, same pattern as FeedbackController's
        // double-submit guard — only a visitor still CheckedIn can transition to CheckedOut,
        // checked as part of the same UPDATE rather than read-then-write.
        var affected = await _context.Visitors
            .Where(v => v.Id == visitorId && v.BranchId == branchId && v.Status == VisitorStatus.CheckedIn)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, VisitorStatus.CheckedOut)
                .SetProperty(v => v.CheckedOutAt, DateTime.UtcNow)
                .SetProperty(v => v.UpdatedAt, DateTime.UtcNow));

        if (affected == 0)
        {
            var exists = await _context.Visitors.AnyAsync(v => v.Id == visitorId && v.BranchId == branchId);
            if (!exists) return NotFound();
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot check out",
                Detail = "Visitor is not currently checked in.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var visitor = await _context.Visitors.FirstAsync(v => v.Id == visitorId);
        return Ok(MapToDto(visitor));
    }

    [HttpPut("branches/{branchId:guid}/visitors/{visitorId:guid}")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateVisitor(Guid branchId, Guid visitorId, [FromBody] UpdateVisitorRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var visitor = await _context.Visitors
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId);
        if (visitor == null) return NotFound();

        visitor.FullName = request.FullName;
        visitor.Phone = request.Phone;
        visitor.Email = request.Email;
        visitor.Company = request.Company;
        visitor.IdNumber = request.IdNumber;
        visitor.Purpose = request.Purpose;
        visitor.HostUserId = request.HostUserId;
        visitor.HostName = request.HostName;
        visitor.Notes = request.Notes;
        visitor.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(MapToDto(visitor));
    }

    [HttpPut("branches/{branchId:guid}/visitors/{visitorId:guid}/watchlist")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetWatchlist(Guid branchId, Guid visitorId, [FromBody] SetWatchlistRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var visitor = await _context.Visitors
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId);
        if (visitor == null) return NotFound();

        visitor.IsWatchlisted = request.IsWatchlisted;
        visitor.WatchlistReason = request.IsWatchlisted ? request.Reason : null;
        visitor.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(MapToDto(visitor));
    }

    [HttpDelete("branches/{branchId:guid}/visitors/{visitorId:guid}")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteVisitor(Guid branchId, Guid visitorId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var visitor = await _context.Visitors
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId);
        if (visitor == null) return NotFound();

        _context.Visitors.Remove(visitor);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
