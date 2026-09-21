namespace QMgr.Application.Interfaces;

/// <summary>
/// Reads the TXT records at a name. An interface with one method because it is the ONE thing in
/// domain verification that needs the outside world: stub it and the whole of Phase 2 can be
/// exercised against the dev tenant without owning a zone, which is what
/// <c>scripts/e2e/custom-domain-e2e.mjs</c> does.
/// </summary>
public interface IDnsTxtLookup
{
    /// <summary>
    /// Every TXT string published at <paramref name="name"/>. An empty list means "nothing found",
    /// which is the same answer as "no such name" ON PURPOSE — a tenant who has not created the
    /// record yet and a tenant who typed the host wrong need the same next step, and telling the
    /// two apart would leak whether a zone exists.
    /// </summary>
    Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken = default);
}
