using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests;

/// <summary>
/// TRIP-MATRIX-01 (Story 3.1, D11) / RD3 (Story 3.3): the on-demand Distance
/// Matrix is now MODE-INVARIANT — it builds the cost matrix from the haversine
/// straight-line distance for every pair, ignoring the collection's TravelMode,
/// the RouteSegment cache, and any per-leg OutgoingTravelMode; it never writes
/// back to the cache and returns null below the two-stop minimum.
/// </summary>
public class DistanceMatrixServiceTests
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed(string mode = TravelMode.Drive)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = mode });
        db.Pois.Add(new Poi { Id = 1, Name = "P1", Latitude = 50.0, Longitude = 20.0, AddedDate = new DateTime(2025, 1, 1) });
        db.Pois.Add(new Poi { Id = 2, Name = "P2", Latitude = 51.0, Longitude = 21.0, AddedDate = new DateTime(2025, 1, 2) });
        db.Pois.Add(new Poi { Id = 3, Name = "P3", Latitude = 52.0, Longitude = 22.0, AddedDate = new DateTime(2025, 1, 3) });
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 1, PoiCollectionId = CollectionId, OrderIndex = 1 });
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 2, PoiCollectionId = CollectionId, OrderIndex = 2 });
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 3, PoiCollectionId = CollectionId, OrderIndex = 3 });
        db.SaveChanges();
        return factory;
    }

    private static DistanceMatrixService Service(IDbContextFactory<AppDbContext> factory) =>
        new(factory, Options.Create(new TravelTimeOptions()));

    // RD3: the matrix is mode-invariant. A cached RouteSegment row (any mode) must
    // NOT be reused for the cost matrix any more — every cell is the haversine
    // straight-line distance. This re-expresses the former
    // "Build_ReusesCachedPair_Directionally" test, whose premise (cache filter by
    // PoiCollection.TravelMode) was removed by Story 3.3.
    [Fact]
    public async Task Build_IgnoresCachedRouteSegments_UsesHaversineDistance()
    {
        var factory = Seed();
        await using (var db = await factory.CreateDbContextAsync())
        {
            // A cached 1->2 row with a deliberately distinctive duration the matrix
            // would have reused under the old cache-filtering behavior.
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive,
                DurationSeconds = 12345, DistanceMeters = 1000, Fidelity = Fidelity.Estimated,
                Source = "Mock", ComputedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var matrix = await Service(factory).BuildAsync(CollectionId);

        matrix.Should().NotBeNull();
        matrix!.Stops.Should().HaveCount(3);
        // Stop indices follow OrderIndex: index 0=Poi1, 1=Poi2, 2=Poi3.
        matrix.DurationSeconds[0][1].Should().NotBe(12345,
            "the matrix is mode-invariant — it no longer reuses cached durations");
        matrix.FromCache.SelectMany(row => row).Should().AllBeEquivalentTo(false,
            "no cell is fed from the cache; all are computed haversine distances");
        // Haversine is symmetric: A->B and B->A distances are equal.
        matrix.DurationSeconds[0][1].Should().BeApproximately(matrix.DurationSeconds[1][0], 1e-6);
        matrix.DurationSeconds[0][0].Should().Be(0, "the diagonal is never routed");
    }

    [Fact]
    public async Task Build_FillsEveryPair_WithHaversineDistance_AndDoesNotWriteCache()
    {
        var factory = Seed();

        var matrix = await Service(factory).BuildAsync(CollectionId);

        matrix.Should().NotBeNull();
        // Every off-diagonal cell is the (positive) haversine distance; nothing cached.
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                if (i != j)
                {
                    matrix!.DurationSeconds[i][j].Should().BeGreaterThan(0);
                    matrix.FromCache[i][j].Should().BeFalse();
                }
            }
        }

        // The matrix is input-only: no rows leaked into the cache.
        await using var verify = await factory.CreateDbContextAsync();
        (await verify.RouteSegments.CountAsync()).Should().Be(0, "the matrix never writes the cache");
    }

    // RD3 (Story 3.3, AC2): building the matrix for collections that differ ONLY in
    // their persisted TravelMode (and per-leg OutgoingTravelMode) yields the SAME
    // cost matrix — the matrix ignores all modes.
    [Theory]
    [InlineData(TravelMode.AnyAir)]
    [InlineData(TravelMode.Drive)]
    [InlineData(TravelMode.Walk)]
    [InlineData(TravelMode.Cycle)]
    public async Task Build_IsModeInvariant_AcrossTripAndPerLegModes(string mode)
    {
        var factory = Seed(mode);
        // Stamp a per-leg mode on each Stop too — it must not influence the matrix.
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var ci in db.PoiCollectionItems)
            {
                ci.OutgoingTravelMode = mode;
            }
            await db.SaveChangesAsync();
        }

        var matrix = await Service(factory).BuildAsync(CollectionId);

        // Reference: a plain Drive collection with no per-leg modes set.
        var reference = await Service(Seed(TravelMode.Drive)).BuildAsync(CollectionId);

        matrix.Should().NotBeNull();
        reference.Should().NotBeNull();
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                matrix!.DurationSeconds[i][j].Should().BeApproximately(
                    reference!.DurationSeconds[i][j], 1e-6,
                    "the cost matrix is identical regardless of trip/per-leg travel mode");
            }
        }
    }

    [Fact]
    public async Task Build_ReturnsNull_WhenFewerThanTwoPlaceableStops()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.Drive });
            db.Pois.Add(new Poi { Id = 1, Name = "P1", Latitude = 50.0, Longitude = 20.0, AddedDate = new DateTime(2025, 1, 1) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 1, PoiCollectionId = CollectionId, OrderIndex = 1 });
            await db.SaveChangesAsync();
        }

        (await Service(factory).BuildAsync(CollectionId)).Should().BeNull();
    }
}
