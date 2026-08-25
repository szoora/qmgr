using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Queue;

public class Token : BaseEntity
{
    public Guid BranchId { get; set; }
    public Guid ServiceTypeId { get; set; }
    public Guid? CounterId { get; set; }

    // Token identification
    public string TokenNumber { get; set; } = string.Empty;
    public string DisplayNumber { get; set; } = string.Empty; // e.g., "GY-001"

    // Customer information (for integration)
    public string? CustomerId { get; set; } // External customer ID
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? CustomerEmail { get; set; }

    // Source tracking
    public TokenSource Source { get; set; } = TokenSource.Kiosk;
    public string? ExternalReference { get; set; } // Reference from external system
    public string? ExternalSystem { get; set; } // e.g., "hospital_mgmt", "banking_core"

    // Status tracking
    public TokenStatus Status { get; set; } = TokenStatus.Waiting;
    public TokenPriority Priority { get; set; } = TokenPriority.Normal;

    // Timestamps
    public DateTime? CalledAt { get; set; }
    public DateTime? ServiceStartedAt { get; set; }
    public DateTime? ServiceCompletedAt { get; set; }

    // Metrics
    public int? EstimatedWaitMinutes { get; set; }
    public int? ActualWaitMinutes { get; set; }
    public int? ServiceDurationMinutes { get; set; }

    // Additional data
    public string? Notes { get; set; }
    public string? Metadata { get; set; } // JSON - Flexible field for integration data

    // Navigation properties
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ServiceType? ServiceType { get; set; }
    public virtual Counter? Counter { get; set; }
    public virtual ICollection<TokenHistory> History { get; set; } = new List<TokenHistory>();
}
