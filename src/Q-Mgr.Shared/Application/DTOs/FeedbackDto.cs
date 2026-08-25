using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

public record FeedbackDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public Guid? TokenId { get; init; }
    public Guid? ServiceTypeId { get; init; }
    public Guid? CounterId { get; init; }
    public Guid? ServedByUserId { get; init; }

    public string FeedbackCode { get; init; } = string.Empty;
    public int Rating { get; init; }
    public string? Comment { get; init; }
    public FeedbackCategory Category { get; init; }
    public FeedbackSource Source { get; init; }

    public string? CustomerName { get; init; }
    public string? CustomerPhone { get; init; }
    public string? CustomerEmail { get; init; }
    public string? TokenDisplayNumber { get; init; }

    public DateTime? ServiceDate { get; init; }
    public DateTime CreatedAt { get; init; }

    // Response from staff
    public string? Response { get; init; }
    public DateTime? RespondedAt { get; init; }
    public Guid? RespondedByUserId { get; init; }

    // Navigation properties for display
    public string? ServiceTypeName { get; init; }
    public string? CounterName { get; init; }
    public string? ServedByName { get; init; }
    public string? RespondedByName { get; init; }
}

public record SubmitFeedbackRequest
{
    public int Rating { get; init; }
    public string? Comment { get; init; }
    public FeedbackCategory Category { get; init; } = FeedbackCategory.General;
    public string? CustomerName { get; init; }
    public string? CustomerPhone { get; init; }
    public string? CustomerEmail { get; init; }
}

public record SubmitFeedbackByCodeRequest
{
    public string FeedbackCode { get; init; } = string.Empty;
    public int Rating { get; init; }
    public string? Comment { get; init; }
    public FeedbackCategory Category { get; init; } = FeedbackCategory.General;
    public string? CustomerName { get; init; }
    public string? CustomerPhone { get; init; }
    public string? CustomerEmail { get; init; }
}

public record RespondToFeedbackRequest
{
    public string Response { get; init; } = string.Empty;
}

public record FeedbackSummaryDto
{
    public int TotalFeedbacks { get; init; }
    public double AverageRating { get; init; }
    public int FiveStarCount { get; init; }
    public int FourStarCount { get; init; }
    public int ThreeStarCount { get; init; }
    public int TwoStarCount { get; init; }
    public int OneStarCount { get; init; }
    public Dictionary<FeedbackCategory, int> CategoryBreakdown { get; init; } = new();
    public Dictionary<FeedbackSource, int> SourceBreakdown { get; init; } = new();
    public int PendingResponseCount { get; init; }
}

public record FeedbackLinkDto
{
    public Guid TokenId { get; init; }
    public string FeedbackCode { get; init; } = string.Empty;
    public string FeedbackUrl { get; init; } = string.Empty;
    public string TokenDisplayNumber { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
}
