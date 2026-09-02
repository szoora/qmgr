namespace QMgr.Web.Services;

/// <summary>
/// Scoped, per-circuit cache of which modules the current org has active, shared between
/// MainLayout (nav gating + the Billing badge count) and any page that can change module
/// ownership (Modules.razor's self-service purchase/remove). Without this, purchasing a module
/// from Modules.razor left the sidebar's "3 available" badge stuck at its stale OnInitializedAsync
/// value until a full page reload — MainLayout is a persistent layout across navigation in Blazor
/// Server, so it never re-runs OnInitializedAsync on its own. Mirrors IBranchStateService's own
/// event-driven refresh pattern exactly.
/// </summary>
public interface IModuleStateService
{
    HashSet<string> ActiveModuleCodes { get; }
    int UnpurchasedCount { get; }
    bool IsLoaded { get; }
    event Action? OnChanged;
    Task LoadAsync(IModuleApiService moduleApi);
    Task RefreshAsync(IModuleApiService moduleApi);
}

public class ModuleStateService : IModuleStateService
{
    private HashSet<string> _activeModuleCodes = new();
    private int _unpurchasedCount;
    private bool _isLoaded;

    public HashSet<string> ActiveModuleCodes => _activeModuleCodes;
    public int UnpurchasedCount => _unpurchasedCount;
    public bool IsLoaded => _isLoaded;
    public event Action? OnChanged;

    public async Task LoadAsync(IModuleApiService moduleApi)
    {
        if (_isLoaded) return;
        await RefreshAsync(moduleApi);
    }

    public async Task RefreshAsync(IModuleApiService moduleApi)
    {
        try
        {
            var mine = await moduleApi.GetMineAsync();
            _activeModuleCodes = mine.Where(m => m.Purchased).Select(m => m.ModuleCode).ToHashSet();
            _unpurchasedCount = mine.Count(m => !m.Purchased);
        }
        catch
        {
            _activeModuleCodes = new();
            _unpurchasedCount = 0;
        }
        finally
        {
            _isLoaded = true;
        }

        OnChanged?.Invoke();
    }
}
