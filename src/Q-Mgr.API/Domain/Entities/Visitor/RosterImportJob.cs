using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Visitor;

/// <summary>
/// One bulk roster upload (admin UI file import, or an external School Management Information
/// System pushing a sync) — tracked as a real background job, not processed inline on the
/// request, since a school-wide roster can be thousands of rows and a request thread has no
/// business blocking on that. Progress is broadcast live over the same notification hub visitor
/// activity already uses (see IRosterImportBroadcaster); this row plus its RosterImportJobEntry
/// children are the durable record — the "logger" — of what actually happened, since a live
/// broadcast alone is lost the moment nobody's watching.
/// </summary>
public class RosterImportJob : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }

    // Null when triggered by an API-key-authenticated external sync rather than an admin in the UI.
    public Guid? CreatedByUserId { get; set; }

    public string? SourceFileName { get; set; }

    // "admin_ui" or "api_sync" — which of the two entry points created this job, purely
    // informational (the job history view labels rows differently for each).
    public string Source { get; set; } = "admin_ui";

    // Which shape RowsJson holds and which processor branch handles it — a roster
    // (student+guardian) upload, or a historical welfare-ledger backfill. Same table, same
    // Entries log, same live progress channel; only the per-row work differs.
    public RosterImportKind Kind { get; set; } = RosterImportKind.Roster;

    // The uploaded rows themselves (serialized List&lt;RosterImportRow&gt; for Kind=Roster,
    // List&lt;WelfareImportRow&gt; for Kind=Welfare), stashed here because
    // the background job runs on a Hangfire worker with no access to the original HTTP request —
    // this is what it reads to actually do the import. Not exposed on RosterImportJobDto; nobody
    // needs the raw payload back once the job's real output (the Entries) exists.
    public string RowsJson { get; set; } = "[]";

    public RosterImportStatus Status { get; set; } = RosterImportStatus.Pending;

    public int TotalRows { get; set; }
    public int ProcessedRows { get; set; }
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int FailedCount { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Set only if the whole job blew up (e.g. a DB error mid-run) rather than individual rows
    // failing validation — per-row failures live on RosterImportJobEntry instead.
    public string? FailureReason { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ICollection<RosterImportJobEntry> Entries { get; set; } = new List<RosterImportJobEntry>();
}
