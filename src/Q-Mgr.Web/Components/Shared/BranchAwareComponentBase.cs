using Microsoft.AspNetCore.Components;
using QMgr.Web.Services;

namespace QMgr.Web.Components.Shared;

/// <summary>
/// One home for "this page must notice when the branch changes".
///
/// <para>
/// <see cref="IBranchStateService.OnBranchChanged"/> (raised by <c>SetBranchAsync</c>) is this app's
/// ONLY cross-page change signal. There is no <c>OrganizationState</c> service and no
/// organization-changed event: a SuperAdmin picking an organization raises nothing. So a page that
/// reads <see cref="IBranchStateService.CurrentBranchId"/> once and never re-reads it keeps
/// <see cref="Guid.Empty"/> for the life of the circuit, fires requests at
/// <c>…/branches/00000000-0000-0000-0000-000000000000/…</c>, which the API answers <b>404</b>, and
/// renders an empty list with no explanation. A reload appears to fix it only because
/// <c>SetBranchAsync</c> has by then written the branch to localStorage.
/// </para>
///
/// <para>
/// Before this existed the same subscribe / compare / clear / reload / dispose triple was hand-rolled
/// in 28 components and missing from 46 more. It is the same one-home convention as
/// <c>IStudentScopeService</c>, <c>ISmtpProfileResolver</c> and <c>PublicDisplayRoute</c>.
/// </para>
///
/// <para><b>How to use it.</b> On the page: <c>@inherits QMgr.Web.Components.Shared.BranchAwareComponentBase</c>,
/// drop its own <c>@inject IBranchStateService BranchState</c> (this base injects it), call
/// <see cref="TrackBranch"/> once from <c>OnAfterRenderAsync(firstRender)</c> next to the first branch
/// read, and override <see cref="OnBranchChangedAsync"/> to clear branch-scoped caches and re-run the
/// load. Do not re-subscribe or unsubscribe by hand, and do NOT declare <c>@implements IDisposable</c>
/// or a <c>Dispose</c> of your own — teardown of the page's own timers and handlers goes in an
/// override of <see cref="DisposeCore"/> (or <see cref="DisposeCoreAsync"/> when it must await).
/// See those methods for the two leaks the old shapes caused.</para>
///
/// <para><b>Four traps, each already paid for in this codebase:</b></para>
/// <list type="number">
/// <item>Clearing cached lookups matters as much as reloading. <c>StudentRoster</c>'s quick-log dialog
/// guarded its category fetch with <c>if (!list.Any())</c>, so after a switch it offered the PREVIOUS
/// organization's categories and the API refused the post. <see cref="OnBranchChangedAsync"/> is where
/// those caches get cleared.</item>
/// <item>Track from <c>OnAfterRenderAsync</c>, behind <c>AppInit.InitializeAsync()</c>, never
/// <c>OnInitializedAsync</c> — auth state is read from localStorage over JS interop, which does not
/// exist during prerendering and races an uninitialised store on a cold navigation.</item>
/// <item><c>OnAfterRenderAsync</c> schedules no render of its own, so the page must call
/// <c>StateHasChanged()</c> after it sets state. This base calls it for you after a CHANGE, but not
/// for the page's own first load.</item>
/// <item>A <see cref="Guid.Empty"/> guard is still the page's job — the subscription removes the
/// reload, the guard is what the page shows while nothing is chosen. The shape
/// <c>WelfareCategoriesSetup</c> uses is a Warning toast reading "No Branch Selected — Please select a
/// branch from the header first."</item>
/// </list>
/// </summary>
public abstract class BranchAwareComponentBase : ComponentBase, IDisposable, IAsyncDisposable
{
    [Inject] protected IBranchStateService BranchState { get; set; } = default!;

    private bool _subscribed;
    private bool _disposed;
    private Guid _trackedBranchId;

    /// <summary>
    /// The branch this component last loaded for. Compare against it rather than re-reading the
    /// service when you need to know whether a change is genuinely a change.
    /// </summary>
    protected Guid TrackedBranchId => _trackedBranchId;

    /// <summary>
    /// Subscribe to branch changes and record the branch the component is loading for. Idempotent,
    /// so calling it from a first-render block that can run more than once costs nothing. Call it
    /// AFTER <c>AppInit.InitializeAsync()</c> and <c>BranchState.InitializeAsync()</c>, so the id it
    /// records is the real one rather than an uninitialised store's <see cref="Guid.Empty"/>.
    /// </summary>
    protected void TrackBranch()
    {
        if (_subscribed || _disposed) return;

        _trackedBranchId = BranchState.CurrentBranchId;
        BranchState.OnBranchChanged += HandleBranchChanged;
        _subscribed = true;
    }

