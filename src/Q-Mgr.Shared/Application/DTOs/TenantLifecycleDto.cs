namespace QMgr.Application.DTOs;

/// <summary>
/// Where a tenant is in its life, for the platform's Tenants page. Lives in Q-Mgr.Shared because
/// the API produces it and the Web renders it — the DTO-duplication rule this codebase has now
/// hit seven times.
/// </summary>
public record TenantLifecycleStatusDto
{
    public Guid OrganizationId { get; init; }

    /// <summary>The <c>TenantStatus</c> name, as a string on the wire.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>When the sweep will move this tenant on by itself. Null when nothing will.</summary>
    public DateTime? NextTransitionDueAt { get; init; }

    /// <summary>What it will become. Null when nothing will.</summary>
    public string? NextStatus { get; init; }

    /// <summary>Days left on the current clock, floored at zero. Null when there is no clock.</summary>
    public int? DaysUntilNextTransition { get; init; }

    /// <summary>True once the tenant is in PendingDeletion — the last window, and still reversible.</summary>
    public bool IsScheduledForDeletion { get; init; }

    /// <summary>Whether this tenant ever transacted. Decides whether a purge can take everything.</summary>
    public bool HasFinancialRecords { get; init; }

    public IReadOnlyList<TenantLifecycleEventDto> History { get; init; } = [];
}

public record TenantLifecycleEventDto
{
    public string? FromStatus { get; init; }
    public string ToStatus { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? Reason { get; init; }
}

/// <summary>
/// What a tenant is made of, before anything is deleted. Read-only, and useful on its own: it is
/// how "what exactly is this tenant" gets an answer, and it is what a purge is checked against.
/// </summary>
public record TenantResidueDto
{
    public Guid OrganizationId { get; init; }
    public string OrganizationName { get; init; } = string.Empty;

    /// <summary>Table name to row count, for every table this tenant has rows in.</summary>
    public IReadOnlyDictionary<string, int> RowsByTable { get; init; } = new Dictionary<string, int>();

    public int TotalRows { get; init; }

    /// <summary>Uploaded files this tenant owns, by stored file name.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Total bytes on disk, for the files that are still there.</summary>
    public long FileBytes { get; init; }

    /// <summary>Files a row points at that are NOT on disk — already gone, or never written.</summary>
    public int FilesMissing { get; init; }

    /// <summary>
    /// Queued or recently-run background jobs carrying this tenant's id. These live in Hangfire's
    /// own schema, in the same database, and the EF model knows nothing about them — their
    /// arguments carry recipient email addresses and message bodies.
    /// </summary>
    public int BackgroundJobs { get; init; }

    /// <summary>Cache keys that would still answer for this tenant.</summary>
    public IReadOnlyList<string> CacheKeys { get; init; } = [];

    /// <summary>Things outside this system: a Stripe customer, a live custom domain and its certificate.</summary>
    public IReadOnlyList<string> ExternalReferences { get; init; } = [];

    /// <summary>Rows a statute requires to be kept, which a purge de-identifies rather than deletes.</summary>
    public IReadOnlyDictionary<string, int> RetainedByTable { get; init; } = new Dictionary<string, int>();
}

/// <summary>The outcome of a purge, and the proof it worked.</summary>
public record TenantPurgeResultDto
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public Guid OrganizationId { get; init; }
    public IReadOnlyDictionary<string, int> RowsDeleted { get; init; } = new Dictionary<string, int>();
    public int TotalRowsDeleted { get; init; }
    public int FilesDeleted { get; init; }
    public int FilesFailed { get; init; }
    public int BackgroundJobsRemoved { get; init; }
    public int CacheKeysDropped { get; init; }
    public int RowsRetained { get; init; }

    /// <summary>
    /// The completeness check, read from <c>information_schema</c> rather than the EF model, after
    /// the deletes and BEFORE the commit. False means the whole transaction was rolled back.
    /// </summary>
    public bool VerificationPassed { get; init; }
    public string? VerificationDetail { get; init; }
    public int DurationMs { get; init; }
}

public record PurgeTenantRequest
{
    /// <summary>The organisation's name, typed back. A purge is irreversible; a misclick must not reach it.</summary>
    public string ConfirmName { get; init; } = string.Empty;
    public string? Reason { get; init; }
}

public record TransitionTenantRequest
{
    public string Status { get; init; } = string.Empty;
    public string? Reason { get; init; }
}
