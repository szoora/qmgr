using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Visitor;

/// <summary>
/// A single visit record — one row per visit, not a recurring "known visitor" directory.
/// Branch-scoped like Token/Feedback/Counter (no global EF query filter — every controller
/// action reaching one by branchId must call VerifyBranchOwnership explicitly).
/// </summary>
public class Visitor : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }

    // Unique per-visit badge/reference code, e.g. "V-20260825-0001"
    public string BadgeCode { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Company { get; set; }
    public string? IdNumber { get; set; }
    public string? PhotoUrl { get; set; }

    public string Purpose { get; set; } = string.Empty;

    // Who they're visiting — captured as a name so a visit record still reads correctly
    // even if the host user is later deleted; HostUserId is the notification target.
    public Guid? HostUserId { get; set; }
    public string HostName { get; set; } = string.Empty;

    public VisitorStatus Status { get; set; } = VisitorStatus.PreRegistered;

    public bool IsWatchlisted { get; set; }
    public string? WatchlistReason { get; set; }

    public DateTime? ScheduledAt { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public DateTime? CheckedOutAt { get; set; }

    public string? Notes { get; set; }

    // Navigation properties
    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual Identity.User? HostUser { get; set; }
}
