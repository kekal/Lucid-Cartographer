using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests.ViewModels;

/// <summary>
/// Story 4.2 (TRIP-OSRM-02, AC4 / NFR8): the ViewModel surfaces the active travel-time
/// provider's declared routing-data attribution (read off
/// <see cref="ITravelTimeProvider.Attribution"/>) so the page can push it to the map's
/// attribution control. An OSM-based provider (OSRM) declares the OSM/ODbL string; the
/// haversine Mock and the no-provider construction paths declare nothing (null). This
/// guards the seam the integration host can't see (no real Leaflet attribution control).
/// </summary>
public class TripViewModelAttributionTests
{
    private sealed class FakeProvider(string? attribution) : ITravelTimeProvider
    {
        public string Source => "Fake";
        public string? Attribution => attribution;
        public Task<TravelLegResult> GetLegAsync(
            TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct) =>
            Task.FromResult(new TravelLegResult(0, 0, Fidelity.Estimated, null));
    }

    private static TripViewModel Build(ITravelTimeProvider? provider)
    {
        var factory = TestDbHelper.CreateFactory();
        var writeLock = new SqliteWriteLock();
        return new TripViewModel(
            TestDbHelper.CreateOrderingService(factory, writeLock),
            factory,
            writeLock,
            new TravelTimeTrigger(),
            new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance,
            provider);
    }

    [Fact]
    public async Task RoutingAttributionHtml_IsNull_WhenNoProvider()
    {
        await using var vm = Build(provider: null);
        vm.RoutingAttributionHtml.Should().BeNull();
    }

    [Fact]
    public async Task RoutingAttributionHtml_IsNull_UnderMockProvider()
    {
        // The haversine Mock is not OSM-derived → declares no routing attribution.
        var mock = new MockTravelTimeProvider(Options.Create(new TravelTimeOptions()));
        await using var vm = Build(mock);
        vm.RoutingAttributionHtml.Should().BeNull();
    }

    [Fact]
    public async Task RoutingAttributionHtml_SurfacesProviderAttribution_WhenOsmBased()
    {
        // Mirrors what OsrmTravelTimeProvider.Attribution returns (UiStrings, NFR5).
        await using var vm = Build(new FakeProvider(UiStrings.TripRoutingAttributionOsm));
        vm.RoutingAttributionHtml.Should().Be(UiStrings.TripRoutingAttributionOsm);
        vm.RoutingAttributionHtml.Should().NotBeNullOrWhiteSpace();
    }
}
