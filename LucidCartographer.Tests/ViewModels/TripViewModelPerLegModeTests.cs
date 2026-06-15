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
/// Story 3.2 (TRIP-LEGMODE-01 / TRIP-CACHE-01, FR-19/20/21): BuildLegs reads each
/// leg's OWN mode (the From-stop's OutgoingTravelMode, null ≡ AnyAir), carries it on
/// TripLeg.Mode, and looks the leg up in the cache by its own (From, To, Mode) key —
/// NOT one trip-wide mode. AnyAir/null legs have no cache row ⇒ null duration ("—").
/// </summary>
public class TripViewModelPerLegModeTests
{
    private const int CollectionId = 1;

    /// <summary>
    /// Seeds a 2-stop roundtrip. <paramref name="mode1"/> is P1's OutgoingTravelMode
    /// (the 1→2 leg's mode); <paramref name="mode2"/> is P2's (the closing 2→1 leg's mode).
    /// </summary>
    private static IDbContextFactory<AppDbContext> Seed(string? mode1, string? mode2)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.AnyAir,
        });
        db.Pois.Add(new Poi { Id = 1, Name = "P1", Latitude = 51, Longitude = 21, AddedDate = new DateTime(2025, 1, 1) });
        db.Pois.Add(new Poi { Id = 2, Name = "P2", Latitude = 52, Longitude = 22, AddedDate = new DateTime(2025, 1, 2) });
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 1, PoiCollectionId = CollectionId, OutgoingTravelMode = mode1 });
        db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 2, PoiCollectionId = CollectionId, OutgoingTravelMode = mode2 });
        db.SaveChanges();
        return factory;
    }

    private static async Task AddSegmentAsync(
        IDbContextFactory<AppDbContext> factory, int from, int to, string mode, int seconds,
        string fidelity = Fidelity.Estimated)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = from, ToPoiId = to, TravelMode = mode,
            DurationSeconds = seconds, DistanceMeters = 8000,
            Fidelity = fidelity, Source = "Mock", ComputedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<TripViewModel> EnabledVmAsync(IDbContextFactory<AppDbContext> factory)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(
            ordering, factory, writeLock,
            new TravelTimeTrigger(), new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeableCount: 2);
        await vm.ToggleAsync();
        return vm;
    }

    [Fact]
    public async Task BuildLegs_SetsLegMode_FromFromStopOutgoingTravelMode()
    {
        var factory = Seed(mode1: TravelMode.Drive, mode2: TravelMode.Walk);
        await using var vm = await EnabledVmAsync(factory);

        var leg12 = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        var leg21 = vm.OrderedLegs.First(l => l.FromPoiId == 2 && l.ToPoiId == 1);
        leg12.Mode.Should().Be(TravelMode.Drive, "leg mode comes from its From-stop (P1) OutgoingTravelMode");
        leg21.Mode.Should().Be(TravelMode.Walk, "the closing leg's mode comes from its From-stop (P2)");
    }

    [Fact]
    public async Task BuildLegs_NullOutgoingMode_NormalizesToAnyAir()
    {
        var factory = Seed(mode1: null, mode2: null);
        await using var vm = await EnabledVmAsync(factory);

        vm.OrderedLegs.Should().OnlyContain(l => l.Mode == TravelMode.AnyAir,
            "a null OutgoingTravelMode is normalized to the single Any/Air state");
    }

    [Fact]
    public async Task GroundModeLeg_WithMatchingCacheRow_ShowsTime()
    {
        var factory = Seed(mode1: TravelMode.Drive, mode2: TravelMode.AnyAir);
        // A cache row keyed by the leg's OWN (1,2,Drive) key.
        await AddSegmentAsync(factory, 1, 2, TravelMode.Drive, 600);

        await using var vm = await EnabledVmAsync(factory);

        var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        leg.DurationSeconds.Should().Be(600, "the leg resolves its time via its own (From,To,Mode) key");
        leg.Fidelity.Should().Be(Fidelity.Estimated);
    }

    [Fact]
    public async Task AnyAirLeg_HasNoCacheRow_NullDuration()
    {
        // Both legs Any/Air with NO cache rows seeded ⇒ never auto-timed ⇒ "—".
        var factory = Seed(mode1: null, mode2: TravelMode.AnyAir);
        await using var vm = await EnabledVmAsync(factory);

        vm.OrderedLegs.Should().OnlyContain(l => l.DurationSeconds == null,
            "an Any/Air leg has no ground cache row ⇒ null duration ⇒ '—'");
    }

    [Fact]
    public async Task TwoLegs_DifferentModes_ResolveToDifferentCacheRows()
    {
        // P1→P2 is Drive; P2→P1 is Walk. Seed a row for EACH mode key with distinct
        // durations and assert each leg picks up its own mode's row — proving the
        // lookup is keyed by (From, To, Mode), not a single trip mode.
        var factory = Seed(mode1: TravelMode.Drive, mode2: TravelMode.Walk);
        await AddSegmentAsync(factory, 1, 2, TravelMode.Drive, 600);
        await AddSegmentAsync(factory, 2, 1, TravelMode.Walk, 1800);
        // A decoy row for the wrong mode on each pair must be ignored.
        await AddSegmentAsync(factory, 1, 2, TravelMode.Walk, 99999);
        await AddSegmentAsync(factory, 2, 1, TravelMode.Drive, 99999);

        await using var vm = await EnabledVmAsync(factory);

        var leg12 = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        var leg21 = vm.OrderedLegs.First(l => l.FromPoiId == 2 && l.ToPoiId == 1);
        leg12.DurationSeconds.Should().Be(600, "the Drive leg resolves the (1,2,Drive) row, not the decoy Walk row");
        leg21.DurationSeconds.Should().Be(1800, "the Walk leg resolves the (2,1,Walk) row, not the decoy Drive row");
    }

    [Fact]
    public async Task GroundLeg_WithRowOnlyUnderDifferentMode_IsNotTimed()
    {
        // The 1→2 leg is Drive, but the only cache row for the pair is Walk ⇒ the Drive
        // leg has no matching row ⇒ null duration (TRIP-CACHE-01: mode is part of the key).
        var factory = Seed(mode1: TravelMode.Drive, mode2: TravelMode.AnyAir);
        await AddSegmentAsync(factory, 1, 2, TravelMode.Walk, 600);

        await using var vm = await EnabledVmAsync(factory);

        var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        leg.DurationSeconds.Should().BeNull("a row under a different mode does not satisfy this leg's (From,To,Mode) key");
    }
}
