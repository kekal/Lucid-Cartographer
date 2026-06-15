using FluentAssertions;
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
        // 90s truncated would be "1m"; round-once gives "2m".
        TravelTimeFormatting.Duration(90).Should().Be("2m");
        // 89s truncated would be "1m"; round-once gives "1m" (rounds to 1).
        TravelTimeFormatting.Duration(89).Should().Be("1m");
        // 30s truncated would be "0m"/"<1m"; round-once gives "1m" (half rounds up).
        TravelTimeFormatting.Duration(30).Should().Be("1m");
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
        // 1h + 89s → 60 + 1 = 61 min → "1h 1m" (89s rounds to 1).
        TravelTimeFormatting.Duration(3600 + 89).Should().Be("1h 1m");
        // 1h + 90s → 60 + 2 = 62 min → "1h 2m" (90s rounds up to 2).
        TravelTimeFormatting.Duration(3600 + 90).Should().Be("1h 2m");
    }
}
