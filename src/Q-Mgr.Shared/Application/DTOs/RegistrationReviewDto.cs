using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

/// <summary>
/// One sign-up attempt as the platform review queue shows it.
/// </summary>
/// <remarks>
/// Lives in the shared project on purpose: both the API that produces it and the Blazor page that
/// renders it need the same shape, and this codebase has repeatedly grown a second, silently
/// diverging copy when a DTO was defined on only one side.
/// </remarks>
public record RegistrationAttemptDto
{
    public Guid Id { get; init; }
    public DateTime CreatedAt { get; init; }

    public string Email { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public bool PhoneWasVerified { get; init; }
    public string OrganizationName { get; init; } = string.Empty;
    public string? ContactName { get; init; }

    public RegistrationRiskDecision Decision { get; init; }
    public int RiskScore { get; init; }

    /// <summary>Plain-language reasons, one per line, as the assessment produced them.</summary>
    public List<string> Signals { get; init; } = new();

    /// <summary>The organization this attempt created, when it was allowed through.</summary>
    public Guid? OrganizationId { get; init; }

    /// <summary>The existing organization it resembled, which is what a reviewer compares against.</summary>
    public Guid? MatchedOrganizationId { get; init; }
    public string? MatchedOrganizationName { get; init; }

    public DateTime? ReviewedAt { get; init; }
    public string? ReviewedByName { get; init; }
    public string? ReviewNotes { get; init; }
    public RegistrationReviewOutcome? ReviewOutcome { get; init; }
}

/// <summary>Counts behind the queue's summary strip.</summary>
public record RegistrationAttemptSummaryDto
{
    public int AwaitingReview { get; init; }
    public int FlaggedLast30Days { get; init; }
    public int BlockedLast30Days { get; init; }
    public int AllowedLast30Days { get; init; }
}

/// <summary>A reviewer's verdict on one attempt.</summary>
public record ReviewRegistrationAttemptRequest
{
    public RegistrationReviewOutcome Outcome { get; init; }
    public string? Notes { get; init; }

    /// <summary>
    /// Suspends the organization the attempt created. Only meaningful when the reviewer decided the
    /// account really is a duplicate; the account is suspended rather than deleted so a mistaken
    /// call can be undone.
    /// </summary>
    public bool SuspendOrganization { get; init; }
}
