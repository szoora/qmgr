using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public class FeedbackController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ILogger<FeedbackController> _logger;

    public FeedbackController(QMgrDbContext context, ITenantContextAccessor tenantAccessor, ILogger<FeedbackController> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <summary>
    /// SECURITY: Feedback (like Counter/Token/Playlist/Display) has no global EF query
    /// filter — it's branch-scoped, not directly org-scoped — so every action reaching one
    /// by branchId must verify ownership explicitly. Feedback rows carry real customer PII
    /// (name, phone, email), so this is a genuine data-exposure boundary, not just access
    /// control housekeeping. SuperAdmin bypass matches every other VerifyBranchOwnership in
    /// this codebase (ContentController, OrganizationsController, and now TokensController).
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
    /// Submit feedback for a token (kiosk/onsite)
    /// </summary>
    [HttpPost("branches/{branchId:guid}/tokens/{tokenId:guid}/feedback")]
    [AllowAnonymous] // Public for customers at kiosk
    [ProducesResponseType(typeof(FeedbackDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SubmitFeedbackForToken(
        Guid branchId,
        Guid tokenId,
        [FromBody] SubmitFeedbackRequest request)
    {
        // Validate rating
        if (request.Rating < 1 || request.Rating > 5)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Rating",
                Detail = "Rating must be between 1 and 5",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Get the token
        var token = await _context.Tokens
            .Include(t => t.ServiceType)
            .Include(t => t.Counter)
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.BranchId == branchId);

        if (token == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Token Not Found",
                Detail = $"Token with ID '{tokenId}' was not found in this branch",
                Status = StatusCodes.Status404NotFound
            });
        }

        // Check if feedback already exists for this token
        var existingFeedback = await _context.Feedbacks
            .FirstOrDefaultAsync(f => f.TokenId == tokenId);

        if (existingFeedback != null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Feedback Already Submitted",
                Detail = "Feedback has already been submitted for this token",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Generate unique feedback code
        var feedbackCode = GenerateFeedbackCode();

        var feedback = new Feedback
        {
            Id = Guid.NewGuid(),
            BranchId = branchId,
            TokenId = tokenId,
            ServiceTypeId = token.ServiceTypeId,
            CounterId = token.CounterId,
            FeedbackCode = feedbackCode,
            Rating = request.Rating,
            Comment = request.Comment,
            Category = request.Category,
            Source = FeedbackSource.Kiosk,
            CustomerName = request.CustomerName ?? token.CustomerName,
            CustomerPhone = request.CustomerPhone ?? token.CustomerPhone,
            CustomerEmail = request.CustomerEmail ?? token.CustomerEmail,
            TokenDisplayNumber = token.DisplayNumber,
            ServiceDate = token.ServiceCompletedAt ?? token.CreatedAt,
            CreatedAt = DateTime.UtcNow
        };

        _context.Feedbacks.Add(feedback);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // CONCURRENCY: the app-level "does feedback already exist" check above has a race —
            // two near-simultaneous submissions for the same token can both pass it before either
            // inserts. idx_feedback_token (now unique, see FeedbackConfiguration) is the real
            // guard; this just turns the resulting DB-level conflict into the same honest 400 the
            // pre-check already returns for the non-racy case, instead of a raw 500.
            return BadRequest(new ProblemDetails
            {
                Title = "Feedback Already Submitted",
                Detail = "Feedback has already been submitted for this token",
                Status = StatusCodes.Status400BadRequest
            });
        }

        _logger.LogInformation("Feedback {FeedbackId} submitted for token {TokenId}", feedback.Id, tokenId);

        var dto = MapToDto(feedback, token.ServiceType?.Name, token.Counter?.DisplayName);
        return CreatedAtAction(nameof(GetFeedback), new { branchId, feedbackId = feedback.Id }, dto);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    /// <summary>
    /// Submit feedback via feedback code (offsite link)
    /// </summary>
    [HttpPost("feedback/submit")]
    [AllowAnonymous] // Public for customers via link
    [ProducesResponseType(typeof(FeedbackDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SubmitFeedbackByCode([FromBody] SubmitFeedbackByCodeRequest request)
    {
        // Validate rating
        if (request.Rating < 1 || request.Rating > 5)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Rating",
                Detail = "Rating must be between 1 and 5",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Find the feedback by code
        var feedback = await _context.Feedbacks
            .Include(f => f.ServiceType)
            .Include(f => f.Counter)
            .FirstOrDefaultAsync(f => f.FeedbackCode == request.FeedbackCode);

        if (feedback == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Invalid Feedback Code",
                Detail = "The feedback code is invalid or has expired",
                Status = StatusCodes.Status404NotFound
            });
        }

        // Check if feedback has already been submitted
        if (feedback.Rating > 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Feedback Already Submitted",
                Detail = "Feedback has already been submitted using this code",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // CONCURRENCY: the check above is racy on its own — two near-simultaneous submissions of
        // the same code could both read Rating == 0 before either writes. ExecuteUpdateAsync's
        // WHERE clause re-checks Rating == 0 as part of the same atomic UPDATE statement, so only
        // one concurrent caller can ever flip it from 0 — the loser gets affected == 0 instead of
        // silently overwriting (or being silently overwritten by) the winner's submission.
        var affected = await _context.Feedbacks
            .Where(f => f.Id == feedback.Id && f.Rating == 0)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Rating, request.Rating)
                .SetProperty(f => f.Comment, request.Comment)
                .SetProperty(f => f.Category, request.Category)
                .SetProperty(f => f.Source, FeedbackSource.Link)
                .SetProperty(f => f.CustomerName, request.CustomerName ?? feedback.CustomerName)
                .SetProperty(f => f.CustomerPhone, request.CustomerPhone ?? feedback.CustomerPhone)
                .SetProperty(f => f.CustomerEmail, request.CustomerEmail ?? feedback.CustomerEmail)
                .SetProperty(f => f.UpdatedAt, DateTime.UtcNow));

        if (affected == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Feedback Already Submitted",
                Detail = "Feedback has already been submitted using this code",
                Status = StatusCodes.Status400BadRequest
            });
        }

        await _context.Entry(feedback).ReloadAsync();

        _logger.LogInformation("Offsite feedback submitted via code {FeedbackCode}", request.FeedbackCode);

        var dto = MapToDto(feedback, feedback.ServiceType?.Name, feedback.Counter?.DisplayName);
        return Ok(dto);
    }

    /// <summary>
    /// Generate a feedback link for a token
    /// </summary>
    [HttpPost("branches/{branchId:guid}/tokens/{tokenId:guid}/feedback-link")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackView)]
    [ProducesResponseType(typeof(FeedbackLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateFeedbackLink(Guid branchId, Guid tokenId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var token = await _context.Tokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.BranchId == branchId);

        if (token == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Token Not Found",
                Detail = $"Token with ID '{tokenId}' was not found",
                Status = StatusCodes.Status404NotFound
            });
        }

        // Check if feedback already exists
        var existingFeedback = await _context.Feedbacks
            .FirstOrDefaultAsync(f => f.TokenId == tokenId);

        if (existingFeedback != null)
        {
            // Return existing link if feedback hasn't been submitted
            if (existingFeedback.Rating == 0)
            {
                return Ok(new FeedbackLinkDto
                {
                    TokenId = tokenId,
                    FeedbackCode = existingFeedback.FeedbackCode,
                    FeedbackUrl = $"/feedback/{existingFeedback.FeedbackCode}",
                    TokenDisplayNumber = token.DisplayNumber,
                    ExpiresAt = existingFeedback.CreatedAt.AddDays(7)
                });
            }

            return BadRequest(new ProblemDetails
            {
                Title = "Feedback Already Submitted",
                Detail = "Feedback has already been submitted for this token",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Create a placeholder feedback entry with just the code
        var feedbackCode = GenerateFeedbackCode();
        var feedback = new Feedback
        {
            Id = Guid.NewGuid(),
            BranchId = branchId,
            TokenId = tokenId,
            ServiceTypeId = token.ServiceTypeId,
            CounterId = token.CounterId,
            FeedbackCode = feedbackCode,
            Rating = 0, // Not yet submitted
            Category = FeedbackCategory.General,
            Source = FeedbackSource.Link,
            TokenDisplayNumber = token.DisplayNumber,
            ServiceDate = token.ServiceCompletedAt ?? token.CreatedAt,
            CustomerName = token.CustomerName,
            CustomerPhone = token.CustomerPhone,
            CustomerEmail = token.CustomerEmail,
            CreatedAt = DateTime.UtcNow
        };

        _context.Feedbacks.Add(feedback);
        await _context.SaveChangesAsync();

        return Ok(new FeedbackLinkDto
        {
            TokenId = tokenId,
            FeedbackCode = feedbackCode,
            FeedbackUrl = $"/feedback/{feedbackCode}",
            TokenDisplayNumber = token.DisplayNumber,
            ExpiresAt = feedback.CreatedAt.AddDays(7)
        });
    }

    /// <summary>
    /// Get feedback details by code (for offsite page)
    /// </summary>
    [HttpGet("feedback/{code}")]
    [AllowAnonymous] // Public for customers via link
    [ProducesResponseType(typeof(FeedbackLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFeedbackByCode(string code)
    {
        var feedback = await _context.Feedbacks
            .Include(f => f.Token)
            .Include(f => f.ServiceType)
            .Include(f => f.Branch)
            .FirstOrDefaultAsync(f => f.FeedbackCode == code);

        if (feedback == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Invalid Code",
                Detail = "The feedback code is invalid or has expired",
                Status = StatusCodes.Status404NotFound
            });
        }

        // Check if already submitted
        var alreadySubmitted = feedback.Rating > 0;

        return Ok(new
        {
            FeedbackCode = feedback.FeedbackCode,
            TokenDisplayNumber = feedback.TokenDisplayNumber,
            ServiceTypeName = feedback.ServiceType?.Name,
            BranchName = feedback.Branch?.Name,
            ServiceDate = feedback.ServiceDate,
            AlreadySubmitted = alreadySubmitted,
            ExistingRating = alreadySubmitted ? feedback.Rating : (int?)null
        });
    }

    /// <summary>
    /// Get all feedbacks for a branch
    /// </summary>
    [HttpGet("branches/{branchId:guid}/feedbacks")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackView)]
    [ProducesResponseType(typeof(List<FeedbackDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFeedbacks(
        Guid branchId,
        [FromQuery] int? rating = null,
        [FromQuery] FeedbackCategory? category = null,
        [FromQuery] FeedbackSource? source = null,
        [FromQuery] bool? hasResponse = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.Feedbacks
            .Include(f => f.ServiceType)
            .Include(f => f.Counter)
            .Where(f => f.BranchId == branchId && f.Rating > 0) // Only submitted feedbacks
            .AsQueryable();

        if (rating.HasValue)
            query = query.Where(f => f.Rating == rating.Value);

        if (category.HasValue)
            query = query.Where(f => f.Category == category.Value);

        if (source.HasValue)
            query = query.Where(f => f.Source == source.Value);

        if (hasResponse.HasValue)
        {
            if (hasResponse.Value)
                query = query.Where(f => f.Response != null);
            else
                query = query.Where(f => f.Response == null);
        }

        if (fromDate.HasValue)
            query = query.Where(f => f.CreatedAt >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(f => f.CreatedAt <= toDate.Value.AddDays(1));

        var feedbacks = await query
            .OrderByDescending(f => f.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(f => new FeedbackDto
            {
                Id = f.Id,
                BranchId = f.BranchId,
                TokenId = f.TokenId,
                ServiceTypeId = f.ServiceTypeId,
                CounterId = f.CounterId,
                ServedByUserId = f.ServedByUserId,
                FeedbackCode = f.FeedbackCode,
                Rating = f.Rating,
                Comment = f.Comment,
                Category = f.Category,
                Source = f.Source,
                CustomerName = f.CustomerName,
                CustomerPhone = f.CustomerPhone,
                CustomerEmail = f.CustomerEmail,
                TokenDisplayNumber = f.TokenDisplayNumber,
                ServiceDate = f.ServiceDate,
                CreatedAt = f.CreatedAt,
                Response = f.Response,
                RespondedAt = f.RespondedAt,
                RespondedByUserId = f.RespondedByUserId,
                ServiceTypeName = f.ServiceType != null ? f.ServiceType.Name : null,
                CounterName = f.Counter != null ? f.Counter.DisplayName : null
            })
            .ToListAsync();

        return Ok(feedbacks);
    }

    /// <summary>
    /// Get a specific feedback
    /// </summary>
    [HttpGet("branches/{branchId:guid}/feedbacks/{feedbackId:guid}")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackView)]
    [ProducesResponseType(typeof(FeedbackDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFeedback(Guid branchId, Guid feedbackId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var feedback = await _context.Feedbacks
            .Include(f => f.ServiceType)
            .Include(f => f.Counter)
            .FirstOrDefaultAsync(f => f.Id == feedbackId && f.BranchId == branchId);

        if (feedback == null)
            return NotFound();

        var dto = MapToDto(feedback, feedback.ServiceType?.Name, feedback.Counter?.DisplayName);
        return Ok(dto);
    }

    /// <summary>
    /// Get feedback summary/analytics for a branch
    /// </summary>
    [HttpGet("branches/{branchId:guid}/feedbacks/summary")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackAnalytics)]
    [ProducesResponseType(typeof(FeedbackSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFeedbackSummary(
        Guid branchId,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.Feedbacks
            .Where(f => f.BranchId == branchId && f.Rating > 0)
            .AsQueryable();

        if (fromDate.HasValue)
            query = query.Where(f => f.CreatedAt >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(f => f.CreatedAt <= toDate.Value.AddDays(1));

        var feedbacks = await query.ToListAsync();

        var summary = new FeedbackSummaryDto
        {
            TotalFeedbacks = feedbacks.Count,
            AverageRating = feedbacks.Any() ? Math.Round(feedbacks.Average(f => f.Rating), 2) : 0,
            FiveStarCount = feedbacks.Count(f => f.Rating == 5),
            FourStarCount = feedbacks.Count(f => f.Rating == 4),
            ThreeStarCount = feedbacks.Count(f => f.Rating == 3),
            TwoStarCount = feedbacks.Count(f => f.Rating == 2),
            OneStarCount = feedbacks.Count(f => f.Rating == 1),
            CategoryBreakdown = feedbacks
                .GroupBy(f => f.Category)
                .ToDictionary(g => g.Key, g => g.Count()),
            SourceBreakdown = feedbacks
                .GroupBy(f => f.Source)
                .ToDictionary(g => g.Key, g => g.Count()),
            PendingResponseCount = feedbacks.Count(f => string.IsNullOrEmpty(f.Response))
        };

        return Ok(summary);
    }

    /// <summary>
    /// Respond to a feedback
    /// </summary>
    [HttpPost("branches/{branchId:guid}/feedbacks/{feedbackId:guid}/respond")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackRespond)]
    [ProducesResponseType(typeof(FeedbackDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RespondToFeedback(
        Guid branchId,
        Guid feedbackId,
        [FromBody] RespondToFeedbackRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var feedback = await _context.Feedbacks
            .Include(f => f.ServiceType)
            .Include(f => f.Counter)
            .FirstOrDefaultAsync(f => f.Id == feedbackId && f.BranchId == branchId);

        if (feedback == null)
            return NotFound();

        feedback.Response = request.Response;
        feedback.RespondedAt = DateTime.UtcNow;
        feedback.RespondedByUserId = GetCurrentUserId();
        feedback.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        var dto = MapToDto(feedback, feedback.ServiceType?.Name, feedback.Counter?.DisplayName);
        return Ok(dto);
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst("userId")?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private static string GenerateFeedbackCode()
    {
        // Generate a short, user-friendly code (8 characters)
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Excluding confusing characters
        var random = new Random();
        return new string(Enumerable.Repeat(chars, 8)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }

    private static FeedbackDto MapToDto(Feedback feedback, string? serviceTypeName, string? counterName)
    {
        return new FeedbackDto
        {
            Id = feedback.Id,
            BranchId = feedback.BranchId,
            TokenId = feedback.TokenId,
            ServiceTypeId = feedback.ServiceTypeId,
            CounterId = feedback.CounterId,
            ServedByUserId = feedback.ServedByUserId,
            FeedbackCode = feedback.FeedbackCode,
            Rating = feedback.Rating,
            Comment = feedback.Comment,
            Category = feedback.Category,
            Source = feedback.Source,
            CustomerName = feedback.CustomerName,
            CustomerPhone = feedback.CustomerPhone,
            CustomerEmail = feedback.CustomerEmail,
            TokenDisplayNumber = feedback.TokenDisplayNumber,
            ServiceDate = feedback.ServiceDate,
            CreatedAt = feedback.CreatedAt,
            Response = feedback.Response,
            RespondedAt = feedback.RespondedAt,
            RespondedByUserId = feedback.RespondedByUserId,
            ServiceTypeName = serviceTypeName,
            CounterName = counterName
        };
    }
}
