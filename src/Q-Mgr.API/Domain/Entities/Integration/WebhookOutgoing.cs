using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Integration;

public class WebhookOutgoing : BaseEntity
{
    public Guid ApiClientId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? Payload { get; set; } // JSON

    public string Status { get; set; } = "pending"; // pending, sent, failed, retrying
    public int Attempts { get; set; } = 0;
    public DateTime? LastAttemptAt { get; set; }
    public string? LastError { get; set; }

    // Navigation properties
    public virtual ApiClient? ApiClient { get; set; }
}
