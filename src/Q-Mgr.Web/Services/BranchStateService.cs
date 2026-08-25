using Blazored.LocalStorage;

namespace QMgr.Web.Services;

/// <summary>
/// Manages the current branch selection across all components
/// </summary>
public interface IBranchStateService
{
    Guid CurrentBranchId { get; }
    string? CurrentBranchName { get; }
    bool IsInitialized { get; }
    bool HasBranches { get; }
    int BranchCount { get; }
    event Action? OnBranchChanged;
    Task InitializeAsync();
    Task SetBranchAsync(Guid branchId, string branchName);
    Task SetCurrentBranchAsync(Guid branchId);
    Task ClearBranchAsync();
    void SetBranchAvailability(int branchCount);
}

public class BranchStateService : IBranchStateService
{
    private readonly ILocalStorageService _localStorage;
    private Guid _currentBranchId = Guid.Empty;
    private string? _currentBranchName;
    private bool _isInitialized;
    private int _branchCount;

    public Guid CurrentBranchId => _currentBranchId;
    public string? CurrentBranchName => _currentBranchName;
    public bool IsInitialized => _isInitialized;
    public bool HasBranches => _branchCount > 0;
    public int BranchCount => _branchCount;
    public event Action? OnBranchChanged;

    public BranchStateService(ILocalStorageService localStorage)
    {
        _localStorage = localStorage;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        try
        {
            var savedBranchId = await _localStorage.GetItemAsync<string>("qmgr-branch");
            var savedBranchName = await _localStorage.GetItemAsync<string>("qmgr-branch-name");

            if (!string.IsNullOrEmpty(savedBranchId) && Guid.TryParse(savedBranchId, out var branchId))
            {
                _currentBranchId = branchId;
                _currentBranchName = savedBranchName;
            }
        }
        catch
        {
            // Use defaults if local storage fails
        }

        _isInitialized = true;
    }

    public async Task SetBranchAsync(Guid branchId, string branchName)
    {
        if (_currentBranchId != branchId || _currentBranchName != branchName)
        {
            _currentBranchId = branchId;
            _currentBranchName = branchName;

            try
            {
                await _localStorage.SetItemAsync("qmgr-branch", branchId.ToString());
                await _localStorage.SetItemAsync("qmgr-branch-name", branchName);
            }
            catch { }

            OnBranchChanged?.Invoke();
        }
    }

    public async Task SetCurrentBranchAsync(Guid branchId)
    {
        if (_currentBranchId != branchId)
        {
            _currentBranchId = branchId;

            try
            {
                await _localStorage.SetItemAsync("qmgr-branch", branchId.ToString());
            }
            catch { }

            OnBranchChanged?.Invoke();
        }
    }

    public async Task ClearBranchAsync()
    {
        _currentBranchId = Guid.Empty;
        _currentBranchName = null;

        try
        {
            await _localStorage.RemoveItemAsync("qmgr-branch");
            await _localStorage.RemoveItemAsync("qmgr-branch-name");
        }
        catch { }

        OnBranchChanged?.Invoke();
    }

    public void SetBranchAvailability(int branchCount)
    {
        _branchCount = branchCount;
    }
}
