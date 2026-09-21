using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Data.Purge;

/// <summary>
/// Refuses to let the application start when a table in the model is not classified in
/// <see cref="TenantDataManifest"/>.
///
/// THIS IS THE WHOLE "flexible for future features" MECHANISM. Everything else about the purge is
/// derived from the model and needs no maintenance; the one judgement that cannot be derived is
/// whether a reachable table is the tenant's or the platform's, and this is what stops somebody
/// forgetting it. A developer who adds a table gets a specific error naming their new entity and
/// the four options, on their own machine, the first time they run the app.
///
/// It FAILS HARD rather than logging. Every other startup reconciliation in this codebase logs and
/// carries on — a missing catalogue row is not worth a dead API — but this one is different: the
/// cost of shipping an unclassified table is that a purge reports success while leaving the rows
/// behind, which is the exact failure the whole design exists to prevent, and it would be found
/// long after the tenant was told their data was gone.
/// </summary>
public static class TenantPurgeModelGuard
{
    public static void Validate(QMgrDbContext db, ILogger logger)
    {
        var (unclassified, stale) = TenantPurgeModel.Validate(db.Model);

        if (unclassified.Count > 0)
        {
            throw new InvalidOperationException(
                $"Tenant purge manifest is incomplete. {unclassified.Count} table(s) in the model are not classified: " +
                $"{string.Join(", ", unclassified)}. " +
                $"Add each to {nameof(TenantDataManifest)}.{nameof(TenantDataManifest.ByEntity)} as one of: " +
                $"{nameof(TenantDataClass.TenantOwned)} (it has its own OrganizationId), " +
                $"{nameof(TenantDataClass.TenantDerived)} (it reaches one through a parent), " +
                $"{nameof(TenantDataClass.PlatformOwned)} (it is shared and must survive a purge), or " +
                $"{nameof(TenantDataClass.StatutoryRetention)} (a statute requires it to be kept). " +
                $"Without this, a tenant purge would silently leave those rows behind.");
        }

        if (stale.Count > 0)
        {
            // A name in the manifest that the model no longer has. Not dangerous — nothing is
            // skipped — so it is a warning rather than a stop, but it means somebody removed a
            // table and left the line, and the next person reads a list that is not the schema.
            logger.LogWarning(
                "Tenant purge manifest has {Count} stale entry/entries for tables no longer in the model: {Names}. Remove them from {Manifest}.",
                stale.Count, string.Join(", ", stale), nameof(TenantDataManifest));
        }

        // Building it here rather than only on first use means a modelling mistake — a table
        // classified as the tenant's with no path to one — is found at startup too, not on the
        // night somebody actually purges a tenant.
        var model = TenantPurgeModel.Build(db.Model);
        logger.LogInformation(
            "Tenant purge plan ready: {Steps} table(s) to empty, {Retained} retained under statute, deepest path {Depth} hop(s).",
            model.Steps.Count, model.Retained.Count, model.Steps.Count == 0 ? 0 : model.Steps.Max(s => s.Depth));
    }
}
