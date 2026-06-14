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
    /// Formats a duration in seconds as "Hh Mm" (e.g. "1h 20m"), "Mm" under an
    /// hour (e.g. "12m"), or "&lt;1m" for a sub-minute positive duration. A null
    /// or negative input yields the em-dash unknown marker.
    /// </summary>
    public static string Duration(int? seconds)
    {
        if (seconds is not { } s || s < 0)
        {
            return UiStrings.TripLegTimeUnknown;
        }

        var totalMinutes = s / 60;
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

        // Positive but under a minute (the Mock can produce this for very short
        // legs) — never collapse to "0m" which would read as a free hop.
        return s > 0 ? UiStrings.TripDurationSubMinute : UiStrings.TripDurationZero;
    }

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
    public static string Arrival(int? offsetSeconds, DateTime? wallClock, string? qualifier, bool isUnknown)
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

        var clockText = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineWallClock, clock);
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
    public static string ArrivalCompact(int? offsetSeconds, DateTime? wallClock, string? qualifier, bool isUnknown)
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

        var clockText = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineWallClock, clock);
        if (qualifier is not null)
        {
            // "~14:10" — the approximation marker is kept; the qualifier word is dropped
            // for width (the badge + the full aria carry the named provenance).
            clockText = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTimelineEstimatedPrefix, clockText);
        }

        return $"{offset} {clockText}";
    }
}
