using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// A notice to staff: a branch, a set of departments, a set of roles, or a staff group; optionally
/// pinned, optionally requiring acknowledgement, with Library documents attached. The closest
/// structural template was <c>DocArticle</c> (rich body, publish state), but that is platform-wide
/// and anonymous by design; Broadcasts target the public with a mandatory unsubscribe footer.
///
/// Read state is NOT a table: publishing fans out one <c>Notification</c> per recipient, whose
/// <c>ReadAt</c> already answers "who has seen it". Acknowledgements are a jsonb map here
/// (userId → timestamp), adequate for a branch of a few hundred staff; a table only if
/// acknowledgement reporting across notices is ever needed.
/// </summary>
public class StaffNotice : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }

    /// <summary>Null = every branch of the organization.</summary>
    public Guid? BranchId { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Sanitised on write, as DocArticle.BodyHtml is.</summary>
    public string BodyHtml { get; set; } = string.Empty;

    public Guid[]? AudienceDepartmentIds { get; set; }
    public string[]? AudienceRoleCodes { get; set; }
    /// <summary>Restrict to one staff group by name (see StaffGroups). Null = every group.</summary>
    [MaxLength(60)]
    public string? AudienceStaffGroup { get; set; }

    public DateTime PublishAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }

    public bool IsPinned { get; set; }
    public bool RequiresAcknowledgement { get; set; }

    /// <summary>Library documents attached to the notice.</summary>
    public Guid[]? AttachmentMediaContentIds { get; set; }

    /// <summary>jsonb: { "userId": "2026-09-16T08:00:00Z", … }. Read and written only through StaffNoticesController's helpers.</summary>
    public string Acknowledgements { get; set; } = "{}";

    public Guid PublishedByUserId { get; set; }

    /// <summary>Set once the per-recipient Notification rows have been created; the fan-out is idempotent on it.</summary>
    public DateTime? NotificationsSentAt { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
}
