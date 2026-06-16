using System.Globalization;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-TRAVELTIME-01: UI-edge conversion of the canonical seconds/meters (AR-11)
/// into human-readable strings. Lives in the Trip slice but is purely
/// presentational — the only place duration/distance leave their canonical units.
/// </summary>
public static class TravelTimeFormatting
{
    /// <summary>
    /// TRIP-RECONCILE-01 (Story 2.1): the SOLE rounding edge for a per-leg duration.
    /// Rounds canonical seconds to whole minutes, nearest minute, round-half-up
    /// (e.g. 90s ⇒ 2 min, 30s ⇒ 1 min, 29s ⇒ 0). Every place that DISPLAYS or SUMS
    /// a leg's minutes must go through this one function so the displayed total
    /// equals Σ of the displayed per-leg minutes (FR-13/14/15). Canonical
    /// <see cref="Data.Entities.RouteSegment"/> seconds are NEVER mutated — only the
    /// display model rounds (NFR2).
    /// </summary>
    public static int DisplayMinutes(int seconds) =>
        (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Formats a duration in seconds as "Hh Mm" (e.g. "1h 20m"), "Mm" under an
    /// hour (e.g. "12m"), or "&lt;1m" for a sub-minute positive duration. A null
    /// or negative input yields the em-dash unknown marker.
    /// TRIP-RECONCILE-01 (Story 2.1): the minute figure is the round-once
    /// <see cref="DisplayMinutes"/> (round-half-up), NOT a truncation of seconds/60,
    /// so the displayed legs and the displayed total reconcile. The unit text is
    /// unchanged here — the "m"→"min" change is Story 2.2 (FR-16).
    /// </summary>
    public static string Duration(int? seconds)
    {
        if (seconds is not { } s || s < 0)
        {
            return UiStrings.TripLegTimeUnknown;
        }

        var totalMinutes = DisplayMinutes(s);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        if (hours > 0)
        {
            return string.Format(CultureInfo.CurrentCulture, UiStrings.TripDurationHoursMinutes, hours, minutes);
        }

        if (minutes > 0)
        {
            return string.Format(CultureInfo.CurrentCulture, UiStrings.TripDurationMinutes, minutes);
        }

        // TRIP-RECONCILE-01: DisplayMinutes rounded to 0 but the canonical seconds
        // are positive (< 30s) — show "<1 min" rather than collapsing to "0m" (a free
        // hop). This leg contributes 0 to the reconciled sum; the "<1 min" is an honest
        // 0-contribution annotation, not a special-cased sum term.
        return s > 0 ? UiStrings.TripDurationSubMinute : UiStrings.TripDurationZero;
    }

    // Trip stops compaction: the SOLE minutes⇄"HH:MM" edge shared by every duration
    // picker (dwell, time limit, per-leg movement). Hours are UNCAPPED — a duration is
    // not a clock time, so >24h is valid (e.g. a multi-day budget). Kept here next to the
    // other UI-edge conversions; the view-models stay in canonical minutes/seconds (NFR2).

    /// <summary>
    /// Formats a non-negative duration in MINUTES as an uncapped "HH:MM" wire value
    /// (e.g. 2880 ⇒ "48:00", 45 ⇒ "00:45", 0 ⇒ "00:00"). A negative input is floored
    /// to 0. Pair with <see cref="TryParseHhmm"/>.
    /// </summary>
    public static string FormatHhmm(int minutes)
    {
        if (minutes < 0)
        {
            minutes = 0;
        }

        return $"{minutes / 60:D2}:{minutes % 60:D2}";
    }

    /// <summary>
    /// Strictly parses an uncapped "HH:MM" duration into MINUTES. Requires 1–4 hour
    /// digits and exactly two minute digits 00–59 (e.g. "125:30" ⇒ 7530); rejects a
    /// bare number ("90"), a single-digit minute ("2:5"), a seconds-bearing value
    /// ("01:30:00"), and non-numeric text — leaving <paramref name="minutes"/> at 0 and
    /// returning false. Hours are unbounded (callers clamp to their own Max).
    /// </summary>
    public static bool TryParseHhmm(string? text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = HhmmPattern.Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var mins = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        minutes = hours * 60 + mins;
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex HhmmPattern =
        new(@"^(\d{1,4}):([0-5]\d)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Formats a distance in meters as "N km" with one decimal at/above 1 km
    /// (e.g. "12.3 km"), or "N m" below it (e.g. "850 m"). Null yields the
    /// em-dash unknown marker.
    /// </summary>
    public static string Distance(double? meters)
    {
        if (meters is not { } m || m < 0)
        {
            return UiStrings.TripLegTimeUnknown;
        }

        if (m >= 1000)
        {
            return string.Format(CultureInfo.CurrentCulture, UiStrings.TripDistanceKilometers, m / 1000);
        }

        return string.Format(CultureInfo.CurrentCulture, UiStrings.TripDistanceMeters, m);
    }

    // TRIP-TIMELINE-01 (Story 2.6): UI-edge formatting of an honest itinerary arrival.
    // The pure ItineraryTimeline computes seconds/DateTime/qualifier/IsUnknown; this is
    // the only place those become a display string (no hardcoded patterns — 2.1 lesson).

    /// <summary>
    /// Formats one arrival as a display string: the unknown em-dash when
    /// <paramref name="isUnknown"/>; otherwise the relative offset (e.g. "+2h 15m")
    /// optionally joined with the wall-clock (e.g. "14:10", shown only when
    /// <paramref name="wallClock"/> is set), and qualified with "· Estimated" / the
    /// "~" approximation prefix when <paramref name="qualifier"/> is set. Clean
    /// (all-confident) arrivals carry no qualifier.
    /// </summary>
    public static string Arrival(int? offsetSeconds, DateTime? wallClock, string? qualifier, bool isUnknown, DateTime? tripStart = null)
    {
        if (isUnknown || offsetSeconds is not { } secs)
        {
            return UiStrings.TripTimelineUnknown;
        }

        var offset = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineOffset, Duration(secs));

        if (wallClock is not { } clock)
        {
            // No trip start ⇒ relative offset only. An Estimated offset is still qualified.
            return qualifier is null
                ? offset
                : string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineQualified, offset, qualifier);
        }

        var clockText = WallClockText(clock, tripStart);
        if (qualifier is not null)
        {
            // "~14:10 · Estimated" — the "~" marks the approximation, the qualifier names it.
            clockText = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineEstimatedPrefix, clockText);
            clockText = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineQualified, clockText, qualifier);
        }

