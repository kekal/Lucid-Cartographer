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
/// TRIP-BULKMODE-01: bulk assignment of one travel mode to ALL of a trip's legs via
/// <see cref="TripViewModel.SetAllLegsModeAsync"/> /
/// <see cref="ITripOrderingService.SetAllOutgoingTravelModesAsync"/>. Covers the spec's
/// I/O matrix: fill-empty (overwrite off), preserve-explicit, overwrite-on, Any/Air
/// revert, the Roundtrip closing-leg From-stop, the open-path Finish exclusion,
/// no-mutation invariants, and the invalid-mode guard.
/// </summary>
public class TripViewModelBulkModeTests
{
    private const int CollectionId = 1;

    /// <summary>
    /// Seeds <paramref name="stopCount"/> placeable stops (P1..Pn by ascending AddedDate,
    /// so the seeded Stop Order is 1..n). <paramref name="finishPoiId"/> sets a distinct
    /// Finish on the collection (the last stop, to mirror a real Finish pin).
    /// <paramref name="initialModes"/> maps PoiId → its OutgoingTravelMode.
    /// </summary>
    private static IDbContextFactory<AppDbContext> Seed(
        int stopCount, int? finishPoiId = null, IReadOnlyDictionary<int, string?>? initialModes = null)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf",
            TravelMode = TravelMode.AnyAir, FinishPoiId = finishPoiId,
        });
        for (var i = 1; i <= stopCount; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            var mode = initialModes is not null && initialModes.TryGetValue(i, out var m) ? m : null;
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId, OutgoingTravelMode = mode });
        }
        db.SaveChanges();
        return factory;
    }

    private static (TripViewModel Vm, IDbContextFactory<AppDbContext> Factory, ITripOrderingService Ordering) Build(
        IDbContextFactory<AppDbContext> factory)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(
            ordering, factory, writeLock,
            new TravelTimeTrigger(), new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance);
        return (vm, factory, ordering);
    }

    private static async Task<TripViewModel> EnabledVmAsync(IDbContextFactory<AppDbContext> factory, int placeable)
    {
        var (vm, _, _) = Build(factory);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return vm;
    }

    private static async Task<Dictionary<int, string?>> ReadModesAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == CollectionId)
            .ToDictionaryAsync(ci => ci.PoiId, ci => ci.OutgoingTravelMode);
    }

    [Fact]
    public async Task FillEmpty_AllAnyAir_SetsEveryFromStopIncludingClosingLeg()
    {
        // 2-stop Roundtrip, both Any/Air. overwrite off + Drive ⇒ BOTH P1 (1→2) and
        // P2 (closing 2→1) become Drive — the closing-leg From-stop is covered.
        var factory = Seed(stopCount: 2);
        await using var vm = await EnabledVmAsync(factory, placeable: 2);

        await vm.SetAllLegsModeAsync(TravelMode.Drive, overwriteExisting: false);

        var modes = await ReadModesAsync(factory);
        modes[1].Should().Be(TravelMode.Drive);
        modes[2].Should().Be(TravelMode.Drive, "the Roundtrip closing leg departs the last stop, so it is a From-stop");
        vm.OrderedLegs.Should().OnlyContain(l => l.Mode == TravelMode.Drive);
    }

    [Fact]
    public async Task FillEmpty_PreservesExplicitModes()
    {
        // P1 already Walk, P2 unset. overwrite off + Drive ⇒ only P2 changes.
        var factory = Seed(stopCount: 2, initialModes: new Dictionary<int, string?> { [1] = TravelMode.Walk });
        await using var vm = await EnabledVmAsync(factory, placeable: 2);

        await vm.SetAllLegsModeAsync(TravelMode.Drive, overwriteExisting: false);

        var modes = await ReadModesAsync(factory);
        modes[1].Should().Be(TravelMode.Walk, "an explicit mode is untouched when overwrite is off");
        modes[2].Should().Be(TravelMode.Drive);
    }

    [Fact]
    public async Task Overwrite_True_ReplacesExplicitModes()
    {
        var factory = Seed(stopCount: 2, initialModes: new Dictionary<int, string?> { [1] = TravelMode.Walk });
        await using var vm = await EnabledVmAsync(factory, placeable: 2);

        await vm.SetAllLegsModeAsync(TravelMode.Drive, overwriteExisting: true);

        var modes = await ReadModesAsync(factory);
        modes[1].Should().Be(TravelMode.Drive, "overwrite on replaces even an explicit mode");
        modes[2].Should().Be(TravelMode.Drive);
    }

    [Fact]
    public async Task BulkAnyAir_RevertsAllLegs_ToUnknownTime()
    {
        var factory = Seed(stopCount: 2, initialModes: new Dictionary<int, string?>
        {
            [1] = TravelMode.Drive, [2] = TravelMode.Drive,
        });
        await using var vm = await EnabledVmAsync(factory, placeable: 2);

        await vm.SetAllLegsModeAsync(TravelMode.AnyAir, overwriteExisting: true);

        var modes = await ReadModesAsync(factory);
        modes.Values.Should().OnlyContain(m => m == TravelMode.AnyAir);
        vm.OrderedLegs.Should().OnlyContain(l => l.Mode == TravelMode.AnyAir && l.DurationSeconds == null,
            "Any/Air legs have no ground cache row ⇒ '—'");
    }

    [Fact]
    public async Task OpenPath_DistinctFinish_DoesNotSetFinishStop()
    {
        // 3 stops, P3 is the distinct Finish ⇒ legs are 1→2 and 2→3 (no closing leg).
        // From-stops are P1 and P2 only; the Finish (P3) departs nothing.
        var factory = Seed(stopCount: 3, finishPoiId: 3);
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        await vm.SetAllLegsModeAsync(TravelMode.Drive, overwriteExisting: false);

        var modes = await ReadModesAsync(factory);
        modes[1].Should().Be(TravelMode.Drive);
        modes[2].Should().Be(TravelMode.Drive);
        modes[3].Should().BeNull("the distinct Finish departs no leg, so its outgoing mode is never assigned");
    }

    [Fact]
    public async Task BulkAssignment_DoesNotMutate_Order_Start_Finish()
    {
        var factory = Seed(stopCount: 3, finishPoiId: 3);
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        var before = await ReadOrderAsync(factory);
        var (startBefore, finishBefore) = await ReadPinsAsync(factory);

        await vm.SetAllLegsModeAsync(TravelMode.Drive, overwriteExisting: true);

        var after = await ReadOrderAsync(factory);
        var (startAfter, finishAfter) = await ReadPinsAsync(factory);
        after.Should().BeEquivalentTo(before, "a bulk mode assignment must not reorder stops");
        startAfter.Should().Be(startBefore);
        finishAfter.Should().Be(finishBefore);
    }

    [Fact]
    public async Task OverwriteOn_SwitchingMode_LeavesManualCacheRowUntouched()
    {
        // AC-7 / FR-12: the bulk path writes only OutgoingTravelMode — it never touches
        // RouteSegment rows. A Manual time stored under the old (1,2,Walk) key must survive
        // an overwrite-on switch to Drive (it simply stops being referenced; background
        // recompute, not run here, keeps its no-downgrade guard).
        var factory = Seed(stopCount: 2, initialModes: new Dictionary<int, string?> { [1] = TravelMode.Walk });
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Walk,
                DurationSeconds = 1234, DistanceMeters = 5000,
                Fidelity = Fidelity.Manual, Source = "Manual", ComputedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await using var vm = await EnabledVmAsync(factory, placeable: 2);

        await vm.SetAllLegsModeAsync(TravelMode.Drive, overwriteExisting: true);

        await using var verify = await factory.CreateDbContextAsync();
        var manual = await verify.RouteSegments.SingleOrDefaultAsync(
            r => r.FromPoiId == 1 && r.ToPoiId == 2 && r.TravelMode == TravelMode.Walk);
        manual.Should().NotBeNull("the bulk path must not delete or alter Manual cache rows");
        manual!.Fidelity.Should().Be(Fidelity.Manual);
        manual.DurationSeconds.Should().Be(1234, "the stored Manual time is preserved verbatim");
    }

    [Fact]
    public async Task InvalidMode_Throws_AtServiceLayer()
    {
        var factory = Seed(stopCount: 2);
        var (_, _, ordering) = Build(factory);

        var act = async () => await ordering.SetAllOutgoingTravelModesAsync(CollectionId, "Teleport", overwriteExisting: true);

        await act.Should().ThrowAsync<ArgumentException>();
        var modes = await ReadModesAsync(factory);
        modes.Values.Should().OnlyContain(m => m == null, "an invalid mode writes nothing");
    }

    private static async Task<Dictionary<int, int>> ReadOrderAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == CollectionId)
            .ToDictionaryAsync(ci => ci.PoiId, ci => ci.OrderIndex);
    }

    private static async Task<(int? Start, int? Finish)> ReadPinsAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var c = await db.PoiCollections.FirstAsync(c => c.Id == CollectionId);
        return (c.StartPoiId, c.FinishPoiId);
    }
}
