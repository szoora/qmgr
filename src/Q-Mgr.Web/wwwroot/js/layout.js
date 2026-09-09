// Shared viewport helpers for MainLayout.razor's responsive sidebar.
// 992px matches layout.css's own tablet/mobile breakpoint for the overlay sidebar.
window.qmgrLayout = {
    isMobileViewport: function () {
        return window.innerWidth <= 992;
    },

    // Bring an element into view by id, scrolling the container it actually sits in.
    //
    // Written because a form can only refuse to save for a reason the user can SEE. The welfare
    // log form sets a warning banner with a confirmation checkbox at the BOTTOM of a scrollable
    // dialog and then refuses to save until it is ticked; the toast said "tick the box" while the
    // box sat below the fold, so Save read as simply not working.
    //
    // Deliberately NOT element.scrollIntoView(). That was tried first and consistently landed
    // ~170px short inside a QModal — it resolves against the nearest scrollport it decides on,
    // which here is not the one that needs to move. Finding the scrollable ancestor and setting
    // scrollTop directly is boring, exact, and cannot pick the wrong container.
    //
    // Returns false when the id is not on the page, so a caller can tell "scrolled" from "there
    // was nothing to scroll to" rather than assuming it worked.
    scrollIntoView: function (elementId) {
        var el = document.getElementById(elementId);
        if (!el) return false;

        // Nearest ancestor that actually scrolls. Falls back to the document scroller.
        var container = null;
        for (var p = el.parentElement; p; p = p.parentElement) {
            var oy = getComputedStyle(p).overflowY;
            if ((oy === 'auto' || oy === 'scroll') && p.scrollHeight > p.clientHeight) {
                container = p;
                break;
            }
        }

        var scrollTo = function () {
            if (!container) {
                el.scrollIntoView({ block: 'center', behavior: 'smooth' });
                return;
            }

            // Distance from the container's top edge to the element's, in the CURRENT scroll
            // position — then centre it, clamped to the real scroll range so a banner at the very
            // bottom (which cannot be centred) still ends up fully visible rather than short.
            var delta = el.getBoundingClientRect().top - container.getBoundingClientRect().top;
            var target = container.scrollTop + delta - (container.clientHeight - el.offsetHeight) / 2;
            var max = container.scrollHeight - container.clientHeight;

            container.scrollTo({
                top: Math.max(0, Math.min(target, max)),
                behavior: 'smooth'
            });
        };

        // Wait a frame: Blazor has only just inserted the banner and the dialog has not finished
        // laying out, so measuring immediately gives a stale position.
        requestAnimationFrame(scrollTo);
        return true;
    }
};