        // Both the relative offset and the wall-clock, space-joined (offset first).
        return $"{offset} {clockText}";
    }

    /// <summary>
    /// TRIP-TIMELINE-01: a COMPACT arrival for the cramped stop-row slot — the em-dash
    /// when unknown; otherwise the offset (and the wall-clock, prefixed "~" when
    /// estimated) WITHOUT the verbose "· Estimated" qualifier word, so a long qualified
    /// arrival never widens the row and starves the name span (the 2.2/2.5 panel-width
    /// lesson). Provenance is still conveyed honestly — the "~" marks the approximation,
    /// the per-leg Fidelity badge names it, and the full <see cref="Arrival"/> text is
    /// carried in the row's title/aria-label. NOT a loss of honesty: an estimated value
    /// is never shown as a clean confident time (the "~" + the em-dash rules still hold).
    /// </summary>
    public static string ArrivalCompact(int? offsetSeconds, DateTime? wallClock, string? qualifier, bool isUnknown, DateTime? tripStart = null)
    {
        if (isUnknown || offsetSeconds is not { } secs)
        {
            return UiStrings.TripTimelineUnknown;
        }

        var offset = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineOffset, Duration(secs));

        if (wallClock is not { } clock)
        {
            // Offset-only (no trip start — the default state). An estimated arrival must
            // STILL carry the "~" approximation marker so it is never visually identical
            // to a confident time; only the verbose "· Estimated" word is dropped for
            // width (the badge + the full-text aria name the provenance).
            return qualifier is null
                ? offset
                : string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineEstimatedPrefix, offset);
        }

        var clockText = WallClockText(clock, tripStart);
        if (qualifier is not null)
        {
            // "~14:10" — the approximation marker is kept; the qualifier word is dropped
            // for width (the badge + the full aria carry the named provenance).
            clockText = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineEstimatedPrefix, clockText);
        }

        return $"{offset} {clockText}";
    }

    /// <summary>
    /// Story 4.2 (FR-27, UX-DR12): the wall-clock display for one arrival. A same-day
    /// arrival (or no trip start to compare against) renders time-only as before; an
    /// arrival on a LATER calendar day than <paramref name="tripStart"/> renders the
    /// locale-driven short date + short time (CultureInfo.CurrentCulture — no hard-coded
    /// MM/dd order), so a multi-day trip reads on its real days. DISPLAY only — the
    /// accumulation math (ItineraryTimeline) is untouched.
    /// </summary>
    private static string WallClockText(DateTime clock, DateTime? tripStart) =>
        tripStart is { } start && clock.Date > start.Date
            ? string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineWallClockWithDate, clock, clock)
            : string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineWallClock, clock);
}
