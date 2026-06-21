using Microsoft.AspNetCore.Http;

namespace LucidCartographer.Services;

/// <summary>
/// Circuit-scoped holder for the client viewport width, used to decide whether
/// to render the desktop layout or the touch-optimized mobile layout.
///
/// Two inputs feed it:
/// 1. The <c>lucid_viewport</c> cookie set by <c>viewport.js</c> on first JS
///    run. Read in the constructor from the active HttpContext so SSR and the
///    first interactive render pick the correct layout immediately. The cookie
///    is the page-jerk fix — without it, every navigation re-renders desktop
///    first, then flips to mobile after JS interop reports a width.
/// 2. Live updates from <c>ViewportObserver</c> via JS interop (debounced
///    resize listener) which call <see cref="SetWidth"/>. This corrects the
///    cookie's coarse value to the real pixel width and handles in-session
///    resize / orientation changes.
/// Registered Scoped so every component on a circuit shares the same instance
/// and agrees on the current breakpoint.
/// </summary>
public sealed class ViewportService
{
    /// <summary>
    /// Mobile layout breakpoint (768px = Tailwind <c>md</c>).
    /// </summary>
    public const int MobileBreakpointPx = 768;

    /// <summary>
    /// Cookie name written by viewport.js and read here. Value is either
    /// "mobile" or "desktop"; anything else is treated as missing.
    /// </summary>
    private const string CookieName = "lucid_viewport";

    /// <summary>
    /// Synthetic widths used when only the cookie is available (i.e. before JS
    /// interop has reported the real pixel count). They just have to sit on
    /// the correct side of MobileBreakpointPx; the live ViewportObserver call
    /// replaces them with the actual width within ~50ms.
    /// </summary>
    private const int CookieMobileWidth = 390;
    private const int CookieDesktopWidth = 1280;

    public int Width { get; private set; }

    /// <summary>
    /// True once either the cookie has been read OR the browser has reported a
    /// width via JS interop. Components can use this to gate "loading" states,
    /// but with the cookie seed Initialized is true from the very first render
    /// on any browser that has visited the site before.
    /// </summary>
    public bool Initialized { get; private set; }

    public bool IsMobile => Initialized && Width > 0 && Width < MobileBreakpointPx;

    public event Action? Changed;

    public ViewportService(IHttpContextAccessor httpContextAccessor)
    {
        // HttpContext is null outside a request (e.g. bUnit tests); JS interop populates Initialized in that case.
        var cookie = httpContextAccessor.HttpContext?.Request?.Cookies[CookieName];
        if (cookie == "mobile")
        {
            Width = CookieMobileWidth;
            Initialized = true;
        }
        else if (cookie == "desktop")
        {
            Width = CookieDesktopWidth;
            Initialized = true;
        }
    }

    /// <summary>
    /// Called by <c>ViewportObserver</c> when the browser reports its width.
    /// Only raises <see cref="Changed"/> when the mobile/desktop verdict
    /// actually flips (or on first report), so a stream of resize events while
    /// staying on one side of the breakpoint doesn't churn the UI.
    /// </summary>
    public void SetWidth(int width)
    {
        var wasMobile = IsMobile;
        var wasInitialized = Initialized;

        Width = width;
        Initialized = true;

        if (!wasInitialized || IsMobile != wasMobile)
        {
            Changed?.Invoke();
        }
    }
}
