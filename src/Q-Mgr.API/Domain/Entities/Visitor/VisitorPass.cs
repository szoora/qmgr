using QMgr.Domain.Common;

namespace QMgr.Domain.Entities.Visitor;

/// <summary>
/// A single physical/digital badge (QR) that admits a GROUP of visitors together — e.g. a
/// contractor crew or tour group — up to MaxVisitors checked in under it at once. Each member
/// scans in/out individually against the same pass; CurrentVisitors is enforced against
/// MaxVisitors at scan time so the group can never exceed its allowance, but members can
/// arrive/leave staggered rather than as a single all-or-nothing unit.
/// Scanning the pass's QR toggles state per visitor: not-currently-in → check in (if under
/// the cap), currently-in → check out. See VisitorScanService.
/// </summary>
public class VisitorPass : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }

    public string Label { get; set; } = string.Empty; // e.g. "ACME Contractors - Roof Repair"
    public int MaxVisitors { get; set; }
    public int CurrentVisitors { get; set; }

    // Opaque signed token embedded in the pass's QR code (ASP.NET Data Protection), re-validated
    // against this row's ExpiresAt/RevokedAt on every scan rather than trusted at face value —
    // a photographed/copied QR stops working the moment the pass expires or is revoked.
    public string TokenId { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }

    public Guid CreatedByUserId { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ICollection<Visitor> Visits { get; set; } = new List<Visitor>();
}
