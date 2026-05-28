using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Tests for the viewport breakpoint flip between desktop and mobile layouts.
/// These tests start at a specific viewport, navigate, then resize and verify
/// the layout switches correctly.
/// Note: These tests override the MobileTestBase 390x844 init viewport by
/// setting a different size before navigation.
/// </summary>
[Collection("Integration")]
public class MobileResponsiveTests : MobileTestBase
{
    [Fact]
    public async Task Mobile_StartsAtDesktop_ResizeFlipsToMobile()
    {
        // Override: start at desktop size before navigating
        await Page.SetViewportSizeAsync(1280, 800);

        // Navigate at desktop size — MapPage renders desktop layout
        await Page.GotoAsync($"{BaseUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 15000
        });
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('script[src*=\"blazor.web.js\"]') !== null",
            null, new PageWaitForFunctionOptions { Timeout = 10000 });
        await Page.WaitForSelectorAsync("nav a", new() { Timeout = 10000 });
        // M07: wait for ViewportObserver to actually report width (the desktop
        // sidebar appears once Vm.IsLoading finishes — interactive render is
        // therefore visible). Replaces a fixed 500ms sleep.
        await Page.WaitForSelectorAsync(".w-60", new() { Timeout = 10000 });

        // At 1280px: desktop header should be visible, mobile tab bar should be hidden/absent
        var desktopHeaderDisplay = await Page.EvaluateAsync<string>(
            "() => getComputedStyle(document.querySelector('.m-desktop-header') ?? document.body).display");
        // Desktop header should NOT be 'none' at 1280px
        Assert.NotEqual("none", desktopHeaderDisplay);

        // Tab bar should not be visible at desktop width (CSS: .m-tabbar { display: none; })
        var tabbarDisplay = await Page.EvaluateAsync<string>(
            @"() => {
                const el = document.querySelector('.m-tabbar');
                return el ? getComputedStyle(el).display : 'absent';
            }");
        Assert.True(tabbarDisplay == "none" || tabbarDisplay == "absent",
            $"Tab bar should be hidden at desktop width, got: {tabbarDisplay}");

        // Now resize to mobile
        await Page.SetViewportSizeAsync(390, 844);
        // M07: state-based wait — Blazor will render .m-app once the viewport
        // debounce (150ms) fires and ViewportService.IsMobile flips.
        await Page.WaitForSelectorAsync(".m-app", new() { Timeout = 10000 });

        // Mobile layout should now be active
        var mApp = Page.Locator(".m-app");
        Assert.True(await mApp.IsVisibleAsync(), ".m-app should appear after resize to mobile width");

        // Tab bar should now be visible
        var tabbarDisplayAfter = await Page.EvaluateAsync<string>(
            "() => getComputedStyle(document.querySelector('.m-tabbar')).display");
        Assert.NotEqual("none", tabbarDisplayAfter);
    }

    [Fact]
    public async Task Mobile_ResizeMobileToDesktop_FlipsBack()
    {
        // MobileTestBase already set viewport to 390x844 before this test
        await Page.GotoAsync($"{BaseUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 15000
        });
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('script[src*=\"blazor.web.js\"]') !== null",
            null, new PageWaitForFunctionOptions { Timeout = 10000 });
        await Page.WaitForSelectorAsync(".m-tabbar", new() { Timeout = 15000 });

        // Verify mobile layout is active
        var mApp = Page.Locator(".m-app");
        Assert.True(await mApp.IsVisibleAsync(), ".m-app should be present at mobile viewport");

        // Resize to desktop
        await Page.SetViewportSizeAsync(1280, 800);
        // M07: state-based wait on the desktop sidebar selector replaces the
        // fixed 500ms debounce sleep.
        await Page.WaitForSelectorAsync(".w-60", new() { Timeout = 10000 });

        // Desktop sidebar (.w-60) should now be visible
        var sidebar = Page.Locator(".w-60");
        Assert.True(await sidebar.IsVisibleAsync(), "Desktop sidebar (.w-60) should appear after resizing to 1280px");

        // The .m-app container should be gone (desktop renders a different structure)
        var mAppAfter = await Page.Locator(".m-app").CountAsync();
        Assert.Equal(0, mAppAfter);
    }

    [Fact]
    public async Task Mobile_FlipDoesNotDuplicateMap()
    {
        // Start at desktop
        await Page.SetViewportSizeAsync(1280, 800);
        await Page.GotoAsync($"{BaseUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 15000
        });
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('script[src*=\"blazor.web.js\"]') !== null",
            null, new PageWaitForFunctionOptions { Timeout = 10000 });
        await Page.WaitForSelectorAsync("nav a", new() { Timeout = 10000 });
        // M07: state-based — desktop sidebar confirms the desktop layout rendered.
        await Page.WaitForSelectorAsync(".w-60", new() { Timeout = 10000 });

        // Verify single map at desktop
        var mapCountDesktop = await Page.Locator("[id^='leaflet-map']").CountAsync();
        Assert.Equal(1, mapCountDesktop);

        // Flip to mobile
        await Page.SetViewportSizeAsync(390, 844);
        await Page.WaitForSelectorAsync(".m-app", new() { Timeout = 10000 });

        // Should still have exactly one map container
        var mapCountMobile = await Page.Locator("[id^='leaflet-map']").CountAsync();
        Assert.Equal(1, mapCountMobile);

        // Flip back to desktop
        await Page.SetViewportSizeAsync(1280, 800);
        await Page.WaitForSelectorAsync(".w-60", new() { Timeout = 10000 });

        // Still one map
        var mapCountBack = await Page.Locator("[id^='leaflet-map']").CountAsync();
        Assert.Equal(1, mapCountBack);

        // M06 (Wave 7) — known coverage gap, documented honestly: we cannot
        // assert a `.leaflet-container` class here because the integration
        // test infrastructure swaps the real LeafletMapService for
        // StubMapService (no JS interop), so the class is never added even
        // on a successful wire. Functional re-wire after the M02
        // OperationCanceledException catch is therefore NOT directly tested
        // — the catch path's side-effects are unobservable through
        // StubMapService. A future bUnit-level test with a mock IMapService
        // could fill this gap; for now this comment is the marker.
    }
}
