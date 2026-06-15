using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

/// <summary>
/// Unit tests for the single OrderIndex write-path. Cover the canonical
/// invariant (1-based, contiguous, gap-free, unique over placeable items),
/// seed determinism, first-vs-already-ordered detection, append-at-end, and
/// re-compaction. Uses EF Core InMemory per the project's unit-test precedent.
/// </summary>
public class TripOrderingServiceTests
{
    private const int CollectionId = 1;

    private static TripOrderingService CreateService(IDbContextFactory<AppDbContext> factory) =>
        new(factory, new SqliteWriteLock(),
            new DistanceMatrixService(factory, Microsoft.Extensions.Options.Options.Create(new TravelTimeOptions())),
            NullLogger<TripOrderingService>.Instance);

    // Seeds a collection with the given (poiId, addedDate, placeable) members.
    // OrderIndex starts at 0 (the column default for a freshly-added row).
    private static async Task<IDbContextFactory<AppDbContext>> SeedAsync(
        params (int PoiId, DateTime Added, bool Placeable)[] members)
    {
        var factory = TestDbHelper.CreateFactory();
        await using var db = await factory.CreateDbContextAsync();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf" });
        foreach (var m in members)
        {
            db.Pois.Add(new Poi
            {
                Id = m.PoiId,
                Name = $"P{m.PoiId}",
                Latitude = m.Placeable ? 50.0 : null,
                Longitude = m.Placeable ? 20.0 : null,
                AddedDate = m.Added
            });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = m.PoiId, PoiCollectionId = CollectionId });
        }
        await db.SaveChangesAsync();
        return factory;
    }

    private static async Task<Dictionary<int, int>> ReadOrderAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == CollectionId)
            .ToDictionaryAsync(ci => ci.PoiId, ci => ci.OrderIndex);
    }

    [Fact]
    public async Task Seed_AssignsOneBasedContiguousOrder_ByAddedDateAscending()
    {
        var factory = await SeedAsync(
            (10, new DateTime(2025, 3, 1), true),
            (11, new DateTime(2025, 1, 1), true),
            (12, new DateTime(2025, 2, 1), true));
        var service = CreateService(factory);

        await service.SeedOrderAsync(CollectionId);

        var order = await ReadOrderAsync(factory);
        order[11].Should().Be(1, "earliest AddedDate is Stop 1 (1-based, not 0-based)");
        order[12].Should().Be(2);
        order[10].Should().Be(3);
    }

    [Fact]
    public async Task Seed_BreaksTies_ByPoiIdAscending()
    {
        var same = new DateTime(2025, 1, 1);
        var factory = await SeedAsync(
            (30, same, true),
            (10, same, true),
            (20, same, true));
        var service = CreateService(factory);

        await service.SeedOrderAsync(CollectionId);

        var order = await ReadOrderAsync(factory);
        order[10].Should().Be(1);
        order[20].Should().Be(2);
        order[30].Should().Be(3);
    }

    [Fact]
    public async Task Seed_LeavesNonPlaceableItems_Unordered()
    {
        var factory = await SeedAsync(
            (1, new DateTime(2025, 1, 1), true),
            (2, new DateTime(2025, 1, 2), false),
            (3, new DateTime(2025, 1, 3), true));
        var service = CreateService(factory);

        await service.SeedOrderAsync(CollectionId);

        var order = await ReadOrderAsync(factory);
        order[1].Should().Be(1);
        order[3].Should().Be(2, "only placeable items are numbered, contiguously");
        order[2].Should().Be(0, "non-placeable items are never Stops");
    }

    [Fact]
    public async Task HasOrder_IsFalseBeforeSeed_AndTrueAfter()
    {
        var factory = await SeedAsync(
            (1, new DateTime(2025, 1, 1), true),
            (2, new DateTime(2025, 1, 2), true));
        var service = CreateService(factory);

        (await service.HasOrderAsync(CollectionId)).Should().BeFalse();

        await service.SeedOrderAsync(CollectionId);

        (await service.HasOrderAsync(CollectionId)).Should().BeTrue();
    }

    [Fact]
    public async Task Append_PutsNewPlaceablePoi_AtMaxPlusOne()
    {
        var factory = await SeedAsync(
            (1, new DateTime(2025, 1, 1), true),
            (2, new DateTime(2025, 1, 2), true));
        var service = CreateService(factory);
        await service.SeedOrderAsync(CollectionId);

        // New POI added to the collection (OrderIndex 0 by default).
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.Add(new Poi { Id = 3, Name = "P3", Latitude = 50, Longitude = 20, AddedDate = new DateTime(2024, 1, 1) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 3, PoiCollectionId = CollectionId });
            await db.SaveChangesAsync();
        }

        await service.AppendStopAsync(CollectionId, 3);

        var order = await ReadOrderAsync(factory);
        order[3].Should().Be(3, "appended as last Stop regardless of its earlier AddedDate");
    }

    [Fact]
    public async Task Append_IsNoOp_ForNonPlaceablePoi()
    {
        var factory = await SeedAsync((1, new DateTime(2025, 1, 1), true), (2, new DateTime(2025, 1, 2), true));
        var service = CreateService(factory);
        await service.SeedOrderAsync(CollectionId);

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.Add(new Poi { Id = 3, Name = "P3", Latitude = null, Longitude = null, AddedDate = new DateTime(2025, 1, 3) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 3, PoiCollectionId = CollectionId });
            await db.SaveChangesAsync();
        }

        await service.AppendStopAsync(CollectionId, 3);

        var order = await ReadOrderAsync(factory);
        order[3].Should().Be(0);
    }

    [Fact]
    public async Task Compact_RemovesGap_AfterRemoval_PreservingRelativeOrder()
    {
        var factory = await SeedAsync(
            (1, new DateTime(2025, 1, 1), true),
            (2, new DateTime(2025, 1, 2), true),
            (3, new DateTime(2025, 1, 3), true));
        var service = CreateService(factory);
        await service.SeedOrderAsync(CollectionId); // 1->1, 2->2, 3->3

        // Remove the middle Stop, leaving orders {1:1, 3:3} — a gap at 2.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var item = await db.PoiCollectionItems.FirstAsync(ci => ci.PoiId == 2 && ci.PoiCollectionId == CollectionId);
            db.PoiCollectionItems.Remove(item);
            await db.SaveChangesAsync();
        }

        await service.CompactOrderAsync(CollectionId);

        var order = await ReadOrderAsync(factory);
        order.Should().HaveCount(2);
        order[1].Should().Be(1);
        order[3].Should().Be(2, "remaining stops re-compact to contiguous 1..N");
        order.Values.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Reconcile_AppendsNewPlaceable_AndCompactsRemoval_InOnePass()
    {
        var factory = await SeedAsync(
            (1, new DateTime(2025, 1, 1), true),
            (2, new DateTime(2025, 1, 2), true),
            (3, new DateTime(2025, 1, 3), true));
        var service = CreateService(factory);
        await service.SeedOrderAsync(CollectionId);

        // Remove Stop 2 (gap) AND add a new placeable POI 4 (OrderIndex 0).
        await using (var db = await factory.CreateDbContextAsync())
        {
            var item = await db.PoiCollectionItems.FirstAsync(ci => ci.PoiId == 2 && ci.PoiCollectionId == CollectionId);
            db.PoiCollectionItems.Remove(item);
            db.Pois.Add(new Poi { Id = 4, Name = "P4", Latitude = 50, Longitude = 20, AddedDate = new DateTime(2025, 1, 9) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 4, PoiCollectionId = CollectionId });
            await db.SaveChangesAsync();
        }

        await service.ReconcileOrderAsync(CollectionId);

        var order = await ReadOrderAsync(factory);
        order[1].Should().Be(1);
        order[3].Should().Be(2, "surviving stops keep relative order, gap closed");
        order[4].Should().Be(3, "newly-added placeable POI appended as the last stop");
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    // === Review [Patch] TRIP-STARTFINISH-07: reconcile releases orphaned pins ===

    [Fact]
    public async Task Reconcile_ReleasesFinishPin_WhenFinishPoiBecomesUnplaceable()
    {
        var (factory, service) = await SeededFourAsync();
        await service.SetFinishAsync(CollectionId, 4); // Finish pinned at Order N=4

        // The Finish POI loses its coordinates — it is now Unplaceable and no
        // longer a routing Stop. (A real enrichment/edit clearing lat/lon.)
        await using (var db = await factory.CreateDbContextAsync())
        {
            var poi = await db.Pois.FirstAsync(p => p.Id == 4);
            poi.Latitude = null;
            poi.Longitude = null;
            await db.SaveChangesAsync();
        }

        await service.ReconcileOrderAsync(CollectionId);

        (await ReadPinsAsync(factory)).Finish.Should().BeNull(
            "an orphaned Finish pin (POI no longer placeable) is released so IsRoundtrip and the drawn closing leg cannot disagree");
        var order = await ReadOrderAsync(factory);
        order[1].Should().Be(1);
        order[2].Should().Be(2);
        order[3].Should().Be(3);
        order[4].Should().Be(0, "the now-unplaceable POI holds OrderIndex 0 (not a stop)");
    }

    [Fact]
    public async Task Reconcile_ReleasesStartPin_WhenStartPoiRemovedFromCollection()
    {
        var (factory, service) = await SeededFourAsync();
        await service.SetStartAsync(CollectionId, 1); // Start pinned at Order 1

        // The Start POI is removed from THIS collection (the PoiCollectionItem is
        // deleted; the Poi row survives, so the FK SetNull on Poi delete never
        // fires — exactly the gap this patch closes).
        await using (var db = await factory.CreateDbContextAsync())
        {
            var item = await db.PoiCollectionItems.FirstAsync(ci => ci.PoiId == 1 && ci.PoiCollectionId == CollectionId);
            db.PoiCollectionItems.Remove(item);
            await db.SaveChangesAsync();
        }

        await service.ReconcileOrderAsync(CollectionId);

        (await ReadPinsAsync(factory)).Start.Should().BeNull(
            "a Start pin whose POI left the collection is released");
        var order = await ReadOrderAsync(factory);
        order.Values.Where(v => v > 0).Should().BeEquivalentTo(new[] { 1, 2, 3 },
            "surviving placeable stops renumber contiguous 1..N");
    }

    [Fact]
    public async Task Reconcile_KeepsSurvivingFinishAtLastSlot_AfterAppend()
    {
        var (factory, service) = await SeededFourAsync();
        await service.SetFinishAsync(CollectionId, 4); // Finish at Order 4

        // A new placeable POI is added while Finish is pinned.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.Add(new Poi { Id = 5, Name = "P5", Latitude = 50, Longitude = 20, AddedDate = new DateTime(2025, 1, 9) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 5, PoiCollectionId = CollectionId });
            await db.SaveChangesAsync();
        }

        await service.ReconcileOrderAsync(CollectionId);

        (await ReadPinsAsync(factory)).Finish.Should().Be(4, "the Finish pin is still valid and retained");
        var order = await ReadOrderAsync(factory);
        order[5].Should().Be(4, "the appended stop lands in the interior, NOT past the pinned Finish");
        order[4].Should().Be(5, "the pinned Finish stays in the last slot (Order N) after an append");
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
    }

    // === Story 1.5: ReorderStopAsync (single writer, pin-aware, no-op safe) ===

    // Wraps the InMemory factory and counts SaveChanges commits so the no-op
    // short-circuit ("no redundant DB write") is directly observable.
    private sealed class CountingDbContextFactory(IDbContextFactory<AppDbContext> inner) : IDbContextFactory<AppDbContext>
    {
        private int _saveCount;

        public int SaveCount => Volatile.Read(ref _saveCount);

        public AppDbContext CreateDbContext()
        {
            var db = inner.CreateDbContext();
            db.SavedChanges += (_, _) => Interlocked.Increment(ref _saveCount);
            return db;
        }
    }

    private static async Task SetPinsAsync(IDbContextFactory<AppDbContext> factory, int? startPoiId, int? finishPoiId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var collection = await db.PoiCollections.FirstAsync(c => c.Id == CollectionId);
        collection.StartPoiId = startPoiId;
        collection.FinishPoiId = finishPoiId;
        await db.SaveChangesAsync();
    }

    private static async Task<(int? Start, int? Finish)> ReadPinsAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var c = await db.PoiCollections.FirstAsync(x => x.Id == CollectionId);
        return (c.StartPoiId, c.FinishPoiId);
    }

    // Seeds 4 placeable POIs (ids 1..4, AddedDate ascending) and seeds the
    // order so 1→1, 2→2, 3→3, 4→4.
    private static async Task<(IDbContextFactory<AppDbContext> Factory, TripOrderingService Service)> SeededFourAsync()
    {
        var factory = await SeedAsync(
            (1, new DateTime(2025, 1, 1), true),
            (2, new DateTime(2025, 1, 2), true),
            (3, new DateTime(2025, 1, 3), true),
            (4, new DateTime(2025, 1, 4), true));
        var service = CreateService(factory);
        await service.SeedOrderAsync(CollectionId);
        return (factory, service);
    }

    [Fact]
    public async Task Reorder_MovesStopForward_AndRenumbersContiguously()
    {
        var (factory, service) = await SeededFourAsync();

        await service.ReorderStopAsync(CollectionId, 1, 3);

        var order = await ReadOrderAsync(factory);
        order[2].Should().Be(1);
        order[3].Should().Be(2);
        order[1].Should().Be(3, "the dragged stop lands exactly on the target slot");
        order[4].Should().Be(4);
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3, 4 }, "1-based, contiguous, gap-free, unique");
    }

    [Fact]
    public async Task Reorder_MovesStopBackward_AndRenumbersContiguously()
    {
        var (factory, service) = await SeededFourAsync();

        await service.ReorderStopAsync(CollectionId, 4, 2);

        var order = await ReadOrderAsync(factory);
        order[1].Should().Be(1);
        order[4].Should().Be(2);
        order[2].Should().Be(3);
        order[3].Should().Be(4);
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task Reorder_OneStepMoves_AreExactlyOnePosition()
    {
        var (factory, service) = await SeededFourAsync();

        await service.ReorderStopAsync(CollectionId, 2, 3); // move-down
        var order = await ReadOrderAsync(factory);
        order[2].Should().Be(3);
        order[3].Should().Be(2);

        await service.ReorderStopAsync(CollectionId, 2, 2); // move-up back
        order = await ReadOrderAsync(factory);
        order[2].Should().Be(2);
        order[3].Should().Be(3);
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task Reorder_ClampsOutOfRangeTargets()
    {
        var (factory, service) = await SeededFourAsync();

        await service.ReorderStopAsync(CollectionId, 2, 99);
        var order = await ReadOrderAsync(factory);
        order[2].Should().Be(4, "an over-range target clamps to N");

        await service.ReorderStopAsync(CollectionId, 2, -5);
        order = await ReadOrderAsync(factory);
        order[2].Should().Be(1, "an under-range target clamps to 1");
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task Reorder_PinnedStartOnly_ClampsIntoInteriorWindow_AndStartStaysAtOne()
    {
        var (factory, service) = await SeededFourAsync();
        await SetPinsAsync(factory, startPoiId: 1, finishPoiId: null);

        // Drop into the pinned first slot clamps to the nearest interior slot (2).
        await service.ReorderStopAsync(CollectionId, 3, 1);

        var order = await ReadOrderAsync(factory);
        order[1].Should().Be(1, "the pinned Start never leaves Order 1");
        order[3].Should().Be(2, "the drop clamps into the movable window [2..N]");
        order[2].Should().Be(3);
        order[4].Should().Be(4);
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });

        // The last slot is open (no Finish pin): an interior stop may take it.
        await service.ReorderStopAsync(CollectionId, 2, 4);
        order = await ReadOrderAsync(factory);
        order[2].Should().Be(4);
        order[1].Should().Be(1);
    }

    [Fact]
    public async Task Reorder_PinnedFinishOnly_ClampsIntoInteriorWindow_AndFinishStaysAtN()
    {
        var (factory, service) = await SeededFourAsync();
        await SetPinsAsync(factory, startPoiId: null, finishPoiId: 4);

        // Drop into the pinned last slot clamps to the nearest interior slot (N-1).
        await service.ReorderStopAsync(CollectionId, 2, 4);

        var order = await ReadOrderAsync(factory);
        order[4].Should().Be(4, "the pinned Finish never leaves Order N");
        order[2].Should().Be(3, "the drop clamps into the movable window [1..N-1]");
        order[1].Should().Be(1);
        order[3].Should().Be(2);
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });

        // The first slot is open (no Start pin): an interior stop may take it.
        await service.ReorderStopAsync(CollectionId, 3, 1);
        order = await ReadOrderAsync(factory);
        order[3].Should().Be(1);
        order[4].Should().Be(4);
    }

    [Fact]
    public async Task Reorder_BothPinned_MovesInteriorOnly_PinsKeepOneAndN()
    {
        var (factory, service) = await SeededFourAsync();
        await SetPinsAsync(factory, startPoiId: 1, finishPoiId: 4);

        await service.ReorderStopAsync(CollectionId, 2, 99); // clamps to N-1 = 3
        await service.ReorderStopAsync(CollectionId, 3, 1);  // clamps to 2

        var order = await ReadOrderAsync(factory);
        order[1].Should().Be(1);
        order[4].Should().Be(4);
        order[3].Should().Be(2);
        order[2].Should().Be(3);
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task Reorder_MovingThePinnedStartOrFinish_IsNoOp()
    {
        var (factory, service) = await SeededFourAsync();
        await SetPinsAsync(factory, startPoiId: 1, finishPoiId: 4);

        await service.ReorderStopAsync(CollectionId, 1, 3);
        await service.ReorderStopAsync(CollectionId, 4, 2);

        var order = await ReadOrderAsync(factory);
        order[1].Should().Be(1);
        order[2].Should().Be(2);
        order[3].Should().Be(3);
        order[4].Should().Be(4);
    }

    [Fact]
    public async Task Reorder_NeverChangesStartFinishDesignation()
    {
        var (factory, service) = await SeededFourAsync();
        await SetPinsAsync(factory, startPoiId: 1, finishPoiId: 4);

        // A drop into the first/last slot clamps — it must NOT transfer the role.
        await service.ReorderStopAsync(CollectionId, 2, 1);
        await service.ReorderStopAsync(CollectionId, 3, 4);

        (await ReadPinsAsync(factory)).Should().Be(((int?)1, (int?)4),
            "reorder never rewrites StartPoiId/FinishPoiId (Story 1.7 owns designation)");
    }

    [Fact]
    public async Task Reorder_NoOpTarget_ShortCircuits_WithoutWriting()
    {
        var (factory, service0) = await SeededFourAsync();
        _ = service0;
        var counting = new CountingDbContextFactory(factory);
        var service = CreateService(counting);

        // Own position, clamped-back-onto-own-position, and unknown POI: none writes.
        await service.ReorderStopAsync(CollectionId, 2, 2);
        await service.ReorderStopAsync(CollectionId, 1, -3); // clamps to 1 == current
        await service.ReorderStopAsync(CollectionId, 999, 2); // not a stop

        counting.SaveCount.Should().Be(0, "a no-op reorder must not run SaveChangesAsync");
        var order = await ReadOrderAsync(factory);
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3, 4 }, "order stays contiguous and untouched");
    }

    [Fact]
    public async Task Reorder_IsNoOp_ForNonPlaceableOrUnorderedPoi()
    {
        var factory = await SeedAsync(
            (1, new DateTime(2025, 1, 1), true),
            (2, new DateTime(2025, 1, 2), true),
            (3, new DateTime(2025, 1, 3), false));
        var service = CreateService(factory);
        await service.SeedOrderAsync(CollectionId);

        await service.ReorderStopAsync(CollectionId, 3, 1);

        var order = await ReadOrderAsync(factory);
        order[3].Should().Be(0, "a non-placeable item is not a Stop and cannot be reordered");
        order[1].Should().Be(1);
        order[2].Should().Be(2);
    }

    [Fact]
    public async Task Reorder_ManualMove_OverridesPriorOrder_AndPersists()
    {
        var (factory, service) = await SeededFourAsync();

        // First "assisted" arrangement, then a manual move overrides it.
        await service.ReorderStopAsync(CollectionId, 4, 1);
        await service.ReorderStopAsync(CollectionId, 1, 4);

        var order = await ReadOrderAsync(factory);
        order[4].Should().Be(1, "the earlier move persists");
        order[1].Should().Be(4, "the later manual move overrides and persists");
        order.Values.Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
    }

    // === Story 1.7: Start/Finish designation (TRIP-STARTFINISH-02) ===

    // Asserts the canonical pin invariant: the placeable OrderIndex set is
    // exactly {1..N} (contiguous, gap-free, unique — no stop holds two values)
    // with the pinned Start at 1 and the pinned Finish at N.
    private static async Task AssertPinInvariantAsync(
        IDbContextFactory<AppDbContext> factory, int n, int? expectedStart, int? expectedFinish)
    {
        var order = await ReadOrderAsync(factory);
        order.Values.Should().BeEquivalentTo(Enumerable.Range(1, n),
            "the placeable OrderIndex set must stay contiguous, gap-free and unique 1..N");
        var pins = await ReadPinsAsync(factory);
        pins.Should().Be((expectedStart, expectedFinish));
        if (expectedStart is { } s)
        {
            order[s].Should().Be(1, "the pinned Start holds Order 1");
        }
        if (expectedFinish is { } f)
        {
            order[f].Should().Be(n, "the pinned Finish holds Order N");
        }
    }

    [Fact]
    public async Task SetStart_PinsToOrderOne_AndRenumbersContiguously()
    {
        var (factory, service) = await SeededFourAsync();

        await service.SetStartAsync(CollectionId, 3);

        var order = await ReadOrderAsync(factory);
        order[3].Should().Be(1, "the designated Start is pinned to Order 1");
        order[1].Should().Be(2);
        order[2].Should().Be(3);
        order[4].Should().Be(4, "interior stops keep their relative order");
        await AssertPinInvariantAsync(factory, 4, expectedStart: 3, expectedFinish: null);
    }

    [Fact]
    public async Task SetFinish_PinsToOrderN_AndRenumbersContiguously()
    {
        var (factory, service) = await SeededFourAsync();

        await service.SetFinishAsync(CollectionId, 2);

        var order = await ReadOrderAsync(factory);
        order[2].Should().Be(4, "the designated Finish is pinned to Order N");
        order[1].Should().Be(1);
        order[3].Should().Be(2);
        order[4].Should().Be(3);
        await AssertPinInvariantAsync(factory, 4, expectedStart: null, expectedFinish: 2);
    }

    [Fact]
    public async Task SetStart_Redesignation_ReleasesOldPin_NoGapNoDuplicate()
    {
        var (factory, service) = await SeededFourAsync();

        await service.SetStartAsync(CollectionId, 3);
        await service.SetStartAsync(CollectionId, 2);

        var order = await ReadOrderAsync(factory);
        order[2].Should().Be(1, "the new Start takes Order 1");
        order[3].Should().Be(2, "the released old Start becomes an interior stop without a gap");
        await AssertPinInvariantAsync(factory, 4, expectedStart: 2, expectedFinish: null);
    }

    [Fact]
    public async Task SetFinish_Redesignation_ReleasesOldPin_NoGapNoDuplicate()
    {
        var (factory, service) = await SeededFourAsync();

        await service.SetFinishAsync(CollectionId, 1);
        await service.SetFinishAsync(CollectionId, 2);

        var order = await ReadOrderAsync(factory);
        order[2].Should().Be(4);
        order[1].Should().Be(3, "the released old Finish becomes an interior stop");
        await AssertPinInvariantAsync(factory, 4, expectedStart: null, expectedFinish: 2);
    }

    [Fact]
    public async Task SetFinish_EqualToCurrentStart_IsRejected_OrderUntouched()
    {
        var (factory, service) = await SeededFourAsync();
        await service.SetStartAsync(CollectionId, 2);

        var act = () => service.SetFinishAsync(CollectionId, 2);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a stop cannot be both Start and Finish");
        await AssertPinInvariantAsync(factory, 4, expectedStart: 2, expectedFinish: null);
    }

    [Fact]
    public async Task SetStart_EqualToCurrentFinish_IsRejected_OrderUntouched()
    {
        var (factory, service) = await SeededFourAsync();
        await service.SetFinishAsync(CollectionId, 3);

        var act = () => service.SetStartAsync(CollectionId, 3);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await AssertPinInvariantAsync(factory, 4, expectedStart: null, expectedFinish: 3);
    }

    [Fact]
    public async Task ClearFinish_RemovesPin_OrderStaysContiguous()
    {
        var (factory, service) = await SeededFourAsync();
        await service.SetFinishAsync(CollectionId, 1); // 2,3,4,1

        await service.ClearFinishAsync(CollectionId);

        var order = await ReadOrderAsync(factory);
        order[1].Should().Be(4, "clearing a pin never reshuffles the order");
        await AssertPinInvariantAsync(factory, 4, expectedStart: null, expectedFinish: null);
    }

    [Fact]
    public async Task ClearStart_RemovesPin_OrderStaysContiguous()
    {
        var (factory, service) = await SeededFourAsync();
        await service.SetStartAsync(CollectionId, 4); // 4,1,2,3

        await service.ClearStartAsync(CollectionId);

        var order = await ReadOrderAsync(factory);
        order[4].Should().Be(1, "the former Start keeps its slot, just unpinned");
        await AssertPinInvariantAsync(factory, 4, expectedStart: null, expectedFinish: null);
    }

    [Fact]
    public async Task SetStart_IsNoOp_ForUnplaceableOrUnknownPoi()
    {
        var factory = await SeedAsync(
            (1, new DateTime(2025, 1, 1), true),
            (2, new DateTime(2025, 1, 2), true),
            (3, new DateTime(2025, 1, 3), false));
        var service = CreateService(factory);
        await service.SeedOrderAsync(CollectionId);

        // Unplaceable stops hold OrderIndex 0 and are excluded from routing —
        // never Start/Finish candidates. Unknown POIs are equally ignored.
        await service.SetStartAsync(CollectionId, 3);
        await service.SetFinishAsync(CollectionId, 3);
        await service.SetStartAsync(CollectionId, 999);

        (await ReadPinsAsync(factory)).Should().Be(((int?)null, (int?)null));
        var order = await ReadOrderAsync(factory);
        order[3].Should().Be(0);
        order[1].Should().Be(1);
        order[2].Should().Be(2);
    }

    [Fact]
    public async Task SetStart_SameStopTwice_IsIdempotentNoOp()
    {
        var (factory, service0) = await SeededFourAsync();
        await service0.SetStartAsync(CollectionId, 2);

        var counting = new CountingDbContextFactory(factory);
        var service = CreateService(counting);
        await service.SetStartAsync(CollectionId, 2);

        counting.SaveCount.Should().Be(0, "re-designating the current Start writes nothing");
        await AssertPinInvariantAsync(factory, 4, expectedStart: 2, expectedFinish: null);
    }

    [Fact]
    public async Task SetStartAndFinish_Together_PinBothEndpoints()
    {
        var (factory, service) = await SeededFourAsync();

        await service.SetStartAsync(CollectionId, 4);
        await service.SetFinishAsync(CollectionId, 1);

        var order = await ReadOrderAsync(factory);
        order[4].Should().Be(1);
        order[1].Should().Be(4);
        order[2].Should().Be(2);
        order[3].Should().Be(3);
        await AssertPinInvariantAsync(factory, 4, expectedStart: 4, expectedFinish: 1);
    }

    [Fact]
    public async Task Pins_SurviveReorder_AndReorderRespectsThem()
    {
        // Cross-story guard: designating via 1.7 then reordering via 1.5 keeps
        // both pins in their slots and the order contiguous.
        var (factory, service) = await SeededFourAsync();
        await service.SetStartAsync(CollectionId, 2);
        await service.SetFinishAsync(CollectionId, 3);

        await service.ReorderStopAsync(CollectionId, 1, 99); // clamps into interior
        await service.ReorderStopAsync(CollectionId, 2, 4);  // pinned Start: no-op

        await AssertPinInvariantAsync(factory, 4, expectedStart: 2, expectedFinish: 3);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public async Task PinPermutations_NeverProduceGapOrDuplicate(int n)
    {
        // Property-style sweep: every distinct (start, finish) pair, set in both
        // orders, then cleared — the placeable OrderIndex set must be exactly
        // {1..N} after every operation and no stop may ever hold two values.
        var members = Enumerable.Range(1, n)
            .Select(i => (PoiId: i, Added: new DateTime(2025, 1, 1).AddDays(i), Placeable: true))
            .ToArray();
        var factory = await SeedAsync(members);
        var service = CreateService(factory);
        await service.SeedOrderAsync(CollectionId);

        for (var s = 1; s <= n; s++)
        {
            // Release any prior Finish first so the new Start is never the
            // pinned Finish (that designation is rejected by design).
            await service.ClearFinishAsync(CollectionId);
            await AssertPinInvariantAsync(factory, n, expectedStart: await CurrentStartAsync(factory), expectedFinish: null);
            await service.SetStartAsync(CollectionId, s);
            await AssertPinInvariantAsync(factory, n, expectedStart: s, expectedFinish: null);

            for (var f = 1; f <= n; f++)
            {
                if (s == f)
                {
                    continue;
                }

                await service.SetFinishAsync(CollectionId, f);
                await AssertPinInvariantAsync(factory, n, expectedStart: s, expectedFinish: f);
            }
        }

        await service.ClearStartAsync(CollectionId);
        await AssertPinInvariantAsync(factory, n, expectedStart: null, expectedFinish: await CurrentFinishAsync(factory));
        await service.ClearFinishAsync(CollectionId);
        await AssertPinInvariantAsync(factory, n, expectedStart: null, expectedFinish: null);
    }

    private static async Task<int?> CurrentStartAsync(IDbContextFactory<AppDbContext> factory) =>
        (await ReadPinsAsync(factory)).Start;

    private static async Task<int?> CurrentFinishAsync(IDbContextFactory<AppDbContext> factory) =>
        (await ReadPinsAsync(factory)).Finish;

    [Fact]
    public async Task GetStopOrder_ReturnsOnlyOrderedPlaceableItems()
    {
        var factory = await SeedAsync(
            (1, new DateTime(2025, 1, 1), true),
            (2, new DateTime(2025, 1, 2), false));
        var service = CreateService(factory);
        await service.SeedOrderAsync(CollectionId);

        var stops = await service.GetStopOrderAsync(CollectionId);

        stops.Should().ContainKey(1).WhoseValue.Should().Be(1);
        stops.Should().NotContainKey(2, "unordered (0) items are excluded");
    }

    // === Story 1.4 (AC1): sole-writer invariant — only the ordering service's
    // SetOrderAsync-routed methods mutate OrderIndex. A sibling write on the same
    // membership (SetDwellMinutesAsync, which saves under the shared write lock)
    // must NOT touch OrderIndex. This anchors "no other code path writes OrderIndex"
    // for the canonical-order-shared-across-views story. ===

    // === Story 3.3: successor-changed leg-mode reset in the sole writer ===

    private static async Task SetModesAsync(
        IDbContextFactory<AppDbContext> factory, params (int PoiId, string? Mode)[] modes)
    {
        await using var db = await factory.CreateDbContextAsync();
        foreach (var (poiId, mode) in modes)
        {
            var item = await db.PoiCollectionItems.FirstAsync(
                ci => ci.PoiId == poiId && ci.PoiCollectionId == CollectionId);
            item.OutgoingTravelMode = mode;
        }
        await db.SaveChangesAsync();
    }

    private static async Task<Dictionary<int, string?>> ReadModesAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == CollectionId)
            .ToDictionaryAsync(ci => ci.PoiId, ci => ci.OutgoingTravelMode);
    }

    [Fact]
    public async Task Reorder_PreservesMode_ForStopWhoseSuccessorIsUnchanged()
    {
        // Roundtrip 1->2->3->4->(1). Move stop 2 to slot 4: new order 1,3,4,2->(1).
        // New successors: 1→3 (changed from 2), 3→4 (UNCHANGED), 4→2 (changed from 1),
        // 2→1 (closing, changed from 3). So stop 3's mode survives; 1, 4, 2 reset.
        var (factory, service) = await SeededFourAsync();
        await SetModesAsync(factory,
            (1, TravelMode.Drive), (2, TravelMode.Walk), (3, TravelMode.Cycle), (4, TravelMode.Drive));

        await service.ReorderStopAsync(CollectionId, 2, 4);

        var order = await ReadOrderAsync(factory);
        order[1].Should().Be(1);
        order[3].Should().Be(2);
        order[4].Should().Be(3);
        order[2].Should().Be(4);

        var modes = await ReadModesAsync(factory);
        modes[3].Should().Be(TravelMode.Cycle, "stop 3's successor (4) is unchanged ⇒ mode preserved");
        modes[1].Should().BeNull("stop 1's successor changed 2→3");
        modes[4].Should().BeNull("stop 4's successor changed 1→2");
        modes[2].Should().BeNull("stop 2's closing successor changed 3→1");
    }

    [Fact]
    public async Task Reorder_RotationOfRoundtrip_PreservesAllModes_NoSuccessorChanged()
    {
        // A pure rotation of a roundtrip cycle changes NO stop's successor:
        // 1->2->3->4->(1) rotated to 2->3->4->1->(2) keeps every (from→to) pair.
        // Every mode must therefore survive (the leg set is identical).
        var (factory, service) = await SeededFourAsync();
        await SetModesAsync(factory,
            (1, TravelMode.Drive), (2, TravelMode.Walk), (3, TravelMode.Cycle), (4, TravelMode.Drive));

        await service.ReorderStopAsync(CollectionId, 1, 4); // order becomes 2,3,4,1

        var modes = await ReadModesAsync(factory);
        modes[1].Should().Be(TravelMode.Drive, "1→2 leg unchanged");
        modes[2].Should().Be(TravelMode.Walk, "2→3 leg unchanged");
        modes[3].Should().Be(TravelMode.Cycle, "3→4 leg unchanged");
        modes[4].Should().Be(TravelMode.Drive, "4→1 closing leg unchanged on the rotated roundtrip");
    }

    [Fact]
    public async Task Reorder_NullsMode_ForStopWhoseSuccessorChanged()
    {
        // Roundtrip 1->2->3->4. Swap 2 and 3 (move 2 to slot 3): 1->3->2->4->(1).
        // Stop 1's successor changed 2->3 (reset); stop 3 now precedes 2 (reset);
        // stop 2 now precedes 4 (reset); stop 4 still closes back to 1 (UNCHANGED).
        var (factory, service) = await SeededFourAsync();
        await SetModesAsync(factory,
            (1, TravelMode.Drive), (2, TravelMode.Walk), (3, TravelMode.Cycle), (4, TravelMode.Drive));

        await service.ReorderStopAsync(CollectionId, 2, 3);

        var order = await ReadOrderAsync(factory);
        order[1].Should().Be(1);
        order[3].Should().Be(2);
        order[2].Should().Be(3);
        order[4].Should().Be(4);

        var modes = await ReadModesAsync(factory);
        modes[1].Should().BeNull("stop 1's successor changed 2→3");
        modes[3].Should().BeNull("stop 3's successor changed 4→2");
        modes[2].Should().BeNull("stop 2's successor changed 3→4");
        modes[4].Should().Be(TravelMode.Drive, "stop 4 still closes back to 1 (roundtrip) ⇒ unchanged ⇒ preserved");
    }

    [Fact]
    public async Task Append_NullsMode_ForNewlyAppearedLeg_AndForNewPredecessor()
    {
        // Roundtrip over {1,2}. Mode on both. Append stop 3 at the end:
        // 1->2->3->(1). Stop 2's successor changed (1 → 3) ⇒ reset. Stop 3 is brand
        // new (no old successor; now closes back to 1) ⇒ null. Stop 1's successor
        // (2) is unchanged ⇒ preserved.
        var factory = await SeedAsync(
            (1, new DateTime(2025, 1, 1), true),
            (2, new DateTime(2025, 1, 2), true));
        var service = CreateService(factory);
        await service.SeedOrderAsync(CollectionId);
        await SetModesAsync(factory, (1, TravelMode.Drive), (2, TravelMode.Walk));

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.Add(new Poi { Id = 3, Name = "P3", Latitude = 52, Longitude = 22, AddedDate = new DateTime(2025, 1, 3) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 3, PoiCollectionId = CollectionId });
            await db.SaveChangesAsync();
        }

        await service.AppendStopAsync(CollectionId, 3);

        var modes = await ReadModesAsync(factory);
        modes[1].Should().Be(TravelMode.Drive, "stop 1's successor (2) is unchanged");
        modes[2].Should().BeNull("stop 2's successor changed from 1 (closing) to 3 (appended)");
        modes[3].Should().BeNull("a newly-appeared leg defaults to null (≡ AnyAir)");
    }

    [Fact]
    public async Task Reorder_OpenPath_LastStopHasNoSuccessor_AndIsNotResetSpuriously()
    {
        // Open path with a distinct pinned Finish=4: legs are 1->2->3->4, NO closing
        // leg. Stop 4 has NO successor (last on an open path). Move stop 1 to slot 3
        // (within the movable window): 2->3->1->4. Stop 4 STILL has no successor and
        // its mode must NOT be reset spuriously; stop 3's successor was 4 then 1 ⇒
        // reset, etc.
        var (factory, service) = await SeededFourAsync();
        await service.SetFinishAsync(CollectionId, 4); // open path, Finish at N=4
        await SetModesAsync(factory,
            (1, TravelMode.Drive), (2, TravelMode.Walk), (3, TravelMode.Cycle), (4, TravelMode.Drive));

        // Sanity: stop 4 carries a mode even though it has no successor (no leg).
        await service.ReorderStopAsync(CollectionId, 1, 3); // 2,3,1 interior, 4 stays last

        var order = await ReadOrderAsync(factory);
        order[4].Should().Be(4, "the pinned Finish stays at Order N on the open path");

        var modes = await ReadModesAsync(factory);
        modes[4].Should().Be(TravelMode.Drive,
            "the open-path last stop has no successor (no leg) ⇒ never reset spuriously");
    }

    [Fact]
    public async Task Reorder_CommitsOrderIndexAndModeReset_Atomically()
    {
        // A single reorder must persist BOTH the new OrderIndex AND the mode reset
        // in one commit (one SaveChanges), observable via a fresh read.
        var (factory0, _) = await SeededFourAsync();
        await SetModesAsync(factory0,
            (1, TravelMode.Drive), (2, TravelMode.Walk), (3, TravelMode.Cycle), (4, TravelMode.Drive));

        var counting = new CountingDbContextFactory(factory0);
        var service = CreateService(counting);

        await service.ReorderStopAsync(CollectionId, 2, 3); // 1,3,2,4

        counting.SaveCount.Should().Be(1, "OrderIndex change and mode reset commit in ONE SaveChanges");

        var order = await ReadOrderAsync(factory0);
        order[2].Should().Be(3);
        order[3].Should().Be(2);
        var modes = await ReadModesAsync(factory0);
        modes[1].Should().BeNull("the mode reset landed in the same commit as the order change");
    }

    [Fact]
    public async Task Reorder_NoOp_DoesNotResetAnyMode()
    {
        // A clamped-onto-own-position reorder writes nothing — and therefore must
        // not null any mode (the early-return is preserved when nothing changed).
        var (factory0, _) = await SeededFourAsync();
        await SetModesAsync(factory0,
            (1, TravelMode.Drive), (2, TravelMode.Walk), (3, TravelMode.Cycle), (4, TravelMode.Drive));

        var counting = new CountingDbContextFactory(factory0);
        var service = CreateService(counting);

        await service.ReorderStopAsync(CollectionId, 2, 2); // no-op

        counting.SaveCount.Should().Be(0, "a no-op reorder writes nothing");
        var modes = await ReadModesAsync(factory0);
        modes[1].Should().Be(TravelMode.Drive);
        modes[2].Should().Be(TravelMode.Walk);
        modes[3].Should().Be(TravelMode.Cycle);
        modes[4].Should().Be(TravelMode.Drive);
    }

    [Fact]
    public async Task ClearFinish_ResurrectsClosingLeg_ResetsNewLastStopMode()
    {
        // H1 (AC1): flipping open path → roundtrip via ClearFinish appears a brand-new
        // closing leg (last→first) WITHOUT any OrderIndex change. The new closing-leg
        // From-stop must be reset to "Any" (null) — a stale mode must not show on a
        // newly-appeared leg (UX-DR11). Stops whose successor didn't flip keep theirs.
        var (factory, service) = await SeededFourAsync();
        await service.SetFinishAsync(CollectionId, 4); // open path 1→2→3→4 (4 = Finish, no closing leg)
        await SetModesAsync(factory,
            (1, TravelMode.Drive), (2, TravelMode.Walk), (3, TravelMode.Cycle), (4, TravelMode.Drive));

        await service.ClearFinishAsync(CollectionId); // → roundtrip: closing leg 4→1 appears

        (await CurrentFinishAsync(factory)).Should().BeNull("Finish was cleared ⇒ roundtrip");
        var modes = await ReadModesAsync(factory);
        modes[4].Should().BeNull("stop 4 gained the new closing leg 4→1 ⇒ reset to Any (H1)");
        modes[1].Should().Be(TravelMode.Drive, "1's successor (2) is unchanged across the flip");
        modes[2].Should().Be(TravelMode.Walk, "2's successor (3) is unchanged");
        modes[3].Should().Be(TravelMode.Cycle, "3's successor (4) is unchanged");
    }

    [Fact]
    public async Task SetFinish_VanishesClosingLeg_ResetsExLastStopMode()
    {
        // The inverse flip: roundtrip → open path via SetFinish on the last stop removes
        // the closing leg (last→first). The ex-last stop's outgoing leg vanishes; its
        // mode is reset so a later ClearFinish can't resurface a stale value.
        var (factory, service) = await SeededFourAsync(); // roundtrip 1→2→3→4→(1)
        await SetModesAsync(factory,
            (1, TravelMode.Drive), (2, TravelMode.Walk), (3, TravelMode.Cycle), (4, TravelMode.Drive));

        await service.SetFinishAsync(CollectionId, 4); // 4 already last ⇒ open path, closing leg 4→1 vanishes

        (await CurrentFinishAsync(factory)).Should().Be(4);
        var modes = await ReadModesAsync(factory);
        modes[4].Should().BeNull("stop 4 lost its closing leg (now the open-path Finish) ⇒ reset");
        modes[1].Should().Be(TravelMode.Drive, "interior successors unchanged across the flip");
        modes[2].Should().Be(TravelMode.Walk);
        modes[3].Should().Be(TravelMode.Cycle);
    }

    [Fact]
    public async Task SetDwellMinutes_DoesNotMutateOrderIndex_SoleWriterInvariant()
    {
        var (factory, service) = await SeededFourAsync(); // 1→1, 2→2, 3→3, 4→4
        var before = await ReadOrderAsync(factory);

        // A non-ordering mutation through the ordering service: dwell only.
        await service.SetDwellMinutesAsync(CollectionId, poiId: 2, minutes: 30);

        var after = await ReadOrderAsync(factory);
        after.Should().Equal(before, "writing dwell must never change any OrderIndex (AC1 sole-writer)");

        await using var db = await factory.CreateDbContextAsync();
        (await db.PoiCollectionItems.FirstAsync(ci => ci.PoiId == 2 && ci.PoiCollectionId == CollectionId))
            .DwellMinutes.Should().Be(30, "the dwell write itself still landed");
    }
}
