using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
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

namespace LucidCartographer.Tests.ViewModels;

/// <summary>
/// Story 2.4 (TRIP-RECOMPUTE-01, AC1/AC4/AC5): the VM's explicit Recompute
/// invalidates the eligible cached rows (keeping Manual), the no-op reorder over a
/// fully-cached trip neither writes nor signals (AC1), and a recompute followed by a
/// background pass with a stub Measured provider upgrades a leg Estimated→Measured,
/// flipping <see cref="TripLeg.IsMeasured"/> and raising <see cref="TripViewModel.StateChanged"/>.
/// </summary>
public class TripViewModelRecomputeTests
{
    private const int CollectionId = 1;

    // A stub provider that always returns a Measured result — the Epic 4 upgrade
    // signal, exercised here since the shipping Mock never yields Measured.
    private sealed class MeasuredProvider : ITravelTimeProvider
    {
        public string Source => "StubMeasured";
        public Task<TravelLegResult> GetLegAsync(TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct) =>
            Task.FromResult(new TravelLegResult(900, 12000, Fidelity.Measured, GeometryPolyline: null));
    }

    // Counts provider calls so the no-op reorder test can prove the compute path was
    // never entered (the trigger was not signalled ⇒ no leg recomputed).
    private sealed class CountingProvider(IOptions<TravelTimeOptions> options) : ITravelTimeProvider
    {
        public int Calls { get; private set; }
        public string Source => "Mock";
        public Task<TravelLegResult> GetLegAsync(TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct)
        {
            Calls++;
            return new MockTravelTimeProvider(options).GetLegAsync(from, to, travelMode, ct);
        }
    }

