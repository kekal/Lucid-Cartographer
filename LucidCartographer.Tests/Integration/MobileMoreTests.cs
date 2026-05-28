namespace LucidCartographer.Tests.Integration;

[Collection("Integration")]
public class MobileMoreTests : MobileTestBase
{
    [Fact]
    public async Task Mobile_More_Renders()
    {
        // H01 (Wave 7): wait on a page-unique landmark, not ".m-app" — the
        // generic landmark is already in DOM (MapPage's mobile shell) and the
        // assertion below targets ".app-header .title" which MapPage's mobile
        // header doesn't contain. Without the specific landmark the assertion
        // races against MapPage's stale DOM under suite load.
        await MobileNavigateAndWaitAsync("/more", ".m-app .segmented");

        // The "More" page shows a title and the theme segmented control
        var moreTitle = Page.Locator(".m-app .app-header .title");
        Assert.True(await moreTitle.IsVisibleAsync(), "More page title should be visible");

        var titleText = await moreTitle.InnerTextAsync();
        Assert.Contains("More", titleText, StringComparison.OrdinalIgnoreCase);

        // The theme segmented control should be present
        var segmented = Page.Locator(".m-app .segmented");
        Assert.True(await segmented.IsVisibleAsync(), "Theme segmented control should be visible");

        // It should contain Light and Dark buttons
        var buttons = segmented.Locator("button");
        var count = await buttons.CountAsync();
        Assert.Equal(2, count);
        var btnTexts = await buttons.AllInnerTextsAsync();
        Assert.Contains("Light", btnTexts);
        Assert.Contains("Dark", btnTexts);
    }

    [Fact]
    public async Task Mobile_DarkTheme_PersistsAcrossReload()
    {
        // H01 (Wave 7): wait on a page-unique landmark — see Mobile_More_Renders.
        await MobileNavigateAndWaitAsync("/more", ".m-app .segmented");

        // Click the "Dark" button in the segmented control
        var darkBtn = Page.Locator(".m-app .segmented button").Filter(
            new Microsoft.Playwright.LocatorFilterOptions { HasText = "Dark" });
        await darkBtn.ClickAsync();
        // M07: wait for the data-theme attribute to actually flip instead of
        // sleeping. SetThemeAsync is a single Blazor invocation followed by a
        // synchronous JS call so this lands fast.
        await Page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.theme === 'dark'",
            null, new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 5000 });

        // Verify the data-theme attribute is set to "dark"
        var themeAfter = await Page.EvaluateAsync<string>(
            "() => document.documentElement.dataset.theme");
        Assert.Equal("dark", themeAfter);

        // Reload the page — theme.js applies the saved theme on load
        await Page.ReloadAsync(new Microsoft.Playwright.PageReloadOptions
        {
            WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle
        });

        // After reload, theme.js should have applied dark from localStorage
        var themeAfterReload = await Page.EvaluateAsync<string>(
            "() => document.documentElement.dataset.theme");
        Assert.Equal("dark", themeAfterReload);

        // Clean up: switch back to light theme
        // Navigate back to /more via the SPA workaround
        await MobileNavigateAndWaitAsync("/more", ".m-app .segmented");
        var lightBtn = Page.Locator(".m-app .segmented button").Filter(
            new Microsoft.Playwright.LocatorFilterOptions { HasText = "Light" });
        await lightBtn.ClickAsync();
        // M07: wait for the data-theme to no longer be "dark".
        await Page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.theme !== 'dark'",
            null, new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 5000 });
        var themeCleanup = await Page.EvaluateAsync<string>(
            "() => document.documentElement.dataset.theme");
        Assert.True(
            string.IsNullOrEmpty(themeCleanup) || themeCleanup != "dark",
            "Theme should be light after cleanup");
    }

    [Fact]
    public async Task Mobile_DarkTheme_PersistsAcrossSpaNavigation()
    {
        // M05 (Wave 7): the sibling reload test only exercises the full-reload
        // theme-restore path. Production added a Blazor `enhancedload` listener
        // specifically for SPA navigation (so the theme stays applied when the
        // user moves between tabs without a full reload). Cover that branch
        // explicitly — a regression that drops the enhancedload listener would
        // not be caught by the reload-only test above.
        await MobileNavigateAndWaitAsync("/more", ".m-app .segmented");

        // Set dark mode.
        var darkBtn = Page.Locator(".m-app .segmented button").Filter(
            new Microsoft.Playwright.LocatorFilterOptions { HasText = "Dark" });
        await darkBtn.ClickAsync();
        await Page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.theme === 'dark'",
            null, new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 5000 });

        Assert.Equal("dark", await Page.EvaluateAsync<string>(
            "() => document.documentElement.dataset.theme"));

        // SPA-navigate to /map by clicking the tab bar (no full reload).
        // Note: Blazor InteractiveServer-to-InteractiveServer nav does NOT
        // fire `enhancedload` (that event is reserved for enhanced HTTP nav).
        // The previous-wave H01 fix landed enhancedload re-apply, but the
        // in-circuit nav path also clears `<html data-theme>` via the
        // renderer's diff. ViewportObserver.OnAfterRenderAsync re-applies the
        // theme on every page's first interactive render to cover this case
        // (M05, Wave 7).
        await Page.Locator(".m-tabbar a").Nth(0).ClickAsync();
        await Page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/",
            new() { Timeout = 10000 });
        await Page.WaitForSelectorAsync(".m-app [id^='leaflet-map']", new() { Timeout = 10000 });

        // Wait for the theme to settle to "dark" on the new page.
        await Page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.theme === 'dark'",
            null, new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 5000 });
        var themeAfterSpaNav = await Page.EvaluateAsync<string>(
            "() => document.documentElement.dataset.theme");
        Assert.Equal("dark", themeAfterSpaNav);

        // Clean up: navigate back to /more and flip to light.
        await MobileNavigateAndWaitAsync("/more", ".m-app .segmented");
        var lightBtn = Page.Locator(".m-app .segmented button").Filter(
            new Microsoft.Playwright.LocatorFilterOptions { HasText = "Light" });
        await lightBtn.ClickAsync();
        await Page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.theme !== 'dark'",
            null, new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 5000 });
    }
}
