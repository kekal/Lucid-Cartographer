using System;
using System.Globalization;
using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 2.1 (TRIP-RECONCILE-01): the round-once display edge. <see cref="TravelTimeFormatting.DisplayMinutes"/>
/// is the SOLE rounding function (round-half-up); <see cref="TravelTimeFormatting.Duration"/>
/// presents those same rounded minutes (not a truncation of seconds/60), so per-leg
/// figures and the trip total reconcile. The unit text is unchanged here (the "m"→"min"
/// change is Story 2.2 / FR-16) — only the rounding rule is under test.
/// </summary>
public class TravelTimeFormattingTests
{
    // === DisplayMinutes: the sole rounding edge (round-half-up) ===

    [Theory]
    [InlineData(0, 0)]
    [InlineData(29, 0)]   // < 30s rounds down to 0
    [InlineData(30, 1)]   // exactly half rounds UP (AwayFromZero)
    [InlineData(31, 1)]
    [InlineData(59, 1)]
    [InlineData(60, 1)]
    [InlineData(89, 1)]
    [InlineData(90, 2)]   // 1.5 min rounds UP to 2
    [InlineData(91, 2)]
    [InlineData(600, 10)]
    [InlineData(3600, 60)]
    public void DisplayMinutes_RoundsHalfUp(int seconds, int expectedMinutes) =>
        TravelTimeFormatting.DisplayMinutes(seconds).Should().Be(expectedMinutes);

    // === Duration: presents DisplayMinutes, NOT a truncation ===

    [Fact]
    public void Duration_RoundsUp_NotTruncates()
    {
        // 90s truncated would be "1 min"; round-once gives "2 min". (FR-16 unit "min".)
        TravelTimeFormatting.Duration(90).Should().Be("2 min");
        // 89s truncated would be "1 min"; round-once gives "1 min" (rounds to 1).
        TravelTimeFormatting.Duration(89).Should().Be("1 min");
        // 30s truncated would be "0 min"/"<1 min"; round-once gives "1 min" (half rounds up).
        TravelTimeFormatting.Duration(30).Should().Be("1 min");
    }

    [Fact]
    public void Duration_SubMinute_WhenRoundsToZeroButPositive()
    {
        // 29s rounds to 0 minutes but is positive ⇒ "<1m" (never a free "0m" hop).
        TravelTimeFormatting.Duration(29).Should().Be(UiStrings.TripDurationSubMinute);
        TravelTimeFormatting.Duration(1).Should().Be(UiStrings.TripDurationSubMinute);
    }

    [Fact]
    public void Duration_Zero_WhenExactlyZero() =>
        TravelTimeFormatting.Duration(0).Should().Be(UiStrings.TripDurationZero);

    [Fact]
    public void Duration_EmDash_WhenNullOrNegative()
    {
        TravelTimeFormatting.Duration(null).Should().Be(UiStrings.TripLegTimeUnknown);
        TravelTimeFormatting.Duration(-1).Should().Be(UiStrings.TripLegTimeUnknown);
    }

    [Fact]
    public void Duration_HoursAndMinutes_UseRoundedMinutes()
    {
        // 1h + 89s → 60 + 1 = 61 min → "1h 1 min" (89s rounds to 1).
        TravelTimeFormatting.Duration(3600 + 89).Should().Be("1h 1 min");
        // 1h + 90s → 60 + 2 = 62 min → "1h 2 min" (90s rounds up to 2).
        TravelTimeFormatting.Duration(3600 + 90).Should().Be("1h 2 min");
    }

    // === Story 4.2 (FR-27, UX-DR12): date-aware multi-day arrivals ===
    //
    // A wall-clock arrival on a LATER calendar day than the trip start shows its DATE
    // alongside the time (locale-driven, no hard-coded order). Same-day arrivals stay
    // time-only; no trip start ⇒ relative offset only. Qualifier/"~" markers preserved.

    private const int Offset2h15 = (2 * 3600) + (15 * 60); // +2h 15 min

