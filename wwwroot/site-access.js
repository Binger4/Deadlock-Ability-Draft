(function () {
    const pollIntervalMs = 5000;
    let checking = false;

    function isExemptPath() {
        const path = window.location.pathname.toLowerCase();
        return path === "/site-closed" ||
            path === "/admin/login" ||
            path.startsWith("/site-access");
    }

    async function checkSiteAccess() {
        if (checking || isExemptPath()) {
            return;
        }

        checking = true;
        try {
            const response = await fetch("/site-access/status", {
                cache: "no-store",
                credentials: "same-origin",
                headers: { "X-Site-Access-Check": "1" }
            });
            if (!response.ok) {
                return;
            }

            const status = await response.json();
            if (status.requiresPassword) {
                const returnUrl = window.location.pathname + window.location.search + window.location.hash;
                window.location.replace("/site-closed?returnUrl=" + encodeURIComponent(returnUrl));
            }
        } catch {
            // A transient network failure should not kick a player out of the site.
        } finally {
            checking = false;
        }
    }

    window.setInterval(checkSiteAccess, pollIntervalMs);
    window.addEventListener("focus", checkSiteAccess);
})();
