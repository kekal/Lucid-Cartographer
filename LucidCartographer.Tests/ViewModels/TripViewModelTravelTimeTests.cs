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
/// (⇒ "—" + computing), sums computed legs into the total, and shows a null total
/// (⇒ "—") whenever any leg is uncomputed.
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
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        var vm = new TripViewModel(
            ordering, factory, writeLock,
            new TravelTimeTrigger(), new TravelTimeProgressService(),
            NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return vm;
    }

    private static async Task AddSegmentAsync(
        IDbContextFactory<AppDbContext> factory, int from, int to, int seconds, double meters, string fidelity)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = from, ToPoiId = to, TravelMode = TravelMode.AnyAir,
            DurationSeconds = seconds, DistanceMeters = meters,
            Fidelity = fidelity, Source = "Mock", ComputedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Legs_PopulateFromCache_WhenRowsExist()
    {
        var factory = Seed(placeable: 2);
        // Roundtrip over P1,P2 ⇒ legs 1→2 and 2→1. Seed both.
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
        vm.TotalTravelTimeSeconds.Should().Be(1200, "Σ of both computed legs");
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
        vm.TotalTravelTimeSeconds.Should().BeNull("any uncomputed leg ⇒ total is em-dash, not false precision");
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
}
