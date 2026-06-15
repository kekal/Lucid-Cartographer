using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Mcp;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Tests;

/// <summary>
/// TRIP-MCP-01 (Story 3.2) / TRIP-LEGMODE-01 (Story 3.6): the MCP TripTools. Each
/// tool is a static method that delegates to ITripOrderingService — driven here with
/// services over an in-memory DB (the same way the service tests run). Covers read
/// (now per-leg travelMode), full-order assignment, Start/Finish designation (incl.
/// the Start==Finish error), dwell, and the new per-leg travel mode tool.
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
            // TRIP-LEGMODE-01 (Story 3.6): the leg leaving stop 1 is a Drive leg (per-leg
            // mode = the From-stop's OutgoingTravelMode); the rest are null ≡ AnyAir.
            db.PoiCollectionItems.Add(new PoiCollectionItem
            {
                PoiId = id,
                PoiCollectionId = CollectionId,
                OrderIndex = id,
                OutgoingTravelMode = id == 1 ? TravelMode.Drive : null,
            });
        }
        // One cached leg keyed at the per-leg mode (1→2 under Drive) so get_trip
        // surfaces a computed duration for that leg's own mode.
        db.RouteSegments.Add(new RouteSegment { FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive, DurationSeconds = 600, DistanceMeters = 5000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
        db.SaveChanges();
        return factory;
    }

    [Fact]
    public async Task GetTrip_ReturnsOrderedStops_AndPerLegModeLegs()
    {
        var factory = Seed();
        var ordering = TestDbHelper.CreateOrderingService(factory);

        var trip = await TripTools.GetTrip(ordering, factory, CollectionId);

        trip.Stops.Select(s => s.PoiId).Should().Equal(1, 2, 3);
        trip.Stops.Select(s => s.OrderIndex).Should().Equal(1, 2, 3);
        // Roundtrip (no Finish) ⇒ 3 legs incl. the closing 3→1.
        trip.Legs.Should().HaveCount(3);
        // The 1→2 leg is a Drive leg and reports the Drive cache row.
        var driveLeg = trip.Legs.Single(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        driveLeg.TravelMode.Should().Be(TravelMode.Drive);
        driveLeg.DurationSeconds.Should().Be(600);
        driveLeg.DistanceMeters.Should().Be(5000);
        // The other legs are Any/Air (From-stop OutgoingTravelMode null) ⇒ no cache row.
        var anyLeg = trip.Legs.Single(l => l.FromPoiId == 2 && l.ToPoiId == 3);
        anyLeg.TravelMode.Should().Be(TravelMode.AnyAir);
        anyLeg.DurationSeconds.Should().BeNull();
        trip.Legs.Single(l => l.FromPoiId == 3 && l.ToPoiId == 1).TravelMode.Should().Be(TravelMode.AnyAir);
    }

    [Fact]
    public async Task GetTrip_LegSelectsCacheRowByItsOwnMode_NotTripWide()
    {
        // Two cache rows for 1→2 under DIFFERENT modes; the leg must pick the one
        // matching its own per-leg mode (Drive), not any other mode's row.
        var factory = Seed();
        using (var db = factory.CreateDbContext())
        {
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Walk, DurationSeconds = 9999, DistanceMeters = 1234, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
        var ordering = TestDbHelper.CreateOrderingService(factory);

        var trip = await TripTools.GetTrip(ordering, factory, CollectionId);

        var driveLeg = trip.Legs.Single(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        driveLeg.TravelMode.Should().Be(TravelMode.Drive);
        driveLeg.DurationSeconds.Should().Be(600, "the leg selects its own Drive row, not the Walk row");
    }

    [Fact]
    public async Task AssignStopOrder_ReordersViaTheService_AndReadsBack()
    {
        var factory = Seed();
        var ordering = TestDbHelper.CreateOrderingService(factory);

        var trip = await TripTools.AssignStopOrder(ordering, factory, CollectionId, new[] { 3, 1, 2 });

        trip.Stops.Select(s => s.PoiId).Should().Equal(3, 1, 2);
        trip.Stops.Select(s => s.OrderIndex).Should().Equal(1, 2, 3);
        // The per-leg modes still round-trip: stop 1 keeps its Drive outgoing mode,
        // so the (now interior) 1→2 leg is still a Drive leg.
        trip.Legs.Single(l => l.FromPoiId == 1 && l.ToPoiId == 2).TravelMode.Should().Be(TravelMode.Drive);
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

    [Fact]
    public async Task SetLegTravelMode_Ground_WritesFromStopMode_AndSignals()
    {
        var factory = Seed();
        var ordering = TestDbHelper.CreateOrderingService(factory);
        var trigger = new TravelTimeTrigger();

        // Stop 2's outgoing leg starts as Any/Air (null); set it to Drive.
        var trip = await TripTools.SetLegTravelMode(ordering, factory, trigger, CollectionId, fromPoiId: 2, travelMode: TravelMode.Drive);

        // The returned DTO's 2→3 leg reflects the new mode.
        trip.Legs.Single(l => l.FromPoiId == 2 && l.ToPoiId == 3).TravelMode.Should().Be(TravelMode.Drive);
        // Verified persisted via a fresh read of the membership (sole-writer).
        using (var db = factory.CreateDbContext())
        {
            (await db.PoiCollectionItems.SingleAsync(ci => ci.PoiCollectionId == CollectionId && ci.PoiId == 2))
                .OutgoingTravelMode.Should().Be(TravelMode.Drive);
        }
        // A ground mode signals the background compute (FR-21) — the trigger is now armed.
        (await trigger.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None))
            .Should().BeTrue("a ground mode signals the travel-time compute");
    }

    [Fact]
    public async Task SetLegTravelMode_AnyAir_SetsAnyAir_AndDoesNotSignal()
    {
        var factory = Seed();
        var ordering = TestDbHelper.CreateOrderingService(factory);
        var trigger = new TravelTimeTrigger();

        // Stop 1 starts Drive; set it back to Any/Air (manual-only).
        var trip = await TripTools.SetLegTravelMode(ordering, factory, trigger, CollectionId, fromPoiId: 1, travelMode: TravelMode.AnyAir);

        trip.Legs.Single(l => l.FromPoiId == 1 && l.ToPoiId == 2).TravelMode.Should().Be(TravelMode.AnyAir);
        // Any/Air is manual-only ⇒ no signal: the trigger never fires (times out).
        (await trigger.WaitAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None))
            .Should().BeFalse("Any/Air leaves the leg manual-only — no compute signal");
    }

    [Fact]
    public async Task SetLegTravelMode_InvalidMode_Throws()
    {
        var factory = Seed();
        var ordering = TestDbHelper.CreateOrderingService(factory);
        var trigger = new TravelTimeTrigger();

        var act = () => TripTools.SetLegTravelMode(ordering, factory, trigger, CollectionId, fromPoiId: 2, travelMode: "Teleport");

        await act.Should().ThrowAsync<ArgumentException>("the sole-writer validates the mode");
    }
}
