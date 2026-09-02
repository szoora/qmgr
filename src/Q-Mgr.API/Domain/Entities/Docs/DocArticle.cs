using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Docs;

/// <summary>
/// A platform-owned onboarding/getting-started guide, authored by platform staff (SuperAdmin)
/// and readable by anyone without logging in. Deliberately has no OrganizationId — this is not
/// tenant content, same pattern as QMgr.Domain.Entities.Platform.PlatformSetting.
/// </summary>
public class DocArticle : BaseAuditableEntity
{
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string BodyHtml { get; set; } = string.Empty;
    public string? CoverImageUrl { get; set; }

    /// <summary>Null = general guide, applies to every industry.</summary>
    public IndustryType? Industry { get; set; }

    public DocArticleStatus Status { get; set; } = DocArticleStatus.Draft;
    public int DisplayOrder { get; set; }
    public DateTime? PublishedAt { get; set; }
}
