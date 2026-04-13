using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services
{
    /// <summary>
    /// ARCH-HIGH-03: Shared Google Maps URL helper — replaces duplicated methods in
    /// PoiTable.razor, PoiDetailPane.razor, and OperationsPage.razor.
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

            return $"https://www.google.com/maps/search/?api=1&query={poi.Latitude},{poi.Longitude}";
        }
    }
}
