using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 2.4 (TRIP-INVALIDATE-01, AC2/AC4): the cache-invalidation service deletes
/// the stale rows on a coordinate change (by-POI, both directions, all modes, never
/// Manual) and on an explicit recompute (eligible = NOT Manual AND NOT Measured),
/// returning the count.
/// </summary>
public class RouteSegmentInvalidationTests
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed()
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.AnyAir,
        });
        for (var i = 1; i <= 3; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static RouteSegment Row(int from, int to, string fidelity, string mode = TravelMode.AnyAir) =>
        new()
        {
            FromPoiId = from, ToPoiId = to, TravelMode = mode,
            DurationSeconds = 600, DistanceMeters = 8000,
            Fidelity = fidelity, Source = "Mock", ComputedAt = DateTime.UtcNow,
        };

    private static async Task AddAsync(IDbContextFactory<AppDbContext> factory, params RouteSegment[] rows)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.RouteSegments.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static IRouteSegmentInvalidationService Service(IDbContextFactory<AppDbContext> factory) =>
        new RouteSegmentInvalidationService(
            factory, new SqliteWriteLock(),
            NullLogger<RouteSegmentInvalidationService>.Instance);

    [Fact]
    public async Task InvalidateForPoi_DeletesBothDirections_AllModes_KeepsManual()
    {
        var factory = Seed();
        await AddAsync(factory,
            Row(1, 2, Fidelity.Estimated),                       // POI 1 as From
            Row(2, 1, Fidelity.Estimated),                       // POI 1 as To
            Row(1, 3, Fidelity.Estimated, TravelMode.Drive),   // other mode, still touches 1
            Row(1, 2, Fidelity.Manual, TravelMode.Drive),      // Manual touching 1 — KEEP
            Row(2, 3, Fidelity.Estimated));                      // does NOT touch 1 — KEEP

        await Service(factory).InvalidateForPoiAsync(1, CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var remaining = await db.RouteSegments.AsNoTracking().ToListAsync();

        remaining.Should().HaveCount(2);
        remaining.Should().Contain(r => r.Fidelity == Fidelity.Manual && r.FromPoiId == 1 && r.ToPoiId == 2);
        remaining.Should().Contain(r => r.FromPoiId == 2 && r.ToPoiId == 3 && r.Fidelity == Fidelity.Estimated);
        remaining.Should().NotContain(r => r.Fidelity == Fidelity.Estimated && (r.FromPoiId == 1 || r.ToPoiId == 1));
    }

    [Fact]
    public async Task InvalidateForPoi_NoRows_IsNoOp()
    {
        var factory = Seed();
        await Service(factory).InvalidateForPoiAsync(1, CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        (await db.RouteSegments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task InvalidateRecomputable_DeletesEligible_KeepsManualAndMeasured_ReturnsCount()
    {
        var factory = Seed();
        await AddAsync(factory,
            Row(1, 2, Fidelity.Estimated),                                        // eligible
            Row(2, 1, Fidelity.Placeholder),                                      // eligible
            Row(2, 3, Fidelity.Estimated, TravelMode.Drive),                    // eligible (EstimatedFallback source)
            Row(1, 3, Fidelity.Manual),                                           // KEEP — Manual
            Row(3, 1, Fidelity.Measured));                                        // KEEP — Measured

        // Stamp the fallback source on one eligible row to prove source doesn't matter — fidelity does.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var fallback = await db.RouteSegments.FirstAsync(r => r.FromPoiId == 2 && r.ToPoiId == 3);
            fallback.Source = TravelTimeSource.EstimatedFallback;
            await db.SaveChangesAsync();
        }

        var deleted = await Service(factory).InvalidateRecomputableForCollectionAsync(CollectionId, CancellationToken.None);

        deleted.Should().Be(3);

        await using var verify = await factory.CreateDbContextAsync();
        var remaining = await verify.RouteSegments.AsNoTracking().ToListAsync();
        remaining.Should().HaveCount(2);
        remaining.Should().Contain(r => r.Fidelity == Fidelity.Manual);
        remaining.Should().Contain(r => r.Fidelity == Fidelity.Measured);
        remaining.Should().NotContain(r => r.Fidelity == Fidelity.Estimated || r.Fidelity == Fidelity.Placeholder);
    }

    [Fact]
    public async Task InvalidateRecomputable_EmptyCollection_ReturnsZero()
    {
        var factory = Seed();
        // No segments at all.
        var deleted = await Service(factory).InvalidateRecomputableForCollectionAsync(CollectionId, CancellationToken.None);
        deleted.Should().Be(0);
    }

    // Story 4.1 (TRIP-OSRM-01, AC7 / Epic-3 retro A6): an EstimatedFallback row is
    // recompute-eligible — it carries Fidelity.Estimated, so the existing
    // (Fidelity != Manual && != Measured) predicate already includes it. This is the
    // load-bearing assertion that, once OSRM is enabled, a degraded straight-line leg
    // is cleared and refilled (as Measured) by the next compute pass, while an
    // already-Measured OSRM row is preserved (never silently downgraded).
    [Fact]
    public async Task InvalidateRecomputable_EstimatedFallbackRow_IsEligible_MeasuredPreserved()
    {
        var factory = Seed();
        await AddAsync(factory,
            Row(1, 2, Fidelity.Estimated),    // an EstimatedFallback leg (source stamped below) — DELETE
            Row(2, 3, Fidelity.Measured));    // an existing OSRM Measured leg — KEEP

        await using (var db = await factory.CreateDbContextAsync())
        {
            var fallback = await db.RouteSegments.FirstAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2);
            fallback.Source = TravelTimeSource.EstimatedFallback;
            var measured = await db.RouteSegments.FirstAsync(r => r.FromPoiId == 2 && r.ToPoiId == 3);
            measured.Source = TravelTimeSource.Osrm;
            await db.SaveChangesAsync();
        }

        var deleted = await Service(factory).InvalidateRecomputableForCollectionAsync(CollectionId, CancellationToken.None);

        deleted.Should().Be(1, "the EstimatedFallback row is recompute-eligible");

        await using var verify = await factory.CreateDbContextAsync();
        var remaining = await verify.RouteSegments.AsNoTracking().ToListAsync();
        remaining.Should().ContainSingle()
            .Which.Should().Match<RouteSegment>(r =>
                r.Fidelity == Fidelity.Measured && r.Source == TravelTimeSource.Osrm,
                "the Measured OSRM row survives recompute");
    }
}
