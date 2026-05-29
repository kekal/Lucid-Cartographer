using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

[Collection("Integration")]
public class MobileMapTests : MobileTestBase
{
    [Fact]
    public async Task Mobile_Map_RendersSplitLayout()
    {
        await MobileNavigateAndWaitForAppAsync("/");

        // The mobile layout uses .m-app as its root container
        var mApp = Page.Locator(".m-app");
        Assert.True(await mApp.IsVisibleAsync(), ".m-app should be present in mobile layout");

        // Leaflet map container should be inside the mobile layout.
        // In tests, StubMapService doesn't run the real Leaflet JS so the
        // element has id="leaflet-map-{guid}" but no ".leaflet-container" class.
        // We verify the container div exists using the id prefix pattern.
        await Page.WaitForSelectorAsync("[id^='leaflet-map']", new() { Timeout = 15000 });
        var leaflet = Page.Locator("[id^='leaflet-map']");
        Assert.True(await leaflet.CountAsync() > 0, "Leaflet map container div should be present");

        // The list region shows place rows (or empty state) below the map
        // The mobile list uses .list or .scroll inside .m-app
        var listRegion = Page.Locator(".m-app .screen");
        Assert.True(await listRegion.IsVisibleAsync(), "Mobile screen container should be visible");
    }

    [Fact]
    public async Task Mobile_Search_FiltersListClientSide()
    {
        await ImportTestFileAsync("sample.gpx", "GPX Places", "#005bbf");
        await MobileNavigateAndWaitForAppAsync("/");

        // Wait for the POI list to populate (the map page loads collections)
        await Page.WaitForSelectorAsync(".m-app .list .row", new() { Timeout = 15000 });
        var beforeCount = await Page.Locator(".m-app .list .row").CountAsync();
        Assert.True(beforeCount > 0, "POI rows should appear after seeding GPX data");

        // Type in the search input inside the mobile app header
        var searchInput = Page.Locator(".m-app .app-header .search input");
        await searchInput.FillAsync("wawel");
        // M07: replace fixed-duration debounce wait with a state-based wait on
        // the filtered row count. @oninput is synchronous but Blazor still
        // needs to ship the render diff over SignalR before the count updates.
        await Page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.m-app .list .row').length === 1",
            null, new PageWaitForFunctionOptions { Timeout = 5000 });

        var afterCount = await Page.Locator(".m-app .list .row").CountAsync();
        // "Wawel Castle" is the only match for "wawel" in sample.gpx
        Assert.Equal(1, afterCount);
        // M09: verify the surviving row IS Wawel Castle, not just "one row".
        var remainingText = await Page.Locator(".m-app .list .row .name").InnerTextAsync();
        Assert.Contains("Wawel Castle", remainingText);
    }

    [Fact]
    public async Task Mobile_CollectionsDrawer_OpensAndCloses()
    {
        await ImportTestFileAsync("sample.gpx", "GPX Places", "#005bbf");
        await MobileNavigateAndWaitForAppAsync("/");

        // Wait for the page to load
        await Page.WaitForSelectorAsync(".m-app .app-header", new() { Timeout = 15000 });

        // Layers button now lives next to the "N places" header in the bottom
        // panel (moved out of .app-header so the search bar takes the full
        // header width). Located by aria-label rather than position so further
        // panel-header additions don't break the lookup.
        var layersBtn = Page.Locator(".m-app button[aria-label='Collections']");
        await layersBtn.ScrollIntoViewIfNeededAsync();
        await layersBtn.DispatchEventAsync("click");

        // The drawer is now inline content in the bottom panel (no modal-screen).
        // It replaces the "N places" header with a back arrow + "Collections"
        // title, and the body shows the collection rows. Wait for the title.
        var collectionsTitle = Page.Locator(".m-app span", new() { HasTextString = "Collections" });
        await collectionsTitle.First.WaitForAsync(new() { Timeout = 8000, State = WaitForSelectorState.Visible });

        // The back arrow is the first icon-btn in the drawer header.
        var backBtn = Page.Locator(".m-app button[aria-label='Back']").First;
        await backBtn.ClickAsync();

        // After closing, the "N places" header is back and the layers button
        // is clickable again.
        await Page.Locator(".m-app button[aria-label='Collections']")
            .WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Visible });
    }

    [Fact]
    public async Task Mobile_TapPoiRow_OpensDetailModal()
    {
        await ImportTestFileAsync("sample.gpx", "GPX Places", "#005bbf");
        await MobileNavigateAndWaitForAppAsync("/");

        // Wait for POI list rows to appear
        await Page.WaitForSelectorAsync(".m-app .list .row", new() { Timeout = 15000 });

        // Capture the first row's name BEFORE tapping so we can assert the
        // detail modal renders THAT POI (and not some other element that
        // happens to share .modal-screen).
        var firstRowName = await Page.Locator(".m-app .list .row .name").First.InnerTextAsync();

        // Tap the first POI row
        var firstRow = Page.Locator(".m-app .list .row").First;
        await firstRow.ClickAsync();

        // H09: previously asserted only .modal-screen visibility, which the
        // collections drawer also matches. The POI detail uniquely renders
        // .m-hero (and now id=poi-detail-name on its heading); key on those.
        var hero = Page.Locator(".modal-screen .m-hero");
        await hero.WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Visible });

        var heading = Page.Locator(".modal-screen #poi-detail-name");
        await heading.WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Visible });
        var headingText = (await heading.InnerTextAsync()).Trim();
        Assert.Equal(firstRowName.Trim(), headingText);
    }

    [Fact]
    public async Task Mobile_DetailBack_ClosesModal()
    {
        await ImportTestFileAsync("sample.gpx", "GPX Places", "#005bbf");
        await MobileNavigateAndWaitForAppAsync("/");

        // Wait for rows and open detail
        await Page.WaitForSelectorAsync(".m-app .list .row", new() { Timeout = 15000 });
        await Page.Locator(".m-app .list .row").First.ClickAsync();

        var modal = Page.Locator(".modal-screen");
        await modal.WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Visible });

        // Click back arrow — POI detail uses .m-hero-btn for the close button
        var backBtn = Page.Locator(".modal-screen button[aria-label='Back']").First;
        await backBtn.ClickAsync();
        await modal.WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Hidden });
        Assert.False(await modal.IsVisibleAsync(), "Detail modal should close after tapping back");
    }
}
