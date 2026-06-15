using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests.ViewModels;

/// <summary>
/// Story 2.2 (AC 1, 2, 5): the ViewModel persists the per-trip TravelMode + signals
/// the recompute trigger (no-op on the active mode), and upserts/clears a manual
/// Any/Air leg time (Fidelity.Manual, Source "Manual", minutesâ†’seconds) updating the
/// total â€” clearing reverts the leg to a Placeholder/uncomputed state.
/// </summary>
public class TripViewModelTravelModeTests
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed(int placeable)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.AnyAir,
        });
        for (var i = 1; i <= placeable; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task<(TripViewModel Vm, TravelTimeTrigger Trigger, IDbContextFactory<AppDbContext> Factory)> EnabledVmAsync(int placeable)
    {
        var factory = Seed(placeable);
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var trigger = new TravelTimeTrigger();
        var vm = new TripViewModel(
            ordering, factory, writeLock,
            trigger, new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return (vm, trigger, factory);
    }

    private static async Task<string?> ReadModeAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PoiCollections.Where(c => c.Id == CollectionId).Select(c => c.TravelMode).FirstAsync();
    }

    [Fact]
    public async Task SetTravelMode_PersistsMode_SignalsTrigger_UpdatesState()
    {
        var (vm, trigger, factory) = await EnabledVmAsync(placeable: 2);
        await using var _ = vm;
        // Drain any pending signal raised during enable (legs uncomputed under
        // AnyAir) so the post-change assertion observes only the mode-change signal.
        // The channel is Bounded(1)+DropWrite, so one drain empties it.
        await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);

        await vm.SetTravelModeAsync(TravelMode.Drive);

        vm.TravelMode.Should().Be(TravelMode.Drive);
        (await ReadModeAsync(factory)).Should().Be(TravelMode.Drive, "the choice persists to PoiCollection.TravelMode");
        // The trigger was signalled (WaitAsync completes immediately, true on signal).
        var signalled = await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        signalled.Should().BeTrue("a mode change signals the background recompute");
    }

    [Fact]
    public async Task SetTravelMode_SameMode_IsNoOp()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;

        await vm.SetTravelModeAsync(TravelMode.AnyAir); // already active

        vm.TravelMode.Should().Be(TravelMode.AnyAir);
        (await ReadModeAsync(factory)).Should().Be(TravelMode.AnyAir);
    }

    [Fact]
    public async Task SetTravelMode_InvalidMode_IsNoOp()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;

        await vm.SetTravelModeAsync("Teleport");

        vm.TravelMode.Should().Be(TravelMode.AnyAir);
        (await ReadModeAsync(factory)).Should().Be(TravelMode.AnyAir);
    }

    // Story 3.2 (TRIP-LEGMODE-01): the trip-wide selector NO LONGER drives leg lookup —
    // legs resolve their cache row by their OWN per-leg mode (the From-stop's
    // OutgoingTravelMode). The old premise (setting PoiCollection.TravelMode = Drive makes
    // the legs pick Drive rows) is invalid under the per-leg model; re-expressed here as:
    // when each From-stop carries OutgoingTravelMode = Drive, the legs resolve the Drive
    // cache rows. (Setting the trip-wide mode is exercised separately above; here we set
    // per-leg modes directly since 3.4 owns the per-leg pill UI.)
    [Fact]
    public async Task DriveLegs_ResolveDriveCacheRows_ByPerLegMode()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;
        // Per-leg Drive mode on both From-stops + Drive cache rows for both roundtrip legs.
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var item in db.PoiCollectionItems.Where(ci => ci.PoiCollectionId == CollectionId))
            {
                item.OutgoingTravelMode = TravelMode.Drive;
            }
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive, DurationSeconds = 300, DistanceMeters = 5000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 2, ToPoiId = 1, TravelMode = TravelMode.Drive, DurationSeconds = 300, DistanceMeters = 5000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        // Re-read projections so the per-leg modes + Drive rows are picked up. The
        // trip-wide selector is now inert for leg lookup, but SetTravelModeAsync still
        // triggers a projection refresh (its persistence side-effect is irrelevant here).
        await vm.SetTravelModeAsync(TravelMode.Walk);

        vm.OrderedLegs.Should().OnlyContain(l => l.Mode == TravelMode.Drive, "each leg's mode is its From-stop's OutgoingTravelMode, NOT the trip-wide selector");
        vm.OrderedLegs.Should().OnlyContain(l => l.Fidelity == Fidelity.Estimated);
        vm.TotalTravelTimeSeconds.Should().Be(600, "the Drive cache rows back the per-leg-Drive legs");
    }

    private static async Task<string?> ReadOutgoingModeAsync(IDbContextFactory<AppDbContext> factory, int poiId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == CollectionId && ci.PoiId == poiId)
            .Select(ci => ci.OutgoingTravelMode)
            .FirstAsync();
    }

    // === Story 3.4 (TRIP-LEGMODE-01, FR-19/21): per-leg mode pill ===

    [Fact]
    public async Task SetLegMode_GroundMode_WritesOutgoingMode_ProjectsLegMode_TriggersCompute()
    {
        var (vm, trigger, factory) = await EnabledVmAsync(placeable: 2);
        await using var _ = vm;
        // Drain the enable-time signal so we observe only the mode-change signal.
        await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);

        await vm.SetLegModeAsync(fromPoiId: 1, TravelMode.Drive);

        (await ReadOutgoingModeAsync(factory, 1)).Should().Be(TravelMode.Drive,
            "the From-stop's OutgoingTravelMode is written");
        var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1);
        leg.Mode.Should().Be(TravelMode.Drive, "the leg's projected Mode reflects the per-leg write");

        var signalled = await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        signalled.Should().BeTrue("a ground mode auto-computes ⇒ the background trigger fires");
    }

    [Fact]
    public async Task SetLegMode_AnyAir_SetsMode_NoComputeTrigger()
    {
        // To isolate the MODE-driven trigger from the shared RefreshProjectionsAsync
        // "any uncomputed leg ⇒ Signal" behavior, give every leg a cache row first so
        // IsAnyLegComputing is false — then the only possible trigger source is the
        // SetLegModeAsync mode logic itself. Both roundtrip legs start Drive + cached.
        var (vm, trigger, factory) = await EnabledVmAsync(placeable: 2);
        await using var _ = vm;
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var item in db.PoiCollectionItems.Where(ci => ci.PoiCollectionId == CollectionId))
            {
                item.OutgoingTravelMode = TravelMode.Drive;
            }
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive, DurationSeconds = 300, DistanceMeters = 5000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 2, ToPoiId = 1, TravelMode = TravelMode.Drive, DurationSeconds = 300, DistanceMeters = 5000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
            // Also seed the Any/Air row for leg 1â†’2 so switching it to Any/Air still
            // resolves a cached row (no uncomputed leg remains after the switch).
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.AnyAir, DurationSeconds = 600, DistanceMeters = 8000, Fidelity = Fidelity.Manual, Source = "Manual", ComputedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        // Refresh projections so the directly-written Drive rows + per-leg modes are
        // picked up (a no-op dwell set refreshes WITHOUT invalidating the cache), then
        // drain every pending signal so the post-switch assertion sees only a new one.
        await vm.SetDwellMinutesAsync(1, null);
        while (await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None)) { }

        await vm.SetLegModeAsync(fromPoiId: 1, TravelMode.AnyAir);

        (await ReadOutgoingModeAsync(factory, 1)).Should().Be(TravelMode.AnyAir,
            "Any/Air is a real stored mode value (the manual-only state)");
        var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1);
        leg.Mode.Should().Be(TravelMode.AnyAir);
        vm.IsAnyLegComputing.Should().BeFalse("every leg has a cache row, so the shared refresh has nothing to kick");

        var signalled = await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        signalled.Should().BeFalse("Any/Air is manual-only ⇒ NO compute trigger (FR-21)");
    }

    [Fact]
    public async Task SetOutgoingTravelMode_InvalidMode_Throws_NoWrite()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);

        var act = async () => await ordering.SetOutgoingTravelModeAsync(CollectionId, fromPoiId: 1, "Teleport", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>("an unknown mode is rejected by the sole writer");
        (await ReadOutgoingModeAsync(factory, 1)).Should().BeNull("nothing is written when the mode is invalid");
    }

    [Fact]
    public async Task SetOutgoingTravelMode_IsSoleWriter_AndNoOpsWhenUnchanged()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);

        // The service writes the mode (sole writer of a single leg's mode).
        await ordering.SetOutgoingTravelModeAsync(CollectionId, fromPoiId: 1, TravelMode.Cycle, CancellationToken.None);
        (await ReadOutgoingModeAsync(factory, 1)).Should().Be(TravelMode.Cycle);

        // Setting the same value again is a no-op (no throw, value unchanged).
        await ordering.SetOutgoingTravelModeAsync(CollectionId, fromPoiId: 1, TravelMode.Cycle, CancellationToken.None);
        (await ReadOutgoingModeAsync(factory, 1)).Should().Be(TravelMode.Cycle);

        // null (≡ AnyAir) clears the stored value back to null.
        await ordering.SetOutgoingTravelModeAsync(CollectionId, fromPoiId: 1, null, CancellationToken.None);
        (await ReadOutgoingModeAsync(factory, 1)).Should().BeNull("null is the undefined/Any-Air state");
    }

    [Fact]
    public async Task SetLegMode_DoesNotChangeStopOrder()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 3);
        await using var _v = vm;
        var before = vm.OrderedStops.Select(s => s.PoiId).ToList();

        await vm.SetLegModeAsync(fromPoiId: 2, TravelMode.Walk);

        vm.OrderedStops.Select(s => s.PoiId).Should().Equal(before, "setting a leg mode never reorders");
    }

    [Fact]
    public async Task SetManualLegTime_WritesManualRow_UpdatesTotal()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;
        // Seed the other roundtrip leg (2â†’1) so the total can resolve once 1â†’2 is manual.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 2, ToPoiId = 1, TravelMode = TravelMode.AnyAir, DurationSeconds = 600, DistanceMeters = 8000, Fidelity = Fidelity.Manual, Source = "Manual", ComputedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await vm.SetManualLegTimeAsync(1, 2, minutes: 90);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.RouteSegments.FirstAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2 && r.TravelMode == TravelMode.AnyAir);
            row.DurationSeconds.Should().Be(90 * 60, "minutes are converted to canonical seconds at the UI edge");
            row.Fidelity.Should().Be(Fidelity.Manual);
            row.Source.Should().Be("Manual");
            row.GeometryPolyline.Should().BeNull();
            row.DistanceMeters.Should().BeGreaterThan(0, "the haversine distance backs the display");
        }

        var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        leg.Fidelity.Should().Be(Fidelity.Manual);
        leg.DurationSeconds.Should().Be(5400);
        vm.TotalTravelTimeSeconds.Should().Be(5400 + 600, "the manual leg folds into the total");
    }

    [Fact]
    public async Task ClearManualLegTime_RemovesRow_RevertsToUncomputed()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;
        await vm.SetManualLegTimeAsync(1, 2, minutes: 45);

        await vm.ClearManualLegTimeAsync(1, 2);

        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.RouteSegments.AnyAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2 && r.TravelMode == TravelMode.AnyAir))
                .Should().BeFalse("clearing deletes the Manual row so the Mock recomputes a Placeholder");
        }

        var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        leg.Fidelity.Should().BeNull("no row â‡’ uncomputed (em-dash, no badge)");
        leg.DurationSeconds.Should().BeNull();
    }
}
