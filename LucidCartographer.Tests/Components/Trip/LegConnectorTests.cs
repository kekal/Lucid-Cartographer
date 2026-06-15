using System.Globalization;
using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using LucidCartographer.Tests;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests.Components;

/// <summary>
/// bUnit coverage for the Story 1.3 inter-row <see cref="LegConnector"/> — a
/// presentational component (NFR1) that renders the per-leg ↓ + travel time +
/// distance + fidelity badge, an Any/Air manual-minutes input, and a Manual-only
/// reset (↺). Asserts the uncomputed neutral "—" (UX-DR11), the reset's
/// Manual-only presence, and that the reset raises Vm.ClearManualLegTimeAsync.
/// </summary>
public class LegConnectorTests : BunitTestContext
{
    private const int CollectionId = 1;

    // A real, Trip-enabled VM over two placeable stops (P1, P2) under the given
    // travel mode, with the supplied directional AnyAir route segment seeded so a
    // leg carries the wanted duration/distance/fidelity. The LegConnector renders
    // off a TripLeg we pull from the VM's OrderedLegs so the names resolve.
    private static async Task<TripViewModel> EnabledVmAsync(string mode = TravelMode.AnyAir, RouteSegment? seg = null)
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = mode });
            for (var i = 1; i <= 2; i++)
            {
                db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
                db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
            }
            if (seg is not null)
            {
                db.RouteSegments.Add(seg);
            }
            await db.SaveChangesAsync();
        }
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 2);
        await vm.ToggleAsync();
        return vm;
    }

    private static RouteSegment EstimatedSeg(int from, int to, int seconds, double meters, string mode = TravelMode.AnyAir) => new()
    {
        FromPoiId = from, ToPoiId = to, TravelMode = mode,
        DurationSeconds = seconds, DistanceMeters = meters,
        Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow,
    };

    private static TripLeg Leg(TripViewModel vm, int from, int to) =>
        vm.OrderedLegs.First(l => l.FromPoiId == from && l.ToPoiId == to);

    [Fact]
    public async Task LegConnector_RendersGlyph_Time_Distance_AndFidelityBadge()
    {
        // Drive mode so no manual input clutters the line; an Estimated 1→2 leg.
        await using var vm = await EnabledVmAsync(mode: TravelMode.Drive, seg: EstimatedSeg(1, 2, 4800, 12000, TravelMode.Drive));
        var leg = Leg(vm, 1, 2);

        var cut = RenderComponent<LegConnector>(p => p
            .Add(x => x.Leg, leg)
            .Add(x => x.Vm, vm));

        // ↓ glyph (decorative).
        cut.Markup.Should().Contain("↓");
        // Travel time + distance via the shared formatters.
        cut.Markup.Should().Contain("1h 20m").And.Contain("12 km");
        cut.Find($"[aria-label=\"{string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegTravelTimeAria, "1h 20m")}\"]").Should().NotBeNull();
        cut.Find($"[aria-label=\"{string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegDistanceAria, "12 km")}\"]").Should().NotBeNull();
        // Fidelity badge with its provenance accessible name.
        var prov = string.Format(CultureInfo.CurrentCulture, UiStrings.TripFidelityAria, UiStrings.TripFidelityEstimated);
        cut.Find($"[aria-label=\"{prov}\"]").TextContent.Trim().Should().Be(UiStrings.TripFidelityEstimated);
    }

    [Fact]
    public async Task LegConnector_Uncomputed_ShowsEmDash_Neutral_NotError()
    {
        // No route segment ⇒ the leg has null duration/distance/fidelity (computing).
        await using var vm = await EnabledVmAsync(mode: TravelMode.Drive);
        var leg = Leg(vm, 1, 2);

        var cut = RenderComponent<LegConnector>(p => p
            .Add(x => x.Leg, leg)
            .Add(x => x.Vm, vm));

        // The time reads the em-dash unknown marker.
        var time = cut.Find($"[aria-label=\"{UiStrings.TripLegComputingAria}\"]");
        time.TextContent.Trim().Should().Be(UiStrings.TripLegTimeUnknown);
        // Neutral/muted tone (UX-DR11) — NEVER an error colour (red/tertiary).
        time.GetAttribute("class").Should().Contain("text-on-surface-variant");
        cut.Markup.Should().NotContain("text-red", "an uncomputed leg is neutral, not an error (UX-DR11)");
        // No fidelity badge for a null fidelity.
        var estProv = string.Format(CultureInfo.CurrentCulture, UiStrings.TripFidelityAria, UiStrings.TripFidelityEstimated);
        cut.FindAll($"[aria-label=\"{estProv}\"]").Should().BeEmpty();
    }

    [Fact]
    public async Task LegConnector_Reset_RenderedOnly_ForManualLeg()
    {
        // Manual override on 1→2 ⇒ the reset (↺) button is present, a real focusable
        // <button> with its UiStrings aria-label.
        await using var vm = await EnabledVmAsync(mode: TravelMode.AnyAir);
        await vm.SetManualLegTimeAsync(1, 2, minutes: 90);
        var manualLeg = Leg(vm, 1, 2);
        manualLeg.Fidelity.Should().Be(Fidelity.Manual);

        var resetAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegResetManualAria, "P1");
        var cut = RenderComponent<LegConnector>(p => p
            .Add(x => x.Leg, manualLeg)
            .Add(x => x.Vm, vm));

        var reset = cut.Find($"button[aria-label=\"{resetAria}\"]");
        reset.GetAttribute("type").Should().Be("button");
        // Hidden at rest, revealed on hover/focus.
        reset.GetAttribute("class").Should().Contain("opacity-0").And.Contain("group-hover:opacity-100").And.Contain("focus:opacity-100");
    }

    [Fact]
    public async Task LegConnector_Reset_Absent_ForNonManualLeg()
    {
        // An Estimated leg has nothing to reset ⇒ no dead reset button (Story 3.5
        // generalises reset to any leg; not here).
        await using var vm = await EnabledVmAsync(mode: TravelMode.Drive, seg: EstimatedSeg(1, 2, 4800, 12000, TravelMode.Drive));
        var leg = Leg(vm, 1, 2);

        var cut = RenderComponent<LegConnector>(p => p
            .Add(x => x.Leg, leg)
            .Add(x => x.Vm, vm));

        cut.Markup.Should().NotContain("↺", "no reset affordance on a non-manual leg");
        var resetAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegResetManualAria, "P1");
        cut.FindAll($"button[aria-label=\"{resetAria}\"]").Should().BeEmpty();
    }

    [Fact]
    public async Task LegConnector_ResetClick_InvokesClearManualLegTime_RevertsToComputed()
    {
        await using var vm = await EnabledVmAsync(mode: TravelMode.AnyAir);
        await vm.SetManualLegTimeAsync(1, 2, minutes: 90);
        vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2).Fidelity.Should().Be(Fidelity.Manual);

        var resetAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegResetManualAria, "P1");
        var cut = RenderComponent<LegConnector>(p => p
            .Add(x => x.Leg, Leg(vm, 1, 2))
            .Add(x => x.Vm, vm));

        await cut.Find($"button[aria-label=\"{resetAria}\"]").ClickAsync(new MouseEventArgs());

        // Clearing deletes the Manual cache row ⇒ the leg reverts off Manual.
        vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2)
            .Fidelity.Should().NotBe(Fidelity.Manual, "reset cleared the manual override via Vm.ClearManualLegTimeAsync");
    }

    [Fact]
    public async Task LegConnector_ManualInput_Present_UnderAnyAir_Absent_UnderDrive()
    {
        var manualAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripManualMinutesAria, "P1");

        await using var anyAir = await EnabledVmAsync(mode: TravelMode.AnyAir);
        var cutAir = RenderComponent<LegConnector>(p => p
            .Add(x => x.Leg, Leg(anyAir, 1, 2))
            .Add(x => x.Vm, anyAir));
        cutAir.FindAll($"input[aria-label=\"{manualAria}\"]").Should().NotBeEmpty("Any/Air legs carry the manual minutes input");

        await using var drive = await EnabledVmAsync(mode: TravelMode.Drive, seg: EstimatedSeg(1, 2, 4800, 12000, TravelMode.Drive));
        var cutDrive = RenderComponent<LegConnector>(p => p
            .Add(x => x.Leg, Leg(drive, 1, 2))
            .Add(x => x.Vm, drive));
        cutDrive.FindAll($"input[aria-label=\"{manualAria}\"]").Should().BeEmpty("the manual input is hidden under non-Any/Air modes");
    }
}
