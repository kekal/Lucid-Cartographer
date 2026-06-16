using LucidCartographer.Services;
using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Desktop end-to-end coverage of the Trip View toggle/seed/persist/restore flow
/// (Story 1.2). The sample GPX seeds one collection of 3 placeable POIs, so the
/// ≥1-placeable gate is satisfied and the lone-visible-collection rule holds.
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

        // Story 1.1 takeover: toggling on replaces the PoiTable with the TripStopList
        // in the same wide region — the plain table is gone and the stop-list panel
        // carries the numbered ("Stop X of N") badges for the seeded order.
        var panel = Page.Locator(StopPanelSelector);
        await panel.WaitForAsync(new() { Timeout = 10000 });
        Assert.Equal("true", await Page.Locator(ToggleSelector).GetAttributeAsync("aria-pressed"));
        await panel.Locator($"[aria-label='{Fmt(UiStrings.TripStopBadgeAria, 1, 3)}']").WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await panel.Locator($"[aria-label='{Fmt(UiStrings.TripStopBadgeAria, 2, 3)}']").IsVisibleAsync());
        Assert.True(await panel.Locator($"[aria-label='{Fmt(UiStrings.TripStopBadgeAria, 3, 3)}']").IsVisibleAsync());
        // The plain PoiTable is no longer rendered in the takeover region.
        Assert.Equal(0, await Page.Locator("td:has-text('Wawel Castle')").CountAsync());
    }

    [Fact]
    public async Task ToggleOff_RemovesBadges_WithoutLosingPois()
    {
        await SeedAsync();
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

        var toggle = Page.Locator(ToggleSelector);
        await toggle.ClickAsync();
        // Story 1.1 takeover: the stop-list panel (with its numbered badge) takes over
        // the region when Trip View is on.
        await Page.WaitForSelectorAsync(StopPanelSelector, new() { Timeout = 10000 });
        await Page.WaitForSelectorAsync($"[aria-label='{Fmt(UiStrings.TripStopBadgeAria, 1, 3)}']", new() { Timeout = 10000 });

        await Page.Locator(ToggleSelector).ClickAsync();

        // Panel gone; the plain PoiTable (all 3 POIs) is restored intact.
        await Page.WaitForSelectorAsync(StopPanelSelector, new() { State = WaitForSelectorState.Detached, Timeout = 10000 });
        Assert.Equal("false", await Page.Locator(ToggleSelector).GetAttributeAsync("aria-pressed"));
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });
        Assert.True(await Page.Locator("td:has-text('Wawel Castle')").IsVisibleAsync());
        // Presence (not visibility): the restored PoiTable is taller than before, so a
        // later row can sit below the fold. CountAsync proves no data loss without
        // depending on viewport scroll position.
        Assert.True(await Page.Locator("td:has-text('Wrocław Market Square')").CountAsync() > 0);
    }

    // Built from UiStrings so the selector tracks the localized aria-label.
    private static string StopPanelSelector => $"section[aria-label=\"{UiStrings.TripStopListAria}\"]";

    [Fact]
    public async Task TripView_TakesOverResultsRegion_AndClearsOnToggleOff()
    {
        await SeedAsync();
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

        // No trip panel before Trip View is on — the plain PoiTable owns the region.
        Assert.Equal(0, await Page.Locator(StopPanelSelector).CountAsync());

        await Page.Locator(ToggleSelector).ClickAsync();

        // Story 1.1: the stop-list panel takes over the wide results region with
        // ordered rows, replacing the PoiTable (no plain table rows remain).
        var panel = Page.Locator(StopPanelSelector);
        await panel.WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await panel.Locator("li").CountAsync() >= 3);
        Assert.True(await panel.GetByText("Wawel Castle").First.IsVisibleAsync());
        Assert.Equal(0, await Page.Locator("td:has-text('Wawel Castle')").CountAsync());

        // Toggling off removes the panel and restores the PoiTable.
        await Page.Locator(ToggleSelector).ClickAsync();
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10000 });
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });
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

    // === Story 1.5: reorder by keyboard controls and by drag ===

    private async Task<IReadOnlyList<string>> StopNamesAsync()
    {
        var labels = new List<string>();
        var rows = Page.Locator($"{StopPanelSelector} li[data-poi-id]");
        var count = await rows.CountAsync();
        for (var i = 0; i < count; i++)
        {
            // Row aria-label is "Stop {n} of {N}: {name}" — take the name part.
            var aria = await rows.Nth(i).GetAttributeAsync("aria-label") ?? string.Empty;
            labels.Add(aria[(aria.IndexOf(": ", StringComparison.Ordinal) + 2)..]);
        }
        return labels;
    }

    private async Task EnableTripViewAsync()
    {
        await SeedAsync();
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });
        await Page.Locator(ToggleSelector).ClickAsync();
        await Page.WaitForSelectorAsync(StopPanelSelector, new() { Timeout = 10000 });
    }

    [Fact]
    public async Task KeyboardMoveDown_PersistsOrder_Announces_WithoutFullReload()
    {
        await EnableTripViewAsync();

        var before = await StopNamesAsync();
        Assert.True(before.Count >= 3);

        // Marker survives only as long as no full page reload happens.
        await Page.EvaluateAsync("() => { window.__noReloadMarker = 1; }");

        // Activate the first stop's move-down control (real button, aria-labelled).
        var downLabel = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.TripMoveStopDown, before[0]);
        await Page.Locator($"{StopPanelSelector} button[aria-label=\"{downLabel}\"]").ClickAsync();

        // The stop moved exactly one position and the aria-live region announced it.
        var announcement = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.TripStopMovedAnnouncement, before[0], 2, before.Count);
        await Page.Locator($"{StopPanelSelector} li[data-poi-id] >> nth=1").Filter(new() { HasText = before[0] })
            .WaitForAsync(new() { Timeout = 10000 });
        await Page.Locator($"span[aria-live='polite']:has-text(\"{announcement}\")").WaitForAsync(new() { Timeout = 10000 });

        var after = await StopNamesAsync();
        Assert.Equal(before[1], after[0]);
        Assert.Equal(before[0], after[1]);

        // Incremental redraw, not a full reload: the marker is still set.
        Assert.Equal(1, await Page.EvaluateAsync<int>("() => window.__noReloadMarker ?? 0"));

        // Persisted: leave the Map page and come back — the new order is restored.
        await ClickDataSourcesTabAsync();
        await ClickMapTabAsync();
        await Page.WaitForSelectorAsync(StopPanelSelector, new() { Timeout = 10000 });
        var restored = await StopNamesAsync();
        Assert.Equal(after, restored);
    }

    [Fact]
    public async Task DragStopToNewPosition_PersistsRenumberedOrder()
    {
        await EnableTripViewAsync();

        var before = await StopNamesAsync();
        Assert.True(before.Count >= 3);

        // HTML5 drag of the first stop onto the last row. Dispatch the DOM drag
        // events directly (Playwright's mouse-gesture drag does not reliably
        // synthesize HTML5 dragstart/drop) — this still exercises the full
        // Blazor handler → VM → service → DB → re-render path end-to-end.
        var rows = Page.Locator($"{StopPanelSelector} li[data-poi-id]");
        var dataTransfer = await Page.EvaluateHandleAsync("() => new DataTransfer()");
        await rows.Nth(0).DispatchEventAsync("dragstart", new { dataTransfer });
        await rows.Nth(before.Count - 1).DispatchEventAsync("drop", new { dataTransfer });

        // The dragged stop lands on the target slot; the rest renumber contiguously.
        await Page.Locator($"{StopPanelSelector} li[data-poi-id] >> nth={before.Count - 1}")
            .Filter(new() { HasText = before[0] })
            .WaitForAsync(new() { Timeout = 10000 });
        var after = await StopNamesAsync();
        Assert.Equal(before[0], after[^1]);
        Assert.Equal(before[1], after[0]);

        // Persisted across SPA re-mount.
        await ClickDataSourcesTabAsync();
        await ClickMapTabAsync();
        await Page.WaitForSelectorAsync(StopPanelSelector, new() { Timeout = 10000 });
        Assert.Equal(after, await StopNamesAsync());
    }

    [Fact]
    public async Task MoveUpOnFirstStop_IsDisabledGuard()
    {
        await EnableTripViewAsync();

        var names = await StopNamesAsync();
        var upLabel = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.TripMoveStopUp, names[0]);
        var downLabel = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.TripMoveStopDown, names[^1]);

        // Edge guards: first stop can't move up, last can't move down.
        Assert.True(await Page.Locator($"{StopPanelSelector} button[aria-label=\"{upLabel}\"]").IsDisabledAsync());
        Assert.True(await Page.Locator($"{StopPanelSelector} button[aria-label=\"{downLabel}\"]").IsDisabledAsync());
    }

    // === Story 1.7: Start/Finish designation + roundtrip vs open path ===

    private static string Fmt(string template, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, template, args);

    // Polls the StubMapService trip-leg recording (the integration host stubs
    // IMapService, so leg presence is observed at the service boundary instead
    // of Leaflet DOM). Returns true when the expected count lands in time.
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
    public async Task DesignateStart_PinsToStopOne_ShowsStartBadge_AndAnnounces()
    {
        await EnableTripViewAsync();
        var before = await StopNamesAsync();
        Assert.True(before.Count >= 3);

        // Designate the SECOND stop as Start via its per-row control.
        await Page.Locator($"{StopPanelSelector} button[aria-label=\"{Fmt(UiStrings.TripSetAsStart, before[1])}\"]").ClickAsync();

        // It is pinned to stop 1, with the distinct Start badge aria, and the
        // designation is announced via the aria-live region.
        await Page.Locator($"{StopPanelSelector} li[data-poi-id] >> nth=0").Filter(new() { HasText = before[1] })
            .WaitForAsync(new() { Timeout = 10000 });
        await Page.Locator($"{StopPanelSelector} [aria-label=\"{Fmt(UiStrings.TripStartBadgeAria, before.Count)}\"]")
            .WaitForAsync(new() { Timeout = 10000 });
        await Page.Locator($"span[aria-live='polite']:has-text(\"{Fmt(UiStrings.TripStartSetAnnouncement, before[1])}\")")
            .WaitForAsync(new() { Timeout = 10000 });

        // The control flipped to Unset (aria-pressed true).
        var unset = Page.Locator($"{StopPanelSelector} button[aria-label=\"{Fmt(UiStrings.TripUnsetStart, before[1])}\"]");
        Assert.Equal("true", await unset.GetAttributeAsync("aria-pressed"));

        // The order stays contiguous: every row still numbered 1..N.
        var after = await StopNamesAsync();
        Assert.Equal(before.Count, after.Count);
    }

    [Fact]
    public async Task FinishUnset_DrawsRoundtripClosingLeg_SetFinish_OpensPath_ClearRestores()
    {
        StubMapService.ResetTripRecording();
        await EnableTripViewAsync();
        var names = await StopNamesAsync();
        var n = names.Count;
        Assert.True(n >= 3);

        // Finish unset ⇒ Roundtrip: N legs incl. the closing leg.
        Assert.True(await WaitForLegCountAsync(n), $"expected {n} roundtrip legs, saw {StubMapService.LastTripLegCount}");
        Assert.True(StubMapService.LastTripLegsRoundtrip, "the roundtrip flag rides the draw call");

        // Designate the FIRST stop as Finish ⇒ open path: it pins to stop N,
        // the closing leg disappears (N−1 legs), and the shape change announces.
        await Page.Locator($"{StopPanelSelector} button[aria-label=\"{Fmt(UiStrings.TripSetAsFinish, names[0])}\"]").ClickAsync();
        await Page.Locator($"{StopPanelSelector} li[data-poi-id] >> nth={n - 1}").Filter(new() { HasText = names[0] })
            .WaitForAsync(new() { Timeout = 10000 });
        await Page.Locator($"{StopPanelSelector} [aria-label=\"{Fmt(UiStrings.TripFinishBadgeAria, n)}\"]")
            .WaitForAsync(new() { Timeout = 10000 });
        await Page.Locator($"span[aria-live='polite']:has-text(\"{Fmt(UiStrings.TripOpenPathAnnounce, names[0])}\")")
            .WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await WaitForLegCountAsync(n - 1), $"expected {n - 1} open-path legs, saw {StubMapService.LastTripLegCount}");
        Assert.False(StubMapService.LastTripLegsRoundtrip);

        // The Finish marker role reached the map layer with its accessible name.
        Assert.NotNull(StubMapService.LastTripMarkerRoles);
        Assert.Equal(Fmt(UiStrings.TripFinishMarkerAria, names[0]), StubMapService.LastTripMarkerRoles!.FinishAria);

        // Clear the Finish ⇒ Roundtrip restored: N legs + roundtrip announcement.
        await Page.Locator($"{StopPanelSelector} button[aria-label=\"{Fmt(UiStrings.TripUnsetFinish, names[0])}\"]").ClickAsync();
        await Page.Locator($"span[aria-live='polite']:has-text(\"{UiStrings.TripRoundtripAnnounce}\")")
            .WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await WaitForLegCountAsync(n), $"expected the closing leg back ({n} legs), saw {StubMapService.LastTripLegCount}");
        Assert.True(StubMapService.LastTripLegsRoundtrip);
    }

    [Fact]
    public async Task SetAsFinish_IsDisabledOnStartRow_AndViceVersa()
    {
        await EnableTripViewAsync();
        var names = await StopNamesAsync();

        await Page.Locator($"{StopPanelSelector} button[aria-label=\"{Fmt(UiStrings.TripSetAsStart, names[0])}\"]").ClickAsync();
        await Page.Locator($"{StopPanelSelector} button[aria-label=\"{Fmt(UiStrings.TripUnsetStart, names[0])}\"]")
            .WaitForAsync(new() { Timeout = 10000 });

        // AC-6 rejection surfaced as a disabled control: the Start row cannot be Finish.
        Assert.True(await Page.Locator($"{StopPanelSelector} button[aria-label=\"{Fmt(UiStrings.TripSetAsFinish, names[0])}\"]").IsDisabledAsync());

        // Designate another stop as Finish — its Set-as-Start control disables.
        await Page.Locator($"{StopPanelSelector} button[aria-label=\"{Fmt(UiStrings.TripSetAsFinish, names[1])}\"]").ClickAsync();
        await Page.Locator($"{StopPanelSelector} button[aria-label=\"{Fmt(UiStrings.TripUnsetFinish, names[1])}\"]")
            .WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await Page.Locator($"{StopPanelSelector} button[aria-label=\"{Fmt(UiStrings.TripSetAsStart, names[1])}\"]").IsDisabledAsync());
    }

    [Fact]
    public async Task StartFinishDesignation_PersistsAcrossReopen()
    {
        await EnableTripViewAsync();
        var names = await StopNamesAsync();

        await Page.Locator($"{StopPanelSelector} button[aria-label=\"{Fmt(UiStrings.TripSetAsStart, names[1])}\"]").ClickAsync();
        await Page.Locator($"{StopPanelSelector} li[data-poi-id] >> nth=0").Filter(new() { HasText = names[1] })
            .WaitForAsync(new() { Timeout = 10000 });

        // Leave the Map page and come back — the Start pin and order are restored.
        await ClickDataSourcesTabAsync();
        await ClickMapTabAsync();
        await Page.WaitForSelectorAsync(StopPanelSelector, new() { Timeout = 10000 });
        await Page.Locator($"{StopPanelSelector} [aria-label=\"{Fmt(UiStrings.TripStartBadgeAria, names.Count)}\"]")
            .WaitForAsync(new() { Timeout = 10000 });
        var restored = await StopNamesAsync();
        Assert.Equal(names[1], restored[0]);
    }

    // === Story 4.2 (TRIP-OSRM-02): geometry through the DTO + the upgrade re-push ===

    // Waits until the StubMapService recorded a leg-geometries push whose entries
    // satisfy the predicate (e.g. "all non-null"). Returns the match or null on timeout.
    private async Task<IReadOnlyList<string?>?> WaitForLegGeometriesAsync(
        Func<IReadOnlyList<string?>, bool> predicate, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var g = StubMapService.LastTripLegGeometries;
            if (g is not null && g.Count > 0 && predicate(g))
            {
                return g;
            }
            await Page.WaitForTimeoutAsync(100);
        }
        return StubMapService.LastTripLegGeometries;
    }

    [Fact]
    public async Task EstimatedLegs_PushNullGeometry_ThenMeasuredCacheFill_RePushesPolyline()
    {
        StubMapService.ResetTripRecording();
        await EnableTripViewAsync();
        var names = await StopNamesAsync();
        Assert.True(names.Count >= 3);

        // Initial draw: no cache rows ⇒ every leg pushes null geometry (dashed/muted).
        var initial = await WaitForLegGeometriesAsync(g => g.All(x => x is null));
        Assert.NotNull(initial);
        Assert.All(initial!, x => Assert.Null(x));

        // A precision-5 encoded polyline (Google/OSRM reference sample).
        const string encoded = "_p~iF~ps|U_ulLnnqC_mqNvxq`@";

        // Fill the cache with Measured rows (carrying geometry) for EVERY directional
        // placeable pair — the active roundtrip legs are a subset, so this guarantees
        // each drawn leg now has a row. Mirrors what the OSRM background compute would
        // persist when OSRM becomes available (Estimated→Measured upgrade, UX-DR9).
        await SeedDataAsync(async db =>
        {
            var col = db.PoiCollections.Single();
            var poiIds = db.PoiCollectionItems
                .Where(i => i.PoiCollectionId == col.Id)
                .Select(i => i.PoiId)
                .ToList();
            foreach (var from in poiIds)
            {
                foreach (var to in poiIds)
                {
                    if (from == to)
                    {
                        continue;
                    }
                    db.RouteSegments.Add(new Data.Entities.RouteSegment
                    {
                        FromPoiId = from, ToPoiId = to, TravelMode = col.TravelMode,
                        DurationSeconds = 600, DistanceMeters = 8000,
                        Fidelity = Data.Entities.Fidelity.Measured,
                        Source = LucidCartographer.Services.Trip.TravelTimeSource.Osrm,
                        GeometryPolyline = encoded, ComputedAt = DateTime.UtcNow,
                    });
                }
            }
            await db.SaveChangesAsync();
        });

        // Signal progress (a leg landed in the cache). The circuit VM observes the
        // shared TravelTimeProgressService singleton, re-reads the cache, rebuilds the
        // legs WITH geometry, and re-pushes — the AC3 dedup must NOT swallow this
        // (LegsEqual compares GeometryPolyline + IsMeasured). Use a non-zero value so
        // BehaviorSubject emits a real change past the Skip(1).
        GetAppService<LucidCartographer.Services.Trip.TravelTimeProgressService>().Set(1);

        var upgraded = await WaitForLegGeometriesAsync(g => g.All(x => x == encoded));
        Assert.NotNull(upgraded);
        Assert.All(upgraded!, x => Assert.Equal(encoded, x));
    }

    [Fact]
    public async Task TripViewState_PersistsAcrossReopen()
    {
        await SeedAsync();
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

        await Page.Locator(ToggleSelector).ClickAsync();
        // Story 1.1 takeover: the stop-list panel (numbered "Stop X of N" badges)
        // owns the region when Trip View is on.
        await Page.WaitForSelectorAsync($"[aria-label='{Fmt(UiStrings.TripStopBadgeAria, 1, 3)}']", new() { Timeout = 10000 });

        // Leave the Map page and come back (SPA navigation re-mounts MapPage).
        await ClickDataSourcesTabAsync();
        await ClickMapTabAsync();

        // Reopening restores Trip View on + the stop-list panel with its badges.
        await Page.WaitForSelectorAsync($"[aria-label='{Fmt(UiStrings.TripStopBadgeAria, 1, 3)}']", new() { Timeout = 10000 });
        Assert.Equal("true", await Page.Locator(ToggleSelector).GetAttributeAsync("aria-pressed"));
    }
}
