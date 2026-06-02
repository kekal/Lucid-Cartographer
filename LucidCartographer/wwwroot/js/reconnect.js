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

    // Reload-loop breaker. A flapping mobile tab or a mid-rolling-restart
    // server can drop the freshly reloaded page again before onConnectionUp
    // fires, which would otherwise reload forever. We cap auto-reloads using
    // sessionStorage (survives reloads, scoped to this tab) and fall back to a
    // manual-retry banner once the cap is hit.
    const BREAKER_COUNT_KEY = 'lucid-reconnect-reloads';
    const BREAKER_FIRST_KEY = 'lucid-reconnect-first-reload';
    const BREAKER_MAX_RELOADS = 3;      // stop auto-reloading after this many…
    const BREAKER_WINDOW_MS = 60000;    // …within this window

    // Bootstrap retry budget for the very first Blazor.start() (RJS-3): a
    // negotiate/handshake blocked by CSP/proxy, a transient 502, or a server
    // that is briefly down at load rejects start() and otherwise leaves a
    // silent non-interactive dead page (onConnectionDown never fires for a
    // circuit that was never established).
    const START_MAX_RETRIES = 3;
    const START_RETRY_INTERVAL_MS = 3000;

    function delay(ms) {
        return new Promise((resolve) => setTimeout(resolve, ms));
    }

    // A lightweight, self-rendered status banner (created here, not scraped
    // from the page). Replaces the default modal we opt out of.
    let banner = null;
    function ensureBanner() {
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
        return banner;
    }
    function showBanner(text) {
        const el = ensureBanner();
        el.textContent = text;
        el.style.cursor = '';
        el.onclick = null;
        el.style.display = 'block';
    }
    // A persistent, clickable banner offering a manual reload. Used as the
    // fall-back when automatic reloading is not appropriate: (a) the reload
    // breaker has tripped (too many reloads in the window — a flapping tab or a
    // server stuck mid-restart), or (b) the initial Blazor.start() never
    // connected. A reload here is a user-acknowledged action, and it clears the
    // breaker so the next attempt starts fresh.
    function showManualRetryBanner(text) {
        const el = ensureBanner();
        el.textContent = text;
        el.style.cursor = 'pointer';
        el.onclick = () => {
            clearReloadBreaker();
            location.reload();
        };
        el.style.display = 'block';
    }
    function hideBanner() {
        if (banner) {
            banner.style.display = 'none';
            banner.onclick = null;
        }
    }

    function readInt(key) {
        try {
            const n = parseInt(sessionStorage.getItem(key) || '', 10);
            return Number.isFinite(n) ? n : 0;
        } catch {
            return 0; // sessionStorage unavailable (private mode / blocked)
        }
    }
    function clearReloadBreaker() {
        try {
            sessionStorage.removeItem(BREAKER_COUNT_KEY);
            sessionStorage.removeItem(BREAKER_FIRST_KEY);
        } catch { /* sessionStorage unavailable */ }
    }
    // Reload to start clean, but only if the breaker has not tripped. Returns
    // true if it triggered a reload, false if it showed the manual-retry banner
    // instead (and therefore did NOT reload).
    function reloadOrBreak(manualText) {
        const now = Date.now();
        let count = readInt(BREAKER_COUNT_KEY);
        let first = readInt(BREAKER_FIRST_KEY);

        // Start (or restart) the window if this is the first reload or the
        // previous window has fully elapsed.
        if (count === 0 || first === 0 || now - first > BREAKER_WINDOW_MS) {
            count = 0;
            first = now;
        }

        if (count >= BREAKER_MAX_RELOADS) {
            // Too many reloads in the window — stop the loop and let the user
            // decide when to retry.
            showManualRetryBanner(manualText);
            return false;
        }

        // Persist the incremented counter immediately *before* reloading so the
        // count survives into the freshly loaded page.
        try {
            sessionStorage.setItem(BREAKER_COUNT_KEY, String(count + 1));
            sessionStorage.setItem(BREAKER_FIRST_KEY, String(first));
        } catch { /* sessionStorage unavailable — reload anyway, just uncapped */ }

        location.reload();
        return true;
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
                    // fresh page can recover it. Once the server has dropped the
                    // circuit there is no live session to keep editing against,
                    // so we just reload automatically (the user's explicit
                    // preference). The breaker still caps runaway reloads: if the
                    // page keeps dropping right after each reload (a flapping
                    // tab / mid-rolling-restart server), it falls back to a
                    // manual-retry banner instead of reloading forever.
                    handling = false;
                    reloadOrBreak('Connection lost — tap to retry');
                    return;
                } catch {
                    // Transport still down (server unreachable). Wait and retry.
                    await delay(interval);
                }
            }
            // Retry budget exhausted — reload to start clean, subject to the
            // breaker so a flapping tab does not reload endlessly.
            handling = false;
            reloadOrBreak('Connection lost — tap to retry');
        },
        // Called when the connection is restored.
        onConnectionUp: () => {
            handling = false;
            clearReloadBreaker();
            hideBanner();
        },
    };

    function startBlazor(attempt) {
        Blazor.start({
            circuit: {
                reconnectionOptions: {
                    maxRetries: MAX_RETRIES,
                    retryIntervalMilliseconds: RETRY_INTERVAL_MS,
                },
                reconnectionHandler: reconnectionHandler,
            },
        }).catch((err) => {
            // The initial connection never came up. onConnectionDown does not
            // fire for a circuit that was never established, so without this we
            // would be left on a silent, non-interactive dead page.
            console.error('Blazor.start() failed (attempt ' + attempt + ')', err);
            if (attempt < START_MAX_RETRIES) {
                showBanner('Connecting… (' + (attempt + 1) + '/' + START_MAX_RETRIES + ')');
                delay(START_RETRY_INTERVAL_MS).then(() => startBlazor(attempt + 1));
            } else {
                showManualRetryBanner('Could not connect — tap to retry');
            }
        });
    }

    startBlazor(1);
})();
