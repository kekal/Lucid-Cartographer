using LucidCartographer.Services;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Mobile end-to-end coverage of the Trip View toggle on the phone render path
/// (Story 1.2, dual-surface requirement). Mirrors the desktop flow: the toggle
/// lives in the mobile bottom-panel results region, seeds the order on enable,
/// and shows Stop badges in the mobile list.
/// </summary>
[Collection("Integration")]
public class MobileTripViewTests : MobileTestBase
{
    private static string ToggleSelector => $"button[role='switch'][aria-label=\"{UiStrings.TripViewToggleAria}\"]";

    [Fact]
    public async Task MobileToggle_SeedsBadges_OnEnable()
    {
        await ImportTestFileAsync("sample.gpx", "Test Places", "#005bbf");
        await MobileNavigateAndWaitForAppAsync("/");

        // Mobile bottom-panel results header hosts the toggle.
        var toggle = Page.Locator(ToggleSelector);
        await toggle.WaitForAsync(new() { Timeout = 15000 });
        Assert.Equal("false", await toggle.GetAttributeAsync("aria-pressed"));

        await toggle.ClickAsync();

        // Badges appear on the mobile list rows and the toggle reflects on-state.
        await Page.WaitForSelectorAsync("[aria-label='Stop 1']", new() { Timeout = 10000 });
        Assert.Equal("true", await Page.Locator(ToggleSelector).GetAttributeAsync("aria-pressed"));
    }

    [Fact]
    public async Task MobileTripView_ShowsStopListInBottomPanel_OnEnable()
    {
        await ImportTestFileAsync("sample.gpx", "Test Places", "#005bbf");
        await MobileNavigateAndWaitForAppAsync("/");

        await Page.Locator(ToggleSelector).ClickAsync();

        // The mobile bottom panel swaps to the Trip stop list (in travel order),
        // while the map stays at the ~46% top. The toggle stays in the header.
        var list = Page.Locator($".list[aria-label=\"{UiStrings.TripStopListAria}\"]");
        await list.WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await list.Locator(".row[data-poi-id]").CountAsync() >= 3);
        Assert.Equal("true", await Page.Locator(ToggleSelector).GetAttributeAsync("aria-pressed"));
    }

    [Fact]
    public async Task MobileTripStopRow_Selection_SetsAriaCurrent()
    {
        await ImportTestFileAsync("sample.gpx", "Test Places", "#005bbf");
        await MobileNavigateAndWaitForAppAsync("/");
        await Page.Locator(ToggleSelector).ClickAsync();

        var list = Page.Locator($".list.trip-stop-list[aria-label=\"{UiStrings.TripStopListAria}\"]");
        await list.WaitForAsync(new() { Timeout = 10000 });

        // Tapping a mobile stop row marks it selected (list→map). The map interop
        // (pan/emphasis) is stubbed in integration; the row state is the
        // DOM-observable proof of the sync on this surface.
        await list.Locator(".row[data-poi-id]").First.ClickAsync();
        await Page.Locator(".list.trip-stop-list .row[aria-current='true']")
            .WaitForAsync(new() { Timeout = 10000 });
        Assert.Equal(1, await Page.Locator(".list.trip-stop-list .row[aria-current]").CountAsync());
    }
}
