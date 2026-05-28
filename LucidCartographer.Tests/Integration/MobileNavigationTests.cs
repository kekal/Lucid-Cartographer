using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

[Collection("Integration")]
public class MobileNavigationTests : MobileTestBase
{
    [Fact]
    public async Task Mobile_BottomTabBar_IsVisible()
    {
        await MobileNavigateAndWaitAsync("/");

        var tabBar = Page.Locator(".m-tabbar");
        Assert.True(await tabBar.IsVisibleAsync(),
            ".m-tabbar should be visible at 390px viewport width");
    }

    [Fact]
    public async Task Mobile_DesktopHeader_IsHidden()
    {
        await MobileNavigateAndWaitAsync("/");

        // The desktop header is in the DOM but hidden via CSS media query at 767px and below.
        var display = await Page.EvaluateAsync<string>(
            @"() => {
                const el = document.querySelector('.m-desktop-header');
                if (!el) return 'absent';
                return getComputedStyle(el).display;
            }");
        // At 390px, the media query hides it via display:none
        Assert.True(display == "none" || display == "absent",
            $"Desktop header should be display:none at mobile width, got: {display}");
    }

    [Fact]
    public async Task Mobile_AllFourTabs_NavigateCorrectly()
    {
        await MobileNavigateAndWaitAsync("/");

        // Get all tab bar links
        var tabLinks = Page.Locator(".m-tabbar a");
        var count = await tabLinks.CountAsync();
        Assert.Equal(4, count);

        // Tab 0 → / (map, already there)
        var href0 = await tabLinks.Nth(0).GetAttributeAsync("href");
        Assert.NotNull(href0);

        // H11: previously this only asserted URL substring — a regression where
        // a tab navigated but the target page threw on init would still pass.
        // After each tab click, wait for a page-unique landmark to confirm the
        // target's interactive render actually completed.

        // Tab 1 → datasources
        await tabLinks.Nth(1).ClickAsync();
        await Page.WaitForURLAsync("**/datasources", new() { Timeout = 10000 });
        Assert.Contains("datasources", Page.Url);
        await Page.WaitForSelectorAsync(".m-app .m-import-card", new() { Timeout = 10000 });

        // Tab 2 → operations
        await tabLinks.Nth(2).ClickAsync();
        await Page.WaitForURLAsync("**/operations", new() { Timeout = 10000 });
        Assert.Contains("operations", Page.Url);
        await Page.WaitForSelectorAsync(".m-app .m-op-card", new() { Timeout = 10000 });

        // Tab 3 → more
        await tabLinks.Nth(3).ClickAsync();
        await Page.WaitForURLAsync("**/more", new() { Timeout = 10000 });
        Assert.Contains("more", Page.Url);
        await Page.WaitForSelectorAsync(".m-app .segmented", new() { Timeout = 10000 });

        // Tab 0 → back to /
        await Page.Locator(".m-tabbar a").Nth(0).ClickAsync();
        await Page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/", new() { Timeout = 10000 });
        var uri = new Uri(Page.Url);
        Assert.Equal("/", uri.AbsolutePath);
        // Map page lands on the search/map layout.
        await Page.WaitForSelectorAsync(".m-app [id^='leaflet-map']", new() { Timeout = 10000 });
    }

    [Fact]
    public async Task Mobile_ActiveTab_HasAriaCurrentPage()
    {
        // Map tab
        await MobileNavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync(".m-tabbar a", new() { Timeout = 10000 });
        // M07: wait for aria-current to actually land rather than sleeping.
        await Page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.m-tabbar a')[0]?.getAttribute('aria-current') === 'page'",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });

        var mapTabCurrent = await Page.Locator(".m-tabbar a").Nth(0).GetAttributeAsync("aria-current");
        Assert.Equal("page", mapTabCurrent);

        // Sources tab
        await Page.Locator(".m-tabbar a").Nth(1).ClickAsync();
        await Page.WaitForURLAsync("**/datasources", new() { Timeout = 10000 });
        await Page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.m-tabbar a')[1]?.getAttribute('aria-current') === 'page'",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });

        var sourcesTabCurrent = await Page.Locator(".m-tabbar a").Nth(1).GetAttributeAsync("aria-current");
        Assert.Equal("page", sourcesTabCurrent);

        // Operations tab
        await Page.Locator(".m-tabbar a").Nth(2).ClickAsync();
        await Page.WaitForURLAsync("**/operations", new() { Timeout = 10000 });
        await Page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.m-tabbar a')[2]?.getAttribute('aria-current') === 'page'",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });

        var opsTabCurrent = await Page.Locator(".m-tabbar a").Nth(2).GetAttributeAsync("aria-current");
        Assert.Equal("page", opsTabCurrent);

        // More tab
        await Page.Locator(".m-tabbar a").Nth(3).ClickAsync();
        await Page.WaitForURLAsync("**/more", new() { Timeout = 10000 });
        await Page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.m-tabbar a')[3]?.getAttribute('aria-current') === 'page'",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });

        var moreTabCurrent = await Page.Locator(".m-tabbar a").Nth(3).GetAttributeAsync("aria-current");
        Assert.Equal("page", moreTabCurrent);
    }
}
