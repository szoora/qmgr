using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

public record TokenDto
{
    public Guid Id { get; init; }
    public string TokenNumber { get; init; } = string.Empty;
    public string DisplayNumber { get; init; } = string.Empty;
    public TokenStatus Status { get; init; }
    public TokenPriority Priority { get; init; }
    public TokenSource Source { get; init; }

    public Guid BranchId { get; init; }
    public Guid ServiceTypeId { get; init; }
    public Guid? CounterId { get; init; }

    public CustomerDto? Customer { get; init; }
    public ServiceTypeDto? ServiceType { get; init; }
    public CounterDto? Counter { get; init; }

    public string? ExternalReference { get; init; }
    public string? ExternalSystem { get; init; }

    public int? PositionInQueue { get; init; }
    public int? EstimatedWaitMinutes { get; init; }
    public int? ActualWaitMinutes { get; init; }
    public int? ServiceDurationMinutes { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime? CalledAt { get; init; }
    public DateTime? ServiceStartedAt { get; init; }
    public DateTime? ServiceCompletedAt { get; init; }

    public string? Notes { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

public record CustomerDto
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
}

public record ServiceTypeDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Prefix { get; init; }
    public int AverageServiceTimeMinutes { get; init; }
    public string? IconUrl { get; init; }
    public string? Color { get; init; }
    public bool IsActive { get; init; } = true;
}

public record CounterDto
{
    public Guid Id { get; init; }
    public string CounterNumber { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public CounterStatus Status { get; init; }
    public bool IsActive { get; init; } = true;
    public TokenDto? CurrentToken { get; init; }
    public List<ServiceTypeDto> ServiceTypes { get; init; } = new();
}
