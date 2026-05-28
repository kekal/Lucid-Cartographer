using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Abstract base for mobile integration tests. Sets the Playwright viewport to
/// 390x844 (iPhone 14 Pro portrait) before the first navigation so the
/// ViewportObserver reports a sub-768px width on first interactive render and
/// the Blazor pages choose the mobile layout branch.
///
/// Because IntegrationTestBase.InitializeAsync is not virtual (it implements
/// IAsyncLifetime directly), this class implements IAsyncLifetime explicitly
/// and chains through base.InitializeAsync(), then immediately sets the
/// viewport.  xUnit resolves IAsyncLifetime on the concrete test class, so
/// this works transparently.
/// </summary>
public abstract class MobileTestBase : IntegrationTestBase, IAsyncLifetime
{
    async Task IAsyncLifetime.InitializeAsync()
    {
        // Run the full base setup (browser launch, page creation, etc.)
        await ((IntegrationTestBase)this).InitializeAsync();

        // Immediately set the viewport to a phone size. The page has been
        // created by base.InitializeAsync() but no navigation has fired yet,
        // so when ViewportObserver calls LucidViewport.register on first
        // render it will see window.innerWidth == 390 and report a mobile width.
        await Page.SetViewportSizeAsync(390, 844);
        Log("MOBILE: viewport set to 390x844");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await ((IntegrationTestBase)this).DisposeAsync();
    }

    // Tab indices in the .m-tabbar: 0=Map, 1=Sources, 2=Operations, 3=More
    private static readonly Dictionary<string, int> TabIndex = new(StringComparer.OrdinalIgnoreCase)
    {
        { "/", 0 }, { "", 0 },
        { "/datasources", 1 }, { "datasources", 1 },
        { "/operations", 2 }, { "operations", 2 },
        { "/more", 3 }, { "more", 3 },
    };

    /// <summary>
    /// Mobile-aware navigation strategy:
    ///
    /// Always start by doing a hard-load of "/" (MapPage). MapPage subscribes
    /// to Viewport.Changed and re-renders to mobile once the observer reports
    /// the 390px width; the tab bar appears as soon as that flip lands.
    ///
    /// For non-root paths we then click the matching tab bar link. This is a
    /// Blazor SPA navigation that preserves the circuit-scoped ViewportService
    /// (already initialized to 390px). When the target page renders during
    /// SPA navigation ViewportService.IsMobile is already true, so the page
    /// renders in mobile mode on its FIRST interactive render — no viewport
    /// flip + re-render round-trip needed.
    ///
    /// M06: All four pages (MapPage, DataSourcesPage, OperationsPage,
    /// MorePage) now subscribe to Viewport.Changed (added in the Wave 1 hot
    /// fix). Direct hard-load of a non-root path would also eventually flip
    /// to mobile, but the SPA-nav strategy here is intentional — it avoids a
    /// second SSR→interactive→viewport-flip render cycle and yields a
    /// significantly more deterministic timing baseline for assertions.
    ///
    /// H01 (Wave 7): The default ".m-app" landmark is too coarse to detect
    /// when a NEW page has finished its first interactive render. ".m-app"
    /// is already in DOM on MapPage and persists across SPA navigation while
    /// the target page renders; consumers that assert page-specific DOM
    /// immediately after this call can race against MapPage's stale subtree.
    /// Callers asserting on page-specific content (e.g. ".app-header .title"
    /// — which MapPage's mobile header DOES NOT contain) should pass a
    /// page-unique landmark selector via <paramref name="landmarkSelector"/>.
    /// </summary>
    protected async Task MobileNavigateAndWaitAsync(string path = "/", string? landmarkSelector = null)
    {
        Log($"MOBILE GO: {path}");

        // Step 1: Hard-load root to get the mobile circuit established
        await Page.GotoAsync($"{BaseUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 15000
        });
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('script[src*=\"blazor.web.js\"]') !== null",
            null, new PageWaitForFunctionOptions { Timeout = 10000 });
        await Page.WaitForSelectorAsync(".m-tabbar", new() { Timeout = 15000 });
        Log("  mobile tab bar visible — ViewportService initialized");

        // Step 2: If target is not root, use SPA navigation via the tab bar
        var normalised = path.TrimEnd('/');
        if (normalised != "" && normalised != "/")
        {
            if (TabIndex.TryGetValue(normalised, out var idx))
            {
                await Page.Locator(".m-tabbar a").Nth(idx).ClickAsync();
                await Page.WaitForURLAsync($"**{normalised}", new() { Timeout = 10000 });
            }
            else
            {
                // Unknown path: direct browser navigation (may miss mobile layout)
                await Page.GotoAsync($"{BaseUrl}{path}", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 15000
                });
            }
            // H01: wait for the page-specific landmark when provided. Falling
            // back to ".m-app" preserves back-compat but is unsafe for any
            // test that asserts page-specific DOM immediately after — see the
            // doc comment above.
            var effectiveLandmark = landmarkSelector ?? ".m-app";
            await Page.WaitForSelectorAsync(effectiveLandmark, new() { Timeout = 15000 });
        }
        else if (landmarkSelector is not null)
        {
            // Caller asked us to confirm a specific landmark for the root
            // (Map) page too.
            await Page.WaitForSelectorAsync(landmarkSelector, new() { Timeout = 15000 });
        }
        Log("  mobile layout ready");
    }

    /// <summary>
    /// Same as MobileNavigateAndWaitAsync but explicitly waits for the .m-app
    /// container to confirm the interactive page has rendered in mobile mode.
    /// </summary>
    protected async Task MobileNavigateAndWaitForAppAsync(string path = "/")
    {
        await MobileNavigateAndWaitAsync(path);
        // Already waiting for .m-app inside MobileNavigateAndWaitAsync; log the completion.
        Log("  .m-app confirmed visible");
    }
}
