// Mobile-friendly Blazor Server reconnection.
//
// Problem: on a phone, locking the screen / backgrounding the tab freezes the
// page and the OS tears down the SignalR WebSocket. By the time the user
// returns, the server has usually discarded the disconnected circuit (default
// retention ~3 min). Blazor's default reconnect UI then lands on a terminal
// "reconnection failed / reload" state and waits for a manual click — nothing
// recovers on its own.
//
// Approach: use Blazor's supported reconnection extension point — a custom
// `reconnectionHandler` (the documented onConnectionDown / onConnectionUp
// contract). We own the retry loop, driving the public `Blazor.reconnect()`
// API, and decide when to reload. No DOM scraping, no reliance on framework
// internals or CSS-class state.
//
// Blazor.reconnect() semantics (verified):
//   • resolves true  → reconnected to the existing circuit (best case)
//   • resolves false → server reachable but the circuit is gone → reload
//   • rejects        → transport still unreachable → wait and retry
(() => {
    const MAX_RETRIES = 30;             // generous: a backgrounded tab may resume long after the drop
    const RETRY_INTERVAL_MS = 3000;

    function delay(ms) {
        return new Promise((resolve) => setTimeout(resolve, ms));
    }

    // A lightweight, self-rendered status banner (created here, not scraped
    // from the page). Replaces the default modal we opt out of.
    let banner = null;
    function showBanner(text) {
        if (!banner) {
            banner = document.createElement('div');
            banner.id = 'reconnect-banner';
            banner.setAttribute('role', 'alert');
            banner.style.cssText =
                'position:fixed;top:0;left:0;right:0;z-index:3000;' +
                'padding:10px 16px;text-align:center;' +
                'background:#1f2937;color:#fff;' +
                'font:600 14px/1.4 system-ui,-apple-system,sans-serif;' +
                'box-shadow:0 2px 8px rgba(0,0,0,.3);';
            document.body.appendChild(banner);
        }
        banner.textContent = text;
        banner.style.display = 'block';
    }
    function hideBanner() {
        if (banner) banner.style.display = 'none';
    }

    let handling = false;
    const reconnectionHandler = {
        // Called once when the circuit drops. We own recovery from here.
        onConnectionDown: async (options) => {
            if (handling) return;
            handling = true;

            const maxRetries = (options && options.maxRetries) || MAX_RETRIES;
            const interval = (options && options.retryIntervalMilliseconds) || RETRY_INTERVAL_MS;

            for (let attempt = 1; attempt <= maxRetries; attempt++) {
                showBanner('Reconnecting to the server… (' + attempt + '/' + maxRetries + ')');
                try {
                    const reconnected = await Blazor.reconnect();
                    if (reconnected) {
                        // Re-attached to the live circuit; onConnectionUp will hide the banner.
                        handling = false;
                        return;
                    }
                    // Server reachable but the circuit was discarded — only a
                    // fresh page can recover. (There is no state worth keeping:
                    // the server-side circuit is already gone.)
                    location.reload();
                    return;
                } catch {
                    // Transport still down (server unreachable). Wait and retry.
                    await delay(interval);
                }
            }
            // Retry budget exhausted — reload to start clean.
            location.reload();
        },
        // Called when the connection is restored.
        onConnectionUp: () => {
            handling = false;
            hideBanner();
        },
    };

    Blazor.start({
        circuit: {
            reconnectionOptions: {
                maxRetries: MAX_RETRIES,
                retryIntervalMilliseconds: RETRY_INTERVAL_MS,
            },
            reconnectionHandler: reconnectionHandler,
        },
    });
})();
