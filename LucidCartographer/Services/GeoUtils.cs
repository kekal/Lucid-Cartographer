using Geolocation;

namespace LucidCartographer.Services;

/// <summary>
/// Centralized distance calculation using the <c>Geolocation</c> NuGet package
/// with Haversine math; sub-meter precision drift is acceptable against identity/slider thresholds.
/// </summary>
public static class GeoUtils
{
    /// <summary>
    /// Great-circle distance in meters. Input ranges are enforced explicitly
    /// because the underlying library silently returns NaN for invalid bounds.
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

        // decimalPlaces: 6 provides sub-millimeter precision adequate for POI-granularity decisions.
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