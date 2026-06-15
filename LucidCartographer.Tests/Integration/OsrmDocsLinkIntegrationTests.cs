using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Story 2.4 (FR-8/10, RD11) — end-to-end coverage of the "How to enable OSRM" link in
/// the Trip View Mock-estimate note. The bUnit tests already assert the link's markup
/// (href / target / rel / text); this exercises the part only an integration test can:
/// actually CLICKING it must open the operator guide that the app SERVES, not a 404 /
/// SPA fallback. Guards against the link silently rotting (e.g. the doc not being
/// shipped — it is an embedded resource served by DocsEndpoints, since UseStaticFiles
/// won't serve .md and the Docker image strips *.md).
/// </summary>
[Collection("Integration")]
public class OsrmDocsLinkIntegrationTests : IntegrationTestBase
{
    private const string Collection = "OSRM Note Trip";

    private static string ToggleSelector => $"button[role='switch'][aria-label=\"{UiStrings.TripViewToggleAria}\"]";
    private static string StopPanelSelector => $"section[aria-label=\"{UiStrings.TripStopListAria}\"]";

    // The Mock-estimate OSRM-recommendation note renders only when the VM RecommendsOsrm:
    // a Mock/null provider (the integration host injects MockTravelTimeProvider) AND at
    // least one NORMALLY Estimated, non-fallback leg. Seed each stop's outgoing leg as a
    // Drive/Mock-Estimated cache row so the active legs resolve to Estimated (Any/Air
    // would be Placeholder and suppress the note).
    private async Task SeedMockEstimatedTripAsync()
    {
        await ImportTestFileAsync("sample.gpx", Collection, "#005bbf");
        await SeedDataAsync(async db =>
        {
            var collection = db.PoiCollections.Single(c => c.Name == Collection);
            var poiIds = db.PoiCollectionItems
                .Where(i => i.PoiCollectionId == collection.Id)
                .Select(i => i.PoiId)
                .ToList();

            // Drive the legs by Drive so they look up the Drive cache rows below.
            foreach (var item in db.PoiCollectionItems.Where(i => i.PoiCollectionId == collection.Id))
            {
                item.OutgoingTravelMode = TravelMode.Drive;
            }

            // Estimated/Mock rows for every directional pair — a superset of the active
            // legs, so each drawn leg has a normal Mock-Estimated row (Source = Mock, NOT
            // EstimatedFallback) and RecommendsOsrm is true.
            foreach (var from in poiIds)
            {
                foreach (var to in poiIds)
                {
                    if (from == to)
                    {
                        continue;
                    }

                    db.RouteSegments.Add(new RouteSegment
                    {
                        FromPoiId = from, ToPoiId = to, TravelMode = TravelMode.Drive,
                        DurationSeconds = 4800, DistanceMeters = 12000,
                        Fidelity = Fidelity.Estimated, Source = TravelTimeSource.Mock,
                        ComputedAt = DateTime.UtcNow,
                    });
                }
            }

            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task ClickingOsrmLink_OpensServedOperatorGuide_NotA404()
    {
        await SeedMockEstimatedTripAsync();
        await NavigateAndWaitAsync("/");
        await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

        // Enter Trip View so the stop-list panel (and its Mock-estimate note) renders.
        await Page.Locator(ToggleSelector).ClickAsync();
        await Page.WaitForSelectorAsync(StopPanelSelector, new() { Timeout = 10000 });

        // The quiet OSRM-recommendation note carries the link by its exact localized text.
        var link = Page.Locator($"a:has-text(\"{UiStrings.TripMockEstimateOsrmLink}\")");
        await link.WaitForAsync(new() { Timeout = 10000 });
        Assert.Equal(UiStrings.TripMockEstimateOsrmHref, await link.GetAttributeAsync("href"));
        Assert.Equal("_blank", await link.GetAttributeAsync("target"));

        // Clicking opens a new tab (target=_blank). Capture the popup AND its navigation
        // response so we can assert the doc is actually served (200), not a 404.
        IResponse? docResponse = null;
        var popup = await Page.RunAndWaitForPopupAsync(async () =>
        {
            await link.ClickAsync();
        });
        popup.Response += (_, r) =>
        {
            if (r.Url.EndsWith("/docs/osrm.md", StringComparison.Ordinal))
            {
                docResponse = r;
            }
        };
        await popup.WaitForLoadStateAsync(LoadState.Load);

        // The new tab landed on the served doc path...
        Assert.EndsWith("/docs/osrm.md", popup.Url, StringComparison.Ordinal);

        // ...and the response was a real 200 for that path. (The popup's own navigation
        // response is the reliable source; the event handler above is a backstop.)
        var status = docResponse?.Status
            ?? (await popup.Context.APIRequest.GetAsync($"{BaseUrl}/docs/osrm.md")).Status;
        Assert.Equal(200, status);

        // ...serving the OPERATOR GUIDE, not the Blazor SPA shell (a 404 would fall
        // through to the app and render the layout, never this heading from osrm.md).
        var body = await popup.InnerTextAsync("body");
        Assert.Contains("Enabling OSRM measured travel times", body, StringComparison.Ordinal);
    }
}
