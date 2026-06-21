using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// Canonical predicate for stop placeability: both coordinates must be non-null. (0,0) is a valid, placeable coordinate.
/// Use this for leg drawing, routing candidate filters, and view logic—never re-inline the null-check.
/// </summary>
internal static class StopPlaceability
{
    /// <summary>Value-level form for EF-projected rows filtered in memory.</summary>
    internal static bool IsPlaceable(double? latitude, double? longitude) =>
        latitude != null && longitude != null;

    internal static bool IsPlaceable(this Poi poi) => IsPlaceable(poi.Latitude, poi.Longitude);
}
