using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// The single canonical "placeable" predicate for the Trip slice.
/// [TRIP-PLACE-01] A stop is placeable iff BOTH coordinates are non-null
/// (null <see cref="Poi.Latitude"/> OR null <see cref="Poi.Longitude"/> ⇒
/// unplaceable). This matches the long-standing codebase convention
/// (<c>Latitude != null &amp;&amp; Longitude != null</c> in PoiService,
/// StartupCleanupService, the enrichment services) — there is no <c>(0,0)</c>
/// sentinel: <c>(0,0)</c> is a real, placeable coordinate pair.
///
/// An unplaceable stop is KEPT in the collection and in the stop list (with
/// the "Not placeable" treatment) but EXCLUDED from everything spatial: map
/// markers, drawn legs, and the all-pairs routing candidate set (the Epic 3
/// Distance Matrix). Leg drawing, the routing candidate accessor and the
/// TripViewModel must all call this one predicate — never re-inline the
/// null-check.
/// </summary>
internal static class StopPlaceability
{
    /// <summary>Value-level form for projected rows (EF queries select the raw nullable coordinates and filter in memory through this).</summary>
    internal static bool IsPlaceable(double? latitude, double? longitude) =>
        latitude != null && longitude != null;

    /// <summary>Entity-level form.</summary>
    internal static bool IsPlaceable(this Poi poi) => IsPlaceable(poi.Latitude, poi.Longitude);
}
