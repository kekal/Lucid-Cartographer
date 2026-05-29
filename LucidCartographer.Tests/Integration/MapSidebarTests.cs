namespace LucidCartographer.Tests.Integration;

[Collection("Integration")]
public class MapSidebarTests : IntegrationTestBase
{
    [Fact]
    public async Task Sidebar_EmptyState_ShowsNoCollectionsMessage()
    {
        // Don't seed any data
        await NavigateAndWaitAsync("/");

        // Sidebar should exist
        var sidebar = Page.Locator(".w-60");
        Assert.True(await sidebar.IsVisibleAsync(), "Sidebar should be visible");

        // Should show the empty state message ("No collections yet.")
        var emptyMessage = Page.Locator(".w-60 .text-center:has-text('No collections')");
        Assert.True(await emptyMessage.IsVisibleAsync(), "Empty state message should be visible");

        // No collection rows should be present (cursor-pointer rows)
        var collectionRows = Page.Locator(".w-60 .cursor-pointer");
        var rowCount = await collectionRows.CountAsync();
        Assert.Equal(0, rowCount);
    }

    [Fact]
    public async Task ToggleVisibilityOff_RemovesChipAndHidesMarkers()
    {
        // Seed GPX data
        await ImportTestFileAsync("sample.gpx", "Test Places", "#b81d17");
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("span:has-text('Test Places')", new() { Timeout = 10000 });

        // Verify collection chip is visible in chip bar
        var chip = Page.Locator("span:has-text('Test Places')").First;
        Assert.True(await chip.IsVisibleAsync(), "Collection chip should be visible initially");

        // The whole sidebar row is the visibility toggle now — click it to
        // turn the collection OFF.
        var toggleRow = Page.Locator(".w-60 .cursor-pointer:has-text('Test Places')").First;
        Assert.True(await toggleRow.IsVisibleAsync(), "Collection row should be visible");
        await toggleRow.ClickAsync();
        await Page.WaitForTimeoutAsync(500);

        // Eye icon should show FILL 0
        var iconStyle = await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places') span:has-text('visibility')").First.GetAttributeAsync("style");
        Assert.NotNull(iconStyle);
        Assert.Contains("'FILL' 0", iconStyle);

        // The chip should be removed from the chip bar
        var chipCount = await Page.Locator("span:has-text('Test Places')").CountAsync();
        // After visibility is off, the chip should not be in the filter bar anymore
        // (it may still exist as text in the sidebar, but not as a visible chip)
        var visibleChip = Page.Locator("span.inline-flex.items-center.gap-1:has-text('Test Places')");
        Assert.False(await visibleChip.IsVisibleAsync(), "Collection chip should be removed from filter bar");

        // Fit All button should be hidden since no collections are visible
        var fitAllButton = Page.Locator("button:has-text('Fit All')");
        Assert.False(await fitAllButton.IsVisibleAsync(), "'Fit All' button should be hidden when no collections are visible");
    }

    [Fact]
    public async Task ToggleVisibilityOn_ShowsChipAndMarkers()
    {
        // Seed GPX data
        await ImportTestFileAsync("sample.gpx", "Test Places", "#b81d17");
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("span:has-text('Test Places')", new() { Timeout = 10000 });

        // The whole sidebar row is the visibility toggle — click it OFF first.
        var toggleRow = Page.Locator(".w-60 .cursor-pointer:has-text('Test Places')").First;
        await toggleRow.ClickAsync();
        await Page.WaitForTimeoutAsync(500);

        // Verify eye icon shows FILL 0
        var iconStyle = await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places') span:has-text('visibility')").First.GetAttributeAsync("style");
        Assert.Contains("'FILL' 0", iconStyle);

        // Click the row again to turn it back ON
        await toggleRow.ClickAsync();
        await Page.WaitForTimeoutAsync(500);

        // Eye icon should change back to FILL 1
        iconStyle = await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places') span:has-text('visibility')").First.GetAttributeAsync("style");
        Assert.Contains("'FILL' 1", iconStyle);

        // A filter chip with text "Test Places" should appear in the chip bar
        // The chip uses class "inline-flex items-center gap-1.5" (Tailwind gap-1.5)
        var chip = Page.Locator("span.inline-flex:has-text('Test Places')");
        Assert.True(await chip.IsVisibleAsync(), "Collection chip should be visible in filter bar");
    }

    [Fact]
    public async Task FitAllButton_IsClickable_DoesNotCrash()
    {
        // Seed GPX data
        await ImportTestFileAsync("sample.gpx", "Test Places", "#b81d17");
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("button:has-text('Fit All')", new() { Timeout = 10000 });

        // Verify "Fit All" button is visible
        var fitAllButton = Page.Locator("button:has-text('Fit All')");
        Assert.True(await fitAllButton.IsVisibleAsync(), "'Fit All' button should be visible");

        // Click the button
        await fitAllButton.ClickAsync();
        // Playwright auto-waits; verify button remains

        // Button should still be visible after click (no crash)
        Assert.True(await fitAllButton.IsVisibleAsync(), "'Fit All' button should remain visible after click");

        // No error messages should appear
        var errors = await Page.Locator("text=/error|Error/i").CountAsync();
        Assert.Equal(0, errors);
    }

    [Fact]
    public async Task MultipleCollections_ShowWithDifferentColors()
    {
        // Import both sample.gpx and sample.kml
        await ImportTestFileAsync("sample.gpx", "GPS Data", "#b81d17");
        await ImportTestFileAsync("sample.kml", "KML Data", "#005bbf");
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync(".w-60 .flex.items-center.gap-2:has-text('KML Data')", new() { Timeout = 10000 });

        // Both collections should appear in the sidebar
        var gpsCollection = Page.Locator(".w-60 .flex.items-center.gap-2:has-text('GPS Data')");
        var kmlCollection = Page.Locator(".w-60 .flex.items-center.gap-2:has-text('KML Data')");

        Assert.True(await gpsCollection.IsVisibleAsync(), "GPS Data collection should be visible");
        Assert.True(await kmlCollection.IsVisibleAsync(), "KML Data collection should be visible");

        // Both should have different color dots
        var gpsDot = gpsCollection.Locator(".w-3.h-3.rounded-full").First;
        var kmlDot = kmlCollection.Locator(".w-3.h-3.rounded-full").First;

        var gpsColor = await gpsDot.GetAttributeAsync("style");
        var kmlColor = await kmlDot.GetAttributeAsync("style");

        Assert.NotNull(gpsColor);
        Assert.NotNull(kmlColor);
        // Verify different colors are shown
        Assert.Contains("b81d17", gpsColor);
        Assert.Contains("005bbf", kmlColor);

        // Both rows should show the visibility (eye) indicator. The eye is now
        // a <span> indicator on the row, not a nested <button> — the row
        // itself is the toggle.
        var gpsVis = gpsCollection.Locator("span:has-text('visibility')");
        var kmlVis = kmlCollection.Locator("span:has-text('visibility')");

        Assert.True(await gpsVis.First.IsVisibleAsync(), "GPS collection should show a visibility indicator");
        Assert.True(await kmlVis.First.IsVisibleAsync(), "KML collection should show a visibility indicator");
    }
}