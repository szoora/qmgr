using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// Server-side token storage for the current user session/circuit.
/// This works with both component context and HTTP handlers.
/// </summary>
public interface ITokenStorageService
{
    string? AccessToken { get; set; }
    string? RefreshToken { get; set; }
    UserInfo? UserInfo { get; set; }
    void Clear();
}

public class TokenStorageService : ITokenStorageService
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public UserInfo? UserInfo { get; set; }

    public void Clear()
    {
        AccessToken = null;
        RefreshToken = null;
        UserInfo = null;
    }
}
