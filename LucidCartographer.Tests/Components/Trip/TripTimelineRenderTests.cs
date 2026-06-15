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

    // === Story 4.5 (FR-31/32/33, UX-DR7): finish designation & roundtrip readout ===
    // The footer's three states across the live VM+service, all copy via UiStrings.

    // AC1 — no Finish (roundtrip): footer reads "Return to start", never "Finish".
    [Fact]
    public async Task FinishFooter_Roundtrip_ReadsReturnToStart_NotFinish()
    {
        var factory = Seed(placeable: 3);
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 3, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 3, 1, 3600, Fidelity.Manual); // closing leg
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        vm.IsRoundtrip.Should().BeTrue("no Finish ⇒ the default roundtrip shape");
        // The footer label is the roundtrip "Return to start" — distinct from "Finish".
        // Scope to the footer span (the per-row finish controls also carry "Finish" in
        // their aria/title, so a whole-markup NotContain would be a false negative).
        FinishFooterLabel(cut).Should().Be(UiStrings.TripTimelineFinishLabel,
            "a roundtrip footer reads 'Return to start', never 'Finish' (UX-DR7)");
    }

    // AC2 — press Finish: stop pinned to N, footer switches to "Finish" + that stop's
    // arrival, and NEVER "Return to start" while a Finish is set.
    [Fact]
    public async Task FinishFooter_SetFinish_ReadsFinish_PinnedToN_NeverReturnToStart()
    {
        var factory = Seed(placeable: 3);
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 3, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // Designate stop 3 the Finish (an open path over 3 stops).
        await cut.InvokeAsync(() => vm.SetFinishAsync(3));
        cut.Render();

        vm.IsRoundtrip.Should().BeFalse("a distinct Finish opens the path");
        vm.OrderedStops[^1].PoiId.Should().Be(3, "the Finish is pinned to Order N");
        vm.OrderedStops[^1].IsFinish.Should().BeTrue();

        // Footer now reads "Finish" and NEVER "Return to start" while the Finish is set.
        FinishFooterLabel(cut).Should().Be(UiStrings.TripTimelineFinishOpenLabel,
            "an open path footer reads 'Finish', never 'Return to start' while a Finish is set (UX-DR7)");
        cut.Markup.Should().NotContain(UiStrings.TripTimelineFinishLabel,
            "'Return to start' is the roundtrip-only footer label — it appears nowhere else");
    }

    // AC3 — unset Finish: footer reverts to "Return to start"; order/dwell preserved
    // (no data loss). Dwell survival is asserted at the VM level too.
    [Fact]
    public async Task FinishFooter_ClearFinish_RevertsToReturnToStart_NoDataLoss()
    {
        var factory = Seed(placeable: 3);
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 3, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 3, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory, placeable: 3);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));
        // Set a dwell on stop 2 so we can prove it survives the Finish round-trip.
        await cut.InvokeAsync(() => vm.SetDwellMinutesAsync(2, 30));

        await cut.InvokeAsync(() => vm.SetFinishAsync(3));
        cut.Render();
        FinishFooterLabel(cut).Should().Be(UiStrings.TripTimelineFinishOpenLabel);

        await cut.InvokeAsync(() => vm.ClearFinishAsync());
        cut.Render();

        vm.IsRoundtrip.Should().BeTrue("clearing the Finish returns to a roundtrip");
        FinishFooterLabel(cut).Should().Be(UiStrings.TripTimelineFinishLabel, "the footer reverts to 'Return to start'");

        // No data loss: order stays 1..3 contiguous and the dwell on stop 2 survives.
        vm.OrderedStops.Select(s => s.OrderIndex).Should().BeEquivalentTo(new[] { 1, 2, 3 });
        vm.StopRows.First(r => r.PoiId == 2).DwellMinutes.Should().Be(30, "dwell survives the Finish set/clear");
    }

    // Story 4.5 (date-aware footer, ties to 4.2): the FOOTER readout itself shows its
    // date when the return/finish arrival lands on a later calendar day than the start.
    [Fact]
    public async Task FinishFooter_MultiDayReturn_ShowsLocaleDate()
    {
        var factory = Seed(placeable: 2);
        // Start 22:00; out 3h ⇒ arrival(2) 01:00 next day; closing 3h ⇒ return 04:00 next day.
        await AddSegmentAsync(factory, 1, 2, 3 * 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3 * 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var start = new DateTime(2026, 6, 15, 22, 0, 0);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));
        await cut.InvokeAsync(() => vm.SetTripStartTimeAsync(start));
        cut.Render();

        // The return-to-start arrival is 06:00... on the NEXT day — its locale date must
        // appear in the footer's aria-label (which carries the full date-aware ArrivalText).
        var nextDay = start.AddHours(6); // 2026-06-16 04:00
        nextDay.Date.Should().BeAfter(start.Date);
        // The footer aria is "Trip ends at {0}" — match on the stable prefix before {0}
        // so this targets the dedicated FOOTER readout, not a per-row arrival.
        var finishAriaPrefix = UiStrings.TripTimelineFinishAria[..UiStrings.TripTimelineFinishAria.IndexOf("{0}", StringComparison.Ordinal)];
        var footerLabel = cut.FindAll("[aria-label]")
            .Select(e => e.GetAttribute("aria-label") ?? string.Empty)
            .First(a => a.StartsWith(finishAriaPrefix, StringComparison.Ordinal));
        footerLabel.Should().Contain(nextDay.ToString("d", CultureInfo.CurrentCulture),
            "the footer return/finish arrival is date-aware on a multi-day trip (passes Vm.TripStartTime)");
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
        cut.Markup.Should().NotContain(UiStrings.TripOverLimitLabel, "120m under a 180m limit â‡’ no over-limit");

        // Over budget â‡’ flag, amber tone, NEVER an error-red token.
        await cut.InvokeAsync(() => vm.SetTimeBudgetMinutesAsync(60));
        cut.Render();
        // Story 4.3: the desktop chip is renamed "Over limit" (amber soft-warn, never red).
        cut.Markup.Should().Contain(UiStrings.TripOverLimitLabel, "120m over a 60m limit â‡’ over-limit");
        cut.Markup.Should().Contain("text-amber-600", "the over-limit chip is a soft amber warn");
        cut.Markup.Should().NotContain("text-tertiary", "the over-limit chip is NEVER red/tertiary");
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

        cut.Markup.Should().NotContain(UiStrings.TripOverLimitLabel, "an uncertain total never shows a false over-limit");
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

    // === Story 4.3 (FR-28/29): time-limit duration (HH:MM) + finish-by deadline ===

    [Fact]
    public async Task TripStopList_TimeLimitDuration_IsHhmm_AndPersistsMinutes()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // The desktop limit is an HH:MM duration picker (native time input), not raw minutes.
        var input = cut.Find($"input[aria-label=\"{UiStrings.TripTimeLimitAria}\"]");
        input.GetAttribute("type").Should().Be("time", "the desktop time limit is an HH:MM duration picker (FR-28)");

        // Entering "02:00" persists 120 canonical minutes (HH:MM → minutes at the UI edge).
        await input.ChangeAsync(new ChangeEventArgs { Value = "02:00" });
        vm.TimeBudgetMinutes.Should().Be(120, "02:00 ⇒ 120 minutes");
    }

    [Fact]
    public async Task TripStopList_TimeLimitDuration_RoundTripsMinutes_Hhmm()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);
        // 90 minutes ⇒ "01:30" in the HH:MM control.
        await vm.SetTimeBudgetMinutesAsync(90);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var input = cut.Find($"input[aria-label=\"{UiStrings.TripTimeLimitAria}\"]");
        input.GetAttribute("value").Should().Be("01:30", "90 minutes renders as the HH:MM duration 01:30");
    }

    [Fact]
    public async Task TripStopList_TimeLimitDuration_Cleared_NullsVm()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);
        await vm.SetTimeBudgetMinutesAsync(90);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var input = cut.Find($"input[aria-label=\"{UiStrings.TripTimeLimitAria}\"]");
        await input.ChangeAsync(new ChangeEventArgs { Value = "" });
        vm.TimeBudgetMinutes.Should().BeNull("clearing the duration clears the limit");
    }

    [Fact]
    public async Task TripStopList_TimeLimitDuration_OverDayLimit_RendersEmpty()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);
        // A >24h limit (2880 min) — only representable via the deadline path; the HH:MM
        // control can't show it, so its value is empty (AC4).
        await vm.SetTimeBudgetMinutesAsync(2880);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var input = cut.Find($"input[aria-label=\"{UiStrings.TripTimeLimitAria}\"]");
        input.GetAttribute("value").Should().BeNullOrEmpty("a >24h limit can't be shown in the HH:MM control");
    }

    [Fact]
    public async Task TripStopList_FinishByDeadline_DisabledWithoutStart()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // No start ⇒ the finish-by deadline input is disabled (it needs a start) + a hint shows.
        var input = cut.Find($"input[aria-label=\"{UiStrings.TripFinishByAria}\"]");
        input.HasAttribute("disabled").Should().BeTrue("the finish-by deadline requires a start time");
        cut.Markup.Should().Contain(UiStrings.TripFinishByNeedsStartHint, "a hint explains the deadline needs a start");
    }

    [Fact]
    public async Task TripStopList_FinishByDeadline_ComputesMinutesOnce_FromDeadlineMinusStart()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);
        var start = new DateTime(2026, 6, 4, 9, 0, 0);
        await vm.SetTripStartTimeAsync(start);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var input = cut.Find($"input[aria-label=\"{UiStrings.TripFinishByAria}\"]");
        input.HasAttribute("disabled").Should().BeFalse("a start is set ⇒ the deadline input is enabled");

        // Deadline 4h after start ⇒ 240 minutes (computed once: deadline − start).
        await input.ChangeAsync(new ChangeEventArgs { Value = "2026-06-04T13:00" });
        vm.TimeBudgetMinutes.Should().Be(240, "13:00 − 09:00 = 240 minutes, computed once");
    }

    [Fact]
    public async Task TripStopList_FinishByDeadline_MultiDay_ProducesOver24hLimit()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);
        // The >24h path (AC4): a multi-day deadline yields >1440 minutes.
        var start = new DateTime(2026, 6, 4, 9, 0, 0);
        await vm.SetTripStartTimeAsync(start);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var input = cut.Find($"input[aria-label=\"{UiStrings.TripFinishByAria}\"]");
        // Two days after start ⇒ 2 × 24 × 60 = 2880 minutes (>1440 — the multi-day horizon).
        await input.ChangeAsync(new ChangeEventArgs { Value = "2026-06-06T09:00" });
        vm.TimeBudgetMinutes.Should().Be(2880, "a 2-day finish-by deadline yields a >24h limit (2880 min)");
    }

    [Fact]
    public async Task TripStopList_FinishByDeadline_DoesNotRecompute_WhenStartLaterChanges()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);
        await vm.SetTripStartTimeAsync(new DateTime(2026, 6, 4, 9, 0, 0));

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // Set the deadline ⇒ 240 minutes persisted (computed once).
        var deadline = cut.Find($"input[aria-label=\"{UiStrings.TripFinishByAria}\"]");
        await deadline.ChangeAsync(new ChangeEventArgs { Value = "2026-06-04T13:00" });
        vm.TimeBudgetMinutes.Should().Be(240);

        // TRIP-SCHEDULE-01: changing the start LATER does NOT recompute the stored limit —
        // only the resulting minutes were persisted, never the deadline itself.
        await cut.InvokeAsync(() => vm.SetTripStartTimeAsync(new DateTime(2026, 6, 4, 7, 0, 0)));
        cut.Render();
        vm.TimeBudgetMinutes.Should().Be(240, "the limit is fixed minutes; it never recomputes when the start changes");
    }

    [Fact]
    public async Task TripStopList_TimeLimitCopy_ReadsTimeLimit_AndOverLimit()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 3600, Fidelity.Manual);
        await AddSegmentAsync(factory, 2, 1, 3600, Fidelity.Manual);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Markup.Should().Contain(UiStrings.TripTimeLimitLabel, "the desktop control reads 'Time limit'");
        UiStrings.TripTimeLimitLabel.Should().Be("Time limit");
        UiStrings.TripOverLimitLabel.Should().Be("Over limit");

        // Over-limit chip reads "Over limit".
        await cut.InvokeAsync(() => vm.SetTimeBudgetMinutesAsync(60)); // total 120 > 60
        cut.Render();
        cut.Markup.Should().Contain(UiStrings.TripOverLimitLabel);
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

    // Story 4.5: reads the finish/return FOOTER label only (the leading <span> of the
    // footer row whose value span carries the "Trip ends at …" aria). Scoped to the
    // footer so the per-row finish controls' "Finish" aria/title can't leak in.
    private static string FinishFooterLabel(IRenderedComponent<TripStopList> cut)
    {
        var finishAriaPrefix = UiStrings.TripTimelineFinishAria[
            ..UiStrings.TripTimelineFinishAria.IndexOf("{0}", StringComparison.Ordinal)];
        var valueSpan = cut.FindAll("[aria-label]")
            .First(e => (e.GetAttribute("aria-label") ?? string.Empty).StartsWith(finishAriaPrefix, StringComparison.Ordinal));
        // The footer is "<div><span>{label}</span><span aria-label='Trip ends at …'>…</span></div>".
        return valueSpan.ParentElement!.Children[0].TextContent.Trim();
    }
}
