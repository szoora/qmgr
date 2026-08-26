using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Visitor;

/// <summary>
/// The outcome of processing exactly one row of a RosterImportJob — one row in, one entry out,
/// always, even for a row that failed. This is the actual "logger" the job's summary counts are
/// computed from, and what the per-import drill-down view reads.
/// </summary>
public class RosterImportJobEntry : BaseEntity
{
    public Guid RosterImportJobId { get; set; }

    public int RowNumber { get; set; }

    // Denormalized from the row so the log is readable without joining back to Student/
    // VisitorProfile — and still readable if this row Failed and nothing was actually created.
    public string? StudentCode { get; set; }
    public string? StudentName { get; set; }
    public string? GuardianName { get; set; }

    public RosterImportRowOutcome Outcome { get; set; }

    // Human-readable detail: which validation rule failed, or "Matched existing student by code",
    // or "Duplicate of row 12 in this file" — always populated, not just on failure.
    public string Message { get; set; } = string.Empty;

    public Guid? StudentId { get; set; }
    public Guid? GuardianProfileId { get; set; }

    public virtual RosterImportJob? RosterImportJob { get; set; }
}
