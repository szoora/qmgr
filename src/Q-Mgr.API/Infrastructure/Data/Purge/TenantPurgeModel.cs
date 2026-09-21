using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace QMgr.Infrastructure.Data.Purge;

/// <summary>
/// The purge plan, DERIVED from the EF model rather than written down.
///
/// EF already knows the whole schema at runtime — every entity, every column, every foreign key
/// and its delete behaviour. So instead of a list of tables in an order somebody maintains, this
/// walks that graph: it finds each table's shortest foreign-key path back to an
/// <c>OrganizationId</c>, builds the SQL predicate for that path, and orders the tables so every
/// dependent is emptied before the thing it depends on. Add a table next month with an
/// <c>OrganizationId</c> and it is picked up with no code change; add one that hangs off a parent
/// and it is picked up too, because the path comes from the foreign key the migration already made.
///
/// The only thing asked of the developer is one line in <see cref="TenantDataManifest"/>, and
/// <see cref="Validate"/> refuses to let the application start without it.
/// </summary>
public sealed class TenantPurgeModel
{
    public const string OrganizationIdColumn = "OrganizationId";

    /// <summary>One table to empty: its SQL identifier, and the WHERE clause that selects one tenant's rows.</summary>
    public sealed record PurgeStep(
        string EntityName,
        string QualifiedTable,
        TenantDataClass Class,
        string WhereClause,
        int Depth,
        string PathDescription);

    /// <summary>Every tenant table, dependents first. The order the deletes run in.</summary>
    public IReadOnlyList<PurgeStep> Steps { get; }

    /// <summary>Tables kept and de-identified rather than deleted, with the columns to blank.</summary>
    public IReadOnlyList<(PurgeStep Step, string[] Columns)> Retained { get; }

    /// <summary>Every table in the model that is NOT the platform's, for the completeness check to look at.</summary>
    public IReadOnlyList<PurgeStep> AllTenantTables { get; }

    private TenantPurgeModel(IReadOnlyList<PurgeStep> steps, IReadOnlyList<(PurgeStep, string[])> retained, IReadOnlyList<PurgeStep> all)
    {
        Steps = steps;
        Retained = retained;
        AllTenantTables = all;
    }

    /// <summary>
    /// Entity names in the model that <see cref="TenantDataManifest"/> does not classify, and
    /// names in the manifest that no longer exist in the model. Both are errors: the first means a
    /// new table would be silently skipped by every purge, the second means a stale line nobody
    /// removed when a table went.
    /// </summary>
    public static (IReadOnlyList<string> Unclassified, IReadOnlyList<string> Stale) Validate(IModel model)
    {
        var inModel = model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.GetTableName() != null)
            .Select(e => e.ClrType.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = inModel.Where(n => !TenantDataManifest.ByEntity.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var stale = TenantDataManifest.ByEntity.Keys.Where(n => !inModel.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return (unclassified, stale);
    }

    public static TenantPurgeModel Build(IModel model)
    {
        var entities = model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.GetTableName() != null)
            .ToList();

        var byClr = entities.ToDictionary(e => e.ClrType.Name, e => e, StringComparer.Ordinal);

        // ── 1. Reach: for each tenant table, the shortest FK path to an OrganizationId ─────────
        var reach = new Dictionary<string, (string Where, int Depth, string Path)>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            var name = entity.ClrType.Name;
            if (!TenantDataManifest.ByEntity.TryGetValue(name, out var cls)) continue;
            if (cls == TenantDataClass.PlatformOwned) continue;

            if (name == "Organization")
            {
                // The organization is found by its own key, not by a column pointing at itself.
                reach[name] = ($"\"Id\" = @org", 0, "itself");
                continue;
            }

            if (HasColumn(entity, OrganizationIdColumn))
            {
                reach[name] = ($"\"{OrganizationIdColumn}\" = @org", 0, OrganizationIdColumn);
                continue;
            }

            var path = FindPath(entity, byClr);
            if (path == null)
            {
                // Classified as the tenant's, but nothing in the model connects it to one. That is
                // a modelling mistake rather than a purge mistake, and it must be loud: silently
                // skipping the table is exactly the failure this whole design exists to prevent.
                throw new InvalidOperationException(
                    $"'{name}' is classified as tenant data but has no foreign-key path to an {OrganizationIdColumn}. " +
                    $"Either give it one, or classify it as {nameof(TenantDataClass.PlatformOwned)} in {nameof(TenantDataManifest)}.");
            }

            reach[name] = path.Value;
        }

        // ── 2. Order: dependents before principals ────────────────────────────────────────────
        var ordered = TopologicalOrder(entities.Where(e => reach.ContainsKey(e.ClrType.Name)).ToList());

        var steps = new List<PurgeStep>();
        var retained = new List<(PurgeStep, string[])>();
        var all = new List<PurgeStep>();

        foreach (var entity in ordered)
        {
            var name = entity.ClrType.Name;
            var (where, depth, path) = reach[name];
            var cls = TenantDataManifest.ByEntity[name];
            var step = new PurgeStep(name, Qualified(entity), cls, where, depth, path);

            all.Add(step);
            if (cls == TenantDataClass.StatutoryRetention)
                retained.Add((step, TenantDataManifest.DeIdentifyColumns.TryGetValue(name, out var cols) ? cols : []));
            else
                steps.Add(step);
        }

        return new TenantPurgeModel(steps, retained, all);
    }

