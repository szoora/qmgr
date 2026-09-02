using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Welfare;

/// <summary>
/// Admin-defined reason a WelfareRecord gets filed under (e.g. "Academic Excellence", "Bullying",
/// "Home Situation") — same "admin picks it, never auto-derived" convention as the roster's own
/// ClassColorSettingsDto, and the same reasoning: a hashed/guessed category would be a category
/// nobody actually chose. Org-scoped, not branch-scoped — a multi-campus tenant defines one
/// category list for every branch, matching how Service Types already work.
/// </summary>
public class WelfareCategory : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }

    public WelfareCaseType CaseType { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Pre-fills a new record's Tier — staff can still override per record.</summary>
    public WelfareTier DefaultTier { get; set; } = WelfareTier.Low;

    /// <summary>
    /// Signed default merit/demerit points (positive for Achievement categories, negative for
    /// Behavior, null for Welfare — a concern isn't scored). Pre-fills, doesn't lock the value.
    /// </summary>
    public int? DefaultPoints { get; set; }

    /// <summary>Hex color for the category chip — validated against the existing HexColor pattern used for branch branding overrides.</summary>
    public string? Color { get; set; }

    public int SortOrder { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual ICollection<WelfareRecord> Records { get; set; } = new List<WelfareRecord>();
}
