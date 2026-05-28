// Mobile UI theme (light/dark) preference. Persisted in localStorage and
// applied as a `data-theme` attribute on <html>; the mobile CSS keys its dark
// tokens off `html[data-theme="dark"] .m-app`, so this only affects the mobile
// screens, never the desktop layout. Lives in an external file so it stays
// CSP-safe (no inline scripts). Applies immediately on load to avoid a flash.
(function () {
    window.LucidTheme = {
        get: function () {
            try { return localStorage.getItem('lucid-theme') || 'light'; }
            catch (e) { return 'light'; }
        },
        apply: function () {
            document.documentElement.dataset.theme = this.get() === 'dark' ? 'dark' : '';
        },
        set: function (mode) {
            try { localStorage.setItem('lucid-theme', mode); } catch (e) { /* private mode */ }
            this.apply();
        }
    };
    window.LucidTheme.apply();

    // Blazor enhanced navigation replaces page content without reloading the
    // document, which clears the data-theme attribute set above. The shipped
    // event is `enhancedload` (fires after each enhanced page load) — there is
    // no `blazor:afternavigation` event. Re-apply so dark mode persists across
    // tab switches.
    document.addEventListener('enhancedload', function () {
        window.LucidTheme.apply();
    });
})();
