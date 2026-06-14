using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Tests;

/// <summary>
/// TRIP-MCP-01 (Story 3.2): the externally-supplied order assignment and the shared
/// dwell write on the single OrderIndex writer. AssignOrderAsync applies a full
/// reorder through the same path as a manual drag, honours pins, and rejects an
/// invalid id set; SetDwellMinutesAsync writes/clears/validates DwellMinutes.
/// </summary>
public class TripOrderingServiceAssignTests
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed(params (int PoiId, bool Placeable)[] members)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.Drive });
        var order = 1;
        foreach (var m in members)
        {
            db.Pois.Add(new Poi
            {
                Id = m.PoiId,
                Name = $"P{m.PoiId}",
                Latitude = m.Placeable ? 50.0 + m.PoiId : (double?)null,
                Longitude = m.Placeable ? 20.0 : (double?)null,
                AddedDate = new DateTime(2025, 1, m.PoiId),
            });
            db.PoiCollectionItems.Add(new PoiCollectionItem
            {
                PoiId = m.PoiId, PoiCollectionId = CollectionId, OrderIndex = m.Placeable ? order++ : 0,
            });
        }
        db.SaveChanges();
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
    public async Task AssignOrder_AppliesFullReorder_Contiguously()
    {
        var factory = Seed((1, true), (2, true), (3, true));
        var service = TestDbHelper.CreateOrderingService(factory);

        await service.AssignOrderAsync(CollectionId, new[] { 3, 1, 2 });

        var order = await ReadOrderAsync(factory);
        order[3].Should().Be(1);
        order[1].Should().Be(2);
        order[2].Should().Be(3);
    }

    [Fact]
    public async Task AssignOrder_HonorsStartAndFinishPins()
    {
        var factory = Seed((1, true), (2, true), (3, true), (4, true));
        var service = TestDbHelper.CreateOrderingService(factory);
        await service.SetStartAsync(CollectionId, 2); // Start pinned to Order 1
        await service.SetFinishAsync(CollectionId, 3); // Finish pinned to Order N

        // Agent tries to put 2 and 3 in the interior — pins must override the ends.
        await service.AssignOrderAsync(CollectionId, new[] { 1, 2, 3, 4 });

        var order = await ReadOrderAsync(factory);
        order[2].Should().Be(1, "the pinned Start stays at Order 1");
        order[3].Should().Be(4, "the pinned Finish stays at Order N");
        new[] { order[1], order[4] }.Should().BeEquivalentTo(new[] { 2, 3 });
    }

    [Theory]
    [InlineData(new[] { 1, 2 })]            // missing a stop
    [InlineData(new[] { 1, 2, 3, 4 })]      // unknown id
    [InlineData(new[] { 1, 2, 2 })]         // duplicate
    public async Task AssignOrder_RejectsInvalidIdSet(int[] ids)
    {
        var factory = Seed((1, true), (2, true), (3, true));
        var service = TestDbHelper.CreateOrderingService(factory);

        var act = () => service.AssignOrderAsync(CollectionId, ids);

        await act.Should().ThrowAsync<ArgumentException>();
        // The order is untouched on rejection.
        (await ReadOrderAsync(factory)).Should().BeEquivalentTo(new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 3 });
    }

    [Fact]
    public async Task AssignOrder_RejectsUnplaceableId()
    {
        var factory = Seed((1, true), (2, true), (3, false)); // poi3 unplaceable (Order 0)
        var service = TestDbHelper.CreateOrderingService(factory);

        var act = () => service.AssignOrderAsync(CollectionId, new[] { 1, 2, 3 });

        await act.Should().ThrowAsync<ArgumentException>("an unplaceable POI is not a Stop");
    }

    [Fact]
    public async Task AssignOrder_RemainsDragEditable()
    {
        var factory = Seed((1, true), (2, true), (3, true));
        var service = TestDbHelper.CreateOrderingService(factory);

        await service.AssignOrderAsync(CollectionId, new[] { 3, 1, 2 });
        // A subsequent manual drag still works — no lock-out.
        await service.ReorderStopAsync(CollectionId, poiId: 2, targetOrderIndex: 1);

        (await ReadOrderAsync(factory))[2].Should().Be(1);
    }

    [Fact]
    public async Task SetDwellMinutes_WritesAndClears()
    {
        var factory = Seed((1, true), (2, true));
        var service = TestDbHelper.CreateOrderingService(factory);

        await service.SetDwellMinutesAsync(CollectionId, 1, 45);
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.PoiCollectionItems.FirstAsync(ci => ci.PoiId == 1)).DwellMinutes.Should().Be(45);
        }

        await service.SetDwellMinutesAsync(CollectionId, 1, null);
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.PoiCollectionItems.FirstAsync(ci => ci.PoiId == 1)).DwellMinutes.Should().BeNull();
        }
    }

    [Fact]
    public async Task SetDwellMinutes_RejectsOutOfRange_AndNoOpsAbsentMembership()
    {
        var factory = Seed((1, true), (2, true));
        var service = TestDbHelper.CreateOrderingService(factory);

        await service.SetDwellMinutesAsync(CollectionId, 1, -5);
        await service.SetDwellMinutesAsync(CollectionId, 1, TripOrderingService.MaxDwellMinutes + 1);
        await service.SetDwellMinutesAsync(CollectionId, poiId: 999, 30); // absent

        await using var db = await factory.CreateDbContextAsync();
        (await db.PoiCollectionItems.FirstAsync(ci => ci.PoiId == 1)).DwellMinutes
            .Should().BeNull("out-of-range values are ignored");
    }
}
