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

    /// <summary>
    /// When service began. <b>Calling a customer starts the service clock</b> — this is set to the
    /// same moment as <see cref="CalledAt"/> by call-next, call-specific and transfer, and is the
    /// value <see cref="ServiceDurationMinutes"/> is measured from on completion.
    ///
    /// The alternative would be a separate "start service" action moving the token to
    /// <c>TokenStatus.Serving</c>, which would exclude the walk from the waiting area. That action
    /// has never existed: until 2026-09-06 nothing assigned this field at all, so
    /// <see cref="ServiceDurationMinutes"/> never populated and every average-service-time figure
    /// in the product — the dashboard tile, Counter Performance, Queue Analytics, Reports Overview
    /// — rendered blank or zero. Calling-is-serving was chosen deliberately (user decision) as the
    /// reading that makes those numbers real now; if a true start-service step is ever added, it
    /// should move this later rather than reintroduce a null.
    ///
    /// A transfer RESTARTS this rather than clearing it, so the recorded duration belongs to the
    /// counter that actually served the customer.
    /// </summary>
    public DateTime? ServiceStartedAt { get; set; }

    public DateTime? ServiceCompletedAt { get; set; }

    // Metrics
    public int? EstimatedWaitMinutes { get; set; }
    public int? ActualWaitMinutes { get; set; }
    public int? ServiceDurationMinutes { get; set; }

    // Customer notification tracking — guards against notifying the same customer twice for the
    // same reason (see IQueueCustomerNotifier). Deliberately two nullable columns on the token
    // itself rather than a separate "sent notifications" table: nothing beyond "which stage was
    // last sent, and when" needs to be stored or queried per token.
    // Values: "Issued", "Approaching", "Called" (kept as text rather than an enum so no new
    // Domain enum type is needed and the column is self-describing in the database).
    public string? LastNotifiedStage { get; set; }
    public DateTime? LastNotifiedAt { get; set; }

    // Additional data
    public string? Notes { get; set; }
    public string? Metadata { get; set; } // JSON - Flexible field for integration data

    // Navigation properties
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ServiceType? ServiceType { get; set; }
    public virtual Counter? Counter { get; set; }
    public virtual ICollection<TokenHistory> History { get; set; } = new List<TokenHistory>();
}
