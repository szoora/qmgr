using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The platform review queue for sign-ups that looked like a repeat of an existing customer.
/// </summary>
/// <remarks>
/// The duplicate check deliberately blocks only on near-proof: the same canonical email address, or
/// a phone number an existing account has verified. Everything softer than that is scored and, past
/// a threshold, flagged rather than refused, because a wrongly refused sign-up is a lost customer
/// who cannot appeal and will not come back. That trade only works if somebody actually looks at
/// the flags, which is what this controller is for.
/// </remarks>
[ApiController]
[Route("api/v1/admin/registration-attempts")]
[Authorize]
[RequirePermission(Permissions.PlatformAdmin)]
[Produces("application/json")]
public class RegistrationReviewController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly ILogger<RegistrationReviewController> _logger;

    public RegistrationReviewController(QMgrDbContext dbContext, ILogger<RegistrationReviewController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Lists sign-up attempts, newest first.
    /// </summary>
    /// <param name="decision">Restrict to one verdict. Omit for all of them.</param>
    /// <param name="pendingOnly">Only attempts nobody has ruled on yet, which is the default view.</param>
    /// <param name="search">Matches the email address or the organization name.</param>
    [HttpGet]
    [ProducesResponseType(typeof(List<RegistrationAttemptDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAttempts(
        [FromQuery] RegistrationRiskDecision? decision = null,
        [FromQuery] bool pendingOnly = false,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(page, 1);

        // Attempts are platform-wide by nature: one that was blocked never produced a tenant to
        // scope it to, so the tenant filter has to be stood down here.
        var query = _dbContext.RegistrationAttempts.IgnoreQueryFilters().AsNoTracking();

        if (decision.HasValue)
        {
            query = query.Where(a => a.Decision == decision.Value);
        }

        if (pendingOnly)
        {
            query = query.Where(a => a.ReviewedAt == null && a.Decision != RegistrationRiskDecision.Allow);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(a =>
                a.Email.ToLower().Contains(term) ||
                a.OrganizationName.ToLower().Contains(term));
        }

        var attempts = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // Names for the two organizations an attempt can point at, and for the reviewer, fetched in
        // one round trip each rather than per row.
        var orgIds = attempts
            .SelectMany(a => new[] { a.OrganizationId, a.MatchedOrganizationId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var orgNames = await _dbContext.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => orgIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Name, cancellationToken);

        var reviewerIds = attempts
            .Where(a => a.ReviewedByUserId.HasValue)
            .Select(a => a.ReviewedByUserId!.Value)
            .Distinct()
            .ToList();

        var reviewerNames = await _dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => reviewerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim(), cancellationToken);

        var results = attempts.Select(a => new RegistrationAttemptDto
        {
            Id = a.Id,
            CreatedAt = a.CreatedAt,
            Email = a.Email,
            Phone = a.Phone,
            PhoneWasVerified = a.PhoneWasVerified,
            OrganizationName = a.OrganizationName,
            ContactName = (a.ContactFirstName + " " + a.ContactLastName).Trim(),
            Decision = a.Decision,
            RiskScore = a.RiskScore,
            Signals = string.IsNullOrWhiteSpace(a.Signals)
                ? new List<string>()
                : a.Signals.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            OrganizationId = a.OrganizationId,
            MatchedOrganizationId = a.MatchedOrganizationId,
            MatchedOrganizationName = a.MatchedOrganizationId.HasValue && orgNames.TryGetValue(a.MatchedOrganizationId.Value, out var matched)
                ? matched
                : null,
            ReviewedAt = a.ReviewedAt,
            ReviewedByName = a.ReviewedByUserId.HasValue && reviewerNames.TryGetValue(a.ReviewedByUserId.Value, out var reviewer)
                ? reviewer
                : null,
            ReviewNotes = a.ReviewNotes,
            ReviewOutcome = a.ReviewOutcome
        }).ToList();

        return Ok(results);
    }

    /// <summary>Counts behind the summary strip at the top of the queue.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(RegistrationAttemptSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken = default)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var query = _dbContext.RegistrationAttempts.IgnoreQueryFilters().AsNoTracking();

        return Ok(new RegistrationAttemptSummaryDto
        {
            AwaitingReview = await query.CountAsync(
                a => a.ReviewedAt == null && a.Decision != RegistrationRiskDecision.Allow, cancellationToken),
            FlaggedLast30Days = await query.CountAsync(
                a => a.CreatedAt >= since && a.Decision == RegistrationRiskDecision.Flag, cancellationToken),
            BlockedLast30Days = await query.CountAsync(
                a => a.CreatedAt >= since && a.Decision == RegistrationRiskDecision.Block, cancellationToken),
            AllowedLast30Days = await query.CountAsync(
                a => a.CreatedAt >= since && a.Decision == RegistrationRiskDecision.Allow, cancellationToken)
        });
    }

    /// <summary>
    /// Records a verdict, and optionally suspends the account the attempt created.
    /// </summary>
    [HttpPost("{id:guid}/review")]
    [ProducesResponseType(typeof(RegistrationAttemptDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Review(
        Guid id,
        [FromBody] ReviewRegistrationAttemptRequest request,
        CancellationToken cancellationToken = default)
    {
        var attempt = await _dbContext.RegistrationAttempts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (attempt == null)
        {
            return NotFound(new { error = "NOT_FOUND", message = "That registration attempt no longer exists." });
        }

        attempt.ReviewedAt = DateTime.UtcNow;
        attempt.ReviewedByUserId = GetCurrentUserId();
        attempt.ReviewOutcome = request.Outcome;
        attempt.ReviewNotes = request.Notes;

        if (request.SuspendOrganization && attempt.OrganizationId.HasValue)
        {
            var organization = await _dbContext.Organizations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Id == attempt.OrganizationId.Value, cancellationToken);

            if (organization != null)
            {
                // Suspended, never deleted. A reviewer can be wrong, and the customer's data has to
                // survive that.
                organization.Status = TenantStatus.Suspended;
                _logger.LogWarning(
                    "Organization {OrganizationId} suspended after registration attempt {AttemptId} was reviewed as a duplicate",
                    organization.Id, attempt.Id);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new RegistrationAttemptDto
        {
            Id = attempt.Id,
            CreatedAt = attempt.CreatedAt,
            Email = attempt.Email,
            Phone = attempt.Phone,
            PhoneWasVerified = attempt.PhoneWasVerified,
            OrganizationName = attempt.OrganizationName,
            ContactName = (attempt.ContactFirstName + " " + attempt.ContactLastName).Trim(),
            Decision = attempt.Decision,
            RiskScore = attempt.RiskScore,
            OrganizationId = attempt.OrganizationId,
            MatchedOrganizationId = attempt.MatchedOrganizationId,
            ReviewedAt = attempt.ReviewedAt,
            ReviewNotes = attempt.ReviewNotes,
            ReviewOutcome = attempt.ReviewOutcome
        });
    }

    private Guid? GetCurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;
}
