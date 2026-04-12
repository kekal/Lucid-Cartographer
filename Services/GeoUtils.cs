namespace LucidCartographer.Services
{
    public static class GeoUtils
    {
        private static readonly double EarthRadiusMeters = 6371000;

        /// <summary>
        /// Calculates the great-circle distance between two points on Earth using the Haversine formula.
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

            var dLat = double.DegreesToRadians(lat2 - lat1);
            var dLon = double.DegreesToRadians(lon2 - lon1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(double.DegreesToRadians(lat1)) * Math.Cos(double.DegreesToRadians(lat2)) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return EarthRadiusMeters * c;
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
}
