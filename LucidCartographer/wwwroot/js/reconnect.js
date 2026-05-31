// Mobile-friendly Blazor Server reconnection.
//
// Problem: on a phone, locking the screen / backgrounding the tab freezes the
// page. The default reconnection loop keeps firing while frozen, burns through
// all its retries, and the circuit ends up stuck in the "reconnect failed"
// state. When the user unlocks and returns, nothing retries on its own — the
// page just sits there showing the failure modal.
//
// Fix: start Blazor manually (so we can widen the retry budget) and, whenever
// the tab comes back to the foreground, force a reconnect if the circuit is
// down. If the server has already discarded the disconnected circuit, a manual
// reconnect can't succeed, so we fall back to a full reload.
(() => {
    Blazor.start({
        circuit: {
            reconnectionOptions: {
                // A bit more headroom than the default (~8) so a brief background
                // freeze doesn't immediately exhaust the budget.
                maxRetries: 20,
                retryIntervalMilliseconds: 3000,
            },
        },
    });

    let reconnecting = false;

    // The framework toggles these classes on the auto-created reconnect modal.
    function circuitIsDown() {
        const modal = document.getElementById('components-reconnect-modal');
        if (!modal) return false;
        return (
            modal.classList.contains('components-reconnect-failed') ||
            modal.classList.contains('components-reconnect-rejected')
        );
    }

    async function tryReconnect() {
        if (reconnecting || !circuitIsDown()) return;
        reconnecting = true;
        try {
            // Restart the retry loop. Resolves false when the server no longer
            // holds the (now-expired) circuit — only a reload recovers that.
            const reconnected = await Blazor.reconnect();
            if (!reconnected) {
                location.reload();
            }
        } catch {
            location.reload();
        } finally {
            reconnecting = false;
        }
    }

    // visibilitychange covers most resume-from-background cases; pageshow/focus
    // catch the mobile-Safari/bfcache variants where it doesn't fire.
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') tryReconnect();
    });
    window.addEventListener('pageshow', tryReconnect);
    window.addEventListener('focus', tryReconnect);
})();
