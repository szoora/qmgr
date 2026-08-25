using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Integration;

public class ApiClient : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecretHash { get; set; } = string.Empty;

    public string? SystemType { get; set; } // hospital_mgmt, banking, pharmacy, custom
    public string? Description { get; set; }

    // Permissions
    public string[]? Scopes { get; set; } // ['queue:write', 'queue:read', 'content:read']
    public Guid[]? AllowedBranches { get; set; }

    // Rate limiting
    public int RateLimitPerMinute { get; set; } = 100;

    // Webhooks
    public string? WebhookUrl { get; set; }
    public string[]? WebhookEvents { get; set; } // ['token.created', 'token.called', 'token.completed']
    public string? WebhookSecret { get; set; }

    public DateTime? LastUsedAt { get; set; }

    // Navigation properties
    public virtual Organization.Organization? Organization { get; set; }
    public virtual ICollection<ApiLog> ApiLogs { get; set; } = new List<ApiLog>();
    public virtual ICollection<WebhookOutgoing> WebhooksOutgoing { get; set; } = new List<WebhookOutgoing>();
}