    private static IDbContextFactory<AppDbContext> Seed(string mode = TravelMode.Drive)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = mode,
        });
        db.Pois.Add(new Poi { Id = 1, Name = "P1", Latitude = 50.0, Longitude = 20.0, AddedDate = new DateTime(2025, 1, 1) });
        db.Pois.Add(new Poi { Id = 2, Name = "P2", Latitude = 51.0, Longitude = 21.0, AddedDate = new DateTime(2025, 1, 2) });
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 1, PoiCollectionId = CollectionId, OrderIndex = 1 });
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 2, PoiCollectionId = CollectionId, OrderIndex = 2 });
        db.SaveChanges();
        return factory;
    }

    private static IOptions<TravelTimeOptions> Opts() => Options.Create(new TravelTimeOptions
    {
        AssumedSpeedMetersPerSecond = 13.8889,
        DriveSpeedMetersPerSecond = 20.0,
    });

    private static ResiliencePipelineProvider<string> Pipelines()
    {
        var services = new ServiceCollection();
        services.AddAppResiliencePipelines();
        return services.BuildServiceProvider().GetRequiredService<ResiliencePipelineProvider<string>>();
    }

    private static TravelTimeComputationBackgroundService BuildCompute(
        IDbContextFactory<AppDbContext> factory, SqliteWriteLock writeLock, ITravelTimeProvider provider) =>
        new(factory, new TravelTimeTrigger(), new TravelTimeProgressService(),
            provider, writeLock, Pipelines(), Opts(),
            NullLogger<TravelTimeComputationBackgroundService>.Instance);

    private static async Task<TripViewModel> EnabledVmAsync(
        IDbContextFactory<AppDbContext> factory, SqliteWriteLock writeLock,
        IRouteSegmentInvalidationService invalidation, TravelTimeTrigger trigger)
    {
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        var vm = new TripViewModel(
            ordering, factory, writeLock, trigger, new TravelTimeProgressService(),
            invalidation, NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 2);
        await vm.ToggleAsync();
        return vm;
    }

    [Fact]
    public async Task Recompute_InvalidatesEligible_KeepsManual_AndReQueuesMissing()
    {
        var factory = Seed();
        var writeLock = new SqliteWriteLock();
        var invalidation = TestDbHelper.CreateInvalidationService(factory, writeLock);
        var compute = BuildCompute(factory, writeLock, new MockTravelTimeProvider(Opts()));

        var trigger = new TravelTimeTrigger();
        await using var vm = await EnabledVmAsync(factory, writeLock, invalidation, trigger);

        // Fully cache the trip: one Estimated leg + one Manual leg.
        await compute.ProcessOnceAsync(CancellationToken.None);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var leg = await db.RouteSegments.FirstAsync(r => r.FromPoiId == 2 && r.ToPoiId == 1);
            leg.Fidelity = Fidelity.Manual;
            leg.Source = "Manual";
            await db.SaveChangesAsync();
        }

        await vm.RecomputeTravelTimesAsync();

        await using var verify = await factory.CreateDbContextAsync();
        var rows = await verify.RouteSegments.AsNoTracking().ToListAsync();
        // The Manual row survives; the Estimated row was deleted (now missing).
        rows.Should().ContainSingle(r => r.Fidelity == Fidelity.Manual && r.FromPoiId == 2 && r.ToPoiId == 1);
        rows.Should().NotContain(r => r.FromPoiId == 1 && r.ToPoiId == 2);
        // A missing leg ⇒ the VM is computing again (RefreshProjections signalled).
        vm.IsAnyLegComputing.Should().BeTrue("the deleted Estimated leg is re-queued");
    }

    [Fact]
    public async Task Recompute_ThenBackgroundPass_WithMeasuredProvider_UpgradesLeg_AndFiresStateChanged()
    {
        var factory = Seed();
        var writeLock = new SqliteWriteLock();
        var invalidation = TestDbHelper.CreateInvalidationService(factory, writeLock);

        var trigger = new TravelTimeTrigger();
        await using var vm = await EnabledVmAsync(factory, writeLock, invalidation, trigger);

        // Seed the trip fully Estimated via the Mock.
        var mockCompute = BuildCompute(factory, writeLock, new MockTravelTimeProvider(Opts()));
        await mockCompute.ProcessOnceAsync(CancellationToken.None);

        var fired = false;
        vm.StateChanged += () => fired = true;

        // Recompute deletes the eligible Estimated rows…
        await vm.RecomputeTravelTimesAsync();
        fired.Should().BeTrue("RecomputeTravelTimesAsync notifies via StateChanged");

        // …and the next background pass (now a Measured provider) refills them Measured.
        var measuredCompute = BuildCompute(factory, writeLock, new MeasuredProvider());
        await measuredCompute.ProcessOnceAsync(CancellationToken.None);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var rows = await db.RouteSegments.AsNoTracking().ToListAsync();
            rows.Should().OnlyContain(r => r.Fidelity == Fidelity.Measured);
        }

        // The VM reflects the upgrade on its next refresh. A second recompute keeps
        // the Measured rows (Measured is NOT eligible for invalidation) and re-reads
        // the projections — proving the upgrade lands via the real refresh path, not
        // a test back-door.
        await vm.RecomputeTravelTimesAsync();
        vm.OrderedLegs.Should().NotBeEmpty();
        vm.OrderedLegs.Should().OnlyContain(l => l.IsMeasured);
        vm.IsAnyLegComputing.Should().BeFalse();
    }

    [Fact]
    public async Task Recompute_WithMock_EstimatedStaysEstimated_NoSpuriousUpgrade()
    {
        var factory = Seed();
        var writeLock = new SqliteWriteLock();
        var invalidation = TestDbHelper.CreateInvalidationService(factory, writeLock);
        var compute = BuildCompute(factory, writeLock, new MockTravelTimeProvider(Opts()));

        var trigger = new TravelTimeTrigger();
        await using var vm = await EnabledVmAsync(factory, writeLock, invalidation, trigger);

        await compute.ProcessOnceAsync(CancellationToken.None);
        await vm.RecomputeTravelTimesAsync();
        await compute.ProcessOnceAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.RouteSegments.AsNoTracking().ToListAsync();
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Fidelity == Fidelity.Estimated, "the Mock yields Estimated for Drive — no spurious Measured upgrade");
    }

    [Fact]
    public async Task NoOpReorder_OverFullyCachedTrip_AddsNoRows_AndDoesNotSignal()
    {
        var factory = Seed();
        var writeLock = new SqliteWriteLock();
        var invalidation = TestDbHelper.CreateInvalidationService(factory, writeLock);
        var counting = new CountingProvider(Opts());
        var compute = BuildCompute(factory, writeLock, counting);

        // Enable Trip View in the DB and fully cache the trip BEFORE building the VM,
        // so the VM's initial projection reads a complete cache (⇒ not computing).
        await using (var db = await factory.CreateDbContextAsync())
        {
            var c = await db.PoiCollections.FirstAsync(x => x.Id == CollectionId);
            c.TripViewEnabled = true;
            await db.SaveChangesAsync();
        }
        await compute.ProcessOnceAsync(CancellationToken.None);
        var callsAfterSeed = counting.Calls;

        // The trigger the VM signals on; we drain it after build so a later signal
        // from the reorder is detectable.
        var trigger = new TravelTimeTrigger();
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        var vm = new TripViewModel(
            ordering, factory, writeLock, trigger, new TravelTimeProgressService(),
            invalidation, NullLogger<TripViewModel>.Instance);
        await using var _ = vm;
        // LoadAsync reads the DB-persisted TripViewEnabled=true ⇒ the VM is enabled
        // with projections built from the full cache (no ToggleAsync needed).
        await vm.LoadAsync(CollectionId, 2);

        long rowsBefore;
        await using (var db = await factory.CreateDbContextAsync())
        {
            rowsBefore = await db.RouteSegments.CountAsync();
        }
        vm.IsAnyLegComputing.Should().BeFalse("the trip is fully cached after the compute pass");

        // Drain any pre-existing signal so a fresh signal from the reorder is detectable.
        await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);

        // A clamped/own-position move yields the same (From,To,Mode) set ⇒ no new legs.
        await vm.MoveStopToAsync(1, 1);

        // No new signal ⇒ WaitAsync(0) times out (returns false).
        var signalled = await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        signalled.Should().BeFalse("a no-op reorder over a fully-cached trip must not signal recompute (AC1)");

        await using var verify = await factory.CreateDbContextAsync();
        (await verify.RouteSegments.CountAsync()).Should().Be((int)rowsBefore, "no rows added");
        counting.Calls.Should().Be(callsAfterSeed, "the provider was not re-invoked");
    }
}