    /// <summary>
    /// Breadth-first over the foreign keys this entity depends on, stopping at the first entity
    /// that carries an <c>OrganizationId</c> (or at the organization itself). Breadth-first rather
    /// than depth-first so the predicate is the SHORTEST path — a welfare note reaches the tenant
    /// through its record in one hop, not through its record's student's branch in three.
    /// </summary>
    private static (string Where, int Depth, string Path)? FindPath(IEntityType start, Dictionary<string, IEntityType> byClr)
    {
        var queue = new Queue<(IEntityType Entity, List<IForeignKey> Path)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { start.ClrType.Name };
        queue.Enqueue((start, []));

        while (queue.Count > 0)
        {
            var (entity, path) = queue.Dequeue();
            if (path.Count > 6) continue; // a path this long is a modelling problem, not a purge one

            foreach (var fk in entity.GetForeignKeys())
            {
                var principal = fk.PrincipalEntityType;
                if (principal.IsOwned() || principal.GetTableName() == null) continue;

                var next = path.Append(fk).ToList();
                var principalName = principal.ClrType.Name;

                // Reached something that knows its own tenant — build the nested predicate.
                if (principalName == "Organization" || HasColumn(principal, OrganizationIdColumn))
                {
                    var anchorColumn = principalName == "Organization" ? "Id" : OrganizationIdColumn;
                    return (BuildNestedWhere(next, anchorColumn), next.Count, Describe(next));
                }

                if (!seen.Add(principalName)) continue;
                if (!TenantDataManifest.ByEntity.TryGetValue(principalName, out var cls) || cls == TenantDataClass.PlatformOwned) continue;

                queue.Enqueue((principal, next));
            }
        }

        return null;
    }

    /// <summary>
    /// Turns a foreign-key path into nested EXISTS clauses.
    ///
    /// EXISTS rather than IN on purpose: an <c>IN (SELECT ...)</c> over a nullable column has
    /// surprising three-valued-logic behaviour, and Postgres plans a correlated EXISTS as a
    /// semi-join anyway. Each level is aliased by depth so a self-referencing table (a department
    /// with a parent department) cannot ambiguate its own columns.
    /// </summary>
    private static string BuildNestedWhere(List<IForeignKey> path, string anchorColumn)
    {
        var sql = new StringBuilder();

        for (var i = 0; i < path.Count; i++)
        {
            var fk = path[i];
            var childAlias = i == 0 ? "t" : $"a{i}";
            var parentAlias = $"a{i + 1}";
            var parentTable = Qualified(fk.PrincipalEntityType);

            var joins = string.Join(" AND ", fk.Properties.Select((p, n) =>
                $"{parentAlias}.\"{fk.PrincipalKey.Properties[n].GetColumnName()}\" = {childAlias}.\"{p.GetColumnName()}\""));

            sql.Append($"EXISTS (SELECT 1 FROM {parentTable} {parentAlias} WHERE {joins} AND ");

            // The innermost level is the one that names the tenant.
            if (i == path.Count - 1)
                sql.Append($"{parentAlias}.\"{anchorColumn}\" = @org");
        }

        sql.Append(new string(')', path.Count));
        return sql.ToString();
    }

    private static string Describe(List<IForeignKey> path)
        => string.Join(" -> ", path.Select(fk => fk.PrincipalEntityType.ClrType.Name));

    private static bool HasColumn(IEntityType entity, string column)
        => entity.GetProperties().Any(p => string.Equals(p.GetColumnName(), column, StringComparison.Ordinal));

    private static string Qualified(IEntityType entity)
    {
        var schema = entity.GetSchema() ?? "qmgr";
        return $"\"{schema}\".\"{entity.GetTableName()}\"";
    }

    /// <summary>
    /// Dependents before principals, so the 36 <c>Restrict</c> foreign keys pointing at
    /// <c>organizations</c> are satisfied rather than fought.
    ///
    /// The graph has a genuine cycle — <c>Organization.SubscriptionId</c> points at
    /// <c>Subscription</c> while <c>Subscription.OrganizationId</c> points back — so this is a
    /// depth-first order that TOLERATES cycles rather than a strict topological sort that would
    /// throw on one. The purge breaks that particular cycle explicitly before it starts by nulling
    /// the organization's subscription pointer, which is what its own SetNull behaviour intends.
    /// </summary>
    private static List<IEntityType> TopologicalOrder(List<IEntityType> entities)
    {
        var names = entities.Select(e => e.ClrType.Name).ToHashSet(StringComparer.Ordinal);
        var order = new List<IEntityType>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 unseen, 1 visiting, 2 done

        void Visit(IEntityType entity)
        {
            var name = entity.ClrType.Name;
            if (state.TryGetValue(name, out var s) && s != 0) return;
            state[name] = 1;

            // Anything that DEPENDS ON this entity must be emptied first.
            foreach (var fk in entity.GetReferencingForeignKeys())
            {
                var dependent = fk.DeclaringEntityType;
                if (dependent.IsOwned() || dependent.GetTableName() == null) continue;
                if (!names.Contains(dependent.ClrType.Name)) continue;
                if (state.TryGetValue(dependent.ClrType.Name, out var ds) && ds == 1) continue; // cycle: leave it
                Visit(dependent);
            }

            state[name] = 2;
            order.Add(entity);
        }

        // The organization is visited last deliberately, so everything reachable from it is
        // already in the list before it is.
        foreach (var entity in entities.Where(e => e.ClrType.Name != "Organization")) Visit(entity);
        foreach (var entity in entities.Where(e => e.ClrType.Name == "Organization")) Visit(entity);

        return order;
    }
}
