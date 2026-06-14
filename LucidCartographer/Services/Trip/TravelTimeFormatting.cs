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
}
