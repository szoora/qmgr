using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Filters;
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
    /// <summary>
    /// Hard cap on how many survey questions a branch can have live at once. A public feedback
    /// form that takes more than about a minute simply doesn't get completed, so this is a real
    /// product constraint, not defensive plumbing: core rating + category + comment + NPS is
    /// already four steps, and eight extra questions is the outer edge of tolerable.
    /// Counted per effective scope (branch-specific + organization-wide) on create/activate.
    /// </summary>
    public const int MaxActiveQuestionsPerBranch = 8;

    /// <summary>Longest free-text survey answer accepted (silently trimmed, never rejected).</summary>
    private const int MaxFreeTextAnswerLength = 1000;

    /// <summary>
    /// Options used for Feedback.ResponsesJson and FeedbackQuestion.OptionsJson only. Enums are
    /// written as strings so a stored answer stays readable/greppable in the DB, and reads are
    /// case-insensitive so a row written by any earlier casing convention still parses.
    /// </summary>
    private static readonly JsonSerializerOptions StoredJson = CreateStoredJsonOptions();

    private static JsonSerializerOptions CreateStoredJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

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

        if (request.NpsScore is < 0 or > 10)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid NPS Score",
                Detail = "The recommendation score must be between 0 and 10",
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

        // Survey answers: validate against the questions that were actually active for this
        // branch/service, so a client can't post answers to questions it was never shown.
        var questions = await GetActiveQuestionsAsync(branchId, token.ServiceTypeId);
        var (answers, answerError) = BuildAnswers(request.Answers, questions);
        if (answerError != null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Survey Answer",
                Detail = answerError,
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
            NpsScore = request.NpsScore,
            ResponsesJson = SerializeAnswers(answers),
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

        if (request.NpsScore is < 0 or > 10)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid NPS Score",
                Detail = "The recommendation score must be between 0 and 10",
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

        var questions = await GetActiveQuestionsAsync(feedback.BranchId, feedback.ServiceTypeId);
        var (answers, answerError) = BuildAnswers(request.Answers, questions);
        if (answerError != null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Survey Answer",
                Detail = answerError,
                Status = StatusCodes.Status400BadRequest
            });
        }
        var responsesJson = SerializeAnswers(answers);

        // CONCURRENCY: the check above is racy on its own — two near-simultaneous submissions of
        // the same code could both read Rating == 0 before either writes. ExecuteUpdateAsync's
        // WHERE clause re-checks Rating == 0 as part of the same atomic UPDATE statement, so only
        // one concurrent caller can ever flip it from 0 — the loser gets affected == 0 instead of
        // silently overwriting (or being silently overwritten by) the winner's submission.
        var affected = await _context.Feedbacks
            .Where(f => f.Id == feedback.Id && f.Rating == 0)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Rating, request.Rating)
                .SetProperty(f => f.NpsScore, request.NpsScore)
                .SetProperty(f => f.ResponsesJson, responsesJson)
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
    [RequireModule(ModuleCodes.EngagementCommunications)]
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
    /// Get feedback details by code (for offsite page).
    /// Returns the branch's active survey questions inline so the public page renders the whole
    /// form from one round trip. The original fields are all still present and unchanged, so an
    /// existing feedback link keeps working exactly as before.
    /// </summary>
    [HttpGet("feedback/{code}")]
    [AllowAnonymous] // Public for customers via link
    [ProducesResponseType(typeof(FeedbackCodeInfoDto), StatusCodes.Status200OK)]
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

        var questions = alreadySubmitted
            ? new List<FeedbackQuestion>()
            : await GetActiveQuestionsAsync(feedback.BranchId, feedback.ServiceTypeId);

        return Ok(new FeedbackCodeInfoDto
        {
            FeedbackCode = feedback.FeedbackCode,
            BranchId = feedback.BranchId,
            ServiceTypeId = feedback.ServiceTypeId,
            TokenDisplayNumber = feedback.TokenDisplayNumber,
            ServiceTypeName = feedback.ServiceType?.Name,
            BranchName = feedback.Branch?.Name,
            ServiceDate = feedback.ServiceDate,
            AlreadySubmitted = alreadySubmitted,
            ExistingRating = alreadySubmitted ? feedback.Rating : null,
            Questions = questions.Select(q => MapQuestionToDto(q, null)).ToList()
        });
    }

    /// <summary>
    /// Get all feedbacks for a branch
    /// </summary>
    [HttpGet("branches/{branchId:guid}/feedbacks")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackView)]
    [RequireModule(ModuleCodes.EngagementCommunications)]
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

        // ResponsesJson is pulled alongside the projection and parsed in memory — the survey
        // answers live in a JSON column, so there is nothing for EF to translate here.
        var rows = await query
            .OrderByDescending(f => f.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(f => new
            {
                f.ResponsesJson,
                Dto = new FeedbackDto
                {
                    Id = f.Id,
                    BranchId = f.BranchId,
                    TokenId = f.TokenId,
                    ServiceTypeId = f.ServiceTypeId,
                    CounterId = f.CounterId,
                    ServedByUserId = f.ServedByUserId,
                    FeedbackCode = f.FeedbackCode,
                    Rating = f.Rating,
                    NpsScore = f.NpsScore,
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
                }
            })
            .ToListAsync();

        var feedbacks = rows
            .Select(r => r.Dto with { QuestionAnswers = ParseAnswers(r.ResponsesJson) })
            .ToList();

        return Ok(feedbacks);
    }

    /// <summary>
    /// Get a specific feedback
    /// </summary>
    [HttpGet("branches/{branchId:guid}/feedbacks/{feedbackId:guid}")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackView)]
    [RequireModule(ModuleCodes.EngagementCommunications)]
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
    [RequireModule(ModuleCodes.EngagementCommunications)]
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
    [RequireModule(ModuleCodes.EngagementCommunications)]
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

    // =======================================================================================
    // Survey questions
    //
    // PERMISSIONS NOTE: there is no `feedback.manage` permission in Domain/Constants/Permissions.cs
    // (the Feedback category is view/respond/analytics), and that file is owned elsewhere, so
    // question CRUD is gated on Permissions.FeedbackRespond — the existing write-level permission
    // in the Feedback category, held by Admin/Manager and not by view-only roles. If a dedicated
    // `feedback.manage` is ever added, swap these four attributes over to it.
    // =======================================================================================

    /// <summary>
    /// List every survey question that applies to a branch, active and inactive, for the admin
    /// builder. Includes organization-wide questions (BranchId == null).
    /// </summary>
    [HttpGet("branches/{branchId:guid}/feedback-questions")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackView)]
    [RequireModule(ModuleCodes.EngagementCommunications)]
    [ProducesResponseType(typeof(List<FeedbackQuestionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFeedbackQuestions(Guid branchId, [FromQuery] bool includeInactive = true)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await GetBranchOrganizationIdAsync(branchId);
        if (organizationId == Guid.Empty) return NotFound();

        var query = _context.Set<FeedbackQuestion>()
            .Where(q => q.OrganizationId == organizationId && (q.BranchId == null || q.BranchId == branchId));

        if (!includeInactive)
            query = query.Where(q => q.IsActive);

        var questions = await query
            .OrderBy(q => q.DisplayOrder)
            .ThenBy(q => q.CreatedAt)
            .ToListAsync();

        var serviceTypeNames = await GetServiceTypeNamesAsync(branchId);

        return Ok(questions.Select(q => MapQuestionToDto(q, LookupServiceTypeName(serviceTypeNames, q.ServiceTypeId))).ToList());
    }

    /// <summary>
    /// The active questions a public feedback form should render for a branch (optionally
    /// narrowed to the service type the customer was served under). Anonymous by design — this
    /// is the same data the customer is about to be shown.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/feedback-questions/public")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(List<FeedbackQuestionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPublicFeedbackQuestions(Guid branchId, [FromQuery] Guid? serviceTypeId = null)
    {
        var questions = await GetActiveQuestionsAsync(branchId, serviceTypeId);
        return Ok(questions.Select(q => MapQuestionToDto(q, null)).ToList());
    }

    /// <summary>
    /// Create a survey question for a branch (or for the whole organization).
    /// </summary>
    [HttpPost("branches/{branchId:guid}/feedback-questions")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackRespond)]
    [RequireModule(ModuleCodes.EngagementCommunications)]
    [ProducesResponseType(typeof(FeedbackQuestionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateFeedbackQuestion(Guid branchId, [FromBody] SaveFeedbackQuestionRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await GetBranchOrganizationIdAsync(branchId);
        if (organizationId == Guid.Empty) return NotFound();

        var validationError = await ValidateQuestionRequestAsync(branchId, request);
        if (validationError != null)
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Question",
                Detail = validationError,
                Status = StatusCodes.Status400BadRequest
            });

        var scopeBranchId = request.AppliesToAllBranches ? (Guid?)null : branchId;

        if (request.IsActive)
        {
            var capError = await ValidateActiveQuestionCapAsync(organizationId, scopeBranchId, excludeQuestionId: null);
            if (capError != null)
                return BadRequest(new ProblemDetails
                {
                    Title = "Too Many Questions",
                    Detail = capError,
                    Status = StatusCodes.Status400BadRequest
                });
        }

        var order = request.DisplayOrder ?? await NextDisplayOrderAsync(organizationId, branchId);

        var question = new FeedbackQuestion
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BranchId = scopeBranchId,
            QuestionText = request.QuestionText.Trim(),
            QuestionType = request.QuestionType,
            OptionsJson = SerializeOptions(request.QuestionType, request.Options),
            ServiceTypeId = request.ServiceTypeId,
            DisplayOrder = order,
            IsRequired = request.IsRequired,
            IsActive = request.IsActive,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = GetCurrentUserId()
        };

        _context.Set<FeedbackQuestion>().Add(question);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Feedback question {QuestionId} created for branch {BranchId}", question.Id, branchId);

        var serviceTypeNames = await GetServiceTypeNamesAsync(branchId);
        return CreatedAtAction(nameof(GetFeedbackQuestions), new { branchId },
            MapQuestionToDto(question, LookupServiceTypeName(serviceTypeNames, question.ServiceTypeId)));
    }

    /// <summary>
    /// Update a survey question. Scope (branch vs organization-wide) is fixed at creation and is
    /// deliberately not changed here — moving a question between scopes would silently change
    /// which historical answers it lines up with.
    /// </summary>
    [HttpPut("branches/{branchId:guid}/feedback-questions/{questionId:guid}")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackRespond)]
    [RequireModule(ModuleCodes.EngagementCommunications)]
    [ProducesResponseType(typeof(FeedbackQuestionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateFeedbackQuestion(Guid branchId, Guid questionId, [FromBody] SaveFeedbackQuestionRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await GetBranchOrganizationIdAsync(branchId);
        if (organizationId == Guid.Empty) return NotFound();

        var question = await _context.Set<FeedbackQuestion>()
            .FirstOrDefaultAsync(q => q.Id == questionId
                                      && q.OrganizationId == organizationId
                                      && (q.BranchId == null || q.BranchId == branchId));

        if (question == null) return NotFound();

        var validationError = await ValidateQuestionRequestAsync(branchId, request);
        if (validationError != null)
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Question",
                Detail = validationError,
                Status = StatusCodes.Status400BadRequest
            });

        // Only re-check the cap when this edit is what turns the question on.
        if (request.IsActive && !question.IsActive)
        {
            var capError = await ValidateActiveQuestionCapAsync(organizationId, question.BranchId, excludeQuestionId: question.Id);
            if (capError != null)
                return BadRequest(new ProblemDetails
                {
                    Title = "Too Many Questions",
                    Detail = capError,
                    Status = StatusCodes.Status400BadRequest
                });
        }

        question.QuestionText = request.QuestionText.Trim();
        question.QuestionType = request.QuestionType;
        question.OptionsJson = SerializeOptions(request.QuestionType, request.Options);
        question.ServiceTypeId = request.ServiceTypeId;
        question.IsRequired = request.IsRequired;
        question.IsActive = request.IsActive;
        if (request.DisplayOrder.HasValue)
            question.DisplayOrder = request.DisplayOrder.Value;
        question.UpdatedAt = DateTime.UtcNow;
        question.UpdatedBy = GetCurrentUserId();

        await _context.SaveChangesAsync();

        var serviceTypeNames = await GetServiceTypeNamesAsync(branchId);
        return Ok(MapQuestionToDto(question, LookupServiceTypeName(serviceTypeNames, question.ServiceTypeId)));
    }

    /// <summary>
    /// Delete a survey question. Answers already collected keep their own snapshot of the
    /// question text and type, so historical feedback stays readable after this.
    /// </summary>
    [HttpDelete("branches/{branchId:guid}/feedback-questions/{questionId:guid}")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackRespond)]
    [RequireModule(ModuleCodes.EngagementCommunications)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteFeedbackQuestion(Guid branchId, Guid questionId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await GetBranchOrganizationIdAsync(branchId);
        if (organizationId == Guid.Empty) return NotFound();

        var question = await _context.Set<FeedbackQuestion>()
            .FirstOrDefaultAsync(q => q.Id == questionId
                                      && q.OrganizationId == organizationId
                                      && (q.BranchId == null || q.BranchId == branchId));

        if (question == null) return NotFound();

        _context.Set<FeedbackQuestion>().Remove(question);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Feedback question {QuestionId} deleted from branch {BranchId}", questionId, branchId);
        return NoContent();
    }

    /// <summary>
    /// Reorder a branch's questions. The posted id list is the new display order, first = 0.
    /// </summary>
    [HttpPut("branches/{branchId:guid}/feedback-questions/reorder")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackRespond)]
    [RequireModule(ModuleCodes.EngagementCommunications)]
    [ProducesResponseType(typeof(List<FeedbackQuestionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReorderFeedbackQuestions(Guid branchId, [FromBody] ReorderFeedbackQuestionsRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await GetBranchOrganizationIdAsync(branchId);
        if (organizationId == Guid.Empty) return NotFound();

        var questions = await _context.Set<FeedbackQuestion>()
            .Where(q => q.OrganizationId == organizationId && (q.BranchId == null || q.BranchId == branchId))
            .ToListAsync();

        var now = DateTime.UtcNow;
        var userId = GetCurrentUserId();

        for (var i = 0; i < request.QuestionIds.Count; i++)
        {
            var question = questions.FirstOrDefault(q => q.Id == request.QuestionIds[i]);
            if (question == null || question.DisplayOrder == i) continue;

            question.DisplayOrder = i;
            question.UpdatedAt = now;
            question.UpdatedBy = userId;
        }

        await _context.SaveChangesAsync();

        var serviceTypeNames = await GetServiceTypeNamesAsync(branchId);
        return Ok(questions
            .OrderBy(q => q.DisplayOrder)
            .ThenBy(q => q.CreatedAt)
            .Select(q => MapQuestionToDto(q, LookupServiceTypeName(serviceTypeNames, q.ServiceTypeId)))
            .ToList());
    }

    // =======================================================================================
    // Analytics
    // =======================================================================================

    /// <summary>
    /// Richer analytics than /summary: average rating, rating distribution, Net Promoter Score
    /// and per-survey-question aggregates.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/feedbacks/stats")]
    [Authorize]
    [RequirePermission(Permissions.FeedbackAnalytics)]
    [RequireModule(ModuleCodes.EngagementCommunications)]
    [ProducesResponseType(typeof(FeedbackStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFeedbackStats(
        Guid branchId,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.Feedbacks
            .Where(f => f.BranchId == branchId && f.Rating > 0)
            .AsQueryable();

        // Npgsql maps these to timestamptz, which rejects an Unspecified-Kind DateTime.
        if (fromDate.HasValue)
        {
            var from = DateTime.SpecifyKind(fromDate.Value, DateTimeKind.Utc);
            query = query.Where(f => f.CreatedAt >= from);
        }

        if (toDate.HasValue)
        {
            var to = DateTime.SpecifyKind(toDate.Value.AddDays(1), DateTimeKind.Utc);
            query = query.Where(f => f.CreatedAt <= to);
        }

        var rows = await query
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new { f.Rating, f.NpsScore, f.ResponsesJson })
            .ToListAsync();

        var npsScores = rows.Where(r => r.NpsScore.HasValue).Select(r => r.NpsScore!.Value).ToList();

        // Current question definitions are only used to order the aggregates sensibly; the
        // aggregates themselves come from the snapshotted answers, so a question that has since
        // been deleted still reports its historical results.
        var organizationId = await GetBranchOrganizationIdAsync(branchId);
        var orderById = await _context.Set<FeedbackQuestion>()
            .Where(q => q.OrganizationId == organizationId && (q.BranchId == null || q.BranchId == branchId))
            .ToDictionaryAsync(q => q.Id, q => q.DisplayOrder);

        var grouped = new Dictionary<Guid, List<FeedbackAnswerDto>>();
        foreach (var row in rows)
        {
            foreach (var answer in ParseAnswers(row.ResponsesJson))
            {
                if (!grouped.TryGetValue(answer.QuestionId, out var list))
                {
                    list = new List<FeedbackAnswerDto>();
                    grouped[answer.QuestionId] = list;
                }
                list.Add(answer);
            }
        }

        var aggregates = grouped
            .Select(kvp => BuildQuestionAggregate(kvp.Key, kvp.Value))
            .OrderBy(a => orderById.TryGetValue(a.QuestionId, out var order) ? order : int.MaxValue)
            .ThenBy(a => a.QuestionText, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stats = new FeedbackStatsDto
        {
            ResponseCount = rows.Count,
            AverageRating = rows.Count > 0 ? Math.Round(rows.Average(r => r.Rating), 2) : 0,
            RatingDistribution = Enumerable.Range(1, 5).ToDictionary(star => star, star => rows.Count(r => r.Rating == star)),
            NpsResponseCount = npsScores.Count,
            PromoterCount = npsScores.Count(s => s >= 9),
            PassiveCount = npsScores.Count(s => s is 7 or 8),
            DetractorCount = npsScores.Count(s => s <= 6),
            Nps = CalculateNps(npsScores),
            QuestionAggregates = aggregates
        };

        return Ok(stats);
    }

    /// <summary>
    /// Net Promoter Score.
    ///
    ///   promoters  = respondents who answered 9 or 10
    ///   passives   = respondents who answered 7 or 8  (denominator only, never the numerator)
    ///   detractors = respondents who answered 0 to 6
    ///
    ///   NPS = (promoters / respondents x 100) - (detractors / respondents x 100)
    ///       = (promoters - detractors) / respondents x 100
    ///
    /// The result lives in -100..+100. The denominator is every respondent who answered the NPS
    /// question, passives included — excluding passives inflates the score.
    ///
    /// THE CLASSIC MISTAKE this deliberately does not make: averaging the raw 0-10 answers and
    /// calling that "NPS". That yields a 0..10 number which is not an NPS and moves completely
    /// differently — a set of answers that are all 8s averages 8.0 (looks excellent) but scores
    /// an NPS of exactly 0 (no promoters, no detractors). Never average NpsScore.
    ///
    /// Returns null rather than 0 when nobody answered: "no data" and "as many detractors as
    /// promoters" are very different findings and must not render the same.
    /// </summary>
    private static double? CalculateNps(IReadOnlyCollection<int> scores)
    {
        if (scores.Count == 0) return null;

        var promoters = scores.Count(s => s >= 9);
        var detractors = scores.Count(s => s <= 6);

        return Math.Round((promoters - detractors) * 100.0 / scores.Count, 1);
    }

    private static FeedbackQuestionAggregateDto BuildQuestionAggregate(Guid questionId, List<FeedbackAnswerDto> answers)
    {
        var latest = answers[^1];
        var type = latest.QuestionType;

        double? averageRating = null;
        var counts = new Dictionary<string, int>();
        var textAnswers = new List<string>();

        if (type == FeedbackQuestionType.FreeText)
        {
            // rows arrive newest-first, so this is the 20 most recent comments
            textAnswers = answers
                .Select(a => a.Answer)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Take(20)
                .ToList();
        }
        else
        {
            foreach (var answer in answers)
            {
                if (string.IsNullOrWhiteSpace(answer.Answer)) continue;
                counts[answer.Answer] = counts.GetValueOrDefault(answer.Answer) + 1;
            }

            if (type == FeedbackQuestionType.Rating)
            {
                var stars = answers
                    .Select(a => int.TryParse(a.Answer, out var v) ? v : (int?)null)
                    .Where(v => v is >= 1 and <= 5)
                    .Select(v => v!.Value)
                    .ToList();

                if (stars.Count > 0)
                    averageRating = Math.Round(stars.Average(), 2);
            }
        }

        return new FeedbackQuestionAggregateDto
        {
            QuestionId = questionId,
            QuestionText = latest.QuestionText,
            QuestionType = type,
            ResponseCount = answers.Count,
            AverageRating = averageRating,
            AnswerCounts = counts,
            TextAnswers = textAnswers
        };
    }

    // =======================================================================================
    // Survey helpers
    // =======================================================================================

    /// <summary>
    /// The questions a customer at this branch/service should actually be asked: active only,
    /// branch-specific plus organization-wide, and filtered to the service type when the
    /// question is pinned to one. A service-pinned question is skipped when the service type is
    /// unknown — better to ask nothing than to ask about a service the customer didn't use.
    /// </summary>
    private async Task<List<FeedbackQuestion>> GetActiveQuestionsAsync(Guid branchId, Guid? serviceTypeId)
    {
        var organizationId = await GetBranchOrganizationIdAsync(branchId);
        if (organizationId == Guid.Empty) return new List<FeedbackQuestion>();

        return await _context.Set<FeedbackQuestion>()
            .Where(q => q.OrganizationId == organizationId
                        && q.IsActive
                        && (q.BranchId == null || q.BranchId == branchId)
                        && (q.ServiceTypeId == null || q.ServiceTypeId == serviceTypeId))
            .OrderBy(q => q.DisplayOrder)
            .ThenBy(q => q.CreatedAt)
            .Take(MaxActiveQuestionsPerBranch)
            .ToListAsync();
    }

    /// <summary>
    /// SECURITY: FeedbackQuestion has no global query filter (same as Feedback itself), so the
    /// organization id is read from the branch row rather than trusted from the request. Works
    /// for SuperAdmin too, whose tenant context isn't scoped to one organization.
    /// </summary>
    private async Task<Guid> GetBranchOrganizationIdAsync(Guid branchId)
        => await _context.Branches
            .Where(b => b.Id == branchId)
            .Select(b => b.OrganizationId)
            .FirstOrDefaultAsync();

    private async Task<Dictionary<Guid, string>> GetServiceTypeNamesAsync(Guid branchId)
        => await _context.ServiceTypes
            .Where(s => s.BranchId == branchId)
            .ToDictionaryAsync(s => s.Id, s => s.Name);

    private static string? LookupServiceTypeName(Dictionary<Guid, string> names, Guid? serviceTypeId)
        => serviceTypeId.HasValue && names.TryGetValue(serviceTypeId.Value, out var name) ? name : null;

    private async Task<int> NextDisplayOrderAsync(Guid organizationId, Guid branchId)
    {
        var max = await _context.Set<FeedbackQuestion>()
            .Where(q => q.OrganizationId == organizationId && (q.BranchId == null || q.BranchId == branchId))
            .Select(q => (int?)q.DisplayOrder)
            .MaxAsync();

        return (max ?? -1) + 1;
    }

    private async Task<string?> ValidateQuestionRequestAsync(Guid branchId, SaveFeedbackQuestionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.QuestionText))
            return "Question text is required.";

        if (request.QuestionText.Trim().Length > 300)
            return "Question text cannot exceed 300 characters.";

        if (!Enum.IsDefined(request.QuestionType))
            return "Unknown question type.";

        if (request.QuestionType == FeedbackQuestionType.SingleChoice)
        {
            var options = (request.Options ?? new List<string>())
                .Select(o => o?.Trim() ?? string.Empty)
                .Where(o => o.Length > 0)
                .ToList();

            if (options.Count < 2)
                return "A multiple-choice question needs at least two options.";

            if (options.Count > 8)
                return "A multiple-choice question can have at most eight options.";

            if (options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
                return "Choice options must be unique.";
        }

        if (request.ServiceTypeId.HasValue)
        {
            var serviceTypeExists = await _context.ServiceTypes
                .AnyAsync(s => s.Id == request.ServiceTypeId.Value && s.BranchId == branchId);

            if (!serviceTypeExists)
                return "The selected service type does not belong to this branch.";
        }

        return null;
    }

    /// <summary>
    /// Enforces <see cref="MaxActiveQuestionsPerBranch"/>. The cap is per *effective* branch
    /// form — branch-specific questions plus organization-wide ones — because that is what a
    /// customer actually sees. An organization-wide question lands on every branch, so it is
    /// checked against whichever branch already has the most questions.
    /// </summary>
    private async Task<string?> ValidateActiveQuestionCapAsync(Guid organizationId, Guid? scopeBranchId, Guid? excludeQuestionId)
    {
        var activeScopes = await _context.Set<FeedbackQuestion>()
            .Where(q => q.OrganizationId == organizationId
                        && q.IsActive
                        && (excludeQuestionId == null || q.Id != excludeQuestionId))
            .Select(q => q.BranchId)
            .ToListAsync();

        var orgWideCount = activeScopes.Count(b => b == null);

        int existingForWorstBranch;
        if (scopeBranchId == null)
        {
            existingForWorstBranch = activeScopes
                .Where(b => b != null)
                .GroupBy(b => b!.Value)
                .Select(g => g.Count())
                .DefaultIfEmpty(0)
                .Max();
        }
        else
        {
            existingForWorstBranch = activeScopes.Count(b => b == scopeBranchId);
        }

        if (orgWideCount + existingForWorstBranch + 1 > MaxActiveQuestionsPerBranch)
        {
            return $"A branch can have at most {MaxActiveQuestionsPerBranch} active survey questions. " +
                   "Deactivate or delete one first — a feedback form longer than about a minute doesn't get completed.";
        }

        return null;
    }

    private static string? SerializeOptions(FeedbackQuestionType type, List<string>? options)
    {
        if (type != FeedbackQuestionType.SingleChoice) return null;

        var cleaned = (options ?? new List<string>())
            .Select(o => o?.Trim() ?? string.Empty)
            .Where(o => o.Length > 0)
            .ToList();

        return cleaned.Count == 0 ? null : JsonSerializer.Serialize(cleaned, StoredJson);
    }

    private static List<string> ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, StoredJson) ?? new List<string>();
        }
        catch (JsonException)
        {
            // A malformed options blob must not 500 the public feedback page.
            return new List<string>();
        }
    }

    private static List<FeedbackAnswerDto> ParseAnswers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<FeedbackAnswerDto>();

        try
        {
            return JsonSerializer.Deserialize<List<FeedbackAnswerDto>>(json, StoredJson) ?? new List<FeedbackAnswerDto>();
        }
        catch (JsonException)
        {
            return new List<FeedbackAnswerDto>();
        }
    }

    private static string? SerializeAnswers(List<FeedbackAnswerDto> answers)
        => answers.Count == 0 ? null : JsonSerializer.Serialize(answers, StoredJson);

    /// <summary>
    /// Validates the posted answers against the questions that were actually active for this
    /// branch/service and normalizes them into the stored snapshot shape. Answers to questions
    /// the customer was never shown are dropped rather than stored.
    /// </summary>
    private static (List<FeedbackAnswerDto> Answers, string? Error) BuildAnswers(
        List<SubmitFeedbackAnswer>? submitted,
        List<FeedbackQuestion> questions)
    {
        var result = new List<FeedbackAnswerDto>();
        if (questions.Count == 0) return (result, null);

        var byQuestionId = (submitted ?? new List<SubmitFeedbackAnswer>())
            .GroupBy(a => a.QuestionId)
            .ToDictionary(g => g.Key, g => g.Last().Answer);

        foreach (var question in questions)
        {
            byQuestionId.TryGetValue(question.Id, out var raw);
            var answer = raw?.Trim();

            if (string.IsNullOrEmpty(answer))
            {
                if (question.IsRequired)
                    return (result, $"\"{question.QuestionText}\" is required.");

                continue; // skipped optional question — nothing stored
            }

            switch (question.QuestionType)
            {
                case FeedbackQuestionType.Rating:
                    if (!int.TryParse(answer, out var stars) || stars < 1 || stars > 5)
                        return (result, $"\"{question.QuestionText}\" must be rated from 1 to 5.");
                    answer = stars.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;

                case FeedbackQuestionType.YesNo:
                    if (answer.Equals("yes", StringComparison.OrdinalIgnoreCase)) answer = "Yes";
                    else if (answer.Equals("no", StringComparison.OrdinalIgnoreCase)) answer = "No";
                    else return (result, $"\"{question.QuestionText}\" must be answered Yes or No.");
                    break;

                case FeedbackQuestionType.SingleChoice:
                    var options = ParseOptions(question.OptionsJson);
                    var match = options.FirstOrDefault(o => o.Equals(answer, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                        return (result, $"\"{answer}\" is not one of the available choices for \"{question.QuestionText}\".");
                    answer = match;
                    break;

                case FeedbackQuestionType.FreeText:
                    if (answer.Length > MaxFreeTextAnswerLength)
                        answer = answer[..MaxFreeTextAnswerLength];
                    break;
            }

            result.Add(new FeedbackAnswerDto
            {
                QuestionId = question.Id,
                QuestionText = question.QuestionText,
                QuestionType = question.QuestionType,
                Answer = answer
            });
        }

        return (result, null);
    }

    private static FeedbackQuestionDto MapQuestionToDto(FeedbackQuestion question, string? serviceTypeName)
        => new()
        {
            Id = question.Id,
            OrganizationId = question.OrganizationId,
            BranchId = question.BranchId,
            QuestionText = question.QuestionText,
            QuestionType = question.QuestionType,
            Options = ParseOptions(question.OptionsJson),
            ServiceTypeId = question.ServiceTypeId,
            ServiceTypeName = serviceTypeName,
            DisplayOrder = question.DisplayOrder,
            IsRequired = question.IsRequired,
            IsActive = question.IsActive,
            CreatedAt = question.CreatedAt,
            UpdatedAt = question.UpdatedAt
        };

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
            NpsScore = feedback.NpsScore,
            QuestionAnswers = ParseAnswers(feedback.ResponsesJson),
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
