using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

public record DocArticleDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? Summary { get; init; }
    public string? CoverImageUrl { get; init; }
    public IndustryType? Industry { get; init; }
    public DocArticleStatus Status { get; init; }
    public int DisplayOrder { get; init; }
    public DateTime? PublishedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public record DocArticleDetailDto : DocArticleDto
{
    public string BodyHtml { get; init; } = string.Empty;
}

public record CreateDocArticleRequest
{
    public string Title { get; init; } = string.Empty;
    public string? Slug { get; init; }
    public string? Summary { get; init; }
    public string BodyHtml { get; init; } = string.Empty;
    public string? CoverImageUrl { get; init; }
    public IndustryType? Industry { get; init; }
    public DocArticleStatus Status { get; init; } = DocArticleStatus.Draft;
    public int? DisplayOrder { get; init; }
}

public record UpdateDocArticleRequest
{
    public string? Title { get; init; }
    public string? Slug { get; init; }
    public string? Summary { get; init; }
    public string? BodyHtml { get; init; }
    public string? CoverImageUrl { get; init; }
    public IndustryType? Industry { get; init; }
    public DocArticleStatus? Status { get; init; }
    public int? DisplayOrder { get; init; }
}
