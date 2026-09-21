namespace QMgr.Infrastructure.Data.Purge;

/// <summary>
/// What a table IS, as far as emptying a tenant out of the database is concerned.
///
/// A model walk can work out that a table is REACHABLE from an organization. It cannot work out
/// whether it SHOULD be emptied: <c>subscription_plans</c> is the module catalogue every tenant
/// shares, <c>permissions</c> is the platform's own list, and an invoice has to outlive the
/// customer by five years because Uganda's Tax Procedures Code says so. That judgement is the one
/// thing in the purge that is declared rather than derived, and it lives in
/// <see cref="TenantDataManifest"/>.
/// </summary>
public enum TenantDataClass
{
    /// <summary>
    /// Carries its own <c>OrganizationId</c>. Deleted by that column.
    /// </summary>
    TenantOwned,

    /// <summary>
    /// Tenant data that reaches the organization only through a parent — a welfare note through
    /// its record, a token through its branch. Deleted through the foreign-key path
    /// <see cref="TenantPurgeModel"/> computes, never through a hand-written join.
    /// </summary>
    TenantDerived,

    /// <summary>
    /// Belongs to the platform, shared by every tenant, and is NEVER touched by a purge. Deleting
    /// one of these while emptying one tenant would break every other tenant on the box.
    /// </summary>
    PlatformOwned,

    /// <summary>
    /// Tenant data a statute requires us to keep after the tenant has gone. It is DE-IDENTIFIED in
    /// place rather than deleted — the amounts, dates and references a tax audit needs are kept and
    /// the contact details are blanked — and then removed on its own clock.
    ///
    /// Uganda's Tax Procedures Code requires tax records for five years after the end of the tax
    /// period. Note the shape of that: it binds only where a record EXISTS. A trial that never paid
    /// raised no invoice and settled no payment, so it has nothing in this class at all and purges
    /// whole — which is the common case here, not the exception.
    /// </summary>
    StatutoryRetention
}
