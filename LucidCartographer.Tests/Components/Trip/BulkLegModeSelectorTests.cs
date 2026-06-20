using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests.Components;

/// <summary>
/// TRIP-BULKMODE-01: bUnit coverage for the header bulk travel-mode selector. The
/// control is presentational — it raises Vm.SetAllLegsModeAsync only. Critically it is
/// ENABLED even on an all-Any/Air trip (IsAnyLegComputing true), which is exactly its
/// reason to exist; it does NOT mirror the Sort/Recompute disable.
/// </summary>
public class BulkLegModeSelectorTests : BunitTestContext
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed(int stops, IReadOnlyDictionary<int, string?>? modes = null)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.AnyAir });
        for (var i = 1; i <= stops; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            var mode = modes is not null && modes.TryGetValue(i, out var m) ? m : null;
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId, OutgoingTravelMode = mode });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task<(TripViewModel Vm, IDbContextFactory<AppDbContext> Factory)> EnabledVmAsync(
        IDbContextFactory<AppDbContext> factory, int placeable)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return (vm, factory);
    }

    [Fact]
    public async Task Selector_IsEnabled_EvenWhenAllLegsAnyAir()
    {
        // All-Any/Air trip ⇒ IsAnyLegComputing is true. The control must still be enabled.
        var (vm, _) = await EnabledVmAsync(Seed(stops: 2), placeable: 2);
        vm.IsAnyLegComputing.Should().BeTrue("an all-Any/Air trip has no settled leg times");

        var cut = RenderComponent<BulkLegModeSelector>(p => p.Add(x => x.Vm, vm));

        var button = cut.Find($"button[aria-label=\"{UiStrings.TripBulkModeAria}\"]");
        button.HasAttribute("disabled").Should().BeFalse("the bulk selector is the remedy for uncomputed legs, not gated on compute state");
    }

    [Fact]
    public async Task ChoosingMode_PersistsToAllLegs_OverwriteOff()
    {
        var (vm, factory) = await EnabledVmAsync(Seed(stops: 2), placeable: 2);

        var cut = RenderComponent<BulkLegModeSelector>(p => p.Add(x => x.Vm, vm));
        cut.Find($"button[aria-label=\"{UiStrings.TripBulkModeAria}\"]").Click(); // open
        cut.Find($"button[aria-label=\"{UiStrings.TripTravelModeDrive}\"]").Click(); // pick Drive

        await using var db = await factory.CreateDbContextAsync();
        var modes = await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == CollectionId)
            .Select(ci => ci.OutgoingTravelMode)
            .ToListAsync();
        modes.Should().OnlyContain(m => m == TravelMode.Drive, "picking Drive assigns it to every from-stop");
    }

    [Fact]
    public async Task OverwriteCheckbox_On_ReplacesExplicitModes()
    {
        var (vm, factory) = await EnabledVmAsync(
            Seed(stops: 2, modes: new Dictionary<int, string?> { [1] = TravelMode.Walk }), placeable: 2);

        var cut = RenderComponent<BulkLegModeSelector>(p => p.Add(x => x.Vm, vm));
        cut.Find($"button[aria-label=\"{UiStrings.TripBulkModeAria}\"]").Click(); // open
        cut.Find($"input[aria-label=\"{UiStrings.TripBulkModeOverwriteAria}\"]").Change(true); // overwrite on
        cut.Find($"button[aria-label=\"{UiStrings.TripTravelModeDrive}\"]").Click(); // pick Drive

        await using var db = await factory.CreateDbContextAsync();
        var p1 = await db.PoiCollectionItems.FirstAsync(ci => ci.PoiCollectionId == CollectionId && ci.PoiId == 1);
        p1.OutgoingTravelMode.Should().Be(TravelMode.Drive, "overwrite-on replaces the explicit Walk mode");
    }

    [Fact]
    public async Task Selector_MountedInStopListHeader_WhenLegsPresent()
    {
        var (vm, _) = await EnabledVmAsync(Seed(stops: 2), placeable: 2);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.FindAll($"button[aria-label=\"{UiStrings.TripBulkModeAria}\"]")
            .Should().ContainSingle("the bulk selector renders in the Trip stops header when legs exist");
    }
}
