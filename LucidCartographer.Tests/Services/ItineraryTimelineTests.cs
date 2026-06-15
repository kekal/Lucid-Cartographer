using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Trip;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 2.6 (TRIP-TIMELINE-01, AC 1/3/4/5): exhaustive coverage of the PURE
/// itinerary-timeline computation. This is where the honesty rule is proven — the
/// fidelity propagation (Unknown ⇒ "—" downstream, Estimated ⇒ qualifier, all-confident
/// ⇒ clean), the walk (Start dwell once, departure = arrival + dwell, next arrival =
/// departure + travel), roundtrip-vs-open shape, unplaceable dwell into the total only,
/// wall-clock only with a start, and the never-false budget overrun.
/// </summary>
public class ItineraryTimelineTests
{
    private static ItineraryStopInput Stop(int id, int? dwell = null) => new(id, dwell);

    private static ItineraryLegInput Leg(int? seconds, string? fidelity) => new(seconds, fidelity);

    // A confident (Measured) leg of the given duration.
    private static ItineraryLegInput Measured(int seconds) => Leg(seconds, Fidelity.Measured);

    // === AC1: the basic walk, Start dwell once, wall-clock with a start ===

    [Fact]
    public void BasicWalk_OpenPath_ComputesOffsets_StartDwellOnce_Clean()
    {
        // 3 stops, open path (not roundtrip ⇒ 2 legs). Dwell: Start=30m, mid=15m.
        // Leg1 = 1h (3600s), Leg2 = 30m (1800s). Measured ⇒ clean (no qualifier).
        var stops = new[] { Stop(1, dwell: 30), Stop(2, dwell: 15), Stop(3) };
        var legs = new[] { Measured(3600), Measured(1800) };

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: false, tripStart: null, budgetMinutes: null);

