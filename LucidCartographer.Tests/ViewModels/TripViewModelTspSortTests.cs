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
/// TRIP-TSP-01 (Story 3.1): the VM's explicit "Sort in Traveling Salesman order".
/// Proves the system NEVER reorders without the explicit press (AC2 — enabling Trip
/// View seeds but does not sort), and that the press untangles the order, fires
/// StateChanged, and sets the aria-live announcement.
/// </summary>
public class TripViewModelTspSortTests
{
    private const int CollectionId = 1;

    // Seed order is by AddedDate; spatial order (on a meridian) differs, so a sort is
    // observable. poi1=50°, poi2=53°, poi3=51°, poi4=52° added in id order.
    private static IDbContextFactory<AppDbContext> Seed()
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.Drive });
        db.Pois.Add(new Poi { Id = 1, Name = "P1", Latitude = 50.0, Longitude = 20.0, AddedDate = new DateTime(2025, 1, 1) });
        db.Pois.Add(new Poi { Id = 2, Name = "P2", Latitude = 53.0, Longitude = 20.0, AddedDate = new DateTime(2025, 1, 2) });
        db.Pois.Add(new Poi { Id = 3, Name = "P3", Latitude = 51.0, Longitude = 20.0, AddedDate = new DateTime(2025, 1, 3) });
        db.Pois.Add(new Poi { Id = 4, Name = "P4", Latitude = 52.0, Longitude = 20.0, AddedDate = new DateTime(2025, 1, 4) });
        for (var id = 1; id <= 4; id++)
        {
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = id, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task<TripViewModel> EnabledVmAsync(IDbContextFactory<AppDbContext> factory, SqliteWriteLock writeLock)
    {
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(
            ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory, writeLock), NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 4);
        await vm.ToggleAsync(); // seed + enable
        return vm;
    }

    [Fact]
    public async Task EnablingTripView_SeedsByAddedDate_AndDoesNotAutoSort()
    {
        var factory = Seed();
        await using var vm = await EnabledVmAsync(factory, new SqliteWriteLock());

        // Seed order is AddedDate (= id) order, NOT the spatial TSP order — proving
        // nothing auto-sorted on enable (AC2).
        vm.OrderedStops.Select(s => s.PoiId).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task SortTravelingSalesmanAsync_UntanglesOrder_FiresStateChanged_AndAnnounces()
    {
        var factory = Seed();
        await using var vm = await EnabledVmAsync(factory, new SqliteWriteLock());

        var fired = false;
        vm.StateChanged += () => fired = true;

        await vm.SortTravelingSalesmanAsync();

        // Spatial order anchored at the first stop (50°): 50,51,52,53 = poi 1,3,4,2.
        vm.OrderedStops.Select(s => s.PoiId).Should().Equal(1, 3, 4, 2);
        fired.Should().BeTrue("the sort notifies via StateChanged");
        vm.LastSortAnnouncement.Should().NotBeNullOrEmpty("a completed sort is announced on the live region");
    }

    // Already-optimal seed: latitude monotonic in AddedDate order, so the seed order
    // is the spatial order and a sort changes nothing.
    private static IDbContextFactory<AppDbContext> SeedAlreadyOptimal()
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.Drive });
        for (var id = 1; id <= 4; id++)
        {
            db.Pois.Add(new Poi { Id = id, Name = $"P{id}", Latitude = 50.0 + id, Longitude = 20.0, AddedDate = new DateTime(2025, 1, id) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = id, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    [Fact]
    public async Task SortTravelingSalesmanAsync_KeepsSilent_WhenOrderUnchanged()
    {
        var factory = SeedAlreadyOptimal();
        await using var vm = await EnabledVmAsync(factory, new SqliteWriteLock());

        await vm.SortTravelingSalesmanAsync();

        // The never-worse guard keeps the already-optimal order ⇒ no reorder ⇒ the
        // live region stays silent (mirrors the no-op-move silence of MoveStopToAsync).
        vm.OrderedStops.Select(s => s.PoiId).Should().Equal(1, 2, 3, 4);
        vm.LastSortAnnouncement.Should().BeNull("a no-op sort must not announce a reorder that didn't happen");
    }

    [Fact]
    public async Task SortTravelingSalesmanAsync_NoOp_WhenTripViewDisabled()
    {
        var factory = Seed();
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(
            ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory, writeLock), NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 4);
        // NOT enabled.

        await vm.SortTravelingSalesmanAsync();

        vm.LastSortAnnouncement.Should().BeNull("the sort is a no-op when Trip View is off");
        await vm.DisposeAsync();
    }
}
