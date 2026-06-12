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
