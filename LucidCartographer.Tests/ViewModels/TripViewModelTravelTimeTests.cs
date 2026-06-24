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
/// Story 2.1 (AC 4, 5, 7): the ViewModel reads RouteSegment cache rows into the
/// TripLeg projection (duration/distance/fidelity), renders missing rows as null
/// (â‡’ "â€”" + computing), sums computed legs into the total, and shows a null total
/// (â‡’ "â€”") whenever any leg is uncomputed.
/// </summary>
public class TripViewModelTravelTimeTests
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed(int placeable)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.AnyAir,
        });
        for (var i = 1; i <= placeable; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task<TripViewModel> EnabledVmAsync(IDbContextFactory<AppDbContext> factory, int placeable)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(
            ordering, factory, writeLock,
            new TravelTimeTrigger(), new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return vm;
    }

    private static async Task AddSegmentAsync(
        IDbContextFactory<AppDbContext> factory, int from, int to, int seconds, double meters, string fidelity,
        string source = "Mock", string? geometry = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = from, ToPoiId = to, TravelMode = TravelMode.AnyAir,
            DurationSeconds = seconds, DistanceMeters = meters,
            Fidelity = fidelity, Source = source, ComputedAt = DateTime.UtcNow,
            GeometryPolyline = geometry,
        });
        await db.SaveChangesAsync();
    }

    // --- Story 4.2 (TRIP-OSRM-02): geometry flows into the leg projection ---

    [Fact]
    public async Task MeasuredLeg_WithGeometry_CarriesPolyline_AndIsMeasured()
    {
        var factory = Seed(placeable: 2);
        // A Measured row with an encoded polyline (the "_p~iF~ps|U" algorithm-reference
        // sample). Geometry threads through verbatim regardless of source. The other leg has none.
        const string encoded = "_p~iF~ps|U_ulLnnqC_mqNvxq`@";
        await AddSegmentAsync(factory, 1, 2, 600, 8000, Fidelity.Measured, TravelTimeSource.Valhalla, encoded);
        await AddSegmentAsync(factory, 2, 1, 600, 8000, Fidelity.Measured, TravelTimeSource.Valhalla, encoded);

        await using var vm = await EnabledVmAsync(factory, 2);

        var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        leg.IsMeasured.Should().BeTrue();
        leg.GeometryPolyline.Should().Be(encoded, "the measured road geometry threads through MakeLeg verbatim");
    }

    [Fact]
    public async Task NonMeasuredRows_CarryNullGeometry()
    {
        var factory = Seed(placeable: 2);
        // Estimated and Placeholder rows never carry road geometry.
        await AddSegmentAsync(factory, 1, 2, 600, 8000, Fidelity.Estimated, TravelTimeSource.Mock);
        await AddSegmentAsync(factory, 2, 1, 720, 9000, Fidelity.Placeholder, TravelTimeSource.Mock);

        await using var vm = await EnabledVmAsync(factory, 2);

        vm.OrderedLegs.Should().OnlyContain(l => l.GeometryPolyline == null,
            "no road geometry for Estimated/Placeholder ⇒ dashed/muted render");
    }

    [Fact]
    public async Task UncomputedLeg_HasNullGeometry()
    {
        var factory = Seed(placeable: 2);
        // No cache rows at all — both roundtrip legs are uncomputed (Air/dashed).
        await using var vm = await EnabledVmAsync(factory, 2);

        vm.OrderedLegs.Should().OnlyContain(l => l.GeometryPolyline == null);
    }

    [Fact]
    public async Task Legs_PopulateFromCache_WhenRowsExist()
    {
        var factory = Seed(placeable: 2);
        // Roundtrip over P1,P2 â‡’ legs 1â†’2 and 2â†’1. Seed both.
        await AddSegmentAsync(factory, 1, 2, 600, 8000, Fidelity.Estimated);
        await AddSegmentAsync(factory, 2, 1, 600, 8000, Fidelity.Estimated);

        await using var vm = await EnabledVmAsync(factory, 2);

        vm.OrderedLegs.Should().HaveCount(2);
        var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        leg.DurationSeconds.Should().Be(600);
        leg.DistanceMeters.Should().Be(8000);
        leg.Fidelity.Should().Be(Fidelity.Estimated);
        leg.IsMeasured.Should().BeFalse("Estimated is not Measured");
        vm.IsAnyLegComputing.Should().BeFalse();
        vm.TotalTravelTimeSeconds.Should().Be(1200, "Î£ of both computed legs");
    }

    [Fact]
    public async Task Leg_MissingCacheRow_LeavesNullFields_AndTotalEmDash()
    {
        var factory = Seed(placeable: 2);
        // Only one of the two roundtrip legs is computed.
        await AddSegmentAsync(factory, 1, 2, 600, 8000, Fidelity.Estimated);

        await using var vm = await EnabledVmAsync(factory, 2);

        var uncomputed = vm.OrderedLegs.First(l => l.FromPoiId == 2 && l.ToPoiId == 1);
        uncomputed.DurationSeconds.Should().BeNull();
        uncomputed.DistanceMeters.Should().BeNull();
        uncomputed.Fidelity.Should().BeNull();

        vm.IsAnyLegComputing.Should().BeTrue();
        vm.TotalTravelTimeSeconds.Should().BeNull("any uncomputed leg â‡’ total is em-dash, not false precision");
    }

    [Fact]
    public async Task ComputedPlaceholderLeg_HidesDuration_AndIsNotComputing()
    {
        // TRIP-TRAVELMODE-01 (AC4): once the background service computes an Any/Air
        // leg it writes a Placeholder row carrying a real straight-line air estimate.
        // That estimate must NEVER surface â€” the leg's display duration is null (â‡’
        // "â€”"), the total is "â€”", but the leg is NOT "computing" (its row exists).
        var factory = Seed(placeable: 2);
        await AddSegmentAsync(factory, 1, 2, 720, 9000, Fidelity.Placeholder);
        await AddSegmentAsync(factory, 2, 1, 720, 9000, Fidelity.Placeholder);

        await using var vm = await EnabledVmAsync(factory, 2);

        var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        leg.Fidelity.Should().Be(Fidelity.Placeholder, "the row exists and is Placeholder");
        leg.DurationSeconds.Should().BeNull("a Placeholder air estimate is never shown as a real time");
        leg.DistanceMeters.Should().Be(9000, "the haversine distance is real and may show");
        vm.IsAnyLegComputing.Should().BeFalse("a computed Placeholder leg is not still computing");
        vm.TotalTravelTimeSeconds.Should().BeNull("a Placeholder leg keeps the total honest ('â€”')");
    }

    [Fact]
    public async Task IsMeasured_TrueOnlyForMeasuredFidelity()
    {
        var factory = Seed(placeable: 2);
        await AddSegmentAsync(factory, 1, 2, 600, 8000, Fidelity.Measured);
        await AddSegmentAsync(factory, 2, 1, 600, 8000, Fidelity.Measured);

        await using var vm = await EnabledVmAsync(factory, 2);

        vm.OrderedLegs.Should().OnlyContain(l => l.IsMeasured);
    }

    // --- Story 2.1 (TRIP-RECONCILE-01): displayed total == Σ displayed per-leg ---

    [Fact]
    public async Task ReconciledTotal_EqualsSumOfDisplayedPerLegMinutes_NotRawSeconds()
    {
        var factory = Seed(placeable: 2);
        // Roundtrip ⇒ legs 1→2 and 2→1, each 90s. Raw Σ = 180s (Duration ⇒ "3m"),
        // but each leg DISPLAYS as DisplayMinutes(90)=2m, so the honest total is 4m.
        await AddSegmentAsync(factory, 1, 2, 90, 8000, Fidelity.Estimated);
        await AddSegmentAsync(factory, 2, 1, 90, 8000, Fidelity.Estimated);

        await using var vm = await EnabledVmAsync(factory, 2);

        // Total stored as Σ DisplayMinutes(leg) × 60 = (2+2) × 60 = 240, NOT raw 180.
        vm.TotalTravelTimeSeconds.Should().Be(240);

        // The reconciliation invariant: displayed total == Σ of displayed per-leg times.
        var displayedTotal = TravelTimeFormatting.Duration(vm.TotalTravelTimeSeconds);
        var sumOfPerLeg = vm.OrderedLegs.Sum(l => TravelTimeFormatting.DisplayMinutes(l.DurationSeconds!.Value));
        TravelTimeFormatting.Duration(sumOfPerLeg * 60).Should().Be(displayedTotal);
        displayedTotal.Should().Be("4 min", "round-once: 2 min + 2 min, not Duration(180s)='3 min'");
    }

    [Fact]
    public async Task ReconciledTotal_PartialTrip_AnyLeg_IsEmDash()
    {
        var factory = Seed(placeable: 2);
        // Only one leg computed; the other is uncomputed (Any/Air) ⇒ partial em-dash.
        await AddSegmentAsync(factory, 1, 2, 90, 8000, Fidelity.Estimated);

        await using var vm = await EnabledVmAsync(factory, 2);

        vm.TotalTravelTimeSeconds.Should().BeNull("any uncomputed leg ⇒ total em-dash");
        TravelTimeFormatting.Duration(vm.TotalTravelTimeSeconds)
            .Should().Be(UiStrings.TripLegTimeUnknown);
    }

    [Fact]
    public async Task ReconciledTotal_EstimatedFallbackLegs_KeepRealDuration_AndReconcile()
    {
        var factory = Seed(placeable: 2);
        // Engine-unreachable fallback: real Estimated durations with sub-minute remainders.
        await AddSegmentAsync(factory, 1, 2, 150, 8000, Fidelity.Estimated, TravelTimeSource.EstimatedFallback);
        await AddSegmentAsync(factory, 2, 1, 30, 8000, Fidelity.Estimated, TravelTimeSource.EstimatedFallback);

        await using var vm = await EnabledVmAsync(factory, 2);

        vm.IsShowingApproximateEstimates.Should().BeTrue("EstimatedFallback legs degrade the trip");
        // DisplayMinutes(150)=3, DisplayMinutes(30)=1 ⇒ total 4m (240s), not Duration(180s)='3m'.
        vm.TotalTravelTimeSeconds.Should().Be(240);
        TravelTimeFormatting.Duration(vm.TotalTravelTimeSeconds).Should().Be("4 min");
    }

    // --- Story 2.3 (TRIP-DEGRADE-01): IsShowingApproximateEstimates flag ---

    [Fact]
    public async Task IsShowingApproximateEstimates_True_WhenAnyLegIsFallback()
    {
        var factory = Seed(placeable: 2);
        // 1â†’2 is a degraded fallback (EstimatedFallback); 2â†’1 is a normal estimate.
        await AddSegmentAsync(factory, 1, 2, 600, 8000, Fidelity.Estimated, TravelTimeSource.EstimatedFallback);
        await AddSegmentAsync(factory, 2, 1, 600, 8000, Fidelity.Estimated, TravelTimeSource.Mock);

        await using var vm = await EnabledVmAsync(factory, 2);

        vm.IsShowingApproximateEstimates.Should().BeTrue("a leg backed by EstimatedFallback degrades the trip");
        var degraded = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        degraded.IsFallback.Should().BeTrue();
        degraded.DurationSeconds.Should().Be(600, "a fallback Estimated leg keeps its real duration");
    }

    [Fact]
    public async Task IsShowingApproximateEstimates_False_ForNormalMockAndManualRows()
    {
        var factory = Seed(placeable: 2);
        await AddSegmentAsync(factory, 1, 2, 600, 8000, Fidelity.Estimated, TravelTimeSource.Mock);
        await AddSegmentAsync(factory, 2, 1, 600, 8000, Fidelity.Manual, TravelTimeSource.Manual);

        await using var vm = await EnabledVmAsync(factory, 2);

        vm.IsShowingApproximateEstimates.Should().BeFalse("a normal Mock/Manual trip is not degraded");
        vm.OrderedLegs.Should().NotContain(l => l.IsFallback);
    }

    [Fact]
    public async Task IsShowingApproximateEstimates_False_ForPlaceholderRows()
    {
        var factory = Seed(placeable: 2);
        await AddSegmentAsync(factory, 1, 2, 600, 8000, Fidelity.Placeholder, TravelTimeSource.Mock);
        await AddSegmentAsync(factory, 2, 1, 600, 8000, Fidelity.Placeholder, TravelTimeSource.Mock);

        await using var vm = await EnabledVmAsync(factory, 2);

        vm.IsShowingApproximateEstimates.Should().BeFalse("Any/Air Placeholder legs are never degradations");
    }
}
