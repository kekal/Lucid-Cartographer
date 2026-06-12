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
/// Unit tests for the Trip View VM. Uses the real TripOrderingService over an
/// InMemory DbContext (the ordering logic is exercised by its own tests; here we
/// verify the VM's gate, toggle/seed/persist, restore, and reconcile wiring).
/// </summary>
public class TripViewModelTests
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
            // Distinct coordinates per POI so leg endpoints are unambiguous.
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static TripViewModel CreateVm(IDbContextFactory<AppDbContext> factory)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        return new TripViewModel(ordering, factory, writeLock, NullLogger<TripViewModel>.Instance);
    }

    [Fact]
    public async Task ToggleAvailable_RequiresSingleCollection_AndTwoPlaceable()
    {
        var factory = SeedFactory(placeable: 2);
        await using var vm = CreateVm(factory);

        await vm.LoadAsync(null, 5);
        vm.IsToggleAvailable.Should().BeFalse("no active collection");

        await vm.LoadAsync(CollectionId, 1);
        vm.IsToggleAvailable.Should().BeFalse("fewer than 2 placeable");

        await vm.LoadAsync(CollectionId, 2);
        vm.IsToggleAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Toggle_FirstEnable_SeedsOrder_PersistsFlag_RaisesStateChanged()
    {
        var factory = SeedFactory(placeable: 3);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 3);
        var fired = 0;
        vm.StateChanged += () => fired++;

        await vm.ToggleAsync();

        vm.IsTripViewEnabled.Should().BeTrue();
        vm.StopOrders.Should().HaveCount(3);
        vm.StopOrders.Values.Should().BeEquivalentTo(new[] { 1, 2, 3 });
        fired.Should().BeGreaterThan(0);

        await using var db = await factory.CreateDbContextAsync();
        (await db.PoiCollections.FirstAsync(c => c.Id == CollectionId)).TripViewEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Toggle_Off_ClearsStopOrders_ButKeepsPersistedOrderIndex()
    {
        var factory = SeedFactory(placeable: 2);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 2);

        await vm.ToggleAsync(); // on
        await vm.ToggleAsync(); // off

        vm.IsTripViewEnabled.Should().BeFalse();
        vm.StopOrders.Should().BeEmpty();

        await using var db = await factory.CreateDbContextAsync();
        var maxOrder = await db.PoiCollectionItems.MaxAsync(ci => ci.OrderIndex);
        maxOrder.Should().Be(2, "order metadata is preserved when Trip View turns off");
        (await db.PoiCollections.FirstAsync(c => c.Id == CollectionId)).TripViewEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Toggle_IsNoOp_WhenUnavailable()
    {
        var factory = SeedFactory(placeable: 1);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 1);

        await vm.ToggleAsync();

        vm.IsTripViewEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Load_RestoresPersistedEnabledState_AndStopOrders()
    {
        var factory = SeedFactory(placeable: 2, tripViewEnabled: true);
        // Pre-seed an order as if a previous session had enabled Trip View.
        var ordering = new TripOrderingService(factory, new SqliteWriteLock(), NullLogger<TripOrderingService>.Instance);
        await ordering.SeedOrderAsync(CollectionId);

        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 2);

        vm.IsTripViewEnabled.Should().BeTrue("reopening restores the persisted on state");
        vm.StopOrders.Should().HaveCount(2);
    }

    [Fact]
    public async Task RefreshAfterMembershipChange_Reconciles_WhenEnabled()
    {
        var factory = SeedFactory(placeable: 2);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 2);
        await vm.ToggleAsync(); // on, seeds 1..2

        // Add a third placeable POI to the collection.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.Add(new Poi { Id = 3, Name = "P3", Latitude = 50, Longitude = 20, AddedDate = new DateTime(2025, 2, 1) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 3, PoiCollectionId = CollectionId });
            await db.SaveChangesAsync();
        }

        await vm.RefreshAfterMembershipChangeAsync(3);

        vm.StopOrders.Should().HaveCount(3);
        vm.StopOrders[3].Should().Be(3, "the added POI is appended as the last stop");
    }

    [Fact]
    public async Task UpdatePlaceableCount_FlipsAvailability_WithoutDbRead()
    {
        var factory = SeedFactory(placeable: 2);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 2);
        vm.IsToggleAvailable.Should().BeTrue();

        vm.UpdatePlaceableCount(1);

        vm.IsToggleAvailable.Should().BeFalse();
    }

    // === Story 1.3: OrderedLegs / OrderedStops projections ===

    [Fact]
    public async Task OrderedLegs_Roundtrip_HasNLegs_IncludingClosingLegBackToStart()
    {
        var factory = SeedFactory(placeable: 3); // FinishPoiId null ⇒ Roundtrip
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 3);
        await vm.ToggleAsync(); // seeds 1..3

        // N stops ⇒ N legs (2 consecutive + the closing leg).
        vm.OrderedLegs.Should().HaveCount(3);
        // Closing leg runs from the last stop back to the Start (Order 1).
        var closing = vm.OrderedLegs[^1];
        closing.FromPoiId.Should().Be(3);
        closing.ToPoiId.Should().Be(1);
        // Every leg is non-Measured in Phase 1.
        vm.OrderedLegs.Should().OnlyContain(l => l.IsMeasured == false);
    }

    [Fact]
    public async Task OrderedLegs_DistinctFinish_HasNMinusOneLegs_NoClosingLeg()
    {
        var factory = SeedFactory(placeable: 3);
        // A distinct Finish (Stop 3) makes the path open.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var col = await db.PoiCollections.FirstAsync(c => c.Id == CollectionId);
            col.FinishPoiId = 3;
            await db.SaveChangesAsync();
        }

        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 3);
        await vm.ToggleAsync();

        vm.OrderedLegs.Should().HaveCount(2, "an open path emits N−1 legs and no closing leg");
        vm.OrderedLegs.Should().NotContain(l => l.FromPoiId == 3 && l.ToPoiId == 1);
    }

    [Fact]
    public async Task OrderedLegs_FinishPointingAtNonPlaceableStop_FallsBackToRoundtrip()
    {
        var factory = SeedFactory(placeable: 3);
        // Designate a coordinate-less POI as Finish — it can't terminate a drawn
        // path, so the loop must stay closed (Roundtrip) rather than drop the leg.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.Add(new Poi { Id = 88, Name = "GhostFinish", Latitude = null, Longitude = null, AddedDate = new DateTime(2025, 2, 1) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 88, PoiCollectionId = CollectionId });
            var col = await db.PoiCollections.FirstAsync(c => c.Id == CollectionId);
            col.FinishPoiId = 88;
            await db.SaveChangesAsync();
        }

        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 3);
        await vm.ToggleAsync();

        vm.OrderedLegs.Should().HaveCount(3, "an unresolvable Finish falls back to a closed loop");
        vm.OrderedLegs[^1].FromPoiId.Should().Be(3);
        vm.OrderedLegs[^1].ToPoiId.Should().Be(1);
    }

    [Fact]
    public async Task OrderedLegs_ExcludesCoordinatelessStops_WithoutBreakingNumbering()
    {
        var factory = SeedFactory(placeable: 2);
        // Add a coordinate-less POI to the collection (not placeable).
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.Add(new Poi { Id = 99, Name = "NoCoords", Latitude = null, Longitude = null, AddedDate = new DateTime(2025, 1, 5) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 99, PoiCollectionId = CollectionId });
            await db.SaveChangesAsync();
        }

        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 2);
        await vm.ToggleAsync();

        // Only the two placeable stops appear, contiguously numbered 1..2.
        vm.OrderedStops.Should().HaveCount(2);
        vm.OrderedStops.Select(s => s.OrderIndex).Should().BeEquivalentTo(new[] { 1, 2 });
        vm.OrderedStops.Should().NotContain(s => s.PoiId == 99);
        // Roundtrip over 2 placeable stops ⇒ 2 legs (1→2 and the 2→1 close).
        vm.OrderedLegs.Should().HaveCount(2);
    }

    [Fact]
    public async Task OrderedStops_ProjectNameAndCoordinates_InStopOrder()
    {
        var factory = SeedFactory(placeable: 2);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 2);
        await vm.ToggleAsync();

        vm.OrderedStops.Select(s => s.OrderIndex).Should().ContainInOrder(1, 2);
        var first = vm.OrderedStops[0];
        first.Name.Should().Be("P1");
        first.Lat.Should().Be(51); // 50 + i (i=1)
        first.Lon.Should().Be(21);
    }

    [Fact]
    public async Task OrderedLegs_AndStops_ClearWhenTripViewTurnsOff()
    {
        var factory = SeedFactory(placeable: 3);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 3);
        await vm.ToggleAsync(); // on
        vm.OrderedLegs.Should().NotBeEmpty();

        await vm.ToggleAsync(); // off

        vm.OrderedLegs.Should().BeEmpty();
        vm.OrderedStops.Should().BeEmpty();
    }

    // === Story 1.4: list ↔ map selection sync ===

    private static async Task<TripViewModel> EnabledVmAsync(IDbContextFactory<AppDbContext> factory, int placeable)
    {
        var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return vm;
    }

    [Fact]
    public async Task SelectStop_SetsSelection_AndRaisesStateChanged()
    {
        await using var vm = await EnabledVmAsync(SeedFactory(placeable: 3), 3);
        var fired = 0;
        vm.StateChanged += () => fired++;

        vm.SelectStop(2);

        vm.SelectedStopPoiId.Should().Be(2);
        vm.SelectedStop!.OrderIndex.Should().Be(2);
        vm.LastSelectionSource.Should().Be(TripSelectionSource.List);
        vm.SelectionAnnouncement.Should().NotBeNullOrEmpty();
        fired.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SelectStop_ReSelectingSameStop_AdvancesSelectionTick()
    {
        // [Review][Patch] A re-select of the already-selected stop must bump
        // SelectionTick (even though poiId is unchanged) so the host can re-run the
        // pan follow-up and re-centre a marker the user scrolled away from (AC1).
        await using var vm = await EnabledVmAsync(SeedFactory(placeable: 3), 3);

        vm.SelectStop(2);
        var tickAfterFirst = vm.SelectionTick;

        vm.SelectStop(2);

        vm.SelectedStopPoiId.Should().Be(2, "re-selecting keeps the same stop selected");
        vm.SelectionTick.Should().BeGreaterThan(tickAfterFirst,
            "an idempotent re-select still advances the tick so the host re-pans");
    }

    [Fact]
    public async Task SelectStop_RecordsSource_Map()
    {
        await using var vm = await EnabledVmAsync(SeedFactory(placeable: 2), 2);

        vm.SelectStop(1, TripSelectionSource.Map);

        vm.LastSelectionSource.Should().Be(TripSelectionSource.Map);
        vm.SelectedStopPoiId.Should().Be(1);
    }

    [Fact]
    public async Task SelectStop_DifferentStop_ReplacesSelection_OnlyOneSelected()
    {
        await using var vm = await EnabledVmAsync(SeedFactory(placeable: 3), 3);

        vm.SelectStop(1);
        vm.SelectStop(3);

        vm.SelectedStopPoiId.Should().Be(3, "selecting another stop replaces the prior one");
        vm.SelectedStop!.PoiId.Should().Be(3);
    }

    [Fact]
    public async Task SelectStop_Null_ClearsSelection()
    {
        await using var vm = await EnabledVmAsync(SeedFactory(placeable: 2), 2);
        vm.SelectStop(1);

        vm.SelectStop(null);

        vm.SelectedStopPoiId.Should().BeNull();
        vm.SelectedStop.Should().BeNull();
        vm.SelectionAnnouncement.Should().BeNull();
    }

    [Fact]
    public async Task SelectStop_IsNoOp_WhenTripViewOff()
    {
        var factory = SeedFactory(placeable: 2);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 2); // Trip View off

        vm.SelectStop(1);

        vm.SelectedStopPoiId.Should().BeNull("selection is meaningless while Trip View is off");
    }

    [Fact]
    public async Task Selection_ClearedWhenTripViewTurnsOff()
    {
        await using var vm = await EnabledVmAsync(SeedFactory(placeable: 2), 2);
        vm.SelectStop(1);
        vm.SelectedStopPoiId.Should().Be(1);

        await vm.ToggleAsync(); // off

        vm.SelectedStopPoiId.Should().BeNull("toggling off must not leave a stale selection (AC4)");
        vm.SelectedStop.Should().BeNull();
    }

    [Fact]
    public async Task Selection_ClearedWhenSelectedStopRemoved()
    {
        var factory = SeedFactory(placeable: 3);
        await using var vm = await EnabledVmAsync(factory, 3);
        vm.SelectStop(3);

        // Remove the selected POI from the collection, then reconcile.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var item = await db.PoiCollectionItems.FirstAsync(ci => ci.PoiId == 3 && ci.PoiCollectionId == CollectionId);
            db.PoiCollectionItems.Remove(item);
            await db.SaveChangesAsync();
        }

        await vm.RefreshAfterMembershipChangeAsync(2);

        vm.SelectedStopPoiId.Should().BeNull("a removed stop can no longer be the selection");
    }

    [Fact]
    public async Task SelectStop_DoesNotDisturbStopProjections()
    {
        await using var vm = await EnabledVmAsync(SeedFactory(placeable: 3), 3);
        var stopsBefore = vm.OrderedStops;
        var legsBefore = vm.OrderedLegs;

        vm.SelectStop(2);

        vm.OrderedStops.Should().BeSameAs(stopsBefore, "selection is independent of the stop/leg projections");
        vm.OrderedLegs.Should().BeSameAs(legsBefore);
    }
}
