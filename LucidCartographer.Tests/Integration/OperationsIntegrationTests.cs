using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

[Collection("Integration")]
public class OperationsIntegrationTests : IntegrationTestBase
{
    private async Task SeedBothCollectionsAsync()
    {
        await ImportTestFileAsync("sample.gpx", "GPX Places", "#005bbf");
        await ImportTestFileAsync("sample.kml", "KML Places", "#006e2c");
    }


    [Fact]
    public async Task OperationsPage_ShowsBothCollectionsInDropdowns()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        // Both dropdowns should contain the two collections
        var selectA = Page.Locator("select").First;
        var selectB = Page.Locator("select").Nth(1);

        var optionsA = await selectA.Locator("option").AllInnerTextsAsync();
        var optionsB = await selectB.Locator("option").AllInnerTextsAsync();

        Assert.Contains(optionsA, o => o.Contains("GPX Places"));
        Assert.Contains(optionsA, o => o.Contains("KML Places"));
        Assert.Contains(optionsB, o => o.Contains("GPX Places"));
        Assert.Contains(optionsB, o => o.Contains("KML Places"));
    }

    [Fact]
    public async Task SubtractResult_ShowsPoiNames()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        var selectA = Page.Locator("select").First;
        await selectA.SelectOptionAsync(new SelectOptionValue { Label = (await selectA.Locator("option").AllInnerTextsAsync()).First(o => o.Contains("GPX Places")) });

        var selectB = Page.Locator("select").Nth(1);
        await selectB.SelectOptionAsync(new SelectOptionValue { Label = (await selectB.Locator("option").AllInnerTextsAsync()).First(o => o.Contains("KML Places")) });

        await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

        await Page.Locator("button:has-text('A - B')").ClickAsync();
        await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });

        // Virtualize only materialises rows once its scroll container has a
        // measured height. On SPA nav into /operations the flex layout may
        // settle a tick late, leaving zero rows rendered. Nudge the window to
        // trigger a re-measure, then wait for the row.
        await Page.EvaluateAsync("() => window.dispatchEvent(new Event('resize'))");
        await Page.Locator("td:has-text('Wawel Castle')").WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await Page.Locator("td:has-text('Wawel Castle')").IsVisibleAsync(),
            "Wawel Castle should appear in the subtract result");
    }

    [Fact]
    public async Task DiscardPoi_AddsVisualStyling()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        var selectA = Page.Locator("select").First;
        await selectA.SelectOptionAsync(new SelectOptionValue { Label = (await selectA.Locator("option").AllInnerTextsAsync()).First(o => o.Contains("GPX Places")) });

        var selectB = Page.Locator("select").Nth(1);
        await selectB.SelectOptionAsync(new SelectOptionValue { Label = (await selectB.Locator("option").AllInnerTextsAsync()).First(o => o.Contains("KML Places")) });

        await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

        await Page.Locator("button:has-text('A - B')").ClickAsync();
        await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });

        // Click "Discard" on the first POI in the result table
        var discardButton = Page.Locator("button:has-text('Discard')").First;
        await discardButton.ClickAsync();
        await Page.WaitForSelectorAsync("tr.opacity-30", new() { Timeout = 5000 });

        // The row should now have opacity-30 styling
        var discardedRow = Page.Locator("tr.opacity-30").First;
        Assert.True(await discardedRow.IsVisibleAsync(),
            "Discarded POI row should have opacity-30 class applied");
    }

    [Fact]
    public async Task DedupOnSingleCollection_Works()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        // Select only collection A for dedup
        var selectA = Page.Locator("select").First;
        await selectA.SelectOptionAsync(new SelectOptionValue { Label = (await selectA.Locator("option").AllInnerTextsAsync()).First(o => o.Contains("GPX Places")) });

        await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

        // Click "Dedup" button
        await Page.Locator("button:has-text('Dedup')").ClickAsync();
        await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });

        // Result should show (same count as input if no duplicates)
        Assert.True(await Page.Locator("text=Result Preview").IsVisibleAsync(),
            "Result preview should appear after dedup");

        var resultText = await Page.Locator("p:has-text('points')").First.InnerTextAsync();
        Assert.Contains("3", resultText);
    }
}