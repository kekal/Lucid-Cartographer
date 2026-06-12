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
        new(factory, new SqliteWriteLock(), NullLogger<TripOrderingService>.Instance);

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
}
