using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Mcp;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Tests;

/// <summary>
/// TRIP-MCP-01 (Story 3.2): the MCP TripTools. Each tool is a static method that
/// delegates to ITripOrderingService — driven here with services over an in-memory
/// DB (the same way the service tests run). Covers read, full-order assignment,
/// Start/Finish designation (incl. the Start==Finish error), and dwell.
/// </summary>
public class McpTripToolsTests
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed()
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.Drive });
        for (var id = 1; id <= 3; id++)
        {
            db.Pois.Add(new Poi { Id = id, Name = $"P{id}", Latitude = 50.0 + id, Longitude = 20.0, AddedDate = new DateTime(2025, 1, id) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = id, PoiCollectionId = CollectionId, OrderIndex = id });
        }
        // One cached leg so get_trip surfaces a computed duration.
        db.RouteSegments.Add(new RouteSegment { FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive, DurationSeconds = 600, DistanceMeters = 5000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
        db.SaveChanges();
        return factory;
    }

    [Fact]
    public async Task GetTrip_ReturnsOrderedStops_AndLegs()
    {
        var factory = Seed();
        var ordering = TestDbHelper.CreateOrderingService(factory);

        var trip = await TripTools.GetTrip(ordering, factory, CollectionId);

        trip.TravelMode.Should().Be(TravelMode.Drive);
        trip.Stops.Select(s => s.PoiId).Should().Equal(1, 2, 3);
        trip.Stops.Select(s => s.OrderIndex).Should().Equal(1, 2, 3);
        // Roundtrip (no Finish) ⇒ 3 legs incl. the closing 3→1.
        trip.Legs.Should().HaveCount(3);
        trip.Legs.Should().Contain(l => l.FromPoiId == 1 && l.ToPoiId == 2 && l.DurationSeconds == 600);
        trip.Legs.Should().Contain(l => l.FromPoiId == 3 && l.ToPoiId == 1 && l.DurationSeconds == null);
    }

    [Fact]
    public async Task AssignStopOrder_ReordersViaTheService_AndReadsBack()
    {
        var factory = Seed();
        var ordering = TestDbHelper.CreateOrderingService(factory);

        var trip = await TripTools.AssignStopOrder(ordering, factory, CollectionId, new[] { 3, 1, 2 });

        trip.Stops.Select(s => s.PoiId).Should().Equal(3, 1, 2);
        trip.Stops.Select(s => s.OrderIndex).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task SetTripStartAndFinish_PinTheEnds()
    {
        var factory = Seed();
        var ordering = TestDbHelper.CreateOrderingService(factory);

        await TripTools.SetTripStart(ordering, factory, CollectionId, poiId: 3);
        var trip = await TripTools.SetTripFinish(ordering, factory, CollectionId, poiId: 1);

        trip.Stops.Single(s => s.PoiId == 3).IsStart.Should().BeTrue();
        trip.Stops.Single(s => s.PoiId == 3).OrderIndex.Should().Be(1);
        trip.Stops.Single(s => s.PoiId == 1).IsFinish.Should().BeTrue();
        trip.Stops.Single(s => s.PoiId == 1).OrderIndex.Should().Be(3);
        // A distinct Finish ⇒ open path: no closing leg back to the first stop.
        trip.Legs.Should().HaveCount(2);
    }

    [Fact]
    public async Task SetTripFinish_OnTheStart_Errors()
    {
        var factory = Seed();
        var ordering = TestDbHelper.CreateOrderingService(factory);
        await TripTools.SetTripStart(ordering, factory, CollectionId, poiId: 2);

        var act = () => TripTools.SetTripFinish(ordering, factory, CollectionId, poiId: 2);

        await act.Should().ThrowAsync<InvalidOperationException>("a stop cannot be both Start and Finish");
    }

    [Fact]
    public async Task SetDwellTime_SetsAndClears()
    {
        var factory = Seed();
        var ordering = TestDbHelper.CreateOrderingService(factory);

        var trip = await TripTools.SetDwellTime(ordering, factory, CollectionId, poiId: 2, minutes: 30);
        trip.Stops.Single(s => s.PoiId == 2).DwellMinutes.Should().Be(30);

        trip = await TripTools.SetDwellTime(ordering, factory, CollectionId, poiId: 2, minutes: null);
        trip.Stops.Single(s => s.PoiId == 2).DwellMinutes.Should().BeNull();
    }
}
