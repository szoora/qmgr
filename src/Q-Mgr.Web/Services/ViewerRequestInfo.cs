namespace QMgr.Web.Services;

/// <summary>
/// The browser's own address and user agent, captured from the host request in App.razor and
/// cascaded into the interactive tree.
///
/// Why it exists: every call this Blazor Server app makes to the API comes from the WEB SERVER's
/// HttpClient, so the API sees the Web box as the caller — in production the loopback, with no
/// browser at all. For the public share viewer that would log every reader as "127.0.0.0, no
/// browser", which defeats the attribution the audit trail exists for. The Web relays these two
/// values as X-Viewer-Ip / X-Viewer-Agent on the public share endpoints, and the API records the
/// truncated / coarsened form (see DocumentShareService.TruncateIp / CoarseUserAgent).
///
/// A JSON-serializable record, because it crosses the static-to-interactive boundary as a
/// parameter of <c>&lt;Routes&gt;</c>.
/// </summary>
public sealed record ViewerRequestInfo(string? IpAddress, string? UserAgent);
