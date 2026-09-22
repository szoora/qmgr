// The two parts of a white-labelled deployment that are not components: the tab's icon, and
// anything else that lives in <head> after the document has been served.
//
// App.razor sets the icon server-side for a tenant's OWN domain, where the host answers the
// question before a circuit exists. This is the other half: a tenant signed in on the platform
// address, where the organisation is only known once the shell has asked the API. Most tenants
// sign in that way and should not need their own domain to stop seeing our icon in their tab.
window.qmgrBrand = (function () {
    /// Replaces every icon link with the tenant's. Removing the old ones matters: a browser is free
    /// to keep using any <link rel="icon"> still in the document, so leaving ours behind gives an
    /// inconsistent tab that changes on refresh.
    function setFavicon(url) {
        if (!url) return;
        document.querySelectorAll('link[rel~="icon"], link[rel="apple-touch-icon"], link[rel="mask-icon"]')
            .forEach(link => link.remove());

        const icon = document.createElement('link');
        icon.rel = 'icon';
        icon.href = url;
        document.head.appendChild(icon);

        const touch = document.createElement('link');
        touch.rel = 'apple-touch-icon';
        touch.href = url;
        document.head.appendChild(touch);
    }

    return { setFavicon };
})();
