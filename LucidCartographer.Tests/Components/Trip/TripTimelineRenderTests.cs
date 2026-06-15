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
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests.Components;

/// <summary>
/// Story 2.6 (TRIP-TIMELINE-01, AC 2/3/4/5/6): bUnit coverage that TripStopList (desktop)
/// and MobileTripPanel (mobile) render the per-stop arrival (offset always; wall-clock
/// only with a start time; Estimated qualifier; "â€”" unknown), the finish/return readout,
/// the soft AMBER (never red) overrun flag shown only when over budget, and that the
/// start-time + budget inputs invoke the VM.
/// </summary>
public class TripTimelineRenderTests : BunitTestContext
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed(int placeable = 2)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.AnyAir,
        });
        for (var i = 1; i <= placeable; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task AddSegmentAsync(
        IDbContextFactory<AppDbContext> factory, int from, int to, int seconds, string fidelity)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = from, ToPoiId = to, TravelMode = TravelMode.AnyAir,
            DurationSeconds = seconds, DistanceMeters = 8000,
            Fidelity = fidelity, Source = fidelity == Fidelity.Manual ? "Manual" : "Mock", ComputedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<TripViewModel> EnabledVmAsync(IDbContextFactory<AppDbContext> factory, int placeable = 2)
    {
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

    // === Offset always; wall-clock only with a start time ===

    [Fact]
    public async Task TripStopList_RendersOffset_Always_WallClockOnlyWithStart()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        // No start time yet â‡’ offset present, no wall-clock colon time.
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));
        cut.Markup.Should().Contain("+", "the relative offset is always shown");
        cut.Markup.Should().NotContain("10:00", "no wall-clock without a start time");

        // Set a start time â‡’ wall-clock appears.
        await cut.InvokeAsync(() => vm.SetTripStartTimeAsync(new DateTime(2026, 6, 14, 9, 0, 0)));
        cut.Render();
        // arrival(2) = 9:00 + leg1(1h) = 10:00.
        cut.Markup.Should().Contain("10:00", "the wall-clock arrival shows once a start time is set");
    }

    [Fact]
    public async Task TripStopList_EstimatedArrival_ShowsQualifier()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Estimated);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Estimated);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // The ARRIVAL VALUE itself must carry the "~" approximation marker (offset-only
        // mode, the default) â€” not merely the per-leg Fidelity badge. A bare "+1h 0m"
        // here would be a confident-looking time over an Estimated leg (TRIP-TIMELINE-01).
        var approxOffset = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            UiStrings.TripTimelineEstimatedPrefix,
            string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.TripTimelineOffset, TravelTimeFormatting.Duration(3600)));
        cut.Markup.Should().Contain(approxOffset, "the estimated arrival keeps its '~' approximation marker, not a clean confident offset");
        // ...and the per-leg Estimated badge still names the provenance.
        cut.Markup.Should().Contain(UiStrings.TripFidelityEstimated, "an Estimated arrival is qualified honestly");
    }

    [Fact]
    public async Task TripStopList_UnknownArrival_ShowsEmDash()
    {
        var factory = Seed();
        // No segments â‡’ legs uncomputed â‡’ every arrival downstream of the first is unknown.
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // The arrival aria-label carries the em-dash unknown marker.
        var unknownAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineArrivalAria, UiStrings.TripTimelineUnknown);
        cut.FindAll($"[aria-label=\"{unknownAria}\"]").Should().NotBeEmpty("an unknown arrival renders the em-dash");
    }

    [Fact]
    public async Task TripStopList_RendersFinishReturnReadout()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // Roundtrip â‡’ "Return to start" finish readout at the end of the list.
        cut.Markup.Should().Contain(UiStrings.TripTimelineFinishLabel);
    }

    // === Story 4.2 (FR-27, UX-DR12): date-aware multi-day arrivals on BOTH surfaces ===

    [Fact]
    public async Task TripStopList_MultiDayArrival_ShowsLocaleDate()
    {
        var factory = Seed();
        // Start 22:00; a 3h leg ⇒ arrival(2) = 01:00 the NEXT calendar day.
        await AddSegmentAsync(factory, 1, 2, 3 * 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3 * 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var start = new DateTime(2026, 6, 15, 22, 0, 0);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));
        await cut.InvokeAsync(() => vm.SetTripStartTimeAsync(start));
        cut.Render();

        // The next-day arrival's locale short date must appear in the markup (no hard-coded order).
        var nextDay = start.AddHours(3); // 2026-06-16 01:00
        nextDay.Date.Should().BeAfter(start.Date);
        cut.Markup.Should().Contain(nextDay.ToString("d", CultureInfo.CurrentCulture),
            "a later-day arrival shows its locale date alongside the time (desktop)");
    }

    [Fact]
    public async Task MobileTripPanel_MultiDayArrival_ShowsLocaleDate()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3 * 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3 * 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var start = new DateTime(2026, 6, 15, 22, 0, 0);
        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));
        await cut.InvokeAsync(() => vm.SetTripStartTimeAsync(start));
        cut.Render();

        var nextDay = start.AddHours(3);
        cut.Markup.Should().Contain(nextDay.ToString("d", CultureInfo.CurrentCulture),
            "the shared formatter reaches mobile by nature (NFR5)");
    }

    // === Overrun flag: only when over budget, amber not red ===

    [Fact]
    public async Task TripStopList_OverrunFlag_ShownOnlyWhenOverBudget_AndAmberNotRed()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual); // 7200s total = 120m
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // Under budget â‡’ no flag.
        await cut.InvokeAsync(() => vm.SetTimeBudgetMinutesAsync(180));
        cut.Render();
        cut.Markup.Should().NotContain(UiStrings.TripBudgetOverrunLabel, "120m under a 180m budget â‡’ no overrun");

        // Over budget â‡’ flag, amber tone, NEVER an error-red token.
        await cut.InvokeAsync(() => vm.SetTimeBudgetMinutesAsync(60));
        cut.Render();
        cut.Markup.Should().Contain(UiStrings.TripBudgetOverrunLabel, "120m over a 60m budget â‡’ overrun");
        cut.Markup.Should().Contain("text-amber-600", "the overrun is a soft amber warn");
        cut.Markup.Should().NotContain("text-tertiary", "the overrun is NEVER red/tertiary");
        cut.Markup.Should().NotContain("text-error");
    }

    [Fact]
    public async Task TripStopList_UnknownTotal_NeverShowsOverrun()
    {
        var factory = Seed();
        // No segments â‡’ unknown total.
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));
        await cut.InvokeAsync(() => vm.SetTimeBudgetMinutesAsync(1));
        cut.Render();

        cut.Markup.Should().NotContain(UiStrings.TripBudgetOverrunLabel, "an uncertain total never shows a false overrun");
    }

    // === Inputs invoke the VM ===

    [Fact]
    public async Task TripStopList_StartTimeInput_InvokesVm()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // Story 4.1 (FR-26, RD10): the desktop start is a native datetime-local that
        // persists the FULL date+time the browser emits (ISO), not today + a time-of-day.
        var input = cut.Find($"input[aria-label=\"{UiStrings.TripStartTimeAria}\"]");
        input.GetAttribute("type").Should().Be("datetime-local", "the desktop start is a date+time picker (FR-26)");

        await input.ChangeAsync(new ChangeEventArgs { Value = "2026-06-04T09:30" });

        vm.TripStartTime.Should().NotBeNull("the start-time input drives the VM");
        vm.TripStartTime!.Value.Should().Be(new DateTime(2026, 6, 4, 9, 30, 0),
            "the full date is preserved, not paired with today's date");
    }

    [Fact]
    public async Task TripStopList_StartTimeInput_Cleared_NullsVm()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);
        await vm.SetTripStartTimeAsync(new DateTime(2026, 6, 4, 9, 30, 0));

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var input = cut.Find($"input[aria-label=\"{UiStrings.TripStartTimeAria}\"]");
        await input.ChangeAsync(new ChangeEventArgs { Value = "" });

        vm.TripStartTime.Should().BeNull("clearing the start input clears the VM start");
    }

    [Fact]
    public async Task TripStopList_StartTimeInput_RendersIsoWireValue()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);
        await vm.SetTripStartTimeAsync(new DateTime(2026, 6, 4, 9, 30, 0));

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // The datetime-local value attribute is the ISO/invariant wire format.
        var input = cut.Find($"input[aria-label=\"{UiStrings.TripStartTimeAria}\"]");
        input.GetAttribute("value").Should().Be("2026-06-04T09:30");
    }

    [Fact]
    public async Task TripStopList_BudgetInput_InvokesVm()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var input = cut.Find($"input[aria-label=\"{UiStrings.TripBudgetAria}\"]");
        await input.ChangeAsync(new ChangeEventArgs { Value = "240" });

        vm.TimeBudgetMinutes.Should().Be(240, "the budget input drives the VM");
    }

    // === Mobile surface mirrors ===

    [Fact]
    public async Task MobileTripPanel_RendersOffset_WallClockWithStart_FinishReadout()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        cut.Markup.Should().Contain("+", "offset always");
        cut.Markup.Should().Contain(UiStrings.TripTimelineFinishLabel, "roundtrip finish/return readout");

        await cut.InvokeAsync(() => vm.SetTripStartTimeAsync(new DateTime(2026, 6, 14, 9, 0, 0)));
        cut.Render();
        cut.Markup.Should().Contain("10:00", "wall-clock once a start time is set");
    }

    [Fact]
    public async Task MobileTripPanel_OverrunFlag_OnlyWhenOver_NeverRed()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        await cut.InvokeAsync(() => vm.SetTimeBudgetMinutesAsync(180));
        cut.Render();
        cut.Markup.Should().NotContain(UiStrings.TripBudgetOverrunLabel);

        await cut.InvokeAsync(() => vm.SetTimeBudgetMinutesAsync(60));
        cut.Render();
        cut.Markup.Should().Contain(UiStrings.TripBudgetOverrunLabel);
        cut.Markup.Should().NotContain("var(--error", "the overrun is never error-red");
    }

    [Fact]
    public async Task MobileTripPanel_BudgetInput_InvokesVm()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        var input = cut.Find($"input[aria-label=\"{UiStrings.TripBudgetAria}\"]");
        await input.ChangeAsync(new ChangeEventArgs { Value = "300" });

        vm.TimeBudgetMinutes.Should().Be(300);
    }
}
