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
/// Story 1.4 (FR-4): the SINGLE canonical Stop Order is shared across both views.
/// Covers the VM's cached <see cref="TripViewModel.CanonicalStopOrder"/> (populated
/// regardless of the Trip View toggle, empty for no-order / multi-collection) and the
/// pure <see cref="TripViewModel.ApplyCanonicalOrder"/> sort the plain list uses.
/// Mirrors the TripViewModelTests setup conventions (real TripOrderingService over an
/// InMemory DbContext via TestDbHelper).
/// </summary>
public class TripViewModelCanonicalOrderTests
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> SeedFactory(int placeable, bool tripViewEnabled = false)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId,
            Name = "Trip",
            Color = "#005bbf",
            TripViewEnabled = tripViewEnabled
        });
        for (var i = 1; i <= placeable; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static TripViewModel CreateVm(IDbContextFactory<AppDbContext> factory)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        return new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
    }

    private static Poi Poi(int id) => new() { Id = id, Name = $"P{id}" };

    // === ApplyCanonicalOrder (pure, in-memory) ===

    [Fact]
    public void ApplyCanonicalOrder_ReturnsUnchanged_WhenCanonicalOrderEmpty()
    {
        var factory = SeedFactory(placeable: 2);
        var vm = CreateVm(factory);
        // No LoadAsync ⇒ CanonicalStopOrder is still empty.
        var input = new List<Poi> { Poi(3), Poi(1), Poi(2) };

        var result = vm.ApplyCanonicalOrder(input);

        result.Should().BeSameAs(input, "an empty canonical order leaves the list untouched (AC3)");
    }

    [Fact]
    public async Task ApplyCanonicalOrder_OrdersByOrderIndex_NonMembersKeptStablyAfter()
    {
        var factory = SeedFactory(placeable: 3);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 3);
        await vm.ToggleAsync(); // seeds order 1..3 (P1=1, P2=2, P3=3 by AddedDate)

        vm.CanonicalStopOrder.Should().HaveCount(3);

        // Incoming list is shuffled and carries two non-members (99, 98) interleaved
        // so we can prove members sort to the front by OrderIndex while the
        // non-members keep their incoming relative order (99 before 98) at the end.
        var input = new List<Poi> { Poi(99), Poi(3), Poi(1), Poi(98), Poi(2) };

        var result = vm.ApplyCanonicalOrder(input);

        result.Select(p => p.Id).Should().Equal(1, 2, 3, 99, 98);
    }

    [Fact]
    public async Task ApplyCanonicalOrder_KeepsNonMembersStable_WhenNoMembersPresent()
    {
        var factory = SeedFactory(placeable: 2);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 2);
        await vm.ToggleAsync(); // order exists, but the input below shares no ids

        var input = new List<Poi> { Poi(50), Poi(40), Poi(60) };

        var result = vm.ApplyCanonicalOrder(input);

        result.Select(p => p.Id).Should().Equal(new[] { 50, 40, 60 }, "non-members preserve incoming order");
    }

    // === CanonicalStopOrder population (regardless of Trip View toggle) ===

    [Fact]
    public async Task CanonicalStopOrder_Populated_ForSingleInScopeCollection_WithOrder_EvenWhenTripViewOff()
    {
        // A collection that was ordered in a prior session but is currently OFF.
        var factory = SeedFactory(placeable: 3, tripViewEnabled: false);
        var ordering = TestDbHelper.CreateOrderingService(factory);
        await ordering.SeedOrderAsync(CollectionId);

        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 3);

        vm.IsTripViewEnabled.Should().BeFalse("the persisted flag is off");
        vm.StopOrders.Should().BeEmpty("badge projection is gated on the toggle");
        vm.CanonicalStopOrder.Should().HaveCount(3, "the canonical order lives on the entity regardless of the toggle (AC2)");
        vm.CanonicalStopOrder.Values.Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public async Task CanonicalStopOrder_Empty_ForSingleInScopeCollection_WithNoOrder()
    {
        // Never put into Trip View ⇒ no OrderIndex written.
        var factory = SeedFactory(placeable: 3, tripViewEnabled: false);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 3);

        vm.CanonicalStopOrder.Should().BeEmpty("a never-ordered collection forces no order on the plain list (AC3)");
    }

    [Fact]
    public async Task CanonicalStopOrder_Empty_WhenActiveCollectionNull_MultiCollection()
    {
        var factory = SeedFactory(placeable: 3);
        var ordering = TestDbHelper.CreateOrderingService(factory);
        await ordering.SeedOrderAsync(CollectionId);

        await using var vm = CreateVm(factory);
        // Scope an order first, then drop to no single collection (multi-collection
        // / search active ⇒ host passes null) and prove the cache clears.
        await vm.LoadAsync(CollectionId, 3);
        vm.CanonicalStopOrder.Should().HaveCount(3);

        await vm.LoadAsync(null, 5);

        vm.CanonicalStopOrder.Should().BeEmpty("no single collection in scope ⇒ no forced order (AC3)");
    }

    [Fact]
    public async Task CanonicalStopOrder_TracksReorder_AndStaysPopulated_WhenToggledOff()
    {
        var factory = SeedFactory(placeable: 3);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 3);
        await vm.ToggleAsync(); // on, seeds P1=1, P2=2, P3=3

        // Move P3 to slot 1 through the single ordering write path.
        await vm.MoveStopToAsync(poiId: 3, targetOrderIndex: 1);

        vm.CanonicalStopOrder[3].Should().Be(1, "the canonical cache tracks a reorder");
        vm.CanonicalStopOrder[1].Should().Be(2);
        vm.CanonicalStopOrder[2].Should().Be(3);

        // Toggling Trip View off keeps the persisted order ⇒ the plain list still
        // follows it (AC4: order persists between the two views, no divergence).
        await vm.ToggleAsync(); // off
        vm.IsTripViewEnabled.Should().BeFalse();
        vm.StopOrders.Should().BeEmpty();
        vm.CanonicalStopOrder[3].Should().Be(1, "the shared canonical order survives the toggle (AC4)");

        var ordered = vm.ApplyCanonicalOrder(new List<Poi> { Poi(1), Poi(2), Poi(3) });
        ordered.Select(p => p.Id).Should().Equal(new[] { 3, 1, 2 }, "the plain list renders the same order while OFF (AC2)");
    }
}
