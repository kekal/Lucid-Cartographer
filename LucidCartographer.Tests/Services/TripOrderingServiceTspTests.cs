using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Tests;

/// <summary>
/// TRIP-TSP-01 (Story 3.1): the TSP-Sort path on the single OrderIndex writer.
/// End-to-end through TripOrderingService + DistanceMatrixService over an in-memory
/// DB: untangling a zig-zag, honouring Start/Finish pins, the never-worse guarantee,
/// the single contiguous 1..N write path, and the below-minimum no-op.
/// Stops are placed on a single meridian (lon constant) so haversine distance is
/// monotonic in latitude and the optimal order is the spatial order.
/// </summary>
public class TripOrderingServiceTspTests
{
    private const int CollectionId = 1;

    // Members: (poiId, latitude, orderIndex). Longitude fixed; placeable.
    private static IDbContextFactory<AppDbContext> Seed(
        string mode, params (int PoiId, double Lat, int Order)[] members)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = mode });
        foreach (var m in members)
        {
            db.Pois.Add(new Poi { Id = m.PoiId, Name = $"P{m.PoiId}", Latitude = m.Lat, Longitude = 20.0, AddedDate = new DateTime(2025, 1, m.PoiId) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = m.PoiId, PoiCollectionId = CollectionId, OrderIndex = m.Order });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task<Dictionary<int, int>> ReadOrderAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == CollectionId)
            .ToDictionaryAsync(ci => ci.PoiId, ci => ci.OrderIndex);
    }

    [Fact]
    public async Task Sort_UntanglesZigZag_IntoSpatialOrder()
    {
        // Latitudes 50,51,52,53 presented in zig-zag Stop Order: A,C,B,D.
        var factory = Seed(TravelMode.Drive,
            (PoiId: 1, Lat: 50.0, Order: 1),   // A
            (PoiId: 3, Lat: 52.0, Order: 2),   // C
            (PoiId: 2, Lat: 51.0, Order: 3),   // B
            (PoiId: 4, Lat: 53.0, Order: 4));  // D
        var service = TestDbHelper.CreateOrderingService(factory);

        await service.SortTravelingSalesmanAsync(CollectionId);

        var order = await ReadOrderAsync(factory);
        // Anchored at the first Stop (A); untangled to A,B,C,D.
        order[1].Should().Be(1, "A stays the anchor");
        order[2].Should().Be(2, "B (lat 51) now follows A");
        order[3].Should().Be(3, "C (lat 52) follows B");
        order[4].Should().Be(4, "D (lat 53) is last");
    }

    [Fact]
    public async Task Sort_HonorsStartAndFinishPins()
    {
        var factory = Seed(TravelMode.Drive,
            (PoiId: 1, Lat: 50.0, Order: 1),
            (PoiId: 2, Lat: 51.0, Order: 2),
            (PoiId: 3, Lat: 52.0, Order: 3),
            (PoiId: 4, Lat: 53.0, Order: 4));
        var service = TestDbHelper.CreateOrderingService(factory);

        // Pin D (poi 4) as Start and A (poi 1) as Finish — spatially the worst
        // endpoints, so only the pins keep them at the ends.
        await service.SetStartAsync(CollectionId, 4);
        await service.SetFinishAsync(CollectionId, 1);

        await service.SortTravelingSalesmanAsync(CollectionId);

        var order = await ReadOrderAsync(factory);
        order[4].Should().Be(1, "the pinned Start stays at Order 1");
        order[1].Should().Be(4, "the pinned Finish stays at Order N");
        // Interior stays contiguous between the pins.
        new[] { order[2], order[3] }.Should().BeEquivalentTo(new[] { 2, 3 });
    }

    [Fact]
    public async Task Sort_NeverWorse_KeepsAlreadyOptimalOrder()
    {
        var factory = Seed(TravelMode.Drive,
            (PoiId: 1, Lat: 50.0, Order: 1),
            (PoiId: 2, Lat: 51.0, Order: 2),
            (PoiId: 3, Lat: 52.0, Order: 3),
            (PoiId: 4, Lat: 53.0, Order: 4));
        var service = TestDbHelper.CreateOrderingService(factory);

        await service.SortTravelingSalesmanAsync(CollectionId);

        var order = await ReadOrderAsync(factory);
        order[1].Should().Be(1);
        order[2].Should().Be(2);
        order[3].Should().Be(3);
        order[4].Should().Be(4);
    }

    [Fact]
    public async Task Sort_ProducesContiguousUniqueOneBasedOrder()
    {
        var factory = Seed(TravelMode.AnyAir,
            (PoiId: 10, Lat: 50.0, Order: 1),
            (PoiId: 11, Lat: 55.0, Order: 2),
            (PoiId: 12, Lat: 51.0, Order: 3),
            (PoiId: 13, Lat: 54.0, Order: 4),
            (PoiId: 14, Lat: 52.0, Order: 5));
        var service = TestDbHelper.CreateOrderingService(factory);

        await service.SortTravelingSalesmanAsync(CollectionId);

        var order = await ReadOrderAsync(factory);
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 },
            "the sort writes a contiguous, gap-free, unique 1..N order through the single writer");
    }

    [Fact]
    public async Task Sort_NoOp_WhenFewerThanTwoPlaceableStops()
    {
        var factory = Seed(TravelMode.Drive, (PoiId: 1, Lat: 50.0, Order: 1));
        var service = TestDbHelper.CreateOrderingService(factory);

        await service.SortTravelingSalesmanAsync(CollectionId);

        (await ReadOrderAsync(factory))[1].Should().Be(1, "a single-stop trip is left untouched");
    }
}
