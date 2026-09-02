using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Docs;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Platform-owned onboarding/getting-started guides. No OrganizationId scoping — same
/// no-tenant-filter model as PlatformSettingsController. Public reads are [AllowAnonymous];
/// writes require platform.docs.view/manage (SuperAdmin only, per RbacSeeder's Admin-role
/// filter excluding every "platform." permission).
/// </summary>
[ApiController]
[Route("api/v1/docs")]
[Produces("application/json")]
[Authorize]
public class DocsController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly IMediaStorageService _mediaStorage;
    private readonly ILogger<DocsController> _logger;

    private const long MaxCoverImageBytes = 5 * 1024 * 1024;
    private static readonly string[] ReservedSlugs = { "admin", "check-slug" };

    public DocsController(QMgrDbContext dbContext, IMediaStorageService mediaStorage, ILogger<DocsController> logger)
    {
        _dbContext = dbContext;
        _mediaStorage = mediaStorage;
        _logger = logger;
    }

    // ---- Public reads ----

    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(List<DocArticleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPublished([FromQuery] IndustryType? industry)
    {
        var query = _dbContext.DocArticles.Where(a => a.Status == DocArticleStatus.Published);
        if (industry.HasValue)
        {
            query = query.Where(a => a.Industry == null || a.Industry == industry.Value);
        }

        var articles = await query
            .OrderBy(a => a.DisplayOrder).ThenBy(a => a.Title)
            .Select(a => ToDto(a))
            .ToListAsync();

        return Ok(articles);
    }

    [HttpGet("{slug}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(DocArticleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBySlug(string slug)
    {
        var article = await _dbContext.DocArticles
            .FirstOrDefaultAsync(a => a.Slug == slug && a.Status == DocArticleStatus.Published);

        // 404 for both "no such slug" and "exists but is a draft" — never leak draft existence.
        if (article == null) return NotFound(new { message = "Article not found." });

        return Ok(ToDetailDto(article));
    }

    // ---- Admin ----

    [HttpGet("admin")]
    [RequirePermission(Permissions.PlatformDocsView)]
    [ProducesResponseType(typeof(List<DocArticleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllForAdmin()
    {
        var articles = await _dbContext.DocArticles
            .OrderBy(a => a.DisplayOrder).ThenBy(a => a.Title)
            .Select(a => ToDto(a))
            .ToListAsync();

        return Ok(articles);
    }

    [HttpGet("admin/{id:guid}")]
    [RequirePermission(Permissions.PlatformDocsView)]
    [ProducesResponseType(typeof(DocArticleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdForAdmin(Guid id)
    {
        var article = await _dbContext.DocArticles.FindAsync(id);
        if (article == null) return NotFound(new { message = "Article not found." });

        return Ok(ToDetailDto(article));
    }

    [HttpGet("admin/check-slug")]
    [RequirePermission(Permissions.PlatformDocsView)]
    public async Task<IActionResult> CheckSlug([FromQuery] string slug, [FromQuery] Guid? excludeId)
    {
        var normalized = Slugify(slug);
        var available = !ReservedSlugs.Contains(normalized) &&
            !await _dbContext.DocArticles.AnyAsync(a => a.Slug == normalized && a.Id != excludeId);

        return Ok(new { available, slug = normalized });
    }

    [HttpPost]
    [RequirePermission(Permissions.PlatformDocsManage)]
    [ProducesResponseType(typeof(DocArticleDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateDocArticleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { message = "Title is required." });

        var slug = Slugify(string.IsNullOrWhiteSpace(request.Slug) ? request.Title : request.Slug);
        if (ReservedSlugs.Contains(slug))
            return Conflict(new { message = $"'{slug}' is a reserved slug." });
        if (await _dbContext.DocArticles.AnyAsync(a => a.Slug == slug))
            return Conflict(new { message = $"An article with slug '{slug}' already exists." });

        var userId = GetCurrentUserId();
        var now = DateTime.UtcNow;

        var article = new DocArticle
        {
            Title = request.Title.Trim(),
            Slug = slug,
            Summary = request.Summary,
            BodyHtml = request.BodyHtml,
            CoverImageUrl = request.CoverImageUrl,
            Industry = request.Industry,
            Status = request.Status,
            DisplayOrder = request.DisplayOrder ?? 0,
            PublishedAt = request.Status == DocArticleStatus.Published ? now : null,
            CreatedBy = userId,
            UpdatedBy = userId
        };

        _dbContext.DocArticles.Add(article);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Docs article {ArticleId} ({Slug}) created by {UserId}", article.Id, article.Slug, userId);

        return CreatedAtAction(nameof(GetByIdForAdmin), new { id = article.Id }, ToDetailDto(article));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.PlatformDocsManage)]
    [ProducesResponseType(typeof(DocArticleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDocArticleRequest request)
    {
        var article = await _dbContext.DocArticles.FindAsync(id);
        if (article == null) return NotFound(new { message = "Article not found." });

        if (!string.IsNullOrWhiteSpace(request.Slug))
        {
            var slug = Slugify(request.Slug);
            if (slug != article.Slug)
            {
                if (ReservedSlugs.Contains(slug))
                    return Conflict(new { message = $"'{slug}' is a reserved slug." });
                if (await _dbContext.DocArticles.AnyAsync(a => a.Slug == slug && a.Id != id))
                    return Conflict(new { message = $"An article with slug '{slug}' already exists." });
                article.Slug = slug;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Title)) article.Title = request.Title.Trim();
        if (request.Summary != null) article.Summary = request.Summary;
        if (request.BodyHtml != null) article.BodyHtml = request.BodyHtml;
        if (request.CoverImageUrl != null) article.CoverImageUrl = request.CoverImageUrl;
        if (request.Industry.HasValue) article.Industry = request.Industry;
        if (request.DisplayOrder.HasValue) article.DisplayOrder = request.DisplayOrder.Value;

        if (request.Status.HasValue && request.Status.Value != article.Status)
        {
            article.Status = request.Status.Value;
            if (article.Status == DocArticleStatus.Published && article.PublishedAt == null)
                article.PublishedAt = DateTime.UtcNow;
        }

        article.UpdatedBy = GetCurrentUserId();
        article.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        return Ok(ToDetailDto(article));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.PlatformDocsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var article = await _dbContext.DocArticles.FindAsync(id);
        if (article == null) return NotFound(new { message = "Article not found." });

        _dbContext.DocArticles.Remove(article);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Docs article {ArticleId} ({Slug}) deleted by {UserId}", id, article.Slug, GetCurrentUserId());

        return NoContent();
    }

    [HttpPost("admin/cover-image/upload")]
    [RequirePermission(Permissions.PlatformDocsManage)]
    [RequestSizeLimit(MaxCoverImageBytes)]
    public async Task<IActionResult> UploadCoverImage(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No file uploaded." });
        if (file.Length > MaxCoverImageBytes)
            return BadRequest(new { message = $"File exceeds the {MaxCoverImageBytes / 1024 / 1024}MB size limit." });

        var mimeType = file.ContentType ?? "";
        if (!mimeType.StartsWith("image/"))
            return BadRequest(new { message = $"File type '{mimeType}' is not allowed — cover images only." });

        await using var stream = file.OpenReadStream();
        var result = await _mediaStorage.UploadAsync(stream, file.FileName, mimeType);
        if (!result.Success)
        {
            _logger.LogError("Docs cover image upload failed for {FileName}: {Error}", file.FileName, result.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to store the uploaded file." });
        }

        return Ok(new { url = result.FileUrl });
    }

    // ---- Helpers ----

    private Guid? GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static string Slugify(string value)
    {
        var lowered = value.Trim().ToLowerInvariant();
        var slug = System.Text.RegularExpressions.Regex.Replace(lowered, @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? Guid.NewGuid().ToString("N")[..8] : slug;
    }

    private static DocArticleDto ToDto(DocArticle a) => new()
    {
        Id = a.Id,
        Title = a.Title,
        Slug = a.Slug,
        Summary = a.Summary,
        CoverImageUrl = a.CoverImageUrl,
        Industry = a.Industry,
        Status = a.Status,
        DisplayOrder = a.DisplayOrder,
        PublishedAt = a.PublishedAt,
        UpdatedAt = a.UpdatedAt
    };

    private static DocArticleDetailDto ToDetailDto(DocArticle a) => new()
    {
        Id = a.Id,
        Title = a.Title,
        Slug = a.Slug,
        Summary = a.Summary,
        CoverImageUrl = a.CoverImageUrl,
        Industry = a.Industry,
        Status = a.Status,
        DisplayOrder = a.DisplayOrder,
        PublishedAt = a.PublishedAt,
        UpdatedAt = a.UpdatedAt,
        BodyHtml = a.BodyHtml
    };
}
