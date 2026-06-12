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

    // === Story 1.5: keyboard reorder on the mobile surface ===

    private static string ListSelector => $".list.trip-stop-list[aria-label=\"{UiStrings.TripStopListAria}\"]";

    private async Task<IReadOnlyList<string>> StopNamesAsync()
    {
        var names = new List<string>();
        var rows = Page.Locator($"{ListSelector} .row[data-poi-id]");
        var count = await rows.CountAsync();
        for (var i = 0; i < count; i++)
        {
            // Row aria-label is "Stop {n} of {N}: {name}" — take the name part.
            var aria = await rows.Nth(i).GetAttributeAsync("aria-label") ?? string.Empty;
            names.Add(aria[(aria.IndexOf(": ", StringComparison.Ordinal) + 2)..]);
        }
        return names;
    }

    [Fact]
    public async Task MobileKeyboardMoveDown_PersistsSameOrderWrite_AndAnnounces()
    {
        await ImportTestFileAsync("sample.gpx", "Test Places", "#005bbf");
        await MobileNavigateAndWaitForAppAsync("/");
        await Page.Locator(ToggleSelector).ClickAsync();
        await Page.Locator(ListSelector).WaitForAsync(new() { Timeout = 10000 });

        var before = await StopNamesAsync();
        Assert.True(before.Count >= 3);

        // Identical control semantics to desktop: same UiStrings aria-label,
        // same one-position move, same announcement text (AC3).
        var downLabel = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.TripMoveStopDown, before[0]);
        var down = Page.Locator($"{ListSelector} button[aria-label=\"{downLabel}\"]");

        // ≥44px touch target on the mobile move control.
        var box = await down.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.Width >= 44 && box.Height >= 44, $"move control is {box.Width}x{box.Height}, expected >=44px");

        await down.ClickAsync();

        await Page.Locator($"{ListSelector} .row[data-poi-id] >> nth=1").Filter(new() { HasText = before[0] })
            .WaitForAsync(new() { Timeout = 10000 });
        var announcement = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.TripStopMovedAnnouncement, before[0], 2, before.Count);
        await Page.Locator($"span[aria-live='polite']:has-text(\"{announcement}\")").WaitForAsync(new() { Timeout = 10000 });

        var after = await StopNamesAsync();
        Assert.Equal(before[1], after[0]);
        Assert.Equal(before[0], after[1]);
    }

    // === Story 1.7: Start/Finish designation on the mobile render path ===

    private static string Fmt(string template, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, template, args);

    private async Task<bool> WaitForLegCountAsync(int expected, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (StubMapService.LastTripLegCount == expected)
            {
                return true;
            }
            await Page.WaitForTimeoutAsync(100);
        }
        return StubMapService.LastTripLegCount == expected;
    }

    [Fact]
    public async Task MobileDesignateStart_PinsToStopOne_ShowsGlyph_AndAnnounces()
    {
        await ImportTestFileAsync("sample.gpx", "Test Places", "#005bbf");
        await MobileNavigateAndWaitForAppAsync("/");
        await Page.Locator(ToggleSelector).ClickAsync();
        await Page.Locator(ListSelector).WaitForAsync(new() { Timeout = 10000 });

        var names = await StopNamesAsync();
        Assert.True(names.Count >= 3);

        // The Set-as-Start control is present, ≥44px, and operable on mobile.
        var setStart = Page.Locator($"{ListSelector} button[aria-label=\"{Fmt(UiStrings.TripSetAsStart, names[1])}\"]");
        var box = await setStart.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.Width >= 44 && box.Height >= 44, $"start control is {box.Width}x{box.Height}, expected >=44px");

        await setStart.ClickAsync();

        // Pinned to stop 1 with the distinct Start glyph (role img + aria) and
        // the aria-live announcement — identical UiStrings text to desktop.
        await Page.Locator($"{ListSelector} .row[data-poi-id] >> nth=0").Filter(new() { HasText = names[1] })
            .WaitForAsync(new() { Timeout = 10000 });
        await Page.Locator($"{ListSelector} [aria-label=\"{Fmt(UiStrings.TripStartBadgeAria, names.Count)}\"]")
            .WaitForAsync(new() { Timeout = 10000 });
        await Page.Locator($"span[aria-live='polite']:has-text(\"{Fmt(UiStrings.TripStartSetAnnouncement, names[1])}\")")
            .WaitForAsync(new() { Timeout = 10000 });
        Assert.Equal("true", await Page
            .Locator($"{ListSelector} button[aria-label=\"{Fmt(UiStrings.TripUnsetStart, names[1])}\"]")
            .GetAttributeAsync("aria-pressed"));
    }

    [Fact]
    public async Task MobileSetFinish_OpensPath_DisablesCrossPin_AndClearRestoresRoundtrip()
    {
        StubMapService.ResetTripRecording();
        await ImportTestFileAsync("sample.gpx", "Test Places", "#005bbf");
        await MobileNavigateAndWaitForAppAsync("/");
        await Page.Locator(ToggleSelector).ClickAsync();
        await Page.Locator(ListSelector).WaitForAsync(new() { Timeout = 10000 });

        var names = await StopNamesAsync();
        var n = names.Count;
        Assert.True(n >= 3);

        // Roundtrip by default: N legs incl. the closing leg.
        Assert.True(await WaitForLegCountAsync(n), $"expected {n} roundtrip legs, saw {StubMapService.LastTripLegCount}");

        // Designate a distinct Finish ⇒ open path: stop N, glyph, announcement,
        // N−1 legs, and the cross-pin control disables.
        await Page.Locator($"{ListSelector} button[aria-label=\"{Fmt(UiStrings.TripSetAsFinish, names[0])}\"]").ClickAsync();
        await Page.Locator($"{ListSelector} .row[data-poi-id] >> nth={n - 1}").Filter(new() { HasText = names[0] })
            .WaitForAsync(new() { Timeout = 10000 });
        await Page.Locator($"{ListSelector} [aria-label=\"{Fmt(UiStrings.TripFinishBadgeAria, n)}\"]")
            .WaitForAsync(new() { Timeout = 10000 });
        await Page.Locator($"span[aria-live='polite']:has-text(\"{Fmt(UiStrings.TripOpenPathAnnounce, names[0])}\")")
            .WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await WaitForLegCountAsync(n - 1), $"expected {n - 1} open-path legs, saw {StubMapService.LastTripLegCount}");
        Assert.True(await Page.Locator($"{ListSelector} button[aria-label=\"{Fmt(UiStrings.TripSetAsStart, names[0])}\"]").IsDisabledAsync());

        // Clearing the Finish restores the Roundtrip (closing leg + announcement).
        await Page.Locator($"{ListSelector} button[aria-label=\"{Fmt(UiStrings.TripUnsetFinish, names[0])}\"]").ClickAsync();
        await Page.Locator($"span[aria-live='polite']:has-text(\"{UiStrings.TripRoundtripAnnounce}\")")
            .WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await WaitForLegCountAsync(n), $"expected the closing leg back ({n} legs), saw {StubMapService.LastTripLegCount}");
    }

    [Fact]
    public async Task MobileMoveEdges_AreDisabledGuards()
    {
        await ImportTestFileAsync("sample.gpx", "Test Places", "#005bbf");
        await MobileNavigateAndWaitForAppAsync("/");
        await Page.Locator(ToggleSelector).ClickAsync();
        await Page.Locator(ListSelector).WaitForAsync(new() { Timeout = 10000 });

        var names = await StopNamesAsync();
        var upFirst = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.TripMoveStopUp, names[0]);
        var downLast = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.TripMoveStopDown, names[^1]);

        Assert.True(await Page.Locator($"{ListSelector} button[aria-label=\"{upFirst}\"]").IsDisabledAsync());
        Assert.True(await Page.Locator($"{ListSelector} button[aria-label=\"{downLast}\"]").IsDisabledAsync());
    }
}
