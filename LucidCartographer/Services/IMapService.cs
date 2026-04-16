using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services
{
    public record MapBounds(double South, double West, double North, double East)
    {
        public bool Contains(double lat, double lon) =>
            lat >= South && lat <= North && lon >= West && lon <= East;
    }

    public interface IMapService
    {
        Task InitMapAsync(string elementId);
        Task ShowCollectionAsync(int collectionId, List<Poi> pois, string color);
        Task HideCollectionAsync(int collectionId);
        Task FocusOnPoiAsync(double lat, double lon, int zoom = 16);
        Task FitBoundsAsync();
        Task RefreshLayoutAsync();
        Task HighlightMarkerAsync(int poiId);
        /// <summary>
        /// CRIT-04: Destroy the JS-side map object to prevent memory leaks on navigation.
        /// </summary>
        Task DestroyMapAsync();

        /// <summary>Enable JS-side moveend tracking; fires OnBoundsChanged on every pan/zoom.</summary>
        Task EnableBoundsTrackingAsync();

        /// <summary>
        /// Callback invoked when a map marker is clicked. The int parameter is the POI ID.
        /// Replaces the previous event Action&lt;int&gt; to avoid interface event coupling (REVIEW-12).
        /// </summary>
        Func<int, Task>? OnMarkerClicked { get; set; }

        /// <summary>Callback invoked when the map viewport changes (pan/zoom).</summary>
        Func<MapBounds, Task>? OnBoundsChanged { get; set; }
    }
}
