// Shared viewport helpers for MainLayout.razor's responsive sidebar.
// 992px matches layout.css's own tablet/mobile breakpoint for the overlay sidebar.
window.qmgrLayout = {
    isMobileViewport: function () {
        return window.innerWidth <= 992;
    }
};
