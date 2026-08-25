using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

public record VisitorDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public string BadgeCode { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Company { get; init; }
    public string? IdNumber { get; init; }
    public string? PhotoUrl { get; init; }

    public string Purpose { get; init; } = string.Empty;
    public Guid? HostUserId { get; init; }
    public string HostName { get; init; } = string.Empty;

    public VisitorStatus Status { get; init; }
    public bool IsWatchlisted { get; init; }
    public string? WatchlistReason { get; init; }

    public DateTime? ScheduledAt { get; init; }
    public DateTime? CheckedInAt { get; init; }
    public DateTime? CheckedOutAt { get; init; }
    public DateTime CreatedAt { get; init; }

    public string? Notes { get; init; }
}

// Pre-register a visitor ahead of their arrival (Status starts as PreRegistered).
// Mutable (not init-only) — bound directly as a Blazor form model via @bind in
// VisitorManagement.razor, which requires settable properties.
public record PreRegisterVisitorRequest
{
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Company { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public Guid? HostUserId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public DateTime? ScheduledAt { get; set; }
    public string? Notes { get; set; }
}

// Walk-in check-in creates and checks in a visitor in one step; checking in an existing
// pre-registered visitor is a separate action (CheckInExistingVisitorRequest below).
// Mutable for the same @bind reason as PreRegisterVisitorRequest above.
public record CheckInVisitorRequest
{
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Company { get; set; }
    public string? IdNumber { get; set; }
    public string? PhotoUrl { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public Guid? HostUserId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public record UpdateVisitorRequest
{
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Company { get; init; }
    public string? IdNumber { get; init; }
    public string Purpose { get; init; } = string.Empty;
    public Guid? HostUserId { get; init; }
    public string HostName { get; init; } = string.Empty;
    public string? Notes { get; init; }
}

public record SetWatchlistRequest
{
    public bool IsWatchlisted { get; init; }
    public string? Reason { get; init; }
}

public record VisitorSummaryDto
{
    public int CurrentlyOnSite { get; init; }
    public int TotalToday { get; init; }
    public int PreRegisteredUpcoming { get; init; }
    public int WatchlistedOnSite { get; init; }
}
