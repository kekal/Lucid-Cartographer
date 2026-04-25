using Geolocation;

namespace LucidCartographer.Services;

/// <summary>
/// Thin wrapper around the <c>Geolocation</c> NuGet package so the rest
/// of the codebase has one call site for "distance between two lat/lon
/// points in meters". The package does spherical great-circle math
/// (Haversine-family); sub-meter drift versus our old hand-rolled
/// R=6,371,000m implementation is well within the 100m identity
/// threshold in <see cref="PoiIdentity"/> and the user-tunable slider
/// in the operations page, so the swap is behaviour-preserving for
/// every real consumer.
/// </summary>
public static class GeoUtils
{
    /// <summary>
    /// Great-circle distance between two points on Earth, in meters.
    /// Input ranges are enforced explicitly because
    /// <c>GeoCalculator.GetDistance</c> accepts out-of-range values
    /// silently and returns NaN, which is harder to diagnose than
    /// an ArgumentOutOfRangeException at the call site.
    /// </summary>
    /// <param name="lat1">Latitude of point 1 in degrees [-90, 90].</param>
    /// <param name="lon1">Longitude of point 1 in degrees [-180, 180].</param>
    /// <param name="lat2">Latitude of point 2 in degrees [-90, 90].</param>
    /// <param name="lon2">Longitude of point 2 in degrees [-180, 180].</param>
    /// <returns>Distance in meters.</returns>
    public static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        ValidateCoordinate(lat1, -90, 90, nameof(lat1));
        ValidateCoordinate(lon1, -180, 180, nameof(lon1));
        ValidateCoordinate(lat2, -90, 90, nameof(lat2));
        ValidateCoordinate(lon2, -180, 180, nameof(lon2));

        // decimalPlaces: 6 gives sub-millimeter precision once
        // converted back to meters — more than enough for any
        // POI-granularity decision.
        return GeoCalculator.GetDistance(lat1, lon1, lat2, lon2,
            decimalPlaces: 6,
            distanceUnit: DistanceUnit.Meters);
    }

    private static void ValidateCoordinate(double value, double min, double max, string paramName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(paramName,
                $"Value {value} is outside the valid range [{min}, {max}].");
        }
    }
}