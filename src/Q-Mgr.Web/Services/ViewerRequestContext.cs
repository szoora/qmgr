namespace QMgr.Web.Services;

/// <summary>
/// Holds the browser's own address and user agent for the life of a circuit, so that EVERY
/// authenticated Web→API call can relay them as X-Viewer-Ip / X-Viewer-Agent (see
/// <see cref="AuthenticationMessageHandler"/>). Until 2026-09-16 the relay existed only on the
/// public share endpoints, as an explicit parameter; the staff activity log needs it on every
/// write, or each row would name the Web server's loopback as the actor's address.
///
/// Scoped per circuit. Set once by Routes.razor from the cascaded <see cref="ViewerRequestInfo"/>
/// App.razor captured from the host request.
/// </summary>
public sealed class ViewerRequestContext
{
    public ViewerRequestInfo? Viewer { get; private set; }

    public void Set(ViewerRequestInfo? viewer)
    {
        if (viewer != null) Viewer = viewer;
    }
}
