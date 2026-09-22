namespace QMgr.Web.Services;

/// <summary>
/// WHAT THIS DEPLOYMENT IS CALLED, for the parts of the document that are not a component: the tab's
/// title and the tab's icon.
///
/// <para>There are two sources and they answer different questions. <c>TenantHostContext</c> answers
/// "whose name is on this HOST", which is what a signed-out visitor on a tenant's own domain must
/// see. This answers "whose name is on this SESSION" — the organisation the person is signed in to —
/// because most tenants sign in on the platform address and should not need their own domain to get
/// the white-labelling they paid for. That is the same split the attribution rule already makes: the
/// host decides on public pages, the organisation decides inside the shell.</para>
///
/// <para>Scoped, so it lives exactly as long as the circuit. <see cref="MainLayout"/> sets it once
/// the organisation's branding has loaded; everything that shows the name subscribes to
/// <see cref="Changed"/> rather than reading a copy it took earlier.</para>
/// </summary>
public interface IBrandContext
{
    /// <summary>The product's name here. "Q-Mgr" until something better is known.</summary>
    string AppName { get; }

    /// <summary>The tab icon for this session, or null to leave the document's own.</summary>
    string? FaviconUrl { get; }

    event Action? Changed;

    void Set(string? appName, string? faviconUrl);

    /// <summary>
    /// Raised when the organisation's BRANDING has been edited and saved — the Appearance page is
    /// the only thing that raises it. <c>MainLayout</c> re-reads the branding and restamps the
    /// palette, so a new colour, logo or name is on screen at once.
    ///
    /// <para>It is a separate event from <see cref="Changed"/> on purpose. <see cref="Changed"/>
    /// says "the name or the icon this session shows has already moved"; this one says "go and
    /// fetch it again". A component that only renders the title must not pay for a refetch.</para>
    /// </summary>
    event Action? BrandingSaved;

    /// <summary>Tell the shell its branding is stale. Called after a successful save.</summary>
    void NotifyBrandingSaved();
}

public sealed class BrandContext : IBrandContext
{
    private string _appName = "Q-Mgr";

    public string AppName => _appName;

    public string? FaviconUrl { get; private set; }

    public event Action? Changed;

    public event Action? BrandingSaved;

    public void NotifyBrandingSaved() => BrandingSaved?.Invoke();

    /// <summary>
    /// Raises <see cref="Changed"/> only when something actually moved. A layout re-rendering is not
    /// news, and a title component that re-rendered on every one of them would be a render loop
    /// waiting for a slow page.
    /// </summary>
    public void Set(string? appName, string? faviconUrl)
    {
        var name = string.IsNullOrWhiteSpace(appName) ? "Q-Mgr" : appName.Trim();
        var favicon = string.IsNullOrWhiteSpace(faviconUrl) ? null : faviconUrl.Trim();

        if (name == _appName && favicon == FaviconUrl) return;

        _appName = name;
        FaviconUrl = favicon;
        Changed?.Invoke();
    }
}
