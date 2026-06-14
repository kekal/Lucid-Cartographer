using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests;

/// <summary>
/// TRIP-MATRIX-01 (Story 3.1, D11): the on-demand Distance Matrix reuses cached
/// directional pairs from the shared RouteSegment cache, fills missing pairs with
/// the haversine estimate, never writes back to the cache, and returns null below
/// the two-stop minimum.
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

    [Fact]
    public async Task Build_ReusesCachedPair_Directionally()
    {
        var factory = Seed();
        await using (var db = await factory.CreateDbContextAsync())
        {
            // Only the 1->2 direction is cached, with a deliberately distinctive value.
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
        matrix.DurationSeconds[0][1].Should().Be(12345, "the 1->2 cached value is reused");
        matrix.FromCache[0][1].Should().BeTrue();
        // The reverse direction was NOT cached ⇒ filled by estimate, not 12345.
        matrix.FromCache[1][0].Should().BeFalse();
        matrix.DurationSeconds[1][0].Should().NotBe(12345);
        matrix.DurationSeconds[0][0].Should().Be(0, "the diagonal is never routed");
    }

    [Fact]
    public async Task Build_FillsMissingPairs_WithHaversineEstimate_AndDoesNotWriteCache()
    {
        var factory = Seed();

        var matrix = await Service(factory).BuildAsync(CollectionId);

        matrix.Should().NotBeNull();
        // Every off-diagonal cell is populated (positive) even with an empty cache.
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

        // The matrix is input-only: no estimated rows leaked into the cache.
        await using var verify = await factory.CreateDbContextAsync();
        (await verify.RouteSegments.CountAsync()).Should().Be(0, "the matrix never writes the cache");
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
