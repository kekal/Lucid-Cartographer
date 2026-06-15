using System.Globalization;
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
/// Story 3.4 (TRIP-LEGMODE-01, FR-19/21/23, UX-DR3/DR11): bUnit coverage for the
/// per-leg mode pill. The pill shows the leg's mode when set; a NEUTRAL outline
/// "Any — set mode" pill (no error colour) when Any/Air; opens a menu of the four
/// modes with the active one checked; selecting raises Vm.SetLegModeAsync.
/// </summary>
public class LegModePillTests : BunitTestContext
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> SeedFactory(int placeable)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.AnyAir });
        for (var i = 1; i <= placeable; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task<TripViewModel> EnabledVmAsync(int placeable)
    {
        var factory = SeedFactory(placeable);
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(
            ordering, factory, writeLock,
            new TravelTimeTrigger(), new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return vm;
    }

    // The leg departing P1 (1â†’2) for the active VM.
    private static TripLeg LegFromP1(TripViewModel vm) =>
        vm.OrderedLegs.First(l => l.FromPoiId == 1);

    [Fact]
    public async Task Pill_ShowsModeLabel_WhenSet()
    {
        await using var vm = await EnabledVmAsync(placeable: 2);
        await vm.SetLegModeAsync(1, TravelMode.Drive);

        var cut = RenderComponent<LegModePill>(p => p
            .Add(x => x.Leg, LegFromP1(vm))
            .Add(x => x.Vm, vm));

        var pillAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegModePillAria, "P1");
        var pill = cut.Find($"button[aria-label=\"{pillAria}\"]");
        pill.TextContent.Should().Contain(UiStrings.TripTravelModeDrive, "a set leg shows its mode label");
        // A set pill uses the primary tint, not an outline-only neutral pill.
        pill.GetAttribute("class").Should().Contain("text-primary");
    }

    [Fact]
    public async Task Pill_AnyAir_ShowsNeutralOutline_NoErrorColour()
    {
        await using var vm = await EnabledVmAsync(placeable: 2);
        // Leg defaults to Any/Air (OutgoingTravelMode null â‡’ AnyAir).
        var cut = RenderComponent<LegModePill>(p => p
            .Add(x => x.Leg, LegFromP1(vm))
            .Add(x => x.Vm, vm));

        var pillAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegModePillAria, "P1");
        var pill = cut.Find($"button[aria-label=\"{pillAria}\"]");
        pill.TextContent.Should().Contain(UiStrings.TripLegModeAnySetMode, "an undefined leg prompts 'Any â€” set mode'");

        var cls = pill.GetAttribute("class") ?? string.Empty;
        cls.Should().Contain("border", "the undefined pill is OUTLINE ONLY");
        // UX-DR11: NEVER an error colour. Assert no red/error/tertiary token leaks in.
        cls.Should().NotContainAny("text-red", "bg-red", "error", "tertiary");
    }

    [Fact]
    public async Task Pill_Click_OpensMenu_WithFourModes_ActiveChecked()
    {
        await using var vm = await EnabledVmAsync(placeable: 2);
        await vm.SetLegModeAsync(1, TravelMode.Walk);

        var cut = RenderComponent<LegModePill>(p => p
            .Add(x => x.Leg, LegFromP1(vm))
            .Add(x => x.Vm, vm));

        var pillAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegModePillAria, "P1");
        cut.Find($"button[aria-label=\"{pillAria}\"]").Click();

        var menu = cut.Find("[role=menu]");
        var items = menu.QuerySelectorAll("[role=menuitemradio]");
        items.Should().HaveCount(4, "the menu offers Walk / Drive / Cycle / Any-Air");

        // The active (Walk) item is checked; the others are not.
        cut.Find($"[role=menuitemradio][aria-label=\"{UiStrings.TripTravelModeWalk}\"]")
            .GetAttribute("aria-checked").Should().Be("true");
        cut.Find($"[role=menuitemradio][aria-label=\"{UiStrings.TripTravelModeDrive}\"]")
            .GetAttribute("aria-checked").Should().Be("false");
    }

    [Fact]
    public async Task Pill_SelectingMode_RaisesSetLegMode()
    {
        await using var vm = await EnabledVmAsync(placeable: 2);
        var cut = RenderComponent<LegModePill>(p => p
            .Add(x => x.Leg, LegFromP1(vm))
            .Add(x => x.Vm, vm));

        var pillAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegModePillAria, "P1");
        cut.Find($"button[aria-label=\"{pillAria}\"]").Click();
        cut.Find($"[role=menuitemradio][aria-label=\"{UiStrings.TripTravelModeCycle}\"]").Click();

        cut.WaitForAssertion(() =>
            vm.OrderedLegs.First(l => l.FromPoiId == 1).Mode.Should().Be(TravelMode.Cycle,
                "selecting a menu mode raises Vm.SetLegModeAsync(FromPoiId, mode)"));
    }
}
