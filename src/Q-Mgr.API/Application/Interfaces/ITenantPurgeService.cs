using QMgr.Application.DTOs;

namespace QMgr.Application.Interfaces;

/// <summary>
/// Empties a tenant out of the system, and proves it.
///
/// THE ORDER IS LOAD-BEARING and the first step is the one that cannot be recovered from:
///
///   1. FILES FIRST. Uploads are stored under a generated GUID name in one flat directory, and
///      nothing in the name says which tenant a file belongs to. Once the rows are gone the bytes
///      are unattributable and permanent — a photograph of an injured child, a visitor's face, a
///      welfare attachment. So every path is collected WHILE THE ROWS STILL EXIST. Getting this
///      backwards is not fixable.
///   2. Background jobs. Hangfire runs on PostgreSQL storage, so queued job arguments sit in the
///      same database in a schema the EF model knows nothing about — and they carry recipient
///      email addresses and message bodies.
///   3. Rows, dependents first, in one transaction, on the order <see cref="Infrastructure.Data.Purge.TenantPurgeModel"/> computes.
///   4. Statutory rows de-identified rather than deleted.
///   5. VERIFY, before the commit. Every table re-read from information_schema — not from the EF
///      model, so a table the model forgot is still checked. Anything left rolls the whole thing back.
///   6. Caches, the custom domain, and the external processor, after the commit.
/// </summary>
public interface ITenantPurgeService
{
    /// <summary>
    /// What this tenant is made of: rows per table, files, background jobs, cache keys, external
    /// references. READ-ONLY, and useful on its own — it is how "what exactly is this tenant" gets
    /// an answer, and it is what a purge is checked against afterwards.
    /// </summary>
    Task<TenantResidueDto> InventoryAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Irreversible. Writes a <c>TenantPurgeCertificate</c> and a <c>TenantTombstone</c> whatever
    /// the outcome, because a failed purge is a thing somebody needs to know about too.
    /// </summary>
    Task<TenantPurgeResultDto> PurgeAsync(Guid organizationId, string actor, Guid? actorUserId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-checks a sample of recent purges by a DIFFERENT route from the one that made them, and
    /// reports anything that came back. NIST SP 800-88's representative-sampling verification,
    /// translated: the point is that a bug in the purge must not be able to hide in the check.
    /// </summary>
    Task<IReadOnlyList<string>> ReVerifyRecentAsync(int sampleSize, CancellationToken cancellationToken = default);
}
