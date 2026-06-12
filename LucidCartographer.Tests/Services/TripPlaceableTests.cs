using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 1.6 — the IsPlaceable exclusion contract. Covers the canonical
/// predicate truth table ([TRIP-PLACE-01]), the placeable-only routing
/// candidate accessor ([TRIP-PLACE-03]), leg exclusion ([TRIP-PLACE-02]) and
/// the presented contiguous numbering over the placeable subset
/// ([TRIP-ORDER-UNPLACE-01]).
/// </summary>
public class TripPlaceableTests
{
    private const int CollectionId = 1;

    // === [TRIP-PLACE-01] predicate truth table ===

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, 21.0, false)]
    [InlineData(51.0, null, false)]
    [InlineData(51.0, 21.0, true)]
    [InlineData(0.0, 0.0, true)] // (0,0) is a real coordinate pair, NOT a sentinel
    public void IsPlaceable_TruthTable(double? lat, double? lon, bool expected)
    {
        StopPlaceability.IsPlaceable(lat, lon).Should().Be(expected);

        var poi = new Poi { Id = 1, Name = "P", Latitude = lat, Longitude = lon };
        poi.IsPlaceable().Should().Be(expected, "the entity overload must agree with the value overload");
    }

    [Fact]
    public void IsPlaceable_MatchesTheExistingCodebaseConvention()
    {
        // The predicate is exactly `Latitude != null && Longitude != null`
        // (PoiService / StartupCleanupService / enrichment convention).
        var samples = new (double? Lat, double? Lon)[]
        {
            (null, null), (null, 1), (1, null), (1, 1), (0, 0), (-90, 180),
        };
        foreach (var (lat, lon) in samples)
        {
            StopPlaceability.IsPlaceable(lat, lon)
                .Should().Be(lat != null && lon != null);
        }
    }

    // === Shared seeding ===

    /// <summary>
    /// Seeds a mixed collection: placeable P1..P3 with an unplaceable POI (id 99)
    /// added BETWEEN them by AddedDate (so it would interleave if it could), plus
    /// a second unplaceable POI (id 98, lat-only) at the end.
    /// </summary>
    private static IDbContextFactory<AppDbContext> SeedMixedFactory()
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf" });
        db.Pois.Add(new Poi { Id = 1, Name = "P1", Latitude = 51, Longitude = 21, AddedDate = new DateTime(2025, 1, 1) });
        db.Pois.Add(new Poi { Id = 99, Name = "NoCoords", Latitude = null, Longitude = null, AddedDate = new DateTime(2025, 1, 2) });
        db.Pois.Add(new Poi { Id = 2, Name = "P2", Latitude = 52, Longitude = 22, AddedDate = new DateTime(2025, 1, 3) });
        db.Pois.Add(new Poi { Id = 3, Name = "P3", Latitude = 53, Longitude = 23, AddedDate = new DateTime(2025, 1, 4) });
        db.Pois.Add(new Poi { Id = 98, Name = "LatOnly", Latitude = 54, Longitude = null, AddedDate = new DateTime(2025, 1, 5) });
        foreach (var poiId in new[] { 1, 99, 2, 3, 98 })
        {
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = poiId, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static TripOrderingService CreateOrdering(IDbContextFactory<AppDbContext> factory) =>
        new(factory, new SqliteWriteLock(), NullLogger<TripOrderingService>.Instance);

    private static async Task<TripViewModel> EnabledVmAsync(IDbContextFactory<AppDbContext> factory, int placeable)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        var vm = new TripViewModel(ordering, factory, writeLock, NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync(); // seed + enable
        return vm;
    }

    // === [TRIP-PLACE-03] routing candidate set ===

    [Fact]
    public async Task GetPlaceableStops_ExcludesUnplaceable_FromTheCandidateSet()
    {
        var factory = SeedMixedFactory();
        var ordering = CreateOrdering(factory);
        await ordering.SeedOrderAsync(CollectionId);

        var candidates = await ordering.GetPlaceableStopsAsync(CollectionId);

        candidates.Should().HaveCount(3, "only the placeable subset is a routing candidate");
        candidates.Select(c => c.PoiId).Should().Equal(1, 2, 3);
        candidates.Select(c => c.PoiId).Should().NotContain(new[] { 98, 99 });
        candidates.Select(c => c.OrderIndex).Should().BeInAscendingOrder();
        // Coordinates are materialized non-null — an all-pairs matrix built over
        // this list can never see a null coordinate.
        candidates.Should().OnlyContain(c => c.Latitude > 0 && c.Longitude > 0);
    }

    [Fact]
    public async Task GetPlaceableStops_AllPairsCandidateList_ContainsNoUnplaceablePoi()
    {
        var factory = SeedMixedFactory();
        var ordering = CreateOrdering(factory);
        await ordering.SeedOrderAsync(CollectionId);

        var candidates = await ordering.GetPlaceableStopsAsync(CollectionId);

        // Simulate the Epic 3 all-pairs enumeration: every (from, to) pair drawn
        // from the accessor — no pair may touch an unplaceable POI.
        var pairs = candidates.SelectMany(_ => candidates, (from, to) => (from.PoiId, to.PoiId)).ToList();
        pairs.Should().HaveCount(9);
        pairs.Should().NotContain(p => p.Item1 == 99 || p.Item2 == 99 || p.Item1 == 98 || p.Item2 == 98);
    }

    // === [TRIP-PLACE-02] leg exclusion ===

    [Fact]
    public async Task Legs_ConnectConsecutivePlaceableStopsOnly_LoopNotSevered()
    {
        var factory = SeedMixedFactory();
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        // 3 placeable stops on a Roundtrip ⇒ exactly 3 legs (1→2, 2→3, 3→1):
        // the interleaved unplaceable POI neither severs the chain nor appears.
        vm.OrderedLegs.Should().HaveCount(3);
        vm.OrderedLegs.Select(l => (l.FromPoiId, l.ToPoiId))
            .Should().Equal((1, 2), (2, 3), (3, 1));
        vm.OrderedLegs.Should().NotContain(l => l.FromPoiId == 99 || l.ToPoiId == 99);
        vm.OrderedLegs.Should().NotContain(l => l.FromPoiId == 98 || l.ToPoiId == 98);
    }

    [Fact]
    public async Task Markers_StopOrders_ExcludeUnplaceable()
    {
        var factory = SeedMixedFactory();
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        // StopOrders is what decorates the map markers — no unplaceable POI in it.
        vm.StopOrders.Keys.Should().BeEquivalentTo(new[] { 1, 2, 3 });
        vm.OrderedStops.Should().NotContain(s => s.PoiId == 99 || s.PoiId == 98);
    }

    // === [TRIP-ORDER-UNPLACE-01] presented numbering integrity ===

    [Fact]
    public async Task StopRows_PlaceableNumbering_IsContiguous_AndUnplaceableCarriesNoNumber()
    {
        var factory = SeedMixedFactory();
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        vm.StopRows.Should().HaveCount(5, "every member stays in the list — nothing is silently dropped");

        var placeable = vm.StopRows.Where(r => r.IsPlaceable).ToList();
        placeable.Select(r => r.DisplayOrder).Should().Equal(1, 2, 3);

        var unplaceable = vm.StopRows.Where(r => !r.IsPlaceable).ToList();
        unplaceable.Select(r => r.PoiId).Should().BeEquivalentTo(new[] { 99, 98 });
        unplaceable.Should().OnlyContain(r => r.DisplayOrder == null,
            "an unplaceable row never consumes or displays a routed number");
    }

    [Fact]
    public async Task StopRows_NumberingStaysContiguous_WhenAPoiBecomesPlaceable()
    {
        var factory = SeedMixedFactory();
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        // Enrichment fills the missing coordinates of POI 99.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var poi = await db.Pois.FirstAsync(p => p.Id == 99);
            poi.Latitude = 55;
            poi.Longitude = 25;
            await db.SaveChangesAsync();
        }

        await vm.RefreshAfterMembershipChangeAsync(4);

        // The newly-placeable POI is appended as the last stop; the presented
        // numbering stays contiguous 1..4 and the stored order of the existing
        // stops is not corrupted.
        var placeable = vm.StopRows.Where(r => r.IsPlaceable).ToList();
        placeable.Select(r => r.DisplayOrder).Should().Equal(1, 2, 3, 4);
        placeable.Select(r => r.PoiId).Should().Equal(1, 2, 3, 99);

        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == CollectionId && ci.OrderIndex > 0)
            .OrderBy(ci => ci.OrderIndex)
            .Select(ci => new { ci.PoiId, ci.OrderIndex })
            .ToListAsync();
        // Stored OrderIndex stays contiguous; existing stops keep their stored order.
        stored.Select(s => s.OrderIndex).Should().Equal(1, 2, 3, 4);
        stored.Take(3).Select(s => s.PoiId).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task StopRows_NumberingStaysContiguous_WhenAStopBecomesUnplaceable()
    {
        var factory = SeedMixedFactory();
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        // The middle stop (P2, stop 2 of 3) loses its coordinates.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var poi = await db.Pois.FirstAsync(p => p.Id == 2);
            poi.Latitude = null;
            poi.Longitude = null;
            await db.SaveChangesAsync();
        }

        await vm.RefreshAfterMembershipChangeAsync(2);

        // The user never sees placeable badges 1,3 — the presentation renumbers
        // the placeable subset contiguously and P2 shows no routed number.
        var placeable = vm.StopRows.Where(r => r.IsPlaceable).ToList();
        placeable.Select(r => r.DisplayOrder).Should().Equal(1, 2);
        placeable.Select(r => r.PoiId).Should().Equal(1, 3);
        vm.StopRows.Single(r => r.PoiId == 2).DisplayOrder.Should().BeNull();
        vm.StopRows.Single(r => r.PoiId == 2).IsPlaceable.Should().BeFalse();
    }

    // === [TRIP-PLACE-04] membership + selection honesty ===

    [Fact]
    public async Task UnplaceableStop_RemainsAMemberOfTheCollection()
    {
        var factory = SeedMixedFactory();
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        await using var db = await factory.CreateDbContextAsync();
        var memberIds = await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == CollectionId)
            .Select(ci => ci.PoiId)
            .ToListAsync();
        memberIds.Should().BeEquivalentTo(new[] { 1, 2, 3, 98, 99 },
            "flagging is presentation-only: no PoiCollectionItem is mutated or removed");
    }

    [Fact]
    public async Task SelectStop_IgnoresUnplaceableRows()
    {
        var factory = SeedMixedFactory();
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        vm.SelectStop(99);

        vm.SelectedStopPoiId.Should().BeNull("an unplaceable row has no marker to pan to");
    }
}
