// Bridges browser back → close-modal for the mobile UI.
//
// A mobile modal (MobileModalScreen, MobilePoiDetail) pushes a sentinel
// history entry when it opens and registers a .NET callback. On `popstate`
// (i.e. the user hit the browser/system Back button) we invoke the top
// callback, which closes that modal. The matching UI close path (back chevron,
// programmatic close, Escape) calls pop(token) which runs history.back() so
// the sentinel entry doesn't leak into the user's history.
//
// Stacking: multiple nested modals push multiple entries, each Back press
// closes them top-down, only the outermost Back navigates the app for real.
//
// No globals leaked beyond window.LucidHistory.
(function () {
    const stack = [];  // [{ token, ref }]
    let nextToken = 0;

    window.addEventListener('popstate', function () {
        if (stack.length === 0) return;
        const top = stack.pop();
        try {
            top.ref.invokeMethodAsync('OnBackPopped');
        } catch (e) {
            // Circuit gone / modal disposed mid-pop — nothing to do.
        }
    });

    window.LucidHistory = {
        // Modal calls on open. Pushes a sentinel state and remembers the ref
        // so popstate can call back into the right modal. Returns a token the
        // modal must hand to pop() for matched teardown.
        push: function (ref) {
            const token = ++nextToken;
            stack.push({ token: token, ref: ref });
            history.pushState({ lucidModal: token }, '');
            return token;
        },
        // Modal calls on UI-driven close (back chevron / Escape / parent
        // unrender). If the token is still on the stack, consume our pushed
        // history entry so the user's Back button reaches the real previous
        // page on the next press. If it's not (Back already popped us), this
        // is a no-op.
        pop: function (token) {
            const idx = stack.findIndex(function (e) { return e.token === token; });
            if (idx < 0) return;
            stack.splice(idx, 1);
            history.back();
        }
    };
})();
