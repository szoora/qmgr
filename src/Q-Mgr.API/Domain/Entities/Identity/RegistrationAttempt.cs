using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Identity;

/// <summary>
/// One sign-up attempt and what the duplicate check decided about it.
/// <para>
/// This is a genuinely new resource rather than columns on Organization, which the project's
/// enhance-before-you-add rule would otherwise prefer: most attempts never become an organization
/// at all (they are blocked, or abandoned at the phone step), and the ones that do still need their
/// own reviewable lifecycle afterwards. There is nothing to hang the record on.
/// </para>
/// <para>
/// It also exists so thresholds can be tuned from evidence. Every decision is stored with the score
/// and the signals that produced it, so a platform administrator can see why something was flagged
/// and whether the weights are behaving, instead of guessing at numbers.
/// </para>
/// </summary>
public class RegistrationAttempt : BaseEntity
{
    /// <summary>Address exactly as typed, kept so an administrator reviewing a flag sees what the applicant saw.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Canonical form actually used for matching. This is what the duplicate check compares.</summary>
    public string NormalizedEmail { get; set; } = string.Empty;

    public string? Phone { get; set; }
    public string? NormalizedPhone { get; set; }
    public bool PhoneWasVerified { get; set; }

    public string OrganizationName { get; set; } = string.Empty;
    public string? NormalizedName { get; set; }
    public string? NameBlockingKey { get; set; }

    public string? ContactFirstName { get; set; }
    public string? ContactLastName { get; set; }

    /// <summary>
    /// Hashed, never the raw address. Ugandan mobile carriers use carrier-grade NAT and offices
    /// share one address, so this is only ever a weak signal; storing it hashed keeps it usable for
    /// correlation without retaining a directly identifying value longer than necessary.
    /// </summary>
    public string? ClientAddressHash { get; set; }

    public RegistrationRiskDecision Decision { get; set; }

    /// <summary>Total risk score. See RegistrationGuardService for the weights and thresholds.</summary>
    public int RiskScore { get; set; }

    /// <summary>Human-readable reasons, newline separated, as shown in the review queue.</summary>
    public string? Signals { get; set; }

    /// <summary>Set when the attempt actually created an organization, so a reviewer can act on it.</summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>Existing organization this most closely resembles, when the check found one.</summary>
    public Guid? MatchedOrganizationId { get; set; }

    /// <summary>Null while a flagged attempt is still awaiting review.</summary>
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewNotes { get; set; }

    /// <summary>What the reviewer concluded. Null means nobody has looked at it yet.</summary>
    public RegistrationReviewOutcome? ReviewOutcome { get; set; }
}
