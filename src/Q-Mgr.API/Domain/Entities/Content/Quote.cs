using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Content;

public class Quote : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public string Category { get; set; } = "Motivational";
    public string Text { get; set; } = string.Empty;
    public string? Author { get; set; }

    // Navigation properties
    public virtual Organization.Organization? Organization { get; set; }
}
