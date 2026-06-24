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
using Polly;
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

    // A no-retry "travel-time" pipeline so a throwing-provider test reaches the
    // fallback immediately instead of waiting out the production retry/backoff.
    private static ResiliencePipelineProvider<string> NoRetryPipelines()
    {
        var services = new ServiceCollection();
        services.AddResiliencePipeline("travel-time", _ => { });
        return services.BuildServiceProvider().GetRequiredService<ResiliencePipelineProvider<string>>();
    }

    private static TravelTimeComputationBackgroundService BuildService(
        IDbContextFactory<AppDbContext> factory, SqliteWriteLock writeLock,
        ITravelTimeProvider? provider = null,
        ResiliencePipelineProvider<string>? pipelines = null)
    {
        var options = Options.Create(new TravelTimeOptions
        {
            AssumedSpeedMetersPerSecond = 13.8889,
            DriveSpeedMetersPerSecond = 20.0,
        });
        provider ??= new MockTravelTimeProvider(options);
        return new TravelTimeComputationBackgroundService(
            factory,
            new TravelTimeTrigger(),
            new TravelTimeProgressService(),
            provider,
            writeLock,
            pipelines ?? Pipelines(),
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
        // Story 3.2 (TRIP-LEGMODE-01): legs are per-leg-mode driven now. A ground mode
        // (Drive) on each From-stop's OutgoingTravelMode is what makes the roundtrip legs
        // auto-compute — the collection-wide TravelMode no longer drives legs. Both stops
        // get Drive so both directional legs (1→2 and the closing 2→1) are enqueued.
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 1, PoiCollectionId = CollectionId, OrderIndex = 1, OutgoingTravelMode = TravelMode.Drive });
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 2, PoiCollectionId = CollectionId, OrderIndex = 2, OutgoingTravelMode = TravelMode.Drive });
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
        // Story 3.2 (TRIP-LEGMODE-01): the row is keyed by the leg's OWN mode (the
        // From-stop's OutgoingTravelMode = Drive), not the collection's trip-wide mode.
        leg.TravelMode.Should().Be(TravelMode.Drive);
        // A ground (Drive) leg from the Mock is Estimated (Placeholder is the Any/Air case,
        // which is now never auto-computed at all — covered by the AnyAir-not-enqueued test).
        leg.Fidelity.Should().Be(Fidelity.Estimated);
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

    // --- Story 3.2 (TRIP-LEGMODE-01, FR-21): ground-only auto-compute, per-leg mode ---

    /// <summary>
    /// A roundtrip where each From-stop carries a different per-leg outgoing mode so the
    /// enqueue/skip decision can be checked per leg.
    /// </summary>
    private static IDbContextFactory<AppDbContext> SeedRoundtripWithModes(string? mode1to2, string? mode2to1)
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
        // From-stop of leg 1→2 is P1; From-stop of the closing leg 2→1 is P2.
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 1, PoiCollectionId = CollectionId, OrderIndex = 1, OutgoingTravelMode = mode1to2 });
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 2, PoiCollectionId = CollectionId, OrderIndex = 2, OutgoingTravelMode = mode2to1 });
        db.SaveChanges();
        return factory;
    }

    [Theory]
    [InlineData(TravelMode.Walk)]
    [InlineData(TravelMode.Drive)]
    [InlineData(TravelMode.Cycle)]
    public async Task ProcessOnce_EnqueuesGroundModeLeg(string groundMode)
    {
        // Leg 1→2 is a ground mode (enqueued); the closing 2→1 is AnyAir (never enqueued)
        // so exactly the one ground leg lands a row, keyed by its own (1,2,mode).
        var factory = SeedRoundtripWithModes(mode1to2: groundMode, mode2to1: TravelMode.AnyAir);
        var service = BuildService(factory, new SqliteWriteLock());

        await service.ProcessOnceAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.RouteSegments.ToListAsync();
        rows.Should().ContainSingle("only the ground-mode leg auto-computes; the AnyAir leg never does");
        var leg = rows.Single();
        leg.FromPoiId.Should().Be(1);
        leg.ToPoiId.Should().Be(2);
        leg.TravelMode.Should().Be(groundMode, "the row is keyed by the leg's own per-leg mode");
        leg.DurationSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ProcessOnce_NeverEnqueuesAnyAirLeg()
    {
        // Both From-stops are AnyAir (one null, one explicit) ⇒ neither leg is ever
        // auto-estimated; the compute pass produces no rows at all (FR-21).
        var factory = SeedRoundtripWithModes(mode1to2: null, mode2to1: TravelMode.AnyAir);
        var service = BuildService(factory, new SqliteWriteLock());

        await service.ProcessOnceAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        (await db.RouteSegments.CountAsync()).Should().Be(0, "AnyAir/null legs are never auto-estimated");
    }

    [Fact]
    public async Task ProcessOnce_MissingRowDetection_IsPerLegMode()
    {
        // A Drive 1→2 leg already has a cache row under (1,2,Drive); the closing 2→1
        // leg is also Drive but has NO row. Missing-row detection is per the leg's own
        // mode key, so only the uncached 2→1 Drive leg is enqueued (the 1→2 row is left
        // intact). A stale (1,2,Walk) row must NOT satisfy the (1,2,Drive) leg.
        var factory = SeedRoundtripWithModes(mode1to2: TravelMode.Drive, mode2to1: TravelMode.Drive);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive,
                DurationSeconds = 4242, DistanceMeters = 5000,
                Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow,
            });
            // A stale Walk row for the SAME pair must not mask the Drive leg's own key.
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 2, ToPoiId = 1, TravelMode = TravelMode.Walk,
                DurationSeconds = 111, DistanceMeters = 222,
                Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var service = BuildService(factory, new SqliteWriteLock());
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        // The existing (1,2,Drive) row is untouched (its leg was not pending).
        var kept = await verify.RouteSegments.SingleAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2 && r.TravelMode == TravelMode.Drive);
        kept.DurationSeconds.Should().Be(4242, "an already-cached leg is not recomputed");
        // The (2,1,Drive) leg had no row under its OWN mode key ⇒ it was computed now,
        // distinct from the stale (2,1,Walk) row which does not satisfy a Drive leg.
        (await verify.RouteSegments.AnyAsync(r => r.FromPoiId == 2 && r.ToPoiId == 1 && r.TravelMode == TravelMode.Drive))
            .Should().BeTrue("the Drive 2→1 leg's missing row is detected per its own mode key");
        (await verify.RouteSegments.SingleAsync(r => r.FromPoiId == 2 && r.ToPoiId == 1 && r.TravelMode == TravelMode.Walk))
            .DurationSeconds.Should().Be(111, "the stale Walk row is left untouched");
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
        // Story 3.2 (TRIP-LEGMODE-01): the 1→2 leg's own mode is Drive (its From-stop's
        // OutgoingTravelMode). Seed the user's Manual row at that SAME (1,2,Drive) key so
        // it is the row covering the actual leg — a compute pass must never overwrite it.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive,
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

    // --- Story 2.3 (TRIP-DEGRADE-01): provider-down straight-line fallback ---

    /// <summary>A Drive collection with N ordered, placeable stops (an open path).</summary>
    private static IDbContextFactory<AppDbContext> SeedDriveOpenPath(int stops)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf",
            TripViewEnabled = true, TravelMode = TravelMode.Drive,
            // Distinct Finish ⇒ open path (N-1 legs) so the leg count is predictable.
            StartPoiId = 1, FinishPoiId = stops,
        });
        for (var i = 1; i <= stops; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50.0 + i, Longitude = 20.0 + i, AddedDate = new DateTime(2025, 1, i) });
            // Story 3.2 (TRIP-LEGMODE-01): per-leg Drive mode on every From-stop so the
            // open-path legs auto-compute (a ground mode). The collection's trip-wide
            // TravelMode no longer drives legs.
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId, OrderIndex = i, OutgoingTravelMode = TravelMode.Drive });
        }
        db.SaveChanges();
        return factory;
    }

    /// <summary>A provider that always throws — stands in for an unreachable routing engine.</summary>
    private sealed class ThrowingProvider : ITravelTimeProvider
    {
        public string Source => "Throwing";
        public string? Attribution => null;
        public bool ProducesMeasuredFidelity => false;
        public Task<TravelLegResult> GetLegAsync(TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct) =>
            throw new InvalidOperationException("routing engine unreachable");
    }

    /// <summary>Throws only for the first leg (from PoiId 1); otherwise delegates to the Mock.</summary>
    private sealed class ThrowOnFirstLegProvider(MockTravelTimeProvider inner) : ITravelTimeProvider
    {
        public string Source => inner.Source;
        public string? Attribution => null;
        public bool ProducesMeasuredFidelity => inner.ProducesMeasuredFidelity;
        public Task<TravelLegResult> GetLegAsync(TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct) =>
            from.PoiId == 1
                ? throw new InvalidOperationException("routing engine unreachable for the first leg")
                : inner.GetLegAsync(from, to, travelMode, ct);
    }

    [Fact]
    public async Task ProcessOnce_ProviderThrows_UpsertsEstimatedFallbackRow_NoExceptionEscapes()
    {
        var factory = SeedDriveOpenPath(stops: 2);
        var service = BuildService(factory, new SqliteWriteLock(),
            provider: new ThrowingProvider(), pipelines: NoRetryPipelines());

        // AC1: never throws out of the loop.
        var act = async () => await service.ProcessOnceAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        await using var db = await factory.CreateDbContextAsync();
        var row = await db.RouteSegments.SingleAsync();
        row.FromPoiId.Should().Be(1);
        row.ToPoiId.Should().Be(2);
        row.Fidelity.Should().Be(Fidelity.Estimated, "a degraded leg falls back to a haversine Estimated value");
        row.Source.Should().Be(TravelTimeSource.EstimatedFallback, "the row is badged as a degradation, not a normal Mock estimate");
        row.DurationSeconds.Should().BeGreaterThan(0, "never blank — a real straight-line duration");
        row.DistanceMeters.Should().BeGreaterThan(0);
        row.GeometryPolyline.Should().BeNull();
    }

    [Fact]
    public async Task ProcessOnce_LegAfterAThrowingOne_StillComputes()
    {
        // 3 stops, open path (Start=1, Finish=3) ⇒ legs 1→2 and 2→3. The first leg
        // (from PoiId 1) throws; the loop must continue and compute the second leg
        // normally via the Mock.
        var factory = SeedDriveOpenPath(stops: 3);
        var options = Options.Create(new TravelTimeOptions { DriveSpeedMetersPerSecond = 20.0 });
        var mock = new MockTravelTimeProvider(options);
        var service = BuildService(factory, new SqliteWriteLock(),
            provider: new ThrowOnFirstLegProvider(mock), pipelines: NoRetryPipelines());

        await service.ProcessOnceAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.RouteSegments.ToListAsync();
        rows.Should().HaveCount(2, "both legs land a row — the throwing first leg falls back, the second computes");

        var degraded = rows.Single(r => r.FromPoiId == 1 && r.ToPoiId == 2);
        degraded.Fidelity.Should().Be(Fidelity.Estimated);
        degraded.Source.Should().Be(TravelTimeSource.EstimatedFallback);

        var normal = rows.Single(r => r.FromPoiId == 2 && r.ToPoiId == 3);
        normal.Fidelity.Should().Be(Fidelity.Estimated, "Drive mode from the Mock is Estimated");
        normal.Source.Should().Be(TravelTimeSource.Mock, "a normally-computed leg keeps the Mock source");
    }

    [Fact]
    public async Task ProcessOnce_ProviderThrows_DoesNotOverwrite_ManualRow()
    {
        var factory = SeedDriveOpenPath(stops: 2);
        // Seed a Manual row for the 1→2 Drive leg. Even with a throwing provider the
        // fallback must NOT overwrite/downgrade it (AC5).
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive,
                DurationSeconds = 5400, DistanceMeters = 123456,
                Fidelity = Fidelity.Manual, Source = TravelTimeSource.Manual, ComputedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var service = BuildService(factory, new SqliteWriteLock(),
            provider: new ThrowingProvider(), pipelines: NoRetryPipelines());
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var manual = await verify.RouteSegments.SingleAsync();
        manual.Fidelity.Should().Be(Fidelity.Manual, "a Manual row is never overwritten by the fallback");
        manual.Source.Should().Be(TravelTimeSource.Manual);
        manual.DurationSeconds.Should().Be(5400);
    }

    [Fact]
    public async Task ProcessOnce_ProviderThrows_DoesNotDowngrade_MeasuredRow()
    {
        // LoadPendingLegsAsync skips pairs that already have a row, so to exercise the
        // UpsertAsync Measured guard directly we drive UpsertAsync via a re-queued key.
        // Simpler: seed a Measured row, then assert a fallback pass leaves it intact.
        // Because the existing row makes the leg "not pending", the pass is a no-op for
        // it — but the guard is the defensive belt for a future recompute path (2.4).
        var factory = SeedDriveOpenPath(stops: 2);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive,
                DurationSeconds = 999, DistanceMeters = 111,
                Fidelity = Fidelity.Measured, Source = "OSRM", ComputedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var service = BuildService(factory, new SqliteWriteLock(),
            provider: new ThrowingProvider(), pipelines: NoRetryPipelines());
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var measured = await verify.RouteSegments.SingleAsync();
        measured.Fidelity.Should().Be(Fidelity.Measured, "a Measured row is never downgraded by the fallback");
        measured.Source.Should().Be("OSRM");
        measured.DurationSeconds.Should().Be(999);
    }

    // TRIP-DEGRADE-01 (AC5) / TRIP-MANUAL-01 (Story 2.2 AC6): drive the
    // no-downgrade guard DIRECTLY. The production loop never re-queues an existing
    // key, so this is the only path that actually enters the guard branch — it is
    // the load-bearing test for the future Story-2.4 recompute path. Asserts that an
    // upsert carrying a fresh fallback Estimated result leaves an existing
    // higher-trust row (Manual or Measured) completely untouched.
    [Theory]
    [InlineData(Fidelity.Manual, "Manual")]
    [InlineData(Fidelity.Measured, "OSRM")]
    public async Task UpsertAsync_NeverDowngrades_ExistingHigherTrustRow(string existingFidelity, string existingSource)
    {
        var factory = SeedDriveOpenPath(stops: 2);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive,
                DurationSeconds = 999, DistanceMeters = 111,
                Fidelity = existingFidelity, Source = existingSource, ComputedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var service = BuildService(factory, new SqliteWriteLock());
        var leg = new TravelTimeComputationBackgroundService.PendingLeg(
            new TravelEndpoint(1, 50, 20), new TravelEndpoint(2, 51, 21), TravelMode.Drive);
        var fallback = new TravelLegResult(
            DurationSeconds: 12345, DistanceMeters: 67890,
            Fidelity: Fidelity.Estimated, GeometryPolyline: null);

        await service.UpsertAsync(leg, fallback, TravelTimeSource.EstimatedFallback, CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.RouteSegments.SingleAsync();
        row.Fidelity.Should().Be(existingFidelity, "a higher-trust row is never downgraded to a fallback estimate");
        row.Source.Should().Be(existingSource);
        row.DurationSeconds.Should().Be(999, "the original duration is preserved");
        row.DistanceMeters.Should().Be(111);
    }

    // --- Story 2.3 (AD-2): capability-gated recompute / upgrade-eligibility ---

    /// <summary>
    /// A measured-capable stub: ProducesMeasuredFidelity=true, returns a Measured leg with a
    /// recognisable source/geometry so an upgrade over a low-fidelity estimate is observable.
    /// </summary>
    private sealed class MeasuredStubProvider : ITravelTimeProvider
    {
        public string Source => "ValhallaStub";
        public string? Attribution => null;
        public bool ProducesMeasuredFidelity => true;
        public Task<TravelLegResult> GetLegAsync(TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct) =>
            Task.FromResult(new TravelLegResult(
                DurationSeconds: 1800, DistanceMeters: 25000,
                Fidelity: Fidelity.Measured, GeometryPolyline: "stub_polyline"));
    }

    /// <summary>
    /// A measured-capable provider that always throws — stands in for a reachable-but-failing
    /// routing engine (e.g. a Valhalla no-route). Exercises the degrade branch on the new
    /// recompute arm (ProducesMeasuredFidelity=true).
    /// </summary>
    private sealed class MeasuredThrowingProvider(Exception toThrow) : ITravelTimeProvider
    {
        public string Source => "ValhallaStub";
        public string? Attribution => null;
        public bool ProducesMeasuredFidelity => true;
        public Task<TravelLegResult> GetLegAsync(TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct) =>
            throw toThrow;
    }

    /// <summary>Seeds an existing (1,2,Drive) row with the given fidelity/source for the roundtrip's 1→2 leg.</summary>
    private static async Task SeedExistingLegAsync(IDbContextFactory<AppDbContext> factory, string fidelity, string source)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive,
            DurationSeconds = 4242, DistanceMeters = 5000,
            Fidelity = fidelity, Source = source, ComputedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(TravelTimeSource.Mock)]
    [InlineData(TravelTimeSource.EstimatedFallback)]
    public async Task ProcessOnce_UpgradeEligibleRow_IsRecomputed_WhenProviderMeasuredCapable(string source)
    {
        // An Estimated row from Mock/EstimatedFallback is upgrade-eligible: a measured-capable
        // provider must re-enqueue it and overwrite the low-fidelity estimate with a Measured value.
        var factory = SeedRoundtripWithModes(mode1to2: TravelMode.Drive, mode2to1: TravelMode.AnyAir);
        await SeedExistingLegAsync(factory, Fidelity.Estimated, source);

        var service = BuildService(factory, new SqliteWriteLock(), provider: new MeasuredStubProvider());
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.RouteSegments.SingleAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2 && r.TravelMode == TravelMode.Drive);
        row.Fidelity.Should().Be(Fidelity.Measured, "an upgrade-eligible row is recomputed to a measured value");
        row.Source.Should().Be("ValhallaStub", "the measured provider's source replaces the estimate");
        row.DurationSeconds.Should().Be(1800, "the measured value overwrites the old estimate");
        row.GeometryPolyline.Should().Be("stub_polyline");
    }

    [Theory]
    [InlineData(TravelTimeSource.Mock)]
    [InlineData(TravelTimeSource.EstimatedFallback)]
    public async Task ProcessOnce_UpgradeEligibleRow_IsLeftAlone_WhenProviderMock(string source)
    {
        // Same upgrade-eligible seed, but the default Mock provider (ProducesMeasuredFidelity=false)
        // must NOT re-enqueue it — otherwise Mock would re-churn its own estimates forever (AC2).
        var factory = SeedRoundtripWithModes(mode1to2: TravelMode.Drive, mode2to1: TravelMode.AnyAir);
        await SeedExistingLegAsync(factory, Fidelity.Estimated, source);

        var service = BuildService(factory, new SqliteWriteLock()); // default MockTravelTimeProvider
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.RouteSegments.SingleAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2 && r.TravelMode == TravelMode.Drive);
        row.Fidelity.Should().Be(Fidelity.Estimated, "a Mock provider never re-enqueues its own estimate (no perpetual rework)");
        row.Source.Should().Be(source);
        row.DurationSeconds.Should().Be(4242, "the row is byte-for-byte unchanged under a Mock pass");
    }

    [Theory]
    [InlineData(Fidelity.Manual, TravelTimeSource.Manual)]
    [InlineData(Fidelity.Measured, TravelTimeSource.Valhalla)]
    public async Task ProcessOnce_ManualOrMeasuredRow_IsNeverReEnqueued_EvenWhenMeasuredCapable(string fidelity, string source)
    {
        // Manual and Measured rows fail IsUpgradeEligible, so even a measured-capable provider
        // never re-queues them — they are preserved exactly (AC3; the UpsertAsync guard is the
        // second line of defence behind this read).
        var factory = SeedRoundtripWithModes(mode1to2: TravelMode.Drive, mode2to1: TravelMode.AnyAir);
        await SeedExistingLegAsync(factory, fidelity, source);

        var service = BuildService(factory, new SqliteWriteLock(), provider: new MeasuredStubProvider());
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.RouteSegments.SingleAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2 && r.TravelMode == TravelMode.Drive);
        row.Fidelity.Should().Be(fidelity, "a protected row is never re-enqueued nor downgraded");
        row.Source.Should().Be(source);
        row.DurationSeconds.Should().Be(4242, "the protected row's value is untouched");
    }

    [Fact]
    public async Task ProcessOnce_EligibleFidelityButWrongSource_IsLeftAlone_WhenMeasuredCapable()
    {
        // Boundary: Estimated fidelity but a Source that is NOT Mock/EstimatedFallback
        // (e.g. "Valhalla") fails the source half of the predicate ⇒ not upgrade-eligible.
        var factory = SeedRoundtripWithModes(mode1to2: TravelMode.Drive, mode2to1: TravelMode.AnyAir);
        await SeedExistingLegAsync(factory, Fidelity.Estimated, TravelTimeSource.Valhalla);

        var service = BuildService(factory, new SqliteWriteLock(), provider: new MeasuredStubProvider());
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.RouteSegments.SingleAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2 && r.TravelMode == TravelMode.Drive);
        row.Fidelity.Should().Be(Fidelity.Estimated, "the source half of the eligibility predicate matters");
        row.Source.Should().Be(TravelTimeSource.Valhalla);
        row.DurationSeconds.Should().Be(4242, "a non-Mock/-fallback estimate is left alone");
    }

    [Fact]
    public async Task ProcessOnce_MeasuredCapableProviderThrows_DegradesLeg_WithoutAbortingBatch()
    {
        // A measured-capable provider that throws on the first leg must degrade THAT leg to an
        // EstimatedFallback (one leg at a time) and still compute the second leg — never throwing
        // out of ProcessOnceAsync (AC4, TRIP-DEGRADE-01, on the new recompute arm).
        var factory = SeedDriveOpenPath(stops: 3);
        var throwingFirst = new DegradeOnFirstLegProvider(new InvalidOperationException("routing engine unreachable"));
        var service = BuildService(factory, new SqliteWriteLock(),
            provider: throwingFirst, pipelines: NoRetryPipelines());

        var act = async () => await service.ProcessOnceAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("a single leg's failure degrades, never aborts the batch");

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.RouteSegments.ToListAsync();
        rows.Should().HaveCount(2, "both legs land a row — the failing first degrades, the second measures");
        var degraded = rows.Single(r => r.FromPoiId == 1 && r.ToPoiId == 2);
        degraded.Fidelity.Should().Be(Fidelity.Estimated);
        degraded.Source.Should().Be(TravelTimeSource.EstimatedFallback, "the failing leg falls back to a haversine estimate");
        var measured = rows.Single(r => r.FromPoiId == 2 && r.ToPoiId == 3);
        measured.Fidelity.Should().Be(Fidelity.Measured, "the second leg computes normally via the measured provider");
        measured.Source.Should().Be("ValhallaStub");
    }

    [Fact]
    public async Task ProcessOnce_ValhallaRouteUnavailable_DegradesToEstimatedFallback()
    {
        // ValhallaRouteUnavailableException is a plain Exception (Story 2.2), so the existing
        // general degrade catch handles it — NOT the cancellation re-throw. The leg degrades to
        // an EstimatedFallback without aborting the pass (AC4).
        var factory = SeedDriveOpenPath(stops: 2);
        var service = BuildService(factory, new SqliteWriteLock(),
            provider: new MeasuredThrowingProvider(new ValhallaRouteUnavailableException("no route")),
            pipelines: NoRetryPipelines());

        var act = async () => await service.ProcessOnceAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("a Valhalla no-route degrades, never errors out of the loop");

        await using var db = await factory.CreateDbContextAsync();
        var row = await db.RouteSegments.SingleAsync();
        row.Fidelity.Should().Be(Fidelity.Estimated, "the Valhalla failure falls back to a haversine estimate");
        row.Source.Should().Be(TravelTimeSource.EstimatedFallback);
        row.DurationSeconds.Should().BeGreaterThan(0, "never blank");
    }

    [Fact]
    public async Task ProcessOnce_PlaceholderUpgradeEligibleRow_IsRecomputed_WhenMeasuredCapable()
    {
        // Coverage gap: every other recompute test seeds Fidelity.Estimated, so the Placeholder
        // arm of IsUpgradeEligible (fidelity is Estimated OR Placeholder) was never exercised.
        // A Placeholder/Mock row on a ground leg is upgrade-eligible per AD-2's literal definition
        // and must be recomputed to a Measured value under a measured-capable provider.
        var factory = SeedRoundtripWithModes(mode1to2: TravelMode.Drive, mode2to1: TravelMode.AnyAir);
        await SeedExistingLegAsync(factory, Fidelity.Placeholder, TravelTimeSource.Mock);

        var service = BuildService(factory, new SqliteWriteLock(), provider: new MeasuredStubProvider());
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.RouteSegments.SingleAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2 && r.TravelMode == TravelMode.Drive);
        row.Fidelity.Should().Be(Fidelity.Measured, "a Placeholder row is upgrade-eligible (the Placeholder arm of the predicate)");
        row.Source.Should().Be("ValhallaStub", "the measured provider's source replaces the placeholder");
        row.DurationSeconds.Should().Be(1800, "the measured value overwrites the placeholder estimate");
    }

    [Theory]
    [InlineData(Fidelity.Manual, TravelTimeSource.Manual)]
    [InlineData(Fidelity.Measured, TravelTimeSource.Valhalla)]
    public async Task ProcessOnce_MixedBatch_UpgradesEligibleLeg_PreservesProtectedLeg_InOnePass(
        string protectedFidelity, string protectedSource)
    {
        // Coverage gap: every recompute test seeds a SINGLE leg. This proves the per-leg
        // eligibility decision is made independently WITHIN ONE PASS — an upgrade-eligible leg is
        // recomputed while a protected (Manual/Measured) leg in the same batch is left untouched.
        // SeedDriveOpenPath(3) yields two ground legs (1→2 and 2→3); seed 1→2 eligible and 2→3 protected.
        var factory = SeedDriveOpenPath(stops: 3);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive,
                DurationSeconds = 4242, DistanceMeters = 5000,
                Fidelity = Fidelity.Estimated, Source = TravelTimeSource.Mock, ComputedAt = DateTime.UtcNow,
            });
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 2, ToPoiId = 3, TravelMode = TravelMode.Drive,
                DurationSeconds = 999, DistanceMeters = 111,
                Fidelity = protectedFidelity, Source = protectedSource, ComputedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var service = BuildService(factory, new SqliteWriteLock(), provider: new MeasuredStubProvider());
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var upgraded = await verify.RouteSegments.SingleAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2);
        upgraded.Fidelity.Should().Be(Fidelity.Measured, "the eligible leg is upgraded in the same pass");
        upgraded.Source.Should().Be("ValhallaStub");
        upgraded.DurationSeconds.Should().Be(1800);

        var preserved = await verify.RouteSegments.SingleAsync(r => r.FromPoiId == 2 && r.ToPoiId == 3);
        preserved.Fidelity.Should().Be(protectedFidelity, "the protected leg is untouched even though a sibling leg was upgraded in the same batch");
        preserved.Source.Should().Be(protectedSource);
        preserved.DurationSeconds.Should().Be(999, "the protected leg's value is preserved ([TRIP-MANUAL-01], through the broadened read + upsert guard)");
    }

    // Story 2.6 (NFR-10 / FR-17, [TRIP-MANUAL-01]): the no-downgrade COUNTER-METRIC. Given BOTH a
    // pre-existing Manual row AND a pre-existing Measured row, the estimate→measured progression (a
    // full measured-provider pass) must leave BOTH rows byte-for-byte intact — duration, distance,
    // fidelity, AND source all unchanged. No Manual/Measured cache row is ever downgraded or deleted
    // as the ladder climbs from Estimated to Measured. This guards the already-built UpsertAsync
    // guard + IsUpgradeEligible read-gate via the production ProcessOnce path; no new guard is added.
    [Fact]
    public async Task ProcessOnce_EstimateToMeasuredProgression_NeverDowngradesManualOrMeasuredRows_NFR10()
    {
        // SeedDriveOpenPath(3) yields two ground legs (1→2 and 2→3). Seed 1→2 Manual and 2→3 Measured,
        // then run a measured-capable pass (the estimate→measured progression). Both protected rows
        // must survive untouched.
        var factory = SeedDriveOpenPath(stops: 3);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive,
                DurationSeconds = 5400, DistanceMeters = 123456,
                Fidelity = Fidelity.Manual, Source = TravelTimeSource.Manual, ComputedAt = DateTime.UtcNow,
            });
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 2, ToPoiId = 3, TravelMode = TravelMode.Drive,
                DurationSeconds = 999, DistanceMeters = 111,
                Fidelity = Fidelity.Measured, Source = TravelTimeSource.Valhalla, ComputedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // A measured-capable provider drives the estimate→measured progression. If the guard were
        // absent it would happily overwrite these rows with its ValhallaStub values.
        var service = BuildService(factory, new SqliteWriteLock(), provider: new MeasuredStubProvider());
        await service.ProcessOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();

        var manual = await verify.RouteSegments.SingleAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2);
        manual.Fidelity.Should().Be(Fidelity.Manual, "a Manual row is never downgraded across the estimate→measured progression");
        manual.Source.Should().Be(TravelTimeSource.Manual, "the Manual source is preserved byte-for-byte");
        manual.DurationSeconds.Should().Be(5400, "the user's Manual duration is untouched");
        manual.DistanceMeters.Should().Be(123456, "the Manual distance is untouched");

        var measured = await verify.RouteSegments.SingleAsync(r => r.FromPoiId == 2 && r.ToPoiId == 3);
        measured.Fidelity.Should().Be(Fidelity.Measured, "a Measured row is never downgraded across the estimate→measured progression");
        measured.Source.Should().Be(TravelTimeSource.Valhalla, "the Measured source is preserved byte-for-byte");
        measured.DurationSeconds.Should().Be(999, "the existing Measured duration is untouched");
        measured.DistanceMeters.Should().Be(111, "the existing Measured distance is untouched");

        // Counter-metric: neither protected row was deleted — exactly the two seeded rows remain.
        (await verify.RouteSegments.CountAsync()).Should().Be(2,
            "no protected row is deleted as the ladder climbs from Estimated to Measured (NFR-10)");
    }

    /// <summary>Measured-capable; throws the given exception on the first leg (PoiId 1), measures otherwise.</summary>
    private sealed class DegradeOnFirstLegProvider(Exception toThrow) : ITravelTimeProvider
    {
        public string Source => "ValhallaStub";
        public string? Attribution => null;
        public bool ProducesMeasuredFidelity => true;
        public Task<TravelLegResult> GetLegAsync(TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct) =>
            from.PoiId == 1
                ? throw toThrow
                : Task.FromResult(new TravelLegResult(
                    DurationSeconds: 1800, DistanceMeters: 25000,
                    Fidelity: Fidelity.Measured, GeometryPolyline: "stub_polyline"));
    }
}