        r.Stops.Should().HaveCount(3);
        // arrival(1) = offset 0.
        r.Stops[0].OffsetSeconds.Should().Be(0);
        // arrival(2) = dwell(Start 30m=1800) + leg1(3600) = 5400.
        r.Stops[1].OffsetSeconds.Should().Be(1800 + 3600);
        // arrival(3) = arrival(2) + dwell(mid 15m=900) + leg2(1800) = 5400 + 900 + 1800 = 8100.
        r.Stops[2].OffsetSeconds.Should().Be(5400 + 900 + 1800);
        // All Measured ⇒ no qualifier anywhere, nothing unknown.
        r.Stops.Should().OnlyContain(a => a.QualifyingFidelity == null && !a.IsUnknown);
        // Open path: the finish/return IS the last stop's arrival (no extra return leg).
        r.FinishOrReturn!.OffsetSeconds.Should().Be(8100);
        r.FinishOrReturn.PoiId.Should().Be(3);
        r.IsTotalUnknown.Should().BeFalse();
        // Total = terminal offset (no unplaceable dwell). Note the final stop's own dwell
        // is NOT counted on an open path (you don't dwell after the trip ends).
        r.TotalSeconds.Should().Be(8100);
        r.TotalQualifyingFidelity.Should().BeNull();
    }

    [Fact]
    public void WallClock_PresentOnlyWithAStartTime()
    {
        var stops = new[] { Stop(1, dwell: 30), Stop(2) };
        var legs = new[] { Measured(3600), Measured(3600) }; // roundtrip ⇒ 2 legs
        var start = new DateTime(2026, 6, 14, 9, 0, 0);

        var withStart = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: true, start, budgetMinutes: null);
        var noStart = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: true, tripStart: null, budgetMinutes: null);

        // With a start, arrival(1) == start; arrival(2) == start + dwell(30m) + leg1(1h).
        withStart.Stops[0].ArrivalWallClock.Should().Be(start);
        withStart.Stops[1].ArrivalWallClock.Should().Be(start.AddMinutes(30).AddHours(1));
        // Offsets are present in both cases (always).
        noStart.Stops[0].OffsetSeconds.Should().Be(0);
        noStart.Stops[1].OffsetSeconds.Should().Be(1800 + 3600);
        // Without a start, NO wall-clock anywhere.
        noStart.Stops.Should().OnlyContain(a => a.ArrivalWallClock == null);
        noStart.FinishOrReturn!.ArrivalWallClock.Should().BeNull();
    }

    // === AC1: roundtrip vs open path ===

    [Fact]
    public void Roundtrip_ProducesDistinctReturnToStartArrival_ViaClosingLeg()
    {
        // 2 stops, roundtrip ⇒ 2 legs (1→2, 2→1). Start dwell 20m, stop2 dwell 10m.
        var stops = new[] { Stop(1, dwell: 20), Stop(2, dwell: 10) };
        var legs = new[] { Measured(3600), Measured(1800) }; // out 1h, back 30m

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: true, tripStart: null, budgetMinutes: null);

        // arrival(2) = dwell(Start 20m=1200) + leg1(3600) = 4800.
        r.Stops[1].OffsetSeconds.Should().Be(1200 + 3600);
        // The return-to-Start = arrival(2) + dwell(stop2 10m=600) + closingLeg(1800).
        r.FinishOrReturn!.PoiId.Should().Be(1, "the closing leg returns to Start");
        r.FinishOrReturn.OffsetSeconds.Should().Be(4800 + 600 + 1800);
        // Total = the return offset (final stop dwell IS counted on a roundtrip).
        r.TotalSeconds.Should().Be(4800 + 600 + 1800);
    }

    [Fact]
    public void OpenPath_EndsAtFinish_NoReturnLegConsumed()
    {
        // 2 stops, open path ⇒ exactly 1 leg. Finish entry mirrors the last arrival.
        var stops = new[] { Stop(1, dwell: 20), Stop(2) };
        var legs = new[] { Measured(3600) };

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: false, tripStart: null, budgetMinutes: null);

        r.Stops.Should().HaveCount(2);
        r.FinishOrReturn!.PoiId.Should().Be(2, "an open path ends at the Finish, not back at Start");
        r.FinishOrReturn.OffsetSeconds.Should().Be(1200 + 3600);
        r.TotalSeconds.Should().Be(1200 + 3600);
    }

    // === AC3: mixed fidelity ⇒ Estimated qualifier downstream + on the total ===

    [Fact]
    public void EstimatedLeg_QualifiesThatArrival_AllDownstream_AndTotal()
    {
        // 3 stops open path. Leg1 Measured (clean), Leg2 Estimated.
        var stops = new[] { Stop(1), Stop(2), Stop(3) };
        var legs = new[] { Measured(3600), Leg(1800, Fidelity.Estimated) };

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: false, tripStart: null, budgetMinutes: null);

        // arrival(1) and arrival(2) are upstream of the Estimated leg ⇒ still clean.
        r.Stops[0].QualifyingFidelity.Should().BeNull();
        r.Stops[1].QualifyingFidelity.Should().BeNull();
        // arrival(3) sums the Estimated leg ⇒ qualified Estimated, but still KNOWN.
        r.Stops[2].QualifyingFidelity.Should().Be(Fidelity.Estimated);
        r.Stops[2].IsUnknown.Should().BeFalse();
        r.Stops[2].OffsetSeconds.Should().Be(3600 + 1800);
        // Total carries the Estimated qualifier (lowest surviving rank).
        r.TotalQualifyingFidelity.Should().Be(Fidelity.Estimated);
        r.IsTotalUnknown.Should().BeFalse();
    }

    [Fact]
    public void ManualLeg_IsConfident_NoQualifier()
    {
        // Manual ranks equal to Measured (both confident) ⇒ a clean time, no qualifier.
        var stops = new[] { Stop(1), Stop(2) };
        var legs = new[] { Leg(3600, Fidelity.Manual), Leg(3600, Fidelity.Manual) };

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: true, tripStart: null, budgetMinutes: null);

        r.Stops.Should().OnlyContain(a => a.QualifyingFidelity == null && !a.IsUnknown);
        r.TotalQualifyingFidelity.Should().BeNull();
    }

    // === AC3/AC4: Unknown propagation — the honesty rule's core ===

    [Fact]
    public void PlaceholderLeg_MakesThatArrival_AndAllDownstream_AndFinish_AndTotal_Unknown()
    {
        // 4 stops open path (3 legs). Leg2 is Placeholder ⇒ Unknown from arrival(3) on.
        var stops = new[] { Stop(1), Stop(2), Stop(3), Stop(4) };
        var legs = new[]
        {
            Measured(3600),
            Leg(null, Fidelity.Placeholder), // a 2.2 Placeholder leg presents null duration
            Measured(3600),
        };

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: false, tripStart: new DateTime(2026, 1, 1, 8, 0, 0), budgetMinutes: null);

        // Upstream of the Placeholder leg is unaffected (known, clean).
        r.Stops[0].IsUnknown.Should().BeFalse();
        r.Stops[1].IsUnknown.Should().BeFalse();
        r.Stops[1].OffsetSeconds.Should().Be(3600);
        r.Stops[1].ArrivalWallClock.Should().NotBeNull();
        // arrival(3) sums the Placeholder ⇒ unknown (no offset, no wall-clock, no qualifier).
        r.Stops[2].IsUnknown.Should().BeTrue();
        r.Stops[2].OffsetSeconds.Should().BeNull();
        r.Stops[2].ArrivalWallClock.Should().BeNull();
        r.Stops[2].QualifyingFidelity.Should().BeNull();
        // Downstream stays unknown even though leg3 is Measured.
        r.Stops[3].IsUnknown.Should().BeTrue();
        // Finish + total are unknown.
        r.FinishOrReturn!.IsUnknown.Should().BeTrue();
        r.IsTotalUnknown.Should().BeTrue();
        r.TotalSeconds.Should().BeNull();
        r.TotalQualifyingFidelity.Should().BeNull();
    }

    [Fact]
    public void NullDurationLeg_IsUnknown_EvenIfFidelityLooksConfident()
    {
        // A null duration is Unknown uniformly (covers genuinely-uncomputed legs).
        var stops = new[] { Stop(1), Stop(2), Stop(3) };
        var legs = new[] { Measured(3600), Leg(null, Fidelity.Measured) };

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: false, tripStart: null, budgetMinutes: null);

        r.Stops[1].IsUnknown.Should().BeFalse();
        r.Stops[2].IsUnknown.Should().BeTrue("a null duration makes the arrival genuinely uncomputable");
        r.IsTotalUnknown.Should().BeTrue();
    }

    [Fact]
    public void Roundtrip_UnknownClosingLeg_MakesReturnAndTotalUnknown_RoutedArrivalsStillKnown()
    {
        var stops = new[] { Stop(1), Stop(2) };
        var legs = new[] { Measured(3600), Leg(null, Fidelity.Placeholder) }; // closing leg unknown

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: true, tripStart: null, budgetMinutes: null);

        // The routed arrivals (1 and 2) are known — only the return is unknown.
        r.Stops[0].IsUnknown.Should().BeFalse();
        r.Stops[1].IsUnknown.Should().BeFalse();
        r.FinishOrReturn!.IsUnknown.Should().BeTrue();
        r.IsTotalUnknown.Should().BeTrue();
    }

    // === AC4: unplaceable dwell adds to the total only ===

    [Fact]
    public void UnplaceableDwell_AddsToTotalOnly_NoArrival_NoTravel()
    {
        // 2 stops roundtrip, no routed dwell. Two unplaceable stops with dwell 40m + 20m.
        var stops = new[] { Stop(1), Stop(2) };
        var legs = new[] { Measured(3600), Measured(3600) };
        var unplaceable = new int?[] { 40, 20, null }; // null contributes zero

        var r = ItineraryTimeline.Compute(stops, legs, unplaceable, isRoundtrip: true, tripStart: null, budgetMinutes: null);

        // Only 2 routed arrivals — the unplaceable stops never appear in the sequence.
        r.Stops.Should().HaveCount(2);
        // Travel-only offset would be 3600 + 3600 = 7200; total adds 60m (3600s) of dwell.
        r.FinishOrReturn!.OffsetSeconds.Should().Be(7200, "unplaceable dwell does not shift routed arrivals");
        r.TotalSeconds.Should().Be(7200 + (40 + 20) * 60);
    }

    [Fact]
    public void UnplaceableDwell_OnUnknownTotal_DoesNotResurrectTheTotal()
    {
        var stops = new[] { Stop(1), Stop(2) };
        var legs = new[] { Leg(null, Fidelity.Placeholder), Measured(3600) };
        var unplaceable = new int?[] { 40 };

        var r = ItineraryTimeline.Compute(stops, legs, unplaceable, isRoundtrip: true, tripStart: null, budgetMinutes: null);

        r.IsTotalUnknown.Should().BeTrue();
        r.TotalSeconds.Should().BeNull("an unknown total stays unknown — unplaceable dwell can't make it precise");
    }

    // === AC5: budget overrun — never a false overrun ===

    [Fact]
    public void Budget_Over_FlagsOverrun()
    {
        var stops = new[] { Stop(1), Stop(2) };
        var legs = new[] { Measured(3600), Measured(3600) }; // total 7200s = 120m
        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: true, tripStart: null, budgetMinutes: 119);
        r.IsOverBudget.Should().BeTrue("120m total exceeds a 119m budget");
    }

    [Fact]
    public void Budget_UnderOrEqual_NoOverrun()
    {
        var stops = new[] { Stop(1), Stop(2) };
        var legs = new[] { Measured(3600), Measured(3600) }; // total 7200s = 120m
        var under = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: true, tripStart: null, budgetMinutes: 121);
        var equal = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: true, tripStart: null, budgetMinutes: 120);
        under.IsOverBudget.Should().BeFalse();
        equal.IsOverBudget.Should().BeFalse("exactly at budget is not over");
    }

    [Fact]
    public void Budget_None_NeverFlags()
    {
        var stops = new[] { Stop(1), Stop(2) };
        var legs = new[] { Measured(99999), Measured(99999) };
        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: true, tripStart: null, budgetMinutes: null);
        r.IsOverBudget.Should().BeFalse("no budget set ⇒ no overrun ever");
    }

    [Fact]
    public void Budget_UnknownTotal_NeverFlagsFalseOverrun()
    {
        // A Placeholder leg makes the total unknown; even with a tiny budget, no overrun.
        var stops = new[] { Stop(1), Stop(2) };
        var legs = new[] { Leg(null, Fidelity.Placeholder), Measured(3600) };
        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: true, tripStart: null, budgetMinutes: 1);
        r.IsOverBudget.Should().BeFalse("an uncertain total can never assert an overrun");
    }

    // === Story 2.1 (TRIP-RECONCILE-01): round-once reconciliation ===

    [Fact]
    public void Reconcile_SubMinuteRemainders_ArrivalsAndTotalSumRoundedPerLegMinutes()
    {
        // 3 stops open path, no dwell. Legs of 90s + 90s — each previously drifted:
        // raw seconds 90+90=180s (3m), but DisplayMinutes(90)=2 each ⇒ Σ=4 min.
        // Arrivals + total must derive from the rounded 2-min legs, NOT raw seconds.
        var stops = new[] { Stop(1), Stop(2), Stop(3) };
        var legs = new[] { Measured(90), Measured(90) };

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: false, tripStart: null, budgetMinutes: null);

        // Each displayed per-leg minute count (the sole rounding edge).
        var legMinutes = legs.Select(l => TravelTimeFormatting.DisplayMinutes(l.DurationSeconds!.Value)).ToArray();
        legMinutes.Should().Equal(2, 2);

        // arrival(2) = leg1 rounded = 2 min = 120s; arrival(3) = (2+2) min = 240s.
        r.Stops[1].OffsetSeconds.Should().Be(legMinutes[0] * 60);
        r.Stops[2].OffsetSeconds.Should().Be((legMinutes[0] + legMinutes[1]) * 60);
        // Total == Σ displayed per-leg minutes (×60). NOT Duration(180s)=3m.
        r.TotalSeconds.Should().Be(legMinutes.Sum() * 60);
        r.TotalSeconds.Should().Be(240, "round-once: 2+2 min, not the raw-seconds 3 min");
    }

    [Fact]
    public void Reconcile_WithDwell_ArrivalsAreRunningSumOfRoundedLegsPlusDwell()
    {
        // Roundtrip, 2 stops. Start dwell 15m, stop2 dwell 10m. Legs 150s out, 30s back.
        // DisplayMinutes(150)=3 (2.5 rounds up), DisplayMinutes(30)=1 (half rounds up).
        var stops = new[] { Stop(1, dwell: 15), Stop(2, dwell: 10) };
        var legs = new[] { Measured(150), Measured(30) };

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: true, tripStart: null, budgetMinutes: null);

        var leg1 = TravelTimeFormatting.DisplayMinutes(150); // 3
        var leg2 = TravelTimeFormatting.DisplayMinutes(30);  // 1
        leg1.Should().Be(3);
        leg2.Should().Be(1);

        // arrival(2) = Start dwell 15m + leg1 3m = 18m.
        r.Stops[1].OffsetSeconds.Should().Be((15 + leg1) * 60);
        // return = arrival(2) + stop2 dwell 10m + leg2 1m = (15+3+10+1) = 29m.
        r.FinishOrReturn!.OffsetSeconds.Should().Be((15 + leg1 + 10 + leg2) * 60);
        r.TotalSeconds.Should().Be((15 + leg1 + 10 + leg2) * 60);
    }

    [Fact]
    public void Reconcile_PartialTrip_AnyLeg_GivesUnknownArrivalAndTotalEmDash()
    {
        // An Any/Air Placeholder leg (null duration) ⇒ that arrival "—" and total unknown.
        var stops = new[] { Stop(1), Stop(2), Stop(3) };
        var legs = new[] { Measured(90), Leg(null, Fidelity.Placeholder) };

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: false, tripStart: null, budgetMinutes: null);

        r.Stops[1].IsUnknown.Should().BeFalse();
        r.Stops[2].IsUnknown.Should().BeTrue("an Any/Air leg makes the arrival uncomputable");
        r.IsTotalUnknown.Should().BeTrue();
        r.TotalSeconds.Should().BeNull("partial trip ⇒ total em-dash, no silent zero");
    }

    [Fact]
    public void Reconcile_MixedFidelity_QualifierPreservedOnRoundedTotal()
    {
        // Measured + Estimated sub-minute-remainder legs: rounding must not disturb the
        // surviving Estimated qualifier on the downstream arrival and the total.
        var stops = new[] { Stop(1), Stop(2), Stop(3) };
        var legs = new[] { Measured(90), Leg(150, Fidelity.Estimated) };

        var r = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: false, tripStart: null, budgetMinutes: null);

        r.Stops[1].QualifyingFidelity.Should().BeNull();
        r.Stops[2].QualifyingFidelity.Should().Be(Fidelity.Estimated);
        r.Stops[2].OffsetSeconds.Should().Be((TravelTimeFormatting.DisplayMinutes(90)
            + TravelTimeFormatting.DisplayMinutes(150)) * 60);
        r.TotalQualifyingFidelity.Should().Be(Fidelity.Estimated);
        r.IsTotalUnknown.Should().BeFalse();
    }

    [Fact]
    public void Reconcile_Budget_ComparesRoundedTotal()
    {
        // Legs 90s + 90s ⇒ rounded total 4 min (240s); raw would be 3 min (180s).
        // A 3-min budget is now EXCEEDED by the honest rounded 4-min total.
        var stops = new[] { Stop(1), Stop(2), Stop(3) };
        var legs = new[] { Measured(90), Measured(90) };

        var over = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: false, tripStart: null, budgetMinutes: 3);
        var ok = ItineraryTimeline.Compute(stops, legs, [], isRoundtrip: false, tripStart: null, budgetMinutes: 4);

        over.IsOverBudget.Should().BeTrue("the reconciled 4-min total exceeds a 3-min budget");
        ok.IsOverBudget.Should().BeFalse("exactly at the reconciled 4-min budget is not over");
    }

    // === Edge: fewer than two placeable stops ⇒ empty ===

    [Fact]
    public void FewerThanTwoStops_ReturnsEmpty()
    {
        var one = ItineraryTimeline.Compute([Stop(1)], [], [], isRoundtrip: true, tripStart: null, budgetMinutes: null);
        one.Should().Be(ItineraryTimelineResult.Empty);
        one.Stops.Should().BeEmpty();
        one.FinishOrReturn.Should().BeNull();
    }
}
