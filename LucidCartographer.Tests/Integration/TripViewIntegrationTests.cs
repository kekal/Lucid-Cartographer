using LucidCartographer.Services;
using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Desktop end-to-end coverage of the Trip View toggle/seed/persist/restore flow
/// (Story 1.2). The sample GPX seeds one collection of 3 placeable POIs, so the
/// ≥2-placeable gate is satisfied and the lone-visible-collection rule holds.
/// </summary>
[Collection("Integration")]
public class TripViewIntegrationTests : IntegrationTestBase
{
    // Built from UiStrings so the selector tracks the localized aria-label.
    private static string ToggleSelector => $"button[role='switch'][aria-label=\"{UiStrings.TripViewToggleAria}\"]";

    private async Task SeedAsync() => await ImportTestFileAsync("sample.gpx", "Test Places", "#005bbf");

    [Fact]
    public async Task Toggle_AppearsInResultsRegion_AndSeedsStopBadges()
    {
        await SeedAsync();
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

        // The toggle is rendered (in the filtered-results header, not a menu) and off.
        var toggle = Page.Locator(ToggleSelector);
        await toggle.WaitForAsync(new() { Timeout = 10000 });
        Assert.Equal("false", await toggle.GetAttributeAsync("aria-pressed"));

        await toggle.ClickAsync();

        // Toggling on seeds the order and shows badges in the list, and announces.
        await Page.WaitForSelectorAsync("[aria-label='Stop 1']", new() { Timeout = 10000 });
        Assert.Equal("true", await Page.Locator(ToggleSelector).GetAttributeAsync("aria-pressed"));
        Assert.True(await Page.Locator("[aria-label='Stop 2']").IsVisibleAsync());
        Assert.True(await Page.Locator("[aria-label='Stop 3']").IsVisibleAsync());
    }

    [Fact]
    public async Task ToggleOff_RemovesBadges_WithoutLosingPois()
    {
        await SeedAsync();
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

        var toggle = Page.Locator(ToggleSelector);
        await toggle.ClickAsync();
        await Page.WaitForSelectorAsync("[aria-label='Stop 1']", new() { Timeout = 10000 });

        await Page.Locator(ToggleSelector).ClickAsync();

        // Badges gone; the plain collection (all 3 POIs) is intact.
        await Page.WaitForSelectorAsync("[aria-label='Stop 1']", new() { State = WaitForSelectorState.Detached, Timeout = 10000 });
        Assert.Equal("false", await Page.Locator(ToggleSelector).GetAttributeAsync("aria-pressed"));
        Assert.True(await Page.Locator("td:has-text('Wawel Castle')").IsVisibleAsync());
        Assert.True(await Page.Locator("td:has-text('Wrocław Market Square')").IsVisibleAsync());
    }

    // Built from UiStrings so the selector tracks the localized aria-label.
    private static string StopPanelSelector => $"section[aria-label=\"{UiStrings.TripStopListAria}\"]";

    [Fact]
    public async Task TripView_ShowsStopListPanel_BesideMap_AndClearsOnToggleOff()
    {
        await SeedAsync();
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

        // No trip panel before Trip View is on.
        Assert.Equal(0, await Page.Locator(StopPanelSelector).CountAsync());

        await Page.Locator(ToggleSelector).ClickAsync();

        // The desktop stop-list panel renders beside the map with ordered rows.
        var panel = Page.Locator(StopPanelSelector);
        await panel.WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await panel.Locator("li").CountAsync() >= 3);
        Assert.True(await panel.GetByText("Wawel Castle").First.IsVisibleAsync());

        // Toggling off removes the panel (no orphaned trip UI).
        await Page.Locator(ToggleSelector).ClickAsync();
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10000 });
    }

    [Fact]
    public async Task TripStopRow_Selection_SetsAriaCurrent_AndReplacesPrior()
    {
        await SeedAsync();
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });
        await Page.Locator(ToggleSelector).ClickAsync();
        await Page.WaitForSelectorAsync(StopPanelSelector, new() { Timeout = 10000 });

        var rows = Page.Locator($"{StopPanelSelector} li[data-poi-id]");
        var firstId = await rows.Nth(0).GetAttributeAsync("data-poi-id");
        var secondId = await rows.Nth(1).GetAttributeAsync("data-poi-id");

        // Selecting a row marks it current (list→map selection state).
        await rows.Nth(0).ClickAsync();
        await Page.Locator($"{StopPanelSelector} li[data-poi-id='{firstId}'][aria-current='true']")
            .WaitForAsync(new() { Timeout = 10000 });

        // Selecting another row moves the selection — exactly one row is current.
        await rows.Nth(1).ClickAsync();
        await Page.Locator($"{StopPanelSelector} li[data-poi-id='{secondId}'][aria-current='true']")
            .WaitForAsync(new() { Timeout = 10000 });
        Assert.Equal(1, await Page.Locator($"{StopPanelSelector} li[aria-current]").CountAsync());
        Assert.Null(await Page.Locator($"{StopPanelSelector} li[data-poi-id='{firstId}']").GetAttributeAsync("aria-current"));
    }

    [Fact]
    public async Task TripViewState_PersistsAcrossReopen()
    {
        await SeedAsync();
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

        await Page.Locator(ToggleSelector).ClickAsync();
        await Page.WaitForSelectorAsync("[aria-label='Stop 1']", new() { Timeout = 10000 });

        // Leave the Map page and come back (SPA navigation re-mounts MapPage).
        await ClickDataSourcesTabAsync();
        await ClickMapTabAsync();

        // Reopening restores Trip View on + the Stop Order badges.
        await Page.WaitForSelectorAsync("[aria-label='Stop 1']", new() { Timeout = 10000 });
        Assert.Equal("true", await Page.Locator(ToggleSelector).GetAttributeAsync("aria-pressed"));
    }
}
