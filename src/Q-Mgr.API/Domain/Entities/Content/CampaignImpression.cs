using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Content;

public class CampaignImpression : BaseEntity
{
    public Guid CampaignId { get; set; }
    public Guid MediaContentId { get; set; }
    public Guid BranchId { get; set; }

    // Navigation properties
    public virtual Campaign? Campaign { get; set; }
    public virtual MediaContent? MediaContent { get; set; }
}
