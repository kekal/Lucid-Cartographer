using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests.ViewModels;

/// <summary>
/// <see cref="TripViewModel.RecommendsMeasuredProvider"/> drives the quiet "enable Valhalla
/// for measured road times" note on a default (no-measured-provider) deployment. It is true
/// only for a non-measured-capable provider (null/Mock, ProducesMeasuredFidelity != true)
/// with at least one NORMALLY Estimated (non-fallback) leg, and stays DISTINCT from
/// <see cref="TripViewModel.IsShowingApproximateEstimates"/> (the engine-unreachable
/// fallback). Capability-gated, so ANY measured provider (e.g. Valhalla) suppresses it
/// (Epic 3: the prior gate keyed on the now-removed OSRM source string).
/// </summary>
public class TripViewModelRecommendsMeasuredProviderTests
{
    private const int CollectionId = 1;

    // A stub provider that declares its measured capability — used to simulate a measured
    // (Valhalla) deployment where the recommendation must be suppressed.
    private sealed class StubProvider(string source, bool producesMeasured) : ITravelTimeProvider
    {
        public string Source => source;
        public string? Attribution => null;
        public bool ProducesMeasuredFidelity => producesMeasured;
        public Task<TravelLegResult> GetLegAsync(
            TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct) =>
            Task.FromResult(new TravelLegResult(0, 0, Fidelity.Estimated, null));
    }

    private static IDbContextFactory<AppDbContext> Seed(int placeable, string travelMode = TravelMode.Drive)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = travelMode,
        });
        for (var i = 1; i <= placeable; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId, OutgoingTravelMode = travelMode });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task AddSegmentAsync(
        IDbContextFactory<AppDbContext> factory, int from, int to, string fidelity,
        string source = TravelTimeSource.Mock, string travelMode = TravelMode.Drive)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = from, ToPoiId = to, TravelMode = travelMode,
            DurationSeconds = 4800, DistanceMeters = 12000,
            Fidelity = fidelity, Source = source, ComputedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<TripViewModel> EnabledVmAsync(
        IDbContextFactory<AppDbContext> factory, int placeable, ITravelTimeProvider? provider = null)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(
            ordering, factory, writeLock,
            new TravelTimeTrigger(), new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance,
            provider);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return vm;
    }

    [Fact]
    public async Task Recommends_True_ForNullProvider_WithNonFallbackEstimatedLegs()
    {
        var factory = Seed(placeable: 2);
        await AddSegmentAsync(factory, 1, 2, Fidelity.Estimated);
        await AddSegmentAsync(factory, 2, 1, Fidelity.Estimated);
        await using var vm = await EnabledVmAsync(factory, placeable: 2, provider: null);

        vm.RecommendsMeasuredProvider.Should().BeTrue();
        // It is the normal Mock-Estimated state, NOT the engine-unreachable fallback.
        vm.IsShowingApproximateEstimates.Should().BeFalse();
    }

    [Fact]
    public async Task Recommends_True_ForMockProvider_WithNonFallbackEstimatedLegs()
    {
        var factory = Seed(placeable: 2);
        await AddSegmentAsync(factory, 1, 2, Fidelity.Estimated);
        await AddSegmentAsync(factory, 2, 1, Fidelity.Estimated);
        await using var vm = await EnabledVmAsync(factory, placeable: 2, new StubProvider(TravelTimeSource.Mock, producesMeasured: false));

        vm.RecommendsMeasuredProvider.Should().BeTrue();
    }

    [Fact]
    public async Task Recommends_False_WhenProviderIsMeasuredCapable()
    {
        var factory = Seed(placeable: 2);
        // Estimated legs present, but a measured-capable provider (Valhalla) is configured ⇒ no recommendation.
        await AddSegmentAsync(factory, 1, 2, Fidelity.Estimated);
        await AddSegmentAsync(factory, 2, 1, Fidelity.Estimated);
        await using var vm = await EnabledVmAsync(factory, placeable: 2, new StubProvider(TravelTimeSource.Valhalla, producesMeasured: true));

        vm.RecommendsMeasuredProvider.Should().BeFalse();
    }

    [Fact]
    public async Task Recommends_False_WhenOnlyFallbackEstimatedLegs()
    {
        var factory = Seed(placeable: 2);
        // The only "estimates" are the engine-unreachable fallback — the fallback note
        // covers this; the measured-provider recommendation note must stay distinct (not shown).
        await AddSegmentAsync(factory, 1, 2, Fidelity.Estimated, TravelTimeSource.EstimatedFallback);
        await AddSegmentAsync(factory, 2, 1, Fidelity.Estimated, TravelTimeSource.EstimatedFallback);
        await using var vm = await EnabledVmAsync(factory, placeable: 2, provider: null);

        vm.IsShowingApproximateEstimates.Should().BeTrue();
        vm.RecommendsMeasuredProvider.Should().BeFalse();
    }

    [Fact]
    public async Task Recommends_False_WhenNoLegs()
    {
        // Trip View off / no enabled trip ⇒ no legs ⇒ no recommendation.
        var factory = Seed(placeable: 2);
        var writeLock = new SqliteWriteLock();
        await using var vm = new TripViewModel(
            TestDbHelper.CreateOrderingService(factory, writeLock), factory, writeLock,
            new TravelTimeTrigger(), new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 2);

        vm.OrderedLegs.Should().BeEmpty();
        vm.RecommendsMeasuredProvider.Should().BeFalse();
    }

    [Fact]
    public async Task Recommends_False_ForAnyAirPlaceholderLegs()
    {
        // Any/Air with no manual entry ⇒ Placeholder legs (not Estimated) ⇒ no recommendation.
        var factory = Seed(placeable: 2, travelMode: TravelMode.AnyAir);
        await AddSegmentAsync(factory, 1, 2, Fidelity.Placeholder, travelMode: TravelMode.AnyAir);
        await AddSegmentAsync(factory, 2, 1, Fidelity.Placeholder, travelMode: TravelMode.AnyAir);
        await using var vm = await EnabledVmAsync(factory, placeable: 2, provider: null);

        vm.RecommendsMeasuredProvider.Should().BeFalse();
    }

    [Fact]
    public async Task Recommends_False_ForMeasuredLegs()
    {
        // Measured legs (e.g. cached from a prior measured run) ⇒ nothing to recommend.
        var factory = Seed(placeable: 2);
        await AddSegmentAsync(factory, 1, 2, Fidelity.Measured, TravelTimeSource.Valhalla);
        await AddSegmentAsync(factory, 2, 1, Fidelity.Measured, TravelTimeSource.Valhalla);
        await using var vm = await EnabledVmAsync(factory, placeable: 2, provider: null);

        vm.RecommendsMeasuredProvider.Should().BeFalse();
    }
}