    /// <summary>
    /// Called when the branch has actually changed to a real branch. Clear every branch-scoped cache
    /// the page holds, then re-run its load. The base calls <c>StateHasChanged()</c> afterwards.
    /// </summary>
    /// <param name="branchId">The new branch. Never <see cref="Guid.Empty"/>.</param>
    protected abstract Task OnBranchChangedAsync(Guid branchId);

    /// <summary>
    /// Called when <see cref="OnBranchChangedAsync"/> throws. The default does nothing: it is
    /// deliberately NOT a rethrow, because this runs from an <c>async void</c> event handler where an
    /// unhandled exception tears the circuit down — a failed reload must not throw the user out of
    /// the app. Override to toast if the page has somewhere to say it.
    /// </summary>
    protected virtual void OnBranchChangeFailed(Exception ex) { }

    private async void HandleBranchChanged()
    {
        // async void because OnBranchChanged is a plain Action — the conventional Blazor shape for an
        // event handler. Everything inside is therefore wrapped: see OnBranchChangeFailed.
        await InvokeAsync(async () =>
        {
            if (_disposed) return;

            var next = BranchState.CurrentBranchId;
            if (next == _trackedBranchId) return;

            _trackedBranchId = next;

            // Nothing to load for "no branch". The page's own Guid.Empty guard is what the reader
            // sees; reloading against Guid.Empty is the 404-and-empty-list bug this class exists to
            // stop. Matches the reference pattern in StudentWelfareTimeline.
            if (next == Guid.Empty) return;

            try
            {
                await OnBranchChangedAsync(next);
            }
            catch (ObjectDisposedException)
            {
                // The component went away mid-await. Nothing to report.
                return;
            }
            catch (Exception ex)
            {
                OnBranchChangeFailed(ex);
            }

            if (!_disposed) StateHasChanged();
        });
    }

    /// <summary>
    /// Removes the subscription, marks the component disposed, then runs the page's own teardown.
    ///
    /// <para><b>Deliberately NOT virtual.</b> It was virtual until 2026-09-19, and eight pages
    /// declared their own <c>public void Dispose()</c> beside an <c>@implements IDisposable</c>.
    /// That second line re-declares the interface, which re-maps <c>IDisposable.Dispose</c> to the
    /// page's own method (C# interface re-implementation) — so THIS method never ran on any of
    /// them: the handler was never removed, <c>_disposed</c> never became true, and a stale
    /// instance re-ran its whole load on every later branch switch for the life of the circuit
    /// (<c>IBranchStateService</c> is scoped, so one instance serves the entire session). The
    /// compiler reported it as CS0114 on every build and nothing acted on it.</para>
    ///
    /// <para>A page with teardown of its own overrides <see cref="DisposeCore"/> or
    /// <see cref="DisposeCoreAsync"/>. Trying to override this method instead is now a compile
    /// ERROR rather than a silent leak, which is the point of sealing it off.</para>
    /// </summary>
    public void Dispose()
    {
        if (Teardown()) DisposeCore();
    }

    /// <summary>
    /// The async half, and the reason this class implements <see cref="IAsyncDisposable"/> at all.
    ///
    /// <para>Blazor's renderer disposes a component with <c>IAsyncDisposable</c> if it has one and
    /// <c>IDisposable</c> only <b>otherwise</b> — an <c>else if</c>, not both. So five pages that
    /// inherited this base and declared <c>@implements IAsyncDisposable</c> had their
    /// <c>DisposeAsync</c> called and <see cref="Dispose"/> never called, leaking the branch
    /// subscription exactly like the eight above. Nothing reported it: unlike the sync case there
    /// is no hiding, no CS0114, no warning of any kind, because the two are different interfaces.
    /// Owning both shapes here is what makes the leak unreachable rather than merely fixed.</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!Teardown()) return;
        DisposeCore();
        await DisposeCoreAsync();
    }

    /// <summary>Unsubscribes once. Returns false if teardown has already run.</summary>
    private bool Teardown()
    {
        if (_disposed) return false;
        _disposed = true;

        if (_subscribed)
        {
            BranchState.OnBranchChanged -= HandleBranchChanged;
            _subscribed = false;
        }
        return true;
    }

    /// <summary>
    /// The page's own synchronous teardown — timers it started, SignalR handlers it attached. Runs
    /// AFTER the branch subscription has been removed, so a branch change can never re-enter a
    /// component on its way out. Do NOT unsubscribe from <c>OnBranchChanged</c> here: the base owns
    /// that, and a page that also does it by hand is the duplication this class exists to end.
    /// </summary>
    protected virtual void DisposeCore() { }

    /// <summary>
    /// The page's own asynchronous teardown — leaving a SignalR group, stopping a JS-side scanner.
    /// Runs after <see cref="DisposeCore"/>. A page that needs both may override both; most need
    /// only one. Never declare <c>@implements IAsyncDisposable</c> on the page: see
    /// <see cref="DisposeAsync"/> for what that used to cost.
    /// </summary>
    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;
}
