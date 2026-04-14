using System.Globalization;
using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services
{
    /// <summary>
    /// ARCH-HIGH-03: Shared Google Maps URL helper — replaces duplicated methods in
    /// PoiTable.razor, PoiDetailPane.razor, and OperationsPage.razor.
    /// IE-14: Shared coordinate extraction from Google Maps URLs.
    /// </summary>
    public static class PoiUrlHelper
    {
        public static string GetGoogleMapsUrl(Poi poi)
        {
            if (!string.IsNullOrEmpty(poi.GoogleMapsUrl))
                return poi.GoogleMapsUrl;

            if (double.IsNaN(poi.Latitude) || double.IsNaN(poi.Longitude)
                || double.IsInfinity(poi.Latitude) || double.IsInfinity(poi.Longitude))
                return "#";

            return $"https://www.google.com/maps/search/?api=1&query={poi.Latitude.ToString(CultureInfo.InvariantCulture)},{poi.Longitude.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>
        /// Extracts coordinates from a Google Maps URL.
        /// IE-14: Consolidated from GoogleMapsListScraper's duplicated @/ parsing blocks.
        /// Checks !3d/!4d parameters first, then @/ anywhere in the URL.
        /// </summary>
        public static (double lat, double lon)? ExtractCoordinatesFromUrl(string url)
        {
            // Try !3d/!4d parameters first (most reliable)
            var lat3d = ExtractBangParam(url, "!3d");
            var lon4d = ExtractBangParam(url, "!4d");
            if (lat3d.HasValue && lon4d.HasValue)
                return (lat3d.Value, lon4d.Value);

            // Try @lat,lon anywhere in the URL (single check, no duplicate /place/ vs non-/place/ paths)
            var atIdx = url.IndexOf("/@");
            if (atIdx >= 0)
            {
                var afterAt = url[(atIdx + 2)..];
                var parts = afterAt.Split(',');
                if (parts.Length >= 2
                    && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var lat)
                    && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var lon))
                {
                    return (lat, lon);
                }
            }

            return null;
        }

        private static double? ExtractBangParam(string url, string prefix)
        {
            var idx = url.IndexOf(prefix);
            if (idx < 0) return null;
            var start = idx + prefix.Length;
            var end = start;
            while (end < url.Length && (char.IsDigit(url[end]) || url[end] == '.' || url[end] == '-'))
                end++;
            if (end > start && double.TryParse(url[start..end], CultureInfo.InvariantCulture, out var val))
                return val;
            return null;
        }
    }
}
