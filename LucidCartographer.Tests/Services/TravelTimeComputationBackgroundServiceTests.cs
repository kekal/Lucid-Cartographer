using FluentAssertions;
using LucidCartographer.Configuration;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.Registry;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 2.1 (AC 6, 7): the background service upserts a RouteSegment row per
/// directional leg with the correct key/fields, writing under the shared write
/// lock, and is idempotent on an unchanged pair (no duplicate rows).
/// </summary>
public class TravelTimeComputationBackgroundServiceTests
{
    private const int CollectionId = 1;

    private static ResiliencePipelineProvider<string> Pipelines()
    {
        var services = new ServiceCollection();
        services.AddAppResiliencePipelines();
        return services.BuildServiceProvider().GetRequiredService<ResiliencePipelineProvider<string>>();
    }

    private static TravelTimeComputationBackgroundService BuildService(
        IDbContextFactory<AppDbContext> factory, SqliteWriteLock writeLock)
    {
        var options = Options.Create(new TravelTimeOptions { AssumedSpeedMetersPerSecond = 13.8889 });
        var provider = new MockTravelTimeProvider(options);
        return new TravelTimeComputationBackgroundService(
            factory,
            new TravelTimeTrigger(),
            new TravelTimeProgressService(),
            provider,
            writeLock,
            Pipelines(),
            options,
            NullLogger<TravelTimeComputationBackgroundService>.Instance);
    }

    private static IDbContextFactory<AppDbContext> SeedTwoStopRoundtrip()
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf",
            TripViewEnabled = true, TravelMode = TravelMode.AnyAir,
        });
        db.Pois.Add(new Poi { Id = 1, Name = "P1", Latitude = 50.0, Longitude = 20.0, AddedDate = new DateTime(2025, 1, 1) });
        db.Pois.Add(new Poi { Id = 2, Name = "P2", Latitude = 51.0, Longitude = 21.0, AddedDate = new DateTime(2025, 1, 2) });
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 1, PoiCollectionId = CollectionId, OrderIndex = 1 });
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 2, PoiCollectionId = CollectionId, OrderIndex = 2 });
        db.SaveChanges();
        return factory;
    }

    [Fact]
    public async Task ProcessOnce_UpsertsRouteSegments_WithCorrectKeyAndFields()
    {
        var factory = SeedTwoStopRoundtrip();
        var service = BuildService(factory, new SqliteWriteLock());

        await service.ProcessOnceAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.RouteSegments.ToListAsync();

        // Roundtrip over 2 stops ⇒ 1→2 and the closing 2→1 (directional, distinct).
        rows.Should().HaveCount(2);
        rows.Should().Contain(r => r.FromPoiId == 1 && r.ToPoiId == 2);
        rows.Should().Contain(r => r.FromPoiId == 2 && r.ToPoiId == 1);

        var leg = rows.First(r => r.FromPoiId == 1 && r.ToPoiId == 2);
        leg.TravelMode.Should().Be(TravelMode.AnyAir);
        // Story 2.2 (TRIP-TRAVELMODE-01): Any/Air legs from the Mock are Placeholder.
        leg.Fidelity.Should().Be(Fidelity.Placeholder);
        leg.Source.Should().Be("Mock");
        leg.GeometryPolyline.Should().BeNull();
        leg.DistanceMeters.Should().BeGreaterThan(0);
        leg.DurationSeconds.Should().BeGreaterThan(0);
        leg.ComputedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task ProcessOnce_IsIdempotent_NoDuplicateRowsOnRerun()
    {
        var factory = SeedTwoStopRoundtrip();
        var service = BuildService(factory, new SqliteWriteLock());

        await service.ProcessOnceAsync(CancellationToken.None);
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.RouteSegments.ToListAsync();
        rows.Should().HaveCount(2, "a re-run must not duplicate cache rows for unchanged pairs");
    }

    [Fact]
    public async Task ProcessOnce_SkipsCollectionsWithTripViewDisabled()
    {
        var factory = SeedTwoStopRoundtrip();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var c = await db.PoiCollections.FirstAsync(x => x.Id == CollectionId);
            c.TripViewEnabled = false;
            await db.SaveChangesAsync();
        }

        var service = BuildService(factory, new SqliteWriteLock());
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        (await verify.RouteSegments.CountAsync()).Should().Be(0);
    }

    // Story 2.2 (AC6, TRIP-MANUAL-01): a user's Manual row is never recomputed or
    // overwritten by a compute pass — its duration/fidelity/source stay intact.
    [Fact]
    public async Task ProcessOnce_DoesNotOverwrite_ManualRow()
    {
        var factory = SeedTwoStopRoundtrip();
        // Seed a Manual row for the 1→2 Any/Air leg the user entered (e.g. a flight).
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.AnyAir,
                DurationSeconds = 5400, DistanceMeters = 123456,
                Fidelity = Fidelity.Manual, Source = "Manual", ComputedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var service = BuildService(factory, new SqliteWriteLock());
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var manual = await verify.RouteSegments.FirstAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2);
        manual.Fidelity.Should().Be(Fidelity.Manual, "the manual entry is protected from recompute");
        manual.Source.Should().Be("Manual");
        manual.DurationSeconds.Should().Be(5400, "the user's flight time is untouched");
        manual.DistanceMeters.Should().Be(123456);
    }
}
