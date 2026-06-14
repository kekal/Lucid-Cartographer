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
/// Story 2.5 (TRIP-DWELL-01, AC 1-5): the ViewModel persists a per-membership dwell
/// time (minutes) on <see cref="PoiCollectionItem.DwellMinutes"/>, clears it to null,
/// round-trips it into <see cref="TripStopRow.DwellMinutes"/>, keeps the same POI's
/// dwell independent across collections, rejects out-of-range values, works on an
/// unplaceable stop, and — being independent of travel times — neither signals the
/// recompute trigger nor touches any <see cref="RouteSegment"/>.
/// </summary>
public class TripViewModelDwellTests
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed(int placeable, int unplaceable = 0)
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
        for (var j = 1; j <= unplaceable; j++)
        {
            var id = 1000 + j;
            // No coordinates ⇒ unplaceable (StopPlaceability), but still a member.
            db.Pois.Add(new Poi { Id = id, Name = $"U{j}", Latitude = null, Longitude = null, AddedDate = new DateTime(2025, 2, j) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = id, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task<(TripViewModel Vm, TravelTimeTrigger Trigger, IDbContextFactory<AppDbContext> Factory)> EnabledVmAsync(
        IDbContextFactory<AppDbContext> factory, int placeable)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        var trigger = new TravelTimeTrigger();
        var vm = new TripViewModel(
            ordering, factory, writeLock,
            trigger, new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return (vm, trigger, factory);
    }

    private static async Task<int?> ReadDwellAsync(IDbContextFactory<AppDbContext> factory, int collectionId, int poiId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == collectionId && ci.PoiId == poiId)
            .Select(ci => ci.DwellMinutes)
            .FirstAsync();
    }

    [Fact]
    public async Task SetDwellMinutes_PersistsOnTheCorrectMembershipOnly_AndRoundTrips()
    {
        var factory = Seed(placeable: 2);
        var (vm, _, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetDwellMinutesAsync(poiId: 1, minutes: 30);

        (await ReadDwellAsync(factory, CollectionId, 1)).Should().Be(30, "the dwell persists on POI 1's membership");
        (await ReadDwellAsync(factory, CollectionId, 2)).Should().BeNull("only POI 1 was set; POI 2 is untouched");

        var row = vm.StopRows.First(r => r.PoiId == 1);
        row.DwellMinutes.Should().Be(30, "the value round-trips into the projection after refresh");
    }

    [Fact]
    public async Task SetDwellMinutes_Null_ClearsTheValue()
    {
        var factory = Seed(placeable: 2);
        var (vm, _, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetDwellMinutesAsync(poiId: 1, minutes: 45);
        (await ReadDwellAsync(factory, CollectionId, 1)).Should().Be(45);

        await vm.SetDwellMinutesAsync(poiId: 1, minutes: null);

        (await ReadDwellAsync(factory, CollectionId, 1)).Should().BeNull("null clears the dwell");
        vm.StopRows.First(r => r.PoiId == 1).DwellMinutes.Should().BeNull();
    }

    [Fact]
    public async Task SetDwellMinutes_LargeValue_IsStoredVerbatim()
    {
        // AC3: overnight is just a large dwell — no special handling.
        var factory = Seed(placeable: 2);
        var (vm, _, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetDwellMinutesAsync(poiId: 1, minutes: 600);

        (await ReadDwellAsync(factory, CollectionId, 1)).Should().Be(600);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(TripViewModel.MaxDwellMinutes + 1)]
    public async Task SetDwellMinutes_OutOfRange_IsRejected_NoWrite(int minutes)
    {
        var factory = Seed(placeable: 2);
        var (vm, _, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetDwellMinutesAsync(poiId: 1, minutes: minutes);

        (await ReadDwellAsync(factory, CollectionId, 1)).Should().BeNull("out-of-range input is rejected; no write");
    }

    [Fact]
    public async Task SetDwellMinutes_AtMax_IsAccepted()
    {
        var factory = Seed(placeable: 2);
        var (vm, _, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetDwellMinutesAsync(poiId: 1, minutes: TripViewModel.MaxDwellMinutes);

        (await ReadDwellAsync(factory, CollectionId, 1)).Should().Be(TripViewModel.MaxDwellMinutes);
    }

    [Fact]
    public async Task SetDwellMinutes_OnUnplaceableStop_RoundTrips()
    {
        // AC4: dwell is available on any stop, including an unplaceable one.
        var factory = Seed(placeable: 2, unplaceable: 1);
        var (vm, _, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        const int unplaceablePoiId = 1001;
        await vm.SetDwellMinutesAsync(unplaceablePoiId, minutes: 120);

        (await ReadDwellAsync(factory, CollectionId, unplaceablePoiId)).Should().Be(120);
        var row = vm.StopRows.First(r => r.PoiId == unplaceablePoiId);
        row.IsPlaceable.Should().BeFalse("the seeded U-stop has no coordinates");
        row.DwellMinutes.Should().Be(120, "an unplaceable stop carries dwell identically");
    }

    private static async Task AddSegmentAsync(IDbContextFactory<AppDbContext> factory, int from, int to)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = from, ToPoiId = to, TravelMode = TravelMode.AnyAir,
            DurationSeconds = 600, DistanceMeters = 8000,
            Fidelity = Fidelity.Manual, Source = "Manual", ComputedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SetDwellMinutes_DoesNotSignalTrigger_NorTouchRouteSegments()
    {
        // AC5 + Dev Notes: dwell is independent of travel times. Seed both roundtrip
        // legs as fully computed (Manual) so RefreshProjectionsAsync has no
        // "computing" leg to signal on — isolating that the dwell write itself never
        // signals the recompute trigger.
        var factory = Seed(placeable: 2);
        await AddSegmentAsync(factory, 1, 2);
        await AddSegmentAsync(factory, 2, 1);
        var (vm, trigger, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;
        // Drain any enable-time signal so the post-write assertion is clean.
        await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);

        var beforeRows = await ReadSegmentCountAsync(factory);

        await vm.SetDwellMinutesAsync(poiId: 1, minutes: 30);

        var signalled = await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        signalled.Should().BeFalse("setting dwell must NOT signal the travel-time recompute");

        var afterRows = await ReadSegmentCountAsync(factory);
        afterRows.Should().Be(beforeRows, "no RouteSegment row is created/changed/removed by a dwell write");
    }

    [Fact]
    public async Task SetDwellMinutes_WithUncomputedLegs_TouchesNoRouteSegments()
    {
        // AC5 real invariant: a dwell write never invalidates or recomputes a cached
        // leg, regardless of compute state. Here NO segments are seeded (both legs are
        // "computing"); editing dwell must still create/change/remove zero RouteSegment
        // rows. (RefreshProjectionsAsync may wake the compute loop because a leg is
        // genuinely uncomputed — the pre-existing IsAnyLegComputing behavior — but that
        // is not a dwell-driven recompute and writes no segment in this unit context.)
        var factory = Seed(placeable: 2);
        var (vm, _, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        (await ReadSegmentCountAsync(factory)).Should().Be(0, "no segments seeded");

        await vm.SetDwellMinutesAsync(poiId: 1, minutes: 45);

        (await ReadSegmentCountAsync(factory)).Should().Be(0,
            "a dwell write never creates/invalidates/recomputes a RouteSegment, even with uncomputed legs");
        vm.StopRows.First(r => r.PoiId == 1).DwellMinutes.Should().Be(45, "the dwell value still persists + round-trips");
    }

    private static async Task<int> ReadSegmentCountAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.RouteSegments.CountAsync();
    }

    [Fact]
    public async Task SetDwellMinutes_SamePoiInTwoCollections_IsIndependent()
    {
        // AC1: dwell lives on the membership, so the same POI carries different dwell
        // per trip. Seed a second collection sharing POI 1, then set each separately.
        var factory = Seed(placeable: 2);
        const int otherCollectionId = 2;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PoiCollections.Add(new PoiCollection
            {
                Id = otherCollectionId, Name = "Trip2", Color = "#005bbf", TravelMode = TravelMode.AnyAir,
            });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 1, PoiCollectionId = otherCollectionId });
            await db.SaveChangesAsync();
        }

        var (vm, _, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetDwellMinutesAsync(poiId: 1, minutes: 30);

        (await ReadDwellAsync(factory, CollectionId, 1)).Should().Be(30, "the active collection's membership is set");
        (await ReadDwellAsync(factory, otherCollectionId, 1)).Should().BeNull("the same POI in another collection is independent");
    }
}
