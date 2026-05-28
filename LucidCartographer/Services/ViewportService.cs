namespace LucidCartographer.Services;

/// <summary>
/// Circuit-scoped holder for the client viewport width, used to decide whether
/// to render the desktop layout or the touch-optimized mobile layout.
///
/// The width is reported from the browser by <c>ViewportObserver</c> via JS
/// interop (a debounced resize listener). Components inject this service,
/// subscribe to <see cref="Changed"/>, and read <see cref="IsMobile"/> to pick
/// their render path. Registered Scoped so every component on a circuit shares
/// the same instance and agrees on the current breakpoint.
/// </summary>
public sealed class ViewportService
{
    /// <summary>
    /// Widths strictly below this (CSS px) render the mobile UI. 768px is the
    /// conventional tablet/phone boundary (Tailwind's <c>md</c>), so phones in
    /// portrait and small tablets get the bottom-tab mobile shell while laptops
    /// and desktops keep the multi-pane desktop layout.
    /// </summary>
    public const int MobileBreakpointPx = 768;

    /// <summary>Last width reported by the browser, in CSS pixels.</summary>
    public int Width { get; private set; }

    /// <summary>
    /// True once the browser has reported a width. Before that the app renders
    /// the desktop layout (matches server prerender), then flips on first
    /// report if the viewport is actually narrow.
    /// </summary>
    public bool Initialized { get; private set; }

    public bool IsMobile => Initialized && Width > 0 && Width < MobileBreakpointPx;

    public event Action? Changed;

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
