namespace LucidCartographer.Tests.Integration;

[Collection("Integration")]
public class PoiTableTests : IntegrationTestBase
{
    [Fact]
    public async Task PoiTable_ShowsCorrectItemCountBadge()
    {
        // Pre-seed with sample.gpx (has 3 items)
        await ImportTestFileAsync("sample.gpx", "Test Places", "#b81d17");
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync(".w-60 .cursor-pointer:has-text('Test Places')", new() { Timeout = 10000 });

        // Imported collections are visible by default, so the table is
        // already populated with their POIs (the list is the union of all
        // visible collections). No collection-row click needed — clicking the
        // row now toggles visibility, which would HIDE this collection.
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

        // Table should show item count badge
        var countBadge = Page.Locator("span.text-xs.text-on-surface-variant.bg-surface-container-high:has-text('3 items')");
        Assert.True(await countBadge.IsVisibleAsync(), "Item count badge should show '3 items'");
    }

    [Fact]
    public async Task PoiTable_FocusButton_IsClickable()
    {
        // Pre-seed with sample.gpx
        await ImportTestFileAsync("sample.gpx", "Test Places", "#b81d17");
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync(".w-60 .cursor-pointer:has-text('Test Places')", new() { Timeout = 10000 });

        // Imported collections are visible by default, so the table is
        // already populated with their POIs (the list is the union of all
        // visible collections). No collection-row click needed — clicking the
        // row now toggles visibility, which would HIDE this collection.
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

        // Find a POI row and locate its focus button
        var focusButton = Page.Locator("tr:has-text('Wawel Castle') button:has(span:has-text('my_location'))").First;
        Assert.True(await focusButton.IsVisibleAsync(), "Focus button should be visible");

        // Click the focus button
        await focusButton.ClickAsync();
        // Playwright auto-waits; verify no crash

        // Should not crash - table should still be visible
        var table = Page.Locator("table");
        Assert.True(await table.IsVisibleAsync(), "Table should remain visible after clicking focus button");

        // No error messages should appear
        var errors = await Page.Locator("text=/error|Error/i").CountAsync();
        Assert.Equal(0, errors);
    }

    // NOTE: A previous "Showing 200 of 250" truncation test was removed. The production
    // PoiTable renders the item count as "{Count} items" and uses <Virtualize> to display
    // all rows — there is no truncation feature. The unit test
    // PoiTableTests.Shows_ShowingXOfY_WhenMoreThan200Pois covers the badge text.
}