    [Fact]
    public void Arrival_SameDay_ShowsTimeOnly_Unchanged()
    {
        var start = new DateTime(2026, 6, 15, 9, 0, 0);
        var wallClock = start.AddSeconds(Offset2h15); // same day, 11:15

        var text = TravelTimeFormatting.Arrival(Offset2h15, wallClock, qualifier: null, isUnknown: false, tripStart: start);

        var expectedTime = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineWallClock, wallClock);
        text.Should().Be($"+2h 15 min {expectedTime}");
        // The locale date must NOT appear for a same-day arrival.
        text.Should().NotContain(wallClock.ToString("d", CultureInfo.CurrentCulture));
    }

    [Fact]
    public void Arrival_LaterDay_ShowsLocaleDateAndTime()
    {
        var start = new DateTime(2026, 6, 15, 22, 0, 0);   // late start
        var wallClock = start.AddHours(5);                  // 03:00 the NEXT day
        wallClock.Date.Should().BeAfter(start.Date);

        var offsetSeconds = (int)(wallClock - start).TotalSeconds;
        var text = TravelTimeFormatting.Arrival(offsetSeconds, wallClock, qualifier: null, isUnknown: false, tripStart: start);

        // Locale-robust expectation: build the date+time with the SAME CultureInfo and
        // format specifiers the formatter uses — never a hard-coded MM/dd order.
        var expectedClock = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineWallClockWithDate, wallClock, wallClock);
        text.Should().EndWith(expectedClock);
        text.Should().StartWith("+");
        // The date component must be present (distinguishes from the time-only path).
        text.Should().Contain(wallClock.ToString("d", CultureInfo.CurrentCulture));
    }

    [Fact]
    public void Arrival_LaterDay_PreservesQualifierAndApproxMarker()
    {
        var start = new DateTime(2026, 6, 15, 22, 0, 0);
        var wallClock = start.AddHours(6); // next day 04:00
        var offsetSeconds = (int)(wallClock - start).TotalSeconds;

        var text = TravelTimeFormatting.Arrival(offsetSeconds, wallClock, qualifier: Fidelity.Estimated, isUnknown: false, tripStart: start);

        text.Should().Contain("~");                 // approximation marker preserved
        text.Should().Contain(Fidelity.Estimated);  // "· Estimated" qualifier preserved
        text.Should().Contain(wallClock.ToString("d", CultureInfo.CurrentCulture)); // and the date
    }

    [Fact]
    public void Arrival_NoTripStart_RelativeOffsetOnly_Unchanged()
    {
        // No wall-clock and no tripStart ⇒ offset only; tripStart is irrelevant here.
        var text = TravelTimeFormatting.Arrival(Offset2h15, wallClock: null, qualifier: null, isUnknown: false, tripStart: null);
        text.Should().Be("+2h 15 min");
    }

    [Fact]
    public void ArrivalCompact_SameDay_ShowsTimeOnly_Unchanged()
    {
        var start = new DateTime(2026, 6, 15, 9, 0, 0);
        var wallClock = start.AddSeconds(Offset2h15);

        var text = TravelTimeFormatting.ArrivalCompact(Offset2h15, wallClock, qualifier: null, isUnknown: false, tripStart: start);

        var expectedTime = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineWallClock, wallClock);
        text.Should().Be($"+2h 15 min {expectedTime}");
        text.Should().NotContain(wallClock.ToString("d", CultureInfo.CurrentCulture));
    }

    [Fact]
    public void ArrivalCompact_LaterDay_ShowsLocaleDateAndTime()
    {
        var start = new DateTime(2026, 6, 15, 23, 30, 0);
        var wallClock = start.AddHours(2); // 01:30 next day
        var offsetSeconds = (int)(wallClock - start).TotalSeconds;

        var text = TravelTimeFormatting.ArrivalCompact(offsetSeconds, wallClock, qualifier: null, isUnknown: false, tripStart: start);

        var expectedClock = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineWallClockWithDate, wallClock, wallClock);
        text.Should().EndWith(expectedClock);
        text.Should().Contain(wallClock.ToString("d", CultureInfo.CurrentCulture));
    }

    [Fact]
    public void ArrivalCompact_LaterDay_KeepsApproxMarker_DropsQualifierWord()
    {
        var start = new DateTime(2026, 6, 15, 23, 0, 0);
        var wallClock = start.AddHours(3); // next day 02:00
        var offsetSeconds = (int)(wallClock - start).TotalSeconds;

        var text = TravelTimeFormatting.ArrivalCompact(offsetSeconds, wallClock, qualifier: Fidelity.Estimated, isUnknown: false, tripStart: start);

        text.Should().Contain("~");                                  // approximation marker kept
        text.Should().NotContain($"· {Fidelity.Estimated}");          // no verbose "· Estimated" word
        text.Should().Contain(wallClock.ToString("d", CultureInfo.CurrentCulture)); // date present
    }

    [Fact]
    public void ArrivalCompact_NoTripStart_RelativeOffsetOnly_Unchanged()
    {
        var text = TravelTimeFormatting.ArrivalCompact(Offset2h15, wallClock: null, qualifier: null, isUnknown: false, tripStart: null);
        text.Should().Be("+2h 15 min");
    }
}
