using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 2.4 (TRIP-INVALIDATE-01, AC2): editing a POI's coordinates via PoiService
/// invalidates that POI's non-Manual cached legs; an unchanged save does not, and a
/// Manual leg is never invalidated.
/// </summary>
public class PoiServiceCoordInvalidationTests
{
    private static (PoiService Service, IDbContextFactory<AppDbContext> Factory) Build()
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.Pois.Add(new Poi { Id = 1, Name = "P1", Latitude = 50.0, Longitude = 20.0, AddedDate = new DateTime(2025, 1, 1) });
        db.Pois.Add(new Poi { Id = 2, Name = "P2", Latitude = 51.0, Longitude = 21.0, AddedDate = new DateTime(2025, 1, 2) });
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive,
            DurationSeconds = 600, DistanceMeters = 8000, Fidelity = Fidelity.Estimated,
            Source = "Mock", ComputedAt = DateTime.UtcNow,
        });
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = 2, ToPoiId = 1, TravelMode = TravelMode.AnyAir,
            DurationSeconds = 700, DistanceMeters = 9000, Fidelity = Fidelity.Manual,
            Source = "Manual", ComputedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var service = new PoiService(
            factory, TestDbHelper.CreateInvalidationService(factory),
            NullLoggerFactory.Instance.CreateLogger<PoiService>());
        return (service, factory);
    }

    private static async Task<Poi> LoadAsync(IDbContextFactory<AppDbContext> factory, int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Pois.FirstAsync(p => p.Id == id);
    }

    [Fact]
    public async Task UpdatePoi_CoordsChanged_InvalidatesNonManualSegments_KeepsManual()
    {
        var (service, factory) = Build();

        var poi = await LoadAsync(factory, 1);
        poi.Latitude = 55.5; // real change
        await service.UpdatePoiAsync(poi);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.RouteSegments.AsNoTracking().ToListAsync();
        // The Estimated leg touching POI 1 is gone; the Manual leg (also touches 1) survives.
        rows.Should().NotContain(r => r.Fidelity == Fidelity.Estimated);
        rows.Should().ContainSingle(r => r.Fidelity == Fidelity.Manual);
    }

    [Fact]
    public async Task UpdatePoi_NoCoordChange_DoesNotInvalidate()
    {
        var (service, factory) = Build();

        var poi = await LoadAsync(factory, 1);
        poi.Name = "Renamed"; // coords untouched
        await service.UpdatePoiAsync(poi);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.RouteSegments.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2, "an unchanged-coords save must not churn the cache");
        rows.Should().Contain(r => r.Fidelity == Fidelity.Estimated);
    }
}
