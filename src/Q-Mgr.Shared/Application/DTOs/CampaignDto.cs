namespace QMgr.Application.DTOs;

public record CampaignDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// All-time impression count. Populated by the branch campaign list (one correlated
    /// subquery per row, served by the (CampaignId, CreatedAt) index); create/update
    /// responses leave it at 0 and the page reloads the list anyway.
    /// </summary>
    public int TotalImpressions { get; init; }
}

/// <summary>
/// Impression report for one campaign over a date range. Daily reuses the shared
/// DayCountDto (same shape as VisitorReportDto.VisitsByDay) rather than duplicating it.
/// </summary>
public record CampaignStatsDto
{
    public Guid CampaignId { get; init; }
    public string CampaignName { get; init; } = string.Empty;
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public int TotalImpressions { get; init; }
    public int UniqueMediaItems { get; init; }
    public List<DayCountDto> Daily { get; init; } = new();
    public List<CampaignMediaImpressionsDto> ByMedia { get; init; } = new();
    public List<CampaignBranchImpressionsDto> ByBranch { get; init; } = new();
}

public record CampaignMediaImpressionsDto
{
    public Guid MediaContentId { get; init; }
    public string Title { get; init; } = string.Empty;
    public int Count { get; init; }
}

public record CampaignBranchImpressionsDto
{
    public Guid BranchId { get; init; }
    public string BranchName { get; init; } = string.Empty;
    public int Count { get; init; }
}

public record CreateCampaignRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
}

public record UpdateCampaignRequest
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public bool? IsActive { get; init; }
}

public record RecordCampaignImpressionRequest
{
    public Guid MediaContentId { get; init; }
}
