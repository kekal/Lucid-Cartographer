// Reports the browser viewport width to Blazor so the app can switch between
// the desktop and mobile layouts. A single .NET object reference is registered
// per circuit; resize events are debounced to avoid flooding the SignalR
// connection. Mirrors the IIFE style of leafletInterop.js (no globals leaked
// beyond window.LucidViewport).
(function () {
    let dotnetRef = null;
    let debounceTimer = null;
    // M01 (Wave 7): integer token identifies which registration call owns the
    // current listener. Ref-equality comparison on DotNetObjectReference doesn't
    // work because Blazor's JS-side DotNetObject wrapper is recreated for each
    // marshalling event — two interop calls with the same .NET ref produce
    // DIFFERENT JS objects, so `dotnetRef !== ref` is always true and the
    // previous-wave unregister was a no-op on every dispose.
    let nextToken = 0;
    let currentToken = 0;

    function currentWidth() {
        return window.innerWidth || document.documentElement.clientWidth || 0;
    }

    function report() {
        if (!dotnetRef) return;
        try {
            dotnetRef.invokeMethodAsync('OnViewportChanged', currentWidth());
        } catch (e) {
            // Circuit gone between resize and invoke — ignore; unregister will
            // run on dispose.
        }
    }

    function onResize() {
        if (debounceTimer) clearTimeout(debounceTimer);
        debounceTimer = setTimeout(report, 150);
    }

    window.LucidViewport = {
        // Register the observer and immediately push the current width so the
        // layout settles on first render without waiting for a resize.
        // M01 (Wave 7): returns { width, token } so the caller can hand the
        // token back on dispose and we can match THIS registration rather than
        // the .NET ref (which Blazor wraps with a fresh JS object per call).
        register: function (ref) {
            dotnetRef = ref;
            const token = ++nextToken;
            currentToken = token;
            window.removeEventListener('resize', onResize);
            window.removeEventListener('orientationchange', onResize);
            window.addEventListener('resize', onResize, { passive: true });
            window.addEventListener('orientationchange', onResize, { passive: true });
            return { width: currentWidth(), token: token };
        },
        // M01 (Wave 7): match by integer token so we don't tear down a listener
        // owned by a NEWER ViewportObserver. A SPA navigation can call
        // register(newRef) before the previous page's DisposeAsync runs;
        // comparing tokens means the older dispose is a no-op while the newer
        // registration's teardown still works correctly.
        unregister: function (token) {
            if (typeof token === 'number' && token !== currentToken) {
                return;
            }
            window.removeEventListener('resize', onResize);
            window.removeEventListener('orientationchange', onResize);
            if (debounceTimer) clearTimeout(debounceTimer);
            dotnetRef = null;
            currentToken = 0;
        }
    };
})();
