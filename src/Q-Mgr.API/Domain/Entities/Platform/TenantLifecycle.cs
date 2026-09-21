using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Platform;

/// <summary>
/// One move between lifecycle states, written down rather than inferred.
///
/// Without this, "why was this tenant deleted?" has no answer — and that is the question that gets
/// asked exactly once and matters enormously when it is. A tenant's status column only ever holds
/// where it is now; this is how it got there.
///
/// PLATFORM-OWNED on purpose: it outlives the organization it is about, because the most important
/// row in it is the last one. It carries no personal data — an id, two states, a timestamp, who
/// acted and a short reason — so keeping it after a purge leaves no trace of anybody.
/// </summary>
public class TenantLifecycleEvent : BaseEntity
{
    /// <summary>Deliberately NOT a foreign key: the organization is gone by the time the last of these is written.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Kept so a platform administrator reading the log does not have to resolve a dead id. Not personal data.</summary>
    public string OrganizationName { get; set; } = string.Empty;

    public TenantStatus? FromStatus { get; set; }
    public TenantStatus ToStatus { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>The administrator who acted, or null when a clock did.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>"tenant-lifecycle-sweep" when a clock did it, else the administrator's display name.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Short, and in a person's words: "trial expired", "requested by the school", "abuse".</summary>
    public string? Reason { get; set; }

    /// <summary>When the NEXT automatic move becomes due, so the platform UI can show a clock rather than a date somebody has to work out.</summary>
    public DateTime? NextTransitionDueAt { get; set; }
}

/// <summary>
/// What is left of a tenant after a purge, and the only thing that is.
///
/// IT HOLDS NO PERSONAL DATA. Not a name, not an address, not an email — two one-way hashes and
/// some counts. You cannot read a person or an organization out of it; you can only ask "does this
/// new sign-up match something that was purged", which is the single question it exists to answer.
///
/// Three reasons it earns its place:
///   - The registration guard keeps working. Without it a purged tenant re-registers tomorrow with
///     no signal at all, and the whole premise of this feature is a high volume of trials.
///   - "What happened to this tenant" has an answer. A deletion nobody can account for is a worse
///     audit position than one that is recorded, and Uganda's Data Protection and Privacy Act asks
///     controllers to be able to DEMONSTRATE compliance, not merely to comply.
///   - It is the ICO's own condition for the backup accommodation: a suppression list of erased
///     identifiers that any restore path checks. <c>qmgr-restore-db.sh</c> reads this table.
/// </summary>
public class TenantTombstone : BaseEntity
{
    /// <summary>The organization's id. Points at nothing now, and is what the restore hook matches on.</summary>
    public Guid OrganizationId { get; set; }

    public DateTime PurgedAt { get; set; } = DateTime.UtcNow;

    public Guid? PurgedByUserId { get; set; }
    public string PurgedBy { get; set; } = string.Empty;

    /// <summary>"trial expired", "requested", "abuse". Not free text about a person.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// SHA-256 of the sign-up email's DOMAIN — never the address. A domain is not a person, and the
    /// duplicate check only ever asks whether a new sign-up looks like a returning one.
    /// </summary>
    public string? EmailDomainHash { get; set; }

    /// <summary>SHA-256 of the organization's NameBlockingKey, so RegistrationGuardService can still flag a re-registration.</summary>
    public string? NameKeyHash { get; set; }

    /// <summary>Whether this tenant ever transacted. Decides whether any financial row was retained at all.</summary>
    public bool HadFinancialRecords { get; set; }

    /// <summary>When the retained financial rows may themselves be deleted. Null when there are none.</summary>
    public DateTime? StatutoryRetentionUntil { get; set; }
}

/// <summary>
/// The proof. One row per purge, holding what was deleted and whether the completeness check
/// passed — written where the tenant no longer is.
///
/// NIST SP 800-88 calls verification "the attribute that provides confidence that data has been
/// sufficiently sanitized", and Uganda's DPPA requires destruction "in a manner that prevents its
/// reconstruction in an intelligible form". Neither is satisfied by a DELETE that returned without
/// throwing. This is what answers an auditor, a regulator, or a customer asking what happened.
/// </summary>
public class TenantPurgeCertificate : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public DateTime PurgedAt { get; set; } = DateTime.UtcNow;
    public string PurgedBy { get; set; } = string.Empty;

    /// <summary>Per-table counts, as JSON. What was actually removed, table by table.</summary>
    public string RowsDeletedJson { get; set; } = "{}";

    public int FilesDeleted { get; set; }
    public int FilesFailed { get; set; }
    public int BackgroundJobsRemoved { get; set; }
    public int CacheKeysDropped { get; set; }

    /// <summary>De-identified rather than deleted, because a statute requires them.</summary>
    public int RowsRetained { get; set; }

    /// <summary>
    /// The completeness check: every table in <c>information_schema</c> re-read after the delete
    /// and before the commit. False means the transaction was rolled back and nothing happened.
    /// </summary>
    public bool VerificationPassed { get; set; }

    /// <summary>What was still there, when it failed. Table names and counts only.</summary>
    public string? VerificationDetail { get; set; }

    public int DurationMs { get; set; }
}
