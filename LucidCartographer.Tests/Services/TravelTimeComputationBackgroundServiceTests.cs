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
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId, OrderIndex = i });
        }
        db.SaveChanges();
        return factory;
    }

    /// <summary>A provider that always throws — stands in for an unreachable routing engine.</summary>
    private sealed class ThrowingProvider : ITravelTimeProvider
    {
        public string Source => "Throwing";
        public Task<TravelLegResult> GetLegAsync(TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct) =>
            throw new InvalidOperationException("routing engine unreachable");
    }

    /// <summary>Throws only for the first leg (from PoiId 1); otherwise delegates to the Mock.</summary>
    private sealed class ThrowOnFirstLegProvider(MockTravelTimeProvider inner) : ITravelTimeProvider
    {
        public string Source => inner.Source;
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

    // Story 4.1 (TRIP-OSRM-01, AC3): end-to-end degradation with the REAL OSRM
    // provider. When OSRM returns code "NoRoute" the provider throws, and the loop's
    // existing TRIP-DEGRADE-01 catch writes an Estimated row stamped
    // Source = EstimatedFallback (never blank, never errors). Confirms the AC3 wiring
    // through the production degradation branch — not a generic throwing stub.
    private sealed class OsrmStubHandler(System.Net.HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class OsrmStubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    [Fact]
    public async Task ProcessOnce_OsrmProviderNoRoute_DegradesToEstimatedFallback()
    {
        var factory = SeedDriveOpenPath(stops: 2);
        var osrmProvider = new OsrmTravelTimeProvider(
            new OsrmStubFactory(new OsrmStubHandler(
                System.Net.HttpStatusCode.OK, "{\"code\":\"NoRoute\",\"routes\":[]}")),
            Options.Create(new OsrmOptions { DriveBaseUrl = "http://osrm-car:5000" }),
            Options.Create(new TravelTimeOptions { DriveSpeedMetersPerSecond = 20.0 }),
            NullLogger<OsrmTravelTimeProvider>.Instance);

        var service = BuildService(factory, new SqliteWriteLock(),
            provider: osrmProvider, pipelines: NoRetryPipelines());

        var act = async () => await service.ProcessOnceAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("a no-route leg degrades, never errors out of the loop");

        await using var db = await factory.CreateDbContextAsync();
        var row = await db.RouteSegments.SingleAsync();
        row.Fidelity.Should().Be(Fidelity.Estimated, "the OSRM no-route leg falls back to a haversine estimate");
        row.Source.Should().Be(TravelTimeSource.EstimatedFallback);
        row.DurationSeconds.Should().BeGreaterThan(0, "never blank");
        row.DistanceMeters.Should().BeGreaterThan(0);
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
}
